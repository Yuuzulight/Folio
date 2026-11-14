using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Folio.Scripting.Ipc;

namespace Folio.Scripting.Tests;

/// <summary>Policy, quota and lifetime tests for the host UDP broker, using real loopback sockets and no process.</summary>
public sealed class UdpBrokerTests
{
    internal static readonly UdpBrokerOptions Loopback = new() { AllowLoopback = true };

    internal static bool IsToken(JsonElement e, long token) => e.TryGetProperty("token", out var t) && t.GetInt64() == token;

    internal static JsonElement Cmd(string json) => JsonDocument.Parse(json).RootElement.Clone();

    internal sealed class Harness : IDisposable
    {
        public readonly List<JsonElement> Events = [];
        public readonly UdpBroker Broker;
        public volatile bool Granted = true;
        public Action<JsonElement>? Hook;

        public Harness(UdpBrokerOptions? options = null)
        {
            Broker = new UdpBroker(options ?? Loopback, () => Granted, evt =>
            {
                lock (Events) Events.Add(evt);
                Hook?.Invoke(evt);
            });
        }

        /// <summary>Sends a child command stamped with the broker's current document generation, as a well-behaved child would.</summary>
        public void Run(string json) =>
            Broker.Handle(Cmd(json.StartsWith('{') ? json.Insert(1, $"\"gen\":{Broker.Generation},") : json));

        public void NextDocument() => Broker.BeginDocument(Broker.Generation + 1);

        public JsonElement[] Snapshot() { lock (Events) return [.. Events]; }

        public async Task<JsonElement> WaitFor(Func<JsonElement, bool> match)
        {
            for (var i = 0; i < 3000; i++)
            {
                foreach (var evt in Snapshot()) if (match(evt)) return evt;
                await Task.Delay(10);
            }
            throw new TimeoutException("Expected broker event did not arrive.");
        }

        public async Task<(long Id, int Port)> Open(int token = 1)
        {
            Run($$"""{"op":"open","token":{{token}}}""");
            var evt = await WaitFor(e => IsToken(e, token));
            Assert.Equal("opened", evt.GetProperty("op").GetString());
            return (evt.GetProperty("id").GetInt64(), evt.GetProperty("port").GetInt32());
        }

        public void Send(long id, IPEndPoint to, byte[] data) =>
            Run($$"""{"op":"send","id":{{id}},"host":"{{to.Address}}","port":{{to.Port}},"data":"{{Convert.ToBase64String(data)}}"}""");

        public void Dispose() => Broker.Dispose();
    }

    internal static Socket Peer()
    {
        var peer = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        peer.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return peer;
    }

    private static async Task<int> Receive(Socket peer, int milliseconds = 300)
    {
        using var timeout = new CancellationTokenSource(milliseconds);
        try { return (await peer.ReceiveFromAsync(new byte[4096], SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token)).ReceivedBytes; }
        catch (OperationCanceledException) { return -1; }
    }

    [Theory]
    [InlineData("8.8.8.8", 3478, true)]
    [InlineData("203.1.2.3", 65535, true)]
    [InlineData("8.8.8.8", 0, false)]
    [InlineData("127.0.0.1", 3478, false)]
    [InlineData("10.1.2.3", 3478, false)]
    [InlineData("172.16.0.1", 3478, false)]
    [InlineData("172.31.255.1", 3478, false)]
    [InlineData("172.32.0.1", 3478, true)]
    [InlineData("192.168.1.1", 3478, false)]
    [InlineData("100.64.0.1", 3478, false)]
    [InlineData("169.254.1.1", 3478, false)]
    [InlineData("224.0.0.251", 5353, false)]
    [InlineData("239.255.255.250", 1900, false)]
    [InlineData("255.255.255.255", 3478, false)]
    [InlineData("0.0.0.0", 3478, false)]
    [InlineData("240.0.0.1", 3478, false)]
    [InlineData("192.0.2.1", 3478, false)]
    [InlineData("192.0.0.1", 3478, false)]
    [InlineData("192.0.0.255", 3478, false)]
    [InlineData("198.18.0.1", 3478, false)]
    [InlineData("198.19.255.1", 3478, false)]
    [InlineData("198.51.100.1", 3478, false)]
    [InlineData("203.0.113.1", 3478, false)]
    [InlineData("198.20.0.1", 3478, true)]
    [InlineData("::1", 3478, false)]
    [InlineData("2001:db8::1", 3478, false)]
    public void DestinationPolicyDeniesNonPublicAndMalformedEndpoints(string address, int port, bool allowed) =>
        Assert.Equal(allowed, UdpDestinationPolicy.IsAllowed(new IPEndPoint(IPAddress.Parse(address), port), new UdpBrokerOptions()));

