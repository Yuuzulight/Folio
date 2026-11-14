using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Folio.Scripting.Ipc;
using Folio.Scripting.Rtc;

namespace Folio.Scripting.Tests;

/// <summary>
/// TURN over the <see cref="IIceTransport"/> seam (so it works through the host UDP broker) and host-side name
/// resolution. Timers run on a manual clock; nothing here sleeps.
/// </summary>
public sealed class TurnTransportTests
{
    private static readonly IPEndPoint TurnServer = new(IPAddress.Parse("8.8.8.8"), 3478);
    private static readonly IPEndPoint RelayAddress = new(IPAddress.Parse("8.8.4.4"), 50000);
    private static readonly IPEndPoint PeerAddress = new(IPAddress.Parse("1.1.1.1"), 5000);

    /// <summary>A clock that only moves when told to. Supports timers, so Task.Delay and CancellationTokenSource timeouts use it.</summary>
    internal sealed class ManualClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private long _now; // milliseconds

        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() { lock (_gate) return _now; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(GetTimestamp());
        public int PendingTimers { get { lock (_gate) return _timers.Count(t => t.Due is not null); } }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            lock (_gate) _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            long target;
            lock (_gate) target = _now + (long)by.TotalMilliseconds;
            while (true)
            {
                ManualTimer? next;
                lock (_gate)
                {
                    next = _timers.Where(t => t.Due is { } due && due <= target).MinBy(t => t.Due);
                    if (next is null) { _now = target; return; }
                    _now = Math.Max(_now, next.Due!.Value);
                }
                next.Fire();
            }
        }

        private long Ms(TimeSpan span) => span == Timeout.InfiniteTimeSpan ? -1 : (long)span.TotalMilliseconds;

