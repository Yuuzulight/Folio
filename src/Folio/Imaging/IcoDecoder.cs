using System.Buffers.Binary;

namespace Folio.Imaging;

/// <summary>Bounded decoder for ICO files, selecting the largest usable image entry.</summary>
internal static class IcoDecoder
{
    public static bool CanDecode(ReadOnlySpan<byte> header) => header.Length >= 4
        && BinaryPrimitives.ReadUInt16LittleEndian(header) == 0
        && BinaryPrimitives.ReadUInt16LittleEndian(header[2..]) == 1;

    public static DecodedImage? Decode(ReadOnlySpan<byte> data, long maxPixels = ImageLimits.MaxPixels)
    {
        try { return DecodeCore(data, maxPixels); }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException)
        { return null; }
    }

    private static DecodedImage? DecodeCore(ReadOnlySpan<byte> data, long maxPixels)
    {
        if (!CanDecode(data) || data.Length < 6) return null;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        if (count == 0 || data.Length < 6 + count * 16) return null;
        var candidates = new List<(int Entry, long Area, int Bits)>();
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + i * 16;
            var width = data[entry] == 0 ? 256 : data[entry];
            var height = data[entry + 1] == 0 ? 256 : data[entry + 1];
            var bits = BinaryPrimitives.ReadUInt16LittleEndian(data[(entry + 6)..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data[(entry + 8)..]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(data[(entry + 12)..]);
            if (size == 0 || offset > data.Length || size > data.Length - offset) continue;
            var area = (long)width * height;
            candidates.Add((entry, area, bits));
        }
        foreach (var candidate in candidates.OrderByDescending(item => item.Area).ThenByDescending(item => item.Bits))
        {
            var imageSize = BinaryPrimitives.ReadUInt32LittleEndian(data[(candidate.Entry + 8)..]);
            var imageOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[(candidate.Entry + 12)..]);
            var image = data.Slice(checked((int)imageOffset), checked((int)imageSize));
            var decoded = PngDecoder.CanDecode(image) ? PngDecoder.Decode(image, maxPixels) : DecodeDib(image, maxPixels);
            if (decoded is not null)
                return decoded;
        }
        return null;
    }

    private static DecodedImage? DecodeDib(ReadOnlySpan<byte> image, long maxPixels)
    {
        if (image.Length < 40) return null;
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(image);
        if (headerSize < 40 || headerSize > image.Length) return null;
        var width = BinaryPrimitives.ReadInt32LittleEndian(image[4..]);
        var combinedHeight = BinaryPrimitives.ReadInt32LittleEndian(image[8..]);
        var planes = BinaryPrimitives.ReadUInt16LittleEndian(image[12..]);
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(image[14..]);
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(image[16..]);
        if (width <= 0 || combinedHeight <= 0 || combinedHeight % 2 != 0 || planes != 1 || compression != 0 || bits is not (16 or 24 or 32)) return null;
        var height = combinedHeight / 2;
        if (!ImageLimits.Allows(width, height, maxPixels)) return null;
        var xorRow = checked(((long)width * bits + 31) / 32 * 4);
        var xorBytes = checked(xorRow * height);
        var maskRow = checked(((long)width + 31) / 32 * 4);
        var maskOffset = checked((long)headerSize + xorBytes);
        if (headerSize + xorBytes + maskRow * height > image.Length) return null;
        var pixels = new byte[checked(width * height * 4)];
        var anyAlpha = false;
        for (var y = 0; y < height; y++)
        {
            var sourceRow = checked((int)(headerSize + y * xorRow));
            var destinationY = height - 1 - y;
            for (var x = 0; x < width; x++)
            {
                var source = sourceRow + x * (bits / 8);
                var destination = (destinationY * width + x) * 4;
                if (bits == 16)
                {
                    var value = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(source, 2));
                    pixels[destination] = (byte)(((value >> 10) & 0x1F) * 255 / 31);
                    pixels[destination + 1] = (byte)(((value >> 5) & 0x1F) * 255 / 31);
                    pixels[destination + 2] = (byte)((value & 0x1F) * 255 / 31);
                    pixels[destination + 3] = 255;
                }
                else
                {
                    pixels[destination] = image[source + 2];
                    pixels[destination + 1] = image[source + 1];
                    pixels[destination + 2] = image[source];
                    pixels[destination + 3] = bits == 32 ? image[source + 3] : (byte)255;
                }
                anyAlpha |= pixels[destination + 3] != 0;
            }
        }
        if (bits == 32 && !anyAlpha)
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        for (var y = 0; y < height; y++)
        {
            var maskRowOffset = checked((int)(maskOffset + y * maskRow));
            var destinationY = height - 1 - y;
            for (var x = 0; x < width; x++)
                if ((image[maskRowOffset + (x >> 3)] & (0x80 >> (x & 7))) != 0)
                    pixels[(destinationY * width + x) * 4 + 3] = 0;
        }
        return new DecodedImage(width, height, pixels);
    }
}
