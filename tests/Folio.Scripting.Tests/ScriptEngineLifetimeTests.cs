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
    [InlineData(false)]
    [InlineData(true)]
    public void KeepingATerminatedWorkerHandleDoesNotRetainItsWorkerRealm(bool reconfigureParent)
    {
        var retired = RetireWorker(reconfigureParent);
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
    private static (JintScriptEngine Parent, Document Document, WorkerState Worker, WeakReference Realm) RetireWorker(bool reconfigureParent)
    {
        var parent = new JintScriptEngine();
        var document = Document.Parse("<html></html>", new FolioOptions { Capabilities = new FeatureCapabilities { Workers = true } });
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
