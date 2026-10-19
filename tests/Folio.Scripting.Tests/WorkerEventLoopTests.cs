using Folio;
using Folio.Hosting;
using Folio.Scripting;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;
using Folio.Resources;
using System.Runtime.CompilerServices;

namespace Folio.Scripting.Tests;

public sealed class WorkerEventLoopTests
{
    [Fact]
    public async Task WorkerStartupDoesNotNeedAnAvailableThreadPoolThread()
    {
        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(WorkerEventLoopTests).Assembly.Location);
        start.ArgumentList.Add("worker-thread-pool-probe");
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.True(process.ExitCode == 0, $"Worker startup probe exited {process.ExitCode}: {await output} {await errors}");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void DisposingEngineWithoutRunnerReleasesItsWorkerEngine()
    {
        using var engine = CreateEngine(new FeatureCapabilities { Workers = true });
        var value = engine.Evaluate("new Worker('data:text/javascript,' + encodeURIComponent('setInterval(function() {}, 100);'))");
        Assert.True(engine.TryGetHostTarget(value, out var target));
        var worker = Assert.IsType<WorkerState>(target);
        engine.Dispose();
        Assert.True(worker.Terminated);
        Assert.Throws<ObjectDisposedException>(() => worker.WorkerEngine!.Evaluate("1"));
    }

