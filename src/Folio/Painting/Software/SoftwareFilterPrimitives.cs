using System.Numerics;

namespace Folio.Painting.Software;

/// <summary>
/// The filter primitives with the most arithmetic (https://www.w3.org/TR/filter-effects-1/): turbulence (Perlin noise, from the
/// specification's reference code), displacement maps, convolution, tiling and lighting. Each takes and returns premultiplied bitmaps.
/// </summary>
internal static class SoftwareFilterPrimitives
{
    private static byte To8(float v) => v <= 0 ? (byte)0 : v >= 1 ? (byte)255 : (byte)(v * 255 + 0.5f);

    private static Vector4 Read(PixelSurface s, int x, int y)
    {
        var i = (y * s.Width + x) * 4;
        return new Vector4(s.Data[i], s.Data[i + 1], s.Data[i + 2], s.Data[i + 3]) / 255f;
    }

    private static void Write(PixelSurface s, int x, int y, Vector4 v)
    {
        var i = (y * s.Width + x) * 4;
        s.Data[i] = To8(v.X);
        s.Data[i + 1] = To8(v.Y);
        s.Data[i + 2] = To8(v.Z);
        s.Data[i + 3] = To8(v.W);
    }

    // ---- turbulence ----

    private const int BSize = 0x100, BM = 0xFF, PerlinN = 0x1000;

    private sealed class NoiseTables
    {
        public readonly int[] Lattice = new int[BSize + BSize + 2];
        public readonly double[,,] Gradient = new double[4, BSize + BSize + 2, 2];

        public NoiseTables(int seed)
        {
            const int RandM = 2147483647, RandA = 16807, RandQ = 127773, RandR = 2836;
            if (seed <= 0)
                seed = -(seed % (RandM - 1)) + 1;
            if (seed > RandM - 1)
                seed = RandM - 1;
            int Random(int s)
            {
                var result = RandA * (s % RandQ) - RandR * (s / RandQ);
                return result <= 0 ? result + RandM : result;
            }
            int i;
            for (var k = 0; k < 4; k++)
            {
                for (i = 0; i < BSize; i++)
                {
                    Lattice[i] = i;
                    for (var j = 0; j < 2; j++)
                    {
                        seed = Random(seed);
                        Gradient[k, i, j] = (double)(seed % (BSize + BSize) - BSize) / BSize;
                    }
                    var s = Math.Sqrt(Gradient[k, i, 0] * Gradient[k, i, 0] + Gradient[k, i, 1] * Gradient[k, i, 1]);
                    if (s == 0)
                        s = 1;
                    Gradient[k, i, 0] /= s;
                    Gradient[k, i, 1] /= s;
                }
            }
            for (i = BSize - 1; i > 0; i--)
            {
                var k = Lattice[i];
                seed = Random(seed);
                var j = seed % BSize;
                Lattice[i] = Lattice[j];
                Lattice[j] = k;
            }
            for (i = 0; i < BSize + 2; i++)
            {
                Lattice[BSize + i] = Lattice[i];
                for (var k = 0; k < 4; k++)
                    for (var j = 0; j < 2; j++)
                        Gradient[k, BSize + i, j] = Gradient[k, i, j];
            }
        }
    }

    private struct Stitch
    {
        public int Width, Height, WrapX, WrapY;
    }

    private static double SCurve(double t) => t * t * (3 - 2 * t);

    private static double Lerp(double t, double a, double b) => a + t * (b - a);

    private static double Noise2(NoiseTables tables, int channel, double vx, double vy, bool stitching, Stitch stitch)
    {
        var t = vx + PerlinN;
        var bx0 = (int)t;
        var bx1 = bx0 + 1;
        var rx0 = t - (int)t;
        var rx1 = rx0 - 1;
        t = vy + PerlinN;
        var by0 = (int)t;
        var by1 = by0 + 1;
        var ry0 = t - (int)t;
        var ry1 = ry0 - 1;
        if (stitching)
        {
            if (bx0 >= stitch.WrapX)
                bx0 -= stitch.Width;
            if (bx1 >= stitch.WrapX)
                bx1 -= stitch.Width;
            if (by0 >= stitch.WrapY)
                by0 -= stitch.Height;
            if (by1 >= stitch.WrapY)
                by1 -= stitch.Height;
        }
        bx0 &= BM;
        bx1 &= BM;
        by0 &= BM;
        by1 &= BM;
        var i = tables.Lattice[bx0];
        var j = tables.Lattice[bx1];
        var b00 = tables.Lattice[i + by0];
        var b10 = tables.Lattice[j + by0];
        var b01 = tables.Lattice[i + by1];
        var b11 = tables.Lattice[j + by1];
        var sx = SCurve(rx0);
        var sy = SCurve(ry0);
        var u = rx0 * tables.Gradient[channel, b00, 0] + ry0 * tables.Gradient[channel, b00, 1];
        var v = rx1 * tables.Gradient[channel, b10, 0] + ry0 * tables.Gradient[channel, b10, 1];
        var a = Lerp(sx, u, v);
        u = rx0 * tables.Gradient[channel, b01, 0] + ry1 * tables.Gradient[channel, b01, 1];
        v = rx1 * tables.Gradient[channel, b11, 0] + ry1 * tables.Gradient[channel, b11, 1];
        var b = Lerp(sx, u, v);
        return Lerp(sy, a, b);
    }

