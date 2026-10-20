using Folio.Dom;
using Folio.Imaging;
using Folio.Painting;
using Folio.Skia;
using Folio.Typography;
using SkiaSharp;

namespace Folio.RenderTests;

/// <summary>Renderer-level coverage for feature failures.  The source objects below are deliberately host fakes: no
/// browser or scripting backend is involved, so these tests exercise only replaced-element painting and layout.</summary>
public sealed class FeatureFallbackRenderTests
{
    private static FontSettings Fonts { get; } = new()
    {
        Source = TestRenderer.Options.Fonts.Source,
        GenericFamilies = TestRenderer.Options.Fonts.GenericFamilies.Keys.ToDictionary(f => f,
            _ => (IReadOnlyList<string>)["Source Sans 3"], StringComparer.OrdinalIgnoreCase),
    };
    private static readonly FallbackReason[] Reasons = Enum.GetValues<FallbackReason>();

    public static TheoryData<string, ColorScheme> FallbackCases
    {
        get
        {
            var cases = new TheoryData<string, ColorScheme>();
            foreach (var reason in Reasons)
                foreach (var scheme in new[] { ColorScheme.Light, ColorScheme.Dark })
                    cases.Add(reason.ToString(), scheme);
            return cases;
        }
    }

    public static IReadOnlySet<string> GoldenNames { get; } =
        new HashSet<string>(Reasons.SelectMany(reason => new[] { "", "-linux" }
            .SelectMany(platform => new[] { "light", "dark" }.Select(scheme =>
                $"feature-fallback/{reason.ToString().ToLowerInvariant()}{platform}-{scheme}"))), StringComparer.Ordinal);

    [Theory]
    [MemberData(nameof(FallbackCases))]
    public void EveryFallbackReasonHasAStableLabelInBothColorSchemes(string reasonName, ColorScheme scheme)
    {
        var reason = Enum.Parse<FallbackReason>(reasonName);
        using var document = CreateDocument($"<canvas id='c' width='360' height='90'></canvas>", scheme);
        var canvas = Canvas(document);
        canvas.CanvasSurface = new FakeCanvasSurface(new(FallbackFeature.WebGl, reason));

        var (displayList, _) = document.Paint(360, 120);
        var label = Assert.Single(displayList.Items, item => item.Kind == DisplayItemKind.Label);
        Assert.Equal(new FeatureFallback(FallbackFeature.WebGl, reason).Label, label.Text);

        using var result = HeadlessRenderer.Render(document, new RenderRequest(360, 120));
        Assert.Equal((360, 120), (result.Width, result.Height));
        Assert.Equal(scheme == ColorScheme.Dark ? new SKColor(18, 18, 18) : SKColors.White, Pixel(result, 359, 119));
        // Skia's glyph rasterization differs across the Windows and Linux font backends.
        // Each platform retains reviewed exact-pixel references; geometry and label assertions are shared.
        var platform = OperatingSystem.IsLinux() ? "-linux" : "";
        var golden = $"feature-fallback/{reasonName.ToLowerInvariant()}{platform}-{scheme.ToString().ToLowerInvariant()}";
        var actual = new PixelBuffer(result.Width, result.Height, result.Pixels.ToArray());
        Assert.Null(GoldenSuite.Repo.Check(golden, actual));
    }

    [Fact]
    public void FallbackVisibilityPreservesCanvasAndAdjacentMarkerGeometry()
    {
        const string body = "<canvas id='c' width='120' height='56' style='display:inline-block;visibility:hidden'></canvas>" +
                            "<span id='marker' style='display:inline-block;width:24px;height:56px;background:#27a'>x</span>";
        using var failed = CreateDocument(body, ColorScheme.Light);
        Canvas(failed).CanvasSurface = new FakeCanvasSurface(new(FallbackFeature.WebGl, FallbackReason.ContextLost));
        using var healthy = CreateDocument(body, ColorScheme.Light);
        Canvas(healthy).CanvasSurface = new FakeCanvasSurface(null);

        failed.Paint(240, 80);
        healthy.Paint(240, 80);
        var failedBounds = failed.GetElementBounds(Canvas(failed))!.Value;
        var healthyBounds = healthy.GetElementBounds(Canvas(healthy))!.Value;
        Assert.Equal((healthyBounds.X, healthyBounds.Y, healthyBounds.Width, healthyBounds.Height),
            (failedBounds.X, failedBounds.Y, failedBounds.Width, failedBounds.Height));

        using var failedImage = HeadlessRenderer.Render(failed, new RenderRequest(240, 80));
        using var healthyImage = HeadlessRenderer.Render(healthy, new RenderRequest(240, 80));
        var actual = new PixelBuffer(failedImage.Width, failedImage.Height, failedImage.Pixels.ToArray());
        var expected = new PixelBuffer(healthyImage.Width, healthyImage.Height, healthyImage.Pixels.ToArray());
        Assert.True(ImageComparer.Compare(expected, actual, Tolerance.Exact).Passed,
            "A visibility-hidden fallback must not paint or alter the adjacent marker.");
        var marker = failed.Node.Descendants.OfType<ElementNode>().Single(e => e.GetAttribute("id") == "marker");
        var markerBounds = failed.GetElementBounds(marker)!.Value;
        Assert.Equal(new SKColor(34, 119, 170), Pixel(failedImage, (int)(markerBounds.X + markerBounds.Width / 2), (int)(markerBounds.Y + markerBounds.Height / 2)));
    }

