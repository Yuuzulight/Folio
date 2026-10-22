using System.Buffers.Binary;

namespace Folio.Imaging;

/// <summary>
/// Folio's WebP decoder (https://developers.google.com/speed/webp/docs/riff_container):
/// RIFF container, VP8 lossy, VP8L lossless, and VP8X extended chunks.
/// The container is parsed; the VP8 and VP8L bitstreams are not decoded yet (#577, #578), so no image is returned.
/// </summary>
internal static class WebpDecoder
{
    private static ReadOnlySpan<byte> RiffHeader => "RIFF"u8;
    private static ReadOnlySpan<byte> WebpSignature => "WEBP"u8;
    private static ReadOnlySpan<byte> Vp8Lossy => "VP8 "u8;
    private static ReadOnlySpan<byte> Vp8Lossless => "VP8L"u8;
    private static ReadOnlySpan<byte> Vp8Extended => "VP8X"u8;
    private static ReadOnlySpan<byte> AlphaChunk => "ALPH"u8;

    public static bool CanDecode(ReadOnlySpan<byte> header) =>
        header.Length >= 12 &&
        header.StartsWith(RiffHeader) &&
        header.Slice(8, 4).SequenceEqual(WebpSignature);

    public static DecodedImage? Decode(ReadOnlySpan<byte> data, long maxPixels = ImageLimits.MaxPixels)
    {
        try
        {
            return DecodeCore(data, maxPixels);
        }
        catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException)
        {
            return null;
        }
    }

    private static DecodedImage? DecodeCore(ReadOnlySpan<byte> data, long maxPixels)
    {
        if (!CanDecode(data))
            return null;

        var pos = 12;
        int canvasWidth = 0;
        int canvasHeight = 0;
        byte[]? alphaData = null;

        while (pos + 8 <= data.Length)
        {
            var chunkTag = data.Slice(pos, 4);
            var chunkSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos + 4, 4));
            pos += 8;

            if (chunkSize < 0 || pos + chunkSize > data.Length)
                throw new InvalidDataException("Truncated WebP chunk.");

            var chunkData = data.Slice(pos, chunkSize);
            var paddedSize = chunkSize + (chunkSize & 1); // RIFF chunks are padded to 2-byte boundaries
            pos += paddedSize;

            if (chunkTag.SequenceEqual(Vp8Extended))
            {
                if (chunkData.Length < 10)
                    throw new InvalidDataException("Truncated VP8X chunk.");

                canvasWidth = 1 + (chunkData[4] | (chunkData[5] << 8) | (chunkData[6] << 16));
                canvasHeight = 1 + (chunkData[7] | (chunkData[8] << 8) | (chunkData[9] << 16));
                if (!ImageLimits.Allows(canvasWidth, canvasHeight, maxPixels))
                    return null;
            }
            else if (chunkTag.SequenceEqual(AlphaChunk))
            {
                alphaData = chunkData.ToArray();
            }
            else if (chunkTag.SequenceEqual(Vp8Lossless))
            {
                return DecodeVp8L(chunkData, canvasWidth, canvasHeight, maxPixels);
            }
            else if (chunkTag.SequenceEqual(Vp8Lossy))
            {
                return DecodeVp8(chunkData, canvasWidth, canvasHeight, alphaData, maxPixels);
            }
        }

        return null;
    }

    // The VP8L (lossless, #577) and VP8 (lossy, #578) bitstreams are not decoded yet. Until they are, a WebP file
    // gives no image at all, which the loader reports, instead of pixels that are not the picture.
    private static DecodedImage? DecodeVp8L(ReadOnlySpan<byte> data, int containerWidth, int containerHeight, long maxPixels) => null;

    private static DecodedImage? DecodeVp8(ReadOnlySpan<byte> data, int containerWidth, int containerHeight, byte[]? alpha, long maxPixels) => null;
}
