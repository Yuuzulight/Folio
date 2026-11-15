#if WINDOWS
using System.Drawing;
using System.Drawing.Imaging;
using Folio.Painting;
using Folio.WinForms;

namespace Folio.WinForms.Tests;

// Presenting only the damaged part of a frame must leave the bitmap exactly what presenting all of it would.
public sealed class DamagePresentationTests
{
    private static readonly Paint White = new(new Rgba(1, 1, 1, 1));

    private static byte[] Bytes(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var bytes = new byte[bitmap.Height * bitmap.Width * 4];
            for (var y = 0; y < bitmap.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), bytes, y * bitmap.Width * 4, bitmap.Width * 4);
            return bytes;
        }
        finally { bitmap.UnlockBits(data); }
    }

    // A fractional-edged rounded shape, drawn the way FolioView draws: transform of scale and scroll, clip to the damage, fill it, then the content.
    private static void Draw(SoftwareRasterSurface surface, float scale, int top, RectF damage, Rgba color)
    {
        var canvas = surface.Canvas;
        canvas.Save();
        canvas.Transform(RetainedScroll.Transform(scale, top));
        canvas.ClipRoundedRect(new RoundedRect(damage, default));
        canvas.FillRoundedRect(new RoundedRect(damage, default), White);
        var shape = new RectF(damage.X + 3.3f, damage.Y + 2.7f, damage.Width - 6.1f, damage.Height - 5.3f);
        canvas.FillRoundedRect(new RoundedRect(shape, new CornerRadii(new System.Numerics.Vector2(9.4f), new System.Numerics.Vector2(9.4f), new System.Numerics.Vector2(9.4f), new System.Numerics.Vector2(9.4f))), new Paint(color));
        canvas.Restore();
    }

    [Theory]
    [InlineData(1f, 0)]
    [InlineData(1.25f, 0)]
    [InlineData(1.5f, 0)]
    [InlineData(1.25f, 37)]
    [InlineData(1.5f, 101)]
    public void ADamageOnlyPresentMakesTheBitmapOfAFullOne(float scale, int top)
    {
        const int width = 211, height = 163; // odd sizes
        using var surface = new SoftwareRasterSurface(width, height);
        using var damaged = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using var full = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        var whole = new Rectangle(0, 0, width, height);
        Draw(surface, scale, top, new RectF(0, top / scale, width / scale, height / scale), new Rgba(0.1f, 0.4f, 0.8f, 1));
        FolioView.CopySurfaceToBitmap(surface, damaged, whole); // the frame the bitmap holds before the damage

        // Damage in layout units, as the control snaps it, and then repaint just that.
        var damage = DisplayListPlayer.SnapToDevicePixels(new RectF(20.3f, top / scale + 17.6f, 61.2f, 38.9f), scale);
        Draw(surface, scale, top, damage, new Rgba(0.9f, 0.2f, 0.1f, 0.8f));
        var changed = FolioView.ChangedArea(damage, scale, top, width, height);
        Assert.True(changed.Width > 0 && changed.Height > 0 && changed.Width < width && changed.Height < height, "the damage is a part of the frame");
        FolioView.CopySurfaceToBitmap(surface, damaged, changed);
        FolioView.CopySurfaceToBitmap(surface, full, whole);

        Assert.True(Bytes(full).AsSpan().SequenceEqual(Bytes(damaged)), $"scale {scale} top {top}: the damage-only bitmap differs from the full one");
    }

    [Fact]
    public void ADamageOffTheFrameChangesNothing()
    {
        var area = FolioView.ChangedArea(new RectF(500, 500, 10, 10), 1f, 0, 100, 100);
        Assert.True(area.Width <= 0 || area.Height <= 0);
    }

    [Fact]
    public void ARowReadFromAColumnIsThePartOfTheFullRow()
    {
        using var surface = new SoftwareRasterSurface(37, 5);
        var random = new Random(9);
        random.NextBytes(surface.PremultipliedPixels);
        var full = new byte[37 * 4];
        var part = new byte[20 * 4];
        surface.ReadBgraRow(3, full);
        surface.ReadBgraRow(3, 11, 20, part);
        Assert.True(full.AsSpan(11 * 4, 20 * 4).SequenceEqual(part));
        surface.ReadBgraRow(4, 17, 20, part); // up to the last column
        surface.ReadBgraRow(4, full);
        Assert.True(full.AsSpan(17 * 4, 20 * 4).SequenceEqual(part));
    }

    [Theory]
    [InlineData(3, 30, 20)] // runs 13 pixels past the edge into the next row
    [InlineData(3, -1, 5)]
    [InlineData(3, 0, 38)]
    [InlineData(3, 5, -1)]
    [InlineData(5, 0, 4)] // row beyond the last
    [InlineData(-1, 0, 4)]
    public void ReadingOutsideTheFrameThrowsInsteadOfReadingTheNextRow(int row, int left, int count)
    {
        using var surface = new SoftwareRasterSurface(37, 5);
        Assert.Throws<ArgumentOutOfRangeException>(() => surface.ReadBgraRow(row, left, count, new byte[200]));
    }
}
#endif
