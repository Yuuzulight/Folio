using System.Buffers.Binary;
using System.Text;

namespace Folio.Typography;

/// <summary>
/// A big-endian reader over font data that never trusts an offset: every read is bounds-checked and a bad offset
/// throws <see cref="InvalidDataException"/>, which <see cref="FontFace.Parse"/> turns into "not a usable font".
/// </summary>
internal readonly struct FontData(ReadOnlyMemory<byte> bytes)
{
    public ReadOnlySpan<byte> Span => bytes.Span;
    public int Length => bytes.Length;

    public FontData Slice(int offset, int length)
    {
        if (offset < 0 || length < 0 || (long)offset + length > bytes.Length)
            throw new InvalidDataException("Font table out of range.");
        return new FontData(bytes.Slice(offset, length));
    }

    public FontData From(int offset) => Slice(offset, Length - offset);

    private ReadOnlySpan<byte> At(int offset, int size)
    {
        if (offset < 0 || (long)offset + size > bytes.Length)
            throw new InvalidDataException("Font data out of range.");
        return bytes.Span.Slice(offset, size);
    }

    public byte U8(int offset) => At(offset, 1)[0];
    public ushort U16(int offset) => BinaryPrimitives.ReadUInt16BigEndian(At(offset, 2));
    public short S16(int offset) => BinaryPrimitives.ReadInt16BigEndian(At(offset, 2));
    public uint U32(int offset) => BinaryPrimitives.ReadUInt32BigEndian(At(offset, 4));
    public string Tag(int offset) => Encoding.ASCII.GetString(At(offset, 4));
    public ReadOnlySpan<byte> Bytes(int offset, int length) => At(offset, length);
}

/// <summary>Font styles for matching (https://www.w3.org/TR/css-fonts-4/#font-style-prop).</summary>
internal enum FaceStyle
{
    Normal,
    Italic,
    Oblique,
}

/// <summary>
/// One face of an OpenType/TrueType font, read by Folio's own parser (docs/study/11-text.md, font parsing): the
/// tables the engine needs for matching, metrics and simple shaping. Outlines are not read; the raster backend
/// draws glyphs by id.
/// </summary>
internal sealed class FontFace : IFontHandle
{
    private readonly FontData _data;
    private readonly Dictionary<string, (int Offset, int Length)> _tables;
    private readonly CharacterMap _cmap;
    private readonly ushort[] _advances;
    private readonly Dictionary<uint, short> _kerning;
    private readonly GposKerning? _gposKerning;

    private FontFace(FontData data, Dictionary<string, (int, int)> tables)
    {
        _data = data;
        _tables = tables;

        var head = Table("head");
        UnitsPerEm = head.U16(18);
        if (UnitsPerEm is < 16 or > 16384)
            throw new InvalidDataException("Bad unitsPerEm.");
        var macStyle = head.U16(44);

        GlyphCount = Table("maxp").U16(4);

        var hhea = Table("hhea");
        Ascent = hhea.S16(4);
        Descent = hhea.S16(6);
        LineGap = hhea.S16(8);
        var metricCount = hhea.U16(34);

        _advances = ReadAdvances(Table("hmtx"), metricCount, GlyphCount);
        _cmap = CharacterMap.Read(Table("cmap"));
        _kerning = TryTable("kern") is { } kern ? ReadKern(kern) : [];
        _gposKerning = TryTable("GPOS") is { } gpos ? GposKerning.Read(gpos) : null;

        Weight = (macStyle & 1) != 0 ? 700 : 400;
        Style = (macStyle & 2) != 0 ? FaceStyle.Italic : FaceStyle.Normal;
        Stretch = 100;
        if (TryTable("OS/2") is { Length: >= 78 } os2)
        {
            Weight = Math.Clamp((int)os2.U16(4), 1, 1000);
            StrikeoutSize = os2.S16(26);
            StrikeoutPosition = os2.S16(28);
            Stretch = os2.U16(6) switch { 1 => 50, 2 => 62.5f, 3 => 75, 4 => 87.5f, 6 => 112.5f, 7 => 125, 8 => 150, 9 => 200, _ => 100 };
            var fsSelection = os2.U16(62);
            Style = (fsSelection & 1) != 0 ? FaceStyle.Italic : (fsSelection & 0x200) != 0 ? FaceStyle.Oblique : Style;
            if ((fsSelection & 0x80) != 0)
            {
                // USE_TYPO_METRICS
                Ascent = os2.S16(68);
                Descent = os2.S16(70);
                LineGap = os2.S16(72);
            }
            if (os2.U16(0) >= 2 && os2.Length >= 90)
            {
                XHeight = os2.S16(86);
                CapHeight = os2.S16(88);
            }
        }

        if (TryTable("post") is { Length: >= 16 } post)
        {
            UnderlinePosition = post.S16(8);
            UnderlineThickness = post.S16(10);
            IsFixedPitch = post.U32(12) != 0;
        }

        var names = TryTable("name") is { } name ? ReadNames(name) : [];
        Family = names.GetValueOrDefault(16) ?? names.GetValueOrDefault(1) ?? "";
        Subfamily = names.GetValueOrDefault(17) ?? names.GetValueOrDefault(2) ?? "";
        FullName = names.GetValueOrDefault(4) ?? Family;
    }

