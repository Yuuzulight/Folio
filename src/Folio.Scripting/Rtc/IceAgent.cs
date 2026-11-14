using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using System.Globalization;

namespace Folio.Scripting.Rtc;

/// <summary>
/// Folio-owned ICE agent (RFC 5245 / RFC 8445) managing candidate gathering,
/// STUN connectivity checks, role resolution, and datagram transport.
/// </summary>
internal sealed class IceAgent : IDisposable
{
    public string LocalUfrag { get; }
    public string LocalPassword { get; }
    public string? RemoteUfrag { get; set; }
    public string? RemotePassword { get; set; }
    public bool IsControlling { get; set; } = true;
    public ulong TieBreaker { get; internal init; }

    public string GatheringState { get; private set; } = "new";
    public string ConnectionState { get; private set; } = "new";

    public event Action<IceCandidate>? OnCandidateGathered;
    public event Action<string>? OnGatheringStateChanged;
    public event Action<string>? OnConnectionStateChanged;
    public event Action<byte[]>? OnDatagramReceived;
    internal event Action? OnMaintenance;
    // Clock for consent freshness, the maintenance tick and STUN retransmission. Tests substitute a manual clock
    // and drive the tick themselves with RunMaintenance (AutoMaintenance = false).
    internal TimeProvider Clock { get; init; } = TimeProvider.System;
    internal bool AutoMaintenance { get; init; } = true;
    internal void RunMaintenance()
    {
        MaintainConsent(); OnMaintenance?.Invoke();
        foreach (var turn in _turnClients) turn.Maintain();
    }
    // Resolves once the connectivity-check pass that is running (or queued) has finished.
    internal async Task WhenChecksIdleAsync(CancellationToken cancellationToken)
    {
        await _checkSerial.WaitAsync(cancellationToken).ConfigureAwait(false);
        _checkSerial.Release();
    }

    private readonly List<IceCandidate> _localCandidates = [];
    private readonly List<IceCandidate> _remoteCandidates = [];
    private readonly List<CandidatePair> _pairs = [];
    private CandidatePair? _selectedPair;

