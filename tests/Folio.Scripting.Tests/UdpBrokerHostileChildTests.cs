using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Folio.Scripting.Ipc;

namespace Folio.Scripting.Tests;

/// <summary>A hostile or broken content process: floods, stalled pipes, forged fields, stale documents.</summary>
public sealed class UdpBrokerHostileChildTests
{
    private static readonly UdpBrokerOptions Open = new() { AllowLoopback = true, CommandsPerSecond = 1e9, BurstCommands = 1e9, OpensPerSecond = 1e9, BurstOpens = 1e9 };
    private static UdpBrokerTests.Harness Harness(UdpBrokerOptions options) => new(options);
    private static readonly string Permit = """{"op":"permit","host":"127.0.0.1"}""";

    private static async Task<int> Receive(Socket peer, int milliseconds = 300)
    {
        using var timeout = new CancellationTokenSource(milliseconds);
        try { return (await peer.ReceiveFromAsync(new byte[4096], SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token)).ReceivedBytes; }
        catch (OperationCanceledException) { return -1; }
    }

    [Fact]
    public async Task EventQueueStaysBoundedAndSocketsAreRevokedWhenTheChildNeverDrains()
    {
        var stalled = new ManualResetEventSlim();
        var stuck = new ManualResetEventSlim();
        using var h = Harness(Open with { MaxSockets = 1, MaxQueuedEvents = 8 });
        h.Hook = _ => { stuck.Set(); stalled.Wait(10_000); }; // the pipe write never completes
        h.Run("""{"op":"open","token":1}""");
        Assert.True(stuck.Wait(TimeSpan.FromSeconds(30))); // the pump is now blocked inside the first write, so the backlog stays queued
        for (var i = 2; i < 500; i++) h.Run($$"""{"op":"open","token":{{i}}}"""); // every refusal queues a `failed`
        Assert.True(h.Broker.Dead);
        Assert.Equal(0, h.Broker.OpenSockets);
        Assert.True(h.Broker.QueuedEvents <= 8, $"queued {h.Broker.QueuedEvents}");
        // Once dead, further commands are refused without queueing anything.
        var before = h.Broker.QueuedEvents;
        for (var i = 0; i < 100; i++) h.Run($$"""{"op":"open","token":{{1000 + i}}}""");
        Assert.Equal(before, h.Broker.QueuedEvents);
        // The next document starts clean even while the old document's backlog is still undrained.
        h.NextDocument();
        Assert.False(h.Broker.Dead);
        h.Run("""{"op":"open","token":5000}""");
        Assert.False(h.Broker.Dead);
        stalled.Set();
        var opened = await h.WaitFor(e => UdpBrokerTests.IsToken(e, 5000));
        Assert.Equal("opened", opened.GetProperty("op").GetString());
    }

    [Fact]
    public async Task OpenAndCloseChurnIsRateLimited()
    {
        using var h = Harness(new UdpBrokerOptions { AllowLoopback = true, MaxSockets = 100 });
        for (var i = 0; i < 60; i++)
        {
            h.Run($$"""{"op":"open","token":{{i}}}""");
            h.Run($$"""{"op":"close","id":{{i}}}""");
        }
        await Task.Delay(100);
        var opened = h.Snapshot().Count(e => e.GetProperty("op").GetString() == "opened");
        Assert.InRange(opened, 1, 6); // burst of 4 plus at most the odd refill
        Assert.True(h.Broker.RateLimited > 0);
    }

    [Fact]
    public void CommandRateLimitAppliesBeforeAnyDecoding()
    {
        using var h = Harness(new UdpBrokerOptions { AllowLoopback = true, BurstCommands = 5, CommandsPerSecond = 0.01 });
        for (var i = 0; i < 100; i++) h.Run("""{"op":"bogus"}""");
        Assert.Equal(5, h.Broker.Malformed);
        Assert.Equal(95, h.Broker.RateLimited);
    }