    private static double Turbulence(NoiseTables tables, int channel, double px, double py, double freqX, double freqY, int octaves, bool fractal, bool stitching, RectF tile)
    {
        var stitch = new Stitch();
        if (stitching)
        {
            // The frequencies are adjusted so that the tile's borders are continuous.
            if (freqX != 0)
            {
                var lo = Math.Floor(tile.Width * freqX) / tile.Width;
                var hi = Math.Ceiling(tile.Width * freqX) / tile.Width;
                freqX = freqX / lo < hi / freqX ? lo : hi;
            }
            if (freqY != 0)
            {
                var lo = Math.Floor(tile.Height * freqY) / tile.Height;
                var hi = Math.Ceiling(tile.Height * freqY) / tile.Height;
                freqY = freqY / lo < hi / freqY ? lo : hi;
            }
            stitch.Width = (int)(tile.Width * freqX + 0.5);
            stitch.WrapX = (int)(tile.X * freqX + PerlinN + stitch.Width);
            stitch.Height = (int)(tile.Height * freqY + 0.5);
            stitch.WrapY = (int)(tile.Y * freqY + PerlinN + stitch.Height);
        }
        double sum = 0;
        var (vx, vy) = (px * freqX, py * freqY);
        double ratio = 1;
        for (var octave = 0; octave < octaves; octave++)
        {
            var n = Noise2(tables, channel, vx, vy, stitching, stitch);
            sum += (fractal ? n : Math.Abs(n)) / ratio;
            vx *= 2;
            vy *= 2;
            ratio *= 2;
            if (stitching)
            {
                stitch.Width *= 2;
                stitch.WrapX = 2 * stitch.WrapX - PerlinN;
                stitch.Height *= 2;
                stitch.WrapY = 2 * stitch.WrapY - PerlinN;
            }
        }
        return sum;
    }

    /// <summary>Perlin noise over the bitmap, the pixel (x, y) at the point the inverse transform makes of its centre, plus half a unit like Skia.</summary>
    public static Frame Noise(PixelSurface like, Noise noise, RectF region, Matrix3x2 transform)
    {
        if (!Matrix3x2.Invert(transform, out var inverse) || !(noise.BaseFrequency.X >= 0 && noise.BaseFrequency.Y >= 0))
            return Frame.Of(new PixelSurface(like.Width, like.Height));
        var tables = new NoiseTables((int)Math.Floor(noise.Seed + 0.5));
        // Skia draws the shader into a bitmap, so the noise is rounded to bytes before any colour filter after it.
        return Frame.Of(Frame.Generated(like.Width, like.Height, (x, y) =>
        {
            // Skia samples the noise at the pixel centre in noise space, then half a unit further.
            var p = Vector2.Transform(new Vector2(x + 0.5f, y + 0.5f), inverse) + new Vector2(0.5f, 0.5f);
            Span<float> c = stackalloc float[4];
            for (var k = 0; k < 4; k++)
            {
                var n = Turbulence(tables, k, p.X, p.Y, noise.BaseFrequency.X, noise.BaseFrequency.Y, Math.Max(noise.Octaves, 0), noise.Fractal, noise.Stitch, region);
                c[k] = (float)(noise.Fractal ? (n + 1) / 2 : n);
            }
            // The channels are straight colour, clamped and then scaled by the unclamped alpha as Skia does.
            var alpha = c[3];
            return new Vector4(Math.Clamp(c[0], 0, 1) * alpha, Math.Clamp(c[1], 0, 1) * alpha, Math.Clamp(c[2], 0, 1) * alpha, Math.Clamp(alpha, 0, 1));
        }).Resolve());
    }

    // ---- displacement ----