    private IIceTransport? _transport;
    private readonly IIceTransport? _suppliedTransport;
    private int _localPort;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private bool _disposed;
    private IReadOnlyList<string> _iceServers;
    private readonly SemaphoreSlim _checkSerial = new(1, 1);
    private Thread? _receiveThread;
    private TurnClient[] _turnClients = [];
    private readonly Dictionary<IceCandidate, TurnClient> _relayClients = [];
    /// <summary>Advertise and use only relay candidates (iceTransportPolicy "relay").</summary>
    internal bool RelayOnly { get; set; }
    internal TimeSpan TurnRequestTimeout { get; set; } = TimeSpan.FromMilliseconds(500);
    internal TimeSpan TurnPermissionRefreshInterval { get; set; } = TimeSpan.FromMinutes(4);
    internal TimeSpan TurnRetryBackoff { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>Time source for TURN timers (retransmission, refresh); tests substitute a fake one.</summary>
    internal TimeProvider TurnClock { get => _turnClock ?? Clock; set => _turnClock = value; }
    private TimeProvider? _turnClock;
    internal IReadOnlyList<string> TurnSkipReasons { get { lock (_gate) return _turnSkipReasons.ToArray(); } }
    private readonly List<string> _turnSkipReasons = [];
    internal int LocalPort => _localPort;
    internal bool IsReceivePumpAlive => _receiveThread?.IsAlive == true;

    public IReadOnlyList<IceCandidate> LocalCandidates { get { lock (_gate) return _localCandidates.ToArray(); } }
    internal IReadOnlyList<IceCandidate> RemoteCandidates { get { lock (_gate) return _remoteCandidates.ToArray(); } }
    internal bool HasCandidatePair(IPEndPoint remote) { lock (_gate) return _pairs.Any(p => p.RemoteEndPoint.Equals(remote)); }

    public IceAgent(IReadOnlyList<string>? iceServers = null, IIceTransport? transport = null)
    {
        _suppliedTransport = transport;
        LocalUfrag = GenerateRandomString(8);
        LocalPassword = GenerateRandomString(24);
        Span<byte> tieBreakerBytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(tieBreakerBytes);
        TieBreaker = BinaryPrimitives.ReadUInt64BigEndian(tieBreakerBytes);
        _iceServers = iceServers?.ToArray() ?? [];
    }

    internal bool TrySetIceServers(string[] servers)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            if (GatheringState != "new") return _iceServers.SequenceEqual(servers, ServerEntryComparer.Instance);
            _iceServers = servers;
            return true;
        }
    }

    // RFC 7064 uses an opaque host and optional port, without a URI authority.
    // This transport currently opens only IPv4 sockets.
    internal static bool TryParseStunServer(string endpoint, out string host, out int port)
    {
        host = "";
        port = 3478;
        if (!endpoint.StartsWith("stun:", StringComparison.OrdinalIgnoreCase)) return false;
        var address = endpoint[5..];
        var colon = address.IndexOf(':');
        host = colon < 0 ? address : address[..colon];
        if (Uri.CheckHostName(host) is not (UriHostNameType.Dns or UriHostNameType.IPv4)) return false;
        return colon < 0 || (int.TryParse(address.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out port)
            && port is > 0 and <= 65535);
    }

    internal const char CredentialSeparator = '\u0001';
    private const int MaxTurnServers = 4;

    /// <summary>Appends TURN credentials to an ICE server URL for <see cref="TrySetIceServers"/> (kept out of any logging).</summary>
    internal static string WithCredentials(string url, string username, string credential) =>
        $"{url}{CredentialSeparator}{username}{CredentialSeparator}{credential}";

    /// <summary>URL part case-insensitive; TURN username and credential compared exactly.</summary>
    internal sealed class ServerEntryComparer : IEqualityComparer<string>
    {
        internal static readonly ServerEntryComparer Instance = new();
        public bool Equals(string? x, string? y)
        {
            if (x is null || y is null) return x is null && y is null;
            var (ux, uy) = (UrlOf(x), UrlOf(y));
            return string.Equals(ux, uy, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x[ux.Length..], y[uy.Length..], StringComparison.Ordinal);
        }
        public int GetHashCode(string value) => StringComparer.OrdinalIgnoreCase.GetHashCode(UrlOf(value));
    }

    internal static string UrlOf(string entry)
    {
        var at = entry.IndexOf(CredentialSeparator);
        return at < 0 ? entry : entry[..at];
    }

    /// <summary>
    /// Parses <c>turn:host[:port][?transport=udp]</c> (RFC 7065). Returns true for any well-formed turn/turns URL;
    /// <paramref name="skipReason"/> says why a well-formed server is nevertheless not used (TCP, TLS, no credentials).
    /// </summary>
    internal static bool TryParseTurnServer(string entry, out string host, out int port, out string username, out string credential, out string? skipReason)
    {
        host = ""; port = 3478; username = credential = ""; skipReason = null;
        var parts = entry.Split(CredentialSeparator);
        if (parts.Length is not (1 or 3)) return false;
        if (parts.Length == 3) (username, credential) = (parts[1], parts[2]);
        var url = parts[0];
        var tls = url.StartsWith("turns:", StringComparison.OrdinalIgnoreCase);
        if (!tls && !url.StartsWith("turn:", StringComparison.OrdinalIgnoreCase)) return false;
        var address = url[(tls ? 6 : 5)..];
        string? transport = null;
        var query = address.IndexOf('?');
        if (query >= 0)
        {
            var pair = address[(query + 1)..];
            address = address[..query];
            if (!pair.StartsWith("transport=", StringComparison.OrdinalIgnoreCase)) return false;
            transport = pair[10..].ToLowerInvariant();
            if (transport is not ("udp" or "tcp")) return false;
        }
        var colon = address.IndexOf(':');
        host = colon < 0 ? address : address[..colon];
        if (Uri.CheckHostName(host) is not (UriHostNameType.Dns or UriHostNameType.IPv4)) return false;
        if (tls) port = 5349;
        if (colon >= 0 && !(int.TryParse(address.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535)) return false;
        skipReason = tls ? "turns: (TLS) is not supported; only UDP transport to the TURN server is implemented"
            : transport == "tcp" ? "transport=tcp is not supported; only UDP transport to the TURN server is implemented"
            : username.Length == 0 || credential.Length == 0 ? "TURN requires a username and credential"
            : null;
        return true;
    }

    private static string GenerateRandomString(int length)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        var sb = new StringBuilder(length);
        foreach (var b in bytes) sb.Append(chars[b % chars.Length]);
        return sb.ToString();
    }

    public async Task StartGatheringAsync()
    {
        lock (_gate)
        {
            if (_disposed || GatheringState != "new") return;
            GatheringState = "gathering";
        }
        OnGatheringStateChanged?.Invoke("gathering");

        var bound = BindSocket();
        if (bound)
        {
            PermitRemoteCandidates();
            GatherHostCandidates();
            await Task.WhenAll(GatherServerReflexiveCandidatesAsync(), GatherRelayCandidatesAsync()).ConfigureAwait(false);
        }

        lock (_gate)
        {
            GatheringState = "complete";
            // Remote SDP may arrive before local gathering. Rebuild pairs
            // now instead of requiring a duplicate trickled candidate.
            FormPairs();
        }
        OnGatheringStateChanged?.Invoke("complete");
        if (!bound)
        {
            // No transport (for example the host refused a socket): nothing can ever connect.
            lock (_gate) { if (_disposed) return; ConnectionState = "failed"; }
            OnConnectionStateChanged?.Invoke("failed");
            return;
        }
        _ = CheckConnectivityAsync();
    }

    private bool BindSocket()
    {
        lock (_gate)
        {
            if (_transport is not null) return true;
            if (_disposed) return false;
            IIceTransport? transport = null;
            try
            {
                transport = _suppliedTransport ?? IceTransportProvider.Create();
                _localPort = transport.Bind().Port;
            }
            catch (Exception error) when (error is SocketException or IOException or InvalidOperationException or TimeoutException or ObjectDisposedException)
            {
                transport?.Dispose();
                return false;
            }
            _transport = transport;
            _receiveThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "Folio ICE receive" };
            _receiveThread.Start();
            return true;
        }
    }

    private void GatherHostCandidates()
    {
        if (RelayOnly) return;
        var addresses = new HashSet<IPAddress>();
        try
        {
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up) continue;
                var props = iface.GetIPProperties();
                foreach (var unicast in props.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(unicast.Address))
                        addresses.Add(unicast.Address);
                }
            }
        }
        catch { }

        addresses.Add(IPAddress.Loopback);

        var foundationIndex = 1;
        foreach (var addr in addresses)
        {
            var candidate = new IceCandidate
            {
                Foundation = foundationIndex.ToString(),
                Component = 1,
                Protocol = "udp",
                Priority = IceCandidate.ComputePriority("host", 1),
                Address = addr.ToString(),
                Port = _localPort,
                Type = "host",
                UsernameFragment = LocalUfrag
            };
            foundationIndex++;

            lock (_gate) _localCandidates.Add(candidate);
            OnCandidateGathered?.Invoke(candidate);
        }
    }

    private async Task GatherServerReflexiveCandidatesAsync()
    {
        if (_iceServers.Count == 0 || RelayOnly) return;
        var budget = TimeSpan.FromSeconds(2);
        var started = Stopwatch.GetTimestamp();
        using var deadline = new CancellationTokenSource(budget);
        foreach (var serverUrl in _iceServers)
        {
            if (deadline.IsCancellationRequested || _cts.IsCancellationRequested) break;
            if (!TryParseStunServer(serverUrl, out var host, out var port)) continue;

            try
            {
                using var lookup = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, deadline.Token);
                var ipv4 = await ResolveAsync(host, lookup.Token).ConfigureAwait(false);
                if (ipv4 is null) continue;
                var remaining = budget - Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero) break;

                var endpoint = new IPEndPoint(ipv4, port);
                var transactionId = new byte[12];
                RandomNumberGenerator.Fill(transactionId);
                var request = StunMessage.EncodeRequest(StunMessage.BindingRequest, transactionId);

                var (response, _) = await SendStunRequestAsync(request, transactionId, endpoint, remaining).ConfigureAwait(false);
                if (response is not null && response.TryXorMappedAddress(out var mapped) && mapped is not null)
                {
                    var srflx = new IceCandidate
                    {
                        Foundation = (_localCandidates.Count + 1).ToString(),
                        Component = 1,
                        Protocol = "udp",
                        Priority = IceCandidate.ComputePriority("srflx", 1),
                        Address = mapped.Address.ToString(),
                        Port = mapped.Port,
                        Type = "srflx",
                        RelatedAddress = IPAddress.Loopback.ToString(),
                        RelatedPort = _localPort,
                        UsernameFragment = LocalUfrag
                    };
                    lock (_gate) _localCandidates.Add(srflx);
                    OnCandidateGathered?.Invoke(srflx);
                }
            }
            catch { }
        }
    }

    private async Task GatherRelayCandidatesAsync()
    {
        string[] servers;
        lock (_gate) servers = _iceServers.ToArray();
        var started = 0;
        var tasks = new List<Task>();
        foreach (var entry in servers)
        {
            if (!TryParseTurnServer(entry, out var host, out var port, out var username, out var credential, out var skip)) continue;
            if (skip is not null) { lock (_gate) _turnSkipReasons.Add($"{UrlOf(entry)}: {skip}"); continue; }
            if (started++ >= MaxTurnServers) break;
            tasks.Add(GatherRelayAsync(host, port, username, credential));
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task GatherRelayAsync(string host, int port, string username, string credential)
    {
        TurnClient? client = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var transport = _transport;
            if (transport is null) return;
            var ipv4 = await ResolveAsync(host, deadline.Token).ConfigureAwait(false);
            if (ipv4 is null) return;
            // The host policy allowlists a name or literal. A name that resolves to a loopback/private/reserved
            // address (rebinding, misconfiguration) is refused; only an allowlisted IP literal may be non-public,
            // which is also the only case where relaying toward non-public peers is permitted.
            if (TurnClient.IsNonPublic(ipv4) && !IPAddress.TryParse(host, out _))
            {
                lock (_gate) _turnSkipReasons.Add($"turn:{host}: resolved to a non-public address");
                return;
            }
            var server = new IPEndPoint(ipv4, port);
            if (!transport.CanSendTo(server))
            {
                lock (_gate) _turnSkipReasons.Add($"turn:{host}: the host does not allow datagrams to this server");
                return;
            }
            client = new TurnClient(server, username, credential, transport, TurnClock)
            {
                RequestTimeout = TurnRequestTimeout,
                PermissionRefreshInterval = TurnPermissionRefreshInterval,
                RetryBackoff = TurnRetryBackoff
            };
            lock (_gate)
            {
                if (_disposed) { client.Dispose(); client = null; return; }
                _turnClients = [.. _turnClients, client];
            }
            var owned = client;
            client.OnPeerData += (peer, data) => ProcessDatagram(data, peer, owned);
            if (!await client.AllocateAsync(deadline.Token).ConfigureAwait(false) || client.Relayed is not { } relayed) return;
            var related = RelayOnly ? null : client.Mapped;
            var candidate = new IceCandidate
            {
                Foundation = $"relay{_turnClients.Length}",
                Component = 1,
                Protocol = "udp",
                Priority = IceCandidate.ComputePriority("relay", 1),
                Address = relayed.Address.ToString(),
                Port = relayed.Port,
                Type = "relay",
                RelatedAddress = related?.Address.ToString() ?? IPAddress.Any.ToString(),
                RelatedPort = related?.Port ?? 0,
                UsernameFragment = LocalUfrag
            };
            lock (_gate)
            {
                if (_disposed) return;
                _localCandidates.Add(candidate);
                _relayClients[candidate] = client;
            }
            client = null; // ownership moved to the agent
            OnCandidateGathered?.Invoke(candidate);
        }
        catch { }
        finally
        {
            if (client is not null)
            {
                lock (_gate) _turnClients = _turnClients.Where(c => !ReferenceEquals(c, client)).ToArray();
                client.Dispose();
            }
        }
    }

    // Names are resolved by the transport: in the sandbox that is the host, never the content process.
    private async Task<IPAddress?> ResolveAsync(string host, CancellationToken cancellation)
    {
        if (IPAddress.TryParse(host, out var literal)) return literal.AddressFamily == AddressFamily.InterNetwork ? literal : null;
        var transport = _transport;
        return transport is null ? null : await transport.ResolveAsync(host, cancellation).ConfigureAwait(false);
    }

    private async Task<(StunMessage? Message, EndPoint? From)> SendStunRequestAsync(
        byte[] request, byte[] transactionId, IPEndPoint destination, TimeSpan timeout, byte[]? integrityKey = null,
        Action<StunMessage>? validatedResponse = null, TurnClient? relay = null)
    {
        var transport = _transport;
        if (transport is null) return (null, null);
        var tcs = new TaskCompletionSource<(StunMessage, EndPoint)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var responseAccepted = 0;

        void Handler(StunMessage msg, EndPoint from, TurnClient? via)
        {
            if (from.Equals(destination) && ReferenceEquals(via, relay) && msg.Type is StunMessage.BindingResponse or StunMessage.BindingErrorResponse &&
                msg.TransactionId.Span.SequenceEqual(transactionId) &&
                (integrityKey is null || (msg.VerifyIntegritySha1(integrityKey) && msg.VerifyFingerprint())) &&
                Interlocked.CompareExchange(ref responseAccepted, 1, 0) == 0)
            {
                // Commit authenticated transport state before the receive loop
                // can encounter DTLS immediately following this STUN response.
                try { validatedResponse?.Invoke(msg); }
                finally { tcs.TrySetResult((msg, from)); }
            }
        }

        lock (_gate)
        {
            if (_disposed || _pendingStunRequests.Count >= 64) return (null, null);
            _pendingStunRequests.Add(Handler);
        }
        try
        {
            var attempts = integrityKey is null ? 1 : 3;
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeout.TotalMilliseconds * ((1 << attempts) - 1)), Clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, timeoutCts.Token);
            using (linked.Token.Register(() =>
            {
                // A response and timeout compete for the same transaction.
                // A late authenticated response cannot revive an expired check.
                if (Interlocked.CompareExchange(ref responseAccepted, 2, 0) == 0)
                    tcs.TrySetCanceled(linked.Token);
            }))
            {
                for (var attempt = 0; attempt < attempts; attempt++)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    if (relay is null) transport.SendTo(request, destination);
                    else relay.SendToPeer(destination, request);
                    var retry = Task.Delay(TimeSpan.FromMilliseconds(timeout.TotalMilliseconds * (1 << attempt)), Clock, linked.Token);
                    if (await Task.WhenAny(tcs.Task, retry).ConfigureAwait(false) == tcs.Task)
                    {
                        var result = await tcs.Task.ConfigureAwait(false);
                        return (result.Item1, result.Item2);
                    }
                    await retry.ConfigureAwait(false);
                }
                if (Interlocked.CompareExchange(ref responseAccepted, 2, 0) == 0) tcs.TrySetCanceled();
                var final = await tcs.Task.ConfigureAwait(false);
                return (final.Item1, final.Item2);
            }
        }
        catch
        {
            return (null, null);
        }
        finally
        {
            lock (_gate) _pendingStunRequests.Remove(Handler);
        }
    }

    private readonly List<Action<StunMessage, EndPoint, TurnClient?>> _pendingStunRequests = [];

    public void AddRemoteCandidate(IceCandidate candidate)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (candidate.Component != 1 || !candidate.Protocol.Equals("udp", StringComparison.OrdinalIgnoreCase) ||
                candidate.Port is < 1 or > 65535 || _remoteCandidates.Count >= 256 ||
                _remoteCandidates.Any(existing => existing.Address == candidate.Address && existing.Port == candidate.Port)) return;
            _remoteCandidates.Add(candidate);
            FormPairs();
        }
        PermitRemoteCandidates();
        // Relay permissions are per peer IP and must exist before the TURN server forwards anything from it.
        if (IPAddress.TryParse(candidate.Address, out var peerIp))
            foreach (var client in _turnClients) _ = client.EnsurePermissionAsync(peerIp);
        else if (candidate.Address.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            _ = ResolveMdnsCandidateAsync(candidate);
        _ = CheckConnectivityAsync();
    }

    // At most 4 lookups run at once; further candidates wait for a slot within the same 3 s budget instead of being dropped.
    private readonly SemaphoreSlim _mdnsSlots = new(4);

    private async Task ResolveMdnsCandidateAsync(IceCandidate candidate)
    {
        if (_disposed) return;
        if (_transport is null && !BindSocket()) return;
        var transport = _transport;
        if (transport is null || _disposed) return;
        var slot = false;
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, _cts.Token);
            await _mdnsSlots.WaitAsync(linked.Token).ConfigureAwait(false);
            slot = true;
            var ip = await transport.ResolveMdnsAsync(candidate.Address, linked.Token).ConfigureAwait(false);
            if (ip is not null && !_disposed)
            {
                var resolved = candidate with { Address = ip.ToString() };
                AddRemoteCandidate(resolved);
            }
        }
        catch { }
        finally
        {
            if (slot) _mdnsSlots.Release();
        }
    }

    private readonly HashSet<IPAddress> _permitted = [];

    // A host-mediated transport only delivers datagrams from addresses we sent to or authorised, and a peer's first
    // check can arrive before ours leaves, so every remote candidate address is authorised. Called outside the gate.
    private void PermitRemoteCandidates()
    {
        var transport = _transport;
        if (transport is null) return;
        IPAddress[] fresh;
        lock (_gate)
            fresh = _remoteCandidates.Select(c => IPAddress.TryParse(c.Address, out var ip) ? ip : null)
                .Where(ip => ip is not null && ip.AddressFamily == AddressFamily.InterNetwork && _permitted.Add(ip)).ToArray()!;
        foreach (var address in fresh)
        {
            try { transport.Permit(address); } catch (Exception error) when (error is ObjectDisposedException or IOException) { }
        }
    }

    private void FormPairs()
    {
        foreach (var local in _localCandidates)
        {
            foreach (var remote in _remoteCandidates)
            {
                if (!string.Equals(local.Protocol, remote.Protocol, StringComparison.OrdinalIgnoreCase)) continue;
                if (!IPAddress.TryParse(remote.Address, out var remoteIp)) continue;
                _relayClients.TryGetValue(local, out var relay);

                var remoteEp = new IPEndPoint(remoteIp, remote.Port);
                if (_pairs.Any(p => p.Local == local && p.RemoteEndPoint.Equals(remoteEp))) continue;

                var pair = new CandidatePair(local, remoteEp, local.Priority, remote.Priority, IsControlling) { Relay = relay };
                if (_pairs.Count >= 512) return;
                _pairs.Add(pair);
            }
        }
        _pairs.Sort((a, b) => b.Priority.CompareTo(a.Priority));
    }

    private async Task CheckConnectivityAsync()
    {
        try
        {
            await _checkSerial.WaitAsync(_cts.Token).ConfigureAwait(false);
            try { await CheckConnectivityCoreAsync().ConfigureAwait(false); }
            finally { _checkSerial.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task CheckConnectivityCoreAsync()
    {
        if (string.IsNullOrEmpty(RemoteUfrag) || string.IsNullOrEmpty(RemotePassword)) return;
        lock (_gate)
        {
            if (_disposed || ConnectionState == "connected") return;
            if (ConnectionState == "new")
            {
                ConnectionState = "checking";
                OnConnectionStateChanged?.Invoke("checking");
            }
        }

        CandidatePair[] candidates;
        int epoch;
        lock (_gate) { candidates = _pairs.ToArray(); epoch = _roleEpoch; }

        foreach (var pair in candidates)
        {
            // A role switch re-prioritised the pairs and queued a fresh pass; this snapshot is stale.
            if (_disposed || Volatile.Read(ref _roleEpoch) != epoch) break;
            // A relayed pair leaves through the TURN server, whose address was vetted when it was gathered.
            if (pair.Relay is null && _transport is { } route && !route.CanSendTo(pair.RemoteEndPoint)) continue;
            if (pair.Relay is not null && !await pair.Relay.EnsurePermissionAsync(pair.RemoteEndPoint.Address).ConfigureAwait(false)) continue;
            try
            {
                StunMessage? response = null;
                // One retry after an authenticated 487 flips our role.
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var role = IsControlling;
                    var (request, transactionId, key) = BuildBindingRequest(pair, role, nominate: role);
                    (response, _) = await SendStunRequestAsync(request, transactionId, pair.RemoteEndPoint,
                        TimeSpan.FromMilliseconds(500), key, message => AcceptConnectivityResponse(pair, message), pair.Relay).ConfigureAwait(false);
                    if (response is null || !response.TryErrorCode(out var code) || code != 487) break;
                    lock (_gate)
                    {
                        // Another check may already have switched; only flip if our role is still the one that conflicted.
                        if (_disposed) return;
                        if (IsControlling == role) SwitchRoleLocked(!role);
                        // Our own 487-driven switch retries inline and must not queue another pass (a peer that always
                        // answers 487 would otherwise loop); only an inbound authenticated request triggers one.
                        epoch = _roleEpoch;
                    }
                }
                if (response is not null && response.Type == StunMessage.BindingResponse)
                {
                    if (ConnectionState == "connected") return;
                }
            }
            catch { }
        }
    }

    // RFC 8445 7.3.1.1: pair priorities depend on the role, so a switch recomputes and re-sorts them. Caller holds _gate.
    private readonly Dictionary<string, bool> _seenRequests = [];
    private readonly Queue<string> _seenOrder = new();
    private int _roleEpoch;
    private void SwitchRoleLocked(bool controlling)
    {
        IsControlling = controlling;
        _roleEpoch++;
        foreach (var pair in _pairs) pair.Recompute(controlling);
        _pairs.Sort((a, b) => b.Priority.CompareTo(a.Priority));
    }

    private (byte[] Request, byte[] TransactionId, byte[] Key) BuildBindingRequest(CandidatePair pair, bool controlling, bool nominate)
    {
        var transactionId = new byte[12];
        RandomNumberGenerator.Fill(transactionId);
        var priorityBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(priorityBytes, (uint)pair.Local.Priority);
        var tieBreakerBytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(tieBreakerBytes, TieBreaker);
        var attributes = new List<(ushort Type, byte[] Value)>
        {
            (StunMessage.Username, Encoding.UTF8.GetBytes($"{RemoteUfrag}:{LocalUfrag}")),
            (StunMessage.Priority, priorityBytes),
            (controlling ? StunMessage.IceControlling : StunMessage.IceControlled, tieBreakerBytes),
        };
        if (nominate) attributes.Add((StunMessage.UseCandidate, []));
        var key = Encoding.UTF8.GetBytes(RemotePassword ?? "");
        return (StunMessage.Encode(StunMessage.BindingRequest, transactionId, attributes, key), transactionId, key);
    }

    // RFC 7675: a connected agent keeps asking the selected peer for consent with authenticated checks every few
    // seconds (randomised), and stops sending once no check has been answered for the consent timeout.
    internal TimeSpan ConsentInterval { get; set; } = TimeSpan.FromSeconds(5);
    internal TimeSpan ConsentTimeout { get; set; } = TimeSpan.FromSeconds(30);
    private long _consentConfirmed;
    private long _consentNextCheck;
    private int _consentInFlight;

    private void ConfirmConsent()
    {
        var now = Clock.GetTimestamp();
        Volatile.Write(ref _consentConfirmed, now);
        if (Volatile.Read(ref _consentNextCheck) == 0) Volatile.Write(ref _consentNextCheck, now + NextConsentDelay());
    }

    private long NextConsentDelay() =>
        (long)(ConsentInterval.TotalSeconds * (0.8 + 0.4 * Random.Shared.NextDouble()) * Clock.TimestampFrequency);

    private void MaintainConsent()
    {
        CandidatePair? pair;
        var expired = false;
        var now = Clock.GetTimestamp();
        lock (_gate)
        {
            if (_disposed || ConnectionState != "connected" || _selectedPair is null) return;
            pair = _selectedPair;
            if (Clock.GetElapsedTime(Volatile.Read(ref _consentConfirmed), now) > ConsentTimeout)
            {
                _selectedPair = null;
                ConnectionState = "failed";
                expired = true;
            }
        }
        if (expired)
        {
            OnConnectionStateChanged?.Invoke("failed");
            return;
        }
        if (now < Volatile.Read(ref _consentNextCheck) || Interlocked.Exchange(ref _consentInFlight, 1) != 0) return;
        Volatile.Write(ref _consentNextCheck, now + NextConsentDelay());
        ConsentCheck = CheckConsentAsync(pair);
    }

    internal Task ConsentCheck { get; private set; } = Task.CompletedTask;

    private async Task CheckConsentAsync(CandidatePair pair)
    {
        try
        {
            var (request, transactionId, key) = BuildBindingRequest(pair, IsControlling, nominate: false);
            await SendStunRequestAsync(request, transactionId, pair.RemoteEndPoint, TimeSpan.FromMilliseconds(500), key,
                response => { if (response.Type == StunMessage.BindingResponse) ConfirmConsent(); }, pair.Relay).ConfigureAwait(false);
        }
        finally { Volatile.Write(ref _consentInFlight, 0); }
    }

    private void AcceptConnectivityResponse(CandidatePair pair, StunMessage response)
    {
        if (response.Type != StunMessage.BindingResponse) return;
        var newlyConnected = false;
        lock (_gate)
        {
            if (_disposed) return;
            pair.State = CandidatePairState.Succeeded;
            if (IsControlling && (_selectedPair is null || pair.Priority > _selectedPair.Priority))
            {
                _selectedPair = pair;
                newlyConnected = ConnectionState != "connected";
                ConnectionState = "connected";
                ConfirmConsent();
            }
        }
        if (newlyConnected) OnConnectionStateChanged?.Invoke("connected");
    }

    private void ReceiveLoop()
    {
        var buffer = new byte[65536];
        EndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);
        var maintenance = Clock.GetTimestamp();
        var errors = 0;

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var transport = _transport;
                if (transport is null) break;
                if (AutoMaintenance && Clock.GetElapsedTime(maintenance) >= TimeSpan.FromMilliseconds(250))
                { maintenance = Clock.GetTimestamp(); RunMaintenance(); }
                // Socket completions and transport retry timers must not wait
                // behind synchronous page work on the shared CLR thread pool.
                var received = transport.Receive(buffer, 250000, ref remoteEp);
                if (received < 0) continue;
                var bytes = buffer.AsSpan(0, received);

                // Only the configured TURN server's own datagrams are unwrapped; its STUN replies never reach ICE checks.
                var consumed = false;
                foreach (var turn in _turnClients)
                    if (turn.Server.Equals(remoteEp) && turn.TryHandleDatagram(bytes)) { consumed = true; break; }
                if (!consumed) ProcessDatagram(bytes, remoteEp, null);
                errors = 0;
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { FailTransport(); break; } // the host closed the socket (revocation, replacement, quota)
            catch
            {
                // A persistent fault must not spin: back off, then give up and fail the connection.
                if (++errors > 50) { FailTransport(); break; }
                if (errors > 5) Thread.Sleep(Math.Min(errors * 5, 100));
            }
        }
    }

    // The transport is gone without this agent closing it: the connection fails once, and the consent loop (which runs
    // on the receive thread) ends with the thread.
    private void FailTransport()
    {
        lock (_gate)
        {
            if (_disposed || ConnectionState is "failed" or "closed") return;
            _selectedPair = null;
            ConnectionState = "failed";
        }
        OnConnectionStateChanged?.Invoke("failed");
    }

    // A datagram from a peer, received directly (via == null) or unwrapped from a TURN relay.
    private void ProcessDatagram(ReadOnlySpan<byte> bytes, EndPoint remoteEp, TurnClient? via)
    {
        if (StunMessage.TryParse(bytes, out var stun))
        {
            HandleStunMessage(stun, remoteEp, via);
            return;
        }
        // Non-STUN datagram: DTLS or SRTP packet
        lock (_gate)
            if (_selectedPair is null || !_selectedPair.RemoteEndPoint.Equals(remoteEp) || !ReferenceEquals(_selectedPair.Relay, via)) return;
        var copy = bytes.ToArray();
        OnDatagramReceived?.Invoke(copy);
    }

    private void HandleStunMessage(StunMessage msg, EndPoint from, TurnClient? via)
    {
        // Notify any pending request waiters
        Action<StunMessage, EndPoint, TurnClient?>[] handlers;
        lock (_gate) handlers = _pendingStunRequests.ToArray();
        foreach (var handler in handlers) handler(msg, from, via);

        if (msg.Type == StunMessage.BindingRequest)
        {
            // Verify and respond to incoming STUN check
            if (!string.IsNullOrEmpty(LocalPassword) && !string.IsNullOrEmpty(RemoteUfrag) &&
                msg.TryAttribute(StunMessage.Username, out var username) &&
                username.Span.SequenceEqual(Encoding.UTF8.GetBytes($"{LocalUfrag}:{RemoteUfrag}")) &&
                msg.TryAttribute(StunMessage.Priority, out var priority) && priority.Length == 4 &&
                msg.VerifyFingerprint() && msg.VerifyIntegritySha1(Encoding.UTF8.GetBytes(LocalPassword)))
            {
                if (from is IPEndPoint ep)
                {
                    var conflict = false;
                    var switched = false;
                    lock (_gate)
                    {
                        // A repeated transaction id is a retransmission or a replay: repeat the earlier decision
                        // without re-evaluating the role attributes.
                        var txKey = Convert.ToHexString(msg.TransactionId.Span);
                        if (_seenRequests.TryGetValue(txKey, out var earlier)) conflict = earlier;
                        else
                        {
                        var roleBefore = IsControlling;
                        var peerControlling = msg.TryAttribute(StunMessage.IceControlling, out var cing) && cing.Length == 8;
                        var peerControlled = msg.TryAttribute(StunMessage.IceControlled, out var ced) && ced.Length == 8;
                        if (peerControlling && IsControlling)
                        {
                            if (TieBreaker >= BinaryPrimitives.ReadUInt64BigEndian(cing.Span)) conflict = true;
                            else SwitchRoleLocked(false);
                        }
                        else if (peerControlled && !IsControlling)
                        {
                            if (TieBreaker >= BinaryPrimitives.ReadUInt64BigEndian(ced.Span)) SwitchRoleLocked(true);
                            else conflict = true;
                        }
                        switched = IsControlling != roleBefore;
                        if (_seenOrder.Count >= 256) _seenRequests.Remove(_seenOrder.Dequeue());
                        _seenOrder.Enqueue(txKey);
                        _seenRequests[txKey] = conflict;
                        }
                    }
                    if (switched) _ = CheckConnectivityAsync();
                    if (conflict)
                    {
                        var err = StunMessage.Encode(StunMessage.BindingErrorResponse, msg.TransactionId.Span,
                            [(StunMessage.ErrorCode, StunMessage.EncodeErrorCode(487, "Role Conflict"))], Encoding.UTF8.GetBytes(LocalPassword));
                        if (via is not null) via.SendToPeer(ep, err);
                        else _transport?.SendTo(err, ep);
                        return;
                    }
                    var xorAddr = new byte[8];
                    xorAddr[1] = 1; // IPv4
                    BinaryPrimitives.WriteUInt16BigEndian(xorAddr.AsSpan(2), (ushort)(ep.Port ^ 0x2112));
                    var ipBytes = ep.Address.GetAddressBytes();
                    BinaryPrimitives.WriteUInt32BigEndian(xorAddr.AsSpan(4), BinaryPrimitives.ReadUInt32BigEndian(ipBytes) ^ StunMessage.Cookie);

                    var attrs = new List<(ushort Type, byte[] Value)>
                    {
                        (StunMessage.XorMappedAddress, xorAddr)
                    };

                    var resp = StunMessage.Encode(StunMessage.BindingResponse, msg.TransactionId.Span, attrs, Encoding.UTF8.GetBytes(LocalPassword));
                    if (via is not null) via.SendToPeer(ep, resp);
                    else _transport?.SendTo(resp, ep);
                    // An authenticated check proves the peer's source address,
                    // including when SDP deliberately hides it behind mDNS.
                    AddRemoteCandidate(new IceCandidate
                    {
                        Foundation = "peer-reflexive",
                        Component = 1,
                        Protocol = "udp",
                        Priority = BinaryPrimitives.ReadUInt32BigEndian(priority.Span),
                        Address = ep.Address.ToString(),
                        Port = ep.Port,
                        Type = "prflx"
                    });
                    var nominated = false;
                    lock (_gate)
                    {
                        var pair = _pairs.FirstOrDefault(candidate => candidate.RemoteEndPoint.Equals(ep) && ReferenceEquals(candidate.Relay, via));
                        if (!_disposed && !IsControlling && pair is not null &&
                            msg.TryAttribute(StunMessage.UseCandidate, out var useCandidate) && useCandidate.IsEmpty && _selectedPair is null)
                        {
                            pair.State = CandidatePairState.Succeeded;
                            _selectedPair = pair; ConnectionState = "connected"; nominated = true;
                            ConfirmConsent();
                        }
                    }
                    if (nominated) OnConnectionStateChanged?.Invoke("connected");
                }
            }
        }
    }

    public void SendDatagram(ReadOnlySpan<byte> datagram)
    {
        var pair = _selectedPair;
        var transport = _transport;
        if (pair is null || transport is null) return;
        try
        {
            if (pair.Relay is not null) pair.Relay.SendToPeer(pair.RemoteEndPoint, datagram);
            else transport.SendTo(datagram, pair.RemoteEndPoint);
        }
        catch { }
    }

    public void Close() => Dispose();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _seenRequests.Clear();
            _seenOrder.Clear();
            ConnectionState = "closed";
            GatheringState = "complete";
        }
        // Best-effort Refresh with LIFETIME 0 while the transport is still open.
        foreach (var turn in _turnClients) turn.Dispose();
        _turnClients = [];
        _cts.Cancel();
        _cts.Dispose();
        _transport?.Dispose();
        _transport = null;
        OnConnectionStateChanged?.Invoke("closed");
    }

    private enum CandidatePairState { Waiting, InProgress, Succeeded, Failed }

    private sealed class CandidatePair(IceCandidate local, IPEndPoint remoteEndPoint, long localPref, long remotePref, bool controlling)
    {
        public void Recompute(bool controlling) => Priority = ComputePriority(localPref, remotePref, controlling);
        public IceCandidate Local { get; } = local;
        public IPEndPoint RemoteEndPoint { get; } = remoteEndPoint;
        public CandidatePairState State { get; set; } = CandidatePairState.Waiting;
        public TurnClient? Relay { get; init; }
        public long Priority { get; private set; } = ComputePriority(localPref, remotePref, controlling);

        private static long ComputePriority(long g, long d, bool controlling)
        {
            var high = Math.Min(g, d);
            var low = Math.Max(g, d);
            return (high << 32) | (low << 1) | (controlling ? (g > d ? 1L : 0L) : (g < d ? 1L : 0L));
        }
    }
}
