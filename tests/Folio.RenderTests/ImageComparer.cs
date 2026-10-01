namespace Folio.RenderTests;

/// <summary>
/// A pixel differs when any channel differs by more than <see cref="ChannelThreshold"/>; an image
/// matches when at most <see cref="MaxDifferingRatio"/> of its pixels differ (docs/study/19-testing.md).
/// </summary>
/// <param name="Radius">
/// How far a matching pixel may be (0: only the same position). With a radius, a pixel of either image matches when
/// some pixel of the other image within <c>Radius</c> pixels, horizontally and vertically, is within the threshold;
/// both images are checked that way, so neither can add or lose a line unnoticed.
/// </param>
public readonly record struct Tolerance(int ChannelThreshold, double MaxDifferingRatio, int Radius = 0)
{
    /// <summary>For reftests: both sides come from the same renderer, so any difference fails.</summary>
    public static Tolerance Exact { get; } = new(0, 0);

    /// <summary>For golden images: at most 0.5% of pixels may differ by more than a small per-channel amount.</summary>
    public static Tolerance Default { get; } = new(8, 0.005);

    /// <summary>
    /// For conformance references, which another renderer draws: its glyph edges are rasterised differently, so a pixel
    /// may match within 1 px and 48 levels, while anything that moves by more than a pixel still differs.
    /// </summary>
    public static Tolerance Conformance { get; } = new(48, 0.005, 1);
}

/// <param name="Diff">
/// Covers both images; differing pixels (including any area only one image has) are red, the rest a faded copy of the expected image.
/// </param>
public sealed record ComparisonResult(bool Passed, long DifferingPixels, double DifferingRatio, PixelBuffer Diff);

public static class ImageComparer
{
    private static readonly byte[] Highlight = [0, 0, 255, 255]; // opaque red, BGRA

    public static ComparisonResult Compare(PixelBuffer expected, PixelBuffer actual, Tolerance tolerance)
    {
        var width = Math.Max(expected.Width, actual.Width);
        var height = Math.Max(expected.Height, actual.Height);
        var diff = new PixelBuffer(width, height);
        long differing = 0;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var inBoth = x < expected.Width && y < expected.Height && x < actual.Width && y < actual.Height;
                if (inBoth && Matches(expected, actual, x, y, tolerance) && (tolerance.Radius == 0 || Matches(actual, expected, x, y, tolerance)))
                {
                    Fade(expected.Pixel(x, y), diff.Pixel(x, y));
                }
                else
                {
                    differing++;
                    Highlight.CopyTo(diff.Pixel(x, y));
                }
            }
        }

        var total = (long)width * height;
        var ratio = total == 0 ? 0 : (double)differing / total;
        var sameSize = expected.Width == actual.Width && expected.Height == actual.Height;
        return new ComparisonResult(sameSize && ratio <= tolerance.MaxDifferingRatio, differing, ratio, diff);
    }

    // Whether the pixel of `image` at (x, y) has a pixel of `other` within the tolerance's radius and threshold.
    private static bool Matches(PixelBuffer image, PixelBuffer other, int x, int y, Tolerance tolerance)
    {
        var pixel = image.Pixel(x, y);
        if (x < other.Width && y < other.Height && Within(pixel, other.Pixel(x, y), tolerance.ChannelThreshold))
            return true; // the common case: the same position
        var r = tolerance.Radius;
        for (var oy = Math.Max(0, y - r); oy <= Math.Min(other.Height - 1, y + r); oy++)
        {
            for (var ox = Math.Max(0, x - r); ox <= Math.Min(other.Width - 1, x + r); ox++)
            {
                if (Within(pixel, other.Pixel(ox, oy), tolerance.ChannelThreshold))
                    return true;
            }
        }
        return false;
    }

    private static bool Within(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int threshold)
    {
        for (var i = 0; i < 4; i++)
        {
            if (Math.Abs(a[i] - b[i]) > threshold)
                return false;
        }
        return true;
    }

    // Composites the premultiplied pixel over white, then lightens its grey level so red stands out.
    private static void Fade(ReadOnlySpan<byte> source, Span<byte> target)
    {
        var white = 255 - source[3];
        var luma = ((source[0] + white) * 29 + (source[1] + white) * 150 + (source[2] + white) * 77) >> 8;
        var grey = (byte)(255 - (255 - luma) / 4);
        target[0] = target[1] = target[2] = grey;
        target[3] = 255;
    }
}