    [Fact]
    public void DedicatedWorkerExposesItsGlobalAndEventLoopTimer()
    {
        using var engine = CreateEngine(new FeatureCapabilities { Workers = true });
        try
        {
            var completed = engine.Evaluate("""
                const worker = new Worker('data:text/javascript,' + encodeURIComponent(
                    "onmessage = function() { postMessage(typeof self + ':' + (self === globalThis) + ':' + typeof setTimeout); }"));
                let globals = '';
                worker.onmessage = event => globals = event.data;
                worker.postMessage(0);
                globals;
                """).AsString();
            Assert.Equal("object:true:function", completed);
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void DedicatedWorkerRunsMicrotasksBeforeZeroDelayTimers()
    {
        using var engine = CreateEngine(new FeatureCapabilities { Workers = true });
        try
        {
            var order = engine.Evaluate("""
                const worker = new Worker('data:text/javascript,' + encodeURIComponent(
                    "onmessage = function() { var order = []; queueMicrotask(function() { order.push('micro'); }); setTimeout(function() { postMessage(order.join(',')); }, 0); }"));
                let result = '';
                let workerError = '';
                worker.onerror = event => workerError = event.message;
                worker.onmessage = event => result = event.data;
                worker.postMessage(0);
                result;
                """).AsString();
            Assert.Equal("micro", order);
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void WorkerReplyWaitsUntilTheParentFinishesItsCurrentTask()
    {
        using var engine = CreateEngine(new FeatureCapabilities { Workers = true }, withEventLoop: true);
        engine.Evaluate("""
            const worker = new Worker('data:text/javascript,' + encodeURIComponent(
                'onmessage = function() { postMessage("reply"); };'));
            const order = ['before'];
            worker.onmessage = () => order.push('message');
            worker.postMessage(0);
            order.push('after');
            """);
        Assert.Equal("before,after", engine.Evaluate("order.join(',')").AsString());
        engine.RunEventLoopUntilIdle();
        Assert.Equal("before,after,message", engine.Evaluate("order.join(',')").AsString());
    }

    [Fact]
    public void DelayedWorkerMessageIsDeliveredByTheParentEventLoop()
    {
        using var engine = CreateEngine(new FeatureCapabilities { Workers = true }, withEventLoop: true);
        try
        {
            var delivered = engine.Evaluate("""
                const worker = new Worker('data:text/javascript,' + encodeURIComponent(
                    "onmessage = function() { setTimeout(function() { postMessage('delayed'); }, 15); }"));
                let result = '';
                let workerError = '';
                worker.onerror = event => workerError = event.message;
                worker.onmessage = event => result = event.data;
                worker.postMessage(0);
                result;
                """).AsString();
            Assert.Equal("", delivered);

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < deadline && engine.Evaluate("result").AsString() != "delayed")
            {
                engine.RunEventLoopUntilIdle();
                Thread.Yield();
            }
            Assert.True(engine.Evaluate("result").AsString() == "delayed", WorkerDiagnostic(engine, "worker"));
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void IdleWorkerWatchdogsDoNotStarveDelayedMessageDelivery()
    {
        using var engine = CreateEngine(new FeatureCapabilities { Workers = true }, withEventLoop: true);
        engine.Evaluate("""
            const idle = Array.from({ length: 7 }, () => new Worker('data:text/javascript,'));
            const active = new Worker('data:text/javascript,' + encodeURIComponent(
                "setTimeout(function() { postMessage('delivered'); }, 15);"));
            let delivered = '';
            let workerError = '';
            active.onerror = event => workerError = event.message;
            active.onmessage = event => delivered = event.data;
            """);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline && engine.Evaluate("delivered").AsString() != "delivered")
        {
            engine.RunEventLoopUntilIdle();
            Thread.Sleep(1);
        }
        Assert.True(engine.Evaluate("delivered").AsString() == "delivered", WorkerDiagnostic(engine, "active"));
    }

    [Fact]
    public void StartupWorkerMessageWaitsForParentHandlerRegistration()
    {
        using var engine = CreateEngine(new FeatureCapabilities { Workers = true }, withEventLoop: true);
        try
        {
            engine.Evaluate("""
                const worker = new Worker('data:text/javascript,' + encodeURIComponent("postMessage('ready');"));
                let result = '';
                let workerError = '';
                worker.onerror = event => workerError = event.message;
                worker.onmessage = event => result = event.data;
                """);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < deadline && engine.Evaluate("result").AsString() != "ready")
            {
                engine.RunEventLoopUntilIdle();
                Thread.Yield();
            }
            Assert.Equal("ready", engine.Evaluate("result").AsString());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void MessagePostedWhileWorkerIsBusyIsDeliveredAfterTheCallback()
    {
        using var engine = CreateEngine(new FeatureCapabilities { Workers = true }, withEventLoop: true, workerTime: TimeSpan.FromSeconds(2));
        try
        {
            engine.Evaluate("""
                const worker = new Worker('data:text/javascript,' + encodeURIComponent(
                    "setTimeout(function() { postMessage('started'); var end = Date.now() + 500; while (Date.now() < end) {} }, 100); onmessage = function(e) { if (e.data === 'queued') postMessage('reply'); };"));
                let started = false;
                let workerError = '';
                worker.onerror = event => workerError = event.message;
                let reply = false;
                worker.onmessage = event => { if (event.data === 'started') started = true; if (event.data === 'reply') reply = true; };
                """);
            var startDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < startDeadline && !engine.Evaluate("started").AsBoolean())
            {
                engine.RunEventLoopUntilIdle();
                Thread.Yield();
            }
            Assert.True(engine.Evaluate("started").AsBoolean(), WorkerDiagnostic(engine, "worker"));
            engine.Evaluate("worker.postMessage('queued')");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < deadline && !engine.Evaluate("reply").AsBoolean())
            {
                engine.RunEventLoopUntilIdle();
                Thread.Yield();
            }
            Assert.True(engine.Evaluate("reply").AsBoolean());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void InfiniteWorkerTimerCallbackIsInterruptedByTheWatchdog()
    {
        using var engine = CreateEngine(new FeatureCapabilities { Workers = true }, workerTime: TimeSpan.FromMilliseconds(50));
        try
        {
            engine.Evaluate("""
                const worker = new Worker('data:text/javascript,' + encodeURIComponent(
                    "onmessage = function() { setTimeout(function() { while (true) {} }, 0); }"));
                let workerError = '';
                worker.onerror = event => workerError = event.message;
                """);
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            engine.Evaluate("worker.postMessage(0)");
            elapsed.Stop();
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1));
            Assert.NotEmpty(engine.Evaluate("workerError").AsString()!);
            Assert.Equal(2, engine.Evaluate("1 + 1").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public async Task WorkerCommandTimeoutDoesNotEscapeIntoTheParentOrWaitForHostIo()
    {
        var loader = new PausingWorkerLoader();
        var scenario = Task.Run(() =>
        {
            using var engine = CreateEngine(new FeatureCapabilities { Workers = true }, workerTime: TimeSpan.FromMilliseconds(200), resourceLoader: loader);
            engine.Evaluate("""
                const worker = new Worker('data:text/javascript,' + encodeURIComponent(
                    'onmessage = function(event) { importScripts(event.data); };'));
                let workerError = '';
                worker.onerror = event => workerError = event.message;
                worker.postMessage('https://parent.example/blocked.js');
                """);
            Assert.NotEmpty(engine.Evaluate("workerError").AsString()!);
            Assert.Equal(2, engine.Evaluate("1 + 1").AsNumber());
            Assert.True(engine.TryGetHostTarget(engine.Evaluate("worker"), out var target));
            return (Worker: Assert.IsType<WorkerState>(target), Error: engine.Evaluate("workerError").AsString());
        }, TestContext.Current.CancellationToken);
        try
        {
            await Task.WhenAny(loader.Requested.Task, scenario).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            if (!loader.Requested.Task.IsCompleted)
                Assert.Fail($"The worker failed before requesting the resource: {(await scenario).Error}");
            await loader.Requested.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var result = await scenario.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.True(SpinWait.SpinUntil(() => result.Worker.ResourcesReleased, TimeSpan.FromSeconds(2)));
            Assert.Throws<ObjectDisposedException>(() => result.Worker.WorkerEngine!.Evaluate("1"));
            Assert.False(loader.Response.Task.IsCompleted);
        }
        finally
        {
            loader.Response.TrySetResult(ResourceResponse.Loaded([], "application/javascript"));
            await scenario.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    private sealed class PausingWorkerLoader : IResourceLoader
    {
        internal readonly TaskCompletionSource Requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<ResourceResponse?> Response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            Requested.TrySetResult();
            return Response.Task;
        }
    }

    [Fact]
    public void ClassicWorkerImportsExecuteWhenCalledRatherThanDuringSourceRewriting()
    {
        var loader = new CountingImportLoader();
        using var engine = CreateEngine(new FeatureCapabilities { Workers = true }, resourceLoader: loader);
        engine.Evaluate("""
            const worker = new Worker('data:text/javascript,' + encodeURIComponent(
                'onmessage = function() { importScripts("https://parent.example/helper.js"); postMessage(importCount); };'));
            let imported = 0;
            worker.onmessage = event => imported = event.data;
            """);
        Assert.Equal(0, loader.Calls);
        engine.Evaluate("worker.postMessage(0)");
        Assert.Equal(1, engine.Evaluate("imported").AsNumber());
        engine.Evaluate("worker.postMessage(0)");
        Assert.Equal(2, engine.Evaluate("imported").AsNumber());
        Assert.Equal(2, loader.Calls);
    }

    private sealed class CountingImportLoader : IResourceLoader
    {
        internal int Calls;
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://parent.example/helper.js", request.Url);
            Interlocked.Increment(ref Calls);
            return Task.FromResult<ResourceResponse?>(ResourceResponse.Loaded(System.Text.Encoding.UTF8.GetBytes("globalThis.importCount = (globalThis.importCount || 0) + 1;"), "application/javascript"));
        }
    }

    [Fact]
    public void TerminatingWorkerDuringBusyCallbackReturnsWithinBound()
    {
        using var engine = CreateEngine(new FeatureCapabilities { Workers = true }, withEventLoop: true, workerTime: TimeSpan.FromSeconds(5));
        try
        {
            engine.Evaluate("""
                const worker = new Worker('data:text/javascript,' + encodeURIComponent(
                    "setTimeout(function() { postMessage('started'); while (true) {} }, 100);"));
                let started = false;
                let workerError = '';
                worker.onerror = event => workerError = event.message;
                worker.onmessage = event => started = event.data === 'started';
                """);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < deadline && !engine.Evaluate("started").AsBoolean())
            {
                engine.RunEventLoopUntilIdle();
                Thread.Yield();
            }
            Assert.True(engine.Evaluate("started").AsBoolean(), WorkerDiagnostic(engine, "worker"));
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            engine.Evaluate("worker.terminate()");
            elapsed.Stop();
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1));
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void ClosingWorkerClearsIntervalsAndDocumentDisposeTerminatesWorkers()
    {
        using var engine = CreateEngine(new FeatureCapabilities { Workers = true }, withEventLoop: true, maxWorkers: 1);
        try
        {
            engine.Evaluate("""
                const worker = new Worker('data:text/javascript,' + encodeURIComponent(
                    "setInterval(function() { postMessage('tick'); }, 0); onmessage = function() { postMessage('closing'); close(); };"));
                let ticks = 0;
                let closed = false;
                worker.onmessage = event => { if (event.data === 'tick') ticks++; if (event.data === 'closing') closed = true; };
                """);
            engine.RunEventLoopUntilIdle();
            var beforeClose = engine.Evaluate("ticks").AsNumber();
            engine.Evaluate("worker.postMessage('close')");
            var closeDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < closeDeadline && !engine.Evaluate("closed").AsBoolean())
            {
                engine.RunEventLoopUntilIdle();
                Thread.Yield();
            }
            Assert.True(engine.Evaluate("closed").AsBoolean());
            engine.RunEventLoopUntilIdle();
            var afterClose = engine.Evaluate("ticks").AsNumber();
            Thread.Sleep(20);
            engine.RunEventLoopUntilIdle();
            Assert.Equal(afterClose, engine.Evaluate("ticks").AsNumber());
            var slotDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            var replacementCreated = false;
            while (DateTime.UtcNow < slotDeadline && !replacementCreated)
            {
                try
                {
                    replacementCreated = engine.Evaluate("worker2 = new Worker('data:text/javascript,')").IsObject;
                    if (replacementCreated) engine.Evaluate("worker2.terminate()");
                }
                catch (ScriptRuntimeException)
                {
                    engine.RunEventLoopUntilIdle();
                    Thread.Yield();
                }
            }
            Assert.True(replacementCreated);
        }
        finally { Assert.True(DomBindings.Dispose(engine)); }
    }

    [Fact]
    public void DisposingDocumentSuppressesQueuedWorkerMessages()
    {
        using var engine = CreateEngine(new FeatureCapabilities { Workers = true }, withEventLoop: true);
        engine.Evaluate("""
            const worker = new Worker('data:text/javascript,' + encodeURIComponent(
                "setTimeout(function() { postMessage('late'); }, 20);"));
            let deliveries = 0;
            worker.onmessage = () => deliveries++;
            """);
        Thread.Sleep(100);
        Assert.True(DomBindings.Dispose(engine));
        engine.RunEventLoopUntilIdle();
        Assert.Equal(0, engine.Evaluate("deliveries").AsNumber());
    }

    private static string WorkerDiagnostic(JintScriptEngine engine, string name)
    {
        Assert.True(engine.TryGetHostTarget(engine.Evaluate(name), out var target));
        var worker = Assert.IsType<WorkerState>(target);
        return $"Worker error: {engine.Evaluate("workerError").AsString()}; terminated: {worker.Terminated}; released: {worker.ResourcesReleased}";
    }

    private static JintScriptEngine CreateEngine(FeatureCapabilities capabilities, bool withEventLoop = false, TimeSpan? workerTime = null, int maxWorkers = 8, IResourceLoader? resourceLoader = null)
    {
        var engine = new JintScriptEngine();
        var loop = withEventLoop ? new HtmlEventLoop(engine) : null;
        var document = Document.Parse("<html><body></body></html>", new FolioOptions
        {
            BaseUri = new Uri("https://parent.example/"),
            Capabilities = capabilities,
            Scripting = ScriptingPolicy.ContentProcess,
            ResourceLoader = resourceLoader,
            Limits = ResourceLimits.Default with { MaxWorkerExecutionTime = workerTime ?? ResourceLimits.Default.MaxWorkerExecutionTime, MaxWorkers = maxWorkers },
        });
        DomBindings.Install(engine, document, eventLoop: loop);
        if (loop is not null)
        {
            ScriptGlobalsInstaller.Install(loop, engine);
            ParentLoops.Add(engine, loop);
        }
        return engine;
    }

    private static readonly ConditionalWeakTable<JintScriptEngine, HtmlEventLoop> ParentLoops = new();

    internal static bool PumpParentLoop(JintScriptEngine engine) => ParentLoops.TryGetValue(engine, out var loop) && loop.RunOneTurn();
}

internal static class WorkerTestEventLoopExtensions
{
    public static void RunEventLoopUntilIdle(this JintScriptEngine engine)
    {
        for (var i = 0; i < 1000; i++)
        {
            if (!WorkerEventLoopTests.PumpParentLoop(engine)) break;
        }
    }
}
