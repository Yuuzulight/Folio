using Folio.Skia;
using SkiaSharp;

namespace Folio.RenderTests;

public class HeadlessRendererTests
{
    private static SKColor Pixel(RenderResult result, int x, int y)
    {
        var p = result.Pixels.Slice((y * result.Width + x) * 4, 4); // premultiplied BGRA; opaque here
        return new SKColor(p[2], p[1], p[0], p[3]);
    }

    // A 20px box that slides 100px right over a second, after a 1s delay, and holds its end.
    private const string Slide = "<style>@keyframes s { from { transform: translateX(0) } to { transform: translateX(100px) } }" +
        " body { margin: 0 } div { width: 20px; height: 20px; background: blue; animation: s 1s linear 1s forwards }</style><div></div>";

    [Theory]
    [InlineData(0.5, 10)]   // still in its delay
    [InlineData(1.5, 60)]   // half way
    [InlineData(5.0, 110)]  // held at its end
    public void DrawsAnimationsAtTheRequestedTime(double seconds, int centre)
    {
        using var document = Document.Parse(Slide);
        using var result = HeadlessRenderer.Render(document, new RenderRequest(200, 20, AnimationTime: TimeSpan.FromSeconds(seconds)));

        Assert.Equal(SKColors.Blue, Pixel(result, centre, 10));
        Assert.Equal(SKColors.White, Pixel(result, centre - 15, 10));
        Assert.Equal(SKColors.White, Pixel(result, centre + 15, 10));
    }

    [Fact]
    public void WithoutATimeTheDocumentIsSettled()
    {
        using var document = Document.Parse(Slide);
        using var result = HeadlessRenderer.Render(document, new RenderRequest(200, 20));

        Assert.Equal(SKColors.Blue, Pixel(result, 110, 10));
        Assert.Equal(SKColors.White, Pixel(result, 10, 10));
    }

    [Fact]
    public void RendersTheWholeDocumentWhenNoHeightIsGiven()
    {
        using var document = Document.Parse("<body style='margin: 0'><div style='height: 30px; background: red'></div><div style='height: 20px'></div>");
        using var result = HeadlessRenderer.Render(document, new RenderRequest(100));

        Assert.Equal((100, 50), (result.Width, result.Height));
        Assert.Equal(SKColors.Red, Pixel(result, 50, 10));
        Assert.Equal(SKColors.White, Pixel(result, 50, 40));
    }

    [Fact]
    public void AViewportHeightFixesTheImageAndDeviceScaleMultipliesIt()
    {
        using var document = Document.Parse("<body style='margin: 0'><div style='height: 50vh; background: blue'></div>");
        using var result = HeadlessRenderer.Render(document, new RenderRequest(100, 40, DeviceScale: 2));

        Assert.Equal((200, 80), (result.Width, result.Height));
        Assert.Equal(SKColors.Blue, Pixel(result, 100, 30));   // 20 CSS px of 40, at twice the scale
        Assert.Equal(SKColors.White, Pixel(result, 100, 50));
    }

    [Fact]
    public void TheDarkSchemeDarkensTheCanvas()
    {
        using var document = Document.Parse("<p>", new FolioOptions { ColorScheme = ColorScheme.Dark });
        using var result = HeadlessRenderer.Render(document, new RenderRequest(10, 10));

        Assert.Equal(new SKColor(18, 18, 18), Pixel(result, 5, 5));
    }

    [Fact]
    public void DrawsTextWithTheDocumentsFonts()
    {
        using var document = Document.Parse("<body style='margin: 0; color: lime'>A", TestRenderer.Options);
        using var result = HeadlessRenderer.Render(document, new RenderRequest(50, 20));

        Assert.Equal(SKColors.Lime, Pixel(result, 8, 8)); // the box font's glyphs fill their em square
        Assert.Equal(SKColors.White, Pixel(result, 30, 8));
    }

    [Fact]
    public void RenderPngMakesAPngOfTheWholePage()
    {
        var png = HeadlessRenderer.RenderPng("<body style='margin: 0'><div style='height: 25px'></div>", 40);

        using var bitmap = SKBitmap.Decode(png);
        Assert.Equal((40, 25), (bitmap.Width, bitmap.Height));
    }

    [Fact]
    public void RejectsImpossibleRequests()
    {
        using var document = Document.Parse("");
        Assert.Throws<ArgumentOutOfRangeException>(() => HeadlessRenderer.Render(document, new RenderRequest(0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => HeadlessRenderer.Render(document, new RenderRequest(10, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => HeadlessRenderer.Render(document, new RenderRequest(10, DeviceScale: 0)));
        using var bare = Document.Parse("<body style='margin: 0'>");
        using var empty = HeadlessRenderer.Render(bare, new RenderRequest(10));
        Assert.Equal((10, 1), (empty.Width, empty.Height)); // an empty page is at least one pixel tall
    }
}