    [Fact]
    public void EmbedderMayOptInToLoopbackAndPrivateRanges()
    {
        Assert.True(UdpDestinationPolicy.IsAllowed(new IPEndPoint(IPAddress.Loopback, 1), new UdpBrokerOptions { AllowLoopback = true }));
        Assert.False(UdpDestinationPolicy.IsAllowed(new IPEndPoint(IPAddress.Parse("10.0.0.1"), 1), new UdpBrokerOptions { AllowLoopback = true }));
        Assert.True(UdpDestinationPolicy.IsAllowed(new IPEndPoint(IPAddress.Parse("10.0.0.1"), 1), new UdpBrokerOptions { AllowPrivate = true }));
        Assert.False(UdpDestinationPolicy.IsAllowed(new IPEndPoint(IPAddress.Parse("169.254.1.1"), 1), new UdpBrokerOptions { AllowPrivate = true, AllowLoopback = true }));
    }

    [Fact]
    public async Task OpenIsRefusedWithoutTheCapabilityAndCreatesNoSocket()
    {
        using var h = new Harness();
        h.Granted = false;
        h.Run("""{"op":"open","token":7}""");
        var evt = await h.WaitFor(e => IsToken(e, 7));
        Assert.Equal("failed", evt.GetProperty("op").GetString());
        Assert.Equal(0, h.Broker.OpenSockets);
    }

    [Fact]
    public async Task SocketQuotaIsEnforcedPerDocument()
    {
        using var h = new Harness(new UdpBrokerOptions { AllowLoopback = true, MaxSockets = 2 });
        await h.Open(1); await h.Open(2);
        h.Run("""{"op":"open","token":3}""");
        var refused = await h.WaitFor(e => IsToken(e, 3));
        Assert.Equal("failed", refused.GetProperty("op").GetString());
        Assert.Equal(2, h.Broker.OpenSockets);
    }

    [Fact]
    public async Task HostPicksDistinctNeverReusedSocketIdsAndPorts()
    {
        using var h = new Harness();
        var first = await h.Open(1);
        h.Run($$"""{"op":"close","id":{{first.Id}}}""");
        var second = await h.Open(2);
        Assert.NotEqual(first.Id, second.Id);
        Assert.True(second.Port > 0);
    }

    [Fact]
    public async Task OversizeDatagramsAreDroppedAndExactlyMaxSizeIsForwarded()
    {
        using var h = new Harness();
        using var peer = Peer();
        var (id, _) = await h.Open();
        var to = (IPEndPoint)peer.LocalEndPoint!;
        h.Send(id, to, new byte[1501]);
        Assert.Equal(-1, await Receive(peer));
        Assert.Equal(1, h.Broker.Oversize);
        h.Send(id, to, new byte[1500]);
        Assert.Equal(1500, await Receive(peer, 2000));
    }

    [Fact]
    public async Task DeniedDestinationsAreDroppedAndCounted()
    {
        using var h = new Harness(new UdpBrokerOptions()); // production policy: loopback denied
        using var peer = Peer();
        var (id, _) = await h.Open();
        h.Send(id, (IPEndPoint)peer.LocalEndPoint!, [1, 2, 3]);
        h.Run($$"""{"op":"send","id":{{id}},"host":"::1","port":9,"data":"AQ=="}""");
        h.Run($$"""{"op":"send","id":{{id}},"host":"localhost","port":9,"data":"AQ=="}""");
        h.Run($$"""{"op":"send","id":{{id}},"host":"08.8.8.8","port":9,"data":"AQ=="}""");
        Assert.Equal(-1, await Receive(peer));
        Assert.Equal(4, h.Broker.Denied);
    }

