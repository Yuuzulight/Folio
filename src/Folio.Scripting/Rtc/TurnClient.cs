using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Folio.Scripting.Rtc;

/// <summary>
/// TURN client (RFC 8656 / RFC 5766) over UDP with the long-term credential mechanism. It owns no socket: it sends
/// to the configured server through the agent's <see cref="IIceTransport"/> (so inside the sandbox every datagram goes
/// through the host broker and its policy and quotas) and the owner routes datagrams from that server to
/// <see cref="TryHandleDatagram"/>. All timing runs on the injected <see cref="TimeProvider"/>.
///
/// Data path: ChannelData once a ChannelBind is confirmed (4 bytes of framing instead of a ~36 byte Send
/// indication, which matters for DTLS/SRTP volume), Send indications until then.
///
/// Not supported: TCP/TLS transports, ALTERNATE-SERVER redirects (refused), IPv6 peers, SASLprep of credentials.
/// </summary>
internal sealed class TurnClient : IDisposable
{
    internal const int MaxDatagram = 8192;
    private const int MaxPending = 16, MaxPermissions = 64, MaxChannels = 64, PermissionsPerRequest = 8;

    private readonly IIceTransport _transport;
    private readonly TimeProvider _clock;
    private readonly string _username, _password;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _pending = [];
    private readonly Dictionary<IPAddress, Task<bool>> _permissions = [];
    private readonly Dictionary<IPEndPoint, Channel> _channels = [];
    private readonly Dictionary<ushort, IPEndPoint> _byNumber = [];
    private readonly SemaphoreSlim _permissionSlots = new(8);
    private string _realm = "", _nonce = "";
    private byte[] _key = [];
    private bool _allocated, _stopped;
    private uint _lifetime = 600;
    private long _nextRefresh, _nextPermissionRefresh;
    private int _refreshBusy, _permissionBusy, _permissionFailures;
    private const int MaxPermissionRefreshFailures = 4;
    private ushort _nextChannel = 0x4000;

    internal IPEndPoint Server { get; }
    internal IPEndPoint? Relayed { get; private set; }
    internal IPEndPoint? Mapped { get; private set; }
    internal bool IsAllocated { get { lock (_gate) return _allocated; } }

    /// <summary>Initial retransmission timeout; doubles per attempt, three attempts per request.</summary>
    internal TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMilliseconds(500);
    internal TimeSpan PermissionRefreshInterval { get; set; } = TimeSpan.FromMinutes(4);
    internal TimeSpan RetryBackoff { get; set; } = TimeSpan.FromSeconds(1);
    internal uint RequestedLifetimeSeconds { get; set; } = 600;

    internal event Action<IPEndPoint, byte[]>? OnPeerData;

    internal TurnClient(IPEndPoint server, string username, string password, IIceTransport transport, TimeProvider? clock = null)
    {
        Server = server;
        _username = username;
        _password = password;
        _transport = transport;
        _clock = clock ?? TimeProvider.System;
    }

    // Best effort, like every datagram: false when the transport knows the server would be refused or is gone.
    private bool _send(byte[] packet)
    {
        try
        {
            if (!_transport.CanSendTo(Server)) return false;
            _transport.SendTo(packet, Server);
            return true;
        }
        catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException or InvalidOperationException) { return false; }
    }

    private sealed record Pending(ushort Type, byte[]? Key, TaskCompletionSource<StunMessage> Completion);
    private sealed class Channel(ushort number) { internal ushort Number { get; } = number; internal bool Bound { get; set; } }

    // Never relay toward addresses a public TURN server could be abused to reach (RFC 1918, loopback, link-local,
    // multicast...), unless the TURN server itself is on such a network (a LAN or test server).
    private bool PeerAllowed(IPAddress peer)
    {
        if (peer.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = peer.GetAddressBytes();
        if (b[0] == 0 || b[0] >= 224) return false;
        return !IsNonPublic(peer) || IsNonPublic(Server.Address);
    }

    internal static bool IsNonPublic(IPAddress a)
    {
        if (a.AddressFamily != AddressFamily.InterNetwork) return true;
        var b = a.GetAddressBytes();
        return b[0] is 0 or 10 or 127 || b[0] >= 224 || (b[0] == 169 && b[1] == 254) || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] is >= 64 and <= 127)
            || (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2) || (b[0] == 198 && b[1] is 18 or 19)
            || (b[0] == 198 && b[1] == 51 && b[2] == 100) || (b[0] == 203 && b[1] == 0 && b[2] == 113);
    }

    private static byte[] LongTermKey(string username, string realm, string password) =>
