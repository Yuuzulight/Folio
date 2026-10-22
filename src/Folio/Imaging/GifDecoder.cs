using System.Buffers.Binary;

namespace Folio.Imaging;

/// <summary>
/// Folio's GIF decoder (https://www.w3.org/Graphics/GIF/spec-gif89a.txt): GIF87a and GIF89a formats,
/// global and local colour tables, graphic control extension with transparency, interlacing,
/// and LZW decompressor. For animated GIFs, the first frame is returned.
/// </summary>
internal static class GifDecoder
{
    private static ReadOnlySpan<byte> Gif87a => "GIF87a"u8;
    private static ReadOnlySpan<byte> Gif89a => "GIF89a"u8;

    public static bool CanDecode(ReadOnlySpan<byte> header) =>
        header.StartsWith(Gif87a) || header.StartsWith(Gif89a);

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
        if (!CanDecode(data) || data.Length < 13)
            return null;

        var width = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
        if (!ImageLimits.Allows(width, height, maxPixels))
            return null;

        var packed = data[10];
        var hasGct = (packed & 0x80) != 0;
        var gctSize = 1 << ((packed & 0x07) + 1);

        var pos = 13;
        byte[]? gct = null;
        if (hasGct)
        {
            var gctByteCount = 3 * gctSize;
            if (data.Length - pos < gctByteCount)
                throw new InvalidDataException("Truncated global colour table.");
            gct = data.Slice(pos, gctByteCount).ToArray();
            pos += gctByteCount;
        }

        int? transparentIndex = null;

