using System.Numerics;
using System.Runtime.CompilerServices;
using Folio.Css;
using Folio.Dom;
using Folio.Style;

namespace Folio.Svg;

/// <summary>
/// A node of the SVG render tree (docs/study/13-svg.md): geometry in user units with paints already resolved, and no
/// link back to the DOM, so other producers (diagrams) can build one too. <see cref="Transform"/> maps the node's user
/// space into its parent's, in row-vector form; <see cref="Opacity"/> below 1 composites the node as a group.
/// </summary>
internal abstract record SvgRenderNode(Matrix3x2 Transform, float Opacity)
{
    /// <summary>What clips the node, in its user space (after <see cref="Transform"/>); null when nothing does.</summary>
    public SvgClipPath? ClipPath { get; init; }

    /// <summary>What masks the node, in its user space (after <see cref="Transform"/>); null when nothing does.</summary>
    public SvgMask? Mask { get; init; }

    /// <summary>What filters the node, in its user space (after <see cref="Transform"/>); null when nothing does.</summary>
    public SvgFilterChain? Filters { get; init; }
}

/// <summary>
/// A filter list with its url() references resolved (https://drafts.csswg.org/filter-effects-1/#FilterProperty): each
/// filter function, or its SVG filter element, in order, and what the element's units need: the filtered element's
/// bounding box and the viewport.
/// </summary>
internal sealed record SvgFilterChain(IReadOnlyList<(FilterFunction Function, SvgFilterReference? Reference)> Filters, CssColor CurrentColor)
{
    /// <summary>
    /// A filter list's chain; null when it filters nothing: none, or a reference to anything but a filter element, which
    /// ignores the whole list.
    /// </summary>
    public static SvgFilterChain? Of(FilterList list, CssColor currentColor, SvgRect? bounds, Vector2 viewport, SvgContext context)
    {
        var filters = new List<(FilterFunction, SvgFilterReference?)>();
        foreach (var function in list.Functions)
        {
            if (function.Name != "url")
                filters.Add((function, null));
            else if (context.Find(function.Url) is { LocalName: "filter" } element && element.Name.Namespace == Namespaces.Svg)
                filters.Add((function, SvgFilterReference.Get(element, bounds, viewport, context.Layout, context.Images)));
            else
                return null;
        }
        return filters.Count > 0 ? new SvgFilterChain(filters, currentColor) : null;
    }

    /// <summary>
    /// A CSS box's chain when its filter list references a filter element, for a border box of this size: user units
    /// are CSS pixels from its top-left corner, and it is the bounding box.
    /// </summary>
    public static SvgFilterChain? ForBox(ElementNode element, ComputedStyle style, float width, float height, Layout.LayoutContext layout) =>
        Of(style.Effects.Filter, style.Inherited.Color, new SvgRect(0, 0, width, height), new Vector2(width, height), new SvgContext(layout, element.OwnerDocument));
}

/// <summary>
/// A filter element as a layout uses it: the bounding box (only where its units need one) and the viewport its lengths
/// resolve against. Every element and box that uses the filter in the same way shares one, so painting builds its
/// primitives once.
/// </summary>
// ponytail: an element's bounding box includes its position, so SVG elements share a filter in bounding box units only
// when they share their box too; CSS boxes, whose box starts at 0, share it by size.
internal sealed class SvgFilterReference(ElementNode element, SvgRect? bounds, Vector2 viewport)
{
    public ElementNode Element { get; } = element;

    public SvgRect? Bounds { get; } = bounds;

    public Vector2 Viewport { get; } = viewport;

    /// <summary>Where feImage's images load from; none means they do not load.</summary>
    public Imaging.ImageLoader? Images { get; init; }

    /// <summary>The layout's reference for this filter element used with this bounding box and viewport.</summary>
    public static SvgFilterReference Get(ElementNode element, SvgRect? bounds, Vector2 viewport, Layout.LayoutContext layout, Imaging.ImageLoader? images)
    {
        static bool BoxUnits(ElementNode e, string name, bool byDefault) => e.GetAttribute(name)?.Trim() is { } units
            ? units == "objectBoundingBox" || units != "userSpaceOnUse" && byDefault
            : byDefault;
        var key = (element, BoxUnits(element, "filterUnits", true) || BoxUnits(element, "primitiveUnits", false) ? bounds : null, viewport);
        if (!layout.SvgFilters.TryGetValue(key, out var reference))
            layout.SvgFilters[key] = reference = new SvgFilterReference(key.element, key.Item2, viewport) { Images = images };
        return reference;
    }
}

