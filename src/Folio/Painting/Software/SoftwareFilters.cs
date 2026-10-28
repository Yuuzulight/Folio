using System.Numerics;

namespace Folio.Painting.Software;

/// <summary>
/// Filter effects on a layer (https://www.w3.org/TR/filter-effects-1/): a chain of primitives, each taking the source, its
/// alpha, the primitive before it or an earlier result. Lengths are in the space the layer was drawn in and are scaled to pixels.
/// A primitive with nothing to work on (a turbulence without noise settings, say) passes its input through.
/// </summary>
internal static class SoftwareFilters
{
    public static PixelSurface Apply(PixelSurface source, IReadOnlyList<Filter> filters, Matrix3x2 transform)
    {
        var scaleX = MathF.Sqrt(transform.M11 * transform.M11 + transform.M12 * transform.M12);
        var scaleY = MathF.Sqrt(transform.M21 * transform.M21 + transform.M22 * transform.M22);
        var results = new Frame[filters.Count];
        var sourceFrame = Frame.Of(source);
        Frame? alphaOnly = null;

        Frame Input(FilterInput input, int i) => input.Source switch
        {
            FilterSource.Previous => i == 0 ? sourceFrame : results[i - 1],
            FilterSource.SourceAlpha => alphaOnly ??= Frame.Of(AlphaOf(source)),
            FilterSource.Result when input.Index >= 0 && input.Index < i => results[input.Index],
            _ => sourceFrame,
        };

        for (var i = 0; i < filters.Count; i++)
        {
            var f = filters[i];
            var linear = f.LinearRgb && f.Kind is not FilterKind.Flood;
            // Colour filters (the conversion to linear light among them) stay pending on the frame; a primitive that
            // draws its inputs takes them as they are, or rounded to bytes where it works on bytes.
            Frame In(FilterInput input) => linear ? Input(input, i).Then(LinearOf) : Input(input, i);
            PixelSurface Pixels(FilterInput input) => In(input).Resolve();
            var (dx, dy) = f.Deviations is { } d ? (d.X, d.Y) : (f.StdDeviation, f.StdDeviation);

            Frame? computed = f.Kind switch
            {
                FilterKind.Blur => Frame.Of(Blur(Pixels(f.In), dx * scaleX, dy * scaleY)),
                FilterKind.ColorMatrix => ColorMatrix(In(f.In), f.Matrix),
                FilterKind.DropShadow => Frame.Of(DropShadow(Pixels(f.In), f, dx * scaleX, dy * scaleY, scaleX, scaleY)),
                FilterKind.Offset => Frame.Of(Offset(Pixels(f.In), f.Offset.X * scaleX, f.Offset.Y * scaleY)),
                FilterKind.Flood => Frame.Of(Flood(source, f.Color)),
                FilterKind.Composite => Frame.Of(Composite(In(f.In), In(f.In2), f)),
                FilterKind.Merge => Frame.Of(Merge(source, (f.Inputs ?? []).Select(In).ToList())),
                FilterKind.Blend => Frame.Of(Blend(In(f.In), In(f.In2), f.Blend)),
                FilterKind.Morphology => Frame.Of(Morphology(Pixels(f.In), f.Radius.X * scaleX, f.Radius.Y * scaleY, f.Dilate)),
                FilterKind.ComponentTransfer => ComponentTransfer(In(f.In), f.Transfer),
                FilterKind.Turbulence when f.Noise is { } noise => SoftwareFilterPrimitives.Noise(source, noise, f.Subregion ?? new RectF(0, 0, source.Width, source.Height), transform),
                FilterKind.DisplacementMap => Frame.Of(SoftwareFilterPrimitives.Displace(Pixels(f.In), Pixels(f.In2), f.Scale * scaleX, f.Scale * scaleY, f.XChannel, f.YChannel)),
                FilterKind.Tile when f is { Source: { Width: > 0, Height: > 0 } tile, Subregion: { } tileRegion } =>
                    Frame.Of(SoftwareFilterPrimitives.Tile(Pixels(f.In), DeviceRect(tile, transform), DeviceRect(tileRegion, transform))),
                FilterKind.ConvolveMatrix => Frame.Of(SoftwareFilterPrimitives.Convolve(Pixels(f.In), f)),
                FilterKind.Image when f.Image is { } image && f.Destination is { Width: > 0, Height: > 0 } destination => Frame.Of(DrawImage(source, image, destination, transform)),
                FilterKind.DiffuseLighting => Frame.Of(SoftwareFilterPrimitives.Light(Pixels(f.In), f, transform, specular: false)),
                FilterKind.SpecularLighting => Frame.Of(SoftwareFilterPrimitives.Light(Pixels(f.In), f, transform, specular: true)),
                _ => null,
            };
            var result = computed ?? Input(f.In, i);
            if (linear && computed is not null)
                result = result.Then(SrgbOf);
            if (f.Subregion is { } region)
                result = Crop(result, region, transform);
            results[i] = result;
        }
        return results[^1].Resolve();
    }

