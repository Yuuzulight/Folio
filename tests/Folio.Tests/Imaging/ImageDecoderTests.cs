using System.Buffers.Binary;
using Folio.Imaging;

namespace Folio.Tests.Imaging;

// Pixel output is checked against SkiaSharp's codecs in Folio.RenderTests (DecoderReferenceTests); these tests cover
// hostile input.
public class ImageDecoderTests
{
    private static byte[] Image(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "images", name));

    private static int FrameHeader(byte[] jpeg) => jpeg.AsSpan().IndexOf([(byte)0xFF, (byte)0xC0]);

    [Fact]
    public void RefusesAWebpOverTheSizeLimitsFromTheHeader()
    {
        Assert.NotNull(WebpDecoder.Decode(Image("lossless-one-pixel.webp"), maxPixels: 1));
        Assert.Null(WebpDecoder.Decode(Image("lossless-two-colours-37x29.webp"), maxPixels: 37 * 29 - 1));
        Assert.NotNull(WebpDecoder.Decode(Image("lossless-two-colours-37x29.webp"), maxPixels: 37 * 29));
    }

    [Theory]
    [InlineData("lossless-sixteen-colours-40x30.webp")]
    [InlineData("lossless-blocks-96x64.webp")]
    [InlineData("lossless-alpha-gradient-50x40.webp")]
    [InlineData("lossless-animated-64x48.webp")]
    public void NoTruncatedWebpThrowsAndAnyImageItGivesIsTheDeclaredSize(string name)
    {
        var webp = Image(name);
        var full = WebpDecoder.Decode(webp)!;
        for (var length = 0; length < webp.Length; length++)
        {
            var decoded = WebpDecoder.Decode(webp.AsSpan(0, length));
            if (decoded is not null)
                Assert.Equal((full.Width, full.Height), (decoded.Width, decoded.Height));
        }
    }

    [Fact]
    public void ALosslessWebpWithABitFlippedIsNeverLargerThanItsHeaderSays()
    {
        var original = Image("lossless-test-pattern-300x200.webp");
        var random = new Random(5);
        for (var run = 0; run < 300; run++)
        {
            var webp = (byte[])original.Clone();
            webp[random.Next(20, webp.Length)] ^= (byte)(1 << random.Next(8));
            var decoded = WebpDecoder.Decode(webp);
            if (decoded is not null)
                Assert.Equal(decoded.Width * decoded.Height * 4, decoded.Pixels.Length);
        }
    }

    [Fact]
    public void RefusesAGifFrameOverTheSizeLimit()
    {
        var gif = Image("one-pixel.gif");
        var descriptor = 13 + 3 * (1 << ((gif[10] & 7) + 1));
        Assert.Equal(0x2C, gif[descriptor]);
        BinaryPrimitives.WriteUInt16LittleEndian(gif.AsSpan(descriptor + 5), 65535);
        BinaryPrimitives.WriteUInt16LittleEndian(gif.AsSpan(descriptor + 7), 65535);
        Assert.Null(GifDecoder.Decode(gif));
    }

    [Fact]
    public void NoTruncatedGifThrowsAndAnyImageItGivesIsTheCanvasSize()
    {
        var gif = Image("interlaced-local-13x11.gif");
        for (var length = 0; length < gif.Length; length++)
        {
            var decoded = GifDecoder.Decode(gif.AsSpan(0, length));
            if (decoded is not null)
                Assert.Equal(13 * 11 * 4, decoded.Pixels.Length);
        }
    }

    [Fact]
    public void RefusesPngsOverTheSizeLimitsFromTheHeader()
    {
        var png = Image("rgb-8.png");
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), ImageLimits.MaxSide + 1);
        Assert.Null(PngDecoder.Decode(png));

        Assert.NotNull(PngDecoder.Decode(Image("rgb-8.png"), maxPixels: 13 * 11));
        Assert.Null(PngDecoder.Decode(Image("rgb-8.png"), maxPixels: 13 * 11 - 1));
    }

    [Fact]
    public void RefusesJpegsOverTheSizeLimitsFromTheHeader()
    {
        var jpeg = Image("baseline-420.jpg");
        BinaryPrimitives.WriteUInt16BigEndian(jpeg.AsSpan(FrameHeader(jpeg) + 7), ImageLimits.MaxSide + 1);
        Assert.Null(JpegDecoder.Decode(jpeg));

        Assert.NotNull(JpegDecoder.Decode(Image("baseline-420.jpg"), maxPixels: 37 * 29));
        Assert.Null(JpegDecoder.Decode(Image("baseline-420.jpg"), maxPixels: 37 * 29 - 1));
    }

    [Fact]
    public void RefusesArithmeticCodedJpegs()
    {
        var jpeg = Image("baseline-444.jpg");
        jpeg[FrameHeader(jpeg) + 1] = 0xC9;
        Assert.Null(JpegDecoder.Decode(jpeg));
    }

    [Fact]
    public void TruncatedSequentialJpegLeavesTheBlocksItDoesNotReachGrey()
    {
        var bytes = Image("baseline-gray.jpg");
        var full = JpegDecoder.Decode(bytes)!;
        var cut = JpegDecoder.Decode(bytes.AsSpan(0, bytes.Length - 150))!;

        Assert.Equal(full.Pixels[0], cut.Pixels[0]);
        Assert.Equal([128, 128, 128, 255], cut.Pixels[^4..]);
    }

    [Theory]
    [InlineData("interlaced-rgba-16.png")]
    [InlineData("palette-8-trns.png")]
    [InlineData("progressive-420-restart.jpg")]
    [InlineData("baseline-422.jpg")]
    public void TruncatedInputNeverThrows(string name)
    {
        var bytes = Image(name);
        for (var length = 0; length < bytes.Length; length++)
        {
            var decoded = name.EndsWith(".png") ? PngDecoder.Decode(bytes.AsSpan(0, length)) : JpegDecoder.Decode(bytes.AsSpan(0, length));
            if (name.EndsWith(".png"))
                Assert.Null(decoded);
        }
    }
}
