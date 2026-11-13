using Folio.Scripting.Dom;
using Folio.Scripting.Jint;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Folio.Scripting.Rtc;

namespace Folio.Scripting.Tests;

public sealed class TurnClientTests
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(100);

    private static string Server(FakeTurnServer server, string user = "user", string password = "pass", string query = "") =>
        IceAgent.WithCredentials($"turn:127.0.0.1:{server.Endpoint.Port}{query}", user, password);

    private static IceAgent Agent(FakeTurnServer server, string user = "user", string password = "pass") =>
        new([Server(server, user, password)]) { TurnRequestTimeout = Fast, TurnRetryBackoff = Fast };

    private static async Task Until(Func<bool> condition, int milliseconds = 8000)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, "Timed out waiting for the condition.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task AllocationWithCorrectCredentialsYieldsRelayCandidate()
    {
        using var server = new FakeTurnServer();
        using var agent = Agent(server);
        var gathered = new List<IceCandidate>();
        agent.OnCandidateGathered += candidate => { lock (gathered) gathered.Add(candidate); };
        await agent.StartGatheringAsync();

        var relay = Assert.Single(agent.LocalCandidates, c => c.Type == "relay");
        Assert.Equal(server.RelayEndpoint!.Address.ToString(), relay.Address);
        Assert.Equal(server.RelayEndpoint.Port, relay.Port);
        Assert.Equal(IceCandidate.ComputePriority("relay", 1), relay.Priority);
        Assert.True(relay.Priority < IceCandidate.ComputePriority("srflx", 1));
        lock (gathered) Assert.Contains(relay, gathered);
        Assert.Equal(1, server.AuthenticatedAllocates); // long-term MESSAGE-INTEGRITY verified by the server
        Assert.Equal(0, server.AuthFailures);
    }

    [Fact]
    public async Task WrongPasswordNeverYieldsARelayAndStopsRetrying()
    {
        using var server = new FakeTurnServer();
        using var agent = Agent(server, password: "wrong");
        await agent.StartGatheringAsync();
        Assert.DoesNotContain(agent.LocalCandidates, c => c.Type == "relay");
        var allocates = server.Allocates;
        Assert.Equal(2, allocates); // unauthenticated challenge + one credentialed attempt
        Assert.Equal(1, server.AuthFailures);
        await Task.Delay(800, TestContext.Current.CancellationToken);
        Assert.Equal(allocates, server.Allocates);
    }

    [Fact]
    public async Task StaleNonceIsRetriedOnceWithTheNewNonce()
    {
        using var server = new FakeTurnServer { StaleNonceOnce = true };
        using var agent = Agent(server);
        await agent.StartGatheringAsync();
        Assert.Single(agent.LocalCandidates, c => c.Type == "relay");
        Assert.Equal(3, server.Allocates); // challenge, stale (438), retried with the fresh nonce
    }

    [Fact]
    public async Task RelayedDatagramsFlowBothWaysThroughChannelData()
    {
        using var server = new FakeTurnServer();
        using var agent = Agent(server);
        agent.RelayOnly = true;
        agent.RemoteUfrag = "remote"; agent.RemotePassword = "remote-password";
        await agent.StartGatheringAsync();
        Assert.All(agent.LocalCandidates, c => Assert.Equal("relay", c.Type));

        using var peer = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        peer.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.OnConnectionStateChanged += state => { if (state == "connected") connected.TrySetResult(); };
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.OnDatagramReceived += data => received.TrySetResult(data);
        agent.AddRemoteCandidate(new IceCandidate
        {
            Foundation = "1", Component = 1, Protocol = "udp", Priority = 100, Type = "host",
            Address = "127.0.0.1", Port = ((IPEndPoint)peer.LocalEndPoint!).Port
        });

        // The peer sees the check arriving from the relayed address, never from the agent's own socket.
        var buffer = new byte[2048];
        var check = await peer.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), TestContext.Current.CancellationToken);
        Assert.Equal(server.RelayEndpoint, check.RemoteEndPoint);
        Assert.True(StunMessage.TryParse(buffer.AsSpan(0, check.ReceivedBytes), out var request));
        await peer.SendToAsync(StunMessage.Encode(StunMessage.BindingResponse, request.TransactionId.Span, [],
            Encoding.UTF8.GetBytes("remote-password")), SocketFlags.None, check.RemoteEndPoint);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);

        // Agent -> peer: Send indications until the channel is bound, then ChannelData.
        var payload = new byte[] { 0x17, 0xfe, 0xfd, 1, 2, 3 };
        await Until(() => { agent.SendDatagram(payload); return server.ChannelDataFromClient > 0; });
        Assert.True(server.ChannelBinds >= 1);
        var datagram = await peer.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), TestContext.Current.CancellationToken);
        Assert.Equal(server.RelayEndpoint, datagram.RemoteEndPoint);
        Assert.Equal(payload, buffer.AsSpan(0, datagram.ReceivedBytes).ToArray());

        // Peer -> agent, delivered to the same OnDatagramReceived as direct datagrams.
        var reply = new byte[] { 0x17, 0xfe, 0xfd, 9, 8, 7 };
        await peer.SendToAsync(reply, SocketFlags.None, server.RelayEndpoint);
        Assert.Equal(reply, await received.Task.WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PermissionAndAllocationRefreshAreSentOnSchedule()
    {
        using var server = new FakeTurnServer { Lifetime = 2 };
        using var agent = Agent(server);
        agent.TurnPermissionRefreshInterval = TimeSpan.FromMilliseconds(300);
        await agent.StartGatheringAsync();
        agent.AddRemoteCandidate(new IceCandidate
        {
            Foundation = "1", Component = 1, Protocol = "udp", Priority = 100, Type = "host", Address = "127.0.0.1", Port = 9
        });
        await Until(() => server.PermissionRequests >= 3); // the first plus at least two refreshes
        await Until(() => server.Refreshes >= 2);          // 2 s lifetime: refreshed about every second
        Assert.All(server.RefreshLifetimes, lifetime => Assert.Equal(600u, lifetime));
    }

    [Fact]
    public async Task DisposeReleasesTheAllocationAndStopsTimers()
    {
        using var server = new FakeTurnServer { Lifetime = 2 };
        var agent = Agent(server);
        agent.TurnPermissionRefreshInterval = TimeSpan.FromMilliseconds(200);
        await agent.StartGatheringAsync();
        agent.AddRemoteCandidate(new IceCandidate
        {
            Foundation = "1", Component = 1, Protocol = "udp", Priority = 100, Type = "host", Address = "127.0.0.1", Port = 9
        });
        await Until(() => server.PermissionRequests >= 2);
        agent.Dispose();
        await Until(() => server.RefreshLifetimes.Contains(0u));
        var total = server.TotalRequests;
        await Task.Delay(1500, TestContext.Current.CancellationToken);
        Assert.Equal(total, server.TotalRequests);
    }

    [Fact]
    public async Task TlsTcpAndCredentialLessServersAreSkippedWithAReason()
    {
        using var server = new FakeTurnServer();
        var port = server.Endpoint.Port;
        using var agent = new IceAgent([
            IceAgent.WithCredentials($"turns:127.0.0.1:{port}", "user", "pass"),
            IceAgent.WithCredentials($"turn:127.0.0.1:{port}?transport=tcp", "user", "pass"),
            $"turn:127.0.0.1:{port}"]) { TurnRequestTimeout = Fast };
        await agent.StartGatheringAsync();
        Assert.DoesNotContain(agent.LocalCandidates, c => c.Type == "relay");
        Assert.Equal(3, agent.TurnSkipReasons.Count);
        Assert.Contains(agent.TurnSkipReasons, r => r.Contains("TLS") && !r.Contains("pass"));
        Assert.Contains(agent.TurnSkipReasons, r => r.Contains("tcp"));
        Assert.Equal(0, server.TotalRequests);
        Assert.Equal("complete", agent.GatheringState);
    }

    [Theory]
    [InlineData("turn:relay.example", "relay.example", 3478, null)]
    [InlineData("turn:relay.example:3479?transport=udp", "relay.example", 3479, null)]
    [InlineData("turns:relay.example", "relay.example", 5349, "TLS")]
    [InlineData("turn:relay.example?transport=tcp", "relay.example", 3478, "tcp")]
    public void TurnUrlsParse(string url, string host, int port, string? skip)
    {
        Assert.True(IceAgent.TryParseTurnServer(IceAgent.WithCredentials(url, "u", "c"), out var h, out var p, out _, out _, out var reason));
        Assert.Equal((host, port), (h, p));
        if (skip is null) Assert.Null(reason); else Assert.Contains(skip, reason);
    }

    [Theory]
    [InlineData("turn:")]
    [InlineData("turn:bad host")]
    [InlineData("turn:relay.example?foo=bar")]
    [InlineData("turn:relay.example:99999")]
    [InlineData("stun:relay.example")]
    public void MalformedTurnUrlsAreRejected(string url) =>
        Assert.False(IceAgent.TryParseTurnServer(url, out _, out _, out _, out _, out _));

    [Fact]
    public void ServerOutsideTheHostPolicyIsNeverContacted()
    {
        using var blocked = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var allowed = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var blockedUrl = $"turn:{blocked.Client.LocalEndPoint}";
        var allowedUrl = $"turn:{allowed.Client.LocalEndPoint}";
        using IScriptEngine engine = new JintScriptEngine();
        using var document = Document.Parse("<html></html>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebRtc = true },
            WebRtcAllowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { allowedUrl }
        });
        using var loop = new HtmlEventLoop(engine);
        DomBindings.Install(engine, document, eventLoop: loop);
        try
        {
            engine.SetGlobalProperty("blocked", ScriptValue.FromString(blockedUrl));
            engine.SetGlobalProperty("permitted", ScriptValue.FromString(allowedUrl));
            Assert.Equal("SecurityError", engine.Evaluate("""
                (function(){
                    try { new RTCPeerConnection({iceServers:[{urls:blocked,username:'u',credential:'c'}]}); }
                    catch (error) { return error.name; }
                })()
                """).AsString());
            allowed.Client.ReceiveTimeout = 5000;
            engine.Evaluate("var peer=new RTCPeerConnection({iceServers:[{urls:permitted,username:'u',credential:'c'}]});peer.createDataChannel('x');peer.createOffer();");
            engine.PerformMicrotaskCheckpoint();
            IPEndPoint sender = new(IPAddress.Any, 0);
            Assert.True(StunMessage.TryParse(allowed.Receive(ref sender), out var allocate));
            Assert.Equal(StunMessage.AllocateRequest, allocate.Type);
            Assert.False(blocked.Client.Poll(0, SelectMode.SelectRead));
            engine.Evaluate("peer.close()");
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public async Task TwoAgentsConnectAndExchangeDatagramsWhenOneCanOnlyRelay()
    {
        using var server = new FakeTurnServer();
        using var a = Agent(server);
        using var b = new IceAgent { IsControlling = false };
        a.RelayOnly = true; // A never advertises or uses a direct (host/srflx) path
        (a.RemoteUfrag, a.RemotePassword) = (b.LocalUfrag, b.LocalPassword);
        (b.RemoteUfrag, b.RemotePassword) = (a.LocalUfrag, a.LocalPassword);
        var connected = new[] { new TaskCompletionSource(), new TaskCompletionSource() };
        a.OnConnectionStateChanged += state => { if (state == "connected") connected[0].TrySetResult(); };
        b.OnConnectionStateChanged += state => { if (state == "connected") connected[1].TrySetResult(); };
        var atB = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var atA = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.OnDatagramReceived += data => atB.TrySetResult(data);
        a.OnDatagramReceived += data => atA.TrySetResult(data);
        await Task.WhenAll(a.StartGatheringAsync(), b.StartGatheringAsync());
        Assert.All(a.LocalCandidates, c => Assert.Equal("relay", c.Type));

        foreach (var candidate in a.LocalCandidates) b.AddRemoteCandidate(candidate);
        foreach (var candidate in b.LocalCandidates.Where(c => c.Address == "127.0.0.1")) a.AddRemoteCandidate(candidate);
        await Task.WhenAll(connected.Select(c => c.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken)));

        var forward = new byte[] { 0x80, 1, 2, 3, 4 };
        var backward = new byte[] { 0x17, 9, 8, 7 };
        await Until(() => { a.SendDatagram(forward); return atB.Task.IsCompleted; });
        await Until(() => { b.SendDatagram(backward); return atA.Task.IsCompleted; });
        Assert.Equal(forward, await atB.Task);
        Assert.Equal(backward, await atA.Task);
    }

    /// <summary>In-process TURN server: long-term credentials, Allocate/Refresh/CreatePermission/ChannelBind, ChannelData relaying.</summary>
    private sealed class FakeTurnServer : IDisposable
    {
        private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private Socket? _relay;
        private IPEndPoint? _client;
        private readonly object _gate = new();
        private readonly HashSet<IPAddress> _permitted = [];
        private readonly Dictionary<ushort, IPEndPoint> _channelPeers = [];
        private readonly Dictionary<IPEndPoint, ushort> _peerChannels = [];
        private string _nonce = "nonce-1";
        private int _allocates, _authenticatedAllocates, _authFailures, _refreshes, _permissionRequests, _channelBinds, _channelDataFromClient, _total;
        private readonly ConcurrentQueue<uint> _refreshLifetimes = new();
        private const string Realm = "folio.test";

        public bool StaleNonceOnce { get; set; }
        public uint Lifetime { get; set; } = 600;
        public IPEndPoint Endpoint { get; }
        public IPEndPoint? RelayEndpoint => _relay?.LocalEndPoint as IPEndPoint;
        public int Allocates => Volatile.Read(ref _allocates);
        public int AuthenticatedAllocates => Volatile.Read(ref _authenticatedAllocates);
        public int AuthFailures => Volatile.Read(ref _authFailures);
        public int Refreshes => Volatile.Read(ref _refreshes);
        public int PermissionRequests => Volatile.Read(ref _permissionRequests);
        public int ChannelBinds => Volatile.Read(ref _channelBinds);
        public int ChannelDataFromClient => Volatile.Read(ref _channelDataFromClient);
        public int TotalRequests => Volatile.Read(ref _total);
        public IReadOnlyCollection<uint> RefreshLifetimes => _refreshLifetimes.ToArray();

        public FakeTurnServer()
        {
            _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            Endpoint = (IPEndPoint)_socket.LocalEndPoint!;
            new Thread(() => Pump(_socket, FromClient)) { IsBackground = true }.Start();
        }

        private static void Pump(Socket socket, Action<byte[], IPEndPoint> handle)
        {
            var buffer = new byte[65536];
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            while (true)
            {
                try
                {
                    var length = socket.ReceiveFrom(buffer, ref from);
                    handle(buffer.AsSpan(0, length).ToArray(), (IPEndPoint)from);
                }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { if (socket.SafeHandle.IsClosed) return; }
            }
        }

        private static byte[] ErrorAttribute(int code, string reason)
        {
            var text = Encoding.UTF8.GetBytes(reason);
            var value = new byte[4 + text.Length];
            value[2] = (byte)(code / 100); value[3] = (byte)(code % 100);
            text.CopyTo(value, 4);
            return value;
        }

        private static IPEndPoint DecodePeer(StunMessage message, ReadOnlyMemory<byte> value)
        {
            var bytes = value.Span;
            var port = BinaryPrimitives.ReadUInt16BigEndian(bytes[2..]) ^ 0x2112;
            var address = BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]) ^ StunMessage.Cookie;
            Span<byte> raw = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(raw, address);
            return new IPEndPoint(new IPAddress(raw), port);
        }

        private void Reply(IPEndPoint to, ushort type, StunMessage request, List<(ushort, byte[])> attributes, byte[]? key)
        {
            var packet = key is null ? StunMessage.EncodeRequest(type, request.TransactionId.Span, attributes)
                : StunMessage.Encode(type, request.TransactionId.Span, attributes, key);
            _socket.SendTo(packet, to);
        }

        private void FromClient(byte[] bytes, IPEndPoint from)
        {
            if (StunMessage.TryParseChannelData(bytes, out var channel, out var data))
            {
                IPEndPoint? peer;
                lock (_gate) _channelPeers.TryGetValue(channel, out peer);
                if (peer is not null && _relay is not null)
                {
                    Interlocked.Increment(ref _channelDataFromClient);
                    _relay.SendTo(data.ToArray(), peer);
                }
                return;
            }
            if (!StunMessage.TryParse(bytes, out var message)) return;
            if (message.Type == StunMessage.SendIndication)
            {
                if (message.TryAttribute(StunMessage.XorPeerAddress, out var peerValue) && message.TryAttribute(StunMessage.Data, out var payload))
                {
                    var peer = DecodePeer(message, peerValue);
                    bool permitted;
                    lock (_gate) permitted = _permitted.Contains(peer.Address);
                    if (permitted && _relay is not null) _relay.SendTo(payload.ToArray(), peer);
                }
                return;
            }
            Interlocked.Increment(ref _total);
            if (message.Type == StunMessage.AllocateRequest) Interlocked.Increment(ref _allocates);
            if (!message.TryText(StunMessage.Username, 255, out var username) || !message.TryAttribute(StunMessage.Integrity, out _))
            {
                Reply(from, (ushort)(message.Type | 0x0110), message, [(StunMessage.ErrorCode, ErrorAttribute(401, "Unauthorized")),
                    (StunMessage.Realm, Encoding.UTF8.GetBytes(Realm)), (StunMessage.Nonce, Encoding.UTF8.GetBytes(_nonce))], null);
                return;
            }
            if (StaleNonceOnce || !message.TryText(StunMessage.Nonce, 763, out var nonce) || nonce != _nonce)
            {
                StaleNonceOnce = false;
                _nonce = "nonce-" + RandomNumberGenerator.GetHexString(6);
                Reply(from, (ushort)(message.Type | 0x0110), message, [(StunMessage.ErrorCode, ErrorAttribute(438, "Stale Nonce")),
                    (StunMessage.Realm, Encoding.UTF8.GetBytes(Realm)), (StunMessage.Nonce, Encoding.UTF8.GetBytes(_nonce))], null);
                return;
            }
            var key = MD5.HashData(Encoding.UTF8.GetBytes($"user:{Realm}:pass"));
            if (username != "user" || !message.VerifyIntegritySha1(key))
            {
                Interlocked.Increment(ref _authFailures);
                Reply(from, (ushort)(message.Type | 0x0110), message, [(StunMessage.ErrorCode, ErrorAttribute(401, "Unauthorized")),
                    (StunMessage.Realm, Encoding.UTF8.GetBytes(Realm)), (StunMessage.Nonce, Encoding.UTF8.GetBytes(_nonce))], null);
                return;
            }
            switch (message.Type)
            {
                case StunMessage.AllocateRequest:
                    Interlocked.Increment(ref _authenticatedAllocates);
                    _client = from;
                    if (_relay is null)
                    {
                        var relay = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                        relay.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                        _relay = relay;
                        new Thread(() => Pump(relay, FromPeer)) { IsBackground = true }.Start();
                    }
                    var lifetime = new byte[4];
                    BinaryPrimitives.WriteUInt32BigEndian(lifetime, Lifetime);
                    Reply(from, StunMessage.AllocateResponse, message,
                    [
                        (StunMessage.XorRelayedAddress, StunMessage.EncodeXorAddress(RelayEndpoint!, message.TransactionId.Span)),
                        (StunMessage.XorMappedAddress, StunMessage.EncodeXorAddress(from, message.TransactionId.Span)),
                        (StunMessage.Lifetime, lifetime)
                    ], key);
                    break;
                case StunMessage.RefreshRequest:
                    message.TryLifetime(out var requested);
                    _refreshLifetimes.Enqueue(requested);
                    Interlocked.Increment(ref _refreshes);
                    var granted = new byte[4];
                    BinaryPrimitives.WriteUInt32BigEndian(granted, requested == 0 ? 0 : Lifetime);
                    Reply(from, StunMessage.RefreshResponse, message, [(StunMessage.Lifetime, granted)], key);
                    break;
                case StunMessage.CreatePermissionRequest:
                    lock (_gate) foreach (var value in message.AttributeValues(StunMessage.XorPeerAddress)) _permitted.Add(DecodePeer(message, value).Address);
                    Interlocked.Increment(ref _permissionRequests);
                    Reply(from, StunMessage.CreatePermissionResponse, message, [], key);
                    break;
                case StunMessage.ChannelBindRequest:
                    if (!message.TryAttribute(StunMessage.ChannelNumber, out var number) || !message.TryAttribute(StunMessage.XorPeerAddress, out var peerValue))
                        return;
                    var peer = DecodePeer(message, peerValue);
                    lock (_gate)
                    {
                        _channelPeers[BinaryPrimitives.ReadUInt16BigEndian(number.Span)] = peer;
                        _peerChannels[peer] = BinaryPrimitives.ReadUInt16BigEndian(number.Span);
                        _permitted.Add(peer.Address);
                    }
                    Interlocked.Increment(ref _channelBinds);
                    Reply(from, StunMessage.ChannelBindResponse, message, [], key);
                    break;
            }
        }

        // Datagram from a peer toward the allocation: dropped without a permission, ChannelData once bound.
        private void FromPeer(byte[] bytes, IPEndPoint from)
        {
            ushort channel;
            lock (_gate)
            {
                if (!_permitted.Contains(from.Address) || _client is null) return;
                _peerChannels.TryGetValue(from, out channel);
            }
            if (channel != 0) { _socket.SendTo(StunMessage.EncodeChannelData(channel, bytes), _client); return; }
            var transactionId = RandomNumberGenerator.GetBytes(12);
            _socket.SendTo(StunMessage.EncodeRequest(StunMessage.DataIndication, transactionId,
                [(StunMessage.XorPeerAddress, StunMessage.EncodeXorAddress(from, transactionId)), (StunMessage.Data, bytes)], fingerprint: false), _client);
        }

        public void Dispose()
        {
            _socket.Dispose();
            _relay?.Dispose();
        }
    }
}