#pragma warning disable CA5351 // MD5 is mandated by the TURN long-term credential mechanism (RFC 8489 section 9.2.2).
        MD5.HashData(Encoding.UTF8.GetBytes($"{username}:{realm}:{password}"));
#pragma warning restore CA5351

    internal async Task<bool> AllocateAsync(CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellation);
        var token = linked.Token;
        // Step 1: unauthenticated request; the server must answer 401 with REALM and NONCE.
        var challenge = await RequestAsync(StunMessage.AllocateRequest, [(StunMessage.RequestedTransport, [17, 0, 0, 0])], null, token).ConfigureAwait(false);
        // 300 (ALTERNATE-SERVER) and every other answer are refused: credentials are only ever sent to the configured server.
        if (challenge is null || challenge.Type != StunMessage.AllocateErrorResponse || !challenge.TryErrorCode(out var code) || code != 401 ||
            !challenge.TryText(StunMessage.Realm, 763, out var realm) || !challenge.TryText(StunMessage.Nonce, 763, out var nonce)) return false;
        lock (_gate)
        {
            if (_stopped) return false;
            (_realm, _nonce, _key) = (realm, nonce, LongTermKey(_username, realm, _password));
        }
        // Step 2: authenticated. A second 401 means the credentials are wrong: stop, never retry.
        var response = await AuthenticatedAsync(StunMessage.AllocateRequest, [(StunMessage.RequestedTransport, [17, 0, 0, 0])], token).ConfigureAwait(false);
        if (response is null || response.Type != StunMessage.AllocateResponse ||
            !response.TryXorRelayedAddress(out var relayed) || relayed is null ||
            relayed.Address.AddressFamily != AddressFamily.InterNetwork || relayed.Port == 0 || relayed.Address.Equals(IPAddress.Any)) return false;
        response.TryXorMappedAddress(out var mapped);
        var lifetime = response.TryLifetime(out var granted) && granted > 0 ? granted : 600;
        lock (_gate)
        {
            if (_stopped) return false;
            (Relayed, Mapped, _lifetime, _allocated) = (relayed, mapped, lifetime, true);
            var now = _clock.GetTimestamp();
            _nextRefresh = now + RefreshDelay(lifetime);
            _nextPermissionRefresh = now + (long)(PermissionRefreshInterval.TotalSeconds * _clock.TimestampFrequency);
        }
        return true;
    }

    // RFC 8656: refresh before expiry; one minute early for the default lifetime, half-way for short ones.
    private long RefreshDelay(uint lifetime) =>
        (long)(Math.Max(lifetime - 60.0, lifetime / 2.0) * _clock.TimestampFrequency);

    private async Task<StunMessage?> AuthenticatedAsync(ushort type, IReadOnlyList<(ushort, byte[])> extra, CancellationToken cancellation)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            string realm, nonce;
            byte[] key;
            lock (_gate) (realm, nonce, key) = (_realm, _nonce, _key);
            var attributes = new List<(ushort, byte[])>
            {
                (StunMessage.Username, Encoding.UTF8.GetBytes(_username)),
                (StunMessage.Realm, Encoding.UTF8.GetBytes(realm)),
                (StunMessage.Nonce, Encoding.UTF8.GetBytes(nonce)),
            };
            attributes.AddRange(extra);
            var response = await RequestAsync(type, attributes, key, cancellation).ConfigureAwait(false);
            // 438 Stale Nonce: adopt the fresh nonce and retry exactly once.
            if (attempt == 0 && response is not null && (response.Type & 0x0110) == 0x0110 && response.TryErrorCode(out var code) && code == 438 &&
                response.TryText(StunMessage.Nonce, 763, out var fresh))
            {
                lock (_gate) _nonce = fresh;
                continue;
            }
            return response;
        }
        return null;
    }

    private async Task<StunMessage?> RequestAsync(ushort type, IReadOnlyList<(ushort Type, byte[] Value)> attributes, byte[]? key, CancellationToken cancellation)
    {
        var transactionId = RandomNumberGenerator.GetBytes(12);
        var packet = key is null ? StunMessage.EncodeRequest(type, transactionId, attributes) : StunMessage.Encode(type, transactionId, attributes, key);
        var completion = new TaskCompletionSource<StunMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = Convert.ToHexString(transactionId);
        lock (_gate)
        {
            if (_stopped || _pending.Count >= MaxPending) return null;
            _pending[id] = new Pending(type, key, completion);
        }
        try
        {
            var timeout = RequestTimeout;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                _send(packet);
                using var delayCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                if (await Task.WhenAny(completion.Task, Task.Delay(timeout, _clock, delayCancel.Token)).ConfigureAwait(false) == completion.Task)
                {
                    delayCancel.Cancel(); // the retransmission timer must not linger on the clock
                    return await completion.Task.ConfigureAwait(false);
                }
                delayCancel.Cancel();
                timeout *= 2;
            }
            return null;
        }
        catch (OperationCanceledException) { return null; }
        finally { lock (_gate) _pending.Remove(id); }
    }

    /// <summary>Consumes datagrams that belong to the TURN session. Returns false for anything else.</summary>
    internal bool TryHandleDatagram(ReadOnlySpan<byte> bytes)
    {
        if (StunMessage.TryParseChannelData(bytes, out var channel, out var data))
        {
            IPEndPoint? peer;
            lock (_gate) if (!_byNumber.TryGetValue(channel, out peer)) return true;
            if (data.Length is > 0 and <= MaxDatagram) OnPeerData?.Invoke(peer, data.ToArray());
            return true;
        }
        if (!StunMessage.TryParse(bytes, out var message)) return false;
        if (message.Type == StunMessage.DataIndication)
        {
            if (message.TryXorPeerAddress(out var source) && source is not null && message.TryAttribute(StunMessage.Data, out var payload) &&
                payload.Length is > 0 and <= MaxDatagram)
            {
                bool permitted;
                lock (_gate) permitted = _permissions.TryGetValue(source.Address, out var granted) && granted.IsCompletedSuccessfully && granted.Result;
                if (permitted) OnPeerData?.Invoke(source, payload.ToArray());
            }
            return true;
        }
        Pending? pending;
        lock (_gate) if (!_pending.TryGetValue(Convert.ToHexString(message.TransactionId.Span), out pending)) return false;
        var error = (message.Type & 0x0110) == 0x0110;
        if (message.Type != (pending.Type | (error ? 0x0110 : 0x0100))) return true;
        var authenticated = pending.Key is not null && message.VerifyIntegritySha1(pending.Key);
        if (error)
        {
            // 401/438 are how servers answer without a valid nonce, so they cannot carry integrity. Every other
            // error must be authenticated or it is ignored (the request then times out).
            message.TryErrorCode(out var code);
            if (!authenticated && !(code == 401 || (code == 438 && pending.Key is not null))) return true;
        }
        else if (!authenticated) return true;
        pending.Completion.TrySetResult(message);
        return true;
    }

    /// <summary>Installs (or reuses) a permission for the peer address; shared by concurrent callers.</summary>
    internal Task<bool> EnsurePermissionAsync(IPAddress peer)
    {
        TaskCompletionSource<bool> completion;
        lock (_gate)
        {
            if (_stopped || !_allocated || !PeerAllowed(peer)) return Task.FromResult(false);
            if (_permissions.TryGetValue(peer, out var existing) && !(existing.IsCompleted && !existing.Result)) return existing;
            if (_permissions.Count >= MaxPermissions)
            {
                // Failed entries are evicted (and so retried on a later call); successful ones are real permissions.
                foreach (var failed in _permissions.Where(p => p.Value.IsCompleted && !p.Value.Result).Select(p => p.Key).ToArray()) _permissions.Remove(failed);
                if (_permissions.Count >= MaxPermissions && !_permissions.ContainsKey(peer)) return Task.FromResult(false);
            }
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _permissions[peer] = completion.Task;
        }
        _ = CompleteAsync();
        return completion.Task;

        async Task CompleteAsync()
        {
            var slot = false;
            try
            {
                // At most 8 CreatePermission requests in flight; the rest wait (bounded by MaxPermissions).
                await _permissionSlots.WaitAsync(_cts.Token).ConfigureAwait(false);
                slot = true;
                completion.TrySetResult(await CreatePermissionsAsync([peer], _cts.Token).ConfigureAwait(false));
            }
            catch { completion.TrySetResult(false); }
            finally { if (slot) _permissionSlots.Release(); }
        }
    }

    private async Task<bool> CreatePermissionsAsync(IReadOnlyList<IPAddress> peers, CancellationToken cancellation)
    {
        var attributes = peers.Select(p => (StunMessage.XorPeerAddress, StunMessage.EncodeXorAddress(new IPEndPoint(p, 0), new byte[12]))).ToList();
        var response = await AuthenticatedAsync(StunMessage.CreatePermissionRequest, attributes, cancellation).ConfigureAwait(false);
        return response?.Type == StunMessage.CreatePermissionResponse;
    }

    /// <summary>Sends toward a peer through the relay. Returns false when the datagram was not sent.</summary>
    internal bool SendToPeer(IPEndPoint peer, ReadOnlySpan<byte> data)
    {
        if (data.Length is 0 or > MaxDatagram) return false;
        ushort? number = null;
        Channel? created = null;
        lock (_gate)
        {
            if (_stopped || !_allocated || !PeerAllowed(peer.Address)) return false;
            if (_channels.TryGetValue(peer, out var channel)) { if (channel.Bound) number = channel.Number; }
            else if (_channels.Count < MaxChannels && _nextChannel <= 0x4fff)
            {
                created = new Channel(_nextChannel++);
                _channels[peer] = created;
                _byNumber[created.Number] = peer;
            }
        }
        byte[] packet;
        if (number is { } bound) packet = StunMessage.EncodeChannelData(bound, data);
        else
        {
            var transactionId = RandomNumberGenerator.GetBytes(12);
            packet = StunMessage.EncodeRequest(StunMessage.SendIndication, transactionId,
                [(StunMessage.XorPeerAddress, StunMessage.EncodeXorAddress(peer, transactionId)), (StunMessage.Data, data.ToArray())], fingerprint: false);
        }
        var sent = _send(packet);
        if (created is not null) _ = BindChannelAsync(peer, created);
        return sent;
    }

    private async Task BindChannelAsync(IPEndPoint peer, Channel channel)
    {
        try
        {
            if (await EnsurePermissionAsync(peer.Address).ConfigureAwait(false) &&
                await SendChannelBindAsync(peer, channel, _cts.Token).ConfigureAwait(false))
            {
                lock (_gate) channel.Bound = true;
                return;
            }
        }
        catch { }
        // A failed bind is forgotten so the next send starts a fresh one (Send indications keep working meanwhile).
        lock (_gate)
        {
            if (channel.Bound) return;
            _channels.Remove(peer);
            _byNumber.Remove(channel.Number);
        }
    }

    private async Task<bool> SendChannelBindAsync(IPEndPoint peer, Channel channel, CancellationToken cancellation)
    {
        var number = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(number, channel.Number);
        var response = await AuthenticatedAsync(StunMessage.ChannelBindRequest,
            [(StunMessage.ChannelNumber, number), (StunMessage.XorPeerAddress, StunMessage.EncodeXorAddress(peer, new byte[12]))], cancellation).ConfigureAwait(false);
        return response?.Type == StunMessage.ChannelBindResponse;
    }

    /// <summary>Drives allocation and permission/channel refresh; call every few hundred milliseconds.</summary>
    internal void Maintain()
    {
        long now;
        lock (_gate)
        {
            if (_stopped || !_allocated) return;
            now = _clock.GetTimestamp();
        }
        if (now >= Volatile.Read(ref _nextRefresh) && Interlocked.Exchange(ref _refreshBusy, 1) == 0) _ = RefreshAllocationAsync();
        if (now >= Volatile.Read(ref _nextPermissionRefresh) && Interlocked.Exchange(ref _permissionBusy, 1) == 0) _ = RefreshPermissionsAsync();
    }

    private async Task RefreshAllocationAsync()
    {
        try
        {
            var token = _cts.Token;
            for (var attempt = 0; attempt < 3 && !token.IsCancellationRequested; attempt++)
            {
                var lifetime = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(lifetime, RequestedLifetimeSeconds);
                var response = await AuthenticatedAsync(StunMessage.RefreshRequest, [(StunMessage.Lifetime, lifetime)], token).ConfigureAwait(false);
                if (response?.Type == StunMessage.RefreshResponse)
                {
                    var granted = response.TryLifetime(out var seconds) && seconds > 0 ? seconds : _lifetime;
                    Volatile.Write(ref _nextRefresh, _clock.GetTimestamp() + RefreshDelay(granted));
                    return;
                }
                if (response is not null) break; // an authenticated refusal (for example 437) is final
                await Task.Delay(TimeSpan.FromTicks(RetryBackoff.Ticks << attempt), _clock, token).ConfigureAwait(false); // exponential backoff
            }
            lock (_gate) _allocated = false; // the relay is gone; sends stop and the pairs time out
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { }
        finally { Volatile.Write(ref _refreshBusy, 0); }
    }

    private async Task RefreshPermissionsAsync()
    {
        var succeeded = true;
        try
        {
            var token = _cts.Token;
            IPAddress[] peers;
            KeyValuePair<IPEndPoint, Channel>[] channels;
            lock (_gate)
            {
                peers = _permissions.Where(p => p.Value.IsCompletedSuccessfully && p.Value.Result).Select(p => p.Key).ToArray();
                channels = _channels.Where(c => c.Value.Bound).ToArray();
            }
            foreach (var chunk in peers.Chunk(PermissionsPerRequest))
                succeeded &= await CreatePermissionsAsync(chunk, token).ConfigureAwait(false);
            foreach (var (peer, channel) in channels)
                succeeded &= await SendChannelBindAsync(peer, channel, token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            // Failures back off exponentially and stop after a few: the relay is presumed gone (the allocation refresh
            // decides that), and a server that refuses must not be asked forever.
            var failures = succeeded ? _permissionFailures = 0 : ++_permissionFailures;
            var next = succeeded ? _clock.GetTimestamp() + (long)(PermissionRefreshInterval.TotalSeconds * _clock.TimestampFrequency)
                : failures >= MaxPermissionRefreshFailures ? long.MaxValue
                : _clock.GetTimestamp() + (long)(RetryBackoff.TotalSeconds * (1 << (failures - 1)) * _clock.TimestampFrequency);
            Volatile.Write(ref _nextPermissionRefresh, next);
            Volatile.Write(ref _permissionBusy, 0);
        }
    }

    /// <summary>Stops all work and makes a best-effort, unacknowledged Refresh with LIFETIME 0.</summary>
    internal void Release()
    {
        byte[]? goodbye = null;
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            if (_allocated)
            {
                var lifetime = new byte[4];
                goodbye = StunMessage.Encode(StunMessage.RefreshRequest, RandomNumberGenerator.GetBytes(12),
                [
                    (StunMessage.Username, Encoding.UTF8.GetBytes(_username)), (StunMessage.Realm, Encoding.UTF8.GetBytes(_realm)),
                    (StunMessage.Nonce, Encoding.UTF8.GetBytes(_nonce)), (StunMessage.Lifetime, lifetime),
                ], _key);
            }
            _allocated = false;
        }
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        if (goodbye is not null) { try { _send(goodbye); } catch { } }
    }

    public void Dispose()
    {
        Release();
        _cts.Dispose();
    }
}
