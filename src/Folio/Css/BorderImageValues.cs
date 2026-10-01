using Folio.Style;

namespace Folio.Css;

/// <summary>Specified border-image-slice: one to four numbers or percentages, and fill.</summary>
internal sealed record BorderImageSliceValue(IReadOnlyList<CssValue> Values, bool Fill) : CssValue;

/// <summary>Specified border-image-width or border-image-outset: one to four numbers, length-percentages or auto.</summary>
internal sealed record BorderImageSidesValue(IReadOnlyList<CssValue> Values) : CssValue;

/// <summary>Specified border-image-repeat.</summary>
internal sealed record BorderImageRepeatValue(BorderImageRepeats Repeat) : CssValue;

/// <summary>
/// The border image properties and the <c>border-image</c> shorthand (https://drafts.csswg.org/css-backgrounds-3/#border-images).
/// </summary>
internal static class BorderImageProperties
{
    public static IEnumerable<Property> Rows =>
    [
        new Property<ImageValue>(PropertyId.BorderImageSource, "border-image-source", false, "none", Source,
            (v, ctx) => ((ImageSpecified)v).Image is GradientImage g ? g with { Computed = GradientParsing.Compute(g.Specified, ctx) } : ((ImageSpecified)v).Image,
            s => s.BorderImage.Source, (b, v) => b.BorderImage = b.BorderImage with { Source = v }),
        new Property<BorderImageSlice>(PropertyId.BorderImageSlice, "border-image-slice", false, "100%", r => Slice(r),
            (v, _) => ComputeSlice((BorderImageSliceValue)v), s => s.BorderImage.Slice, (b, v) => b.BorderImage = b.BorderImage with { Slice = v }),
        new Property<BorderImageSides>(PropertyId.BorderImageWidth, "border-image-width", false, "1", r => Sides(r, auto: true, percent: true),
            (v, ctx) => ComputeSides((BorderImageSidesValue)v, ctx), s => s.BorderImage.Width, (b, v) => b.BorderImage = b.BorderImage with { Width = v }),
        new Property<BorderImageSides>(PropertyId.BorderImageOutset, "border-image-outset", false, "0", r => Sides(r, auto: false, percent: false),
            (v, ctx) => ComputeSides((BorderImageSidesValue)v, ctx), s => s.BorderImage.Outset, (b, v) => b.BorderImage = b.BorderImage with { Outset = v }),
        new Property<BorderImageRepeats>(PropertyId.BorderImageRepeat, "border-image-repeat", false, "stretch", r => Repeat(r),
            (v, _) => ((BorderImageRepeatValue)v).Repeat, s => s.BorderImage.Repeat, (b, v) => b.BorderImage = b.BorderImage with { Repeat = v }),
    ];

    public static readonly PropertyId[] Longhands =
    [
        PropertyId.BorderImageSource, PropertyId.BorderImageSlice, PropertyId.BorderImageWidth, PropertyId.BorderImageOutset, PropertyId.BorderImageRepeat,
    ];

