using System.Numerics;
using Folio.Imaging;

namespace Folio.Painting.Software;

/// <summary>Where a fill's colour comes from: a premultiplied RGBA colour (components 0 to 1) at a point of the surface.</summary>
internal abstract class Shader
{
    /// <summary>The colour at the centre of pixel (x, y), premultiplied.</summary>
    public abstract Vector4 ColorAt(int x, int y);

    public virtual bool IsSolid => false;

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
    private sealed record Level(int Width, int Height, byte[] Pixels);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IImageHandle, List<Level>> MipCache = new();

    private readonly List<Level> _levels;
    private readonly Matrix33 _inverse; // device to image pixels
    private readonly bool _repeatX, _repeatY, _smooth;
    private readonly float _lod;
    private bool _clamp;

    private ImageShader(IImageHandle image, Matrix33 inverse, bool repeatX, bool repeatY, bool smooth)
    {
        _levels = MipCache.GetValue(image, i => [new Level(i.Width, i.Height, i.Pixels.ToArray())]);
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
    }

    // Half the size, each pixel the average of four, taken premultiplied so that transparent pixels do not darken the colour.
    private static Level Halve(Level from)
    {
        var w = Math.Max(1, from.Width / 2);
        var h = Math.Max(1, from.Height / 2);
        var pixels = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                for (var k = 0; k < 4; k++)
                {
                    var sx = Math.Min(2 * x + (k & 1), from.Width - 1);
                    var sy = Math.Min(2 * y + (k >> 1), from.Height - 1);
                    var i = (sy * from.Width + sx) * 4;
                    var alpha = from.Pixels[i + 3] / 255f;
                    r += from.Pixels[i] * alpha;
                    g += from.Pixels[i + 1] * alpha;
                    b += from.Pixels[i + 2] * alpha;
                    a += alpha;
                }
                var o = (y * w + x) * 4;
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
        return Matrix33.Invert(imageToDevice, out var inverse) ? new ImageShader(image, inverse, true, true, smooth) { _clamp = true } : null;
    }

    public override Vector4 ColorAt(int x, int y)
    {
        var p = _inverse.Apply(new Vector2(x + 0.5f, y + 0.5f));
        Vector4 color;
        if (!_smooth)
            color = Texel(0, p.X, p.Y, nearest: true);
        else if (_lod <= 0)
            color = Texel(0, p.X, p.Y, nearest: false);
        else
        {
            var lower = Math.Min((int)MathF.Floor(_lod), _levels.Count - 1);
            var upper = Math.Min(lower + 1, _levels.Count - 1);
            var t = _lod - MathF.Floor(_lod);
            color = Vector4.Lerp(SampleLevel(lower, p), SampleLevel(upper, p), t);
        }
        // The texels were filtered as straight colours (as the Skia backend does); premultiply the result.
        return new Vector4(color.X * color.W, color.Y * color.W, color.Z * color.W, color.W);
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
        var i = (y * w + x) * 4;
        return new Vector4(l.Pixels[i], l.Pixels[i + 1], l.Pixels[i + 2], l.Pixels[i + 3]) / 255f;
    }
}
