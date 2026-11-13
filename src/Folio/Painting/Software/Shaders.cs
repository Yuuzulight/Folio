using System.Numerics;
using Folio.Imaging;

namespace Folio.Painting.Software;

/// <summary>Where a fill's colour comes from: a premultiplied RGBA colour (components 0 to 1) at a point of the surface.</summary>
internal abstract class Shader
{
    /// <summary>The colour at the centre of pixel (x, y), premultiplied.</summary>
    public abstract Vector4 ColorAt(int x, int y);

    public virtual bool IsSolid => false;

    /// <summary>
    /// The colour of every pixel of row <paramref name="y"/> from <paramref name="x"/> on, when it is the same bits as
    /// <see cref="ColorAt"/> gives for each of them; false when it varies (or might) along the row.
    /// </summary>
    public virtual bool TryRowColor(int x, int y, out Vector4 color)
    {
        color = default;
        return false;
    }

    // Colours are held as bytes, as the Skia backend holds them: 0.5 of alpha is 128 of 255, not exactly a half.
    public static float Byte(float v) => MathF.Round(Math.Clamp(float.IsFinite(v) ? v : 0, 0f, 1f) * 255) / 255;

    public static Vector4 Premultiplied(Rgba c)
    {
        var a = Byte(c.A);
        return new Vector4(Byte(c.R) * a, Byte(c.G) * a, Byte(c.B) * a, a);
    }

    public static Shader For(in Paint paint, in Matrix33 transform) =>
        paint.Gradient is { } gradient ? GradientShader.Create(gradient, transform)
        : paint.Pattern is { } pattern ? (Shader?)ImageShader.ForPattern(pattern, transform) ?? new SolidShader(Vector4.Zero)
        : new SolidShader(Premultiplied(paint.Color));
}

internal sealed class SolidShader(Vector4 color) : Shader
{
    public Vector4 Color { get; } = color;

    public override bool IsSolid => true;

    public override Vector4 ColorAt(int x, int y) => Color;
}

/// <summary>Linear, radial (with an optional focal circle) and conic gradients (https://drafts.csswg.org/css-images-3/).</summary>
internal sealed class GradientShader : Shader
{
    private readonly Gradient _gradient;
    private readonly Matrix33 _inverse; // device to the space the gradient's own transform maps from
    private readonly float[] _offsets;
    private readonly Vector4[] _colors; // straight alpha, not premultiplied
    private readonly Vector2 _stretch; // radial: the factor that turns the ellipse into a circle, about the centre
    private readonly float _rotation; // conic: radians

    private GradientShader(Gradient gradient, Matrix33 inverse)
    {
        _gradient = gradient;
        _inverse = inverse;
        var stops = gradient.Stops.ToArray();
        _offsets = stops.Select(s => Math.Clamp(float.IsFinite(s.Offset) ? s.Offset : 0, 0, 1)).ToArray();
        for (var i = 1; i < _offsets.Length; i++)
            _offsets[i] = Math.Max(_offsets[i], _offsets[i - 1]);
        _colors = stops.Select(s => new Vector4(Byte(s.Color.R), Byte(s.Color.G), Byte(s.Color.B), Byte(s.Color.A))).ToArray();
        var r = gradient.Radii;
        _stretch = new Vector2(1, r.X > 0 && r.Y > 0 ? r.X / r.Y : 1);
        _rotation = (gradient.StartAngle - 90) * MathF.PI / 180;
    }

    public static Shader Create(Gradient gradient, in Matrix33 transform)
    {
        if (gradient.Stops.Count == 0)
            return new SolidShader(Vector4.Zero);
        if (gradient.Stops.Count == 1)
            return new SolidShader(Premultiplied(gradient.Stops[0].Color));
        var toCanvas = gradient.Transform ?? Matrix3x2.Identity;
        // Device point = gradient point x toCanvas x transform (row vectors).
        if (!Matrix33.Invert(toCanvas * transform, out var inverse))
            return new SolidShader(Vector4.Zero);
        return new GradientShader(gradient, inverse);
    }

