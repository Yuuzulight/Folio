using System.Buffers.Binary;
using System.Numerics;

namespace Folio.Imaging;

/// <summary>Bounded decoder for the uncompressed BMP variants used by web artifacts.</summary>
internal static class BmpDecoder
{
    public static bool CanDecode(ReadOnlySpan<byte> header) => header.Length >= 2 && header[0] == (byte)'B' && header[1] == (byte)'M';

    public static DecodedImage? Decode(ReadOnlySpan<byte> data, long maxPixels = ImageLimits.MaxPixels)
    {
        try { return DecodeCore(data, maxPixels); }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException)
        { return null; }
    }

    private static DecodedImage? DecodeCore(ReadOnlySpan<byte> data, long maxPixels)
    {
        if (!CanDecode(data) || data.Length < 54) return null;
        var pixelOffset = Read32(data, 10);
        var dibSize = Read32(data, 14);
        if (dibSize < 40 || dibSize > data.Length - 14) return null;
        var width = ReadI32(data, 18);
        var rawHeight = ReadI32(data, 22);
        var planes = Read16(data, 26);
        var bits = Read16(data, 28);
        var compression = Read32(data, 30);
        if (width <= 0 || rawHeight == 0 || rawHeight == int.MinValue || planes != 1) return null;
        var topDown = rawHeight < 0;
        var height = Math.Abs(rawHeight);
        if (!ImageLimits.Allows(width, height, maxPixels)) return null;
        if (bits is not (8 or 16 or 24 or 32) || compression is not (0 or 3) || pixelOffset > data.Length) return null;
        if (compression == 3 && bits is not (16 or 32)) return null;

        long paletteEnd = 0;
        var palette = bits == 8 ? ReadPalette(data, dibSize, Read32(data, 46), out paletteEnd) : null;
        if (bits == 8 && (palette is null || pixelOffset < paletteEnd)) return null;
        var (redMask, greenMask, blueMask, alphaMask) = ReadMasks(data, dibSize, bits, compression, pixelOffset);
        var rowBytes = checked(((long)width * bits + 31) / 32 * 4);
        var totalBytes = checked(rowBytes * height);
        if (pixelOffset + totalBytes > data.Length) return null;
        var pixels = new byte[checked(width * height * 4)];
        for (var sourceY = 0; sourceY < height; sourceY++)
        {
            var destinationY = topDown ? sourceY : height - 1 - sourceY;
            var row = checked((int)(pixelOffset + sourceY * rowBytes));
            for (var x = 0; x < width; x++)
            {
                var source = row + (bits == 8 ? x : x * (bits / 8));
                var destination = checked((destinationY * width + x) * 4);
                if (bits == 8)
                {
                    var index = data[source];
                    if (index >= palette!.Length) return null;
                    var colour = palette[index];
                    pixels[destination] = colour.R; pixels[destination + 1] = colour.G;
                    pixels[destination + 2] = colour.B; pixels[destination + 3] = 255;
                }
                else if (bits is 16 or 32)
                {
                    var value = bits == 16
                        ? BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(source, 2))
                        : BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(source, 4));
                    pixels[destination] = Extract(value, redMask);
                    pixels[destination + 1] = Extract(value, greenMask);
                    pixels[destination + 2] = Extract(value, blueMask);
                    // BI_RGB 32-bit BMP stores the fourth byte as reserved, not
                    // alpha.  Treating it as transparency makes the common
                    // opaque BGRA form disappear when that reserved byte is 0.
                    pixels[destination + 3] = bits == 32 && compression == 0
                        ? (byte)255
                        : alphaMask == 0 ? (byte)255 : Extract(value, alphaMask);
                }
                else
                {
                    pixels[destination] = data[source + 2];
                    pixels[destination + 1] = data[source + 1];
                    pixels[destination + 2] = data[source];
                    pixels[destination + 3] = 255;
                }
            }
        }
        return new DecodedImage(width, height, pixels);
    }

    private static BmpRgb[]? ReadPalette(ReadOnlySpan<byte> data, uint dibSize, uint declared, out long end)
    {
        var count = declared == 0 ? 256u : declared;
        if (count is 0 or > 256) { end = 0; return null; }
        var start = 14L + dibSize;
        end = checked(start + count * 4L);
        if (end > data.Length) return null;
        var palette = new BmpRgb[count];
        for (var i = 0; i < count; i++)
        {
            var p = checked((int)(start + i * 4));
            palette[i] = new(data[p + 2], data[p + 1], data[p]);
        }
        return palette;
    }

    private static (uint Red, uint Green, uint Blue, uint Alpha) ReadMasks(ReadOnlySpan<byte> data, uint dibSize,
        ushort bits, uint compression, uint pixelOffset)
    {
        if (bits == 16 && compression == 0) return (0x7C00, 0x03E0, 0x001F, 0);
        if (bits == 32 && compression == 0) return (0x00FF0000, 0x0000FF00, 0x000000FF, 0);
        if (compression != 3) return (0, 0, 0, 0);
        var offset = dibSize >= 52 ? 14L + 40 : checked(14L + dibSize);
        if (offset + 12 > data.Length || (dibSize < 52 && offset + 12 > pixelOffset)) throw new InvalidDataException();
        var red = Read32(data, checked((int)offset));
        var green = Read32(data, checked((int)offset + 4));
        var blue = Read32(data, checked((int)offset + 8));
        var alpha = offset + 16 <= data.Length && (dibSize >= 56 || offset + 16 <= pixelOffset) ? Read32(data, checked((int)offset + 12)) : 0;
        if (red == 0 || green == 0 || blue == 0 || (red & green) != 0 || (red & blue) != 0 || (green & blue) != 0)
            throw new InvalidDataException();
        return (red, green, blue, alpha);
    }

    private static byte Extract(uint value, uint mask)
    {
        if (mask == 0) return 0;
        var shift = BitOperations.TrailingZeroCount(mask);
        var max = mask >> shift;
        return (byte)(((value & mask) >> shift) * 255u / max);
    }

    private static ushort Read16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
    private static uint Read32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
    private static int ReadI32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));

    private readonly record struct BmpRgb(byte R, byte G, byte B);
}
