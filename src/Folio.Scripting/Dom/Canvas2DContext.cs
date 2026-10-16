using System.Numerics;
using Folio.Imaging;
using Folio.Painting;

namespace Folio.Scripting.Dom;

/// <summary>
/// The drawing state of a 2D context (HTML §4.12.5.1): everything <c>save()</c> keeps and <c>restore()</c> brings back
/// except the clip, which lives in the surface's canvas.
/// </summary>
internal sealed class CanvasDrawingState
{
    public Matrix3x2 Transform = Matrix3x2.Identity;
    public PaintSource FillStyle = PaintSource.Black;
    public PaintSource StrokeStyle = PaintSource.Black;
    public double LineWidth = 1;
    public string LineCap = "butt";
    public string LineJoin = "miter";
    public double MiterLimit = 10;
    public double[] LineDash = [];
    public double LineDashOffset;
    public double GlobalAlpha = 1;
    public string GlobalCompositeOperation = "source-over";
    public Rgba ShadowColor = new(0, 0, 0, 0);
    public double ShadowBlur;
    public double ShadowOffsetX;
    public double ShadowOffsetY;
    public string Filter = "none";
    public Filter[]? FilterPrimitives;
    public bool ImageSmoothingEnabled = true;
    public string ImageSmoothingQuality = "low";
    public string Font = "10px sans-serif";
    public string TextAlign = "start";
    public string TextBaseline = "alphabetic";
    public string Direction = "inherit";
    public string FontKerning = "auto";
    public string FontStretch = "normal";
    public string FontVariantCaps = "normal";
    public string TextRendering = "auto";
    public string LetterSpacing = "0px";
    public string WordSpacing = "0px";

    public CanvasDrawingState Clone() => (CanvasDrawingState)MemberwiseClone();
}

/// <summary>
/// A <c>CanvasRenderingContext2D</c> drawing into a raster surface. Drawing is immediate: every call changes the
/// bitmap, so reads see it. The surface's canvas stays at the identity transform between calls; each draw maps its
/// geometry by the current transform inside a save/restore pair, and clips are set in canvas coordinates.
/// </summary>
internal sealed partial class Canvas2DContext
{
    // save() calls past this many are not kept, so a script cannot grow the stack without bound; their restore() calls are ignored too.
    private const int MaxSavedStates = 10_000;

    private readonly Stack<CanvasDrawingState> _saved = [];
    private int _unsaved;
    private readonly Hosting.CanvasStyleEngine? _styles;

    public Canvas2DContext(IRasterSurface surface, bool alpha, Func<Rgba> currentColor, Hosting.CanvasStyleEngine? styles)
    {
        Opaque = !alpha;
        CurrentColor = currentColor;
        _styles = styles;
        Surface = surface;
        Reattach(surface);
    }

    public IRasterSurface Surface { get; private set; }

    /// <summary>Whether the context was made with <c>alpha: false</c>: the bitmap starts opaque black.</summary>
    public bool Opaque { get; }

    /// <summary>Draws on a new bitmap with the state of a new context: nothing saved, no path, no clip, defaults.</summary>
    public void Reattach(IRasterSurface surface)
    {
        Surface = surface;
        _saved.Clear();
        _unsaved = 0;
        State = new CanvasDrawingState();
        Path.Clear();
        // A new bitmap is transparent, or opaque black without an alpha channel.
        if (Opaque)
            surface.Canvas.FillPath(new PathData().AddRoundedRect(new RoundedRect(new RectF(0, 0, Width, Height), default)), FillRule.NonZero, new Paint(new Rgba(0, 0, 0, 1), Blend: BlendMode.Copy));
        Version++;
    }

    public Func<Rgba> CurrentColor { get; }

    public CanvasDrawingState State { get; private set; } = new();

    /// <summary>The path being built, in canvas coordinates.</summary>
    public CanvasPath Path { get; } = new();

    /// <summary>Counts every change to the bitmap, so a painter knows when to take a new snapshot.</summary>
    public long Version { get; private set; }

    private ICanvas Canvas => Surface.Canvas;

    public int Width => Surface.Width;