/// <summary>
/// A mask (https://drafts.csswg.org/css-masking-1/#svg-masks): <paramref name="Children"/>, which
/// <paramref name="ContentTransform"/> maps into the masked node's user space, drawn inside <paramref name="Region"/>.
/// The node stays where they are opaque: their luminance times their alpha when <paramref name="Luminance"/>, else
/// their alpha. Outside the region it is masked away.
/// </summary>
internal sealed record SvgMask(IReadOnlyList<SvgRenderNode> Children, Matrix3x2 ContentTransform, SvgRect Region, bool Luminance);

/// <summary>
/// A clip region (https://drafts.csswg.org/css-masking-1/#svg-clipping-paths): the union of <paramref name="Children"/>
/// (shapes filled opaque with their clip-rule, and text), which <paramref name="Transform"/> maps into the clipped
/// node's user space, itself clipped by <paramref name="ClipPath"/>. No children clip everything away.
/// </summary>
internal sealed record SvgClipPath(IReadOnlyList<SvgRenderNode> Children, Matrix3x2 Transform, SvgClipPath? ClipPath = null)
{
    public static SvgClipPath Everything { get; } = new([], Matrix3x2.Identity);
}

/// <summary>A group of nodes in paint order, clipped to <paramref name="Clip"/> (in the node's own user space, after its transform) when given.</summary>
internal sealed record SvgContainerNode(Matrix3x2 Transform, float Opacity, IReadOnlyList<SvgRenderNode> Children, SvgRect? Clip = null)
    : SvgRenderNode(Transform, Opacity);

/// <summary>
/// A foreignObject (https://www.w3.org/TR/SVG2/embedded.html#ForeignObjectElement): its HTML content laid out by CSS
/// in a box of <paramref name="Rect"/>'s size, placed at its corner and clipped to it.
/// </summary>
internal sealed record SvgForeignNode(Matrix3x2 Transform, float Opacity, SvgRect Rect, Layout.Fragment Content) : SvgRenderNode(Transform, Opacity);

/// <summary>A fill: its paint and whether the fill rule is evenodd.</summary>
internal readonly record struct SvgFill(SvgResolvedPaint Paint, bool EvenOdd);

/// <summary>A stroke in user units: its paint, width, caps, joins, and a dash pattern with an even count.</summary>
internal sealed record SvgStroke(SvgResolvedPaint Paint, float Width, StrokeLinecap Cap, StrokeLinejoin Join, float MiterLimit,
                                 IReadOnlyList<float>? Dashes, float DashOffset);

/// <summary>A path painted with a fill and/or a stroke, the stroke first when <paramref name="StrokeFirst"/>.</summary>
/// <param name="Markers">The markers drawn over the shape after its fill and stroke, in its user space.</param>
internal sealed record SvgShapeNode(Matrix3x2 Transform, float Opacity, IReadOnlyList<PathSegment> Path, SvgFill? Fill, SvgStroke? Stroke,
                                    bool StrokeFirst = false, IReadOnlyList<SvgRenderNode>? Markers = null) : SvgRenderNode(Transform, Opacity);

/// <summary>
/// Builds the render tree of an outermost svg element from its DOM subtree and computed styles: viewports and viewBox
/// (https://www.w3.org/TR/SVG2/coords.html), transforms, the basic shapes and paths, and their fills and strokes
/// (https://www.w3.org/TR/SVG2/painting.html).
/// </summary>
// ponytail: structure (svg, g, a, use with symbol), shapes, paths and text, painted with colours and gradients and
// clipped by clip paths; markers, masks, patterns and the other elements are not rendered yet (issue #97).
internal static class SvgRenderTree
{
    /// <summary>
    /// The natural size of an outermost svg element (https://www.w3.org/TR/SVG2/coords.html#SizingSVGInCSS): width and
    /// height when they are absolute lengths, and the aspect ratio from them or else from the viewBox.
    /// </summary>
    public static (float? Width, float? Height, float? Ratio) NaturalSize(ElementNode svg, float fontSize)
    {
        static float? Absolute((float Value, bool Percent)? length) => length is { Percent: false, Value: >= 0 } l ? l.Value : null;
        var width = Absolute(SvgGeometry.ParseLength(svg.GetAttribute("width"), fontSize));
        var height = Absolute(SvgGeometry.ParseLength(svg.GetAttribute("height"), fontSize));
        var ratio = width > 0 && height > 0 ? width / height
            : SvgGeometry.ParseViewBox(svg.GetAttribute("viewBox")) is { Width: > 0, Height: > 0 } box ? box.Width / box.Height
            : null;
        return (width, height, ratio);
    }

