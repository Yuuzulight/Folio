using System.Numerics;
using Folio.Css;
using Folio.Dom;
using Folio.Interaction;
using Folio.Layout;
using Folio.Painting;
using Folio.Style;

namespace Folio;

// The geometry CSSOM View asks of the last layout (https://www.w3.org/TR/cssom-view-1/): where an element's boxes are in the
// viewport, through the scrolling and the transforms around them, which positioned ancestor offsets are measured from, and
// what lies under a point.
public sealed partial class Document
{
    /// <summary>The layout viewport of the last paint: its size and the device scale; an unpainted document has none.</summary>
    internal (float Width, float Height, float Scale)? Viewport => _lastPaint is { } paint ? (paint.Width, paint.Height, paint.Scale) : null;

    /// <summary>A scroll container's offset changed (a script, the user, a smooth scroll's frame); scripts send their <c>scroll</c> events at the next rendering opportunity.</summary>
    internal event Action<ElementNode>? ScrollOffsetChanged;

    private void SetScrollOffset(ElementNode node, Vector2 offset)
    {
        if (_scrollOffsets.TryGetValue(node, out var old) && old == offset)
            return;
        _scrollOffsets[node] = offset;
        ScrollOffsetChanged?.Invoke(node);
    }

    /// <summary>Scrolls an element node to a position, clamped to its range; <paramref name="behavior"/> is <c>smooth</c> or <c>instant</c>/<c>auto</c>.</summary>
    internal void ScrollToNode(ElementNode node, float x, float y, string? behavior = null) => ScrollTo(Wrap(node), x, y, behavior);

    /// <summary>What media queries are evaluated against now: the viewport, the colour scheme and the motion preference.</summary>
    internal MediaContext CurrentMedia()
    {
        var (width, height, scale) = Viewport ?? (800, 600, 1);
        return new MediaContext(width, height, scale, Options.ColorScheme == ColorScheme.Dark) { ReducedMotion = Options.ReducedMotion };
    }

    /// <summary>Whether a media query list matches the document's current environment (<c>matchMedia(query).matches</c>).</summary>
    internal bool MatchesMedia(string query) => Conditions.MediaMatches(query, CurrentMedia());

    /// <summary>How far the viewport is scrolled.</summary>
    internal Vector2 ViewportScroll => Node.DocumentElement is { } root && _scrollOffsets.TryGetValue(root, out var offset) ? offset : Vector2.Zero;

    private readonly record struct Placed(Fragment Fragment, float X, float Y, Matrix4x4 Matrix, bool Fixed, Fragment? Line);

    // Every fragment of the last layout with its border box's top-left corner in document coordinates (scroll offsets of scroll
    // containers applied, but not the viewport's) and the transform that maps its points to the viewport: its own, if it has one,
    // after those of the boxes around it (https://www.w3.org/TR/css-transforms-1/#transform-rendering).
    private IEnumerable<Placed> PlaceFragments(bool scrolled = true)
    {
        if (_page is not { } page)
            yield break;
        var stack = new Stack<(Fragment Fragment, float X, float Y, Matrix4x4 Around, bool Fixed, Fragment? Line)>();
        stack.Push((page, 0, 0, Matrix4x4.Identity, false, null));
        while (stack.TryPop(out var item))
        {
            var (fragment, x, y, around, isFixed, line) = item;
            var matrix = around;
            var fixedHere = isFixed;
            if (fragment.Box is { } box)
            {
                if (box.Style.Box.Position == Position.Fixed)
                    fixedHere = true;
                if (box.IsTransformed)
                    matrix = Matrix4x4.CreateTranslation(-x, -y, 0) * box.Style.Transform.Matrix(fragment.Width, fragment.Height) * Matrix4x4.CreateTranslation(x, y, 0) * around;
            }
            yield return new Placed(fragment, x, y, matrix, fixedHere, line);
            // The viewport's scroll is applied by the caller (a fixed box does not scroll with it); every other scroll container's here.
            var scrolls = scrolled && fragment.Box?.Node is ElementNode scroller && scroller != Node.DocumentElement && IsScrollContainer(scroller) && _scrollOffsets.TryGetValue(scroller, out var offset) ? offset : Vector2.Zero;
            for (var i = fragment.Children.Count - 1; i >= 0; i--)
            {
                var child = fragment.Children[i];
                stack.Push((child.Fragment, x + child.X - scrolls.X, y + child.Y - scrolls.Y, matrix, fixedHere, fragment.Kind == FragmentKind.Line ? fragment : line));
            }
        }
    }

