using System.Numerics;
using Folio.Interaction;
using Folio.Tests.Layout;

namespace Folio.Tests.Interaction;

public class HitTesterTests
{
    private const string Page = "<!DOCTYPE html><style>body { margin: 0; height: 600px } div { width: 100px; height: 100px }</style>";

    private static HitResult? Hit(string html, float x, float y) => HitTester.Hit(BlockLayoutTests.LayOut(Page + html), new(x, y));

    private static string? At(string html, float x, float y) => Hit(html, x, y)?.Node is { } e ? e.GetAttribute("id") ?? e.LocalName : null;

    [Fact]
    public void OverlappingBoxesHitInPaintOrder()
    {
        const string html = """
            <div id=flow style="width: 300px; height: 300px"></div>
            <div id=low style="position: absolute; top: 0; left: 25px; z-index: -1; width: 400px; height: 700px"></div>
            <div id=high style="position: absolute; top: 0; z-index: 2"></div>
            <div id=auto style="position: absolute; top: 0; left: 50px"></div>
            <div id=later style="position: absolute; top: 0; left: 80px; z-index: 2; width: 20px"></div>
            """;
        Assert.Equal("high", At(html, 60, 50));  // z-index 2 over z-index auto, though earlier in the tree
        Assert.Equal("later", At(html, 90, 50)); // equal z-index: the later box
        Assert.Equal("auto", At(html, 140, 50));
        Assert.Equal("flow", At(html, 200, 50)); // blocks paint over negative z-index
        Assert.Equal("body", At(html, 350, 50)); // the body is a block too
        Assert.Equal("low", At(html, 350, 650)); // below the body
        Assert.Null(At(html, 350, 750));
    }

    [Fact]
    public void TransformedBoxesHitThroughTheirInverse()
    {
        const string turned = "<div id=turned style='position: absolute; left: 100px; top: 100px; transform: rotate(45deg)'></div>";
        Assert.Equal("turned", At(turned, 150, 85));  // outside the box as laid out, inside it turned
        Assert.Equal("body", At(turned, 105, 105));   // inside the box as laid out, outside it turned

        const string scaled = "<div id=scaled style='position: absolute; left: 100px; top: 100px; transform: scale(2)'></div>";
        var hit = Hit(scaled, 60, 60);
        Assert.Equal("scaled", hit?.Node.GetAttribute("id"));
        Assert.Equal(new Vector2(5, 5), hit!.LocalPoint);
        Assert.Equal("body", At(scaled, 45, 45));
    }

    [Fact]
    public void OverflowClipsWhatItHides()
    {
        const string html = "<div id=clip style='overflow: hidden'><div id=child style='width: 300px; height: 300px'></div></div>";
        Assert.Equal("child", At(html, 50, 50));
        Assert.Equal("body", At(html, 150, 50));
        Assert.Equal("body", At(html, 50, 150));
    }

    [Fact]
    public void RoundedCornersMissOutsideTheirCurve()
    {
        const string html = "<div id=round style='position: absolute; top: 0; left: 0; border-radius: 50px'></div>";
        Assert.Equal("body", At(html, 10, 10));
        Assert.Equal("round", At(html, 50, 50));
        Assert.Equal("round", At(html, 95, 50));
        Assert.Equal("body", At(html, 95, 95));
    }

    [Fact]
    public void ClipPathsHitOnlyTheirRegion()
    {
        const string html = "<div id=cut style='position: absolute; top: 0; left: 0; clip-path: polygon(0 0, 100% 0, 0 100%)'></div>";
        Assert.Equal("cut", At(html, 20, 20));
        Assert.Equal("body", At(html, 80, 80));
    }

    [Fact]
    public void PointerEventsNonePassesThrough()
    {
        const string html = """
            <div id=under style="position: absolute; top: 0; left: 0"></div>
            <div id=over style="position: absolute; top: 0; left: 0; pointer-events: none">
              <div id=inner style="pointer-events: auto; width: 10px; height: 10px"></div>
            </div>
            """;
        Assert.Equal("under", At(html, 50, 50));
        Assert.Equal("inner", At(html, 5, 5));
    }

    [Fact]
    public void HiddenBoxesAreSkipped()
    {
        const string html = """
            <div id=under style="position: absolute; top: 0; left: 0"></div>
            <div id=over style="position: absolute; top: 0; left: 0; visibility: hidden">
              <div id=shown style="visibility: visible; width: 10px; height: 10px"></div>
            </div>
            """;
        Assert.Equal("under", At(html, 50, 50));
        Assert.Equal("shown", At(html, 5, 5));
    }

    [Fact]
    public void TextResolvesToTheInlineBoxItIsIn()
    {
        const string html = "<p style='margin: 0'>ab<span id=outer>cd<b>ef</b></span></p>";
        Assert.Equal("p", At(html, 5, 5));
        Assert.Equal("outer", At(html, 40, 5));
        Assert.Equal("b", At(html, 70, 5));
    }
}
