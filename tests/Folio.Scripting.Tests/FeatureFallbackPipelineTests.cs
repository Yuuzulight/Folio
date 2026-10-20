using Folio.Dom;
using Folio.Painting;
using Folio.Resources;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;
using Folio.Skia;

namespace Folio.Scripting.Tests;

public sealed class FeatureFallbackPipelineTests
{
    [Fact]
    public void WebGlPermissionAndLossReachPaintAndAccessibilityThenRecover()
    {
        using var document = Document.Parse("<body style='margin:0'><canvas id='c' aria-label='Chart' width='260' height='90'></canvas>",
            new FolioOptions { Raster = new SkiaRasterBackend() });
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        engine.Evaluate("var c=document.getElementById('c'); c.getContext('webgl')");
        AssertFailure(document, "WebGL unavailable: not allowed");
        DomBindings.UpdateCapabilities(engine, new FeatureCapabilities { WebGl = true });
        engine.Evaluate("var gl=c.getContext('webgl'); gl.clearColor(1,0,0,1); gl.clear(gl.COLOR_BUFFER_BIT)");
        AssertHealthy(document);
        engine.Evaluate("var loss=gl.getExtension('WEBGL_lose_context'); loss.loseContext(); c.getContext('webgl')");
        AssertFailure(document, "WebGL unavailable: graphics connection lost");
        engine.Evaluate("loss.restoreContext()");
        AssertHealthy(document);
        DomBindings.UpdateCapabilities(engine, FeatureCapabilities.Default);
        AssertFailure(document, "WebGL unavailable: not allowed");
    }

    [Fact]
    public void RefusedFrameHasReasonAndRecoversWithInlineDocument()
    {
        var loader = new RefusingLoader();
        using var document = Document.Parse("<iframe id='f' src='https://example.test/refused' style='width:220px;height:80px'></iframe>",
            new FolioOptions { Capabilities = new FeatureCapabilities { Iframes = true }, ResourceLoader = loader });
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        engine.Evaluate("var f=document.getElementById('f'); f.contentDocument");
        AssertFailure(document, "Frame unavailable: source unavailable");
        AssertFailure(document, "Frame unavailable: source unavailable");
        Assert.Equal(1, loader.Requests);
        var frame = document.Node.Descendants.OfType<ElementNode>().Single(e => e.LocalName == "iframe");
        var failedBounds = document.GetElementBounds(frame);
        engine.Evaluate("f.srcdoc='<p>Recovered</p>'");
        AssertHealthy(document);
        Assert.Equal(failedBounds, document.GetElementBounds(frame));
    }

    [Fact]
    public void UnsupportedVideoHasReasonWithPosterStillPainted()
    {
        using var document = Document.Parse("<video id='v' src='data:application/octet-stream,broken' poster='data:image/svg+xml,%3Csvg xmlns=\"http://www.w3.org/2000/svg\" width=\"220\" height=\"80\"%3E%3Crect width=\"220\" height=\"80\" fill=\"%23237dcd\"/%3E%3C/svg%3E' style='width:220px;height:80px'></video>",
            new FolioOptions { Capabilities = new FeatureCapabilities { Media = true }, Raster = new SkiaRasterBackend() });
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        engine.Evaluate("document.getElementById('v').load()");
        var paint = AssertFailure(document, "Video unavailable: unsupported format");
        Assert.Contains(paint.Items, item => item.Kind == DisplayItemKind.FillPath);
    }

    [Theory]
    [InlineData("https://example.test/refused")]
    [InlineData("data:application/octet-stream,")]
    [InlineData("")]
    public void FailedOrEmptyMediaReturnsDecoderCapacity(string badSource)
    {
        using var document = Document.Parse($"<audio id='bad' src='{badSource}'></audio><audio id='good' src='data:application/octet-stream,A'></audio>",
            new FolioOptions
            {
                Capabilities = new FeatureCapabilities { Media = true, Sound = true },
                ResourceLoader = new RefusingLoader(), MediaDecoder = new AudioDecoder(),
                Limits = ResourceLimits.Default with { MaxMediaDecoders = 1 }
            });
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        Assert.True(engine.Evaluate("document.getElementById('good').error === null && document.getElementById('good').readyState === 4").AsBoolean());
    }

