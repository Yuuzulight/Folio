using System.Buffers.Binary;
using Folio.Css;
using Folio.Resources;
using Folio.Style;
using Folio.Typography;
using static Folio.Tests.Typography.WebFontWriter;

namespace Folio.Tests.Typography;

public class WebFontTests
{
    private static readonly byte[] Box = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fonts", "FolioBox.ttf"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WoffUnwrapsToTheSameTables(bool compress)
    {
        var decoded = WebFontDecoder.Decode(Woff(Box, compress));

        Assert.NotNull(decoded);
        AssertTablesEqual(ReadTables(Box), ReadTables(decoded));
        Assert.Equal("Folio Box", FontFace.Parse(decoded)!.Family);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Woff2ReconstructsGlyfLocaAndHmtx(bool explicitBoxes)
    {
        var decoded = WebFontDecoder.Decode(Woff2(Box, explicitBoxes));

        Assert.NotNull(decoded);
        var (original, result) = (ReadTables(Box), ReadTables(decoded));
        AssertTablesEqual(original.Where(t => t.Key is not ("glyf" or "loca")).ToDictionary(), result.Where(t => t.Key is not ("glyf" or "loca")).ToDictionary());
        AssertSameGlyphs(original, result);
        var face = FontFace.Parse(decoded)!;
        Assert.Equal((220, 1000, -500), (face.GlyphCount, face.Advance(face.GlyphFor('W')), face.Kerning(face.GlyphFor('×'), face.GlyphFor('÷'))));
    }

    [Fact]
    public void Woff2ReconstructsCompositeGlyphsInstructionsAndSideBearingsFromXMin()
    {
        var font = WithCompositeGlyph(Box);

        var decoded = WebFontDecoder.Decode(Woff2(font));

        Assert.NotNull(decoded);
        var (original, result) = (ReadTables(font), ReadTables(decoded));
        Assert.Equal(original["hmtx"], result["hmtx"]); // stored as "left side bearing = xMin" for every glyph
        AssertSameGlyphs(original, result);
        var glyphs = SplitGlyphs(result["glyf"], result["loca"], 221, longLoca: false);
        Assert.Equal(SplitGlyphs(original["glyf"], original["loca"], 221, longLoca: false)[220], glyphs[220]);
    }

    [Fact]
    public void PlainFontsPassThroughAndOtherDataIsRefused()
    {
        Assert.Equal(Box, WebFontDecoder.Decode(Box));
        Assert.Null(WebFontDecoder.Decode("<html>"u8));
        Assert.Null(WebFontDecoder.Decode([]));
    }

    [Fact]
    public void RefusesMalformedWoff()
    {
        var woff = Woff(Box);
        Assert.Null(WebFontDecoder.Decode(woff.AsSpan(0, woff.Length / 2)));   // truncated: the length field disagrees
        Assert.Null(WebFontDecoder.Decode(Patch(woff, 8, (uint)woff.Length + 4)));

        var firstCompressed = FirstEntry(woff, e => e.Compressed + 1 < e.Original);
        Assert.Null(WebFontDecoder.Decode(Patch(woff, firstCompressed + 12, 0x7FFFFFFF)));                   // claims a huge table
        Assert.Null(WebFontDecoder.Decode(Patch(woff, firstCompressed + 12, EntryAt(woff, firstCompressed).Original - 1))); // inflates past its length
        Assert.Null(WebFontDecoder.Decode(Patch(woff, firstCompressed + 12, EntryAt(woff, firstCompressed).Original + 1))); // inflates short
        Assert.Null(WebFontDecoder.Decode(Patch(woff, firstCompressed + 4, (uint)woff.Length)));             // offset out of range
    }

    [Fact]
    public void RefusesMalformedWoff2()
    {
        var woff2 = Woff2(Box);
        Assert.Null(WebFontDecoder.Decode(woff2.AsSpan(0, woff2.Length - 10)));
        Assert.Null(WebFontDecoder.Decode(Patch(woff2, 20, 0x10000000))); // compressed size past the end

        // Corrupt the compressed stream: it no longer decompresses to the declared table sizes.
        var corrupt = (byte[])woff2.Clone();
        for (var i = corrupt.Length - 40; i < corrupt.Length; i++)
            corrupt[i] ^= 0x5A;
        Assert.Null(WebFontDecoder.Decode(corrupt));

        // A table directory whose sizes add up past the cap is refused before anything is decompressed.
        byte[] huge = [.. woff2.AsSpan(0, 12), 0, 1, 0, 0, .. new byte[32], 0x0A | (3 << 6), 0x8F, 0xFF, 0xFF, 0xFF, 0x7F];
        BinaryPrimitives.WriteUInt32BigEndian(huge.AsSpan(8), (uint)huge.Length);
        Assert.Null(WebFontDecoder.Decode(huge));
    }

    [Fact]
    public void ParsesFontFaceDescriptors()
    {
        var rule = ParseRule("""
            font-family: "My Font"; src: local(Arial), url(a.woff2) format("woff2"), url(b.svg) format(svg), url(c.woff) tech(color-COLRv1);
            font-weight: 300 700; font-style: italic; font-stretch: condensed 125%; unicode-range: U+0-7F, u+4??, U+1F600-1F64F;
            font-display: swap
            """, "https://example.com/css/site.css");

        Assert.NotNull(rule);
        Assert.Equal("My Font", rule.Family);
        Assert.Equal([new FontFaceSource(null, "Arial"), new FontFaceSource("https://example.com/css/a.woff2", null)], rule.Sources);
        Assert.Equal((300, 700), rule.Weight);
        Assert.Equal(FontStyle.Italic, rule.Style);
        Assert.Equal((75f, 125f), rule.Stretch);
        Assert.Equal([(0, 0x7F), (0x400, 0x4FF), (0x1F600, 0x1F64F)], rule.UnicodeRange);
        Assert.Equal(FontDisplay.Swap, rule.Display);
        Assert.True(rule.Covers('A') && rule.Covers(0x4A0) && !rule.Covers(0x100));
    }

    [Fact]
    public void FontFaceDefaultsAndInvalidDescriptors()
    {
        var rule = ParseRule("font-family: Box Font; src: url(data:,x); font-weight: 1001; unicode-range: U+?0; font-display: soon", null)!;

        Assert.Equal("Box Font", rule.Family);
        Assert.Null(rule.Weight);                               // invalid: stays auto
        Assert.Equal([(0, 0x10FFFF)], rule.UnicodeRange);      // invalid: stays everything
        Assert.Equal(FontDisplay.Auto, rule.Display);
        Assert.Null(ParseRule("src: url(a.woff)", null));        // no family
        Assert.Null(ParseRule("font-family: A; src: url(a.svg) format(svg)", null)); // no usable source
        Assert.Null(ParseRule("font-family: A; src: url(a.woff) nonsense", null));
    }

    [Fact]
    public void WebFacesShadowInstalledFamiliesAndHonourUnicodeRange()
    {
        var fonts = new FontCollection(new FontFolderSource(Path.Combine(AppContext.BaseDirectory, "fonts")));
        var dataUrl = "data:font/woff2;base64," + Convert.ToBase64String(Woff2(Box));
        var rules = new[]
        {
            ParseRule($"font-family: Web; src: url(https://fonts.example/web.woff2), url({dataUrl}); unicode-range: U+41-5A; font-weight: 100 900", null)!,
            ParseRule("font-family: Folio Box; src: url(https://fonts.example/box.woff2)", null)!,
        };
        var reports = new List<string>();

        WebFonts.Load(rules, fonts, ResourceLoader.DataUrlsOnly, reports.Add);

        Assert.Equal("Folio Box", fonts.Match("Web", FaceStyle.Normal, 650, 100)?.Family);          // from the data: URL
        Assert.NotNull(fonts.FaceForCluster(["Web"], FaceStyle.Normal, 400, 100, "A"));
        Assert.Null(fonts.FaceForCluster(["Web"], FaceStyle.Normal, 400, 100, "a"));                 // outside its range
        Assert.Null(fonts.Match("Folio Box", FaceStyle.Normal, 400, 100));                           // blocked, and not the installed font
        Assert.Contains(reports, r => r.Contains("https://fonts.example/box.woff2", StringComparison.Ordinal));
    }

    private static FontFaceRule? ParseRule(string declarations, string? baseUrl)
    {
        var sheet = CssParser.ParseStyleSheet("@font-face {" + declarations + "}");
        return FontFaceRule.Parse(sheet.Source, (AtRule)sheet.Rules[0], baseUrl);
    }

    private static void AssertTablesEqual(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (tag, data) in expected)
            Assert.True(data.AsSpan().SequenceEqual(actual[tag]), $"Table {tag} differs.");
    }

