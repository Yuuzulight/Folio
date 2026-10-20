using Folio.Dom;
using Folio.Imaging;
using Folio.Interaction;
using Folio.Painting;

namespace Folio.Tests.Painting;

public sealed class FeatureFallbackTests
{
    [Theory]
    [InlineData("video", true)]
    [InlineData("audio", true)]
    [InlineData("audio", false)]
    public void AllowedIdleMediaHasNoFailureLabel(string kind, bool sound)
    {
        using var document = Document.Parse($"<{kind} controls aria-label='Player'></{kind}>",
            new FolioOptions { Capabilities = new FeatureCapabilities { Media = true, Sound = sound } });
        var element = document.Node.Descendants.OfType<ElementNode>().Single(e => e.LocalName == kind);
        Assert.Null(FeatureFallbacks.For(element));
        var (paint, _) = document.Paint(400, 200);
        Assert.DoesNotContain(paint.Items, item => item.Text?.Contains("unavailable") == true);
        Assert.DoesNotContain(Descendants(Assert.IsType<AccessibleNode>(document.AccessibilityTree())),
            node => node.Name?.Contains("unavailable") == true);
        element.SetAttribute("src", "missing.media");
        Assert.Equal(sound ? FallbackReason.SourceUnavailable : FallbackReason.HostDenied, FeatureFallbacks.For(element)?.Reason);
    }

    [Theory]
    [InlineData("inline-block")]
    [InlineData("block")]
    public void FailedCanvasKeepsUsedSizeAndExposesReason(string display)
    {
        using var document = Document.Parse($"<body style='margin:0'><canvas id='c' aria-label='Chart' style='display:{display};width:140px;height:70px'></canvas><div id='after' style='width:10px;height:10px'></div>");
        var canvas = document.Node.Descendants.OfType<ElementNode>().Single(e => e.LocalName == "canvas");
        document.Paint(400, 200);
        var before = document.GetElementBounds(canvas);
        canvas.CanvasSurface = new FailedCanvas(new(FallbackFeature.WebGl, FallbackReason.GraphicsUnavailable));
        var (paint, _) = document.Paint(400, 200);
        Assert.Equal(before, document.GetElementBounds(canvas));
        var label = Assert.Single(paint.Items, item => item.Kind == DisplayItemKind.Label);
        Assert.Equal("WebGL unavailable: graphics support missing", label.Text);
        Assert.Equal((140f, 70f), (label.Shape.Rect.Width, label.Shape.Rect.Height));
        var accessible = Descendants(Assert.IsType<AccessibleNode>(document.AccessibilityTree())).Single(n => n.Role == "image");
        Assert.Equal("Chart. WebGL unavailable: graphics support missing", accessible.Name);
    }

    [Fact]
    public void HiddenFailureDoesNotPaintOrExposeAName()
    {
        using var document = Document.Parse("<canvas style='visibility:hidden'></canvas>");
        var canvas = document.Node.Descendants.OfType<ElementNode>().Single(e => e.LocalName == "canvas");
        canvas.CanvasSurface = new FailedCanvas(new(FallbackFeature.WebGl, FallbackReason.ContextLost));
        var (paint, _) = document.Paint(400, 200);
        Assert.DoesNotContain(paint.Items, item => item.Kind == DisplayItemKind.Label);
        Assert.DoesNotContain(Descendants(Assert.IsType<AccessibleNode>(document.AccessibilityTree())), node => node.Role == "image");
    }

    [Fact]
    public void AFailureDoesNotReadStaleCanvasPixels()
    {
        using var document = Document.Parse("<canvas></canvas>");
        var canvas = document.Node.Descendants.OfType<ElementNode>().Single(e => e.LocalName == "canvas");
        canvas.CanvasSurface = new FailedCanvas(new(FallbackFeature.WebGl, FallbackReason.HostDenied));
        document.Paint(400, 200);
    }

    [Theory]
    [InlineData(ColorScheme.Light)]
    [InlineData(ColorScheme.Dark)]
    public void FailurePaletteFollowsHostScheme(ColorScheme scheme)
    {
        using var document = Document.Parse("<video style='width:100px;height:60px;color:transparent;background:transparent'></video>",
            new FolioOptions { ColorScheme = scheme });
        var (paint, _) = document.Paint(200, 100);
        var label = Assert.Single(paint.Items, item => item.Kind == DisplayItemKind.Label);
        var fill = Assert.Single(paint.Items, item => item.Kind == DisplayItemKind.Fill && item.Shape.Rect == label.Shape.Rect);
        Assert.Equal(1, fill.Color.A);
        Assert.Equal(1, label.Color.A);
        Assert.True(scheme == ColorScheme.Dark ? label.Color.R > fill.Color.R : label.Color.R < fill.Color.R);
    }

    [Theory]
    [InlineData("video")]
    [InlineData("audio")]
    [InlineData("iframe")]
    [InlineData("embed")]
    [InlineData("object")]
    public void ForeignElementsDoNotAcquireHtmlFeatureFailures(string localName)
    {
        using var document = Document.Parse("<body></body>");
        var body = document.Node.Descendants.OfType<ElementNode>().Single(e => e.LocalName == "body");
        var svg = document.Node.CreateElement(Namespaces.Svg, "svg");
        svg.SetAttribute("aria-label", "Vector scene");
        body.AppendChild(svg);
        var foreign = document.Node.CreateElement(Namespaces.Svg, localName);
        svg.AppendChild(foreign);
        foreign.SetAttribute("role", "button");
        foreign.SetAttribute("aria-label", "Vector action");
        Assert.Null(FeatureFallbacks.For(foreign));
        document.Paint(200, 100);
        var tree = Assert.IsType<AccessibleNode>(document.AccessibilityTree());
        Assert.Equal("Vector scene", Assert.Single(Descendants(tree), n => n.Role == "image").Name);
        Assert.DoesNotContain(Descendants(tree),
            n => n.Role is "video" or "audio" or "iframe");
    }

    private static IEnumerable<AccessibleNode> Descendants(AccessibleNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        foreach (var descendant in Descendants(child))
            yield return descendant;
    }

    private sealed class FailedCanvas(FeatureFallback failure) : ICanvasSurface, IFeatureFallbackSource
    {
        public int Width => 300;
        public int Height => 150;
        public FeatureFallback? Fallback => failure;
        public IImageHandle? Snapshot() => throw new InvalidOperationException("Failed canvas pixels must not be used.");
    }
}
