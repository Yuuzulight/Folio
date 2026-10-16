using Folio.Imaging;
using Folio.Painting;
using SkiaSharp;

namespace Folio.Skia;

/// <summary>Makes the bitmaps canvas elements draw on with SkiaSharp.</summary>
public sealed class SkiaRasterBackend : IRasterBackend
{
    // One shaper for all the canvases made here: it holds no state of its own.
    private readonly HarfBuzzShaper _shaper = new();

    public Folio.Typography.ITextShaper? TextShaper => _shaper;

    public IRasterSurface? CreateSurface(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return null;
        var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        return surface is null ? null : new SkiaRasterSurface(surface);
    }
}

internal sealed class SkiaRasterSurface : IRasterSurface
{
    private readonly SKSurface _surface;

    public SkiaRasterSurface(SKSurface surface)
    {
        _surface = surface;
        (Width, Height) = (surface.Canvas.DeviceClipBounds.Width, surface.Canvas.DeviceClipBounds.Height);
        Canvas = new SkiaCanvas(surface.Canvas);
    }

    public int Width { get; }

    public int Height { get; }

    public ICanvas Canvas { get; }

    public byte[] ReadPixels(int x, int y, int width, int height)
    {
        var pixels = new byte[Math.Max(0, width) * Math.Max(0, height) * 4];
        if (pixels.Length == 0)
            return pixels;
        // Skia copies only the part that overlaps the surface and converts premultiplied pixels to straight alpha.
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var (sx, sy) = (Math.Max(0, x), Math.Max(0, y));
        var (ex, ey) = (Math.Min(Width, x + width), Math.Min(Height, y + height));
        if (ex <= sx || ey <= sy)
            return pixels;
        var inside = new SKImageInfo(ex - sx, ey - sy, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var part = new byte[inside.BytesSize];
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(part, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            if (!_surface.ReadPixels(inside, pin.AddrOfPinnedObject(), inside.RowBytes, sx, sy))
                return pixels;
        }
        finally
        {
            pin.Free();
        }
        for (var row = 0; row < inside.Height; row++)
            Buffer.BlockCopy(part, row * inside.RowBytes, pixels, ((sy - y + row) * width + (sx - x)) * 4, inside.RowBytes);
        return pixels;
    }

    public void WritePixels(int x, int y, int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
            return;
        using var image = SKImage.FromPixelCopy(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul), rgba);
        var canvas = _surface.Canvas;
        canvas.Save();
        canvas.ResetMatrix();
        canvas.ClipRect(SKRect.Create(0, 0, Width, Height));
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        canvas.DrawImage(image, x, y, paint);
        canvas.Restore();
    }

    public byte[]? Encode(string mimeType, double quality)
    {
        var format = mimeType switch
        {
            "image/png" => SKEncodedImageFormat.Png,
            "image/jpeg" => SKEncodedImageFormat.Jpeg,
            "image/webp" => SKEncodedImageFormat.Webp,
            _ => (SKEncodedImageFormat?)null,
        };
        if (format is not { } kind)
            return null;
        using var image = _surface.Snapshot();
        using var data = image.Encode(kind, (int)Math.Round(Math.Clamp(quality, 0, 1) * 100));
        return data?.ToArray();
    }

    public IImageHandle Snapshot() => new SnapshotImage(Width, Height, ReadPixels(0, 0, Width, Height));

    public void Dispose() => _surface.Dispose();

    private sealed class SnapshotImage(int width, int height, byte[] data) : IImageHandle
    {
        public int Width { get; } = width;

        public int Height { get; } = height;

        ReadOnlyMemory<byte> IImageHandle.Pixels => data;
    }
}
