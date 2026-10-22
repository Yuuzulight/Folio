using System.Numerics;
using Folio.Imaging;
using Folio.Typography;

namespace Folio.Painting;

/// <summary>
/// Folio's own in-house software raster backend behind <see cref="IRasterBackend"/>
/// (docs/dependencies.md, Towards all-Folio): scanline pixel buffer with clipping,
/// transforms, geometric fills, and alpha blending.
/// </summary>
public sealed class SoftwareRasterBackend : IRasterBackend
{
    public static readonly SoftwareRasterBackend Instance = new();

    public ITextShaper? TextShaper => ComplexShaper.Instance;

    public IRasterSurface? CreateSurface(int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > ImageLimits.MaxPixels)
            return null;
        return new SoftwareRasterSurface(width, height);
    }
}

public sealed class SoftwareRasterSurface : IRasterSurface
{
    private readonly int _width;
    private readonly int _height;
    private readonly byte[] _pixels; // RGBA8 straight alpha
    private readonly SoftwareCanvas _canvas;

    public SoftwareRasterSurface(int width, int height)
    {
        _width = width;
        _height = height;
        _pixels = new byte[width * height * 4];
        _canvas = new SoftwareCanvas(width, height, _pixels);
    }

    public int Width => _width;
    public int Height => _height;
    public ICanvas Canvas => _canvas;

    public byte[] ReadPixels(int x, int y, int width, int height)
    {
        var result = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
        {
            var srcY = y + row;
            if (srcY < 0 || srcY >= _height) continue;

            for (var col = 0; col < width; col++)
            {
                var srcX = x + col;
                if (srcX < 0 || srcX >= _width) continue;

                var srcIdx = (srcY * _width + srcX) * 4;
                var dstIdx = (row * width + col) * 4;

                result[dstIdx] = _pixels[srcIdx];
                result[dstIdx + 1] = _pixels[srcIdx + 1];
                result[dstIdx + 2] = _pixels[srcIdx + 2];
                result[dstIdx + 3] = _pixels[srcIdx + 3];
            }
        }
        return result;
    }

    public void WritePixels(int x, int y, int width, int height, ReadOnlySpan<byte> rgba)
    {
        for (var row = 0; row < height; row++)
        {
            var dstY = y + row;
            if (dstY < 0 || dstY >= _height) continue;

            for (var col = 0; col < width; col++)
            {
                var dstX = x + col;
                if (dstX < 0 || dstX >= _width) continue;

                var srcIdx = (row * width + col) * 4;
                if (srcIdx + 3 >= rgba.Length) break;
                var dstIdx = (dstY * _width + dstX) * 4;

                _pixels[dstIdx] = rgba[srcIdx];
                _pixels[dstIdx + 1] = rgba[srcIdx + 1];
                _pixels[dstIdx + 2] = rgba[srcIdx + 2];
                _pixels[dstIdx + 3] = rgba[srcIdx + 3];
            }
        }
    }

    public byte[]? Encode(string mimeType, double quality) => null;

    public IImageHandle Snapshot() => new DecodedImage(_width, _height, (byte[])_pixels.Clone());

    public void Dispose() { }
}

public sealed class SoftwareCanvas : ICanvas
{
    private readonly int _width;
    private readonly int _height;
    private readonly byte[] _pixels;

    private readonly Stack<(Matrix3x2 Matrix, RectF Clip)> _stateStack = new();
    private Matrix3x2 _currentMatrix = Matrix3x2.Identity;
    private RectF _currentClip;

    public SoftwareCanvas(int width, int height, byte[] pixels)
    {
        _width = width;
        _height = height;
        _pixels = pixels;
        _currentClip = new RectF(0, 0, width, height);
    }

    public void Save()
    {
        _stateStack.Push((_currentMatrix, _currentClip));
    }

    public void Restore()
    {
        if (_stateStack.Count > 0)
        {
            var (m, c) = _stateStack.Pop();
            _currentMatrix = m;
            _currentClip = c;
        }
    }

    public void Transform(in Matrix3x2 matrix)
    {
        _currentMatrix = matrix * _currentMatrix;
    }

    public void ClipRoundedRect(in RoundedRect rect)
    {
        _currentClip = Intersect(_currentClip, rect.Rect);
    }

    public void ClipPath(PathData path, FillRule rule)
    {
        var bounds = ComputePathBounds(path);
        _currentClip = Intersect(_currentClip, bounds);
    }

