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
        using var bitmap = Render("<style>body { margin: 0 } p { margin: 0; text-decoration: underline red 2px; text-underline-offset: 3px }</style>" +
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

    [Fact]
    public void UnderlinesSkipTheGlyphsInk()
    {
        // The box font's glyphs reach 3.2px below the baseline, through the underline; the text itself is transparent.
        using var bitmap = Render("<style>body { margin: 0 } p { margin: 0; color: transparent; text-decoration: underline red 2px }</style>" +
            "<p>a&nbsp;a</p><p style='text-decoration-skip-ink: none'>a&nbsp;a</p>");

        Assert.Equal(SKColors.White, bitmap.GetPixel(8, 15)); // under the first glyph
        Assert.Equal(SKColors.Red, bitmap.GetPixel(24, 15));  // under the space, away from the glyphs
        Assert.Equal(SKColors.Red, bitmap.GetPixel(8, 31));   // skip-ink: none
    }

    [Fact]
    public void TransformsMapTheBoxAndItsContent()
    {
        // A quarter turn clockwise about the top-left corner, then moved to (50, 10): the box covers x 30 to 50 and
        // y 10 to 50, its child the top right corner of that.
        using var bitmap = Render("<style>body { margin: 0 }</style><div style='width: 40px; height: 20px; background: blue; " +
            "transform-origin: 0 0; transform: translate(50px, 10px) rotate(90deg)'><div style='width: 10px; height: 10px; background: red'></div></div>");

        Assert.Equal(SKColors.Red, bitmap.GetPixel(45, 15));
        Assert.Equal(SKColors.Blue, bitmap.GetPixel(35, 15));
        Assert.Equal(SKColors.Blue, bitmap.GetPixel(45, 45));
        Assert.Equal(SKColors.White, bitmap.GetPixel(20, 15));
        Assert.Equal(SKColors.White, bitmap.GetPixel(45, 5));
    }

    [Fact]
    public void UnderlinesSkipTheGlyphsInkUnderATransform()
    {
        // Doubled from the top-left corner: the first glyph's gap spans x -4 to 36, the line shows under the space.
        using var bitmap = Render("<style>body { margin: 0 } p { margin: 0; color: transparent; text-decoration: underline red 2px; " +
            "transform-origin: 0 0; transform: scale(2) }</style><p>a&nbsp;a</p>");

        Assert.Equal(SKColors.White, bitmap.GetPixel(16, 30));
        Assert.Equal(SKColors.Red, bitmap.GetPixel(48, 30));
    }

    [Fact]
    public void ColourFiltersMapTheBoxesColours()
    {
        using var bitmap = Render("<style>body { margin: 0 } div { height: 10px; background: red }</style><div style='background: black; filter: invert(1)'></div>" +
            "<div style='filter: grayscale(1)'></div><div style='filter: brightness(0.5)'></div><div style='filter: opacity(0.5)'></div><div style='filter: hue-rotate(120deg)'></div>");

        Assert.Equal(SKColors.White, bitmap.GetPixel(5, 5));
        AssertNear(new SKColor(54, 54, 54), bitmap.GetPixel(5, 15));
        AssertNear(new SKColor(128, 0, 0), bitmap.GetPixel(5, 25));
        AssertNear(new SKColor(255, 128, 128), bitmap.GetPixel(5, 35));
        // Red turned a third of the way round comes out greenish (the matrix is an approximation of a hue rotation).
        var rotated = bitmap.GetPixel(5, 45);
        Assert.True(rotated.Green > rotated.Red && rotated.Green > rotated.Blue, rotated.ToString());
    }

    [Fact]
    public void BlurSpreadsTheBoxBeyondItsEdges()
    {
        using var bitmap = Render("<style>body { margin: 0 }</style><div style='margin: 40px; width: 20px; height: 20px; background: blue; filter: blur(3px)'></div>");

        Assert.NotEqual(SKColors.White, bitmap.GetPixel(37, 50));
        Assert.True(bitmap.GetPixel(50, 50).Red < 40);
        Assert.Equal(SKColors.White, bitmap.GetPixel(75, 50));
    }

    [Fact]
    public void AZeroBlurChangesNothing()
    {
        using var bitmap = Render("<style>body { margin: 0 }</style><div style='width: 10px; height: 10px; background: red; filter: blur(0) invert(1) blur(0)'></div>");

        Assert.Equal(SKColors.Cyan, bitmap.GetPixel(5, 5));
        Assert.Equal(SKColors.White, bitmap.GetPixel(15, 5));
    }

    [Fact]
    public void DropShadowPaintsTheAlphaMovedUnderTheBox()
    {
        using var bitmap = Render("<style>body { margin: 0 }</style><div style='width: 10px; height: 10px; background: red; filter: drop-shadow(blue 20px 5px)'></div>");

        Assert.Equal(SKColors.Red, bitmap.GetPixel(5, 5));
        Assert.Equal(SKColors.Blue, bitmap.GetPixel(25, 10));
        Assert.Equal(SKColors.White, bitmap.GetPixel(15, 5));
    }

    [Fact]
    public void BackdropFiltersFilterWhatLiesUnderTheBorderBoxOnly()
    {
        // The red box and the white canvas under the panel from x 20 to 60 are inverted; the rest is untouched.
        using var bitmap = Render("<style>body { margin: 0 } div { position: absolute; top: 0; height: 40px }</style><div style='width: 40px; background: red'></div>" +
            "<div style='left: 20px; width: 40px; backdrop-filter: invert(1)'></div><div style='top: 50px; width: 40px; backdrop-filter: invert(1); opacity: 0.5'></div>");

        Assert.Equal(SKColors.Red, bitmap.GetPixel(10, 20));
        Assert.Equal(SKColors.Cyan, bitmap.GetPixel(30, 20));
        Assert.Equal(SKColors.Black, bitmap.GetPixel(50, 20));
        Assert.Equal(SKColors.White, bitmap.GetPixel(70, 20));
        // Opacity applies to the filtered backdrop as well.
        AssertNear(new SKColor(128, 128, 128), bitmap.GetPixel(20, 70));
    }

    // Each mode's result for the backdrop rgb(51, 153, 204) and the source rgb(230, 77, 26), worked out from the blend
    // functions of https://drafts.csswg.org/compositing-2/#blending (and the addition for plus-lighter).
    [Theory]
    [InlineData("normal", 230, 77, 26)]
    [InlineData("multiply", 46, 46, 21)]
    [InlineData("screen", 235, 184, 209)]
    [InlineData("overlay", 92, 113, 163)]
    [InlineData("darken", 51, 77, 26)]
    [InlineData("lighten", 230, 153, 204)]
    [InlineData("color-dodge", 255, 219, 227)]
    [InlineData("color-burn", 29, 0, 0)]
    [InlineData("hard-light", 215, 92, 42)]
    [InlineData("soft-light", 102, 129, 172)]
    [InlineData("difference", 179, 76, 178)]
    [InlineData("exclusion", 189, 138, 188)]
    [InlineData("hue", 213, 98, 60)]
    [InlineData("saturation", 25, 161, 229)]
    [InlineData("color", 241, 88, 37)]
    [InlineData("luminosity", 40, 142, 193)]
    [InlineData("plus-lighter", 255, 230, 230)]
    public void BlendModesFollowTheirFormulas(string mode, int r, int g, int b)
    {
        using var mixed = Render($"<style>body {{ margin: 0; background: rgb(51, 153, 204) }} div {{ height: 10px; background: rgb(230, 77, 26) }}</style><div style='mix-blend-mode: {mode}'></div>" +
            $"<div style='background: linear-gradient(rgb(230, 77, 26), rgb(230, 77, 26)) rgb(51, 153, 204); background-blend-mode: {(mode == "plus-lighter" ? "normal" : mode)}'></div>");

        AssertNear(new SKColor((byte)r, (byte)g, (byte)b), mixed.GetPixel(5, 5), 3);
        if (mode != "plus-lighter")
            AssertNear(new SKColor((byte)r, (byte)g, (byte)b), mixed.GetPixel(5, 15), 3);
    }

    [Fact]
    public void BlendingStopsAtTheIsolatedGroup()
    {
        // Multiplying blue into the page's yellow gives black; inside an isolated group with nothing under it, blue stays blue.
        using var bitmap = Render("<style>body { margin: 0; background: yellow } span { display: block; height: 10px; background: blue; mix-blend-mode: multiply }</style>" +
            "<span></span><div style='isolation: isolate'><span></span></div><div style='opacity: 0.99'><span></span></div>");

        Assert.Equal(SKColors.Black, bitmap.GetPixel(5, 5));
        Assert.Equal(SKColors.Blue, bitmap.GetPixel(5, 15));
        AssertNear(SKColors.Blue, bitmap.GetPixel(5, 25), 4);
    }

    private static void AssertNear(SKColor expected, SKColor actual, int tolerance = 2) =>
        Assert.True(Math.Abs(expected.Red - actual.Red) <= tolerance && Math.Abs(expected.Green - actual.Green) <= tolerance
                    && Math.Abs(expected.Blue - actual.Blue) <= tolerance, $"Expected {expected}, got {actual}.");

    [Fact]
    public void BackgroundImagesTileTheirPixels()
    {
        // The test image's left half is red and its right half green; doubled, each half is 4px wide.
        using var bitmap = Render("<style>body { margin: 0 } div { width: 20px; height: 4px; image-rendering: pixelated; background: url(data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAQAAAACCAYAAAB/qH1jAAAAEklEQVR4nGP4z8DwH4SRIKoAAAslD/HAvA0nAAAAAElFTkSuQmCC) 0 0 / 8px 4px }</style><div></div>");

        Assert.Equal(SKColors.Red, bitmap.GetPixel(1, 1));
        Assert.Equal(new SKColor(0, 255, 0), bitmap.GetPixel(5, 1));
        Assert.Equal(SKColors.Red, bitmap.GetPixel(9, 1));
        Assert.Equal(new SKColor(0, 255, 0), bitmap.GetPixel(13, 1));
        Assert.Equal(SKColors.Red, bitmap.GetPixel(17, 1));
        Assert.Equal(SKColors.White, bitmap.GetPixel(21, 1));
    }

    [Fact]
    public void AlphaAndLuminanceMasksFadeTheBox()
    {
        using var bitmap = Render("<style>body { margin: 0 } div { width: 20px; height: 40px; background: red }</style>" +
            "<div style='mask-image: linear-gradient(black, transparent)'></div><div style='mask: linear-gradient(white, black) luminance; margin: -40px 0 0 30px'></div>" +
            "<div style='mask: url(data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAQAAAACCAYAAAB/qH1jAAAAEklEQVR4nGP4z8DwH4SRIKoAAAslD/HAvA0nAAAAAElFTkSuQmCC) 0 0 / 20px 40px luminance; margin: -40px 0 0 60px'></div>");

        AssertNear(SKColors.Red, bitmap.GetPixel(10, 0), 8);
        AssertNear(SKColors.White, bitmap.GetPixel(10, 39), 8);
        Assert.InRange(bitmap.GetPixel(10, 20).Green, 110, 145);
        AssertNear(SKColors.Red, bitmap.GetPixel(40, 0), 8);
        AssertNear(SKColors.White, bitmap.GetPixel(40, 39), 8);
        // A red mask keeps 21% of the box, a green one 72%.
        AssertNear(new SKColor(255, 201, 201), bitmap.GetPixel(65, 20), 3);
        AssertNear(new SKColor(255, 72, 72), bitmap.GetPixel(75, 20), 3);
    }

    [Fact]
    public void MaskLayersComposite()
    {
        // The border idiom: the content box excluded from the border box leaves the padding ring.
        using var bitmap = Render("<style>body { margin: 0 } div { width: 20px; height: 20px; background: blue }</style>" +
            "<div style='padding: 5px; mask: linear-gradient(#fff 0 0) content-box, linear-gradient(#fff 0 0); mask-composite: exclude'></div>" +
            "<div style='margin: -30px 0 0 40px; mask: linear-gradient(to right, black 50%, transparent 50%), linear-gradient(black 50%, transparent 50%); mask-composite: intersect'></div>" +
            "<div style='margin: 10px 0 0 40px; mask: linear-gradient(to right, black 50%, transparent 50%), linear-gradient(black 50%, transparent 50%); mask-composite: subtract'></div>");

        Assert.Equal(SKColors.Blue, bitmap.GetPixel(2, 15));
        Assert.Equal(SKColors.White, bitmap.GetPixel(15, 15));
        Assert.Equal(SKColors.Blue, bitmap.GetPixel(45, 5));
        Assert.Equal(SKColors.White, bitmap.GetPixel(55, 5));
        Assert.Equal(SKColors.White, bitmap.GetPixel(45, 15));
        // Subtract keeps the left half where the top half is not.
        Assert.Equal(SKColors.White, bitmap.GetPixel(45, 35));
        Assert.Equal(SKColors.Blue, bitmap.GetPixel(45, 45));
        Assert.Equal(SKColors.White, bitmap.GetPixel(55, 45));
    }

    [Fact]
    public void MasksApplyAfterFilters()
    {
        // The blur spreads past the box, but the mask, clipped to the border box, cuts it off there.
        using var bitmap = Render("<style>body { margin: 0 }</style><div style='margin: 20px; width: 40px; height: 20px; background: blue; filter: blur(4px); mask-image: linear-gradient(black, black)'></div>");

        Assert.Equal(SKColors.White, bitmap.GetPixel(18, 30));
        Assert.NotEqual(SKColors.White, bitmap.GetPixel(21, 30));
    }

    // Lays out and paints a document on a white 100x100 surface.
    private static SKBitmap Render(string html)
    {
        var document = TreeBuilder.Parse("<!DOCTYPE html>" + html);
        StyleResolver.Resolve(document, new MediaContext(100, 100));
        var images = new Folio.Imaging.ImageLoader(Folio.Resources.ResourceLoader.DataUrlsOnly, null);
        var fragment = LayoutEngine.LayoutDocument(BoxTreeBuilder.Build(document, images)!, 100, 100, BoxFont.Value);
        var list = DisplayListBuilder.Build(fragment, images);

        var bitmap = new SKBitmap(new SKImageInfo(100, 100, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        DisplayListPlayer.Replay(list, new SkiaCanvas(canvas));
        canvas.Flush();
        return bitmap;
    }
}
