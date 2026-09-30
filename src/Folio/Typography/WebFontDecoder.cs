using System.Buffers.Binary;
using System.IO.Compression;

namespace Folio.Typography;

/// <summary>
/// Unwraps web font files to plain OpenType/TrueType data (docs/dependencies.md, font file parsing): WOFF 1 tables
/// inflated with <see cref="ZLibStream"/>, WOFF 2 data with <see cref="BrotliDecoder"/> plus the reconstruction of
/// transformed <c>glyf</c>, <c>loca</c> and <c>hmtx</c> tables. Plain sfnt data passes through. Every size is checked
/// against the input and <see cref="MaxDecodedBytes"/> before anything is allocated; bad data returns null.
/// </summary>
internal static class WebFontDecoder
{
    /// <summary>The largest font a web font may unwrap to.</summary>
    // ponytail: one fixed cap; make it an option if hosts need larger fonts (CJK web fonts are usually subset).
    public const int MaxDecodedBytes = 32 * 1024 * 1024;

    /// <summary>The sfnt data of a WOFF, WOFF 2 or plain font file; null when it is none of these or is malformed.</summary>
    public static byte[]? Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
            return null;
        try
        {
            return BinaryPrimitives.ReadUInt32BigEndian(data) switch
            {
                0x774F4646 /* wOFF */ => DecodeWoff(data),
                0x774F4632 /* wOF2 */ => DecodeWoff2(data),
                0x00010000 or 0x4F54544F /* OTTO */ or 0x74727565 /* true */ or 0x74746366 /* ttcf */ => data.ToArray(),
                _ => null,
            };
        }
        catch (Exception e) when (e is InvalidDataException or IOException or OverflowException or ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    // https://www.w3.org/TR/WOFF/#WOFFHeader and #TableDirectory
    private static byte[] DecodeWoff(ReadOnlySpan<byte> data)
    {
        var r = new Reader(data);
        r.Skip(4);
        var flavor = r.U32();
        if (r.U32() != data.Length)
            throw new InvalidDataException("WOFF length does not match the data.");
        var count = r.U16();
        r.Skip(2 + 4 + 4 + 12 + 8); // reserved, totalSfntSize, versions, metadata, private data
        var tables = new List<Table>(count);
        long total = 0;
        for (var i = 0; i < count; i++)
        {
            var (tag, offset, compressed, original) = (r.U32(), r.U32(), r.U32(), r.U32());
            r.Skip(4); // origChecksum: recomputed below
            total += original;
            if (compressed > original || total > MaxDecodedBytes || (long)offset + compressed > data.Length)
                throw new InvalidDataException("Bad WOFF table entry.");
            var source = data.Slice((int)offset, (int)compressed);
            tables.Add(new Table(tag, compressed == original ? source.ToArray() : Inflate(source, (int)original)));
        }
        return WriteSfnt(flavor, tables);
    }

    private static byte[] Inflate(ReadOnlySpan<byte> source, int length)
    {
        var output = new byte[length];
        using var stream = new ZLibStream(new MemoryStream(source.ToArray()), CompressionMode.Decompress);
        stream.ReadExactly(output);
        if (stream.ReadByte() >= 0)
            throw new InvalidDataException("WOFF table inflates past its length.");
        return output;
    }

    // https://www.w3.org/TR/WOFF2/#table_dir_format: known tags by index 0-62; 63 means the tag follows.
    private static readonly string[] KnownTags =
    [
        "cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "cvt ", "fpgm", "glyf", "loca", "prep", "CFF ",
        "VORG", "EBDT", "EBLC", "gasp", "hdmx", "kern", "LTSH", "PCLT", "VDMX", "vhea", "vmtx", "BASE", "GDEF", "GPOS",
        "GSUB", "EBSC", "JSTF", "MATH", "CBDT", "CBLC", "COLR", "CPAL", "SVG ", "sbix", "acnt", "avar", "bdat", "bloc",
        "bsln", "cvar", "fdsc", "feat", "fmtx", "fvar", "gvar", "hsty", "just", "lcar", "mort", "morx", "opbd", "prop",
        "trak", "Zapf", "Silf", "Glat", "Gloc", "Feat", "Sill",
    ];

    private const uint Glyf = 0x676C7966, Loca = 0x6C6F6361, Hmtx = 0x686D7478, Hhea = 0x68686561, Maxp = 0x6D617870;

    // https://www.w3.org/TR/WOFF2/#woff20Header
    private static byte[] DecodeWoff2(ReadOnlySpan<byte> data)
    {
        var r = new Reader(data);
        r.Skip(4);
        var flavor = r.U32();
        if (flavor == 0x74746366)
            throw new InvalidDataException("WOFF 2 font collections are not supported."); // ponytail: rare on the web
        if (r.U32() != data.Length)
            throw new InvalidDataException("WOFF 2 length does not match the data.");
        var count = r.U16();
        r.Skip(2 + 4); // reserved, totalSfntSize
        var compressedSize = r.U32();
        r.Skip(4 + 12 + 8); // versions, metadata, private data

        var entries = new List<(uint Tag, int Transform, uint Original, uint Stored)>(count);
        long total = 0;
        for (var i = 0; i < count; i++)
        {
            var flags = r.U8();
            var tag = (flags & 0x3F) == 63 ? r.U32() : TagValue(KnownTags[flags & 0x3F]);
            var transform = flags >> 6;
            var original = r.Base128();
            // glyf and loca are transformed by version 0 and stored as-is by version 3; other tables the reverse.
            var transformed = tag is Glyf or Loca ? transform == 0 : transform != 0;
            if (tag is Glyf or Loca && transform is not (0 or 3) || tag == Hmtx && transform > 1 || tag is not (Glyf or Loca or Hmtx) && transform != 0)
                throw new InvalidDataException("Unknown WOFF 2 transform.");
            var stored = transformed ? r.Base128() : original;
            if (tag == Loca && transformed && stored != 0)
                throw new InvalidDataException("A transformed loca table has data.");
            total += stored;
            if (original > MaxDecodedBytes || total > MaxDecodedBytes)
                throw new InvalidDataException("WOFF 2 font is too large.");
            entries.Add((tag, transformed ? 1 : 0, original, stored));
        }

        if ((long)r.Position + compressedSize > data.Length)
            throw new InvalidDataException("WOFF 2 compressed data out of range.");
        var stream = new byte[total];
        if (!BrotliDecoder.TryDecompress(data.Slice(r.Position, (int)compressedSize), stream, out var written) || written != total)
            throw new InvalidDataException("WOFF 2 data does not decompress to its table sizes.");

        var tables = new List<Table>(count);
        var offset = 0;
        var parts = new Dictionary<uint, (int Offset, int Length, bool Transformed, uint Original)>();
        foreach (var (tag, transformed, original, stored) in entries)
        {
            parts[tag] = (offset, (int)stored, transformed == 1, original);
            offset += (int)stored;
        }

        short[]? xMins = null;
        foreach (var (tag, _, _, _) in entries)
        {
            var (start, length, transformed, original) = parts[tag];
            var bytes = stream.AsSpan(start, length);
            if (!transformed)
            {
                if (bytes.Length != original)
                    throw new InvalidDataException("WOFF 2 table size mismatch.");
                tables.Add(new Table(tag, bytes.ToArray()));
            }
            else if (tag == Glyf)
            {
                if (!parts.TryGetValue(Loca, out var loca) || !loca.Transformed)
                    throw new InvalidDataException("A transformed glyf table needs a transformed loca table.");
                var (glyf, locaBytes, mins) = ReconstructGlyf(bytes, loca.Original);
                xMins = mins;
                tables.Add(new Table(Glyf, glyf));
                tables.Add(new Table(Loca, locaBytes));
            }
            // A transformed loca is written with glyf, a transformed hmtx below, once glyf, hhea and maxp are known.
            CheckTotal(tables);
        }
        if (parts.TryGetValue(Loca, out var locaPart) && locaPart.Transformed && !(parts.TryGetValue(Glyf, out var glyfPart) && glyfPart.Transformed))
            throw new InvalidDataException("A transformed loca table needs a transformed glyf table.");
        if (parts.TryGetValue(Hmtx, out var hmtx) && hmtx.Transformed)
            tables.Add(new Table(Hmtx, ReconstructHmtx(stream.AsSpan(hmtx.Offset, hmtx.Length), xMins, tables, hmtx.Original)));
        CheckTotal(tables);
        return WriteSfnt(flavor, tables);
    }

    // https://www.w3.org/TR/WOFF2/#glyf_table_format
    private static (byte[] Glyf, byte[] Loca, short[] XMins) ReconstructGlyf(ReadOnlySpan<byte> data, uint locaLength)
    {
        var header = new Reader(data);
        header.Skip(2); // reserved
        var options = header.U16();
        var glyphCount = header.U16();
        var longOffsets = header.U16() != 0;
        var sizes = new int[7];
        for (var i = 0; i < sizes.Length; i++)
            sizes[i] = checked((int)header.U32());
        if (locaLength != (glyphCount + 1) * (longOffsets ? 4u : 2u))
            throw new InvalidDataException("loca size does not match the glyph count.");

        var at = header.Position;
        var contours = Take(data, ref at, sizes[0]);
        var pointCounts = Take(data, ref at, sizes[1]);
        var flags = Take(data, ref at, sizes[2]);
        var glyphs = Take(data, ref at, sizes[3]);
        var composites = Take(data, ref at, sizes[4]);
        var bitmapSize = 4 * ((glyphCount + 31) / 32);
        if (sizes[5] < bitmapSize)
            throw new InvalidDataException("bbox stream too short.");
        var bboxBitmap = Take(data, ref at, bitmapSize);
        var bboxes = Take(data, ref at, sizes[5] - bitmapSize);
        var instructions = Take(data, ref at, sizes[6]);
        var overlap = (options & 1) != 0 ? Take(data, ref at, (glyphCount + 7) / 8) : default;

        var glyf = new GlyphWriter();
        var loca = new byte[locaLength];
        var xMins = new short[glyphCount];
        var points = new List<(int X, int Y, bool On)>();
        for (var glyph = 0; glyph < glyphCount; glyph++)
        {
            var start = glyf.Length;
            var hasBox = (bboxBitmap.ByteAt(glyph >> 3) & (0x80 >> (glyph & 7))) != 0;
            var contourCount = (short)contours.U16();
            if (contourCount == 0)
            {
                if (hasBox)
                    throw new InvalidDataException("An empty glyph has a bounding box.");
            }
            else if (contourCount == -1)
            {
                if (!hasBox)
                    throw new InvalidDataException("A composite glyph needs an explicit bounding box.");
                glyf.S16(-1);
                var box = ReadBox(ref bboxes);
                glyf.Box(box);
                xMins[glyph] = box.XMin;
                // Component records: flags, glyph index, arguments, then an optional scale or matrix.
                bool more, withInstructions = false;
                do
                {
                    var componentFlags = composites.U16();
                    more = (componentFlags & 0x0020) != 0;
                    withInstructions |= (componentFlags & 0x0100) != 0;
                    var size = 2 + ((componentFlags & 0x0001) != 0 ? 4 : 2)
                        + ((componentFlags & 0x0008) != 0 ? 2 : (componentFlags & 0x0040) != 0 ? 4 : (componentFlags & 0x0080) != 0 ? 8 : 0);
                    glyf.U16(componentFlags);
                    glyf.Bytes(composites.Bytes(size));
                } while (more);
                if (withInstructions)
                {
                    var length = glyphs.U255();
                    glyf.U16((ushort)length);
                    glyf.Bytes(instructions.Bytes(length));
                }
            }
            else if (contourCount > 0)
            {
                var endPoints = new ushort[contourCount];
                var total = 0;
                for (var c = 0; c < contourCount; c++)
                {
                    total += pointCounts.U255();
                    if (total > 0xFFFF)
                        throw new InvalidDataException("Too many points.");
                    endPoints[c] = (ushort)(total - 1);
                }
                DecodeTriplets(ref flags, ref glyphs, total, points);
                var instructionLength = glyphs.U255();
                var box = hasBox ? ReadBox(ref bboxes) : BoxOf(points);
                xMins[glyph] = box.XMin;
                var overlapping = (options & 1) != 0 && (overlap.ByteAt(glyph >> 3) & (0x80 >> (glyph & 7))) != 0;

                glyf.S16(contourCount);
                glyf.Box(box);
                foreach (var end in endPoints)
                    glyf.U16(end);
                glyf.U16((ushort)instructionLength);
                glyf.Bytes(instructions.Bytes(instructionLength));
                glyf.SimpleOutline(points, overlapping);
            }
            else
            {
                throw new InvalidDataException("Bad contour count.");
            }
            glyf.PadTo(longOffsets ? 4 : 2);
            WriteLocaEntry(loca, glyph, start, longOffsets);
            if (glyf.Length > MaxDecodedBytes)
                throw new InvalidDataException("glyf reconstructs too large.");
        }
        WriteLocaEntry(loca, glyphCount, glyf.Length, longOffsets);
        return (glyf.ToArray(), loca, xMins);
    }

    private static Reader Take(ReadOnlySpan<byte> data, ref int at, int size)
    {
        if (size < 0 || (long)at + size > data.Length)
            throw new InvalidDataException("glyf stream out of range.");
        var stream = new Reader(data.Slice(at, size));
        at += size;
        return stream;
    }

    private static void WriteLocaEntry(byte[] loca, int index, int offset, bool longOffsets)
    {
        if (longOffsets)
            BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(index * 4), (uint)offset);
        else if (offset / 2 > 0xFFFF)
            throw new InvalidDataException("glyf too large for short loca offsets.");
        else
            BinaryPrimitives.WriteUInt16BigEndian(loca.AsSpan(index * 2), (ushort)(offset / 2));
    }

