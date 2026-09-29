using System.Numerics;
using Folio.Css;
using Folio.Dom;
using Folio.Layout;
using Folio.Style;

namespace Folio.Painting;

/// <summary>
/// Builds the display list from the fragment tree: first the stacking tree
/// (docs/study/10-layout-positioning-overflow-stacking.md, option A), then its paint order per CSS 2.2 Appendix E.
/// Every painted box carries the clip chain of its containing blocks' overflow clips.
/// </summary>
// ponytail: M1 paints background colours and borders; images, text, outlines and markers come with their own work.
internal static class DisplayListBuilder
{
    // A box's border box on the canvas, with the overflow clips it is painted under.
    private sealed record PaintBox(Fragment Fragment, float X, float Y, ClipNode? Clip)
    {
        public Box Box => Fragment.Box!;
        public RectF Rect => new(X, Y, Fragment.Width, Fragment.Height);
    }

    private sealed class ClipNode(ClipNode? parent, RoundedRect shape)
    {
        public ClipNode? Parent { get; } = parent;
        public RoundedRect Shape { get; } = shape;
        public int Depth { get; } = parent is null ? 1 : parent.Depth + 1;
    }

    // A stacking context, or a box painted as if it were one (floats, and positioned boxes with z-index auto), whose
    // own positioned descendants and stacking contexts belong to the nearest real context.
    private sealed class Context(PaintBox? owner, bool real, int z, int order)
    {
        public PaintBox? Owner { get; } = owner;
        public bool Real { get; } = real;
        public int Z { get; } = z;
        public int Order { get; } = order;
        public List<PaintBox> Blocks { get; } = [];
        public List<Context> Floats { get; } = [];
        public List<Context> Negative { get; } = [];
        public List<Context> ZeroOrAuto { get; } = [];
        public List<Context> Positive { get; } = [];
    }

    public static DisplayList Build(Fragment initialContainingBlock)
    {
        var list = new DisplayList();
        if (initialContainingBlock.Children is not [var rootPlaced, ..])
            return list;

        var root = rootPlaced.Fragment;
        var order = TreeOrder(root.Box!);
        // The root element's stacking context also holds the positioned boxes placed in the initial containing block.
        var rootBox = new PaintBox(root, rootPlaced.X, rootPlaced.Y, null);
        var rootContext = new Context(rootBox, real: true, 0, 0);
        Collect(rootContext, rootContext, rootBox, root.Children, order);
        Collect(rootContext, rootContext, new PaintBox(initialContainingBlock, 0, 0, null), initialContainingBlock.Children.Skip(1), order);

        // The root's background, or else the body's, paints the whole canvas (css-backgrounds-3 §2.11.2).
        var (canvasBox, canvasColor) = CanvasBackground(root.Box!);
        var canvas = new RectF(0, 0,
            Math.Max(initialContainingBlock.Width, rootPlaced.X + root.Width), Math.Max(initialContainingBlock.Height, rootPlaced.Y + root.Height));
        if (canvasColor.A > 0)
            list.Items.Add(new DisplayItem(DisplayItemKind.Fill, new RoundedRect(canvas, default), canvasColor));

        var emitter = new Emitter(list, canvasBox);
        emitter.Emit(rootContext);
        emitter.Finish();
        return list;
    }

    private static void Collect(Context context, Context real, PaintBox parent, IEnumerable<ChildFragment> children, Dictionary<Box, int> order)
    {
        var childClip = OverflowClip(parent) is { } shape ? new ClipNode(parent.Clip, shape) : parent.Clip;
        foreach (var child in children)
        {
            var placed = new PaintBox(child.Fragment, parent.X + child.X, parent.Y + child.Y, childClip);
            var box = placed.Box;
            var style = box.Style.Box;
            var index = order.GetValueOrDefault(box);
            if (CreatesStackingContext(box))
            {
                var z = style.ZIndex ?? 0;
                var c = new Context(placed, real: true, z, index);
                (z < 0 ? real.Negative : z > 0 ? real.Positive : real.ZeroOrAuto).Add(c);
                Collect(c, c, placed, placed.Fragment.Children, order);
            }
            else if (style.Position != Position.Static)
            {
                var c = new Context(placed, real: false, 0, index);
                real.ZeroOrAuto.Add(c);
                Collect(c, real, placed, placed.Fragment.Children, order);
            }
            else if (box.IsFloat)
            {
                var c = new Context(placed, real: false, 0, index);
                context.Floats.Add(c);
                Collect(c, real, placed, placed.Fragment.Children, order);
            }
            else
            {
                context.Blocks.Add(placed);
                Collect(context, real, placed, placed.Fragment.Children, order);
            }
        }
    }