    // ---- helpers ----

    private static IntRect DeviceRect(RectF r, Matrix3x2 transform)
    {
        var corners = new[] { new Vector2(r.X, r.Y), new Vector2(r.Right, r.Y), new Vector2(r.Right, r.Bottom), new Vector2(r.X, r.Bottom) }.Select(p => Vector2.Transform(p, transform)).ToArray();
        return new IntRect((int)MathF.Round(corners.Min(p => p.X)), (int)MathF.Round(corners.Min(p => p.Y)), (int)MathF.Round(corners.Max(p => p.X)), (int)MathF.Round(corners.Max(p => p.Y)));
    }

    // feImage: the image scaled into its destination rectangle.
    private static PixelSurface DrawImage(PixelSurface like, Imaging.IImageHandle image, RectF destination, Matrix3x2 transform)
    {
        var o = new PixelSurface(like.Width, like.Height);
        var imageToUser = Matrix3x2.CreateScale(destination.Width / image.Width, destination.Height / image.Height) * Matrix3x2.CreateTranslation(destination.X, destination.Y);
        var toDevice = Matrix33.FromAffine(imageToUser * transform);
        if (ImageShader.ForDrawing(image, toDevice, smooth: true) is not { } shader || !Matrix33.Invert(toDevice, out var inverse))
            return o;
        for (var y = 0; y < o.Height; y++)
        {
            for (var x = 0; x < o.Width; x++)
            {
                var p = inverse.Apply(new Vector2(x + 0.5f, y + 0.5f));
                if (p.X < 0 || p.Y < 0 || p.X >= image.Width || p.Y >= image.Height)
                    continue;
                var c = shader.ColorAt(x, y);
                var i = (y * o.Width + x) * 4;
                o.Data[i] = To8(c.X);
                o.Data[i + 1] = To8(c.Y);
                o.Data[i + 2] = To8(c.Z);
                o.Data[i + 3] = To8(c.W);
            }
        }
        return o;
    }

    private static PixelSurface AlphaOf(PixelSurface s)
    {
        var o = new PixelSurface(s.Width, s.Height);
        for (var i = 3; i < s.Data.Length; i += 4)
            o.Data[i] = s.Data[i];
        return o;
    }

    private static PixelSurface Map(PixelSurface s, Func<Vector4, Vector4> f)
    {
        var o = new PixelSurface(s.Width, s.Height);
        for (var i = 0; i < s.Data.Length; i += 4)
        {
            var v = f(new Vector4(s.Data[i], s.Data[i + 1], s.Data[i + 2], s.Data[i + 3]) / 255f);
            o.Data[i] = To8(v.X);
            o.Data[i + 1] = To8(v.Y);
            o.Data[i + 2] = To8(v.Z);
            o.Data[i + 3] = To8(v.W);
        }
        return o;
    }