    public void ClearRect(in RectF rect)
    {
        var minX = Math.Max(0, (int)Math.Floor(rect.X));
        var maxX = Math.Min(_width, (int)Math.Ceiling(rect.Right));
        var minY = Math.Max(0, (int)Math.Floor(rect.Y));
        var maxY = Math.Min(_height, (int)Math.Ceiling(rect.Bottom));

        for (var y = minY; y < maxY; y++)
        {
            for (var x = minX; x < maxX; x++)
            {
                var idx = (y * _width + x) * 4;
                _pixels[idx] = 0;
                _pixels[idx + 1] = 0;
                _pixels[idx + 2] = 0;
                _pixels[idx + 3] = 0;
            }
        }
    }

    public void FillRoundedRect(in RoundedRect rect, in Paint paint)
    {
        if (rect.Radii.IsZero)
        {
            FillRect(rect.Rect, paint);
            return;
        }

        var minX = Math.Max(0, (int)Math.Max(_currentClip.X, Math.Floor(rect.Rect.X)));
        var maxX = Math.Min(_width, (int)Math.Min(_currentClip.Right, Math.Ceiling(rect.Rect.Right)));
        var minY = Math.Max(0, (int)Math.Max(_currentClip.Y, Math.Floor(rect.Rect.Y)));
        var maxY = Math.Min(_height, (int)Math.Min(_currentClip.Bottom, Math.Ceiling(rect.Rect.Bottom)));

        var tl = rect.Radii.TopLeft;
        var tr = rect.Radii.TopRight;
        var br = rect.Radii.BottomRight;
        var bl = rect.Radii.BottomLeft;

        var isGradient = paint.Gradient is not null;
        var (solidR, solidG, solidB, solidA) = isGradient ? default : EvaluatePaintColor(paint, 0, 0);

        for (var y = minY; y < maxY; y++)
        {
            for (var x = minX; x < maxX; x++)
            {
                if (tl.X > 0 && tl.Y > 0 && x < rect.Rect.X + tl.X && y < rect.Rect.Y + tl.Y)
                {
                    var nx = (x - (rect.Rect.X + tl.X)) / tl.X;
                    var ny = (y - (rect.Rect.Y + tl.Y)) / tl.Y;
                    if (nx * nx + ny * ny > 1f) continue;
                }
                else if (tr.X > 0 && tr.Y > 0 && x > rect.Rect.Right - tr.X && y < rect.Rect.Y + tr.Y)
                {
                    var nx = (x - (rect.Rect.Right - tr.X)) / tr.X;
                    var ny = (y - (rect.Rect.Y + tr.Y)) / tr.Y;
                    if (nx * nx + ny * ny > 1f) continue;
                }
                else if (br.X > 0 && br.Y > 0 && x > rect.Rect.Right - br.X && y > rect.Rect.Bottom - br.Y)
                {
                    var nx = (x - (rect.Rect.Right - br.X)) / br.X;
                    var ny = (y - (rect.Rect.Bottom - br.Y)) / br.Y;
                    if (nx * nx + ny * ny > 1f) continue;
                }
                else if (bl.X > 0 && bl.Y > 0 && x < rect.Rect.X + bl.X && y > rect.Rect.Bottom - bl.Y)
                {
                    var nx = (x - (rect.Rect.X + bl.X)) / bl.X;
                    var ny = (y - (rect.Rect.Bottom - bl.Y)) / bl.Y;
                    if (nx * nx + ny * ny > 1f) continue;
                }

                var idx = (y * _width + x) * 4;
                if (isGradient)
                {
                    var (r, g, b, a) = EvaluatePaintColor(paint, x, y);
                    BlendPixel(idx, r, g, b, a);
                }
                else
                {
                    BlendPixel(idx, solidR, solidG, solidB, solidA);
                }
            }
        }
    }

    public void FillRect(in RectF rect, in Paint paint)
    {
        var minX = Math.Max(0, (int)Math.Max(_currentClip.X, Math.Floor(rect.X)));
        var maxX = Math.Min(_width, (int)Math.Min(_currentClip.Right, Math.Ceiling(rect.Right)));
        var minY = Math.Max(0, (int)Math.Max(_currentClip.Y, Math.Floor(rect.Y)));
        var maxY = Math.Min(_height, (int)Math.Min(_currentClip.Bottom, Math.Ceiling(rect.Bottom)));

        var isGradient = paint.Gradient is not null;
        var (solidR, solidG, solidB, solidA) = isGradient ? default : EvaluatePaintColor(paint, 0, 0);

        for (var y = minY; y < maxY; y++)
        {
            for (var x = minX; x < maxX; x++)
            {
                var idx = (y * _width + x) * 4;
                if (isGradient)
                {
                    var (r, g, b, a) = EvaluatePaintColor(paint, x, y);
                    BlendPixel(idx, r, g, b, a);
                }
                else
                {
                    BlendPixel(idx, solidR, solidG, solidB, solidA);
                }
            }
        }
    }