    public int Height => Surface.Height;

    // ---------------------------------------------------------------- state

    public void Save()
    {
        if (_saved.Count >= MaxSavedStates)
        {
            _unsaved++;
            return;
        }
        _saved.Push(State.Clone());
        Canvas.Save();
    }

    public void Restore()
    {
        if (_unsaved > 0)
        {
            _unsaved--;
            return;
        }
        if (!_saved.TryPop(out var previous))
            return;
        State = previous;
        Canvas.Restore();
    }

    // ---------------------------------------------------------------- transforms

    private static bool Finite(params double[] values) => values.All(double.IsFinite);

    public void Scale(double x, double y)
    {
        if (Finite(x, y))
            State.Transform = Matrix3x2.CreateScale((float)x, (float)y) * State.Transform;
    }

    public void Rotate(double angle)
    {
        if (Finite(angle))
            State.Transform = Matrix3x2.CreateRotation((float)angle) * State.Transform;
    }

    public void Translate(double x, double y)
    {
        if (Finite(x, y))
            State.Transform = Matrix3x2.CreateTranslation((float)x, (float)y) * State.Transform;
    }

    public void Transform(double a, double b, double c, double d, double e, double f)
    {
        if (Finite(a, b, c, d, e, f))
            State.Transform = new Matrix3x2((float)a, (float)b, (float)c, (float)d, (float)e, (float)f) * State.Transform;
    }

    public void SetTransform(double a, double b, double c, double d, double e, double f)
    {
        if (Finite(a, b, c, d, e, f))
            State.Transform = new Matrix3x2((float)a, (float)b, (float)c, (float)d, (float)e, (float)f);
    }

    public void ResetTransform() => State.Transform = Matrix3x2.Identity;

    // ---------------------------------------------------------------- compositing

    /// <summary>The blend mode of a <c>globalCompositeOperation</c> value; null if it is not one.</summary>
    public static BlendMode? BlendOf(string operation) => operation switch
    {
        "source-over" => BlendMode.Normal,
        "source-in" => BlendMode.SourceIn,
        "source-out" => BlendMode.SourceOut,
        "source-atop" => BlendMode.SourceAtop,
        "destination-over" => BlendMode.DestinationOver,
        "destination-in" => BlendMode.DestinationIn,
        "destination-out" => BlendMode.DestinationOut,
        "destination-atop" => BlendMode.DestinationAtop,
        "lighter" => BlendMode.PlusLighter,
        "copy" => BlendMode.Copy,
        "xor" => BlendMode.Xor,
        "multiply" => BlendMode.Multiply,
        "screen" => BlendMode.Screen,
        "overlay" => BlendMode.Overlay,
        "darken" => BlendMode.Darken,
        "lighten" => BlendMode.Lighten,
        "color-dodge" => BlendMode.ColorDodge,
        "color-burn" => BlendMode.ColorBurn,
        "hard-light" => BlendMode.HardLight,
        "soft-light" => BlendMode.SoftLight,
        "difference" => BlendMode.Difference,
        "exclusion" => BlendMode.Exclusion,
        "hue" => BlendMode.Hue,
        "saturation" => BlendMode.Saturation,
        "color" => BlendMode.Color,
        "luminosity" => BlendMode.Luminosity,
        _ => null,
    };

    // These change the bitmap where the shape is not too (outside it, the source is transparent), so they need a layer
    // that covers the whole canvas.
    private static bool ReachesOutsideShape(BlendMode mode) =>
        mode is BlendMode.Copy or BlendMode.SourceIn or BlendMode.SourceOut or BlendMode.DestinationIn or BlendMode.DestinationAtop;

    private bool ShadowActive =>
        State.ShadowColor.A > 0 && (State.ShadowBlur > 0 || State.ShadowOffsetX != 0 || State.ShadowOffsetY != 0);

