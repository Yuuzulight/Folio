using System.Security.Cryptography;
using System.Text;
using Folio.Dom;
using Folio.Html;
using Folio.Resources;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

/// <summary>
/// Script loading through the host's resource loader (issue #496): document order for blocking scripts, async and defer,
/// <c>load</c> and <c>error</c> on external scripts (parser-inserted and inserted at run time), and integrity.
/// </summary>
public class ScriptLoadingTests
{
    private sealed class FakeLoader : IResourceLoader
    {
        public List<ResourceRequest> Requests { get; } = [];
        public Dictionary<string, string> Scripts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult<ResourceResponse?>(Scripts.TryGetValue(request.Url, out var body)
                ? ResourceResponse.Loaded(Encoding.UTF8.GetBytes(body), "application/javascript")
                : ResourceResponse.Refused("Not found in the fake loader"));
        }
    }

    private static string Integrity(string algorithm, string body) =>
        algorithm + "-" + Convert.ToBase64String(algorithm == "sha256" ? SHA256.HashData(Encoding.UTF8.GetBytes(body)) : SHA384.HashData(Encoding.UTF8.GetBytes(body)));

    private static (string Log, FakeLoader Loader, List<Diagnostic> Diagnostics) Run(string html, FakeLoader? loader = null, FolioOptions? optionsOverride = null)
    {
        loader ??= new FakeLoader();
        var diagnostics = new List<Diagnostic>();
        var options = optionsOverride ?? new FolioOptions { Scripting = ScriptingPolicy.ContentProcess, BaseUri = new Uri("https://example.com/app/index.html"), ResourceLoader = loader };
        var document = TreeBuilder.Parse(html, new ParserLimits(ScriptingEnabled: true));
        using var engine = new JintScriptEngine();
        var loop = new HtmlEventLoop(engine);
        ScriptGlobalsInstaller.Install(loop, engine);
        DomBindings.Install(engine, document, eventLoop: loop);
        using var runner = new ScriptRunner(document, engine, loop, options, diagnostics);
        engine.Evaluate("window.log = [];");
        runner.Run();
        return (engine.Evaluate("window.log.join(';')").AsString()!, loader, diagnostics);
    }

    [Fact]
    public void ConfiguredDiagramLibraryIsReplacedAndItsSourceIsNotEvaluated()
    {
        var loader = new FakeLoader();
        loader.Scripts["https://example.com/app/mermaid.js"] = "throw new Error('library must not run');";
        var options = new FolioOptions
        {
            Scripting = ScriptingPolicy.ContentProcess,
            BaseUri = new Uri("https://example.com/app/index.html"),
            ResourceLoader = loader,
            DiagramLibraryUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "https://example.com/app/mermaid.js" }
        };
        var (log, _, diagnostics) = Run("<div class='mermaid'>flowchart LR\nA[Start] --> B[Finish]</div><script src='mermaid.js'></script><script>mermaid.run(); log.push(document.querySelector('.mermaid').innerHTML.includes('<svg'));</script>", optionsOverride: options);

        Assert.Equal("true", log);
        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCode.ScriptError);
        Assert.Contains(diagnostics, d => d.Feature == "diagrams");
    }

    [Fact]
    public void DiagramStandInInsertsAnSvgThatScriptsCanInspectAndStyle()
    {
        var loader = new FakeLoader();
        loader.Scripts["https://example.com/app/mermaid.js"] = "throw new Error('library must not run');";
        var options = new FolioOptions
        {
            Scripting = ScriptingPolicy.ContentProcess,
            BaseUri = new Uri("https://example.com/app/index.html"),
            ResourceLoader = loader,
            DiagramLibraryUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "https://example.com/app/mermaid.js" }
        };

        var (log, _, _) = Run("<div class='mermaid'>sequenceDiagram\nAlice->>Bob: Hello</div><script src='mermaid.js'></script><script>mermaid.run(); const svg = document.querySelector('.mermaid svg'); log.push(svg !== null && svg.getAttribute('class') === 'folio-diagram' && svg.querySelector('.folio-diagram-edge') !== null);</script>", optionsOverride: options);

        Assert.Equal("true", log);
    }

    [Fact]
    public void UnsupportedDiagramSyntaxRunsTheConfiguredLibrary()
    {
        var loader = new FakeLoader();
        loader.Scripts["https://example.com/app/mermaid.js"] = "mermaid = { run: function() { document.querySelector('.mermaid').innerHTML = '<p>fallback</p>'; } };";
        var options = new FolioOptions
        {
            Scripting = ScriptingPolicy.ContentProcess,
            BaseUri = new Uri("https://example.com/app/index.html"),
            ResourceLoader = loader,
            DiagramLibraryUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "https://example.com/app/mermaid.js" }
        };
        var (log, _, diagnostics) = Run("<div class='mermaid'>unsupportedDiagram x</div><script src='mermaid.js'></script><script>mermaid.run(); log.push(document.querySelector('.mermaid').textContent);</script>", optionsOverride: options);

        Assert.Equal("fallback", log);
        Assert.Contains(diagnostics, d => d.Feature == "diagrams" && d.Message.Contains("source position", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BlockingScriptsRunInDocumentOrderThenDeferThenAsyncAfterDomContentLoadedAndBeforeLoad()
    {
        var loader = new FakeLoader();
        loader.Scripts["https://example.com/app/a.js"] = "log.push('async');";
        loader.Scripts["https://example.com/app/b.js"] = "log.push('blocking');";
        loader.Scripts["https://example.com/app/d.js"] = "log.push('defer');";
        var (log, _, _) = Run(@"<!DOCTYPE html>
            <script>document.addEventListener('DOMContentLoaded', () => log.push('dcl')); window.addEventListener('load', () => log.push('load'));</script>
            <script async src='a.js'></script>
            <script src='b.js'></script>
            <script defer src='d.js'></script>
            <script>log.push('inline');</script>", loader);
        Assert.Equal("blocking;inline;defer;dcl;async;load", log);
    }

    [Fact]
    public void LoadFiresOnAnExternalScriptAfterItRunsAndDoesNotBubble()
    {
        var loader = new FakeLoader();
        loader.Scripts["https://example.com/app/ok.js"] = "log.push('ran');";
        var (log, _, _) = Run(@"<!DOCTYPE html>
            <script>document.addEventListener('load', () => log.push('bubbled'));</script>
            <script src='ok.js' onload=""log.push('onload ' + this.localName)""></script>", loader);
        Assert.Equal("ran;onload script", log);
    }

    [Fact]
    public void ErrorFiresWhenTheLoaderRefusesAndThePageCarriesOn()
    {
        var (log, _, diagnostics) = Run(@"<!DOCTYPE html>
            <script src='missing.js' onerror=""log.push('error')"" onload=""log.push('load')""></script>
            <script>log.push('after');</script>");
        Assert.Equal("error;after", log);
        Assert.Contains(diagnostics, d => d.Code == DiagnosticCode.ResourceNotLoaded);
    }

    [Fact]
    public void AnIntegrityMismatchFiresErrorAndTheScriptNeverRuns()
    {
        var loader = new FakeLoader();
        loader.Scripts["https://example.com/app/lib.js"] = "window.ran = true; log.push('ran');";
        var (log, _, diagnostics) = Run($@"<!DOCTYPE html>
            <script src='lib.js' integrity='sha256-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=' onerror=""log.push('error')"" onload=""log.push('load')""></script>
            <script>log.push('ran=' + window.ran);</script>", loader);
        Assert.Equal("error;ran=undefined", log);
        Assert.Contains(diagnostics, d => d.Code == DiagnosticCode.ResourceNotLoaded);
    }

    [Fact]
    public void AMatchingIntegrityRunsTheScriptAndTheRequestCarriesTheMetadata()
    {
        const string Body = "log.push('ran');";
        var loader = new FakeLoader();
        loader.Scripts["https://example.com/app/lib.js"] = Body;
        var integrity = Integrity("sha384", Body);
        var (log, _, _) = Run($"<!DOCTYPE html><script src='lib.js' integrity='sha256-wrong {integrity}' onload=\"log.push('load')\"></script>", loader);
        Assert.Equal("ran;load", log);
        var request = Assert.Single(loader.Requests);
        Assert.Equal(ResourceKind.Script, request.Kind);
        Assert.Equal($"sha256-wrong {integrity}", request.Integrity);
    }

    [Fact]
    public void AScriptInsertedAtRunTimeLoadsAfterTheInsertingScriptAndFiresLoad()
    {
        var loader = new FakeLoader();
        loader.Scripts["https://example.com/app/dyn.js"] = "log.push('ran');";
        var (log, _, _) = Run(@"<!DOCTYPE html><html><head></head><body><script>
            const s = document.createElement('script');
            s.src = 'dyn.js';
            s.onload = () => log.push('load');
            s.onerror = () => log.push('error');
            document.head.appendChild(s);
            log.push('after-append');
        </script></body></html>", loader);
        Assert.Equal("after-append;ran;load", log);
    }

    [Fact]
    public void AScriptInsertedAtRunTimeThatCannotLoadFiresError()
    {
        var (log, _, _) = Run(@"<!DOCTYPE html><html><head></head><body><script>
            const s = document.createElement('script');
            s.src = 'nothing.js';
            s.onload = () => log.push('load');
            s.onerror = () => log.push('error');
            document.head.appendChild(s);
            log.push('after-append');
        </script></body></html>");
        Assert.Equal("after-append;error", log);
    }

    [Fact]
    public void AnInlineScriptInsertedAtRunTimeRunsAtOnceAndFiresNoLoad()
    {
        var (log, _, _) = Run(@"<!DOCTYPE html><html><head></head><body><script>
            const s = document.createElement('script');
            s.textContent = ""log.push('inline-ran')"";
            s.onload = () => log.push('load');
            document.body.appendChild(s);
            log.push('after');
        </script></body></html>");
        Assert.Equal("inline-ran;after", log);
    }

    [Fact]
    public void CrossoriginAndReferrerpolicyAreAcceptedAndChangeNothing()
    {
        var loader = new FakeLoader();
        loader.Scripts["https://example.com/app/x.js"] = "log.push('ran');";
        var (log, _, _) = Run("<!DOCTYPE html><script src='x.js' crossorigin='anonymous' referrerpolicy='no-referrer'></script>", loader);
        Assert.Equal("ran", log);
    }
}
