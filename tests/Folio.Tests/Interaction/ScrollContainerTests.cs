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

    [Fact]
    public void KeyboardScrollingArrowsPageHomeEndAndSpace()
    {
        const string html = """
            <!DOCTYPE html>
            <style>
              body { margin: 0; width: 800px; height: 600px; }
              #outer { width: 400px; height: 400px; overflow: scroll; }
              #inner { width: 200px; height: 200px; overflow: scroll; }
              .spacer { width: 1000px; height: 1000px; }
            </style>
            <div id=outer>
              <div id=inner tabindex=0>
                <div class=spacer></div>
              </div>
              <div class=spacer></div>
            </div>
            """;

        using var doc = Document.Parse(html);
        doc.Paint(800, 600);

        var inner = doc.GetElementById("inner")!;
        var outer = doc.GetElementById("outer")!;

        // 1. Focus on inner element
        inner.Focus();
        Assert.Equal(inner, doc.Input.FocusedElement);

        // ArrowDown scrolls by 40px
        doc.Input.HandleKeyDown(new KeyEvent("ArrowDown", "ArrowDown"));
        Assert.Equal(40, inner.ScrollTop);
        Assert.Equal(0, outer.ScrollTop);

        // ArrowRight scrolls by 40px
        doc.Input.HandleKeyDown(new KeyEvent("ArrowRight", "ArrowRight"));
        Assert.Equal(40, inner.ScrollLeft);

        // ArrowUp scrolls back up by 40px
        doc.Input.HandleKeyDown(new KeyEvent("ArrowUp", "ArrowUp"));
        Assert.Equal(0, inner.ScrollTop);

        // ArrowLeft scrolls back by 40px
        doc.Input.HandleKeyDown(new KeyEvent("ArrowLeft", "ArrowLeft"));
        Assert.Equal(0, inner.ScrollLeft);

        // PageDown scrolls by pageStep = inner.ClientHeight - 40 = 200 - 40 = 160px
        doc.Input.HandleKeyDown(new KeyEvent("PageDown", "PageDown"));
        Assert.Equal(160, inner.ScrollTop);

        // Space scrolls down by pageStep = 160px
        doc.Input.HandleKeyDown(new KeyEvent(" ", "Space"));
        Assert.Equal(320, inner.ScrollTop);

        // Shift+Space scrolls back up by pageStep = 160px
        doc.Input.HandleKeyDown(new KeyEvent(" ", "Space", Modifiers: KeyModifiers.Shift));
        Assert.Equal(160, inner.ScrollTop);

        // PageUp scrolls back up by pageStep = 160px
        doc.Input.HandleKeyDown(new KeyEvent("PageUp", "PageUp"));
        Assert.Equal(0, inner.ScrollTop);

        // End scrolls to bottom (Control+End or End)
        doc.Input.HandleKeyDown(new KeyEvent("End", "End", Modifiers: KeyModifiers.Control));
        // max scroll is 1000 - 200 = 800
        Assert.Equal(800, inner.ScrollTop);

        // Home scrolls to top
        doc.Input.HandleKeyDown(new KeyEvent("Home", "Home", Modifiers: KeyModifiers.Control));
        Assert.Equal(0, inner.ScrollTop);
    }

    [Fact]
    public void ScrollbarThumbDraggingMovesOffsetProportionally()
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

        // Vertical scrollbar is at the right edge: x between 188 and 200 (thickness 12px)
        // Scroller is at (0, 0), height = 200.
        // Initially at top (scrollTop = 0), thumb is at the top of the track: y in [0, thumbHeight].
        // Thumb height = max(20, 200 * (200 / 1000)) = 40px.
        // Point (194, 20) is inside the thumb!
        doc.Input.HandlePointerDown(new PointerEvent(new Vector2(194, 20), Button: PointerButton.Primary));

        // Drag down by 50px: pointer moves from y=20 to y=70
        doc.Input.HandlePointerMove(new PointerEvent(new Vector2(194, 70)));

        // Available track = 200 - 40 = 160px.
        // Delta pointer = 50px.
        // Max scroll = 1000 - 200 = 800px.
        // Scroll offset = 50 / 160 * 800 = 250px.
        Assert.Equal(250, scroller.ScrollTop);

        // Release pointer
        doc.Input.HandlePointerUp(new PointerEvent(new Vector2(194, 70), Button: PointerButton.Primary));

        // Moving further after release does not change scroll offset
        doc.Input.HandlePointerMove(new PointerEvent(new Vector2(194, 100)));
        Assert.Equal(250, scroller.ScrollTop);
    }
}


