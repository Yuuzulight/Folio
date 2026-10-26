namespace Folio.Imaging;

/// <summary>Decodes one or more image formats into pixels.</summary>
/// <remarks>
/// Swap-out interface, sketched in docs/dependencies.md.
/// </remarks>
public interface IImageDecoder
{
    bool CanDecode(ReadOnlySpan<byte> header);
    IImageHandle? Decode(ReadOnlySpan<byte> data, long maxPixels = ImageLimits.MaxPixels);
}
