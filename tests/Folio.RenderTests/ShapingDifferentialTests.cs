using Folio.Typography;
using Folio.Skia;

namespace Folio.RenderTests;

/// <summary>
/// Small checked-in corpus for the mandatory Folio-owned shaping replacement.
/// Complex scripts remain on the HarfBuzz oracle until their per-script gate is
/// proven; simple runs must be byte-for-byte equivalent in glyphs and clusters.
/// </summary>
public sealed class ShapingDifferentialTests
{
    private static readonly Lazy<FontCollection> Fonts = new(() =>
        FontCollection.FromFolder(Path.Combine(RepoPaths.Tests, "fonts")));

    public static TheoryData<string, string, string> Corpus => new()
    {
        { "Source Sans 3", "The quick brown fox, AVATAR", "Latin" },
        { "Folio Box", "1234", "Latin digits" },
        { "Source Sans 3", "Γειά σου Κόσμε", "Greek" },
        { "Source Sans 3", "Привет, мир", "Cyrillic" },
        { "Source Sans 3", "Ångström — naïve", "Latin extended" },
        { "Noto Sans Hebrew", "שלום", "Hebrew base" },
    };

    [Theory]
    [MemberData(nameof(Corpus))]
    public void FolioShaperMatchesTheOracleForProvenRuns(string family, string text, string script)
    {
        var face = Fonts.Value.Match(family, FaceStyle.Normal, 400, 100)!;
        Assert.True(SimpleShaper.CanShape(text, face), $"The corpus entry {script} must be on the Folio path.");

        var folio = SimpleShaper.Shape(text, 0, text.Length, face, 16);
        var oracle = new HarfBuzzShaper().Shape(text, 0, text.Length, face, 16,
            rightToLeft: script.Contains("Hebrew", StringComparison.Ordinal),
            language: script.Contains("Hebrew", StringComparison.Ordinal) ? "he" : null);

        Assert.Equal(oracle.Glyphs, folio.Glyphs);
        Assert.Equal(oracle.Clusters, folio.Clusters);
        Assert.Equal(oracle.Advances, folio.Advances);
    }

    [Fact]
    public void DifferentialReportCatchesADeliberatelyBrokenKerningValue()
    {
        var face = Fonts.Value.Match("Folio Box", FaceStyle.Normal, 400, 100)!;
        var folio = SimpleShaper.Shape("AVATAR", 0, 6, face, 16);
        var oracle = new HarfBuzzShaper().Shape("AVATAR", 0, 6, face, 16, false, null);
        Assert.Equal(oracle.Glyphs, folio.Glyphs);
        Assert.Equal(oracle.Clusters, folio.Clusters);

        var broken = (float[])folio.Advances.Clone();
        broken[0] += 1;
        Assert.NotEqual(oracle.Advances, broken);
    }

    [Theory]
    [InlineData("Noto Sans Arabic", "السَّلَامُ عَلَيْكُمْ", "Arabic")]
    [InlineData("Noto Sans JP", "こんにちは世界", "CJK")]
    public void ComplexScriptsRemainOnTheOracleUntilTheirGateIsProven(string family, string text, string script)
    {
        var face = Fonts.Value.Match(family, FaceStyle.Normal, 400, 100)!;
        Assert.False(SimpleShaper.CanShape(text, face), $"{script} unexpectedly bypassed the replacement gate.");
        var actual = new HybridShaper().Shape(text, 0, text.Length, face, 16, rightToLeft: script == "Arabic", language: script == "Arabic" ? "ar" : null);
        var oracle = new HarfBuzzShaper().Shape(text, 0, text.Length, face, 16, rightToLeft: script == "Arabic", language: script == "Arabic" ? "ar" : null);
        Assert.Equal(oracle.Glyphs, actual.Glyphs);
        Assert.Equal(oracle.Clusters, actual.Clusters);
        Assert.Equal(oracle.Advances, actual.Advances);
        Assert.Equal(oracle.Offsets, actual.Offsets);
    }

    [Fact]
    public void PerScriptSwitchRoutesAComplexRunToFolioWithoutChangingTheHarness()
    {
        var face = Fonts.Value.Match("Noto Sans Arabic", FaceStyle.Normal, 400, 100)!;
        const string text = "السَّلَامُ عَلَيْكُمْ";
        var switched = new HybridShaper(new HashSet<string>(StringComparer.Ordinal) { "Arabic" });
        var actual = switched.Shape(text, 0, text.Length, face, 16, rightToLeft: true, language: "ar");
        var expected = ComplexShaper.Instance.Shape(text, 0, text.Length, face, 16, rightToLeft: true, language: "ar");

        Assert.Equal(expected.Glyphs, actual.Glyphs);
        Assert.Equal(expected.Clusters, actual.Clusters);
        Assert.Equal(expected.Advances, actual.Advances);
    }

