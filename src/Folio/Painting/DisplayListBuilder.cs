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
// ponytail: M1 paints background colours, gradients and url() images, borders, text and its decorations, and images;
// background-attachment: fixed paints like scroll.
internal static class DisplayListBuilder
{
    // A box's border box on the canvas, with the overflow clips it is painted under. LineEnd marks text that ends its line.
    // Scale is the device pixels per CSS pixel its edges snap to (docs/study/12-painting.md), or 0 under a transform.
    private sealed record PaintBox(Fragment Fragment, float X, float Y, ClipNode? Clip, bool LineEnd = false, float Scale = 0)
    {
        /// <summary>The padding box of the nearest scroll container (or the viewport) on the canvas, for sticky boxes.</summary>
        public RectF Scrollport { get; init; }

        /// <summary>The border box of the nearest block-level container, which a sticky box may not leave.</summary>
        public RectF StickyLimit { get; init; }

        /// <summary>The parent's perspective matrix (perspective property) in canvas coordinates, if it has one.</summary>
        public Matrix4x4? Perspective { get; init; }

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
    public static DisplayList Build(Fragment initialContainingBlock, Imaging.ImageLoader? images = null, float deviceScale = 1)
    {
        var list = new DisplayList();
        if (initialContainingBlock.Children is not [var rootPlaced, ..])
            return list;

        var root = rootPlaced.Fragment;
        var order = TreeOrder(root.Box!);
        // The root element's stacking context also holds the positioned boxes placed in the initial containing block.
        var viewport = new RectF(0, 0, initialContainingBlock.Width, initialContainingBlock.Height);
        var rootBox = new PaintBox(root, rootPlaced.X, rootPlaced.Y, null, Scale: root.Box is { IsTransformed: true } ? 0 : deviceScale)
            { Scrollport = viewport, StickyLimit = viewport };
        var rootContext = new Context(rootBox, real: true, 0, 0);
        // A replaced root element (the svg root of an SVG document) paints its content like any replaced box.
        if (root.Svg is not null || root.Box is ReplacedBox { Image: not null })
            rootContext.Text.Add(rootBox);
        Collect(rootContext, rootContext, rootBox, root.Children, order);
        Collect(rootContext, rootContext, new PaintBox(initialContainingBlock, 0, 0, null, Scale: deviceScale) { Scrollport = viewport, StickyLimit = viewport },
            initialContainingBlock.Children.Skip(1), order);
        // The root group blends with the canvas background, which is painted outside it.
        rootContext.Isolated = false;

        // The root's background, or else the body's, paints the whole canvas (css-backgrounds-3 §2.11.2).
        var (canvasBox, canvasColor) = CanvasBackground(root.Box!);
        var canvas = new RectF(0, 0,
            Math.Max(initialContainingBlock.Width, rootPlaced.X + root.Width), Math.Max(initialContainingBlock.Height, rootPlaced.Y + root.Height));
        if (canvasColor.A > 0)
            list.Items.Add(new DisplayItem(DisplayItemKind.Fill, new RoundedRect(canvas, default), canvasColor));

        var emitter = new Emitter(list, canvasBox, images);
        // Its images are placed as for the root element and painted over the whole canvas.
        if (canvasBox is not null)
            emitter.PaintBackgroundImages(canvasBox.Style, rootBox, BorderBox(rootBox), new RoundedRect(canvas, default));
        emitter.Emit(rootContext);
        emitter.Finish();
        return list;
    }

    private static bool IsScrollContainer(Box box) =>
        box.Style.Box.OverflowX is Overflow.Hidden or Overflow.Scroll or Overflow.Auto
        || box.Style.Box.OverflowY is Overflow.Hidden or Overflow.Scroll or Overflow.Auto;

    private static RectF PaddingBox(PaintBox box)
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
    private static float StickyOffset(Box box, PaintBox placed)
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

    private static bool IsWholeTranslation(Matrix3x2 m, float scale) =>
        scale > 0 && m.M11 == 1 && m.M12 == 0 && m.M21 == 0 && m.M22 == 1
        && MathF.Abs(m.M31 * scale - MathF.Round(m.M31 * scale)) < 0.001f && MathF.Abs(m.M32 * scale - MathF.Round(m.M32 * scale)) < 0.001f;

    // A single-line select is built as a block container holding its chosen option's text.
    private static bool IsDropDown(Box box) => box is BlockContainerBox { Node: Dom.ElementNode { LocalName: "select" } };

