using System.Numerics;
using Folio.Dom;
using Folio.Interaction;
using Folio.Layout;
using Folio.Painting;
using Folio.Tests.Layout;

namespace Folio.Tests.Interaction;

public class TextPositionTests
{
    private const string Page = "<!DOCTYPE html><style>body { margin: 0; width: 800px; height: 600px } p { margin: 0; font: 20px/1.5 monospace }</style>";

    private static (Fragment Fragment, HitResult? Hit) HitTest(string html, float x, float y)
    {
        var fragment = BlockLayoutTests.LayOut(Page + html);
        var hit = HitTester.Hit(fragment, new Vector2(x, y));
        return (fragment, hit);
    }

    [Fact]
    public void LtrRunMapsPointsToNearestClusterBoundary()
    {
        // "abcde": 5 chars at 20px font (BoxFont: each character has advance 20px)
        const string html = "<p id=p>abcde</p>";
        var (frag, hitAtFirstHalf) = HitTest(html, 8, 10); // inside 'a', left half
        Assert.NotNull(hitAtFirstHalf?.Position);
        Assert.Equal(0, hitAtFirstHalf!.Position!.Offset);

        var (_, hitAtSecondHalf) = HitTest(html, 15, 10); // inside 'a', right half
        Assert.NotNull(hitAtSecondHalf?.Position);
        Assert.Equal(1, hitAtSecondHalf!.Position!.Offset);

        var (_, hitAtMiddle) = HitTest(html, 45, 10); // inside 'c' (x: 40-60, mid 50) -> left half
        Assert.NotNull(hitAtMiddle?.Position);
        Assert.Equal(2, hitAtMiddle!.Position!.Offset);

        var (_, hitAtEnd) = HitTest(html, 95, 10); // inside 'e' (x: 80-100, mid 90) -> right half
        Assert.NotNull(hitAtEnd?.Position);
        Assert.Equal(5, hitAtEnd!.Position!.Offset);
    }

    [Fact]
    public void RtlRunVisualLeftIsLogicalEnd()
    {
        // In an RTL run, logical start is at visual right.
        // Clicking visual right half maps to cluster start; visual left half maps to cluster end.
        // Use Hebrew characters (\u05D0..\u05D4) with monospace font so the run is RightToLeft == true.
        const string html = "<p id=p dir=rtl style='width: 100px; font: 20px monospace'>\u05D0\u05D1\u05D2\u05D3\u05D4</p>";
        var (frag, hitRightEdge) = HitTest(html, 95, 10); // visual right edge (logical start of first char \u05D0)
        Assert.NotNull(hitRightEdge?.Position);
        Assert.Equal(0, hitRightEdge!.Position!.Offset);

        var (_, hitLeftEdge) = HitTest(html, 5, 10); // visual leftmost edge (logical end of last char \u05D4)
        Assert.NotNull(hitLeftEdge?.Position);
        Assert.Equal(5, hitLeftEdge!.Position!.Offset);
    }

    [Fact]
    public void MixedDirectionLineResolvesCorrectOffsets()
    {
        const string html = "<p id=p>hello <span dir=rtl>world</span> end</p>";
        var (frag, hitLtr) = HitTest(html, 10, 10);
        Assert.NotNull(hitLtr?.Position);
        Assert.IsAssignableFrom<Text>(hitLtr!.Position!.Node);

        var (_, hitRtl) = HitTest(html, 150, 10);
        Assert.NotNull(hitRtl?.Position);
        Assert.IsAssignableFrom<Text>(hitRtl!.Position!.Node);
    }

    [Fact]
    public void CombiningMarksAndLigaturesDoNotSplitCluster()
    {
        // "e\u0301" is 'e' with combining acute accent (grapheme cluster length 2).
        const string html = "<p id=p>e\u0301f</p>";
        var (frag, hitStart) = HitTest(html, 5, 10);
        Assert.NotNull(hitStart?.Position);
        Assert.Equal(0, hitStart!.Position!.Offset);

        var (_, hitEnd) = HitTest(html, 15, 10);
        Assert.NotNull(hitEnd?.Position);
        // Offset must be 0 or 2, never 1 inside the combining mark cluster!
        Assert.True(hitEnd!.Position!.Offset is 0 or 2, $"Offset was {hitEnd.Position.Offset}, expected grapheme cluster boundary 0 or 2");
    }

