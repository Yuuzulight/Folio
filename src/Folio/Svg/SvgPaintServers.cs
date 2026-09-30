using System.Globalization;
using System.Numerics;
using Folio.Css;
using Folio.Dom;
using Folio.Style;

namespace Folio.Svg;

internal enum SvgSpreadMethod { Pad, Reflect, Repeat }

/// <summary>
/// A gradient paint in user units (https://www.w3.org/TR/SVG2/pservers.html): linear from <paramref name="P1"/> to
/// <paramref name="P2"/>, or radial from the focal circle at <paramref name="P2"/> of <paramref name="FocusRadius"/> to
/// the circle at <paramref name="P1"/> of <paramref name="Radius"/>, all in gradient space, which
/// <paramref name="Transform"/> maps to user space. Stop colours include the paint's opacity.
/// </summary>
internal sealed record SvgGradient(bool Radial, Vector2 P1, Vector2 P2, float Radius, float FocusRadius,
                                   IReadOnlyList<(float Offset, CssColor Color)> Stops, SvgSpreadMethod Spread, Matrix3x2 Transform);

/// <summary>A paint ready to draw: a colour with its opacity, or a gradient with the opacity in its stops.</summary>
internal readonly record struct SvgResolvedPaint(CssColor Color, SvgGradient? Gradient = null);

/// <summary>Paint servers: the paints fills and strokes reference with url().</summary>
internal sealed partial class SvgContext
{
    /// <summary>
    /// A fill or stroke paint (https://www.w3.org/TR/SVG2/painting.html#SpecifyingPaint): a colour, or the gradient a
    /// url() references, or its fallback when the reference is to nothing usable; null when nothing is painted.
    /// <paramref name="bounds"/> gives the element's bounding box, for gradients in object bounding box units.
    /// </summary>
    public SvgResolvedPaint? Paint(SvgPaint paint, float opacity, ComputedStyle style, Vector2 viewport, Func<SvgRect?> bounds)
    {
        if (paint.Url is { } url && Find(url) is { Name.Namespace: var ns, LocalName: "linearGradient" or "radialGradient" } gradient
            && ns == Namespaces.Svg)
        {
            return Gradient(gradient, opacity, viewport, bounds);
        }
        if (paint.Color is not { } color)
            return null;
        var c = color.Resolve(style.Inherited.Color);
        var alpha = c.A * opacity;
        return alpha > 0 ? new SvgResolvedPaint(c with { A = alpha }) : null;
    }

    // Gradients reference others with href to take the attributes and stops they leave out; a chain stops at a cycle or
    // after this many links.
    private const int MaxHrefChain = 32;

