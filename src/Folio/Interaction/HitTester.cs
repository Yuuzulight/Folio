using System.Globalization;
using System.Numerics;
using Folio.Dom;
using Folio.Layout;
using Folio.Painting;
using Folio.Style;

namespace Folio.Interaction;

/// <summary>A position in a DOM text node or element (UAX #29 grapheme cluster boundary).</summary>
internal sealed record TextPosition(Node Node, int Offset);

/// <summary>What is under a point: the element, the box and fragment hit, and the point in the fragment's own coordinates.</summary>
/// <param name="Node">The box's element; text resolves to the inline box it is in, or else its block's element.</param>
/// <param name="LocalPoint">The point from the fragment's top-left corner, through any transforms around it.</param>
/// <param name="Position">The nearest text caret position, if within or near text content.</param>
/// <param name="Style">The computed style of the hit fragment or box.</param>
internal sealed record HitResult(ElementNode Node, Box Box, Fragment Fragment, Vector2 LocalPoint, TextPosition? Position = null, ComputedStyle? Style = null);

/// <summary>
/// Hit testing in reverse paint order (docs/study/15-interaction.md, option B; https://www.w3.org/TR/cssom-view-1/#dom-document-elementfrompoint):
/// the stacking tree painting builds, walked front to back, so the first box that holds the point is the one painted on
/// top. A box is hit inside its border box with its corners rounded, under the overflow clips and clip paths around it
/// and through the inverse of the transforms around it, unless it is hidden or has pointer-events: none.
/// </summary>
// ponytail: the stacking tree is built again for every hit, a walk of the whole page; cache it with the layout if
// pointer moves over large documents show up. Scroll offsets join when scroll containers keep them (#421); a url()
// clip path does not clip hits.
internal static class HitTester
{
    /// <param name="point">Page coordinates in CSS pixels.</param>
    /// <param name="deviceScale">The scale the page was painted at, whose device pixels box edges snapped to.</param>
    public static HitResult? Hit(Fragment initialContainingBlock, Vector2 point, float deviceScale = 1)
    {
        if (PaintOrderWalker.StackingTree(initialContainingBlock, deviceScale) is not { } root)
            return null;
        var visitor = new Visitor(point);
        PaintOrderWalker.Walk(root, visitor, frontToBack: true);
        if (visitor.Result is not { } hit)
            return null;
        if (hit.Position is not null)
            return hit;
        var snapped = SnapToNearestLineBox(initialContainingBlock, point);
        return snapped is not null ? hit with { Position = snapped } : hit;
    }

    private sealed class Visitor(Vector2 point) : IPaintVisitor
    {
        public HitResult? Result { get; private set; }

        // The point in the current context's coordinates, and the clip its owner sits under: the clips from there up
        // were tested outside the context's transform, so its boxes test only the ones below.
        private Vector2 _point = point;
        private ClipNode? _base;
        private readonly Stack<(Vector2 Point, ClipNode? Base)?> _outer = new();

        public bool BeginContext(Context context)
        {
            if (Result is not null)
                return false;
            var owner = context.Real ? context.Owner : null;
            var transform = owner is { Box.IsTransformed: true } ? PaintOrderWalker.Transform(owner) : (Matrix4x4?)null;
            var clipPath = owner is null || owner.Box.Style.Effects.ClipPath is { IsNone: true } or { Url: not null } ? (DisplayItem?)null
                : DisplayListBuilder.ClipPathItem(owner, owner.Box.Style.Effects.ClipPath);
            if (transform is null && clipPath is null)
            {
                _outer.Push(null);
                return true;
            }
            // Painting draws the context inside its owner's clips, then through its transform and clip path.
            if (!InClips(owner!.Clip))
                return false;
            var mapped = _point;
            if (transform is { } matrix)
            {
                if (Unproject(matrix, _point) is not { } inside)
                    return false;
                mapped = inside;
            }
            if (clipPath is { } clip && !(clip.Path is { } path ? InPath(path, clip.Rule, mapped) : Contains(clip.Shape, mapped)))
                return false;
            _outer.Push((_point, _base));
            (_point, _base) = (mapped, owner.Clip);
            return true;
        }

        public void EndContext(Context context)
        {
            if (_outer.Pop() is { } outer)
                (_point, _base) = outer;
        }

        public void VisitBackground(PaintBox box, List<PaintBox> text) => Test(box, PaintOrderWalker.BorderBox(box), box.Box.Style);

        public void VisitText(PaintBox text) =>
            Test(text, new RoundedRect(text.Rect, default), text.Fragment.Text?.Style ?? text.Box.Style, text.Fragment.Text?.Inline);

