namespace Folio.Tests.Layout;

public class GridLayoutTests
{
    // Thirty grids, one inside another: each lays its item out to measure it and again to place it, which without reuse
    // is 2^30 layouts of the innermost one.
    [Fact(Timeout = 10_000)]
    public async Task DeeplyNestedGridsLayOutOnceOrTwicePerLevel()
    {
        var html = "<!DOCTYPE html>" + string.Concat(Enumerable.Repeat("<div style=\"display: grid; padding: 1px\">", 30)) +
            "<p style=\"margin: 0; height: 10px\"></p>" + string.Concat(Enumerable.Repeat("</div>", 30));

        var root = await Task.Run(() => BlockLayoutTests.LayOut(html), TestContext.Current.CancellationToken);

        // The outermost grid holds 29 more of 1px padding each and the 10px paragraph.
        var grid = root;
        while (grid.Box?.Style.Box.Display != Folio.Style.Display.Grid && grid.Children.Count > 0)
            grid = grid.Children[0].Fragment;
        Assert.Equal(70, grid.Height);
    }

    // The same for row subgrids, each measured to size its parent's rows and laid out again in them.
    [Fact(Timeout = 10_000)]
    public async Task DeeplyNestedRowSubgridsLayOutInLinearTime()
    {
        var html = "<!DOCTYPE html><div style=\"display: grid\">" +
            string.Concat(Enumerable.Repeat("<div style=\"display: grid; grid-row: span 2; grid-template-rows: subgrid\"><p style=\"margin: 0; height: 5px\"></p>", 30)) +
            string.Concat(Enumerable.Repeat("</div>", 31));

        var root = await Task.Run(() => BlockLayoutTests.LayOut(html), TestContext.Current.CancellationToken);

        Assert.True(root.Height > 0);
    }
}