    [Fact]
    public void PointsOutsideTextSnapToNearestCaretPosition()
    {
        const string html = "<div style='padding: 20px'><p id=p style='margin: 0'>hello</p></div>";
        // Left of paragraph (in padding): snaps to start of line (offset 0)
        var (_, hitLeft) = HitTest(html, 5, 25);
        Assert.NotNull(hitLeft?.Position);
        Assert.Equal(0, hitLeft!.Position!.Offset);

        // Right of paragraph: snaps to end of line (offset 5)
        var (_, hitRight) = HitTest(html, 300, 25);
        Assert.NotNull(hitRight?.Position);
        Assert.Equal(5, hitRight!.Position!.Offset);

        // Above paragraph (in padding): snaps to line box
        var (_, hitAbove) = HitTest(html, 50, 5);
        Assert.NotNull(hitAbove?.Position);
        Assert.IsAssignableFrom<Text>(hitAbove!.Position!.Node);

        // Below paragraph
        var (_, hitBelow) = HitTest(html, 50, 70);
        Assert.NotNull(hitBelow?.Position);
        Assert.IsAssignableFrom<Text>(hitBelow!.Position!.Node);
    }

    [Fact]
    public void PointsBetweenLinesSnapToNearestLine()
    {
        const string html = "<p id=p style='margin: 0; line-height: 2'>line one<br>line two</p>";
        // Between lines: Y coordinate halfway between line 1 and line 2
        var (_, hitUpper) = HitTest(html, 50, 15);
        Assert.NotNull(hitUpper?.Position);

        var (_, hitLower) = HitTest(html, 50, 50);
        Assert.NotNull(hitLower?.Position);
    }

    [Fact]
    public void PointInTableCellResolvesCorrectly()
    {
        const string html = "<table style='border-spacing: 0'><tr><td id=cell style='padding: 10px'>cell text</td></tr></table>";
        var (_, hitInside) = HitTest(html, 20, 15);
        Assert.NotNull(hitInside?.Position);
        Assert.Equal("cell", hitInside!.Node.GetAttribute("id"));

        var (_, hitPadding) = HitTest(html, 5, 15); // in cell left padding
        Assert.NotNull(hitPadding?.Position);
        Assert.Equal(0, hitPadding!.Position!.Offset);
    }

    [Fact]
    public void VerticalTextSnapsAlongVerticalAxis()
    {
        const string html = "<p style='writing-mode: vertical-rl; height: 100px; font: 20px/1.5 monospace'>abc</p>";
        var (_, hitTop) = HitTest(html, 15, 5);
        Assert.NotNull(hitTop?.Position);
        Assert.Equal(0, hitTop!.Position!.Offset);

        var (_, hitBottom) = HitTest(html, 15, 95);
        Assert.NotNull(hitBottom?.Position);
        Assert.Equal(3, hitBottom!.Position!.Offset);
    }

    [Fact]
    public void CaretRectRoundTripsHitTesting()
    {
        const string html = "<p id=p>Testing roundtrip caret</p>";
        var frag = BlockLayoutTests.LayOut(Page + html);

        static Fragment? FindTextFragment(Fragment node)
        {
            if (node.Kind == FragmentKind.Text && node.Text?.Node != null)
                return node;
            foreach (var child in node.Children)
            {
                var found = FindTextFragment(child.Fragment);
                if (found != null) return found;
            }
            return null;
        }

        var textFrag = FindTextFragment(frag);
        Assert.NotNull(textFrag);
        var textNode = textFrag.Value.Text!.Value.Node!;

        // For multiple test offsets, CaretRect -> Hit test contains that point
        for (var offset = 0; offset <= 10; offset++)
        {
            var pos = new TextPosition(textNode, offset);
            var caret = HitTester.CaretRect(frag, pos);
            Assert.True(caret.Width > 0, "Caret width should be positive");
            Assert.True(caret.Height > 0, "Caret height should be positive");

            // Point inside caret
            var hit = HitTester.Hit(frag, new Vector2(caret.X, caret.Y + caret.Height / 2f));
            Assert.NotNull(hit?.Position);
            Assert.Equal(pos.Offset, hit!.Position!.Offset);
        }
    }
}
