using Folio.Style;

namespace Folio.Css;

/// <summary>A specified SVG paint: none (no colour and no URL), a colour, or a URL with an optional fallback colour.</summary>
internal sealed record SvgPaintValue(string? Url, CssValue? Color) : CssValue;

internal sealed record DashArrayValue(IReadOnlyList<CssValue> Dashes) : CssValue;

internal sealed record UrlValue(string Url) : CssValue;

/// <summary>
/// The SVG painting and text properties (https://www.w3.org/TR/SVG2/painting.html, https://www.w3.org/TR/SVG2/text.html),
/// all inherited. Stroke lengths also accept plain numbers, meaning px.
/// </summary>
// ponytail: context-fill and context-stroke (for marker and use content) are not accepted yet.
internal static class SvgProperties
{
    public static IEnumerable<Property> Rows =>
    [
        Paint(PropertyId.Fill, "fill", "black", s => s.Svg.Fill, (b, v) => b.Svg = b.Svg with { Fill = v }),
        Opacity(PropertyId.FillOpacity, "fill-opacity", s => s.Svg.FillOpacity, (b, v) => b.Svg = b.Svg with { FillOpacity = v }),
        Keywords(PropertyId.FillRule, "fill-rule", "nonzero", new() { ["nonzero"] = SvgFillRule.Nonzero, ["evenodd"] = SvgFillRule.Evenodd },
            s => s.Svg.FillRule, (b, v) => b.Svg = b.Svg with { FillRule = v }),
        Keywords(PropertyId.ClipRule, "clip-rule", "nonzero", new() { ["nonzero"] = SvgFillRule.Nonzero, ["evenodd"] = SvgFillRule.Evenodd },
            s => s.Svg.ClipRule, (b, v) => b.Svg = b.Svg with { ClipRule = v }),
        Marker(PropertyId.MarkerStart, "marker-start", s => s.Svg.MarkerStart, (b, v) => b.Svg = b.Svg with { MarkerStart = v }),
        Marker(PropertyId.MarkerMid, "marker-mid", s => s.Svg.MarkerMid, (b, v) => b.Svg = b.Svg with { MarkerMid = v }),
        Marker(PropertyId.MarkerEnd, "marker-end", s => s.Svg.MarkerEnd, (b, v) => b.Svg = b.Svg with { MarkerEnd = v }),
        Paint(PropertyId.Stroke, "stroke", "none", s => s.Svg.Stroke, (b, v) => b.Svg = b.Svg with { Stroke = v }),
        Opacity(PropertyId.StrokeOpacity, "stroke-opacity", s => s.Svg.StrokeOpacity, (b, v) => b.Svg = b.Svg with { StrokeOpacity = v }),
        Length(PropertyId.StrokeWidth, "stroke-width", "1px", nonNegative: true, s => s.Svg.StrokeWidth, (b, v) => b.Svg = b.Svg with { StrokeWidth = v }),
        Keywords(PropertyId.StrokeLinecap, "stroke-linecap", "butt",
            new() { ["butt"] = StrokeLinecap.Butt, ["round"] = StrokeLinecap.Round, ["square"] = StrokeLinecap.Square },
            s => s.Svg.StrokeLinecap, (b, v) => b.Svg = b.Svg with { StrokeLinecap = v }),
        // miter-clip and arcs fall back to miter (https://www.w3.org/TR/SVG2/painting.html#LineJoin).
        Keywords(PropertyId.StrokeLinejoin, "stroke-linejoin", "miter",
            new()
            {
                ["miter"] = StrokeLinejoin.Miter, ["miter-clip"] = StrokeLinejoin.Miter, ["arcs"] = StrokeLinejoin.Miter,
                ["round"] = StrokeLinejoin.Round, ["bevel"] = StrokeLinejoin.Bevel,
            },
            s => s.Svg.StrokeLinejoin, (b, v) => b.Svg = b.Svg with { StrokeLinejoin = v }),
        new Property<float>(PropertyId.StrokeMiterlimit, "stroke-miterlimit", true, "4",
            r => r.Number() is { } n && n >= 1 ? new NumberValue(n) : null,
            (v, _) => ((NumberValue)v).Number,
            s => s.Svg.StrokeMiterlimit, (b, v) => b.Svg = b.Svg with { StrokeMiterlimit = v }),
        new Property<DashArray>(PropertyId.StrokeDasharray, "stroke-dasharray", true, "none",
            ParseDashArray,
            (v, ctx) => v is DashArrayValue d ? new DashArray([.. d.Dashes.Select(x => ToLength(x, ctx))]) : DashArray.None,
            s => s.Svg.StrokeDasharray, (b, v) => b.Svg = b.Svg with { StrokeDasharray = v }),
        Length(PropertyId.StrokeDashoffset, "stroke-dashoffset", "0", nonNegative: false, s => s.Svg.StrokeDashoffset, (b, v) => b.Svg = b.Svg with { StrokeDashoffset = v }),
        new Property<PaintOrder>(PropertyId.PaintOrder, "paint-order", true, "normal",
            PaintOrderValue,
            (v, _) => v is KeywordValue { Keyword: "stroke" } ? PaintOrder.Stroke : PaintOrder.Normal,
            s => s.Svg.PaintOrder, (b, v) => b.Svg = b.Svg with { PaintOrder = v }),
        Keywords(PropertyId.TextAnchor, "text-anchor", "start", new() { ["start"] = TextAnchor.Start, ["middle"] = TextAnchor.Middle, ["end"] = TextAnchor.End },
            s => s.Svg.TextAnchor, (b, v) => b.Svg = b.Svg with { TextAnchor = v }),
        Keywords(PropertyId.DominantBaseline, "dominant-baseline", "auto",
            new()
            {
                ["auto"] = DominantBaseline.Auto, ["text-bottom"] = DominantBaseline.TextBottom, ["alphabetic"] = DominantBaseline.Alphabetic,
                ["ideographic"] = DominantBaseline.Ideographic, ["middle"] = DominantBaseline.Middle, ["central"] = DominantBaseline.Central,
                ["mathematical"] = DominantBaseline.Mathematical, ["hanging"] = DominantBaseline.Hanging, ["text-top"] = DominantBaseline.TextTop,
                // SVG 1.1's values, still common in SVG files (https://www.w3.org/TR/SVG11/text.html#DominantBaselineProperty).
                ["text-before-edge"] = DominantBaseline.TextTop, ["text-after-edge"] = DominantBaseline.TextBottom,
                ["use-script"] = DominantBaseline.Auto, ["no-change"] = DominantBaseline.Auto, ["reset-size"] = DominantBaseline.Auto,
            },
            s => s.Svg.DominantBaseline, (b, v) => b.Svg = b.Svg with { DominantBaseline = v }),
    ];

