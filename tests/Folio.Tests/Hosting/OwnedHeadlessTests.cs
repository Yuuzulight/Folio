using Folio.Imaging;
using Folio.Rendering;
using Folio.Typography;

namespace Folio.Tests.Hosting;

public sealed class OwnedHeadlessTests
{
    [Fact]
    public void AnOfflineInterceptorDoesNotRequireANetworkLoader()
    {
        var options = new FolioOptions { ResourceInterceptor = new OfflineStyle() };
        var png = HeadlessRenderer.RenderPng("<link rel=stylesheet href='https://offline.invalid/style.css'><body style='margin:0'><div style='height:20px'></div></body>", 30, options);
        var image = Assert.IsType<DecodedImage>(ImageDecoder.Decode(png));
        Assert.Equal(new byte[] { 0, 128, 0, 255 }, image.Pixels.AsSpan((5 * image.Width + 5) * 4, 4).ToArray());
        Assert.Null(options.ResourceLoader);
    }

    private sealed class OfflineStyle : Folio.Resources.IResourceInterceptor
    {
        public Folio.Resources.ResourceResponse? Intercept(Folio.Resources.ResourceRequest request) =>
            request.Url == "https://offline.invalid/style.css"
                ? Folio.Resources.ResourceResponse.Loaded(System.Text.Encoding.UTF8.GetBytes("body{background:green}"), "text/css") : null;
    }

    [Fact]
    public void DefaultPackageFontsProduceVisibleTextWithoutAnOracle()
    {
        var png = HeadlessRenderer.RenderPng("<body style='margin:0;color:black;background:white'>Mana is ready</body>", 240);
        var decoded = Assert.IsType<DecodedImage>(ImageDecoder.Decode(png));
        Assert.Equal(240, decoded.Width);
        Assert.True(decoded.Height > 0);
        Assert.Contains(Enumerable.Range(0, decoded.Width * decoded.Height), i =>
            decoded.Pixels[i * 4 + 3] == 255 && decoded.Pixels[i * 4] < 100);
    }

    [Fact]
    public void OtherOptionsDoNotSilentlyTurnOffDefaultFontsOrMutateTheCaller()
    {
        var options = new FolioOptions { ColorScheme = ColorScheme.Dark };
        var png = HeadlessRenderer.RenderPng("<body style='margin:0;color:white'>Visible text</body>", 240, options);
        var image = Assert.IsType<DecodedImage>(ImageDecoder.Decode(png));
        Assert.Contains(Enumerable.Range(0, image.Width * image.Height), i => image.Pixels[i * 4] > 200);
        Assert.Same(FontSettings.Default, options.Fonts);
    }

    [Fact]
    public void RgbaOutputScaleAndPngRoundTripPreserveColours()
    {
        using var document = Document.Parse("<body style='margin:0'><div style='height:10px;background:red'></div></body>");
        using var image = HeadlessRenderer.Render(document, new RenderRequest(20, 20, 2));
        Assert.Equal((40, 40), (image.Width, image.Height));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, image.Pixels.Slice((5 * 40 + 5) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, image.Pixels.Slice((30 * 40 + 5) * 4, 4).ToArray());
        using var stream = new MemoryStream();
        image.SavePng(stream);
        Assert.True(stream.CanWrite);
        var decoded = Assert.IsType<DecodedImage>(ImageDecoder.Decode(stream.ToArray()));
        Assert.Equal(image.Pixels.ToArray(), decoded.Pixels);
    }

    [Fact]
    public void WholePageHeightDarkBackgroundAndDisposalAreExplicit()
    {
        using var document = Document.Parse("<body style='margin:0'><div style='height:37px'></div>", new FolioOptions { ColorScheme = ColorScheme.Dark });
        var image = HeadlessRenderer.Render(document, new RenderRequest(20));
        Assert.Equal((20, 37), (image.Width, image.Height));
        Assert.Equal(new byte[] { 18, 18, 18, 255 }, image.Pixels[..4].ToArray());
        image.Dispose();
        image.Dispose();
        Assert.Throws<ObjectDisposedException>(() => image.Pixels.ToArray());
        Assert.Throws<ObjectDisposedException>(() => image.SavePng(Stream.Null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(9)]
    public void InvalidScaleIsRejectedBeforeImageAllocation(float scale)
    {
        using var document = Document.Parse("<p>test</p>");
        Assert.Throws<ArgumentOutOfRangeException>(() => HeadlessRenderer.Render(document, new RenderRequest(20, 20, scale)));
    }

    [Fact]
    public void VeryTallPagesAreClippedBeforePixelAllocation()
    {
        using var document = Document.Parse("<body style='margin:0'><div style='height:1000000000px'></div>");
        using var image = HeadlessRenderer.Render(document, new RenderRequest(2));
        Assert.Equal(HeadlessRenderer.MaxSide, image.Height);
        Assert.Contains(image.Diagnostics, d => d.Code == DiagnosticCode.LimitExceeded);
    }

    [Fact]
    public void DefaultFamiliesHaveActualSerifMonospaceAndFallbackFonts()
    {
        var source = new BundledFontSource();
        Assert.Equal(4, source.OpenFamily("Source Code Pro").Count);
        Assert.Equal(4, source.OpenFamily("Source Serif 4").Count);
        Assert.Equal("Noto Sans Arabic", source.MatchCharacter(0x0627, 400, false));
        Assert.Equal("Noto Sans JP", source.MatchCharacter(0x3042, 400, false));
        Assert.Equal("Noto Color Emoji", source.MatchCharacter(0x1f600, 400, false));
        Assert.Empty(source.OpenFamily("not an installed family"));
    }
}