    /// <summary>The render tree of an outermost svg element laid out in a content box of this size, or null when nothing shows.</summary>
    public static SvgContainerNode? Build(ElementNode svg, float width, float height, Layout.LayoutContext layout)
    {
        var style = svg.ComputedStyle();
        var context = new SvgContext(layout, svg.OwnerDocument);
        // Its transform, opacity and clip path belong to its CSS box, which paints them.
        return style is null ? null : Viewport(svg, style, new SvgRect(0, 0, width, height), Matrix3x2.Identity, 1, context);
    }

    /// <summary>
    /// The clip path a CSS box's <c>clip-path: url()</c> references (https://drafts.csswg.org/css-masking-1/#the-clip-path),
    /// for a border box of this size: user units are CSS pixels from the border box's top-left corner, and the border
    /// box is the bounding box. Null when the reference is not to a clipPath element, which then clips nothing.
    /// </summary>
    // ponytail: percentages in the clip path resolve against the border box rather than the clipPath's nearest viewport.
    public static SvgClipPath? BoxClipPath(ElementNode element, string url, float width, float height, Layout.LayoutContext layout)
    {
        var context = new SvgContext(layout, element.OwnerDocument);
        if (context.Find(url) is not { LocalName: "clipPath" } clip || clip.Name.Namespace != Namespaces.Svg)
            return null;
        return ClipPath(clip, Rectangle(new SvgRect(0, 0, width, height), null), new Vector2(width, height), context);
    }

    // A rectangle as a shape filled with this fill (or not filled).
    private static SvgShapeNode Rectangle(SvgRect r, SvgFill? fill) => new(Matrix3x2.Identity, 1,
        [new('M', new(r.X, r.Y)), new('L', new(r.X + r.Width, r.Y)), new('L', new(r.X + r.Width, r.Y + r.Height)), new('L', new(r.X, r.Y + r.Height)), new('Z')],
        fill, null);

    // An svg element's viewport: its viewBox mapped into the rectangle, the content clipped to it unless overflow is
    // visible. A viewBox with a zero size disables rendering (https://www.w3.org/TR/SVG2/coords.html#ViewBoxAttribute).
    private static SvgContainerNode? Viewport(ElementNode svg, ComputedStyle style, SvgRect rect, Matrix3x2 transform, float opacity,
                                               SvgContext context)
    {
        var viewBox = SvgGeometry.ParseViewBox(svg.GetAttribute("viewBox"));
        if (viewBox is { Width: 0 } or { Height: 0 } || rect.Width <= 0 || rect.Height <= 0)
            return null;
        var map = viewBox is { } box
            ? SvgGeometry.ViewBoxTransform(box, svg.GetAttribute("preserveAspectRatio"), rect)
            : Matrix3x2.CreateTranslation(rect.X, rect.Y);
        if (!Invertible(map))
            return null;
        var size = viewBox is { } b ? new Vector2(b.Width, b.Height) : new Vector2(rect.Width, rect.Height);
        var clips = style.Box.OverflowX != Overflow.Visible || style.Box.OverflowY != Overflow.Visible;
        // The element's own transform moves its viewport rectangle too; the viewBox maps into that rectangle.
        return new SvgContainerNode(transform, opacity, [new SvgContainerNode(map, 1, Children(svg, size, context))], clips ? rect : null);
    }

    internal static List<SvgRenderNode> Children(ElementNode parent, Vector2 viewport, SvgContext context)
    {
        var nodes = new List<SvgRenderNode>();
        // Content that nests deeper than the stack allows is left out (study 16: limits stop work gracefully).
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
            return nodes;
        for (var child = parent.FirstChild; child is not null; child = child.NextSibling)
        {
            if (child is ElementNode { Name.Namespace: var ns } element && ns == Namespaces.Svg
                && context.Style(element) is { Box.Display: not Display.None } style
                && Node(element, style, viewport, context) is { } node)
                nodes.Add(node);
        }
        return nodes;
    }

    private static SvgRenderNode? Node(ElementNode element, ComputedStyle style, Vector2 viewport, SvgContext context, bool clipping = false)
    {
        var node = Unclipped(element, style, viewport, context, clipping);
        if (node is null)
            return null;
        if (style.Effects.ClipPath.Url is { } url && context.Find(url) is { LocalName: "clipPath" } clip && clip.Name.Namespace == Namespaces.Svg)
            node = node with { ClipPath = ClipPath(clip, node, viewport, context) };
        // In a clip path only the geometry counts, so masks and filters there are ignored.
        if (!clipping && MaskReference(style.Mask, context) is var (mask, mode))
            node = node with { Mask = Mask(mask, mode, Bounds(node), viewport, context) };
        if (!clipping && !style.Effects.Filter.IsNone)
            node = node with { Filters = SvgFilterChain.Of(style.Effects.Filter, style.Inherited.Color, Bounds(node), viewport, context) };
        return node;
    }

