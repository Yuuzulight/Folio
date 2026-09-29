using System.Buffers.Binary;
using System.IO.Compression;

namespace Folio.Imaging;

/// <summary>
/// Folio's PNG decoder (https://www.w3.org/TR/png-3/): every colour type and bit depth, Adam7 interlacing and
/// <c>tRNS</c>. Other ancillary chunks (gamma and colour profiles included) are ignored, CRCs are not checked, and an
/// APNG shows its default image. Malformed, truncated or over-limit input gives null.
/// </summary>
internal static class PngDecoder
{
    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];

    // https://www.w3.org/TR/png-3/#8Interlace: (x0, y0, dx, dy) per pass.
    private static readonly (int X0, int Y0, int Dx, int Dy)[] Adam7 =
        [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)];

    public static bool CanDecode(ReadOnlySpan<byte> header) => header.StartsWith(Signature);

    public static DecodedImage? Decode(ReadOnlySpan<byte> data, long maxPixels = ImageLimits.MaxPixels)
    {
        try
        {
            return DecodeCore(data, maxPixels);
        }
        // IOException: truncated or corrupt zlib data.
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            return null;
        }
    }

    private static DecodedImage? DecodeCore(ReadOnlySpan<byte> data, long maxPixels)
    {
        if (!CanDecode(data))
            return null;

        int width = 0, height = 0, depth = 0, colorType = 0, interlace = 0;
        byte[]? palette = null;
        byte[]? transparency = null;
        using var compressed = new MemoryStream();
        var pos = Signature.Length;
        while (true)
        {
            if (data.Length - pos < 12)
                throw new InvalidDataException("Truncated PNG.");
            var length = BinaryPrimitives.ReadUInt32BigEndian(data[pos..]);
            if (length > data.Length - pos - 12)
                throw new InvalidDataException("Truncated PNG chunk.");
            var type = data.Slice(pos + 4, 4);
            var body = data.Slice(pos + 8, (int)length);
            pos += 12 + (int)length;

            if (width == 0 && !type.SequenceEqual("IHDR"u8))
                throw new InvalidDataException("IHDR is not first.");
            if (type.SequenceEqual("IHDR"u8))
            {
                if (width != 0 || body.Length != 13)
                    throw new InvalidDataException("Bad IHDR.");
                var w = BinaryPrimitives.ReadUInt32BigEndian(body);
                var h = BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                if (w > ImageLimits.MaxSide || h > ImageLimits.MaxSide || !ImageLimits.Allows((int)w, (int)h, maxPixels))
                    return null;
                (width, height, depth, colorType, interlace) = ((int)w, (int)h, body[8], body[9], body[12]);
                var validDepth = colorType switch
                {
                    0 => depth is 1 or 2 or 4 or 8 or 16,
                    3 => depth is 1 or 2 or 4 or 8,
                    2 or 4 or 6 => depth is 8 or 16,
                    _ => false,
                };
                if (!validDepth || body[10] != 0 || body[11] != 0 || interlace > 1)
                    throw new InvalidDataException("Unsupported IHDR.");
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                if (length % 3 != 0 || length > 256 * 3)
                    throw new InvalidDataException("Bad PLTE.");
                // Indices past the palette decode as transparent black.
                palette = new byte[256 * 4];
                for (var i = 0; i < length / 3; i++)
                {
                    body.Slice(i * 3, 3).CopyTo(palette.AsSpan(i * 4));
                    palette[i * 4 + 3] = 255;
                }
            }
            else if (type.SequenceEqual("tRNS"u8))
            {
                transparency = body.ToArray();
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                compressed.Write(body);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }
        }

        if (colorType == 3)
        {
            if (palette is null)
                throw new InvalidDataException("Missing PLTE.");
            for (var i = 0; transparency is not null && i < Math.Min(transparency.Length, 256); i++)
                palette[i * 4 + 3] = transparency[i];
        }

        var channels = colorType switch { 0 or 3 => 1, 2 => 3, 4 => 2, _ => 4 };
        var bitsPerPixel = channels * depth;
        var passes = interlace == 1 ? Adam7 : [(0, 0, 1, 1)];
        long rawSize = 0;
        foreach (var (x0, y0, dx, dy) in passes)
        {
            var passWidth = (width - x0 + dx - 1) / dx;
            var passHeight = (height - y0 + dy - 1) / dy;
            if (passWidth > 0 && passHeight > 0)
                rawSize += passHeight * (1 + ((long)passWidth * bitsPerPixel + 7) / 8);
        }
        // Deflate expands at most about 1032:1, so shorter data is truncated: refuse it before allocating.
        if (rawSize > compressed.Length * 1032 + 1024)
            throw new InvalidDataException("Truncated image data.");

        var raw = new byte[rawSize];
        compressed.Position = 0;
        using (var zlib = new ZLibStream(compressed, CompressionMode.Decompress))
            zlib.ReadExactly(raw);

        var key = colorType is 0 or 2 && transparency?.Length >= channels * 2 ? transparency : null;
        var pixels = new byte[width * height * 4];
        var bytesPerPixel = Math.Max(1, bitsPerPixel / 8);
        var offset = 0;
        foreach (var (x0, y0, dx, dy) in passes)
        {
            var passWidth = (width - x0 + dx - 1) / dx;
            var passHeight = (height - y0 + dy - 1) / dy;
            if (passWidth == 0 || passHeight == 0)
                continue;

            var rowBytes = (passWidth * bitsPerPixel + 7) / 8;
            ReadOnlySpan<byte> previous = new byte[rowBytes];
            for (var y = 0; y < passHeight; y++, offset += 1 + rowBytes)
            {
                var row = raw.AsSpan(offset + 1, rowBytes);
                Unfilter(raw[offset], row, previous, bytesPerPixel);
                previous = row;

                for (var x = 0; x < passWidth; x++)
                {
                    var target = pixels.AsSpan(((y0 + y * dy) * width + x0 + x * dx) * 4, 4);
                    var first = x * channels;
                    switch (colorType)
                    {
                        case 3:
                            palette.AsSpan(Sample(row, first, depth) * 4, 4).CopyTo(target);
                            break;
                        case 0 or 4:
                            target[0] = target[1] = target[2] = To8(Sample(row, first, depth), depth);
                            target[3] = colorType == 4 ? To8(Sample(row, first + 1, depth), depth) : (byte)255;
                            break;
                        default:
                            for (var c = 0; c < 3; c++)
                                target[c] = To8(Sample(row, first + c, depth), depth);
                            target[3] = colorType == 6 ? To8(Sample(row, first + 3, depth), depth) : (byte)255;
                            break;
                    }

                    if (key is not null)
                    {
                        var match = true;
                        for (var c = 0; c < channels; c++)
                            match &= Sample(row, first + c, depth) == BinaryPrimitives.ReadUInt16BigEndian(key.AsSpan(c * 2));
                        if (match)
                            target[3] = 0;
                    }
                }
            }
        }
        return new DecodedImage(width, height, pixels);
    }

    // https://www.w3.org/TR/png-3/#9Filters
    private static void Unfilter(byte filter, Span<byte> row, ReadOnlySpan<byte> previous, int bpp)
    {
        switch (filter)
        {
            case 0:
                break;
            case 1:
                for (var i = bpp; i < row.Length; i++)
                    row[i] += row[i - bpp];
                break;
            case 2:
                for (var i = 0; i < row.Length; i++)
                    row[i] += previous[i];
                break;
            case 3:
                for (var i = 0; i < row.Length; i++)
                    row[i] += (byte)(((i >= bpp ? row[i - bpp] : 0) + previous[i]) >> 1);
                break;
            case 4:
                for (var i = 0; i < row.Length; i++)
                {
                    int a = i >= bpp ? row[i - bpp] : 0, b = previous[i], c = i >= bpp ? previous[i - bpp] : 0;
                    int pa = Math.Abs(b - c), pb = Math.Abs(a - c), pc = Math.Abs(a + b - 2 * c);
                    row[i] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
                }
                break;
            default:
                throw new InvalidDataException("Bad filter type.");
        }
    }

    /// <summary>The index-th sample of a row at its full bit depth.</summary>
    private static int Sample(ReadOnlySpan<byte> row, int index, int depth) => depth switch
    {
        8 => row[index],
        16 => BinaryPrimitives.ReadUInt16BigEndian(row[(index * 2)..]),
        _ => (row[index * depth / 8] >> (8 - depth - index * depth % 8)) & ((1 << depth) - 1),
    };

    // 16-bit samples keep their high byte; low depths scale to the full 0-255 range.
    private static byte To8(int sample, int depth) => depth switch
    {
        8 => (byte)sample,
        16 => (byte)(sample >> 8),
        _ => (byte)(sample * 255 / ((1 << depth) - 1)),
    };
}
