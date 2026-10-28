using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Folio.Imaging;
using Folio.Painting;
using Folio.Painting.Software;
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

    private static byte[] AddTables(byte[] source, params (string Tag, byte[] Data)[] additions)
    {
        var oldCount = BinaryPrimitives.ReadUInt16BigEndian(source.AsSpan(4));
        var directoryShift = 16 * additions.Length;
        var oldHeader = 12 + 16 * oldCount;
        var shiftedLength = checked(source.Length + directoryShift);
        var output = new byte[shiftedLength + additions.Sum(item => (item.Data.Length + 3) & ~3)];
        source.AsSpan(0, oldHeader).CopyTo(output);
        source.AsSpan(oldHeader).CopyTo(output.AsSpan(oldHeader + directoryShift));
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(4), checked((ushort)(oldCount + additions.Length)));
        for (var i = 0; i < oldCount; i++)
        {
            var record = 12 + 16 * i;
            var oldOffset = BinaryPrimitives.ReadUInt32BigEndian(output.AsSpan(record + 8));
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(record + 8), checked(oldOffset + (uint)directoryShift));
        }

        var write = shiftedLength;
        for (var i = 0; i < additions.Length; i++)
        {
            var (tag, data) = additions[i];
            write = (write + 3) & ~3;
            var record = 12 + 16 * (oldCount + i);
            Encoding.ASCII.GetBytes(tag).CopyTo(output.AsSpan(record, 4));
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(record + 8), checked((uint)write));
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(record + 12), checked((uint)data.Length));
            data.CopyTo(output.AsSpan(write));
            write += (data.Length + 3) & ~3;
        }
        return output;
    }

    private static byte[] SyntheticSbix(int glyphCount, ushort glyph, byte[] png)
    {
        // sbix header + one strike. Strike offsets and glyph data offsets are
        // relative to the strike, as required by the OpenType sbix table.
        var strikeHeader = 8 + 4 * (glyphCount + 1);
        var start = strikeHeader;
        var end = checked(start + 8 + png.Length); // origin (4) + tag (4) + PNG
        var sbix = new byte[8 + 4 + strikeHeader + 8 + png.Length];
        BinaryPrimitives.WriteUInt32BigEndian(sbix.AsSpan(4), 1); // one strike
        BinaryPrimitives.WriteUInt32BigEndian(sbix.AsSpan(8), 12); // strike starts after header and offset
        BinaryPrimitives.WriteUInt16BigEndian(sbix.AsSpan(12), 64); // ppem
        BinaryPrimitives.WriteUInt16BigEndian(sbix.AsSpan(14), 72); // resolution
        for (var i = 0; i <= glyphCount; i++)
            BinaryPrimitives.WriteUInt32BigEndian(sbix.AsSpan(16 + 4 * i), i <= glyph ? (i == glyph ? (uint)start : 0u) : (uint)end);
        var data = 20 + 4 * (glyphCount + 1);
        BinaryPrimitives.WriteInt16BigEndian(sbix.AsSpan(data), 0);
        BinaryPrimitives.WriteInt16BigEndian(sbix.AsSpan(data + 2), 1);
        "png "u8.CopyTo(sbix.AsSpan(data + 4));
        png.CopyTo(sbix.AsSpan(data + 8));
        return sbix;
    }

    private static byte[] SyntheticColrV1(ushort glyph)
    {
        // COLR v1 header (base glyph list at offset 34), followed by one
        // PaintGlyph->PaintSolid graph using palette entry 0.
        var colr = new byte[56];
        BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(0), 1);
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(14), 34);
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(34), 1);
        BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(38), glyph);
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(40), 10);
        colr[44] = 10; // PaintGlyph
        colr[45] = 0; colr[46] = 0; colr[47] = 6; // child PaintSolid
        BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(48), glyph);
        colr[50] = 2; // PaintSolid
        BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(51), 0);
        BinaryPrimitives.WriteInt16BigEndian(colr.AsSpan(53), 0x4000);
        return colr;
    }

    private static byte[] SyntheticColrV1VarSolid(ushort glyph)
    {
        var colr = SyntheticColrV1(glyph);
        Array.Resize(ref colr, 60);
        colr[50] = 3; // PaintVarSolid; default-instance alpha follows the base value.
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(55), 0); // variation index
        return colr;
    }

    private static byte[] SyntheticColrV1Translated(ushort glyph)
    {
        var colr = new byte[68];
        BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(0), 1);
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(14), 34);
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(34), 1);
        BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(38), glyph);
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(40), 10);
        colr[44] = 14; colr[45] = 0; colr[46] = 0; colr[47] = 12; // PaintTranslate -> PaintGlyph
        BinaryPrimitives.WriteInt32BigEndian(colr.AsSpan(48), 20 << 16);
        BinaryPrimitives.WriteInt32BigEndian(colr.AsSpan(52), 0);
        colr[56] = 10; colr[57] = 0; colr[58] = 0; colr[59] = 6;
        BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(60), glyph);
        colr[62] = 2; BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(63), 0); BinaryPrimitives.WriteInt16BigEndian(colr.AsSpan(65), 0x4000);
        return colr;
    }

    private static byte[] SyntheticColrV1VarTranslated(ushort glyph)
    {
        var colr = SyntheticColrV1Translated(glyph);
        colr[44] = 15; // PaintVarTranslate; default-instance translation uses base dx/dy.
        return colr;
    }

    private static byte[] SyntheticColrV1Transform(ushort glyph)
    {
        var colr = new byte[84];
        BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(0), 1);
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(14), 34);
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(34), 1);
        BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(38), glyph);
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(40), 10);
        colr[44] = 12; colr[45] = 0; colr[46] = 0; colr[47] = 28; // PaintTransform -> PaintGlyph
        BinaryPrimitives.WriteInt32BigEndian(colr.AsSpan(48), 1 << 16);
        BinaryPrimitives.WriteInt32BigEndian(colr.AsSpan(52), 0);
        BinaryPrimitives.WriteInt32BigEndian(colr.AsSpan(56), 0);
        BinaryPrimitives.WriteInt32BigEndian(colr.AsSpan(60), 1 << 16);
        BinaryPrimitives.WriteInt32BigEndian(colr.AsSpan(64), 20 << 16);
        BinaryPrimitives.WriteInt32BigEndian(colr.AsSpan(68), 0);
        colr[72] = 10; colr[73] = 0; colr[74] = 0; colr[75] = 6;
        BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(76), glyph);
        colr[78] = 2; BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(79), 0); BinaryPrimitives.WriteInt16BigEndian(colr.AsSpan(81), 0x4000);
        return colr;
    }

    private static byte[] SyntheticColrV1Scale(ushort glyph)
    {
        var colr = new byte[76];
        BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(0), 1);
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(14), 34);
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(34), 1);
        BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(38), glyph);
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(40), 10);
        colr[44] = 16; colr[45] = 0; colr[46] = 0; colr[47] = 12; // PaintScale -> PaintGlyph
        BinaryPrimitives.WriteInt16BigEndian(colr.AsSpan(48), 0x6000);
        BinaryPrimitives.WriteInt16BigEndian(colr.AsSpan(50), 0x6000);
        colr[56] = 10; colr[57] = 0; colr[58] = 0; colr[59] = 6;
        BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(60), glyph);
        colr[62] = 2; BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(63), 0); BinaryPrimitives.WriteInt16BigEndian(colr.AsSpan(65), 0x4000);
        return colr;
    }

    private static byte[] SyntheticColrV1Rotate(ushort glyph)
    {
        var colr = SyntheticColrV1Scale(glyph);
        colr[44] = 24; // PaintRotate -> PaintGlyph
        BinaryPrimitives.WriteInt16BigEndian(colr.AsSpan(48), 0x2000); // 90 degrees
        return colr;
    }

    private static byte[] SyntheticColrV1Skew(ushort glyph)
    {
        var colr = SyntheticColrV1Scale(glyph);
        colr[44] = 28; // PaintSkew -> PaintGlyph
        BinaryPrimitives.WriteInt16BigEndian(colr.AsSpan(48), 0x1000);
        BinaryPrimitives.WriteInt16BigEndian(colr.AsSpan(50), 0);
        return colr;
    }

    private static byte[] SyntheticColrV1LinearGradient(ushort glyph)
    {
        // PaintGlyph -> PaintLinearGradient with two CPAL stops and a horizontal
        // font-unit gradient. The graph exercises the Folio software shader path.
        var colr = new byte[104];
        BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(0), 1);
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(14), 34);
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(34), 1);
        BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(38), glyph);
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(40), 10);
        colr[44] = 10; colr[45] = 0; colr[46] = 0; colr[47] = 12;
        BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(48), glyph);
        colr[56] = 4; colr[57] = 0; colr[58] = 0; colr[59] = 30;
        BinaryPrimitives.WriteInt16BigEndian(colr.AsSpan(60), 0);
        BinaryPrimitives.WriteInt16BigEndian(colr.AsSpan(62), 0);
        BinaryPrimitives.WriteInt16BigEndian(colr.AsSpan(64), 500);
        BinaryPrimitives.WriteInt16BigEndian(colr.AsSpan(66), 0);
        BinaryPrimitives.WriteInt16BigEndian(colr.AsSpan(68), 0);
        BinaryPrimitives.WriteInt16BigEndian(colr.AsSpan(70), 500);
        colr[86] = 0; BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(87), 2);
        BinaryPrimitives.WriteInt16BigEndian(colr.AsSpan(89), 0);
        BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(91), 0);
        BinaryPrimitives.WriteInt16BigEndian(colr.AsSpan(93), 0x4000);
        BinaryPrimitives.WriteInt16BigEndian(colr.AsSpan(95), 0x4000);
        BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(97), 1);
        BinaryPrimitives.WriteInt16BigEndian(colr.AsSpan(99), 0x4000);
        return colr;
    }

    private static byte[] SyntheticColrV1RadialGradient(ushort glyph)
    {
        var colr = SyntheticColrV1LinearGradient(glyph);
        colr[56] = 6; // PaintRadialGradient
        BinaryPrimitives.WriteInt16BigEndian(colr.AsSpan(60), 500);
        BinaryPrimitives.WriteInt16BigEndian(colr.AsSpan(62), 500);
        BinaryPrimitives.WriteInt16BigEndian(colr.AsSpan(64), 500);
        BinaryPrimitives.WriteInt16BigEndian(colr.AsSpan(66), 500);
        BinaryPrimitives.WriteInt16BigEndian(colr.AsSpan(68), 0);
        BinaryPrimitives.WriteInt16BigEndian(colr.AsSpan(70), 500);
        BinaryPrimitives.WriteInt16BigEndian(colr.AsSpan(72), 500);
        return colr;
    }

    private static byte[] SyntheticColrV1VarLinearGradient(ushort glyph)
    {
        var colr = SyntheticColrV1LinearGradient(glyph);
        colr[56] = 5; // PaintVarLinearGradient; zero variation indices use base geometry.
        return colr;
    }

    private static byte[] SyntheticColrV1VarRadialGradient(ushort glyph)
    {
        var colr = SyntheticColrV1RadialGradient(glyph);
        colr[56] = 7; // PaintVarRadialGradient; zero variation indices use base geometry.
        return colr;
    }

    private static byte[] SyntheticColrV1VarSweepGradient(ushort glyph)
    {
        var colr = SyntheticColrV1SweepGradient(glyph);
        colr[56] = 9; // PaintVarSweepGradient; zero variation indices use base geometry.
        return colr;
    }

    private static byte[] SyntheticColrV1SweepGradient(ushort glyph)
    {
        var colr = SyntheticColrV1LinearGradient(glyph);
        colr[56] = 8; // PaintSweepGradient
        BinaryPrimitives.WriteInt16BigEndian(colr.AsSpan(60), 250);
        BinaryPrimitives.WriteInt16BigEndian(colr.AsSpan(62), 250);
        BinaryPrimitives.WriteInt16BigEndian(colr.AsSpan(64), unchecked((short)0xC000));
        BinaryPrimitives.WriteInt16BigEndian(colr.AsSpan(66), 0x4000);
        return colr;
    }

    private static byte[] SyntheticColrV1Composite(ushort glyph)
    {
        var colr = new byte[84];
        BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(0), 1);
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(14), 34);
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(34), 1);
        BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(38), glyph);
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(40), 10);
        colr[44] = 32; colr[45] = 0; colr[46] = 0; colr[47] = 28; // source
        colr[48] = 13; // screen
        colr[49] = 0; colr[50] = 0; colr[51] = 8; // backdrop
        colr[52] = 10; colr[53] = 0; colr[54] = 0; colr[55] = 6;
        colr[58] = 2; BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(59), 0); BinaryPrimitives.WriteInt16BigEndian(colr.AsSpan(61), 0x4000);
        BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(56), glyph);
        colr[72] = 10; colr[73] = 0; colr[74] = 0; colr[75] = 6;
        BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(76), glyph);
        colr[78] = 2; BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(79), 1); BinaryPrimitives.WriteInt16BigEndian(colr.AsSpan(81), 0x4000);
        return colr;
    }

    private static byte[] SyntheticCpal() => [0, 0, 0, 1, 0, 1, 0, 1, 0, 0, 0, 12, 0, 0, 255, 255];

    private static byte[] SyntheticCpalTwo() => [0, 0, 0, 2, 0, 1, 0, 2, 0, 0, 0, 12, 0, 0, 255, 255, 255, 0, 0, 255];

    private static byte[] SyntheticColrV1Layers(ushort lowerGlyph, ushort upperGlyph)
    {
        var colr = new byte[86];
        BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(0), 1);
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(14), 34);
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(18), 50);
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(34), 1);
        BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(38), lowerGlyph);
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(40), 10);
        colr[44] = 1; colr[45] = 2; // PaintColrLayers
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(46), 0);
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(50), 2);
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(54), 12);
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(58), 24);
        colr[62] = 10; colr[65] = 6; BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(66), lowerGlyph);
        colr[68] = 2; BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(69), 0); BinaryPrimitives.WriteInt16BigEndian(colr.AsSpan(71), 0x4000);
        colr[74] = 10; colr[77] = 6; BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(78), upperGlyph);
        colr[80] = 2; BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(81), 1); BinaryPrimitives.WriteInt16BigEndian(colr.AsSpan(83), 0x4000);
        return colr;
    }

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
    public void DrawsSyntheticColrV1PaintGlyph()
    {
        var source = File.ReadAllBytes(Path.Combine(RepoPaths.Tests, "fonts", "SourceSans3-Regular.ttf"));
        var original = FontFace.Parse(source)!;
        var glyph = original.GlyphFor('A');
        var face = FontFace.Parse(AddTables(source,
            ("CPAL", SyntheticCpal()), ("COLR", SyntheticColrV1(glyph))))!;
        using var surface = SoftwareRasterBackend.Instance.CreateSurface(96, 96)!;
        White(surface.Canvas);
        surface.Canvas.DrawGlyphs(face, 64, [glyph], [new Vector2(12, 76)], Solid(1, 1, 1));
        var pixels = surface.ReadPixels(0, 0, 96, 96);
        Assert.True(pixels.Chunk(4).Any(pixel => pixel[0] > 180 && pixel[1] < 80 && pixel[2] < 80 && pixel[3] > 0),
            "The synthetic COLR v1 PaintGlyph did not render its CPAL color.");
    }

    [Fact]
    public void DrawsSyntheticColrV1VariableSolidAtDefaultInstance()
    {
        var source = File.ReadAllBytes(Path.Combine(RepoPaths.Tests, "fonts", "SourceSans3-Regular.ttf"));
        var original = FontFace.Parse(source)!;
        var glyph = original.GlyphFor('A');
        var face = FontFace.Parse(AddTables(source,
            ("CPAL", SyntheticCpal()), ("COLR", SyntheticColrV1VarSolid(glyph))))!;
        using var surface = SoftwareRasterBackend.Instance.CreateSurface(96, 96)!;
        White(surface.Canvas);
        surface.Canvas.DrawGlyphs(face, 64, [glyph], [new Vector2(12, 76)], Solid(1, 1, 1));
        var pixels = surface.ReadPixels(0, 0, 96, 96);
        Assert.True(pixels.Chunk(4).Any(pixel => pixel[0] > 180 && pixel[1] < 80 && pixel[2] < 80 && pixel[3] > 0),
            "The synthetic COLR v1 PaintVarSolid did not render its default-instance CPAL color.");
    }

    [Fact]
    public void DrawsSyntheticColrV1TranslatedPaint()
    {
        var source = File.ReadAllBytes(Path.Combine(RepoPaths.Tests, "fonts", "SourceSans3-Regular.ttf"));
        var original = FontFace.Parse(source)!;
        var glyph = original.GlyphFor('A');
        var face = FontFace.Parse(AddTables(source,
            ("CPAL", SyntheticCpal()), ("COLR", SyntheticColrV1Translated(glyph))))!;
        using var surface = SoftwareRasterBackend.Instance.CreateSurface(96, 96)!;
        White(surface.Canvas);
        surface.Canvas.DrawGlyphs(face, 64, [glyph], [new Vector2(12, 76)], Solid(1, 1, 1));
        var pixels = surface.ReadPixels(0, 0, 96, 96);
        var redXs = pixels.Chunk(4).Select((pixel, index) => pixel[0] > 180 && pixel[1] < 80 && pixel[2] < 80 && pixel[3] > 0 ? index % 96 : -1).Where(x => x >= 0).ToArray();
        Assert.NotEmpty(redXs);
        Assert.True(redXs.Min() > 28, $"The COLR v1 PaintTranslate wrapper did not move the glyph (min x={redXs.Min()}).");
    }

    [Fact]
    public void DrawsSyntheticColrV1VariableTranslateAtDefaultInstance()
    {
        var source = File.ReadAllBytes(Path.Combine(RepoPaths.Tests, "fonts", "SourceSans3-Regular.ttf"));
        var original = FontFace.Parse(source)!;
        var glyph = original.GlyphFor('A');
        var face = FontFace.Parse(AddTables(source,
            ("CPAL", SyntheticCpal()), ("COLR", SyntheticColrV1VarTranslated(glyph))))!;
        var parsed = ColorGlyphs.Get(face, glyph);
        Assert.NotNull(parsed);
        Assert.True(Assert.Single(parsed!).TransformOrIdentity.Translation.X > 19);
    }

    [Fact]
    public void DrawsSyntheticColrV1TransformPaint()
    {
        var source = File.ReadAllBytes(Path.Combine(RepoPaths.Tests, "fonts", "SourceSans3-Regular.ttf"));
        var original = FontFace.Parse(source)!;
        var glyph = original.GlyphFor('A');
        var face = FontFace.Parse(AddTables(source,
            ("CPAL", SyntheticCpal()), ("COLR", SyntheticColrV1Transform(glyph))))!;
        using var surface = SoftwareRasterBackend.Instance.CreateSurface(96, 96)!;
        White(surface.Canvas);
        surface.Canvas.DrawGlyphs(face, 64, [glyph], [new Vector2(12, 76)], Solid(1, 1, 1));
        var pixels = surface.ReadPixels(0, 0, 96, 96);
        var redXs = pixels.Chunk(4).Select((pixel, index) => pixel[0] > 180 && pixel[1] < 80 && pixel[2] < 80 && pixel[3] > 0 ? index % 96 : -1).Where(x => x >= 0).ToArray();
        Assert.NotEmpty(redXs);
        Assert.True(redXs.Min() > 28, $"The COLR v1 PaintTransform wrapper did not move the glyph (min x={redXs.Min()}).");
    }

    [Fact]
    public void DrawsSyntheticColrV1ScalePaint()
    {
        var source = File.ReadAllBytes(Path.Combine(RepoPaths.Tests, "fonts", "SourceSans3-Regular.ttf"));
        var original = FontFace.Parse(source)!;
        var glyph = original.GlyphFor('A');
        var face = FontFace.Parse(AddTables(source,
            ("CPAL", SyntheticCpal()), ("COLR", SyntheticColrV1Scale(glyph))))!;
        using var surface = SoftwareRasterBackend.Instance.CreateSurface(96, 96)!;
        White(surface.Canvas);
        surface.Canvas.DrawGlyphs(face, 64, [glyph], [new Vector2(12, 76)], Solid(1, 1, 1));
        var pixels = surface.ReadPixels(0, 0, 96, 96);
        var red = pixels.Chunk(4).Select((pixel, index) => pixel[0] > 180 && pixel[1] < 80 && pixel[2] < 80 && pixel[3] > 0 ? index % 96 : -1).Where(x => x >= 0).ToArray();
        Assert.NotEmpty(red);
        Assert.True(red.Max() - red.Min() > 20, "The COLR v1 PaintScale wrapper did not preserve a scaled glyph width.");
    }

    [Fact]
    public void DrawsSyntheticColrV1RotatePaint()
    {
        var source = File.ReadAllBytes(Path.Combine(RepoPaths.Tests, "fonts", "SourceSans3-Regular.ttf"));
        var original = FontFace.Parse(source)!;
        var glyph = original.GlyphFor('A');
        var face = FontFace.Parse(AddTables(source,
            ("CPAL", SyntheticCpal()), ("COLR", SyntheticColrV1Rotate(glyph))))!;
        var parsed = ColorGlyphs.Get(face, glyph);
        Assert.NotNull(parsed);
        Assert.NotEqual(Matrix3x2.Identity, Assert.Single(parsed!).TransformOrIdentity);
        using var surface = SoftwareRasterBackend.Instance.CreateSurface(96, 96)!;
        White(surface.Canvas);
        surface.Canvas.DrawGlyphs(face, 64, [glyph], [new Vector2(12, 76)], Solid(1, 1, 1));
        Assert.Contains(surface.ReadPixels(0, 0, 96, 96).Chunk(4), pixel => pixel[0] > 180 && pixel[1] < 80 && pixel[2] < 80 && pixel[3] > 0);
    }

    [Fact]
    public void DrawsSyntheticColrV1SkewPaint()
    {
        var source = File.ReadAllBytes(Path.Combine(RepoPaths.Tests, "fonts", "SourceSans3-Regular.ttf"));
        var original = FontFace.Parse(source)!;
        var glyph = original.GlyphFor('A');
        var face = FontFace.Parse(AddTables(source,
            ("CPAL", SyntheticCpal()), ("COLR", SyntheticColrV1Skew(glyph))))!;
        var parsed = ColorGlyphs.Get(face, glyph);
        Assert.NotNull(parsed);
        Assert.NotEqual(Matrix3x2.Identity, Assert.Single(parsed!).TransformOrIdentity);
        using var surface = SoftwareRasterBackend.Instance.CreateSurface(96, 96)!;
        White(surface.Canvas);
        surface.Canvas.DrawGlyphs(face, 64, [glyph], [new Vector2(12, 76)], Solid(1, 1, 1));
        Assert.Contains(surface.ReadPixels(0, 0, 96, 96).Chunk(4), pixel => pixel[0] > 180 && pixel[1] < 80 && pixel[2] < 80 && pixel[3] > 0);
    }

    [Fact]
    public void DrawsSyntheticColrV1LinearGradientPaint()
    {
        var source = File.ReadAllBytes(Path.Combine(RepoPaths.Tests, "fonts", "SourceSans3-Regular.ttf"));
        var original = FontFace.Parse(source)!;
        var glyph = original.GlyphFor('A');
        var face = FontFace.Parse(AddTables(source,
            ("CPAL", SyntheticCpalTwo()), ("COLR", SyntheticColrV1LinearGradient(glyph))))!;
        var parsed = ColorGlyphs.Get(face, glyph);
        Assert.NotNull(parsed);
        var layer = Assert.Single(parsed!);
        Assert.Equal(glyph, layer.Glyph);
        var gradient = Assert.IsType<Gradient>(layer.Gradient);
        Assert.Equal(2, gradient.Stops.Count);
        Assert.True(gradient.Stops[0].Color.R > .9f);
        Assert.True(gradient.Stops[1].Color.B > .9f);
        using var surface = SoftwareRasterBackend.Instance.CreateSurface(96, 96)!;
        White(surface.Canvas);
        surface.Canvas.DrawGlyphs(face, 64, [glyph], [new Vector2(12, 76)], Solid(1, 1, 1));
        var pixels = surface.ReadPixels(0, 0, 96, 96);
        Assert.Contains(pixels.Chunk(4), pixel => pixel[0] > 150 && pixel[2] < 120 && pixel[3] > 0);
        Assert.Contains(pixels.Chunk(4), pixel => pixel[2] > 150 && pixel[0] < 120 && pixel[3] > 0);
    }

    [Fact]
    public void DrawsSyntheticColrV1VariableLinearGradientAtDefaultInstance()
    {
        var source = File.ReadAllBytes(Path.Combine(RepoPaths.Tests, "fonts", "SourceSans3-Regular.ttf"));
        var original = FontFace.Parse(source)!;
        var glyph = original.GlyphFor('A');
        var face = FontFace.Parse(AddTables(source,
            ("CPAL", SyntheticCpalTwo()), ("COLR", SyntheticColrV1VarLinearGradient(glyph))))!;
        var parsed = ColorGlyphs.Get(face, glyph);
        Assert.NotNull(parsed);
        Assert.Equal(GradientKind.Linear, Assert.IsType<Gradient>(Assert.Single(parsed!).Gradient).Kind);
    }

    [Fact]
    public void DrawsSyntheticColrV1VariableRadialGradientAtDefaultInstance()
    {
        var source = File.ReadAllBytes(Path.Combine(RepoPaths.Tests, "fonts", "SourceSans3-Regular.ttf"));
        var original = FontFace.Parse(source)!;
        var glyph = original.GlyphFor('A');
        var face = FontFace.Parse(AddTables(source,
            ("CPAL", SyntheticCpalTwo()), ("COLR", SyntheticColrV1VarRadialGradient(glyph))))!;
        var parsed = ColorGlyphs.Get(face, glyph);
        Assert.Equal(GradientKind.Radial, Assert.IsType<Gradient>(Assert.Single(parsed!).Gradient).Kind);
    }

    [Fact]
    public void DrawsSyntheticColrV1VariableSweepGradientAtDefaultInstance()
    {
        var source = File.ReadAllBytes(Path.Combine(RepoPaths.Tests, "fonts", "SourceSans3-Regular.ttf"));
        var original = FontFace.Parse(source)!;
        var glyph = original.GlyphFor('A');
        var face = FontFace.Parse(AddTables(source,
            ("CPAL", SyntheticCpalTwo()), ("COLR", SyntheticColrV1VarSweepGradient(glyph))))!;
        var parsed = ColorGlyphs.Get(face, glyph);
        Assert.Equal(GradientKind.Conic, Assert.IsType<Gradient>(Assert.Single(parsed!).Gradient).Kind);
    }

    [Fact]
    public void DrawsSyntheticColrV1RadialGradientPaint()
    {
        var source = File.ReadAllBytes(Path.Combine(RepoPaths.Tests, "fonts", "SourceSans3-Regular.ttf"));
        var original = FontFace.Parse(source)!;
        var glyph = original.GlyphFor('A');
        var face = FontFace.Parse(AddTables(source,
            ("CPAL", SyntheticCpalTwo()), ("COLR", SyntheticColrV1RadialGradient(glyph))))!;
        var parsed = ColorGlyphs.Get(face, glyph);
        Assert.NotNull(parsed);
        Assert.Equal(GradientKind.Radial, Assert.IsType<Gradient>(Assert.Single(parsed!).Gradient).Kind);
        using var surface = SoftwareRasterBackend.Instance.CreateSurface(96, 96)!;
        White(surface.Canvas);
        surface.Canvas.DrawGlyphs(face, 64, [glyph], [new Vector2(12, 76)], Solid(1, 1, 1));
        var pixels = surface.ReadPixels(0, 0, 96, 96);
        Assert.Contains(pixels.Chunk(4), pixel => pixel[0] > 150 && pixel[2] < 120 && pixel[3] > 0);
        Assert.Contains(pixels.Chunk(4), pixel => pixel[2] > 150 && pixel[0] < 120 && pixel[3] > 0);
    }

    [Fact]
    public void DrawsSyntheticColrV1SweepGradientPaint()
    {
        var source = File.ReadAllBytes(Path.Combine(RepoPaths.Tests, "fonts", "SourceSans3-Regular.ttf"));
        var original = FontFace.Parse(source)!;
        var glyph = original.GlyphFor('A');
        var face = FontFace.Parse(AddTables(source,
            ("CPAL", SyntheticCpalTwo()), ("COLR", SyntheticColrV1SweepGradient(glyph))))!;
        var parsed = ColorGlyphs.Get(face, glyph);
        Assert.NotNull(parsed);
        var gradient = Assert.IsType<Gradient>(Assert.Single(parsed!).Gradient);
        Assert.Equal(GradientKind.Conic, gradient.Kind);
        Assert.Equal(90, gradient.StartAngle);
        Assert.Equal(450, gradient.EndAngle);
        using var surface = SoftwareRasterBackend.Instance.CreateSurface(96, 96)!;
        White(surface.Canvas);
        surface.Canvas.DrawGlyphs(face, 64, [glyph], [new Vector2(12, 76)], Solid(1, 1, 1));
        var pixels = surface.ReadPixels(0, 0, 96, 96);
        Assert.Contains(pixels.Chunk(4), pixel => pixel[0] > 150 && pixel[2] < 120 && pixel[3] > 0);
        Assert.Contains(pixels.Chunk(4), pixel => pixel[2] > 150 && pixel[0] < 120 && pixel[3] > 0);
    }

    [Fact]
    public void DrawsSyntheticColrV1CompositePaint()
    {
        var source = File.ReadAllBytes(Path.Combine(RepoPaths.Tests, "fonts", "SourceSans3-Regular.ttf"));
        var original = FontFace.Parse(source)!;
        var glyph = original.GlyphFor('A');
        var face = FontFace.Parse(AddTables(source,
            ("CPAL", SyntheticCpalTwo()), ("COLR", SyntheticColrV1Composite(glyph))))!;
        var parsed = ColorGlyphs.Get(face, glyph);
        Assert.NotNull(parsed);
        Assert.Contains(parsed!, layer => layer.Blend == BlendMode.Screen);
        using var surface = SoftwareRasterBackend.Instance.CreateSurface(96, 96)!;
        White(surface.Canvas);
        surface.Canvas.DrawGlyphs(face, 64, [glyph], [new Vector2(12, 76)], Solid(1, 1, 1));
        var pixels = surface.ReadPixels(0, 0, 96, 96);
        Assert.Contains(pixels.Chunk(4), pixel => pixel[0] > 100 && pixel[2] > 100 && pixel[1] < 100 && pixel[3] > 0);
    }

    [Fact]
    public void DrawsSyntheticColrV1LayerList()
    {
        var source = File.ReadAllBytes(Path.Combine(RepoPaths.Tests, "fonts", "SourceSans3-Regular.ttf"));
        var original = FontFace.Parse(source)!;
        var glyph = original.GlyphFor('A');
        var upperGlyph = original.GlyphFor('B');
        var face = FontFace.Parse(AddTables(source,
            ("CPAL", SyntheticCpalTwo()), ("COLR", SyntheticColrV1Layers(glyph, upperGlyph))))!;
        using var surface = SoftwareRasterBackend.Instance.CreateSurface(96, 96)!;
        White(surface.Canvas);
        surface.Canvas.DrawGlyphs(face, 64, [glyph], [new Vector2(12, 76)], Solid(1, 1, 1));
        var pixels = surface.ReadPixels(0, 0, 96, 96);
        Assert.True(pixels.Chunk(4).Any(pixel => pixel[0] > 180 && pixel[1] < 80 && pixel[2] < 80 && pixel[3] > 0),
            "The lower synthetic COLR v1 layer did not render.");
        Assert.True(pixels.Chunk(4).Any(pixel => pixel[2] > 180 && pixel[0] < 80 && pixel[1] < 80 && pixel[3] > 0),
            "The upper synthetic COLR v1 layer did not render.");
    }

    [Fact]
    public void DrawsTextFromCff2GlyphOutlines()
    {
        var face = Load("FolioCff2.otf");
        var glyph = face.GlyphFor('A');
        Assert.NotEqual((ushort)0, glyph);
        var comparison = Compare(c => c.DrawGlyphs(face, 64, [glyph], [new Vector2(12, 76)], Solid(0.1f, 0.1f, 0.6f)),
            width: 96, height: 96, background: White);
        Assert.True(comparison.Mean < 8 && comparison.OverForty < 0.12,
            $"CFF2 raster diverged from the Skia oracle: mean={comparison.Mean:F2}, over40={comparison.OverForty:P2}, max={comparison.Max}.");
        using var surface = SoftwareRasterBackend.Instance.CreateSurface(96, 96)!;
        White(surface.Canvas);
        surface.Canvas.DrawGlyphs(face, 64, [glyph], [new Vector2(12, 76)], Solid(0.1f, 0.1f, 0.6f));
        var pixels = surface.ReadPixels(0, 0, 96, 96);
        Assert.True(pixels.Chunk(4).Count(p => p[3] > 0 && p[0] < 250) > 500,
            "The CFF2 glyph did not produce a raster outline.");
    }

    [Fact]
    public void DrawsTextFromCffType2GlyphOutlines()
    {
        var face = Load("NotoSansJP-Regular.otf");
        var glyph = face.GlyphFor('A');
        Assert.NotEqual((ushort)0, glyph);
        var comparison = Compare(c => c.DrawGlyphs(face, 64, [glyph], [new Vector2(12, 76)], Solid(0.1f, 0.1f, 0.6f)),
            width: 96, height: 96, background: White);
        Assert.True(comparison.Mean < 8 && comparison.OverForty < 0.12,
            $"CFF raster diverged from the Skia oracle: mean={comparison.Mean:F2}, over40={comparison.OverForty:P2}, max={comparison.Max}.");
        using var surface = SoftwareRasterBackend.Instance.CreateSurface(96, 96)!;
        White(surface.Canvas);
        surface.Canvas.DrawGlyphs(face, 64, [glyph], [new Vector2(12, 76)], Solid(0.1f, 0.1f, 0.6f));
        var pixels = surface.ReadPixels(0, 0, 96, 96);
        Assert.True(pixels.Chunk(4).Count(p => p[3] > 0 && p[0] < 250) > 500,
            "CFF glyph did not produce a raster outline.");
    }

    [Fact]
    public void DecodesAndDrawsCff2FromWoff2()
    {
        var compressed = File.ReadAllBytes(Path.Combine(RepoPaths.Tests, "fonts", "FolioCff2.woff2"));
        var face = FontFace.Parse(WebFontDecoder.Decode(compressed));
        Assert.NotNull(face);
        var glyph = face!.GlyphFor('A');
        var comparison = Compare(c => c.DrawGlyphs(face, 64, [glyph], [new Vector2(12, 76)], Solid(0.1f, 0.1f, 0.6f)),
            width: 96, height: 96, background: White);
        Assert.True(comparison.Mean < 8 && comparison.OverForty < 0.12,
            $"WOFF2 CFF2 raster diverged from the Skia oracle: mean={comparison.Mean:F2}, over40={comparison.OverForty:P2}, max={comparison.Max}.");
        using var surface = SoftwareRasterBackend.Instance.CreateSurface(96, 96)!;
        White(surface.Canvas);
        surface.Canvas.DrawGlyphs(face, 64, [glyph], [new Vector2(12, 76)], Solid(0.1f, 0.1f, 0.6f));
        var pixels = surface.ReadPixels(0, 0, 96, 96);
        Assert.True(pixels.Chunk(4).Count(p => p[3] > 0 && p[0] < 250) > 500,
            "The WOFF2 CFF2 glyph did not produce a raster outline.");
    }

    [Fact]
    public void RejectsMalformedCff2OutlineWithinRasterLimits()
    {
        var bytes = File.ReadAllBytes(Path.Combine(RepoPaths.Tests, "fonts", "FolioCff2.otf"));
        var tableCount = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(4));
        var cff2Offset = -1;
        for (var i = 0; i < tableCount; i++)
        {
            var record = 12 + i * 16;
            if (bytes.AsSpan(record, 4).SequenceEqual("CFF2"u8))
            {
                cff2Offset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(record + 8)));
                break;
            }
        }
        Assert.True(cff2Offset >= 0);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(cff2Offset + 2), ushort.MaxValue);

        var face = FontFace.Parse(bytes);
        Assert.NotNull(face);
        using var surface = SoftwareRasterBackend.Instance.CreateSurface(96, 96)!;
        White(surface.Canvas);
        var glyph = face!.GlyphFor('A');
        surface.Canvas.DrawGlyphs(face, 64, [glyph], [new Vector2(12, 76)], Solid(0.1f, 0.1f, 0.6f));
        var pixels = surface.ReadPixels(0, 0, 96, 96);
        Assert.All(pixels.Chunk(4), pixel => Assert.True(pixel[0] == 255 && pixel[1] == 255 && pixel[2] == 255,
            "Malformed CFF2 data escaped the outline limit and painted pixels."));
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

    [Fact]
    public void RendersRepresentativeNotoBitmapColourGlyphCorpus()
    {
        var face = Load("NotoColorEmoji.ttf");
        var codePoints = new[] { 0x1F603, 0x1F44D, 0x1F389, 0x1F680, 0x1F431, 0x1F31F, 0x2764 };
        var glyphs = codePoints.Select(face.GlyphFor).ToArray();
        Assert.All(glyphs, glyph => Assert.NotEqual((ushort)0, glyph));
        using var surface = SoftwareRasterBackend.Instance.CreateSurface(7 * 64, 80)!;
        White(surface.Canvas);
        var origins = glyphs.Select((_, i) => new Vector2(8 + 64 * i, 60)).ToArray();
        surface.Canvas.DrawGlyphs(face, 48, glyphs, origins, Solid(1, 1, 1));
        var pixels = surface.ReadPixels(0, 0, 7 * 64, 80);
        for (var i = 0; i < glyphs.Length; i++)
        {
            var region = pixels
                .Chunk(4)
                .Skip(64 * 80 * 0)
                .Where((_, index) => index % (7 * 64) >= i * 64 && index % (7 * 64) < (i + 1) * 64);
            Assert.Contains(region, pixel => pixel[0] < 250 || pixel[1] < 250 || pixel[2] < 250);
        }
    }

    [Fact]
    public void DrawsSbixPngColourGlyph()
    {
        var source = File.ReadAllBytes(Path.Combine(RepoPaths.Tests, "fonts", "SourceSans3-Regular.ttf"));
        var original = FontFace.Parse(source)!;
        var glyph = original.GlyphFor('A');
        var png = File.ReadAllBytes(Path.Combine(RepoPaths.Tests, "images", "gray-1.png"));
        var face = FontFace.Parse(AddTables(source, ("sbix", SyntheticSbix(original.GlyphCount, glyph, png))))!;
        using var surface = SoftwareRasterBackend.Instance.CreateSurface(32, 32)!;
        White(surface.Canvas);
        surface.Canvas.DrawGlyphs(face, 16, [glyph], [new Vector2(8, 20)], Solid(1, 1, 1));
        var pixels = surface.ReadPixels(0, 0, 32, 32);
        Assert.Contains(pixels.Chunk(4), pixel => pixel[0] < 255 || pixel[1] < 255 || pixel[2] < 255);
    }

    private static void White(ICanvas c) => c.FillRoundedRect(new RoundedRect(new RectF(0, 0, 1000, 1000), default), Solid(1, 1, 1));
}
