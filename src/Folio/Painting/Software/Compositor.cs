using System.Numerics;

namespace Folio.Painting.Software;

/// <summary>A bitmap of premultiplied RGBA, one byte per channel.</summary>
internal sealed class PixelSurface(int width, int height)
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    public byte[] Data { get; } = new byte[checked(width * height * 4)];

    public PixelSurface Clone()
    {
        var copy = new PixelSurface(Width, Height);
        Array.Copy(Data, copy.Data, Data.Length);
        return copy;
    }
}

/// <summary>Combines a source colour with the pixel under it by a blend mode (https://www.w3.org/TR/compositing-1/).</summary>
internal static class Compositor
{
    /// <summary>
    /// Draws a premultiplied source colour (components 0 to 1) onto the pixel at <paramref name="offset"/> of the surface data,
    /// scaled by <paramref name="coverage"/> (0 to 1), by the blend mode.
    /// </summary>
    public static void Blend(byte[] data, int offset, Vector4 source, float coverage, BlendMode mode)
    {
        if (coverage <= 0)
            return;
        if (mode == BlendMode.Normal && coverage >= 1 && source.W >= 1)
        {
            BlendOpaque(data, offset, source);
            return;
        }
        var dr = data[offset] / 255f;
        var dg = data[offset + 1] / 255f;
        var db = data[offset + 2] / 255f;
        var da = data[offset + 3] / 255f;

        float r, g, b, a;
        if (mode == BlendMode.Normal)
        {
            // Source over, with the source scaled by coverage.
            source *= coverage;
            var inverse = 1 - source.W;
            r = source.X + dr * inverse;
            g = source.Y + dg * inverse;
            b = source.Z + db * inverse;
            a = source.W + da * inverse;
        }
        else
        {
            // Any other mode: the result for full coverage, then mixed with the backdrop by coverage.
            var d = new Vector4(dr, dg, db, da);
            var full = Combine(mode, source, d);
            var mixed = d + (full - d) * Math.Min(coverage, 1f);
            (r, g, b, a) = (mixed.X, mixed.Y, mixed.Z, mixed.W);
        }

        data[offset] = To8(r);
        data[offset + 1] = To8(g);
        data[offset + 2] = To8(b);
        data[offset + 3] = To8(a);
    }

    /// <summary>Stores an opaque normal-mode source without reading or blending the destination.</summary>
    public static void BlendOpaque(byte[] data, int offset, Vector4 source)
    {
        data[offset] = To8(source.X);
        data[offset + 1] = To8(source.Y);
        data[offset + 2] = To8(source.Z);
        data[offset + 3] = 255;
    }

    /// <summary>The blend of a premultiplied source over a premultiplied backdrop (components 0 to 1), without rounding.</summary>
    public static Vector4 Mix(BlendMode mode, Vector4 source, Vector4 backdrop) =>
        mode == BlendMode.Normal ? source + backdrop * (1 - source.W) : Combine(mode, source, backdrop);

    private static byte To8(float v) => v <= 0 ? (byte)0 : v >= 1 ? (byte)255 : (byte)(v * 255 + 0.5f);

    // The result of the mode for a premultiplied source s over a premultiplied backdrop d.
    private static Vector4 Combine(BlendMode mode, Vector4 s, Vector4 d)
    {
        var sa = s.W;
        var da = d.W;
        switch (mode)
        {
            case BlendMode.PlusLighter:
                return Vector4.Min(Vector4.One, s + d);
            case BlendMode.SourceIn:
                return s * da;
            case BlendMode.SourceOut:
                return s * (1 - da);
            case BlendMode.DestinationIn:
                return d * sa;
            case BlendMode.DestinationOut:
                return d * (1 - sa);
            case BlendMode.Xor:
                return s * (1 - da) + d * (1 - sa);
            case BlendMode.SourceAtop:
                return s * da + d * (1 - sa);
            case BlendMode.DestinationOver:
                return d + s * (1 - da);
            case BlendMode.DestinationAtop:
                return d * sa + s * (1 - da);
            case BlendMode.Copy:
                return s;
            default:
            {
                // A blend function of the unpremultiplied colours.
                var cs = sa > 0 ? new Vector3(s.X, s.Y, s.Z) / sa : Vector3.Zero;
                var cb = da > 0 ? new Vector3(d.X, d.Y, d.Z) / da : Vector3.Zero;
                var blended = BlendFunction(mode, cb, cs);
                var color = new Vector3(s.X, s.Y, s.Z) * (1 - da) + new Vector3(d.X, d.Y, d.Z) * (1 - sa) + blended * (sa * da);
                return new Vector4(color, sa + da * (1 - sa));
            }
        }
    }