    // A linear gradient along device y: x reaches t only through a zero (no x term in p.Y or w, and a zero x
    // component of the axis), so ColorAt is the same bits across the row and one lookup serves it.
    public override bool TryRowColor(int x, int y, out Vector4 color)
    {
        var d = _gradient.End - _gradient.Start;
        if (_gradient.Kind == GradientKind.Linear && d.X == 0 && _inverse.M12 == 0 && _inverse.M13 == 0
            && float.IsFinite(_inverse.M11) && float.IsFinite(_inverse.M21) && float.IsFinite(_inverse.M31) && float.IsFinite(_gradient.Start.X))
        {
            color = ColorAt(x, y);
            return true;
        }
        color = default;
        return false;
    }

    public override Vector4 ColorAt(int x, int y)
    {
        var p = _inverse.Apply(new Vector2(x + 0.5f, y + 0.5f));
        float t;
        switch (_gradient.Kind)
        {
            case GradientKind.Linear:
            {
                var d = _gradient.End - _gradient.Start;
                var length2 = d.LengthSquared();
                t = length2 > 0 ? Vector2.Dot(p - _gradient.Start, d) / length2 : 0;
                break;
            }
            case GradientKind.Radial:
            {
                var c = _gradient.Center;
                var radius = Math.Max(_gradient.Radii.X, 0.001f);
                // The stretch turns the ellipse into a circle about its centre.
                var q = new Vector2(p.X, c.Y + (p.Y - c.Y) * _stretch.Y);
                if (_gradient.Focus is not { } focus)
                {
                    t = (q - c).Length() / radius;
                    break;
                }
                if (!TwoPointConical(q, focus, Math.Max(_gradient.FocusRadius, 0), c, radius, out t))
                    return Vector4.Zero;
                break;
            }
            default:
            {
                var c = _gradient.Center;
                var v = p - c;
                var (sin, cos) = MathF.SinCos(-_rotation);
                var rotated = new Vector2(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
                var angle = MathF.Atan2(rotated.Y, rotated.X) * 180 / MathF.PI;
                if (angle < 0)
                    angle += 360;
                t = angle / Math.Max(_gradient.EndAngle - _gradient.StartAngle, 0.001f);
                break;
            }
        }
        return Lookup(Spread(t));
    }

    // The circle through the point on the line from the focal circle to the end circle (the largest parameter with a radius at least zero).
    private static bool TwoPointConical(Vector2 p, Vector2 focus, float focusRadius, Vector2 center, float radius, out float t)
    {
        var cd = center - focus;
        var pd = p - focus;
        var dr = radius - focusRadius;
        var a = Vector2.Dot(cd, cd) - dr * dr;
        var b = Vector2.Dot(pd, cd) + focusRadius * dr;
        var c = Vector2.Dot(pd, pd) - focusRadius * focusRadius;
        t = 0;
        if (MathF.Abs(a) < 1e-9f)
        {
            if (MathF.Abs(b) < 1e-9f)
                return false;
            t = c / (2 * b);
            return focusRadius + t * dr >= 0;
        }
        var discriminant = b * b - a * c;
        if (discriminant < 0)
            return false;
        var root = MathF.Sqrt(discriminant);
        var t1 = (b + root) / a;
        var t2 = (b - root) / a;
        var (hi, lo) = t1 >= t2 ? (t1, t2) : (t2, t1);
        if (focusRadius + hi * dr >= 0)
        {
            t = hi;
            return true;
        }
        if (focusRadius + lo * dr >= 0)
        {
            t = lo;
            return true;
        }
        return false;
    }

    private float Spread(float t)
    {
        if (!float.IsFinite(t))
            return 0;
        switch (_gradient.Spread)
        {
            case GradientSpread.Repeat:
                return t - MathF.Floor(t);
            case GradientSpread.Reflect:
            {
                var m = t - 2 * MathF.Floor(t / 2);
                return m > 1 ? 2 - m : m;
            }
            default:
                return Math.Clamp(t, 0, 1);
        }
    }

    // Colours between stops are interpolated as straight (not premultiplied) values, then premultiplied.
    private Vector4 Lookup(float t)
    {
        var last = _offsets.Length - 1;
        Vector4 color;
        if (t <= _offsets[0])
            color = _colors[0];
        else if (t >= _offsets[last])
            color = _colors[last];
        else
        {
            var i = 1;
            while (i < last && t > _offsets[i])
                i++;
            var span = _offsets[i] - _offsets[i - 1];
            color = span > 0 ? Vector4.Lerp(_colors[i - 1], _colors[i], (t - _offsets[i - 1]) / span) : _colors[i];
        }
        return new Vector4(color.X * color.W, color.Y * color.W, color.Z * color.W, color.W);
    }
}

/// <summary>
/// An image as a fill, sampled through a transform and tiled in each direction or not at all. Shrunk by more than half it is
/// sampled from the two nearest levels of a mipmap, each half the size of the one before, and blended between them.
/// </summary>
internal sealed class ImageShader : Shader
{
    private sealed record Level(int Width, int Height, ReadOnlyMemory<byte> Pixels);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IImageHandle, List<Level>> MipCache = new();

