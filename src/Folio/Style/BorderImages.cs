using System.Globalization;
using Folio.Css;

namespace Folio.Style;

/// <summary>https://drafts.csswg.org/css-backgrounds-3/#border-image-repeat</summary>
internal enum BorderImageRepeat { Stretch, Repeat, Round, Space }

/// <summary>border-image-repeat: the horizontal and vertical keywords.</summary>
internal readonly record struct BorderImageRepeats(BorderImageRepeat X, BorderImageRepeat Y)
{
    public override string ToString() => X == Y ? Name(X) : $"{Name(X)} {Name(Y)}";

    private static string Name(BorderImageRepeat r) => r.ToString().ToLowerInvariant();
}

/// <summary>A border-image-slice offset: image pixels, or a percentage of the image's size.</summary>
internal readonly record struct ImageSlice(float Value, bool Percent)
{
    /// <summary>The offset in image pixels for an image this size along the offset's axis (larger ones count as all of it).</summary>
    public float Resolve(float size) => Math.Min(Percent ? Value / 100 * size : Value, size);

    public override string ToString() => Math.Round(Value, 3).ToString(CultureInfo.InvariantCulture) + (Percent ? "%" : "");
}

/// <summary>border-image-slice: the offsets from the top, right, bottom and left, and whether the middle is kept.</summary>
internal sealed record BorderImageSlice(ImageSlice Top, ImageSlice Right, ImageSlice Bottom, ImageSlice Left, bool Fill)
{
    public override string ToString() => $"{Top} {Right} {Bottom} {Left}{(Fill ? " fill" : "")}";
}

/// <summary>A border-image-width or border-image-outset side: a multiple of the border width, a length-percentage, or auto (neither).</summary>
internal readonly record struct BorderImageSide(float? Number, LengthPercentage? Length)
{
    public override string ToString() => Number is { } n ? Math.Round(n, 3).ToString(CultureInfo.InvariantCulture) : Length?.ToString() ?? "auto";
}

/// <summary>border-image-width or border-image-outset: top, right, bottom and left.</summary>
internal sealed record BorderImageSides(BorderImageSide Top, BorderImageSide Right, BorderImageSide Bottom, BorderImageSide Left)
{
    public override string ToString() => $"{Top} {Right} {Bottom} {Left}";
}

/// <summary>Border images (not inherited, https://drafts.csswg.org/css-backgrounds-3/#border-images).</summary>
internal sealed record BorderImageGroup(ImageValue Source, BorderImageSlice Slice, BorderImageSides Width, BorderImageSides Outset, BorderImageRepeats Repeat)
{
    public static BorderImageGroup Initial { get; } = new(NoImage.Instance,
        new(new(100, true), new(100, true), new(100, true), new(100, true), false),
        new(new(1, null), new(1, null), new(1, null), new(1, null)),
        new(new(0, null), new(0, null), new(0, null), new(0, null)),
        new(BorderImageRepeat.Stretch, BorderImageRepeat.Stretch));
}
