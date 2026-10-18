using System.Text;
using Folio.Resources;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

public sealed class IframeLifecycleTests
{
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

    private static FolioOptions Options(IResourceLoader? loader = null, ScriptingPolicy scripting = ScriptingPolicy.ContentProcess) => new()
    {
        BaseUri = new Uri("https://parent.example/root/index.html"),
        Capabilities = new FeatureCapabilities { Iframes = true },
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
}