    [Theory]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("010.0.0.1")]
    [InlineData("8.8.8.8 ")]
    [InlineData(" 8.8.8.8")]
    [InlineData("0x7f.1")]
    [InlineData("0x7f000001")]
    [InlineData("2130706433")]
    [InlineData("127.1")]
    [InlineData("127.000.000.001")]
    [InlineData("1.2.3.4.5")]
    [InlineData("localhost")]
    [InlineData("")]
    public async Task NonCanonicalHostsAreNeverResolvedOrSent(string host)
    {
        using var h = Harness(Open); // loopback is allowed, so a form that parsed to 127.0.0.1 would be delivered
        using var peer = UdpBrokerTests.Peer();
        var (id, _) = await h.Open();
        h.Run($$"""{"op":"send","id":{{id}},"host":{{JsonSerializer.Serialize(host)}},"port":{{((IPEndPoint)peer.LocalEndPoint!).Port}},"data":"AQID"}""");
        h.Run($$"""{"op":"permit","host":{{JsonSerializer.Serialize(host)}}}""");
        Assert.Equal(-1, await Receive(peer));
        Assert.Equal(2, h.Broker.Denied);
    }

    [Theory]
    [InlineData("AQ ID")]
    [InlineData("AQ\\nID")]
    [InlineData("AQ")]
    [InlineData("A===")]
    [InlineData("AQID=")]
    [InlineData("!!!!")]
    [InlineData("")]
    public async Task BadBase64IsDroppedAndCounted(string data)
    {
        using var h = Harness(Open);
        using var peer = UdpBrokerTests.Peer();
        var (id, _) = await h.Open();
        h.Run($$"""{"op":"send","id":{{id}},"host":"127.0.0.1","port":{{((IPEndPoint)peer.LocalEndPoint!).Port}},"data":"{{data}}"}""");
        Assert.Equal(-1, await Receive(peer));
        Assert.Equal(1, h.Broker.Malformed);
        h.Run($$"""{"op":"send","id":{{id}},"host":"127.0.0.1","port":{{((IPEndPoint)peer.LocalEndPoint!).Port}},"data":"AQID"}""");
        Assert.Equal(3, await Receive(peer, 2000));
    }

    [Fact]
    public async Task ConcurrentSendsRacingRevocationNeitherThrowNorLeakSockets()
    {
        using var h = Harness(Open);
        using var peer = UdpBrokerTests.Peer();
        var (id, _) = await h.Open();
        var to = (IPEndPoint)peer.LocalEndPoint!;
        var senders = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 300; i++) h.Send(id, to, [1, 2, 3]);
        })).ToArray();
        await Task.Delay(5);
        h.Granted = false;
        h.Broker.Revoke();
        await Task.WhenAll(senders);
        Assert.Equal(0, h.Broker.OpenSockets);
        h.Granted = true;
        h.Run($$"""{"op":"send","id":{{id}},"host":"127.0.0.1","port":{{to.Port}},"data":"AQID"}""");
        Assert.True(h.Broker.Denied > 0); // the revoked id is dead for good
    }

    [Fact]
    public async Task UnsolicitedInboundIsDroppedButSentToAndPermittedPeersGetThrough()
    {
        using var h = Harness(Open);
        var (_, port) = await h.Open();
        using var stranger = UdpBrokerTests.Peer();
        var target = new IPEndPoint(IPAddress.Loopback, port);
        await stranger.SendToAsync(new byte[] { 1 }, SocketFlags.None, target);
        for (var i = 0; i < 200 && Interlocked.Read(ref h.Broker.Unsolicited) == 0; i++) await Task.Delay(10);
        Assert.Equal(1, h.Broker.Unsolicited);
        Assert.DoesNotContain(h.Snapshot(), e => e.GetProperty("op").GetString() == "datagram");

        // A peer the child sent to may answer.
        h.Run($$"""{"op":"send","id":1,"host":"127.0.0.1","port":{{((IPEndPoint)stranger.LocalEndPoint!).Port}},"data":"AQ=="}""");
        Assert.Equal(1, await Receive(stranger, 2000));
        await stranger.SendToAsync(new byte[] { 2 }, SocketFlags.None, target);
        await h.WaitFor(e => e.GetProperty("op").GetString() == "datagram");
    }

    [Fact]
    public async Task PermittedPeerMayInitiateAndThePermitListIsCapped()
    {
        using var h = Harness(Open with { MaxPermitted = 2 });
        var (_, port) = await h.Open();
        using var peer = UdpBrokerTests.Peer();
        h.Run(Permit);
        await peer.SendToAsync(new byte[] { 9 }, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, port));
        var evt = await h.WaitFor(e => e.GetProperty("op").GetString() == "datagram");
        Assert.Equal("127.0.0.1", evt.GetProperty("host").GetString());
        Assert.Equal(0, h.Broker.Denied);
        h.Run("""{"op":"permit","host":"127.0.0.2"}""");
        h.Run("""{"op":"permit","host":"127.0.0.3"}"""); // third distinct address: over the cap
        Assert.Equal(1, h.Broker.Denied);
        h.Run("""{"op":"permit","host":"127.0.0.2"}"""); // already permitted: no cost
        Assert.Equal(1, h.Broker.Denied);
    }

    [Fact]
    public void PermitFollowsTheDestinationPolicy()
    {
        using var h = Harness(new UdpBrokerOptions()); // production policy
        h.Run("""{"op":"permit","host":"127.0.0.1"}""");
        h.Run("""{"op":"permit","host":"10.0.0.1"}""");
        h.Run("""{"op":"permit","host":"224.0.0.1"}""");
        Assert.Equal(3, h.Broker.Denied);
    }

    [Fact]
    public async Task PermitsDoNotSurviveIntoTheNextDocument()
    {
        using var h = Harness(Open);
        var (_, port) = await h.Open();
        h.Run(Permit);
        h.NextDocument();
        var (_, newPort) = await h.Open(2);
        using var peer = UdpBrokerTests.Peer();
        await peer.SendToAsync(new byte[] { 1 }, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, newPort));
        for (var i = 0; i < 200 && Interlocked.Read(ref h.Broker.Unsolicited) == 0; i++) await Task.Delay(10);
        Assert.Equal(1, h.Broker.Unsolicited);
        Assert.True(port > 0);
    }

    [Fact]
    public async Task AnOpenFromTheReplacedDocumentIsRefusedAndConsumesNoQuota()
    {
        using var h = Harness(Open with { MaxSockets = 1 });
        var stale = h.Broker.Generation;
        h.NextDocument();
        h.Broker.Handle(UdpBrokerTests.Cmd($$"""{"op":"open","gen":{{stale}},"token":1}"""));
        var refused = await h.WaitFor(e => UdpBrokerTests.IsToken(e, 1));
        Assert.Equal("failed", refused.GetProperty("op").GetString());
        Assert.Equal(0, h.Broker.OpenSockets);
        Assert.True((await h.Open(2)).Id > 0); // the single-socket quota is intact
    }

    [Fact]
    public async Task OldDocumentOpensRacingTheReplacementNeverSurvive()
    {
        using var h = Harness(Open with { MaxSockets = 1000 });
        var stale = h.Broker.Generation;
        using var stop = new CancellationTokenSource();
        var racer = Task.Run(() =>
        {
            for (var i = 0; i < 400 && !stop.IsCancellationRequested; i++)
                h.Broker.Handle(UdpBrokerTests.Cmd($$"""{"op":"open","gen":{{stale}},"token":{{i}}}"""));
        });
        await Task.Delay(2);
        h.NextDocument();
        await racer;
        Assert.Equal(0, h.Broker.OpenSockets); // everything the old document opened was closed or refused
    }

    [Fact]
    public async Task RefusedQueueWriteDoesNotSpendBudgetAndReplacementCannotMintBursts()
    {
        using var h = Harness(new UdpBrokerOptions { AllowLoopback = true, OutgoingQueuePerSocket = 1, BurstPackets = 3, PacketsPerSecond = 0.01 });
        using var peer = UdpBrokerTests.Peer();
        var (id, _) = await h.Open();
        var to = (IPEndPoint)peer.LocalEndPoint!;
        var pause = new TaskCompletionSource();
        h.Broker.SendPause = pause.Task;
        for (var i = 0; i < 6; i++) h.Send(id, to, [1]);
        Assert.True(h.Broker.DroppedOutgoing >= 3);
        Assert.Equal(0, h.Broker.RateLimited); // the refunds kept the bucket from running dry
        pause.SetResult();

        // Fewer than a second after the limiters were made, a new document keeps the same (spent) buckets.
        using var g = Harness(new UdpBrokerOptions { AllowLoopback = true, BurstPackets = 1, PacketsPerSecond = 0.01 });
        var (gid, _) = await g.Open();
        g.Send(gid, to, [1]); g.Send(gid, to, [1]);
        Assert.Equal(1, g.Broker.RateLimited);
        g.NextDocument();
        var (nid, _) = await g.Open(2);
        g.Send(nid, to, [1]);
        Assert.Equal(2, g.Broker.RateLimited);
    }

    [Fact]
    public void OversizeOrUnclassifiableUdpFramesAreRefusedWithoutAllocatingThem()
    {
        var kind = (int)ContentMessageKind.UdpCommand;
        // A frame that claims 16 MiB and starts like a UDP command: the read stops after the prefix.
        using var claimed = new CountingStream(Frame(16 * 1024 * 1024, $$"""{"Kind":{{kind}},"Sequence":1,"Payload":{""", 0));
        Assert.Throws<InvalidDataException>(() => ContentTransport.ReadAsync(claimed).AsTask().GetAwaiter().GetResult());
        Assert.True(claimed.BytesRead <= 4 + 32, $"read {claimed.BytesRead}");
        // A large frame that hides its kind is refused the same way.
        using var hidden = new CountingStream(Frame(16 * 1024 * 1024, """{"Sequence":1,"Payload":{""", 0));
        Assert.Throws<InvalidDataException>(() => ContentTransport.ReadAsync(hidden).AsTask().GetAwaiter().GetResult());
        Assert.True(hidden.BytesRead <= 4 + 32);
        // UDP events are held to the same size.
        using var evt = new CountingStream(Frame(100_000, $$"""{"Kind":{{(int)ContentMessageKind.UdpEvent}},"Sequence":1,"Payload":{""", 0));
        Assert.Throws<InvalidDataException>(() => ContentTransport.ReadAsync(evt).AsTask().GetAwaiter().GetResult());
    }

    [Fact]
    public void LargeNonUdpFramesAndSmallUdpFramesStillRoundTrip()
    {
        var large = new ContentMessage(ContentMessageKind.Load, 7, JsonSerializer.SerializeToElement(new { html = new string('x', 200_000) }));
        using var largeStream = new MemoryStream(ContentProtocol.Encode(large));
        var readLarge = ContentTransport.ReadAsync(largeStream).AsTask().GetAwaiter().GetResult();
        Assert.Equal(ContentMessageKind.Load, readLarge!.Kind);
        var small = new ContentMessage(ContentMessageKind.UdpCommand, 8, JsonSerializer.SerializeToElement(new { op = "open", gen = 1, token = 1 }));
        using var smallStream = new MemoryStream(ContentProtocol.Encode(small));
        Assert.Equal(ContentMessageKind.UdpCommand, ContentTransport.ReadAsync(smallStream).AsTask().GetAwaiter().GetResult()!.Kind);
        // Our own serializer really does put the kind first, which the large-frame check relies on.
        Assert.StartsWith($$"""{"Kind":{{(int)ContentMessageKind.Load}},""", Encoding.UTF8.GetString(ContentProtocol.Encode(large), 4, 20));
    }

    [Fact(Timeout = 60000)]
    public async Task RawCommandsFromAChildWithoutTheCapabilityCreateNothing()
    {
        await using var client = ContentProcessClient.StartWithOptions(ContentHostTestPaths.Resolve(), new FolioOptions(),
            new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromSeconds(15)));
        client.UdpOptions = Open;
        var handle = typeof(ContentProcessClient).GetMethod("HandleUdpCommand", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var generation = (long)typeof(ContentProcessClient).GetField("_udpGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
        for (var i = 0; i < 10; i++)
            handle.Invoke(client, [JsonDocument.Parse($$"""{"op":"open","gen":{{generation}},"token":{{i}}}""").RootElement.Clone()]);
        Assert.Equal(0, client.UdpOpenSockets);

        // Granting the capability does not retroactively create anything; only a fresh open does, and revoking closes it.
        await client.SendAsync(ContentMessageKind.Load, new { html = "<body></body>", capabilities = new FeatureCapabilities { WebRtc = true }, script = "1" }, TestContext.Current.CancellationToken);
        generation = (long)typeof(ContentProcessClient).GetField("_udpGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
        Assert.Equal(0, client.UdpOpenSockets);
        handle.Invoke(client, [JsonDocument.Parse($$"""{"op":"open","gen":{{generation}},"token":99}""").RootElement.Clone()]);
        Assert.Equal(1, client.UdpOpenSockets);
        await client.UpdateCapabilitiesAsync(FeatureCapabilities.Default, TestContext.Current.CancellationToken);
        Assert.Equal(0, client.UdpOpenSockets);
        // And a forged command carrying the previous generation is refused.
        await client.UpdateCapabilitiesAsync(new FeatureCapabilities { WebRtc = true }, TestContext.Current.CancellationToken);
        handle.Invoke(client, [JsonDocument.Parse($$"""{"op":"open","gen":{{generation - 1}},"token":100}""").RootElement.Clone()]);
        Assert.Equal(0, client.UdpOpenSockets);
    }

    [Fact]
    public void DuplicateKindCannotSmuggleAnOversizeUdpFramePastTheCap()
    {
        var kind = (int)ContentMessageKind.UdpCommand;
        // The first Kind passes the prefix check; System.Text.Json keeps the last one.
        var json = $$"""{"Kind":0,"Sequence":0,"Payload":{"pad":"{{new string('x', 60_000)}}"},"Kind":{{kind}},"Version":1}""";
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[4 + payload.Length];
        BitConverter.TryWriteBytes(frame, payload.Length);
        payload.CopyTo(frame, 4);
        Assert.True(ContentProtocol.TryDecode(frame, out var decoded) && decoded!.Kind == ContentMessageKind.UdpCommand); // really decodes as UDP
        using var stream = new MemoryStream(frame);
        Assert.Throws<InvalidDataException>(() => ContentTransport.ReadAsync(stream).AsTask().GetAwaiter().GetResult());
        // The same trick on a small frame is within the cap and harmless.
        var small = Encoding.UTF8.GetBytes($$"""{"Kind":0,"Sequence":0,"Payload":{},"Kind":{{kind}},"Version":1}""");
        var smallFrame = new byte[4 + small.Length];
        BitConverter.TryWriteBytes(smallFrame, small.Length);
        small.CopyTo(smallFrame, 4);
        using var smallStream = new MemoryStream(smallFrame);
        Assert.Equal(ContentMessageKind.UdpCommand, ContentTransport.ReadAsync(smallStream).AsTask().GetAwaiter().GetResult()!.Kind);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("65536")]
    [InlineData("2147483648")]
    [InlineData("99999999999")]
    public async Task OutOfRangePortsAreDeniedWithoutThrowing(string port)
    {
        using var h = Harness(Open);
        var (id, _) = await h.Open();
        h.Run($$"""{"op":"send","id":{{id}},"host":"127.0.0.1","port":{{port}},"data":"AQID"}""");
        Assert.Equal(1, h.Broker.Denied);
        Assert.Equal(1, h.Broker.OpenSockets);
    }

    [Fact]
    public async Task NoSocketSurvivesAKillEvenWhenOpensRaceIt()
    {
        var stalled = new ManualResetEventSlim();
        using var h = Harness(Open with { MaxSockets = 10_000, MaxQueuedEvents = 8 });
        h.Hook = _ => stalled.Wait(10_000);
        var threads = Enumerable.Range(0, 4).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 300; i++) h.Run($$"""{"op":"open","token":{{t * 1000 + i}}}""");
        })).ToArray();
        await Task.WhenAll(threads);
        Assert.True(h.Broker.Dead);
        Assert.Equal(0, h.Broker.OpenSockets);
        stalled.Set();
    }

    [Fact]
    public async Task ARefusedPeerDoesNotBreakTheSocket()
    {
        // Sending to a closed local port provokes ICMP port-unreachable, which Windows surfaces as WSAECONNRESET on receive.
        using var h = Harness(Open with { MaxSocketErrors = 2 });
        var (id, port) = await h.Open();
        h.Run(Permit);
        int closedPort;
        using (var gone = UdpBrokerTests.Peer()) closedPort = ((IPEndPoint)gone.LocalEndPoint!).Port;
        for (var i = 0; i < 10; i++)
        {
            h.Send(id, new IPEndPoint(IPAddress.Loopback, closedPort), [1]);
            await Task.Delay(20);
        }
        Assert.Equal(1, h.Broker.OpenSockets);
        using var peer = UdpBrokerTests.Peer();
        await peer.SendToAsync(new byte[] { 7 }, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, port));
        await h.WaitFor(e => e.GetProperty("op").GetString() == "datagram");
    }

    private static byte[] Frame(int claimedLength, string prefix, int _)
    {
        prefix = prefix.PadRight(48);
        var bytes = new byte[4 + prefix.Length];
        BitConverter.TryWriteBytes(bytes, claimedLength);
        Encoding.UTF8.GetBytes(prefix).CopyTo(bytes, 4);
        return bytes;
    }

    // Serves the given bytes, then fails any attempt to read the claimed rest of the frame; counts what was consumed.
    private sealed class CountingStream(byte[] data) : Stream
    {
        private int _position;
        public int BytesRead { get; private set; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = Math.Min(buffer.Length, data.Length - _position);
            data.AsMemory(_position, n).CopyTo(buffer);
            _position += n; BytesRead += n;
            return ValueTask.FromResult(n);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
