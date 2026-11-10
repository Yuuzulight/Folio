using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

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

    private readonly List<IceCandidate> _localCandidates = [];
    private readonly List<IceCandidate> _remoteCandidates = [];
    private readonly List<CandidatePair> _pairs = [];
    private CandidatePair? _selectedPair;

    private Socket? _socket;
    private int _localPort;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private bool _disposed;
    private readonly IReadOnlyList<string> _iceServers;
    private readonly SemaphoreSlim _checkSerial = new(1, 1);

    public IReadOnlyList<IceCandidate> LocalCandidates { get { lock (_gate) return _localCandidates.ToArray(); } }

    public IceAgent(IReadOnlyList<string>? iceServers = null)
    {
        LocalUfrag = GenerateRandomString(8);
        LocalPassword = GenerateRandomString(24);
        Span<byte> tieBreakerBytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(tieBreakerBytes);
        TieBreaker = BinaryPrimitives.ReadUInt64BigEndian(tieBreakerBytes);
        _iceServers = iceServers ?? [];
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
        _ = Task.Run(CheckConnectivityAsync);
    }

    private void BindSocket()
    {
        lock (_gate)
        {
            if (_socket is not null) return;
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            _localPort = ((IPEndPoint)_socket.LocalEndPoint!).Port;
            _ = Task.Run(ReceiveLoopAsync);
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
        foreach (var serverUrl in _iceServers)
        {
            if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri)) continue;
            if (!string.Equals(uri.Scheme, "stun", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                var host = uri.Host;
                var port = uri.Port > 0 ? uri.Port : 3478;
                var ipAddresses = await Dns.GetHostAddressesAsync(host, _cts.Token).ConfigureAwait(false);
                var ipv4 = ipAddresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (ipv4 is null) continue;

                var endpoint = new IPEndPoint(ipv4, port);
                var transactionId = new byte[12];
                RandomNumberGenerator.Fill(transactionId);
                var request = StunMessage.EncodeRequest(StunMessage.BindingRequest, transactionId);

                var (response, from) = await SendStunRequestAsync(request, transactionId, endpoint, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
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
        byte[] request, byte[] transactionId, IPEndPoint destination, TimeSpan timeout, byte[]? integrityKey = null)
    {
        if (_socket is null) return (null, null);
        var tcs = new TaskCompletionSource<(StunMessage, EndPoint)>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(StunMessage msg, EndPoint from)
        {
            if (from.Equals(destination) && msg.Type is StunMessage.BindingResponse or StunMessage.BindingErrorResponse &&
                msg.TransactionId.Span.SequenceEqual(transactionId) &&
                (integrityKey is null || (msg.VerifyIntegritySha1(integrityKey) && msg.VerifyFingerprint())))
                tcs.TrySetResult((msg, from));
        }

        lock (_gate)
        {
            if (_disposed || _pendingStunRequests.Count >= 64) return (null, null);
            _pendingStunRequests.Add(Handler);
        }
        try
        {
            await _socket.SendToAsync(request, SocketFlags.None, destination, _cts.Token).ConfigureAwait(false);
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, timeoutCts.Token);
            using (linked.Token.Register(() => tcs.TrySetCanceled()))
            {
                var result = await tcs.Task.ConfigureAwait(false);
                return (result.Item1, result.Item2);
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
        _ = Task.Run(CheckConnectivityAsync);
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
                var transactionId = new byte[12];
                RandomNumberGenerator.Fill(transactionId);

                var username = $"{RemoteUfrag}:{LocalUfrag}";
                var usernameBytes = Encoding.UTF8.GetBytes(username);
                var priorityBytes = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(priorityBytes, (uint)pair.Local.Priority);

                var attributes = new List<(ushort Type, byte[] Value)>
                {
                    (StunMessage.Username, usernameBytes),
                    (StunMessage.Priority, priorityBytes)
                };

                if (IsControlling)
                {
                    var tieBreakerBytes = new byte[8];
                    BinaryPrimitives.WriteUInt64BigEndian(tieBreakerBytes, TieBreaker);
                    attributes.Add((StunMessage.IceControlling, tieBreakerBytes));
                    attributes.Add((StunMessage.UseCandidate, []));
                }
                else
                {
                    var tieBreakerBytes = new byte[8];
                    BinaryPrimitives.WriteUInt64BigEndian(tieBreakerBytes, TieBreaker);
                    attributes.Add((StunMessage.IceControlled, tieBreakerBytes));
                }

                var key = Encoding.UTF8.GetBytes(RemotePassword);
                var request = StunMessage.Encode(StunMessage.BindingRequest, transactionId, attributes, key);

                var (response, _) = await SendStunRequestAsync(request, transactionId, pair.RemoteEndPoint, TimeSpan.FromMilliseconds(500), key).ConfigureAwait(false);
                if (response is not null && response.Type == StunMessage.BindingResponse)
                {
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
                        }
                    }
                    if (newlyConnected) OnConnectionStateChanged?.Invoke("connected");
                    if (ConnectionState == "connected") return;
                }
            }
            catch { }
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[65536];
        EndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (_socket is null) break;
                var result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, remoteEp, _cts.Token).ConfigureAwait(false);
                var bytes = buffer.AsSpan(0, result.ReceivedBytes);

                if (StunMessage.TryParse(bytes, out var stun))
                {
                    HandleStunMessage(stun, result.RemoteEndPoint);
                }
                else
                {
                    // Non-STUN datagram: DTLS or SRTP packet
                    lock (_gate)
                        if (_selectedPair is null || !_selectedPair.RemoteEndPoint.Equals(result.RemoteEndPoint)) continue;
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