    /// <summary>
    /// The masks a CSS box's mask layers reference (https://drafts.csswg.org/css-masking-1/#the-mask-image), by layer, for
    /// a border box of this size: user units are CSS pixels from its top-left corner, and it is the bounding box. Null
    /// when no layer references a mask element.
    /// </summary>
    public static SvgMask?[]? BoxMasks(ElementNode element, MaskGroup masks, float width, float height, Layout.LayoutContext layout)
    {
        var context = new SvgContext(layout, element.OwnerDocument);
        var result = new SvgMask?[masks.Images.Count];
        var any = false;
        for (var i = 0; i < result.Length; i++)
        {
            if (masks.Images[i] is UrlImage url && context.Find(url.Url) is { LocalName: "mask" } mask && mask.Name.Namespace == Namespaces.Svg)
            {
                result[i] = Mask(mask, masks.Modes[i % masks.Modes.Count], new SvgRect(0, 0, width, height), new Vector2(width, height), context);
                any = true;
            }
        }
        return any ? result : null;
    }

    // The top mask layer that references an SVG mask element, and its mask-mode.
    // ponytail: other mask layers (images, gradients, more references) are ignored on SVG elements.
    private static (ElementNode Mask, MaskMode Mode)? MaskReference(MaskGroup masks, SvgContext context)
    {
        for (var i = 0; i < masks.Images.Count; i++)
        {
            if (masks.Images[i] is UrlImage url && context.Find(url.Url) is { LocalName: "mask" } mask && mask.Name.Namespace == Namespaces.Svg)
                return (mask, masks.Modes[i % masks.Modes.Count]);
        }
        return null;
    }

    /// <summary>
    /// The mask an element references (https://drafts.csswg.org/css-masking-1/#MaskElement) for a masked node with this
    /// bounding box: its region from x, y, width and height in maskUnits (the bounding box by default, from -10% to
    /// 120%), its content in maskContentUnits (user space by default), luminance or alpha by mask-type unless the
    /// layer's mask-mode says. A reference back to a mask being built, or a bounding box with no area where bounding box
    /// units need one, masks everything away.
    /// </summary>
    private static SvgMask Mask(ElementNode mask, MaskMode mode, SvgRect? bounds, Vector2 viewport, SvgContext context)
    {
        var nothing = new SvgMask([], Matrix3x2.Identity, default, false);
        if (!context.Masking.Add(mask) || !RuntimeHelpers.TryEnsureSufficientExecutionStack())
            return nothing;
        try
        {
            var style = mask.ComputedStyle();
            var fontSize = style?.Font.Size ?? 16;
            var luminance = mode == MaskMode.MatchSource ? style?.Mask.Type != MaskType.Alpha : mode == MaskMode.Luminance;
            bool BoxUnits(string name, bool byDefault) => mask.GetAttribute(name)?.Trim() is { } units
                ? units == "objectBoundingBox" || units != "userSpaceOnUse" && byDefault
                : byDefault;
            var (regionInBox, contentInBox) = (BoxUnits("maskUnits", true), BoxUnits("maskContentUnits", false));
            if ((regionInBox || contentInBox) && bounds is not { Width: > 0, Height: > 0 })
                return nothing;
            var box = bounds ?? default;

            float Length(string name, string fallback, SvgAxis axis)
            {
                var text = mask.GetAttribute(name) ?? fallback;
                if (!regionInBox)
                    return SvgGeometry.Length(text, axis, viewport, fontSize, SvgGeometry.Length(fallback, axis, viewport, fontSize));
                var length = SvgGeometry.ParseLength(text, fontSize) ?? SvgGeometry.ParseLength(fallback, fontSize)!.Value;
                var fraction = length.Percent ? length.Value / 100 : length.Value;
                return axis == SvgAxis.Horizontal ? fraction * box.Width : fraction * box.Height;
            }
            var (x, y) = (Length("x", "-10%", SvgAxis.Horizontal), Length("y", "-10%", SvgAxis.Vertical));
            var (width, height) = (Length("width", "120%", SvgAxis.Horizontal), Length("height", "120%", SvgAxis.Vertical));
            // A region with no area masks everything away.
            if (width <= 0 || height <= 0)
                return nothing;
            var region = regionInBox ? new SvgRect(box.X + x, box.Y + y, width, height) : new SvgRect(x, y, width, height);
            var content = contentInBox ? new Matrix3x2(box.Width, 0, 0, box.Height, box.X, box.Y) : Matrix3x2.Identity;
            var contentViewport = contentInBox ? Vector2.One : viewport;
            var (children, overBudget) = context.ReferencedContent(mask, contentViewport, () => Children(mask, contentViewport, context));
            // With the budget for referenced content spent, the mask keeps everything in its region.
            return overBudget
                ? new SvgMask([Rectangle(region, new SvgFill(new SvgResolvedPaint(new CssColor(1, 1, 1, 1)), false))], Matrix3x2.Identity, region, false)
                : new SvgMask(children, content, region, luminance);
        }
        finally
        {
            context.Masking.Remove(mask);
        }
    }