    // https://www.w3.org/TR/CSS22/visuren.html#z-index, css-position-3, css-color-4 opacity, compositing-1 isolation.
    private static bool CreatesStackingContext(Box box)
    {
        var style = box.Style.Box;
        return box.Parent is null
            || style.Position is Position.Fixed or Position.Sticky
            || style.ZIndex is not null && (style.Position != Position.Static || box.Parent is FlexContainerBox or GridContainerBox)
            || style.Opacity < 1
            || style.Isolation == Isolation.Isolate;
    }

    // Overflow other than visible clips a box's contents to its padding box (css-overflow-3 §3); an axis left visible
    // is not clipped.
    private static RoundedRect? OverflowClip(PaintBox box)
    {
        if (box.Fragment.Box is not { } b)
            return null;
        var (x, y) = (b.Style.Box.OverflowX, b.Style.Box.OverflowY);
        if (x == Overflow.Visible && y == Overflow.Visible)
            return null;
        var border = b.Style.Border;
        var padding = BorderBox(box).Inset(border.TopWidth, border.RightWidth, border.BottomWidth, border.LeftWidth);
        if (x != Overflow.Visible && y != Overflow.Visible)
            return padding;
        const float Far = 1e7f;
        var rect = padding.Rect;
        return new RoundedRect(x == Overflow.Visible
            ? new RectF(-Far, rect.Y, 2 * Far, rect.Height)
            : new RectF(rect.X, -Far, rect.Width, 2 * Far), default);
    }

    private static RoundedRect BorderBox(PaintBox box) => new(box.Rect, Radii(box.Box.Style.Border, box.Rect));

    // Used corner radii: percentages of the border box, then scaled down together if adjacent ones overlap
    // (https://www.w3.org/TR/css-backgrounds-3/#corner-overlap).
    private static CornerRadii Radii(BorderGroup border, RectF rect)
    {
        Vector2 R(CornerRadius r) => new(r.X.Resolve(rect.Width), r.Y.Resolve(rect.Height));
        var (tl, tr, br, bl) = (R(border.TopLeftRadius), R(border.TopRightRadius), R(border.BottomRightRadius), R(border.BottomLeftRadius));
        var f = 1f;
        void Fit(float length, float sum)
        {
            if (sum > 0)
                f = Math.Min(f, length / sum);
        }
        Fit(rect.Width, tl.X + tr.X);
        Fit(rect.Width, bl.X + br.X);
        Fit(rect.Height, tl.Y + bl.Y);
        Fit(rect.Height, tr.Y + br.Y);
        return new CornerRadii(tl * f, tr * f, br * f, bl * f);
    }

    private static (Box? Owner, CssColor Color) CanvasBackground(Box root)
    {
        var rootColor = root.Style.Background.Color.Resolve(root.Style.Inherited.Color);
        if (rootColor.A > 0 || root.Node is not Element { LocalName: "html" })
            return (root, rootColor);
        var body = root.Children.FirstOrDefault(b => b.Node is Element { LocalName: "body" } e && e.Name.Namespace == Namespaces.Html);
        return body is null ? (root, rootColor) : (body, body.Style.Background.Color.Resolve(body.Style.Inherited.Color));
    }

    // Boxes in box tree order (document order), for ordering positioned boxes and stacking contexts.
    private static Dictionary<Box, int> TreeOrder(Box root)
    {
        var order = new Dictionary<Box, int>();
        var stack = new Stack<Box>();
        stack.Push(root);
        while (stack.TryPop(out var box))
        {
            order[box] = order.Count;
            for (var i = box.Children.Count - 1; i >= 0; i--)
                stack.Push(box.Children[i]);
        }
        return order;
    }

    private sealed class Emitter(DisplayList list, Box? canvasBox)
    {
        private readonly List<ClipNode> _open = [];
        private int _floor; // clips below this index belong to an enclosing opacity layer and stay open