    /// <summary>
    /// The mask border properties (https://drafts.csswg.org/css-masking-1/#mask-borders): the border image grammar with
    /// other initial values (slices of 0, auto widths), and mask-border-mode.
    /// </summary>
    // ponytail: the mask shorthand does not reset mask-border, and -webkit-mask-box-image is not accepted.
    public static IEnumerable<Property> MaskBorderRows =>
    [
        new Property<ImageValue>(PropertyId.MaskBorderSource, "mask-border-source", false, "none", Source,
            (v, ctx) => ((ImageSpecified)v).Image is GradientImage g ? g with { Computed = GradientParsing.Compute(g.Specified, ctx) } : ((ImageSpecified)v).Image,
            s => s.Mask.MaskBorder.Source, (b, v) => b.Mask = b.Mask with { Border = b.Mask.MaskBorder with { Source = v } }),
        new Property<BorderImageSlice>(PropertyId.MaskBorderSlice, "mask-border-slice", false, "0", r => Slice(r),
            (v, _) => ComputeSlice((BorderImageSliceValue)v), s => s.Mask.MaskBorder.Slice, (b, v) => b.Mask = b.Mask with { Border = b.Mask.MaskBorder with { Slice = v } }),
        new Property<BorderImageSides>(PropertyId.MaskBorderWidth, "mask-border-width", false, "auto", r => Sides(r, auto: true, percent: true),
            (v, ctx) => ComputeSides((BorderImageSidesValue)v, ctx), s => s.Mask.MaskBorder.Width, (b, v) => b.Mask = b.Mask with { Border = b.Mask.MaskBorder with { Width = v } }),
        new Property<BorderImageSides>(PropertyId.MaskBorderOutset, "mask-border-outset", false, "0", r => Sides(r, auto: false, percent: false),
            (v, ctx) => ComputeSides((BorderImageSidesValue)v, ctx), s => s.Mask.MaskBorder.Outset, (b, v) => b.Mask = b.Mask with { Border = b.Mask.MaskBorder with { Outset = v } }),
        new Property<BorderImageRepeats>(PropertyId.MaskBorderRepeat, "mask-border-repeat", false, "stretch", r => Repeat(r),
            (v, _) => ((BorderImageRepeatValue)v).Repeat, s => s.Mask.MaskBorder.Repeat, (b, v) => b.Mask = b.Mask with { Border = b.Mask.MaskBorder with { Repeat = v } }),
        new Property<MaskType>(PropertyId.MaskBorderMode, "mask-border-mode", false, "alpha", MaskBorderMode,
            (v, _) => ((KeywordValue)v).Keyword == "luminance" ? MaskType.Luminance : MaskType.Alpha, s => s.Mask.BorderMode, (b, v) => b.Mask = b.Mask with { BorderMode = v }),
    ];

    public static readonly PropertyId[] MaskBorderLonghands =
    [
        PropertyId.MaskBorderSource, PropertyId.MaskBorderSlice, PropertyId.MaskBorderWidth, PropertyId.MaskBorderOutset, PropertyId.MaskBorderRepeat,
        PropertyId.MaskBorderMode,
    ];

    private static CssValue? MaskBorderMode(ValueReader r) => r.Keyword("luminance", "alpha") is { } k ? new KeywordValue(k) : null;

    /// <summary>The <c>mask-border</c> shorthand: as border-image's, with mask-border-mode as well.</summary>
    public static List<(PropertyId, CssValue)>? MaskBorderShorthand(ValueReader r)
    {
        CssValue? mode = null;
        if (Shorthand(r, PropertyId.MaskBorderSource, PropertyId.MaskBorderSlice, PropertyId.MaskBorderWidth, PropertyId.MaskBorderOutset,
                PropertyId.MaskBorderRepeat, reader => mode is null && MaskBorderMode(reader) is { } m ? mode = m : null) is not { } values)
            return null;
        values.Add((PropertyId.MaskBorderMode, mode ?? Properties.Get(PropertyId.MaskBorderMode).Initial));
        return values;
    }

    // none | <image>
    private static CssValue? Source(ValueReader r) =>
        r.Keyword("none") is not null ? new ImageSpecified(NoImage.Instance) : BackgroundParsing.Image(r) is { } image ? new ImageSpecified(image) : null;

    // [ <number [0,∞]> | <percentage [0,∞]> ]{1,4} && fill?
    private static BorderImageSliceValue? Slice(ValueReader r)
    {
        var mark = r.Mark;
        var fill = r.Keyword("fill") is not null;
        var values = new List<CssValue>();
        while (values.Count < 4 && (r.Number(nonNegative: true) is { } n ? new NumberValue(n) : Percentage(r)) is { } value)
            values.Add(value);
        if (values.Count == 0)
        {
            r.Reset(mark);
            return null;
        }
        if (!fill)
            fill = r.Keyword("fill") is not null;
        return new BorderImageSliceValue(values, fill);
    }

    private static CssValue? Percentage(ValueReader r)
    {
        var mark = r.Mark;
        if (r.LengthPercentage(nonNegative: true) is PercentageValue p)
            return p;
        r.Reset(mark);
        return null;
    }

    // border-image-width: [ <length-percentage [0,∞]> | <number [0,∞]> | auto ]{1,4};
    // border-image-outset: [ <length [0,∞]> | <number [0,∞]> ]{1,4}. A unitless zero is a number.
    private static BorderImageSidesValue? Sides(ValueReader r, bool auto, bool percent)
    {
        var values = new List<CssValue>();
        while (values.Count < 4)
        {
            var value = r.Number(nonNegative: true) is { } n ? new NumberValue(n)
                : auto && r.Keyword("auto") is not null ? new KeywordValue("auto")
                : r.LengthPercentage(allowPercent: percent, nonNegative: true);
            if (value is null)
                break;
            values.Add(value);
        }
        return values.Count > 0 ? new BorderImageSidesValue(values) : null;
    }