    // The clip path an element references (https://drafts.csswg.org/css-masking-1/#ClipPathElement): its shapes and
    // text, in user space or the clipped node's bounding box, and its own clip path. A reference back to a clip path
    // being built, or a bounding box with no area in bounding box units, clips everything, so the element is not shown.
    private static SvgClipPath ClipPath(ElementNode clip, SvgRenderNode clipped, Vector2 viewport, SvgContext context)
    {
        if (!context.Clipping.Add(clip) || !RuntimeHelpers.TryEnsureSufficientExecutionStack())
            return SvgClipPath.Everything;
        try
        {
            var style = clip.ComputedStyle();
            var toUser = SvgGeometry.ParseTransform(clip.GetAttribute("transform")) ?? Matrix3x2.Identity;
            if (clip.GetAttribute("clipPathUnits")?.Trim() == "objectBoundingBox")
            {
                if (Bounds(clipped) is not { Width: > 0, Height: > 0 } box)
                    return SvgClipPath.Everything;
                toUser *= new Matrix3x2(box.Width, 0, 0, box.Height, box.X, box.Y);
                viewport = Vector2.One;
            }
            var shapesViewport = viewport;
            var (children, overBudget) = context.ReferencedContent(clip, viewport, () =>
            {
                var nodes = new List<SvgRenderNode>();
                for (var child = clip.FirstChild; child is not null; child = child.NextSibling)
                {
                    if (child is ElementNode { Name.Namespace: var ns, LocalName: "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon" or "path" or "text" } element
                        && ns == Namespaces.Svg && element.ComputedStyle() is { Box.Display: not Display.None } childStyle
                        && Node(element, childStyle, shapesViewport, context, clipping: true) is { } node)
                        nodes.Add(node);
                }
                return nodes;
            });
            // With the budget for referenced content spent, the region is the bounding box of its shapes.
            if (overBudget)
            {
                children = Bounds(new SvgContainerNode(Matrix3x2.Identity, 1, children)) is { } bounds
                    ? [Rectangle(bounds, new SvgFill(new SvgResolvedPaint(CssColor.Black), false))]
                    : [];
            }
            // The clipPath's own clip path clips its region.
            var own = style?.Effects.ClipPath.Url is { } url && context.Find(url) is { LocalName: "clipPath" } next && next.Name.Namespace == Namespaces.Svg
                ? ClipPath(next, clipped, viewport, context)
                : null;
            return new SvgClipPath(children, toUser, own);
        }
        finally
        {
            context.Clipping.Remove(clip);
        }
    }

    /// <summary>A node's bounding box in its own user space (https://www.w3.org/TR/SVG2/coords.html#BoundingBoxes).</summary>
    public static SvgRect? Bounds(SvgRenderNode node) => node switch
    {
        SvgShapeNode shape => SvgGeometry.Bounds(shape.Path),
        SvgTextNode text => text.Bounds,
        SvgContainerNode container => container.Children.Select(c => Bounds(c) is { } b ? SvgGeometry.Transform(b, c.Transform) : (SvgRect?)null)
            .Aggregate((SvgRect?)null, SvgGeometry.Union),
        _ => null,
    };

