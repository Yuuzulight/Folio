using System.Numerics;

namespace Folio.Painting.Software;

/// <summary>
/// A filter primitive's result: premultiplied colour (0 to 1) as a function of the pixel. The colour filters that follow
/// one add no rounding of their own, as in Skia, which keeps them pending until a primitive draws its inputs; only
/// <see cref="Resolve"/> rounds to bytes.
/// </summary>
internal sealed class Frame
{
    private readonly Func<int, int, Vector4> _at;
    private PixelSurface? _pixels;

    private Frame(int width, int height, Func<int, int, Vector4> at, PixelSurface? pixels)
    {
        Width = width;
        Height = height;
        _at = at;
        _pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public static Frame Of(PixelSurface surface)
    {
        var (data, width) = (surface.Data, surface.Width);
        return new Frame(surface.Width, surface.Height, (x, y) =>
        {
            var i = (y * width + x) * 4;
            return new Vector4(data[i], data[i + 1], data[i + 2], data[i + 3]) / 255f;
        }, surface);
    }

    public static Frame Generated(int width, int height, Func<int, int, Vector4> at) => new(width, height, at, null);

    public Vector4 At(int x, int y) => _at(x, y);

    /// <summary>This frame with a colour function applied to every pixel, unrounded.</summary>
    public Frame Then(Func<Vector4, Vector4> function)
    {
        var at = _at;
        return new Frame(Width, Height, (x, y) => function(at(x, y)), null);
    }

    /// <summary>This frame with what lies outside the pixel rectangle cleared.</summary>
    public Frame Cropped(int left, int top, int right, int bottom)
    {
        var at = _at;
        return new Frame(Width, Height, (x, y) => x >= left && x < right && y >= top && y < bottom ? at(x, y) : Vector4.Zero, null);
    }

    /// <summary>The pixels rounded to bytes.</summary>
    public PixelSurface Resolve()
    {
        if (_pixels is not null)
            return _pixels;
        var o = new PixelSurface(Width, Height);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var v = _at(x, y);
                var i = (y * Width + x) * 4;
                o.Data[i] = To8(v.X);
                o.Data[i + 1] = To8(v.Y);
                o.Data[i + 2] = To8(v.Z);
                o.Data[i + 3] = To8(v.W);
            }
        }
        return _pixels = o;
    }

    private static byte To8(float v) => v <= 0 ? (byte)0 : v >= 1 ? (byte)255 : (byte)(v * 255 + 0.5f);
}
