using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

/// <summary>
/// Unit tests for #450: timers, queueMicrotask, promises, requestAnimationFrame,
/// performance.now(), performance.timeOrigin, Date.now() consistency, and determinism.
/// </summary>
public class TimersAndAnimationTests
{
    private sealed class Recorder
    {
        public List<string> Trace { get; } = new();
        public void Record(string msg) => Trace.Add(msg);
    }

    private static (JintScriptEngine Engine, HtmlEventLoop Loop, Recorder Recorder) CreateHarness(
        IEventLoopClock? clock = null,
        int? randomSeed = null)
    {
        var clk = clock ?? new VirtualEventLoopClock();
        var options = new ScriptEngineOptions
        {
            Clock = () => clk.NowUtc,
            RandomSeed = randomSeed,
        };
        var engine = new JintScriptEngine(options);
        var loop = new HtmlEventLoop(engine: engine, clock: clk);
        ScriptGlobalsInstaller.Install(loop, engine);

        var recorder = new Recorder();
        var template = new HostObjectTemplate("Recorder")
            .DefineMethod("record", (target, _, args) =>
            {
                var msg = args.Length > 0 ? args[0].AsString() ?? "" : "";
                ((Recorder)target).Record(msg);
                return ScriptValue.Undefined;
            });

        engine.SetGlobalProperty("recorder", engine.CreateHostObject(template, recorder));
        return (engine, loop, recorder);
    }

