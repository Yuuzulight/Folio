using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

public class HtmlEventLoopTests
{
    private class TrackingRenderingHooks : IRenderingStepsHooks
    {
        public List<string> Events { get; } = new();

        public void RunResizeSteps() => Events.Add("resize");
        public void RunScrollSteps() => Events.Add("scroll");
        public void RunMediaQueryChanges() => Events.Add("mediaQuery");
        public void RunAnimationFrameCallbacks(double timestamp) => Events.Add($"raf:{timestamp:F1}");
        public void RunResizeObservers() => Events.Add("resizeObserver");
        public void RunIntersectionObservers() => Events.Add("intersectionObserver");
        public void RunUpdateStyleAndLayout() => Events.Add("styleLayout");
        public void RunPaint() => Events.Add("paint");
    }

    [Fact]
    public void OneTaskPerTurnFollowedByMicrotasks()
    {
        using var loop = new HtmlEventLoop();
        var trace = new List<string>();

        loop.QueueTask(TaskSource.DomManipulation, () =>
        {
            trace.Add("task1");
            loop.QueueMicrotask(() => trace.Add("microtask1-1"));
            loop.QueueMicrotask(() => trace.Add("microtask1-2"));
        });

        loop.QueueTask(TaskSource.UserInteraction, () =>
        {
            trace.Add("task2");
            loop.QueueMicrotask(() => trace.Add("microtask2-1"));
        });

        Assert.Empty(trace);

        // Turn 1: runs task1, then its 2 microtasks
        var ran1 = loop.RunOneTurn();
        Assert.True(ran1);
        Assert.Equal(new[] { "task1", "microtask1-1", "microtask1-2" }, trace);

        // Turn 2: runs task2, then its microtask
        var ran2 = loop.RunOneTurn();
        Assert.True(ran2);
        Assert.Equal(new[] { "task1", "microtask1-1", "microtask1-2", "task2", "microtask2-1" }, trace);

        // Turn 3: idle
        var ran3 = loop.RunOneTurn();
        Assert.False(ran3);
    }

    [Fact]
    public void MicrotasksQueuedInsideMicrotasksRunBeforeNextTask()
    {
        using var loop = new HtmlEventLoop();
        var trace = new List<string>();

        loop.QueueTask(TaskSource.Generic, () =>
        {
            trace.Add("task1");
            loop.QueueMicrotask(() =>
            {
                trace.Add("microtask-depth1");
                loop.QueueMicrotask(() =>
                {
                    trace.Add("microtask-depth2");
                    loop.QueueMicrotask(() => trace.Add("microtask-depth3"));
                });
            });
        });

        loop.QueueTask(TaskSource.Generic, () => trace.Add("task2"));

        loop.RunOneTurn();

        // Depth 1, 2, and 3 microtasks must all drain before task2 runs
        Assert.Equal(new[] { "task1", "microtask-depth1", "microtask-depth2", "microtask-depth3" }, trace);

        loop.RunOneTurn();
        Assert.Equal(new[] { "task1", "microtask-depth1", "microtask-depth2", "microtask-depth3", "task2" }, trace);
    }

    private sealed class Recorder
    {
        public List<string> Trace { get; } = new();
        public void Record(string msg) => Trace.Add(msg);
    }

    [Fact]
    public void EnginePromiseReactionsInterleaveWithMicrotasks()
    {
        using var engine = new JintScriptEngine();
        using var loop = new HtmlEventLoop(engine: engine);
        var recorder = new Recorder();

        var template = new HostObjectTemplate("Recorder")
            .DefineMethod("record", (target, _, args) =>
            {
                var msg = args.Length > 0 ? args[0].AsString() ?? "" : "";
                ((Recorder)target).Record(msg);
                return ScriptValue.Undefined;
            });

        var hostObj = engine.CreateHostObject(template, recorder);
        engine.SetGlobalProperty("recorder", hostObj);

        loop.QueueTask(TaskSource.Generic, () =>
        {
            recorder.Trace.Add("taskStart");
            engine.Evaluate("Promise.resolve().then(() => recorder.record('promise1'));");
            loop.QueueMicrotask(() => recorder.Trace.Add("microtask1"));
            engine.Evaluate("Promise.resolve().then(() => recorder.record('promise2'));");
        });

        loop.RunOneTurn();

        Assert.Equal("taskStart", recorder.Trace[0]);
        // Promise reactions and microtasks drained during checkpoint
        Assert.Contains("promise1", recorder.Trace);
        Assert.Contains("microtask1", recorder.Trace);
        Assert.Contains("promise2", recorder.Trace);
        Assert.Equal(4, recorder.Trace.Count);
    }

