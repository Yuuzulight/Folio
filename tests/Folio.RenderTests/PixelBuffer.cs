using SkiaSharp;

namespace Folio.RenderTests;

/// <summary>Premultiplied BGRA8 pixels, row-major without padding (the layout rendered images use).</summary>
public sealed class PixelBuffer
{
    public PixelBuffer(int width, int height, byte[]? pixels = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        pixels ??= new byte[width * height * 4];
        if (pixels.Length != width * height * 4)
            throw new ArgumentException($"Expected {width * height * 4} bytes for {width}x{height}, got {pixels.Length}.", nameof(pixels));

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    /// <summary>The four bytes (B, G, R, A) of one pixel.</summary>
    public Span<byte> Pixel(int x, int y) => Pixels.AsSpan((y * Width + x) * 4, 4);

    public static PixelBuffer LoadPng(string path)
    {
        var info = SKBitmap.DecodeBounds(path);
        if (info.Width == 0 || info.Height == 0)
            throw new InvalidDataException($"Not a readable image: {path}");

        using var bitmap = SKBitmap.Decode(path, new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul))
            ?? throw new InvalidDataException($"Not a readable image: {path}");
        return new PixelBuffer(bitmap.Width, bitmap.Height, bitmap.Bytes);
    }

    public void SavePng(string path)
    {
        using var image = SKImage.FromPixelCopy(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul), Pixels);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(path);
        data.SaveTo(file);
    }
}