    private readonly List<Level> _levels;
    private readonly Matrix33 _inverse; // device to image pixels
    private readonly bool _repeatX, _repeatY, _smooth;
    private readonly float _lod;
    private bool _clamp;

    private ImageShader(IImageHandle image, Matrix33 inverse, bool repeatX, bool repeatY, bool smooth)
    {
        _levels = MipCache.GetValue(image, i => [new Level(i.Width, i.Height, i.Pixels)]);
        _inverse = inverse;
        _repeatX = repeatX;
        _repeatY = repeatY;
        _smooth = smooth;
        // Image pixels per device pixel, along the way the image is shrunk most.
        var pixelsPerDevicePixel = MathF.Sqrt(Math.Abs(inverse.M11 * inverse.M22 - inverse.M12 * inverse.M21)) / Math.Max(MathF.Abs(inverse.M33), 1e-6f);
        _lod = smooth && pixelsPerDevicePixel > 1 ? MathF.Log2(pixelsPerDevicePixel) : 0;
        if (_lod > 0)
        {
            lock (_levels)
            {
                while (_levels.Count - 1 < (int)MathF.Ceiling(_lod) && (_levels[^1].Width > 1 || _levels[^1].Height > 1))
                    _levels.Add(Halve(_levels[^1]));
            }
        }
        if (_smooth && _lod > 0 && float.IsFinite(_lod))
        {
            // The two levels a sample blends, with their scales to level 0, fixed once instead of looked up per pixel.
            var lowerIndex = Math.Min((int)MathF.Floor(_lod), _levels.Count - 1);
            var upperIndex = Math.Min(lowerIndex + 1, _levels.Count - 1);
            _mipT = _lod - MathF.Floor(_lod);
            _lowerLevel = _levels[lowerIndex];
            _upperLevel = _levels[upperIndex];
            _lowerScale = new Vector2((float)_lowerLevel.Width / _levels[0].Width, (float)_lowerLevel.Height / _levels[0].Height);
            _upperScale = new Vector2((float)_upperLevel.Width / _levels[0].Width, (float)_upperLevel.Height / _levels[0].Height);
        }
    }

    /// <summary>Test switch: sample every pixel through the general path.</summary>
    [ThreadStatic]
    internal static bool DisableSeparableSampling;

    private Level? _lowerLevel, _upperLevel;
    private Vector2 _lowerScale, _upperScale;
    private float _mipT;

    // Half the size, each pixel the average of four, taken premultiplied so that transparent pixels do not darken the colour.
    private static Level Halve(Level from)
    {
        var w = Math.Max(1, from.Width / 2);
        var h = Math.Max(1, from.Height / 2);
        var pixels = new byte[w * h * 4];
        var source = from.Pixels.Span;
        for (var y = 0; y < h; y++)
        {
            var top = 2 * y * from.Width * 4;
            var bottom = Math.Min(2 * y + 1, from.Height - 1) * from.Width * 4;
            for (var x = 0; x < w; x++)
            {
                var left = 2 * x * 4;
                var right = Math.Min(2 * x + 1, from.Width - 1) * 4;
                var tl = top + left;
                var tr = top + right;
                var bl = bottom + left;
                var br = bottom + right;
                var o = (y * w + x) * 4;
                if ((source[tl + 3] & source[tr + 3] & source[bl + 3] & source[br + 3]) == 255)
                {
                    for (var channel = 0; channel < 3; channel++)
                        pixels[o + channel] = (byte)((source[tl + channel] + source[tr + channel] + source[bl + channel] + source[br + channel] + 2) / 4);
                    pixels[o + 3] = 255;
                    continue;
                }
                float r = 0, g = 0, b = 0, a = 0;
                for (var k = 0; k < 4; k++)
                {
                    var i = (k < 2 ? top : bottom) + ((k & 1) == 0 ? left : right);
                    var alpha = source[i + 3] / 255f;
                    r += source[i] * alpha;
                    g += source[i + 1] * alpha;
                    b += source[i + 2] * alpha;
                    a += alpha;
                }
                if (a > 0)
                {
                    pixels[o] = (byte)Math.Clamp((int)(r / a + 0.5f), 0, 255);
                    pixels[o + 1] = (byte)Math.Clamp((int)(g / a + 0.5f), 0, 255);
                    pixels[o + 2] = (byte)Math.Clamp((int)(b / a + 0.5f), 0, 255);
                }
                pixels[o + 3] = (byte)Math.Clamp((int)(a / 4 * 255 + 0.5f), 0, 255);
            }
        }
        return new Level(w, h, pixels);
    }

