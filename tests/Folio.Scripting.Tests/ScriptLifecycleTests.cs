using System.Text;
using Folio.Dom;
using Folio.Html;
using Folio.Resources;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

public class ScriptLifecycleTests
{
    private sealed class MockResourceLoader : IResourceLoader
    {
        public List<ResourceRequest> Requests { get; } = [];
        public Dictionary<string, byte[]> Responses { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Responses.TryGetValue(request.Url, out var data))
            {
                return Task.FromResult<ResourceResponse?>(ResourceResponse.Loaded(data, "application/javascript"));
            }
            return Task.FromResult<ResourceResponse?>(ResourceResponse.Refused("Not found in mock"));
        }
    }

    private static (ScriptRunner runner, JintScriptEngine engine, DocumentNode doc, List<Diagnostic> diagnostics) CreateHarness(
        string html,
        FolioOptions? options = null,
        MockResourceLoader? mockLoader = null)
    {
        var diagnostics = new List<Diagnostic>();
        var opts = options ?? new FolioOptions
        {
            Scripting = ScriptingPolicy.ContentProcess,
            BaseUri = new Uri("https://example.com/app/index.html"),
            ResourceLoader = mockLoader,
        };

        var doc = TreeBuilder.Parse(html, new ParserLimits(ScriptingEnabled: opts.Scripting != ScriptingPolicy.Disabled));
        var engine = new JintScriptEngine();
        var loop = new HtmlEventLoop(engine);
        ScriptGlobalsInstaller.Install(loop, engine);
        DomBindings.Install(engine, doc, eventLoop: loop);

        var runner = new ScriptRunner(doc, engine, loop, opts, diagnostics);
        return (runner, engine, doc, diagnostics);
    }

    [Fact]
    public void DisabledScriptingPolicyExecutesNothing()
    {
        var html = @"
            <!DOCTYPE html>
            <script>window.executed = true;</script>
        ";
        var opts = new FolioOptions { Scripting = ScriptingPolicy.Disabled };
        var (runner, engine, doc, diagnostics) = CreateHarness(html, opts);

        runner.Run();

        var val = engine.Evaluate("typeof window !== 'undefined' && window.executed");
        Assert.False(val.AsBoolean());
        Assert.Equal("loading", doc.ReadyState);
    }

    [Fact]
    public void DocumentReadyStateTransitionsAndEventsFireInOrder()
    {
        var html = @"
            <!DOCTYPE html>
            <script>
                window.log = [];
                log.push('inline-1:' + document.readyState);
                document.addEventListener('readystatechange', () => {
                    log.push('readystatechange:' + document.readyState);
                });
                document.addEventListener('DOMContentLoaded', () => {
                    log.push('DOMContentLoaded:' + document.readyState);
                });
                window.addEventListener('load', () => {
                    log.push('load:' + document.readyState);
                });
            </script>
            <script>
                log.push('inline-2:' + document.readyState);
            </script>
        ";

        var (runner, engine, doc, _) = CreateHarness(html);
        runner.Run();

        Assert.Equal("complete", doc.ReadyState);
        var log = engine.Evaluate("window.log.join(';')").AsString();

        // Execution sequence:
        // Document parse finishes -> readyState becomes 'interactive'
        // normal scripts run while readyState is interactive
        // DOMContentLoaded fires on document
        // readyState becomes 'complete'
        // load fires on window
        Assert.Equal("inline-1:interactive;inline-2:interactive;DOMContentLoaded:interactive;readystatechange:complete;load:complete", log);
    }

    [Fact]
    public void ExecutionOrder_Classic_Defer_Async_Module()
    {
        var mockLoader = new MockResourceLoader();
        mockLoader.Responses["https://example.com/app/external.js"] = Encoding.UTF8.GetBytes("window.log.push('external-normal');");
        mockLoader.Responses["https://example.com/app/defer.js"] = Encoding.UTF8.GetBytes("window.log.push('external-defer');");

        var html = @"
            <!DOCTYPE html>
            <script>window.log = [];</script>
            <script defer src='defer.js'></script>
            <script defer>window.log.push('inline-defer');</script>
            <script src='external.js'></script>
            <script>window.log.push('inline-normal');</script>
            <script type='module'>window.log.push('module-1');</script>
            <script type='module'>window.log.push('module-2');</script>
        ";

        var (runner, engine, _, _) = CreateHarness(html, mockLoader: mockLoader);
        runner.Run();

        var log = engine.Evaluate("window.log.join(';')").AsString();
        // Normal scripts first (external, inline-normal), then defer (defer.js, inline-defer), then module (module-1, module-2)
        Assert.Equal("external-normal;inline-normal;external-defer;inline-defer;module-1;module-2", log);

        // Verify request log goes through configured loader with Script kind
        var scriptReqs = mockLoader.Requests.Where(r => r.Kind == ResourceKind.Script).ToList();
        Assert.Equal(2, scriptReqs.Count);
        Assert.Equal("https://example.com/app/external.js", scriptReqs[0].Url);
        Assert.Equal("https://example.com/app/defer.js", scriptReqs[1].Url);
    }

    [Fact]
    public void DataUrlScriptLoadsAndExecutes()
    {
        var dataScript = "data:text/javascript," + Uri.EscapeDataString("window.dataRan = 42;");
        var html = $@"
            <!DOCTYPE html>
            <script src='{dataScript}'></script>
        ";

        var (runner, engine, _, _) = CreateHarness(html);
        runner.Run();

        Assert.Equal(42.0, engine.Evaluate("window.dataRan").AsNumber());
    }

    [Fact]
    public void DocumentCurrentScriptReflectsActiveElementOrNull()
    {
        var html = @"
            <!DOCTYPE html>
            <script id='s1'>
                window.s1_curr = document.currentScript ? document.currentScript.id : 'null';
            </script>
            <script type='module' id='s2'>
                window.s2_curr = document.currentScript ? document.currentScript.id : 'null';
            </script>
            <script id='s3'>
                window.s3_curr = document.currentScript ? document.currentScript.id : 'null';
            </script>
        ";

        var (runner, engine, doc, _) = CreateHarness(html);
        runner.Run();

        Assert.Equal("s1", engine.Evaluate("window.s1_curr").AsString());
        // For module scripts, currentScript is null
        Assert.Equal("null", engine.Evaluate("window.s2_curr").AsString());
        Assert.Equal("s3", engine.Evaluate("window.s3_curr").AsString());
        // Outside script execution, currentScript is null
        Assert.Null(doc.CurrentScript);
    }

    [Fact]
    public void ScriptInsertedByScriptRunsExactlyOnce()
    {
        var html = @"
            <!DOCTYPE html>
            <script>
                window.log = [];
                var s = document.createElement('script');
                s.textContent = 'window.log.push(""inserted"");';
                document.body.appendChild(s);
                // Inserting it again does not execute again (ScriptAlreadyStarted)
                document.body.appendChild(s);
            </script>
        ";

        var (runner, engine, _, _) = CreateHarness(html);
        runner.Run();

        var log = engine.Evaluate("window.log.join(';')").AsString();
        Assert.Equal("inserted", log);
    }

    [Fact]
    public void UncaughtScriptErrorEmitsDiagnosticAndDispatchesWindowErrorWhileLaterScriptsContinue()
    {
        var html = @"
            <!DOCTYPE html>
            <script>
                window.errorCount = 0;
                window.addEventListener('error', (e) => {
                    window.errorCount++;
                    window.lastErrorMsg = e.message;
                });
            </script>
            <script>
                throw new Error('Boom!');
            </script>
            <script>
                window.continued = true;
            </script>
        ";

        var (runner, engine, _, diagnostics) = CreateHarness(html);
        runner.Run();

        Assert.True(engine.Evaluate("window.continued").AsBoolean());
        Assert.Equal(1.0, engine.Evaluate("window.errorCount").AsNumber());
        Assert.Contains("Boom!", engine.Evaluate("window.lastErrorMsg").AsString());

        var scriptErrors = diagnostics.Where(d => d.Code == DiagnosticCode.ScriptError).ToList();
        var diag = Assert.Single(scriptErrors);
        Assert.Equal(Severity.Error, diag.Severity);
        Assert.Contains("Boom!", diag.Message);
        Assert.NotNull(diag.Location);
    }

    [Fact]
    public void WindowGlobals_LocationAndNavigatorAreAvailable()
    {
        var html = @"
            <!DOCTYPE html>
            <script>
                window.res = {
                    windowEqualsSelf: window === self,
                    windowEqualsGlobalThis: window === globalThis,
                    href: location.href,
                    origin: location.origin,
                    protocol: location.protocol,
                    host: location.host,
                    hostname: location.hostname,
                    pathname: location.pathname,
                    userAgent: navigator.userAgent,
                    onLine: navigator.onLine
                };
            </script>
        ";

        var (runner, engine, _, _) = CreateHarness(html);
        runner.Run();

        Assert.True(engine.Evaluate("window.res.windowEqualsSelf").AsBoolean());
        Assert.True(engine.Evaluate("window.res.windowEqualsGlobalThis").AsBoolean());
        Assert.Equal("https://example.com/app/index.html", engine.Evaluate("window.res.href").AsString());
        Assert.Equal("https://example.com", engine.Evaluate("window.res.origin").AsString());
        Assert.Equal("https:", engine.Evaluate("window.res.protocol").AsString());
        Assert.Equal("example.com", engine.Evaluate("window.res.host").AsString());
        Assert.Equal("example.com", engine.Evaluate("window.res.hostname").AsString());
        Assert.Equal("/app/index.html", engine.Evaluate("window.res.pathname").AsString());
        Assert.Equal("Folio/1.0", engine.Evaluate("window.res.userAgent").AsString());
        Assert.True(engine.Evaluate("window.res.onLine").AsBoolean());
    }
}
