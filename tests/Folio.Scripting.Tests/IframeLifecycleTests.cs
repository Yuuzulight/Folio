using System.Text;
using Folio.Dom;
using Folio.Painting;
using Folio.Resources;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;
using Folio.Scripting.Native;

namespace Folio.Scripting.Tests;

public sealed class IframeLifecycleTests
{
    [Fact]
    public void NativeEngineSandboxedIframeHonorsOriginAndScriptPolicy()
    {
        using var document = Document.Parse("<iframe id='f' sandbox='allow-scripts' srcdoc=\"<script>document.body.setAttribute('data-ran','yes')</script>\"></iframe>", Options());
        using var engine = new NativeJavaScriptEngine();
        DomBindings.Install(engine, document);
        engine.Evaluate("var f=document.getElementById('f'); f.contentWindow.document;");
        document.Paint(300, 200);
        Assert.Equal("null", engine.Evaluate("f.contentWindow.origin").AsString());
        Assert.Null(engine.Evaluate("f.contentDocument").Raw);
        var child = Assert.IsType<Document>(document.QuerySelector("#f")!.Node.FramePresentation!.ChildDocument);
        Assert.Equal("yes", child.QuerySelector("body")!.GetAttribute("data-ran"));
    }

    [Fact]
    public void NativeEngineIframeMessagesCrossTheWindowBoundary()
    {
        using var document = Document.Parse("<iframe id='f'></iframe>", Options());
        using var engine = new NativeJavaScriptEngine();
        DomBindings.Install(engine, document);
        engine.Evaluate("var parentValue=0; var f=document.getElementById('f'); f.srcdoc=\"<script>addEventListener('message',function(e){ parent.postMessage({answer:e.data.answer+1}, '*'); });</script>\"; f.contentDocument; f.contentWindow.onmessage=function(e){parentValue=e.data.answer;}; f.contentWindow.postMessage({answer:6}, 'https://parent.example');");
        Assert.Equal(7, engine.Evaluate("parentValue").AsNumber());
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", false)]
    [InlineData("allow-same-origin", false)]
    [InlineData("allow-scripts allow-same-origin", true)]
    public void SandboxAndHostPolicyGateChildScripts(string? sandbox, bool runs)
    {
        using var document = Document.Parse("<iframe id='f'></iframe>", Options());
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        try
        {
            engine.SetGlobalProperty("sandboxValue", sandbox is null ? ScriptValue.Null : ScriptValue.FromString(sandbox));
            engine.Evaluate("var f=document.getElementById('f'); if(sandboxValue !== null) f.sandbox=sandboxValue; f.srcdoc=\"<script>document.body.setAttribute('data-ran','yes')</script>\"; f.contentWindow.document;");
            document.Paint(300, 200);
            var child = Assert.IsType<Document>(document.QuerySelector("#f")!.Node.FramePresentation!.ChildDocument);
            Assert.Equal(runs, child.QuerySelector("body")!.GetAttribute("data-ran") == "yes");
            if (sandbox == "") Assert.True(engine.Evaluate("f.contentDocument === null && f.contentWindow.origin === 'null'").AsBoolean());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void ChildGlobalIsIsolatedAndRelativeScriptsUseChildLocation()
    {
        var loader = new Loader();
        using var document = Document.Parse("<iframe id='f' src='child/page.html'></iframe>", Options(loader));
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        try
        {
            Assert.True(engine.Evaluate("var parentSecret=42; var child=document.getElementById('f').contentDocument; child.body.getAttribute('data-isolated')==='undefined' && typeof childSecret==='undefined'").AsBoolean());
            Assert.Contains("https://parent.example/root/child/asset.js", loader.Urls);
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void DisabledHostScriptingCannotBeEnabledBySandboxToken()
    {
        using var document = Document.Parse("<iframe id='f' sandbox='allow-scripts allow-same-origin' srcdoc=\"<script>document.body.setAttribute('data-ran','yes')</script>\"></iframe>", Options(scripting: ScriptingPolicy.Disabled));
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        try { Assert.True(engine.Evaluate("document.getElementById('f').contentDocument.body.getAttribute('data-ran') === null").AsBoolean()); }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void ThrowingLiveIframePermissionFailsClosed()
    {
        using var document = Document.Parse("<iframe id='f' srcdoc='<p>child</p>'></iframe>");
        using var engine = new JintScriptEngine();
        var element = document.Node.Descendants.OfType<ElementNode>().Single(node => node.LocalName == "iframe");
        var frame = new IframeState(engine, new HostObjectTemplate("MessageEvent"), element, enabled: true,
            parentOptions: new FolioOptions { Capabilities = new FeatureCapabilities { Iframes = true } },
            checkPermission: () => throw new InvalidOperationException("permission probe failed"));
        frame.LoadInlineDocument(dispatchLoad: false);
        Assert.Null(frame.ChildDocument);
        Assert.Equal(FallbackReason.HostDenied, frame.Fallback?.Reason);
        frame.Close();
    }

    [Fact]
    public void SandboxedChildBlocksFormSubmissionUnlessAllowFormsIsGranted()
    {
        using var blockedDocument = Document.Parse("<form id='f'></form>", new FolioOptions
        {
            BaseUri = new Uri("https://parent.example/root/index.html"),
            Capabilities = new FeatureCapabilities { Iframes = true },
            Scripting = ScriptingPolicy.ContentProcess,
            FormsAllowed = false
        });
        using var blockedEngine = new JintScriptEngine();
        DomBindings.Install(blockedEngine, blockedDocument);
        try { Assert.Equal("SecurityError", blockedEngine.Evaluate("try{document.getElementById('f').submit();'allowed';}catch(e){e.name}").AsString()); }
        finally { DomBindings.Dispose(blockedEngine); }

        using var allowedDocument = Document.Parse("<form id='f'></form>", Options());
        using var allowedEngine = new JintScriptEngine();
        DomBindings.Install(allowedEngine, allowedDocument);
        try { Assert.Equal("allowed", allowedEngine.Evaluate("try{document.getElementById('f').submit();'allowed';}catch(e){e.name}").AsString()); }
        finally { DomBindings.Dispose(allowedEngine); }
    }

    [Fact]
    public void RevokingIframesRemovesChildAndRegrantCreatesFreshContext()
    {
        using var document = Document.Parse("<iframe id='f' srcdoc='<p>child</p>'></iframe>", Options());
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("var f=document.getElementById('f'); var previous=f.contentDocument;");
            DomBindings.UpdateCapabilities(engine, FeatureCapabilities.Default);
            Assert.True(engine.Evaluate("f.contentDocument === null").AsBoolean());
            Assert.Null(document.QuerySelector("#f")!.Node.FramePresentation!.ChildDocument);
            DomBindings.UpdateCapabilities(engine, new FeatureCapabilities { Iframes = true });
            Assert.True(engine.Evaluate("f.contentDocument !== null && f.contentDocument !== previous").AsBoolean());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void RegrantCannotEnableMoreThanHostIframeLimit()
    {
        using var document = Document.Parse("<iframe id='a' srcdoc='<p>a</p>'></iframe><iframe id='b' srcdoc='<p>b</p>'></iframe>", new FolioOptions
        {
            BaseUri = new Uri("https://parent.example/"),
            Limits = ResourceLimits.Default with { MaxIframes = 1 }
        });
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("var a=document.getElementById('a'); var b=document.getElementById('b'); a.contentDocument; b.contentDocument;");
            DomBindings.UpdateCapabilities(engine, new FeatureCapabilities { Iframes = true });
            Assert.True(engine.Evaluate("a.contentDocument !== null && b.contentDocument === null").AsBoolean());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NestedFramesShareTheAggregateIframeLimit()
    {
        using var document = Document.Parse("<iframe id='root' srcdoc=\"<iframe id='nested' srcdoc='<p>nested</p>'></iframe>\"></iframe>", new FolioOptions
        {
            BaseUri = new Uri("https://parent.example/"),
            Capabilities = new FeatureCapabilities { Iframes = true },
            Limits = ResourceLimits.Default with { MaxIframes = 1 }
        });
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        try
        {
            Assert.True(engine.Evaluate("var root=document.getElementById('root'); root.contentDocument !== null").AsBoolean());
            Assert.True(engine.Evaluate("var nested=root.contentDocument.getElementById('nested'); var nestedBlocked=false; try { nested.contentDocument; } catch(e) { nestedBlocked=true; } nestedBlocked").AsBoolean());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NavigationAndParentTeardownStopChildHostMedia()
    {
        var output = new AudioOutput();
        var decoder = new AudioDecoder();
        using var document = Document.Parse("<iframe id='f' allow='media'></iframe>", new FolioOptions
        {
            BaseUri = new Uri("https://parent.example/"),
            Capabilities = new FeatureCapabilities { Iframes = true, Media = true },
            Scripting = ScriptingPolicy.ContentProcess,
            MediaAudioOutput = output, MediaDecoder = decoder
        });
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("""var f=document.getElementById('f'); f.srcdoc="<audio src='data:audio/test;base64,AAAAAA=='></audio>"; f.contentDocument;""");
            Assert.Equal(1, decoder.Calls);
            var child = Assert.IsType<Document>(document.GetElementById("f")!.Node.FramePresentation!.ChildDocument);
            Assert.Same(output, child.Options.MediaAudioOutput);
            engine.Evaluate("f.srcdoc='<p>replacement</p>'");
            Assert.Equal(1, output.Stops);
            engine.Evaluate("""f.srcdoc="<audio src='data:audio/test;base64,AAAAAA=='></audio>"; f.contentDocument;""");
            Assert.Equal(2, decoder.Calls);
        }
        finally { DomBindings.Dispose(engine); }
        Assert.Equal(2, output.Stops);
    }

    private sealed class AudioDecoder : IMediaDecoder
    {
        public int Calls;
        public MediaDecodeResult? Decode(ReadOnlyMemory<byte> data, string? type, bool video, int maxOutputBytes)
        { Calls++; return new(1, null, new MediaAudioFrame(8000, 1, new float[4])); }
    }
    private sealed class AudioOutput : IMediaAudioOutput, IMediaAudioOutputLifecycle
    {
        public int Stops;
        public void Submit(MediaAudioFrame frame) { }
        public void Start() { }
        public void Pause() { }
        public void Flush() { }
        public void Stop() => Stops++;
    }

    [Theory]
    [InlineData(true, "workers", true)]
    [InlineData(true, "", false)]
    [InlineData(false, "workers", false)]
    [InlineData(true, "workers 'none'", false)]
    [InlineData(true, "workers https://other.example", false)]
    [InlineData(true, "workers *", true)]
    [InlineData(true, "workers 'self'", true)]
    [InlineData(true, "workers 'src'", true)]
    [InlineData(true, "WORKERS\t'self'", true)]
    public void FrameAllowPolicyCanOnlyNarrowHostWorkerGrant(bool hostWorkers, string allow, bool granted)
    {
        using var document = Document.Parse("<iframe id='f'></iframe>", new FolioOptions
        {
            BaseUri = new Uri("https://parent.example/"),
            Scripting = ScriptingPolicy.ContentProcess,
            Capabilities = new FeatureCapabilities { Iframes = true, Workers = hostWorkers }
        });
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        try
        {
            engine.SetGlobalProperty("frameAllow", ScriptValue.FromString(allow));
            engine.Evaluate("var f=document.getElementById('f'); f.allow=frameAllow; f.srcdoc='<p>child</p>'; f.contentDocument;");
            var child = Assert.IsType<Document>(document.GetElementById("f")!.Node.FramePresentation!.ChildDocument);
            Assert.Equal(granted, child.Options.Capabilities.Workers);
        }
        finally { DomBindings.Dispose(engine); }
    }

    private static FolioOptions Options(IResourceLoader? loader = null, ScriptingPolicy scripting = ScriptingPolicy.ContentProcess, FeatureCapabilities? capabilities = null) => new()
    {
        BaseUri = new Uri("https://parent.example/root/index.html"),
        Capabilities = capabilities ?? new FeatureCapabilities { Iframes = true },
        ResourceLoader = loader,
        Scripting = scripting
    };

    private sealed class Loader : IResourceLoader
    {
        public List<string> Urls { get; } = [];
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            Urls.Add(request.Url);
            var script = request.Url.EndsWith("asset.js", StringComparison.Ordinal);
            var source = script ? "var childSecret=7; document.body.setAttribute('data-isolated',typeof parentSecret)" : "<script src='asset.js'></script>";
            return Task.FromResult<ResourceResponse?>(ResourceResponse.Loaded(Encoding.UTF8.GetBytes(source), script ? "text/javascript" : "text/html"));
        }
    }

    [Fact]
    public void ParentAndChildExchangeStructuredMessagesAcrossWindowBoundary()
    {
        using var document = Document.Parse("<iframe id='f'></iframe>", Options());
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("var parentValue=0; var f=document.getElementById('f'); f.srcdoc=\"<script>document.body.setAttribute('data-parent', typeof parent + ':' + typeof parent.postMessage); onmessage=function(e){parent.postMessage({answer:e.data.answer+1}, '*')}</script>\"; f.contentDocument; f.contentWindow.onmessage=function(e){parentValue=e.data.answer}; f.contentWindow.postMessage({answer:6}, 'https://parent.example');");
            var firstChild = Assert.IsType<Document>(document.GetElementById("f")!.Node.FramePresentation!.ChildDocument);
            Assert.Equal("object:function", firstChild.QuerySelector("body")!.GetAttribute("data-parent"));
            Assert.Equal(7, engine.Evaluate("parentValue").AsNumber());

            engine.Evaluate("f.srcdoc=\"<script>onmessage=function(e){document.body.setAttribute('data-answer', String(e.data.answer))}</script>\"; f.contentDocument; f.contentWindow.postMessage({answer:9}, 'https://parent.example');");
            var child = Assert.IsType<Document>(document.GetElementById("f")!.Node.FramePresentation!.ChildDocument);
            Assert.Equal("9", child.QuerySelector("body")!.GetAttribute("data-answer"));

            engine.Evaluate("f.srcdoc=\"<script>onmessage=function(e){e.data.postMessage(11)}</script>\"; f.contentDocument; var channel=new MessageChannel(); var portValue=0; channel.port1.onmessage=function(e){portValue=e.data}; f.contentWindow.postMessage(channel.port2, 'https://parent.example', [channel.port2]);");
            Assert.Equal(11, engine.Evaluate("portValue").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void WindowMessagesExposeCorrectOriginSourceAndTarget()
    {
        using var document = Document.Parse("<iframe id='f'></iframe>", Options());
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("""
                var f = document.getElementById('f');
                var parentSource = null, parentTarget = null, parentOrigin = '';
                f.srcdoc = "<script>onmessage=function(e){ parent.postMessage('from-child', '*'); }</script>";
                f.contentDocument;
                f.contentWindow.onmessage = function(e) { parentMessage = [e.origin, e.source === f.contentWindow, e.target === f.contentWindow]; };
                f.contentWindow.postMessage('from-parent', '*');
                var parentMessageResult = parentMessage;
                """);
            var child = Assert.IsType<Document>(document.GetElementById("f")!.Node.FramePresentation!.ChildDocument);
            Assert.Equal("https://parent.example", engine.Evaluate("parentMessageResult[0]").AsString());
            Assert.True(engine.Evaluate("parentMessageResult[1]").AsBoolean());
            Assert.True(engine.Evaluate("parentMessageResult[2]").AsBoolean());

            engine.Evaluate("f.contentWindow.onmessage = function(e) { parentSource = e.source; parentTarget = e.target; parentOrigin = e.origin; }; f.contentWindow.postMessage('again', '*');");
            // srcdoc inherits the embedding document's origin.
            Assert.Equal("https://parent.example", engine.Evaluate("parentOrigin").AsString());
            Assert.True(engine.Evaluate("parentSource === f.contentWindow").AsBoolean());
            Assert.True(engine.Evaluate("parentTarget === f.contentWindow").AsBoolean());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void WindowMessagesEnforceTargetOriginAndExposeOpaqueOrigin()
    {
        using var document = Document.Parse("<iframe id='f'></iframe>", Options());
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("var f=document.getElementById('f'); f.srcdoc=\"<script>onmessage=function(e){ parent.postMessage(e.data, '*'); }</script>\"; f.contentDocument; var received=0; f.contentWindow.onmessage=function(e){received++}; f.contentWindow.postMessage(1, 'https://wrong.example');");
            Assert.Equal(0, engine.Evaluate("received").AsNumber());
            engine.Evaluate("f.contentWindow.postMessage(1, '*');");
            Assert.Equal(1, engine.Evaluate("received").AsNumber());

            using var opaqueDocument = Document.Parse("<iframe id='opaque' sandbox='allow-scripts'></iframe>", Options());
            using var opaqueEngine = new JintScriptEngine();
            DomBindings.Install(opaqueEngine, opaqueDocument);
            opaqueEngine.Evaluate("var f=document.getElementById('opaque'); f.srcdoc=\"<script>onmessage=function(e){ parent.postMessage('reply', '*'); }</script>\"; f.contentDocument; var origin=''; f.contentWindow.onmessage=function(e){origin=e.origin}; f.contentWindow.postMessage(1, 'null');");
            Assert.Equal("null", opaqueEngine.Evaluate("origin").AsString());
            DomBindings.Dispose(opaqueEngine);
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void TransferredPortCanCrossAnIframeIntoAWorkerInOrder()
    {
        var options = Options(capabilities: new FeatureCapabilities { Iframes = true, Workers = true });
        using var document = Document.Parse("<iframe id='f' allow='workers'></iframe>", options);
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("var f=document.getElementById('f'); f.srcdoc=\"<script>onmessage=function(e){var p=e.data;var w=new Worker('data:text/javascript,'+encodeURIComponent('onmessage=function(x){postMessage(x.data+1)}'));w.onmessage=function(x){p.postMessage(x.data)};w.postMessage(40)}</script>\"; f.contentDocument; var ch=new MessageChannel(); var values=[]; ch.port1.onmessage=function(e){values.push(e.data)}; f.contentWindow.postMessage(ch.port2, '*', [ch.port2]);");
            Assert.True(SpinWait.SpinUntil(() => { document.Paint(100, 100); return engine.Evaluate("values.length").AsNumber() > 0; }, TimeSpan.FromSeconds(2)));
            Assert.Equal(41, engine.Evaluate("values[0]").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }
}
