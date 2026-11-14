using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Folio.Scripting.Ipc;

/// <summary>
/// Minimal multicast-DNS client (RFC 6762) for resolving .local hostname candidates.
/// Issues one-shot DNS Type A queries over UDP 5353 and parses bounded response datagrams.
/// </summary>
internal static partial class MdnsClient
{
    public const int MdnsPort = 5353;
    public static readonly IPAddress MulticastAddress = IPAddress.Parse("224.0.0.251");
    public const int MaxPacketSize = 1500;

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.local\z", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    public static partial Regex ValidMdnsNameRegex();

    /// <summary>Builds a standard DNS query for an A record of the specified name.</summary>
    public static byte[] BuildQuery(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var labels = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var nameLength = 1; // Trailing zero byte
        foreach (var label in labels)
            nameLength += 1 + System.Text.Encoding.ASCII.GetByteCount(label);

        // Header: 12 bytes; Question: nameLength + 2 (type) + 2 (class)
        var packet = new byte[12 + nameLength + 4];

        // Transaction ID: 0 (recommended for one-shot mDNS)
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0, 2), 0);
        // Flags: 0x0000 (standard query)
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), 0);
        // QDCOUNT = 1
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), 1);

        var offset = 12;
        foreach (var label in labels)
        {
            var len = System.Text.Encoding.ASCII.GetByteCount(label);
            packet[offset++] = (byte)len;
            System.Text.Encoding.ASCII.GetBytes(label, packet.AsSpan(offset, len));
            offset += len;
        }
        packet[offset++] = 0; // End of QNAME

        // QTYPE = 1 (A record)
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, 2), 1);
        offset += 2;
        // QCLASS = 1 (IN) with unicast response bit (0x8000 | 0x0001 = 0x8001) per RFC 6762 section 5.1
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, 2), 0x8001);

        return packet;
    }

    /// <summary>
    /// Parses a legacy-unicast mDNS reply (RFC 6762 section 6.7) to our one-shot A query. The header must be a plain response
    /// (QR set, opcode 0, rcode 0, ID 0), the question must echo <paramref name="name"/> as an IN A question, and the first
    /// A record whose owner name equals <paramref name="name"/> (case-insensitive) and whose address is not loopback, "this"
    /// network, multicast/reserved, or link-local wins. CNAMEs are not followed: a record owned by any other name is ignored.
    /// The host's own <see cref="UdpDestinationPolicy"/> (private ranges) is applied afterwards by the broker.
    /// </summary>
    public static bool TryParseResponse(ReadOnlySpan<byte> data, string name, out IPAddress? address)
    {
        address = null;
        if (data.Length < 12) return false;

        if (BinaryPrimitives.ReadUInt16BigEndian(data[0..2]) != 0) return false;
        var flags = BinaryPrimitives.ReadUInt16BigEndian(data[2..4]);
        if ((flags & 0x8000) == 0 || ((flags >> 11) & 0xF) != 0 || (flags & 0xF) != 0) return false;

        var qdCount = BinaryPrimitives.ReadUInt16BigEndian(data[4..6]);
        var anCount = BinaryPrimitives.ReadUInt16BigEndian(data[6..8]);
        if (qdCount != 1 || anCount == 0) return false;

        var offset = 12;
        if (!TryReadName(data, ref offset, out var qname) || !string.Equals(qname, name, StringComparison.OrdinalIgnoreCase)) return false;
        if (offset + 4 > data.Length) return false;
        if (BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2)) != 1
            || (BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 2, 2)) & 0x7FFF) != 1) return false;
        offset += 4;

        for (var i = 0; i < anCount && i < 64; i++)
        {
            if (!TryReadName(data, ref offset, out var owner)) return false;
            if (offset + 10 > data.Length) return false;

            var type = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
            var cls = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 2, 2)) & 0x7FFF; // Mask off cache-flush bit
            var rdLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 8, 2));
            offset += 10;
            if (offset + rdLength > data.Length) return false;

            if (type == 1 && cls == 1 && rdLength == 4 && string.Equals(owner, name, StringComparison.OrdinalIgnoreCase))
            {
                var candidate = new IPAddress(data.Slice(offset, 4));
                if (IsUsableAnswer(candidate)) { address = candidate; return true; }
            }
            offset += rdLength;
        }

        return false;
    }

    private static bool IsUsableAnswer(IPAddress ip)
    {
        Span<byte> b = stackalloc byte[4];
        if (!ip.TryWriteBytes(b, out _)) return false;
        return !(b[0] == 0 || b[0] == 127 || b[0] >= 224 || (b[0] == 169 && b[1] == 254));
    }

    /// <summary>A reply must come from mDNS port 5353 on a private or link-local IPv4 address (the multicast scope is the local link).</summary>
    public static bool IsAcceptableSource(IPEndPoint source)
    {
        if (source.Port != MdnsPort || source.Address.AddressFamily != AddressFamily.InterNetwork) return false;
        Span<byte> b = stackalloc byte[4];
        if (!source.Address.TryWriteBytes(b, out _)) return false;
        return b[0] == 10 || (b[0] == 172 && (b[1] & 0xF0) == 16) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    /// <summary>Reads a possibly compressed name, advancing <paramref name="offset"/> past its first encoding. At most 16 pointer hops and 255 bytes.</summary>
    private static bool TryReadName(ReadOnlySpan<byte> data, ref int offset, out string name)
    {
        name = "";
        var pos = offset;
        var end = -1; // where the stream continues after the first pointer
        var hops = 0;
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            if (pos >= data.Length) return false;
            var len = data[pos];
            if (len == 0) { pos++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                if (pos + 2 > data.Length || ++hops > 16) return false;
                var target = ((len & 0x3F) << 8) | data[pos + 1];
                if (end < 0) end = pos + 2;
                pos = target;
                continue;
            }
            if ((len & 0xC0) != 0 || pos + 1 + len > data.Length) return false;
            if (sb.Length + len + 1 > 255) return false;
            if (sb.Length > 0) sb.Append('.');
            for (var i = 0; i < len; i++) sb.Append((char)data[pos + 1 + i]);
            pos += 1 + len;
        }
        offset = end >= 0 ? end : pos;
        name = sb.ToString();
        return true;
    }

    /// <summary>Sends an mDNS query and waits for a response up to the specified cancellation token.</summary>
    public static async Task<IPAddress?> ResolveAsync(string name, CancellationToken cancellation)
    {
        if (!ValidMdnsNameRegex().IsMatch(name)) return null;

        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            // RFC 6762 section 11: mDNS packets carry TTL 255. The egress interface is the OS default route's choice
            // and a Windows firewall may drop the query or the unicast reply; failure just means "not resolved".
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));

            var query = BuildQuery(name);
            var target = new IPEndPoint(MulticastAddress, MdnsPort);
            await socket.SendToAsync(query, target, cancellation).ConfigureAwait(false);

            var buffer = new byte[MaxPacketSize];
            while (!cancellation.IsCancellationRequested)
            {
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cancellation).ConfigureAwait(false);
                if (result.RemoteEndPoint is IPEndPoint source && IsAcceptableSource(source)
                    && TryParseResponse(buffer.AsSpan(0, result.ReceivedBytes), name, out var ip))
                    return ip;
            }
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine($"mDNS lookup failed: {error.GetType().Name}: {error.Message}"); return null; }

        return null;
    }
}