    private static (short XMin, short YMin, short XMax, short YMax) ReadBox(ref Reader bboxes) =>
        ((short)bboxes.U16(), (short)bboxes.U16(), (short)bboxes.U16(), (short)bboxes.U16());

    private static (short XMin, short YMin, short XMax, short YMax) BoxOf(List<(int X, int Y, bool On)> points)
    {
        if (points.Count == 0)
            return default;
        var (xMin, yMin, xMax, yMax) = (int.MaxValue, int.MaxValue, int.MinValue, int.MinValue);
        foreach (var (x, y, _) in points)
            (xMin, yMin, xMax, yMax) = (Math.Min(xMin, x), Math.Min(yMin, y), Math.Max(xMax, x), Math.Max(yMax, y));
        return (checked((short)xMin), checked((short)yMin), checked((short)xMax), checked((short)yMax));
    }

    // https://www.w3.org/TR/WOFF2/#triplet_decoding: one flag byte per point selects how many data bytes follow and
    // how they split into x and y deltas; the flag's low bits carry the signs.
    private static void DecodeTriplets(ref Reader flags, ref Reader data, int count, List<(int X, int Y, bool On)> points)
    {
        points.Clear();
        int x = 0, y = 0;
        for (var i = 0; i < count; i++)
        {
            var flag = flags.U8();
            var on = flag >> 7 == 0;
            flag &= 0x7F;
            int dx, dy;
            if (flag < 10)
            {
                dx = 0;
                dy = WithSign(flag, ((flag & 14) << 7) + data.U8());
            }
            else if (flag < 20)
            {
                dx = WithSign(flag, (((flag - 10) & 14) << 7) + data.U8());
                dy = 0;
            }
            else if (flag < 84)
            {
                var b0 = flag - 20;
                var b1 = data.U8();
                dx = WithSign(flag, 1 + (b0 & 0x30) + (b1 >> 4));
                dy = WithSign(flag >> 1, 1 + ((b0 & 0x0C) << 2) + (b1 & 0x0F));
            }
            else if (flag < 120)
            {
                var b0 = flag - 84;
                dx = WithSign(flag, 1 + ((b0 / 12) << 8) + data.U8());
                dy = WithSign(flag >> 1, 1 + (((b0 % 12) >> 2) << 8) + data.U8());
            }
            else if (flag < 124)
            {
                var b1 = data.U8();
                var b2 = data.U8();
                dx = WithSign(flag, (b1 << 4) + (b2 >> 4));
                dy = WithSign(flag >> 1, ((b2 & 0x0F) << 8) + data.U8());
            }
            else
            {
                dx = WithSign(flag, data.U16());
                dy = WithSign(flag >> 1, data.U16());
            }
            x += dx;
            y += dy;
            points.Add((x, y, on));
        }
    }

