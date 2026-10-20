using Folio.Dom;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;
using Folio.Resources;

namespace Folio.Scripting.Tests;

public sealed class FeatureFallbackBindingsTests
{
    [Fact]
    public void WebGlDenialIsTypedAndRegrantClearsIt()
    {
        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<canvas id='c'></canvas>", new FolioOptions { Capabilities = FeatureCapabilities.Default });
        DomBindings.Install(engine, document);
        engine.Evaluate("document.getElementById('c').getContext('webgl2')");
        var canvas = Assert.IsType<CanvasElementState>(document.Node.Descendants.OfType<ElementNode>().Single(e => e.LocalName == "canvas").CanvasSurface);
        Assert.Equal(new FeatureFallback(FallbackFeature.WebGl, FallbackReason.HostDenied), canvas.Fallback);
        DomBindings.UpdateCapabilities(engine, new FeatureCapabilities { WebGl = true });
        engine.Evaluate("document.getElementById('c').getContext('webgl2')");
        Assert.Equal(new FeatureFallback(FallbackFeature.WebGl, FallbackReason.GraphicsUnavailable), canvas.Fallback);
    }

    [Fact]
    public void WebGlContextLossAndRestoreAreReportedAtTheTransition()
    {
        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<canvas id='c'></canvas>", new FolioOptions { Capabilities = new FeatureCapabilities { WebGl = true } });
        DomBindings.Install(engine, document);
        engine.Evaluate("var gl=document.getElementById('c').getContext('webgl'); var loss=gl.getExtension('WEBGL_lose_context'); loss.loseContext()");
        var canvas = Assert.IsType<CanvasElementState>(document.Node.Descendants.OfType<ElementNode>().Single(e => e.LocalName == "canvas").CanvasSurface);
        Assert.Equal(new FeatureFallback(FallbackFeature.WebGl, FallbackReason.ContextLost), canvas.Fallback);
        engine.Evaluate("loss.restoreContext()");
        Assert.Equal(new FeatureFallback(FallbackFeature.WebGl, FallbackReason.GraphicsUnavailable), canvas.Fallback);
    }

    [Fact]
    public void ValidEmptyIframeDoesNotBecomeFallback()
    {
        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<iframe id='f' srcdoc=''></iframe>", new FolioOptions { Capabilities = new FeatureCapabilities { Iframes = true } });
        DomBindings.Install(engine, document);
        engine.Evaluate("document.getElementById('f').contentDocument");
        var frame = Assert.IsType<IframeState>(document.Node.Descendants.OfType<ElementNode>().Single(e => e.LocalName == "iframe").FramePresentation);
        Assert.Null(frame.Fallback);
        frame.Close();
        Assert.Equal(new FeatureFallback(FallbackFeature.Iframes, FallbackReason.Closed), frame.Fallback);
    }

    [Fact]
    public void ThrowingMediaLoaderBecomesTypedSourceFailure()
    {
        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<audio id='a' src='https://example.test/a.wav'></audio>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Media = true },
            ResourceLoader = new ThrowingLoader()
        });
        DomBindings.Install(engine, document);
        engine.Evaluate("document.getElementById('a').load()");
        var media = Assert.IsType<MediaElementState>(document.Node.Descendants.OfType<ElementNode>().Single(e => e.LocalName == "audio").MediaPresentation);
        Assert.Equal(new FeatureFallback(FallbackFeature.Audio, FallbackReason.SourceRefused), media.Fallback);
    }

    private sealed class ThrowingLoader : IResourceLoader
    {
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException("loader failure");
    }
}
