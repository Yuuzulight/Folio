using System.Runtime.CompilerServices;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

// Forced collections must not interrupt another fixture's execution deadline.
[CollectionDefinition("Engine lifetime", DisableParallelization = true)]
public sealed class EngineLifetimeCollection;

[Collection("Engine lifetime")]
public sealed class ScriptEngineLifetimeTests
{
    [Fact]
    public void ReconfiguredEngineDoesNotRetainGeneratedWrappersFromTheRetiredRealm()
    {
        using var engine = new JintScriptEngine();
        var target = new object();
        var retired = RetireGeneratedWrapperRealm(engine, target);
        AssertCollected(retired);
        GC.KeepAlive(target);
        GC.KeepAlive(engine);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RetireGeneratedWrapperRealm(JintScriptEngine engine, object target)
    {
        var template = new HostObjectTemplate("OpaqueTestObject");
        _ = WebIdlConversions.WrapHostObject(engine, template, target);
        var retired = new WeakReference(engine.Evaluate("globalThis").Raw!);
        engine.Configure(ScriptLimits.Default);
        return retired;
    }

    [Fact]
    public void LiveDocumentDoesNotKeepIndividuallyTerminatedWorkersInItsRegistry()
    {
        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<p>worker registry</p>", new FolioOptions { Capabilities = new FeatureCapabilities { Workers = true } });
        DomBindings.Install(engine, document);
        var retired = RetireWorkerHandle(engine);
        AssertCollected(retired);
        GC.KeepAlive(document);
        GC.KeepAlive(engine);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RetireWorkerHandle(JintScriptEngine engine)
    {
        var wrapper = engine.Evaluate("new Worker('data:text/javascript,')");
        Assert.True(engine.TryGetHostTarget(wrapper, out var target));
        var worker = Assert.IsType<WorkerState>(target);
        worker.Terminate();
        Assert.True(worker.ResourcesReleased);
        engine.Evaluate("0");
        return new WeakReference(worker);
    }

    [Fact]
    public void LiveParentWorkerDoesNotRetainIndividuallyTerminatedChildren()
    {
        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<p>nested worker registry</p>", new FolioOptions { Capabilities = new FeatureCapabilities { Workers = true } });
        DomBindings.Install(engine, document);
        var retired = RetireNestedWorkerHandle(engine);
        try { AssertCollected(retired); }
        finally { DomBindings.Dispose(engine); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RetireNestedWorkerHandle(JintScriptEngine engine)
    {
        const string source = "var nested=new Worker('data:text/javascript,');onmessage=()=>{nested.terminate();nested=null;postMessage('dropped');};";
        engine.Evaluate("var parentWorker=new Worker('data:text/javascript,'+encodeURIComponent(" + System.Text.Json.JsonSerializer.Serialize(source) + "));var result='';parentWorker.onmessage=e=>result=e.data;");
        Assert.True(engine.TryGetHostTarget(engine.GetGlobalProperty("parentWorker"), out var target));
        var parent = Assert.IsType<WorkerState>(target);
        // Controlled startup is finished and both workers are idle; this reads
        // an existing plain global without running artifact code on this thread.
        Assert.True(parent.WorkerEngine!.TryGetHostTarget(parent.WorkerEngine.GetGlobalProperty("nested"), out var childTarget));
        var child = Assert.IsType<WorkerState>(childTarget);
        var marker = new WeakReference(child);
        engine.Evaluate("parentWorker.postMessage(0);");
        Assert.Equal("dropped", engine.Evaluate("result").AsString());
        Assert.True(child.ResourcesReleased);
        return marker;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void DisposedEngineDoesNotRetainItsRealmWhenTheHostKeepsItsWrapper(bool dom, bool exception)
    {
        var retired = RetireRealm(dom, exception, rebuild: false);
        try { AssertCollected(retired.Marker); }
        finally { GC.KeepAlive(retired.Engine); GC.KeepAlive(retired.Error); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReconfiguringAnEngineReleasesItsPreviousDocumentRealm(bool exception)
    {
        var retired = RetireRealm(dom: true, exception, rebuild: true);
        try
        {
            Assert.Equal(42, retired.Engine.Evaluate("21 * 2").AsNumber());
            AssertCollected(retired.Marker);
        }
        finally { retired.Engine.Dispose(); GC.KeepAlive(retired.Error); }
    }

    [Fact]
    public void DisposedEngineReleasesItsEventLoopCallback()
    {
        var retired = RetirePump();
        try { AssertCollected(retired.Marker); }
        finally { GC.KeepAlive(retired.Engine); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void KeepingATerminatedWorkerHandleDoesNotRetainItsWorkerRealm(bool reconfigureParent, bool wasm)
    {
        var retired = RetireWorker(reconfigureParent, wasm);
        try
        {
            Assert.True(retired.Worker.Terminated);
            Assert.True(retired.Worker.ResourcesReleased);
            AssertCollected(retired.Realm);
        }
        finally
        {
            retired.Parent.Dispose(); retired.Document.Dispose();
            GC.KeepAlive(retired.Worker);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (JintScriptEngine Parent, Document Document, WorkerState Worker, WeakReference Realm) RetireWorker(bool reconfigureParent, bool wasm)
    {
        var parent = new JintScriptEngine();
        var document = Document.Parse("<html></html>", new FolioOptions { Capabilities = new FeatureCapabilities { Workers = true, WebAssembly = wasm } });
        DomBindings.Install(parent, document);
        var value = parent.Evaluate("new Worker('data:text/javascript,')");
        Assert.True(parent.TryGetHostTarget(value, out var target));
        var worker = Assert.IsType<WorkerState>(target);
        // Initialization is synchronous and this empty worker is idle. Reading
        // its existing global does not execute script or enqueue work.
        var realm = new WeakReference(worker.WorkerEngine!.GetGlobalProperty("globalThis").Raw!);
        if (reconfigureParent) parent.Configure(ScriptLimits.Default);
        else worker.Terminate();
        Assert.True(SpinWait.SpinUntil(() => worker.ResourcesReleased, TimeSpan.FromSeconds(2)));
        return (parent, document, worker, realm);
    }

    private sealed record Retired(JintScriptEngine Engine, WeakReference Marker, ScriptRuntimeException? Error);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Retired RetireRealm(bool dom, bool exception, bool rebuild)
    {
        var engine = new JintScriptEngine();
        using var document = dom ? Document.Parse("<p>old document</p>") : null;
        if (document is not null)
        {
            DomBindings.Install(engine, document);
            engine.Evaluate("structuredClone({ values: new Map([[1, 2]]) });");
        }
        var marker = new byte[1024 * 1024];
        engine.SetGlobalProperty("retainedHostObject", engine.CreateHostObject(new HostObjectTemplate("Marker"), marker));
        ScriptRuntimeException? failure = null;
        if (exception)
        {
            try { engine.Evaluate("throw new Error('retired realm')"); }
            catch (ScriptRuntimeException error) { failure = error; }
        }
        if (rebuild) engine.Configure(ScriptLimits.Default);
        else engine.Dispose();
        return new(engine, new(marker), failure);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Retired RetirePump()
    {
        var engine = new JintScriptEngine();
        var marker = new byte[1024 * 1024];
        engine.SetEventLoopPump(() => { GC.KeepAlive(marker); return false; });
        engine.Dispose();
        return new(engine, new(marker), null);
    }

    private static void AssertCollected(WeakReference reference)
    {
        for (var i = 0; i < 3 && reference.IsAlive; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
        Assert.False(reference.IsAlive, "A retired script realm or callback still roots its host object.");
    }
}
