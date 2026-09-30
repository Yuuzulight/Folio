using Folio.Css;

namespace Folio.Style;

/// <summary>https://drafts.csswg.org/css-masking-1/#the-mask-mode (images are alpha masks under match-source).</summary>
internal enum MaskMode { MatchSource, Alpha, Luminance }

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
/// list's; the other lists repeat when shorter. The first layer is the top one.
/// </summary>
internal sealed record MaskGroup(IReadOnlyList<ImageValue> Images, IReadOnlyList<MaskMode> Modes, IReadOnlyList<RepeatStyle> Repeats,
                                 IReadOnlyList<BackgroundPosition> Positions, IReadOnlyList<BackgroundSize> Sizes, IReadOnlyList<GeometryBox> Origins,
                                 IReadOnlyList<MaskClip> Clips, IReadOnlyList<MaskComposite> Composites)
{
    public static MaskGroup Initial { get; } = new([NoImage.Instance], [MaskMode.MatchSource], [new RepeatStyle(BackgroundRepeat.Repeat, BackgroundRepeat.Repeat)],
        [new BackgroundPosition(default, default)], [new BackgroundSize(BackgroundSizeKind.Explicit, SizeValue.Auto, SizeValue.Auto)],
        [GeometryBox.BorderBox], [new MaskClip(GeometryBox.BorderBox)], [MaskComposite.Add]);

    /// <summary>Whether the element is masked: some layer has an image. Layers of none alone mask nothing.</summary>
    public bool IsMasked => Images.Any(i => i is not NoImage);
}
