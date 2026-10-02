using Folio.Css;

namespace Folio.Style;

/// <summary>https://drafts.csswg.org/css-masking-1/#the-mask-mode (images are alpha masks under match-source).</summary>
internal enum MaskMode { MatchSource, Alpha, Luminance }

/// <summary>https://drafts.csswg.org/css-masking-1/#the-mask-type: how an SVG mask element's content becomes a mask.</summary>
internal enum MaskType { Luminance, Alpha }

/// <summary>https://drafts.csswg.org/css-masking-1/#the-mask-composite: source over, source out, source in and xor.</summary>
internal enum MaskComposite { Add, Subtract, Intersect, Exclude }

/// <summary>A mask-clip value: a box, or <c>no-clip</c> when <see cref="Box"/> is null.</summary>
internal readonly record struct MaskClip(GeometryBox? Box)
{
    public override string ToString() => Box is { } box
        ? string.Concat(box.ToString().Select((c, i) => char.IsUpper(c) && i > 0 ? $"-{char.ToLowerInvariant(c)}" : $"{char.ToLowerInvariant(c)}"))
        : "no-clip";
}

/// <summary>
/// Mask layers (not inherited, https://drafts.csswg.org/css-masking-1/#positioned-masks): the layer count is the image
/// list's; the other lists repeat when shorter. The first layer is the top one. <paramref name="Type"/> is mask-type,
/// which applies to SVG mask elements. <paramref name="Border"/> is the mask border
/// (https://drafts.csswg.org/css-masking-1/#mask-borders), an alpha or luminance mask by <paramref name="BorderMode"/>.
/// </summary>
internal sealed record MaskGroup(IReadOnlyList<ImageValue> Images, IReadOnlyList<MaskMode> Modes, IReadOnlyList<RepeatStyle> Repeats,
                                 IReadOnlyList<BackgroundPosition> Positions, IReadOnlyList<BackgroundSize> Sizes, IReadOnlyList<GeometryBox> Origins,
                                 IReadOnlyList<MaskClip> Clips, IReadOnlyList<MaskComposite> Composites, MaskType Type = MaskType.Luminance,
                                 BorderImageGroup? Border = null, MaskType BorderMode = MaskType.Alpha)
{
    public static MaskGroup Initial { get; } = new([NoImage.Instance], [MaskMode.MatchSource], [new RepeatStyle(BackgroundRepeat.Repeat, BackgroundRepeat.Repeat)],
        [new BackgroundPosition(default, default)], [new BackgroundSize(BackgroundSizeKind.Explicit, SizeValue.Auto, SizeValue.Auto)],
        [GeometryBox.BorderBox], [new MaskClip(GeometryBox.BorderBox)], [MaskComposite.Add], Border: BorderInitial);

    /// <summary>The mask border's initial values: no source, slices of 0, auto widths, no outset, stretched.</summary>
    public static BorderImageGroup BorderInitial { get; } = BorderImageGroup.Initial with
    {
        Slice = new(new(0, false), new(0, false), new(0, false), new(0, false), false),
        Width = new(new(null, null), new(null, null), new(null, null), new(null, null)),
    };

    /// <summary>The mask border, its initial values when none is set.</summary>
    public BorderImageGroup MaskBorder => Border ?? BorderInitial;

    /// <summary>Whether the element is masked: some layer or the mask border has an image. Layers of none alone mask nothing.</summary>
    public bool IsMasked => HasLayers || MaskBorder.Source is not NoImage;

    /// <summary>Whether some mask layer has an image.</summary>
    public bool HasLayers
    {
        get
        {
            // By index: painting asks for every box, and an enumerator over the list would be an allocation each time.
            for (var i = 0; i < Images.Count; i++)
            {
                if (Images[i] is not NoImage)
                    return true;
            }
            return false;
        }
    }
}