    [Fact]
    public void EmptyMediaElementIsIdleAndCanLoadAfterward()
    {
        const string poster = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==";
        using var document = Document.Parse($"<video id='v' controls poster='{poster}'></video><audio id='a'></audio>",
            new FolioOptions
            {
                Capabilities = new FeatureCapabilities { Media = true, Sound = true },
                MediaDecoder = new AudioDecoder(),
                Limits = ResourceLimits.Default with { MaxMediaDecoders = 1 }
            });
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        Assert.True(engine.Evaluate("var v=document.getElementById('v'); var a=document.getElementById('a'); v.error===null && a.error===null && v.readyState===0 && a.readyState===0 && v.networkState===0 && a.networkState===0 && v.controls").AsBoolean());
        var media = Assert.IsType<MediaElementState>(document.GetElementById("v")!.Node.MediaPresentation);
        Assert.Null(media.Fallback);
        Assert.Equal((1, 1), (media.Poster!.Width, media.Poster.Height));
        AssertHealthy(document);
        engine.Evaluate("a.src='data:application/octet-stream,A'");
        Assert.True(engine.Evaluate("a.error===null && a.readyState===4").AsBoolean());
    }

    [Fact]
    public void OversizedWebGlShowsLimitAndClearsWhenResized()
    {
        using var document = Document.Parse("<canvas id='c' width='20000' style='width:220px;height:80px'></canvas>",
            new FolioOptions { Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend() });
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        engine.Evaluate("var c=document.getElementById('c'); c.getContext('webgl')");
        AssertFailure(document, "WebGL unavailable: resource limit reached");
        engine.Evaluate("c.width=20");
        AssertHealthy(document);
    }

    [Fact]
    public void Choosing2DAfterDeniedWebGlClearsItsFailureAndLaterWebGlDoesNotHide2D()
    {
        using var document = Document.Parse("<canvas id='c'></canvas>", new FolioOptions { Raster = new SkiaRasterBackend() });
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        engine.Evaluate("var c=document.getElementById('c'); c.getContext('webgl')");
        AssertFailure(document, "WebGL unavailable: not allowed");
        engine.Evaluate("var two=c.getContext('2d'); two.fillStyle='red'; two.fillRect(0,0,10,10); c.getContext('webgl')");
        AssertHealthy(document);
        Assert.Contains(document.DisplayList!.Items, item => item.Kind == DisplayItemKind.Image);
    }