    [Fact]
    public void PerScriptSwitchDoesNotForceMixedRunsThroughOneScriptPath()
    {
        var face = Fonts.Value.Match("Noto Sans Arabic", FaceStyle.Normal, 400, 100)!;
        const string text = "hello سلام";
        var switched = new HybridShaper(new HashSet<string>(StringComparer.Ordinal) { "Arabic" });
        var actual = switched.Shape(text, 0, text.Length, face, 16, rightToLeft: true, language: "ar");
        var oracle = new HarfBuzzShaper().Shape(text, 0, text.Length, face, 16, rightToLeft: true, language: "ar");

        Assert.Equal(oracle.Glyphs, actual.Glyphs);
        Assert.Equal(oracle.Clusters, actual.Clusters);
    }

    [Fact]
    public void PerScriptSwitchCanPromoteHebrewToTheFolioComplexShaper()
    {
        var face = Fonts.Value.Match("Noto Sans Hebrew", FaceStyle.Normal, 400, 100)!;
        const string text = "שָׁ";
        var switched = new HybridShaper(new HashSet<string>(StringComparer.Ordinal) { "Hebrew" });
        var actual = switched.Shape(text, 0, text.Length, face, 16, rightToLeft: true, language: "he");
        var expected = ComplexShaper.Instance.Shape(text, 0, text.Length, face, 16, rightToLeft: true, language: "he");

        Assert.Equal(expected.Glyphs, actual.Glyphs);
        Assert.Equal(expected.Clusters, actual.Clusters);
        Assert.Equal(expected.Advances, actual.Advances);
        Assert.Equal(expected.Offsets, actual.Offsets);
    }

    [Theory]
    [InlineData("سلام")]
    [InlineData("لا")]
    [InlineData("لم")]
    [InlineData("مُحَمَّد")]
    public void ArabicComplexShaperMatchesTheOracleForTheCurrentCorpus(string text)
    {
        var face = Fonts.Value.Match("Noto Sans Arabic", FaceStyle.Normal, 400, 100)!;
        var actual = ComplexShaper.Instance.Shape(text, 0, text.Length, face, 16, rightToLeft: true, language: "ar");
        var oracle = new HarfBuzzShaper().Shape(text, 0, text.Length, face, 16, rightToLeft: true, language: "ar");
        Assert.Equal(oracle.Glyphs, actual.Glyphs);
        Assert.Equal(oracle.Clusters, actual.Clusters);
        Assert.Equal(oracle.Advances, actual.Advances);
        Assert.Equal(oracle.Offsets, actual.Offsets);
    }

    [Theory]
    [InlineData("العربية لغة جميلة")]
    [InlineData("مَرْحَبًا بِكُمْ")]
    [InlineData("اللغة العربية")]
    [InlineData("فلسطين")]
    public void ArabicComplexShaperMatchesGlyphsForExtendedCorpus(string text)
    {
        var face = Fonts.Value.Match("Noto Sans Arabic", FaceStyle.Normal, 400, 100)!;
        var actual = ComplexShaper.Instance.Shape(text, 0, text.Length, face, 16, rightToLeft: true, language: "ar");
        var oracle = new HarfBuzzShaper().Shape(text, 0, text.Length, face, 16, rightToLeft: true, language: "ar");
        Assert.Equal(oracle.Glyphs, actual.Glyphs);
        Assert.Equal(oracle.Clusters, actual.Clusters);
        Assert.Equal(oracle.Advances, actual.Advances);
        Assert.Equal(oracle.Offsets.Length, actual.Offsets.Length);
        for (var i = 0; i < oracle.Offsets.Length; i++)
        {
            Assert.InRange(MathF.Abs(oracle.Offsets[i].X - actual.Offsets[i].X), 0, 0.001f);
            Assert.InRange(MathF.Abs(oracle.Offsets[i].Y - actual.Offsets[i].Y), 0, 0.001f);
        }
    }

    [Theory]
    [InlineData("שָׁלוֹם")]
    [InlineData("בְּרֵאשִׁית")]
    [InlineData("מַלְכָּה")]
    public void HebrewComplexShaperMatchesGlyphsAndPlacementForExtendedCorpus(string text)
    {
        var face = Fonts.Value.Match("Noto Sans Hebrew", FaceStyle.Normal, 400, 100)!;
        var actual = ComplexShaper.Instance.Shape(text, 0, text.Length, face, 16, rightToLeft: true, language: "he");
        var oracle = new HarfBuzzShaper().Shape(text, 0, text.Length, face, 16, rightToLeft: true, language: "he");
        Assert.Equal(oracle.Glyphs, actual.Glyphs);
        Assert.Equal(oracle.Clusters, actual.Clusters);
        Assert.Equal(oracle.Advances, actual.Advances);
        Assert.Equal(oracle.Offsets.Length, actual.Offsets.Length);
        for (var i = 0; i < oracle.Offsets.Length; i++)
        {
            Assert.InRange(MathF.Abs(oracle.Offsets[i].X - actual.Offsets[i].X), 0, 0.001f);
            Assert.InRange(MathF.Abs(oracle.Offsets[i].Y - actual.Offsets[i].Y), 0, 0.001f);
        }
    }
}