    public string Family { get; }
    public string Subfamily { get; }
    public string FullName { get; }

    /// <summary>1–1000 (OS/2 usWeightClass).</summary>
    public int Weight { get; }

    public FaceStyle Style { get; }

    /// <summary>Width as a percentage of normal (OS/2 usWidthClass).</summary>
    public float Stretch { get; }

    public int UnitsPerEm { get; }
    public int GlyphCount { get; }

    /// <summary>Line metrics in font units (typo metrics when the font asks for them); descent is negative.</summary>
    public int Ascent { get; }
    public int Descent { get; }
    public int LineGap { get; }

    /// <summary>In font units; 0 when the font does not say.</summary>
    public int XHeight { get; }
    public int CapHeight { get; }
    public int UnderlinePosition { get; }
    public int UnderlineThickness { get; }

    /// <summary>The top of the strikeout stroke above the baseline, and its thickness (OS/2); zero when unknown.</summary>
    public int StrikeoutPosition { get; }
    public int StrikeoutSize { get; }
    public bool IsFixedPitch { get; }

    /// <summary>
    /// Parses one face; null when the data is not a usable font (truncated, bad offsets, missing required tables).
    /// Collections (<c>ttcf</c>) take a face index.
    /// </summary>
    public static FontFace? Parse(ReadOnlyMemory<byte> bytes, int faceIndex = 0)
    {
        try
        {
            var data = new FontData(bytes);
            var offset = 0;
            if (data.Length >= 12 && data.Tag(0) == "ttcf")
            {
                if (faceIndex < 0 || faceIndex >= data.U32(8))
                    return null;
                offset = checked((int)data.U32(12 + 4 * faceIndex));
            }
            else if (faceIndex != 0)
            {
                return null;
            }

            var version = data.U32(offset);
            if (version is not (0x00010000 or 0x4F54544F /* OTTO */ or 0x74727565 /* true */))
                return null;
            var count = data.U16(offset + 4);
            var tables = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
            for (var i = 0; i < count; i++)
            {
                var record = offset + 12 + 16 * i;
                var tableOffset = checked((int)data.U32(record + 8));
                var tableLength = checked((int)data.U32(record + 12));
                data.Slice(tableOffset, tableLength); // validates the range
                tables[data.Tag(record)] = (tableOffset, tableLength);
            }
            return new FontFace(data, tables) { Data = bytes, FaceIndex = faceIndex };
        }
        catch (Exception e) when (e is InvalidDataException or OverflowException or ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>The file this face came from.</summary>
    public ReadOnlyMemory<byte> Data { get; private init; }

    public int FaceIndex { get; private init; }

    public bool HasTable(string tag) => _tables.ContainsKey(tag);

    /// <summary>The glyph for a code point; 0 (.notdef) when the font has none.</summary>
    public ushort GlyphFor(int codePoint) => _cmap.Lookup(codePoint);

    public bool Covers(int codePoint) => GlyphFor(codePoint) != 0;

    /// <summary>Advance width in font units.</summary>
    public int Advance(ushort glyph) => glyph < _advances.Length ? _advances[glyph] : 0;

    /// <summary>
    /// Kerning between two glyphs in font units: from the GPOS kern feature when the font has one, else from the kern table.
    /// </summary>
    public int Kerning(ushort left, ushort right) =>
        _gposKerning?.Kerning(left, right) ?? _kerning.GetValueOrDefault(((uint)left << 16) | right);

    private FontData Table(string tag) => TryTable(tag) ?? throw new InvalidDataException($"Missing {tag} table.");

    private FontData? TryTable(string tag) => _tables.TryGetValue(tag, out var t) ? _data.Slice(t.Offset, t.Length) : null;

    // https://learn.microsoft.com/en-us/typography/opentype/spec/hmtx: the last advance repeats for the remaining glyphs.
    private static ushort[] ReadAdvances(FontData hmtx, int metricCount, int glyphCount)
    {
        if (metricCount == 0 || metricCount > glyphCount)
            throw new InvalidDataException("Bad numberOfHMetrics.");
        var advances = new ushort[glyphCount];
        for (var i = 0; i < glyphCount; i++)
            advances[i] = i < metricCount ? hmtx.U16(4 * i) : advances[metricCount - 1];
        return advances;
    }

    // https://learn.microsoft.com/en-us/typography/opentype/spec/kern: horizontal format 0 subtables (Windows layout).
    private static Dictionary<uint, short> ReadKern(FontData kern)
    {
        var pairs = new Dictionary<uint, short>();
        if (kern.U16(0) != 0)
            return pairs; // the Apple layout of the kern table is not read
        var offset = 4;
        for (var t = 0; t < kern.U16(2); t++)
        {
            var length = kern.U16(offset + 2);
            var coverage = kern.U16(offset + 4);
            // Format 0, horizontal, kerning values (not minimums), not cross-stream.
            if ((coverage & 0xFF00) == 0 && (coverage & 0x0007) == 0x0001)
            {
                var count = kern.U16(offset + 6);
                for (var i = 0; i < count; i++)
                {
                    var pair = offset + 14 + 6 * i;
                    pairs.TryAdd(((uint)kern.U16(pair) << 16) | kern.U16(pair + 2), kern.S16(pair + 4));
                }
            }
            offset += Math.Max((int)length, 6);
        }
        return pairs;
    }

    // https://learn.microsoft.com/en-us/typography/opentype/spec/name: Windows Unicode names first, then Mac Roman as ASCII.
    private static Dictionary<int, string> ReadNames(FontData name)
    {
        var names = new Dictionary<int, string>();
        var count = name.U16(2);
        var storage = name.U16(4);
        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = 0; i < count; i++)
            {
                var record = 6 + 12 * i;
                var (platform, encoding, language, id) = (name.U16(record), name.U16(record + 2), name.U16(record + 4), name.U16(record + 6));
                var bytes = name.Bytes(storage + name.U16(record + 10), name.U16(record + 8));
                string? text = pass == 0
                    ? (platform == 3 && encoding is 1 or 10 && (language & 0xFF) == 0x09) || platform == 0 ? Encoding.BigEndianUnicode.GetString(bytes) : null
                    : platform == 1 && encoding == 0 ? Encoding.Latin1.GetString(bytes) : null;
                if (text is not null)
                    names.TryAdd(id, text);
            }
        }
        return names;
    }
}

