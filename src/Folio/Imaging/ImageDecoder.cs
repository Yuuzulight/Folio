namespace Folio.Imaging;

/// <summary>
/// Folio-owned still-image decoding entry point shared by document images and
/// canvas/media fallbacks.  Keeping format selection here prevents individual
/// consumers from silently supporting different subsets of the image formats.
/// </summary>
public static class ImageDecoder
{
    /// <summary>Decode a bounded still image, or return <see langword="null"/> for an unsupported/corrupt format.</summary>
    public static DecodedImage? Decode(ReadOnlySpan<byte> data, int maxPixels = 16_777_216) =>
        PngDecoder.CanDecode(data) ? PngDecoder.Decode(data, maxPixels)
        : JpegDecoder.CanDecode(data) ? JpegDecoder.Decode(data, maxPixels)
        : GifDecoder.CanDecode(data) ? GifDecoder.Decode(data, maxPixels)
        : WebpDecoder.CanDecode(data) ? WebpDecoder.Decode(data, maxPixels)
        : BmpDecoder.CanDecode(data) ? BmpDecoder.Decode(data, maxPixels)
        : IcoDecoder.CanDecode(data) ? IcoDecoder.Decode(data, maxPixels)
        : null;
}
