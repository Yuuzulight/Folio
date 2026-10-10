using System.Numerics;
using Folio.Css;
using Folio.Dom;
using Folio.Layout;
using Folio.Style;

namespace Folio.Painting;

// A box's border box on the canvas, with the overflow clips it is painted under. LineEnd marks text that ends its line.
// Scale is the device pixels per CSS pixel its edges snap to (docs/study/12-painting.md), or 0 under a transform.
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

/// <summary>
/// What a <see cref="PaintOrderWalker"/> walk visits: each stacking context, with the boxes it paints in their order.
/// </summary>
internal interface IPaintVisitor
{
    /// <summary>A context starts; returning false skips it and its content (a box flattened to nothing).</summary>
    bool BeginContext(Context context);
    void EndContext(Context context);

    /// <param name="text">The text of the stacking context the box paints in, which an inline box clips to.</param>
    void VisitBackground(PaintBox box, List<PaintBox> text);
    void VisitCollapsedBorders(PaintBox table, List<PaintBox> cells);
    void VisitText(PaintBox text);
    void VisitOutline(PaintBox box);
}

/// <summary>
/// The stacking tree of a fragment tree and its paint order (CSS 2.2 Appendix E, css-position-3 stacking contexts),
/// shared by painting and hit testing: <see cref="StackingTree"/> builds the tree, <see cref="Walk"/> visits it.
/// </summary>
internal static class PaintOrderWalker
{
    /// <summary>The root element's stacking context with all it holds, or null when the document has no root box.</summary>
    /// <param name="deviceScale">Device pixels per CSS pixel, which box edges and text baselines snap to.</param>
    /// <param name="scrollOffsets">Scroll offsets of scroll containers by element node (CSSOM View / #421).</param>
    internal static Context? StackingTree(Fragment initialContainingBlock, float deviceScale = 1, IReadOnlyDictionary<ElementNode, Vector2>? scrollOffsets = null)
    {
        if (initialContainingBlock.Children is not [var rootPlaced, ..])
            return null;

        var root = rootPlaced.Fragment;
        var order = TreeOrder(root.Box!);
        // The root element's stacking context also holds the positioned boxes placed in the initial containing block.
        var viewport = new RectF(0, 0, initialContainingBlock.Width, initialContainingBlock.Height);
        var atViewport = new Surroundings(viewport, viewport, null);
        var rootBox = new PaintBox(root, rootPlaced.X, rootPlaced.Y, null, Scale: root.Box is { IsTransformed: true } ? 0 : deviceScale)
            { Around = atViewport };
        var rootContext = new Context(rootBox, real: true, 0, 0);
        // A replaced root element (the svg root of an SVG document) paints its content like any replaced box.
        if (root.Svg is not null || root.Box is ReplacedBox { Image: not null })
            rootContext.Text.Add(rootBox);
        Collect(rootContext, rootContext, rootBox, root.Children, order, scrollOffsets);
        Collect(rootContext, rootContext, new PaintBox(initialContainingBlock, 0, 0, null, Scale: deviceScale) { Around = atViewport },
            initialContainingBlock.Children.Slice(1), order, scrollOffsets);
        // The root group blends with the canvas background, which is painted outside it.
        rootContext.Isolated = false;
        return rootContext;
    }

    private static bool IsScrollContainer(Box box) =>
        box.Style.Box.OverflowX is Overflow.Hidden or Overflow.Scroll or Overflow.Auto
        || box.Style.Box.OverflowY is Overflow.Hidden or Overflow.Scroll or Overflow.Auto;

    internal static RectF PaddingBox(PaintBox box)
    {
        var border = box.Box.Style.Border;
        return box.Rect.Inset(border.TopWidth, border.RightWidth, border.BottomWidth, border.LeftWidth);
    }

    /// <summary>
    /// How far a sticky box moves at the scroll position Folio draws: up to keep its bottom inset from the
    /// scrollport's bottom, down to keep its top inset from the top, and horizontally for left/right insets,
    /// never leaving the box it sits in (https://www.w3.org/TR/css-position-3/#stickypos-insets).
    /// </summary>
    private static Vector2 StickyOffset(Box box, PaintBox placed)
    {
        var (spacing, port, limit) = (box.Style.Spacing, placed.Scrollport, placed.StickyLimit);
        var (top, bottom) = (placed.Y, placed.Y + placed.Fragment.Height);
        var (left, right) = (placed.X, placed.X + placed.Fragment.Width);
        var dy = 0f;
        if (spacing.Bottom is { Kind: SizeKind.Length } b && bottom > port.Y + port.Height - b.Length.Resolve(port.Height))
            dy = Math.Max(port.Y + port.Height - b.Length.Resolve(port.Height) - bottom, Math.Min(0, limit.Y - top));
        if (spacing.Top is { Kind: SizeKind.Length } t && top + dy < port.Y + t.Length.Resolve(port.Height))
            dy = Math.Min(port.Y + t.Length.Resolve(port.Height) - top, Math.Max(dy, limit.Y + limit.Height - bottom));

        var dx = 0f;
        if (spacing.Right is { Kind: SizeKind.Length } r && right > port.X + port.Width - r.Length.Resolve(port.Width))
            dx = Math.Max(port.X + port.Width - r.Length.Resolve(port.Width) - right, Math.Min(0, limit.X - left));
        if (spacing.Left is { Kind: SizeKind.Length } l && left + dx < port.X + l.Length.Resolve(port.Width))
            dx = Math.Min(port.X + l.Length.Resolve(port.Width) - left, Math.Max(dx, limit.X + limit.Width - right));

        return new Vector2(dx, dy);
    }

