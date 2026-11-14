using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;

namespace Folio.Scripting.Ipc;

/// <summary>Per-document limits and destination policy for host-mediated UDP. Defaults are the production values.</summary>
internal sealed record UdpBrokerOptions
{
    public int MaxSockets { get; init; } = 4;
    public int MaxDatagramBytes { get; init; } = 1500;
    public double BytesPerSecond { get; init; } = 1024 * 1024;
    public double BurstBytes { get; init; } = 128 * 1024;
    public double PacketsPerSecond { get; init; } = 1000;
    public double BurstPackets { get; init; } = 200;
    public int OutgoingQueuePerSocket { get; init; } = 64;
    /// <summary>Datagrams queued towards the child, before drop-and-count.</summary>
    public int IncomingQueue { get; init; } = 256;
    /// <summary>All events queued towards the child (control events included). Past this the child is not draining and its UDP is killed.</summary>
    public int MaxQueuedEvents { get; init; } = 512;
    /// <summary>Every command, of any kind, costs one token before it is decoded.</summary>
    public double CommandsPerSecond { get; init; } = 2000;
    public double BurstCommands { get; init; } = 500;
    public double OpensPerSecond { get; init; } = 1;
    public double BurstOpens { get; init; } = 4;
    /// <summary>Addresses the child may explicitly authorise for unsolicited inbound traffic, per document.</summary>
    public int MaxPermitted { get; init; } = 64;
    /// <summary>Addresses remembered as already sent to, per document.</summary>
    public int MaxSentTo { get; init; } = 256;
    public int MaxSocketErrors { get; init; } = 20;
    /// <summary>A replaced document gets fresh rate limiters at most this often.</summary>
    public TimeSpan MinLimiterResetInterval { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Loopback (127/8) is denied unless the embedder opts in; tests do.</summary>
    public bool AllowLoopback { get; init; }
    /// <summary>RFC 1918 and CGNAT ranges are denied unless the embedder opts in.</summary>
    public bool AllowPrivate { get; init; }
}

internal static class UdpDestinationPolicy
{
    /// <summary>Only globally routable unicast IPv4 with a non-zero port, plus opt-in loopback/private ranges.</summary>
    public static bool IsAllowed(IPEndPoint endpoint, UdpBrokerOptions options)
    {
        if (endpoint.Port is < 1 or > 65535 || endpoint.Address.AddressFamily != AddressFamily.InterNetwork) return false;
        Span<byte> b = stackalloc byte[4];
        if (!endpoint.Address.TryWriteBytes(b, out _)) return false;
        if (b[0] == 127) return options.AllowLoopback;
        if (b[0] == 10 || (b[0] == 172 && (b[1] & 0xF0) == 16) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && (b[1] & 0xC0) == 64))
            return options.AllowPrivate;
        return !(b[0] == 0 || b[0] >= 224 // "this" network, multicast, reserved and the limited broadcast
            || (b[0] == 169 && b[1] == 254) // link-local
            || (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2) || (b[0] == 198 && (b[1] & 0xFE) == 18)
            || (b[0] == 198 && b[1] == 51 && b[2] == 100) || (b[0] == 203 && b[1] == 0 && b[2] == 113));
    }
}

/// <summary>Packet and byte token buckets taken together so a refused datagram spends nothing.</summary>
internal sealed class UdpRateLimiter(double bytesPerSecond, double burstBytes, double packetsPerSecond, double burstPackets)
{
    private readonly object _gate = new();
    private double _bytes = burstBytes, _packets = burstPackets;
    private readonly double _burstBytes = burstBytes, _burstPackets = burstPackets;
    private long _stamp = System.Diagnostics.Stopwatch.GetTimestamp();

    public void Refund(int bytes)
    {
        lock (_gate) { _bytes = Math.Min(_burstBytes, _bytes + bytes); _packets = Math.Min(_burstPackets, _packets + 1); }
    }

    public bool TryTake(int bytes)
    {
        lock (_gate)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var seconds = (now - _stamp) / (double)System.Diagnostics.Stopwatch.Frequency;
            _stamp = now;
            _bytes = Math.Min(_burstBytes, _bytes + seconds * bytesPerSecond);
            _packets = Math.Min(_burstPackets, _packets + seconds * packetsPerSecond);
            if (_bytes < bytes || _packets < 1) return false;
            _bytes -= bytes; _packets -= 1;
            return true;
        }
    }
}


