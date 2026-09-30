using Folio.Css;

namespace Folio.Style;

/// <summary>
/// An SVG paint (https://www.w3.org/TR/SVG2/painting.html#SpecifyingPaint): <c>none</c> when both parts are null, a
/// colour (possibly <c>currentcolor</c>, resolved per element), or a <c>url()</c> reference to a paint server with its
/// fallback colour, if any.
/// </summary>
internal readonly record struct SvgPaint(CssColor? Color, string? Url = null)
{
    public static SvgPaint None => default;

    public override string ToString() => Url is null ? Color?.ToString() ?? "none" : $"url({Url}) {Color?.ToString() ?? "none"}";
}

internal enum SvgFillRule { Nonzero, Evenodd }

/// <summary>A marker property's value: the text inside url(), or none when null.</summary>
internal readonly record struct MarkerReference(string? Url)
{
    public override string ToString() => Url is null ? "none" : $"url({Url})";
}

internal enum StrokeLinecap { Butt, Round, Square }

internal enum StrokeLinejoin { Miter, Round, Bevel }

/// <summary>paint-order, reduced to what differs without markers: whether the stroke is painted before the fill.</summary>
internal enum PaintOrder { Normal, Stroke }

internal enum TextAnchor { Start, Middle, End }

internal enum DominantBaseline { Auto, TextBottom, Alphabetic, Ideographic, Middle, Central, Mathematical, Hanging, TextTop }

/// <summary>A computed stroke-dasharray: lengths, or none when empty.</summary>
internal sealed class DashArray(IReadOnlyList<LengthPercentage> dashes) : IEquatable<DashArray>
{
    public static DashArray None { get; } = new([]);

    public IReadOnlyList<LengthPercentage> Dashes { get; } = dashes;

    public bool Equals(DashArray? other) => other is not null && Dashes.SequenceEqual(other.Dashes);

    public override bool Equals(object? obj) => Equals(obj as DashArray);

    public override int GetHashCode() => Dashes.Count == 0 ? 0 : HashCode.Combine(Dashes.Count, Dashes[0]);

    public override string ToString() => Dashes.Count == 0 ? "none" : string.Join(", ", Dashes);
}

/// <summary>
/// The SVG painting and text properties (all inherited): fill and stroke with their opacities and stroke geometry,
/// clip-rule (https://drafts.csswg.org/css-masking-1/#the-clip-rule), the markers (url() references;
/// https://www.w3.org/TR/SVG2/painting.html#VertexMarkerProperties),
/// (https://www.w3.org/TR/SVG2/painting.html), paint-order, text-anchor (https://www.w3.org/TR/SVG2/text.html#TextAnchoringProperties)
/// and dominant-baseline (https://www.w3.org/TR/css-inline-3/#dominant-baseline-property). Lengths keep their
/// percentages, which refer to the SVG viewport.
/// </summary>
internal sealed record SvgGroup(SvgPaint Fill, float FillOpacity, SvgFillRule FillRule, SvgPaint Stroke, float StrokeOpacity,
                                LengthPercentage StrokeWidth, StrokeLinecap StrokeLinecap, StrokeLinejoin StrokeLinejoin, float StrokeMiterlimit,
                                DashArray StrokeDasharray, LengthPercentage StrokeDashoffset, PaintOrder PaintOrder, TextAnchor TextAnchor,
                                DominantBaseline DominantBaseline, SvgFillRule ClipRule = SvgFillRule.Nonzero, MarkerReference MarkerStart = default,
                                MarkerReference MarkerMid = default, MarkerReference MarkerEnd = default)
{
    public static SvgGroup Initial { get; } = new(new SvgPaint(CssColor.Black), 1, SvgFillRule.Nonzero, SvgPaint.None, 1, new LengthPercentage(1),
        StrokeLinecap.Butt, StrokeLinejoin.Miter, 4, DashArray.None, default, PaintOrder.Normal, TextAnchor.Start, DominantBaseline.Auto);
}

/// <summary>
/// The gradient stop properties (not inherited): stop-color and stop-opacity (https://www.w3.org/TR/SVG2/pservers.html#StopColorProperties).
/// </summary>
internal sealed record SvgStopGroup(CssColor StopColor, float StopOpacity)
{
    public static SvgStopGroup Initial { get; } = new(CssColor.Black, 1);
}
