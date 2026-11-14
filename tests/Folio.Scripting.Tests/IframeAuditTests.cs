using Folio;
using Folio.Hosting;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

public sealed class IframeAuditTests
{
    [Fact]
    public void IframeSrcdocRunsScriptsWhenSandboxAllowsScriptsAndInheritsWorkerCapability()
    {
        using var engine = CreateEngine(new FeatureCapabilities { Iframes = true, Workers = true });
        try
        {
            var ran = engine.Evaluate("""
                const frame = document.createElement('iframe');
                frame.setAttribute('sandbox', 'allow-scripts allow-same-origin');
                frame.allow = 'workers';
                frame.srcdoc = "<script>document.body.setAttribute('data-ran', typeof Worker)</script>";
                document.body.appendChild(frame);
                frame.contentDocument.body.getAttribute('data-ran') === 'function';
                """).AsBoolean();
            Assert.True(ran);
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void IframeNavigationCleansUpHandlersFromThePreviousChildContext()
    {
        using var engine = CreateEngine(new FeatureCapabilities { Iframes = true });
        try
        {
            var cleaned = engine.Evaluate("""
                const frame = document.createElement('iframe');
                frame.srcdoc = '<p>first</p>';
                document.body.appendChild(frame);
                let messages = 0;
                frame.contentWindow.onmessage = () => messages++;
                frame.contentWindow.postMessage('before', '*');
                frame.srcdoc = '<p>second</p>';
                frame.contentWindow.postMessage('after', '*');
                messages === 1;
                """).AsBoolean();
            Assert.True(cleaned);
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void IframeWithoutAllowWebRtcDeniesRTCPeerConnection()
    {
        using var engine = CreateEngine(new FeatureCapabilities { Iframes = true, WebRtc = true });
        try
        {
            var result = engine.Evaluate("""
                const frame = document.createElement('iframe');
                frame.setAttribute('sandbox', 'allow-scripts allow-same-origin');
                frame.srcdoc = "<script>var res = ''; try { new RTCPeerConnection(); res = 'allowed'; } catch(e) { res = e.name; } document.body.setAttribute('data-res', res);</script>";
                document.body.appendChild(frame);
                frame.contentDocument.body.getAttribute('data-res');
                """).AsString();
            Assert.Equal("NotSupportedError", result);
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void IframeWithExplicitAllowWebRtcNoneDeniesRTCPeerConnection()
    {
        using var engine = CreateEngine(new FeatureCapabilities { Iframes = true, WebRtc = true });
        try
        {
            var result = engine.Evaluate("""
                const frame = document.createElement('iframe');
                frame.setAttribute('sandbox', 'allow-scripts allow-same-origin');
                frame.allow = "webrtc 'none'";
                frame.srcdoc = "<script>var res = ''; try { new RTCPeerConnection(); res = 'allowed'; } catch(e) { res = e.name; } document.body.setAttribute('data-res', res);</script>";
                document.body.appendChild(frame);
                frame.contentDocument.body.getAttribute('data-res');
                """).AsString();
            Assert.Equal("NotSupportedError", result);
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void IframeWithAllowWebRtcAllowsRTCPeerConnection()
    {
        using var engine = CreateEngine(new FeatureCapabilities { Iframes = true, WebRtc = true });
        try
        {
            var result = engine.Evaluate("""
                const frame = document.createElement('iframe');
                frame.setAttribute('sandbox', 'allow-scripts allow-same-origin');
                frame.allow = "webrtc";
                frame.srcdoc = "<script>var res = ''; try { var pc = new RTCPeerConnection(); res = pc.connectionState; } catch(e) { res = e.name; } document.body.setAttribute('data-res', res);</script>";
                document.body.appendChild(frame);
                frame.contentDocument.body.getAttribute('data-res');
                """).AsString();
            Assert.Equal("new", result);
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void IframeInheritsParentIceEndpointsAndAllowTokensDoNotWidenThem()
    {
        var parentEndpoints = new HashSet<string>(["stun:stun.example:3478", "turn:turn.example"], StringComparer.OrdinalIgnoreCase);
        var (engine, document) = CreateEngineAndDocument(new FeatureCapabilities { Iframes = true, WebRtc = true }, parentEndpoints);
        try
        {
            engine.Evaluate("""
                const frame = document.createElement('iframe');
                frame.id = 'target';
                frame.setAttribute('sandbox', 'allow-scripts');
                frame.allow = "webrtc https://other.example stun:evil.example:3478";
                frame.srcdoc = "<p>child</p>";
                document.body.appendChild(frame);
                frame.contentWindow.document;
                """);
            var childDoc = Assert.IsType<Document>(document.QuerySelector("#target")!.Node.FramePresentation!.ChildDocument);
            Assert.True(parentEndpoints.SetEquals(childDoc.Options.WebRtcAllowedOrigins));
        }
        finally
        {
            DomBindings.Dispose(engine);
            document.Dispose();
        }
    }

    [Fact]
    public void CrossOriginSrcIframeWithoutAllowDeniesRTCPeerConnection()
    {
        var loader = new CrossOriginLoader();
        using var document = Document.Parse("<iframe id='f' sandbox='allow-scripts' src='https://other.example/page.html'></iframe>", new FolioOptions
        {
            BaseUri = new Uri("https://parent.example/"),
            Capabilities = new FeatureCapabilities { Iframes = true, WebRtc = true },
            Scripting = ScriptingPolicy.ContentProcess,
            ResourceLoader = loader,
        });
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("document.getElementById('f').contentDocument;");
            var child = Assert.IsType<Document>(document.GetElementById("f")!.Node.FramePresentation!.ChildDocument);
            Assert.Equal("NotSupportedError", child.QuerySelector("body")!.GetAttribute("data-res"));
        }
        finally { DomBindings.Dispose(engine); }
    }

    private sealed class CrossOriginLoader : Folio.Resources.IResourceLoader
    {
        public Task<Folio.Resources.ResourceResponse?> LoadAsync(Folio.Resources.ResourceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<Folio.Resources.ResourceResponse?>(Folio.Resources.ResourceResponse.Loaded(
                System.Text.Encoding.UTF8.GetBytes("<script>var res=''; try { new RTCPeerConnection(); res='allowed'; } catch(e) { res=e.name; } document.body.setAttribute('data-res', res);</script>"),
                "text/html"));
    }

    private static JintScriptEngine CreateEngine(FeatureCapabilities capabilities)
    {
        var (engine, _) = CreateEngineAndDocument(capabilities);
        return engine;
    }

    private static (JintScriptEngine Engine, Document Document) CreateEngineAndDocument(FeatureCapabilities capabilities, IReadOnlySet<string>? webRtcAllowedOrigins = null)
    {
        var engine = new JintScriptEngine();
        var document = Document.Parse("<html><body></body></html>", new FolioOptions
        {
            BaseUri = new Uri("https://parent.example/"),
            Capabilities = capabilities,
            Scripting = ScriptingPolicy.ContentProcess,
            WebRtcAllowedOrigins = webRtcAllowedOrigins ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        });
        DomBindings.Install(engine, document);
        return (engine, document);
    }
}
