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
    public void DeeplyNestedInlineBoxesLayOutQuickly()
    {
        // Each piece of a line joins only the boxes open around it: 500 nested boxes with 2,000 empty ones inside the
        // deepest took seconds when every piece was checked against every box on the line.
        var html = "<!DOCTYPE html><p>" + string.Concat(Enumerable.Repeat("<span>", 500)) + string.Concat(Enumerable.Repeat("<b></b>", 2000)) + "x</p>";
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var dump = BlockLayoutTests.Dump(BlockLayoutTests.LayOut(html));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"took {watch.Elapsed}");
        Assert.Contains(dump, line => line.TrimStart().StartsWith("text \"x\"", StringComparison.Ordinal));
    }
}
