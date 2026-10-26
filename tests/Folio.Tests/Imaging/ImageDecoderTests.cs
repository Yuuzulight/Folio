using System.Buffers.Binary;
using Folio.Imaging;

namespace Folio.Tests.Imaging;

// Pixel output is checked against SkiaSharp's codecs in Folio.RenderTests (DecoderReferenceTests); these tests cover
// hostile input.
public class ImageDecoderTests
{
    private static readonly byte[] OnePixelGif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==");

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
    public void DecodesTheFirstGifFrameWithoutSkia()
    {
        var decoded = GifDecoder.Decode(OnePixelGif);

        Assert.NotNull(decoded);
        Assert.Equal(1, decoded.Width);
        Assert.Equal(1, decoded.Height);
        Assert.Equal([255, 255, 255, 255], decoded.Pixels);
    }

    [Fact]
    public void DecodesBottomUp24BitBmpIntoRgba()
    {
        var bmp = Bmp(2, 2, 24, topDown: false, pixels: [
            255, 0, 0, 0, 255, 0, // bottom row: blue, green (BGR)
            0, 0, 255, 255, 255, 255]); // top row: red, white
        var decoded = BmpDecoder.Decode(bmp);
        Assert.NotNull(decoded);
        Assert.Equal([255, 0, 0, 255, 255, 255, 255, 255, 0, 0, 255, 255, 0, 255, 0, 255], decoded.Pixels);
    }

    [Fact]
    public void DecodesTopDown32BitBmpAndPreservesAlpha()
    {
        var bmp = Bmp(1, 2, 32, topDown: true, pixels: [10, 20, 30, 40, 50, 60, 70, 80]);
        var decoded = BmpDecoder.Decode(bmp);
        Assert.NotNull(decoded);
        Assert.Equal([30, 20, 10, 40, 70, 60, 50, 80], decoded.Pixels);
    }

    [Fact]
    public void DecodesRgb555Bmp()
    {
        var bmp = Bmp(1, 1, 16, topDown: true, pixels: [0, 0x7C, 0, 0]);
        var decoded = BmpDecoder.Decode(bmp);
        Assert.NotNull(decoded);
        Assert.Equal([255, 0, 0, 255], decoded.Pixels);
    }

    [Fact]
    public void DecodesBitfieldRgb565Bmp()
    {
        var bmp = BitfieldBmp(0xF800, 0xF800, 0x07E0); // red in the RGB565 source word
        var decoded = BmpDecoder.Decode(bmp);
        Assert.NotNull(decoded);
        Assert.Equal([255, 0, 0, 255], decoded.Pixels);
    }

    [Fact]
    public void DecodesPalettedBmpAndRejectsTruncationAndOversize()
    {
        var bmp = Bmp(2, 1, 8, topDown: true, pixels: [0, 1], palette: [(B: (byte)0, G: (byte)0, R: (byte)255), (B: (byte)255, G: (byte)0, R: (byte)0)]);
        var decoded = BmpDecoder.Decode(bmp);
        Assert.NotNull(decoded);
        Assert.Equal([255, 0, 0, 255, 0, 0, 255, 255], decoded.Pixels);
        Assert.Null(BmpDecoder.Decode(bmp.AsSpan(0, bmp.Length - 1)));
        Assert.Null(BmpDecoder.Decode(bmp, maxPixels: 1));
    }

    [Fact]
    public void Decodes32BitIcoDibAndAppliesTheAndMask()
    {
        var ico = Ico32(1, 1, b: 30, g: 20, r: 10, a: 0, masked: true);
        var decoded = IcoDecoder.Decode(ico);
        Assert.NotNull(decoded);
        Assert.Equal([10, 20, 30, 0], decoded.Pixels);

        ico = Ico32(1, 1, b: 30, g: 20, r: 10, a: 0, masked: false);
        decoded = IcoDecoder.Decode(ico);
        Assert.NotNull(decoded);
        Assert.Equal([10, 20, 30, 255], decoded.Pixels);
        Assert.Null(IcoDecoder.Decode(ico.AsSpan(0, ico.Length - 1)));
    }

    [Fact]
    public void Decodes16BitIcoDib()
    {
        var ico = Ico16(0x7C00); // RGB555 red
        var decoded = IcoDecoder.Decode(ico);
        Assert.NotNull(decoded);
        Assert.Equal([255, 0, 0, 255], decoded.Pixels);
    }