    private static void Write(PixelSurface s, int x, int y, Vector4 v)
    {
        var i = (y * s.Width + x) * 4;
        s.Data[i] = To8(v.X);
        s.Data[i + 1] = To8(v.Y);
        s.Data[i + 2] = To8(v.Z);
        s.Data[i + 3] = To8(v.W);
    }

    private static byte To8(float v) => v <= 0 ? (byte)0 : v >= 1 ? (byte)255 : (byte)(v * 255 + 0.5f);

    private static Vector4 Straight(Vector4 p) => p.W > 0 ? new Vector4(p.X / p.W, p.Y / p.W, p.Z / p.W, p.W) : Vector4.Zero;

    private static Vector4 Premultiply(Vector4 c) => new(c.X * c.W, c.Y * c.W, c.Z * c.W, c.W);

    private static float SrgbToLinear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    private static float LinearToSrgb(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1 / 2.4f) - 0.055f;

    private static Vector4 LinearOf(Vector4 p)
    {
        var c = Straight(p);
        return Premultiply(new Vector4(SrgbToLinear(c.X), SrgbToLinear(c.Y), SrgbToLinear(c.Z), c.W));
    }

    private static Vector4 SrgbOf(Vector4 p)
    {
        var c = Straight(p);
        return Premultiply(new Vector4(LinearToSrgb(c.X), LinearToSrgb(c.Y), LinearToSrgb(c.Z), c.W));
    }

    private static Frame Crop(Frame frame, RectF region, Matrix3x2 transform)
    {
        // The region's bounding box in pixels; what lies outside it is cleared.
        var corners = new[] { new Vector2(region.X, region.Y), new Vector2(region.Right, region.Y), new Vector2(region.Right, region.Bottom), new Vector2(region.X, region.Bottom) }
            .Select(p => Vector2.Transform(p, transform)).ToArray();
        return frame.Cropped((int)MathF.Round(corners.Min(p => p.X)), (int)MathF.Round(corners.Min(p => p.Y)),
            (int)MathF.Round(corners.Max(p => p.X)), (int)MathF.Round(corners.Max(p => p.Y)));
    }

    // ---- primitives ----

    private static PixelSurface Blur(PixelSurface s, float sigmaX, float sigmaY) =>
        sigmaX <= 0 && sigmaY <= 0 ? s : Wrap(s, GaussianBlur.Apply(s.Data, s.Width, s.Height, 4, Math.Max(sigmaX, 0), Math.Max(sigmaY, 0)));

    private static PixelSurface Wrap(PixelSurface like, byte[] data)
    {
        var o = new PixelSurface(like.Width, like.Height);
        Array.Copy(data, o.Data, data.Length);
        return o;
    }

    private static Frame ColorMatrix(Frame s, IReadOnlyList<float>? m)
    {
        if (m is not { Count: 20 })
            return s;
        return s.Then(p =>
        {
            var c = Straight(p);
            float Row(int r) => m[r * 5] * c.X + m[r * 5 + 1] * c.Y + m[r * 5 + 2] * c.Z + m[r * 5 + 3] * c.W + m[r * 5 + 4];
            var result = new Vector4(Math.Clamp(Row(0), 0, 1), Math.Clamp(Row(1), 0, 1), Math.Clamp(Row(2), 0, 1), Math.Clamp(Row(3), 0, 1));
            return Premultiply(result);
        });
    }

    private static PixelSurface Offset(PixelSurface s, float dx, float dy)
    {
        var (ox, oy) = ((int)MathF.Round(dx), (int)MathF.Round(dy));
        if (ox == 0 && oy == 0)
            return s;
        var o = new PixelSurface(s.Width, s.Height);
        for (var y = 0; y < s.Height; y++)
        {
            var sy = y - oy;
            if (sy < 0 || sy >= s.Height)
                continue;
            for (var x = 0; x < s.Width; x++)
            {
                var sx = x - ox;
                if (sx >= 0 && sx < s.Width)
                    Array.Copy(s.Data, (sy * s.Width + sx) * 4, o.Data, (y * s.Width + x) * 4, 4);
            }
        }
        return o;
    }

