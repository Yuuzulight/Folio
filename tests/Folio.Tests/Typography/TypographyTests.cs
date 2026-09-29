using System.Buffers.Binary;
using Folio.Typography;

namespace Folio.Tests.Typography;

public class TypographyTests
{
    private static readonly string FontsFolder = Path.Combine(AppContext.BaseDirectory, "fonts");
    private static readonly byte[] BoxFontBytes = File.ReadAllBytes(Path.Combine(FontsFolder, "FolioBox.ttf"));
    private static readonly FontFace Box = FontFace.Parse(BoxFontBytes)!;

    [Fact]
    public void ReadsTheBoxFontNamesAndMetrics()
    {
        Assert.Equal("Folio Box", Box.Family);
        Assert.Equal("Regular", Box.Subfamily);
        Assert.Equal(1000, Box.UnitsPerEm);
        Assert.Equal((800, -200, 0), (Box.Ascent, Box.Descent, Box.LineGap));
        Assert.Equal((800, 800), (Box.XHeight, Box.CapHeight));
        Assert.Equal(220, Box.GlyphCount);
        Assert.Equal((400, FaceStyle.Normal, 100f), (Box.Weight, Box.Style, Box.Stretch));
        Assert.True(Box.IsFixedPitch);
        Assert.Equal((-100, 50), (Box.UnderlinePosition, Box.UnderlineThickness));
    }

    [Fact]
    public void MapsCharactersToGlyphs()
    {
        Assert.Equal(1, Box.GlyphFor(' '));
        Assert.Equal(34, Box.GlyphFor('A'));
        Assert.NotEqual(0, Box.GlyphFor('\u20AC'));
        Assert.NotEqual(0, Box.GlyphFor('\uFFFD'));
        Assert.Equal(0, Box.GlyphFor('\u0100'));
        Assert.Equal(0, Box.GlyphFor(0x1F600));
    }

    [Fact]
    public void ReadsAdvancesAndKerning()
    {
        var times = Box.GlyphFor('\u00D7');
        var divide = Box.GlyphFor('\u00F7');

        Assert.Equal(1000, Box.Advance(Box.GlyphFor('W')));
        Assert.Equal(-500, Box.Kerning(times, divide));
        Assert.Equal(0, Box.Kerning(divide, times));
    }

