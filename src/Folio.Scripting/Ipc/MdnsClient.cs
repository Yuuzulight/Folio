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

    [GeneratedRegex(@"^[0-9a-f-]{36}\.local$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
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

    /// <summary>Parses an mDNS response for the first IPv4 Address (Type A) in the answer section.</summary>
    public static bool TryParseResponse(ReadOnlySpan<byte> data, string name, out IPAddress? address)
    {
        address = null;
        if (data.Length < 12) return false;

        // Flags: QR bit must be set (response)
        var flags = BinaryPrimitives.ReadUInt16BigEndian(data[2..4]);
        if ((flags & 0x8000) == 0) return false;

        var qdCount = BinaryPrimitives.ReadUInt16BigEndian(data[4..6]);
        var anCount = BinaryPrimitives.ReadUInt16BigEndian(data[6..8]);
        if (anCount == 0) return false;

        var offset = 12;

        // Skip Question section
        for (var i = 0; i < qdCount; i++)
        {
            if (!SkipName(data, ref offset)) return false;
            if (offset + 4 > data.Length) return false;
            offset += 4; // Skip QTYPE and QCLASS
        }

        // Parse Answer section (capped at at most 64 answers)
        for (var i = 0; i < anCount && i < 64 && offset < data.Length; i++)
        {
            if (!SkipName(data, ref offset)) return false;
            if (offset + 10 > data.Length) return false;

            var type = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
            var cls = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 2, 2)) & 0x7FFF; // Mask off cache-flush bit
            offset += 8; // Type (2) + Class (2) + TTL (4)

            var rdLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
            offset += 2;

            if (offset + rdLength > data.Length) return false;

            if (type == 1 && cls == 1 && rdLength == 4) // Type A, Class IN, IPv4 length 4
            {
                address = new IPAddress(data.Slice(offset, 4));
                return true;
            }

            offset += rdLength;
        }

        return false;
    }

    private static bool SkipName(ReadOnlySpan<byte> data, ref int offset)
    {
        var jumps = 0;
        while (offset < data.Length && jumps < 128)
        {
            var len = data[offset];
            if (len == 0)
            {
                offset++;
                return true;
            }
            if ((len & 0xC0) == 0xC0) // Compression pointer
            {
                if (offset + 2 > data.Length) return false;
                offset += 2;
                return true;
            }
            if ((len & 0xC0) != 0) return false; // Reserved or invalid label format

            if (offset + 1 + len > data.Length) return false;
            offset += 1 + len;
            jumps++;
        }
        return false;
    }

    /// <summary>Sends an mDNS query and waits for a response up to the specified cancellation token.</summary>
    public static async Task<IPAddress?> ResolveAsync(string name, CancellationToken cancellation)
    {
        if (!ValidMdnsNameRegex().IsMatch(name)) return null;

        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));

            var query = BuildQuery(name);
            var target = new IPEndPoint(MulticastAddress, MdnsPort);
            await socket.SendToAsync(query, target, cancellation).ConfigureAwait(false);

            var buffer = new byte[MaxPacketSize];
            while (!cancellation.IsCancellationRequested)
            {
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cancellation).ConfigureAwait(false);
                if (TryParseResponse(buffer.AsSpan(0, result.ReceivedBytes), name, out var ip))
                    return ip;
            }
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception) { return null; }

        return null;
    }
}
