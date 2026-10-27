using System.Numerics;
using Folio.Imaging;
using Folio.Painting;
using Folio.Skia;
using Folio.Typography;

namespace Folio.RenderTests;

/// <summary>
/// Folio's own raster backend against the Skia one: the same scene is drawn through both, and the pixels compared. Edge
/// pixels of antialiased shapes differ a little (the two sample coverage differently), so a scene passes when the mean
/// difference is small and few pixels differ by much; the numbers are in the failure message.
/// </summary>
public class SoftwareRasterTests(ITestOutputHelper output)
{
    private const int Size = 128;

    private static Paint Solid(float r, float g, float b, float a = 1) => new(new Rgba(r, g, b, a));

    private static PathData Circle(float cx, float cy, float r)
    {
        const float K = 0.5522848f;
        return new PathData().MoveTo(cx + r, cy)
            .CubicTo(new(cx + r, cy + K * r), new(cx + K * r, cy + r), new(cx, cy + r))
            .CubicTo(new(cx - K * r, cy + r), new(cx - r, cy + K * r), new(cx - r, cy))
            .CubicTo(new(cx - r, cy - K * r), new(cx - K * r, cy - r), new(cx, cy - r))
            .CubicTo(new(cx + K * r, cy - r), new(cx + r, cy - K * r), new(cx + r, cy)).Close();
    }

    private static PathData Star(float cx, float cy, float r)
    {
        var path = new PathData();
        for (var i = 0; i < 5; i++)
        {
            var a = -MathF.PI / 2 + i * 4 * MathF.PI / 5;
            var p = new Vector2(cx + r * MathF.Cos(a), cy + r * MathF.Sin(a));
            if (i == 0)
                path.MoveTo(p.X, p.Y);
            else
                path.LineTo(p.X, p.Y);
        }
        return path.Close();
    }

    private static PathData Polyline(params float[] xy)
    {
        var path = new PathData().MoveTo(xy[0], xy[1]);
        for (var i = 2; i < xy.Length; i += 2)
            path.LineTo(xy[i], xy[i + 1]);
        return path;
    }

    private static (double Mean, double OverEight, double OverForty, int Max) Compare(Action<ICanvas> scene, int width = Size, int height = Size, Action<ICanvas>? background = null)
    {
        using var skia = new SkiaRasterBackend().CreateSurface(width, height)!;
        using var software = SoftwareRasterBackend.Instance.CreateSurface(width, height)!;
        foreach (var surface in new[] { skia, software })
        {
            background?.Invoke(surface.Canvas);
            scene(surface.Canvas);
        }
        var expected = skia.ReadPixels(0, 0, width, height);
        var actual = software.ReadPixels(0, 0, width, height);
        double sum = 0;
        int over8 = 0, over40 = 0, max = 0;
        for (var i = 0; i < expected.Length; i += 4)
        {
            // Compare as premultiplied colour on white, so that near-transparent pixels' colours do not count.
            int worst = 0;
            for (var c = 0; c < 3; c++)
            {
                var e = Over(expected[i + c], expected[i + 3]);
                var a = Over(actual[i + c], actual[i + 3]);
                worst = Math.Max(worst, Math.Abs(e - a));
                sum += Math.Abs(e - a);
            }
            worst = Math.Max(worst, Math.Abs(expected[i + 3] - actual[i + 3]));
            max = Math.Max(max, worst);
            if (worst > 8)
                over8++;
            if (worst > 40)
                over40++;
        }
        var pixels = expected.Length / 4;
        return (sum / (pixels * 3), over8 / (double)pixels, over40 / (double)pixels, max);

        static int Over(byte c, byte a) => (c * a + 255 * (255 - a) + 127) / 255;
    }

