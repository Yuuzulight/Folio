using System.Numerics;
using Folio.Imaging;
using Folio.Painting;
using Folio.Painting.Software;

namespace Folio.Tests.Painting;

// A scaled image's per-column and per-row sampling must give the bytes of the per-pixel general path.
public class ScaledImageDrawTests
{
    private static DecodedImage Source(int width, int height, bool translucent)
    {
        var pixels = new byte[width * height * 4];
        new Random(width * 31 + height).NextBytes(pixels);
        for (var i = 3; i < pixels.Length; i += 4)
            pixels[i] = translucent ? pixels[i] : (byte)255;
        return new DecodedImage(width, height, pixels);
    }

    private static byte[] Draw(DecodedImage image, bool general, RectF destination, Matrix3x2 transform, bool clip = false, bool rounded = false)
    {
        ImageShader.DisableSeparableSampling = general;
        try
        {
            var surface = new SoftwareRasterSurface(80, 60);
            var canvas = surface.Canvas;
            canvas.FillRoundedRect(new RoundedRect(new RectF(0, 0, 80, 60), default), new Paint(new Rgba(.2f, .6f, .9f, 1)));
            canvas.Save();
            if (clip)
                canvas.ClipRoundedRect(new RoundedRect(new RectF(5, 4, 60, 45), rounded ? new CornerRadii(new Vector2(9), new Vector2(9), new Vector2(9), new Vector2(9)) : default));
            canvas.Transform(transform);
            canvas.DrawImage(image, destination, ImageSampling.Smooth);
            canvas.Restore();
            return surface.PremultipliedPixels.ToArray();
        }
        finally
        {
            ImageShader.DisableSeparableSampling = false;
        }
    }

    // Cold: the image's mip levels are not built yet (a fresh image); warm: they are (the image was drawn before).
    // Every combination must give the same bytes.
    private static void AssertSame(int width, int height, bool translucent, RectF destination, Matrix3x2 transform, bool clip = false, bool rounded = false)
    {
        var coldGeneral = Source(width, height, translucent);
        var expected = Draw(coldGeneral, general: true, destination, transform, clip, rounded);
        Assert.Equal(expected, Draw(coldGeneral, general: false, destination, transform, clip, rounded)); // fast, warm
        Assert.Equal(expected, Draw(Source(width, height, translucent), general: false, destination, transform, clip, rounded)); // fast, cold
        var coldFast = Source(width, height, translucent);
        Draw(coldFast, general: false, destination, transform, clip, rounded);
        Assert.Equal(expected, Draw(coldFast, general: true, destination, transform, clip, rounded)); // general, warm
    }

    private static void AssertSameBothWays(int width, int height, RectF destination, Matrix3x2 transform, bool clip = false)
    {
        foreach (var translucent in new[] { false, true })
            foreach (var rounded in clip ? new[] { false, true } : new[] { false })
                AssertSame(width, height, translucent, destination, transform, clip, rounded);
    }

    public static TheoryData<float, float, float, float, bool> Placements => new()
    {
        { 3, 2, 58, 36, false },
        { 10, 8, 50, 31, false },
        { -7, -5, 58, 36, false },
        { 40, 30, 58, 36, false },
        { 3.25f, 2.5f, 58.5f, 36, false },
        { 3, 2, 90, 55, false },
        { 3, 2, 58, 36, true },
        { -9.5f, 40.75f, 58, 36, true },
    };

    [Theory]
    [MemberData(nameof(Placements))]
    public void ColumnAndRowSamplingMatchesTheGeneralPath(float x, float y, float width, float height, bool clip) =>
        AssertSameBothWays(64, 40, new RectF(x, y, width, height), Matrix3x2.Identity, clip);

    [Fact]
    public void ARotatedImageStillRendersThroughTheGeneralPath() =>
        AssertSame(64, 40, true, new RectF(3, 2, 58, 36), Matrix3x2.CreateRotation(0.3f, new Vector2(40, 30)));

    [Fact]
    public void AFourTimesShrinkOfAnOddSourceUsesNonExactMipScales() =>
        AssertSameBothWays(33, 17, new RectF(4, 3, 33 / 4f, 17 / 4f), Matrix3x2.Identity);

    [Theory]
    [InlineData(-1f, 1f)]
    [InlineData(1f, -1f)]
    [InlineData(-1f, -1f)]
    public void FlippedImagesMatch(float scaleX, float scaleY)
    {
        // A negative scale about the destination's centre: the device-to-image map has a negative axis.
        var transform = Matrix3x2.CreateScale(scaleX, scaleY, new Vector2(35, 25));
        AssertSameBothWays(64, 40, new RectF(10, 8, 50, 31), transform);
        AssertSameBothWays(37, 21, new RectF(10, 8, 20, 9), transform);
    }

    [Theory]
    [InlineData(37, 21, 1.5f, 1.5f)]
    [InlineData(37, 21, 3f, 1.7f)]
    [InlineData(37, 21, 1.1f, 6f)]
    [InlineData(63, 41, 2f, 2f)]
    [InlineData(63, 41, 3f, 1.7f)]
    [InlineData(63, 41, 1.03f, 1.03f)]
    public void OddSizesAtSeveralShrinkFactorsMatch(int width, int height, float shrinkX, float shrinkY) =>
        AssertSameBothWays(width, height, new RectF(2.5f, 1, width / shrinkX, height / shrinkY), Matrix3x2.Identity);

    [Theory]
    [InlineData(1, 1, 0.5f)]
    [InlineData(2, 2, 0.125f)]
    [InlineData(3, 3, 0.125f)]
    [InlineData(1, 200, 0.2f)]
    [InlineData(200, 1, 0.2f)]
    [InlineData(1, 200, 0.05f)]
    [InlineData(200, 1, 0.05f)]
    [InlineData(5, 5, 0.01f)]
    public void TinyAndThinSourcesMatch(int width, int height, float scale)
    {
        AssertSameBothWays(width, height, new RectF(3, 2, width * scale, height * scale), Matrix3x2.Identity);
        AssertSameBothWays(width, height, new RectF(3.4f, 2.7f, Math.Max(1, width * scale), Math.Max(1, height * scale)), Matrix3x2.Identity, clip: true);
    }

    [Fact]
    public void ASubPixelOffsetTranslucentSourceMatches() =>
        AssertSameBothWays(64, 40, new RectF(0.3f, 0.7f, 58.4f, 35.1f), Matrix3x2.CreateTranslation(0.25f, 0.5f), clip: true);

    [Theory]
    [InlineData(1e-15f)]
    [InlineData(1e-20f)]
    [InlineData(1e-30f)]
    [InlineData(1e20f)]
    public void AHugeScaleMatrixDoesNotThrowAndMatches(float scale) =>
        AssertSameBothWays(64, 40, new RectF(3, 2, 58, 36), Matrix3x2.CreateScale(scale));
}
