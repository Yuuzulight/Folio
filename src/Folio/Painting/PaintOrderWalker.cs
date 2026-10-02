using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Folio.Css;
using Folio.Dom;
using Folio.Layout;
using Folio.Style;

namespace Folio.Painting;

internal sealed record PaintBox(Fragment Fragment, float X, float Y, ClipNode? Clip, bool LineEnd = false, float Scale = 0)
{
    /// <summary>What the box's parent gives all its children: shared, since a large document has tens of thousands of boxes.</summary>
    public required Surroundings Around { get; init; }

    /// <summary>The padding box of the nearest scroll container (or the viewport) on the canvas, for sticky boxes.</summary>
    public RectF Scrollport => Around.Scrollport;

    /// <summary>The border box of the nearest block-level container, which a sticky box may not leave.</summary>
    public RectF StickyLimit => Around.StickyLimit;

    /// <summary>The parent's perspective matrix (perspective property) in canvas coordinates, if it has one.</summary>
    public Matrix4x4? Perspective => Around.Perspective;

    public Box Box => Fragment.Box!;

    /// <summary>The border box, each edge rounded to the nearest device pixel, so neighbours never gap or overlap.</summary>
    public RectF Rect
    {
        get
        {
            var (left, top) = (Snap(X), Snap(Y));
            return new(left, top, Snap(X + Fragment.Width) - left, Snap(Y + Fragment.Height) - top);
        }
    }

    public float Snap(float position) => Scale > 0 ? MathF.Floor(position * Scale + 0.5f) / Scale : position;
}

/// <param name="Table">The nearest table grid box around the children.</param>
/// <param name="Cells">
/// When that table's borders collapse: its cells where layout put them (before sticky positioning), whose borders it
/// paints after all its backgrounds.
/// </param>
internal sealed record Surroundings(RectF Scrollport, RectF StickyLimit, Matrix4x4? Perspective, PaintBox? Table = null, List<PaintBox>? Cells = null);

internal sealed class ClipNode(ClipNode? parent, RoundedRect shape)
{
    public ClipNode? Parent { get; } = parent;
    public RoundedRect Shape { get; } = shape;
    public int Depth { get; } = parent is null ? 1 : parent.Depth + 1;
}

// A stacking context, or a box painted as if it were one (floats, and positioned boxes with z-index auto), whose
// own positioned descendants and stacking contexts belong to the nearest real context.
internal sealed class Context(PaintBox? owner, bool real, int z, int order)
{
    public PaintBox? Owner { get; } = owner;
    public bool Real { get; } = real;
    public int Z { get; } = z;
    public int Order { get; } = order;
    public List<PaintBox> Blocks { get; } = [];

    /// <summary>The cells of the tables among <see cref="Blocks"/> whose borders collapse.</summary>
    public Dictionary<PaintBox, List<PaintBox>> CollapsedCells { get; } = new(ReferenceEqualityComparer.Instance);
    public List<Context> Floats { get; } = [];
    public List<PaintBox> Text { get; } = [];
    public List<Context> Negative { get; } = [];
    public List<Context> ZeroOrAuto { get; } = [];
    public List<Context> Positive { get; } = [];
    public List<PaintBox> Outlines { get; } = [];

    /// <summary>
    /// A box in the context blends with it: the context is an isolated group, painted into a layer of its own so
    /// the blending stops at its edge (https://drafts.csswg.org/compositing-2/#isolation).
    /// </summary>
    public bool Isolated { get; set; }
}

/// <param name="images">Loads url() images for backgrounds; without one, they are not painted.</param>
/// <param name="deviceScale">Device pixels per CSS pixel, which box edges and text baselines snap to.</param>

internal interface IPaintVisitor
{
    bool BeginContext(Context context);
    void EndContext(Context context);
    void VisitBackground(PaintBox box, List<PaintBox> text);
    void VisitCollapsedBorders(PaintBox table, List<PaintBox> cells);
    void VisitText(PaintBox text);
    void VisitOutline(PaintBox box);
}

internal static class PaintOrderWalker
{
    internal static bool IsScrollContainer(Box box) =>
        box.Style.Box.OverflowX is Overflow.Hidden or Overflow.Scroll or Overflow.Auto
        || box.Style.Box.OverflowY is Overflow.Hidden or Overflow.Scroll or Overflow.Auto;

