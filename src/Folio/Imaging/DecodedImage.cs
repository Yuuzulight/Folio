namespace Folio.Imaging;

/// <summary>Decoded pixels: RGBA8 with straight (not premultiplied) alpha, row-major without padding.</summary>
internal sealed record DecodedImage(int Width, int Height, byte[] Pixels) : IImageHandle
{
    ReadOnlyMemory<byte> IImageHandle.Pixels => Pixels;
}

/// <summary>
/// Image size limits from docs/study/16-resources-and-security.md, checked from the header before any pixel
/// memory is allocated.
/// </summary>
// ponytail: constants until the hosting ResourceLimits carries image limits; the decoders already take maxPixels.
internal static class ImageLimits
{
    public const int MaxSide = 16384;
    public const long MaxPixels = 64L * 1024 * 1024;

    public static bool Allows(int width, int height, long maxPixels) =>
        width is > 0 and <= MaxSide && height is > 0 and <= MaxSide && (long)width * height <= maxPixels;
}
