using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using Folio.Scripting.Rtc;

namespace Folio.Scripting.Ipc;

/// <summary>
/// Content-process side of host-mediated UDP. Creates <see cref="IIceTransport"/> instances that own no socket:
/// every operation is a command to the host's <see cref="UdpBroker"/>, and datagrams come back as events.
/// </summary>
internal sealed class MediatedUdpClient(Action<JsonElement> sendCommand)
{
    private const int IncomingCapacity = 256;
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<long, (TaskCompletionSource<(long Id, int Port)> Result, Transport Owner)> _pendingOpens = new();
    private readonly ConcurrentDictionary<long, Transport> _byId = new();
    private long _nextToken, _generation = 1;

    /// <summary>The document generation the host stamped into the current Load. New transports carry it on every command.</summary>
    public long Generation { get => Volatile.Read(ref _generation); set => Volatile.Write(ref _generation, value); }

    public IIceTransport CreateTransport() => new Transport(this);

    /// <summary>Routes one host event. Runs on the connection reader: it must never block.</summary>
    public void Deliver(JsonElement evt)
    {
        if (evt.ValueKind != JsonValueKind.Object || !evt.TryGetProperty("op", out var op) || op.ValueKind != JsonValueKind.String) return;
        switch (op.GetString())
        {
            case "opened" when TryNumber(evt, "token", out var token) && TryNumber(evt, "id", out var id)
                && TryNumber(evt, "port", out var port64) && port64 is > 0 and <= 65535:
                var port = (int)port64;
                if (_pendingOpens.TryGetValue(token, out var open))
                {
                    // Map the id before the binder wakes so no later event can miss its transport.
                    open.Owner.Policy = new UdpBrokerOptions
                    {
                        AllowLoopback = evt.TryGetProperty("allowLoopback", out var l) && l.ValueKind == JsonValueKind.True,
                        AllowPrivate = evt.TryGetProperty("allowPrivate", out var r) && r.ValueKind == JsonValueKind.True
                    };
                    _byId[id] = open.Owner;
                    open.Result.TrySetResult((id, port));
                }
                break;
            case "failed" when TryNumber(evt, "token", out var failedToken):
                if (_pendingOpens.TryGetValue(failedToken, out var failed)) failed.Result.TrySetException(new IOException("The host refused the UDP socket."));
                break;
            case "datagram" when TryTransport(evt, out var transport) && evt.TryGetProperty("host", out var h) && h.ValueKind == JsonValueKind.String
                && IPAddress.TryParse(h.GetString(), out var address) && TryNumber(evt, "port", out var peerPort) && peerPort is > 0 and <= 65535
                && evt.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String:
                try { transport.Enqueue(Convert.FromBase64String(d.GetString()!), new IPEndPoint(address, (int)peerPort)); }
                catch (FormatException) { }
                break;
            case "closed" when TryTransport(evt, out var closing): closing.HostClosed(); break;
        }
    }

    /// <summary>The connection is gone: every transport fails its pending operations and closes.</summary>
    public void Close()
    {
        foreach (var pending in _pendingOpens.Values) pending.Result.TrySetException(new IOException("The host connection closed."));
        foreach (var transport in _byId.Values) transport.HostClosed();
    }

    private bool TryTransport(JsonElement evt, out Transport transport)
    {
        transport = null!;
        return TryNumber(evt, "id", out var id) && _byId.TryGetValue(id, out transport!);
    }

    private static bool TryNumber(JsonElement parent, string name, out long value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value);
    }

    private void Send(object command)
    {
        try { sendCommand(ScriptJson.SerializeToElement(command)); }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException) { }
    }

    private sealed class Transport(MediatedUdpClient owner) : IIceTransport
    {
        private readonly Channel<(byte[] Data, IPEndPoint From)> _incoming = Channel.CreateBounded<(byte[], IPEndPoint)>(
            new BoundedChannelOptions(IncomingCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
        private long _id;
        private int _disposed;
        private readonly long _generation = owner.Generation;
        internal UdpBrokerOptions Policy { get; set; } = new();

        public void Permit(IPAddress address)
        {
            if (Volatile.Read(ref _id) == 0 || Volatile.Read(ref _disposed) != 0) return;
            owner.Send(new Dictionary<string, object?> { ["op"] = "permit", ["gen"] = _generation, ["host"] = address.ToString() });
        }

        public bool CanSendTo(IPEndPoint destination) => UdpDestinationPolicy.IsAllowed(destination, Policy);

        public IPEndPoint Bind()
        {
            if (Volatile.Read(ref _id) != 0) throw new InvalidOperationException("Already bound.");
            var token = Interlocked.Increment(ref owner._nextToken);
            var opened = new TaskCompletionSource<(long Id, int Port)>(TaskCreationOptions.RunContinuationsAsynchronously);
            owner._pendingOpens[token] = (opened, this);
            try
            {
                owner.Send(new Dictionary<string, object?> { ["op"] = "open", ["gen"] = _generation, ["token"] = token });
                if (!opened.Task.Wait(OpenTimeout)) throw new TimeoutException("The host did not answer the UDP open request.");
                var (id, port) = opened.Task.Result;
                Volatile.Write(ref _id, id);
                if (Volatile.Read(ref _disposed) != 0) { Dispose(); throw new ObjectDisposedException(nameof(Transport)); }
                return new IPEndPoint(IPAddress.Any, port);
            }
            catch (AggregateException error) when (error.InnerException is IOException io) { throw io; }
            finally { owner._pendingOpens.TryRemove(token, out _); }
        }

        internal void Enqueue(byte[] data, IPEndPoint from) => _incoming.Writer.TryWrite((data, from));

        public void SendTo(ReadOnlySpan<byte> datagram, IPEndPoint destination)
        {
            var id = Volatile.Read(ref _id);
            if (id == 0 || Volatile.Read(ref _disposed) != 0 || datagram.Length == 0) return;
            owner.Send(new Dictionary<string, object?>
            {
                ["op"] = "send", ["gen"] = _generation, ["id"] = id, ["host"] = destination.Address.ToString(), ["port"] = destination.Port,
                ["data"] = Convert.ToBase64String(datagram)
            });
        }

        public int Receive(byte[] buffer, int timeoutMicroseconds, ref EndPoint remote)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(1, timeoutMicroseconds / 1000)));
            try
            {
                var (data, from) = _incoming.Reader.ReadAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
                remote = from;
                data.CopyTo(buffer, 0);
                return Math.Min(data.Length, buffer.Length);
            }
            catch (OperationCanceledException) { ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this); return -1; }
            catch (ChannelClosedException) { throw new ObjectDisposedException(nameof(Transport)); }
        }

        /// <summary>The host closed this socket (revocation, replacement or quota); no close command is echoed back.</summary>
        internal void HostClosed()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            owner._byId.TryRemove(Volatile.Read(ref _id), out _);
            _incoming.Writer.TryComplete();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            var id = Volatile.Read(ref _id);
            if (id != 0)
            {
                owner._byId.TryRemove(id, out _);
                owner.Send(new Dictionary<string, object?> { ["op"] = "close", ["gen"] = _generation, ["id"] = id });
            }
            _incoming.Writer.TryComplete();
        }
    }
}