    [Theory]
    [InlineData("navigation", "close")]
    [InlineData("navigation", "revoke")]
    [InlineData("navigation", "change")]
    [InlineData("navigation", "dispose")]
    [InlineData("loader", "close")]
    [InlineData("loader", "revoke")]
    [InlineData("loader", "change")]
    [InlineData("loader", "dispose")]
    public void HostCallbackCannotCommitRetiredOrSupersededFrame(string hook, string action)
    {
        using var engine = new JintScriptEngine();
        Document? currentDocument = null;
        void ChangeLifetime()
        {
            var frame = Assert.IsType<IframeState>(currentDocument!.GetElementById("f")!.Node.FramePresentation);
            switch (action)
            {
                case "close": frame.Close(); break;
                case "revoke": DomBindings.UpdateCapabilities(engine, FeatureCapabilities.Default); break;
                case "dispose": DomBindings.Dispose(engine); break;
                case "change": frame.Element.SetAttribute("srcdoc", "<p>Changed</p>"); frame.Invalidate(); break;
            }
        }
        var loader = new CallbackFrameLoader(hook == "loader" ? ChangeLifetime : null);
        using var document = Document.Parse("<iframe id='f' src='https://example.test/old'></iframe>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Iframes = true },
            ResourceLoader = loader,
            IframeNavigationRequested = hook == "navigation" ? _ => ChangeLifetime() : null
        });
        currentDocument = document;
        DomBindings.Install(engine, document);
        engine.Evaluate("document.getElementById('f').contentDocument");
        var state = Assert.IsType<IframeState>(document.GetElementById("f")!.Node.FramePresentation);
        Assert.Null(state.ChildDocument);
        Assert.Null(((IFramePresentation)state).DisplayList);
        Assert.Equal(hook == "navigation" ? 0 : 1, loader.Requests);
        if (action == "change")
        {
            document.Paint(400, 200);
            Assert.Contains("Changed", state.ChildDocument!.DocumentElement!.TextContent);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReentrantEngineFactoryDisposesAbortedStartupAndPreservesReplacement(bool replace)
    {
        using var document = Document.Parse("<iframe srcdoc='<p>Old</p>'></iframe>");
        IframeState? frame = null;
        using var parent = new ReentrantFactory(() =>
        {
            if (!replace) { frame!.Close(); return; }
            frame!.Element.SetAttribute("srcdoc", "<p>Newest</p>");
            frame.Invalidate();
            frame.LoadInlineDocument(dispatchLoad: false);
        });
        frame = new IframeState(parent, new HostObjectTemplate("MessageEvent"),
            document.Node.Descendants.OfType<ElementNode>().Single(e => e.LocalName == "iframe"), enabled: true,
            parentOptions: new FolioOptions { Scripting = ScriptingPolicy.ContentProcess, Capabilities = new FeatureCapabilities { Iframes = true } });
        try
        {
            frame.LoadInlineDocument(dispatchLoad: false);
            Assert.Throws<ObjectDisposedException>(() => parent.Children[0].Evaluate("1"));
            if (replace)
            {
                Assert.Equal(2, parent.Children.Count);
                Assert.Equal(42, parent.Children[1].Evaluate("6*7").AsNumber());
                Assert.Contains("Newest", frame.ChildDocument!.DocumentElement!.TextContent);
            }
            else Assert.Null(frame.ChildDocument);
        }
        finally { frame.Close(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SyntheticStreamReplacementClearsFailureOnlyWithMediaPermission(bool allowed)
    {
        using var document = Document.Parse("<video id='v' src='data:application/octet-stream,broken' style='width:220px;height:80px'></video>",
            new FolioOptions { Capabilities = new FeatureCapabilities { Media = allowed } });
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        AssertFailure(document, allowed ? "Video unavailable: unsupported format" : "Video unavailable: not allowed");
        engine.Evaluate("var v=document.getElementById('v'); v.srcObject=new MediaStream([new MediaStreamTrack('video')])");
        if (allowed)
        {
            AssertHealthy(document);
            Assert.True(engine.Evaluate("v.error===null").AsBoolean());
            var image = Assert.Single(document.DisplayList!.Items, item => item.Kind == DisplayItemKind.Image).Image!;
            Assert.Equal((640, 360), (image.Width, image.Height));
        }
        else
        {
            var paint = AssertFailure(document, "Video unavailable: not allowed");
            Assert.DoesNotContain(paint.Items, item => item.Kind == DisplayItemKind.Image);
        }
    }

    [Fact]
    public void DecodedVideoSourceRecoveryPaintsItsFrame()
    {
        using var document = Document.Parse("<video id='v' src='data:application/octet-stream,broken'></video>",
            new FolioOptions { Capabilities = new FeatureCapabilities { Media = true } });
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        AssertFailure(document, "Video unavailable: unsupported format");
        engine.Evaluate("document.getElementById('v').src='data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg=='");
        AssertHealthy(document);
        var image = Assert.Single(document.DisplayList!.Items, item => item.Kind == DisplayItemKind.Image).Image!;
        Assert.Equal((1, 1), (image.Width, image.Height));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, image.Pixels.ToArray());
    }

    [Fact]
    public void RetiringChildCannotClearReplacementCreatedByAudioStopCallback()
    {
        using var document = Document.Parse("<iframe allow='media; sound' srcdoc='<audio src=\"data:application/octet-stream,a\"></audio>'></iframe>");
        using var parent = new ReentrantFactory(() => { });
        var output = new SwitchingOutput();
        var frame = new IframeState(parent, new HostObjectTemplate("MessageEvent"),
            document.Node.Descendants.OfType<ElementNode>().Single(e => e.LocalName == "iframe"), enabled: true,
            parentOptions: new FolioOptions
            {
                Scripting = ScriptingPolicy.ContentProcess,
                Capabilities = new FeatureCapabilities { Iframes = true, Media = true, Sound = true },
                MediaDecoder = new AudioDecoder(), MediaAudioOutput = output
            });
        try
        {
            frame.LoadInlineDocument(dispatchLoad: false);
            output.OnStop = () =>
            {
                output.OnStop = null;
                frame.Element.SetAttribute("srcdoc", "<p>Newest</p>");
                frame.Invalidate();
                frame.LoadInlineDocument(dispatchLoad: false);
            };
            frame.Invalidate();
            Assert.Equal(2, parent.Children.Count);
            Assert.Throws<ObjectDisposedException>(() => parent.Children[0].Evaluate("1"));
            Assert.Equal(42, parent.Children[1].Evaluate("6*7").AsNumber());
            Assert.Contains("Newest", frame.ChildDocument!.DocumentElement!.TextContent);
        }
        finally { output.OnStop = null; frame.Close(); }
    }

    private static DisplayList AssertFailure(Document document, string reason)
    {
        var (paint, _) = document.Paint(400, 200);
        Assert.Contains(paint.Items, item => item.Kind == DisplayItemKind.Label && item.Text == reason);
        Assert.Contains(reason, document.AccessibilityTree()!.Dump());
        return paint;
    }

    private static void AssertHealthy(Document document)
    {
        var (paint, _) = document.Paint(400, 200);
        Assert.DoesNotContain(paint.Items, item => item.Text?.Contains("unavailable:", StringComparison.Ordinal) == true);
        Assert.DoesNotContain("unavailable:", document.AccessibilityTree()!.Dump());
    }

    private sealed class RefusingLoader : IResourceLoader
    {
        public int Requests { get; private set; }
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult<ResourceResponse?>(ResourceResponse.Refused("blocked by test host"));
        }
    }

    private sealed class AudioDecoder : IMediaDecoder
    {
        public MediaDecodeResult? Decode(ReadOnlyMemory<byte> data, string? contentType, bool video, int maxOutputBytes) =>
            new(1, null, new MediaAudioFrame(8000, 1, new float[8]));
    }

    private sealed class CallbackFrameLoader(Action? callback) : IResourceLoader
    {
        public int Requests { get; private set; }
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            Requests++;
            callback?.Invoke();
            return Task.FromResult<ResourceResponse?>(ResourceResponse.Loaded("<p>Stale</p>"u8.ToArray(), "text/html"));
        }
    }

    private sealed class ReentrantFactory(Action onFirstCreation) : IScriptEngine, IScriptEngineFactory
    {
        private readonly JintScriptEngine _inner = new();
        public List<JintScriptEngine> Children { get; } = [];
        public IScriptEngine CreateIsolated()
        {
            var child = new JintScriptEngine();
            Children.Add(child);
            if (Children.Count == 1) onFirstCreation();
            return child;
        }
        public void Configure(ScriptLimits limits) => _inner.Configure(limits);
        public ScriptValue Evaluate(string source, string url = "eval") => _inner.Evaluate(source, url);
        public ScriptValue Call(ScriptValue function, ScriptValue thisValue, ReadOnlySpan<ScriptValue> args) => _inner.Call(function, thisValue, args);
        public ScriptValue CreateHostObject(IHostObjectTemplate template, object target) => _inner.CreateHostObject(template, target);
        public bool TryGetHostTarget(ScriptValue value, out object? target) => _inner.TryGetHostTarget(value, out target);
        public ScriptValue CreateArray(ReadOnlySpan<ScriptValue> elements) => _inner.CreateArray(elements);
        public void SetGlobalProperty(string name, ScriptValue value) => _inner.SetGlobalProperty(name, value);
        public ScriptValue GetGlobalProperty(string name) => _inner.GetGlobalProperty(name);
        public void PerformMicrotaskCheckpoint() => _inner.PerformMicrotaskCheckpoint();
        public void Interrupt() => _inner.Interrupt();
        public void Dispose()
        {
            foreach (var child in Children) child.Dispose();
            _inner.Dispose();
        }
    }

    private sealed class SwitchingOutput : IMediaAudioOutput, IMediaAudioOutputLifecycle
    {
        public Action? OnStop { get; set; }
        public void Submit(MediaAudioFrame frame) { }
        public void Start() { }
        public void Pause() { }
        public void Flush() { }
        public void Stop() => OnStop?.Invoke();
    }
}