    public static ImageShader? ForPattern(ImagePattern pattern, in Matrix33 transform)
    {
        if (pattern.Image.Width <= 0 || pattern.Image.Height <= 0 || pattern.Image.Pixels.Length < pattern.Image.Width * pattern.Image.Height * 4)
            return null;
        return Matrix33.Invert(pattern.Transform * transform, out var inverse)
            ? new ImageShader(pattern.Image, inverse, pattern.RepeatX, pattern.RepeatY, pattern.Sampling == ImageSampling.Smooth)
            : null;
    }

    /// <summary>An image drawn into a destination: image pixel (u, v) lands at (u, v) x <paramref name="imageToDevice"/>; outside the image the edge pixels continue.</summary>
    public static ImageShader? ForDrawing(IImageHandle image, in Matrix33 imageToDevice, bool smooth)
    {
        if (image.Width <= 0 || image.Height <= 0 || image.Pixels.Length < image.Width * image.Height * 4)
            return null;
        if (!Matrix33.Invert(imageToDevice, out var inverse))
            return null;
        var shader = new ImageShader(image, inverse, true, true, smooth) { _clamp = true };
        shader._separable = !DisableSeparableSampling && shader._lowerLevel is not null
            && System.Runtime.InteropServices.MemoryMarshal.TryGetArray(shader._lowerLevel.Pixels, out var lowerPixels) && lowerPixels.Offset == 0
            && System.Runtime.InteropServices.MemoryMarshal.TryGetArray(shader._upperLevel!.Pixels, out var upperPixels) && upperPixels.Offset == 0
            && inverse.M12 == 0 && inverse.M21 == 0 && inverse.M13 == 0 && inverse.M23 == 0
            && float.IsFinite(inverse.M11) && float.IsFinite(inverse.M22) && float.IsFinite(inverse.M31) && float.IsFinite(inverse.M32) && float.IsFinite(inverse.M33);
        if (shader._separable)
        {
            shader._lowerBytes = ArrayOf(shader._lowerLevel!);
            shader._upperBytes = ArrayOf(shader._upperLevel!);
            shader._lowerRows = new RowCache(shader._lowerLevel!.Width);
            shader._upperRows = new RowCache(shader._upperLevel!.Width);
        }
        return shader;
    }

    public override Vector4 ColorAt(int x, int y)
    {
        if (_separable && (uint)x < MaxSeparableX)
            return Premultiplied(SampleSeparable(x, y));
        var p = _inverse.Apply(new Vector2(x + 0.5f, y + 0.5f));
        Vector4 color;
        if (!_smooth)
            color = Texel(0, p.X, p.Y, nearest: true);
        else if (_lod <= 0)
            color = Texel(0, p.X, p.Y, nearest: false);
        else if (_clamp)
            color = Vector4.Lerp(
                TexelClamped(_lowerLevel!, p.X * _lowerScale.X, p.Y * _lowerScale.Y),
                TexelClamped(_upperLevel!, p.X * _upperScale.X, p.Y * _upperScale.Y), _mipT);
        else
        {
            var lower = Math.Min((int)MathF.Floor(_lod), _levels.Count - 1);
            var upper = Math.Min(lower + 1, _levels.Count - 1);
            var t = _lod - MathF.Floor(_lod);
            color = Vector4.Lerp(SampleLevel(lower, p), SampleLevel(upper, p), t);
        }
        return Premultiplied(color);
    }

    // The texels were filtered as straight colours (as the Skia backend does); premultiply the result.
    private static Vector4 Premultiplied(Vector4 color) => new(color.X * color.W, color.Y * color.W, color.Z * color.W, color.W);