    /// <summary>
    /// Draws one shape the way every drawing method does (HTML §4.12.5.1.14 "shadows and compositing"): filtered, with
    /// its shadow, composited with <c>globalCompositeOperation</c>. <paramref name="draw"/> draws in user space with the
    /// paint it is handed.
    /// </summary>
    private void Render(PaintSource source, Action<ICanvas, Paint> draw, bool shadows = true)
    {
        var transform = State.Transform;
        if (!Matrix3x2.Invert(transform, out _) || !float.IsFinite(transform.M11 + transform.M12 + transform.M21 + transform.M22 + transform.M31 + transform.M32))
            return;
        var mode = BlendOf(State.GlobalCompositeOperation) ?? BlendMode.Normal;
        var paint = source.ToPaint((float)State.GlobalAlpha, transform);
        var shadow = shadows && ShadowActive;
        var layers = 0;
        var canvas = Canvas;
        // Outermost: the composite operation, applied to everything the shape and its shadow draw.
        if (mode != BlendMode.Normal && (shadow || ReachesOutsideShape(mode) || State.FilterPrimitives is not null))
        {
            canvas.PushLayer(new LayerOptions(1, Blend: mode));
            layers++;
        }
        else
        {
            paint = paint with { Blend = mode };
        }
        if (shadow)
        {
            // The shadow takes its alpha from the shape drawn at globalAlpha, so the colour's own is all it adds.
            var color = State.ShadowColor;
            canvas.PushLayer(new LayerOptions(1, [new Filter(FilterKind.DropShadow, (float)State.ShadowBlur / 2, Offset: new Vector2((float)State.ShadowOffsetX, (float)State.ShadowOffsetY), Color: color)]));
            layers++;
        }
        if (State.FilterPrimitives is { } filters)
        {
            canvas.PushLayer(new LayerOptions(1, filters));
            layers++;
        }
        canvas.Save();
        canvas.Transform(transform);
        draw(canvas, paint);
        canvas.Restore();
        while (layers-- > 0)
            canvas.PopLayer();
        Version++;
    }

    // ---------------------------------------------------------------- rectangles

    public void FillRect(double x, double y, double w, double h)
    {
        if (!Finite(x, y, w, h) || w == 0 || h == 0)
            return;
        Render(State.FillStyle, (canvas, paint) => canvas.FillPath(RectPath(x, y, w, h), FillRule.NonZero, paint));
    }

    public void StrokeRect(double x, double y, double w, double h)
    {
        if (!Finite(x, y, w, h) || (w == 0 && h == 0))
            return;
        var stroke = CurrentStroke();
        Render(State.StrokeStyle, (canvas, paint) => canvas.StrokePath(RectPath(x, y, w, h), stroke, paint));
    }

    public void ClearRect(double x, double y, double w, double h)
    {
        if (!Finite(x, y, w, h) || w == 0 || h == 0 || !Matrix3x2.Invert(State.Transform, out _))
            return;
        var canvas = Canvas;
        canvas.Save();
        canvas.Transform(State.Transform);
        // Clearing ignores alpha, shadows and the composite operation: it replaces pixels with transparent ones.
        canvas.FillPath(RectPath(x, y, w, h), FillRule.NonZero, new Paint(default, Blend: BlendMode.Copy));
        canvas.Restore();
        Version++;
    }

    private static PathData RectPath(double x, double y, double w, double h) =>
        new PathData().MoveTo((float)x, (float)y).LineTo((float)(x + w), (float)y).LineTo((float)(x + w), (float)(y + h)).LineTo((float)x, (float)(y + h)).Close();

    // ---------------------------------------------------------------- paths

    public void BeginPath() => Path.Clear();

    public void MoveTo(double x, double y) => Path.MoveTo(State.Transform, x, y);

    public void LineTo(double x, double y) => Path.LineTo(State.Transform, x, y);

    public void ClosePath() => Path.ClosePath();

    public void QuadraticCurveTo(double cpx, double cpy, double x, double y) => Path.QuadraticCurveTo(State.Transform, cpx, cpy, x, y);

    public void BezierCurveTo(double c1x, double c1y, double c2x, double c2y, double x, double y) =>
        Path.BezierCurveTo(State.Transform, c1x, c1y, c2x, c2y, x, y);

    public bool ArcTo(double x1, double y1, double x2, double y2, double radius) => Path.ArcTo(State.Transform, x1, y1, x2, y2, radius);