    // [ stretch | repeat | round | space ]{1,2}
    private static BorderImageRepeatValue? Repeat(ValueReader r)
    {
        if (RepeatKeyword(r) is not { } x)
            return null;
        return new BorderImageRepeatValue(new BorderImageRepeats(x, RepeatKeyword(r) ?? x));
    }

    private static BorderImageRepeat? RepeatKeyword(ValueReader r) => r.Keyword("stretch", "repeat", "round", "space") switch
    {
        "stretch" => BorderImageRepeat.Stretch,
        "repeat" => BorderImageRepeat.Repeat,
        "round" => BorderImageRepeat.Round,
        "space" => BorderImageRepeat.Space,
        _ => null,
    };

    // Top, right, bottom and left from one to four values, as for margins.
    private static T[] Four<T>(IReadOnlyList<T> v) => v.Count switch
    {
        1 => [v[0], v[0], v[0], v[0]],
        2 => [v[0], v[1], v[0], v[1]],
        3 => [v[0], v[1], v[2], v[1]],
        _ => [v[0], v[1], v[2], v[3]],
    };

    private static BorderImageSlice ComputeSlice(BorderImageSliceValue value)
    {
        var s = Four(value.Values).Select(v => v is PercentageValue p ? new ImageSlice(p.Percent, true) : new ImageSlice(((NumberValue)v).Number, false)).ToArray();
        return new BorderImageSlice(s[0], s[1], s[2], s[3], value.Fill);
    }

    private static BorderImageSides ComputeSides(BorderImageSidesValue value, ComputeContext ctx)
    {
        var s = Four(value.Values).Select(v => v switch
        {
            NumberValue n => new BorderImageSide(n.Number, null),
            KeywordValue => new BorderImageSide(null, null),
            _ => new BorderImageSide(null, ctx.LengthPercentage(v, nonNegative: true)),
        }).ToArray();
        return new BorderImageSides(s[0], s[1], s[2], s[3]);
    }

    /// <summary>
    /// The <c>border-image</c> shorthand: source || slice [ / width | / width? / outset ]? || repeat, the others reset.
    /// </summary>
    public static List<(PropertyId, CssValue)>? Shorthand(ValueReader r) =>
        Shorthand(r, PropertyId.BorderImageSource, PropertyId.BorderImageSlice, PropertyId.BorderImageWidth, PropertyId.BorderImageOutset,
            PropertyId.BorderImageRepeat, null);

    // The border image grammar for one set of longhands; other components (the mask border's mode) are read by extra,
    // which returns null when there is none.
    private static List<(PropertyId, CssValue)>? Shorthand(ValueReader r, PropertyId sourceId, PropertyId sliceId, PropertyId widthId, PropertyId outsetId,
                                                         PropertyId repeatId, Func<ValueReader, CssValue?>? extra)
    {
        var any = false;
        CssValue? source = null, slice = null, width = null, outset = null, repeat = null;
        while (!r.AtEnd)
        {
            if (source is null && Source(r) is { } s)
            {
                source = s;
            }
            else if (slice is null && Slice(r) is { } sl)
            {
                slice = sl;
                if (r.Delim('/'))
                {
                    width = Sides(r, auto: true, percent: true);
                    if (r.Delim('/'))
                    {
                        if ((outset = Sides(r, auto: false, percent: false)) is null)
                            return null;
                    }
                    else if (width is null)
                    {
                        return null;
                    }
                }
            }
            else if (repeat is null && Repeat(r) is { } rep)
            {
                repeat = rep;
            }
            else if (extra?.Invoke(r) is not null)
            {
                any = true;
            }
            else
            {
                return null;
            }
        }
        if (source is null && slice is null && repeat is null && !any)
            return null;
        return
        [
            (sourceId, source ?? Properties.Get(sourceId).Initial),
            (sliceId, slice ?? Properties.Get(sliceId).Initial),
            (widthId, width ?? Properties.Get(widthId).Initial),
            (outsetId, outset ?? Properties.Get(outsetId).Initial),
            (repeatId, repeat ?? Properties.Get(repeatId).Initial),
        ];
    }
}