    private static void Collect(Context context, Context real, PaintBox parent, IEnumerable<ChildFragment> children, Dictionary<Box, int> order)
    {
        var childClip = OverflowClip(parent) is { } shape ? new ClipNode(parent.Clip, shape) : parent.Clip;
        foreach (var child in children)
        {
            // Snapping stops at a box transformed by anything but a whole-pixel translation: its own geometry and its
            // subtree are drawn through the transform (docs/study/12-painting.md).
            var scale = child.Fragment.Box is { IsTransformed: true } transformed
                && !IsWholeTranslation(transformed.Style.Transform.Matrix2D(child.Fragment.Width, child.Fragment.Height), parent.Scale) ? 0 : parent.Scale;
            var placed = new PaintBox(child.Fragment, parent.X + child.X, parent.Y + child.Y, childClip, Scale: scale)
            {
                Scrollport = parent.Fragment.Box is { } scroller && IsScrollContainer(scroller) ? PaddingBox(parent) : parent.Scrollport,
                // A cell's containing block is the table, so rows and row groups pass their limit through.
                StickyLimit = parent.Fragment.Box is TablePartBox { Part: not (TablePart.Table or TablePart.Cell or TablePart.Caption) } ? parent.StickyLimit
                    : parent.Fragment.Box is BlockContainerBox or FlexContainerBox or GridContainerBox ? parent.Rect : parent.StickyLimit,
                Perspective = parent.Fragment.Box?.Style.Transform.PerspectiveMatrix(parent.Fragment.Width, parent.Fragment.Height) is { } perspective
                    ? Matrix4x4.CreateTranslation(-parent.X, -parent.Y, 0) * perspective * Matrix4x4.CreateTranslation(parent.X, parent.Y, 0)
                    : null,
            };
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
                context.Text.Add(parent.Fragment.Kind == FragmentKind.Line && child.Fragment == parent.Fragment.Children[^1].Fragment
                    ? placed with { LineEnd = true } : placed);
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

    private static RectF Snapped(PaintBox box, RectF r) =>
        new(box.Snap(r.X), box.Snap(r.Y), box.Snap(r.X + r.Width) - box.Snap(r.X), box.Snap(r.Y + r.Height) - box.Snap(r.Y));

    private static RoundedRect BorderBox(PaintBox box)
    {
        var border = box.Box.Style.Border;
        return new(box.Rect, Radii(border.TopLeftRadius, border.TopRightRadius, border.BottomRightRadius, border.BottomLeftRadius, box.Rect));
    }

    // Used corner radii: percentages of the rectangle, then scaled down together if adjacent ones overlap
    // (https://www.w3.org/TR/css-backgrounds-3/#corner-overlap).
    private static CornerRadii Radii(CornerRadius topLeft, CornerRadius topRight, CornerRadius bottomRight, CornerRadius bottomLeft, RectF rect)
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
    private static (Box? Owner, CssColor Color) CanvasBackground(Box root)
    {
        var rootColor = root.Style.Background.Color.Resolve(root.Style.Inherited.Color);
        if (rootColor.A > 0 || root.Style.Background.Images.Any(i => i is not NoImage) || root.Node is not ElementNode { LocalName: "html" })
            return (root, rootColor);
        var body = root.Children.FirstOrDefault(b => b.Node is ElementNode { LocalName: "body" } e && e.Name.Namespace == Namespaces.Html);
        return body is null ? (root, rootColor) : (body, body.Style.Background.Color.Resolve(body.Style.Inherited.Color));
    }

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

    private sealed class Emitter(DisplayList list, Box? canvasBox, Imaging.ImageLoader? images)
    {
        private readonly List<ClipNode> _open = [];
        private int _floor; // clips below this index belong to an enclosing opacity layer and stay open

        public void Emit(Context context)
        {
            var owner = context.Real ? context.Owner : null;
            // A list that references SVG filter elements is a layer per filter, laid out with the box; its opacity() stays a filter.
            var references = owner is not null && owner.Box.Style.Effects.Filter.HasReference;
            var svgFilters = references && owner!.Fragment.SvgFilters is { } chain ? SvgFilterPrimitives.Of(chain) : null;
            var (filters, filterOpacity) = owner is null || references ? (null, 1) : FilterPrimitives.ForLayer(owner.Box.Style.Effects.Filter, owner.Box.Style.Inherited.Color);
            var opacity = (owner is not null && owner.Box.Style.Box.Opacity < 1 ? owner.Box.Style.Box.Opacity : 1) * filterOpacity;
            var backdrop = owner is null ? null : FilterPrimitives.Of(owner.Box.Style.Effects.BackdropFilter, owner.Box.Style.Inherited.Color);
            var blend = owner is null ? BlendMode.Normal : Blend(owner.Box.Style.Effects.MixBlendMode);
            var mask = owner is not null && owner.Box.Style.Mask.IsMasked ? owner.Box.Style.Mask : null;
            var layered = opacity < 1 || filters is not null || svgFilters is not null || backdrop is not null || blend != BlendMode.Normal || context.Isolated || mask is not null;
            // A mask applies after the filter and before opacity, so with both the filter gets a layer of its own inside.
            var innerFilter = mask is not null && filters is not null;
            var transform = owner is not null && owner.Box.IsTransformed ? Transform(owner) : (Matrix4x4?)null;
            // A transform that cannot be inverted flattens the box to nothing: it and its content are not displayed
            // (https://www.w3.org/TR/css-transforms-1/#transform-function-lists).
            if (transform is { } singular && Determinant2D(singular) == 0)
                return;
            // A url() reference to an SVG clipPath clips with its path when it is one plain shape; any other region is a
            // mask drawn over the box in a layer of its own. A reference to anything else clips nothing.
            // ponytail: a backdrop filter under such a mask sees only that layer, not what is behind the box.
            var svgClip = owner?.Fragment.SvgClip;
            var clipOrigin = owner is null ? default : new Vector2(BorderBox(owner).Rect.X, BorderBox(owner).Rect.Y);
            var clipPath = owner is null || owner.Box.Style.Effects.ClipPath.IsNone ? (DisplayItem?)null
                : owner.Box.Style.Effects.ClipPath.Url is null ? ClipPathItem(owner, owner.Box.Style.Effects.ClipPath)
                : svgClip is null ? null : SvgPainter.ClipItem(svgClip, clipOrigin);
            var clipMask = clipPath is null && owner?.Box.Style.Effects.ClipPath.Url is not null ? svgClip : null;
            var grouped = layered || transform is not null || clipPath is not null || clipMask is not null;
            var floor = _floor;
            // The clips outside stay open under the group; the transform, the clip path and the layer apply to the box and
            // all it holds. The clip path is in the box's coordinates and clips what the layer composites.
            // The layer filters it, then applies opacity (https://drafts.csswg.org/filter-effects-1/#placement) and blends
            // it; a backdrop filter is clipped to the border box (https://drafts.csswg.org/filter-effects-2/#backdrop-filter-operation).
            if (grouped)
            {
                SetClip(owner!.Clip);
                if (transform is { } matrix)
                    list.Items.Add(matrix is { M14: 0, M24: 0, M44: 1 }
                        ? new DisplayItem(DisplayItemKind.PushTransform, Transform: new Matrix3x2(matrix.M11, matrix.M12, matrix.M21, matrix.M22, matrix.M41, matrix.M42))
                        : new DisplayItem(DisplayItemKind.PushTransform, Projection: matrix));
                if (clipPath is { } clip)
                    list.Items.Add(clip);
                else if (clipMask is not null)
                    list.Items.Add(new DisplayItem(DisplayItemKind.PushLayer));
                if (layered)
                    list.Items.Add(new DisplayItem(DisplayItemKind.PushLayer, backdrop is null ? default : BorderBox(owner), Opacity: opacity,
                        Filters: innerFilter ? null : filters, Backdrop: backdrop, Blend: blend));
                if (innerFilter)
                    list.Items.Add(new DisplayItem(DisplayItemKind.PushLayer, Filters: filters));
                if (svgFilters is not null)
                {
                    // The filters are in the border box's coordinates: the layers start there, the content goes back.
                    list.Items.Add(new DisplayItem(DisplayItemKind.PushTransform, Transform: Matrix3x2.CreateTranslation(clipOrigin)));
                    for (var i = svgFilters.Count - 1; i >= 0; i--)
                        list.Items.Add(new DisplayItem(DisplayItemKind.PushLayer, Filters: svgFilters[i]));
                    list.Items.Add(new DisplayItem(DisplayItemKind.PushTransform, Transform: Matrix3x2.CreateTranslation(-clipOrigin)));
                }
                _floor = _open.Count;
            }

            if (context.Owner is { } self)
                PaintBackground(self, context.Text);
            foreach (var c in Sorted(context.Negative))
                Emit(c);
            foreach (var block in context.Blocks)
                PaintBackground(block, context.Text);
            foreach (var c in context.Floats)
                Emit(c);
            foreach (var text in context.Text)
                PaintText(text);
            foreach (var c in context.ZeroOrAuto.OrderBy(c => c.Order))
                Emit(c);
            foreach (var c in Sorted(context.Positive))
                Emit(c);
            // Outlines last, over everything else in the stacking context (CSS 2.2 Appendix E, step 10).
            foreach (var box in context.Outlines)
                PaintOutline(box);

            if (grouped)
            {
                PopTo(_floor);
                if (innerFilter)
                    list.Items.Add(new DisplayItem(DisplayItemKind.Pop));
                for (var i = 0; svgFilters is not null && i < svgFilters.Count + 2; i++)
                    list.Items.Add(new DisplayItem(DisplayItemKind.Pop));
                if (mask is not null)
                    PaintMask(owner!, mask);
                if (layered)
                    list.Items.Add(new DisplayItem(DisplayItemKind.Pop));
                if (clipMask is not null)
                    SvgPainter.PaintClipMask(clipMask, clipOrigin, list.Items);
                if (clipPath is not null || clipMask is not null)
                    list.Items.Add(new DisplayItem(DisplayItemKind.Pop));
                if (transform is not null)
                    list.Items.Add(new DisplayItem(DisplayItemKind.Pop));
                _floor = floor;
            }
        }

        // A box's transform (css-transforms-2 §6) in canvas coordinates: its matrix is relative to the border box's
        // top-left corner, the reference box being the border box.
        // Under a parent's perspective, the parent's perspective matrix applies after the box's own transform
        // (https://www.w3.org/TR/css-transforms-2/#accumulated-3d-transformation-matrix-computation).
        // ponytail: every box flattens into its parent's plane; preserve-3d and backface-visibility come when pages need them.
        private static Matrix4x4 Transform(PaintBox box)
        {
            var matrix = Matrix4x4.CreateTranslation(-box.X, -box.Y, 0)
                * box.Box.Style.Transform.Matrix(box.Fragment.Width, box.Fragment.Height)
                * Matrix4x4.CreateTranslation(box.X, box.Y, 0);
            return box.Perspective is { } perspective ? matrix * perspective : matrix;
        }

        // The determinant of the projective 2D transform a 3D matrix flattens to: the x, y and w rows and columns.
        private static float Determinant2D(Matrix4x4 m) =>
            m.M11 * (m.M22 * m.M44 - m.M24 * m.M42) - m.M12 * (m.M21 * m.M44 - m.M24 * m.M41) + m.M14 * (m.M21 * m.M42 - m.M22 * m.M41);

        private static BlendMode Blend(Style.BlendMode mode) => Enum.Parse<BlendMode>(mode.ToString());

        /// <summary>
        /// A clip-path as a clip item (https://drafts.csswg.org/css-masking-1/#the-clip-path): a basic shape resolved
        /// against its reference box (https://drafts.csswg.org/css-shapes-1/#basic-shape-functions), or the box's own
        /// shape with its corners. Circles, ellipses and insets are rounded rectangles; polygons and paths are paths.
        /// </summary>
        private static DisplayItem ClipPathItem(PaintBox box, ClipPath clip)
        {
            var reference = ReferenceBox(box, clip.Box ?? GeometryBox.BorderBox);
            var r = reference.Rect;
            var rule = FillRule.NonZero;
            PathData path;
            switch (clip.Shape)
            {
                case InsetShape i:
                    var inset = r.Inset(i.Top.Resolve(r.Height), i.Right.Resolve(r.Width), i.Bottom.Resolve(r.Height), i.Left.Resolve(r.Width));
                    return new DisplayItem(DisplayItemKind.PushClip, new RoundedRect(inset, Radii(i.TopLeft, i.TopRight, i.BottomRight, i.BottomLeft, inset)));
                case EllipseShape e:
                    var (rx, ry) = EllipseRadii(e, r.Width, r.Height);
                    var (cx, cy) = (r.X + e.Center.X.Resolve(r.Width), r.Y + e.Center.Y.Resolve(r.Height));
                    var corner = new Vector2(rx, ry);
                    return new DisplayItem(DisplayItemKind.PushClip, new RoundedRect(new RectF(cx - rx, cy - ry, 2 * rx, 2 * ry), new CornerRadii(corner, corner, corner, corner)));
                case PolygonShape p:
                    path = new PathData();
                    for (var n = 0; n < p.Points.Count; n++)
                    {
                        var (x, y) = (r.X + p.Points[n].X.Resolve(r.Width), r.Y + p.Points[n].Y.Resolve(r.Height));
                        if (n == 0)
                            path.MoveTo(x, y);
                        else
                            path.LineTo(x, y);
                    }
                    path.Close();
                    rule = p.EvenOdd ? FillRule.EvenOdd : FillRule.NonZero;
                    break;
                case PathShape d:
                    path = new PathData();
                    var origin = new Vector2(r.X, r.Y);
                    foreach (var segment in d.Segments)
                    {
                        _ = segment.Verb switch
                        {
                            'M' => path.MoveTo(segment.P1.X + r.X, segment.P1.Y + r.Y),
                            'L' => path.LineTo(segment.P1.X + r.X, segment.P1.Y + r.Y),
                            'C' => path.CubicTo(segment.P1 + origin, segment.P2 + origin, segment.P3 + origin),
                            _ => path.Close(),
                        };
                    }
                    rule = d.EvenOdd ? FillRule.EvenOdd : FillRule.NonZero;
                    break;
                default:
                    return new DisplayItem(DisplayItemKind.PushClip, reference);
            }
            return new DisplayItem(DisplayItemKind.PushClip, reference, Path: path, Rule: rule);
        }

        // The radii of circle() and ellipse() in a reference box of this size. A percentage refers to the box's width or
        // height, or for a circle to its diagonal divided by the square root of 2; closest-side and farthest-side
        // measure to the nearest or furthest edge, a circle's to any of the four.
        private static (float X, float Y) EllipseRadii(EllipseShape e, float width, float height)
        {
            var (cx, cy) = (e.Center.X.Resolve(width), e.Center.Y.Resolve(height));
            float Side(bool farthest, float a, float b) => farthest ? Math.Max(Math.Abs(a), Math.Abs(b)) : Math.Min(Math.Abs(a), Math.Abs(b));
            if (e.RadiusY is not { } radiusY)
            {
                var radius = e.RadiusX.Length is { } length ? length.Resolve(MathF.Sqrt((width * width + height * height) / 2))
                    : e.RadiusX.FarthestSide ? Math.Max(Side(true, cx, width - cx), Side(true, cy, height - cy))
                    : Math.Min(Side(false, cx, width - cx), Side(false, cy, height - cy));
                return (radius, radius);
            }
            return (e.RadiusX.Length?.Resolve(width) ?? Side(e.RadiusX.FarthestSide, cx, width - cx),
                    radiusY.Length?.Resolve(height) ?? Side(radiusY.FarthestSide, cy, height - cy));
        }

        // A reference box with its corners (https://drafts.csswg.org/css-masking-1/#typedef-geometry-box): for CSS boxes,
        // fill-box is the content box and stroke-box and view-box the border box. The margin box's corners grow by the
        // margins.
        // ponytail: the margin box's top and bottom margins are the computed ones, percentages against the box's own
        // width; collapsed and auto margins are not looked up.
        private static RoundedRect ReferenceBox(PaintBox box, GeometryBox which)
        {
            var borderBox = BorderBox(box);
            var style = box.Box.Style;
            switch (which)
            {
                case GeometryBox.PaddingBox:
                    return Area(borderBox, style, box.Fragment, BackgroundBox.PaddingBox);
                case GeometryBox.ContentBox or GeometryBox.FillBox:
                    return Area(borderBox, style, box.Fragment, BackgroundBox.ContentBox);
                case GeometryBox.MarginBox:
                    float M(SizeValue margin) => margin.Kind == SizeKind.Length ? margin.Length.Resolve(box.Fragment.Width) : 0;
                    var (top, right, bottom, left) = (M(style.Spacing.MarginTop), box.Fragment.MarginRight, M(style.Spacing.MarginBottom), box.Fragment.MarginLeft);
                    static Vector2 Grown(Vector2 r, float x, float y) => r == Vector2.Zero ? r : Vector2.Max(r + new Vector2(x, y), Vector2.Zero);
                    var c = borderBox.Radii;
                    return new RoundedRect(borderBox.Rect.Inset(-top, -right, -bottom, -left),
                        new CornerRadii(Grown(c.TopLeft, left, top), Grown(c.TopRight, right, top), Grown(c.BottomRight, right, bottom), Grown(c.BottomLeft, left, bottom)));
                default:
                    return borderBox;
            }
        }

        private static IEnumerable<Context> Sorted(List<Context> contexts) => contexts.OrderBy(c => c.Z).ThenBy(c => c.Order);

        /// <param name="text">The text of the stacking context the box paints in, which an inline box clips to.</param>
        private void PaintBackground(PaintBox box, List<PaintBox> text)
        {
            var style = box.Box.Style;
            // A table wrapper shares the table's style; the table grid box inside it paints the table.
            if (style.Inherited.Visibility != Visibility.Visible || box.Box is TableWrapperBox || box.Fragment.SkipsDecorations)
                return;
            var shape = BorderBox(box);
            var border = box.Fragment.PaintedBorder ?? style.Border;
            var color = box.Box == canvasBox ? CssColor.Transparent : style.Background.Color.Resolve(style.Inherited.Color);
            // Outer shadows go under the background, inset ones over it and under the border (css-backgrounds-3 §7.1).
            if (style.Shadows.Box.Count > 0)
                PaintBoxShadows(box, shape, border, inset: false);
            // Blended background layers blend with each other and the colour only, in an isolated group
            // (https://drafts.csswg.org/compositing-2/#background-blend-mode).
            var modes = style.Effects.BackgroundBlendModes;
            var blended = box.Box != canvasBox && style.Background.Images.Where((image, i) => image is GradientImage or UrlImage && modes[i % modes.Count] != Style.BlendMode.Normal).Any();
            // background-clip: text paints the background only inside the glyphs of the box's text and its in-flow
            // descendants' (css-backgrounds-4 §3.1): the background goes into a layer that the glyphs then mask.
            // ponytail: when any layer clips to text, the colour and every layer do.
            var clipsToText = box.Box != canvasBox && style.Background.Clips.Contains(BackgroundBox.Text);
            if (clipsToText)
            {
                SetClip(box.Clip);
                list.Items.Add(new DisplayItem(DisplayItemKind.PushLayer));
            }
            if (blended)
            {
                SetClip(box.Clip);
                list.Items.Add(new DisplayItem(DisplayItemKind.PushLayer));
            }
            if (color.A > 0)
            {
                SetClip(box.Clip);
                list.Items.Add(new DisplayItem(DisplayItemKind.Fill, BackgroundArea(shape, style, box.Fragment), color));
            }
            if (box.Box != canvasBox)
                PaintBackgroundImages(style, box, shape);
            if (blended)
                list.Items.Add(new DisplayItem(DisplayItemKind.Pop));
            if (clipsToText)
            {
                // The glyphs, drawn together into a layer that keeps the background only where they are.
                list.Items.Add(new DisplayItem(DisplayItemKind.PushLayer, Blend: BlendMode.DestinationIn));
                // An inline box's text is not inside its fragment but beside it on its lines.
                var glyphText = box.Box is InlineBox inline ? text.Where(t => IsWithin(t.Fragment.Text?.Inline, inline)) : TextIn(box);
                foreach (var run in glyphText)
                {
                    if (GlyphsOf(run) is { } glyphs)
                        list.Items.Add(new DisplayItem(DisplayItemKind.Glyphs, Color: CssColor.Black, Glyphs: glyphs));
                }
                list.Items.Add(new DisplayItem(DisplayItemKind.Pop));
                list.Items.Add(new DisplayItem(DisplayItemKind.Pop));
            }
            if (style.Shadows.Box.Count > 0)
                PaintBoxShadows(box, shape, border, inset: true);
            // A border image that can be drawn replaces the border styles (collapsed table borders have none).
            if (box.Fragment.PaintedBorder is null && PaintBorderImage(box, border, style.BorderImage, "border-image-source"))
                return;
            if (border.TopWidth + border.RightWidth + border.BottomWidth + border.LeftWidth > 0)
            {
                SetClip(box.Clip);
                var current = style.Inherited.Color;
                var used = border with
                {
                    TopColor = border.TopColor.Resolve(current), RightColor = border.RightColor.Resolve(current),
                    BottomColor = border.BottomColor.Resolve(current), LeftColor = border.LeftColor.Resolve(current),
                };
                // A collapsed table border is centred on the cell's edges, half outside it, and has no radii; its edges snap
                // to device pixels like a box's, so a 1px border is one solid pixel and neighbours share it exactly.
                var borderShape = box.Fragment.PaintedBorder is not null && box.Box is TablePartBox { Part: TablePart.Cell }
                    ? new RoundedRect(Snapped(box, box.Rect.Inset(-border.TopWidth / 2, -border.RightWidth / 2, -border.BottomWidth / 2, -border.LeftWidth / 2)), default)
                    : shape;
                list.Items.Add(new DisplayItem(DisplayItemKind.Border, borderShape, Border: used));
            }
        }

        /// <summary>
        /// Box shadows (https://www.w3.org/TR/css-backgrounds-3/#box-shadow), the last written first so the first ends up
        /// on top: an outer shadow is the border box moved by its offset and grown by its spread, seen only outside the
        /// border box; an inset one is the padding box moved and shrunk, seen inside the padding box around it. Blur
        /// radii are twice the Gaussian standard deviation.
        /// </summary>
        private void PaintBoxShadows(PaintBox box, RoundedRect borderBox, BorderGroup border, bool inset)
        {
            var style = box.Box.Style;
            var padding = borderBox.Inset(border.TopWidth, border.RightWidth, border.BottomWidth, border.LeftWidth);
            for (var i = style.Shadows.Box.Count - 1; i >= 0; i--)
            {
                var shadow = style.Shadows.Box[i];
                var color = shadow.Color.Resolve(style.Inherited.Color);
                if (shadow.Inset != inset || color.A <= 0)
                    continue;
                var basis = inset ? padding : borderBox;
                var grow = inset ? -shadow.Spread : shadow.Spread;
                var rect = basis.Rect;
                var moved = new RectF(rect.X + shadow.X - grow, rect.Y + shadow.Y - grow, Math.Max(0, rect.Width + 2 * grow), Math.Max(0, rect.Height + 2 * grow));
                static System.Numerics.Vector2 Grown(System.Numerics.Vector2 r, float by) =>
                    r == System.Numerics.Vector2.Zero ? r : System.Numerics.Vector2.Max(r + new System.Numerics.Vector2(by), System.Numerics.Vector2.Zero);
                var radii = basis.Radii;
                var shape = new RoundedRect(moved, new CornerRadii(Grown(radii.TopLeft, grow), Grown(radii.TopRight, grow), Grown(radii.BottomRight, grow), Grown(radii.BottomLeft, grow)));
                SetClip(box.Clip);
                list.Items.Add(new DisplayItem(DisplayItemKind.BoxShadow, shape, color, Blur: shadow.Blur / 2, Inset: inset, Box: basis));
            }
        }

        // A text fragment's glyphs, left to right or, for right-to-left runs, from its right edge; or replaced content.
        // A text fragment's glyphs with their baseline origins on the canvas (baselines snap vertically only), or null.
        private static GlyphRun? GlyphsOf(PaintBox box)
        {
            var run = box.Fragment.Text!;
            if (run.Run.Face is not { } face || run.GlyphEnd <= run.GlyphStart)
                return null;
            var count = run.GlyphEnd - run.GlyphStart;
            var glyphs = new ushort[count];
            var origins = new Vector2[count];
            var baseline = box.Snap(box.Y + run.Ascent);
            var x = run.RightToLeft ? box.X + box.Fragment.Width : box.X;
            for (var i = 0; i < count; i++)
            {
                var g = run.GlyphStart + i;
                var advance = run.Run.Advances[g];
                if (run.RightToLeft)
                    x -= advance;
                glyphs[i] = run.Run.Glyphs[g];
                origins[i] = new Vector2(x, baseline) + (run.Run.Offsets?[g] ?? Vector2.Zero);
                if (!run.RightToLeft)
                    x += advance;
            }
            return new GlyphRun(face, run.Run.Size, glyphs, origins);
        }

        // The visible text fragments in a box and its in-flow descendants, placed on the canvas.
        // Whether text directly in one inline box is inside another. Inline boxes keep no parent box, so this goes by
        // their elements: the same element's pseudo-element boxes are inside its box, and descendants' boxes are too.
        private static bool IsWithin(InlineBox? box, InlineBox inline)
        {
            if (box is null)
                return false;
            if (box.Node == inline.Node)
                return box == inline || inline.PseudoElement == PseudoElement.None;
            for (var node = box.Node?.Parent; node is not null; node = node.Parent)
            {
                if (node == inline.Node)
                    return true;
            }
            return false;
        }

        private static IEnumerable<PaintBox> TextIn(PaintBox box)
        {
            var stack = new Stack<PaintBox>([box]);
            while (stack.TryPop(out var current))
            {
                foreach (var child in current.Fragment.Children)
                {
                    var placed = new PaintBox(child.Fragment, current.X + child.X, current.Y + child.Y, null, Scale: current.Scale);
                    if (child.Fragment.Kind == FragmentKind.Text)
                    {
                        if (child.Fragment.Text!.Style.Inherited.Visibility == Visibility.Visible)
                            yield return placed;
                    }
                    else if (child.Fragment.Box is not { IsFloat: true } and not { IsAbsolutelyPositioned: true })
                        stack.Push(placed);
                }
            }
        }

        private void PaintText(PaintBox box)
        {
            if (box.Box is MarkerBox { Symbol: { } symbol })
            {
                PaintSymbol(box, symbol);
                return;
            }
            if (box.Fragment.Kind == FragmentKind.Box)
            {
                if (IsDropDown(box.Box))
                    PaintDropDownArrow(box);
                else if (box.Fragment.Svg is { } svg)
                    PaintSvg(box, svg);
                else
                    PaintImage(box);
                return;
            }
            var run = box.Fragment.Text!;
            var style = run.Style;
            if (run.Turned)
            {
                PaintTurnedText(box, run);
                return;
            }
            if (style.Inherited.Visibility != Visibility.Visible || run.Run.Face is not { } face || GlyphsOf(box) is not { } glyphRun)
                return;
            var (glyphs, origins) = (glyphRun.Glyphs, glyphRun.Origins);
            var baseline = box.Snap(box.Y + run.Ascent);
            SetClip(box.Clip);
            // Text shadows go under the text and its decorations, the last written lowest (css-text-decor-3 §4).
            if (style.Text.TextShadows is { } shadows)
            {
                SetClip(box.Clip);
                for (var i = shadows.Count - 1; i >= 0; i--)
                {
                    var shadow = shadows[i];
                    var shadowColor = shadow.Color.Resolve(style.Inherited.Color);
                    if (shadowColor.A <= 0)
                        continue;
                    var moved = origins.Select(o => o + new Vector2(shadow.X, shadow.Y)).ToArray();
                    list.Items.Add(new DisplayItem(DisplayItemKind.Glyphs, Color: shadowColor, Glyphs: new GlyphRun(face, run.Run.Size, glyphs, moved), Blur: shadow.Blur / 2));
                }
            }
            var decorations = style.Inherited.Decorations is null ? null
                : DecorationLines(box, run, face, baseline, style.Text.SkipInk == SkipInk.None ? null : glyphRun);
            if (decorations is not null)
                list.Items.AddRange(decorations.Where(d => d.Under).Select(d => d.Item));
            list.Items.Add(new DisplayItem(DisplayItemKind.Glyphs, Color: style.Inherited.Color, Glyphs: glyphRun));
            if (decorations is not null)
                list.Items.AddRange(decorations.Where(d => !d.Under).Select(d => d.Item));
        }

        /// <summary>
        /// Text in a vertical line (Layout.VerticalLayout): its central baseline runs down the fragment, the text's
        /// ascent in from its right edge. Upright glyphs are centred on it, each hanging from its vertical origin;
        /// sideways ones are drawn as horizontal text turned a quarter clockwise, their alphabetic baseline left of it.
        /// </summary>
        // ponytail: no decorations or shadows on vertical text yet.
        private void PaintTurnedText(PaintBox box, Layout.TextRun run)
        {
            if (run.Style.Inherited.Visibility != Visibility.Visible || run.Run.Face is not { } face || run.GlyphEnd <= run.GlyphStart)
                return;
            var count = run.GlyphEnd - run.GlyphStart;
            var (size, scale) = (run.Run.Size, run.Run.Size / face.UnitsPerEm);
            var right = box.X + box.Fragment.Width;
            var glyphs = new ushort[count];
            var origins = new Vector2[count];
            var along = 0f;
            for (var i = 0; i < count; i++)
            {
                var g = run.GlyphStart + i;
                glyphs[i] = run.Run.Glyphs[g];
                origins[i] = run.Run.Upright
                    ? new Vector2(right - run.Ascent - face.Advance(glyphs[i]) * scale / 2, box.Y + along + face.Vertical(glyphs[i]).Origin * scale)
                    : new Vector2(along, run.Ascent + Layout.InlineLayout.CentralOffset(face, size));
                along += run.Run.Advances[g];
            }
            SetClip(box.Clip);
            var item = new DisplayItem(DisplayItemKind.Glyphs, Color: run.Style.Inherited.Color, Glyphs: new GlyphRun(face, size, glyphs, origins));
            if (run.Run.Upright)
            {
                list.Items.Add(item);
                return;
            }
            list.Items.Add(new DisplayItem(DisplayItemKind.PushTransform, Transform: new Matrix3x2(0, 1, -1, 0, right, box.Y)));
            list.Items.Add(item);
            list.Items.Add(new DisplayItem(DisplayItemKind.Pop));
        }

        /// <summary>
        /// The lines of the decorations applied to a text fragment, outermost decorating box first
        /// (https://www.w3.org/TR/css-text-decor-4/#line-decoration): underlines and overlines go under the glyphs,
        /// line-throughs over them. Positions and auto thicknesses come from the font's post and OS/2 metrics.
        /// Underlines and overlines carry <paramref name="skipInk"/>, the glyphs they leave gaps around.
        /// </summary>
        // ponytail: each fragment places its lines from its own font, so a decorating box with mixed fonts or sizes gets
        // lines at several heights rather than one position for the whole box.
        private static List<(DisplayItem Item, bool Under)> DecorationLines(PaintBox box, Layout.TextRun run, Typography.FontFace face, float baseline,
                                                                             GlyphRun? skipInk)
        {
            var chain = new List<AppliedDecoration>();
            for (var d = run.Style.Inherited.Decorations; d is not null; d = d.Outer)
                chain.Insert(0, d);

            // Spaces hanging at the end of a line are not decorated (text-decoration-skip-spaces: start end).
            var (left, right) = (box.X, box.X + box.Fragment.Width);
            if (box.LineEnd && run.Style.Text.WhiteSpaceCollapse is not (WhiteSpaceCollapse.Preserve or WhiteSpaceCollapse.BreakSpaces))
            {
                var (space, ideographic) = (face.GlyphFor(' '), face.GlyphFor('\u3000'));
                var trim = 0f;
                for (var g = run.GlyphEnd - 1; g >= run.GlyphStart && run.Run.Glyphs[g] is var id && id != 0 && (id == space || id == ideographic); g--)
                    trim += run.Run.Advances[g];
                (left, right) = run.RightToLeft ? (left + trim, right) : (left, right - trim);
            }

            var scale = run.Run.Size / face.UnitsPerEm;
            var lines = new List<(DisplayItem, bool)>();
            foreach (var d in chain)
            {
                if (d.Color.A <= 0)
                    continue;
                var thickness = d.Thickness is { } t ? Math.Max(0, t) : Math.Max(1, face.UnderlineThickness > 0 ? face.UnderlineThickness * scale : run.Run.Size / 16);
                if (thickness <= 0 || right <= left)
                    continue;
                void Add(float top, bool under, int doubleDirection)
                {
                    if (d.Style == TextDecorationStyle.Double)
                    {
                        lines.Add((new DisplayItem(DisplayItemKind.Decoration, new RoundedRect(new RectF(left, top, right - left, thickness), default), d.Color,
                            Glyphs: under ? skipInk : null), under));
                        top += 2 * thickness * doubleDirection;
                    }
                    lines.Add((new DisplayItem(DisplayItemKind.Decoration, new RoundedRect(new RectF(left, top, right - left, thickness), default), d.Color,
                        Glyphs: under ? skipInk : null, LineStyle: d.Style == TextDecorationStyle.Double ? TextDecorationStyle.Solid : d.Style), under));
                }
                if (d.Line.HasFlag(TextDecorationLine.Underline))
                    Add(baseline + (d.Offset ?? (face.UnderlinePosition != 0 ? -face.UnderlinePosition * scale : run.Run.Size / 10)), true, 1);
                if (d.Line.HasFlag(TextDecorationLine.Overline))
                    Add(baseline - run.Ascent, true, -1);
                if (d.Line.HasFlag(TextDecorationLine.LineThrough))
                {
                    var above = face.StrikeoutPosition > 0 ? face.StrikeoutPosition * scale : (face.XHeight > 0 ? face.XHeight * scale : run.Run.Size / 2) / 2 + thickness / 2;
                    Add(baseline - above, false, 1);
                }
            }
            return lines;
        }

        /// <summary>
        /// An outline around the border box, <c>outline-offset</c> away from it, following its corners
        /// (https://www.w3.org/TR/css-ui-4/#outline-props). The auto style draws as solid.
        /// </summary>
        private void PaintOutline(PaintBox box)
        {
            var style = box.Box.Style;
            if (style.Inherited.Visibility != Visibility.Visible)
                return;
            var outline = style.Outline;
            var (w, grow) = (outline.Width, outline.Offset + outline.Width);
            var shape = BorderBox(box);
            static System.Numerics.Vector2 Grown(System.Numerics.Vector2 r, float by) => r == System.Numerics.Vector2.Zero ? r : System.Numerics.Vector2.Max(r + new System.Numerics.Vector2(by), System.Numerics.Vector2.Zero);
            var radii = shape.Radii;
            var outer = new RoundedRect(shape.Rect.Inset(-grow, -grow, -grow, -grow),
                new CornerRadii(Grown(radii.TopLeft, grow), Grown(radii.TopRight, grow), Grown(radii.BottomRight, grow), Grown(radii.BottomLeft, grow)));
            var borderStyle = outline.Style == OutlineStyle.Auto ? BorderStyle.Solid : Enum.Parse<BorderStyle>(outline.Style.ToString());
            var color = outline.Color.Resolve(style.Inherited.Color);
            SetClip(box.Clip);
            list.Items.Add(new DisplayItem(DisplayItemKind.Border, outer,
                Border: new BorderGroup(w, w, w, w, borderStyle, borderStyle, borderStyle, borderStyle, color, color, color, color)));
        }

        /// <summary>
        /// Background image layers, bottom layer first (css-backgrounds-3 §3): url() images and gradients, each sized by
        /// background-size in its background-origin box (auto keeps an image's natural size and ratio; a gradient has no
        /// size of its own, so it fills the box), placed by background-position, tiled by background-repeat, and
        /// clipped to its background-clip box. <paramref name="geometry"/> gives the boxes; <paramref name="paintingArea"/>,
        /// when given, replaces the clip box (the canvas).
        /// </summary>
        // ponytail: a layer of more than 4096 tiles paints one tile; url() images that fail to load paint nothing.
        public void PaintBackgroundImages(ComputedStyle style, PaintBox geometry, RoundedRect borderBox, RoundedRect? paintingArea = null)
        {
            var background = style.Background;
            var sampling = style.Inherited.ImageRendering is ImageRendering.Pixelated or ImageRendering.CrispEdges ? ImageSampling.Pixelated : ImageSampling.Smooth;
            for (var i = background.Images.Count - 1; i >= 0; i--)
            {
                var gradient = background.Images[i] is GradientImage { Computed: { } g } ? g : null;
                var image = background.Images[i] is UrlImage url ? images?.Load(url.Url, "background-image") : null;
                if (gradient is null && image is null)
                    continue;
                var origin = Area(borderBox, geometry.Box.Style, geometry.Fragment, background.Origins[i % background.Origins.Count]).Rect;
                var clip = paintingArea ?? Area(borderBox, geometry.Box.Style, geometry.Fragment, background.Clips[i % background.Clips.Count]);
                var blend = Blend(style.Effects.BackgroundBlendModes[i % style.Effects.BackgroundBlendModes.Count]);
                SetClip(geometry.Clip);
                list.Items.Add(new DisplayItem(DisplayItemKind.PushClip, clip));
                // Images have no paint to blend with, so a blended image layer is a layer of its own.
                var imageBlend = image is not null && blend != BlendMode.Normal;
                if (imageBlend)
                    list.Items.Add(new DisplayItem(DisplayItemKind.PushLayer, Blend: blend));
                PaintTiles(gradient, image, style, origin, clip.Rect, background.Sizes[i % background.Sizes.Count],
                    background.Positions[i % background.Positions.Count], background.Repeats[i % background.Repeats.Count], blend, sampling);
                if (imageBlend)
                    list.Items.Add(new DisplayItem(DisplayItemKind.Pop));
                list.Items.Add(new DisplayItem(DisplayItemKind.Pop));
            }
        }

        // One background or mask layer's tiles: sized, placed in the positioning area and repeated over the painting area.
        private void PaintTiles(ComputedGradient? gradient, Imaging.DecodedImage? image, ComputedStyle style, RectF origin, RectF painting,
                                BackgroundSize size, Style.BackgroundPosition position, RepeatStyle repeat, BlendMode blend, ImageSampling sampling)
        {
            var (w, h) = TileSize(size, origin, image is null ? null : (image.Width, image.Height), repeat);
            if (w <= 0 || h <= 0)
                return;
            var (x, y) = (origin.X + position.X.Resolve(origin.Width - w), origin.Y + position.Y.Resolve(origin.Height - h));
            var across = Tiles(repeat.X, x, w, origin.X, origin.Width, painting.X, painting.Right);
            var down = Tiles(repeat.Y, y, h, origin.Y, origin.Height, painting.Y, painting.Bottom);
            if ((across.End - across.Start) / across.Step * ((down.End - down.Start) / down.Step) > 4096)
                (across, down) = ((x, w, x + w), (y, h, y + h));
            for (var ty = down.Start; ty < down.End - 0.01f; ty += down.Step)
            {
                for (var tx = across.Start; tx < across.End - 0.01f; tx += across.Step)
                {
                    var tile = new RectF(tx, ty, w, h);
                    if (image is not null)
                        list.Items.Add(new DisplayItem(DisplayItemKind.Image, new RoundedRect(tile, default), Image: image, Sampling: sampling));
                    else if (GradientGeometry.Build(gradient!, tile, style.Inherited.Color) is { } paint)
                        list.Items.Add(new DisplayItem(DisplayItemKind.Fill, new RoundedRect(tile, default), Gradient: paint, Blend: blend));
                }
            }
        }

        /// <summary>
        /// The mask (https://drafts.csswg.org/css-masking-1/#the-mask-image-rendering-model): its layers, bottom first, each
        /// drawn into a layer of its own and composited onto those below by its mask-composite operator (the bottom one,
        /// with nothing below, drawn as it is), then applied to what the box painted by keeping it where the mask is opaque. A layer's image is
        /// sized, placed and tiled like a background in its mask-origin box and clipped to its mask-clip box; an image
        /// that does not load, and none, are transparent. A luminance layer is drawn over opaque black and turned into
        /// alpha, so its alpha is the luminance of its colour times its own alpha. A layer that references an SVG mask
        /// element is that mask's content in its region, the border box being its bounding box, luminance or alpha by
        /// its mask-type unless mask-mode says.
        /// </summary>
        // ponytail: with no-clip a layer's tiles cover the border box and the origin box only.
        private void PaintMask(PaintBox box, MaskGroup mask)
        {
            if (mask.HasLayers)
                PaintMaskLayers(box, mask);
            if (mask.MaskBorder.Source is not NoImage)
                PaintMaskBorder(box, mask);
        }

        /// <summary>
        /// The mask border (https://drafts.csswg.org/css-masking-1/#mask-borders): its image cut and laid out as a border
        /// image would be, as a mask of its own over what the box painted (and its mask layers): alpha, or luminance by
        /// mask-border-mode. An image that does not load masks the box away.
        /// </summary>
        private void PaintMaskBorder(PaintBox box, MaskGroup mask)
        {
            var border = box.Box.Style.Border;
            list.Items.Add(new DisplayItem(DisplayItemKind.PushLayer, Blend: BlendMode.DestinationIn));
            var luminance = mask.BorderMode == MaskType.Luminance;
            if (luminance)
            {
                list.Items.Add(new DisplayItem(DisplayItemKind.PushLayer, Filters: FilterPrimitives.LuminanceToAlpha));
                list.Items.Add(new DisplayItem(DisplayItemKind.Fill, new RoundedRect(BorderImageArea(box, border, mask.MaskBorder), default), CssColor.Black));
            }
            PaintBorderImage(box, border, mask.MaskBorder, "mask-border-source", clip: false);
            if (luminance)
                list.Items.Add(new DisplayItem(DisplayItemKind.Pop));
            list.Items.Add(new DisplayItem(DisplayItemKind.Pop));
        }

        private void PaintMaskLayers(PaintBox box, MaskGroup mask)
        {
            var sampling = box.Box.Style.Inherited.ImageRendering is ImageRendering.Pixelated or ImageRendering.CrispEdges ? ImageSampling.Pixelated : ImageSampling.Smooth;
            list.Items.Add(new DisplayItem(DisplayItemKind.PushLayer, Blend: BlendMode.DestinationIn));
            for (var i = mask.Images.Count - 1; i >= 0; i--)
            {
                var gradient = mask.Images[i] is GradientImage { Computed: { } g } ? g : null;
                var svg = box.Fragment.SvgMasks is { } svgMasks ? svgMasks[i] : null;
                // A reference to an element that is not a mask is a transparent layer, not an image to load.
                var image = svg is null && mask.Images[i] is UrlImage url && !url.Url.StartsWith('#') ? images?.Load(url.Url, "mask-image") : null;
                var composite = i == mask.Images.Count - 1 ? MaskComposite.Add : mask.Composites[i % mask.Composites.Count];
                if (gradient is null && image is null && svg is null && composite == MaskComposite.Add)
                    continue;
                var luminance = svg?.Luminance ?? mask.Modes[i % mask.Modes.Count] == MaskMode.Luminance;
                var operation = composite switch
                {
                    MaskComposite.Subtract => BlendMode.SourceOut,
                    MaskComposite.Intersect => BlendMode.SourceIn,
                    MaskComposite.Exclude => BlendMode.Xor,
                    _ => BlendMode.Normal,
                };
                list.Items.Add(new DisplayItem(DisplayItemKind.PushLayer, Filters: luminance ? FilterPrimitives.LuminanceToAlpha : null, Blend: operation));
                if (svg is not null)
                {
                    // mask-origin, mask-clip, mask-size, mask-position and mask-repeat do not apply to a mask element.
                    var region = new RectF(box.Rect.X + svg.Region.X, box.Rect.Y + svg.Region.Y, svg.Region.Width, svg.Region.Height);
                    if (luminance)
                        list.Items.Add(new DisplayItem(DisplayItemKind.Fill, new RoundedRect(region, default), CssColor.Black));
                    SvgPainter.PaintMaskContent(svg, new Vector2(box.Rect.X, box.Rect.Y), list.Items);
                    list.Items.Add(new DisplayItem(DisplayItemKind.Pop));
                    continue;
                }
                var origin = ReferenceBox(box, mask.Origins[i % mask.Origins.Count]).Rect;
                // The mask painting area is the box's rectangle, without its corners (unlike background-clip).
                var clip = mask.Clips[i % mask.Clips.Count].Box is { } clipBox ? new RoundedRect(ReferenceBox(box, clipBox).Rect, default) : (RoundedRect?)null;
                var painting = clip?.Rect ?? Union(origin, box.Rect);
                if (clip is { } shape)
                    list.Items.Add(new DisplayItem(DisplayItemKind.PushClip, shape));
                if (luminance)
                    list.Items.Add(new DisplayItem(DisplayItemKind.Fill, new RoundedRect(painting, default), CssColor.Black));
                if (gradient is not null || image is not null)
                {
                    PaintTiles(gradient, image, box.Box.Style, origin, painting, mask.Sizes[i % mask.Sizes.Count], mask.Positions[i % mask.Positions.Count],
                        mask.Repeats[i % mask.Repeats.Count], BlendMode.Normal, sampling);
                }
                if (clip is not null)
                    list.Items.Add(new DisplayItem(DisplayItemKind.Pop));
                list.Items.Add(new DisplayItem(DisplayItemKind.Pop));
            }
            list.Items.Add(new DisplayItem(DisplayItemKind.Pop));

            static RectF Union(RectF a, RectF b)
            {
                var (x, y) = (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y));
                return new RectF(x, y, Math.Max(a.Right, b.Right) - x, Math.Max(a.Bottom, b.Bottom) - y);
            }
        }

        /// <summary>
        /// A border image (https://drafts.csswg.org/css-backgrounds-3/#border-images): the image, or a gradient drawn at
        /// the size of the border image area (the border box grown by border-image-outset), is cut into nine parts by
        /// border-image-slice. The corners are scaled into the corners of the area, border-image-width wide and tall
        /// (scaled down together when opposite ones overlap); the edges are scaled to their width and tiled along their
        /// length by border-image-repeat; the middle, kept only with fill, is scaled like the top and left edges and tiled
        /// both ways (https://drafts.csswg.org/css-backgrounds-3/#border-image-process). False when there is no image to
        /// draw, so the border styles are used.
        /// </summary>
        // The border image area: the border box grown by the outsets, multiples of the border width or lengths.
        private static RectF BorderImageArea(PaintBox box, BorderGroup border, BorderImageGroup borderImage)
        {
            static float Outset(BorderImageSide side, float borderWidth) => side.Number is { } n ? n * borderWidth : side.Length?.Px ?? 0;
            var o = borderImage.Outset;
            return box.Rect.Inset(-Outset(o.Top, border.TopWidth), -Outset(o.Right, border.RightWidth),
                -Outset(o.Bottom, border.BottomWidth), -Outset(o.Left, border.LeftWidth));
        }

        private bool PaintBorderImage(PaintBox box, BorderGroup border, BorderImageGroup borderImage, string what, bool clip = true)
        {
            var style = box.Box.Style;
            var gradient = borderImage.Source is GradientImage { Computed: { } g } ? g : null;
            var image = borderImage.Source is UrlImage url ? images?.Load(url.Url, what) : null;
            if (gradient is null && image is null)
                return false;

            var area = BorderImageArea(box, border, borderImage);
            var (imageWidth, imageHeight) = image is not null ? ((float)image.Width, (float)image.Height) : (area.Width, area.Height);
            if (area.Width <= 0 || area.Height <= 0 || imageWidth <= 0 || imageHeight <= 0)
                return true;

            var s = borderImage.Slice;
            var (st, sr, sb, sl) = (s.Top.Resolve(imageHeight), s.Right.Resolve(imageWidth), s.Bottom.Resolve(imageHeight), s.Left.Resolve(imageWidth));
            // Widths are multiples of the border width, lengths (percentages of the area), or auto: the slice's size
            // in an image with natural dimensions, the border width in one without.
            float Width(BorderImageSide side, float borderWidth, float basis, float slice) =>
                side.Number is { } n ? n * borderWidth : side.Length is { } l ? l.Resolve(basis) : image is not null ? slice : borderWidth;
            var w = borderImage.Width;
            var (wt, wr, wb, wl) = (Width(w.Top, border.TopWidth, area.Height, st), Width(w.Right, border.RightWidth, area.Width, sr),
                Width(w.Bottom, border.BottomWidth, area.Height, sb), Width(w.Left, border.LeftWidth, area.Width, sl));
            var f = Math.Min(1, Math.Min(wl + wr > 0 ? area.Width / (wl + wr) : 1, wt + wb > 0 ? area.Height / (wt + wb) : 1));
            (wt, wr, wb, wl) = (wt * f, wr * f, wb * f, wl * f);

            if (clip)
                SetClip(box.Clip);
            var (x0, x1, x2, x3) = (area.X, area.X + wl, area.Right - wr, area.Right);
            var (y0, y1, y2, y3) = (area.Y, area.Y + wt, area.Bottom - wb, area.Bottom);
            var (mw, mh) = (imageWidth - sl - sr, imageHeight - st - sb);
            var sampling = style.Inherited.ImageRendering is ImageRendering.Pixelated or ImageRendering.CrispEdges ? ImageSampling.Pixelated : ImageSampling.Smooth;
            var repeat = borderImage.Repeat;

            // Corners, scaled to fit.
            Part(new RectF(0, 0, sl, st), new RectF(x0, y0, wl, wt));
            Part(new RectF(imageWidth - sr, 0, sr, st), new RectF(x2, y0, wr, wt));
            Part(new RectF(imageWidth - sr, imageHeight - sb, sr, sb), new RectF(x2, y2, wr, wb));
            Part(new RectF(0, imageHeight - sb, sl, sb), new RectF(x0, y2, wl, wb));
            // Edges: as tall (or wide) as their region, tiled along it.
            float Factor(float to, float from) => from > 0 ? to / from : 0;
            Tile(new RectF(sl, 0, mw, st), new RectF(x1, y0, x2 - x1, wt), mw * Factor(wt, st), wt, repeat.X, BorderImageRepeat.Stretch);
            Tile(new RectF(sl, imageHeight - sb, mw, sb), new RectF(x1, y2, x2 - x1, wb), mw * Factor(wb, sb), wb, repeat.X, BorderImageRepeat.Stretch);
            Tile(new RectF(0, st, sl, mh), new RectF(x0, y1, wl, y2 - y1), wl, mh * Factor(wl, sl), BorderImageRepeat.Stretch, repeat.Y);
            Tile(new RectF(imageWidth - sr, st, sr, mh), new RectF(x2, y1, wr, y2 - y1), wr, mh * Factor(wr, sr), BorderImageRepeat.Stretch, repeat.Y);
            // The middle: its width scaled like the top edge (or the bottom one, or not at all), its height like the left
            // edge (or the right one, or not at all).
            if (s.Fill)
            {
                static float Usable(float factor) => factor > 0 && float.IsFinite(factor) ? factor : 0;
                var fx = Usable(Factor(wt, st)) is > 0 and var top ? top : Usable(Factor(wb, sb)) is > 0 and var bottom ? bottom : 1;
                var fy = Usable(Factor(wl, sl)) is > 0 and var left ? left : Usable(Factor(wr, sr)) is > 0 and var right ? right : 1;
                Tile(new RectF(sl, st, mw, mh), new RectF(x1, y1, x2 - x1, y2 - y1), mw * fx, mh * fy, repeat.X, repeat.Y);
            }
            return true;

            // Tiles of one part over its region: stretch fills the region, round fits a whole number of tiles, repeat
            // centres them, and space spreads the whole tiles that fit with equal gaps around them.
            void Tile(RectF source, RectF region, float tileWidth, float tileHeight, BorderImageRepeat horizontal, BorderImageRepeat vertical)
            {
                if (source.Width <= 0 || source.Height <= 0 || region.Width <= 0 || region.Height <= 0 || tileWidth <= 0 || tileHeight <= 0)
                    return;
                var across = Axis(horizontal, region.X, region.Width, tileWidth);
                var down = Axis(vertical, region.Y, region.Height, tileHeight);
                // ponytail: a part of more than 1024 tiles is stretched instead.
                if (across.Count * down.Count > 1024)
                    (across, down) = (Axis(BorderImageRepeat.Stretch, region.X, region.Width, tileWidth), Axis(BorderImageRepeat.Stretch, region.Y, region.Height, tileHeight));
                for (var j = 0; j < down.Count; j++)
                {
                    for (var i = 0; i < across.Count; i++)
                        Part(source, new RectF(across.Start + i * across.Step, down.Start + j * down.Step, across.Size, down.Size), region);
                }
            }

            // One slice of the image drawn into a tile, clipped to its region: the whole image is placed so the slice
            // lands on the tile.
            void Part(RectF source, RectF tile, RectF? region = null)
            {
                if (source.Width <= 0 || source.Height <= 0 || tile.Width <= 0 || tile.Height <= 0)
                    return;
                var (kx, ky) = (tile.Width / source.Width, tile.Height / source.Height);
                var whole = new RectF(tile.X - source.X * kx, tile.Y - source.Y * ky, imageWidth * kx, imageHeight * ky);
                var visible = region is { } r ? Intersect(tile, r) : tile;
                if (visible.Width <= 0 || visible.Height <= 0)
                    return;
                if (image is not null)
                {
                    list.Items.Add(new DisplayItem(DisplayItemKind.PushClip, new RoundedRect(visible, default)));
                    list.Items.Add(new DisplayItem(DisplayItemKind.Image, new RoundedRect(whole, default), Image: image, Sampling: sampling));
                    list.Items.Add(new DisplayItem(DisplayItemKind.Pop));
                }
                else if (GradientGeometry.Build(gradient!, whole, style.Inherited.Color) is { } paint)
                {
                    list.Items.Add(new DisplayItem(DisplayItemKind.Fill, new RoundedRect(visible, default), Gradient: paint));
                }
            }

            static RectF Intersect(RectF a, RectF b)
            {
                var (x, y) = (Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
                return new RectF(x, y, Math.Max(0, Math.Min(a.Right, b.Right) - x), Math.Max(0, Math.Min(a.Bottom, b.Bottom) - y));
            }
        }

        // Where the tiles of a border image part go along one axis of its region: the first one's start, the step, how
        // many, and their size.
        private static (float Start, float Step, int Count, float Size) Axis(BorderImageRepeat mode, float start, float length, float size)
        {
            switch (mode)
            {
                case BorderImageRepeat.Stretch:
                    return (start, length, 1, length);
                case BorderImageRepeat.Round:
                    var n = Math.Max(1, MathF.Round(length / size, MidpointRounding.AwayFromZero));
                    return (start, length / n, (int)n, length / n);
                case BorderImageRepeat.Space:
                    var count = MathF.Floor(length / size + 1e-4f);
                    if (count < 1)
                        return (start, size, 0, size);
                    var gap = (length - count * size) / (count + 1);
                    return (start + gap, size + gap, (int)count, size);
                default: // repeat: centred
                    var centred = start + (length - size) / 2;
                    var first = centred - MathF.Ceiling((centred - start) / size) * size;
                    return (first, size, (int)MathF.Ceiling((start + length - first) / size - 1e-4f), size);
            }
        }

        /// <summary>
        /// A layer's tile size (https://www.w3.org/TR/css-backgrounds-3/#background-size) in its positioning area:
        /// cover and contain scale the natural size, an auto side follows the other through the natural ratio, and
        /// both auto is the natural size; without natural dimensions (gradients) auto is the area's. round then
        /// rescales each rounding side to fit a whole number of tiles, and an auto other side keeps the ratio.
        /// </summary>
        private static (float Width, float Height) TileSize(BackgroundSize size, RectF area, (float Width, float Height)? natural, RepeatStyle repeat)
        {
            var (areaWidth, areaHeight) = (area.Width, area.Height);
            float? width = size.Kind == BackgroundSizeKind.Explicit && size.Width.Kind == SizeKind.Length ? size.Width.Length.Resolve(areaWidth) : null;
            float? height = size.Kind == BackgroundSizeKind.Explicit && size.Height.Kind == SizeKind.Length ? size.Height.Length.Resolve(areaHeight) : null;
            float w, h;
            if (natural is not ({ } nw, { } nh) || nw <= 0 || nh <= 0)
            {
                (w, h) = size.Kind == BackgroundSizeKind.Explicit ? (width ?? areaWidth, height ?? areaHeight) : (areaWidth, areaHeight);
            }
            else if (size.Kind != BackgroundSizeKind.Explicit)
            {
                var scale = size.Kind == BackgroundSizeKind.Cover ? Math.Max(areaWidth / nw, areaHeight / nh) : Math.Min(areaWidth / nw, areaHeight / nh);
                (w, h) = (nw * scale, nh * scale);
            }
            else
            {
                (w, h) = (width, height) switch
                {
                    ({ } a, { } b) => (a, b),
                    ({ } a, null) => (a, a * nh / nw),
                    (null, { } b) => (b * nw / nh, b),
                    _ => (nw, nh),
                };
            }

            // https://www.w3.org/TR/css-backgrounds-3/#valdef-background-repeat-round
            var autoWidth = size.Kind == BackgroundSizeKind.Explicit && width is null;
            var autoHeight = size.Kind == BackgroundSizeKind.Explicit && height is null;
            if (repeat.X == BackgroundRepeat.Round && w > 0 && areaWidth > 0)
            {
                var rounded = areaWidth / Math.Max(1, MathF.Round(areaWidth / w, MidpointRounding.AwayFromZero));
                if (repeat.Y != BackgroundRepeat.Round && autoHeight)
                    h *= rounded / w;
                w = rounded;
            }
            if (repeat.Y == BackgroundRepeat.Round && h > 0 && areaHeight > 0)
            {
                var rounded = areaHeight / Math.Max(1, MathF.Round(areaHeight / h, MidpointRounding.AwayFromZero));
                if (repeat.X != BackgroundRepeat.Round && autoWidth)
                    w *= rounded / h;
                h = rounded;
            }
            return (w, h);
        }

        /// <summary>
        /// Where tiles go along one axis (https://www.w3.org/TR/css-backgrounds-3/#background-repeat): one at the
        /// placed position, or every tile size from one that reaches into the painting area to its end. space spreads
        /// as many whole tiles as fit over the positioning area, the first and last touching its edges, and ignores the
        /// position; with room for fewer than two it places one like no-repeat.
        /// </summary>
        private static (float Start, float Step, float End) Tiles(BackgroundRepeat mode, float placed, float size, float areaStart, float areaLength,
                                                                   float paintStart, float paintEnd)
        {
            if (mode == BackgroundRepeat.Space && MathF.Floor(areaLength / size) is var count and >= 2)
            {
                var step = size + (areaLength - count * size) / (count - 1);
                return (areaStart - MathF.Ceiling((areaStart - paintStart) / step) * step, step, paintEnd);
            }
            return mode is BackgroundRepeat.NoRepeat or BackgroundRepeat.Space
                ? (placed, size, placed + size)
                : (placed - MathF.Ceiling((placed - paintStart) / size) * size, size, paintEnd);
        }

        // A background box of the border box: border-box, padding-box or content-box (text clips as border-box).
        private static RoundedRect Area(RoundedRect borderBox, ComputedStyle style, Fragment fragment, BackgroundBox which)
        {
            if (which is BackgroundBox.BorderBox or BackgroundBox.Text)
                return borderBox;
            var border = style.Border;
            var padding = borderBox.Inset(border.TopWidth, border.RightWidth, border.BottomWidth, border.LeftWidth);
            if (which == BackgroundBox.PaddingBox)
                return padding;
            var s = style.Spacing;
            return padding.Inset(s.PaddingTop.Resolve(fragment.Width), s.PaddingRight.Resolve(fragment.Width),
                s.PaddingBottom.Resolve(fragment.Width), s.PaddingLeft.Resolve(fragment.Width));
        }

        /// <summary>
        /// An image in its content box, sized and placed by object-fit and object-position
        /// (https://www.w3.org/TR/css-images-3/#the-object-fit) and clipped to the content box.
        /// </summary>
        // ponytail: padding percentages resolve against the box's own width, as for backgrounds.
        private void PaintImage(PaintBox box)
        {
            var replaced = (ReplacedBox)box.Box;
            var style = replaced.Style;
            if (style.Inherited.Visibility != Visibility.Visible || replaced.Image is not { } image || replaced.NaturalSize is not { } natural)
                return;
            var (border, spacing, w) = (style.Border, style.Spacing, box.Fragment.Width);
            var content = box.Rect.Inset(border.TopWidth + spacing.PaddingTop.Resolve(w), border.RightWidth + spacing.PaddingRight.Resolve(w),
                border.BottomWidth + spacing.PaddingBottom.Resolve(w), border.LeftWidth + spacing.PaddingLeft.Resolve(w));
            if (content.Width <= 0 || content.Height <= 0)
                return;

            var (width, height) = natural;
            var contain = Math.Min(content.Width / width, content.Height / height);
            var scale = style.Replaced.Fit switch
            {
                ObjectFit.Contain => contain,
                ObjectFit.Cover => Math.Max(content.Width / width, content.Height / height),
                ObjectFit.None => 1,
                ObjectFit.ScaleDown => Math.Min(1, contain),
                _ => float.NaN,
            };
            var (objectWidth, objectHeight) = float.IsNaN(scale) ? (content.Width, content.Height) : (width * scale, height * scale);
            var position = style.Replaced.Position;
            var destination = new RectF(content.X + position.X.Resolve(content.Width - objectWidth), content.Y + position.Y.Resolve(content.Height - objectHeight),
                objectWidth, objectHeight);

            SetClip(box.Clip);
            var overflows = destination.X < content.X || destination.Y < content.Y || destination.Right > content.Right || destination.Bottom > content.Bottom;
            if (overflows)
                list.Items.Add(new DisplayItem(DisplayItemKind.PushClip, new RoundedRect(content, default)));
            var sampling = style.Inherited.ImageRendering is ImageRendering.Pixelated or ImageRendering.CrispEdges ? ImageSampling.Pixelated : ImageSampling.Smooth;
            list.Items.Add(new DisplayItem(DisplayItemKind.Image, new RoundedRect(destination, default), Image: image, Sampling: sampling));
            if (overflows)
                list.Items.Add(new DisplayItem(DisplayItemKind.Pop));
        }

        // An outermost svg element's drawing, its user space starting at the content box's top-left corner.
        // ponytail: padding percentages resolve against the box's own width, as for images.
        private void PaintSvg(PaintBox box, Svg.SvgContainerNode svg)
        {
            var (border, spacing, w) = (box.Box.Style.Border, box.Box.Style.Spacing, box.Fragment.Width);
            SetClip(box.Clip);
            // The drawing starts on a whole device pixel, as other replaced content does, so its strokes and edges fall
            // on the same pixels as in the reference.
            SvgPainter.Paint(svg, new Vector2(box.Snap(box.X + border.LeftWidth + spacing.PaddingLeft.Resolve(w)),
                box.Snap(box.Y + border.TopWidth + spacing.PaddingTop.Resolve(w))), list.Items);
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
        // A disc fills the marker's square with an ellipse, a circle strokes that ellipse 1px wide (centred on its edge),
        // and a square fills the square, all in the marker's colour.
        /// <summary>
        /// A drop-down select's arrow: a chevron in the text colour, centred in the arrow area at the inline end of the
        /// padding box and halfway down (study 15: static appearance).
        /// </summary>
        // ponytail: one chevron size for every font size; scale it when a page shows controls at other sizes.
        private void PaintDropDownArrow(PaintBox box)
        {
            var style = box.Box.Style;
            if (style.Inherited.Visibility != Visibility.Visible || style.Inherited.Color.A <= 0)
                return;
            SetClip(box.Clip);
            var (rect, border) = (box.Rect, style.Border);
            var half = Layout.BoxTreeBuilder.SelectArrowWidth / 2;
            var x = style.Text.Direction == Direction.Rtl ? rect.X + border.LeftWidth + half : rect.Right - border.RightWidth - half;
            var y = rect.Y + rect.Height / 2;
            var path = new PathData().MoveTo(x - 3.5f, y - 2.25f).LineTo(x, y + 1.25f).LineTo(x + 3.5f, y - 2.25f);
            list.Items.Add(new DisplayItem(DisplayItemKind.StrokePath, Color: style.Inherited.Color, Path: path, Stroke: new Stroke(2.25f)));
        }

        // A disclosure triangle filling its square (the fragment's height, at its start): pointing to the inline end when
        // closed, down when open.
        private void PaintDisclosure(PaintBox box, ListSymbol symbol, ComputedStyle style)
        {
            var side = box.Fragment.Height;
            var rtl = style.Text.Direction == Direction.Rtl;
            var (x, y) = (rtl ? box.X + box.Fragment.Width - side : box.X, box.Y);
            PathData Triangle(params float[] uv) => new PathData().MoveTo(x + uv[0] * side, y + uv[1] * side)
                .LineTo(x + uv[2] * side, y + uv[3] * side).LineTo(x + uv[4] * side, y + uv[5] * side).Close();
            var path = symbol == ListSymbol.DisclosureOpen ? Triangle(0, 0.07f, 0.5f, 0.93f, 1, 0.07f)
                : rtl ? Triangle(1, 0, 0.14f, 0.5f, 1, 1)
                : Triangle(0, 0, 0.86f, 0.5f, 0, 1);
            list.Items.Add(new DisplayItem(DisplayItemKind.FillPath, Color: style.Inherited.Color, Path: path));
        }

        private void PaintSymbol(PaintBox box, ListSymbol symbol)
        {
            var style = box.Box.Style;
            if (style.Inherited.Visibility != Visibility.Visible || style.Inherited.Color.A <= 0)
                return;
            SetClip(box.Clip);
            if (symbol is ListSymbol.DisclosureClosed or ListSymbol.DisclosureOpen)
            {
                PaintDisclosure(box, symbol, style);
                return;
            }
            // Snapped as a whole, so the square stays square and the disc round.
            var side = box.Snap(box.Fragment.Width);
            var rect = new RectF(box.Snap(box.X), box.Snap(box.Y), side, side);
            var corner = new Vector2(rect.Width / 2, rect.Height / 2);
            var round = new CornerRadii(corner, corner, corner, corner);
            if (symbol == ListSymbol.Circle)
            {
                const float k = 0.5522848f; // cubic Bezier approximation of a quarter circle
                var (cx, cy, rx, ry) = (rect.X + rect.Width / 2, rect.Y + rect.Height / 2, rect.Width / 2, rect.Height / 2);
                var path = new PathData().MoveTo(cx + rx, cy)
                    .CubicTo(new(cx + rx, cy + k * ry), new(cx + k * rx, cy + ry), new(cx, cy + ry))
                    .CubicTo(new(cx - k * rx, cy + ry), new(cx - rx, cy + k * ry), new(cx - rx, cy))
                    .CubicTo(new(cx - rx, cy - k * ry), new(cx - k * rx, cy - ry), new(cx, cy - ry))
                    .CubicTo(new(cx + k * rx, cy - ry), new(cx + rx, cy - k * ry), new(cx + rx, cy))
                    .Close();
                list.Items.Add(new DisplayItem(DisplayItemKind.StrokePath, Color: style.Inherited.Color, Path: path, Stroke: new Stroke(1)));
                return;
            }
            list.Items.Add(new DisplayItem(DisplayItemKind.Fill, new RoundedRect(rect, symbol == ListSymbol.Disc ? round : default), style.Inherited.Color));
        }

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
