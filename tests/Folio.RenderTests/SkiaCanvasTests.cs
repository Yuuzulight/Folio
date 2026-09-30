using Folio.Css;
using Folio.Html;
using Folio.Layout;
using Folio.Painting;
using Folio.Skia;
using Folio.Style;
using Folio.Typography;
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

    // The test box font for every family: each inked glyph is a full em square from 0.8em above the baseline.
    private static readonly Lazy<FontCollection> BoxFont = new(() =>
    {
        var fonts = FontCollection.FromFolder(Path.Combine(RepoPaths.Tests, "fonts"));
        foreach (var generic in new[] { "serif", "sans-serif", "monospace" })
            fonts.GenericFamilies[generic] = ["Folio Box"];
        return fonts;
    });

    [Fact]
    public void DrawsTextInItsColour()
    {
        using var bitmap = Render("<style>body { margin: 0; color: blue } span { color: red }</style><p style='margin: 0'>a<span>b</span> </p>");

        Assert.Equal(SKColors.Blue, bitmap.GetPixel(8, 8));
        Assert.Equal(SKColors.Red, bitmap.GetPixel(24, 8));
        Assert.Equal(SKColors.White, bitmap.GetPixel(40, 8)); // the space is blank
        Assert.Equal(SKColors.White, bitmap.GetPixel(8, 20)); // below the line
    }

    [Fact]
    public void DrawsTextDecorationsInTheirStyles()
    {
        using var bitmap = Render("<style>body { margin: 0 } p { margin: 0; text-decoration: underline red 2px; text-underline-offset: 3.2px }</style>" +
            "<p>&nbsp;&nbsp;&nbsp;</p><p style='text-decoration-style: dotted'>&nbsp;&nbsp;&nbsp;</p><p style='text-decoration-style: wavy'>&nbsp;&nbsp;&nbsp;</p>");

        // Solid: an unbroken line from 16px to 18px.
        Assert.All(Enumerable.Range(0, 48), x => Assert.Equal(SKColors.Red, bitmap.GetPixel(x, 16)));
        Assert.Equal(SKColors.White, bitmap.GetPixel(24, 13));
        // Dotted: ink and gaps along the line.
        var dotted = Enumerable.Range(0, 48).Select(x => bitmap.GetPixel(x, 33)).ToList();
        Assert.Contains(dotted, p => p.Green < 128);
        Assert.Contains(SKColors.White, dotted);
        // Wavy: the line's ink moves up and down, so no single row holds all of it.
        var inked = Enumerable.Range(0, 48).Select(x => Enumerable.Range(44, 10).First(y => bitmap.GetPixel(x, y).Green < 128)).ToList();
        Assert.True(inked.Max() - inked.Min() >= 2, string.Join(",", inked));
    }

    [Fact]
    public void DrawsImagesScaledToTheirBoxes()
    {
        // A 2x2 image (red, lime / blue, white) drawn 20px square with square pixels.
        using var bitmap = Render("<style>body { margin: 0 }</style><img style='display: block; width: 20px; image-rendering: pixelated' src='data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAE0lEQVR4nGP4z8DwHwwZGP6DAQBJyAn3FGMynQAAAABJRU5ErkJggg=='>");

        Assert.Equal(SKColors.Red, bitmap.GetPixel(5, 5));
        Assert.Equal(SKColors.Lime, bitmap.GetPixel(15, 5));
        Assert.Equal(SKColors.Blue, bitmap.GetPixel(5, 15));
        Assert.Equal(SKColors.White, bitmap.GetPixel(15, 15));
        Assert.Equal(SKColors.White, bitmap.GetPixel(25, 5));
    }

    // Lays out and paints a document on a white 100x100 surface.
    private static SKBitmap Render(string html)
    {
        var document = TreeBuilder.Parse("<!DOCTYPE html>" + html);
        StyleResolver.Resolve(document, new MediaContext(100, 100));
        var images = new Folio.Imaging.ImageLoader(Folio.Resources.ResourceLoader.DataUrlsOnly, null);
        var fragment = LayoutEngine.LayoutDocument(BoxTreeBuilder.Build(document, images)!, 100, 100, BoxFont.Value);
        var list = DisplayListBuilder.Build(fragment);

        var bitmap = new SKBitmap(new SKImageInfo(100, 100, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        DisplayListPlayer.Replay(list, new SkiaCanvas(canvas));
        canvas.Flush();
        return bitmap;
    }
}
