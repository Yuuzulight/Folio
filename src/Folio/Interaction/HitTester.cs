using System.Numerics;
using Folio.Dom;
using Folio.Layout;
using Folio.Painting;
using Folio.Style;

namespace Folio.Interaction;

/// <summary>What is under a point: the element, the box and fragment hit, and the point in the fragment's own coordinates.</summary>
/// <param name="Node">The box's element; text resolves to the inline box it is in, or else its block's element.</param>
/// <param name="LocalPoint">The point from the fragment's top-left corner, through any transforms around it.</param>
internal sealed record HitResult(ElementNode Node, Box Box, Fragment Fragment, Vector2 LocalPoint);

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
        return visitor.Result;
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
                    Result = new HitResult(element, box.Box, box.Fragment, _point - new Vector2(box.X, box.Y));
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
