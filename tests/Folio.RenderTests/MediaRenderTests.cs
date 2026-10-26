using Folio.Dom;
using Folio.Painting;
using Folio.Skia;
using Folio.Typography;
using SkiaSharp;

namespace Folio.RenderTests;

public sealed class MediaRenderTests
{
    [Theory]
    [InlineData(ColorScheme.Light, 255, 255, 255)]
    [InlineData(ColorScheme.Dark, 18, 18, 18)]
    public void ControlsAndActiveCuesPaintInBothColorSchemes(ColorScheme scheme, byte backgroundRed, byte backgroundGreen, byte backgroundBlue)
    {
        using var document = Document.Parse(
            $"<body style='margin:0;background:rgb({backgroundRed},{backgroundGreen},{backgroundBlue})'><video controls style='width:200px;height:100px'></video></body>",
            new FolioOptions { ColorScheme = scheme, Fonts = TestRenderer.Options.Fonts,
                Capabilities = new FeatureCapabilities { Media = true } });
        var video = document.Node.Descendants.OfType<ElementNode>().Single(e => e.LocalName == "video");
        video.MediaPresentation = new RenderMedia();

        var (displayList, _) = document.Paint(240, 140);
        Assert.Contains(displayList.Items, item => item.Text == "Caption");
        Assert.Contains(displayList.Items, item => item.Text is { } text && text.Contains("Ⅱ") && text.Contains("0:04 / 0:12"));

        using var rendered = HeadlessRenderer.Render(document, new RenderRequest(240, 140));
        var controlPixel = Pixel(rendered, 100, 90);
        Assert.True(controlPixel.Red < 80 && controlPixel.Green < 80 && controlPixel.Blue < 80,
            $"expected dark controls, got {controlPixel}");

        var actual = new PixelBuffer(rendered.Width, rendered.Height, rendered.Pixels.ToArray());
        var golden = $"media/runtime-controls-and-cues-{scheme.ToString().ToLowerInvariant()}";
        Assert.Null(GoldenSuite.Repo.Check(golden, actual));
    }

    [Theory]
    [InlineData("contain", 200, 50)]
    [InlineData("cover", 400, 100)]
    public void VideoFramesHonorObjectFitAndClipToTheContentBox(string fit, float expectedWidth, float expectedHeight)
    {
        using var document = Document.Parse($"<video style='margin:0;width:200px;height:100px;object-fit:{fit}'></video>",
            new FolioOptions { Fonts = TestRenderer.Options.Fonts, Capabilities = new FeatureCapabilities { Media = true } });
        var video = document.Node.Descendants.OfType<ElementNode>().Single(e => e.LocalName == "video");
        video.MediaPresentation = new RenderFrameMedia();

        var (displayList, _) = document.Paint(220, 120);
        var image = Assert.Single(displayList.Items, item => item.Kind == DisplayItemKind.Image);
        Assert.Equal(expectedWidth, image.Shape.Rect.Width, 3);
        Assert.Equal(expectedHeight, image.Shape.Rect.Height, 3);
        if (fit == "cover") Assert.Contains(displayList.Items, item => item.Kind == DisplayItemKind.PushClip);
    }

    [Theory]
    [InlineData("fill")]
    [InlineData("contain")]
    [InlineData("cover")]
    [InlineData("none")]
    [InlineData("scale-down")]
    public void VideoFrameGoldensCoverEveryObjectFitMode(string fit)
    {
        using var document = Document.Parse($"<body style='margin:0;background:#20242a'><video style='width:320px;height:180px;object-fit:{fit}'></video></body>",
            new FolioOptions { Fonts = TestRenderer.Options.Fonts, Capabilities = new FeatureCapabilities { Media = true } });
        var video = document.Node.Descendants.OfType<ElementNode>().Single(e => e.LocalName == "video");
        video.MediaPresentation = new RenderFrameMedia();
        using var rendered = HeadlessRenderer.Render(document, new RenderRequest(360, 220));
        var actual = new PixelBuffer(rendered.Width, rendered.Height, rendered.Pixels.ToArray());
        Assert.Null(GoldenSuite.Repo.Check($"media/video-frame-{fit}", actual));
    }

    [Fact]
    public void VideoPosterGoldenCoversPausedPosterState()
    {
        using var document = Document.Parse("<body style='margin:0;background:#20242a'><video style='width:320px;height:180px'></video></body>",
            new FolioOptions { Fonts = TestRenderer.Options.Fonts, Capabilities = new FeatureCapabilities { Media = true } });
        var video = document.Node.Descendants.OfType<ElementNode>().Single(e => e.LocalName == "video");
        video.MediaPresentation = new RenderPosterMedia();
        using var rendered = HeadlessRenderer.Render(document, new RenderRequest(360, 220));
        var actual = new PixelBuffer(rendered.Width, rendered.Height, rendered.Pixels.ToArray());
        Assert.Null(GoldenSuite.Repo.Check("media/video-poster", actual));
    }

    private static SKColor Pixel(RenderResult result, int x, int y)
    {
        var p = result.Pixels.Slice((y * result.Width + x) * 4, 4);
        return new SKColor(p[2], p[1], p[0], p[3]);
    }

    private sealed class RenderMedia : IMediaPresentation
    {
        public bool Controls => true;
        public bool Paused => false;
        public bool Muted => false;
        public double CurrentTime => 4;
        public double Duration => 12;
        public double Volume => 1;
        public IMediaPoster? Poster => null;
        public IReadOnlyList<IMediaCue> ActiveCues { get; } = [new Cue()];
        public void TogglePlayPause() { }
        public void SeekBy(double seconds) { }
        public void ToggleMute() { }
        public void SetVolume(double volume) { }
    }

    private sealed class Cue : IMediaCue
    {
        public string Text => "Caption";
    }

    private sealed class RenderFrameMedia : IMediaPresentation, IMediaFrameSource
    {
        public bool Controls => false;
        public bool Paused => false;
        public bool Muted => false;
        public double CurrentTime => 0;
        public double Duration => 1;
        public double Volume => 1;
        public IMediaPoster? Poster => null;
        public IMediaPoster? CurrentFrame { get; } = new Frame(4, 1, true);
        public IReadOnlyList<IMediaCue> ActiveCues { get; } = [];
        public void TogglePlayPause() { }
        public void SeekBy(double seconds) { }
        public void ToggleMute() { }
        public void SetVolume(double volume) { }
    }

    private sealed class RenderPosterMedia : IMediaPresentation
    {
        public bool Controls => false;
        public bool Paused => true;
        public bool Muted => false;
        public double CurrentTime => 0;
        public double Duration => 1;
        public double Volume => 1;
        public IMediaPoster? Poster { get; } = new Frame(4, 1, false);
        public IReadOnlyList<IMediaCue> ActiveCues { get; } = [];
        public void TogglePlayPause() { }
        public void SeekBy(double seconds) { }
        public void ToggleMute() { }
        public void SetVolume(double volume) { }
    }

    private sealed class Frame(int width, int height, bool frame) : IMediaPoster
    {
        public int Width => width;
        public int Height => height;
        public ReadOnlyMemory<byte> Pixels { get; } = CreatePixels(width, height, frame);

        private static byte[] CreatePixels(int width, int height, bool frame)
        {
            var pixels = new byte[width * height * 4];
            for (var i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = frame ? (byte)220 : (byte)40;
                pixels[i + 1] = frame ? (byte)80 : (byte)180;
                pixels[i + 2] = frame ? (byte)40 : (byte)220;
                pixels[i + 3] = 255;
            }
            return pixels;
        }
    }
}
