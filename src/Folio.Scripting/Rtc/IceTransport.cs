using System.Net;
using System.Net.Sockets;

namespace Folio.Scripting.Rtc;

/// <summary>
/// The datagram seam under <see cref="IceAgent"/>: one bound IPv4 UDP endpoint. The default
/// implementation owns a socket; the content process uses a host-mediated implementation instead.
/// </summary>
internal interface IIceTransport : IDisposable
{
    /// <summary>Binds an ephemeral local endpoint and returns it. Callers never choose the address or port.</summary>
    IPEndPoint Bind();

    /// <summary>Sends one datagram. Delivery is best effort; a refused or dropped datagram does not throw.</summary>
    void SendTo(ReadOnlySpan<byte> datagram, IPEndPoint destination);

    /// <summary>Authorises unsolicited inbound datagrams from this address (a remote candidate). Direct sockets accept everything and ignore it.</summary>
    void Permit(IPAddress address) { }

    /// <summary>False when the transport knows the destination would be refused, so ICE can skip the pair instead of timing out.</summary>
    bool CanSendTo(IPEndPoint destination) => true;

    /// <summary>Resolves a DNS name to an IPv4 address where the transport lives: the default asks the local resolver; the
    /// host-mediated transport asks the host, so a sandboxed content process never resolves names itself. Null when it cannot.</summary>
    async Task<IPAddress?> ResolveAsync(string host, CancellationToken cancellation) =>
        (await Dns.GetHostAddressesAsync(host, cancellation).ConfigureAwait(false)).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

    /// <summary>Resolves a .local mDNS hostname candidate to an IPv4 address. Not resolved (null) by default: a direct transport never sends multicast queries on behalf of remote SDP; only the host-mediated transport resolves.</summary>
    Task<IPAddress?> ResolveMdnsAsync(string host, CancellationToken cancellation) => Task.FromResult<IPAddress?>(null);

    /// <summary>Waits up to the timeout for a datagram. Returns its length, or -1 on timeout.
    /// Throws <see cref="ObjectDisposedException"/> once the transport is closed.</summary>
    int Receive(byte[] buffer, int timeoutMicroseconds, ref EndPoint remote);
}

/// <summary>Chooses the transport new ICE agents use. Null (the default) means a direct socket.</summary>
internal static class IceTransportProvider
{
    internal static Func<IIceTransport>? Factory { get; set; }
    internal static IIceTransport Create() => Factory?.Invoke() ?? new DirectIceTransport();
}

internal sealed class DirectIceTransport : IIceTransport
{
    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

    public IPEndPoint Bind()
    {
        _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        return (IPEndPoint)_socket.LocalEndPoint!;
    }

    public void SendTo(ReadOnlySpan<byte> datagram, IPEndPoint destination) => _socket.SendTo(datagram, SocketFlags.None, destination);

    public int Receive(byte[] buffer, int timeoutMicroseconds, ref EndPoint remote)
    {
        if (!_socket.Poll(timeoutMicroseconds, SelectMode.SelectRead)) return -1;
        return _socket.ReceiveFrom(buffer, SocketFlags.None, ref remote);
    }

    public void Dispose() => _socket.Dispose();
}