        // Outlines and collapsed borders are not hit (https://www.w3.org/TR/css-ui-4/#outline-props); cells are, by their boxes.
        public void VisitCollapsedBorders(PaintBox table, List<PaintBox> cells) { }

        public void VisitOutline(PaintBox box) { }

        private void Test(PaintBox box, RoundedRect shape, ComputedStyle style, Box? inline = null)
        {
            if (Result is not null || style.Inherited.Visibility != Visibility.Visible || style.Ui.PointerEvents == PointerEvents.None
                || !Contains(shape, _point) || !InClips(box.Clip))
                return;
            // An anonymous box belongs to the element its nearest named ancestor box is for.
            for (var owner = inline ?? box.Box; owner is not null; owner = owner.Parent)
            {
                if (owner.Node is ElementNode element)
                {
                    var local = _point - new Vector2(box.X, box.Y);
                    TextPosition? position = null;
                    if (box.Fragment.Kind == FragmentKind.Text && box.Fragment.Text is { } textRun)
                        position = TextPositionFromRun(box.Fragment, textRun, local.X, local.Y);
                    Result = new HitResult(element, box.Box, box.Fragment, local, position, style);
                    return;
                }
            }
        }

        private bool InClips(ClipNode? clip)
        {
            for (; clip is not null && clip != _base; clip = clip.Parent)
            {
                if (!Contains(clip.Shape, _point))
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Resolves a point within a text run to a DOM Text node and UTF-16 offset at a grapheme cluster boundary.
    /// </summary>
    internal static TextPosition? TextPositionFromRun(Fragment fragment, TextRun textRun, float localX, float localY)
    {
        var run = textRun.Run;
        if (textRun.Node is not Text domText || run.Glyphs.Length == 0)
            return null;

        var fullText = domText.Data;
        var count = textRun.GlyphEnd - textRun.GlyphStart;
        if (count <= 0)
            return new TextPosition(domText, 0);

        var glyphs = run.Glyphs;
        var advances = run.Advances;
        var clusters = run.Clusters;

        // Group glyphs by grapheme cluster boundary in the text
        var groups = new List<(int TextStart, int TextEnd, float Width)>();
        var curIdx = 0;
        while (curIdx < count)
        {
            var g = textRun.GlyphStart + curIdx;
            var cStart = clusters[g];
            var nextClusterLength = StringInfo.GetNextTextElementLength(fullText, Math.Clamp(cStart, 0, fullText.Length - 1));
            var cEnd = Math.Min(fullText.Length, cStart + nextClusterLength);

            var w = 0f;
            var advancedAny = false;
            while (curIdx < count)
            {
                var glyphGlobal = textRun.GlyphStart + curIdx;
                if (clusters[glyphGlobal] >= cEnd && advancedAny)
                    break;
                w += advances[glyphGlobal];
                curIdx++;
                advancedAny = true;
            }
            groups.Add((cStart, cEnd, w));
        }

        if (groups.Count == 0)
            return new TextPosition(domText, 0);

        if (textRun.Turned)
        {
            // Vertical text: advances run along localY (top to bottom)
            var yPos = localY;
            var acc = 0f;
            for (var i = 0; i < groups.Count; i++)
            {
                var grp = groups[i];
                var nextAcc = acc + grp.Width;
                if (yPos <= nextAcc || i == groups.Count - 1)
                {
                    var mid = acc + grp.Width / 2f;
                    return new TextPosition(domText, yPos < mid ? grp.TextStart : grp.TextEnd);
                }
                acc = nextAcc;
            }
            return new TextPosition(domText, groups[^1].TextEnd);
        }

        if (textRun.RightToLeft)
        {
            // RTL: glyphs laid out right-to-left from fragment.Width down to 0
            var xPos = localX;
            var curRight = fragment.Width;
            for (var i = 0; i < groups.Count; i++)
            {
                var grp = groups[i];
                var curLeft = curRight - grp.Width;
                if (xPos >= curLeft || i == groups.Count - 1)
                {
                    var mid = (curLeft + curRight) / 2f;
                    // In RTL, the right side is the logical start, the left side is the logical end.
                    return new TextPosition(domText, xPos >= mid ? grp.TextStart : grp.TextEnd);
                }
                curRight = curLeft;
            }
            return new TextPosition(domText, groups[^1].TextEnd);
        }
        else
        {
            // LTR: glyphs laid out left-to-right from 0 to fragment.Width
            var xPos = localX;
            var acc = 0f;
            for (var i = 0; i < groups.Count; i++)
            {
                var grp = groups[i];
                var nextAcc = acc + grp.Width;
                if (xPos <= nextAcc || i == groups.Count - 1)
                {
                    var mid = acc + grp.Width / 2f;
                    return new TextPosition(domText, xPos < mid ? grp.TextStart : grp.TextEnd);
                }
                acc = nextAcc;
            }
            return new TextPosition(domText, groups[^1].TextEnd);
        }
    }

    private static TextPosition? SnapToNearestLineBox(Fragment initialContainingBlock, Vector2 pagePoint)
    {
        var lineBoxes = new List<(Fragment Line, float Left, float Top, float Width, float Height, bool Vertical)>();
        CollectLineBoxes(initialContainingBlock, 0, 0, lineBoxes);

        if (lineBoxes.Count == 0)
            return null;

        // Find nearest line box by distance to its bounds
        var bestDist = float.MaxValue;
        (Fragment Line, float Left, float Top, float Width, float Height, bool Vertical)? bestLine = null;

        foreach (var lb in lineBoxes)
        {
            var dx = pagePoint.X < lb.Left ? lb.Left - pagePoint.X : pagePoint.X > lb.Left + lb.Width ? pagePoint.X - (lb.Left + lb.Width) : 0;
            var dy = pagePoint.Y < lb.Top ? lb.Top - pagePoint.Y : pagePoint.Y > lb.Top + lb.Height ? pagePoint.Y - (lb.Top + lb.Height) : 0;
            var dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestLine = lb;
            }
        }

        if (bestLine is not { } targetLine)
            return null;

        return SnapWithinLineBox(targetLine.Line, targetLine.Left, targetLine.Top, pagePoint, targetLine.Vertical);
    }

    private static void CollectLineBoxes(Fragment fragment, float parentX, float parentY,
        List<(Fragment Line, float Left, float Top, float Width, float Height, bool Vertical)> lines)
    {
        foreach (var child in fragment.Children)
        {
            var x = parentX + child.X;
            var y = parentY + child.Y;
            if (child.Fragment.Kind == FragmentKind.Line)
            {
                var isVertical = child.Fragment.Children.Any(c => c.Fragment.Text?.Turned == true);
                lines.Add((child.Fragment, x, y, child.Fragment.Width, child.Fragment.Height, isVertical));
            }
            else
            {
                CollectLineBoxes(child.Fragment, x, y, lines);
            }
        }
    }

    private static TextPosition? SnapWithinLineBox(Fragment lineFragment, float lineLeft, float lineTop, Vector2 pagePoint, bool isVertical)
    {
        var textFragments = new List<(Fragment Frag, TextRun Run, float Left, float Top, float Width, float Height)>();
        foreach (var child in lineFragment.Children)
        {
            if (child.Fragment.Kind == FragmentKind.Text && child.Fragment.Text is { Node: Text } tr)
            {
                textFragments.Add((child.Fragment, tr, lineLeft + child.X, lineTop + child.Y, child.Fragment.Width, child.Fragment.Height));
            }
        }

        if (textFragments.Count == 0)
            return null;

        if (isVertical)
        {
            // Vertical text: lines run vertically, pick nearest text fragment along Y
            var pY = pagePoint.Y;
            // Sort by Top
            textFragments.Sort((a, b) => a.Top.CompareTo(b.Top));
            if (pY <= textFragments[0].Top)
            {
                var first = textFragments[0];
                return TextPositionFromRun(first.Frag, first.Run, pagePoint.X - first.Left, 0);
            }
            if (pY >= textFragments[^1].Top + textFragments[^1].Height)
            {
                var last = textFragments[^1];
                return TextPositionFromRun(last.Frag, last.Run, pagePoint.X - last.Left, last.Height);
            }
            foreach (var tf in textFragments)
            {
                if (pY >= tf.Top && pY <= tf.Top + tf.Height)
                    return TextPositionFromRun(tf.Frag, tf.Run, pagePoint.X - tf.Left, pY - tf.Top);
            }
            // Between fragments: pick closer
            var closest = textFragments.MinBy(tf => Math.Min(Math.Abs(pY - tf.Top), Math.Abs(pY - (tf.Top + tf.Height))));
            var localY = Math.Clamp(pY - closest.Top, 0, closest.Height);
            return TextPositionFromRun(closest.Frag, closest.Run, pagePoint.X - closest.Left, localY);
        }
        else
        {
            // Horizontal text: visual order left to right
            textFragments.Sort((a, b) => a.Left.CompareTo(b.Left));
            var pX = pagePoint.X;
            if (pX <= textFragments[0].Left)
            {
                var first = textFragments[0];
                var localX = first.Run.RightToLeft ? first.Width : 0;
                return TextPositionFromRun(first.Frag, first.Run, localX, pagePoint.Y - first.Top);
            }
            if (pX >= textFragments[^1].Left + textFragments[^1].Width)
            {
                var last = textFragments[^1];
                var localX = last.Run.RightToLeft ? 0 : last.Width;
                return TextPositionFromRun(last.Frag, last.Run, localX, pagePoint.Y - last.Top);
            }
            foreach (var tf in textFragments)
            {
                if (pX >= tf.Left && pX <= tf.Left + tf.Width)
                    return TextPositionFromRun(tf.Frag, tf.Run, pX - tf.Left, pagePoint.Y - tf.Top);
            }
            // Between fragments: pick closer
            var closest = textFragments.MinBy(tf => Math.Min(Math.Abs(pX - tf.Left), Math.Abs(pX - (tf.Left + tf.Width))));
            var clampX = Math.Clamp(pX - closest.Left, 0, closest.Width);
            return TextPositionFromRun(closest.Frag, closest.Run, clampX, pagePoint.Y - closest.Top);
        }
    }

    /// <summary>
    /// Computes the 1px-wide caret rectangle in page coordinates for a given text position.
    /// </summary>
    public static RectF CaretRect(Fragment initialContainingBlock, TextPosition position)
    {
        var runs = new List<(Fragment Frag, TextRun Run, float Left, float Top, float LineHeight)>();
        CollectTextRuns(initialContainingBlock, 0, 0, null, runs);

        var matching = runs.FindAll(r => r.Run.Node == position.Node);
        if (matching.Count == 0)
            return default;

        var fullText = ((Text)position.Node).Data;

        // Locate run covering the position offset
        var runEntry = matching.Find(r =>
        {
            var start = r.Run.Run.Clusters[r.Run.GlyphStart];
            var end = r.Run.Run.Clusters[r.Run.GlyphEnd - 1] + StringInfo.GetNextTextElementLength(fullText, Math.Clamp(r.Run.Run.Clusters[r.Run.GlyphEnd - 1], 0, fullText.Length - 1));
            return position.Offset >= start && position.Offset <= end;
        });

        if (runEntry == default)
            runEntry = position.Offset <= matching[0].Run.Run.Clusters[matching[0].Run.GlyphStart] ? matching[0] : matching[^1];

        var textRun = runEntry.Run;
        var frag = runEntry.Frag;
        var run = textRun.Run;
        var count = textRun.GlyphEnd - textRun.GlyphStart;

        var groups = new List<(int TextStart, int TextEnd, float Width)>();
        var curIdx = 0;
        while (curIdx < count)
        {
            var g = textRun.GlyphStart + curIdx;
            var cStart = run.Clusters[g];
            var nextLen = StringInfo.GetNextTextElementLength(fullText, Math.Clamp(cStart, 0, fullText.Length - 1));
            var cEnd = Math.Min(fullText.Length, cStart + nextLen);

            var w = 0f;
            var advancedAny = false;
            while (curIdx < count)
            {
                var gGlobal = textRun.GlyphStart + curIdx;
                if (run.Clusters[gGlobal] >= cEnd && advancedAny)
                    break;
                w += run.Advances[gGlobal];
                curIdx++;
                advancedAny = true;
            }
            groups.Add((cStart, cEnd, w));
        }

        if (textRun.Turned)
        {
            var yOffset = 0f;
            foreach (var grp in groups)
            {
                if (position.Offset <= grp.TextStart)
                    break;
                if (position.Offset >= grp.TextEnd)
                {
                    yOffset += grp.Width;
                    continue;
                }
                break;
            }
            var caretY = runEntry.Top + yOffset;
            var caretX = runEntry.Left;
            return new RectF(caretX, caretY, frag.Width, 1);
        }
        else if (textRun.RightToLeft)
        {
            var xOffset = frag.Width;
            foreach (var grp in groups)
            {
                if (position.Offset <= grp.TextStart)
                    break;
                xOffset -= grp.Width;
                if (position.Offset <= grp.TextEnd)
                    break;
            }
            var caretX = runEntry.Left + xOffset;
            var caretY = runEntry.Top;
            var h = runEntry.LineHeight > 0 ? runEntry.LineHeight : frag.Height;
            return new RectF(caretX, caretY, 1, h);
        }
        else
        {
            var xOffset = 0f;
            foreach (var grp in groups)
            {
                if (position.Offset <= grp.TextStart)
                    break;
                xOffset += grp.Width;
                if (position.Offset <= grp.TextEnd)
                    break;
            }
            var caretX = runEntry.Left + xOffset;
            var caretY = runEntry.Top;
            var h = runEntry.LineHeight > 0 ? runEntry.LineHeight : frag.Height;
            return new RectF(caretX, caretY, 1, h);
        }
    }

    private static void CollectTextRuns(Fragment fragment, float parentX, float parentY, float? curLineHeight,
        List<(Fragment Frag, TextRun Run, float Left, float Top, float LineHeight)> list)
    {
        foreach (var child in fragment.Children)
        {
            var x = parentX + child.X;
            var y = parentY + child.Y;
            if (child.Fragment.Kind == FragmentKind.Text && child.Fragment.Text is { } tr)
            {
                list.Add((child.Fragment, tr, x, y, curLineHeight ?? child.Fragment.Height));
            }
            else
            {
                var lh = child.Fragment.Kind == FragmentKind.Line ? child.Fragment.Height : curLineHeight;
                CollectTextRuns(child.Fragment, x, y, lh, list);
            }
        }
    }

    // The point on the plane z = 0 that a transform flattened to 2D (its x, y and w rows and columns, as painted) puts
    // at the given one, or null if none does.
    private static Vector2? Unproject(Matrix4x4 m, Vector2 p)
    {
        var flat = new Matrix4x4(m.M11, m.M12, 0, m.M14, m.M21, m.M22, 0, m.M24, 0, 0, 1, 0, m.M41, m.M42, 0, m.M44);
        if (!Matrix4x4.Invert(flat, out var inverse))
            return null;
        var v = Vector4.Transform(new Vector4(p, 0, 1), inverse);
        return v.W > 0 ? new Vector2(v.X, v.Y) / v.W : null;
    }

    // Inside a rectangle and, in each corner, inside the corner's ellipse.
    internal static bool Contains(RoundedRect shape, Vector2 p)
    {
        var r = shape.Rect;
        if (p.X < r.X || p.Y < r.Y || p.X >= r.Right || p.Y >= r.Bottom)
            return false;
        return InCorner(shape.Radii.TopLeft, new(r.X, r.Y), -1, -1) && InCorner(shape.Radii.TopRight, new(r.Right, r.Y), 1, -1)
            && InCorner(shape.Radii.BottomRight, new(r.Right, r.Bottom), 1, 1) && InCorner(shape.Radii.BottomLeft, new(r.X, r.Bottom), -1, 1);

        bool InCorner(Vector2 radius, Vector2 corner, float sx, float sy)
        {
            if (radius.X <= 0 || radius.Y <= 0)
                return true;
            var center = corner - new Vector2(sx * radius.X, sy * radius.Y);
            var d = (p - center) / radius;
            return (p.X - center.X) * sx <= 0 || (p.Y - center.Y) * sy <= 0 || d.LengthSquared() <= 1;
        }
    }

    // Inside a path by its fill rule, its curves flattened to lines.
    // ponytail: eight segments a curve; finer when clip paths with large curves need exact edges.
    private static bool InPath(PathData path, FillRule rule, Vector2 p)
    {
        var winding = 0;
        var (start, current) = (Vector2.Zero, Vector2.Zero);
        foreach (var command in path.Commands)
        {
            switch (command.Verb)
            {
                case PathVerb.MoveTo:
                    Edge(current, start);
                    start = current = command.P1;
                    break;
                case PathVerb.LineTo:
                    Edge(current, command.P1);
                    current = command.P1;
                    break;
                case PathVerb.CubicTo:
                    var from = current;
                    for (var i = 1; i <= 8; i++)
                    {
                        var t = i / 8f;
                        var u = 1 - t;
                        var next = u * u * u * from + 3 * u * u * t * command.P1 + 3 * u * t * t * command.P2 + t * t * t * command.P3;
                        Edge(current, next);
                        current = next;
                    }
                    break;
                default:
                    Edge(current, start);
                    current = start;
                    break;
            }
        }
        Edge(current, start);
        return rule == FillRule.EvenOdd ? (winding & 1) != 0 : winding != 0;

        // A ray to the right crossing an edge upwards winds once, downwards unwinds.
        void Edge(Vector2 a, Vector2 b)
        {
            if ((a.Y <= p.Y) == (b.Y <= p.Y))
                return;
            var x = a.X + (p.Y - a.Y) / (b.Y - a.Y) * (b.X - a.X);
            if (x > p.X)
                winding += b.Y > a.Y ? 1 : -1;
        }
    }
}
