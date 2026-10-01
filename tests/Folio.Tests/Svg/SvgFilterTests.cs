using Folio.Painting;
using Folio.Tests.Layout;

namespace Folio.Tests.Svg;

public class SvgFilterTests
{
    [Fact]
    public void BoxesOfOneSizeShareTheirFiltersPrimitives()
    {
        // 500 boxes use one filter of 200 primitives: built once, not once per box.
        var primitives = string.Concat(Enumerable.Repeat("<feOffset dx=\"1\" />", 200));
        var html = "<!DOCTYPE html><style>div { width: 50px; height: 10px; background: red; filter: url(#f) }</style>" +
            $"<svg width=\"0\" height=\"0\" style=\"position: absolute\"><filter id=\"f\">{primitives}</filter></svg>" +
            string.Concat(Enumerable.Repeat("<div></div>", 500)) + "<div style=\"width: 60px\"></div>";

        var list = DisplayListBuilder.Build(BlockLayoutTests.LayOut(html));

        var filters = list.Items.Where(i => i.Kind == DisplayItemKind.PushLayer && i.Filters is { Count: 200 }).Select(i => i.Filters!).ToList();
        Assert.Equal(501, filters.Count);
        // One list for the 500 boxes of one size, another for the wider one.
        Assert.Equal(2, filters.Distinct(ReferenceEqualityComparer.Instance).Count());
    }

    [Fact]
    public void SvgElementsShareAFilterInUserSpace()
    {
        var html = "<!DOCTYPE html><svg width=\"200\" height=\"100\"><filter id=\"f\" filterUnits=\"userSpaceOnUse\"><feGaussianBlur stdDeviation=\"2\" /></filter>" +
            string.Concat(Enumerable.Range(0, 20).Select(i => $"<rect x=\"{i * 5}\" width=\"4\" height=\"4\" filter=\"url(#f)\" />")) + "</svg>";

        var list = DisplayListBuilder.Build(BlockLayoutTests.LayOut(html));

        var filters = list.Items.Where(i => i.Kind == DisplayItemKind.PushLayer && i.Filters is not null).Select(i => i.Filters!).ToList();
        Assert.Equal(20, filters.Count);
        Assert.Single(filters.Distinct(ReferenceEqualityComparer.Instance));
    }
}