/// <summary>
/// Host-owned UDP for the content process. The child asks for sockets and names IPv4 destinations; the host
/// creates every socket, picks the local port, enforces destination policy and quotas, and forwards datagrams.
/// Socket ids are host-assigned and never reused. Every command carries the document generation the host stamped
/// into the Load, so a command from a replaced document is refused.
/// Wire (JSON payloads): commands {op:"open",gen,token} {op:"send",gen,id,host,port,data(base64)} {op:"permit",gen,host}
/// {op:"close",gen,id}; events {op:"opened",token,id,port,allowLoopback,allowPrivate} {op:"failed",token,reason}
/// {op:"datagram",id,host,port,data} {op:"closed",id,reason}.
/// Inbound datagrams are delivered only from addresses the child has sent to, or has permitted.
/// </summary>
internal sealed class UdpBroker : IDisposable
{
    private readonly UdpBrokerOptions _options;
    private readonly Func<bool> _granted;
    private readonly Action<JsonElement> _emit;
    private readonly object _gate = new();
    private readonly Dictionary<long, UdpSocketLease> _sockets = [];
    private readonly HashSet<uint> _sentTo = [], _permitted = [];
    private readonly Channel<Event> _events = Channel.CreateUnbounded<Event>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _pump;
    private UdpRateLimiter _outLimit, _inLimit, _commandLimit, _openLimit;
    private long _nextId, _generation, _limiterStamp;
    private int _queued, _dead;
    private long _epoch; // guarded by _gate
    private bool _disposed;

    // Counters for diagnostics and tests.
    public long DroppedOutgoing, DroppedIncoming, Denied, Oversize, RateLimited, Malformed, SendFailed, Unsolicited;
    public int OpenSockets { get { lock (_gate) return _sockets.Count; } }
    public long Generation => Volatile.Read(ref _generation);
    /// <summary>True once the child failed to drain its events; UDP stays off until the next document.</summary>
    public bool Dead => Volatile.Read(ref _dead) != 0;
    /// <summary>Events accepted but not yet handed to the transport.</summary>
    public int QueuedEvents => Volatile.Read(ref _queued);
    /// <summary>Test seam: while set and incomplete, send loops hold their datagrams so queue limits can be observed.</summary>
    internal Task? SendPause;

    /// <param name="granted">Re-evaluated on every operation; false means the WebRTC capability is off.</param>
    /// <param name="emit">Delivers an event to the child. Called from one pump thread; may block. No lock is held meanwhile.</param>
    public UdpBroker(UdpBrokerOptions options, Func<bool> granted, Action<JsonElement> emit, long generation = 1)
    {
        _options = options; _granted = granted; _emit = emit; _generation = generation;
        _outLimit = NewPacketLimiter(); _inLimit = NewPacketLimiter();
        _commandLimit = NewCountLimiter(options.CommandsPerSecond, options.BurstCommands);
        _openLimit = NewCountLimiter(options.OpensPerSecond, options.BurstOpens);
        _limiterStamp = System.Diagnostics.Stopwatch.GetTimestamp();
        _pump = Task.Run(PumpAsync);
    }

    private UdpRateLimiter NewPacketLimiter() => new(_options.BytesPerSecond, _options.BurstBytes, _options.PacketsPerSecond, _options.BurstPackets);
    private static UdpRateLimiter NewCountLimiter(double perSecond, double burst) => new(1e12, 1e12, perSecond, burst);

    private readonly record struct Event(long Generation, string Op, long Token, long Id, string? Text, IPEndPoint? Peer, byte[]? Data, bool Datagram)
    {
        /// <summary>The queue-accounting epoch this event was counted in; see <see cref="BeginDocument"/>.</summary>
        public long Epoch { get; init; }
    }

    private sealed class UdpSocketLease(long id, Socket socket, int queue) : IDisposable
    {
        public long Id { get; } = id;
        public Socket Socket { get; } = socket;
        public CancellationTokenSource Cancellation { get; } = new();
        // Wait: TryWrite reports false when full, so drops are counted.
        public Channel<(byte[] Data, IPEndPoint To)> Outgoing { get; } = Channel.CreateBounded<(byte[], IPEndPoint)>(
            new BoundedChannelOptions(queue) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        public void Dispose()
        {
            try { Cancellation.Cancel(); } catch (ObjectDisposedException) { }
            Outgoing.Writer.TryComplete();
            try { Socket.Dispose(); } catch (ObjectDisposedException) { }
        }
    }

    // The child is untrusted: a wrong-typed field must read as absent, never throw.
    private static bool TryInt64(JsonElement parent, string name, out long value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value);
    }

