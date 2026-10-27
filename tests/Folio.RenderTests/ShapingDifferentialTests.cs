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
}