/// <summary>The cmap subtable Folio uses: format 12 (full Unicode) when present, else format 4 (BMP).</summary>
internal sealed class CharacterMap
{
    private enum Kind : byte
    {
        Sequential, // format 12: glyph = start glyph + offset
        Delta,      // format 4: glyph = (code point + delta) mod 65536
        Constant,   // one glyph
    }

    private readonly (uint Start, uint End, uint Glyph, Kind Kind)[] _groups;

    private CharacterMap((uint, uint, uint, Kind)[] groups) => _groups = groups;

    public static CharacterMap Read(FontData cmap)
    {
        var count = cmap.U16(2);
        int? format4 = null, format12 = null;
        for (var i = 0; i < count; i++)
        {
            var record = 4 + 8 * i;
            var (platform, encoding) = (cmap.U16(record), cmap.U16(record + 2));
            var offset = checked((int)cmap.U32(record + 4));
            var format = cmap.U16(offset);
            var unicode = platform == 0 || (platform == 3 && encoding is 1 or 10);
            if (!unicode)
                continue;
            if (format == 12)
                format12 ??= offset;
            else if (format == 4)
                format4 ??= offset;
        }
        if (format12 is { } f12)
            return ReadFormat12(cmap.From(f12));
        if (format4 is { } f4)
            return ReadFormat4(cmap.From(f4));
        return new CharacterMap([]);
    }

