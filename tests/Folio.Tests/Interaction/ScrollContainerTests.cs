using System.Numerics;
using Folio;
using Folio.Dom;
using Folio.Painting;

namespace Folio.Tests.Interaction;

public class ScrollContainerTests
{
    private const string Page = """
        <!DOCTYPE html>
        <style>
          body { margin: 0; width: 800px; height: 600px }
          .container { width: 200px; height: 200px; box-sizing: border-box; overflow: scroll; border: 10px solid black; }
          .content { width: 500px; height: 600px; background: red; }
        </style>
        """;

    [Fact]
    public void ScrollMetricsComputedCorrectly()
    {
        using var doc = Document.Parse(Page + "<div id=scroller class=container><div class=content></div></div>");
        doc.Paint(800, 600);

        var scroller = doc.GetElementById("scroller")!;
        Assert.NotNull(scroller);

        // 200px width with 10px borders on left and right -> 180px clientWidth
        Assert.Equal(180, scroller.ClientWidth);
        Assert.Equal(180, scroller.ClientHeight);

        // 500px content width and 600px content height
        Assert.Equal(500, scroller.ScrollWidth);
        Assert.Equal(600, scroller.ScrollHeight);

        Assert.Equal(0, scroller.ScrollLeft);
        Assert.Equal(0, scroller.ScrollTop);
    }

    [Fact]
    public void ScrollToUpdatesOffsetsAndClamps()
    {
        using var doc = Document.Parse(Page + "<div id=scroller class=container><div class=content></div></div>");
        doc.Paint(800, 600);

        var scroller = doc.GetElementById("scroller")!;

        // Max scroll: 500 - 180 = 320 for X, 600 - 180 = 420 for Y
        scroller.ScrollTo(50, 100);
        Assert.Equal(50, scroller.ScrollLeft);
        Assert.Equal(100, scroller.ScrollTop);

        // Over-clamping
        scroller.ScrollTo(1000, 1000);
        Assert.Equal(320, scroller.ScrollLeft);
        Assert.Equal(420, scroller.ScrollTop);

        // Under-clamping (negative values)
        scroller.ScrollTo(-50, -50);
        Assert.Equal(0, scroller.ScrollLeft);
        Assert.Equal(0, scroller.ScrollTop);
    }

    [Fact]
    public void HitTestingScrolledContentTranslatesByScrollOffset()
    {
        const string html = """
            <!DOCTYPE html>
            <style>
              body { margin: 0; width: 800px; height: 600px }
              #scroller { width: 200px; height: 200px; overflow: hidden; }
              #box1 { width: 200px; height: 200px; background: red; }
              #box2 { width: 200px; height: 200px; background: blue; }
            </style>
            <div id=scroller>
              <div id=box1></div>
              <div id=box2></div>
            </div>
            """;

        using var doc = Document.Parse(html);
        doc.Paint(800, 600);

        var scroller = doc.GetElementById("scroller")!;

        // Initially at (0, 0), point (100, 100) hits box1
        var hit1 = doc.HitAt(100, 100);
        Assert.Equal("box1", hit1?.Node.GetAttribute("id"));

        // Scroll down by 200px so box2 moves into the visible port
        scroller.ScrollTop = 200;

        // Point (100, 100) should now hit box2!
        var hit2 = doc.HitAt(100, 100);
        Assert.Equal("box2", hit2?.Node.GetAttribute("id"));
        Assert.Equal(new Vector2(100, 100), hit2!.LocalPoint);
    }

    [Fact]
    public void StickyElementWithinScrollContainerStaysPinnedUnderScroll()
    {
        const string html = """
            <!DOCTYPE html>
            <style>
              body { margin: 0; width: 800px; height: 600px }
              #scroller { width: 200px; height: 200px; overflow: scroll; }
              #sticky { position: sticky; top: 10px; width: 50px; height: 50px; }
              #spacer { height: 1000px; }
            </style>
            <div id=scroller>
              <div id=sticky></div>
              <div id=spacer></div>
            </div>
            """;

        using var doc = Document.Parse(html);
        doc.Paint(800, 600);

        var scroller = doc.GetElementById("scroller")!;

        // Initially at top: sticky is at y = 0
        var hitInitial = doc.HitAt(25, 25);
        Assert.Equal("sticky", hitInitial?.Node.GetAttribute("id"));

        // Scroll down by 100px. The sticky element's top inset is 10px, so it pins to y = 10 in the scroller
        scroller.ScrollTop = 100;

        // At canvas y = 25 (which is 25px into scroller port), sticky element should still be hit!
        var hitPinned = doc.HitAt(25, 25);
        Assert.Equal("sticky", hitPinned?.Node.GetAttribute("id"));
    }

    [Fact]
    public void DocumentViewportScrolling()
    {
        const string html = """
            <!DOCTYPE html>
            <style>
              body { margin: 0; width: 800px; height: 1200px }
              #top { height: 600px; }
              #bottom { height: 600px; }
            </style>
            <div id=top></div>
            <div id=bottom></div>
            """;

        using var doc = Document.Parse(html);
        doc.Paint(800, 600);

        Assert.Equal(0, doc.ScrollTop);
        Assert.Equal(600, doc.ClientHeight);
        Assert.Equal(1200, doc.ScrollHeight);

        doc.ScrollTo(0, 300);
        Assert.Equal(300, doc.ScrollTop);
    }
}
