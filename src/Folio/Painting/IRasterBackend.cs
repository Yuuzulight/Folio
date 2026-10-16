using Folio.Imaging;

namespace Folio.Painting;

/// <summary>Creates raster surfaces, fonts and images for a drawing backend.</summary>
/// <remarks>
/// Swap-out interface, sketched in docs/dependencies.md. Members are added together with the value
/// types they use when the first implementation lands.
/// </remarks>
public interface IRasterBackend
{
    /// <summary>
    /// A transparent bitmap of the given size in pixels that can be drawn on and read back, or null when the backend
    /// cannot make one that size. Canvas elements draw into these.
    /// </summary>
    IRasterSurface? CreateSurface(int width, int height);

    /// <summary>Shapes the text that <c>SimpleShaper</c> cannot (complex scripts, marks); null leaves it all to <c>SimpleShaper</c>.</summary>
    Typography.ITextShaper? TextShaper => null;
}

/// <summary>A bitmap a script draws on (a canvas element's) and reads back.</summary>
public interface IRasterSurface : IDisposable
{
    int Width { get; }

    int Height { get; }

    /// <summary>Draws onto the bitmap; one unit is one pixel.</summary>
    ICanvas Canvas { get; }

    /// <summary>
    /// The pixels of a rectangle as RGBA8 with straight (not premultiplied) alpha, row-major without padding. What lies
    /// outside the bitmap reads as transparent black.
    /// </summary>
    byte[] ReadPixels(int x, int y, int width, int height);

    /// <summary>
    /// Replaces the pixels of a rectangle with RGBA8 straight-alpha ones, without blending; what falls outside the bitmap
    /// is ignored.
    /// </summary>
    void WritePixels(int x, int y, int width, int height, ReadOnlySpan<byte> rgba);

    /// <summary>
    /// The bitmap encoded as an image file of the type (image/png, image/jpeg or image/webp), <paramref name="quality"/>
    /// from 0 to 1 for the lossy ones; null when the backend cannot make that type.
    /// </summary>
    byte[]? Encode(string mimeType, double quality);

    /// <summary>The whole bitmap as an image that does not change when the surface is drawn on afterwards.</summary>
    IImageHandle Snapshot();
}