    private static int WithSign(int flag, int value) => (flag & 1) != 0 ? value : -value;

    // https://www.w3.org/TR/WOFF2/#hmtx_table_format: advances, then left side bearings unless flags say they equal
    // the glyphs' xMin (bit 0 for proportional glyphs, bit 1 for the monospaced tail).
    private static byte[] ReconstructHmtx(ReadOnlySpan<byte> data, short[]? xMins, List<Table> tables, uint original)
    {
        var hhea = tables.Find(t => t.Tag == Hhea)?.Data;
        var maxp = tables.Find(t => t.Tag == Maxp)?.Data;
        if (xMins is null || hhea is not { Length: >= 36 } || maxp is not { Length: >= 6 })
            throw new InvalidDataException("A transformed hmtx table needs a transformed glyf table, hhea and maxp.");
        var metricCount = BinaryPrimitives.ReadUInt16BigEndian(hhea.AsSpan(34));
        var glyphCount = BinaryPrimitives.ReadUInt16BigEndian(maxp.AsSpan(4));
        if (metricCount == 0 || metricCount > glyphCount || glyphCount != xMins.Length || original != 2u * metricCount + 2u * glyphCount)
            throw new InvalidDataException("hmtx does not match hhea and maxp.");

        var r = new Reader(data);
        var flags = r.U8();
        if ((flags & 0xFC) != 0 || (flags & 3) == 0)
            throw new InvalidDataException("Bad hmtx transform flags.");
        var advances = new ushort[metricCount];
        for (var i = 0; i < metricCount; i++)
            advances[i] = r.U16();
        var output = new byte[original];
        var o = 0;
        for (var i = 0; i < metricCount; i++, o += 4)
        {
            BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(o), advances[i]);
            BinaryPrimitives.WriteInt16BigEndian(output.AsSpan(o + 2), (flags & 1) != 0 ? xMins[i] : (short)r.U16());
        }
        for (var i = metricCount; i < glyphCount; i++, o += 2)
            BinaryPrimitives.WriteInt16BigEndian(output.AsSpan(o), (flags & 2) != 0 ? xMins[i] : (short)r.U16());
        return output;
    }

    private sealed record Table(uint Tag, byte[] Data);

    private static void CheckTotal(List<Table> tables)
    {
        if (tables.Sum(t => (long)t.Data.Length + 20) > MaxDecodedBytes)
            throw new InvalidDataException("Font is too large.");
    }

    private static uint TagValue(string tag) => BinaryPrimitives.ReadUInt32BigEndian([(byte)tag[0], (byte)tag[1], (byte)tag[2], (byte)tag[3]]);

    // https://learn.microsoft.com/en-us/typography/opentype/spec/otff#table-directory: records sorted by tag,
    // tables 4-byte aligned with their checksums.
    private static byte[] WriteSfnt(uint flavor, List<Table> tables)
    {
        tables.Sort((a, b) => a.Tag.CompareTo(b.Tag));
        for (var i = 1; i < tables.Count; i++)
        {
            if (tables[i].Tag == tables[i - 1].Tag)
                throw new InvalidDataException("Duplicate table.");
        }
        var count = tables.Count;
        var headerSize = 12 + 16 * count;
        var size = (long)headerSize + tables.Sum(t => (t.Data.Length + 3L) & ~3L);
        if (size > MaxDecodedBytes)
            throw new InvalidDataException("Font is too large.");
        var output = new byte[size];
        var span = output.AsSpan();
        var power = count == 0 ? 0 : (int)Math.Log2(count);
        BinaryPrimitives.WriteUInt32BigEndian(span, flavor);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], (ushort)count);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], (ushort)(16 << power));
        BinaryPrimitives.WriteUInt16BigEndian(span[8..], (ushort)power);
        BinaryPrimitives.WriteUInt16BigEndian(span[10..], (ushort)(count * 16 - (16 << power)));
        var offset = headerSize;
        for (var i = 0; i < count; i++)
        {
            var (tag, data) = (tables[i].Tag, tables[i].Data);
            var record = span[(12 + 16 * i)..];
            data.CopyTo(span[offset..]);
            BinaryPrimitives.WriteUInt32BigEndian(record, tag);
            BinaryPrimitives.WriteUInt32BigEndian(record[4..], Checksum(span.Slice(offset, (data.Length + 3) & ~3)));
            BinaryPrimitives.WriteUInt32BigEndian(record[8..], (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(record[12..], (uint)data.Length);
            offset += (data.Length + 3) & ~3;
        }
        return output;
    }

    private static uint Checksum(ReadOnlySpan<byte> padded)
    {
        uint sum = 0;
        for (var i = 0; i < padded.Length; i += 4)
            sum += BinaryPrimitives.ReadUInt32BigEndian(padded[i..]);
        return sum;
    }

    /// <summary>A bounds-checked big-endian cursor; reading past the end throws <see cref="InvalidDataException"/>.</summary>
    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;

        public int Position { get; private set; }

        public ReadOnlySpan<byte> Bytes(int count)
        {
            if (count < 0 || (long)Position + count > _data.Length)
                throw new InvalidDataException("Web font data out of range.");
            var bytes = _data.Slice(Position, count);
            Position += count;
            return bytes;
        }

        public void Skip(int count) => Bytes(count);

        public readonly byte ByteAt(int index) =>
            index < _data.Length ? _data[index] : throw new InvalidDataException("Web font data out of range.");

        public byte U8() => Bytes(1)[0];

        public ushort U16() => BinaryPrimitives.ReadUInt16BigEndian(Bytes(2));

        public uint U32() => BinaryPrimitives.ReadUInt32BigEndian(Bytes(4));

        // https://www.w3.org/TR/WOFF2/#UIntBase128: at most five bytes, no leading zeros, no overflow.
        public uint Base128()
        {
            uint value = 0;
            for (var i = 0; i < 5; i++)
            {
                var b = U8();
                if (i == 0 && b == 0x80)
                    throw new InvalidDataException("UIntBase128 with a leading zero.");
                if ((value & 0xFE000000) != 0)
                    throw new InvalidDataException("UIntBase128 overflows.");
                value = (value << 7) | (uint)(b & 0x7F);
                if ((b & 0x80) == 0)
                    return value;
            }
            throw new InvalidDataException("UIntBase128 longer than five bytes.");
        }

        // https://www.w3.org/TR/WOFF2/#255UInt16
        public int U255() => U8() switch
        {
            253 => U16(),
            255 => U8() + 253,
            254 => U8() + 506,
            var b => b,
        };
    }

    /// <summary>Writes reconstructed glyph records.</summary>
    private sealed class GlyphWriter
    {
        private readonly MemoryStream _stream = new();

        public int Length => (int)_stream.Length;

        public void U16(ushort value)
        {
            Span<byte> bytes = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
            _stream.Write(bytes);
        }

        public void S16(short value) => U16((ushort)value);

        public void Bytes(ReadOnlySpan<byte> bytes) => _stream.Write(bytes);

        public void Box((short XMin, short YMin, short XMax, short YMax) box)
        {
            S16(box.XMin);
            S16(box.YMin);
            S16(box.XMax);
            S16(box.YMax);
        }

        public void PadTo(int alignment)
        {
            while (_stream.Length % alignment != 0)
                _stream.WriteByte(0);
        }

        // TrueType simple glyph flags and coordinates: a byte per delta when it fits, "same" when zero; no repeats.
        public void SimpleOutline(List<(int X, int Y, bool On)> points, bool overlapping)
        {
            int px = 0, py = 0;
            for (var i = 0; i < points.Count; i++)
            {
                var (dx, dy) = (points[i].X - px, points[i].Y - py);
                (px, py) = (points[i].X, points[i].Y);
                var flag = (points[i].On ? 0x01 : 0) | (i == 0 && overlapping ? 0x40 : 0);
                flag |= dx == 0 ? 0x10 : Math.Abs(dx) < 256 ? 0x02 | (dx > 0 ? 0x10 : 0) : 0;
                flag |= dy == 0 ? 0x20 : Math.Abs(dy) < 256 ? 0x04 | (dy > 0 ? 0x20 : 0) : 0;
                _stream.WriteByte((byte)flag);
            }
            WriteDeltas(points, p => p.X);
            WriteDeltas(points, p => p.Y);
        }

        private void WriteDeltas(List<(int X, int Y, bool On)> points, Func<(int X, int Y, bool On), int> coordinate)
        {
            var previous = 0;
            foreach (var point in points)
            {
                var delta = coordinate(point) - previous;
                previous = coordinate(point);
                if (delta == 0)
                    continue;
                if (Math.Abs(delta) < 256)
                    _stream.WriteByte((byte)Math.Abs(delta));
                else
                    S16(checked((short)delta));
            }
        }

        public byte[] ToArray() => _stream.ToArray();
    }
}