    [Fact]
    public async Task UnknownSocketIdsAndMalformedCommandsAreIgnored()
    {
        using var h = new Harness();
        using var peer = Peer();
        var (id, _) = await h.Open();
        var to = (IPEndPoint)peer.LocalEndPoint!;
        h.Send(id + 100, to, [1]);
        h.Run("""{"op":"send","id":"x"}""");
        h.Run("""{"op":"bind","address":"0.0.0.0","port":53}""");
        h.Run("[1,2]");
        h.Run($$"""{"op":"send","id":{{id}},"host":"127.0.0.1","port":{{to.Port}},"data":"!!notbase64!!"}""");
        Assert.Equal(-1, await Receive(peer));
        Assert.Equal(1, h.Broker.Denied);
        Assert.Equal(4, h.Broker.Malformed);
        Assert.Equal(0, h.Broker.Oversize);
        Assert.Equal(1, h.Broker.OpenSockets);
    }

    [Fact]
    public async Task RateLimitDropsDatagramsBeyondTheBurst()
    {
        using var h = new Harness(new UdpBrokerOptions { AllowLoopback = true, BurstPackets = 2, PacketsPerSecond = 0.01 });
        using var peer = Peer();
        var (id, _) = await h.Open();
        var to = (IPEndPoint)peer.LocalEndPoint!;
        for (var i = 0; i < 5; i++) h.Send(id, to, [1]);
        Assert.Equal(1, await Receive(peer, 2000));
        Assert.Equal(1, await Receive(peer, 2000));
        Assert.Equal(-1, await Receive(peer));
        Assert.Equal(3, h.Broker.RateLimited);
    }

    [Fact]
    public async Task ByteBudgetIsEnforcedIndependentlyOfPacketCount()
    {
        using var h = new Harness(new UdpBrokerOptions { AllowLoopback = true, BurstBytes = 1000, BytesPerSecond = 1 });
        using var peer = Peer();
        var (id, _) = await h.Open();
        var to = (IPEndPoint)peer.LocalEndPoint!;
        h.Send(id, to, new byte[600]);
        h.Send(id, to, new byte[600]);
        Assert.Equal(600, await Receive(peer, 2000));
        Assert.Equal(-1, await Receive(peer));
        Assert.Equal(1, h.Broker.RateLimited);
    }

