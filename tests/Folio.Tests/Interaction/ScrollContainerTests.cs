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

    [Fact]
    public void WheelRoutingNestedContainersChainAtEdge()
    {
        const string html = """
            <!DOCTYPE html>
            <style>
              body { margin: 0; width: 800px; height: 600px; overflow: scroll; }
              #outer { width: 300px; height: 300px; overflow: scroll; }
              #inner { width: 200px; height: 200px; overflow: scroll; }
              .spacer { width: 1000px; height: 1000px; }
            </style>
            <div id=outer>
              <div id=inner>
                <div class=spacer></div>
              </div>
              <div class=spacer></div>
            </div>
            """;

        using var doc = Document.Parse(html);
        doc.Paint(800, 600);

        var inner = doc.GetElementById("inner")!;
        var outer = doc.GetElementById("outer")!;

        // Pointer is at (50, 50), directly inside #inner
        // 1. Wheel scroll within #inner bounds: #inner consumes it
        doc.Input.HandleWheel(new WheelEvent(new Vector2(50, 50), new Vector2(0, 100)));
        Assert.Equal(100, inner.ScrollTop);
        Assert.Equal(0, outer.ScrollTop);

        // Max scroll for inner is 1000 - 200 = 800.
        // Scroll by 800 more: inner reaches 800 (max), remaining 100 chains to outer!
        doc.Input.HandleWheel(new WheelEvent(new Vector2(50, 50), new Vector2(0, 800)));
        Assert.Equal(800, inner.ScrollTop);
        Assert.Equal(100, outer.ScrollTop);

        // When inner is already at max, further scroll chains completely to outer
        doc.Input.HandleWheel(new WheelEvent(new Vector2(50, 50), new Vector2(0, 50)));
        Assert.Equal(800, inner.ScrollTop);
        Assert.Equal(150, outer.ScrollTop);
    }

    [Fact]
    public void OverscrollBehaviorContainStopsChaining()
    {
        const string html = """
            <!DOCTYPE html>
            <style>
              body { margin: 0; width: 800px; height: 600px; }
              #outer { width: 300px; height: 300px; overflow: scroll; }
              #inner { width: 200px; height: 200px; overflow: scroll; overscroll-behavior: contain; }
              .spacer { width: 1000px; height: 1000px; }
            </style>
            <div id=outer>
              <div id=inner>
                <div class=spacer></div>
              </div>
              <div class=spacer></div>
            </div>
            """;

        using var doc = Document.Parse(html);
        doc.Paint(800, 600);

        var inner = doc.GetElementById("inner")!;
        var outer = doc.GetElementById("outer")!;

        Assert.Equal("contain", inner.OverscrollBehaviorX);
        Assert.Equal("contain", inner.OverscrollBehaviorY);

        // Scroll inner past its max (max is 800). Delta is 1000.
        // Inner should clamp to 800, and outer should NOT receive the remaining 200!
        doc.Input.HandleWheel(new WheelEvent(new Vector2(50, 50), new Vector2(0, 1000)));
        Assert.Equal(800, inner.ScrollTop);
        Assert.Equal(0, outer.ScrollTop);

        // Scrolling further at boundary still does not chain
        doc.Input.HandleWheel(new WheelEvent(new Vector2(50, 50), new Vector2(0, 100)));
        Assert.Equal(800, inner.ScrollTop);
        Assert.Equal(0, outer.ScrollTop);
    }

    [Fact]
    public void ShiftWheelScrollsHorizontally()
    {
        const string html = """
            <!DOCTYPE html>
            <style>
              body { margin: 0; width: 800px; height: 600px; }
              #scroller { width: 200px; height: 200px; overflow: scroll; }
              .spacer { width: 1000px; height: 1000px; }
            </style>
            <div id=scroller>
              <div class=spacer></div>
            </div>
            """;

        using var doc = Document.Parse(html);
        doc.Paint(800, 600);

        var scroller = doc.GetElementById("scroller")!;

        // Vertical delta with Shift modifier should scroll horizontally
        doc.Input.HandleWheel(new WheelEvent(new Vector2(50, 50), new Vector2(0, 120), Modifiers: KeyModifiers.Shift));
        Assert.Equal(120, scroller.ScrollLeft);
        Assert.Equal(0, scroller.ScrollTop);
    }

    [Fact]
    public void DeltaModesConvertLineAndPage()
    {
        const string html = """
            <!DOCTYPE html>
            <style>
              body { margin: 0; width: 800px; height: 600px; }
              #scroller { width: 200px; height: 200px; overflow: scroll; }
              .spacer { width: 1000px; height: 1000px; }
            </style>
            <div id=scroller>
              <div class=spacer></div>
            </div>
            """;

        using var doc = Document.Parse(html);
        doc.Paint(800, 600);

        var scroller = doc.GetElementById("scroller")!;

        // 1. Line mode: 3 lines = 3 * 16 = 48px
        doc.Input.HandleWheel(new WheelEvent(new Vector2(50, 50), new Vector2(0, 3), DeltaMode: WheelDeltaMode.Line));
        Assert.Equal(48, scroller.ScrollTop);

        // 2. High-precision fractional pixel delta
        doc.Input.HandleWheel(new WheelEvent(new Vector2(50, 50), new Vector2(0, 12.5f), DeltaMode: WheelDeltaMode.Pixel));
        Assert.Equal(60.5f, scroller.ScrollTop);

        // 3. Page mode: viewport client height is 600
        doc.Input.HandleWheel(new WheelEvent(new Vector2(50, 50), new Vector2(0, 1), DeltaMode: WheelDeltaMode.Page));
        // 60.5 + 600 = 660.5
        Assert.Equal(660.5f, scroller.ScrollTop);
    }
}