    private static SvgRenderNode? Unclipped(ElementNode element, ComputedStyle style, Vector2 viewport, SvgContext context, bool clipping)
    {
        var transform = Transform(element, style, viewport);
        // A transform that cannot be inverted draws nothing.
        if (!Invertible(transform))
            return null;
        var fontSize = style.Font.Size;
        float X(string name) => SvgGeometry.Length(element.GetAttribute(name), SvgAxis.Horizontal, viewport, fontSize);
        float Y(string name) => SvgGeometry.Length(element.GetAttribute(name), SvgAxis.Vertical, viewport, fontSize);
        float Other(string name) => SvgGeometry.Length(element.GetAttribute(name), SvgAxis.Other, viewport, fontSize);
        bool Has(string name) => SvgGeometry.ParseLength(element.GetAttribute(name), fontSize) is not null;

        switch (element.LocalName)
        {
            case "g" or "a":
                return new SvgContainerNode(transform, style.Box.Opacity, Children(element, viewport, context));
            case "use":
                return Use(element, style, transform, viewport, context);
            case "svg":
            {
                // A nested viewport: width and height default to 100%.
                var rect = new SvgRect(X("x"), Y("y"),
                    Has("width") ? X("width") : viewport.X, Has("height") ? Y("height") : viewport.Y);
                return Viewport(element, style, rect, transform, style.Box.Opacity, context);
            }
            case "rect":
            {
                var (width, height) = (X("width"), Y("height"));
                if (width <= 0 || height <= 0)
                    return null;
                // An auto radius takes the other's value; both are clamped to half the side (SVG 2 §10.2).
                var (rx, ry) = (Has("rx") ? Math.Max(0, X("rx")) : (float?)null, Has("ry") ? Math.Max(0, Y("ry")) : (float?)null);
                var (cornerX, cornerY) = (rx ?? ry ?? 0, ry ?? rx ?? 0);
                return Shape(SvgGeometry.Rect(X("x"), Y("y"), width, height, Math.Min(cornerX, width / 2), Math.Min(cornerY, height / 2)));
            }
            case "circle":
            {
                var r = Other("r");
                return r > 0 ? Shape(SvgGeometry.Ellipse(X("cx"), Y("cy"), r, r)) : null;
            }
            case "ellipse":
            {
                var (rx, ry) = (Has("rx") ? X("rx") : (float?)null, Has("ry") ? Y("ry") : (float?)null);
                var (radiusX, radiusY) = (rx ?? ry ?? 0, ry ?? rx ?? 0);
                return radiusX > 0 && radiusY > 0 ? Shape(SvgGeometry.Ellipse(X("cx"), Y("cy"), radiusX, radiusY)) : null;
            }
            case "line":
                return Shape([new PathSegment('M', new(X("x1"), Y("y1"))), new PathSegment('L', new(X("x2"), Y("y2")))], canFill: false, markable: true);
            case "polyline" or "polygon":
                return SvgGeometry.Polyline(element.GetAttribute("points"), element.LocalName == "polygon") is { } points ? Shape(points, markable: true) : null;
            case "path":
                return PathDataParser.Parse(element.GetAttribute("d") ?? "", upToError: true) is { } path ? Shape(path, markable: true) : null;
            case "text":
                return SvgText.Build(element, style, transform, viewport, context, clipping);
            case "foreignObject" when !clipping:
            {
                // Its children are laid out as the content of a block of its width and height.
                var rect = new SvgRect(X("x"), Y("y"), X("width"), Y("height"));
                if (rect.Width <= 0 || rect.Height <= 0 || style.Inherited.Visibility != Visibility.Visible
                    || Layout.BoxTreeBuilder.BuildContents(element) is not { } contents)
                    return null;
                var content = Layout.BlockLayout.Layout(contents,
                    new Layout.ConstraintSpace(rect.Width, rect.Height, FixedWidth: rect.Width, FixedHeight: rect.Height), context.Layout);
                return new SvgForeignNode(transform, style.Box.Opacity, rect, content);
            }
            default:
                return null;
        }

        SvgShapeNode? Shape(IReadOnlyList<PathSegment> path, bool canFill = true, bool markable = false)
        {
            if (style.Inherited.Visibility != Visibility.Visible)
                return null;
            var svg = style.Svg;
            // In a clip path only the geometry counts, filled with the clip rule.
            if (clipping)
                return new SvgShapeNode(transform, 1, path, new SvgFill(new SvgResolvedPaint(CssColor.Black), svg.ClipRule == SvgFillRule.Evenodd), null);
            SvgRect? bounds = null;
            SvgRect? Bounds() => bounds ??= SvgGeometry.Bounds(path);
            var fill = canFill && context.Paint(svg.Fill, svg.FillOpacity, style, viewport, Bounds) is { } fillPaint
                ? new SvgFill(fillPaint, svg.FillRule == SvgFillRule.Evenodd)
                : (SvgFill?)null;
            var stroke = Stroke(style, viewport, context, Bounds);
            var markers = markable ? Markers(path, style, viewport, context) : null;
            return fill is null && stroke is null && markers is null ? null
                : new SvgShapeNode(transform, style.Box.Opacity, path, fill, stroke, svg.PaintOrder == PaintOrder.Stroke, markers);
        }
    }

