using System.Buffers.Binary;

namespace Folio.Scripting.Rtc;

/// <summary>One reception report block (RFC 3550 section 6.4.1).</summary>
internal readonly record struct RtcpReportBlock(uint Ssrc, byte FractionLost, int CumulativeLost, uint ExtendedHighestSequence, uint Jitter, uint LastSr, uint DelaySinceLastSr);

internal enum RtcpKind { SenderReport, ReceiverReport, Bye, Other }

internal sealed record RtcpPacket(RtcpKind Kind, uint Ssrc, uint NtpMiddle, uint RtpTimestamp, uint PacketCount, uint OctetCount, IReadOnlyList<RtcpReportBlock> Blocks, IReadOnlyList<uint> ByeSsrcs);

/// <summary>Builds and parses the RTCP subset Folio needs: SR, RR, SDES CNAME and BYE. Parsing is bounded and never throws on hostile input.</summary>
internal static class RtcpCodec
{
    internal const int MaxPacketsPerCompound = 16;
    internal const int MaxBlocks = 31;

    internal static byte[] BuildReport(uint ssrc, bool sender, long ntp, uint rtpTimestamp, uint packets, uint octets,
        IReadOnlyList<RtcpReportBlock> blocks, string cname)
    {
        var count = Math.Min(blocks.Count, MaxBlocks);
        var cnameBytes = System.Text.Encoding.ASCII.GetBytes(cname.Length > 60 ? cname[..60] : cname);
        var reportLength = (sender ? 28 : 8) + count * 24;
        var sdesLength = (4 + 4 + 2 + cnameBytes.Length + 1 + 3) & ~3;
        var output = new byte[reportLength + sdesLength];
        output[0] = (byte)(0x80 | count);
        output[1] = (byte)(sender ? 200 : 201);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(2), (ushort)(reportLength / 4 - 1));
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(4), ssrc);
        var offset = 8;
        if (sender)
        {
            BinaryPrimitives.WriteUInt64BigEndian(output.AsSpan(8), (ulong)ntp);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(16), rtpTimestamp);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(20), packets);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(24), octets);
            offset = 28;
        }
        for (var i = 0; i < count; i++, offset += 24)
        {
            var b = blocks[i];
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(offset), b.Ssrc);
            output[offset + 4] = b.FractionLost;
            var lost = Math.Clamp(b.CumulativeLost, -0x800000, 0x7FFFFF) & 0xFFFFFF;
            output[offset + 5] = (byte)(lost >> 16); output[offset + 6] = (byte)(lost >> 8); output[offset + 7] = (byte)lost;
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(offset + 8), b.ExtendedHighestSequence);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(offset + 12), b.Jitter);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(offset + 16), b.LastSr);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(offset + 20), b.DelaySinceLastSr);
        }
        var sdes = output.AsSpan(reportLength);
        sdes[0] = 0x81; sdes[1] = 202;
        BinaryPrimitives.WriteUInt16BigEndian(sdes[2..], (ushort)(sdesLength / 4 - 1));
        BinaryPrimitives.WriteUInt32BigEndian(sdes[4..], ssrc);
        sdes[8] = 1; sdes[9] = (byte)cnameBytes.Length;
        cnameBytes.CopyTo(sdes[10..]);
        return output;
    }

    internal static byte[] BuildBye(uint ssrc)
    {
        var output = new byte[8];
        output[0] = 0x81; output[1] = 203;
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(4), ssrc);
        return output;
    }

    /// <summary>Parses a compound packet. Malformed or truncated trailing packets are ignored; at most <see cref="MaxPacketsPerCompound"/> are read.</summary>
    internal static List<RtcpPacket> Parse(ReadOnlySpan<byte> data)
    {
        var result = new List<RtcpPacket>();
        var offset = 0;
        while (offset + 4 <= data.Length && result.Count < MaxPacketsPerCompound)
        {
            if ((data[offset] >> 6) != 2) break;
            var count = data[offset] & 0x1F;
            var type = data[offset + 1];
            var length = (BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]) + 1) * 4;
            if (offset + length > data.Length) break;
            var body = data.Slice(offset, length);
            switch (type)
            {
                case 200 when length >= 28:
                {
                    var blocks = ParseBlocks(body[28..], count);
                    var ntp = BinaryPrimitives.ReadUInt64BigEndian(body[8..]);
                    result.Add(new RtcpPacket(RtcpKind.SenderReport, BinaryPrimitives.ReadUInt32BigEndian(body[4..]), (uint)(ntp >> 16),
                        BinaryPrimitives.ReadUInt32BigEndian(body[16..]), BinaryPrimitives.ReadUInt32BigEndian(body[20..]),
                        BinaryPrimitives.ReadUInt32BigEndian(body[24..]), blocks, []));
                    break;
                }
                case 201 when length >= 8:
                    result.Add(new RtcpPacket(RtcpKind.ReceiverReport, BinaryPrimitives.ReadUInt32BigEndian(body[4..]), 0, 0, 0, 0, ParseBlocks(body[8..], count), []));
                    break;
                case 203:
                {
                    var ssrcs = new List<uint>();
                    for (var i = 0; i < count && 4 + i * 4 + 4 <= length; i++) ssrcs.Add(BinaryPrimitives.ReadUInt32BigEndian(body[(4 + i * 4)..]));
                    result.Add(new RtcpPacket(RtcpKind.Bye, ssrcs.Count > 0 ? ssrcs[0] : 0, 0, 0, 0, 0, [], ssrcs));
                    break;
                }
                default:
                    result.Add(new RtcpPacket(RtcpKind.Other, 0, 0, 0, 0, 0, [], []));
                    break;
            }
            offset += length;
        }
        return result;
    }

    private static List<RtcpReportBlock> ParseBlocks(ReadOnlySpan<byte> data, int count)
    {
        var blocks = new List<RtcpReportBlock>();
        for (var i = 0; i < count && i < MaxBlocks && (i + 1) * 24 <= data.Length; i++)
        {
            var b = data.Slice(i * 24, 24);
            var lost = (b[5] << 16) | (b[6] << 8) | b[7];
            if ((lost & 0x800000) != 0) lost -= 0x1000000;
            blocks.Add(new RtcpReportBlock(BinaryPrimitives.ReadUInt32BigEndian(b), b[4], lost, BinaryPrimitives.ReadUInt32BigEndian(b[8..]),
                BinaryPrimitives.ReadUInt32BigEndian(b[12..]), BinaryPrimitives.ReadUInt32BigEndian(b[16..]), BinaryPrimitives.ReadUInt32BigEndian(b[20..])));
        }
        return blocks;
    }
}