    [Fact]
    public void SpecOrderingOfMicrotasksPromisesAndTimers()
    {
        var clock = new VirtualEventLoopClock();
        var (engine, loop, recorder) = CreateHarness(clock);
        using (engine)
        using (loop)
        {
            // Execute task that schedules a timeout, a microtask, and a promise reaction
            loop.QueueTask(TaskSource.Generic, () =>
            {
                engine.Evaluate(@"
                    setTimeout(() => recorder.record('timeout-0'), 0);
                    Promise.resolve().then(() => recorder.record('promise-1'));
                    queueMicrotask(() => recorder.record('microtask-1'));
                    recorder.record('sync-end');
                ");
            });

            Assert.Empty(recorder.Trace);

            // Turn 1 runs the initial task, followed by microtask checkpoint (sync-end, promise-1, microtask-1)
            var ran1 = loop.RunOneTurn();
            Assert.True(ran1);
            Assert.Equal(new[] { "sync-end", "promise-1", "microtask-1" }, recorder.Trace);

            // Turn 2 runs the expired timer task (at t=0)
            var ran2 = loop.RunOneTurn();
            Assert.True(ran2);
            Assert.Equal(new[] { "sync-end", "promise-1", "microtask-1", "timeout-0" }, recorder.Trace);
        }
    }

    [Fact]
    public void SetTimeoutPassesExtraArgumentsAndSupportsStringHandler()
    {
        var clock = new VirtualEventLoopClock();
        var (engine, loop, recorder) = CreateHarness(clock);
        using (engine)
        using (loop)
        {
            engine.Evaluate(@"
                setTimeout((a, b, c) => recorder.record(`args:${a},${b},${c}`), 0, 'hello', 42, true);
                setTimeout('recorder.record(""eval-timer"")', 0);
            ");

            loop.SpinUntilIdle();

            Assert.Equal(new[] { "args:hello,42,true", "eval-timer" }, recorder.Trace);
        }
    }

    [Fact]
    public void TimerNestingClampingAppliesAfterFiveLevels()
    {
        var clock = new VirtualEventLoopClock();
        var (engine, loop, recorder) = CreateHarness(clock);
        using (engine)
        using (loop)
        {
            // Chain 6 nested zero-delay setTimeout calls and record scheduled time
            engine.Evaluate(@"
                function nest(level) {
                    if (level > 6) return;
                    setTimeout(() => {
                        recorder.record(`level:${level}@${performance.now()}`);
                        nest(level + 1);
                    }, 0);
                }
                nest(1);
            ");

            // Spin through time advances
            // Levels 1-5 have 0ms clamp; level 6 must have >= 4ms delay
            for (var i = 0; i < 10; i++)
            {
                loop.SpinUntilIdle();
                clock.Advance(1.0);
            }

            Assert.True(recorder.Trace.Count >= 6);
            Assert.Equal("level:1@0", recorder.Trace[0]);
            Assert.Equal("level:2@0", recorder.Trace[1]);
            Assert.Equal("level:3@0", recorder.Trace[2]);
            Assert.Equal("level:4@0", recorder.Trace[3]);
            Assert.Equal("level:5@0", recorder.Trace[4]);
            // Level 6 was clamped to at least 4 ms
            Assert.Equal("level:6@4", recorder.Trace[5]);
        }
    }

    [Fact]
    public void ClearIntervalFromInsideItsOwnCallback()
    {
        var clock = new VirtualEventLoopClock();
        var (engine, loop, recorder) = CreateHarness(clock);
        using (engine)
        using (loop)
        {
            engine.Evaluate(@"
                let count = 0;
                let id = setInterval(() => {
                    count++;
                    recorder.record(`tick:${count}`);
                    if (count === 3) {
                        clearInterval(id);
                    }
                }, 10);
            ");

            for (var t = 0; t <= 100; t += 10)
            {
                clock.SetNow(t);
                loop.SpinUntilIdle();
            }

            // Must tick exactly 3 times and stop
            Assert.Equal(new[] { "tick:1", "tick:2", "tick:3" }, recorder.Trace);
            Assert.True(loop.IsIdle());
        }
    }

    [Fact]
    public void ClearTimeoutCancelsTimerBeforeExecution()
    {
        var clock = new VirtualEventLoopClock();
        var (engine, loop, recorder) = CreateHarness(clock);
        using (engine)
        using (loop)
        {
            engine.Evaluate(@"
                let id1 = setTimeout(() => recorder.record('never-1'), 20);
                let id2 = setTimeout(() => recorder.record('ran-2'), 20);
                clearTimeout(id1);
            ");

            clock.SetNow(30);
            loop.SpinUntilIdle();

            Assert.Equal(new[] { "ran-2" }, recorder.Trace);
        }
    }

    [Fact]
    public void AnimationFramesShareTimestampAcrossCallbacksOfSameFrame()
    {
        var clock = new VirtualEventLoopClock();
        var (engine, loop, recorder) = CreateHarness(clock);
        using (engine)
        using (loop)
        {
            engine.Evaluate(@"
                requestAnimationFrame(t1 => recorder.record(`cb1:${t1}`));
                requestAnimationFrame(t2 => recorder.record(`cb2:${t2}`));
            ");

            // None run without a frame tick
            loop.SpinUntilIdle();
            Assert.Empty(recorder.Trace);

            // Frame tick at 16.6 ms
            clock.SetNow(16.6);
            loop.UpdateTheRendering();

            Assert.Equal(new[] { "cb1:16.6", "cb2:16.6" }, recorder.Trace);
        }
    }

    [Fact]
    public void AnimationFramesRequestedDuringFrameRunInNextFrame()
    {
        var clock = new VirtualEventLoopClock();
        var (engine, loop, recorder) = CreateHarness(clock);
        using (engine)
        using (loop)
        {
            engine.Evaluate(@"
                requestAnimationFrame(t1 => {
                    recorder.record(`frame1:${t1}`);
                    requestAnimationFrame(t2 => recorder.record(`frame2:${t2}`));
                });
            ");

            // Frame 1
            clock.SetNow(16.0);
            loop.UpdateTheRendering();
            Assert.Equal(new[] { "frame1:16" }, recorder.Trace);

            // Frame 2
            clock.SetNow(32.0);
            loop.UpdateTheRendering();
            Assert.Equal(new[] { "frame1:16", "frame2:32" }, recorder.Trace);
        }
    }

    [Fact]
    public void CancelAnimationFrameCancelsCallback()
    {
        var clock = new VirtualEventLoopClock();
        var (engine, loop, recorder) = CreateHarness(clock);
        using (engine)
        using (loop)
        {
            engine.Evaluate(@"
                let id1 = requestAnimationFrame(t => recorder.record('raf-never'));
                let id2 = requestAnimationFrame(t => recorder.record('raf-ok'));
                cancelAnimationFrame(id1);
            ");

            clock.SetNow(16.0);
            loop.UpdateTheRendering();

            Assert.Equal(new[] { "raf-ok" }, recorder.Trace);
        }
    }

    [Fact]
    public void PerformanceNowAndTimeOriginMatchEventLoopClock()
    {
        var origin = new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        var clock = new VirtualEventLoopClock(origin);
        var (engine, loop, _) = CreateHarness(clock);
        using (engine)
        using (loop)
        {
            var timeOrigin = engine.Evaluate("performance.timeOrigin").AsNumber();
            Assert.Equal(origin.ToUnixTimeMilliseconds(), (long)timeOrigin);

            var now0 = engine.Evaluate("performance.now()").AsNumber();
            Assert.Equal(0.0, now0);

            clock.Advance(123.45);
            var now1 = engine.Evaluate("performance.now()").AsNumber();
            Assert.Equal(123.45, Math.Round(now1, 2));

            // Date.now() is consistent with virtual clock origin + nowMs in test mode
            var dateNow = engine.Evaluate("Date.now()").AsNumber();
            Assert.Equal(origin.ToUnixTimeMilliseconds() + 123, (long)dateNow);
        }
    }

    [Fact]
    public void CallbackExceptionReportsDiagnosticAndLoopCarriesOn()
    {
        var clock = new VirtualEventLoopClock();
        var (engine, loop, recorder) = CreateHarness(clock);
        using (engine)
        using (loop)
        {
            var exceptionsCaught = new List<Exception>();
            loop.UncaughtException += ex => exceptionsCaught.Add(ex);

            engine.Evaluate(@"
                setTimeout(() => {
                    recorder.record('before-error');
                    throw new Error('Boom!');
                }, 0);
                setTimeout(() => {
                    recorder.record('after-error');
                }, 0);
            ");

            loop.SpinUntilIdle();

            // Loop did not crash and ran the second timer
            Assert.Equal(new[] { "before-error", "after-error" }, recorder.Trace);
            Assert.Single(exceptionsCaught);
            Assert.Contains("Boom!", exceptionsCaught[0].Message);
        }
    }

    [Fact]
    public void VirtualClockReplayProducesIdenticalTraceTwice()
    {
        List<string> RunScenario()
        {
            var clock = new VirtualEventLoopClock();
            var (engine, loop, recorder) = CreateHarness(clock, randomSeed: 12345);
            using (engine)
            using (loop)
            {
                engine.Evaluate(@"
                    let frameCount = 0;
                    function onFrame(t) {
                        frameCount++;
                        recorder.record(`frame:${frameCount}@${t.toFixed(1)}`);
                        if (frameCount < 3) {
                            requestAnimationFrame(onFrame);
                        }
                    }
                    requestAnimationFrame(onFrame);

                    setTimeout(() => recorder.record(`timer-10@${performance.now()}`), 10);
                    setInterval(() => recorder.record(`interval-20@${performance.now()}`), 20);

                    queueMicrotask(() => recorder.record('micro1'));
                    Promise.resolve().then(() => recorder.record('promise1'));
                ");

                // Simulate 60 ms in 16.6 ms frame steps
                for (var t = 0.0; t <= 60.0; t += 16.6)
                {
                    clock.SetNow(t);
                    loop.UpdateTheRendering();
                    loop.SpinUntilIdle();
                }

                return recorder.Trace;
            }
        }

        var trace1 = RunScenario();
        var trace2 = RunScenario();

        Assert.NotEmpty(trace1);
        Assert.Equal(trace1, trace2);
    }
}
