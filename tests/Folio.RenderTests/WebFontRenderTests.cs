using Folio.Skia;
using Folio.Tests.Typography;
using SkiaSharp;

namespace Folio.RenderTests;

public class WebFontRenderTests
{
    private static readonly byte[] Box = File.ReadAllBytes(Path.Combine(RepoPaths.Tests, "fonts", "FolioBox.ttf"));

    private static SKColor Pixel(RenderResult result, int x, int y)
    {
        var p = result.Pixels.Slice((y * result.Width + x) * 4, 4);
        return new SKColor(p[2], p[1], p[0], p[3]);
    }

    [Theory]
    [InlineData("woff2")]
    [InlineData("woff")]
    public void DrawsTextWithADataUrlWebFont(string format)
    {
        // No font source at all: the only font is the one the page brings.
        var font = format == "woff2" ? WebFontWriter.Woff2(Box) : WebFontWriter.Woff(Box);
        var html = $"<style>@font-face {{ font-family: Web; src: url(data:font/{format};base64,{Convert.ToBase64String(font)}) format({format}) }}</style>"
            + "<body style='margin: 0; color: lime; font: 20px Web'>AB";
        using var document = Document.Parse(html);
        using var result = HeadlessRenderer.Render(document, new RenderRequest(60, 20));

        Assert.Equal(SKColors.Lime, Pixel(result, 8, 8));    // the box font's glyphs fill their em square
        Assert.Equal(SKColors.Lime, Pixel(result, 28, 8));
        Assert.Equal(SKColors.White, Pixel(result, 50, 8));
    }

    [Fact]
    public void ABlockedWebFontFallsBackToTheNextFamily()
    {
        const string html = "<style>@font-face { font-family: Web; src: url(https://fonts.example/web.woff2) }</style>"
            + "<body style='margin: 0; color: lime; font: 20px Web, \"Folio Box\"'>A";
        using var document = Document.Parse(html, TestRenderer.Options);
        using var result = HeadlessRenderer.Render(document, new RenderRequest(40, 20));

        Assert.Equal(SKColors.Lime, Pixel(result, 8, 8));
        Assert.Equal(SKColors.White, Pixel(result, 30, 8));
    }
}