    private static bool IsWholeTranslation(Matrix3x2 m, float scale) =>
        scale > 0 && m.M11 == 1 && m.M12 == 0 && m.M21 == 0 && m.M22 == 1
        && MathF.Abs(m.M31 * scale - MathF.Round(m.M31 * scale)) < 0.001f && MathF.Abs(m.M32 * scale - MathF.Round(m.M32 * scale)) < 0.001f;

    // A single-line select is built as a block container holding its chosen option's text.
    internal static bool IsDropDown(Box box) => box is BlockContainerBox { Node: Dom.ElementNode { LocalName: "select" } };

    private static void Collect(Context context, Context real, PaintBox parent, ChildList children, Dictionary<Box, int> order, IReadOnlyDictionary<ElementNode, Vector2>? scrollOffsets)
    {
        // Content that nests deeper than the stack allows is left out rather than overflowing it, as layout does.
        if (!System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
            return;
        var childClip = OverflowClip(parent) is { } shape ? new ClipNode(parent.Clip, shape) : parent.Clip;
        var isScroller = parent.Fragment.Box is { } scrollerBox && IsScrollContainer(scrollerBox);
        var port = isScroller ? PaddingBox(parent) : parent.Scrollport;
        var scrollOffset = isScroller && parent.Fragment.Box?.Node is ElementNode scrollerEl && scrollOffsets is not null
            && scrollOffsets.TryGetValue(scrollerEl, out var offset) ? offset : Vector2.Zero;
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
            var placed = new PaintBox(child.Fragment, parent.X + child.X - scrollOffset.X, parent.Y + child.Y - scrollOffset.Y, childClip, child.Fragment == lastOnLine, scale) { Around = around };
            if (around.Cells is { } cells && child.Fragment is { PaintedBorder: not null, Box: TablePartBox { Part: TablePart.Cell } })
                cells.Add(placed);
            // A cell's content fragment shares the cell's box; the cell has already been moved.
            if (child.Fragment.Box is { Style.Box.Position: Position.Sticky } sticky && sticky != parent.Fragment.Box)
            {
                var stickyOffset = StickyOffset(sticky, placed);
                placed = placed with { X = placed.X + stickyOffset.X, Y = placed.Y + stickyOffset.Y };
            }
            // Line boxes only hold inline content; text is painted with text painting.
            if (child.Fragment.Kind == FragmentKind.Line)
            {
                Collect(context, real, placed, child.Fragment.Children, order, scrollOffsets);
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
                Collect(context, real, placed, placed.Fragment.Children, order, scrollOffsets);
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
                Collect(c, c, placed, placed.Fragment.Children, order, scrollOffsets);
            }
            else if (style.Position != Position.Static)
            {
                var c = new Context(placed, real: false, 0, index);
                real.ZeroOrAuto.Add(c);
                AddContent(c);
                Collect(c, real, placed, placed.Fragment.Children, order, scrollOffsets);
            }
            else if (box.IsFloat)
            {
                var c = new Context(placed, real: false, 0, index);
                context.Floats.Add(c);
                AddContent(c);
                Collect(c, real, placed, placed.Fragment.Children, order, scrollOffsets);
            }
            else
            {
                context.Blocks.Add(placed);
                AddContent(context);
                Collect(context, real, placed, placed.Fragment.Children, order, scrollOffsets);
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
    private static bool CreatesStackingContext(Box box)
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
    private static bool HasZIndex(Box box) =>
        box.Style.Box.ZIndex is not null && (box.Style.Box.Position != Position.Static || box.Parent is FlexContainerBox or GridContainerBox);

    // Overflow other than visible clips a box's contents to its padding box (css-overflow-3 §3); an axis left visible
    // is not clipped.
    private static RoundedRect? OverflowClip(PaintBox box)
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

    // A box's transform (css-transforms-2 §6) in canvas coordinates: its matrix is relative to the border box's
    // top-left corner, the reference box being the border box.
    // Under a parent's perspective, the parent's perspective matrix applies after the box's own transform
    // (https://www.w3.org/TR/css-transforms-2/#accumulated-3d-transformation-matrix-computation).
    // ponytail: every box flattens into its parent's plane; preserve-3d and backface-visibility come when pages need them.
    internal static Matrix4x4 Transform(PaintBox box)
    {
        var matrix = Matrix4x4.CreateTranslation(-box.X, -box.Y, 0)
            * box.Box.Style.Transform.Matrix(box.Fragment.Width, box.Fragment.Height)
            * Matrix4x4.CreateTranslation(box.X, box.Y, 0);
        return box.Perspective is { } perspective ? matrix * perspective : matrix;
    }

    // The determinant of the projective 2D transform a 3D matrix flattens to: the x, y and w rows and columns.
    internal static float Determinant2D(Matrix4x4 m) =>
        m.M11 * (m.M22 * m.M44 - m.M24 * m.M42) - m.M12 * (m.M21 * m.M44 - m.M24 * m.M41) + m.M14 * (m.M21 * m.M42 - m.M22 * m.M41);

    // Boxes in box tree order (document order), for ordering positioned boxes and stacking contexts. Boxes in inline
    // content (inline boxes, atomic inlines, floats and positioned boxes) are reached through their paragraph's items.
    private static Dictionary<Box, int> TreeOrder(Box root)
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

    /// <summary>
    /// Walks a stacking context in paint order (CSS 2.2 Appendix E): back to front for painting, or front to back
    /// (<paramref name="frontToBack"/>) for hit testing, the exact reverse. In both directions the visitor sees a
    /// context begin before its content and end after it, so it can apply the context's transform, clips and effects.
    /// </summary>
    internal static void Walk(Context context, IPaintVisitor visitor, bool frontToBack = false)
    {
        if (!visitor.BeginContext(context))
            return;
        var steps = Steps(context);
        foreach (var step in frontToBack ? steps.Reverse() : steps)
        {
            switch (step.Kind)
            {
                case StepKind.Context: Walk(step.Child!, visitor, frontToBack); break;
                case StepKind.Background: visitor.VisitBackground(step.Box!, context.Text); break;
                case StepKind.CollapsedBorders: visitor.VisitCollapsedBorders(step.Box!, context.CollapsedCells[step.Box!]); break;
                case StepKind.Text: visitor.VisitText(step.Box!); break;
                case StepKind.Outline: visitor.VisitOutline(step.Box!); break;
            }
        }
        visitor.EndContext(context);
    }

    private enum StepKind { Context, Background, CollapsedBorders, Text, Outline }

    private readonly record struct Step(StepKind Kind, PaintBox? Box = null, Context? Child = null);

    // A context's paint order, back to front: its own background, negative z, blocks, floats, inline content, z-index
    // auto and 0, positive z, then outlines over everything else (CSS 2.2 Appendix E, steps 1 to 10).
    private static IEnumerable<Step> Steps(Context context)
    {
        if (context.Owner is { } self)
            yield return new(StepKind.Background, self);
        foreach (var c in Sorted(context.Negative))
            yield return new(StepKind.Context, Child: c);
        // A table with collapsed borders paints them once all its backgrounds are painted, under any positioned cells
        // (CSS 2.2 Appendix E, step 4; css-tables-3 §6.5): when the next block is outside it, or at the end.
        var tables = new Stack<PaintBox>();
        foreach (var block in context.Blocks)
        {
            while (tables.TryPeek(out var table) && !Inside(block, table))
                yield return new(StepKind.CollapsedBorders, tables.Pop());
            yield return new(StepKind.Background, block);
            if (context.CollapsedCells.TryGetValue(block, out var cells) && cells.Count > 0)
                tables.Push(block);
        }
        while (tables.TryPop(out var table))
            yield return new(StepKind.CollapsedBorders, table);
        foreach (var c in context.Floats)
            yield return new(StepKind.Context, Child: c);
        foreach (var text in context.Text)
            yield return new(StepKind.Text, text);
        foreach (var c in context.ZeroOrAuto.OrderBy(c => c.Order))
            yield return new(StepKind.Context, Child: c);
        foreach (var c in Sorted(context.Positive))
            yield return new(StepKind.Context, Child: c);
        foreach (var box in context.Outlines)
            yield return new(StepKind.Outline, box);
    }

    private static IEnumerable<Context> Sorted(List<Context> contexts) => contexts.OrderBy(c => c.Z).ThenBy(c => c.Order);

    // Whether a box is in a table, by the tables around it (boxes in inline content have no parent box to go by).
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
