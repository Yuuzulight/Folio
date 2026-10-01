using Folio.Style;

namespace Folio.Css;

internal enum GradientFunction { Linear, Radial, Conic }

/// <summary>One specified stop: a colour with up to two positions, or a transition hint (a position with no colour).</summary>
internal sealed record StopSpecified(CssValue? Color, CssValue? Position, CssValue? Position2);

/// <summary>
/// A specified gradient (https://www.w3.org/TR/css-images-4/#gradients). Linear: an angle in degrees or a side or corner
/// (-1, 0 or 1 per axis, y down). Radial: circle or ellipse, an extent keyword or explicit radii, a centre. Conic: a
/// start angle in degrees and a centre. Conic stop positions are kept as percentages of a full turn.
/// </summary>
internal sealed record GradientSpecified(
    GradientFunction Kind, bool Repeating, float Angle, (int X, int Y)? ToCorner, bool Circle, string Extent,
    CssValue? SizeX, CssValue? SizeY, PositionSpecified? At, ColorSpace Space, HueInterpolation Hue, IReadOnlyList<StopSpecified> Stops);

/// <summary>A computed stop: a colour (currentcolor kept symbolic) or a hint, with an optional position.</summary>
internal readonly record struct ComputedStop(CssColor? Color, LengthPercentage? Position);

/// <summary>A computed gradient: its specified form with lengths in px and colours computed; stops with two positions are two stops.</summary>
internal sealed record ComputedGradient(
    GradientFunction Kind, bool Repeating, float Angle, (int X, int Y)? ToCorner, bool Circle, string Extent,
    LengthPercentage? SizeX, LengthPercentage? SizeY, Style.BackgroundPosition At, ColorSpace Space, HueInterpolation Hue,
    IReadOnlyList<ComputedStop> Stops);

/// <summary>Parsing and computing of gradient functions.</summary>
internal static class GradientParsing
{
    public static GradientSpecified? Parse(string function, ValueReader r)
    {
        var repeating = function.StartsWith("repeating-", StringComparison.Ordinal);
        var kind = function.EndsWith("linear-gradient", StringComparison.Ordinal) ? GradientFunction.Linear
            : function.EndsWith("radial-gradient", StringComparison.Ordinal) ? GradientFunction.Radial : GradientFunction.Conic;

        var groups = new List<ValueReader>();
        var current = new List<ComponentValue>();
        foreach (var value in r.Rest())
        {
            if (value is PreservedToken { Token.Kind: CssTokenKind.Comma })
            {
                groups.Add(new ValueReader(r.Source, current));
                current = [];
            }
            else
            {
                current.Add(value);
            }
        }
        groups.Add(new ValueReader(r.Source, current));

        var gradient = new GradientSpecified(kind, repeating, kind == GradientFunction.Linear ? 180 : 0, null, false, kind == GradientFunction.Radial ? "farthest-corner" : "",
            null, null, null, ColorSpace.Srgb, HueInterpolation.Shorter, []);
        var explicitSpace = false;
        var first = groups[0];
        if (Prelude(first.Copy(), ref gradient, ref explicitSpace))
            groups.RemoveAt(0);

        var stops = new List<StopSpecified>();
        var legacy = true;
        foreach (var group in groups)
        {
            if (Stop(group, kind == GradientFunction.Conic, ref legacy) is not { } stop)
                return null;
            stops.Add(stop);
        }
        // At least two colour stops, a stop with two positions counting as two (linear-gradient(#fff 0 0) is one
        // colour filling the box); hints only between colour stops.
        if (stops.Where(s => s.Color is not null).Sum(s => s.Position2 is null ? 1 : 2) < 2)
            return null;
        for (var i = 0; i < stops.Count; i++)
        {
            if (stops[i].Color is null && (i == 0 || i == stops.Count - 1 || stops[i - 1].Color is null))
                return null;
        }
        // Without "in", gradients of legacy sRGB colours interpolate in sRGB, others in Oklab (css-images-4 §3.1).
        if (!explicitSpace && !legacy)
            gradient = gradient with { Space = ColorSpace.Oklab };
        return gradient with { Stops = stops };
    }

