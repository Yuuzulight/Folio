using Folio.Style;

namespace Folio.Css;

/// <summary>
/// The mask layer properties and the <c>mask</c> shorthand (https://drafts.csswg.org/css-masking-1/#positioned-masks),
/// with the <c>-webkit-mask-*</c> names artifacts write for older engines.
/// </summary>
// ponytail: mask-border waits for its own work.
internal static class MaskProperties
{
    public static Properties.LazyRow[] Rows =>
    [
        new(PropertyId.MaskImage, "mask-image", static () => new Property<IReadOnlyList<ImageValue>>(PropertyId.MaskImage, "mask-image", false, "none", BackgroundParsing.ImageList,
            (v, ctx) => ((LayerListValue<ImageValue>)v).Items
                .Select(i => i is GradientImage g ? g with { Computed = GradientParsing.Compute(g.Specified, ctx) } : i).ToList(),
            s => s.Mask.Images, (b, v) => b.Mask = b.Mask with { Images = v })),
        new(PropertyId.MaskMode, "mask-mode", static () => Properties.Layers<MaskMode, MaskMode>(PropertyId.MaskMode, "mask-mode", "match-source", Mode, (x, _) => x,
            s => s.Mask.Modes, (b, v) => b.Mask = b.Mask with { Modes = v })),
        new(PropertyId.MaskRepeat, "mask-repeat", static () => Properties.Layers<RepeatStyle, RepeatStyle>(PropertyId.MaskRepeat, "mask-repeat", "repeat", BackgroundParsing.Repeat, (x, _) => x,
            s => s.Mask.Repeats, (b, v) => b.Mask = b.Mask with { Repeats = v })),
        new(PropertyId.MaskPosition, "mask-position", static () => Properties.Layers<PositionSpecified, BackgroundPosition>(PropertyId.MaskPosition, "mask-position", "0% 0%", BackgroundParsing.Position,
            Properties.ComputePosition, s => s.Mask.Positions, (b, v) => b.Mask = b.Mask with { Positions = v })),
        new(PropertyId.MaskSize, "mask-size", static () => Properties.Layers<SizeSpecified, BackgroundSize>(PropertyId.MaskSize, "mask-size", "auto", BackgroundParsing.Size, Properties.ComputeSize,
            s => s.Mask.Sizes, (b, v) => b.Mask = b.Mask with { Sizes = v })),
        new(PropertyId.MaskOrigin, "mask-origin", static () => Properties.Layers<GeometryBox, GeometryBox>(PropertyId.MaskOrigin, "mask-origin", "border-box", Box, (x, _) => x,
            s => s.Mask.Origins, (b, v) => b.Mask = b.Mask with { Origins = v })),
        new(PropertyId.MaskClip, "mask-clip", static () => Properties.Layers<MaskClip, MaskClip>(PropertyId.MaskClip, "mask-clip", "border-box", Clip, (x, _) => x,
            s => s.Mask.Clips, (b, v) => b.Mask = b.Mask with { Clips = v })),
        new(PropertyId.MaskComposite, "mask-composite", static () => Properties.Layers<MaskComposite, MaskComposite>(PropertyId.MaskComposite, "mask-composite", "add", Composite, (x, _) => x,
            s => s.Mask.Composites, (b, v) => b.Mask = b.Mask with { Composites = v })),
    ];

    private static readonly PropertyId[] Longhands =
    [
        PropertyId.MaskImage, PropertyId.MaskMode, PropertyId.MaskPosition, PropertyId.MaskSize, PropertyId.MaskRepeat, PropertyId.MaskOrigin,
        PropertyId.MaskClip, PropertyId.MaskComposite,
    ];

    /// <summary>The <c>mask</c> shorthand, its <c>-webkit-</c> name, and the prefixed longhands.</summary>
    public static IEnumerable<(string Name, Properties.Shorthand Shorthand)> Shorthands =>
    [
        ("mask", new Properties.Shorthand(Longhands, Parse)),
        ("-webkit-mask", new Properties.Shorthand(Longhands, Parse)),
        Alias("-webkit-mask-image", PropertyId.MaskImage),
        Alias("-webkit-mask-repeat", PropertyId.MaskRepeat),
        Alias("-webkit-mask-position", PropertyId.MaskPosition),
        Alias("-webkit-mask-size", PropertyId.MaskSize),
        Alias("-webkit-mask-origin", PropertyId.MaskOrigin),
        Alias("-webkit-mask-clip", PropertyId.MaskClip),
        // The prefixed composite takes Porter-Duff names; those with a mask-composite equivalent map to it.
        ("-webkit-mask-composite", new Properties.Shorthand([PropertyId.MaskComposite],
            r => BackgroundParsing.List(r, LegacyComposite) is { } list ? [(PropertyId.MaskComposite, list)] : null)),
    ];

    private static (string, Properties.Shorthand) Alias(string name, PropertyId id) =>
        (name, new Properties.Shorthand([id], r => Properties.Get(id).Parse(r) is { } value ? [(id, value)] : null));

    private static MaskMode? Mode(ValueReader r) => r.Keyword("match-source", "alpha", "luminance") switch
    {
        "match-source" => MaskMode.MatchSource,
        "alpha" => MaskMode.Alpha,
        "luminance" => MaskMode.Luminance,
        _ => null,
    };

