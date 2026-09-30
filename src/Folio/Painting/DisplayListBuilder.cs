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
// ponytail: M1 paints background colours and gradients, borders, text and its decorations, and images; url()
// background images come with their own work.
internal static class DisplayListBuilder
{
    // A box's border box on the canvas, with the overflow clips it is painted under. LineEnd marks text that ends its line.
    private sealed record PaintBox(Fragment Fragment, float X, float Y, ClipNode? Clip, bool LineEnd = false)
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
        public List<PaintBox> Text { get; } = [];
        public List<Context> Negative { get; } = [];
        public List<Context> ZeroOrAuto { get; } = [];
        public List<Context> Positive { get; } = [];
        public List<PaintBox> Outlines { get; } = [];
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
            // Line boxes only hold inline content; text is painted with text painting.
            if (child.Fragment.Kind == FragmentKind.Line)
            {
                Collect(context, real, placed, child.Fragment.Children, order);
                continue;
            }
            if (child.Fragment.Kind == FragmentKind.Text)
            {
                context.Text.Add(parent.Fragment.Kind == FragmentKind.Line && child.Fragment == parent.Fragment.Children[^1].Fragment
                    ? placed with { LineEnd = true } : placed);
                continue;
            }
            var box = placed.Box;
            var style = box.Style.Box;
            var index = order.GetValueOrDefault(box);
            // A transformed box's outline is transformed with it, so it paints in the box's own stacking context.
            var outlined = box.Style.Outline.Width > 0 && box is not TableWrapperBox;
            if (outlined && !box.IsTransformed)
                real.Outlines.Add(placed);
            // Replaced content paints with the inline content, after backgrounds and floats (CSS 2.2 Appendix E, step 7).
            var content = box is ReplacedBox { Image: not null } ? placed : null;
            if (CreatesStackingContext(box))
            {
                // A stacking context made by opacity, a transform or the like on a box z-index does not apply to sits at 0.
                var z = HasZIndex(box) ? style.ZIndex!.Value : 0;
                var c = new Context(placed, real: true, z, index);
                (z < 0 ? real.Negative : z > 0 ? real.Positive : real.ZeroOrAuto).Add(c);
                if (outlined && box.IsTransformed)
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
    // css-transforms-2 (any transform property other than none).
    // The root's context is made by Build. Boxes in inline content (inline boxes, floats and atomic inlines found
    // there) have no parent box, so a missing parent says nothing here.
    private static bool CreatesStackingContext(Box box)
    {
        var style = box.Style.Box;
        return style.Position is Position.Fixed or Position.Sticky
            || HasZIndex(box)
            || style.Opacity < 1
            || style.Isolation == Isolation.Isolate
            || box.IsTransformed;
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
        if (rootColor.A > 0 || root.Node is not ElementNode { LocalName: "html" })
            return (root, rootColor);
        var body = root.Children.FirstOrDefault(b => b.Node is ElementNode { LocalName: "body" } e && e.Name.Namespace == Namespaces.Html);
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
            var owner = context.Real ? context.Owner : null;
            var opacity = owner is not null && owner.Box.Style.Box.Opacity < 1 ? owner.Box.Style.Box.Opacity : 1;
            var transform = owner is not null && owner.Box.IsTransformed ? Transform(owner) : (Matrix3x2?)null;
            // A transform that cannot be inverted flattens the box to nothing: it and its content are not displayed
            // (https://www.w3.org/TR/css-transforms-1/#transform-function-lists).
            if (transform is { } singular && !Matrix3x2.Invert(singular, out _))
                return;
            var floor = _floor;
            // The clips outside stay open under the group; the transform and the layer apply to the box and all it holds.
            if (opacity < 1 || transform is not null)
            {
                SetClip(owner!.Clip);
                if (transform is { } matrix)
                    list.Items.Add(new DisplayItem(DisplayItemKind.PushTransform, Transform: matrix));
                if (opacity < 1)
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
            foreach (var text in context.Text)
                PaintText(text);
            foreach (var c in context.ZeroOrAuto.OrderBy(c => c.Order))
                Emit(c);
            foreach (var c in Sorted(context.Positive))
                Emit(c);
            // Outlines last, over everything else in the stacking context (CSS 2.2 Appendix E, step 10).
            foreach (var box in context.Outlines)
                PaintOutline(box);

            if (opacity < 1 || transform is not null)
            {
                PopTo(_floor);
                if (opacity < 1)
                    list.Items.Add(new DisplayItem(DisplayItemKind.Pop));
                if (transform is not null)
                    list.Items.Add(new DisplayItem(DisplayItemKind.Pop));
                _floor = floor;
            }
        }

        // A box's transform (css-transforms-2 §6) in canvas coordinates: its matrix is relative to the border box's
        // top-left corner, the reference box being the border box.
        // ponytail: 3D functions are flattened to 2D (TransformGroup.Matrix2D); perspective and preserve-3d wait for M2 3D.
        private static Matrix3x2 Transform(PaintBox box) =>
            Matrix3x2.CreateTranslation(-box.X, -box.Y)
            * box.Box.Style.Transform.Matrix2D(box.Fragment.Width, box.Fragment.Height)
            * Matrix3x2.CreateTranslation(box.X, box.Y);

        private static IEnumerable<Context> Sorted(List<Context> contexts) => contexts.OrderBy(c => c.Z).ThenBy(c => c.Order);

        private void PaintBackground(PaintBox box)
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
            if (color.A > 0)
            {
                SetClip(box.Clip);
                list.Items.Add(new DisplayItem(DisplayItemKind.Fill, BackgroundArea(shape, style, box.Fragment), color));
            }
            if (box.Box != canvasBox)
                PaintBackgroundImages(box, shape);
            if (style.Shadows.Box.Count > 0)
                PaintBoxShadows(box, shape, border, inset: true);
            if (border.TopWidth + border.RightWidth + border.BottomWidth + border.LeftWidth > 0)
            {
                SetClip(box.Clip);
                var current = style.Inherited.Color;
                var used = border with
                {
                    TopColor = border.TopColor.Resolve(current), RightColor = border.RightColor.Resolve(current),
                    BottomColor = border.BottomColor.Resolve(current), LeftColor = border.LeftColor.Resolve(current),
                };
                // A collapsed table border is centred on the cell's edges, half outside it, and has no radii.
                var borderShape = box.Fragment.PaintedBorder is not null && box.Box is TablePartBox { Part: TablePart.Cell }
                    ? new RoundedRect(box.Rect.Inset(-border.TopWidth / 2, -border.RightWidth / 2, -border.BottomWidth / 2, -border.LeftWidth / 2), default)
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
        private void PaintText(PaintBox box)
        {
            if (box.Fragment.Kind == FragmentKind.Box)
            {
                PaintImage(box);
                return;
            }
            var run = box.Fragment.Text!;
            var style = run.Style;
            if (style.Inherited.Visibility != Visibility.Visible || run.Run.Face is not { } face || run.GlyphEnd <= run.GlyphStart)
                return;
            var count = run.GlyphEnd - run.GlyphStart;
            var glyphs = new ushort[count];
            var origins = new Vector2[count];
            var baseline = box.Y + run.Ascent;
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
            var glyphRun = new GlyphRun(face, run.Run.Size, glyphs, origins);
            var decorations = style.Inherited.Decorations is null ? null
                : DecorationLines(box, run, face, baseline, style.Text.SkipInk == SkipInk.None ? null : glyphRun);
            if (decorations is not null)
                list.Items.AddRange(decorations.Where(d => d.Under).Select(d => d.Item));
            list.Items.Add(new DisplayItem(DisplayItemKind.Glyphs, Color: style.Inherited.Color, Glyphs: glyphRun));
            if (decorations is not null)
                list.Items.AddRange(decorations.Where(d => !d.Under).Select(d => d.Item));
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
        /// Gradient background layers, bottom layer first (css-backgrounds-3 §3): each sized by background-size in its
        /// background-origin box (a gradient has no size of its own, so auto, cover and contain fill the box), placed by
        /// background-position, repeated by background-repeat, and clipped to its background-clip box.
        /// </summary>
        // ponytail: space and round repeat like repeat; url() layers are drawn once images load.
        private void PaintBackgroundImages(PaintBox box, RoundedRect borderBox)
        {
            var style = box.Box.Style;
            var background = style.Background;
            for (var i = background.Images.Count - 1; i >= 0; i--)
            {
                if (background.Images[i] is not GradientImage { Computed: { } gradient })
                    continue;
                var origin = Area(borderBox, style, box.Fragment, background.Origins[i % background.Origins.Count]).Rect;
                var clip = Area(borderBox, style, box.Fragment, background.Clips[i % background.Clips.Count]);
                var size = background.Sizes[i % background.Sizes.Count];
                var (w, h) = size.Kind != BackgroundSizeKind.Explicit ? (origin.Width, origin.Height)
                    : (size.Width.Kind == SizeKind.Length ? size.Width.Length.Resolve(origin.Width) : origin.Width,
                       size.Height.Kind == SizeKind.Length ? size.Height.Length.Resolve(origin.Height) : origin.Height);
                if (w <= 0 || h <= 0)
                    continue;
                var position = background.Positions[i % background.Positions.Count];
                var (x, y) = (origin.X + position.X.Resolve(origin.Width - w), origin.Y + position.Y.Resolve(origin.Height - h));
                var repeat = background.Repeats[i % background.Repeats.Count];
                // Tiles cover the clip box in the axes that repeat, starting from one that touches the placed tile.
                var (x0, x1) = repeat.X == BackgroundRepeat.NoRepeat ? (x, x + w) : (x - MathF.Ceiling((x - clip.Rect.X) / w) * w, clip.Rect.Right);
                var (y0, y1) = repeat.Y == BackgroundRepeat.NoRepeat ? (y, y + h) : (y - MathF.Ceiling((y - clip.Rect.Y) / h) * h, clip.Rect.Bottom);
                if ((x1 - x0) / w * ((y1 - y0) / h) > 4096)
                    (x0, x1, y0, y1) = (x, x + w, y, y + h);

                SetClip(box.Clip);
                list.Items.Add(new DisplayItem(DisplayItemKind.PushClip, clip));
                for (var ty = y0; ty < y1 - 0.01f; ty += h)
                {
                    for (var tx = x0; tx < x1 - 0.01f; tx += w)
                    {
                        var tile = new RectF(tx, ty, w, h);
                        if (GradientGeometry.Build(gradient, tile, style.Inherited.Color) is { } paint)
                            list.Items.Add(new DisplayItem(DisplayItemKind.Fill, new RoundedRect(tile, default), Gradient: paint));
                    }
                }
                list.Items.Add(new DisplayItem(DisplayItemKind.Pop));
            }
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