    // The part before the first stop; false when the first group is a stop instead.
    private static bool Prelude(ValueReader r, ref GradientSpecified gradient, ref bool explicitSpace)
    {
        if (r.AtEnd)
            return false;
        var any = false;
        while (!r.AtEnd)
        {
            if (Interpolation(r) is { } method)
            {
                gradient = gradient with { Space = method.Space, Hue = method.Hue };
                explicitSpace = true;
            }
            else if (gradient.Kind == GradientFunction.Linear && Angle(r) is { } angle)
                gradient = gradient with { Angle = angle };
            else if (gradient.Kind == GradientFunction.Linear && r.Keyword("to") is not null)
            {
                int x = 0, y = 0;
                for (var i = 0; i < 2 && r.Keyword("left", "right", "top", "bottom") is { } side; i++)
                {
                    if (side is "left" or "right")
                        x = x != 0 ? int.MinValue : side == "left" ? -1 : 1;
                    else
                        y = y != 0 ? int.MinValue : side == "top" ? -1 : 1;
                }
                if (x == int.MinValue || y == int.MinValue || x == 0 && y == 0)
                    return false;
                gradient = gradient with { ToCorner = (x, y) };
            }
            else if (gradient.Kind == GradientFunction.Radial && r.Keyword("circle", "ellipse") is { } shape)
                gradient = gradient with { Circle = shape == "circle" };
            else if (gradient.Kind == GradientFunction.Radial && r.Keyword("closest-side", "closest-corner", "farthest-side", "farthest-corner") is { } extent)
                gradient = gradient with { Extent = extent };
            else if (gradient.Kind == GradientFunction.Radial && r.LengthPercentage(nonNegative: true) is { } size)
            {
                var second = r.LengthPercentage(nonNegative: true);
                gradient = gradient with { SizeX = size, SizeY = second ?? size, Circle = gradient.Circle || second is null, Extent = "" };
            }
            else if (gradient.Kind == GradientFunction.Conic && r.Keyword("from") is not null && Angle(r) is { } from)
                gradient = gradient with { Angle = from };
            else if (gradient.Kind != GradientFunction.Linear && r.Keyword("at") is not null && BackgroundParsing.Position(r) is { } at)
                gradient = gradient with { At = at };
            else
                return false;
            any = true;
        }
        return any;
    }

    // in <rectangular space> | in <polar space> [<hue method> hue]?
    private static (ColorSpace Space, HueInterpolation Hue)? Interpolation(ValueReader r)
    {
        var mark = r.Mark;
        if (r.Keyword("in") is null)
            return null;
        ColorSpace? space = r.Keyword("srgb", "srgb-linear", "lab", "oklab", "lch", "oklch", "xyz", "xyz-d50", "xyz-d65", "hsl", "hwb") switch
        {
            "srgb" => ColorSpace.Srgb, "srgb-linear" => ColorSpace.SrgbLinear, "lab" => ColorSpace.Lab, "oklab" => ColorSpace.Oklab,
            "lch" => ColorSpace.Lch, "oklch" => ColorSpace.Oklch, "xyz-d50" => ColorSpace.XyzD50, "xyz" or "xyz-d65" => ColorSpace.XyzD65,
            "hsl" => ColorSpace.Hsl, "hwb" => ColorSpace.Hwb, _ => null,
        };
        if (space is not { } s)
        {
            r.Reset(mark);
            return null;
        }
        var hue = HueInterpolation.Shorter;
        if (ColorSpaces.IsPolar(s) && r.Copy().Keyword("shorter", "longer", "increasing", "decreasing") is not null)
        {
            hue = Enum.Parse<HueInterpolation>(r.Keyword("shorter", "longer", "increasing", "decreasing")!, ignoreCase: true);
            if (r.Keyword("hue") is null)
            {
                r.Reset(mark);
                return null;
            }
        }
        return (s, hue);
    }