        public void Emit(Context context)
        {
            var opacity = context.Real && context.Owner is { } owner && owner.Box.Style.Box.Opacity < 1 ? owner.Box.Style.Box.Opacity : 1;
            var floor = _floor;
            if (opacity < 1)
            {
                SetClip(context.Owner!.Clip);
                list.Items.Add(new DisplayItem(DisplayItemKind.PushOpacity, Opacity: opacity));
                _floor = _open.Count;
            }

            if (context.Owner is { } self)
                PaintBackground(self);
            foreach (var c in Sorted(context.Negative))
                Emit(c);
            foreach (var block in context.Blocks)
                PaintBackground(block);
            foreach (var c in context.Floats)
                Emit(c);
            foreach (var c in context.ZeroOrAuto.OrderBy(c => c.Order))
                Emit(c);
            foreach (var c in Sorted(context.Positive))
                Emit(c);

            if (opacity < 1)
            {
                PopTo(_floor);
                list.Items.Add(new DisplayItem(DisplayItemKind.Pop));
                _floor = floor;
            }
        }

        private static IEnumerable<Context> Sorted(List<Context> contexts) => contexts.OrderBy(c => c.Z).ThenBy(c => c.Order);

        private void PaintBackground(PaintBox box)
        {
            var style = box.Box.Style;
            if (style.Inherited.Visibility != Visibility.Visible)
                return;
            var shape = BorderBox(box);
            var border = style.Border;
            var color = box.Box == canvasBox ? CssColor.Transparent : style.Background.Color.Resolve(style.Inherited.Color);
            if (color.A > 0)
            {
                SetClip(box.Clip);
                list.Items.Add(new DisplayItem(DisplayItemKind.Fill, BackgroundArea(shape, style, box.Fragment), color));
            }
            if (border.TopWidth + border.RightWidth + border.BottomWidth + border.LeftWidth > 0)
            {
                SetClip(box.Clip);
                var current = style.Inherited.Color;
                var used = border with
                {
                    TopColor = border.TopColor.Resolve(current), RightColor = border.RightColor.Resolve(current),
                    BottomColor = border.BottomColor.Resolve(current), LeftColor = border.LeftColor.Resolve(current),
                };
                list.Items.Add(new DisplayItem(DisplayItemKind.Border, shape, Border: used));
            }
        }

        // background-clip of the bottom layer decides where the colour is painted (css-backgrounds-3 §3.10).
        // ponytail: content-box padding percentages resolve against the box's own width, not its containing block's.
        private static RoundedRect BackgroundArea(RoundedRect borderBox, ComputedStyle style, Fragment fragment)
        {
            var border = style.Border;
            var clip = style.Background.Clips[^1];
            if (clip == BackgroundBox.BorderBox)
                return borderBox;
            var padding = borderBox.Inset(border.TopWidth, border.RightWidth, border.BottomWidth, border.LeftWidth);
            if (clip != BackgroundBox.ContentBox)
                return padding;
            var s = style.Spacing;
            return padding.Inset(s.PaddingTop.Resolve(fragment.Width), s.PaddingRight.Resolve(fragment.Width),
                s.PaddingBottom.Resolve(fragment.Width), s.PaddingLeft.Resolve(fragment.Width));
        }

        // Moves from the open clips to the target chain: pops what differs, pushes what is new.
        // ponytail: a box whose chain leaves an enclosing opacity layer's clips stays clipped by them.
        private void SetClip(ClipNode? target)
        {
            var chain = new List<ClipNode>();
            for (var node = target; node is not null; node = node.Parent)
                chain.Add(node);
            chain.Reverse();
            var common = 0;
            while (common < _open.Count && common < chain.Count && _open[common] == chain[common])
                common++;
            if (common < _floor)
            {
                PopTo(_floor);
                return;
            }
            PopTo(common);
            foreach (var node in chain.Skip(common))
            {
                list.Items.Add(new DisplayItem(DisplayItemKind.PushClip, node.Shape));
                _open.Add(node);
            }
        }

        private void PopTo(int count)
        {
            while (_open.Count > count)
            {
                _open.RemoveAt(_open.Count - 1);
                list.Items.Add(new DisplayItem(DisplayItemKind.Pop));
            }
        }

        // Called at the end so the list is balanced.
        public void Finish() => PopTo(0);
    }
}
