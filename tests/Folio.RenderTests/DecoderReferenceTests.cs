using Folio.Imaging;
using SkiaSharp;

namespace Folio.RenderTests;

/// <summary>Folio's own PNG and JPEG decoders against SkiaSharp's codecs, over the images in tests/images.</summary>
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
}
