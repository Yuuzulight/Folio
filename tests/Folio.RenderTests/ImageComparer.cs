namespace Folio.RenderTests;

/// <summary>
/// A pixel differs when any channel differs by more than <see cref="ChannelThreshold"/>; an image
/// matches when at most <see cref="MaxDifferingRatio"/> of its pixels differ (docs/study/19-testing.md).
/// </summary>
public readonly record struct Tolerance(int ChannelThreshold, double MaxDifferingRatio)
{
    /// <summary>For reftests: both sides come from the same renderer, so any difference fails.</summary>
    public static Tolerance Exact { get; } = new(0, 0);

    /// <summary>For golden images: at most 0.5% of pixels may differ by more than a small per-channel amount.</summary>
    public static Tolerance Default { get; } = new(8, 0.005);
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
                if (inBoth && Within(expected.Pixel(x, y), actual.Pixel(x, y), tolerance.ChannelThreshold))
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
