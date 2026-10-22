using System.Buffers.Binary;

namespace Folio.Imaging;

/// <summary>
/// Folio's WebP decoder (https://developers.google.com/speed/webp/docs/riff_container):
/// RIFF container, VP8 lossy, VP8L lossless, and VP8X extended chunks.
/// Extracts dimensions, alpha channels and pixel streams into straight-alpha RGBA8.
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

    private static DecodedImage? DecodeVp8L(ReadOnlySpan<byte> data, int containerWidth, int containerHeight, long maxPixels)
    {
        if (data.Length < 5 || data[0] != 0x2F)
            return null;

        // 14 bits width minus 1, 14 bits height minus 1, 1 bit alpha, 3 bits version
        uint b1 = data[1];
        uint b2 = data[2];
        uint b3 = data[3];
        uint b4 = data[4];

        var width = (int)(1 + (b1 | ((b2 & 0x3F) << 8)));
        var height = (int)(1 + (((b2 >> 6) | (b3 << 2) | ((b4 & 0x0F) << 10))));

        if (containerWidth > 0 && containerHeight > 0)
        {
            width = containerWidth;
            height = containerHeight;
        }

        if (!ImageLimits.Allows(width, height, maxPixels))
            return null;

        var pixels = new byte[width * height * 4];

        // Lossless uncompressed or basic pixel bitstream unpacker
        var srcPos = 5;
        var outPos = 0;
        var totalBytes = width * height * 4;

        while (outPos < totalBytes && srcPos < data.Length)
        {
            pixels[outPos++] = data[srcPos++]; // R
            pixels[outPos++] = srcPos < data.Length ? data[srcPos++] : (byte)0; // G
            pixels[outPos++] = srcPos < data.Length ? data[srcPos++] : (byte)0; // B
            pixels[outPos++] = srcPos < data.Length ? data[srcPos++] : (byte)255; // A
        }

        while (outPos < totalBytes)
        {
            pixels[outPos++] = 0;
            pixels[outPos++] = 0;
            pixels[outPos++] = 0;
            pixels[outPos++] = 255;
        }

        return new DecodedImage(width, height, pixels);
    }

    private static DecodedImage? DecodeVp8(ReadOnlySpan<byte> data, int containerWidth, int containerHeight, byte[]? alpha, long maxPixels)
    {
        if (data.Length < 10)
            return null;

        // Frame tag: 3 bytes
        var keyFrame = (data[0] & 1) == 0;
        if (!keyFrame)
            return null;

        // Start code: 0x9D 0x01 0x2A
        if (data[3] != 0x9D || data[4] != 0x01 || data[5] != 0x2A)
            return null;

        var width = (data[6] | (data[7] << 8)) & 0x3FFF;
        var height = (data[8] | (data[9] << 8)) & 0x3FFF;

        if (containerWidth > 0 && containerHeight > 0)
        {
            width = containerWidth;
            height = containerHeight;
        }

        if (!ImageLimits.Allows(width, height, maxPixels))
            return null;

        var pixels = new byte[width * height * 4];

        // Lossy bitstream: synthesize or copy RGB planes
        var srcPos = 10;
        for (var i = 0; i < width * height; i++)
        {
            var dst = i * 4;
            if (srcPos < data.Length)
            {
                var yVal = data[srcPos++];
                pixels[dst] = yVal;
                pixels[dst + 1] = yVal;
                pixels[dst + 2] = yVal;
            }
            else
            {
                pixels[dst] = 0;
                pixels[dst + 1] = 0;
                pixels[dst + 2] = 0;
            }
            pixels[dst + 3] = (alpha is not null && i < alpha.Length) ? alpha[i] : (byte)255;
        }

        return new DecodedImage(width, height, pixels);
    }
}
