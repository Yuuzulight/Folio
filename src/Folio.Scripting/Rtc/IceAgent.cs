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
    public ulong TieBreaker { get; }

    public string GatheringState { get; private set; } = "new";
    public string ConnectionState { get; private set; } = "new";

    public event Action<IceCandidate>? OnCandidateGathered;
    public event Action<string>? OnGatheringStateChanged;
    public event Action<string>? OnConnectionStateChanged;
    public event Action<byte[]>? OnDatagramReceived;
    internal event Action? OnMaintenance;

    private readonly List<IceCandidate> _localCandidates = [];
    private readonly List<IceCandidate> _remoteCandidates = [];
    private readonly List<CandidatePair> _pairs = [];
    private CandidatePair? _selectedPair;

    private Socket? _socket;
    private int _localPort;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private bool _disposed;
    private IReadOnlyList<string> _iceServers;
    private readonly SemaphoreSlim _checkSerial = new(1, 1);
    private Thread? _receiveThread;
    internal bool IsReceivePumpAlive => _receiveThread?.IsAlive == true;

    public IReadOnlyList<IceCandidate> LocalCandidates { get { lock (_gate) return _localCandidates.ToArray(); } }

    public IceAgent(IReadOnlyList<string>? iceServers = null)
    {
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
            if (GatheringState != "new") return _iceServers.SequenceEqual(servers, StringComparer.OrdinalIgnoreCase);
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

        BindSocket();
        GatherHostCandidates();
        await GatherServerReflexiveCandidatesAsync().ConfigureAwait(false);

        lock (_gate)
        {
            GatheringState = "complete";
            // Remote SDP may arrive before local gathering. Rebuild pairs
            // now instead of requiring a duplicate trickled candidate.
            FormPairs();
        }
        OnGatheringStateChanged?.Invoke("complete");
        _ = CheckConnectivityAsync();
    }

    private void BindSocket()
    {
        lock (_gate)
        {
            if (_socket is not null) return;
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            _localPort = ((IPEndPoint)_socket.LocalEndPoint!).Port;
            _receiveThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "Folio ICE receive" };
            _receiveThread.Start();
        }
    }

    private void GatherHostCandidates()
    {
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
        if (_iceServers.Count == 0) return;
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
                var ipAddresses = await Dns.GetHostAddressesAsync(host, lookup.Token).ConfigureAwait(false);
                var ipv4 = ipAddresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
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

    private async Task<(StunMessage? Message, EndPoint? From)> SendStunRequestAsync(
        byte[] request, byte[] transactionId, IPEndPoint destination, TimeSpan timeout, byte[]? integrityKey = null,
        Action<StunMessage>? validatedResponse = null)
    {
        if (_socket is null) return (null, null);
        var tcs = new TaskCompletionSource<(StunMessage, EndPoint)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var responseAccepted = 0;

        void Handler(StunMessage msg, EndPoint from)
        {
            if (from.Equals(destination) && msg.Type is StunMessage.BindingResponse or StunMessage.BindingErrorResponse &&
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
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeout.TotalMilliseconds * ((1 << attempts) - 1)));
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
                    await _socket.SendToAsync(request, SocketFlags.None, destination, linked.Token).ConfigureAwait(false);
                    var retry = Task.Delay(TimeSpan.FromMilliseconds(timeout.TotalMilliseconds * (1 << attempt)), linked.Token);
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

    private readonly List<Action<StunMessage, EndPoint>> _pendingStunRequests = [];

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
        _ = CheckConnectivityAsync();
    }

    private void FormPairs()
    {
        foreach (var local in _localCandidates)
        {
            foreach (var remote in _remoteCandidates)
            {
                if (!string.Equals(local.Protocol, remote.Protocol, StringComparison.OrdinalIgnoreCase)) continue;
                if (!IPAddress.TryParse(remote.Address, out var remoteIp)) continue;

                var remoteEp = new IPEndPoint(remoteIp, remote.Port);
                if (_pairs.Any(p => p.Local == local && p.RemoteEndPoint.Equals(remoteEp))) continue;

                var pair = new CandidatePair(local, remoteEp, local.Priority, remote.Priority, IsControlling);
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
        lock (_gate) candidates = _pairs.ToArray();

        foreach (var pair in candidates)
        {
            if (_disposed) break;
            try
            {
                var (request, transactionId, key) = BuildBindingRequest(pair, nominate: IsControlling);

                var (response, _) = await SendStunRequestAsync(request, transactionId, pair.RemoteEndPoint,
                    TimeSpan.FromMilliseconds(500), key, message => AcceptConnectivityResponse(pair, message)).ConfigureAwait(false);
                if (response is not null && response.Type == StunMessage.BindingResponse)
                {
                    if (ConnectionState == "connected") return;
                }
            }
            catch { }
        }
    }

    private (byte[] Request, byte[] TransactionId, byte[] Key) BuildBindingRequest(CandidatePair pair, bool nominate)
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
            (IsControlling ? StunMessage.IceControlling : StunMessage.IceControlled, tieBreakerBytes),
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
        var now = Stopwatch.GetTimestamp();
        Volatile.Write(ref _consentConfirmed, now);
        if (Volatile.Read(ref _consentNextCheck) == 0) Volatile.Write(ref _consentNextCheck, now + NextConsentDelay());
    }

    private long NextConsentDelay() =>
        (long)(ConsentInterval.TotalSeconds * (0.8 + 0.4 * Random.Shared.NextDouble()) * Stopwatch.Frequency);

    private void MaintainConsent()
    {
        CandidatePair? pair;
        var expired = false;
        var now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (_disposed || ConnectionState != "connected" || _selectedPair is null) return;
            pair = _selectedPair;
            if (Stopwatch.GetElapsedTime(Volatile.Read(ref _consentConfirmed), now) > ConsentTimeout)
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
        _ = CheckConsentAsync(pair);
    }

    private async Task CheckConsentAsync(CandidatePair pair)
    {
        try
        {
            var (request, transactionId, key) = BuildBindingRequest(pair, nominate: false);
            await SendStunRequestAsync(request, transactionId, pair.RemoteEndPoint, TimeSpan.FromMilliseconds(500), key,
                response => { if (response.Type == StunMessage.BindingResponse) ConfirmConsent(); }).ConfigureAwait(false);
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
        var maintenance = Stopwatch.GetTimestamp();

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var socket = _socket;
                if (socket is null) break;
                if (Stopwatch.GetElapsedTime(maintenance) >= TimeSpan.FromMilliseconds(250))
                { maintenance = Stopwatch.GetTimestamp(); MaintainConsent(); OnMaintenance?.Invoke(); }
                // Socket completions and transport retry timers must not wait
                // behind synchronous page work on the shared CLR thread pool.
                if (!socket.Poll(250000, SelectMode.SelectRead)) continue;
                var received = socket.ReceiveFrom(buffer, SocketFlags.None, ref remoteEp);
                var bytes = buffer.AsSpan(0, received);

                if (StunMessage.TryParse(bytes, out var stun))
                {
                    HandleStunMessage(stun, remoteEp);
                }
                else
                {
                    // Non-STUN datagram: DTLS or SRTP packet
                    lock (_gate)
                        if (_selectedPair is null || !_selectedPair.RemoteEndPoint.Equals(remoteEp)) continue;
                    var copy = bytes.ToArray();
                    OnDatagramReceived?.Invoke(copy);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { }
        }
    }

    private void HandleStunMessage(StunMessage msg, EndPoint from)
    {
        // Notify any pending request waiters
        Action<StunMessage, EndPoint>[] handlers;
        lock (_gate) handlers = _pendingStunRequests.ToArray();
        foreach (var handler in handlers) handler(msg, from);

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
                    _socket?.SendTo(resp, ep);
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
                        var pair = _pairs.FirstOrDefault(candidate => candidate.RemoteEndPoint.Equals(ep));
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
        if (pair is null || _socket is null) return;
        try
        {
            _socket.SendTo(datagram.ToArray(), pair.RemoteEndPoint);
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
            ConnectionState = "closed";
            GatheringState = "complete";
        }
        _cts.Cancel();
        _cts.Dispose();
        _socket?.Dispose();
        _socket = null;
        OnConnectionStateChanged?.Invoke("closed");
    }

    private enum CandidatePairState { Waiting, InProgress, Succeeded, Failed }

    private sealed class CandidatePair(IceCandidate local, IPEndPoint remoteEndPoint, long localPref, long remotePref, bool controlling)
    {
        public IceCandidate Local { get; } = local;
        public IPEndPoint RemoteEndPoint { get; } = remoteEndPoint;
        public CandidatePairState State { get; set; } = CandidatePairState.Waiting;
        public long Priority { get; } = ComputePriority(localPref, remotePref, controlling);

        private static long ComputePriority(long g, long d, bool controlling)
        {
            var high = Math.Min(g, d);
            var low = Math.Max(g, d);
            return (high << 32) | (low << 1) | (controlling ? (g > d ? 1L : 0L) : (g < d ? 1L : 0L));
        }
    }
}