    public void Rect(double x, double y, double w, double h) => Path.Rect(State.Transform, x, y, w, h);

    public bool Arc(double x, double y, double radius, double start, double end, bool anticlockwise) =>
        Path.Arc(State.Transform, x, y, radius, start, end, anticlockwise);

    public bool Ellipse(double x, double y, double rx, double ry, double rotation, double start, double end, bool anticlockwise) =>
        Path.Ellipse(State.Transform, x, y, rx, ry, rotation, start, end, anticlockwise);

    public void RoundRect(double x, double y, double w, double h, (double X, double Y)[] radii)
    {
        if (Finite(x, y, w, h))
            Path.RoundRect(State.Transform, x, y, w, h, radii);
    }

    // A path to draw under the current transform: a Path2D is already in user space; the context's own path is in
    // canvas coordinates and is mapped back by the inverse of the transform it is drawn under.
    private PathData UserSpace(CanvasPath? path) =>
        path is not null ? path.ToPathData(Matrix3x2.Identity)
            : Matrix3x2.Invert(State.Transform, out var inverse) ? Path.ToPathData(inverse) : new PathData();

    public void Fill(CanvasPath? path, FillRule rule)
    {
        var data = UserSpace(path);
        if (data.Commands.Count == 0)
            return;
        Render(State.FillStyle, (canvas, paint) => canvas.FillPath(data, rule, paint));
    }

    public void Stroke(CanvasPath? path)
    {
        var data = UserSpace(path);
        if (data.Commands.Count == 0)
            return;
        var stroke = CurrentStroke();
        Render(State.StrokeStyle, (canvas, paint) => canvas.StrokePath(data, stroke, paint));
    }

    public void Clip(CanvasPath? path, FillRule rule)
    {
        // Clips are set in canvas coordinates, at the identity transform.
        var data = path is not null ? path.ToPathData(State.Transform) : Path.ToPathData(Matrix3x2.Identity);
        Canvas.ClipPath(data, rule);
    }

    public bool IsPointInPath(CanvasPath? path, double x, double y, FillRule rule)
    {
        if (!Finite(x, y))
            return false;
        var target = path is null ? Path : path.Transformed(State.Transform);
        return target.Contains(x, y, rule);
    }

    public bool IsPointInStroke(CanvasPath? path, double x, double y)
    {
        if (!Finite(x, y) || !Matrix3x2.Invert(State.Transform, out var inverse))
            return false;
        // The pen is round in user space, so the test runs there.
        var point = Vector2.Transform(new Vector2((float)x, (float)y), inverse);
        var target = path ?? Path.Transformed(inverse);
        return target.NearStroke(point.X, point.Y, State.LineWidth);
    }

    private Stroke CurrentStroke()
    {
        var cap = State.LineCap switch { "round" => LineCap.Round, "square" => LineCap.Square, _ => LineCap.Butt };
        var join = State.LineJoin switch { "round" => LineJoin.Round, "bevel" => LineJoin.Bevel, _ => LineJoin.Miter };
        var dashes = State.LineDash.Length == 0 ? null : State.LineDash.Select(d => (float)d).ToArray();
        return new Stroke((float)State.LineWidth, cap, dashes, join, (float)State.MiterLimit, (float)State.LineDashOffset);
    }

    // ---------------------------------------------------------------- line style and other properties

    public void SetLineDash(IReadOnlyList<double> segments)
    {
        if (segments.Any(s => !double.IsFinite(s) || s < 0))
            return;
        // An odd list is repeated, so dashes and gaps alternate (HTML §4.12.5.1.2).
        State.LineDash = segments.Count % 2 == 1 ? [.. segments, .. segments] : [.. segments];
    }

    public void SetShadowColor(Rgba color) => State.ShadowColor = color;

    // ---------------------------------------------------------------- gradients and patterns

    public static CanvasGradient CreateLinearGradient(double x0, double y0, double x1, double y1) =>
        new() { Shape = CanvasGradient.GradientShape.Linear, X0 = x0, Y0 = y0, X1 = x1, Y1 = y1 };