    private static (byte R, byte G, byte B, byte A) EvaluatePaintColor(in Paint paint, int x, int y)
    {
        if (paint.Gradient is { } grad && grad.Stops.Count > 0)
        {
            float t = 0f;
            if (grad.Kind == GradientKind.Linear)
            {
                var dx = grad.End.X - grad.Start.X;
                var dy = grad.End.Y - grad.Start.Y;
                var lenSq = dx * dx + dy * dy;
                if (lenSq > 1e-6f)
                    t = ((x - grad.Start.X) * dx + (y - grad.Start.Y) * dy) / lenSq;
            }
            else if (grad.Kind == GradientKind.Radial)
            {
                var dx = (x - grad.Center.X) / Math.Max(1e-3f, grad.Radii.X);
                var dy = (y - grad.Center.Y) / Math.Max(1e-3f, grad.Radii.Y);
                t = MathF.Sqrt(dx * dx + dy * dy);
            }

            switch (grad.Spread)
            {
                case GradientSpread.Repeat:
                    t = t - MathF.Floor(t);
                    break;
                case GradientSpread.Reflect:
                    var intPart = (int)MathF.Floor(t);
                    var frac = t - intPart;
                    t = (intPart % 2 == 0) ? frac : (1f - frac);
                    break;
                default: // Pad
                    t = Math.Clamp(t, 0f, 1f);
                    break;
            }

            var c = InterpolateStops(grad.Stops, t);
            return ((byte)Math.Clamp((int)(c.R * 255), 0, 255),
                    (byte)Math.Clamp((int)(c.G * 255), 0, 255),
                    (byte)Math.Clamp((int)(c.B * 255), 0, 255),
                    (byte)Math.Clamp((int)(c.A * 255), 0, 255));
        }

        var col = paint.Color;
        return ((byte)Math.Clamp((int)(col.R * 255), 0, 255),
                (byte)Math.Clamp((int)(col.G * 255), 0, 255),
                (byte)Math.Clamp((int)(col.B * 255), 0, 255),
                (byte)Math.Clamp((int)(col.A * 255), 0, 255));
    }

    private static Rgba InterpolateStops(IReadOnlyList<GradientStop> stops, float t)
    {
        if (stops.Count == 1 || t <= stops[0].Offset)
            return stops[0].Color;
        if (t >= stops[^1].Offset)
            return stops[^1].Color;

        for (var i = 0; i < stops.Count - 1; i++)
        {
            if (t >= stops[i].Offset && t <= stops[i + 1].Offset)
            {
                var span = stops[i + 1].Offset - stops[i].Offset;
                var f = span > 1e-6f ? (t - stops[i].Offset) / span : 0f;
                var c1 = stops[i].Color;
                var c2 = stops[i + 1].Color;
                return new Rgba(
                    c1.R + (c2.R - c1.R) * f,
                    c1.G + (c2.G - c1.G) * f,
                    c1.B + (c2.B - c1.B) * f,
                    c1.A + (c2.A - c1.A) * f);
            }
        }

        return stops[^1].Color;
    }

    public void FillPath(PathData path, FillRule rule, in Paint paint)
    {
        FillRect(ComputePathBounds(path), paint);
    }

    public void StrokePath(PathData path, in Stroke stroke, in Paint paint)
    {
        var bounds = ComputePathBounds(path);
        var expanded = new RectF(bounds.X - stroke.Width, bounds.Y - stroke.Width, bounds.Width + 2 * stroke.Width, bounds.Height + 2 * stroke.Width);
        FillRect(expanded, paint);
    }

    public void DrawGlyphs(IFontHandle font, float size, ReadOnlySpan<ushort> glyphs, ReadOnlySpan<Vector2> origins, in Paint paint)
    {
        var c = paint.Color;
        var r = (byte)Math.Clamp((int)(c.R * 255), 0, 255);
        var g = (byte)Math.Clamp((int)(c.G * 255), 0, 255);
        var b = (byte)Math.Clamp((int)(c.B * 255), 0, 255);
        var a = (byte)Math.Clamp((int)(c.A * 255), 0, 255);

        for (var i = 0; i < origins.Length; i++)
        {
            var origin = origins[i];
            var minX = Math.Max(0, (int)origin.X);
            var maxX = Math.Min(_width, (int)(origin.X + size));
            var minY = Math.Max(0, (int)(origin.Y - size));
            var maxY = Math.Min(_height, (int)origin.Y);

            for (var y = minY; y < maxY; y++)
            {
                for (var x = minX; x < maxX; x++)
                {
                    var idx = (y * _width + x) * 4;
                    BlendPixel(idx, r, g, b, a);
                }
            }
        }
    }

