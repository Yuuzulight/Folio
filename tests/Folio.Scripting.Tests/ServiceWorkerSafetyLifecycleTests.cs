using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Folio.Resources;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;
using Folio.Scripting.Native;

namespace Folio.Scripting.Tests;

public sealed class ServiceWorkerSafetyLifecycleTests
{
    private const string Origin = "https://worker.example/";

    [Fact]
    public async Task LoadTimeoutDoesNotNeedAnAvailableThreadPoolThread()
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("service-worker-load-deadline-probe");
        using var process = Process.Start(start)!;
        var cancellation = TestContext.Current.CancellationToken;
        try
        {
            await process.WaitForExitAsync(cancellation).WaitAsync(TimeSpan.FromSeconds(10), cancellation);
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    internal static int RunLoadDeadlineProbe()
    {
        ThreadPool.GetMinThreads(out _, out var minIo);
        ThreadPool.GetMaxThreads(out _, out var maxIo);
        if (!ThreadPool.SetMinThreads(1, minIo) || !ThreadPool.SetMaxThreads(1, maxIo)) return 2;
        return Task.Run(() =>
        {
            var loader = new PendingLoader();
            try
            {
                ServiceWorkerBindings.LoadScript(loader, Origin + "sw.js", CancellationToken.None, TimeSpan.FromMilliseconds(75));
                return 1;
            }
            catch (WebIdlException error) when (error.Name == "TimeoutError" && loader.Token.IsCancellationRequested)
            {
                return 0;
            }
        }).GetAwaiter().GetResult();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonCooperatingRegistrationLoadTimesOutAndCannotEvaluateLateScript(bool native)
    {
        using var engine = Engine(native);
        var loader = new PendingLoader();
        using var document = Install(engine, loader, TimeSpan.FromMilliseconds(75));
        var watch = Stopwatch.StartNew();
        engine.Evaluate("var result = ''; navigator.serviceWorker.register('/sw.js').catch(e => result = e.name);");
        Drain(engine);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Equal("TimeoutError", engine.Evaluate("result").AsString());
        Assert.True(loader.Token.IsCancellationRequested);
        loader.Complete("self.onactivate = function() { self.clients.claim(); }; self.onfetch = e => e.respondWith(new Response('late'));");
        Drain(engine);
        Assert.True(engine.Evaluate("navigator.serviceWorker.controller === null").AsBoolean());
        Assert.Equal(42, engine.Evaluate("21 * 2").AsNumber());
        AssertBoundedRetirement(engine, document);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonCooperatingImportIsBoundedAndCannotRunAfterTimeout(bool native)
    {
        using var engine = Engine(native);
        var loader = new PendingLoader("importScripts('blocked.js'); self.onactivate = function() { self.clients.claim(); };");
        using var document = Install(engine, loader, TimeSpan.FromMilliseconds(100));
        var watch = Stopwatch.StartNew();
        engine.Evaluate("var registration; var error = ''; navigator.serviceWorker.register('/sw.js').then(r => registration = r, e => error = e.name || e.message);");
        Drain(engine);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.True(loader.Token.IsCancellationRequested);
        Assert.False(engine.Evaluate("!!registration").AsBoolean());
        loader.Complete("self.onactivate = function() { self.clients.claim(); };");
        Drain(engine);
        Assert.True(engine.Evaluate("navigator.serviceWorker.controller === null").AsBoolean());
        Assert.Equal(42, engine.Evaluate("40 + 2").AsNumber());
        AssertBoundedRetirement(engine, document);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeferredInstallRemainsInstallingUntilAllLifetimePromisesSettle(bool native)
    {
        using var engine = Engine(native);
        var loader = new ScriptLoader("""
            var finishOne, finishTwo;
            self.oninstall = function(e) {
                e.waitUntil(new Promise(function(resolve) { finishOne = resolve; }));
                e.waitUntil(new Promise(function(resolve) { finishTwo = resolve; }));
            };
            self.onmessage = function(e) { if (e.data === 1) finishOne(); else finishTwo(); };
            self.onactivate = function(e) { e.waitUntil(self.clients.claim()); };
            """);
        using var document = Install(engine, loader);
        Register(engine);
        engine.Evaluate("var candidate = registration.installing; var order = [candidate.state]; candidate.addEventListener('statechange', function(e) { order.push(e.target.state); });");
        Assert.True(engine.Evaluate("candidate.state === 'installing' && registration.active === null && registration.waiting === null").AsBoolean());
        engine.Evaluate("candidate.postMessage(1);");
        Drain(engine);
        Assert.True(engine.Evaluate("registration.installing === candidate && registration.active === null").AsBoolean());
        engine.Evaluate("candidate.postMessage(2);");
        Drain(engine);
        Assert.True(engine.Evaluate("registration.installing === null && registration.waiting === null && registration.active === candidate && candidate.state === 'activated'").AsBoolean());
        Assert.True(engine.Evaluate("navigator.serviceWorker.controller === candidate").AsBoolean());
        Assert.Equal("installing,installed,activating,activated", engine.Evaluate("order.join(',')").AsString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectedInstallNeverActivatesOrClaims(bool native)
    {
        using var engine = Engine(native);
        using var document = Install(engine, new ScriptLoader("""
            var rejectInstall;
            self.oninstall = function(e) { e.waitUntil(new Promise(function(resolve, reject) { rejectInstall = reject; })); };
            self.onmessage = function() { rejectInstall(new Error('install rejected')); };
            self.onactivate = function() { self.clients.claim(); };
            """));
        Register(engine);
        engine.Evaluate("var candidate = registration.installing; candidate.postMessage('reject');");
        Drain(engine);
        Assert.True(engine.Evaluate("candidate.state === 'redundant' && registration.active === null && registration.waiting === null && registration.installing === null").AsBoolean());
        Assert.True(engine.Evaluate("navigator.serviceWorker.controller === null").AsBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeferredActivationDelaysActivatedStateAndReady(bool native)
    {
        using var engine = Engine(native);
        using var document = Install(engine, new ScriptLoader("""
            var finishActivation;
            self.onactivate = function(e) { e.waitUntil(new Promise(function(resolve) { finishActivation = resolve; })); };
            self.onmessage = function() { finishActivation(); };
            """));
        engine.Evaluate("var ready = false; navigator.serviceWorker.ready.then(function() { ready = true; });");
        Register(engine);
        Assert.True(engine.Evaluate("registration.active.state === 'activating' && !ready").AsBoolean());
        engine.Evaluate("registration.active.postMessage('finish');");
        Drain(engine);
        Assert.True(engine.Evaluate("registration.active.state === 'activated' && ready").AsBoolean());
        Assert.True(engine.Evaluate("navigator.serviceWorker.controller === null").AsBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UpdateWaitsWithoutReplacingActiveRuntimeUntilSkipWaiting(bool native)
    {
        using var engine = Engine(native);
        var loader = new ScriptLoader("self.onactivate = e => e.waitUntil(self.clients.claim()); self.onmessage = function() { self.postMessage('old'); };");
        using var document = Install(engine, loader);
        Register(engine);
        engine.Evaluate("var oldWorker = registration.active;");
        loader.Script = "self.onmessage = function(e) { if (e.data === 'skip') self.skipWaiting(); else self.postMessage('new'); };";
        engine.Evaluate("registration.update();");
        Drain(engine);
        Assert.True(engine.Evaluate("registration.waiting.state === 'installed' && registration.active === oldWorker && navigator.serviceWorker.controller === oldWorker").AsBoolean());
        engine.Evaluate("var reply = ''; oldWorker.onmessage = e => reply = e.data; oldWorker.postMessage('probe');");
        Assert.Equal("old", engine.Evaluate("reply").AsString());
        engine.Evaluate("var replacement = registration.waiting; replacement.postMessage('skip');");
        Drain(engine);
        Assert.True(engine.Evaluate("registration.waiting === null && registration.active === replacement && replacement.state === 'activated' && oldWorker.state === 'redundant' && navigator.serviceWorker.controller === replacement").AsBoolean());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RejectedUpdatePreservesActiveAndPreviouslyWaitingWorkers(bool native, bool hasWaiting)
    {
        using var engine = Engine(native);
        var loader = new ScriptLoader("self.onactivate = e => e.waitUntil(self.clients.claim()); self.onmessage = function() { self.postMessage('old'); };");
        using var document = Install(engine, loader);
        Register(engine);
        engine.Evaluate("var oldWorker = registration.active; var oldWaiting = null;");
        if (hasWaiting)
        {
            loader.Script = "self.onmessage = function() { self.postMessage('waiting'); };";
            engine.Evaluate("registration.update();");
            Drain(engine);
            engine.Evaluate("oldWaiting = registration.waiting;");
            Assert.True(engine.Evaluate("oldWaiting.state === 'installed'").AsBoolean());
        }
        loader.Script = """
            var rejectInstall;
            self.oninstall = function(e) { e.waitUntil(new Promise(function(resolve, reject) { rejectInstall = reject; })); };
            self.onmessage = function() { rejectInstall(new Error('replacement rejected')); };
            """;
        engine.Evaluate("registration.update();");
        Drain(engine);
        engine.Evaluate("var rejectedWorker = registration.installing; rejectedWorker.postMessage('reject');");
        Drain(engine);
        Assert.True(engine.Evaluate("rejectedWorker.state === 'redundant' && registration.installing === null && registration.active === oldWorker && oldWorker.state === 'activated' && registration.waiting === oldWaiting && navigator.serviceWorker.controller === oldWorker").AsBoolean());
        if (hasWaiting) Assert.True(engine.Evaluate("oldWaiting.state === 'installed'").AsBoolean());
        engine.Evaluate("var reply = ''; oldWorker.onmessage = e => reply = e.data; oldWorker.postMessage('probe');");
        Assert.Equal("old", engine.Evaluate("reply").AsString());
        // A rejected candidate's retired lease must not own subsequent updates.
        loader.Script = "self.skipWaiting(); self.onmessage = function() { self.postMessage('retry'); };";
        engine.Evaluate("registration.update();");
        Drain(engine);
        Assert.True(engine.Evaluate("registration.active !== oldWorker && registration.active.state === 'activated' && registration.waiting === null && oldWorker.state === 'redundant'").AsBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectedActivationRestoresTheUsableActiveWorker(bool native)
    {
        using var engine = Engine(native);
        var loader = new ScriptLoader("self.onactivate = e => e.waitUntil(self.clients.claim());");
        using var document = Install(engine, loader);
        Register(engine);
        engine.Evaluate("var oldWorker = registration.active; var rejectedWorker; registration.onupdatefound = function() { rejectedWorker = registration.installing; };");
        loader.Script = "self.skipWaiting(); self.onactivate = e => e.waitUntil(Promise.reject(new Error('activation rejected')));";
        engine.Evaluate("registration.update();");
        Drain(engine);
        Assert.True(engine.Evaluate("rejectedWorker.state === 'redundant' && registration.active === oldWorker && oldWorker.state === 'activated' && registration.waiting === null && navigator.serviceWorker.controller === oldWorker").AsBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedActivationPersistenceDoesNotRestoreARetiredWorker(bool native)
    {
        using var engine = Engine(native);
        var storage = new RegistrationStorage();
        var loader = new ScriptLoader("self.onactivate = e => e.waitUntil(self.clients.claim());");
        using var document = Install(engine, loader, storage: storage);
        Register(engine);
        engine.Evaluate("var oldWorker = registration.active; var rejectedWorker; registration.onupdatefound = function() { rejectedWorker = registration.installing; };");
        var persisted = storage.Read("default:service-worker-registration/%2F");
        Assert.NotNull(persisted);
        storage.RejectRegistrationWrites = true;
        loader.Script = "self.skipWaiting(); self.onmessage = function() { self.postMessage('new'); };";
        engine.Evaluate("registration.update();");
        Drain(engine);
        Assert.True(engine.Evaluate("rejectedWorker.state === 'redundant' && registration.active === oldWorker && oldWorker.state === 'activated' && navigator.serviceWorker.controller === oldWorker").AsBoolean());
        Assert.Equal(persisted, storage.Read("default:service-worker-registration/%2F"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThrowingStateChangeListenerCannotStrandASkipWaitingActivation(bool native)
    {
        using var engine = Engine(native);
        var loader = new ScriptLoader("self.onactivate = e => e.waitUntil(self.clients.claim());");
        using var document = Install(engine, loader);
        Register(engine);
        loader.Script = "self.onmessage = function() { self.skipWaiting(); };";
        engine.Evaluate("registration.update();");
        Drain(engine);
        engine.Evaluate("var replacement = registration.waiting; replacement.onstatechange = function() { throw new Error('notification'); }; replacement.postMessage('skip');");
        Drain(engine);
        Assert.True(engine.Evaluate("registration.waiting === null && registration.active === replacement && replacement.state === 'activated' && navigator.serviceWorker.controller === replacement").AsBoolean());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ClaimControlsOnlyAnInScopeClientAndDispatchesOneChange(bool native, bool inScope)
    {
        using var engine = Engine(native);
        using var document = Install(engine, new ScriptLoader("self.onactivate = e => e.waitUntil(self.clients.claim()); self.onmessage = function() { self.clients.claim(); };"), basePath: inScope ? "app/page" : "other/page");
        engine.Evaluate("var changes = 0; navigator.serviceWorker.oncontrollerchange = function() { changes++; };");
        Register(engine, "/app/sw.js");
        engine.Evaluate("registration.active.postMessage('claim-again');");
        Drain(engine);
        Assert.Equal(inScope, engine.Evaluate("navigator.serviceWorker.controller === registration.active").AsBoolean());
        Assert.Equal(inScope ? 1 : 0, engine.Evaluate("changes").AsNumber());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevocationOrCloseCancelsAnAlreadyWaitingNonCooperatingHostLoad(bool close)
    {
        using var authority = new ServiceWorkerCapabilityAuthority(true);
        var loader = new PendingLoader();
        var testCancellation = TestContext.Current.CancellationToken;
        var generationCancellation = authority.LoadCancellation;
        long finishedAt = 0;
        var pending = Task.Factory.StartNew(() =>
        {
            try { return ServiceWorkerBindings.LoadScript(loader, Origin + "sw.js", generationCancellation, TimeSpan.FromSeconds(30)); }
            finally { Volatile.Write(ref finishedAt, Stopwatch.GetTimestamp()); }
        }, testCancellation, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await loader.Started.Task.WaitAsync(TimeSpan.FromSeconds(2), testCancellation);
        var canceledAt = Stopwatch.GetTimestamp();
        if (close) authority.Dispose(); else authority.UpdateCapabilities(false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await pending.WaitAsync(TimeSpan.FromSeconds(2), testCancellation); });
        // Measure the blocked load's actual exit, not the delay before xUnit
        // schedules this continuation again under full-suite thread contention.
        var retirement = Stopwatch.GetElapsedTime(canceledAt, Volatile.Read(ref finishedAt));
        Assert.True(retirement >= TimeSpan.Zero && retirement < TimeSpan.FromSeconds(2), $"Load retirement took {retirement}.");
        Assert.True(loader.Token.IsCancellationRequested);
    }

    [Fact]
    public void DisposingRetainedRuntimeDisposesEachEngineExactlyOnceAndReleasesHandles()
    {
        using var page = new JintScriptEngine();
        using var document = Document.Parse("<p>worker</p>");
        var context = new DomContext(page, document.Node);
        var factory = new CountingFactory();
        using var runtime = new IsolatedServiceWorkerRuntime(page, context, factory, null, new Uri(Origin));
        var weak = LoadAndRetire(runtime, factory);
        Assert.Equal(2, factory.Created);
        Assert.Equal(2, factory.Disposed);
        runtime.Dispose();
        runtime.Retire();
        Assert.Equal(2, factory.Disposed);
        for (var i = 0; i < 3 && weak.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.False(weak.IsAlive);
        Assert.Equal("InvalidStateError", runtime.Load("self.onmessage = function() {};", Origin + "sw.js"));
        GC.KeepAlive(runtime);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadAndRetire(IsolatedServiceWorkerRuntime runtime, CountingFactory factory)
    {
        Assert.Null(runtime.Load("self.onmessage = function() {};", Origin + "sw.js"));
        Assert.Null(runtime.Load("self.onfetch = function() {};", Origin + "sw.js"));
        var weak = factory.Last!;
        runtime.Dispose();
        return weak;
    }

    private static IScriptEngine Engine(bool native) => native ? new NativeJavaScriptEngine() : new JintScriptEngine();

    private static BindingLifetime Install(IScriptEngine engine, IResourceLoader loader, TimeSpan? timeout = null, string basePath = "", IArtifactStorage? storage = null)
    {
        var document = Document.Parse("<p>worker</p>", new FolioOptions
        {
            BaseUri = new Uri(Origin + basePath),
            Capabilities = new FeatureCapabilities { ServiceWorkers = true },
            Limits = ResourceLimits.Default with { MaxWorkerExecutionTime = timeout ?? TimeSpan.FromSeconds(2) },
            ResourceLoader = loader,
            ArtifactStorage = storage
        });
        DomBindings.Install(engine, document);
        return new BindingLifetime(engine, document);
    }

    private static void Register(IScriptEngine engine, string script = "/sw.js")
    {
        engine.Evaluate("var registration; navigator.serviceWorker.register('" + script + "').then(function(r) { registration = r; });");
        Drain(engine);
    }

    private static void Drain(IScriptEngine engine) { for (var i = 0; i < 8; i++) engine.PerformMicrotaskCheckpoint(); }

    private static void AssertBoundedRetirement(IScriptEngine engine, BindingLifetime document)
    {
        var watch = Stopwatch.StartNew();
        DomBindings.UpdateCapabilities(engine, FeatureCapabilities.Default);
        Assert.True(DomBindings.Dispose(engine));
        document.Dispose();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
    }

    private sealed class BindingLifetime(IScriptEngine engine, Document document) : IDisposable
    {
        public void Dispose() { DomBindings.Dispose(engine); document.Dispose(); }
    }

    private sealed class RegistrationStorage : IArtifactStorage
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        internal bool RejectRegistrationWrites;
        public string? Read(string key) => _values.GetValueOrDefault(key);
        public void Write(string key, string value)
        {
            if (RejectRegistrationWrites && key.EndsWith("service-worker-registration/%2F", StringComparison.Ordinal)) throw new IOException("registration persistence rejected");
            _values[key] = value;
        }
        public void Delete(string key) => _values.Remove(key);
        public IReadOnlyList<string> Keys(string prefix) => _values.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
    }

    private sealed class ScriptLoader(string script) : IResourceLoader
    {
        internal string Script { get; set; } = script;
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken) => Task.FromResult<ResourceResponse?>(Response(Script));
    }

    private sealed class PendingLoader(string? entry = null) : IResourceLoader
    {
        private readonly TaskCompletionSource<ResourceResponse?> _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token { get; private set; }
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            if (entry is not null && request.Url.EndsWith("/sw.js", StringComparison.Ordinal)) return Task.FromResult<ResourceResponse?>(Response(entry));
            Token = cancellationToken;
            Started.TrySetResult(true);
            return _pending.Task;
        }
        internal void Complete(string script) => _pending.TrySetResult(Response(script));
    }

    private static ResourceResponse Response(string script) => ResourceResponse.Loaded(Encoding.UTF8.GetBytes(script), "application/javascript");

    private sealed class CountingFactory : IScriptEngineFactory
    {
        internal int Created;
        internal int Disposed;
        internal WeakReference? Last;
        public IScriptEngine CreateIsolated()
        {
            var wrapper = new CountingEngine(this);
            Created++;
            Last = new WeakReference(wrapper);
            return wrapper;
        }
    }

    private sealed class CountingEngine(CountingFactory owner) : IScriptEngine, IHostScriptEvaluator
    {
        private readonly IScriptEngine _inner = new NativeJavaScriptEngine();
        public void Dispose() { owner.Disposed++; _inner.Dispose(); }
        public void Configure(ScriptLimits limits) => _inner.Configure(limits);
        public ScriptValue Evaluate(string source, string url = "eval") => _inner.Evaluate(source, url);
        public ScriptValue EvaluateHostScript(string source, string url) => _inner.EvaluateHostScript(source, url);
        public ScriptValue Call(ScriptValue function, ScriptValue thisValue, ReadOnlySpan<ScriptValue> args) => _inner.Call(function, thisValue, args);
        public ScriptValue CreateHostObject(IHostObjectTemplate template, object target) => _inner.CreateHostObject(template, target);
        public bool TryGetHostTarget(ScriptValue value, out object? target) => _inner.TryGetHostTarget(value, out target);
        public ScriptValue CreateArray(ReadOnlySpan<ScriptValue> elements) => _inner.CreateArray(elements);
        public ScriptValue CreatePlainObject(IReadOnlyDictionary<string, ScriptValue> properties) => _inner.CreatePlainObject(properties);
        public ScriptValue GetProperty(ScriptValue target, string name) => _inner.GetProperty(target, name);
        public void SetGlobalProperty(string name, ScriptValue value) => _inner.SetGlobalProperty(name, value);
        public ScriptValue GetGlobalProperty(string name) => _inner.GetGlobalProperty(name);
        public void PerformMicrotaskCheckpoint() => _inner.PerformMicrotaskCheckpoint();
        public void Interrupt() => _inner.Interrupt();
    }
}
