using System.Globalization;

namespace Folio.Tests.Layout;

public class InlineLayoutTests
{
    // Inline, flex, grid and table cases share the dump format and the test font.
    private static readonly Lazy<Dictionary<string, CaseFiles.Case>> AllCases = new(() =>
        CaseFiles.Load("Layout", "Inline").Concat(CaseFiles.Load("Layout", "Flex")).Concat(CaseFiles.Load("Layout", "Grid")).Concat(CaseFiles.Load("Layout", "Table")).ToDictionary());

    public static TheoryData<string> Ids => new(AllCases.Value.Keys.Order());

    [Theory]
    [MemberData(nameof(Ids))]
    public void LaysOut(string id)
    {
        var test = AllCases.Value[id];
        var viewport = test.Directives.FirstOrDefault(d => d.StartsWith("viewport ", StringComparison.Ordinal))?.Split(' ');
        var (width, height) = viewport is null ? (800f, 600f) : (float.Parse(viewport[1], CultureInfo.InvariantCulture), float.Parse(viewport[2], CultureInfo.InvariantCulture));

        Assert.Equal(test.Expected, BlockLayoutTests.Dump(BlockLayoutTests.LayOut(test.Input, width, height)));
    }

    [Fact]
    public void LineBuildingGrowsQuadraticallyWithNestingDepth()
    {
        // Each piece of a line joins only the boxes open around it. When every piece was checked against every box on
        // the line, walking the open chain each time, the cost grew with the cube of the depth: 64 times for four times
        // the depth, against 16 now. The bound between them leaves room for a slow or busy machine.
        var (shallow, deep) = (LayoutTime(150), LayoutTime(600));

        Assert.True(deep < 32 * Math.Max(shallow, 1), $"depth 150: {shallow:F1} ms, depth 600: {deep:F1} ms");
    }

    // The best of three layouts of nested inline boxes this deep, with four times as many empty ones in the deepest.
    private static double LayoutTime(int depth)
    {
        var html = "<!DOCTYPE html><p>" + string.Concat(Enumerable.Repeat("<span>", depth)) + string.Concat(Enumerable.Repeat("<b></b>", 4 * depth)) + "x</p>";
        var document = Folio.Html.TreeBuilder.Parse(html, new Folio.Html.ParserLimits(MaxDepth: 5000));
        Folio.Style.StyleResolver.Resolve(document, new Folio.Css.MediaContext(800, 600), measure: Folio.Layout.InlineLayout.MeasureWith(BlockLayoutTests.BoxFont.Value));
        var best = double.MaxValue;
        for (var run = 0; run < 3; run++)
        {
            var root = Folio.Layout.BoxTreeBuilder.Build(document)!;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Folio.Layout.LayoutEngine.LayoutDocument(root, 800, 600, BlockLayoutTests.BoxFont.Value);
            best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
        }
        return best;
    }
}
