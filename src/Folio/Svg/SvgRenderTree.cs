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
}

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

/// <summary>A fill: its colour (opacity included) and whether the fill rule is evenodd.</summary>
internal readonly record struct SvgFill(CssColor Color, bool EvenOdd);

/// <summary>A stroke in user units: colour (opacity included), width, caps, joins, and a dash pattern with an even count.</summary>
internal sealed record SvgStroke(CssColor Color, float Width, StrokeLinecap Cap, StrokeLinejoin Join, float MiterLimit,
                                 IReadOnlyList<float>? Dashes, float DashOffset);

/// <summary>A path painted with a fill and/or a stroke, the stroke first when <paramref name="StrokeFirst"/>.</summary>
internal sealed record SvgShapeNode(Matrix3x2 Transform, float Opacity, IReadOnlyList<PathSegment> Path, SvgFill? Fill, SvgStroke? Stroke,
                                    bool StrokeFirst = false) : SvgRenderNode(Transform, Opacity);

/// <summary>
/// Builds the render tree of an outermost svg element from its DOM subtree and computed styles: viewports and viewBox
/// (https://www.w3.org/TR/SVG2/coords.html), transforms, the basic shapes and paths, and their fills and strokes
/// (https://www.w3.org/TR/SVG2/painting.html).
/// </summary>
// ponytail: structure (svg, g, a), shapes, paths and text, with clip paths; use, gradients, markers, masks and the
// other elements are not rendered yet (issue #97).
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

    private static List<SvgRenderNode> Children(ElementNode parent, Vector2 viewport, SvgContext context)
    {
        var nodes = new List<SvgRenderNode>();
        // Content that nests deeper than the stack allows is left out (study 16: limits stop work gracefully).
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
            return nodes;
        for (var child = parent.FirstChild; child is not null; child = child.NextSibling)
        {
            if (child is ElementNode { Name.Namespace: var ns } element && ns == Namespaces.Svg
                && element.ComputedStyle() is { Box.Display: not Display.None } style
                && Node(element, style, viewport, context) is { } node)
                nodes.Add(node);
        }
        return nodes;
    }

    private static SvgRenderNode? Node(ElementNode element, ComputedStyle style, Vector2 viewport, SvgContext context, bool clipping = false)
    {
        var node = Unclipped(element, style, viewport, context, clipping);
        if (node is null || style.Effects.ClipPath.Url is not { } url)
            return node;
        return context.Find(url) is { LocalName: "clipPath" } clip && clip.Name.Namespace == Namespaces.Svg
            ? node with { ClipPath = ClipPath(clip, node, viewport, context) }
            : node;
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
            var children = new List<SvgRenderNode>();
            for (var child = clip.FirstChild; child is not null; child = child.NextSibling)
            {
                if (child is ElementNode { Name.Namespace: var ns, LocalName: "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon" or "path" or "text" } element
                    && ns == Namespaces.Svg && element.ComputedStyle() is { Box.Display: not Display.None } childStyle
                    && Node(element, childStyle, viewport, context, clipping: true) is { } node)
                    children.Add(node);
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
                return Shape([new PathSegment('M', new(X("x1"), Y("y1"))), new PathSegment('L', new(X("x2"), Y("y2")))], canFill: false);
            case "polyline" or "polygon":
                return SvgGeometry.Polyline(element.GetAttribute("points"), element.LocalName == "polygon") is { } points ? Shape(points) : null;
            case "path":
                return PathDataParser.Parse(element.GetAttribute("d") ?? "", upToError: true) is { } path ? Shape(path) : null;
            case "text":
                return SvgText.Build(element, style, transform, viewport, context, clipping);
            default:
                return null;
        }

        SvgShapeNode? Shape(IReadOnlyList<PathSegment> path, bool canFill = true)
        {
            if (style.Inherited.Visibility != Visibility.Visible)
                return null;
            var svg = style.Svg;
            // In a clip path only the geometry counts, filled with the clip rule.
            if (clipping)
                return new SvgShapeNode(transform, 1, path, new SvgFill(CssColor.Black, svg.ClipRule == SvgFillRule.Evenodd), null);
            var fill = canFill && Color(svg.Fill, svg.FillOpacity, style) is { } fillColor
                ? new SvgFill(fillColor, svg.FillRule == SvgFillRule.Evenodd)
                : (SvgFill?)null;
            var stroke = Stroke(style, viewport);
            return fill is null && stroke is null ? null
                : new SvgShapeNode(transform, style.Box.Opacity, path, fill, stroke, svg.PaintOrder == PaintOrder.Stroke);
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

    // A paint's colour with its opacity, or null when nothing is painted. Paint servers (url()) paint their fallback
    // until stage 2 brings them.
    private static CssColor? Color(SvgPaint paint, float opacity, ComputedStyle style)
    {
        if (paint.Color is not { } color)
            return null;
        var c = color.Resolve(style.Inherited.Color);
        var alpha = c.A * opacity;
        return alpha > 0 ? c with { A = alpha } : null;
    }

    // https://www.w3.org/TR/SVG2/painting.html#StrokeProperties: no stroke at zero width; a dash array summing to zero,
    // like none, draws solid, and an odd one is repeated to make it even.
    private static SvgStroke? Stroke(ComputedStyle style, Vector2 viewport)
    {
        var svg = style.Svg;
        var diagonal = SvgGeometry.Diagonal(viewport);
        var width = svg.StrokeWidth.Resolve(diagonal);
        if (width <= 0 || Color(svg.Stroke, svg.StrokeOpacity, style) is not { } color)
            return null;
        var dashes = svg.StrokeDasharray.Dashes.Select(d => Math.Max(0, d.Resolve(diagonal))).ToList();
        if (dashes.Sum() <= 0)
            dashes.Clear();
        else if (dashes.Count % 2 == 1)
            dashes.AddRange(dashes.ToList());
        return new SvgStroke(color, width, svg.StrokeLinecap, svg.StrokeLinejoin, svg.StrokeMiterlimit, dashes.Count > 0 ? dashes : null,
            svg.StrokeDashoffset.Resolve(diagonal));
    }
}
