using System.Numerics;
using Folio.Imaging;
using Folio.Painting;
using Folio.Painting.Software;

namespace Folio.Tests.Painting;

// A scaled image's per-column and per-row sampling must give the bytes of the per-pixel general path.
public class ScaledImageDrawTests
{
    private static DecodedImage Source(bool translucent)
    {
        var pixels = new byte[64 * 40 * 4];
        var random = new Random(7);
        random.NextBytes(pixels);
        for (var i = 3; i < pixels.Length; i += 4)
            pixels[i] = translucent ? pixels[i] : (byte)255;
        return new DecodedImage(64, 40, pixels);
    }

    private static byte[] Draw(bool general, bool translucent, RectF destination, Matrix3x2 transform, bool clip, bool roundedClip = false)
    {
        ImageShader.DisableSeparableSampling = general;
        try
        {
            var surface = new SoftwareRasterSurface(80, 60);
            var canvas = surface.Canvas;
            canvas.FillRoundedRect(new RoundedRect(new RectF(0, 0, 80, 60), default), new Paint(new Rgba(.2f, .6f, .9f, 1)));
            canvas.Save();
            if (clip)
                canvas.ClipRoundedRect(new RoundedRect(new RectF(5, 4, 60, 45), roundedClip ? new CornerRadii(new Vector2(9), new Vector2(9), new Vector2(9), new Vector2(9)) : default));
            canvas.Transform(transform);
            canvas.DrawImage(Source(translucent), destination, ImageSampling.Smooth);
            canvas.Restore();
            return surface.PremultipliedPixels.ToArray();
        }
        finally
        {
            ImageShader.DisableSeparableSampling = false;
        }
    }

    public static TheoryData<float, float, float, float, bool> Placements => new()
    {
        { 3, 2, 58, 36, false },      // shrunk, integer offset
        { 10, 8, 50, 31, false },     // shrunk more
        { -7, -5, 58, 36, false },    // partly off the surface
        { 40, 30, 58, 36, false },    // partly off the far edge
        { 3.25f, 2.5f, 58.5f, 36, false }, // fractional placement
        { 3, 2, 90, 55, false },      // enlarged, no mip blend
        { 3, 2, 58, 36, true },       // under a clip
    };

    [Theory]
    [MemberData(nameof(Placements))]
    public void ColumnAndRowSamplingMatchesTheGeneralPath(float x, float y, float width, float height, bool clip)
    {
        foreach (var translucent in new[] { false, true })
            foreach (var rounded in new[] { false, true })
                Assert.Equal(
                    Draw(general: true, translucent, new RectF(x, y, width, height), Matrix3x2.Identity, clip, rounded),
                    Draw(general: false, translucent, new RectF(x, y, width, height), Matrix3x2.Identity, clip, rounded));
    }

    [Fact]
    public void ARotatedImageStillRendersThroughTheGeneralPath()
    {
        var rotated = Matrix3x2.CreateRotation(0.3f, new Vector2(40, 30));
        Assert.Equal(
            Draw(general: true, translucent: true, new RectF(3, 2, 58, 36), rotated, clip: false),
            Draw(general: false, translucent: true, new RectF(3, 2, 58, 36), rotated, clip: false));
    }
}
