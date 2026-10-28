using System.Collections.Concurrent;

namespace Folio.Scripting;

/// <summary>
/// Folio's HTML event loop implementation (study 17, HTML §8.1.7).
/// Manages task sources and queues, microtasks, checkpoints, and "update the rendering" steps.
/// </summary>
public sealed class HtmlEventLoop : IDisposable
{
    private readonly IScriptEngine? _engine;
    private readonly IEventLoopClock _clock;
    private readonly Dictionary<TaskSource, Queue<EventLoopTask>> _taskQueues = new();
    private readonly Queue<Action> _microtasks = new();
    private readonly List<ScheduledTimer> _timers = new();
    private readonly List<AnimationFrameCallbackEntry> _animationCallbacks = new();
    private int _nextTimerId = 1;
    private int _nextRafId = 1;
    private long _nextTaskSeq = 1;
    private int _currentTimerNesting;
    private bool _inMicrotaskCheckpoint;
    private bool _disposed;

    public HtmlEventLoop(IScriptEngine? engine = null, IEventLoopClock? clock = null, IRenderingStepsHooks? renderingHooks = null)
    {
        _engine = engine;
        _clock = clock ?? new RealEventLoopClock();
        RenderingHooks = renderingHooks ?? new RenderingStepsHooks();
        _engine?.SetEventLoopPump(RunOneTurn);

        foreach (TaskSource source in Enum.GetValues<TaskSource>())
        {
            _taskQueues[source] = new Queue<EventLoopTask>();
        }
    }

    public IEventLoopClock Clock => _clock;
    public IScriptEngine? Engine => _engine;
    public IRenderingStepsHooks RenderingHooks { get; set; }

    /// <summary>Called once per rendering update so document-owned media can advance against the same clock.</summary>
    internal Action<double>? MediaPlaybackTick { get; set; }