    // <angle> in degrees, or a zero (which linear-gradient accepts for 0deg).
    private static float? Angle(ValueReader r)
    {
        var mark = r.Mark;
        if (r.Dimension() is { } d)
        {
            float? degrees = d.Unit.ToLowerInvariant() switch
            {
                "deg" => d.Value, "grad" => d.Value * 0.9f, "rad" => d.Value * 180 / MathF.PI, "turn" => d.Value * 360, _ => null,
            };
            if (degrees is not null)
                return degrees;
            r.Reset(mark);
        }
        return null;
    }

    // <color> && <position>{0,2}, or a hint <position>. Conic positions are angles or percentages.
    private static StopSpecified? Stop(ValueReader r, bool conic, ref bool legacy)
    {
        CssValue? color = null;
        var positions = new List<CssValue>();
        while (!r.AtEnd)
        {
            if (color is null && r.Copy().ColorSpecified() is not null)
            {
                legacy &= IsLegacy(r);
                color = r.ColorSpecified();
            }
            else if (positions.Count < 2 && (conic ? ConicPosition(r) : r.LengthPercentage()) is { } position)
                positions.Add(position);
            else
                return null;
        }
        if (color is null && positions.Count != 1)
            return null;
        return new StopSpecified(color, positions.ElementAtOrDefault(0), positions.ElementAtOrDefault(1));
    }

    // <angle-percentage> | <zero> (https://www.w3.org/TR/css-images-4/#typedef-color-stop-angle).
    private static CssValue? ConicPosition(ValueReader r)
    {
        if (Angle(r) is { } degrees)
            return new PercentageValue(degrees / 360 * 100);
        var mark = r.Mark;
        if (r.Number() == 0)
            return new PercentageValue(0);
        r.Reset(mark);
        return r.LengthPercentage() is PercentageValue p ? p : null;
    }

    // Colours written in the legacy sRGB syntaxes: names, hex, rgb(), hsl(), hwb() and the keywords.
    private static bool IsLegacy(ValueReader r) =>
        r.Copy().Function("lab") is null && r.Copy().Function("lch") is null && r.Copy().Function("oklab") is null
        && r.Copy().Function("oklch") is null && r.Copy().Function("color") is null && r.Copy().Function("color-mix") is null;

    public static ComputedGradient Compute(GradientSpecified g, ComputeContext ctx)
    {
        var stops = new List<ComputedStop>();
        foreach (var stop in g.Stops)
        {
            LengthPercentage? P(CssValue? v) => v is null ? null : ctx.LengthPercentage(v);
            if (stop.Color is null)
            {
                stops.Add(new ComputedStop(null, P(stop.Position)));
                continue;
            }
            var color = ctx.Color(stop.Color, ctx.CurrentColor);
            stops.Add(new ComputedStop(color, P(stop.Position)));
            if (stop.Position2 is not null)
                stops.Add(new ComputedStop(color, P(stop.Position2)));
        }
        var at = g.At is { } p
            ? new Style.BackgroundPosition(FromEdge(ctx.LengthPercentage(p.X), p.XFromEnd), FromEdge(ctx.LengthPercentage(p.Y), p.YFromEnd))
            : new Style.BackgroundPosition(new LengthPercentage(0, 50), new LengthPercentage(0, 50));
        return new ComputedGradient(g.Kind, g.Repeating, g.Angle, g.ToCorner, g.Circle, g.Extent,
            g.SizeX is null ? null : ctx.LengthPercentage(g.SizeX), g.SizeY is null ? null : ctx.LengthPercentage(g.SizeY),
            at, g.Space, g.Hue, stops);

        static LengthPercentage FromEdge(LengthPercentage value, bool fromEnd) =>
            !fromEnd ? value : value.Calc is null ? new LengthPercentage(-value.Px, 100 - value.Percent) : value;
    }
}