    // The markers of a path, line, polyline or polygon (https://www.w3.org/TR/SVG2/painting.html#Markers):
    // marker-start at the first vertex, marker-end at the last, marker-mid at the others. Null when there are none.
    private static List<SvgRenderNode>? Markers(IReadOnlyList<PathSegment> path, ComputedStyle style, Vector2 viewport, SvgContext context)
    {
        var svg = style.Svg;
        if (svg.MarkerStart.Url is null && svg.MarkerMid.Url is null && svg.MarkerEnd.Url is null)
            return null;
        ElementNode? MarkerElement(MarkerReference reference) =>
            context.Find(reference.Url) is { LocalName: "marker" } marker && marker.Name.Namespace == Namespaces.Svg ? marker : null;
        var (start, mid, end) = (MarkerElement(svg.MarkerStart), MarkerElement(svg.MarkerMid), MarkerElement(svg.MarkerEnd));
        var strokeWidth = svg.StrokeWidth.Resolve(SvgGeometry.Diagonal(viewport));
        var vertices = SvgGeometry.Vertices(path);
        var markers = new List<SvgRenderNode>();
        for (var i = 0; i < vertices.Count; i++)
        {
            var (point, incoming, outgoing) = vertices[i];
            var angle = SvgGeometry.MarkerAngle(incoming, outgoing);
            if (i == 0 && start is not null && Marker(start, point, angle, true, strokeWidth, context) is { } first)
                markers.Add(first);
            if (i > 0 && i < vertices.Count - 1 && mid is not null && Marker(mid, point, angle, false, strokeWidth, context) is { } middle)
                markers.Add(middle);
            if (i == vertices.Count - 1 && end is not null && Marker(end, point, angle, false, strokeWidth, context) is { } last)
                markers.Add(last);
        }
        return markers.Count > 0 ? markers : null;
    }

    // One marker instance (https://www.w3.org/TR/SVG2/painting.html#MarkerElement): its content, in its own style,
    // mapped by its viewBox into markerWidth by markerHeight (scaled by the stroke width unless markerUnits is
    // userSpaceOnUse), with (refX, refY) at the vertex, turned by orient, and clipped to that viewport unless overflow
    // is visible. A marker used inside its own content draws nothing.
    private static SvgRenderNode? Marker(ElementNode marker, Vector2 vertex, float autoAngle, bool isStart, float strokeWidth, SvgContext context)
    {
        if (context.Style(marker) is not { } style || !RuntimeHelpers.TryEnsureSufficientExecutionStack() || !context.Marking.Add(marker))
            return null;
        try
        {
            var fontSize = style.Font.Size;
            float Number(string name, float fallback) =>
                SvgGeometry.ParseLength(marker.GetAttribute(name), fontSize) is { Percent: false } length ? length.Value : fallback;
            var (width, height) = (Number("markerWidth", 3), Number("markerHeight", 3));
            var scale = marker.GetAttribute("markerUnits")?.Trim() == "userSpaceOnUse" ? 1 : strokeWidth;
            var viewBox = SvgGeometry.ParseViewBox(marker.GetAttribute("viewBox"));
            if (width <= 0 || height <= 0 || scale <= 0 || viewBox is { Width: 0 } or { Height: 0 })
                return null;
            var rect = new SvgRect(0, 0, width, height);
            var map = viewBox is { } box ? SvgGeometry.ViewBoxTransform(box, marker.GetAttribute("preserveAspectRatio"), rect) : Matrix3x2.Identity;
            var reference = Vector2.Transform(new Vector2(
                Reference("refX", viewBox?.X ?? 0, viewBox?.Width ?? width, "left", "right"),
                Reference("refY", viewBox?.Y ?? 0, viewBox?.Height ?? height, "top", "bottom")), map);
            var angle = marker.GetAttribute("orient")?.Trim() switch
            {
                "auto" => autoAngle,
                "auto-start-reverse" => isStart ? autoAngle + 180 : autoAngle,
                var orient => SvgGeometry.ParseAngle(orient) ?? 0,
            };
            var place = Matrix3x2.CreateTranslation(-reference) * Matrix3x2.CreateScale(scale)
                        * Matrix3x2.CreateRotation(angle * MathF.PI / 180) * Matrix3x2.CreateTranslation(vertex);
            var size = viewBox is { } b ? new Vector2(b.Width, b.Height) : new Vector2(width, height);
            var clips = style.Box.OverflowX != Overflow.Visible || style.Box.OverflowY != Overflow.Visible;
            return new SvgContainerNode(place, style.Box.Opacity, [new SvgContainerNode(map, 1, Children(marker, size, context))], clips ? rect : null);

            // refX and refY: a number in the viewBox, or a keyword for its start, centre or end.
            float Reference(string name, float origin, float extent, string startKeyword, string endKeyword)
            {
                var value = marker.GetAttribute(name)?.Trim();
                return value == startKeyword ? origin : value == "center" ? origin + extent / 2 : value == endKeyword ? origin + extent : Number(name, 0);
            }
        }
        finally
        {
            context.Marking.Remove(marker);
        }
    }

