using System.Numerics;
using Folio.Imaging;
using Folio.Painting;

namespace Folio.RenderTests;

/// <summary>
/// Folio's own raster backend against values worked out by hand: the coverage of simple shapes (#568), the sampling modes of
/// images at 0.5, 1 and 3 times (#570) and every blend mode against the formulas of the Compositing specification (#571).
/// </summary>
public class SoftwareRasterUnitTests
{
    private static SoftwareRasterSurface Surface(int size = 16) => new(size, size);

    private static byte[] Read(SoftwareRasterSurface surface) => surface.ReadPixels(0, 0, surface.Width, surface.Height);

    private static int Alpha(byte[] pixels, int width, int x, int y) => pixels[(y * width + x) * 4 + 3];

    private static void Fill(SoftwareRasterSurface surface, PathData path, FillRule rule = FillRule.NonZero) =>
        surface.Canvas.FillPath(path, rule, new Paint(new Rgba(0, 0, 0, 1)));

    private static PathData Polygon(params (float X, float Y)[] points)
    {
        var path = new PathData().MoveTo(points[0].X, points[0].Y);
        foreach (var (x, y) in points.Skip(1))
            path.LineTo(x, y);
        return path.Close();
    }

    // ---- coverage ----

    [Fact]
    public void ARectangleOnWholePixelsCoversThemFullyAndNothingAround()
    {
        using var surface = Surface();
        Fill(surface, Polygon((2, 3), (6, 3), (6, 8), (2, 8)));
        var pixels = Read(surface);
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
                Assert.Equal(x is >= 2 and < 6 && y is >= 3 and < 8 ? 255 : 0, Alpha(pixels, 16, x, y));
        }
    }

    [Fact]
    public void ARectangleOnHalfPixelsCoversTheEdgePixelsByHalf()
    {
        using var surface = Surface();
        Fill(surface, Polygon((0.5f, 0), (2.5f, 0), (2.5f, 2), (0.5f, 2)));
        var pixels = Read(surface);
        Assert.InRange(Alpha(pixels, 16, 0, 0), 126, 129);
        Assert.Equal(255, Alpha(pixels, 16, 1, 0));
        Assert.InRange(Alpha(pixels, 16, 2, 1), 126, 129);
        Assert.Equal(0, Alpha(pixels, 16, 3, 0));
    }

    [Fact]
    public void ATriangleCoversItsAreaAndHalfOfThePixelsOnItsDiagonal()
    {
        using var surface = Surface(8);
        Fill(surface, Polygon((0, 0), (4, 0), (0, 4)));
        var pixels = Read(surface);
        Assert.Equal(255, Alpha(pixels, 8, 0, 0));
        Assert.Equal(0, Alpha(pixels, 8, 3, 3));
        // The diagonal x + y = 4 splits each of the pixels (3, 0), (2, 1), (1, 2) and (0, 3) in half.
        foreach (var (x, y) in new[] { (3, 0), (2, 1), (1, 2), (0, 3) })
            Assert.InRange(Alpha(pixels, 8, x, y), 125, 130);
        var total = 0;
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
                total += Alpha(pixels, 8, x, y);
        }
        Assert.InRange(total / 255.0, 7.9, 8.1); // the triangle's area is 8
    }

    [Fact]
    public void ACircleCoversItsArea()
    {
        using var surface = Surface(40);
        surface.Canvas.FillRoundedRect(new RoundedRect(new RectF(10, 10, 20, 20), new CornerRadii(new(10, 10), new(10, 10), new(10, 10), new(10, 10))), new Paint(new Rgba(0, 0, 0, 1)));
        var pixels = Read(surface);
        var total = 0.0;
        for (var y = 0; y < 40; y++)
        {
            for (var x = 0; x < 40; x++)
                total += Alpha(pixels, 40, x, y) / 255.0;
        }
        Assert.InRange(total, Math.PI * 100 * 0.99, Math.PI * 100 * 1.01);
        Assert.Equal(255, Alpha(pixels, 40, 20, 20));
        Assert.Equal(0, Alpha(pixels, 40, 10, 10));
    }

    [Fact]
    public void EvenOddLeavesTheHoleOfNestedShapesAndNonZeroFillsIt()
    {
        PathData Nested() => new PathData().MoveTo(0, 0).LineTo(12, 0).LineTo(12, 12).LineTo(0, 12).Close().MoveTo(3, 3).LineTo(9, 3).LineTo(9, 9).LineTo(3, 9).Close();
        using var evenOdd = Surface();
        Fill(evenOdd, Nested(), FillRule.EvenOdd);
        Assert.Equal(0, Alpha(Read(evenOdd), 16, 5, 5));
        Assert.Equal(255, Alpha(Read(evenOdd), 16, 1, 1));
        using var nonZero = Surface();
        Fill(nonZero, Nested(), FillRule.NonZero);
        Assert.Equal(255, Alpha(Read(nonZero), 16, 5, 5));
    }

    // ---- image sampling ----

    // Four by four: red grows with x by 60, green with y by 60, blue 128.
    private static IImageHandle Ramp()
    {
        var pixels = new byte[4 * 4 * 4];
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                var i = (y * 4 + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = ((byte)(60 * x), (byte)(60 * y), (byte)128, (byte)255);
            }
        }
        return new DecodedImage(4, 4, pixels);
    }

    private static byte[] Drawn(int size, float scale, ImageSampling sampling)
    {
        using var surface = Surface(size);
        surface.Canvas.DrawImage(Ramp(), new RectF(0, 0, 4 * scale, 4 * scale), sampling);
        return Read(surface);
    }

    private static int Red(byte[] pixels, int width, int x, int y) => pixels[(y * width + x) * 4];

    private static int Green(byte[] pixels, int width, int x, int y) => pixels[(y * width + x) * 4 + 1];

    [Fact]
    public void AnImageAtOneTimesIsCopiedExactlyByBothSamplingModes()
    {
        foreach (var sampling in new[] { ImageSampling.Smooth, ImageSampling.Pixelated })
        {
            var pixels = Drawn(8, 1, sampling);
            for (var y = 0; y < 4; y++)
            {
                for (var x = 0; x < 4; x++)
                {
                    Assert.Equal(60 * x, Red(pixels, 8, x, y));
                    Assert.Equal(60 * y, Green(pixels, 8, x, y));
                }
            }
        }
    }

    [Fact]
    public void AnImageAtThreeTimesRepeatsPixelsWhenPixelatedAndInterpolatesWhenSmooth()
    {
        var pixelated = Drawn(16, 3, ImageSampling.Pixelated);
        for (var x = 0; x < 12; x++)
            Assert.Equal(60 * (x / 3), Red(pixelated, 16, x, 0));
        for (var y = 0; y < 12; y++)
            Assert.Equal(60 * (y / 3), Green(pixelated, 16, 0, y));

        var smooth = Drawn(16, 3, ImageSampling.Smooth);
        // A destination pixel's centre (d + 0.5) / 3 sits d / 3 - 1/3 texels from the first texel's centre.
        Assert.Equal(0, Red(smooth, 16, 0, 4));
        for (var d = 1; d < 11; d++)
            Assert.InRange(Red(smooth, 16, d, 4), 60 * (d / 3.0 - 1 / 3.0) - 2, 60 * (d / 3.0 - 1 / 3.0) + 2);
        Assert.InRange(Red(smooth, 16, 11, 4), 178, 181); // clamped at the last texel
    }

    [Fact]
    public void AnImageAtHalfAverages2x2BlocksWhenSmooth()
    {
        var pixels = Drawn(4, 0.5f, ImageSampling.Smooth);
        Assert.InRange(Red(pixels, 4, 0, 0), 28, 32); // columns 0 and 1: (0 + 60) / 2
        Assert.InRange(Red(pixels, 4, 1, 0), 148, 152); // columns 2 and 3: (120 + 180) / 2
        Assert.InRange(Green(pixels, 4, 0, 0), 28, 32);
        Assert.InRange(Green(pixels, 4, 0, 1), 148, 152);
    }

    // ---- blend modes ----

    private static double Lum(double[] c) => 0.3 * c[0] + 0.59 * c[1] + 0.11 * c[2];

    private static double[] ClipColor(double[] c)
    {
        var l = Lum(c);
        var n = c.Min();
        var x = c.Max();
        if (n < 0)
            c = c.Select(v => l + (v - l) * l / (l - n)).ToArray();
        if (x > 1)
            c = c.Select(v => l + (v - l) * (1 - l) / (x - l)).ToArray();
        return c;
    }

    private static double[] SetLum(double[] c, double l)
    {
        var d = l - Lum(c);
        return ClipColor(c.Select(v => v + d).ToArray());
    }

    private static double Sat(double[] c) => c.Max() - c.Min();

    private static double[] SetSat(double[] c, double s)
    {
        var order = new[] { 0, 1, 2 }.OrderBy(i => c[i]).ToArray();
        var (min, mid, max) = (order[0], order[1], order[2]);
        var result = new double[3];
        if (c[max] > c[min])
        {
            result[mid] = (c[mid] - c[min]) * s / (c[max] - c[min]);
            result[max] = s;
        }
        return result;
    }

    private static double Separable(BlendMode mode, double cb, double cs)
    {
        double HardLight(double b, double s) => s <= 0.5 ? b * 2 * s : b + (2 * s - 1) - b * (2 * s - 1);
        return mode switch
        {
            BlendMode.Multiply => cb * cs,
            BlendMode.Screen => cb + cs - cb * cs,
            BlendMode.Overlay => HardLight(cs, cb),
            BlendMode.Darken => Math.Min(cb, cs),
            BlendMode.Lighten => Math.Max(cb, cs),
            BlendMode.ColorDodge => cb == 0 ? 0 : cs == 1 ? 1 : Math.Min(1, cb / (1 - cs)),
            BlendMode.ColorBurn => cb == 1 ? 1 : cs == 0 ? 0 : 1 - Math.Min(1, (1 - cb) / cs),
            BlendMode.HardLight => HardLight(cb, cs),
            BlendMode.SoftLight => cs <= 0.5 ? cb - (1 - 2 * cs) * cb * (1 - cb)
                : cb + (2 * cs - 1) * ((cb <= 0.25 ? ((16 * cb - 12) * cb + 4) * cb : Math.Sqrt(cb)) - cb),
            BlendMode.Difference => Math.Abs(cb - cs),
            BlendMode.Exclusion => cb + cs - 2 * cb * cs,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }

    // The premultiplied result of source (straight colour cs, alpha sa) over a backdrop (cb, ab), by the compositing spec.
    private static double[] Expected(BlendMode mode, double[] cs, double sa, double[] cb, double ab)
    {
        var s = cs.Select(v => v * sa).ToArray();
        var b = cb.Select(v => v * ab).ToArray();
        double[] PorterDuff(double fa, double fb) => [.. Enumerable.Range(0, 3).Select(i => s[i] * fa + b[i] * fb), sa * fa + ab * fb];
        switch (mode)
        {
            case BlendMode.PlusLighter: return [.. Enumerable.Range(0, 3).Select(i => Math.Min(1, s[i] + b[i])), Math.Min(1, sa + ab)];
            case BlendMode.SourceIn: return PorterDuff(ab, 0);
            case BlendMode.SourceOut: return PorterDuff(1 - ab, 0);
            case BlendMode.DestinationIn: return PorterDuff(0, sa);
            case BlendMode.Xor: return PorterDuff(1 - ab, 1 - sa);
            case BlendMode.SourceAtop: return PorterDuff(ab, 1 - sa);
            case BlendMode.DestinationOver: return PorterDuff(1 - ab, 1);
            case BlendMode.DestinationOut: return PorterDuff(0, 1 - sa);
            case BlendMode.DestinationAtop: return PorterDuff(1 - ab, sa);
            case BlendMode.Copy: return PorterDuff(1, 0);
        }
        double[] blended = mode switch
        {
            BlendMode.Normal => cs,
            BlendMode.Hue => SetLum(SetSat(cs, Sat(cb)), Lum(cb)),
            BlendMode.Saturation => SetLum(SetSat(cb, Sat(cs)), Lum(cb)),
            BlendMode.Color => SetLum(cs, Lum(cb)),
            BlendMode.Luminosity => SetLum(cb, Lum(cs)),
            _ => [.. Enumerable.Range(0, 3).Select(i => Separable(mode, cb[i], cs[i]))],
        };
        return [.. Enumerable.Range(0, 3).Select(i => sa * (1 - ab) * cs[i] + sa * ab * blended[i] + (1 - sa) * ab * cb[i]), sa + ab * (1 - sa)];
    }

    [Fact]
    public void EveryBlendModeMatchesTheFormulasOfTheCompositingSpecification()
    {
        // Colours on byte steps, so the inputs are exact; the result is rounded to bytes twice (premultiplied, then straight).
        double[][] colours = [[204 / 255.0, 51 / 255.0, 128 / 255.0], [0, 1, 128 / 255.0], [26 / 255.0, 179 / 255.0, 230 / 255.0], [1, 1, 1], [0, 0, 0]];
        var failures = new List<string>();
        foreach (var mode in Enum.GetValues<BlendMode>())
        {
            foreach (var source in colours)
            {
                foreach (var backdrop in colours)
                {
                    foreach (var (sa, ab) in new[] { (1.0, 1.0), (0.5, 1.0), (1.0, 0.6), (0.5, 0.6) })
                    {
                        // 128/255 and 153/255 are the alphas that 0.5 and 0.6 become as bytes.
                        var (alphaS, alphaB) = (Math.Round(sa * 255) / 255, Math.Round(ab * 255) / 255);
                        using var surface = Surface(4);
                        var canvas = surface.Canvas;
                        canvas.FillRoundedRect(new RoundedRect(new RectF(0, 0, 4, 4), default), new Paint(new Rgba((float)backdrop[0], (float)backdrop[1], (float)backdrop[2], (float)alphaB)));
                        canvas.FillRoundedRect(new RoundedRect(new RectF(0, 0, 4, 4), default), new Paint(new Rgba((float)source[0], (float)source[1], (float)source[2], (float)alphaS), Blend: mode));
                        var actual = surface.ReadPixels(1, 1, 1, 1);
                        var expected = Expected(mode, source, alphaS, backdrop, alphaB);
                        // The pixel is read straight; compare premultiplied, within the rounding of the bytes the backdrop and the result are held in.
                        var a = actual[3] / 255.0;
                        double[] got = [actual[0] / 255.0 * a, actual[1] / 255.0 * a, actual[2] / 255.0 * a, a];
                        if (got.Zip(expected, (g, e) => Math.Abs(g - e)).Max() > 3.0 / 255)
                            failures.Add($"{mode} s=({string.Join(",", source.Select(v => v.ToString("0.00")))})@{alphaS:0.00} b=({string.Join(",", backdrop.Select(v => v.ToString("0.00")))})@{alphaB:0.00}: " +
                                         $"got ({string.Join(",", got.Select(v => v.ToString("0.000")))}), expected ({string.Join(",", expected.Select(v => v.ToString("0.000")))})");
                    }
                }
            }
        }
        Assert.True(failures.Count == 0, $"{failures.Count} blends differ from the specification:\n" + string.Join("\n", failures.Take(15)));
    }
}
