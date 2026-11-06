using Folio.Skia;
using Folio.Typography;

namespace Folio.RenderTests;

public class NativeCorpusShapingTests
{
    [Theory]
    [InlineData("bilingual-train-timetable")]
    [InlineData("chinese-tech-news-article")]
    [InlineData("japanese-recipe-card")]
    [InlineData("cjk-and-emoji")]
    public void NativeShapingMatchesTheOracleForMixedLanguagePageRuns(string name)
    {
        var shaper = new ComparingShaper();
        var html = File.ReadAllText(Path.Combine(RepoPaths.Tests, "conformance", "S6", name, "index.html"));
        using var document = Document.Parse(html, Conformance.Options);
        document.Paint(800, 2100, 1, shaper);
        Assert.True(shaper.Differences.Count == 0, string.Join("\n", shaper.Differences.Take(12)));
    }

    private sealed class ComparingShaper : ITextShaper
    {
        private readonly HarfBuzzShaper _oracle = new();
        internal List<string> Differences { get; } = [];

        public ShapedText Shape(string text, int start, int length, IFontHandle font, float size, bool rightToLeft, string? language)
        {
            var actual = ComplexShaper.Instance.Shape(text, start, length, font, size, rightToLeft, language);
            var expected = _oracle.Shape(text, start, length, font, size, rightToLeft, language);
            if (!actual.Glyphs.SequenceEqual(expected.Glyphs) || !actual.Clusters.SequenceEqual(expected.Clusters)
                || !actual.Advances.SequenceEqual(expected.Advances) || !actual.Offsets.SequenceEqual(expected.Offsets))
                Differences.Add(System.Text.Json.JsonSerializer.Serialize(new
                {
                    Text = text.Substring(start, length), language, size,
                    Glyphs = actual.Glyphs, ExpectedGlyphs = expected.Glyphs,
                    Advances = actual.Advances, ExpectedAdvances = expected.Advances,
                    Clusters = actual.Clusters, ExpectedClusters = expected.Clusters,
                    Offsets = actual.Offsets, ExpectedOffsets = expected.Offsets,
                }));
            return actual;
        }
    }
}
