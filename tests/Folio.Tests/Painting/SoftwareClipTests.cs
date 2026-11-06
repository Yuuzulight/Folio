using System.Numerics;
using Folio.Painting;

namespace Folio.Tests.Painting;

public class SoftwareClipTests
{
    private static readonly RoundedRect Box = new(new RectF(0, 0, 20, 20), new CornerRadii(new Vector2(6), new Vector2(6), new Vector2(6), new Vector2(6)));

    // The canvas reuses its last rounded clip; a clip of the same box under another transform or inside another clip
    // must still land where that state puts it.
    [Fact]
    public void ARepeatedRoundedClipFollowsTheTransformAndTheClipAroundIt()
    {
        var surface = new SoftwareRasterSurface(40, 40);
        var canvas = surface.Canvas;
        var red = new Paint(new Rgba(1, 0, 0, 1));
        var all = new RoundedRect(new RectF(-40, -40, 120, 120), default);

        canvas.Save();
        canvas.ClipRoundedRect(Box);
        canvas.Restore();

        canvas.Save();
        canvas.Transform(Matrix3x2.CreateTranslation(20, 20));
        canvas.ClipRoundedRect(Box);
        canvas.FillRoundedRect(all, red);
        canvas.Restore();
        Assert.Equal(255, Alpha(surface, 30, 30));
        Assert.Equal(0, Alpha(surface, 10, 10));

        canvas.Save();
        canvas.ClipRoundedRect(new RoundedRect(new RectF(0, 0, 10, 40), default));
        canvas.ClipRoundedRect(Box);
        canvas.FillRoundedRect(all, red);
        canvas.Restore();
        Assert.Equal(255, Alpha(surface, 8, 10));
        Assert.Equal(0, Alpha(surface, 15, 10));
    }

    private static byte Alpha(SoftwareRasterSurface surface, int x, int y) => surface.PremultipliedPixels[(y * surface.Width + x) * 4 + 3];
}
