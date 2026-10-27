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

    private static JintScriptEngine CreateEngine(FeatureCapabilities capabilities)
    {
        var engine = new JintScriptEngine();
        var document = Document.Parse("<html><body></body></html>", new FolioOptions
        {
            BaseUri = new Uri("https://parent.example/"),
            Capabilities = capabilities,
            Scripting = ScriptingPolicy.ContentProcess,
        });
        DomBindings.Install(engine, document);
        return engine;
    }
}