    public void DrawImage(IImageHandle image, in RectF destination, ImageSampling sampling)
    {
        if (image is not DecodedImage decoded) return;

        var minX = Math.Max(0, (int)Math.Max(_currentClip.X, Math.Floor(destination.X)));
        var maxX = Math.Min(_width, (int)Math.Min(_currentClip.Right, Math.Ceiling(destination.Right)));
        var minY = Math.Max(0, (int)Math.Max(_currentClip.Y, Math.Floor(destination.Y)));
        var maxY = Math.Min(_height, (int)Math.Min(_currentClip.Bottom, Math.Ceiling(destination.Bottom)));

        var srcPixels = decoded.Pixels;
        var srcW = decoded.Width;
        var srcH = decoded.Height;

        for (var y = minY; y < maxY; y++)
        {
            var v = (float)(y - destination.Y) / destination.Height;
            var srcY = Math.Clamp((int)(v * srcH), 0, srcH - 1);

            for (var x = minX; x < maxX; x++)
            {
                var u = (float)(x - destination.X) / destination.Width;
                var srcX = Math.Clamp((int)(u * srcW), 0, srcW - 1);

                var srcIdx = (srcY * srcW + srcX) * 4;
                var dstIdx = (y * _width + x) * 4;

                var sr = srcPixels[srcIdx];
                var sg = srcPixels[srcIdx + 1];
                var sb = srcPixels[srcIdx + 2];
                var sa = srcPixels[srcIdx + 3];

                BlendPixel(dstIdx, sr, sg, sb, sa);
            }
        }
    }

    private void BlendPixel(int dstIdx, byte sr, byte sg, byte sb, byte sa)
    {
        if (sa == 0) return;
        if (sa == 255)
        {
            _pixels[dstIdx] = sr;
            _pixels[dstIdx + 1] = sg;
            _pixels[dstIdx + 2] = sb;
            _pixels[dstIdx + 3] = 255;
            return;
        }

        var alpha = sa / 255f;
        var invAlpha = 1f - alpha;

        _pixels[dstIdx] = (byte)(sr * alpha + _pixels[dstIdx] * invAlpha);
        _pixels[dstIdx + 1] = (byte)(sg * alpha + _pixels[dstIdx + 1] * invAlpha);
        _pixels[dstIdx + 2] = (byte)(sb * alpha + _pixels[dstIdx + 2] * invAlpha);
        _pixels[dstIdx + 3] = (byte)Math.Min(255, _pixels[dstIdx + 3] + sa);
    }

    public void PushLayer(in LayerOptions options) { Save(); }
    public void PopLayer() { Restore(); }

    private static RectF Intersect(in RectF a, in RectF b)
    {
        var left = Math.Max(a.X, b.X);
        var top = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.Right, b.Right);
        var bottom = Math.Min(a.Bottom, b.Bottom);
        return right > left && bottom > top ? new RectF(left, top, right - left, bottom - top) : new RectF(0, 0, 0, 0);
    }

    private static RectF ComputePathBounds(PathData path)
    {
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;

        foreach (var cmd in path.Commands)
        {
            switch (cmd.Verb)
            {
                case PathVerb.MoveTo:
                case PathVerb.LineTo:
                    minX = Math.Min(minX, cmd.P1.X);
                    minY = Math.Min(minY, cmd.P1.Y);
                    maxX = Math.Max(maxX, cmd.P1.X);
                    maxY = Math.Max(maxY, cmd.P1.Y);
                    break;
                case PathVerb.CubicTo:
                    minX = Math.Min(minX, Math.Min(cmd.P1.X, Math.Min(cmd.P2.X, cmd.P3.X)));
                    minY = Math.Min(minY, Math.Min(cmd.P1.Y, Math.Min(cmd.P2.Y, cmd.P3.Y)));
                    maxX = Math.Max(maxX, Math.Max(cmd.P1.X, Math.Max(cmd.P2.X, cmd.P3.X)));
                    maxY = Math.Max(maxY, Math.Max(cmd.P1.Y, Math.Max(cmd.P2.Y, cmd.P3.Y)));
                    break;
            }
        }

        if (minX > maxX || minY > maxY)
            return new RectF(0, 0, 0, 0);

        return new RectF(minX, minY, maxX - minX, maxY - minY);
    }
}
