using System.Buffers.Binary;
using System.IO.Compression;

namespace Folio.Tests.Typography;

/// <summary>
/// Test-only WOFF and WOFF 2 writers (https://www.w3.org/TR/WOFF/, https://www.w3.org/TR/WOFF2/), so the decoder can be
/// checked on round trips of Folio's own box font. The WOFF 2 writer applies the glyf/loca transform and, when the
/// font allows it, the hmtx transform.
/// </summary>
internal static class WebFontWriter
{
    public static Dictionary<string, byte[]> ReadTables(byte[] sfnt)
    {
        var count = BinaryPrimitives.ReadUInt16BigEndian(sfnt.AsSpan(4));
        var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var record = sfnt.AsSpan(12 + 16 * i);
            var tag = System.Text.Encoding.ASCII.GetString(record[..4]);
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(record[12..]);
            tables[tag] = sfnt.AsSpan(offset, length).ToArray();
        }
        return tables;
    }

    public static byte[] WriteSfnt(Dictionary<string, byte[]> tables)
    {
        var sorted = tables.OrderBy(t => t.Key, StringComparer.Ordinal).ToList();
        var output = new List<byte>();
        output.AddRange(U32(0x00010000));
        output.AddRange(U16(sorted.Count));
        output.AddRange(new byte[6]);
        var offset = 12 + 16 * sorted.Count;
        foreach (var (tag, data) in sorted)
        {
            output.AddRange(System.Text.Encoding.ASCII.GetBytes(tag));
            output.AddRange(U32(0));
            output.AddRange(U32((uint)offset));
            output.AddRange(U32((uint)data.Length));
            offset += (data.Length + 3) & ~3;
        }
        foreach (var (_, data) in sorted)
        {
            output.AddRange(data);
            output.AddRange(new byte[((data.Length + 3) & ~3) - data.Length]);
        }
        return [.. output];
    }

    /// <param name="compress">Whether tables are zlib-compressed (only kept when smaller, as the format requires).</param>
    public static byte[] Woff(byte[] sfnt, bool compress = true)
    {
        var tables = ReadTables(sfnt).OrderBy(t => t.Key, StringComparer.Ordinal).ToList();
        var stored = tables.Select(t =>
        {
            if (!compress)
                return t.Value;
            using var buffer = new MemoryStream();
            using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(t.Value);
            return buffer.Length < t.Value.Length ? buffer.ToArray() : t.Value;
        }).ToList();

        var header = 44 + 20 * tables.Count;
        var offset = header;
        var directory = new List<byte>();
        var data = new List<byte>();
        for (var i = 0; i < tables.Count; i++)
        {
            directory.AddRange(System.Text.Encoding.ASCII.GetBytes(tables[i].Key));
            directory.AddRange(U32((uint)offset));
            directory.AddRange(U32((uint)stored[i].Length));
            directory.AddRange(U32((uint)tables[i].Value.Length));
            directory.AddRange(U32(0));
            data.AddRange(stored[i]);
            var padding = ((stored[i].Length + 3) & ~3) - stored[i].Length;
            data.AddRange(new byte[padding]);
            offset += stored[i].Length + padding;
        }
        var total = header + data.Count;
        return [.. "wOFF"u8.ToArray(), .. U32(0x00010000), .. U32((uint)total), .. U16(tables.Count), 0, 0, .. U32((uint)sfnt.Length),
            0, 1, 0, 0, .. new byte[20], .. directory, .. data];
    }

    private static readonly string[] KnownTags = ["cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "cvt ", "fpgm", "glyf", "loca", "prep"];

    /// <param name="explicitBoxes">Store every simple glyph's bounding box instead of letting the decoder compute it.</param>
    public static byte[] Woff2(byte[] sfnt, bool explicitBoxes = false)
    {
        var tables = ReadTables(sfnt);
        var glyphCount = BinaryPrimitives.ReadUInt16BigEndian(tables["maxp"].AsSpan(4));
        var longLoca = BinaryPrimitives.ReadInt16BigEndian(tables["head"].AsSpan(50)) != 0;
        var glyphs = SplitGlyphs(tables["glyf"], tables["loca"], glyphCount, longLoca);
        var (glyf, xMins) = TransformGlyf(glyphs, longLoca, explicitBoxes);
        var hmtx = TransformHmtx(tables["hmtx"], BinaryPrimitives.ReadUInt16BigEndian(tables["hhea"].AsSpan(34)), xMins);

        // glyf must come before loca; the rest in tag order.
        var order = tables.Keys.Where(t => t is not ("glyf" or "loca")).Order(StringComparer.Ordinal).Prepend("loca").Prepend("glyf").ToList();
        var directory = new List<byte>();
        var stream = new List<byte>();
        foreach (var tag in order)
        {
            var known = Array.IndexOf(KnownTags, tag);
            var transformed = tag is "glyf" or "loca" || (tag == "hmtx" && hmtx is not null);
            var version = tag is "glyf" or "loca" ? 0 : transformed ? 1 : 0;
            directory.Add((byte)((version << 6) | (known >= 0 ? known : 63)));
            if (known < 0)
                directory.AddRange(System.Text.Encoding.ASCII.GetBytes(tag));
            directory.AddRange(Base128((uint)tables[tag].Length));
            var data = tag switch { "glyf" => glyf, "loca" => [], "hmtx" when hmtx is not null => hmtx, _ => tables[tag] };
            if (transformed)
                directory.AddRange(Base128((uint)data.Length));
            stream.AddRange(data);
        }
        var compressed = new byte[BrotliEncoder.GetMaxCompressedLength(stream.Count)];
        Assert.True(BrotliEncoder.TryCompress([.. stream], compressed, out var written));

        var total = 48 + directory.Count + written;
        return [.. "wOF2"u8.ToArray(), .. U32(0x00010000), .. U32((uint)total), .. U16(order.Count), 0, 0, .. U32((uint)sfnt.Length),
            .. U32((uint)written), 0, 1, 0, 0, .. new byte[20], .. directory, .. compressed.AsSpan(0, written)];
    }

    public static List<byte[]> SplitGlyphs(byte[] glyf, byte[] loca, int count, bool longLoca)
    {
        int Offset(int i) => longLoca ? (int)BinaryPrimitives.ReadUInt32BigEndian(loca.AsSpan(4 * i)) : 2 * BinaryPrimitives.ReadUInt16BigEndian(loca.AsSpan(2 * i));
        return [.. Enumerable.Range(0, count).Select(i => glyf.AsSpan(Offset(i), Offset(i + 1) - Offset(i)).ToArray())];
    }

    /// <summary>A simple glyph's contents, whatever flag encoding it was written with.</summary>
    public sealed record SimpleGlyph(short[] Box, ushort[] EndPoints, byte[] Instructions, (int X, int Y, bool On)[] Points)
    {
        public bool Same(SimpleGlyph other) => Box.SequenceEqual(other.Box) && EndPoints.SequenceEqual(other.EndPoints)
            && Instructions.SequenceEqual(other.Instructions) && Points.SequenceEqual(other.Points);
    }

    public static SimpleGlyph ReadSimple(byte[] glyph)
    {
        var span = glyph.AsSpan();
        var contours = BinaryPrimitives.ReadInt16BigEndian(span);
        var box = Enumerable.Range(0, 4).Select(i => BinaryPrimitives.ReadInt16BigEndian(glyph.AsSpan(2 + 2 * i))).ToArray();
        var ends = Enumerable.Range(0, contours).Select(i => BinaryPrimitives.ReadUInt16BigEndian(glyph.AsSpan(10 + 2 * i))).ToArray();
        var at = 10 + 2 * contours;
        var instructionLength = BinaryPrimitives.ReadUInt16BigEndian(span[at..]);
        var instructions = span.Slice(at + 2, instructionLength).ToArray();
        at += 2 + instructionLength;
        var count = contours == 0 ? 0 : ends[^1] + 1;
        var flags = new List<byte>();
        while (flags.Count < count)
        {
            var flag = span[at++];
            flags.Add(flag);
            if ((flag & 0x08) != 0)
                flags.AddRange(Enumerable.Repeat(flag, span[at++]));
        }
        int[] Coordinates(byte shortBit, byte sameBit)
        {
            var values = new int[count];
            var value = 0;
            for (var i = 0; i < count; i++)
            {
                if ((flags[i] & shortBit) != 0)
                    value += (flags[i] & sameBit) != 0 ? glyph[at++] : -glyph[at++];
                else if ((flags[i] & sameBit) == 0)
                {
                    value += BinaryPrimitives.ReadInt16BigEndian(glyph.AsSpan(at));
                    at += 2;
                }
                values[i] = value;
            }
            return values;
        }
        var xs = Coordinates(0x02, 0x10);
        var ys = Coordinates(0x04, 0x20);
        return new SimpleGlyph(box, ends, instructions, [.. Enumerable.Range(0, count).Select(i => (xs[i], ys[i], (flags[i] & 1) != 0))]);
    }

    private static (byte[] Data, short[] XMins) TransformGlyf(List<byte[]> glyphs, bool longLoca, bool explicitBoxes)
    {
        List<byte> contours = [], pointCounts = [], flags = [], data = [], composites = [], boxes = [], instructions = [];
        var bitmap = new byte[4 * ((glyphs.Count + 31) / 32)];
        var xMins = new short[glyphs.Count];
        for (var g = 0; g < glyphs.Count; g++)
        {
            var glyph = glyphs[g];
            if (glyph.Length == 0)
            {
                contours.AddRange(U16(0));
                continue;
            }
            var contourCount = BinaryPrimitives.ReadInt16BigEndian(glyph);
            contours.AddRange(U16((ushort)contourCount));
            xMins[g] = BinaryPrimitives.ReadInt16BigEndian(glyph.AsSpan(2));
            if (contourCount < 0)
            {
                bitmap[g >> 3] |= (byte)(0x80 >> (g & 7));
                boxes.AddRange(glyph[2..10]);
                var at = 10;
                ushort componentFlags;
                var withInstructions = false;
                do
                {
                    componentFlags = BinaryPrimitives.ReadUInt16BigEndian(glyph.AsSpan(at));
                    withInstructions |= (componentFlags & 0x100) != 0;
                    var size = 4 + ((componentFlags & 1) != 0 ? 4 : 2)
                        + ((componentFlags & 8) != 0 ? 2 : (componentFlags & 0x40) != 0 ? 4 : (componentFlags & 0x80) != 0 ? 8 : 0);
                    composites.AddRange(glyph[at..(at + size)]);
                    at += size;
                } while ((componentFlags & 0x20) != 0);
                if (withInstructions)
                {
                    var length = BinaryPrimitives.ReadUInt16BigEndian(glyph.AsSpan(at));
                    data.AddRange(U255(length));
                    instructions.AddRange(glyph[(at + 2)..(at + 2 + length)]);
                }
                continue;
            }

            var simple = ReadSimple(glyph);
            var previousEnd = -1;
            foreach (var end in simple.EndPoints)
            {
                pointCounts.AddRange(U255(end - previousEnd));
                previousEnd = end;
            }
            var (px, py) = (0, 0);
            foreach (var (x, y, on) in simple.Points)
            {
                var (flag, bytes) = Triplet(x - px, y - py);
                flags.Add((byte)(flag | (on ? 0 : 0x80)));
                data.AddRange(bytes);
                (px, py) = (x, y);
            }
            data.AddRange(U255(simple.Instructions.Length));
            instructions.AddRange(simple.Instructions);
            if (explicitBoxes)
            {
                bitmap[g >> 3] |= (byte)(0x80 >> (g & 7));
                boxes.AddRange(glyph[2..10]);
            }
        }
        List<byte>[] streams = [contours, pointCounts, flags, data, composites, [.. bitmap, .. boxes], instructions];
        List<byte> output = [0, 0, 0, 0, .. U16(glyphs.Count), .. U16(longLoca ? 1 : 0)];
        foreach (var s in streams)
            output.AddRange(U32((uint)s.Count));
        foreach (var s in streams)
            output.AddRange(s);
        return ([.. output], xMins);
    }

    // The WOFF 2 triplet encoding: the smallest of the six forms that holds the deltas.
    private static (int Flag, byte[] Bytes) Triplet(int dx, int dy)
    {
        var (ax, ay) = (Math.Abs(dx), Math.Abs(dy));
        var (xs, ys) = (dx > 0 ? 1 : 0, dy > 0 ? 1 : 0);
        if (dx == 0 && ay < 1280)
            return (((ay >> 8) << 1) | ys, [(byte)ay]);
        if (dy == 0 && ax < 1280)
            return (10 + ((ax >> 8) << 1) + xs, [(byte)ax]);
        if (ax is >= 1 and <= 64 && ay is >= 1 and <= 64)
            return (20 + (((ax - 1) & 0x30) | (((ay - 1) >> 4) << 2) | (ys << 1) | xs), [(byte)((((ax - 1) & 0x0F) << 4) | ((ay - 1) & 0x0F))]);
        if (ax is >= 1 and <= 768 && ay is >= 1 and <= 768)
            return (84 + 12 * ((ax - 1) >> 8) + 4 * ((ay - 1) >> 8) + (ys << 1) + xs, [(byte)(ax - 1), (byte)(ay - 1)]);
        if (ax < 4096 && ay < 4096)
            return (120 + (ys << 1) + xs, [(byte)(ax >> 4), (byte)(((ax & 0x0F) << 4) | (ay >> 8)), (byte)ay]);
        return (124 + (ys << 1) + xs, [.. U16(ax), .. U16(ay)]);
    }

    // Transformed only when the left side bearings equal xMin, per the flag bits the format defines.
    private static byte[]? TransformHmtx(byte[] hmtx, int metricCount, short[] xMins)
    {
        var lsb = Enumerable.Range(0, xMins.Length)
            .Select(i => BinaryPrimitives.ReadInt16BigEndian(hmtx.AsSpan(i < metricCount ? 4 * i + 2 : 4 * metricCount + 2 * (i - metricCount))))
            .ToArray();
        var proportional = Enumerable.Range(0, metricCount).All(i => lsb[i] == xMins[i]);
        var monospaced = Enumerable.Range(metricCount, xMins.Length - metricCount).All(i => lsb[i] == xMins[i]);
        if (!proportional && !monospaced)
            return null;
        List<byte> output = [(byte)((proportional ? 1 : 0) | (monospaced ? 2 : 0))];
        for (var i = 0; i < metricCount; i++)
            output.AddRange(hmtx.AsSpan(4 * i, 2));
        if (!proportional)
            output.AddRange(Enumerable.Range(0, metricCount).SelectMany(i => U16((ushort)lsb[i])));
        if (!monospaced)
            output.AddRange(Enumerable.Range(metricCount, xMins.Length - metricCount).SelectMany(i => U16((ushort)lsb[i])));
        return [.. output];
    }

    private static byte[] Base128(uint value)
    {
        var bytes = new List<byte> { (byte)(value & 0x7F) };
        for (value >>= 7; value != 0; value >>= 7)
            bytes.Insert(0, (byte)(0x80 | (value & 0x7F)));
        return [.. bytes];
    }

    private static byte[] U255(int value) => value switch
    {
        < 253 => [(byte)value],
        < 506 => [255, (byte)(value - 253)],
        < 762 => [254, (byte)(value - 506)],
        _ => [253, .. U16(value)],
    };

    public static byte[] U16(int value) => [(byte)(value >> 8), (byte)value];

    public static byte[] U32(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
}