    /// <summary>stop-color and stop-opacity, which are not inherited.</summary>
    public static IEnumerable<Property> StopRows =>
    [
        new Property<CssColor>(PropertyId.StopColor, "stop-color", false, "black", r => r.ColorSpecified(), (v, ctx) => ctx.Color(v, ctx.CurrentColor),
            s => s.SvgStop.StopColor, (b, v) => b.SvgStop = b.SvgStop with { StopColor = v }),
        new Property<float>(PropertyId.StopOpacity, "stop-opacity", false, "1",
            r => r.Number() is { } n ? new NumberValue(n) : r.LengthPercentage() is PercentageValue p ? p : null,
            (v, _) => Math.Clamp(v is PercentageValue p ? p.Percent / 100 : ((NumberValue)v).Number, 0, 1),
            s => s.SvgStop.StopOpacity, (b, v) => b.SvgStop = b.SvgStop with { StopOpacity = v }),
    ];

    // none | <color> | <url> [none | <color>]? (https://www.w3.org/TR/SVG2/painting.html#SpecifyingPaint)
    private static Property<SvgPaint> Paint(PropertyId id, string name, string initial, Func<ComputedStyle, SvgPaint> get, Action<StyleBuilder, SvgPaint> set) =>
        new(id, name, true, initial,
            r =>
            {
                if (r.Keyword("none") is not null)
                    return new SvgPaintValue(null, null);
                if (r.Url() is not { } url)
                    return r.ColorSpecified() is { } color ? new SvgPaintValue(null, color) : null;
                return r.AtEnd || r.Keyword("none") is not null ? new SvgPaintValue(url, null)
                    : r.ColorSpecified() is { } fallback ? new SvgPaintValue(url, fallback) : null;
            },
            (v, ctx) => v is SvgPaintValue p ? new SvgPaint(p.Color is null ? null : ctx.Color(p.Color, ctx.CurrentColor), p.Url) : SvgPaint.None,
            get, set);

