using System.Buffers.Binary;

namespace Folio.Imaging;

/// <summary>Folio's GIF decoder. It decodes the first frame, including interlacing and transparency.</summary>
internal static class GifDecoder
{
    public static bool CanDecode(ReadOnlySpan<byte> header) => header.Length >= 6
        && (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8));

    public static DecodedImage? Decode(ReadOnlySpan<byte> data, long maxPixels = ImageLimits.MaxPixels)
    {
        try { return DecodeCore(data, maxPixels); }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException)
        { return null; }
    }

    private static DecodedImage? DecodeCore(ReadOnlySpan<byte> data, long maxPixels)
    {
        if (!CanDecode(data) || data.Length < 13) return null;
        var p = 6;
        var width = U16(data, ref p);
        var height = U16(data, ref p);
        if (!ImageLimits.Allows(width, height, maxPixels)) return null;
        var packed = Byte(data, ref p);
        _ = Byte(data, ref p); // background colour index
        _ = Byte(data, ref p); // pixel aspect ratio
        var global = (packed & 0x80) != 0 ? ReadPalette(data, ref p, 1 << ((packed & 7) + 1)) : null;
        var transparent = -1;
        while (p < data.Length)
        {
            var marker = Byte(data, ref p);
            if (marker == 0x3B) break; // trailer, no frame
            if (marker == 0x21)
            {
                var label = Byte(data, ref p);
                if (label == 0xF9)
                {
                    if (Byte(data, ref p) != 4 || p + 4 > data.Length) throw new InvalidDataException();
                    var gce = Byte(data, ref p);
                    _ = U16(data, ref p); // delay
                    var index = Byte(data, ref p);
                    if ((gce & 1) != 0) transparent = index;
                    else transparent = -1;
                    if (Byte(data, ref p) != 0) throw new InvalidDataException();
                }
                else
                    SkipSubBlocks(data, ref p);
                continue;
            }
            if (marker != 0x2C) throw new InvalidDataException();

            var left = U16(data, ref p);
            var top = U16(data, ref p);
            var frameWidth = U16(data, ref p);
            var frameHeight = U16(data, ref p);
            var descriptor = Byte(data, ref p);
            if (frameWidth == 0 || frameHeight == 0 || left + frameWidth > width || top + frameHeight > height)
                throw new InvalidDataException();
            var palette = (descriptor & 0x80) != 0 ? ReadPalette(data, ref p, 1 << ((descriptor & 7) + 1)) : global;
            if (palette is null) throw new InvalidDataException();
            var indices = DecodePixels(data, ref p, frameWidth * frameHeight);
            var pixels = new byte[checked(width * height * 4)];
            // GIF's interlaced rows are presented in four passes.
            var row = 0;
            foreach (var (start, step) in (descriptor & 0x40) != 0
                         ? new[] { (0, 8), (4, 8), (2, 4), (1, 2) }
                         : new[] { (0, 1) })
                for (var y = start; y < frameHeight; y += step)
                {
                    for (var x = 0; x < frameWidth; x++)
                    {
                        var destination = ((top + y) * width + left + x) * 4;
                        var colourIndex = indices[row++];
                        if (colourIndex == transparent || colourIndex >= palette.Length)
                            continue;
                        var colour = palette[colourIndex];
                        pixels[destination] = colour.R;
                        pixels[destination + 1] = colour.G;
                        pixels[destination + 2] = colour.B;
                        pixels[destination + 3] = 255;
                    }
                }
            return new DecodedImage(width, height, pixels);
        }
        return null;
    }

    private static Rgb[] ReadPalette(ReadOnlySpan<byte> data, ref int p, int count)
    {
        if (count <= 0 || p > data.Length - count * 3) throw new InvalidDataException();
        var palette = new Rgb[count];
        for (var i = 0; i < count; i++) palette[i] = new(data[p++], data[p++], data[p++]);
        return palette;
    }

    private static byte[] DecodePixels(ReadOnlySpan<byte> data, ref int p, int expected)
    {
        var minimum = Byte(data, ref p);
        if (minimum is < 2 or > 8) throw new InvalidDataException();
        var compressed = ReadSubBlocks(data, ref p);
        var clear = 1 << minimum;
        var end = clear + 1;
        var table = new List<byte[]>(clear + 2);
        for (var i = 0; i < clear; i++) table.Add([(byte)i]);
        table.Add([]); table.Add([]);
        var result = new byte[expected];
        var written = 0;
        var bits = 0;
        var bitCount = 0;
        var bytePosition = 0;
        var codeSize = minimum + 1;
        var previous = -1;
        while (written < expected)
        {
            while (bitCount < codeSize)
            {
                if (bytePosition >= compressed.Length) throw new InvalidDataException();
                bits |= compressed[bytePosition++] << bitCount;
                bitCount += 8;
            }
            var code = bits & ((1 << codeSize) - 1);
            bits >>= codeSize;
            bitCount -= codeSize;
            if (code == clear)
            {
                table.RemoveRange(clear + 2, Math.Max(0, table.Count - clear - 2));
                codeSize = minimum + 1;
                previous = -1;
                continue;
            }
            if (code == end) break;
            byte[] entry;
            if (code < table.Count) entry = table[code];
            else if (code == table.Count && previous >= 0) entry = [.. table[previous], table[previous][0]];
            else throw new InvalidDataException();
            if (written > expected - entry.Length) throw new InvalidDataException();
            entry.CopyTo(result, written);
            written += entry.Length;
            // GIF LZW code tables are capped at 12 bits (4096 entries). A malformed stream must not
            // keep appending entries after the table is full; doing so can grow attacker-controlled
            // byte arrays far beyond the bounded decoded image.
            if (previous >= 0 && table.Count < 4096)
            {
                var prior = table[previous];
                table.Add([.. prior, entry[0]]);
                if (table.Count == (1 << codeSize) && codeSize < 12) codeSize++;
            }
            previous = code;
        }
        if (written != expected) throw new InvalidDataException();
        return result;
    }

    private static byte[] ReadSubBlocks(ReadOnlySpan<byte> data, ref int p)
    {
        var result = new List<byte>();
        while (true)
        {
            var size = Byte(data, ref p);
            if (size == 0) return [.. result];
            if (p > data.Length - size) throw new InvalidDataException();
            result.AddRange(data.Slice(p, size).ToArray());
            p += size;
        }
    }

    private static void SkipSubBlocks(ReadOnlySpan<byte> data, ref int p)
    {
        while (true)
        {
            var size = Byte(data, ref p);
            if (size == 0) return;
            if (p > data.Length - size) throw new InvalidDataException();
            p += size;
        }
    }

    private static byte Byte(ReadOnlySpan<byte> data, ref int p)
    {
        if ((uint)p >= (uint)data.Length) throw new InvalidDataException();
        return data[p++];
    }

    private static int U16(ReadOnlySpan<byte> data, ref int p)
    {
        if (p > data.Length - 2) throw new InvalidDataException();
        var value = BinaryPrimitives.ReadUInt16LittleEndian(data[p..]);
        p += 2;
        return value;
    }

    private readonly record struct Rgb(byte R, byte G, byte B);
}