        while (pos < data.Length)
        {
            var blockType = data[pos++];
            if (blockType == 0x3B) // Trailer
                break;

            if (blockType == 0x21) // Extension block
            {
                if (pos >= data.Length) throw new InvalidDataException("Truncated extension.");
                var label = data[pos++];
                if (label == 0xF9) // Graphic Control Extension
                {
                    if (pos >= data.Length) throw new InvalidDataException("Truncated GCE.");
                    var blockSize = data[pos++];
                    if (blockSize >= 4 && pos + blockSize <= data.Length)
                    {
                        var flags = data[pos];
                        var hasTransparent = (flags & 0x01) != 0;
                        if (hasTransparent)
                            transparentIndex = data[pos + 3];
                    }
                    pos += blockSize;
                    // Skip any terminator
                    while (pos < data.Length && data[pos] != 0)
                        pos += 1 + data[pos];
                    if (pos < data.Length && data[pos] == 0)
                        pos++;
                }
                else
                {
                    // Skip data sub-blocks
                    while (pos < data.Length && data[pos] != 0)
                        pos += 1 + data[pos];
                    if (pos < data.Length && data[pos] == 0)
                        pos++;
                }
            }
            else if (blockType == 0x2C) // Image Descriptor
            {
                if (data.Length - pos < 9)
                    throw new InvalidDataException("Truncated image descriptor.");

                var imgLeft = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
                var imgTop = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 2)..]);
                var imgWidth = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 4)..]);
                var imgHeight = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 6)..]);
                var imgPacked = data[pos + 8];
                pos += 9;

                var hasLct = (imgPacked & 0x80) != 0;
                var isInterlaced = (imgPacked & 0x40) != 0;
                var lctSize = 1 << ((imgPacked & 0x07) + 1);

                byte[]? activePalette = gct;
                if (hasLct)
                {
                    var lctByteCount = 3 * lctSize;
                    if (data.Length - pos < lctByteCount)
                        throw new InvalidDataException("Truncated local colour table.");
                    activePalette = data.Slice(pos, lctByteCount).ToArray();
                    pos += lctByteCount;
                }

                if (activePalette is null)
                    throw new InvalidDataException("No active colour table for image.");

                if (pos >= data.Length)
                    throw new InvalidDataException("Missing LZW minimum code size.");
                int minCodeSize = data[pos++];

                // Read all sub-blocks of LZW compressed data
                using var lzwStream = new MemoryStream();
                while (pos < data.Length && data[pos] != 0)
                {
                    int subBlockLen = data[pos++];
                    if (pos + subBlockLen > data.Length)
                        throw new InvalidDataException("Truncated LZW sub-block.");
                    lzwStream.Write(data.Slice(pos, subBlockLen));
                    pos += subBlockLen;
                }
                if (pos < data.Length && data[pos] == 0)
                    pos++;

                var pixelCount = imgWidth * imgHeight;
                var decompressedIndices = DecompressLzw(lzwStream.ToArray(), minCodeSize, pixelCount);

                var framePixels = new byte[width * height * 4];
                RenderIndicesToRgba(decompressedIndices, framePixels, width, height, imgLeft, imgTop, imgWidth, imgHeight, isInterlaced, activePalette, transparentIndex);

                return new DecodedImage(width, height, framePixels);
            }
        }

        return null;
    }

    private static byte[] DecompressLzw(byte[] compressed, int minCodeSize, int expectedPixels)
    {
        var output = new byte[expectedPixels];
        var outPos = 0;

        int clearCode = 1 << minCodeSize;
        int endCode = clearCode + 1;
        int codeSize = minCodeSize + 1;
        int maxCode = 1 << codeSize;

        var prefix = new int[4096];
        var suffix = new byte[4096];
        var stack = new byte[4096];
        int stackPos = 0;

        void ResetTable()
        {
            codeSize = minCodeSize + 1;
            maxCode = 1 << codeSize;
            for (var i = 0; i < clearCode; i++)
            {
                prefix[i] = -1;
                suffix[i] = (byte)i;
            }
        }

        ResetTable();
        int available = endCode + 1;
        int oldCode = -1;

        int bitPos = 0;
        var totalBits = compressed.Length * 8;

        int ReadBits(int count)
        {
            if (bitPos + count > totalBits) return endCode;
            int value = 0;
            for (var i = 0; i < count; i++)
            {
                var byteIndex = (bitPos + i) / 8;
                var bitIndex = (bitPos + i) % 8;
                if ((compressed[byteIndex] & (1 << bitIndex)) != 0)
                    value |= (1 << i);
            }
            bitPos += count;
            return value;
        }

        while (outPos < expectedPixels)
        {
            var code = ReadBits(codeSize);
            if (code == clearCode)
            {
                ResetTable();
                available = endCode + 1;
                oldCode = -1;
                continue;
            }
            if (code == endCode)
                break;

            int inCode = code;
            if (code >= available)
            {
                stack[stackPos++] = (byte)oldCode;
                code = oldCode;
            }

            while (code >= clearCode)
            {
                if (code >= prefix.Length || stackPos >= stack.Length) break;
                stack[stackPos++] = suffix[code];
                code = prefix[code];
            }

            var firstChar = suffix[code];
            stack[stackPos++] = firstChar;

            if (inCode >= available && stackPos > 0)
                stack[0] = firstChar;

            while (stackPos > 0 && outPos < expectedPixels)
            {
                output[outPos++] = stack[--stackPos];
            }

            if (available < 4096 && oldCode != -1)
            {
                prefix[available] = oldCode;
                suffix[available] = firstChar;
                available++;
                if (available >= maxCode && codeSize < 12)
                {
                    codeSize++;
                    maxCode = 1 << codeSize;
                }
            }
            oldCode = inCode;
        }

        return output;
    }

    private static void RenderIndicesToRgba(
        byte[] indices, byte[] outputRgba,
        int canvasWidth, int canvasHeight,
        int imgLeft, int imgTop, int imgWidth, int imgHeight,
        bool isInterlaced, byte[] palette, int? transparentIndex)
    {
        var srcPos = 0;
        int[] passStarts = [0, 4, 2, 1];
        int[] passSteps = [8, 8, 4, 2];

        int passCount = isInterlaced ? 4 : 1;

        for (var pass = 0; pass < passCount; pass++)
        {
            var yStart = isInterlaced ? passStarts[pass] : 0;
            var yStep = isInterlaced ? passSteps[pass] : 1;

            for (var y = yStart; y < imgHeight; y += yStep)
            {
                var dstY = imgTop + y;
                if (dstY >= canvasHeight)
                {
                    srcPos += imgWidth;
                    continue;
                }

                for (var x = 0; x < imgWidth; x++)
                {
                    if (srcPos >= indices.Length) break;
                    var index = indices[srcPos++];
                    var dstX = imgLeft + x;
                    if (dstX >= canvasWidth) continue;

                    var dstOffset = (dstY * canvasWidth + dstX) * 4;
                    if (transparentIndex.HasValue && index == transparentIndex.Value)
                    {
                        // Transparent pixel: leave RGBA as 0,0,0,0
                        continue;
                    }

                    var palOffset = index * 3;
                    if (palOffset + 2 < palette.Length)
                    {
                        outputRgba[dstOffset] = palette[palOffset];     // R
                        outputRgba[dstOffset + 1] = palette[palOffset + 1]; // G
                        outputRgba[dstOffset + 2] = palette[palOffset + 2]; // B
                        outputRgba[dstOffset + 3] = 255;                // A
                    }
                }
            }
        }
    }
}