    // none | <url>
    private static Property<MarkerReference> Marker(PropertyId id, string name, Func<ComputedStyle, MarkerReference> get, Action<StyleBuilder, MarkerReference> set) =>
        new(id, name, true, "none",
            r => r.Keyword("none") is not null ? new KeywordValue("none") : r.Url() is { } url ? new UrlValue(url) : null,
            (v, _) => new MarkerReference(v is UrlValue u ? u.Url : null),
            get, set);

    // <number> | <percentage>, clamped to [0, 1].
    private static Property<float> Opacity(PropertyId id, string name, Func<ComputedStyle, float> get, Action<StyleBuilder, float> set) =>
        new(id, name, true, "1",
            r => r.Number() is { } n ? new NumberValue(n) : r.LengthPercentage() is PercentageValue p ? p : null,
            (v, _) => Math.Clamp(v is PercentageValue p ? p.Percent / 100 : ((NumberValue)v).Number, 0, 1),
            get, set);

    // <length-percentage> | <number>
    private static Property<LengthPercentage> Length(PropertyId id, string name, string initial, bool nonNegative,
                                                     Func<ComputedStyle, LengthPercentage> get, Action<StyleBuilder, LengthPercentage> set) =>
        new(id, name, true, initial, r => LengthOrNumber(r, nonNegative), ToLength, get, set);

    private static Property<T> Keywords<T>(PropertyId id, string name, string initial, Dictionary<string, T> keywords,
                                           Func<ComputedStyle, T> get, Action<StyleBuilder, T> set) =>
        new(id, name, true, initial,
            r => r.Keyword(keywords.Keys.ToArray()) is { } k ? new KeywordValue(k) : null,
            (v, _) => keywords[((KeywordValue)v).Keyword],
            get, set);

    private static CssValue? LengthOrNumber(ValueReader r, bool nonNegative) =>
        r.Number(nonNegative) is { } n ? new NumberValue(n) : r.LengthPercentage(nonNegative: nonNegative);

    private static LengthPercentage ToLength(CssValue value, ComputeContext context) =>
        value is NumberValue n ? new LengthPercentage(n.Number) : context.LengthPercentage(value);

    // none | [<length-percentage> | <number>]+#, separated by commas and/or white space; none may be negative.
    private static CssValue? ParseDashArray(ValueReader r)
    {
        if (r.Keyword("none") is not null)
            return new DashArrayValue([]);
        var dashes = new List<CssValue>();
        do
        {
            if (LengthOrNumber(r, nonNegative: true) is not { } dash)
                return null;
            dashes.Add(dash);
        }
        while (r.Comma() || !r.AtEnd);
        return new DashArrayValue(dashes);
    }

    // normal | [fill || stroke || markers]: the stroke goes first when it is listed before the fill, or the fill is not
    // listed (the missing ones follow in the order fill, stroke, markers).
    private static CssValue? PaintOrderValue(ValueReader r)
    {
        if (r.Keyword("normal") is not null)
            return new KeywordValue("normal");
        var seen = new List<string>();
        while (!r.AtEnd)
        {
            if (r.Keyword("fill", "stroke", "markers") is not { } k || seen.Contains(k))
                return null;
            seen.Add(k);
        }
        if (seen.Count == 0)
            return null;
        var fill = seen.IndexOf("fill");
        var stroke = seen.IndexOf("stroke");
        return new KeywordValue(stroke >= 0 && (fill < 0 || stroke < fill) ? "stroke" : "normal");
    }
}