    [Fact]
    public async Task IncomingQueueOverflowDropsAndCountsInsteadOfGrowing()
    {
        var release = new ManualResetEventSlim();
        using var h = new Harness(new UdpBrokerOptions { AllowLoopback = true, IncomingQueue = 1 });
        var (id, port) = await h.Open();
        h.Run("""{"op":"permit","host":"127.0.0.1"}""");
        var delivered = 0;
        h.Hook = evt => { if (evt.GetProperty("op").GetString() == "datagram") { Interlocked.Increment(ref delivered); release.Wait(5000); } };
        using var peer = Peer();
        for (var i = 0; i < 12; i++)
        {
            await peer.SendToAsync(new byte[] { (byte)i }, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, port));
            await Task.Delay(5);
        }
        for (var i = 0; i < 200 && Interlocked.Read(ref h.Broker.DroppedIncoming) == 0; i++) await Task.Delay(10);
        Assert.True(h.Broker.DroppedIncoming > 0);
        release.Set();
        Assert.True(id > 0);
    }

    [Fact]
    public async Task OutgoingQueueOverflowDropsAndCounts()
    {
        using var h = new Harness(new UdpBrokerOptions { AllowLoopback = true, OutgoingQueuePerSocket = 2 });
        using var peer = Peer();
        var (id, _) = await h.Open();
        var to = (IPEndPoint)peer.LocalEndPoint!;
        var pause = new TaskCompletionSource();
        h.Broker.SendPause = pause.Task;
        for (var i = 0; i < 6; i++) h.Send(id, to, [(byte)i]);
        // The send loop may already hold one datagram; the queue keeps at most two more.
        Assert.InRange(h.Broker.DroppedOutgoing, 3, 4);
        pause.SetResult();
        Assert.True(await Receive(peer, 2000) > 0);
    }

    [Fact]
    public async Task RevocationClosesEverySocketAndTellsTheChild()
    {
        using var h = new Harness();
        var (id, port) = await h.Open(1);
        await h.Open(2);
        Assert.Equal(2, h.Broker.OpenSockets);
        h.Granted = false;
        using var peer = Peer();
        h.Send(id, (IPEndPoint)peer.LocalEndPoint!, [1]);
        Assert.Equal(0, h.Broker.OpenSockets);
        await h.WaitFor(e => e.GetProperty("op").GetString() == "closed" && e.GetProperty("id").GetInt64() == id);
        Assert.Equal(-1, await Receive(peer));
        // The ephemeral port is released: a datagram to it elicits no event.
        await peer.SendToAsync(new byte[] { 1 }, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, port));
        await Task.Delay(100);
        Assert.DoesNotContain(h.Snapshot(), e => e.GetProperty("op").GetString() == "datagram");
    }

    [Fact]
    public async Task DocumentReplacementClosesSocketsAndDiscardsQueuedDatagrams()
    {
        var release = new ManualResetEventSlim();
        using var h = new Harness();
        var (oldId, port) = await h.Open(1);
        h.Run("""{"op":"permit","host":"127.0.0.1"}""");
        var entered = new ManualResetEventSlim();
        var delivered = 0;
        h.Hook = evt =>
        {
            if (evt.GetProperty("op").GetString() != "datagram") return;
            Interlocked.Increment(ref delivered);
            entered.Set();
            release.Wait(5000);
        };
        using var peer = Peer();
        for (var i = 0; i < 4; i++) await peer.SendToAsync(new byte[] { (byte)i }, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, port));
        Assert.True(entered.Wait(3000)); // one datagram is in flight, the rest queued behind it
        await Task.Delay(100);
        h.NextDocument();
        release.Set();
        var next = await h.Open(2);
        await h.WaitFor(e => e.GetProperty("op").GetString() == "closed" && e.GetProperty("id").GetInt64() == oldId);
        await Task.Delay(150);
        Assert.Equal(1, Volatile.Read(ref delivered)); // only the datagram already in flight; none leaks into the new document
        Assert.NotEqual(oldId, next.Id);
        Assert.Equal(1, h.Broker.OpenSockets);
        // The retired id is dead: sending on it is refused.
        h.Send(oldId, (IPEndPoint)peer.LocalEndPoint!, [9]);
        Assert.Equal(1, h.Broker.Denied);
        Assert.Equal(-1, await Receive(peer));
    }

    [Fact]
    public async Task ReplacementResetsTheDocumentQuotas()
    {
        using var h = new Harness(new UdpBrokerOptions { AllowLoopback = true, BurstPackets = 1, PacketsPerSecond = 0.01, MinLimiterResetInterval = TimeSpan.Zero });
        using var peer = Peer();
        var (id, _) = await h.Open(1);
        var to = (IPEndPoint)peer.LocalEndPoint!;
        h.Send(id, to, [1]); h.Send(id, to, [1]);
        Assert.Equal(1, h.Broker.RateLimited);
        h.NextDocument();
        var next = await h.Open(2);
        h.Send(next.Id, to, [2]);
        Assert.Equal(1, h.Broker.RateLimited);
    }

    [Fact]
    public async Task InboundDatagramsFromDeniedSourcesAreDropped()
    {
        using var h = new Harness(new UdpBrokerOptions()); // loopback sources are not allowed
        var (_, port) = await h.Open();
        using var peer = Peer();
        await peer.SendToAsync(new byte[] { 1 }, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, port));
        for (var i = 0; i < 200 && Interlocked.Read(ref h.Broker.Denied) == 0; i++) await Task.Delay(10);
        Assert.Equal(1, h.Broker.Denied);
        Assert.DoesNotContain(h.Snapshot(), e => e.GetProperty("op").GetString() == "datagram");
    }

    [Fact]
    public async Task DisposeClosesAllSockets()
    {
        var h = new Harness();
        await h.Open();
        h.Dispose();
        Assert.Equal(0, h.Broker.OpenSockets);
        h.Run("""{"op":"open","token":9}""");
        Assert.Equal(0, h.Broker.OpenSockets);
    }
}