    private static Vector3 BlendFunction(BlendMode mode, Vector3 cb, Vector3 cs)
    {
        switch (mode)
        {
            case BlendMode.Hue:
                return SetLum(SetSat(cs, Sat(cb)), Lum(cb));
            case BlendMode.Saturation:
                return SetLum(SetSat(cb, Sat(cs)), Lum(cb));
            case BlendMode.Color:
                return SetLum(cs, Lum(cb));
            case BlendMode.Luminosity:
                return SetLum(cb, Lum(cs));
        }
        return new Vector3(Channel(mode, cb.X, cs.X), Channel(mode, cb.Y, cs.Y), Channel(mode, cb.Z, cs.Z));
    }

    private static float Channel(BlendMode mode, float cb, float cs) => mode switch
    {
        BlendMode.Multiply => cb * cs,
        BlendMode.Screen => cb + cs - cb * cs,
        BlendMode.Overlay => HardLight(cs, cb),
        BlendMode.Darken => Math.Min(cb, cs),
        BlendMode.Lighten => Math.Max(cb, cs),
        BlendMode.ColorDodge => cb == 0 ? 0 : cs >= 1 ? 1 : Math.Min(1, cb / (1 - cs)),
        BlendMode.ColorBurn => cb >= 1 ? 1 : cs <= 0 ? 0 : 1 - Math.Min(1, (1 - cb) / cs),
        BlendMode.HardLight => HardLight(cb, cs),
        BlendMode.SoftLight => SoftLight(cb, cs),
        BlendMode.Difference => Math.Abs(cb - cs),
        BlendMode.Exclusion => cb + cs - 2 * cb * cs,
        _ => cs,
    };

    private static float HardLight(float cb, float cs) => cs <= 0.5f ? cb * 2 * cs : cb + (2 * cs - 1) - cb * (2 * cs - 1);

    private static float SoftLight(float cb, float cs)
    {
        if (cs <= 0.5f)
            return cb - (1 - 2 * cs) * cb * (1 - cb);
        var d = cb <= 0.25f ? ((16 * cb - 12) * cb + 4) * cb : MathF.Sqrt(cb);
        return cb + (2 * cs - 1) * (d - cb);
    }

    private static float Lum(Vector3 c) => 0.3f * c.X + 0.59f * c.Y + 0.11f * c.Z;

    private static float Sat(Vector3 c) => Math.Max(c.X, Math.Max(c.Y, c.Z)) - Math.Min(c.X, Math.Min(c.Y, c.Z));

    private static Vector3 ClipColor(Vector3 c)
    {
        var l = Lum(c);
        var n = Math.Min(c.X, Math.Min(c.Y, c.Z));
        var x = Math.Max(c.X, Math.Max(c.Y, c.Z));
        if (n < 0)
            c = new Vector3(l) + (c - new Vector3(l)) * l / (l - n);
        if (x > 1)
            c = new Vector3(l) + (c - new Vector3(l)) * (1 - l) / (x - l);
        return c;
    }

    private static Vector3 SetLum(Vector3 c, float l) => ClipColor(c + new Vector3(l - Lum(c)));

    private static Vector3 SetSat(Vector3 c, float s)
    {
        var max = Math.Max(c.X, Math.Max(c.Y, c.Z));
        var min = Math.Min(c.X, Math.Min(c.Y, c.Z));
        if (max <= min)
            return Vector3.Zero;
        return (c - new Vector3(min)) * s / (max - min);
    }
}