    private static readonly KeywordMap<GeometryBox> BoxKeywords = new()
    {
        ["border-box"] = GeometryBox.BorderBox, ["padding-box"] = GeometryBox.PaddingBox, ["content-box"] = GeometryBox.ContentBox,
        ["margin-box"] = GeometryBox.MarginBox, ["fill-box"] = GeometryBox.FillBox, ["stroke-box"] = GeometryBox.StrokeBox,
        ["view-box"] = GeometryBox.ViewBox,
    };

    private static GeometryBox? Box(ValueReader r) => r.Keyword([.. BoxKeywords.Keys]) is { } keyword ? BoxKeywords[keyword] : null;

    private static MaskClip? Clip(ValueReader r) =>
        r.Keyword("no-clip") is not null ? new MaskClip(null) : Box(r) is { } box ? new MaskClip(box) : null;

    private static MaskComposite? Composite(ValueReader r) => r.Keyword("add", "subtract", "intersect", "exclude") switch
    {
        "add" => MaskComposite.Add,
        "subtract" => MaskComposite.Subtract,
        "intersect" => MaskComposite.Intersect,
        "exclude" => MaskComposite.Exclude,
        _ => null,
    };

    private static MaskComposite? LegacyComposite(ValueReader r) => r.Keyword("source-over", "source-out", "source-in", "xor") switch
    {
        "source-over" => MaskComposite.Add,
        "source-out" => MaskComposite.Subtract,
        "source-in" => MaskComposite.Intersect,
        "xor" => MaskComposite.Exclude,
        _ => null,
    };

    // <mask-layer># where <mask-layer> = <mask-reference> || <position> [ / <bg-size> ]? || <repeat-style> ||
    // <geometry-box> || [ <geometry-box> | no-clip ] || <compositing-operator> || <masking-mode>. One box sets the
    // origin and the clip, two set the origin then the clip.
    private static List<(PropertyId, CssValue)>? Parse(ValueReader r)
    {
        var images = new List<ImageValue>();
        var modes = new List<MaskMode>();
        var positions = new List<PositionSpecified>();
        var sizes = new List<SizeSpecified>();
        var repeats = new List<RepeatStyle>();
        var origins = new List<GeometryBox>();
        var clips = new List<MaskClip>();
        var composites = new List<MaskComposite>();
        do
        {
            ImageValue? image = null;
            MaskMode? mode = null;
            PositionSpecified? position = null;
            SizeSpecified? size = null;
            RepeatStyle? repeat = null;
            MaskComposite? composite = null;
            var boxes = new List<MaskClip>();
            var any = false;
            while (!r.AtEnd && !r.PeekComma())
            {
                any = true;
                if (image is null && BackgroundParsing.Image(r) is { } i)
                    image = i;
                else if (position is null && BackgroundParsing.Position(r) is { } p)
                {
                    position = p;
                    if (r.Delim('/') && (size = BackgroundParsing.Size(r)) is null)
                        return null;
                }
                else if (repeat is null && BackgroundParsing.Repeat(r) is { } rep)
                    repeat = rep;
                else if (boxes.Count < 2 && Clip(r) is { } box)
                    boxes.Add(box);
                else if (composite is null && Composite(r) is { } c)
                    composite = c;
                else if (mode is null && Mode(r) is { } m)
                    mode = m;
                else
                    return null;
            }
            // no-clip can only be the clip: after a box, or alone.
            if (!any || (boxes.Count > 0 && boxes[0].Box is null && boxes.Count > 1))
                return null;
            images.Add(image ?? NoImage.Instance);
            modes.Add(mode ?? MaskMode.MatchSource);
            positions.Add(position ?? new PositionSpecified(false, new PercentageValue(0), false, new PercentageValue(0)));
            sizes.Add(size ?? new SizeSpecified(BackgroundSizeKind.Explicit, null, null));
            repeats.Add(repeat ?? new RepeatStyle(BackgroundRepeat.Repeat, BackgroundRepeat.Repeat));
            origins.Add(boxes.Count > 0 && boxes[0].Box is { } origin ? origin : GeometryBox.BorderBox);
            clips.Add(boxes.Count > 1 ? boxes[1] : boxes.Count == 1 ? boxes[0] : new MaskClip(GeometryBox.BorderBox));
            composites.Add(composite ?? MaskComposite.Add);
        }
        while (r.Comma());
        return
        [
            (PropertyId.MaskImage, new LayerListValue<ImageValue>(images)),
            (PropertyId.MaskMode, new LayerListValue<MaskMode>(modes)),
            (PropertyId.MaskPosition, new LayerListValue<PositionSpecified>(positions)),
            (PropertyId.MaskSize, new LayerListValue<SizeSpecified>(sizes)),
            (PropertyId.MaskRepeat, new LayerListValue<RepeatStyle>(repeats)),
            (PropertyId.MaskOrigin, new LayerListValue<GeometryBox>(origins)),
            (PropertyId.MaskClip, new LayerListValue<MaskClip>(clips)),
            (PropertyId.MaskComposite, new LayerListValue<MaskComposite>(composites)),
        ];
    }
}