    private static void AssertSameGlyphs(Dictionary<string, byte[]> original, Dictionary<string, byte[]> result)
    {
        var count = BinaryPrimitives.ReadUInt16BigEndian(original["maxp"].AsSpan(4));
        var before = SplitGlyphs(original["glyf"], original["loca"], count, longLoca: false);
        var after = SplitGlyphs(result["glyf"], result["loca"], count, longLoca: false);
        for (var g = 0; g < count; g++)
        {
            Assert.Equal(before[g].Length == 0, after[g].Length == 0);
            if (before[g].Length > 0 && BinaryPrimitives.ReadInt16BigEndian(before[g]) > 0)
                Assert.True(ReadSimple(before[g]).Same(ReadSimple(after[g])), $"Glyph {g} differs.");
        }
    }

    // The box font plus glyph 220: a composite of 'A' moved 100 units right, with two bytes of instructions. The
    // .notdef side bearing is set to its xMin, so every side bearing equals xMin and hmtx can drop them all.
    private static byte[] WithCompositeGlyph(byte[] font)
    {
        var tables = ReadTables(font);
        var glyphs = SplitGlyphs(tables["glyf"], tables["loca"], 220, longLoca: false);
        glyphs.Add([.. U16(0xFFFF), .. U16(100), .. U16(0xFF38), .. U16(1100), .. U16(800),
            .. U16(0x0103), .. U16(34), .. U16(100), .. U16(0), .. U16(2), 0xB0, 0x01]);
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
        tables["glyf"] = [.. glyf];
        tables["loca"] = [.. loca];
        BinaryPrimitives.WriteUInt16BigEndian(tables["maxp"].AsSpan(4), 221);
        BinaryPrimitives.WriteUInt16BigEndian(tables["hhea"].AsSpan(34), 221);
        var hmtx = tables["hmtx"];
        BinaryPrimitives.WriteInt16BigEndian(hmtx.AsSpan(2), 50);
        tables["hmtx"] = [.. hmtx, .. U16(1000), .. U16(100)];
        return WriteSfnt(tables);
    }

    private static byte[] Patch(byte[] data, int offset, uint value)
    {
        var copy = (byte[])data.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(offset), value);
        return copy;
    }

    private static (uint Compressed, uint Original) EntryAt(byte[] woff, int entry) =>
        (BinaryPrimitives.ReadUInt32BigEndian(woff.AsSpan(entry + 8)), BinaryPrimitives.ReadUInt32BigEndian(woff.AsSpan(entry + 12)));

    private static int FirstEntry(byte[] woff, Func<(uint Compressed, uint Original), bool> predicate)
    {
        var count = BinaryPrimitives.ReadUInt16BigEndian(woff.AsSpan(12));
        return Enumerable.Range(0, count).Select(i => 44 + 20 * i).First(e => predicate(EntryAt(woff, e)));
    }
}
