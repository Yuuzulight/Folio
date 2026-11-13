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
    /// <summary>Datagrams queued towards the child across all sockets, before drop-and-count.</summary>
    public int IncomingQueue { get; init; } = 256;
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
/// Socket ids are host-assigned and never reused, so a late datagram can never reach a later document.
/// Wire (JSON payloads): commands {op:"open",token} {op:"send",id,host,port,data(base64)} {op:"close",id};
/// events {op:"opened",token,id,port,allowLoopback,allowPrivate} {op:"failed",token,reason} {op:"datagram",id,host,port,data} {op:"closed",id,reason}.
/// </summary>
internal sealed class UdpBroker : IDisposable
{
    private readonly UdpBrokerOptions _options;
    private readonly Func<bool> _granted;
    private readonly Action<JsonElement> _emit;
    private readonly object _gate = new();
    private readonly Dictionary<long, UdpSocketLease> _sockets = [];
    private readonly Channel<Event> _events = Channel.CreateUnbounded<Event>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _pump;
    private UdpRateLimiter _outLimit, _inLimit;
    private long _nextId, _generation = 1; // generation 0 marks control events that always pass
    private int _queuedIncoming;
    private bool _disposed;

    // Counters for diagnostics and tests.
    public long DroppedOutgoing, DroppedIncoming, Denied, Oversize, RateLimited, Malformed, SendFailed;
    /// <summary>Test seam: while set and incomplete, send loops hold their datagrams so queue limits can be observed.</summary>
    internal Task? SendPause;
    public int OpenSockets { get { lock (_gate) return _sockets.Count; } }

    /// <param name="granted">Re-evaluated on every operation; false means the WebRTC capability is off.</param>
    /// <param name="emit">Delivers an event to the child. Called from one pump thread; may block.</param>
    public UdpBroker(UdpBrokerOptions options, Func<bool> granted, Action<JsonElement> emit)
    {
        _options = options; _granted = granted; _emit = emit;
        _outLimit = NewLimiter(); _inLimit = NewLimiter();
        _pump = Task.Run(PumpAsync);
    }

    private UdpRateLimiter NewLimiter() => new(_options.BytesPerSecond, _options.BurstBytes, _options.PacketsPerSecond, _options.BurstPackets);

    private readonly record struct Event(long Generation, string Op, long Token, long Id, string? Text, IPEndPoint? Peer, byte[]? Data, bool Datagram);