    // A use element (https://www.w3.org/TR/SVG2/struct.html#UseElement): its target instanced as a child placed at x, y
    // after the use's own transform, a symbol or svg target as a viewport sized by the use. A target that is the use
    // itself, an ancestor of it, or an element already being instanced further out draws nothing.
    private static SvgRenderNode? Use(ElementNode use, ComputedStyle style, Matrix3x2 transform, Vector2 viewport, SvgContext context)
    {
        if (context.Find(use.GetAttribute("href") ?? use.GetAttribute("xlink:href")) is not { } target || target.Name.Namespace != Namespaces.Svg
            || context.Using.Contains(target) || !RuntimeHelpers.TryEnsureSufficientExecutionStack())
            return null;
        for (Node? n = use; n is not null; n = n.Parent)
        {
            if (n == target)
                return null;
        }
        if (!context.BeginInstance(target, style))
            return null;
        try
        {
            if (context.Style(target) is not { Box.Display: not Display.None } targetStyle)
                return null;
            var fontSize = style.Font.Size;
            var place = Matrix3x2.CreateTranslation(SvgGeometry.Length(use.GetAttribute("x"), SvgAxis.Horizontal, viewport, fontSize),
                SvgGeometry.Length(use.GetAttribute("y"), SvgAxis.Vertical, viewport, fontSize));
            SvgRenderNode? content;
            if (target.LocalName is "symbol" or "svg")
            {
                // The use's width and height, else the target's, else 100%.
                float Size(string name, SvgAxis axis, float whole) =>
                    SvgGeometry.ParseLength(use.GetAttribute(name), fontSize) is not null ? SvgGeometry.Length(use.GetAttribute(name), axis, viewport, fontSize)
                    : SvgGeometry.ParseLength(target.GetAttribute(name), targetStyle.Font.Size) is not null
                        ? SvgGeometry.Length(target.GetAttribute(name), axis, viewport, targetStyle.Font.Size)
                        : whole;
                var rect = new SvgRect(0, 0, Size("width", SvgAxis.Horizontal, viewport.X), Size("height", SvgAxis.Vertical, viewport.Y));
                content = Viewport(target, targetStyle, rect, Matrix3x2.Identity, targetStyle.Box.Opacity, context);
            }
            else
            {
                content = Node(target, targetStyle, viewport, context);
            }
            return content is null ? null : new SvgContainerNode(place * transform, style.Box.Opacity, [content]);
        }
        finally
        {
            context.EndInstance(target);
        }
    }

    // Finite and not singular: what can be drawn through.
    private static bool Invertible(Matrix3x2 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M31)
        && float.IsFinite(m.M32) && Matrix3x2.Invert(m, out _);

    // The CSS transform property when set (its reference box the view box, https://www.w3.org/TR/css-transforms-1/#transform-box),
    // else the transform attribute.
    private static Matrix3x2 Transform(ElementNode element, ComputedStyle style, Vector2 viewport) =>
        style.Transform.IsTransformed ? style.Transform.Matrix2D(viewport.X, viewport.Y)
        : SvgGeometry.ParseTransform(element.GetAttribute("transform")) ?? Matrix3x2.Identity;

    // https://www.w3.org/TR/SVG2/painting.html#StrokeProperties: no stroke at zero width; a dash array summing to zero,
    // like none, draws solid, and an odd one is repeated to make it even.
    private static SvgStroke? Stroke(ComputedStyle style, Vector2 viewport, SvgContext context, Func<SvgRect?> bounds)
    {
        var svg = style.Svg;
        var diagonal = SvgGeometry.Diagonal(viewport);
        var width = svg.StrokeWidth.Resolve(diagonal);
        if (width <= 0 || context.Paint(svg.Stroke, svg.StrokeOpacity, style, viewport, bounds) is not { } paint)
            return null;
        var dashes = svg.StrokeDasharray.Dashes.Select(d => Math.Max(0, d.Resolve(diagonal))).ToList();
        if (dashes.Sum() <= 0)
            dashes.Clear();
        else if (dashes.Count % 2 == 1)
            dashes.AddRange(dashes.ToList());
        return new SvgStroke(paint, width, svg.StrokeLinecap, svg.StrokeLinejoin, svg.StrokeMiterlimit, dashes.Count > 0 ? dashes : null,
            svg.StrokeDashoffset.Resolve(diagonal));
    }
}