    internal static RectF PaddingBox(PaintBox box)
    {
        var border = box.Box.Style.Border;
        return box.Rect.Inset(border.TopWidth, border.RightWidth, border.BottomWidth, border.LeftWidth);
    }

    /// <summary>
    /// How far a sticky box moves at the scroll position Folio draws (the start): up to keep its bottom inset from the
    /// scrollport's bottom, then down to keep its top inset from the top, never leaving the box it sits in
    /// (https://www.w3.org/TR/css-position-3/#stickypos-insets).
    /// </summary>
    // ponytail: vertical insets only; left and right stickiness waits for horizontal scrolling.
    internal static float StickyOffset(Box box, PaintBox placed)
    {
        var (spacing, port, limit) = (box.Style.Spacing, placed.Scrollport, placed.StickyLimit);
        var (top, bottom) = (placed.Y, placed.Y + placed.Fragment.Height);
        var dy = 0f;
        if (spacing.Bottom is { Kind: SizeKind.Length } b && bottom > port.Y + port.Height - b.Length.Resolve(port.Height))
            dy = Math.Max(port.Y + port.Height - b.Length.Resolve(port.Height) - bottom, Math.Min(0, limit.Y - top));
        if (spacing.Top is { Kind: SizeKind.Length } t && top + dy < port.Y + t.Length.Resolve(port.Height))
            dy = Math.Min(port.Y + t.Length.Resolve(port.Height) - top, Math.Max(dy, limit.Y + limit.Height - bottom));
        return dy;
    }

    internal static bool IsWholeTranslation(Matrix3x2 m, float scale) =>
        scale > 0 && m.M11 == 1 && m.M12 == 0 && m.M21 == 0 && m.M22 == 1
        && MathF.Abs(m.M31 * scale - MathF.Round(m.M31 * scale)) < 0.001f && MathF.Abs(m.M32 * scale - MathF.Round(m.M32 * scale)) < 0.001f;

    // A single-line select is built as a block container holding its chosen option's text.
    internal static bool IsDropDown(Box box) => box is BlockContainerBox { Node: Dom.ElementNode { LocalName: "select" } };