    [Fact]
    public void ReadsAFaceFromACollection()
    {
        // Wrap the font in a one-face collection: a ttcf header, then the font with its table offsets moved.
        var font = (byte[])BoxFontBytes.Clone();
        var tables = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        for (var i = 0; i < tables; i++)
        {
            var at = 12 + 16 * i + 8;
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(at), BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(at)) + 16);
        }
        byte[] collection = [.. "ttcf"u8.ToArray(), 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 16, .. font];

        Assert.Equal("Folio Box", FontFace.Parse(collection, 0)?.Family);
        Assert.Null(FontFace.Parse(collection, 1));
    }

    [Fact]
    public void DamagedFontDataIsRejectedWithoutThrowing()
    {
        var random = new Random(1234);
        for (var i = 0; i < 2000; i++)
        {
            var bytes = (byte[])BoxFontBytes.Clone();
            if (i % 2 == 0)
            {
                bytes = bytes[..random.Next(bytes.Length)];
            }
            else
            {
                for (var k = 0; k < 8; k++)
                    bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
            }
            var face = FontFace.Parse(bytes);
            if (face is not null)
            {
                // A face that parses must also answer lookups without throwing.
                for (var c = 0x20; c < 0x80; c++)
                    _ = face.Advance(face.GlyphFor(c));
            }
        }
        Assert.Null(FontFace.Parse(new byte[] { 0, 1, 0, 0, 0, 9 }));
        Assert.Null(FontFace.Parse(Array.Empty<byte>()));
    }

    [Fact]
    public void SimpleShaperUsesAdvancesAndKerning()
    {
        var run = SimpleShaper.Shape("A\u00D7\u00F7B", 0, 4, Box, 16);

        Assert.Equal([Box.GlyphFor('A'), Box.GlyphFor('\u00D7'), Box.GlyphFor('\u00F7'), Box.GlyphFor('B')], run.Glyphs);
        Assert.Equal([0, 1, 2, 3], run.Clusters);
        Assert.Equal([16f, 8f, 16f, 16f], run.Advances);
        Assert.Equal(56, run.Width);
    }

    [Fact]
    public void SimpleShaperShapesPartOfAString()
    {
        var run = SimpleShaper.Shape("xxHix", 2, 2, Box, 10);

        Assert.Equal([2, 3], run.Clusters);
        Assert.Equal(20, run.Width);
    }

    [Theory]
    [InlineData("Hello, world", true)]
    [InlineData("caf\u00E9 \u20AC5", true)]
    [InlineData("e\u0301", false)]          // combining mark
    [InlineData("\u0627\u0644", false)]     // Arabic
    [InlineData("\u0100", false)]           // Latin, but not in the font
    [InlineData("\U0001F600", false)]       // emoji
    public void SimpleShaperTakesOnlySimpleCoveredText(string text, bool simple)
    {
        Assert.Equal(simple, SimpleShaper.CanShape(text, Box));
    }

    [Theory]
    [InlineData(400, 400)]
    [InlineData(450, 500)]   // 400-500: heavier up to 500 first
    [InlineData(420, 400)]   // 420 \u2192 500 is not available below 500 \u2192 lighter
    [InlineData(300, 100)]   // below 400: lighter first
    [InlineData(600, 700)]   // above 500: heavier first
    [InlineData(950, 700)]   // nothing heavier: lighter
    public void WeightMatching(int desired, int expected)
    {
        FaceTraits[] faces = [new(100, FaceStyle.Normal, 100), new(400, FaceStyle.Normal, 100), new(700, FaceStyle.Normal, 100)];
        if (desired == 450)
            faces = [.. faces, new(500, FaceStyle.Normal, 100)];

        var match = FontMatcher.Match(faces.Select(f => new Holder(f)).ToList(), h => h.Traits, 100, FaceStyle.Normal, desired);

        Assert.Equal(expected, match!.Traits.Weight);
    }

    [Theory]
    [InlineData("Italic", "Oblique")]
    [InlineData("Normal", "Normal")]
    [InlineData("Oblique", "Oblique")]
    public void StyleMatchingPrefersItalicThenOblique(string desired, string expected)
    {
        var faces = new List<Holder> { new(new(400, FaceStyle.Normal, 100)), new(new(400, FaceStyle.Oblique, 100)) };

        var match = FontMatcher.Match(faces, h => h.Traits, 100, Enum.Parse<FaceStyle>(desired), 400);

        Assert.Equal(Enum.Parse<FaceStyle>(expected), match!.Traits.Style);
    }

    [Theory]
    [InlineData(100f, 100f)]
    [InlineData(90f, 75f)]    // at or below normal: narrower first
    [InlineData(112.5f, 125f)] // above normal: wider first
    [InlineData(200f, 125f)]
    public void StretchMatching(float desired, float expected)
    {
        var faces = new List<Holder> { new(new(400, FaceStyle.Normal, 75)), new(new(400, FaceStyle.Normal, 100)), new(new(400, FaceStyle.Normal, 125)) };

        Assert.Equal(expected, FontMatcher.Match(faces, h => h.Traits, desired, FaceStyle.Normal, 400)!.Traits.Stretch);
    }

    [Fact]
    public void CollectionMatchesFamiliesAndFallsBackPerCluster()
    {
        var fonts = FontCollection.FromFolder(FontsFolder);
        fonts.GenericFamilies["monospace"] = ["Folio Box"];

        Assert.Equal(Box.Family, fonts.Match("folio box", FaceStyle.Italic, 700, 100)!.Family);
        Assert.Equal("Folio Box", fonts.Match("monospace", FaceStyle.Normal, 400, 100)?.Family);
        Assert.Null(fonts.Match("No Such Family", FaceStyle.Normal, 400, 100));
        Assert.Equal("Folio Box", fonts.FaceForCluster(["Missing", "Folio Box"], FaceStyle.Normal, 400, 100, "A")?.Family);
        Assert.Null(fonts.FaceForCluster(["Folio Box"], FaceStyle.Normal, 400, 100, "\u0100"));

        // Step 2: the cluster's script picks further families.
        Assert.Null(fonts.FaceForCluster(["Missing"], FaceStyle.Normal, 400, 100, "A"));
        fonts.ScriptFallbacks[Script.Latin] = ["Folio Box"];
        Assert.Equal("Folio Box", fonts.FaceForCluster(["Missing"], FaceStyle.Normal, 400, 100, "A")?.Family);
    }

    [Fact]
    public void CollectionOpensFamiliesFromItsSourceOnFirstUse()
    {
        var fonts = FontCollection.For(new FontSettings
        {
            Source = new FontFolderSource(FontsFolder),
            GenericFamilies = new Dictionary<string, IReadOnlyList<string>> { ["monospace"] = ["Folio Box"] },
        });

        Assert.Equal("Folio Box", fonts.Match("folio box", FaceStyle.Normal, 400, 100)?.Family);
        Assert.Equal("Folio Box", fonts.Match("monospace", FaceStyle.Normal, 400, 100)?.Family);
        Assert.Null(fonts.Match("Missing", FaceStyle.Normal, 400, 100));
        // Step 3: the source names a family for a character no requested family covers.
        Assert.Equal("Folio Box", fonts.FaceForCluster(["Missing"], FaceStyle.Normal, 400, 100, "A")?.Family);
        Assert.Null(fonts.FaceForCluster(["Missing"], FaceStyle.Normal, 400, 100, "Ā"));
    }

    [Fact]
    public void FolderSourceIsEmptyForAMissingFolder() =>
        Assert.Empty(new FontFolderSource(Path.Combine(FontsFolder, "missing")).OpenFamily("Folio Box"));

    private sealed record Holder(FaceTraits Traits);
}