    private sealed class UdpSocketLease(long id, Socket socket, int queue) : IDisposable
    {
        public long Id { get; } = id;
        public Socket Socket { get; } = socket;
        public CancellationTokenSource Cancellation { get; } = new();
        public Channel<(byte[] Data, IPEndPoint To)> Outgoing { get; } = Channel.CreateBounded<(byte[], IPEndPoint)>(
            new BoundedChannelOptions(queue) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait }); // Wait: TryWrite then reports false when full, so drops are counted
        public void Dispose()
        {
            try { Cancellation.Cancel(); } catch (ObjectDisposedException) { }
            Outgoing.Writer.TryComplete();
            try { Socket.Dispose(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>Handles one child command. Never blocks on the network and never throws for hostile input.</summary>
    public void Handle(JsonElement command)
    {
        if (command.ValueKind != JsonValueKind.Object || !command.TryGetProperty("op", out var op) || op.ValueKind != JsonValueKind.String)
        { Interlocked.Increment(ref Malformed); return; }
        switch (op.GetString())
        {
            case "open" when TryInt64(command, "token", out var token): Open(token); break;
            case "send": Send(command); break;
            case "close" when TryInt64(command, "id", out var id): Close(id, "closed by document"); break;
            default: Interlocked.Increment(ref Malformed); break;
        }
    }

    // The child is untrusted: a wrong-typed field must read as absent, never throw.
    private static bool TryInt64(JsonElement parent, string name, out long value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value);
    }

    private void Open(long token)
    {
        UdpSocketLease? lease = null;
        string? refusal = null;
        long generation;
        lock (_gate)
        {
            generation = _generation;
            if (_disposed || !_granted()) refusal = "WebRTC networking is not granted";
            else if (_sockets.Count >= _options.MaxSockets) refusal = "socket quota exceeded";
            else
            {
                try
                {
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    try { socket.Bind(new IPEndPoint(IPAddress.Any, 0)); } catch { socket.Dispose(); throw; }
                    lease = new UdpSocketLease(++_nextId, socket, _options.OutgoingQueuePerSocket);
                    _sockets.Add(lease.Id, lease);
                }
                catch (SocketException) { refusal = "bind failed"; }
            }
        }
        if (lease is null) { _events.Writer.TryWrite(new(0, "failed", token, 0, refusal, null, null, false)); return; }
        _events.Writer.TryWrite(new(generation, "opened", token, lease.Id, null, (IPEndPoint)lease.Socket.LocalEndPoint!, null, false));
        _ = Task.Run(() => SendLoopAsync(lease));
        _ = Task.Run(() => ReceiveLoopAsync(lease, generation));
    }

    private void Send(JsonElement command)
    {
        if (!TryInt64(command, "id", out var id)
            || !command.TryGetProperty("host", out var hostValue) || hostValue.ValueKind != JsonValueKind.String
            || !TryInt64(command, "port", out var port64) || port64 is < int.MinValue or > int.MaxValue
            || !command.TryGetProperty("data", out var dataValue) || dataValue.ValueKind != JsonValueKind.String)
        { Interlocked.Increment(ref Malformed); return; }
        var port = (int)port64;
        UdpSocketLease? lease;
        lock (_gate) _sockets.TryGetValue(id, out lease);
        if (lease is null) { Interlocked.Increment(ref Denied); return; }
        if (!_granted()) { Revoke(); Interlocked.Increment(ref Denied); return; }
        var text = hostValue.GetString()!;
        if (text.Length > 15 || !IPAddress.TryParse(text, out var address) || address.AddressFamily != AddressFamily.InterNetwork
            || address.ToString() != text || !UdpDestinationPolicy.IsAllowed(new IPEndPoint(address, port), _options))
        { Interlocked.Increment(ref Denied); return; }
        var encoded = dataValue.GetString()!;
        var data = new byte[_options.MaxDatagramBytes];
        if (encoded.Length > (_options.MaxDatagramBytes + 2) / 3 * 4 || !Convert.TryFromBase64String(encoded, data, out var length) || length == 0)
        { Interlocked.Increment(ref Oversize); return; }
        UdpRateLimiter limiter;
        lock (_gate) limiter = _outLimit;
        if (!limiter.TryTake(length)) { Interlocked.Increment(ref RateLimited); return; }
        if (!lease.Outgoing.Writer.TryWrite((data[..length], new IPEndPoint(address, port)))) Interlocked.Increment(ref DroppedOutgoing);
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
        try
        {
            while (!lease.Cancellation.IsCancellationRequested)
            {
                SocketReceiveFromResult result;
                try { result = await lease.Socket.ReceiveFromAsync(buffer, SocketFlags.None, any, lease.Cancellation.Token).ConfigureAwait(false); }
                catch (SocketException) { continue; } // e.g. ICMP port unreachable surfaced as a reset
                var from = (IPEndPoint)result.RemoteEndPoint;
                if (!_granted()) { Revoke(); return; }
                if (result.ReceivedBytes > _options.MaxDatagramBytes) { Interlocked.Increment(ref Oversize); continue; }
                if (!UdpDestinationPolicy.IsAllowed(from, _options)) { Interlocked.Increment(ref Denied); continue; }
                UdpRateLimiter limiter;
                lock (_gate) limiter = _inLimit;
                if (!limiter.TryTake(result.ReceivedBytes)) { Interlocked.Increment(ref RateLimited); continue; }
                if (Interlocked.Increment(ref _queuedIncoming) > _options.IncomingQueue)
                { Interlocked.Decrement(ref _queuedIncoming); Interlocked.Increment(ref DroppedIncoming); continue; }
                _events.Writer.TryWrite(new(generation, "datagram", 0, lease.Id, null, from, buffer[..result.ReceivedBytes], true));
            }
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException) { }
    }

    private async Task PumpAsync()
    {
        await foreach (var item in _events.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (item.Datagram) Interlocked.Decrement(ref _queuedIncoming);
            if (item.Generation != 0 && item.Generation != Volatile.Read(ref _generation)) continue; // a retired document
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
        _events.Writer.TryWrite(new(0, "closed", 0, id, reason, null, null, false));
    }

    private void CloseAll(string reason)
    {
        UdpSocketLease[] leases;
        lock (_gate) { leases = [.. _sockets.Values]; _sockets.Clear(); }
        foreach (var lease in leases)
        {
            lease.Dispose();
            _events.Writer.TryWrite(new(0, "closed", 0, lease.Id, reason, null, null, false));
        }
    }

    /// <summary>Capability revocation: every socket closes and the child is told.</summary>
    public void Revoke() => CloseAll("WebRTC networking was revoked");

    /// <summary>Document replacement: sockets close, queued datagrams are discarded and quotas reset.
    /// Events of the retired generation are never delivered.</summary>
    public void Retire()
    {
        lock (_gate) { _generation++; _outLimit = NewLimiter(); _inLimit = NewLimiter(); }
        CloseAll("the document was replaced");
    }

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _generation++; }
        CloseAll("the broker was disposed");
        _events.Writer.TryComplete();
        try { _pump.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
    }
}