    internal static void Collect(Context context, Context real, PaintBox parent, ChildList children, Dictionary<Box, int> order)
    {
        // Content that nests deeper than the stack allows is left out rather than overflowing it, as layout does.
        if (!System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
            return;
        var childClip = OverflowClip(parent) is { } shape ? new ClipNode(parent.Clip, shape) : parent.Clip;
        var port = parent.Fragment.Box is { } scroller && IsScrollContainer(scroller) ? PaddingBox(parent) : parent.Scrollport;
        // A cell's containing block is the table, so rows and row groups pass their limit through.
        var limit = parent.Fragment.Box is TablePartBox { Part: not (TablePart.Table or TablePart.Cell or TablePart.Caption) } ? parent.StickyLimit
            : parent.Fragment.Box is BlockContainerBox or FlexContainerBox or GridContainerBox ? parent.Rect : parent.StickyLimit;
        var perspective = parent.Fragment.Box?.Style.Transform.PerspectiveMatrix(parent.Fragment.Width, parent.Fragment.Height) is { } matrix
            ? Matrix4x4.CreateTranslation(-parent.X, -parent.Y, 0) * matrix * Matrix4x4.CreateTranslation(parent.X, parent.Y, 0)
            : (Matrix4x4?)null;
        // A table's children start a list of its cells when its borders collapse (PaintCollapsedBorders).
        var table = parent.Fragment.Box is TablePartBox { Part: TablePart.Table };
        // The children share one, the parent's own when nothing changes (a line's text, a row's cells).
        var around = !table && perspective is null && parent.Perspective is null && port == parent.Scrollport && limit == parent.StickyLimit
            ? parent.Around
            : table ? new Surroundings(port, limit, perspective, parent, parent.Fragment.PaintedBorder is null ? null : [])
            : new Surroundings(port, limit, perspective, parent.Around.Table, parent.Around.Cells);
        if (table && around.Cells is { } tableCells)
            context.CollapsedCells[parent] = tableCells;
        var lastOnLine = parent.Fragment.Kind == FragmentKind.Line && parent.Fragment.Children.Count > 0 ? parent.Fragment.Children[^1].Fragment : (Fragment?)null;
        foreach (var child in children)
        {
            // Snapping stops at a box transformed by anything but a whole-pixel translation: its own geometry and its
            // subtree are drawn through the transform (docs/study/12-painting.md).
            var scale = child.Fragment.Box is { IsTransformed: true } transformed
                && !IsWholeTranslation(transformed.Style.Transform.Matrix2D(child.Fragment.Width, child.Fragment.Height), parent.Scale) ? 0 : parent.Scale;
            var placed = new PaintBox(child.Fragment, parent.X + child.X, parent.Y + child.Y, childClip, child.Fragment == lastOnLine, scale) { Around = around };
            if (around.Cells is { } cells && child.Fragment is { PaintedBorder: not null, Box: TablePartBox { Part: TablePart.Cell } })
                cells.Add(placed);
            // A cell's content fragment shares the cell's box; the cell has already been moved.
            if (child.Fragment.Box is { Style.Box.Position: Position.Sticky } sticky && sticky != parent.Fragment.Box)
                placed = placed with { Y = placed.Y + StickyOffset(sticky, placed) };
            // Line boxes only hold inline content; text is painted with text painting.
            if (child.Fragment.Kind == FragmentKind.Line)
            {
                Collect(context, real, placed, child.Fragment.Children, order);
                continue;
            }
            // A marker drawn as a shape paints with the text, like the marker text it stands for.
            if (child.Fragment.Kind == FragmentKind.Text || child.Fragment.Box is MarkerBox { Symbol: not null })
            {
                context.Text.Add(placed);
                continue;
            }
            var box = placed.Box;
            var style = box.Style.Box;
            var index = order.GetValueOrDefault(box);
            // A transformed box's outline is transformed with it, and a filtered or clipped box's is filtered or clipped
            // with it (https://drafts.csswg.org/filter-effects-1/#FilterProperty, https://drafts.csswg.org/css-masking-1/#the-clip-path),
            // so those paint in the box's own stacking context.
            var outlined = box.Style.Outline.Width > 0 && box is not TableWrapperBox;
            var ownOutline = box.IsTransformed
                || (!box.Style.Effects.Filter.IsNone || !box.Style.Effects.ClipPath.IsNone || box.Style.Mask.IsMasked) && box is not TablePartBox { Part: TablePart.Table };
            if (outlined && !ownOutline)
                real.Outlines.Add(placed);
            // Replaced content paints with the inline content, after backgrounds and floats (CSS 2.2 Appendix E, step 7).
            var content = box is ReplacedBox { Image: not null } || placed.Fragment.Svg is not null || IsDropDown(box) ? placed : null;
            // A table's positioning and stacking properties apply to its wrapper box (CSS 2 §17.4); the table grid box,
            // which shares the wrapper's style, paints as a plain block inside it.
            if (box is TablePartBox { Part: TablePart.Table })
            {
                context.Blocks.Add(placed);
                Collect(context, real, placed, placed.Fragment.Children, order);
            }
            else if (CreatesStackingContext(box))
            {
                // A stacking context made by opacity, a transform or the like on a box z-index does not apply to sits at 0.
                var z = HasZIndex(box) ? style.ZIndex!.Value : 0;
                var c = new Context(placed, real: true, z, index);
                (z < 0 ? real.Negative : z > 0 ? real.Positive : real.ZeroOrAuto).Add(c);
                if (box.Style.Effects.MixBlendMode != Style.BlendMode.Normal)
                    real.Isolated = true;
                if (outlined && ownOutline)
                    c.Outlines.Add(placed);
                AddContent(c);
                Collect(c, c, placed, placed.Fragment.Children, order);
            }
            else if (style.Position != Position.Static)
            {
                var c = new Context(placed, real: false, 0, index);
                real.ZeroOrAuto.Add(c);
                AddContent(c);
                Collect(c, real, placed, placed.Fragment.Children, order);
            }
            else if (box.IsFloat)
            {
                var c = new Context(placed, real: false, 0, index);
                context.Floats.Add(c);
                AddContent(c);
                Collect(c, real, placed, placed.Fragment.Children, order);
            }
            else
            {
                context.Blocks.Add(placed);
                AddContent(context);
                Collect(context, real, placed, placed.Fragment.Children, order);
            }

            void AddContent(Context target)
            {
                if (content is not null)
                    target.Text.Add(content);
            }
        }
    }

    // https://www.w3.org/TR/CSS22/visuren.html#z-index, css-position-3, css-color-4 opacity, compositing-1 isolation,
    // css-transforms-2 (any transform property other than none), filter-effects-1 filter, filter-effects-2 backdrop-filter
    // compositing-2 mix-blend-mode, and css-masking-1 clip-path and mask.
    // The root's context is made by Build. Boxes in inline content (inline boxes, floats and atomic inlines found
    // there) have no parent box, so a missing parent says nothing here.
    internal static bool CreatesStackingContext(Box box)
    {
        var style = box.Style.Box;
        return style.Position is Position.Fixed or Position.Sticky
            || HasZIndex(box)
            || style.Opacity < 1
            || style.Isolation == Isolation.Isolate
            || box.IsTransformed
            || box.Style.Transform.Perspective is not null && box is not InlineBox
            || !box.Style.Effects.Filter.IsNone
            || !box.Style.Effects.BackdropFilter.IsNone
            || box.Style.Effects.MixBlendMode != Style.BlendMode.Normal
            || !box.Style.Effects.ClipPath.IsNone
            || box.Style.Mask.IsMasked;
    }

    // z-index applies to positioned boxes and to flex and grid items.
    internal static bool HasZIndex(Box box) =>
        box.Style.Box.ZIndex is not null && (box.Style.Box.Position != Position.Static || box.Parent is FlexContainerBox or GridContainerBox);

    // Overflow other than visible clips a box's contents to its padding box (css-overflow-3 §3); an axis left visible
    // is not clipped.
    internal static RoundedRect? OverflowClip(PaintBox box)
    {
        if (box.Fragment.Box is not { } b || box.Fragment.Kind != FragmentKind.Box)
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

    internal static RectF Snapped(PaintBox box, RectF r) =>
        new(box.Snap(r.X), box.Snap(r.Y), box.Snap(r.X + r.Width) - box.Snap(r.X), box.Snap(r.Y + r.Height) - box.Snap(r.Y));

    internal static RoundedRect BorderBox(PaintBox box)
    {
        var border = box.Box.Style.Border;
        return new(box.Rect, Radii(border.TopLeftRadius, border.TopRightRadius, border.BottomRightRadius, border.BottomLeftRadius, box.Rect));
    }

    // Used corner radii: percentages of the rectangle, then scaled down together if adjacent ones overlap
    // (https://www.w3.org/TR/css-backgrounds-3/#corner-overlap).
    internal static CornerRadii Radii(CornerRadius topLeft, CornerRadius topRight, CornerRadius bottomRight, CornerRadius bottomLeft, RectF rect)
    {
        Vector2 R(CornerRadius r) => new(r.X.Resolve(rect.Width), r.Y.Resolve(rect.Height));
        var (tl, tr, br, bl) = (R(topLeft), R(topRight), R(bottomRight), R(bottomLeft));
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

    // The body's background moves to the canvas only when the root has none: a transparent colour and no images.
    internal static (Box? Owner, CssColor Color) CanvasBackground(Box root)
    {
        var rootColor = root.Style.Background.Color.Resolve(root.Style.Inherited.Color);
        if (rootColor.A > 0 || root.Style.Background.Images.Any(i => i is not NoImage) || root.Node is not ElementNode { LocalName: "html" })
            return (root, rootColor);
        var body = root.Children.FirstOrDefault(b => b.Node is ElementNode { LocalName: "body" } e && e.Name.Namespace == Namespaces.Html);
        return body is null ? (root, rootColor) : (body, body.Style.Background.Color.Resolve(body.Style.Inherited.Color));
    }

    // Boxes in box tree order (document order), for ordering positioned boxes and stacking contexts. Boxes in inline
    // content (inline boxes, atomic inlines, floats and positioned boxes) are reached through their paragraph's items.
    internal static Dictionary<Box, int> TreeOrder(Box root)
    {
        var order = new Dictionary<Box, int>();
        var stack = new Stack<Box>();
        stack.Push(root);
        while (stack.TryPop(out var box))
        {
            if (!order.TryAdd(box, order.Count))
                continue;
            var inline = box is BlockContainerBox { Inline: { } context } ? context.Items : null;
            for (var i = (inline?.Count ?? 0) - 1; i >= 0; i--)
            {
                if (inline![i] is { Box: { } item, Kind: not InlineItemKind.CloseBox })
                    stack.Push(item);
            }
            for (var i = box.Children.Count - 1; i >= 0; i--)
                stack.Push(box.Children[i]);
        }
        return order;
    }



    // Traversal logic
    internal static void Walk(Context context, IPaintVisitor visitor, bool reverse = false)
    {
        if (!visitor.BeginContext(context))
            return;
        if (!reverse)
        {
            if (context.Owner is { } self)
                visitor.VisitBackground(self, context.Text);
            foreach (var c in Sorted(context.Negative))
                Walk(c, visitor, reverse);
            
            var tables = new Stack<PaintBox>();
            foreach (var block in context.Blocks)
            {
                while (tables.TryPeek(out var table) && !Inside(block, table))
                    visitor.VisitCollapsedBorders(tables.Pop(), context.CollapsedCells[tables.Peek()]);
                
                visitor.VisitBackground(block, context.Text);
                if (context.CollapsedCells.TryGetValue(block, out var cells) && cells.Count > 0)
                    tables.Push(block);
            }
            while (tables.TryPop(out var table))
                visitor.VisitCollapsedBorders(table, context.CollapsedCells[table]);
            
            foreach (var c in context.Floats)
                Walk(c, visitor, reverse);
            foreach (var text in context.Text)
                visitor.VisitText(text);
            foreach (var c in context.ZeroOrAuto.OrderBy(c => c.Order))
                Walk(c, visitor, reverse);
            foreach (var c in Sorted(context.Positive))
                Walk(c, visitor, reverse);
            foreach (var box in context.Outlines)
                visitor.VisitOutline(box);
        }
        else
        {
            // Reverse (front to back)
            foreach (var box in context.Outlines.AsEnumerable().Reverse())
                visitor.VisitOutline(box);
            foreach (var c in Sorted(context.Positive).Reverse())
                Walk(c, visitor, reverse);
            foreach (var c in context.ZeroOrAuto.OrderByDescending(c => c.Order))
                Walk(c, visitor, reverse);
            foreach (var text in context.Text.AsEnumerable().Reverse())
                visitor.VisitText(text);
            foreach (var c in context.Floats.AsEnumerable().Reverse())
                Walk(c, visitor, reverse);

            var blocksRev = context.Blocks.AsEnumerable().Reverse().ToList();
            var tables = new Stack<PaintBox>();
            // For reverse collapsed borders, the logic is trickier since tables can be nested.
            // But hit testing doesn't care about borders. 
            // So we just visit backgrounds.
            foreach (var block in blocksRev)
            {
                while (tables.TryPeek(out var table) && !Inside(block, table))
                    visitor.VisitCollapsedBorders(tables.Pop(), context.CollapsedCells[tables.Peek()]);
                
                if (context.CollapsedCells.TryGetValue(block, out var cells) && cells.Count > 0)
                    tables.Push(block);
                visitor.VisitBackground(block, context.Text);
            }
            while (tables.TryPop(out var table))
                visitor.VisitCollapsedBorders(table, context.CollapsedCells[table]);

            foreach (var c in Sorted(context.Negative).Reverse())
                Walk(c, visitor, reverse);
            if (context.Owner is { } selfReverse)
                visitor.VisitBackground(selfReverse, context.Text);
        }
        visitor.EndContext(context);
    }

    private static IEnumerable<Context> Sorted(List<Context> contexts) => contexts.OrderBy(c => c.Z).ThenBy(c => c.Order);

    private static bool Inside(PaintBox box, PaintBox table)
    {
        for (var t = box.Around.Table; t is not null; t = t.Around.Table)
        {
            if (ReferenceEquals(t, table))
                return true;
        }
        return false;
    }
}