    [Fact]
    public void RenderingStepsRunInSpecOrderOnlyOnFrameTick()
    {
        var clock = new VirtualEventLoopClock();
        var hooks = new TrackingRenderingHooks();
        using var loop = new HtmlEventLoop(clock: clock, renderingHooks: hooks);

        loop.QueueTask(TaskSource.Generic, () => { });
        loop.RunOneTurn();

        // Macro task execution does not trigger rendering steps on its own
        Assert.Empty(hooks.Events);

        // Frame tick
        clock.SetNow(16.6);
        loop.UpdateTheRendering();

        var expectedOrder = new[]
        {
            "resize",
            "scroll",
            "mediaQuery",
            "raf:16.6",
            "resizeObserver",
            "intersectionObserver",
            "styleLayout",
            "paint"
        };
        Assert.Equal(expectedOrder, hooks.Events);
    }

    [Fact]
    public void VirtualClockDeterminismAcrossTenSecondSchedule()
    {
        List<string> RunSchedule()
        {
            var clock = new VirtualEventLoopClock();
            using var loop = new HtmlEventLoop(clock: clock);
            var log = new List<string>();

            // Schedule tasks across 10 seconds (10,000 ms)
            loop.AddTimer(() => log.Add($"timer@100 (now={clock.NowMs})"), 100);
            loop.AddTimer(() => log.Add($"timer@500 (now={clock.NowMs})"), 500);
            loop.AddTimer(() =>
            {
                log.Add($"timer@2500 (now={clock.NowMs})");
                loop.QueueMicrotask(() => log.Add($"micro@2500 (now={clock.NowMs})"));
            }, 2500);
            loop.AddTimer(() => log.Add($"timer@10000 (now={clock.NowMs})"), 10000);

            // Frame updates every 100 ms up to 10,000 ms
            for (var t = 0.0; t <= 10000.0; t += 100.0)
            {
                clock.SetNow(t);
                loop.SpinUntilIdle();
            }

            return log;
        }

        var run1 = RunSchedule();
        var run2 = RunSchedule();

        Assert.NotEmpty(run1);
        Assert.Equal(run1, run2);
        Assert.Equal(5, run1.Count);
        Assert.Equal("timer@100 (now=100)", run1[0]);
        Assert.Equal("timer@500 (now=500)", run1[1]);
        Assert.Equal("timer@2500 (now=2500)", run1[2]);
        Assert.Equal("micro@2500 (now=2500)", run1[3]);
        Assert.Equal("timer@10000 (now=10000)", run1[4]);
    }

    [Fact]
    public void SettledDetectionEndsOnIdleAndVirtualTime()
    {
        var clock = new VirtualEventLoopClock();
        using var loop = new HtmlEventLoop(clock: clock);

        Assert.True(loop.IsIdle());

        var ran = false;
        var timerId = loop.AddTimer(() => ran = true, 50.0);
        Assert.False(loop.IsIdle());

        // Clock hasn't advanced yet: spinning doesn't execute the timer
        loop.SpinUntilIdle();
        Assert.False(ran);
        Assert.False(loop.IsIdle());

        // Advance past timer due time
        clock.Advance(50.0);
        Assert.False(loop.IsIdle());

        loop.SpinUntilIdle();
        Assert.True(ran);
        Assert.True(loop.IsIdle());

        // Fixed virtual time advance: if timer is cleared, becomes idle immediately
        var timerId2 = loop.AddTimer(() => { }, 100.0);
        Assert.False(loop.IsIdle());
        loop.ClearTimer(timerId2);
        Assert.True(loop.IsIdle());
    }
}
