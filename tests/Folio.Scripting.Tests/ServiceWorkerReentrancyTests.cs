using System.Text;
using Folio.Resources;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

public sealed class ServiceWorkerReentrancyTests
{
    [Fact]
    public void SkipWaitingBeforeInfiniteLoopStillUsesBoundedValidation()
    {
        using var engine = new JintScriptEngine();
        engine.Configure(new ScriptLimits { Timeout = TimeSpan.FromSeconds(3) });
        using var document = Document.Parse("<p>service worker</p>", new FolioOptions
        {
            BaseUri = new Uri("https://worker.example/"),
            Capabilities = new FeatureCapabilities { ServiceWorkers = true },
            Limits = ResourceLimits.Default with { MaxWorkerExecutionTime = TimeSpan.FromMilliseconds(50) },
            ResourceLoader = new InfiniteLoader()
        });
        DomBindings.Install(engine, document);
        engine.Evaluate("var result='pending';navigator.serviceWorker.register('/worker.js').then(()=>result='allowed',e=>result=e.name);");
        engine.PerformMicrotaskCheckpoint();
        Assert.Equal("TimeoutError", engine.Evaluate("result").AsString());
    }

    private sealed class InfiniteLoader : IResourceLoader
    {
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<ResourceResponse?>(ResourceResponse.Loaded(Encoding.UTF8.GetBytes("self.skipWaiting();while(true){}"), "application/javascript"));
    }

    [Fact]
    public void RevocationDuringUpdateFoundPreventsReplacementScriptExecution()
    {
        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<p>service worker</p>", new FolioOptions
        {
            BaseUri = new Uri("https://worker.example/"),
            Capabilities = new FeatureCapabilities { ServiceWorkers = true },
            ResourceLoader = new Loader()
        });
        DomBindings.Install(engine, document);
        var bridge = new HostObjectTemplate("HostRevocation").DefineMethod("revoke", (_, _, _) =>
        {
            DomBindings.UpdateCapabilities(engine, FeatureCapabilities.Default);
            DomBindings.UpdateCapabilities(engine, new FeatureCapabilities { ServiceWorkers = true });
            return ScriptValue.Undefined;
        });
        engine.SetGlobalProperty("hostControl", engine.CreateHostObject(bridge, new object()));
        engine.Evaluate("var registration; navigator.serviceWorker.register('/worker.js').then(r=>registration=r);");
        engine.PerformMicrotaskCheckpoint();
        Assert.Equal(1, engine.Evaluate("executions").AsNumber());
        engine.Evaluate("var result='pending';registration.onupdatefound=()=>hostControl.revoke();registration.update().then(()=>result='allowed',e=>result=e.name);");
        engine.PerformMicrotaskCheckpoint();
        Assert.Equal("InvalidStateError", engine.Evaluate("result").AsString());
        Assert.Equal(1, engine.Evaluate("executions").AsNumber());
        Assert.True(engine.Evaluate("navigator.serviceWorker.controller === null").AsBoolean());
    }

    [Fact]
    public void FetchResponsePendingAcrossRevokeAndRegrantCannotDeliverOldWorkerResponse()
    {
        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<p>service worker</p>", new FolioOptions
        {
            BaseUri = new Uri("https://worker.example/"),
            Capabilities = new FeatureCapabilities { ServiceWorkers = true },
            ResourceLoader = new Loader()
        });
        DomBindings.Install(engine, document);
        engine.Evaluate("var registration; navigator.serviceWorker.register('/worker.js').then(r=>registration=r);");
        engine.PerformMicrotaskCheckpoint();
        engine.Evaluate("var response='pending';registration.active._dispatchFetch(new Request('https://worker.example/data')).then(()=>response='allowed',e=>response=e.name);");
        DomBindings.UpdateCapabilities(engine, FeatureCapabilities.Default);
        DomBindings.UpdateCapabilities(engine, new FeatureCapabilities { ServiceWorkers = true });
        engine.Evaluate("finishResponse(new Response('old'));");
        engine.PerformMicrotaskCheckpoint();
        Assert.Equal("InvalidStateError", engine.Evaluate("response").AsString());
    }

    private sealed class Loader : IResourceLoader
    {
        private int _loads;
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            var script = ++_loads == 1
                ? "globalThis.executions=1;self.onfetch=e=>e.respondWith(new Promise(resolve=>globalThis.finishResponse=resolve));"
                : "globalThis.executions+=100;";
            return Task.FromResult<ResourceResponse?>(ResourceResponse.Loaded(Encoding.UTF8.GetBytes(script), "application/javascript"));
        }
    }
}