    public static CanvasGradient CreateRadialGradient(double x0, double y0, double r0, double x1, double y1, double r1) =>
        new() { Shape = CanvasGradient.GradientShape.Radial, X0 = x0, Y0 = y0, R0 = r0, X1 = x1, Y1 = y1, R1 = r1 };

    public static CanvasGradient CreateConicGradient(double angle, double x, double y) =>
        new() { Shape = CanvasGradient.GradientShape.Conic, Angle = angle, X0 = x, Y0 = y };

    // ---------------------------------------------------------------- images and pixels

    /// <summary>drawImage (HTML §4.12.5.1.16): the part of the image inside the source rectangle into the destination rectangle.</summary>
    public void DrawImage(IImageHandle image, double sx, double sy, double sw, double sh, double dx, double dy, double dw, double dh)
    {
        if (!Finite(sx, sy, sw, sh, dx, dy, dw, dh) || sw == 0 || sh == 0 || dw == 0 || dh == 0 || image.Width == 0 || image.Height == 0)
            return;
        // Negative sizes flip the rectangle's origin.
        if (sw < 0) { sx += sw; sw = -sw; }
        if (sh < 0) { sy += sh; sh = -sh; }
        if (dw < 0) { dx += dw; dw = -dw; }
        if (dh < 0) { dy += dh; dh = -dh; }
        // The source rectangle is clipped to the image, and the destination shrinks to match.
        var (cx0, cy0) = (Math.Max(sx, 0), Math.Max(sy, 0));
        var (cx1, cy1) = (Math.Min(sx + sw, image.Width), Math.Min(sy + sh, image.Height));
        if (cx1 <= cx0 || cy1 <= cy0)
            return;
        var (scaleX, scaleY) = (dw / sw, dh / sh);
        var (ddx, ddy) = (dx + (cx0 - sx) * scaleX, dy + (cy0 - sy) * scaleY);
        var (ddw, ddh) = ((cx1 - cx0) * scaleX, (cy1 - cy0) * scaleY);
        var source = new RectF((float)cx0, (float)cy0, (float)(cx1 - cx0), (float)(cy1 - cy0));
        var destination = new RectF((float)ddx, (float)ddy, (float)ddw, (float)ddh);
        var sampling = State.ImageSmoothingEnabled ? ImageSampling.Smooth : ImageSampling.Pixelated;
        Render(PaintSource.Black, (canvas, paint) => canvas.DrawImage(image, source, destination, sampling, paint));
    }

    /// <summary>getImageData: the pixels of a rectangle as RGBA8 with straight alpha.</summary>
    public byte[] GetImageData(int x, int y, int width, int height) => Surface.ReadPixels(x, y, width, height);

    /// <summary>putImageData: the dirty part of the pixels replaces the bitmap's, ignoring transform, clip, alpha and the composite operation.</summary>
    public void PutImageData(byte[] pixels, int width, int height, int dx, int dy, int dirtyX, int dirtyY, int dirtyWidth, int dirtyHeight)
    {
        // The dirty rectangle, flipped if negative and cut to the image.
        if (dirtyWidth < 0) { dirtyX += dirtyWidth; dirtyWidth = -dirtyWidth; }
        if (dirtyHeight < 0) { dirtyY += dirtyHeight; dirtyHeight = -dirtyHeight; }
        var (x0, y0) = (Math.Max(dirtyX, 0), Math.Max(dirtyY, 0));
        var (x1, y1) = (Math.Min((long)dirtyX + dirtyWidth, width), Math.Min((long)dirtyY + dirtyHeight, height));
        if (x1 <= x0 || y1 <= y0)
            return;
        var (w, h) = ((int)(x1 - x0), (int)(y1 - y0));
        var part = new byte[w * h * 4];
        for (var row = 0; row < h; row++)
            Buffer.BlockCopy(pixels, (((int)y0 + row) * width + (int)x0) * 4, part, row * w * 4, w * 4);
        Surface.WritePixels(dx + (int)x0, dy + (int)y0, w, h, part);
        Version++;
    }
}
