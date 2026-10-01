using System.Numerics;
using System.Runtime.CompilerServices;
using Folio.Imaging;
using Folio.Painting;
using Folio.Typography;
using SkiaSharp;

namespace Folio.Skia;

/// <summary>An <see cref="ICanvas"/> drawing onto a SkiaSharp canvas, antialiased, one CSS pixel per unit.</summary>
/// <param name="subpixelText">Subpixel (LCD) antialiasing for text, for opaque backgrounds on a known screen; greyscale otherwise.</param>
public sealed class SkiaCanvas(SKCanvas canvas, bool subpixelText = false) : ICanvas
{
    public void Save() => canvas.Save();

    public void Restore() => canvas.Restore();

    public void Transform(in Matrix3x2 matrix)
    {
        var m = ToSkia(matrix);
        canvas.Concat(in m);
    }

    public void Transform(in Matrix4x4 matrix)
    {
        var m = new SKMatrix(matrix.M11, matrix.M21, matrix.M41, matrix.M12, matrix.M22, matrix.M42, matrix.M14, matrix.M24, matrix.M44);
        canvas.Concat(in m);
    }

    public void ClipRoundedRect(in RoundedRect rect)
    {
        using var skRect = ToSkia(rect);
        canvas.ClipRoundRect(skRect, SKClipOperation.Intersect, antialias: true);
    }

    public void ClipPath(PathData path, FillRule rule)
    {
        using var skPath = ToSkia(path, rule);
        canvas.ClipPath(skPath, SKClipOperation.Intersect, antialias: true);
    }

    public void FillRoundedRect(in RoundedRect rect, in Paint paint)
    {
        using var skRect = ToSkia(rect);
        using var skPaint = Fill(paint);
        canvas.DrawRoundRect(skRect, skPaint);
    }

    public void FillPath(PathData path, FillRule rule, in Paint paint)
    {
        using var skPath = ToSkia(path, rule);
        using var skPaint = Fill(paint);
        canvas.DrawPath(skPath, skPaint);
    }

    public void StrokePath(PathData path, in Stroke stroke, in Paint paint)
    {
        using var skPath = ToSkia(path, FillRule.NonZero);
        using var skPaint = Fill(paint);
        skPaint.Style = SKPaintStyle.Stroke;
        skPaint.StrokeWidth = stroke.Width;
        skPaint.StrokeCap = stroke.Cap switch { LineCap.Round => SKStrokeCap.Round, LineCap.Square => SKStrokeCap.Square, _ => SKStrokeCap.Butt };
        skPaint.StrokeJoin = stroke.Join switch { LineJoin.Round => SKStrokeJoin.Round, LineJoin.Bevel => SKStrokeJoin.Bevel, _ => SKStrokeJoin.Miter };
        skPaint.StrokeMiter = stroke.MiterLimit;
        using var dash = stroke.Dashes is { Count: > 0 } d ? SKPathEffect.CreateDash([.. d], stroke.DashOffset) : null;
        skPaint.PathEffect = dash;
        canvas.DrawPath(skPath, skPaint);
    }

    public void DrawGlyphs(IFontHandle font, float size, ReadOnlySpan<ushort> glyphs, ReadOnlySpan<Vector2> origins, in Paint paint)
    {
        if (Typeface(font) is not { } typeface || glyphs.Length == 0)
            return;
        // Subpixel positioning; greyscale edges unless the caller knows the backdrop is opaque (study 12).
        using var skFont = new SKFont(typeface, size) { Subpixel = true, Edging = subpixelText ? SKFontEdging.SubpixelAntialias : SKFontEdging.Antialias };
        var points = new SKPoint[origins.Length];
        for (var i = 0; i < points.Length; i++)
            points[i] = new SKPoint(origins[i].X, origins[i].Y);
        using var builder = new SKTextBlobBuilder();
        builder.AddPositionedRun(glyphs, skFont, points);
        using var blob = builder.Build();
        using var skPaint = Fill(paint);
        canvas.DrawText(blob, 0, 0, skPaint);
    }

