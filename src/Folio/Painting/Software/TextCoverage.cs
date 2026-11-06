namespace Folio.Painting.Software;

/// <summary>
/// Grayscale text coverage in a gamma-encoded display. Solve the ordinary alpha blend for the coverage that produces
/// a luminance-linear blend, with modest contrast enhancement for thin strokes. This concerns glyph masks only;
/// page geometry, clipping masks, paint opacity and image colour spaces keep their normal compositing rules.
/// </summary>
internal static class TextCoverage
{
    // Fixed-size tables (2 KiB), shared by all fonts. Colour is quantised to three bits, never retained as cache keys
    // of arbitrary user-controlled objects. The mask itself stays independent of paint alpha and backdrop contents.
    private static readonly byte[][] Tables = Enumerable.Range(0, 8).Select(level => Table((level << 5) | (level << 2) | (level >> 1))).ToArray();
    private const double Gamma = 2.2;
    private const double Contrast = .5;

    internal static int Luminance(in Paint paint)
    {
        if (paint.Gradient is not null || paint.Pattern is not null) return -1;
        var value = (int)MathF.Round(Math.Clamp(.2126f * paint.Color.R + .7152f * paint.Color.G + .0722f * paint.Color.B, 0, 1) * 255);
        if (value < 0) return -1;
        // Eight luminance levels keep neighbouring foreground colours on the same glyph-mask curve, matching
        // the reference text rasterizer's three-bit luminance precision. Expand those bits across the byte.
        var level = value >> 5;
        return (level << 5) | (level << 2) | (level >> 1);
    }

    internal static byte Correct(byte value, int luminance) => luminance < 0 ? value : Tables[luminance >> 5][value];

    internal static void Apply(Coverage coverage, int luminance)
    {
        if (luminance < 0) return;
        var table = Tables[luminance >> 5];
        for (var i = 0; i < coverage.Data.Length; i++) coverage.Data[i] = table[coverage.Data[i]];
    }

    private static byte[] Table(int luminance)
    {
        var result = new byte[256];
        var foreground = luminance / 255d;
        // A glyph mask is reusable before its actual backdrop is known. The perceptual inverse of the foreground
        // is a continuous estimate and avoids dependence on whatever another thread has painted behind the glyph.
        var backdrop = 1 - foreground;
        var lightForeground = Math.Pow(foreground, Gamma); var lightBackdrop = Math.Pow(backdrop, Gamma);
        for (var i = 0; i < result.Length; i++)
        {
            var coverage = i / 255d;
            var enhanced = coverage + Contrast * lightBackdrop * coverage * (1 - coverage);
            var encoded = Math.Pow(lightForeground * enhanced + lightBackdrop * (1 - enhanced), 1 / Gamma);
            var corrected = Math.Abs(foreground - backdrop) < 1d / 256 ? enhanced : (encoded - backdrop) / (foreground - backdrop);
            result[i] = (byte)Math.Clamp((int)Math.Round(corrected * 255), 0, 255);
        }
        // Preserve empty and fully covered pixels exactly despite floating point round-off.
        result[0] = 0; result[255] = 255;
        return result;
    }
}