    private static PixelSurface DropShadow(PixelSurface s, Filter f, float sigmaX, float sigmaY, float scaleX, float scaleY)
    {
        var alpha = Offset(AlphaOf(s), f.Offset.X * scaleX, f.Offset.Y * scaleY);
        var shadow = sigmaX <= 0 && sigmaY <= 0
            ? alpha
            : Wrap(alpha, GaussianBlur.Apply(alpha.Data, alpha.Width, alpha.Height, 4, Math.Max(sigmaX, 0), Math.Max(sigmaY, 0), activeChannelMask: 8));
        var tint = Shader.Premultiplied(f.Color);
        var o = new PixelSurface(s.Width, s.Height);
        for (var i = 0; i < o.Data.Length; i += 4)
        {
            var shadowAlpha = shadow.Data[i + 3] / 255f;
            // Most pixels in a drop-shadow surface are transparent.  Preserve the
            // original source directly in that case instead of entering the
            // vector blend path; this is both exact for source-over and avoids
            // four float conversions per empty pixel.
            if (shadowAlpha <= 0)
            {
                o.Data[i] = s.Data[i];
                o.Data[i + 1] = s.Data[i + 1];
                o.Data[i + 2] = s.Data[i + 2];
                o.Data[i + 3] = s.Data[i + 3];
                continue;
            }
            var below = tint * shadowAlpha;
            o.Data[i] = To8(below.X);
            o.Data[i + 1] = To8(below.Y);
            o.Data[i + 2] = To8(below.Z);
            o.Data[i + 3] = To8(below.W);
            Compositor.Blend(o.Data, i, new Vector4(s.Data[i], s.Data[i + 1], s.Data[i + 2], s.Data[i + 3]) / 255f, 1, BlendMode.Normal);
        }
        return o;
    }

    private static PixelSurface Flood(PixelSurface like, Rgba color)
    {
        var o = new PixelSurface(like.Width, like.Height);
        var c = Shader.Premultiplied(color);
        for (var i = 0; i < o.Data.Length; i += 4)
        {
            o.Data[i] = To8(c.X);
            o.Data[i + 1] = To8(c.Y);
            o.Data[i + 2] = To8(c.Z);
            o.Data[i + 3] = To8(c.W);
        }
        return o;
    }

    private static PixelSurface Composite(Frame a, Frame b, Filter f)
    {
        // a is the foreground (in), b the background (in2).
        var o = new PixelSurface(a.Width, a.Height);
        for (var y = 0; y < o.Height; y++)
        {
            for (var x = 0; x < o.Width; x++)
            {
                var s = a.At(x, y);
                var d = b.At(x, y);
                Vector4 r;
                switch (f.Operator)
                {
                    case CompositeOperator.In: r = s * d.W; break;
                    case CompositeOperator.Out: r = s * (1 - d.W); break;
                    case CompositeOperator.Atop: r = s * d.W + d * (1 - s.W); break;
                    case CompositeOperator.Xor: r = s * (1 - d.W) + d * (1 - s.W); break;
                    case CompositeOperator.Arithmetic when f.Coefficients is { Count: 4 } k:
                        r = k[0] * s * d + k[1] * s + k[2] * d + new Vector4(k[3]);
                        r = Vector4.Clamp(r, Vector4.Zero, Vector4.One);
                        r = new Vector4(Math.Min(r.X, r.W), Math.Min(r.Y, r.W), Math.Min(r.Z, r.W), r.W);
                        break;
                    case CompositeOperator.Arithmetic: r = Vector4.Zero; break;
                    default: r = s + d * (1 - s.W); break;
                }
                Write(o, x, y, r);
            }
        }
        return o;
    }

