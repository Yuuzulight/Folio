using Folio.Css;
using Folio.Html;
using Folio.Layout;
using Folio.Painting;
using Folio.Skia;
using Folio.Style;
using SkiaSharp;

namespace Folio.RenderTests;

/// <summary>Display lists replayed through <see cref="SkiaCanvas"/>, checked pixel by pixel.</summary>
public class SkiaCanvasTests
{
    [Fact]
    public void PaintsTheCanvasBackgroundAndBoxes()
    {
        using var bitmap = Render("<style>body { margin: 0; background: red } div { margin: 10px; height: 20px; background: blue }</style><div></div>");

        Assert.Equal(SKColors.Red, bitmap.GetPixel(5, 5));
        Assert.Equal(SKColors.Blue, bitmap.GetPixel(20, 20));
        Assert.Equal(SKColors.Red, bitmap.GetPixel(20, 35));
    }

    [Fact]
    public void PaintsEachBorderSideInItsOwnColour()
    {
        using var bitmap = Render("<style>body { margin: 0 } div { width: 40px; height: 40px; border: 10px solid; border-color: red lime blue yellow; background: white }</style><div></div>");

        Assert.Equal(SKColors.Red, bitmap.GetPixel(30, 3));
        Assert.Equal(SKColors.Lime, bitmap.GetPixel(56, 30));
        Assert.Equal(SKColors.Blue, bitmap.GetPixel(30, 56));
        Assert.Equal(SKColors.Yellow, bitmap.GetPixel(3, 30));
        Assert.Equal(SKColors.White, bitmap.GetPixel(30, 30));
    }

    [Fact]
    public void RoundedCornersLeaveTheCornerUnpainted()
    {
        using var bitmap = Render("<style>body { margin: 0 } div { width: 60px; height: 60px; border-radius: 30px; background: blue }</style><div></div>");

        Assert.Equal(SKColors.White, bitmap.GetPixel(1, 1));
        Assert.Equal(SKColors.Blue, bitmap.GetPixel(30, 30));
    }

    [Fact]
    public void OverflowHiddenClipsChildren()
    {
        using var bitmap = Render("<style>body { margin: 0 }</style><div style='overflow: hidden; height: 20px'><div style='height: 60px; background: blue'></div></div>");

        Assert.Equal(SKColors.Blue, bitmap.GetPixel(10, 10));
        Assert.Equal(SKColors.White, bitmap.GetPixel(10, 40));
    }

    [Fact]
    public void OpacityBlendsTheLayer()
    {
        using var bitmap = Render("<style>body { margin: 0 }</style><div style='opacity: 0.5; height: 20px; background: red'></div>");

        var pixel = bitmap.GetPixel(10, 10);
        Assert.Equal(255, pixel.Red);
        Assert.InRange(pixel.Green, 126, 129);
        Assert.InRange(pixel.Blue, 126, 129);
    }

    [Fact]
    public void DashedBordersLeaveGaps()
    {
        using var bitmap = Render("<style>body { margin: 0 } div { width: 100px; height: 20px; border-top: 4px dashed black }</style><div></div>");

        var row = Enumerable.Range(0, 100).Select(x => bitmap.GetPixel(x, 2)).ToList();
        Assert.Contains(SKColors.Black, row);
        Assert.Contains(SKColors.White, row);
    }

    // Lays out and paints a document on a white 100x100 surface.
    private static SKBitmap Render(string html)
    {
        var document = TreeBuilder.Parse("<!DOCTYPE html>" + html);
        StyleResolver.Resolve(document, new MediaContext(100, 100));
        var fragment = LayoutEngine.LayoutDocument(BoxTreeBuilder.Build(document)!, 100, 100);
        var list = DisplayListBuilder.Build(fragment);

        var bitmap = new SKBitmap(new SKImageInfo(100, 100, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        DisplayListPlayer.Replay(list, new SkiaCanvas(canvas));
        canvas.Flush();
        return bitmap;
    }
}