    [Theory]
    [InlineData(ColorScheme.Light)]
    [InlineData(ColorScheme.Dark)]
    public void DisabledVideoFallbackRetainsStaticPoster(ColorScheme scheme)
    {
        using var document = CreateDocument("<video id='v' disabled controls style='width:160px;height:90px'></video>", scheme);
        var video = document.Node.Descendants.OfType<ElementNode>().Single(e => e.LocalName == "video");
        video.MediaPresentation = new FakeMedia(new(FallbackFeature.Video, FallbackReason.HostDenied));

        using var result = HeadlessRenderer.Render(document, new RenderRequest(200, 120));
        // The poster occupies the lower portion; the failure strip occupies the first 28 CSS pixels.
        Assert.Equal(new SKColor(35, 125, 205), Pixel(result, 80, 70));
        Assert.NotEqual(Pixel(result, 80, 70), scheme == ColorScheme.Dark ? new SKColor(33, 38, 46) : new SKColor(237, 240, 245));
    }

    [Theory]
    [InlineData("png", "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=")]
    [InlineData("svg", "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='160' height='90'%3E%3Crect width='160' height='90' fill='%23237dcd'/%3E%3C/svg%3E")]
    public void StaticVideoPosterSourcesAreRetainedWhenMediaIsDisabled(string format, string poster)
    {
        Assert.StartsWith($"data:image/{format}", poster);
        using var document = CreateDocument($"<video disabled poster=\"{poster}\" style='width:160px;height:90px'></video>", ColorScheme.Light);
        var video = document.Node.Descendants.OfType<ElementNode>().Single(e => e.LocalName == "video");
        var (displayList, _) = document.Paint(200, 120);
        Assert.Equal("video", video.LocalName);
        Assert.Contains(displayList.Items, item => format == "svg" ? item.Kind == DisplayItemKind.FillPath : item.Kind == DisplayItemKind.Image);

        using var result = HeadlessRenderer.Render(document, new RenderRequest(200, 120));
        Assert.NotEqual(SKColors.White, Pixel(result, 80, 70));
    }

    private static Document CreateDocument(string body, ColorScheme scheme) =>
        Document.Parse($"<html><body style='margin:0;background:{(scheme == ColorScheme.Dark ? "#121212" : "white")}'>{body}</body></html>", new FolioOptions
        {
            ColorScheme = scheme,
            Fonts = Fonts,
        });

    [Fact]
    public void AllowedIdleVideoPaintsItsPosterWithoutAFailureStrip()
    {
        using var document = Document.Parse("<body style='margin:0'><video controls style='width:160px;height:90px' " +
            "poster=\"data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='160' height='90'%3E%3Crect width='160' height='90' fill='%23237dcd'/%3E%3C/svg%3E\"></video>",
            new FolioOptions { Fonts = Fonts, Capabilities = new FeatureCapabilities { Media = true } });
        var (displayList, _) = document.Paint(200, 120);
        Assert.DoesNotContain(displayList.Items, item => item.Text?.Contains("unavailable") == true);
        using var result = HeadlessRenderer.Render(document, new RenderRequest(200, 120));
        Assert.Equal(new SKColor(35, 125, 205), Pixel(result, 80, 10));
        Assert.Equal(new SKColor(35, 125, 205), Pixel(result, 80, 70));
    }

    private static ElementNode Canvas(Document document) =>
        document.Node.Descendants.OfType<ElementNode>().Single(e => e.LocalName == "canvas");

    private static SKColor Pixel(RenderResult result, int x, int y)
    {
        var p = result.Pixels.Slice((y * result.Width + x) * 4, 4);
        return new SKColor(p[2], p[1], p[0], p[3]);
    }

    private sealed class FakeCanvasSurface(FeatureFallback? fallback) : ICanvasSurface, IFeatureFallbackSource
    {
        public int Width => 120;
        public int Height => 56;
        public FeatureFallback? Fallback => fallback;
        public IImageHandle? Snapshot() => null;
    }

    private sealed class FakeMedia(FeatureFallback fallback) : IMediaPresentation, IFeatureFallbackSource
    {
        public bool Controls => true;
        public bool Paused => true;
        public bool Muted => true;
        public double CurrentTime => 0;
        public double Duration => 0;
        public double Volume => 0;
        public IMediaPoster Poster { get; } = new SolidPoster(35, 125, 205);
        public IReadOnlyList<IMediaCue> ActiveCues => [];
        FeatureFallback? IFeatureFallbackSource.Fallback => fallback;
        public void TogglePlayPause() { }
        public void SeekBy(double seconds) { }
        public void ToggleMute() { }
        public void SetVolume(double volume) { }
    }

    private sealed class SolidPoster(byte red, byte green, byte blue) : IMediaPoster
    {
        public int Width => 160;
        public int Height => 90;
        public ReadOnlyMemory<byte> Pixels { get; } = CreatePixels(red, green, blue);

        private static byte[] CreatePixels(byte red, byte green, byte blue)
        {
            var pixels = new byte[160 * 90 * 4];
            for (var i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = red;
                pixels[i + 1] = green;
                pixels[i + 2] = blue;
                pixels[i + 3] = 255;
            }
            return pixels;
        }
    }
}
