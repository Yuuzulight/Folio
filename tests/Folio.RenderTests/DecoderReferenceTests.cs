using System.Buffers.Binary;
using Folio.Imaging;
using SkiaSharp;

namespace Folio.RenderTests;

/// <summary>Folio-owned image decoders against SkiaSharp's codecs, over corpus and generated inputs.</summary>
public class DecoderReferenceTests
{
    private static readonly string Folder = Path.Combine(RepoPaths.Tests, "images");

    public static TheoryData<string> Images =>
        new(Directory.GetFiles(Folder).Where(f => f.EndsWith(".png") || f.EndsWith(".jpg")).Select(f => Path.GetFileName(f)));

    [Theory]
    [MemberData(nameof(Images))]
    public void MatchesSkiaSharp(string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(Folder, name));
        var png = name.EndsWith(".png");
        var actual = png ? PngDecoder.Decode(bytes) : JpegDecoder.Decode(bytes);
        Assert.NotNull(actual);

        var size = SKBitmap.DecodeBounds(bytes);
        using var expected = SKBitmap.Decode(bytes, new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));

        var worst = 0;
        var reference = expected.Bytes;
        for (var i = 0; i < reference.Length; i++)
            worst = Math.Max(worst, Math.Abs(reference[i] - actual.Pixels[i]));

        // PNG is lossless. JPEG decoders may differ by rounding in the inverse DCT, upsampling and colour conversion.
        Assert.InRange(worst, 0, png ? 0 : 3);
    }

    [Fact]
    public void GifFirstFrameMatchesSkiaSharp()
    {
        var bytes = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==");
        var actual = GifDecoder.Decode(bytes);
        Assert.NotNull(actual);
        using var expected = SKBitmap.Decode(bytes, new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        Assert.Equal(expected.Bytes, actual.Pixels);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void GeneratedGifVariantsMatchSkiaSharp(bool interlaced, bool transparent)
    {
        var bytes = BuildGif(interlaced, transparent);
        var actual = GifDecoder.Decode(bytes);
        Assert.NotNull(actual);
        using var expected = SKBitmap.Decode(bytes, new SKImageInfo(4, 4, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        Assert.NotNull(expected);
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        Assert.Equal(expected.Bytes, actual.Pixels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BmpMatchesSkiaSharp(bool topDown)
    {
        var bytes = BuildBmp(topDown);
        var actual = BmpDecoder.Decode(bytes);
        Assert.NotNull(actual);
        using var expected = SKBitmap.Decode(bytes, new SKImageInfo(2, 2, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        Assert.Equal(expected.Bytes, actual.Pixels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SixteenBitBmpMatchesSkiaSharp(bool bitfields)
    {
        var bytes = BuildBmp16(bitfields);
        var actual = BmpDecoder.Decode(bytes);
        Assert.NotNull(actual);
        using var expected = SKBitmap.Decode(bytes, new SKImageInfo(2, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        Assert.Equal(expected.Bytes, actual.Pixels);
    }

    [Fact]
    public void EmbeddedPngIcoMatchesSkiaSharp()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var bytes = new byte[22 + png.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 1);
        bytes[6] = 1; bytes[7] = 1; // width and height
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), 1); // colour planes
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14), (uint)png.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(18), 22);
        png.CopyTo(bytes.AsSpan(22));

        var actual = IcoDecoder.Decode(bytes);
        Assert.NotNull(actual);
        using var expected = SKBitmap.Decode(bytes, new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        Assert.NotNull(expected);
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        Assert.Equal(expected.Bytes, actual.Pixels);
    }

    private static byte[] BuildBmp(bool topDown)
    {
        var rowBytes = 8;
        var result = new byte[54 + rowBytes * 2];
        result[0] = (byte)'B'; result[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(2), (uint)result.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(10), 54);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(18), 2);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(22), topDown ? -2 : 2);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(28), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(34), 16);
        // Two rows of BGR pixels, padded to four bytes. The decoder reverses these for bottom-up BMPs.
        var pixels = new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 0, 0, 255, 255, 255, 255, 0, 0 };
        pixels.CopyTo(result.AsSpan(54));
        return result;
    }

    private static byte[] BuildGif(bool interlaced, bool transparent)
    {
        const int width = 4;
        const int height = 4;
        var result = new List<byte>(128);
        result.AddRange("GIF89a"u8.ToArray());
        result.AddRange([4, 0, 4, 0, 0x80, 0, 0]); // global two-colour table
        result.AddRange([0, 0, 0, 255, 32, 64]);
        if (transparent) result.AddRange([0x21, 0xF9, 0x04, 0x01, 0, 0, 0, 0]);
        result.Add(0x2C);
        result.AddRange([0, 0, 0, 0, 4, 0, 4, 0, (byte)(interlaced ? 0x40 : 0)]);

        // A clear code before each literal keeps this generated corpus valid without relying on
        // a particular encoder's dictionary growth strategy.
        result.Add(2);
        var indices = new List<int>(width * height);
        if (interlaced)
        {
            foreach (var (start, step) in new[] { (0, 8), (4, 8), (2, 4), (1, 2) })
                for (var y = start; y < height; y += step)
                    for (var x = 0; x < width; x++) indices.Add((x + y) & 1);
        }
        else
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++) indices.Add((x + y) & 1);

        var codes = new List<int>(indices.Count * 2 + 2) { 4 };
        foreach (var index in indices) { codes.Add(index); codes.Add(4); }
        codes.RemoveAt(codes.Count - 1);
        codes.Add(5);
        var packed = new List<byte>();
        var bits = 0;
        var count = 0;
        foreach (var code in codes)
        {
            bits |= code << count;
            count += 3;
            while (count >= 8)
            {
                packed.Add((byte)bits);
                bits >>= 8;
                count -= 8;
            }
        }
        if (count > 0) packed.Add((byte)bits);
        result.Add((byte)packed.Count);
        result.AddRange(packed);
        result.Add(0);
        result.Add(0x3B);
        return [.. result];
    }

    private static byte[] BuildBmp16(bool bitfields)
    {
        const int header = 54;
        const int masks = 12;
        var pixelOffset = header + (bitfields ? masks : 0);
        var result = new byte[pixelOffset + 4];
        result[0] = (byte)'B'; result[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(2), (uint)result.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(10), (uint)pixelOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(18), 2);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(28), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(30), bitfields ? 3u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(34), 4);
        if (bitfields)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(54), 0xF800);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(58), 0x07E0);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(62), 0x001F);
        }
        var pixels = bitfields ? new byte[] { 0, 0xF8, 0xE0, 0x07 } : new byte[] { 0, 0x7C, 0xE0, 3 };
        pixels.CopyTo(result.AsSpan(pixelOffset));
        return result;
    }
}