    private void Check(string name, Action<ICanvas> scene, double mean = 0.6, double over8 = 0.02, double over40 = 0.004, int width = Size, int height = Size, Action<ICanvas>? background = null)
    {
        var r = Compare(scene, width, height, background);
        output.WriteLine($"{name}: mean {r.Mean:F3}, >8 {r.OverEight:P2}, >40 {r.OverForty:P2}, max {r.Max}");
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "software-raster-report.txt"), $"{name}: mean {r.Mean:F3}, >8 {r.OverEight:P2}, >40 {r.OverForty:P2}, max {r.Max}\n");
        Assert.True(r.Mean <= mean && r.OverEight <= over8 && r.OverForty <= over40, $"{name}: mean {r.Mean:F3} (max {mean}), >8 {r.OverEight:P2} (max {over8:P2}), >40 {r.OverForty:P2} (max {over40:P2}), worst {r.Max}");
    }

    // ---- fills ----

    [Fact]
    public void FillsRectanglesOnAndOffTheGrid() => Check("rectangles", c =>
    {
        c.FillRoundedRect(new RoundedRect(new RectF(10, 10, 40, 30), default), Solid(1, 0, 0));
        c.FillRoundedRect(new RoundedRect(new RectF(60.5f, 10.25f, 40.3f, 30.6f), default), Solid(0, 0.5f, 1));
        c.FillRoundedRect(new RoundedRect(new RectF(10, 60, 0.4f, 50), default), Solid(0, 0, 0));
    });

    [Fact]
    public void FillsRoundedRectangles() => Check("rounded rectangles", c =>
    {
        var radii = new CornerRadii(new(20, 20), new(10, 5), new(0, 0), new(30, 12));
        c.FillRoundedRect(new RoundedRect(new RectF(10, 10, 100, 60), radii), Solid(0.2f, 0.6f, 0.3f));
        c.FillRoundedRect(new RoundedRect(new RectF(20.5f, 80.5f, 60, 30), new CornerRadii(new(15, 15), new(15, 15), new(15, 15), new(15, 15))), Solid(0.8f, 0.2f, 0.5f, 0.6f));
    });

    [Fact]
    public void FillsCurvesAndOverlapsByEachRule()
    {
        Check("circle", c => c.FillPath(Circle(64, 64, 50), FillRule.NonZero, Solid(0.1f, 0.3f, 0.9f)));
        Check("star nonzero", c => c.FillPath(Star(64, 64, 55), FillRule.NonZero, Solid(0.9f, 0.5f, 0.1f)));
        Check("star evenodd", c => c.FillPath(Star(64, 64, 55), FillRule.EvenOdd, Solid(0.9f, 0.5f, 0.1f)));
        Check("opposite contours", c =>
        {
            var path = new PathData().MoveTo(10, 10).LineTo(100, 10).LineTo(100, 100).LineTo(10, 100).Close()
                .MoveTo(30, 30).LineTo(30, 80).LineTo(80, 80).LineTo(80, 30).Close();
            c.FillPath(path, FillRule.NonZero, Solid(0, 0, 0));
        });
    }

    [Fact]
    public void AppliesTransforms() => Check("rotated shapes", c =>
    {
        c.Save();
        c.Transform(Matrix3x2.CreateRotation(0.5f, new Vector2(64, 64)));
        c.FillRoundedRect(new RoundedRect(new RectF(30, 40, 70, 40), default), Solid(0.4f, 0.1f, 0.7f));
        c.Transform(Matrix3x2.CreateScale(0.6f, 1.4f));
        c.FillPath(Circle(60, 50, 22), FillRule.NonZero, Solid(1, 0.8f, 0, 0.7f));
        c.Restore();
    });

    // ---- strokes ----

    [Fact]
    public void StrokesWithEveryCapAndJoin()
    {
        foreach (var cap in Enum.GetValues<LineCap>())
            Check($"caps {cap}", c => c.StrokePath(Polyline(20, 30, 100, 30, 100, 80), new Stroke(14, cap), Solid(0, 0, 0)), mean: 1.2, over8: 0.04, over40: 0.012);
        foreach (var join in Enum.GetValues<LineJoin>())
            Check($"joins {join}", c => c.StrokePath(Polyline(20, 100, 60, 20, 100, 100), new Stroke(12, LineCap.Butt, Join: join), Solid(0.8f, 0, 0)), mean: 1.2, over8: 0.04, over40: 0.012);
        Check("miter limit", c => c.StrokePath(Polyline(20, 100, 60, 20, 100, 100), new Stroke(12, LineCap.Butt, Join: LineJoin.Miter, MiterLimit: 1.2f), Solid(0, 0.5f, 0)), mean: 1.2, over8: 0.04, over40: 0.012);
    }

    [Fact]
    public void StrokesClosedShapesThinLinesAndDashes()
    {
        Check("closed star", c => c.StrokePath(Star(64, 64, 50), new Stroke(5, Join: LineJoin.Round), Solid(0, 0, 0)), mean: 1.2, over8: 0.04, over40: 0.012);
        Check("thin lines", c =>
        {
            c.StrokePath(Polyline(10, 10.5f, 120, 20), new Stroke(1), Solid(0, 0, 0));
            c.StrokePath(Polyline(10, 40, 120, 40), new Stroke(0.5f), Solid(0, 0, 0));
            c.StrokePath(Polyline(10, 60.5f, 120, 60.5f), new Stroke(1), Solid(0, 0, 0));
        }, mean: 1.2, over8: 0.04, over40: 0.012);
        Check("dashes", c => c.StrokePath(Circle(64, 64, 45), new Stroke(6, LineCap.Butt, Dashes: [10, 6]), Solid(0.2f, 0.2f, 0.8f)), mean: 1.5, over8: 0.06, over40: 0.02);
        Check("round dots", c => c.StrokePath(Polyline(15, 64, 115, 64), new Stroke(8, LineCap.Round, Dashes: [0, 14]), Solid(0.9f, 0.1f, 0.1f)), mean: 1.2, over8: 0.04, over40: 0.012);
        Check("stroke under a transform", c =>
        {
            c.Transform(Matrix3x2.CreateScale(2, 0.5f) * Matrix3x2.CreateRotation(0.4f));
            c.StrokePath(new PathData().MoveTo(10, 40).LineTo(40, 70).LineTo(60, 40), new Stroke(8, LineCap.Square, Join: LineJoin.Miter), Solid(0, 0, 0));
        }, mean: 1.5, over8: 0.06, over40: 0.02);
    }

    // ---- clipping ----

    [Fact]
    public void ClipsByPathsAndRoundedRectangles()
    {
        Check("path clip", c =>
        {
            c.Save();
            c.ClipPath(Circle(64, 64, 45), FillRule.NonZero);
            c.FillRoundedRect(new RoundedRect(new RectF(0, 0, 128, 128), default), Solid(0.9f, 0.2f, 0.2f));
            c.FillPath(Star(64, 64, 70), FillRule.NonZero, Solid(0.2f, 0.2f, 0.9f, 0.7f));
            c.Restore();
        });
        Check("nested clips", c =>
        {
            c.Save();
            c.ClipRoundedRect(new RoundedRect(new RectF(10, 10, 100, 100), new CornerRadii(new(30, 30), new(30, 30), new(30, 30), new(30, 30))));
            c.Save();
            c.ClipPath(Circle(90, 40, 50), FillRule.NonZero);
            c.FillRoundedRect(new RoundedRect(new RectF(0, 0, 128, 128), default), Solid(0.1f, 0.6f, 0.3f));
            c.Restore();
            c.FillRoundedRect(new RoundedRect(new RectF(0, 64, 128, 64), default), Solid(0, 0, 0, 0.4f));
            c.Restore();
        });
    }

    // ---- gradients and patterns ----

    private static Gradient Linear(GradientSpread spread, float x0 = 20, float x1 = 70) => new(GradientKind.Linear,
        [new(0, new Rgba(1, 0, 0, 1)), new(0.5f, new Rgba(1, 1, 0, 1)), new(1, new Rgba(0, 0.4f, 1, 1))], spread, Start: new(x0, 0), End: new(x1, 40));

    [Fact]
    public void FillsLinearGradients()
    {
        foreach (var spread in Enum.GetValues<GradientSpread>())
            Check($"linear {spread}", c => c.FillRoundedRect(new RoundedRect(new RectF(0, 0, 128, 128), default), new Paint(default, Gradient: Linear(spread))), mean: 1.0, over8: 0.06, over40: 0.01);
        Check("linear with transform and alpha", c => c.FillRoundedRect(new RoundedRect(new RectF(5, 5, 118, 118), default), new Paint(default, Gradient: new Gradient(GradientKind.Linear,
            [new(0, new Rgba(0, 0, 0, 1)), new(1, new Rgba(1, 1, 1, 0.2f))], GradientSpread.Pad, Start: new(0, 0), End: new(100, 0), Transform: Matrix3x2.CreateRotation(0.7f)))), mean: 1.0, over8: 0.06, over40: 0.01);
    }

    [Fact]
    public void FillsRadialAndConicGradients()
    {
        var stops = new List<GradientStop> { new(0, new Rgba(1, 1, 1, 1)), new(0.4f, new Rgba(0.9f, 0.3f, 0.1f, 1)), new(1, new Rgba(0.1f, 0.1f, 0.5f, 1)) };
        Check("radial circle", c => c.FillRoundedRect(new RoundedRect(new RectF(0, 0, 128, 128), default), new Paint(default, Gradient: new Gradient(GradientKind.Radial, stops, Center: new(64, 64), Radii: new(50, 50)))), mean: 1.0, over8: 0.06, over40: 0.01);
        Check("radial ellipse", c => c.FillRoundedRect(new RoundedRect(new RectF(0, 0, 128, 128), default), new Paint(default, Gradient: new Gradient(GradientKind.Radial, stops, GradientSpread.Repeat, Center: new(64, 64), Radii: new(60, 30)))), mean: 1.0, over8: 0.06, over40: 0.01);
        Check("radial focal", c => c.FillRoundedRect(new RoundedRect(new RectF(0, 0, 128, 128), default), new Paint(default, Gradient: new Gradient(GradientKind.Radial, stops, Center: new(64, 64), Radii: new(50, 50), Focus: new Vector2(40, 50), FocusRadius: 5))), mean: 1.5, over8: 0.08, over40: 0.02);
        Check("conic", c => c.FillRoundedRect(new RoundedRect(new RectF(0, 0, 128, 128), default), new Paint(default, Gradient: new Gradient(GradientKind.Conic, stops, Center: new(64, 64), StartAngle: 30, EndAngle: 390))), mean: 1.0, over8: 0.06, over40: 0.02);
    }

    private static IImageHandle Checker()
    {
        var pixels = new byte[8 * 8 * 4];
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
            {
                var on = ((x / 2) + (y / 2)) % 2 == 0;
                var i = (y * 8 + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = on ? ((byte)230, (byte)40, (byte)40, (byte)255) : ((byte)30, (byte)60, (byte)200, (byte)(x < 4 ? 255 : 128));
            }
        return new DecodedImage(8, 8, pixels);
    }

    [Fact]
    public void DrawsAndTilesImages()
    {
        var image = Checker();
        Check("image smooth", c => c.DrawImage(image, new RectF(10.5f, 10.5f, 100, 90), ImageSampling.Smooth), mean: 3.0, over8: 0.2, over40: 0.06);
        Check("image pixelated", c => c.DrawImage(image, new RectF(10, 10, 96, 96), ImageSampling.Pixelated), mean: 1.0, over8: 0.05, over40: 0.03);
        Check("pattern repeat", c => c.FillRoundedRect(new RoundedRect(new RectF(0, 0, 128, 128), default), new Paint(new Rgba(0, 0, 0, 1), Pattern: new ImagePattern(image, true, true, Matrix3x2.CreateScale(4)))), mean: 3.0, over8: 0.12, over40: 0.06);
        Check("pattern once", c => c.FillRoundedRect(new RoundedRect(new RectF(0, 0, 128, 128), default), new Paint(new Rgba(0, 0, 0, 0.8f), Pattern: new ImagePattern(image, false, false, Matrix3x2.CreateScale(8) * Matrix3x2.CreateTranslation(20, 20)))), mean: 3.0, over8: 0.12, over40: 0.06);
    }

    // ---- compositing ----

    [Fact]
    public void CombinesByEveryBlendMode()
    {
        foreach (var mode in Enum.GetValues<BlendMode>())
        {
            Check($"blend {mode}", c =>
            {
                c.FillRoundedRect(new RoundedRect(new RectF(10, 10, 70, 70), default), Solid(0.8f, 0.3f, 0.2f));
                c.FillRoundedRect(new RoundedRect(new RectF(40, 40, 70, 70), default), Solid(0.2f, 0.6f, 0.9f, 0.8f) with { Blend = mode });
            }, mean: 1.0, over8: 0.03, over40: 0.01);
        }
    }

    [Fact]
    public void ComposesLayersWithOpacityAndBlendModes()
    {
        Check("layer opacity", c =>
        {
            c.PushLayer(new LayerOptions(0.5f));
            c.FillRoundedRect(new RoundedRect(new RectF(10, 10, 70, 70), default), Solid(1, 0, 0));
            c.FillRoundedRect(new RoundedRect(new RectF(40, 40, 70, 70), default), Solid(0, 0, 1));
            c.PopLayer();
        });
        Check("nested layers", c =>
        {
            c.PushLayer(new LayerOptions(0.8f));
            c.FillRoundedRect(new RoundedRect(new RectF(10, 10, 100, 100), default), Solid(0.2f, 0.7f, 0.3f));
            c.PushLayer(new LayerOptions(0.5f, Blend: BlendMode.Multiply));
            c.FillPath(Circle(64, 64, 40), FillRule.NonZero, Solid(0.9f, 0.4f, 0.1f));
            c.PopLayer();
            c.PopLayer();
        });
    }

    // ---- filters and blur ----

    [Fact]
    public void BlursPaintsAndLayers()
    {
        Check("blurred fill", c => c.FillRoundedRect(new RoundedRect(new RectF(30, 30, 60, 60), default), Solid(0, 0, 0, 0.8f) with { Blur = 6 }), mean: 2.0, over8: 0.08, over40: 0.01);
        Check("blurred fill under a stretch", c =>
        {
            c.Transform(Matrix3x2.CreateScale(1.8f, 0.6f));
            c.FillRoundedRect(new RoundedRect(new RectF(15, 40, 40, 80), default), Solid(0, 0, 0, 0.8f) with { Blur = 6 });
        }, mean: 2.0, over8: 0.08, over40: 0.01);
        Check("blurred fill under a rotation", c =>
        {
            c.Transform(Matrix3x2.CreateRotation(0.6f, new Vector2(64, 64)));
            c.FillRoundedRect(new RoundedRect(new RectF(30, 40, 60, 40), default), Solid(0, 0, 0, 0.8f) with { Blur = 5 });
        }, mean: 2.0, over8: 0.08, over40: 0.01);
        Check("blur filter", c =>
        {
            c.PushLayer(new LayerOptions(1, [new Filter(FilterKind.Blur, 4)]));
            c.FillPath(Star(64, 64, 45), FillRule.NonZero, Solid(0.9f, 0.1f, 0.1f));
            c.PopLayer();
        }, mean: 1.5, over8: 0.1, over40: 0.02);
        Check("large blur filter", c =>
        {
            c.PushLayer(new LayerOptions(1, [new Filter(FilterKind.Blur, 12)]));
            c.FillRoundedRect(new RoundedRect(new RectF(30, 30, 60, 60), default), Solid(0.1f, 0.1f, 0.9f));
            c.PopLayer();
        }, mean: 2.0, over8: 0.15, over40: 0.03);
        Check("drop shadow", c =>
        {
            c.PushLayer(new LayerOptions(1, [new Filter(FilterKind.DropShadow, 3, Offset: new Vector2(6, 6), Color: new Rgba(0, 0, 0, 0.6f))]));
            c.FillRoundedRect(new RoundedRect(new RectF(25, 25, 60, 60), default), Solid(0.9f, 0.7f, 0.1f));
            c.PopLayer();
        }, mean: 2.0, over8: 0.15, over40: 0.03);
        Check("colour matrix", c =>
        {
            c.PushLayer(new LayerOptions(1, [new Filter(FilterKind.ColorMatrix, Matrix: [0.33f, 0.33f, 0.33f, 0, 0, 0.33f, 0.33f, 0.33f, 0, 0, 0.33f, 0.33f, 0.33f, 0, 0, 0, 0, 0, 1, 0])]));
            c.FillPath(Star(64, 64, 45), FillRule.NonZero, Solid(0.9f, 0.1f, 0.1f));
            c.FillPath(Circle(64, 64, 20), FillRule.NonZero, Solid(0.1f, 0.8f, 0.2f));
            c.PopLayer();
        });
    }

    // ---- text ----

    private static FontFace Load(string name) => FontFace.Parse(File.ReadAllBytes(Path.Combine(RepoPaths.Tests, "fonts", name)))!;

    [Fact]
    public void DrawsTextFromGlyphOutlines()
    {
        var face = Load("SourceSans3-Regular.ttf");
        ushort[] glyphs = [.. "Folio texts"u8.ToArray().Select(b => face.GlyphFor(b))];
        Vector2[] Origins(float size)
        {
            var origins = new Vector2[glyphs.Length];
            var x = 6f;
            for (var i = 0; i < glyphs.Length; i++)
            {
                origins[i] = new Vector2(x, 0);
                x += face.Advance(glyphs[i]) * size / face.UnitsPerEm;
            }
            return origins;
        }
        // Text edges are thinner features than shapes', and the two rasterisers weigh them differently; the limits are looser.
        Check("text 18px", c => c.DrawGlyphs(face, 18, glyphs, Origins(18).Select(o => o + new Vector2(0, 40)).ToArray(), Solid(0, 0, 0)), mean: 2.0, over8: 0.12, over40: 0.04, background: White);
        Check("text 40px", c => c.DrawGlyphs(face, 40, glyphs, Origins(40).Select(o => o + new Vector2(0, 90)).ToArray(), Solid(0.1f, 0.1f, 0.6f)), mean: 3.0, over8: 0.10, over40: 0.02, background: White, width: 300, height: 128);
    }

    [Fact]
    public void DrawsTextFromCff2GlyphOutlines()
    {
        var face = Load("FolioCff2.otf");
        var glyph = face.GlyphFor('A');
        Assert.NotEqual((ushort)0, glyph);
        using var surface = SoftwareRasterBackend.Instance.CreateSurface(96, 96)!;
        White(surface.Canvas);
        surface.Canvas.DrawGlyphs(face, 64, [glyph], [new Vector2(12, 76)], Solid(0.1f, 0.1f, 0.6f));
        var pixels = surface.ReadPixels(0, 0, 96, 96);
        Assert.True(pixels.Chunk(4).Count(p => p[3] > 0 && p[0] < 250) > 500,
            "The CFF2 glyph did not produce a raster outline.");
    }

    [Fact]
    public void DecodesAndDrawsCff2FromWoff2()
    {
        var compressed = File.ReadAllBytes(Path.Combine(RepoPaths.Tests, "fonts", "FolioCff2.woff2"));
        var face = FontFace.Parse(WebFontDecoder.Decode(compressed));
        Assert.NotNull(face);
        var glyph = face!.GlyphFor('A');
        using var surface = SoftwareRasterBackend.Instance.CreateSurface(96, 96)!;
        White(surface.Canvas);
        surface.Canvas.DrawGlyphs(face, 64, [glyph], [new Vector2(12, 76)], Solid(0.1f, 0.1f, 0.6f));
        var pixels = surface.ReadPixels(0, 0, 96, 96);
        Assert.True(pixels.Chunk(4).Count(p => p[3] > 0 && p[0] < 250) > 500,
            "The WOFF2 CFF2 glyph did not produce a raster outline.");
    }

    [Fact]
    public void DrawsBitmapColourGlyphs()
    {
        var face = Load("NotoColorEmoji.ttf");
        var glyph = face.GlyphFor(0x1F600); // 😀, a CBDT/CBLC PNG glyph.
        Assert.NotEqual((ushort)0, glyph);
        Check("bitmap colour glyph", c => c.DrawGlyphs(face, 48, [glyph], [new Vector2(12, 58)], Solid(1, 1, 1)),
            mean: 4.0, over8: 0.12, over40: 0.04, background: White, width: 96, height: 80);
    }

    private static void White(ICanvas c) => c.FillRoundedRect(new RoundedRect(new RectF(0, 0, 1000, 1000), default), Solid(1, 1, 1));
}