    // https://www.w3.org/TR/SVG2/pservers.html#LinearGradientElement and #RadialGradientElement.
    private SvgResolvedPaint? Gradient(ElementNode element, float opacity, Vector2 viewport, Func<SvgRect?> bounds)
    {
        var chain = new List<ElementNode> { element };
        for (var e = element; chain.Count < MaxHrefChain;)
        {
            var next = Find(e.GetAttribute("href") ?? e.GetAttribute("xlink:href"));
            if (next is not { LocalName: "linearGradient" or "radialGradient" } || next.Name.Namespace != Namespaces.Svg || chain.Contains(next))
                break;
            chain.Add(next);
            e = next;
        }
        var radial = element.LocalName == "radialGradient";
        // An attribute comes from the first gradient in the chain that has it; the geometry ones only from gradients of
        // the same kind.
        string? Attribute(string name, bool geometry = false) =>
            chain.Where(g => !geometry || g.LocalName == element.LocalName).Select(g => g.GetAttribute(name)).FirstOrDefault(v => v is not null);

        var stops = Stops(chain.FirstOrDefault(g => g.Children.OfType<ElementNode>().Any(IsStop)), opacity);
        if (stops.Count == 0)
            return null;
        if (stops.Count == 1)
            return new SvgResolvedPaint(stops[0].Color);

        var userSpace = Attribute("gradientUnits")?.Trim() == "userSpaceOnUse";
        var box = userSpace ? default : bounds();
        // In object bounding box units, a box with no width or height shows nothing.
        if (!userSpace && box is not { Width: > 0, Height: > 0 })
            return null;
        var toUser = SvgGeometry.ParseTransform(Attribute("gradientTransform")) ?? Matrix3x2.Identity;
        if (box is { } b)
            toUser *= new Matrix3x2(b.Width, 0, 0, b.Height, b.X, b.Y);
        if (!float.IsFinite(toUser.GetDeterminant()) || Math.Abs(toUser.GetDeterminant()) < 1e-12f)
            return null;
        var spread = Attribute("spreadMethod")?.Trim() switch
        {
            "reflect" => SvgSpreadMethod.Reflect,
            "repeat" => SvgSpreadMethod.Repeat,
            _ => SvgSpreadMethod.Pad,
        };

        // Lengths: fractions of the box (numbers or percentages) in object bounding box units, user lengths otherwise.
        var fontSize = element.ComputedStyle()?.Font.Size ?? 16;
        float Length(string name, string fallback, SvgAxis axis)
        {
            var text = Attribute(name, geometry: true) ?? fallback;
            if (userSpace)
                return SvgGeometry.Length(text, axis, viewport, fontSize, SvgGeometry.Length(fallback, axis, viewport, fontSize));
            var length = SvgGeometry.ParseLength(text, fontSize) ?? SvgGeometry.ParseLength(fallback, fontSize)!.Value;
            return length.Percent ? length.Value / 100 : length.Value;
        }

        SvgGradient gradient;
        if (radial)
        {
            var (cx, cy, r) = (Length("cx", "50%", SvgAxis.Horizontal), Length("cy", "50%", SvgAxis.Vertical), Length("r", "50%", SvgAxis.Other));
            var fx = Attribute("fx", geometry: true) is null ? cx : Length("fx", "50%", SvgAxis.Horizontal);
            var fy = Attribute("fy", geometry: true) is null ? cy : Length("fy", "50%", SvgAxis.Vertical);
            var fr = Math.Max(0, Length("fr", "0%", SvgAxis.Other));
            // A zero radius paints the last stop's colour.
            if (r <= 0)
                return new SvgResolvedPaint(stops[^1].Color);
            gradient = new SvgGradient(true, new Vector2(cx, cy), new Vector2(fx, fy), r, fr, stops, spread, toUser);
        }
        else
        {
            var start = new Vector2(Length("x1", "0%", SvgAxis.Horizontal), Length("y1", "0%", SvgAxis.Vertical));
            var end = new Vector2(Length("x2", "100%", SvgAxis.Horizontal), Length("y2", "0%", SvgAxis.Vertical));
            // A gradient with no length paints the last stop's colour.
            if (start == end)
                return new SvgResolvedPaint(stops[^1].Color);
            gradient = new SvgGradient(false, start, end, 0, 0, stops, spread, toUser);
        }
        return new SvgResolvedPaint(default, gradient);
    }

    private static bool IsStop(ElementNode element) => element.LocalName == "stop" && element.Name.Namespace == Namespaces.Svg;

    // https://www.w3.org/TR/SVG2/pservers.html#StopElement: offsets clamped to [0, 1] and never before the previous one.
    private static List<(float Offset, CssColor Color)> Stops(ElementNode? gradient, float opacity)
    {
        var stops = new List<(float Offset, CssColor Color)>();
        if (gradient is null)
            return stops;
        var previous = 0f;
        foreach (var stop in gradient.Children.OfType<ElementNode>().Where(IsStop))
        {
            var text = stop.GetAttribute("offset")?.Trim() ?? "0";
            var percent = text.EndsWith('%');
            var offset = float.TryParse(percent ? text[..^1] : text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? (percent ? value / 100 : value) : 0;
            offset = previous = Math.Max(previous, Math.Clamp(float.IsFinite(offset) ? offset : 0, 0, 1));
            var style = stop.ComputedStyle();
            var color = (style?.SvgStop.StopColor ?? CssColor.Black).Resolve(style?.Inherited.Color ?? CssColor.Black);
            stops.Add((offset, color with { A = color.A * (style?.SvgStop.StopOpacity ?? 1) * opacity }));
        }
        return stops;
    }
}
