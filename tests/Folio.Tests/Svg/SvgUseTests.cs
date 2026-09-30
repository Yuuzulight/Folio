using Folio.Painting;
using Folio.Svg;
using Folio.Tests.Layout;

namespace Folio.Tests.Svg;

public class SvgUseTests
{
    [Fact]
    public void UsesThatDoubleAtEveryLevelStopAtTheInstanceLimit()
    {
        // Twenty levels of two uses each would build a million rectangles.
        var defs = string.Concat(Enumerable.Range(1, 20).Select(i => $"<g id=\"a{i}\"><use href=\"#a{i - 1}\" /><use href=\"#a{i - 1}\" /></g>"));
        var html = $"<!DOCTYPE html><svg><defs><rect id=\"a0\" width=\"1\" height=\"1\" />{defs}</defs><use href=\"#a20\" /></svg>";

        var list = DisplayListBuilder.Build(BlockLayoutTests.LayOut(html));

        var rectangles = list.Items.Count(i => i.Kind == DisplayItemKind.FillPath);
        Assert.InRange(rectangles, 1, SvgContext.MaxInstancedElements);
    }
}
