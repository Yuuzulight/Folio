using System.Buffers.Binary;
using System.Text;

/// <summary>
/// Writes Folio's test "box font" (docs/study/19-testing.md): every glyph with ink is a filled em square from the
/// descender to the ascender, so text layout in tests is exact and independent of any system font.
/// </summary>
/// <remarks>
/// Design: 1000 units per em, ascender 800, descender -200, no line gap; every glyph advances 1000 units.
/// Space and no-break space are blank; .notdef is a hollow square. One kerning pair, U+00D7 × followed by U+00F7 ÷,
/// is -500 units, so shaping tests can see kerning without affecting ordinary text.
/// </remarks>
internal static class BoxFont
{
    private const int UnitsPerEm = 1000;
    private const int Ascender = 800;
    private const int Descender = -200;

    public static int[] CodePoints { get; } =
    [
        .. Enumerable.Range(0x20, 0x7F - 0x20),
        .. Enumerable.Range(0xA0, 0x100 - 0xA0),
        .. Enumerable.Range(0x2010, 0x2028 - 0x2010),
        0x2030, 0x20AC, 0x2122, 0xFFFD,
    ];

    private static bool IsBlank(int codePoint) => codePoint is 0x20 or 0xA0;

    public static byte[] Build()
    {
        // Glyph 0 is .notdef; glyph i + 1 maps CodePoints[i].
        var glyphCount = CodePoints.Length + 1;
        var glyphs = new List<byte[]> { HollowSquare() };
        glyphs.AddRange(CodePoints.Select(cp => IsBlank(cp) ? [] : FilledSquare()));

        var glyf = new List<byte>();
        var loca = new List<byte>();
        foreach (var glyph in glyphs)
        {
            loca.AddRange(U16(glyf.Count / 2));
            glyf.AddRange(glyph);
            if (glyf.Count % 2 != 0)
                glyf.Add(0);
        }
        loca.AddRange(U16(glyf.Count / 2));

        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["OS/2"] = Os2(),
            ["cmap"] = Cmap(),
            ["glyf"] = [.. glyf],
            ["head"] = Head(),
            ["hhea"] = Hhea(glyphCount),
            ["hmtx"] = [.. Enumerable.Range(0, glyphCount).SelectMany(_ => U16(1000).Concat(U16(0)))],
            ["kern"] = Kern(),
            ["loca"] = [.. loca],
            ["maxp"] = Maxp(glyphCount),
            ["name"] = Name(),
            ["post"] = Post(),
        };
        return Assemble(tables);
    }

    private static byte[] FilledSquare() => SimpleGlyph([(0, Descender, UnitsPerEm, Ascender)]);

    private static byte[] HollowSquare() => SimpleGlyph([(50, Descender + 50, UnitsPerEm - 50, Ascender - 50), (100, Descender + 100, UnitsPerEm - 100, Ascender - 100)]);

    // A glyph of rectangular contours (outer clockwise, inner counter-clockwise), no instructions, 16-bit coordinates.
    private static byte[] SimpleGlyph((int XMin, int YMin, int XMax, int YMax)[] rectangles)
    {
        var bytes = new List<byte>();
        var bounds = rectangles[0];
        bytes.AddRange(S16(rectangles.Length));
        bytes.AddRange(S16(bounds.XMin));
        bytes.AddRange(S16(bounds.YMin));
        bytes.AddRange(S16(bounds.XMax));
        bytes.AddRange(S16(bounds.YMax));
        for (var i = 0; i < rectangles.Length; i++)
            bytes.AddRange(U16(i * 4 + 3)); // endPtsOfContours
        bytes.AddRange(U16(0)); // instructionLength

        var points = new List<(int X, int Y)>();
        for (var i = 0; i < rectangles.Length; i++)
        {
            var (x0, y0, x1, y1) = rectangles[i];
            points.AddRange(i == 0
                ? [(x0, y0), (x0, y1), (x1, y1), (x1, y0)]
                : [(x0, y0), (x1, y0), (x1, y1), (x0, y1)]);
        }
        bytes.AddRange(points.Select(_ => (byte)0x01)); // ON_CURVE_POINT, 16-bit signed x and y deltas
        var (px, py) = (0, 0);
        foreach (var (x, _) in points)
        {
            bytes.AddRange(S16(x - px));
            px = x;
        }
        foreach (var (_, y) in points)
        {
            bytes.AddRange(S16(y - py));
            py = y;
        }
        return [.. bytes];
    }

    private static byte[] Head() =>
    [
        .. U32(0x00010000), .. U32(0x00010000), // version, fontRevision
        .. U32(0), // checksumAdjustment, patched after assembly
        .. U32(0x5F0F3CF5), .. U16(0x000B), .. U16(UnitsPerEm),
        .. new byte[16], // created, modified
        .. S16(0), .. S16(Descender), .. S16(UnitsPerEm), .. S16(Ascender),
        .. U16(0), .. U16(8), .. S16(2), .. S16(0), .. S16(0), // macStyle, lowestRecPPEM, fontDirectionHint, indexToLocFormat (short), glyphDataFormat
    ];

    private static byte[] Hhea(int glyphCount) =>
    [
        .. U32(0x00010000), .. S16(Ascender), .. S16(Descender), .. S16(0), .. U16(UnitsPerEm),
        .. S16(0), .. S16(0), .. S16(UnitsPerEm), .. S16(1), .. S16(0), .. S16(0),
        .. new byte[8], .. S16(0), .. U16(glyphCount),
    ];

    private static byte[] Maxp(int glyphCount) =>
    [
        .. U32(0x00010000), .. U16(glyphCount), .. U16(8), .. U16(2), .. U16(0), .. U16(0),
        .. U16(2), .. U16(0), .. U16(0), .. U16(0), .. U16(0), .. U16(0), .. U16(0), .. U16(0), .. U16(0),
    ];

    private static byte[] Os2() =>
    [
        .. U16(4), .. S16(UnitsPerEm), .. U16(400), .. U16(5), .. U16(0), // version, xAvgCharWidth, weight, width, fsType
        .. S16(650), .. S16(600), .. S16(0), .. S16(75), .. S16(650), .. S16(600), .. S16(0), .. S16(350), // sub/superscript
        .. S16(50), .. S16(300), .. S16(0), // strikeout size and position, family class
        .. new byte[10], // panose
        .. U32(1), .. U32(0), .. U32(0), .. U32(0), // unicode ranges: Basic Latin
        .. "FOLI"u8.ToArray(), .. U16(0x00C0), // vendor, fsSelection: REGULAR | USE_TYPO_METRICS
        .. U16(0x20), .. U16(0xFFFD), // first and last character
        .. S16(Ascender), .. S16(Descender), .. S16(0), .. U16(Ascender), .. U16(-Descender), // typo and win metrics
        .. U32(1), .. U32(0), // code page ranges: Latin 1
        .. S16(Ascender), .. S16(Ascender), .. U16(0), .. U16(0x20), .. U16(1), // x-height, cap height, default, break, max context
    ];

    // Format 4 over the code points, referenced by the Unicode (0,3) and Windows (3,1) encodings.
    private static byte[] Cmap()
    {
        var segments = new List<(int Start, int End, int FirstGlyph)>();
        for (var i = 0; i < CodePoints.Length; i++)
        {
            if (segments.Count > 0 && segments[^1].End + 1 == CodePoints[i])
                segments[^1] = segments[^1] with { End = CodePoints[i] };
            else
                segments.Add((CodePoints[i], CodePoints[i], i + 1));
        }
        segments.Add((0xFFFF, 0xFFFF, 0)); // required final segment

        var count = segments.Count;
        var searchRange = 2 * (1 << (int)Math.Floor(Math.Log2(count)));
        var sub = new List<byte>();
        sub.AddRange(U16(4));
        sub.AddRange(U16(16 + 8 * count));
        sub.AddRange(U16(0));
        sub.AddRange(U16(count * 2));
        sub.AddRange(U16(searchRange));
        sub.AddRange(U16((int)Math.Log2(searchRange / 2)));
        sub.AddRange(U16(count * 2 - searchRange));
        foreach (var s in segments)
            sub.AddRange(U16(s.End));
        sub.AddRange(U16(0));
        foreach (var s in segments)
            sub.AddRange(U16(s.Start));
        foreach (var s in segments)
            sub.AddRange(U16(s.Start == 0xFFFF ? 1 : (s.FirstGlyph - s.Start) & 0xFFFF)); // idDelta
        foreach (var _ in segments)
            sub.AddRange(U16(0)); // idRangeOffset

        return [.. U16(0), .. U16(2), .. U16(0), .. U16(3), .. U32(20), .. U16(3), .. U16(1), .. U32(20), .. sub];
    }

    private static byte[] Kern()
    {
        var left = Array.IndexOf(CodePoints, 0xD7) + 1;
        var right = Array.IndexOf(CodePoints, 0xF7) + 1;
        return
        [
            .. U16(0), .. U16(1), // version, one subtable
            .. U16(0), .. U16(6 + 8 + 6), .. U16(0x0001), // format 0, horizontal
            .. U16(1), .. U16(6), .. U16(0), .. U16(0), // nPairs, searchRange, entrySelector, rangeShift
            .. U16(left), .. U16(right), .. S16(-500),
        ];
    }

    private static byte[] Name()
    {
        (int Id, string Text)[] names =
        [
            (1, "Folio Box"), (2, "Regular"), (3, "Folio Box Regular 1.0"), (4, "Folio Box"), (5, "Version 1.0"), (6, "FolioBox-Regular"),
        ];
        var strings = new List<byte>();
        var records = new List<byte>();
        foreach (var (id, text) in names)
        {
            var bytes = Encoding.BigEndianUnicode.GetBytes(text);
            records.AddRange([.. U16(3), .. U16(1), .. U16(0x409), .. U16(id), .. U16(bytes.Length), .. U16(strings.Count)]);
            strings.AddRange(bytes);
        }
        return [.. U16(0), .. U16(names.Length), .. U16(6 + 12 * names.Length), .. records, .. strings];
    }

    private static byte[] Post() =>
    [
        .. U32(0x00030000), .. U32(0), .. S16(-100), .. S16(50), .. U32(1), .. new byte[16],
    ];

    // The sfnt container: table directory, 4-byte aligned tables, checksums, head checksumAdjustment.
    private static byte[] Assemble(SortedDictionary<string, byte[]> tables)
    {
        var numTables = tables.Count;
        var searchRange = 16 * (1 << (int)Math.Floor(Math.Log2(numTables)));
        var header = new List<byte>();
        header.AddRange(U32(0x00010000));
        header.AddRange(U16(numTables));
        header.AddRange(U16(searchRange));
        header.AddRange(U16((int)Math.Log2(searchRange / 16)));
        header.AddRange(U16(numTables * 16 - searchRange));

        var offset = 12 + 16 * numTables;
        var body = new List<byte>();
        var headOffset = 0;
        foreach (var (tag, data) in tables)
        {
            if (tag == "head")
                headOffset = offset + body.Count;
            header.AddRange(Encoding.ASCII.GetBytes(tag));
            header.AddRange(U32((int)Checksum(data)));
            header.AddRange(U32(offset + body.Count));
            header.AddRange(U32(data.Length));
            body.AddRange(data);
            while (body.Count % 4 != 0)
                body.Add(0);
        }

        var font = header.Concat(body).ToArray();
        var adjustment = unchecked(0xB1B0AFBA - Checksum(font));
        BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(headOffset + 8), adjustment);
        return font;
    }

    private static uint Checksum(byte[] data)
    {
        uint sum = 0;
        for (var i = 0; i < data.Length; i += 4)
        {
            uint word = 0;
            for (var k = 0; k < 4; k++)
                word = (word << 8) | (i + k < data.Length ? data[i + k] : 0u);
            sum = unchecked(sum + word);
        }
        return sum;
    }

    private static byte[] U16(int v) => [(byte)(v >> 8), (byte)v];

    private static byte[] S16(int v) => U16(v & 0xFFFF);

    private static byte[] U32(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
}
