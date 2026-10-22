using System.Buffers.Binary;

namespace Folio.Imaging;

/// <summary>
/// Folio's WebP decoder (https://developers.google.com/speed/webp/docs/riff_container): the RIFF container with its
/// VP8X extended header, lossless VP8L images (<see cref="WebpLossless"/>) and, for an animated file, its first
/// frame on a transparent canvas. The VP8 lossy bitstream is not decoded yet (#578), so a lossy file gives no image.
/// The result is straight-alpha RGBA8.
/// </summary>
internal static class WebpDecoder
{
    private const int FlagAnimation = 0x02;

    public static bool CanDecode(ReadOnlySpan<byte> header) =>
        header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header.Slice(8, 4).SequenceEqual("WEBP"u8);

    public static DecodedImage? Decode(ReadOnlySpan<byte> data, long maxPixels = ImageLimits.MaxPixels)
    {
        try
        {
            return DecodeCore(data, maxPixels);
        }
        catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    private readonly record struct Chunk(string Tag, int Start, int Length);

    // The chunks between start and end, each padded to an even size. A chunk that runs past the end is an error.
    private static List<Chunk> Chunks(ReadOnlySpan<byte> data, int start, int end)
    {
        var chunks = new List<Chunk>();
        var position = start;
        while (position + 8 <= end)
        {
            var size = (long)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(position + 4, 4));
            if (position + 8 + size > end)
                throw new InvalidDataException("A WebP chunk runs past the end of the file.");
            chunks.Add(new Chunk(System.Text.Encoding.ASCII.GetString(data.Slice(position, 4)), position + 8, (int)size));
            position += 8 + (int)size + (int)(size & 1);
        }
        return chunks;
    }

    private static DecodedImage? DecodeCore(ReadOnlySpan<byte> data, long maxPixels)
    {
        if (!CanDecode(data))
            return null;
        var riffEnd = (int)Math.Min(data.Length, 8L + BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4)));
        var chunks = Chunks(data, 12, riffEnd);

        int canvasWidth = 0, canvasHeight = 0;
        var animated = false;
        Chunk? alpha = null;
        foreach (var chunk in chunks)
        {
            switch (chunk.Tag)
            {
                case "VP8X":
                    if (chunk.Length < 10)
                        throw new InvalidDataException("A truncated VP8X chunk.");
                    animated = (data[chunk.Start] & FlagAnimation) != 0;
                    canvasWidth = 1 + (data[chunk.Start + 4] | data[chunk.Start + 5] << 8 | data[chunk.Start + 6] << 16);
                    canvasHeight = 1 + (data[chunk.Start + 7] | data[chunk.Start + 8] << 8 | data[chunk.Start + 9] << 16);
                    if (!ImageLimits.Allows(canvasWidth, canvasHeight, maxPixels))
                        return null;
                    break;
                case "ALPH":
                    alpha = chunk;
                    break;
                case "VP8L":
                case "VP8 ":
                    if (animated)
                        break; // the frames carry their own image chunks
                    return DecodeImage(data, chunk, alpha, canvasWidth, canvasHeight, maxPixels);
                case "ANMF" when animated:
                    return DecodeFirstFrame(data, chunk, canvasWidth, canvasHeight, maxPixels);
            }
        }
        return null;
    }

    // The first frame of an animated file, drawn on a transparent canvas as a still image of the file would be.
    private static DecodedImage? DecodeFirstFrame(ReadOnlySpan<byte> data, Chunk frame, int canvasWidth, int canvasHeight, long maxPixels)
    {
        if (frame.Length < 16)
            throw new InvalidDataException("A truncated ANMF chunk.");
        var x = 2 * (data[frame.Start] | data[frame.Start + 1] << 8 | data[frame.Start + 2] << 16);
        var y = 2 * (data[frame.Start + 3] | data[frame.Start + 4] << 8 | data[frame.Start + 5] << 16);
        var width = 1 + (data[frame.Start + 6] | data[frame.Start + 7] << 8 | data[frame.Start + 8] << 16);
        var height = 1 + (data[frame.Start + 9] | data[frame.Start + 10] << 8 | data[frame.Start + 11] << 16);

        Chunk? alpha = null;
        DecodedImage? image = null;
        foreach (var chunk in Chunks(data, frame.Start + 16, frame.Start + frame.Length))
        {
            if (chunk.Tag == "ALPH")
                alpha = chunk;
            else if (chunk.Tag is "VP8L" or "VP8 ")
            {
                image = DecodeImage(data, chunk, alpha, width, height, maxPixels);
                break;
            }
        }
        if (image is null || image.Width != width || image.Height != height)
            return null;

        var pixels = new byte[checked(canvasWidth * canvasHeight * 4)];
        for (var row = 0; row < height && y + row < canvasHeight; row++)
        {
            var columns = Math.Min(width, canvasWidth - x);
            if (columns > 0)
                image.Pixels.AsSpan(row * width * 4, columns * 4).CopyTo(pixels.AsSpan(((y + row) * canvasWidth + x) * 4));
        }
        return new DecodedImage(canvasWidth, canvasHeight, pixels);
    }

    private static DecodedImage? DecodeImage(ReadOnlySpan<byte> data, Chunk chunk, Chunk? alpha, int expectedWidth, int expectedHeight, long maxPixels)
    {
        if (chunk.Tag == "VP8 ")
            return DecodeLossy(data.Slice(chunk.Start, chunk.Length), alpha is { } a ? data.Slice(a.Start, a.Length) : default, expectedWidth, expectedHeight, maxPixels);

        if (WebpLossless.Decode(data.Slice(chunk.Start, chunk.Length), maxPixels) is not var (width, height, argb))
            return null;
        // An extended file states its canvas size, and the image must fill it.
        if ((expectedWidth != 0 && expectedWidth != width) || (expectedHeight != 0 && expectedHeight != height))
            return null;

        var pixels = new byte[argb.Length * 4];
        for (var i = 0; i < argb.Length; i++)
        {
            var pixel = argb[i];
            pixels[i * 4] = (byte)(pixel >> 16);
            pixels[i * 4 + 1] = (byte)(pixel >> 8);
            pixels[i * 4 + 2] = (byte)pixel;
            pixels[i * 4 + 3] = (byte)(pixel >> 24);
        }
        return new DecodedImage(width, height, pixels);
    }

    // The VP8 key frame and ALPH chunk are decoded in #578; until then a lossy file gives no image rather than a wrong one.
    private static DecodedImage? DecodeLossy(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> alpha, int expectedWidth, int expectedHeight, long maxPixels) => null;
}