    private static CharacterMap ReadFormat12(FontData table)
    {
        var count = table.U32(12);
        if (count > (uint)(table.Length / 12))
            throw new InvalidDataException("Bad cmap group count.");
        var groups = new (uint, uint, uint, Kind)[count];
        for (var i = 0; i < groups.Length; i++)
        {
            var g = 16 + 12 * i;
            groups[i] = (table.U32(g), table.U32(g + 4), table.U32(g + 8), Kind.Sequential);
        }
        return new CharacterMap(groups);
    }

    // Format 4 segments with idRangeOffset 0 become delta groups; others are expanded into one group per code point.
    private static CharacterMap ReadFormat4(FontData table)
    {
        var segCount = table.U16(6) / 2;
        var endAt = 14;
        var startAt = endAt + 2 * segCount + 2;
        var deltaAt = startAt + 2 * segCount;
        var rangeAt = deltaAt + 2 * segCount;
        var groups = new List<(uint, uint, uint, Kind)>();
        for (var i = 0; i < segCount; i++)
        {
            uint end = table.U16(endAt + 2 * i), start = table.U16(startAt + 2 * i);
            var delta = table.U16(deltaAt + 2 * i);
            var rangeOffset = table.U16(rangeAt + 2 * i);
            if (start > end || start == 0xFFFF)
                continue;
            if (rangeOffset == 0)
            {
                groups.Add((start, end, delta, Kind.Delta));
                continue;
            }
            for (var c = start; c <= end; c++)
            {
                var glyphAt = rangeAt + 2 * i + rangeOffset + 2 * (int)(c - start);
                var glyph = table.U16(glyphAt);
                if (glyph != 0)
                    groups.Add((c, c, (uint)((glyph + delta) & 0xFFFF), Kind.Constant));
            }
        }
        return new CharacterMap([.. groups.OrderBy(g => g.Item1)]);
    }

    public ushort Lookup(int codePoint)
    {
        var c = (uint)codePoint;
        int lo = 0, hi = _groups.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            var g = _groups[mid];
            if (c < g.Start)
                hi = mid - 1;
            else if (c > g.End)
                lo = mid + 1;
            else
            {
                var glyph = g.Kind switch
                {
                    Kind.Delta => (c + g.Glyph) & 0xFFFF,
                    Kind.Constant => g.Glyph,
                    _ => g.Glyph + (c - g.Start),
                };
                return glyph > 0xFFFF ? (ushort)0 : (ushort)glyph;
            }
        }
        return 0;
    }
}