    // Canonical dotted-quad only: no mapped, octal, hex, padded or whitespace forms.
    private static bool TryAddress(JsonElement parent, out IPAddress address)
    {
        address = IPAddress.None;
        if (!parent.TryGetProperty("host", out var host) || host.ValueKind != JsonValueKind.String || host.GetString() is not { Length: >= 7 and <= 15 } text
            || !IPAddress.TryParse(text, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork || parsed.ToString() != text) return false;
        address = parsed;
        return true;
    }

    private static uint Key(IPAddress address)
    {
        Span<byte> b = stackalloc byte[4];
        address.TryWriteBytes(b, out _);
        return BinaryPrimitives.ReadUInt32BigEndian(b);
    }

    /// <summary>Handles one child command. Never blocks on the network and never throws for hostile input.
    /// The command-rate limit is applied before any field of the command is read (the frame itself is already parsed).</summary>
    public void Handle(JsonElement command)
    {
        if (Dead) { Interlocked.Increment(ref Denied); return; }
        UdpRateLimiter limiter;
        lock (_gate) limiter = _commandLimit;
        if (!limiter.TryTake(0)) { Interlocked.Increment(ref RateLimited); return; }
        if (command.ValueKind != JsonValueKind.Object || !command.TryGetProperty("op", out var op) || op.ValueKind != JsonValueKind.String)
        { Interlocked.Increment(ref Malformed); return; }
        var name = op.GetString();
        if (!TryInt64(command, "gen", out var gen)) { Interlocked.Increment(ref Malformed); return; }
        if (gen != Generation)
        {
            // A command from a replaced document: nothing it asks for survives into the current one.
            Interlocked.Increment(ref Denied);
            if (name == "open" && TryInt64(command, "token", out var staleToken))
                Enqueue(new(0, "failed", staleToken, 0, "the document was replaced", null, null, false));
            return;
        }
        switch (name)
        {
            case "open" when TryInt64(command, "token", out var token): Open(token, gen); break;
            case "send": Send(command); break;
            case "permit": Permit(command); break;
            case "close" when TryInt64(command, "id", out var id): Close(id, "closed by document"); break;
            default: Interlocked.Increment(ref Malformed); break;
        }
    }

    private void Open(long token, long requestedGeneration)
    {
        UdpSocketLease? lease = null;
        string? refusal = null;
        long generation;
        lock (_gate)
        {
            generation = _generation;
            // Re-checked under the lock BeginDocument takes, so an old document's open can never land in the new one.
            if (requestedGeneration != generation) { refusal = "the document was replaced"; Interlocked.Increment(ref Denied); }
            else if (_disposed || Dead || !_granted()) refusal = "WebRTC networking is not granted";
            else if (!_openLimit.TryTake(0)) { refusal = "socket open rate exceeded"; Interlocked.Increment(ref RateLimited); }
            else if (_sockets.Count >= _options.MaxSockets) refusal = "socket quota exceeded";
            else
            {
                try
                {
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    try
                    {
                        // Windows reports an ICMP port-unreachable as a receive error on the whole socket; a peer going away must not break it.
                        if (OperatingSystem.IsWindows()) socket.IOControl(unchecked((int)0x9800000C), [0], null); // SIO_UDP_CONNRESET off
                        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                    }
                    catch { socket.Dispose(); throw; }
                    lease = new UdpSocketLease(++_nextId, socket, _options.OutgoingQueuePerSocket);
                    _sockets.Add(lease.Id, lease);
                }
                catch (SocketException) { refusal = "bind failed"; }
            }
        }
        if (lease is null) { Enqueue(new(0, "failed", token, 0, refusal, null, null, false)); return; }
        Enqueue(new(generation, "opened", token, lease.Id, null, (IPEndPoint)lease.Socket.LocalEndPoint!, null, false));
        _ = Task.Run(() => SendLoopAsync(lease));
        _ = Task.Run(() => ReceiveLoopAsync(lease, generation));
    }

    private void Permit(JsonElement command)
    {
        if (!_granted()) { Revoke(); Interlocked.Increment(ref Denied); return; }
        if (!TryAddress(command, out var address) || !UdpDestinationPolicy.IsAllowed(new IPEndPoint(address, 1), _options))
        { Interlocked.Increment(ref Denied); return; }
        lock (_gate)
        {
            var key = Key(address);
            if (_permitted.Count < _options.MaxPermitted || _permitted.Contains(key)) _permitted.Add(key);
            else Interlocked.Increment(ref Denied);
        }
    }

    private void Send(JsonElement command)
    {
        if (!TryInt64(command, "id", out var id) || !TryInt64(command, "port", out var port64)
            || !command.TryGetProperty("data", out var dataValue) || dataValue.ValueKind != JsonValueKind.String)
        { Interlocked.Increment(ref Malformed); return; }
        if (port64 is < 1 or > 65535) { Interlocked.Increment(ref Denied); return; } // before any IPEndPoint is built
        var port = (int)port64;
        UdpSocketLease? lease;
        lock (_gate) _sockets.TryGetValue(id, out lease);
        if (lease is null) { Interlocked.Increment(ref Denied); return; }
        if (!_granted()) { Revoke(); Interlocked.Increment(ref Denied); return; }
        if (!TryAddress(command, out var address) || !UdpDestinationPolicy.IsAllowed(new IPEndPoint(address, port), _options))
        { Interlocked.Increment(ref Denied); return; }
        var encoded = dataValue.GetString()!;
        if (encoded.Length > (_options.MaxDatagramBytes + 2) / 3 * 4) { Interlocked.Increment(ref Oversize); return; }
        // TryFromBase64String would silently skip whitespace.
        foreach (var c in encoded) if (c <= ' ') { Interlocked.Increment(ref Malformed); return; }
        Span<byte> buffer = _options.MaxDatagramBytes <= 4096 ? stackalloc byte[_options.MaxDatagramBytes] : new byte[_options.MaxDatagramBytes];
        if (!Convert.TryFromBase64String(encoded, buffer, out var length) || length == 0) { Interlocked.Increment(ref Malformed); return; }
        UdpRateLimiter limiter;
        lock (_gate) limiter = _outLimit;
        if (!limiter.TryTake(length)) { Interlocked.Increment(ref RateLimited); return; }
        if (!lease.Outgoing.Writer.TryWrite((buffer[..length].ToArray(), new IPEndPoint(address, port))))
        {
            limiter.Refund(length); // a refused write must not cost budget
            Interlocked.Increment(ref DroppedOutgoing);
            return;
        }
        lock (_gate) if (_sentTo.Count < _options.MaxSentTo) _sentTo.Add(Key(address));
    }

    private async Task SendLoopAsync(UdpSocketLease lease)
    {
        try
        {
            await foreach (var (data, to) in lease.Outgoing.Reader.ReadAllAsync(lease.Cancellation.Token).ConfigureAwait(false))
            {
                if (SendPause is { } pause) await pause.WaitAsync(lease.Cancellation.Token).ConfigureAwait(false);
                try { await lease.Socket.SendToAsync(data, SocketFlags.None, to, lease.Cancellation.Token).ConfigureAwait(false); }
                catch (SocketException) { Interlocked.Increment(ref SendFailed); }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException) { }
    }

    private async Task ReceiveLoopAsync(UdpSocketLease lease, long generation)
    {
        var buffer = new byte[_options.MaxDatagramBytes + 1];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        var errors = 0;
        try
        {
            while (!lease.Cancellation.IsCancellationRequested)
            {
                SocketReceiveFromResult result;
                try
                {
                    result = await lease.Socket.ReceiveFromAsync(buffer, SocketFlags.None, any, lease.Cancellation.Token).ConfigureAwait(false);
                    errors = 0;
                }
                catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionReset) { continue; } // ICMP unreachable from a vanished peer; not a fault of this socket
                catch (SocketException)
                {
                    // Persistent errors back off, then close the socket.
                    if (++errors > _options.MaxSocketErrors) { Close(lease.Id, "socket error"); return; }
                    await Task.Delay(Math.Min(errors * 10, 200), lease.Cancellation.Token).ConfigureAwait(false);
                    continue;
                }
                var from = (IPEndPoint)result.RemoteEndPoint;
                if (!_granted()) { Revoke(); return; }
                if (result.ReceivedBytes > _options.MaxDatagramBytes) { Interlocked.Increment(ref Oversize); continue; }
                if (!UdpDestinationPolicy.IsAllowed(from, _options)) { Interlocked.Increment(ref Denied); continue; }
                bool solicited;
                UdpRateLimiter limiter;
                lock (_gate) { solicited = _sentTo.Contains(Key(from.Address)) || _permitted.Contains(Key(from.Address)); limiter = _inLimit; }
                if (!solicited) { Interlocked.Increment(ref Unsolicited); continue; } // not an address the child sent to or permitted
                if (!limiter.TryTake(result.ReceivedBytes)) { Interlocked.Increment(ref RateLimited); continue; }
                Enqueue(new(generation, "datagram", 0, lease.Id, null, from, buffer[..result.ReceivedBytes], true));
            }
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException) { }
    }

    /// <summary>Counts every event kind. Datagrams are dropped past <see cref="UdpBrokerOptions.IncomingQueue"/>; control events
    /// are never dropped silently, so exceeding <see cref="UdpBrokerOptions.MaxQueuedEvents"/> means the child is not draining and its UDP is killed.</summary>
    private void Enqueue(Event item)
    {
        if (Dead) return;
        var limit = item.Datagram ? _options.IncomingQueue : _options.MaxQueuedEvents;
        // Counting and epoch capture are atomic with BeginDocument, which restarts the count for the new document.
        lock (_gate)
        {
            if (_queued + 1 > limit) { if (item.Datagram) { Interlocked.Increment(ref DroppedIncoming); return; } }
            else
            {
                if (_events.Writer.TryWrite(item with { Epoch = _epoch })) Interlocked.Increment(ref _queued);
                return;
            }
        }
        Kill();
    }

    private void Kill()
    {
        if (Interlocked.Exchange(ref _dead, 1) != 0) return;
        UdpSocketLease[] leases;
        lock (_gate) { leases = [.. _sockets.Values]; _sockets.Clear(); _sentTo.Clear(); _permitted.Clear(); }
        foreach (var lease in leases) lease.Dispose();
    }

    private async Task PumpAsync()
    {
        await foreach (var item in _events.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            lock (_gate) { if (item.Epoch == _epoch) Interlocked.Decrement(ref _queued); }
            if (Dead) continue;
            if (item.Generation != 0 && item.Generation != Generation) continue; // a retired document
            try { _emit(Serialize(item)); }
            catch { /* a failing transport is the supervisor's concern; keep draining so queues stay bounded */ }
        }
    }

    private JsonElement Serialize(Event item) => ScriptJson.SerializeToElement(item.Op switch
    {
        "opened" => new Dictionary<string, object?> { ["op"] = "opened", ["token"] = item.Token, ["id"] = item.Id, ["port"] = item.Peer!.Port,
            // Advisory copy of the destination policy so the child can skip pairs the host would only drop.
            ["allowLoopback"] = _options.AllowLoopback, ["allowPrivate"] = _options.AllowPrivate },
        "failed" => new Dictionary<string, object?> { ["op"] = "failed", ["token"] = item.Token, ["reason"] = item.Text },
        "datagram" => new Dictionary<string, object?> { ["op"] = "datagram", ["id"] = item.Id, ["host"] = item.Peer!.Address.ToString(), ["port"] = item.Peer.Port, ["data"] = Convert.ToBase64String(item.Data!) },
        _ => new Dictionary<string, object?> { ["op"] = "closed", ["id"] = item.Id, ["reason"] = item.Text },
    });

    private void Close(long id, string reason)
    {
        UdpSocketLease? lease;
        lock (_gate) { if (!_sockets.Remove(id, out lease)) return; }
        lease.Dispose();
        Enqueue(new(0, "closed", 0, id, reason, null, null, false));
    }

    private void CloseAll(string reason)
    {
        UdpSocketLease[] leases;
        lock (_gate) { leases = [.. _sockets.Values]; _sockets.Clear(); }
        foreach (var lease in leases)
        {
            lease.Dispose();
            Enqueue(new(0, "closed", 0, lease.Id, reason, null, null, false));
        }
    }

    /// <summary>Capability revocation: every socket closes and the child is told.</summary>
    public void Revoke() => CloseAll("WebRTC networking was revoked");

    /// <summary>A new document starts (the host sent its Load): sockets close, the permit lists and queued events of the old
    /// generation are discarded, and commands stamped with an older generation are refused from now on.
    /// Rate limiters are renewed at most once per <see cref="UdpBrokerOptions.MinLimiterResetInterval"/>, so replacing documents cannot mint bursts.</summary>
    public void BeginDocument(long generation)
    {
        lock (_gate)
        {
            _generation = generation;
            // Events still queued for the retired document no longer count against the new one's bound (the pump drops them).
            _epoch++; Volatile.Write(ref _queued, 0);
            _sentTo.Clear(); _permitted.Clear();
            Interlocked.Exchange(ref _dead, 0);
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (System.Diagnostics.Stopwatch.GetElapsedTime(_limiterStamp, now) >= _options.MinLimiterResetInterval)
            {
                _outLimit = NewPacketLimiter(); _inLimit = NewPacketLimiter();
                _commandLimit = NewCountLimiter(_options.CommandsPerSecond, _options.BurstCommands);
                _openLimit = NewCountLimiter(_options.OpensPerSecond, _options.BurstOpens);
                _limiterStamp = now;
            }
        }
        CloseAll("the document was replaced");
    }

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _generation++; }
        Kill();
        _events.Writer.TryComplete();
        try { _pump.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
    }
}