    // With no rotation, skew or perspective in the device-to-image map, a sample's x position depends only on x and its y
    // position only on y, so the texel indices and weights are worked out once per column and once per row (the same
    // arithmetic as Texel, which the zero terms leave bit-identical) rather than once per pixel.
    private const int MaxSeparableX = 16384;

    private struct Axis
    {
        public int A, B; // the two texel indices (rows are already multiplied by the level width); A < 0: not yet computed
        public float F;  // the weight of B
    }

    private Axis[]? _colLower, _colUpper;
    private byte[]? _lowerBytes, _upperBytes;
    private RowCache? _lowerRows, _upperRows;
    private Axis _rowLower, _rowUpper;
    private int _rowY = int.MinValue;
    private bool _separable;

    private static Axis Locate(float position, int size, int stride)
    {
        var u = position - 0.5f;
        var x0 = (int)MathF.Floor(u);
        return new Axis
        {
            A = Math.Clamp(x0, 0, size - 1) * stride,
            B = Math.Clamp(x0 + 1, 0, size - 1) * stride,
            F = u - x0,
        };
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private Vector4 SampleSeparable(int x, int y)
    {
        if (_colLower is null || x >= _colLower.Length)
        {
            var size = Math.Min(MaxSeparableX, Math.Max(x + 1, (_colLower?.Length ?? 64) * 2));
            _colLower = Grow(_colLower, size);
            _colUpper = Grow(_colUpper, size);
        }
        ref var lowerColumn = ref _colLower[x];
        ref var upperColumn = ref _colUpper![x];
        if (lowerColumn.A < 0)
        {
            var px = _inverse.Apply(new Vector2(x + 0.5f, 0.5f)).X;
            lowerColumn = Locate(px * _lowerScale.X, _lowerLevel!.Width, 1);
            upperColumn = Locate(px * _upperScale.X, _upperLevel!.Width, 1);
        }
        if (y != _rowY)
        {
            var py = _inverse.Apply(new Vector2(0.5f, y + 0.5f)).Y;
            _rowLower = Locate(py * _lowerScale.Y, _lowerLevel!.Height, 1);
            _rowUpper = Locate(py * _upperScale.Y, _upperLevel!.Height, 1);
            _rowY = y;
        }
        return Vector4.Lerp(Bilinear(_lowerRows!.Get(_lowerBytes!, _rowLower.A, false), _lowerRows.Get(_lowerBytes!, _rowLower.B, true), lowerColumn, _rowLower.F),
            Bilinear(_upperRows!.Get(_upperBytes!, _rowUpper.A, false), _upperRows.Get(_upperBytes!, _rowUpper.B, true), upperColumn, _rowUpper.F), _mipT);
    }

    private static byte[] ArrayOf(Level level) => System.Runtime.InteropServices.MemoryMarshal.TryGetArray(level.Pixels, out var segment) ? segment.Array! : [];

    private static Axis[] Grow(Axis[]? old, int size)
    {
        var grown = new Axis[size];
        if (old is not null)
            Array.Copy(old, grown, old.Length);
        for (var i = old?.Length ?? 0; i < size; i++)
            grown[i].A = -1;
        return grown;
    }

    // The rows a bilinear sample reads, as floats: converted once when the sample row changes, not once per sample.
    private sealed class RowCache(int width)
    {
        private readonly int[] _row = [-1, -1];
        private readonly Vector4[][] _buffer = [new Vector4[width], new Vector4[width]];
        private int _last;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        public Vector4[] Get(byte[] pixels, int row, bool keepLast)
        {
            for (var i = 0; i < 2; i++)
                if (_row[i] == row)
                    return _buffer[_last = i];
            var slot = keepLast ? 1 - _last : _last;
            var buffer = _buffer[slot];
            var unit = ByteToUnit;
            var from = row * width * 4;
            for (var x = 0; x < width; x++, from += 4)
                buffer[x] = new Vector4(unit[pixels[from]], unit[pixels[from + 1]], unit[pixels[from + 2]], unit[pixels[from + 3]]);
            _row[slot] = row;
            _last = slot;
            return buffer;
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static Vector4 Bilinear(Vector4[] top, Vector4[] bottom, Axis column, float rowWeight)
    {
        var topColor = Vector4.Lerp(top[column.A], top[column.B], column.F);
        var bottomColor = Vector4.Lerp(bottom[column.A], bottom[column.B], column.F);
        return Vector4.Lerp(topColor, bottomColor, rowWeight);
    }

    // Texel's clamped bilinear branch on a known level, with the same arithmetic.
    private Vector4 TexelClamped(Level image, float px, float py)
    {
        var u = px - 0.5f;
        var v = py - 0.5f;
        var x0 = (int)MathF.Floor(u);
        var y0 = (int)MathF.Floor(v);
        var fx = u - x0;
        var fy = v - y0;
        var pixels = image.Pixels.Span;
        var left = Math.Clamp(x0, 0, image.Width - 1);
        var right = Math.Clamp(x0 + 1, 0, image.Width - 1);
        var topRow = Math.Clamp(y0, 0, image.Height - 1) * image.Width;
        var bottomRow = Math.Clamp(y0 + 1, 0, image.Height - 1) * image.Width;
        var topColor = Vector4.Lerp(ReadPixel(pixels, topRow + left), ReadPixel(pixels, topRow + right), fx);
        var bottomColor = Vector4.Lerp(ReadPixel(pixels, bottomRow + left), ReadPixel(pixels, bottomRow + right), fx);
        return Vector4.Lerp(topColor, bottomColor, fy);
    }

    // The sample at a point of the image, in the level's own pixels.
    private Vector4 SampleLevel(int level, Vector2 p)
    {
        var scaleX = (float)_levels[level].Width / _levels[0].Width;
        var scaleY = (float)_levels[level].Height / _levels[0].Height;
        return Texel(level, p.X * scaleX, p.Y * scaleY, nearest: false);
    }

    private Vector4 Texel(int level, float px, float py, bool nearest)
    {
        if (nearest)
            return Fetch(level, (int)MathF.Floor(px), (int)MathF.Floor(py));
        var u = px - 0.5f;
        var v = py - 0.5f;
        var x0 = (int)MathF.Floor(u);
        var y0 = (int)MathF.Floor(v);
        var fx = u - x0;
        var fy = v - y0;
        if (_clamp)
        {
            var image = _levels[level];
            var pixels = image.Pixels.Span;
            var left = Math.Clamp(x0, 0, image.Width - 1);
            var right = Math.Clamp(x0 + 1, 0, image.Width - 1);
            var topRow = Math.Clamp(y0, 0, image.Height - 1) * image.Width;
            var bottomRow = Math.Clamp(y0 + 1, 0, image.Height - 1) * image.Width;
            var topColor = Vector4.Lerp(ReadPixel(pixels, topRow + left), ReadPixel(pixels, topRow + right), fx);
            var bottomColor = Vector4.Lerp(ReadPixel(pixels, bottomRow + left), ReadPixel(pixels, bottomRow + right), fx);
            return Vector4.Lerp(topColor, bottomColor, fy);
        }
        var top = Vector4.Lerp(Fetch(level, x0, y0), Fetch(level, x0 + 1, y0), fx);
        var bottom = Vector4.Lerp(Fetch(level, x0, y0 + 1), Fetch(level, x0 + 1, y0 + 1), fx);
        return Vector4.Lerp(top, bottom, fy);
    }

    private Vector4 Fetch(int level, int x, int y)
    {
        var l = _levels[level];
        var w = l.Width;
        var h = l.Height;
        if (_clamp)
        {
            x = Math.Clamp(x, 0, w - 1);
            y = Math.Clamp(y, 0, h - 1);
        }
        else
        {
            if (_repeatX)
                x = ((x % w) + w) % w;
            else if (x < 0 || x >= w)
                return Vector4.Zero;
            if (_repeatY)
                y = ((y % h) + h) % h;
            else if (y < 0 || y >= h)
                return Vector4.Zero;
        }
        return ReadPixel(l.Pixels.Span, y * w + x);
    }

    private static readonly float[] ByteToUnit = Enumerable.Range(0, 256).Select(b => b / 255f).ToArray();

    private static Vector4 ReadPixel(ReadOnlySpan<byte> pixels, int index)
    {
        var i = index * 4;
        // Table entries are b / 255f, the same division, so the result is bit-identical.
        var unit = ByteToUnit;
        return new Vector4(unit[pixels[i]], unit[pixels[i + 1]], unit[pixels[i + 2]], unit[pixels[i + 3]]);
    }
}