        private sealed class ManualTimer(ManualClock owner, TimerCallback callback, object? state) : ITimer
        {
            public long? Due;
            private long _period = -1;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    var due = owner.Ms(dueTime);
                    Due = due < 0 ? null : owner._now + due;
                    _period = owner.Ms(period);
                }
                return true;
            }

            public void Fire()
            {
                lock (owner._gate) Due = _period > 0 ? owner._now + _period : null;
                callback(state);
            }

            public void Dispose() { lock (owner._gate) { Due = null; owner._timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    /// <summary>
    /// An in-memory datagram transport with a scripted TURN server behind it. Replies are handed to <see cref="Direct"/> when
    /// set (unit tests driving a TurnClient) or queued for the agent's receive loop.
    /// </summary>
    private sealed class ScriptedTurnTransport : IIceTransport
    {
        private const string Realm = "folio.test";
        private readonly BlockingCollection<(byte[] Data, IPEndPoint From)> _queue = [];
        private readonly object _gate = new();
        private int _disposed;
        private string _nonce = "nonce-1";

        public readonly List<(IPEndPoint To, byte[] Data)> Sent = [];
        public readonly List<ushort> ServerSaw = [];
        public readonly List<string> Resolved = [];
        public bool Respond = true, Allow = true;
        public IPAddress? ResolveTo = IPAddress.Parse("8.8.8.8");
        public Func<byte[], bool>? Direct;

        public int SentCount { get { lock (_gate) return Sent.Count; } }
        public (IPEndPoint To, byte[] Data)[] SentSnapshot() { lock (_gate) return [.. Sent]; }
        public bool ServerSawType(ushort type) { lock (_gate) return ServerSaw.Contains(type); }

        public IPEndPoint Bind() => new(IPAddress.Any, 40000);
        public bool CanSendTo(IPEndPoint destination) => Allow;

        public Task<IPAddress?> ResolveAsync(string host, CancellationToken cancellation)
        {
            lock (_gate) Resolved.Add(host);
            return Task.FromResult(ResolveTo);
        }

        public void SendTo(ReadOnlySpan<byte> datagram, IPEndPoint destination)
        {
            var bytes = datagram.ToArray();
            lock (_gate) Sent.Add((destination, bytes));
            if (Respond && destination.Equals(TurnServer)) Serve(bytes);
        }

        public int Receive(byte[] buffer, int timeoutMicroseconds, ref EndPoint remote)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!_queue.TryTake(out var item, Math.Max(1, timeoutMicroseconds / 1000))) return -1;
            item.Data.CopyTo(buffer, 0);
            remote = item.From;
            return item.Data.Length;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
            _queue.CompleteAdding();
        }

        private void Reply(byte[] packet)
        {
            if (Direct is { } direct) direct(packet);
            else if (!_queue.IsAddingCompleted) _queue.Add((packet, TurnServer));
        }

        private void Serve(byte[] packet)
        {
            if (!StunMessage.TryParse(packet, out var message) || message.Type == StunMessage.SendIndication) return;
            lock (_gate) ServerSaw.Add(message.Type);
            var key = MD5.HashData(Encoding.UTF8.GetBytes($"user:{Realm}:pass"));
            if (!message.TryText(StunMessage.Username, 255, out _) || !message.TryAttribute(StunMessage.Integrity, out _) || !message.VerifyIntegritySha1(key))
            {
                Reply(StunMessage.EncodeRequest((ushort)(message.Type | 0x0110), message.TransactionId.Span,
                [
                    (StunMessage.ErrorCode, StunMessage.EncodeErrorCode(401, "Unauthorized")),
                    (StunMessage.Realm, Encoding.UTF8.GetBytes(Realm)), (StunMessage.Nonce, Encoding.UTF8.GetBytes(_nonce))
                ]));
                return;
            }
            var lifetime = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(lifetime, 600);
            ushort type = 0;
            List<(ushort, byte[])> attributes = [];
            switch (message.Type)
            {
                case StunMessage.AllocateRequest:
                    type = StunMessage.AllocateResponse;
                    attributes =
                    [
                        (StunMessage.XorRelayedAddress, StunMessage.EncodeXorAddress(RelayAddress, message.TransactionId.Span)),
                        (StunMessage.XorMappedAddress, StunMessage.EncodeXorAddress(new IPEndPoint(IPAddress.Parse("9.9.9.9"), 41000), message.TransactionId.Span)),
                        (StunMessage.Lifetime, lifetime)
                    ];
                    break;
                case StunMessage.RefreshRequest: type = StunMessage.RefreshResponse; attributes = [(StunMessage.Lifetime, lifetime)]; break;
                case StunMessage.CreatePermissionRequest: type = StunMessage.CreatePermissionResponse; break;
                case StunMessage.ChannelBindRequest: type = StunMessage.ChannelBindResponse; break;
            }
            if (type != 0) Reply(StunMessage.Encode(type, message.TransactionId.Span, attributes, key));
        }
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew(); // only a failsafe bound; progress is made by yielding, not by sleeping
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), "Timed out waiting for the condition.");
            await Task.Yield();
        }
    }

    private static (TurnClient Client, ScriptedTurnTransport Transport, ManualClock Clock) NewClient()
    {
        var transport = new ScriptedTurnTransport();
        var clock = new ManualClock();
        TurnClient? client = null;
        client = new TurnClient(TurnServer, "user", "pass", transport, clock);
        transport.Direct = bytes => client!.TryHandleDatagram(bytes);
        return (client, transport, clock);
    }

    // Walks a request through its three transmissions (500 ms, then 1 s, then 2 s) on the manual clock.
    private static async Task RetryAcrossTheClockAsync(ManualClock clock, ScriptedTurnTransport transport, int sendsExpectedPerAttempt)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Eventually(() => clock.PendingTimers == 1 && transport.SentCount == (attempt + 1) * sendsExpectedPerAttempt);
            clock.Advance(TimeSpan.FromMilliseconds(500 << attempt));
        }
    }

    [Fact]
    public async Task AllocationGoesOnlyToTheServerThroughTheTransport()
    {
        var (client, transport, clock) = NewClient();
        using var _ = client;
        Assert.True(await client.AllocateAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, clock.PendingTimers); // answered requests leave no retransmission timer behind
        Assert.Equal(RelayAddress, client.Relayed);
        var sent = transport.SentSnapshot();
        Assert.Equal(2, sent.Length); // the unauthenticated probe, then the credentialed request
        Assert.All(sent, datagram => Assert.Equal(TurnServer, datagram.To));
    }

    [Fact]
    public async Task UnansweredRequestsRetransmitOnTheInjectedClockAndThenGiveUp()
    {
        var (client, transport, clock) = NewClient();
        using var _ = client;
        transport.Respond = false;
        var allocation = client.AllocateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, transport.SentCount); // sent at once; the timer has not been touched
        await RetryAcrossTheClockAsync(clock, transport, 1);
        Assert.False(await allocation.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
        Assert.Equal(3, transport.SentCount);
        Assert.False(client.IsAllocated);
    }

    [Fact]
    public async Task ADatagramTheTransportRefusesIsNeverSent()
    {
        var (client, transport, clock) = NewClient();
        using var _ = client;
        transport.Allow = false; // the host would refuse this destination
        var allocation = client.AllocateAsync(TestContext.Current.CancellationToken);
        await RetryAcrossTheClockAsync(clock, transport, 0);
        Assert.False(await allocation.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
        Assert.Equal(0, transport.SentCount);
    }

    [Fact]
    public async Task RefreshAndReleaseAreSentThroughTheTransportOnTheClock()
    {
        var (client, transport, clock) = NewClient();
        Assert.True(await client.AllocateAsync(TestContext.Current.CancellationToken));
        client.Maintain();
        Assert.False(transport.ServerSawType(StunMessage.RefreshRequest)); // nothing is due yet
        clock.Advance(TimeSpan.FromSeconds(539));
        client.Maintain();
        Assert.False(transport.ServerSawType(StunMessage.RefreshRequest)); // one second before the 540 s refresh point
        clock.Advance(TimeSpan.FromSeconds(2));
        client.Maintain();
        await Eventually(() => transport.ServerSawType(StunMessage.RefreshRequest));

        client.Dispose();
        var goodbye = transport.SentSnapshot()[^1];
        Assert.Equal(TurnServer, goodbye.To);
        Assert.True(StunMessage.TryParse(goodbye.Data, out var message));
        Assert.Equal(StunMessage.RefreshRequest, message.Type);
        Assert.True(message.TryLifetime(out var lifetime));
        Assert.Equal(0u, lifetime);
    }

    [Fact]
    public async Task RelayedPeerTrafficLeavesThroughTheTransportToTheServer()
    {
        var (client, transport, _) = NewClient();
        using var _ = client;
        Assert.True(await client.AllocateAsync(TestContext.Current.CancellationToken));
        Assert.True(await client.EnsurePermissionAsync(PeerAddress.Address));
        Assert.True(client.SendToPeer(PeerAddress, [0x17, 0xFE, 0xFD, 1]));
        // The peer is never addressed directly: its first datagram is a Send indication to the server.
        Assert.All(transport.SentSnapshot(), datagram => Assert.Equal(TurnServer, datagram.To));
        Assert.Contains(transport.SentSnapshot(), datagram => StunMessage.TryParse(datagram.Data, out var m) && m.Type == StunMessage.SendIndication);

        transport.Allow = false;
        Assert.False(client.SendToPeer(PeerAddress, [1, 2, 3])); // refused by the transport, reported, not thrown
    }

    [Fact]
    public async Task PermissionRefreshBacksOffExponentiallyAndGivesUpAfterABoundedNumberOfFailures()
    {
        var (client, transport, clock) = NewClient();
        using var _ = client;
        Assert.True(await client.AllocateAsync(TestContext.Current.CancellationToken));
        Assert.True(await client.EnsurePermissionAsync(PeerAddress.Address));
        int Requests() => transport.SentSnapshot().Count(d => StunMessage.TryParse(d.Data, out var m) && m.Type == StunMessage.CreatePermissionRequest);
        var baseline = Requests();
        transport.Respond = false;
        // Seconds to move before each round: past the 4 minute refresh, then past the 1 s, 2 s and 4 s backoffs.
        int[] advances = [241, 2, 3, 5];
        for (var round = 0; round < advances.Length; round++)
        {
            clock.Advance(TimeSpan.FromSeconds(advances[round]));
            var sent = baseline + 3 * round;
            await Eventually(() => { client.Maintain(); return Requests() >= sent + 1; });
            for (var transmission = 0; transmission < 3; transmission++)
            {
                await Eventually(() => clock.PendingTimers == 1 && Requests() == sent + transmission + 1);
                clock.Advance(TimeSpan.FromMilliseconds(500 << transmission));
            }
        }
        // The fourth failure was the last: no amount of time makes it ask again.
        clock.Advance(TimeSpan.FromSeconds(100));
        for (var i = 0; i < 2000; i++) { client.Maintain(); await Task.Yield(); }
        Assert.Equal(baseline + 12, Requests());
    }

    private static IceAgent Agent(ScriptedTurnTransport transport, ManualClock clock, string url = "turn:turn.example:3478") =>
        new([IceAgent.WithCredentials(url, "user", "pass")], transport) { TurnClock = clock };

    [Fact]
    public async Task AgentResolvesTheTurnNameThroughTheTransportAndAllocatesThere()
    {
        var transport = new ScriptedTurnTransport();
        using var agent = Agent(transport, new ManualClock());
        await agent.StartGatheringAsync();
        Assert.Equal(["turn.example"], transport.Resolved);
        var relay = Assert.Single(agent.LocalCandidates, c => c.Type == "relay");
        Assert.Equal(RelayAddress.Address.ToString(), relay.Address);
        Assert.Equal(RelayAddress.Port, relay.Port);
        Assert.All(transport.SentSnapshot(), datagram => Assert.Equal(TurnServer, datagram.To));
    }

    [Fact]
    public async Task AgentDoesNotResolveAnIpLiteralAndStillAllocates()
    {
        var transport = new ScriptedTurnTransport();
        using var agent = Agent(transport, new ManualClock(), "turn:8.8.8.8:3478");
        await agent.StartGatheringAsync();
        Assert.Empty(transport.Resolved);
        Assert.Single(agent.LocalCandidates, c => c.Type == "relay");
    }

    [Theory]
    [InlineData("refused")]
    [InlineData("unresolvable")]
    [InlineData("private")]
    public async Task AServerTheHostWillNotReachYieldsNoRelayAndSendsNothing(string why)
    {
        var transport = new ScriptedTurnTransport();
        if (why == "refused") transport.Allow = false;
        if (why == "unresolvable") transport.ResolveTo = null;
        if (why == "private") transport.ResolveTo = IPAddress.Parse("10.0.0.7");
        using var agent = Agent(transport, new ManualClock());
        await agent.StartGatheringAsync();
        Assert.DoesNotContain(agent.LocalCandidates, c => c.Type == "relay");
        Assert.Equal(0, transport.SentCount);
        Assert.Equal("complete", agent.GatheringState);
        if (why != "unresolvable") Assert.Single(agent.TurnSkipReasons);
    }

    [Fact]
    public async Task TurnAllocationWorksThroughTheHostBroker()
    {
        using var server = new TurnClientTests.FakeTurnServer();
        UdpBroker? broker = null;
        var wire = new MediatedUdpClient(command => broker!.Handle(command));
        broker = new UdpBroker(UdpBrokerTests.Loopback, () => true, wire.Deliver);
        using var _ = broker;
        using var agent = new IceAgent([IceAgent.WithCredentials($"turn:127.0.0.1:{server.Endpoint.Port}", "user", "pass")], wire.CreateTransport())
        { TurnRequestTimeout = TimeSpan.FromMilliseconds(500) };
        await agent.StartGatheringAsync();
        var relay = Assert.Single(agent.LocalCandidates, c => c.Type == "relay");
        Assert.Equal(server.RelayEndpoint!.Port, relay.Port);
        Assert.Equal(1, broker.OpenSockets); // the agent's one socket; the TURN client has none of its own
        Assert.Equal(0, broker.Denied);
    }

    // ---- host-side name resolution ----

    private sealed class Wire : IDisposable
    {
        public readonly MediatedUdpClient Client;
        public readonly UdpBroker Broker;
        public volatile bool Granted = true;

        public Wire(UdpBrokerOptions options, Func<string, bool>? allow = null)
        {
            Client = new MediatedUdpClient(command => Broker!.Handle(command));
            Broker = new UdpBroker(options, () => Granted, Client.Deliver, 1, allow ?? (_ => true));
        }

        public async Task<IPAddress?> Resolve(string name) =>
            await Client.CreateTransport().ResolveAsync(name, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        public void Dispose() { Broker.Dispose(); Client.Close(); }
    }

    private static UdpBrokerOptions Resolving(Func<string, Task<IPAddress[]>> resolver, TimeProvider? clock = null) =>
        new() { Resolver = (name, _) => resolver(name), Clock = clock ?? new ManualClock() }; // no test relies on real time passing; a stalled runner must not time a lookup out

    private static Task<IPAddress[]> Answer(params string[] addresses) => Task.FromResult(addresses.Select(IPAddress.Parse).ToArray());

    [Fact]
    public async Task HostResolvesAnAllowedNameAndTheChildNeverDoes()
    {
        var asked = new ConcurrentQueue<string>();
        using var wire = new Wire(Resolving(name => { asked.Enqueue(name); return Answer("8.8.8.8"); }), name => name == "turn.example");
        Assert.Equal(IPAddress.Parse("8.8.8.8"), await wire.Resolve("turn.example"));
        Assert.Equal(["turn.example"], asked);
    }

    [Fact]
    public async Task WithoutAnAllowlistNoNameResolves()
    {
        var asked = 0;
        UdpBroker? broker = null;
        var client = new MediatedUdpClient(command => broker!.Handle(command));
        broker = new UdpBroker(Resolving(_ => { Interlocked.Increment(ref asked); return Answer("8.8.8.8"); }), () => true, client.Deliver);
        using var _ = broker;
        var answer = await client.CreateTransport().ResolveAsync("turn.example", TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.Null(answer);
        Assert.Equal(0, asked);
        Assert.Equal(1, broker.Denied);
    }

    [Fact]
    public async Task NamesOutsideTheHostAllowlistAreRefusedWithoutALookup()
    {
        var asked = 0;
        using var wire = new Wire(Resolving(_ => { Interlocked.Increment(ref asked); return Answer("8.8.8.8"); }), name => name == "turn.example");
        Assert.Null(await wire.Resolve("evil.example"));
        Assert.Equal(0, asked);
        Assert.Equal(1, wire.Broker.Denied);
    }

    [Theory]
    [InlineData("127.0.0.1")] [InlineData("10.0.0.5")] [InlineData("192.168.1.9")] [InlineData("169.254.1.1")] [InlineData("0.0.0.0")]
    public async Task ANameThatResolvesIntoTheHostsNetworkYieldsNothing(string address)
    {
        using var wire = new Wire(Resolving(_ => Answer(address)));
        Assert.Null(await wire.Resolve("rebind.example"));
    }

    [Fact]
    public async Task OnlyTheAllowedAnswerOfAMixedResultIsReturned()
    {
        using var wire = new Wire(Resolving(_ => Answer("10.0.0.5", "8.8.4.4")));
        Assert.Equal(IPAddress.Parse("8.8.4.4"), await wire.Resolve("mixed.example"));
    }

    [Fact]
    public async Task ResolutionFollowsTheSameLoopbackOptInAsSockets()
    {
        using var wire = new Wire(Resolving(_ => Answer("127.0.0.1")) with { AllowLoopback = true });
        Assert.Equal(IPAddress.Loopback, await wire.Resolve("local.test"));
    }

    [Theory]
    [InlineData("")] [InlineData("1.2.3.4")] [InlineData("has space.example")] [InlineData("a..b")]
    public async Task MalformedNamesAreRefused(string name)
    {
        var asked = 0;
        using var wire = new Wire(Resolving(_ => { Interlocked.Increment(ref asked); return Answer("8.8.8.8"); }));
        Assert.Null(await wire.Resolve(name));
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task OverlongNamesAreRefused()
    {
        using var wire = new Wire(Resolving(_ => Answer("8.8.8.8")));
        Assert.Null(await wire.Resolve(string.Join('.', Enumerable.Repeat(new string('a', 60), 5)))); // 304 characters
    }

    [Fact]
    public async Task ADocumentMayResolveOnlySoManyDistinctNames()
    {
        using var wire = new Wire(Resolving(_ => Answer("8.8.8.8")) with { MaxResolvedNames = 2 });
        Assert.NotNull(await wire.Resolve("a.example"));
        Assert.NotNull(await wire.Resolve("b.example"));
        Assert.Null(await wire.Resolve("c.example"));
        Assert.NotNull(await wire.Resolve("A.example")); // a repeat is not a new name
        wire.Broker.BeginDocument(2);
        wire.Client.Generation = 2;
        Assert.NotNull(await wire.Resolve("c.example")); // the new document starts over
    }

    [Fact]
    public async Task LookupsAreRateLimited()
    {
        using var wire = new Wire(Resolving(_ => Answer("8.8.8.8")) with { BurstResolves = 2, ResolvesPerSecond = 0.0001 });
        Assert.NotNull(await wire.Resolve("a.example"));
        Assert.NotNull(await wire.Resolve("b.example"));
        Assert.Null(await wire.Resolve("c.example"));
        Assert.Equal(1, wire.Broker.RateLimited);
    }

    [Fact]
    public async Task LookupsInFlightAreCappedAndNotQueued()
    {
        var gate = new TaskCompletionSource<IPAddress[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource();
        using var wire = new Wire(Resolving(name => { if (name == "slow.example") { started.TrySetResult(); return gate.Task; } return Answer("8.8.8.8"); }) with { MaxResolvesInFlight = 1 });
        var slow = wire.Resolve("slow.example");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.Null(await wire.Resolve("other.example")); // refused at once, not held behind the slow one
        gate.SetResult([IPAddress.Parse("8.8.4.4")]);
        Assert.Equal(IPAddress.Parse("8.8.4.4"), await slow);
        Assert.NotNull(await wire.Resolve("other.example")); // the slot came back
    }

    [Fact]
    public async Task ALookupThatNeverAnswersTimesOutOnTheInjectedClock()
    {
        var clock = new ManualClock();
        var stuck = new TaskCompletionSource<IPAddress[]>();
        using var wire = new Wire(Resolving(_ => stuck.Task, clock) with { ResolveTimeout = TimeSpan.FromSeconds(5) });
        var answer = wire.Resolve("hang.example");
        await Eventually(() => clock.PendingTimers == 1);
        Assert.False(answer.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Null(await answer);
    }

    [Fact]
    public async Task ALookupThatOutlivesItsTimeoutKeepsItsSlotUntilTheOsCallFinishes()
    {
        var clock = new ManualClock();
        var stuck = new TaskCompletionSource<IPAddress[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var wire = new Wire(Resolving(_ => { Interlocked.Increment(ref calls); return calls == 1 ? stuck.Task : Answer("8.8.8.8"); }, clock)
            with { MaxResolvesInFlight = 1, ResolveTimeout = TimeSpan.FromSeconds(5), BurstResolves = 10000 });
        var first = wire.Resolve("hang.example");
        await Eventually(() => clock.PendingTimers == 1);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Null(await first); // the child was answered...
        Assert.Null(await wire.Resolve("next.example")); // ...but the slot is still taken
        Assert.Equal(1, calls);
        stuck.SetResult([]); // the OS call finally returns
        IPAddress? retry = null;
        for (var i = 0; i < 5000 && retry is null; i++) { retry = await wire.Resolve("next.example"); await Task.Yield(); }
        Assert.NotNull(retry); // the slot came back once the OS call ended
    }

    [Fact]
    public async Task AnyResolverExceptionStillAnswersTheChild()
    {
        using var wire = new Wire(Resolving(_ => throw new InvalidProgramException("boom")));
        Assert.Null(await wire.Resolve("a.example"));
        using var faulted = new Wire(Resolving(_ => Task.FromException<IPAddress[]>(new NullReferenceException())));
        Assert.Null(await faulted.Resolve("b.example"));
    }

    [Fact]
    public async Task ARevokedCapabilityResolvesNothing()
    {
        var asked = 0;
        using var wire = new Wire(Resolving(_ => { Interlocked.Increment(ref asked); return Answer("8.8.8.8"); })) { Granted = false };
        Assert.Null(await wire.Resolve("turn.example"));
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task AResolverFailureIsAnAnswerNotACrash()
    {
        using var wire = new Wire(Resolving(_ => throw new System.Net.Sockets.SocketException(11001)));
        Assert.Null(await wire.Resolve("nowhere.example"));
        Assert.False(wire.Broker.Dead);
    }

    [Fact]
    public async Task ACommandFromAReplacedDocumentIsNotResolved()
    {
        var asked = 0;
        using var wire = new Wire(Resolving(_ => { Interlocked.Increment(ref asked); return Answer("8.8.8.8"); }));
        wire.Broker.BeginDocument(2); // the client still carries generation 1
        Assert.Null(await wire.Resolve("turn.example"));
        Assert.Equal(0, asked);
    }

    [Theory]
    [InlineData("turn:relay.example", "relay.example", true)]
    [InlineData("turn:relay.example:3478?transport=udp", "relay.example", true)]
    [InlineData("stun:relay.example:19302", "RELAY.example", true)]
    [InlineData("turn:relay.example", "other.example", false)]
    [InlineData("turn:sub.relay.example", "relay.example", false)]
    [InlineData("stun:other.example/relay.example", "relay.example", false)]
    [InlineData("relay.example", "relay.example", false)]
    public void TheHostMatchesNamesAgainstTheWebRtcAllowlistByHostPart(string entry, string name, bool expected) =>
        Assert.Equal(expected, ContentProcessClient.IsAllowedIceHost([entry], name));
}