    /// <summary>Enqueues a task to the appropriate task queue.</summary>
    public void QueueTask(TaskSource source, Action action, string? description = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_taskQueues)
        {
            var task = new EventLoopTask(source, action, _nextTaskSeq++, description);
            _taskQueues[source].Enqueue(task);
        }
    }

    /// <summary>Enqueues a microtask to the microtask queue (HTML §8.1.7.2).</summary>
    public void QueueMicrotask(Action microtask)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_microtasks)
        {
            _microtasks.Enqueue(microtask);
        }
    }

    /// <summary>
    /// Executes all queued microtasks, including promise reactions from the script engine,
    /// until the microtask queue is empty.
    /// </summary>
    public void PerformMicrotaskCheckpoint()
    {
        if (_inMicrotaskCheckpoint || _disposed)
            return;

        _inMicrotaskCheckpoint = true;
        try
        {
            while (true)
            {
                Action? microtask = null;
                lock (_microtasks)
                {
                    if (_microtasks.Count > 0)
                        microtask = _microtasks.Dequeue();
                }

                if (microtask is not null)
                {
                    void RunMicrotask()
                    {
                        try { microtask(); }
                        catch (Exception ex) { OnTaskException(ex); }
                    }
                    if (_engine is not null) _engine.RunHostTask(RunMicrotask);
                    else RunMicrotask();
                    continue;
                }

                // Also allow the engine to process any queued promise jobs
                if (_engine is not null)
                {
                    try
                    {
                        _engine.PerformMicrotaskCheckpoint();
                    }
                    catch (Exception ex)
                    {
                        OnTaskException(ex);
                    }
                }

                // Check if any new microtasks were queued as a result
                lock (_microtasks)
                {
                    if (_microtasks.Count == 0)
                        break;
                }
            }
        }
        finally
        {
            _inMicrotaskCheckpoint = false;
        }
    }

    /// <summary>
    /// Executes one turn of the event loop (HTML §8.1.7.3):
    /// 1. Select the oldest runnable task from task queues and execute it.
    /// 2. Perform a microtask checkpoint.
    /// Returns true if a task was executed, false if queues were empty.
    /// </summary>
    public bool RunOneTurn() => RunOneTurnCore(allowEngineMicrotasks: true);

    private bool RunOneTurnCore(bool allowEngineMicrotasks)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckTimers();

        EventLoopTask? selectedTask = null;
        Queue<EventLoopTask>? selectedQueue = null;
        lock (_taskQueues)
        {
            // HTML §8.1.7.3: Select a runnable task (oldest task across runnable queues)
            foreach (var queue in _taskQueues.Values)
            {
                if (queue.Count > 0)
                {
                    var candidate = queue.Peek();
                    if (selectedTask is null || candidate.SequenceNumber < selectedTask.SequenceNumber)
                    {
                        selectedTask = candidate;
                        selectedQueue = queue;
                    }
                }
            }

            if (selectedQueue is not null)
            {
                selectedQueue.Dequeue();
            }
        }

        if (selectedTask is not null)
        {
            void RunTask()
            {
                try
                {
                    selectedTask.Action();
                }
                catch (Exception ex)
                {
                    OnTaskException(ex);
                }
            }

            if (_engine is not null)
                _engine.RunHostTask(RunTask);
            else
                RunTask();

            PerformMicrotaskCheckpoint();
            return true;
        }

        // Even if no macro task ran, perform microtask checkpoint if any exist.
        // The engine pump skips this while evaluation is suspended on another
        // thread; Jint processes its own queued jobs on that thread.
        if (allowEngineMicrotasks)
            PerformMicrotaskCheckpoint();
        return false;
    }

    /// <summary>
    /// Runs the "Update the rendering" steps (HTML §8.1.7.3, steps 11–12) driven by an external frame tick.
    /// 1. Resize steps.
    /// 2. Scroll steps.
    /// 3. Media query changes.
    /// 4. Animation frame callbacks.
    /// 5. Resize observers.
    /// 6. Intersection observers.
    /// 7. Update style and layout.
    /// 8. Paint.
    /// </summary>
    public void UpdateTheRendering()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var timestamp = _clock.NowMs;

        MediaPlaybackTick?.Invoke(timestamp);

        RenderingHooks.RunResizeSteps();
        RenderingHooks.RunScrollSteps();
        RenderingHooks.RunMediaQueryChanges();

        // HTML §8.1.7.3 step 11: Run the animation frame callbacks
        List<AnimationFrameCallbackEntry> callbacksToRun;
        lock (_animationCallbacks)
        {
            callbacksToRun = new List<AnimationFrameCallbackEntry>(_animationCallbacks);
            _animationCallbacks.Clear();
        }

        foreach (var entry in callbacksToRun)
        {
            if (entry.Cancelled)
                continue;
            try
            {
                entry.Callback(timestamp);
            }
            catch (Exception ex)
            {
                OnTaskException(ex);
            }
        }

        RenderingHooks.RunAnimationFrameCallbacks(timestamp);
        RenderingHooks.RunResizeObservers();
        RenderingHooks.RunIntersectionObservers();
        RenderingHooks.RunUpdateStyleAndLayout();
        RenderingHooks.RunPaint();

        PerformMicrotaskCheckpoint();
    }

    /// <summary>
    /// Checks all scheduled timers against current clock and moves expired timers to the timer task queue.
    /// </summary>
    internal void CheckTimers()
    {
        var now = _clock.NowMs;
        List<ScheduledTimer> ready = new();

        lock (_timers)
        {
            for (var i = _timers.Count - 1; i >= 0; i--)
            {
                var timer = _timers[i];
                if (timer.Cancelled)
                {
                    _timers.RemoveAt(i);
                    continue;
                }

                if (timer.DueTimeMs <= now)
                {
                    ready.Add(timer);
                    if (timer.IsInterval)
                    {
                        var nextDue = now + Math.Max(timer.NestingLevel >= 5 ? 4.0 : 0.0, timer.IntervalMs);
                        timer.DueTimeMs = nextDue;
                        timer.NestingLevel += 1;
                    }
                    else
                    {
                        _timers.RemoveAt(i);
                    }
                }
            }
        }

        // Sort ready timers by due time and id for stable ordering
        ready.Sort((a, b) => a.DueTimeMs != b.DueTimeMs ? a.DueTimeMs.CompareTo(b.DueTimeMs) : a.Id.CompareTo(b.Id));

        foreach (var timer in ready)
        {
            QueueTask(TaskSource.Timer, () =>
            {
                if (!timer.Cancelled)
                {
                    var prevNesting = _currentTimerNesting;
                    _currentTimerNesting = timer.NestingLevel + 1;
                    try
                    {
                        timer.Action();
                    }
                    finally
                    {
                        _currentTimerNesting = prevNesting;
                    }
                }
            }, $"Timer {timer.Id}");
        }
    }

    public int AddTimer(Action action, double delayMs, bool isInterval = false, int? nestingLevel = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var effectiveNesting = nestingLevel ?? _currentTimerNesting;
        // HTML clamping: 4 ms clamp after five nesting levels
        var clampedDelay = effectiveNesting >= 5 ? Math.Max(4.0, delayMs) : Math.Max(0.0, delayMs);
        var dueTime = _clock.NowMs + clampedDelay;

        lock (_timers)
        {
            var id = _nextTimerId++;
            _timers.Add(new ScheduledTimer(id, action, dueTime, delayMs, isInterval, effectiveNesting));
            return id;
        }
    }

    public bool ClearTimer(int id)
    {
        lock (_timers)
        {
            var idx = _timers.FindIndex(t => t.Id == id);
            if (idx >= 0)
            {
                var timer = _timers[idx];
                timer.Cancelled = true;
                _timers.RemoveAt(idx);
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Schedules an animation frame callback to run during the next "update the rendering" steps (HTML §8.1.7.3).
    /// </summary>
    public int RequestAnimationFrame(Action<double> callback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_animationCallbacks)
        {
            var id = _nextRafId++;
            _animationCallbacks.Add(new AnimationFrameCallbackEntry(id, callback));
            return id;
        }
    }

    /// <summary>
    /// Cancels a previously scheduled animation frame callback.
    /// </summary>
    public bool CancelAnimationFrame(int id)
    {
        lock (_animationCallbacks)
        {
            var entry = _animationCallbacks.Find(c => c.Id == id);
            if (entry is not null)
            {
                entry.Cancelled = true;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Returns true if there are no pending tasks, microtasks, timers, or animation frame callbacks in the loop.
    /// </summary>
    public bool IsIdle()
    {
        lock (_taskQueues)
        {
            if (_taskQueues.Values.Any(q => q.Count > 0))
                return false;
        }
        lock (_microtasks)
        {
            if (_microtasks.Count > 0)
                return false;
        }
        lock (_timers)
        {
            if (_timers.Count > 0)
                return false;
        }
        lock (_animationCallbacks)
        {
            if (_animationCallbacks.Any(c => !c.Cancelled))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Runs all pending tasks and microtasks until no tasks remain runnable at current clock time,
    /// or until maxTurns is reached.
    /// </summary>
    public int SpinUntilIdle(int maxTurns = 1000, TimeSpan? waitForTimers = null)
    {
        var turns = 0;
        var deadline = waitForTimers is { } wait
            ? System.Diagnostics.Stopwatch.GetTimestamp() + (long)(wait.TotalSeconds * System.Diagnostics.Stopwatch.Frequency)
            : long.MinValue;
        while (turns < maxTurns)
        {
            if (!RunOneTurn())
            {
                if (waitForTimers is null || IsIdle() || System.Diagnostics.Stopwatch.GetTimestamp() >= deadline)
                    break;
                // A real-time timer can become due between the final clock check
                // and the next turn.  A bounded wait is opt-in for host boundaries
                // that must drain zero-delay work without changing ordinary loop
                // semantics or waiting on animation callbacks.
                Thread.Sleep(1);
                continue;
            }
            turns++;
        }
        return turns;
    }

    public event Action<Exception>? UncaughtException;

    internal void ReportException(Exception ex) => OnTaskException(ex);

    private void OnTaskException(Exception ex)
    {
        UncaughtException?.Invoke(ex);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        lock (_timers) { _timers.Clear(); }
        lock (_microtasks) { _microtasks.Clear(); }
        lock (_animationCallbacks) { _animationCallbacks.Clear(); }
        lock (_taskQueues)
        {
            foreach (var q in _taskQueues.Values)
                q.Clear();
        }
    }

    private sealed class ScheduledTimer
    {
        public ScheduledTimer(int id, Action action, double dueTimeMs, double intervalMs, bool isInterval, int nestingLevel)
        {
            Id = id;
            Action = action;
            DueTimeMs = dueTimeMs;
            IntervalMs = intervalMs;
            IsInterval = isInterval;
            NestingLevel = nestingLevel;
        }

        public int Id { get; }
        public Action Action { get; }
        public double DueTimeMs { get; set; }
        public double IntervalMs { get; }
        public bool IsInterval { get; }
        public int NestingLevel { get; set; }
        public bool Cancelled { get; set; }
    }

    private sealed class AnimationFrameCallbackEntry
    {
        public AnimationFrameCallbackEntry(int id, Action<double> callback)
        {
            Id = id;
            Callback = callback;
        }

        public int Id { get; }
        public Action<double> Callback { get; }
        public bool Cancelled { get; set; }
    }
}
