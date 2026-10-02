using Folio.Css;
using Folio.Html;
using Folio.Layout;
using Folio.Skia;
using Folio.Style;
using Folio.Typography;

namespace Folio.RenderTests;

public class HarfBuzzShaperTests
{
    private static readonly Lazy<FontCollection> Fonts = new(() => FontCollection.FromFolder(Path.Combine(RepoPaths.Tests, "fonts")));

    private static FontFace BoxFace => Fonts.Value.Match("Folio Box", FaceStyle.Normal, 400, 100)!;

    // The study's differential check: same glyphs, clusters and advances (kerning included). The box font kerns with a
    // kern table, the text font with GPOS pair adjustment (text without ligatures).
    [Theory]
    [InlineData("Folio Box", "The quick brown fox, 1234! \u00D7\u00F7 x")]
    [InlineData("Source Sans 3", "To Vila Nova, AVATAR Tokyo: \"Walk\" across the upper deck; Lyon, P.T. Yarrow & Wo. 1,240 testers.")]
    public void MatchesSimpleShaperOnSimpleText(string family, string text)
    {
        var face = Fonts.Value.Match(family, FaceStyle.Normal, 400, 100)!;

        var simple = SimpleShaper.Shape(text, 0, text.Length, face, 16);
        var shaped = new HarfBuzzShaper().Shape(text, 0, text.Length, face, 16, rightToLeft: false, language: null);

        Assert.Equal(simple.Glyphs, shaped.Glyphs);
        Assert.Equal(simple.Clusters, shaped.Clusters);
        // Where each glyph is drawn (pen position plus offset) and the total width agree; how a kerning adjustment is
        // split between a pair's advances and offsets may differ.
        Assert.Equal(Positions(simple.Advances, null), Positions(shaped.Advances, shaped.Offsets));
        Assert.Equal(simple.Width, shaped.Advances.Sum(), 3);
        Assert.Contains(simple.Advances.Select((a, i) => a - face.Advance(simple.Glyphs[i]) * 16f / face.UnitsPerEm), k => k < 0); // kerned

        static float[] Positions(float[] advances, System.Numerics.Vector2[]? offsets)
        {
            var (x, result) = (0f, new float[advances.Length]);
            for (var i = 0; i < advances.Length; i++)
            {
                result[i] = MathF.Round(x + (offsets?[i].X ?? 0), 3);
                x += advances[i];
            }
            return result;
        }
    }

    // font-variant-numeric asks for figure features: the text font's default figures are tabular, and pnum swaps in
    // proportional ones by GSUB single substitution, which tnum swaps back.
    [Fact]
    public void FigureFeaturesComeFromGsubSingleSubstitution()
    {
        var face = Fonts.Value.Match("Source Sans 3", FaceStyle.Normal, 400, 100)!;

        var tabular = SimpleShaper.Shape("1180", 0, 4, face, 16);
        var proportional = SimpleShaper.Shape("1180", 0, 4, face, 16, "pnum");

        Assert.All(tabular.Advances, a => Assert.Equal(tabular.Advances[3], a, 3));
        Assert.True(proportional.Advances[0] < proportional.Advances[3], "a proportional 1 is narrower than a 0");
        Assert.NotEqual(tabular.Glyphs[0], proportional.Glyphs[0]);
        Assert.Equal(face.GlyphFor('x'), SimpleShaper.Shape("x", 0, 1, face, 16, "pnum").Glyphs[0]); // not covered: unchanged
    }

    [Fact]
    public void SystemFontSourceOpensTheFamilyItMatches()
    {
        var source = new SystemFontSource();
        if (source.MatchCharacter('A', 400, italic: false) is not { } family)
        {
            Assert.Skip("This machine has no system fonts.");
            return;
        }

        var faces = source.OpenFamily(family);

        Assert.NotEmpty(faces);
        Assert.Contains(faces, f => FontFace.Parse(f.Data, f.FaceIndex)?.Covers('A') == true);
        Assert.Empty(source.OpenFamily("No Such Family Anywhere"));
    }

    [Fact]
    public void ReturnsRightToLeftRunsInLogicalOrder()
    {
        var shaped = new HarfBuzzShaper().Shape("xABy", 1, 2, BoxFace, 16, rightToLeft: true, language: null);

        Assert.Equal([1, 2], shaped.Clusters);
        Assert.Equal([BoxFace.GlyphFor('A'), BoxFace.GlyphFor('B')], shaped.Glyphs);
    }

    [Fact]
    public void LayoutSendsOnlyComplexRunsToTheShaper()
    {
        var shaper = new HarfBuzzShaper();
        Assert.All(Runs("<p>plain text</p>", shaper), r => Assert.Null(r.Offsets));
        Assert.All(Runs("<p>e\u0301</p>", shaper), r => Assert.NotNull(r.Offsets)); // a combining mark
        Assert.All(Runs("<p>e\u0301</p>", null), r => Assert.Null(r.Offsets));
    }

    [Fact]
    public void RunsWithTheFontsDefaultLigaturesGoToTheShaperUnlessLettersAreSpaced()
    {
        // Source Sans 3 has an ft ligature (liga), which only the shaper forms; letter-spacing turns ligatures off.
        var shaper = new HarfBuzzShaper();
        var ligated = Assert.Single(Runs("<p style=\"font-family: 'Source Sans 3'\">aft</p>", shaper));
        Assert.NotNull(ligated.Offsets);
        Assert.Equal(2, ligated.Glyphs.Length);
        var spaced = Assert.Single(Runs("<p style=\"font-family: 'Source Sans 3'; letter-spacing: 1px\">aft</p>", shaper));
        Assert.Null(spaced.Offsets);
        Assert.Equal(3, spaced.Glyphs.Length);
    }

    private static List<ShapedRun> Runs(string html, ITextShaper? shaper)
    {
        var document = TreeBuilder.Parse("<!DOCTYPE html>" + html);
        StyleResolver.Resolve(document, new MediaContext(200, 100));
        var fonts = Fonts.Value;
        fonts.GenericFamilies["serif"] = ["Folio Box"];
        var root = LayoutEngine.LayoutDocument(BoxTreeBuilder.Build(document)!, 200, 100, fonts, shaper);
        var runs = new List<ShapedRun>();
        void Walk(Fragment f)
        {
            if (f.Text is { } t)
                runs.Add(t.Run);
            foreach (var c in f.Children)
                Walk(c.Fragment);
        }
        Walk(root);
        return runs;
    }
}
