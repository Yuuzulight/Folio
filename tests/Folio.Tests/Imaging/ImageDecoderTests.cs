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