    private static RectF BoundsOf(RectF rect, Matrix4x4 matrix, Vector2 shift)
    {
        if (matrix.IsIdentity)
            return new RectF(rect.X - shift.X, rect.Y - shift.Y, rect.Width, rect.Height);
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var corner in new[] { new Vector2(rect.X, rect.Y), new Vector2(rect.Right, rect.Y), new Vector2(rect.Right, rect.Bottom), new Vector2(rect.X, rect.Bottom) })
        {
            var p = Vector4.Transform(new Vector4(corner, 0, 1), matrix);
            var (px, py) = p.W != 0 && p.W != 1 ? (p.X / p.W, p.Y / p.W) : (p.X, p.Y);
            (minX, minY, maxX, maxY) = (Math.Min(minX, px), Math.Min(minY, py), Math.Max(maxX, px), Math.Max(maxY, py));
        }
        return new RectF(minX - shift.X, minY - shift.Y, maxX - minX, maxY - minY);
    }

    private static bool IsInside(Node? node, ElementNode ancestor)
    {
        for (var cursor = node; cursor is not null; cursor = cursor.Parent)
        {
            if (cursor == ancestor)
                return true;
        }
        return false;
    }

    /// <summary>
    /// The rectangles of an element's boxes in viewport coordinates, as <c>getClientRects()</c> lists them: its border box if it
    /// has one, else (an inline element) one per line the element's text and inline-level children are on. Empty for an element
    /// with no box (display: none, not rendered).
    /// </summary>
    internal IReadOnlyList<RectF> ClientRects(ElementNode element)
    {
        var shift = ViewportScroll;
        if (element == Node.DocumentElement && _page is { } icb)
            return [new RectF(0, 0, icb.Width, icb.Height)];
        var boxes = new List<RectF>();
        var lines = new Dictionary<Fragment, RectF>();
        var order = new List<Fragment>();
        foreach (var placed in PlaceFragments())
        {
            var fragment = placed.Fragment;
            var rect = new RectF(placed.X, placed.Y, fragment.Width, fragment.Height);
            var offset = placed.Fixed ? Vector2.Zero : shift;
            if (fragment.Kind == FragmentKind.Box && fragment.Box?.Node == element)
                boxes.Add(BoundsOf(rect, placed.Matrix, offset));
            else if (fragment.Kind == FragmentKind.Text && fragment.Text is { } text && IsInside(text.Node ?? text.Inline?.Node, element) && fragment.Box is { } owner
                && element.Name.Namespace == Namespaces.Html)
            {
                // Text fragments sit under their line; group the pieces of one line into one rectangle.
                var bounds = BoundsOf(rect, placed.Matrix, offset);
                var key = placed.Line ?? fragment;
                if (lines.TryGetValue(key, out var existing))
                    lines[key] = Union(existing, bounds);
                else
                {
                    lines[key] = bounds;
                    order.Add(key);
                }
            }
        }
        if (boxes.Count > 0)
            return boxes;
        return order.Select(line => lines[line]).ToList();

        static RectF Union(RectF a, RectF b)
        {
            var (l, t, r, bt) = (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));
            return new RectF(l, t, r - l, bt - t);
        }
    }

    /// <summary>The union of an element's rectangles in viewport coordinates, <c>getBoundingClientRect()</c>; null when it has none.</summary>
    internal RectF? ClientBoundingRect(ElementNode element)
    {
        var rects = ClientRects(element);
        if (rects.Count == 0)
            return null;
        var (l, t, r, b) = (rects.Min(x => x.X), rects.Min(x => x.Y), rects.Max(x => x.Right), rects.Max(x => x.Bottom));
        return new RectF(l, t, r - l, b - t);
    }

    /// <summary>An element's border box laid out, before transforms, and its border widths: <c>offsetWidth</c> and <c>clientLeft</c>.</summary>
    internal (RectF Rect, float BorderLeft, float BorderTop)? LayoutBox(ElementNode element)
    {
        if (element == Node.DocumentElement && _page is { } icb)
            return (new RectF(0, 0, icb.Width, icb.Height), 0, 0);
        foreach (var placed in PlaceFragments(scrolled: false))
        {
            if (placed.Fragment.Kind == FragmentKind.Box && placed.Fragment.Box?.Node == element)
            {
                var border = placed.Fragment.Box.Style.Border;
                return (new RectF(placed.X, placed.Y, placed.Fragment.Width, placed.Fragment.Height), border.LeftWidth, border.TopWidth);
            }
        }
        return null;
    }

    /// <summary>
    /// The element an element's <c>offsetLeft</c> and <c>offsetTop</c> are measured from (https://www.w3.org/TR/cssom-view-1/#dom-htmlelement-offsetparent):
    /// the nearest ancestor that is positioned, or for an element that is not, also a table, cell or the body; null for the root
    /// and the body, a fixed element and one with no box.
    /// </summary>
    internal ElementNode? OffsetParent(ElementNode element)
    {
        if (LayoutBox(element) is null || element == Node.DocumentElement || IsHtmlBody(element))
            return null;
        var own = FindFragment(element)?.Box?.Style.Box.Position ?? Position.Static;
        if (own == Position.Fixed)
            return null;
        for (var cursor = element.Parent as ElementNode; cursor is not null; cursor = cursor.Parent as ElementNode)
        {
            if (IsHtmlBody(cursor))
                return cursor;
            var position = FindFragment(cursor)?.Box?.Style.Box.Position ?? Position.Static;
            if (position != Position.Static)
                return cursor;
            if (own == Position.Static && cursor.Name.Namespace == Namespaces.Html && cursor.LocalName is "td" or "th" or "table")
                return cursor;
        }
        return Node.DocumentElement is { } root && root != element ? FirstBody() : null;

        ElementNode? FirstBody() => Node.DocumentElement?.Children.OfType<ElementNode>().FirstOrDefault(IsHtmlBody);
    }

    private static bool IsHtmlBody(ElementNode e) => e.Name.Namespace == Namespaces.Html && e.LocalName == "body";

    /// <summary>The border box's offset from its offset parent's padding edge, or from the page's origin when there is none.</summary>
    internal Vector2? OffsetPosition(ElementNode element)
    {
        if (LayoutBox(element) is not { } own)
            return null;
        if (IsHtmlBody(element))
            return Vector2.Zero;
        // A body that is not positioned stands for the page: browsers measure from its origin, margin and all.
        if (OffsetParent(element) is { } parent && !(IsHtmlBody(parent) && (FindFragment(parent)?.Box?.Style.Box.Position ?? Position.Static) == Position.Static) && LayoutBox(parent) is { } box)
            return new Vector2(own.Rect.X - box.Rect.X - box.BorderLeft, own.Rect.Y - box.Rect.Y - box.BorderTop);
        return new Vector2(own.Rect.X, own.Rect.Y);
    }

    /// <summary>
    /// The elements under a point in viewport coordinates, topmost first, ending with the root element
    /// (https://www.w3.org/TR/cssom-view-1/#dom-document-elementsfrompoint); empty outside the viewport.
    /// </summary>
    internal IReadOnlyList<ElementNode> ElementsAtClientPoint(float x, float y)
    {
        if (_page is not { } page || Viewport is not { } viewport || x < 0 || y < 0 || x >= viewport.Width || y >= viewport.Height)
            return [];
        var hits = HitTester.HitAll(page, new Vector2(x, y), viewport.Scale, _scrollOffsets);
        if (Node.DocumentElement is { } root && !hits.Contains(root))
            hits.Add(root);
        return hits;
    }
}
