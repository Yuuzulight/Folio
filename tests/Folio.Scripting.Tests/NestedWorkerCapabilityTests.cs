using System.Text.Json;
using Folio;
using Folio.Hosting;
using Folio.Resources;
using Folio.Scripting;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

[Collection("Worker execution deadlines")]
public sealed class NestedWorkerCapabilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedWorkerMessagingHasNoDocumentOrWindow(bool wasm)
    {
        using var fixture = new Installed(new FeatureCapabilities { Workers = true, WebAssembly = wasm }, maxWorkers: 4, parentLoop: true);
        var nested = "onmessage=function(e){postMessage(typeof document+':'+typeof window+':'+e.data);};";
        var source = "const n=new Worker('data:text/javascript,'+encodeURIComponent(" + JsonSerializer.Serialize(nested) + ")); n.onmessage=e=>postMessage(e.data); onmessage=()=>n.postMessage('ready');";
        fixture.Start(source);
        Assert.Equal("undefined:undefined:ready", fixture.Post());
    }

    [Fact]
    public void RootWebAssemblyRevocationReachesNestedWorkerAndRegrantWorks()
    {
        using var fixture = new Installed(new FeatureCapabilities { Workers = true, WebAssembly = true }, maxWorkers: 4, parentLoop: true);
        var nested = "const retained=WebAssembly; onmessage=function(){try{postMessage(new retained.Memory({initial:1}).buffer.byteLength);}catch(e){postMessage(e.name);}};";
        var source = "const n=new Worker('data:text/javascript,'+encodeURIComponent(" + JsonSerializer.Serialize(nested) + ")); n.onmessage=e=>postMessage(e.data); onmessage=()=>n.postMessage(0);";
        fixture.Start(source);
        DomBindings.UpdateCapabilities(fixture.Engine, new FeatureCapabilities { Workers = true, WebAssembly = false });
        Assert.Equal("NotSupportedError", fixture.Post());
        DomBindings.UpdateCapabilities(fixture.Engine, new FeatureCapabilities { Workers = true, WebAssembly = true });
        Assert.Equal("65536", fixture.Post());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedWasmDenialReportsThroughDocumentOwner(bool parentLoop)
    {
        using var fixture = new Installed(new FeatureCapabilities { Workers = true, WebAssembly = false }, maxWorkers: 4, parentLoop: parentLoop);
        var ownerThread = Environment.CurrentManagedThreadId;
        var originalSink = fixture.Document.Node.DiagnosticSink;
        fixture.Document.Node.DiagnosticSink = diagnostic =>
        {
            if (diagnostic.Code == DiagnosticCode.CapabilityDenied)
                Assert.Equal(ownerThread, Environment.CurrentManagedThreadId);
            originalSink?.Invoke(diagnostic);
        };
        var nested = "const retained=WebAssembly; onmessage=function(){try{new retained.Memory({initial:1});postMessage('allowed');}catch(e){postMessage(e.name);}};";
        var source = "const n=new Worker('data:text/javascript,'+encodeURIComponent(" + JsonSerializer.Serialize(nested) + ")); n.onmessage=e=>postMessage(e.data); onmessage=()=>n.postMessage(0);";
        fixture.Start(source);
        Assert.Equal("NotSupportedError", fixture.Post());
        Assert.Contains(fixture.Runner.Diagnostics, diagnostic => diagnostic.Code == DiagnosticCode.CapabilityDenied && diagnostic.Feature == "WebAssembly");
    }

    [Fact]
    public void TerminatingParentReleasesNestedWorkersAndAggregateSlots()
    {
        using var fixture = new Installed(new FeatureCapabilities { Workers = true }, maxWorkers: 2, parentLoop: true);
        var nested = "setInterval(function(){},1000); onmessage=function(){postMessage('alive');};";
        var source = "const n=new Worker('data:text/javascript,'+encodeURIComponent(" + JsonSerializer.Serialize(nested) + ")); n.onmessage=e=>postMessage(e.data); onmessage=()=>n.postMessage(0);";
        fixture.Start(source);
        Assert.Equal("alive", fixture.Post());
        Assert.Equal("QuotaExceededError", fixture.Engine.Evaluate("try { new Worker('data:text/javascript,'); 'allowed'; } catch(e) { e.name; }").AsString());
        fixture.Engine.Evaluate("worker.terminate();");
        fixture.Until(() => fixture.RootWorker.Terminated && fixture.RootWorker.ResourcesReleased);
        fixture.Start(source, "replacement");
        Assert.Equal("alive", fixture.Post("replacement"));
    }

    [Fact]
    public void ClosingParentReleasesNestedWorkerAndAllowsPairRecreation()
    {
        using var fixture = new Installed(new FeatureCapabilities { Workers = true }, maxWorkers: 2, parentLoop: true);
        var nested = "onmessage=function(){postMessage('alive');};";
        var source = "const n=new Worker('data:text/javascript,'+encodeURIComponent(" + JsonSerializer.Serialize(nested) + ")); onmessage=()=>{n.postMessage(0); close();};";
        fixture.Start(source);
        fixture.Engine.Evaluate("worker.postMessage(0)");
        fixture.Until(() => fixture.RootWorker.Terminated && fixture.RootWorker.ResourcesReleased);
        fixture.Start("const n=new Worker('data:text/javascript,'+encodeURIComponent('onmessage=()=>postMessage(\"pair\");')); n.onmessage=e=>postMessage(e.data); onmessage=()=>n.postMessage(0);", "replacement");
        Assert.Equal("pair", fixture.Post("replacement"));
    }

    private sealed class Installed : IDisposable
    {
        public JintScriptEngine Engine { get; } = new();
        public Document Document { get; }
        public HtmlEventLoop Loop { get; }
        public ScriptRunner Runner { get; }
        private readonly bool _parentLoop;

        internal Installed(FeatureCapabilities capabilities, int maxWorkers, bool parentLoop)
        {
            _parentLoop = parentLoop;
            var options = new FolioOptions
            {
                Capabilities = capabilities,
                Limits = ResourceLimits.Default with { MaxWorkers = maxWorkers, MaxWebAssemblyBytes = 65536 },
                ResourceLoader = new EmptyLoader()
            };
            Document = Document.Parse("<p>nested worker test</p>", options);
            Loop = new HtmlEventLoop(Engine);
            DomBindings.Install(Engine, Document, eventLoop: parentLoop ? Loop : null);
            if (parentLoop) ScriptGlobalsInstaller.Install(Loop, Engine);
            Runner = new ScriptRunner(Document, Engine, Loop, options);
            Engine.Evaluate("var responses=[],errors=[];");
        }

        internal WorkerState RootWorker
        {
            get { Assert.True(Engine.TryGetHostTarget(Engine.Evaluate("worker"), out var value)); return Assert.IsType<WorkerState>(value); }
        }

        internal void Start(string source, string name = "worker") => Engine.Evaluate($"var {name}=new Worker('data:text/javascript,'+encodeURIComponent({JsonSerializer.Serialize(source)})); {name}.onmessage=e=>responses.push(String(e.data)); {name}.onerror=e=>errors.push(e.message);");

        internal string Post(string name = "worker")
        {
            var before = Engine.Evaluate("responses.length").AsNumber();
            Engine.Evaluate($"{name}.postMessage(0)");
            Until(() => Engine.Evaluate("responses.length").AsNumber() > before);
            return Engine.Evaluate("responses[responses.length-1]").AsString()!;
        }

        internal void Until(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (!condition() && DateTime.UtcNow < deadline)
            {
                if (_parentLoop) Loop.SpinUntilIdle();
                Thread.Yield();
            }
            Assert.True(condition(), Engine.Evaluate("errors.join('|')").AsString());
        }

        public void Dispose() { Runner.Dispose(); Loop.Dispose(); Engine.Dispose(); Document.Dispose(); }
    }

    private sealed class EmptyLoader : IResourceLoader
    {
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken) => Task.FromResult<ResourceResponse?>(null);
    }
}