    public void DrawImage(IImageHandle image, in RectF destination, ImageSampling sampling)
    {
        if (Image(image) is not { } skImage)
            return;
        var options = sampling == ImageSampling.Pixelated
            ? new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None)
            : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawImage(skImage, new SKRect(destination.X, destination.Y, destination.Right, destination.Bottom), options, paint);
    }

    // One Skia image per image handle, copied from its straight-alpha RGBA pixels on first use.
    private static readonly ConditionalWeakTable<IImageHandle, SKImage?> Images = new();

    private static SKImage? Image(IImageHandle image) => Images.GetValue(image, i =>
        i.Width > 0 && i.Height > 0 && i.Pixels.Length >= i.Width * i.Height * 4
            ? SKImage.FromPixelCopy(new SKImageInfo(i.Width, i.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul), i.Pixels.Span)
            : null);

    public float[] GlyphIntercepts(IFontHandle font, float size, ReadOnlySpan<ushort> glyphs, ReadOnlySpan<Vector2> origins, float top, float bottom)
    {
        if (Typeface(font) is not { } typeface || glyphs.Length == 0)
            return [];
        using var skFont = new SKFont(typeface, size) { Subpixel = true };
        var points = new SKPoint[origins.Length];
        for (var i = 0; i < points.Length; i++)
            points[i] = new SKPoint(origins[i].X, origins[i].Y);
        using var builder = new SKTextBlobBuilder();
        builder.AddPositionedRun(glyphs, skFont, points);
        using var blob = builder.Build();
        return blob?.GetIntercepts(top, bottom) ?? [];
    }

    // One typeface per font handle, created from its bytes on first use.
    private static readonly ConditionalWeakTable<IFontHandle, SKTypeface?> Typefaces = new();

    private static SKTypeface? Typeface(IFontHandle font) => Typefaces.GetValue(font, f =>
    {
        using var data = SKData.CreateCopy(f.Data.Span);
        return SKTypeface.FromData(data, f.FaceIndex);
    });

    public void PushLayer(in LayerOptions options)
    {
        var owned = new List<IDisposable>();
        try
        {
            var skPaint = Own(owned, new SKPaint
            {
                Color = SKColors.Black.WithAlpha(ToByte(options.Opacity)), ImageFilter = Chain(options.Filters, null, owned), BlendMode = ToSkia(options.Blend),
            });
            if (options.Backdrop is not { Count: > 0 } backdrop)
            {
                canvas.SaveLayer(skPaint);
                return;
            }
            // The backdrop is read inside the clip only, its edges mirrored for blurs (filter-effects-2 §2.1).
            var r = options.BackdropClip.Rect;
            var crop = Own(owned, SKImageFilter.CreateCrop(new SKRect(r.X, r.Y, r.Right, r.Bottom), SKShaderTileMode.Mirror));
            canvas.SaveLayer(new SKCanvasSaveLayerRec { Paint = skPaint, Backdrop = Chain(backdrop, crop, owned) });
            // Then it is clipped: everything outside the clip is cleared.
            using var clip = ToSkia(options.BackdropClip);
            canvas.Save();
            canvas.ClipRoundRect(clip, SKClipOperation.Difference, antialias: true);
            canvas.Clear(SKColors.Transparent);
            canvas.Restore();
        }
        finally
        {
            foreach (var o in owned)
                o.Dispose();
        }
    }

    private static T Own<T>(List<IDisposable> owned, T item) where T : IDisposable
    {
        owned.Add(item);
        return item;
    }

    // Filter primitives as a graph of image filters (a null filter is the layer's content, Skia's source): each takes its
    // inputs from the source, its alpha, the primitive before it or an earlier one's result, works in linear light when
    // asked, and is cropped to its subregion. Skia may return no filter for one that does nothing (a zero blur); its
    // input then goes on unchanged.
    private static SKImageFilter? Chain(IReadOnlyList<Filter>? filters, SKImageFilter? source, List<IDisposable> owned)
    {
        if (filters is not { Count: > 0 })
            return source;
        var results = new SKImageFilter?[filters.Count];
        SKImageFilter? alpha = null;
        SKImageFilter? Input(FilterInput input, int i) => input.Source switch
        {
            FilterSource.Previous => i == 0 ? source : results[i - 1],
            FilterSource.SourceAlpha => alpha ??= Own(owned, SKImageFilter.CreateColorFilter(Own(owned, SKColorFilter.CreateColorMatrix(AlphaOnly)), source)),
            FilterSource.Result when input.Index >= 0 && input.Index < i => results[input.Index],
            _ => source,
        };
        SKImageFilter Color(SKColorFilter filter, SKImageFilter? input) => Own(owned, SKImageFilter.CreateColorFilter(Own(owned, filter), input));

        for (var i = 0; i < filters.Count; i++)
        {
            var f = filters[i];
            // In linear light the inputs are converted from sRGB first and the result back after. A flood's colour is
            // the same either way; turbulence has no input, its noise being in the working space.
            var linear = f.LinearRgb && f.Kind is not FilterKind.Flood;
            SKImageFilter? In(FilterInput input)
            {
                var image = Input(input, i);
                return linear ? Color(SKColorFilter.CreateSrgbToLinearGamma(), image) : image;
            }
            var (sx, sy) = f.Deviations is { } d ? (d.X, d.Y) : (f.StdDeviation, f.StdDeviation);
            var next = f.Kind switch
            {
                FilterKind.Blur => SKImageFilter.CreateBlur(sx, sy, In(f.In)),
                FilterKind.DropShadow => SKImageFilter.CreateDropShadow(f.Offset.X, f.Offset.Y, sx, sy, ToSkia(f.Color), In(f.In)),
                FilterKind.ColorMatrix => SKImageFilter.CreateColorFilter(Own(owned, SKColorFilter.CreateColorMatrix([.. f.Matrix ?? Identity])), In(f.In)),
                FilterKind.Offset => SKImageFilter.CreateOffset(f.Offset.X, f.Offset.Y, In(f.In)),
                FilterKind.Flood => SKImageFilter.CreateShader(Own(owned, SKShader.CreateColor(ToSkia(f.Color))), false),
                FilterKind.Composite when f.Operator == CompositeOperator.Arithmetic =>
                    f.Coefficients is { Count: 4 } k ? SKImageFilter.CreateArithmetic(k[0], k[1], k[2], k[3], true, In(f.In2), In(f.In)) : null,
                FilterKind.Composite => SKImageFilter.CreateBlendMode(f.Operator switch
                {
                    CompositeOperator.In => SKBlendMode.SrcIn,
                    CompositeOperator.Out => SKBlendMode.SrcOut,
                    CompositeOperator.Atop => SKBlendMode.SrcATop,
                    CompositeOperator.Xor => SKBlendMode.Xor,
                    _ => SKBlendMode.SrcOver,
                }, In(f.In2), In(f.In)),
                FilterKind.Merge => SKImageFilter.CreateMerge([.. (f.Inputs ?? []).Select(In)]),
                FilterKind.Blend => SKImageFilter.CreateBlendMode(ToSkia(f.Blend), In(f.In2), In(f.In)),
                FilterKind.Morphology when f.Dilate => SKImageFilter.CreateDilate(f.Radius.X, f.Radius.Y, In(f.In)),
                FilterKind.Morphology => SKImageFilter.CreateErode(f.Radius.X, f.Radius.Y, In(f.In)),
                FilterKind.ComponentTransfer => SKImageFilter.CreateColorFilter(Own(owned, SKColorFilter.CreateTable(
                    Table(f.Transfer, 3), Table(f.Transfer, 0), Table(f.Transfer, 1), Table(f.Transfer, 2))), In(f.In)),
                FilterKind.Turbulence => Turbulence(f.Noise, f.Subregion, owned),
                FilterKind.DisplacementMap => SKImageFilter.CreateDisplacementMapEffect(ToSkia(f.XChannel), ToSkia(f.YChannel), f.Scale, In(f.In2), In(f.In)),
                FilterKind.Tile when f is { Source: { Width: > 0, Height: > 0 } src, Subregion: { } dst } =>
                    // Skia's tile takes no source input: an offset of nothing stands for the source.
                    SKImageFilter.CreateTile(new SKRect(src.X, src.Y, src.Right, src.Bottom), new SKRect(dst.X, dst.Y, dst.Right, dst.Bottom),
                        In(f.In) ?? Own(owned, SKImageFilter.CreateOffset(0, 0))),
                FilterKind.ConvolveMatrix => Convolution(f, In(f.In)),
                FilterKind.Image when f.Image is { } handle && Image(handle) is { } image && f.Destination is { Width: > 0, Height: > 0 } dest =>
                    SKImageFilter.CreateImage(image, new SKRect(0, 0, image.Width, image.Height), new SKRect(dest.X, dest.Y, dest.Right, dest.Bottom),
                        new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)),
                FilterKind.DiffuseLighting or FilterKind.SpecularLighting => Lighting(f, In(f.In)),
                _ => null,
            };
            var result = next is null ? Input(f.In, i) : Own(owned, next);
            if (linear && next is not null)
                result = Color(SKColorFilter.CreateLinearToSrgbGamma(), result);
            if (f.Subregion is { } r)
                result = Own(owned, SKImageFilter.CreateCrop(new SKRect(r.X, r.Y, r.Right, r.Bottom), SKShaderTileMode.Decal, result));
            results[i] = result;
        }
        return results[^1];
    }

    private static readonly float[] Identity = [1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0];

    // feConvolveMatrix weighs the source with the kernel turned half way round (Filter Effects 1 §9.9); Skia weighs it
    // as it is, so the kernel goes in reversed. A kernel that does not fit its size, or a divisor of 0, is no filter.
    private static SKImageFilter? Convolution(Filter f, SKImageFilter? input)
    {
        if (f.Kernel is not { } kernel || f.KernelColumns <= 0 || f.KernelRows <= 0 || kernel.Count != f.KernelColumns * f.KernelRows || f.Divisor == 0
            || f.TargetX < 0 || f.TargetX >= f.KernelColumns || f.TargetY < 0 || f.TargetY >= f.KernelRows)
            return null;
        var reversed = kernel.Reverse().ToArray();
        var edges = f.EdgeMode switch { EdgeMode.Duplicate => SKShaderTileMode.Clamp, EdgeMode.Wrap => SKShaderTileMode.Repeat, _ => SKShaderTileMode.Decal };
        // Reversed, kernel entry (x, y) weighs the source at (x - targetX, y - targetY) from the pixel, as in the spec.
        var offset = new SKPointI(f.TargetX, f.TargetY);
        return SKImageFilter.CreateMatrixConvolution(new SKSizeI(f.KernelColumns, f.KernelRows), reversed, 1 / f.Divisor, f.Bias, offset, edges,
            !f.PreserveAlpha, input);
    }

    // The lighting primitives with their light source (Filter Effects 1 §9.4, §9.17 and the light source elements).
    private static SKImageFilter? Lighting(Filter f, SKImageFilter? input)
    {
        if (f.Light is not { } light)
            return null;
        var color = ToSkia(f.Color with { A = 1 });
        static SKPoint3 P(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);
        // A spot light's cone is the limiting cone angle in degrees; without one it lights the whole half space.
        var cone = light.ConeAngle ?? 90;
        return (f.Kind, light.Kind) switch
        {
            (FilterKind.DiffuseLighting, LightKind.Distant) => SKImageFilter.CreateDistantLitDiffuse(P(light.Direction), color, f.SurfaceScale, f.LightingConstant, input),
            (FilterKind.DiffuseLighting, LightKind.Point) => SKImageFilter.CreatePointLitDiffuse(P(light.Position), color, f.SurfaceScale, f.LightingConstant, input),
            (FilterKind.DiffuseLighting, _) => SKImageFilter.CreateSpotLitDiffuse(P(light.Position), P(light.Target), light.Exponent, cone, color, f.SurfaceScale,
                f.LightingConstant, input),
            (_, LightKind.Distant) => SKImageFilter.CreateDistantLitSpecular(P(light.Direction), color, f.SurfaceScale, f.LightingConstant, f.Shininess, input),
            (_, LightKind.Point) => SKImageFilter.CreatePointLitSpecular(P(light.Position), color, f.SurfaceScale, f.LightingConstant, f.Shininess, input),
            _ => SKImageFilter.CreateSpotLitSpecular(P(light.Position), P(light.Target), light.Exponent, cone, color, f.SurfaceScale,
                f.LightingConstant, f.Shininess, input),
        };
    }

    // Black with the input's alpha (SourceAlpha).
    private static readonly float[] AlphaOnly = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0];

    // A component transfer function as a table of 256 straight-alpha values
    // (https://drafts.csswg.org/filter-effects-1/#feComponentTransferElement).
    private static byte[] Table(IReadOnlyList<TransferFunction>? functions, int channel)
    {
        var f = functions is not null && channel < functions.Count ? functions[channel] : null;
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
            table[i] = (byte)MathF.Round(Math.Clamp(float.IsFinite(value) ? value : 0, 0, 1) * 255);
        }
        return table;

        static float Interpolate(IReadOnlyList<float> v, float c)
        {
            var k = Math.Min((int)(c * (v.Count - 1)), v.Count - 2);
            return v[k] + (c * (v.Count - 1) - k) * (v[k + 1] - v[k]);
        }
    }

    // Perlin noise everywhere (cropped to the subregion like any result); stitching tiles it at the subregion's size.
    private static SKImageFilter? Turbulence(Noise? noise, RectF? subregion, List<IDisposable> owned)
    {
        if (noise is null || !(noise.BaseFrequency.X >= 0 && noise.BaseFrequency.Y >= 0))
            return null;
        var tile = noise.Stitch && subregion is { } r ? new SKSizeI((int)MathF.Round(r.Width), (int)MathF.Round(r.Height)) : SKSizeI.Empty;
        var (fx, fy, octaves) = (noise.BaseFrequency.X, noise.BaseFrequency.Y, Math.Max(0, noise.Octaves));
        var shader = noise.Fractal
            ? SKShader.CreatePerlinNoiseFractalNoise(fx, fy, octaves, noise.Seed, tile)
            : SKShader.CreatePerlinNoiseTurbulence(fx, fy, octaves, noise.Seed, tile);
        return shader is null ? null : SKImageFilter.CreateShader(Own(owned, shader), false);
    }

    private static SKColorChannel ToSkia(ColorChannel channel) => channel switch
    {
        ColorChannel.R => SKColorChannel.R,
        ColorChannel.G => SKColorChannel.G,
        ColorChannel.B => SKColorChannel.B,
        _ => SKColorChannel.A,
    };

    public void PopLayer() => canvas.Restore();

    private static SKBlendMode ToSkia(BlendMode mode) => mode switch
    {
        BlendMode.Normal => SKBlendMode.SrcOver,
        BlendMode.PlusLighter => SKBlendMode.Plus,
        BlendMode.SourceIn => SKBlendMode.SrcIn,
        BlendMode.SourceOut => SKBlendMode.SrcOut,
        BlendMode.DestinationIn => SKBlendMode.DstIn,
        _ => Enum.Parse<SKBlendMode>(mode.ToString()),
    };

    private static SKPaint Fill(in Paint paint) => new()
    {
        IsAntialias = true, Style = SKPaintStyle.Fill, Color = paint.Gradient is null ? ToSkia(paint.Color) : SKColors.Black, BlendMode = ToSkia(paint.Blend),
        MaskFilter = paint.Blur > 0 ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal, paint.Blur) : null,
        Shader = paint.Gradient is { } gradient ? Shader(gradient) : null,
    };

    // Stops between which alpha changes come with extra stops interpolated premultiplied, so the plain sRGB
    // interpolation here matches css-color-4 §12.3.
    private static SKShader Shader(Gradient gradient)
    {
        var colors = gradient.Stops.Select(s => ToSkia(s.Color)).ToArray();
        var offsets = gradient.Stops.Select(s => Math.Clamp(s.Offset, 0, 1)).ToArray();
        var tile = gradient.Spread switch
        {
            GradientSpread.Repeat => SKShaderTileMode.Repeat,
            GradientSpread.Reflect => SKShaderTileMode.Mirror,
            _ => SKShaderTileMode.Clamp,
        };
        // Gradient space to canvas: the gradient's own transform after any the kind needs.
        var toCanvas = gradient.Transform is { } t ? ToSkia(t) : SKMatrix.Identity;
        switch (gradient.Kind)
        {
            case GradientKind.Linear:
                return SKShader.CreateLinearGradient(new SKPoint(gradient.Start.X, gradient.Start.Y), new SKPoint(gradient.End.X, gradient.End.Y),
                    colors, offsets, tile, toCanvas);
            case GradientKind.Radial:
            {
                // A circle of the horizontal radius, stretched vertically about the centre into the ellipse.
                var (c, r) = (gradient.Center, gradient.Radii);
                var matrix = SKMatrix.CreateScale(1, r.X > 0 ? r.Y / r.X : 1, c.X, c.Y).PostConcat(toCanvas);
                var radius = Math.Max(r.X, 0.001f);
                return gradient.Focus is { } f
                    ? SKShader.CreateTwoPointConicalGradient(new SKPoint(f.X, f.Y), Math.Max(gradient.FocusRadius, 0), new SKPoint(c.X, c.Y), radius,
                        colors, offsets, tile, matrix)
                    : SKShader.CreateRadialGradient(new SKPoint(c.X, c.Y), radius, colors, offsets, tile, matrix);
            }
            default:
            {
                // Sweeps start at 3 o'clock; CSS angles start at 12 o'clock.
                var c = gradient.Center;
                var matrix = SKMatrix.CreateRotationDegrees(gradient.StartAngle - 90, c.X, c.Y).PostConcat(toCanvas);
                var sweep = Math.Max(gradient.EndAngle - gradient.StartAngle, 0.001f);
                return SKShader.CreateSweepGradient(new SKPoint(c.X, c.Y), colors, offsets, tile, 0, sweep, matrix);
            }
        }
    }

    // Skia's matrices map column vectors, so the row-vector form's rows are its columns.
    private static SKMatrix ToSkia(in Matrix3x2 m) => new(m.M11, m.M21, m.M31, m.M12, m.M22, m.M32, 0, 0, 1);

    private static SKColor ToSkia(Rgba c) => new(ToByte(c.R), ToByte(c.G), ToByte(c.B), ToByte(c.A));

    private static byte ToByte(float v) => (byte)MathF.Round(Math.Clamp(v, 0, 1) * 255);

    private static SKRoundRect ToSkia(in RoundedRect rect)
    {
        var (r, c) = (rect.Rect, rect.Radii);
        var skRect = new SKRoundRect();
        skRect.SetRectRadii(new SKRect(r.X, r.Y, r.Right, r.Bottom),
            [new(c.TopLeft.X, c.TopLeft.Y), new(c.TopRight.X, c.TopRight.Y), new(c.BottomRight.X, c.BottomRight.Y), new(c.BottomLeft.X, c.BottomLeft.Y)]);
        return skRect;
    }

    private static SKPath ToSkia(PathData path, FillRule rule)
    {
        using var builder = new SKPathBuilder { FillType = rule == FillRule.EvenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding };
        foreach (var command in path.Commands)
        {
            switch (command.Verb)
            {
                case PathVerb.MoveTo:
                    builder.MoveTo(command.P1.X, command.P1.Y);
                    break;
                case PathVerb.LineTo:
                    builder.LineTo(command.P1.X, command.P1.Y);
                    break;
                case PathVerb.CubicTo:
                    builder.CubicTo(command.P1.X, command.P1.Y, command.P2.X, command.P2.Y, command.P3.X, command.P3.Y);
                    break;
                default:
                    builder.Close();
                    break;
            }
        }
        return builder.Detach();
    }
}