    private static PixelSurface Merge(PixelSurface like, List<Frame> inputs)
    {
        var o = new PixelSurface(like.Width, like.Height);
        for (var y = 0; y < o.Height; y++)
        {
            for (var x = 0; x < o.Width; x++)
            {
                var v = Vector4.Zero;
                foreach (var input in inputs)
                    v = Compositor.Mix(BlendMode.Normal, input.At(x, y), v);
                Write(o, x, y, v);
            }
        }
        return o;
    }

    private static PixelSurface Blend(Frame top, Frame bottom, BlendMode mode)
    {
        var o = new PixelSurface(bottom.Width, bottom.Height);
        for (var y = 0; y < o.Height; y++)
        {
            for (var x = 0; x < o.Width; x++)
                Write(o, x, y, Compositor.Mix(mode, top.At(x, y), bottom.At(x, y)));
        }
        return o;
    }

    private static PixelSurface Morphology(PixelSurface s, float radiusX, float radiusY, bool dilate)
    {
        var rx = (int)MathF.Round(radiusX);
        var ry = (int)MathF.Round(radiusY);
        if (rx <= 0 && ry <= 0)
            return s;
        PixelSurface Pass(PixelSurface input, int r, bool horizontal)
        {
            if (r <= 0)
                return input;
            var o = new PixelSurface(input.Width, input.Height);
            for (var y = 0; y < input.Height; y++)
            {
                for (var x = 0; x < input.Width; x++)
                {
                    for (var c = 0; c < 4; c++)
                    {
                        var best = dilate ? 0 : 255;
                        for (var k = -r; k <= r; k++)
                        {
                            var px = horizontal ? x + k : x;
                            var py = horizontal ? y : y + k;
                            var v = px < 0 || py < 0 || px >= input.Width || py >= input.Height ? 0 : input.Data[(py * input.Width + px) * 4 + c];
                            best = dilate ? Math.Max(best, v) : Math.Min(best, v);
                        }
                        o.Data[(y * input.Width + x) * 4 + c] = (byte)best;
                    }
                }
            }
            return o;
        }
        return Pass(Pass(s, rx, true), ry, false);
    }

    private static Frame ComponentTransfer(Frame s, IReadOnlyList<TransferFunction>? functions)
    {
        if (functions is null)
            return s;
        var tables = new byte[4][];
        for (var channel = 0; channel < 4; channel++)
            tables[channel] = Table(functions, channel);
        return s.Then(p =>
        {
            var c = Straight(p);
            var mapped = new Vector4(tables[0][To8(c.X)], tables[1][To8(c.Y)], tables[2][To8(c.Z)], tables[3][To8(c.W)]) / 255f;
            return Premultiply(mapped);
        });
    }

    // One channel's transfer function as a 256-entry table (the order is red, green, blue, alpha).
    private static byte[] Table(IReadOnlyList<TransferFunction> functions, int channel)
    {
        var f = channel < functions.Count ? functions[channel] : null;
        var v = f?.Values is { Count: > 0 } values ? values : null;
        var table = new byte[256];
        for (var i = 0; i < table.Length; i++)
        {
            var c = i / 255f;
            var value = f?.Kind switch
            {
                TransferKind.Table when v is { Count: 1 } => v[0],
                TransferKind.Table when v is not null => Interpolate(v, c),
                TransferKind.Discrete when v is not null => v[Math.Min((int)(c * v.Count), v.Count - 1)],
                TransferKind.Linear => f.Slope * c + f.Intercept,
                TransferKind.Gamma => f.Amplitude * MathF.Pow(c, f.Exponent) + f.Offset,
                _ => c,
            };
            table[i] = To8(float.IsFinite(value) ? value : 0);
        }
        return table;

        static float Interpolate(IReadOnlyList<float> v, float c)
        {
            var k = Math.Min((int)(c * (v.Count - 1)), v.Count - 2);
            return v[k] + (c * (v.Count - 1) - k) * (v[k + 1] - v[k]);
        }
    }
}