    /// <summary>Each pixel of the source moved by the channels of the map: P'(x, y) = P(x + scale (X(x, y) - 0.5), y + scale (Y(x, y) - 0.5)).</summary>
    public static PixelSurface Displace(PixelSurface source, PixelSurface map, float scaleX, float scaleY, ColorChannel xChannel, ColorChannel yChannel)
    {
        var o = new PixelSurface(source.Width, source.Height);
        float Channel(Vector4 premultiplied, ColorChannel c)
        {
            // The map's colours are straight values.
            var a = premultiplied.W;
            var straight = a > 0 ? premultiplied / a : Vector4.Zero;
            return c switch { ColorChannel.R => straight.X, ColorChannel.G => straight.Y, ColorChannel.B => straight.Z, _ => a };
        }
        for (var y = 0; y < o.Height; y++)
        {
            for (var x = 0; x < o.Width; x++)
            {
                var m = Read(map, x, y);
                var sx = (int)MathF.Floor(x + scaleX * (Channel(m, xChannel) - 0.5f) + 0.5f);
                var sy = (int)MathF.Floor(y + scaleY * (Channel(m, yChannel) - 0.5f) + 0.5f);
                if (sx >= 0 && sx < source.Width && sy >= 0 && sy < source.Height)
                    Array.Copy(source.Data, (sy * source.Width + sx) * 4, o.Data, (y * o.Width + x) * 4, 4);
            }
        }
        return o;
    }

    // ---- tiling ----

    /// <summary>The input's pixels inside <paramref name="tile"/> repeated across <paramref name="destination"/> (device pixels).</summary>
    public static PixelSurface Tile(PixelSurface source, IntRect tile, IntRect destination)
    {
        var o = new PixelSurface(source.Width, source.Height);
        tile = IntRect.Intersect(tile, new IntRect(0, 0, source.Width, source.Height));
        if (tile.IsEmpty)
            return o;
        destination = IntRect.Intersect(destination, new IntRect(0, 0, source.Width, source.Height));
        for (var y = destination.Top; y < destination.Bottom; y++)
        {
            var sy = tile.Top + (((y - tile.Top) % tile.Height) + tile.Height) % tile.Height;
            for (var x = destination.Left; x < destination.Right; x++)
            {
                var sx = tile.Left + (((x - tile.Left) % tile.Width) + tile.Width) % tile.Width;
                Array.Copy(source.Data, (sy * source.Width + sx) * 4, o.Data, (y * o.Width + x) * 4, 4);
            }
        }
        return o;
    }

    // ---- convolution ----

