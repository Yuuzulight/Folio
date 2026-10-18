using Folio;
using Folio.Hosting;
using Folio.Scripting;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;
using System.Runtime.CompilerServices;

namespace Folio.Scripting.Tests;

public sealed class WorkerEventLoopTests
{
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
                worker.onmessage = event => result = event.data;
                worker.postMessage(0);
                result;
                """).AsString();
            Assert.Equal("micro", order);
        }
        finally { DomBindings.Dispose(engine); }
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
            Assert.Equal("delayed", engine.Evaluate("result").AsString());
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
            active.onmessage = event => delivered = event.data;
            """);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline && engine.Evaluate("delivered").AsString() != "delivered")
        {
            engine.RunEventLoopUntilIdle();
            Thread.Sleep(1);
        }
        Assert.Equal("delivered", engine.Evaluate("delivered").AsString());
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
                let reply = false;
                worker.onmessage = event => { if (event.data === 'started') started = true; if (event.data === 'reply') reply = true; };
                """);
            var startDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < startDeadline && !engine.Evaluate("started").AsBoolean())
            {
                engine.RunEventLoopUntilIdle();
                Thread.Yield();
            }
            Assert.True(engine.Evaluate("started").AsBoolean());
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
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            engine.Evaluate("""
                const worker = new Worker('data:text/javascript,' + encodeURIComponent(
                    "onmessage = function() { setTimeout(function() { while (true) {} }, 0); }"));
                worker.postMessage(0);
                """);
            elapsed.Stop();
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1));
            Assert.Equal(2, engine.Evaluate("1 + 1").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
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
                worker.onmessage = event => started = event.data === 'started';
                """);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < deadline && !engine.Evaluate("started").AsBoolean())
            {
                engine.RunEventLoopUntilIdle();
                Thread.Yield();
            }
            Assert.True(engine.Evaluate("started").AsBoolean());
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

    private static JintScriptEngine CreateEngine(FeatureCapabilities capabilities, bool withEventLoop = false, TimeSpan? workerTime = null, int maxWorkers = 8)
    {
        var engine = new JintScriptEngine();
        var loop = withEventLoop ? new HtmlEventLoop(engine) : null;
        var document = Document.Parse("<html><body></body></html>", new FolioOptions
        {
            BaseUri = new Uri("https://parent.example/"),
            Capabilities = capabilities,
            Scripting = ScriptingPolicy.ContentProcess,
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