    [Fact]
    public void DecodesAnIcoWithAnEmbeddedPngEntry()
    {
        var png = Image("rgb-8.png");
        var ico = new byte[22 + png.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(4), 1);
        ico[6] = 13; ico[7] = 11;
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(10), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(12), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(14), (uint)png.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(18), 22);
        png.CopyTo(ico.AsSpan(22));
        var decoded = IcoDecoder.Decode(ico);
        Assert.NotNull(decoded);
        Assert.Equal(13, decoded.Width);
        Assert.Equal(11, decoded.Height);
    }

    [Fact]
    public void FallsBackToTheNextDecodableIcoEntry()
    {
        var valid = Ico32(1, 1, b: 30, g: 20, r: 10, a: 255, masked: false);
        var validPayload = valid.AsSpan(22).ToArray();
        const int headerSize = 6 + 2 * 16;
        var firstOffset = headerSize;
        var secondOffset = firstOffset + 48;
        var ico = new byte[secondOffset + validPayload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(4), 2);
        // The largest entry is structurally valid but has an invalid DIB header.
        ico[6] = 2; ico[7] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(12), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(14), 48);
        BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(18), (uint)firstOffset);
        // The smaller entry is valid and should be selected after the first fails.
        ico[22] = 1; ico[23] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(28), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(30), (uint)validPayload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(34), (uint)secondOffset);
        validPayload.CopyTo(ico.AsSpan(secondOffset));

        var decoded = IcoDecoder.Decode(ico);
        Assert.NotNull(decoded);
        Assert.Equal([10, 20, 30, 255], decoded.Pixels);
    }

    private static byte[] Bmp(int width, int height, ushort bits, bool topDown, byte[] pixels, params (byte B, byte G, byte R)[] palette)
    {
        var rowBytes = ((width * bits + 31) / 32) * 4;
        var paletteBytes = palette.Length * 4;
        var offset = 54 + paletteBytes;
        var result = new byte[offset + rowBytes * height];
        result[0] = (byte)'B'; result[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(2), (uint)result.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(10), (uint)offset);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(22), topDown ? -height : height);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(28), bits);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(34), (uint)(rowBytes * height));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(46), (uint)palette.Length);
        for (var i = 0; i < palette.Length; i++)
        {
            var p = 54 + i * 4; result[p] = palette[i].B; result[p + 1] = palette[i].G; result[p + 2] = palette[i].R;
        }
        for (var y = 0; y < height; y++)
            pixels.AsSpan(y * width * (bits / 8), width * (bits / 8)).CopyTo(result.AsSpan(offset + y * rowBytes));
        return result;
    }

    private static byte[] BitfieldBmp(ushort pixel, uint redMask, uint greenMask, uint blueMask = 0x001F)
    {
        const int offset = 66;
        var result = new byte[offset + 4];
        result[0] = (byte)'B'; result[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(2), (uint)result.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(10), offset);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(18), 1);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(28), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(30), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(34), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(54), redMask);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(58), greenMask);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(62), blueMask);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(offset), pixel);
        return result;
    }

    private static byte[] Ico32(int width, int height, byte b, byte g, byte r, byte a, bool masked)
    {
        var result = new byte[22 + 40 + 4 + 4];
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4), 1);
        result[6] = (byte)width; result[7] = (byte)height;
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(10), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(12), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(14), 48);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(18), 22);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(22), 40);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(26), width);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(30), height * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(34), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(36), 32);
        result[62] = b; result[63] = g; result[64] = r; result[65] = a;
        if (masked) result[66] = 0x80;
        return result;
    }

    private static byte[] Ico16(ushort pixel)
    {
        var result = new byte[22 + 40 + 4 + 4];
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4), 1);
        result[6] = 1; result[7] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(10), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(12), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(14), 48);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(18), 22);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(22), 40);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(26), 1);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(30), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(34), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(36), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(62), pixel);
        return result;
    }

    [Fact]
    public void RefusesGifFramesOverTheSizeLimitBeforeAllocatingPixels()
    {
        var gif = OnePixelGif.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(gif.AsSpan(6), 2);
        Assert.Null(GifDecoder.Decode(gif, maxPixels: 1));
    }

    [Fact]
    public void GifTransparencyLeavesTransparentPixelsUnpainted()
    {
        // Insert a GIF89a graphics-control extension before the one-pixel image; index 0 is transparent.
        var gif = OnePixelGif[..19].Concat(new byte[] { 0x21, 0xF9, 0x04, 0x01, 0, 0, 1, 0 }).Concat(OnePixelGif[19..]).ToArray();
        var decoded = GifDecoder.Decode(gif);
        Assert.NotNull(decoded);
        Assert.Equal([0, 0, 0, 0], decoded.Pixels);
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