    /// <summary>
    /// feConvolveMatrix: result(x, y) = sum over the kernel of source(x - targetX + j, y - targetY + i) times the kernel entry turned
    /// half way round, divided by the divisor, plus bias times alpha.
    /// </summary>
    public static PixelSurface Convolve(PixelSurface source, Filter f)
    {
        var o = new PixelSurface(source.Width, source.Height);
        if (f.Kernel is not { } kernel || f.KernelColumns <= 0 || f.KernelRows <= 0 || kernel.Count != f.KernelColumns * f.KernelRows || f.Divisor == 0
            || f.TargetX < 0 || f.TargetX >= f.KernelColumns || f.TargetY < 0 || f.TargetY >= f.KernelRows)
            return source;
        int w = source.Width, h = source.Height;
        Vector4 At(int x, int y)
        {
            if (x < 0 || x >= w || y < 0 || y >= h)
            {
                switch (f.EdgeMode)
                {
                    case EdgeMode.Duplicate: x = Math.Clamp(x, 0, w - 1); y = Math.Clamp(y, 0, h - 1); break;
                    case EdgeMode.Wrap: x = ((x % w) + w) % w; y = ((y % h) + h) % h; break;
                    default: return Vector4.Zero;
                }
            }
            var p = Read(source, x, y);
            // With the alpha preserved the colours are convolved straight.
            return f.PreserveAlpha && p.W > 0 ? new Vector4(p.X / p.W, p.Y / p.W, p.Z / p.W, p.W) : p;
        }
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var sum = Vector4.Zero;
                for (var i = 0; i < f.KernelRows; i++)
                {
                    for (var j = 0; j < f.KernelColumns; j++)
                    {
                        var weight = kernel[f.KernelColumns - j - 1 + (f.KernelRows - i - 1) * f.KernelColumns];
                        sum += At(x - f.TargetX + j, y - f.TargetY + i) * weight;
                    }
                }
                var result = sum / f.Divisor;
                var alpha = f.PreserveAlpha ? Read(source, x, y).W : Math.Clamp(result.W + f.Bias, 0, 1);
                var rgb = new Vector3(result.X + f.Bias * (f.PreserveAlpha ? 1 : alpha), result.Y + f.Bias * (f.PreserveAlpha ? 1 : alpha), result.Z + f.Bias * (f.PreserveAlpha ? 1 : alpha));
                rgb = Vector3.Clamp(rgb, Vector3.Zero, Vector3.One);
                if (f.PreserveAlpha)
                    rgb *= alpha; // premultiplied again
                else
                    rgb = Vector3.Min(rgb, new Vector3(alpha));
                Write(o, x, y, new Vector4(rgb, alpha));
            }
        }
        return o;
    }

    // ---- lighting ----

    /// <summary>feDiffuseLighting and feSpecularLighting: the alpha channel of the input is a height map lit from a light source.</summary>
    public static PixelSurface Light(PixelSurface source, Filter f, Matrix3x2 transform, bool specular)
    {
        var o = new PixelSurface(source.Width, source.Height);
        if (f.Light is not { } light)
            return source;
        int w = source.Width, h = source.Height;
        float A(int x, int y) => source.Data[(Math.Clamp(y, 0, h - 1) * w + Math.Clamp(x, 0, w - 1)) * 4 + 3] / 255f;
        var scale = MathF.Sqrt(Math.Abs(transform.M11 * transform.M22 - transform.M12 * transform.M21));
        var surfaceScale = f.SurfaceScale;
        var color = new Vector3(Shader.Byte(f.Color.R), Shader.Byte(f.Color.G), Shader.Byte(f.Color.B));

        // The light's place on the bitmap.
        Vector3 Position(Vector3 p)
        {
            var xy = Vector2.Transform(new Vector2(p.X, p.Y), transform);
            return new Vector3(xy.X, xy.Y, p.Z * scale);
        }
        var position = Position(light.Position);
        var target = Position(light.Target);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                // The surface normal by the Sobel kernels (the edges use the nearest pixels instead, as the spec's edge cases do).
                float nx = -surfaceScale / 4 * ((A(x + 1, y - 1) + 2 * A(x + 1, y) + A(x + 1, y + 1)) - (A(x - 1, y - 1) + 2 * A(x - 1, y) + A(x - 1, y + 1)));
                float ny = -surfaceScale / 4 * ((A(x - 1, y + 1) + 2 * A(x, y + 1) + A(x + 1, y + 1)) - (A(x - 1, y - 1) + 2 * A(x, y - 1) + A(x + 1, y - 1)));
                var n = Vector3.Normalize(new Vector3(nx, ny, 1));

                var z = surfaceScale * A(x, y);
                Vector3 l;
                var lightColor = color;
                switch (light.Kind)
                {
                    case LightKind.Distant:
                        l = Vector3.Normalize(light.Direction);
                        break;
                    case LightKind.Point:
                        l = Vector3.Normalize(position - new Vector3(x, y, z));
                        break;
                    default:
                    {
                        l = Vector3.Normalize(position - new Vector3(x, y, z));
                        var s = Vector3.Normalize(target - position);
                        var minusLDotS = -Vector3.Dot(l, s);
                        var cone = light.ConeAngle;
                        if (minusLDotS <= 0 || (cone is { } angle && minusLDotS < MathF.Cos(angle * MathF.PI / 180)))
                            lightColor = Vector3.Zero;
                        else
                            lightColor = color * MathF.Pow(minusLDotS, light.Exponent);
                        break;
                    }
                }

                Vector4 result;
                if (!specular)
                {
                    var factor = f.LightingConstant * Math.Max(Vector3.Dot(n, l), 0);
                    var rgb = Vector3.Clamp(lightColor * factor, Vector3.Zero, Vector3.One);
                    result = new Vector4(rgb, 1);
                }
                else
                {
                    var halfway = Vector3.Normalize(l + new Vector3(0, 0, 1));
                    var factor = f.LightingConstant * MathF.Pow(Math.Max(Vector3.Dot(n, halfway), 0), f.Shininess);
                    var rgb = Vector3.Clamp(lightColor * factor, Vector3.Zero, Vector3.One);
                    var alpha = Math.Max(rgb.X, Math.Max(rgb.Y, rgb.Z));
                    // Premultiplied: the colour is already at most the alpha.
                    result = new Vector4(rgb, alpha);
                }
                Write(o, x, y, result);
            }
        }
        return o;
    }
}
