using Folio.Resources;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Folio.Scripting;

namespace Folio.Scripting.Dom;

internal sealed class WorkerState
{
    private readonly Action? _releaseWorker;
    private readonly Action? _releaseWorkerMemory;
    private readonly IHostObjectTemplate _messagePortTemplate;
    private readonly TimeSpan _workerExecutionTime;
    private readonly IReadOnlyDictionary<string, string>? _blobSources;
    private readonly WorkerBlobSources? _workerBlobs;
    private readonly object _workerGate = new();
    private readonly List<WorkerState> _children = [];
    private Action<WorkerState>? _unregisterFromParent;
    private readonly object _loadGate = new();
    private readonly CancellationTokenSource _workerLoads = new();
    private bool _loadsDisposed;
    private readonly Queue<(StructuredClonePayload Payload, ScriptMessageBudget.Reservation Reservation)> _pendingParentMessages = new();
    private readonly ScriptMessageBudget _parentMessageBudget;
    private readonly ScriptMessageBudget? _workerMessageBudget;
    private readonly HtmlEventLoop? _parentEventLoop;
    private readonly Queue<Exception> _pendingErrors = new();
    private bool _released;
    private volatile bool _closedByWorker;
    private volatile bool _parentDetached;
    private volatile bool _workerExecuting;
    private ScriptValue _onError = ScriptValue.Null;
    private ScriptValue _workerScope;
    private ScriptValue _dispatchWorkerEvent;
    private ScriptValue _workerEventInit;
    private readonly List<ScriptValue> _nativeMessageListeners = [];
    private Exception? _lastReportedError;
    private volatile FeatureCapabilities _capabilities;
    private readonly WeakReference<DomContext>? _parentContext;
    private int _wasmDenialReported;
    private int _pendingWasmDenial;
    private string? _pendingFetchFailure;
    private WorkerFetchContext? _fetchContext;
    private Action? _disposeWebAssembly;
    private readonly HtmlEventLoop? _workerEventLoop;
    private sealed record WorkerCommand(Action Execute, Action Abandon);
    private readonly BlockingCollection<WorkerCommand>? _workerCommands;
    private Thread? _workerThread;
    private int _workerThreadId;
    private long _executionStartedTick;
    private Thread? _watchdogThread;
    public WorkerState(IScriptEngine engine, IHostObjectTemplate messageEventTemplate, IHostObjectTemplate messagePortTemplate, string scriptUrl, bool enabled, int maxMessageBytes, IScriptEngine? workerEngine = null, IResourceLoader? resourceLoader = null, long maxWorkerMemoryBytes = 64L * 1024 * 1024, int maxWebAssemblyBytes = 64 * 1024 * 1024, FeatureCapabilities? capabilities = null, TimeSpan maxWorkerExecutionTime = default, Action? releaseWorker = null, Action? releaseWorkerMemory = null, bool moduleWorker = false, IReadOnlyDictionary<string, string>? blobSources = null, HtmlEventLoop? parentEventLoop = null, DomContext? parentContext = null, Action<WorkerState>? registerWorker = null, Action<WorkerState>? unregisterFromParent = null)
    {
        Engine = engine;
        _unregisterFromParent = unregisterFromParent;
        _capabilities = capabilities ?? FeatureCapabilities.Default;
        _parentContext = parentContext is null ? null : new(parentContext);
        _parentMessageBudget = ScriptMessageBudget.For(engine, maxWorkerMemoryBytes);
        _workerMessageBudget = workerEngine is null ? null : ScriptMessageBudget.For(workerEngine, maxWorkerMemoryBytes);
        MessageEventTemplate = messageEventTemplate;
        _messagePortTemplate = messagePortTemplate;
        ScriptUrl = scriptUrl;
        Origin = Uri.TryCreate(scriptUrl, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : "null";
        Enabled = enabled;
        MaxMessageBytes = Math.Max(0, maxMessageBytes);
        WorkerEngine = workerEngine;
        _releaseWorker = releaseWorker;
        _releaseWorkerMemory = releaseWorkerMemory;
        _workerBlobs = _workerMessageBudget is null ? null : new WorkerBlobSources(blobSources, _workerMessageBudget);
        _blobSources = _workerBlobs ?? blobSources;
        _parentEventLoop = parentEventLoop;
        _workerExecutionTime = maxWorkerExecutionTime <= TimeSpan.Zero ? TimeSpan.FromSeconds(5) : maxWorkerExecutionTime;
        var workerLimits = new ScriptLimits { MemoryLimitBytes = Math.Max(1, maxWorkerMemoryBytes), Timeout = _workerExecutionTime };
        if (WorkerEngine is not null)
        {
            _workerEventLoop = new HtmlEventLoop(WorkerEngine);
            _workerEventLoop.UncaughtException += error =>
            {
                lock (_workerGate) _pendingErrors.Enqueue(error);
                QueueParentTask(DrainErrors, "worker error");
            };
            _workerCommands = new BlockingCollection<WorkerCommand>();
            _workerThread = new Thread(RunWorkerLoop)
            {
                IsBackground = true,
                Name = "Folio worker event loop"
            };
            _watchdogThread = new Thread(RunWatchdog)
            {
                IsBackground = true,
                Name = "Folio worker watchdog"
            };
        }
        if (WorkerEngine is not null)
        {
            _workerThread!.Start();
            _watchdogThread!.Start();
            try
            {
                RunOnWorker(() =>
                {
                    WorkerEngine.Configure(workerLimits with { Timeout = TimeSpan.FromSeconds(5) });
                    WorkerEngine.SetEventLoopPump(_workerEventLoop!.RunOneTurn);
                    ScriptGlobalsInstaller.Install(_workerEventLoop!, WorkerEngine);
                    DomBindings.InstallDomException(WorkerEngine);
                    WorkerPlatformBindings.Install(WorkerEngine, reportConsole: ReportConsole,
                        createBlobUrl: _workerBlobs!.Create, revokeBlobUrl: _workerBlobs!.Revoke);
                    if (WorkerEngine is Folio.Scripting.Native.NativeJavaScriptEngine)
                    {
                        var events = new HostObjectTemplate("NativeWorkerEvents")
                            .DefineMethod("add", (target, _, args) => { if (args.Length > 1 && args[1].IsFunction) ((WorkerState)target)._nativeMessageListeners.Add(args[1]); return ScriptValue.Undefined; })
                            .DefineMethod("remove", (target, _, args) => { var raw = args.Length > 1 ? args[1].Raw : null; if (raw is not null) ((WorkerState)target)._nativeMessageListeners.RemoveAll(item => item.Raw is not null && item.Raw.Equals(raw)); return ScriptValue.Undefined; })
                            .DefineMethod("dispatch", (target, _, args) => { var message = args.Length > 2 ? args[2] : ScriptValue.Undefined; ((WorkerState)target).DispatchNativeWorkerEvent(args.Length > 1 ? args[1].AsString() ?? string.Empty : string.Empty, message.IsObject ? ((WorkerState)target).WorkerEngine!.GetProperty(message, "data") : message); return ScriptValue.Undefined; })
                            .DefineMethod("init", static (target, _, args) => ((WorkerState)target).CreateNativeWorkerEvent(args));
                        var eventsObject = WorkerEngine.CreateHostObject(events, this);
                        WorkerEngine.SetGlobalProperty("__folio_worker_events", eventsObject);
                        WorkerEngine.SetGlobalProperty("addEventListener", WorkerEngine.GetProperty(eventsObject, "add"));
                        WorkerEngine.SetGlobalProperty("removeEventListener", WorkerEngine.GetProperty(eventsObject, "remove"));
                        _dispatchWorkerEvent = WorkerEngine.GetProperty(eventsObject, "dispatch");
                        _workerEventInit = WorkerEngine.GetProperty(eventsObject, "init");
                    }
                    else
                    {
                        _dispatchWorkerEvent = DomEvents.Install(WorkerEngine, _workerEventLoop, workerScope: true).DispatchHostEvent;
                        _workerEventInit = WorkerEngine.EvaluateHostScript("(function(data,ports){return {data:data,ports:ports};})", "folio:worker-event-init");
                    }
                    var workerPortTemplate = MessagePortBindings.Install(WorkerEngine, MessageEventTemplate, _workerEventLoop!, MaxMessageBytes, maxWorkerMemoryBytes);
                    if (_parentContext?.TryGetTarget(out var rootContext) == true)
                        WorkerBindings.Install(WorkerEngine, rootContext, MessageEventTemplate, workerPortTemplate, resourceLoader,
                            Uri.TryCreate(ScriptUrl, UriKind.Absolute, out var nestedBase) ? nestedBase : null, _blobSources, _workerEventLoop, RegisterChild, UnregisterChild);
                    _disposeWebAssembly = NativeWebAssemblyBridge.Install(WorkerEngine, maxWebAssemblyBytes, _workerExecutionTime, IsWebAssemblyAllowed).Dispose;
                    WebAssemblyBindings.Install(WorkerEngine, IsWebAssemblyAllowed, maxWebAssemblyBytes);
                    _fetchContext = new WorkerFetchContext(Uri.TryCreate(ScriptUrl, UriKind.Absolute, out var workerBase) ? workerBase : null,
                        resourceLoader, _workerLoads.Token, _workerMessageBudget!, ReportFetchFailure);
                    FetchBindings.Install(WorkerEngine, _fetchContext);
                    var bridge = new HostObjectTemplate("WorkerBridge")
                        .DefineMethod("close", (_, _, _) => { CloseFromWorker(); return ScriptValue.Undefined; })
                        .DefineMethod("postMessage", (_, _, args) => { if (args.Length > 0) { if (args[0].IsFunction) throw WebIdlException.DomException("DataCloneError", "Functions cannot be cloned into a document."); DispatchToParent(args[0], args.Length > 1 ? args[1] : ScriptValue.Undefined); } return ScriptValue.Undefined; });
                    WorkerEngine.SetGlobalProperty("__folio_worker_bridge", WorkerEngine.CreateHostObject(bridge, this));
                    WorkerEngine.EvaluateHostScript("(function(host){globalThis.postMessage=function(value,transfer){return host.postMessage(value,transfer);};globalThis.close=function(){return host.close();};})(__folio_worker_bridge);delete globalThis.__folio_worker_bridge;", "folio:worker-bridge");
                    var imports = new HostObjectTemplate("WorkerImports")
                        .DefineMethod("load", (_, _, args) =>
                        {
                            if (moduleWorker) throw WebIdlException.TypeError("importScripts is unavailable in module workers.");
                            foreach (var argument in args)
                            {
                                var reference = argument.AsString() ?? string.Empty;
                                var resolved = ResolveScriptUrl(ScriptUrl, reference);
                                if (!TryReadScript(resolved, resourceLoader, out var imported, _blobSources, _workerLoads.Token))
                                    throw WebIdlException.DomException("NetworkError", $"Worker script could not load '{reference}'.");
                                WorkerEngine.Evaluate(imported, resolved);
                            }
                            return ScriptValue.Undefined;
                        });
                    WorkerEngine.SetGlobalProperty("__folio_worker_imports", WorkerEngine.CreateHostObject(imports, this));
                    WorkerEngine.EvaluateHostScript("(function(host){const apply=Reflect.apply;globalThis.importScripts=function(){return apply(host.load,host,arguments);};})(__folio_worker_imports);delete globalThis.__folio_worker_imports;", "folio:worker-imports");
                    _workerScope = WorkerEngine.Evaluate("globalThis");
                    WorkerEngine.SetGlobalProperty("self", _workerScope);
                }, enforceWatchdog: false);
            }
            catch (Exception error)
            {
                _scriptError = error;
                lock (_workerGate) _pendingErrors.Enqueue(error);
                _terminated = true;
                _workerCommands!.CompleteAdding();
                return;
            }
            // Register before loading or evaluating any artifact code. A concurrent
            // grant change must include this worker, and revocation can cancel its load.
            var startupToken = _workerLoads.Token;
            registerWorker?.Invoke(this);
            if (Terminated) return;
            try
            {
                var loaded = moduleWorker
                    ? TryReadModuleScript(scriptUrl, resourceLoader, out var source, blobSources: _blobSources, cancellationToken: startupToken)
                    : TryReadScript(scriptUrl, resourceLoader, out source, blobSources: _blobSources, cancellationToken: startupToken);
                if (loaded)
                {
                    RunOnWorker(() =>
                    {
                        try { WorkerEngine!.Evaluate(source, scriptUrl); }
                        catch (Exception error) { if (!_closedByWorker) { _scriptError = error; lock (_workerGate) _pendingErrors.Enqueue(error); } }
                    });
                    if (_parentEventLoop is null)
                    {
                        DrainCapabilityDenials();
                        DrainParentMessages();
                        DrainErrors();
                    }
                }
                else FailWorkerCommand(new InvalidOperationException($"Worker script could not load '{scriptUrl}'."));
            }
            catch (Exception error)
            {
                if (!Terminated) FailWorkerCommand(error);
            }
        }
        else registerWorker?.Invoke(this);
    }
    public IScriptEngine Engine { get; }
    public IScriptEngine? WorkerEngine { get; }
    public IHostObjectTemplate MessageEventTemplate { get; }
    public string ScriptUrl { get; }
    public string Origin { get; }
    public bool Enabled { get; }
    internal bool HasFailure => _scriptError is not null;
    internal void PublishCapabilities(FeatureCapabilities capabilities)
    {
        // Publish without entering the worker VM: a busy exported function must
        // observe revocation before its next instruction, not after a queued command.
        _capabilities = capabilities;
    }

    private bool IsWebAssemblyAllowed()
    {
        var capabilities = _capabilities;
        if (!Terminated && capabilities.Workers && capabilities.WebAssembly &&
            (_parentContext is null || _parentContext.TryGetTarget(out var owner) && !owner.ScriptFeaturesDisposed)) return true;
        if (Interlocked.CompareExchange(ref _wasmDenialReported, 1, 0) == 0)
        {
            Volatile.Write(ref _pendingWasmDenial, 1);
            QueueParentTask(DrainCapabilityDenials, "worker capability denial");
        }
        return false;
    }

    private void DrainCapabilityDenials()
    {
        if (_parentContext?.TryGetTarget(out var parent) == true && !parent.ScriptFeaturesDisposed)
        {
            parent.EnterDocumentTurn(Engine);
            parent.DrainWorkerDiagnostics();
            if (Interlocked.Exchange(ref _pendingWasmDenial, 0) != 0)
                parent.CheckCapability("WebAssembly", false);
            if (Interlocked.Exchange(ref _pendingFetchFailure, null) is { } error)
                parent.ReportWorkerDiagnostic(new Diagnostic(DiagnosticCode.ResourceNotLoaded, Severity.Warning,
                    $"Worker fetch failed: {error}", null, "fetch"));
        }
    }

    private void ReportConsole(string level, string text)
    {
        if (_parentContext is null || !_parentContext.TryGetTarget(out var parent) || parent.ScriptFeaturesDisposed) return;
        parent.ReportWorkerDiagnostic(new Diagnostic(DiagnosticCode.ScriptConsole,
            level == "error" ? Severity.Error : level == "warn" ? Severity.Warning : Severity.Info,
            $"Worker ({(ScriptUrl.Length > 120 ? ScriptUrl[..120] + "..." : ScriptUrl)}): {(text.Length > 3800 ? text[..3800] : text)}", null, "worker.console"));
    }

    private void ReportFetchFailure(string error)
    {
        // Coalesce failures until the owner drains them; an artifact cannot grow
        // an unbounded diagnostic queue while the parent is busy.
        if (Interlocked.CompareExchange(ref _pendingFetchFailure, error.Length > 512 ? error[..512] : error, null) is null)
            QueueParentTask(DrainCapabilityDenials, "worker fetch diagnostic");
    }
    public int MaxMessageBytes { get; }
    private volatile bool _terminated;
    public bool Terminated => _terminated;
    internal bool ResourcesReleased => Volatile.Read(ref _released);
    public ScriptValue OnMessage { get; set; } = ScriptValue.Null;
    public ScriptValue OnMessageError { get; set; } = ScriptValue.Null;
    public ScriptValue OnError
    {
        get => _onError;
        set { _onError = value; if (value.IsFunction && _scriptError is { } error) ReportError(error); }
    }
    public ScriptValue Source { get; set; } = ScriptValue.Null;
    private readonly Dictionary<string, List<ScriptValue>> _listeners = new(StringComparer.Ordinal);
    private volatile Exception? _scriptError;
    public void AddEventListener(string type, ScriptValue listener)
    {
        if (!listener.IsFunction) return;
        if (!_listeners.TryGetValue(type, out var list)) _listeners[type] = list = [];
        if (!list.Contains(listener)) list.Add(listener);
        if (string.Equals(type, "error", StringComparison.Ordinal) && _scriptError is { } error) ReportError(error);
    }
    public void RemoveEventListener(string type, ScriptValue listener)
    {
        if (_listeners.TryGetValue(type, out var list)) list.Remove(listener);
    }
    public void Terminate()
    {
        _parentDetached = true;
        ClearParentMessages();
        if (!Terminated)
        {
            _terminated = true;
            WorkerEngine?.Interrupt();
            CancelWorkerLoads();
            _workerCommands?.CompleteAdding();
        }
        TerminateChildren();
        // A closed/failed worker may still be running its final cleanup.
        // Detaching its parent must wait for that cleanup too.
        if (_workerThread is { } thread && thread != Thread.CurrentThread)
            thread.Join(_workerExecutionTime + TimeSpan.FromMilliseconds(100));
        if (_workerThread is null)
            ReleaseWorkerResources();
    }
    private void CloseFromWorker()
    {
        _closedByWorker = true;
        _terminated = true;
        WorkerEngine?.Interrupt();
        CancelWorkerLoads();
        _workerCommands?.CompleteAdding();
        TerminateChildren();
    }
    private void RegisterChild(WorkerState child)
    {
        bool terminate;
        lock (_workerGate) { terminate = Terminated; if (!terminate) _children.Add(child); }
        if (terminate) child.Terminate();
    }
    private void TerminateChildren()
    {
        WorkerState[] children;
        lock (_workerGate) { children = _children.ToArray(); _children.Clear(); }
        foreach (var child in children) child.Terminate();
    }
    private void UnregisterChild(WorkerState child)
    {
        lock (_workerGate) _children.Remove(child);
    }
    private void RunWorkerLoop()
    {
        _workerThreadId = Environment.CurrentManagedThreadId;
        try
        {
            while (!Terminated)
            {
                try
                {
                    // A timed BlockingCollection take can remain in its semaphore
                    // wait under contention, preventing overdue timers from running.
                    // Poll commands without blocking, then give the timer loop its turn.
                    var ranCommand = _workerCommands!.TryTake(out var command);
                    if (ranCommand) command!.Execute();
                    var ranTurn = false;
                    if (!Terminated && !_workerEventLoop!.IsIdle())
                        ExecuteWithWatchdog(() => ranTurn = _workerEventLoop.RunOneTurn());
                    if (!Terminated && !ranCommand && !ranTurn)
                        Thread.Sleep(1);
                }
                catch (InvalidOperationException) { break; }
                catch (Exception error) { QueueWorkerError(error); }
            }
        }
        finally { _terminated = true; ReleaseWorkerResources(); }
    }

    private void RunWatchdog()
    {
        while (!Terminated)
        {
            var started = Volatile.Read(ref _executionStartedTick);
            if (_workerExecuting && started != 0 && System.Diagnostics.Stopwatch.GetTimestamp() - started >= _workerExecutionTime.TotalSeconds * System.Diagnostics.Stopwatch.Frequency
                && Volatile.Read(ref _executionStartedTick) == started && _workerExecuting)
                FailWorkerCommand(new ScriptLimitExceededException("Timeout", "Worker execution exceeded its time limit."));
            // Yield keeps a thread runnable on Windows and can consume a core
            // for every idle worker, starving its event loop under contention.
            // Sleep releases the processor while retaining a bounded check interval.
            Thread.Sleep(1);
        }
    }

    private void ReleaseWorkerResources()
    {
        TerminateChildren();
        // The worker loop marks itself terminated before cleanup. Let the
        // watchdog exit before disposing the engine it may still interrupt.
        _watchdogThread?.Join();
        while (_workerCommands is not null && _workerCommands.TryTake(out var abandoned)) abandoned.Abandon();
        try { _disposeWebAssembly?.Invoke(); } catch { }
        try { if (WorkerEngine is not null) DomBindings.Dispose(WorkerEngine); } catch { }
        _fetchContext?.Dispose();
        _fetchContext = null;
        _workerBlobs?.Dispose();
        _workerEventLoop?.Dispose();
        WorkerEngine?.Dispose();
        // The page may keep its Worker object after terminate(). These are
        // handles into the worker VM, unlike the page-owned event listeners.
        _workerScope = ScriptValue.Undefined;
        _dispatchWorkerEvent = ScriptValue.Undefined;
        _nativeMessageListeners.Clear();
        _workerEventInit = ScriptValue.Undefined;
        // Cleanup may win the race with the parent's Terminate call. Cancel
        // before disposing the source so pending startup loads still wake up.
        CancelWorkerLoads();
        lock (_loadGate)
        {
            _loadsDisposed = true;
            _workerLoads.Dispose();
        }
        lock (_workerGate)
        {
            if (_released) return;
            Interlocked.Exchange(ref _unregisterFromParent, null)?.Invoke(this);
            if (_parentContext?.TryGetTarget(out var parent) == true) parent.UnregisterWorker(this);
            _releaseWorker?.Invoke();
            _releaseWorkerMemory?.Invoke();
            Volatile.Write(ref _released, true);
        }
    }

    private void RunOnWorker(Action action, bool enforceWatchdog = true, Action? abandon = null)
    {
        if (WorkerEngine is null || Environment.CurrentManagedThreadId == _workerThreadId)
        {
            action();
            return;
        }

        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? failure = null;
        void AbandonCommand()
        {
            try { abandon?.Invoke(); }
            finally
            {
                failure = new ScriptInterruptedException("The worker stopped before delivery.");
                completed.TrySetResult();
            }
        }
        try
        {
            _workerCommands!.Add(new WorkerCommand(() =>
            {
                try
                {
                    void ExecuteCommand()
                    {
                        action();
                        if (!Terminated)
                        {
                            _workerEventLoop?.PerformMicrotaskCheckpoint();
                            for (var turn = 0; turn < 1000 && _workerEventLoop is not null && !_workerEventLoop.IsIdle() && RunWorkerTurn(); turn++) { }
                        }
                    }
                    if (enforceWatchdog) ExecuteWithWatchdog(ExecuteCommand);
                    else ExecuteCommand();
                }
                catch (Exception error) { failure = error; }
                finally { completed.TrySetResult(); }
            }, AbandonCommand));
        }
        catch (InvalidOperationException) { AbandonCommand(); }
        var waitTimeout = enforceWatchdog ? _workerExecutionTime + TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(30);
        if (!completed.Task.Wait(waitTimeout))
            throw new TimeoutException("The worker command exceeded its execution limit.");
        if (failure is not null) throw failure;
    }
    private bool RunWorkerTurn()
    {
        var ran = false;
        ExecuteWithWatchdog(() => ran = _workerEventLoop!.RunOneTurn());
        return ran;
    }
    private void ExecuteWithWatchdog(Action action)
    {
        var previousExecuting = _workerExecuting;
        var previousTick = Volatile.Read(ref _executionStartedTick);
        _workerExecuting = true;
        if (previousTick == 0)
            Volatile.Write(ref _executionStartedTick, System.Diagnostics.Stopwatch.GetTimestamp());
        try { action(); }
        finally
        {
            _workerExecuting = previousExecuting;
            Volatile.Write(ref _executionStartedTick, previousTick);
        }
    }
    public bool PostMessage(ScriptValue value, ScriptValue transfer = default)
    {
        DrainCapabilityDenials();
        if (value.IsString && (value.AsString()?.Length ?? 0) > MaxMessageBytes) return false;
        if (_scriptError is not null && !Terminated) { ReportError(_scriptError); return true; }
        if (WorkerEngine is not null && !Terminated)
        {
            transfer = StructuredCloneCodec.NormalizeTransfer(Engine, transfer, postMessage: true);
            var prepared = StructuredCloneCodec.Prepare(Engine, value, transfer, MaxMessageBytes, MessagePortEndpoint.TryGetPort);
            using var reservation = _workerMessageBudget!.Reserve(prepared.Payload.MemoryCost);
            var payload = prepared.Commit();
            // Keep the sender-side detachment invariant at the worker bridge even when
            // an engine's transfer-list normalization wrapped the original values.
            if (Engine.TryGetArrayItems(transfer, out var transferItems))
                foreach (var item in transferItems) Engine.TryDetachArrayBuffer(item);
            // RunOnWorker queues the command when the worker is already executing a
            // callback, so delivery cannot be stranded behind a busy event turn.
            try { DeliverToWorker(payload); }
            catch (Exception error) when (error is TimeoutException or ScriptLimitExceededException or ScriptInterruptedException)
            {
                FailWorkerCommand(error);
            }
            if (_parentEventLoop is null)
            {
                DrainCapabilityDenials();
                DrainParentMessages();
                MessagePortEndpoint.DrainAll(Engine);
                DrainErrors();
            }
            return true;
        }
        if (Enabled && !Terminated && OnMessage.IsFunction)
        {
            var message = Engine.CreateHostObject(MessageEventTemplate, new MessageEventState(value, string.Empty, ScriptValue.Null, target: Source, ports: StructuredCloneCodec.CreatePorts(Engine, [])));
            if (OnMessage.IsFunction) Engine.Call(OnMessage, ScriptValue.Undefined, [message]);
            if (_listeners.TryGetValue("message", out var listeners)) foreach (var listener in listeners.ToArray()) if (listener.IsFunction) Engine.Call(listener, ScriptValue.Undefined, [message]);
        }
        return true;
    }

    private void DeliverToWorker(StructuredClonePayload payload)
    {
        RunOnWorker(() =>
        {
            // Commands already execute serially on the worker owner thread.
            // Holding the queue lock across script/host execution would prevent
            // the parent from reporting a timed-out command until it returned.
            if (Terminated) { payload.AbortTransfers(); return; }
            StructuredCloneResult decoded;
            try
            {
                decoded = StructuredCloneCodec.Decode(WorkerEngine!, payload,
                    (port, destination) => port.ClaimTransfer(destination, _workerEventLoop, MaxMessageBytes).GetWrapper());
            }
            catch
            {
                DispatchWorkerMessage("messageerror", ScriptValue.Undefined, []);
                return;
            }
            DispatchWorkerMessage("message", decoded.Value, decoded.Ports);
        }, abandon: payload.AbortTransfers);
    }

    internal void ConnectSharedPort(StructuredClonePayload payload)
    {
        if (WorkerEngine is null || Terminated)
        {
            payload.AbortTransfers();
            throw WebIdlException.DomException(DomExceptionNames.InvalidStateError, "The shared worker is not running.");
        }
        RunOnWorker(() =>
        {
            if (Terminated) { payload.AbortTransfers(); return; }
            try
            {
                var decoded = StructuredCloneCodec.Decode(WorkerEngine!, payload,
                    (port, destination) => port.ClaimTransfer(destination, _workerEventLoop, MaxMessageBytes).GetWrapper());
                var message = WorkerEngine.Call(_workerEventInit, ScriptValue.Undefined,
                    [ScriptValue.Undefined, StructuredCloneCodec.CreatePorts(WorkerEngine, decoded.Ports)]);
                WorkerEngine.Call(_dispatchWorkerEvent, ScriptValue.Undefined,
                    [_workerScope, ScriptValue.FromString("connect"), message]);
            }
            catch (Exception error) { QueueWorkerError(error); }
        }, abandon: payload.AbortTransfers);
        if (_parentEventLoop is null) MessagePortEndpoint.DrainAll(Engine);
    }

    private void DispatchWorkerMessage(string type, ScriptValue data, IReadOnlyList<ScriptValue> ports)
    {
        ScriptValue message;
        try
        {
            message = WorkerEngine!.Call(_workerEventInit, ScriptValue.Undefined,
                [data, StructuredCloneCodec.CreatePorts(WorkerEngine, ports)]);
        }
        catch (Exception error)
        {
            foreach (var port in ports) MessagePortEndpoint.TryGetPort(WorkerEngine!, port)?.Close();
            if (type == "message") DispatchWorkerMessage("messageerror", ScriptValue.Undefined, []);
            else QueueWorkerError(error);
            return;
        }
        try
        {
            WorkerEngine.Call(_dispatchWorkerEvent, ScriptValue.Undefined, [_workerScope, ScriptValue.FromString(type), message]);
        }
        catch (Exception error) { QueueWorkerError(error); }
    }

    private void DispatchNativeWorkerEvent(string type, ScriptValue data)
    {
        if (WorkerEngine is null) return;
        var eventValue = WorkerEngine.CreatePlainObject(new Dictionary<string, ScriptValue>
        {
            ["type"] = ScriptValue.FromString(type), ["data"] = data,
            ["target"] = _workerScope, ["currentTarget"] = _workerScope,
            ["isTrusted"] = ScriptValue.True,
        });
        var handler = WorkerEngine.GetGlobalProperty(type == "message" ? "onmessage" : "onerror");
        if (handler.IsFunction) WorkerEngine.Call(handler, _workerScope, [eventValue]);
        if (type == "message")
            foreach (var listener in _nativeMessageListeners.ToArray()) WorkerEngine.Call(listener, _workerScope, [eventValue]);
    }

    private ScriptValue CreateNativeWorkerEvent(ReadOnlySpan<ScriptValue> args) => WorkerEngine!.CreatePlainObject(new Dictionary<string, ScriptValue>
    {
        ["data"] = args.Length > 0 ? args[0] : ScriptValue.Undefined,
        ["ports"] = args.Length > 1 ? args[1] : WorkerEngine.CreateArray([]),
    });

    private void QueueWorkerError(Exception error)
    {
        if (_closedByWorker) return;
        lock (_workerGate) _pendingErrors.Enqueue(error);
        QueueParentTask(DrainErrors, "worker error");
    }

    private void FailWorkerCommand(Exception error)
    {
        _scriptError = error;
        _terminated = true;
        WorkerEngine?.Interrupt();
        CancelWorkerLoads();
        _workerCommands?.CompleteAdding();
        lock (_workerGate) _pendingErrors.Enqueue(error);
        QueueParentTask(DrainErrors, "worker error");
    }

    private void CancelWorkerLoads()
    {
        lock (_loadGate)
            if (!_loadsDisposed)
            {
                try { _workerLoads.Cancel(); }
                catch (AggregateException) { } // A host cancellation callback cannot prevent worker shutdown.
            }
    }

    private void DispatchToParent(ScriptValue value, ScriptValue transfer)
    {
        if (Terminated) return;
        transfer = StructuredCloneCodec.NormalizeTransfer(WorkerEngine!, transfer, postMessage: true);
        var prepared = StructuredCloneCodec.Prepare(WorkerEngine!, value, transfer, MaxMessageBytes, MessagePortEndpoint.TryGetPort);
        var reservation = _parentMessageBudget.Reserve(prepared.Payload.MemoryCost);
        StructuredClonePayload? payload = null;
        try
        {
            payload = prepared.Commit();
            lock (_workerGate)
            {
                if (!_parentDetached)
                {
                    _pendingParentMessages.Enqueue((payload, reservation));
                    payload = null;
                    reservation = null!;
                }
            }
        }
        finally
        {
            reservation?.Dispose();
            payload?.AbortTransfers();
        }
        QueueParentTask(DrainParentMessages, "worker message");
    }

    private void ClearParentMessages()
    {
        (StructuredClonePayload Payload, ScriptMessageBudget.Reservation Reservation)[] abandoned;
        lock (_workerGate)
        {
            abandoned = _pendingParentMessages.ToArray();
            _pendingParentMessages.Clear();
        }
        foreach (var message in abandoned)
        {
            message.Reservation.Dispose();
            message.Payload.AbortTransfers();
        }
    }

    private void QueueParentTask(Action action, string description)
    {
        if (_parentDetached || _parentEventLoop is null) return;
        try { _parentEventLoop.QueueTask(TaskSource.Generic, action, description); }
        catch (ObjectDisposedException) { _parentDetached = true; ClearParentMessages(); }
    }

    private void DrainParentMessages()
    {
        while (!_parentDetached)
        {
            (StructuredClonePayload Payload, ScriptMessageBudget.Reservation Reservation) next;
            lock (_workerGate)
            {
                if (_pendingParentMessages.Count == 0) return;
                next = _pendingParentMessages.Dequeue();
            }
            next.Reservation.Dispose();
            ScriptValue message;
            StructuredCloneResult? decoded = null;
            try
            {
                decoded = StructuredCloneCodec.Decode(Engine, next.Payload,
                    (port, destination) => port.ClaimTransfer(destination, _parentEventLoop, MaxMessageBytes).GetWrapper());
                message = Engine.CreateHostObject(MessageEventTemplate, new MessageEventState(decoded.Value, string.Empty, ScriptValue.Null, target: Source,
                    ports: StructuredCloneCodec.CreatePorts(Engine, decoded.Ports)));
            }
            catch (Exception error)
            {
                decoded?.AbortPorts(Engine);
                DispatchMessageError(error);
                if (_parentEventLoop is not null) return;
                continue;
            }
            InvokeParentMessageHandler(OnMessage, message);
            if (_listeners.TryGetValue("message", out var listeners))
                foreach (var listener in listeners.ToArray())
                    if (listeners.Contains(listener)) InvokeParentMessageHandler(listener, message);
            if (_parentEventLoop is not null) return;
        }
    }

    private void InvokeParentMessageHandler(ScriptValue handler, ScriptValue message)
    {
        if (!handler.IsFunction) return;
        try { Engine.Call(handler, Source, [message]); }
        catch (Exception error)
        {
            _parentEventLoop?.ReportException(error);
        }
    }

    private void DrainErrors()
    {
        if (_parentDetached) return;
        while (!_parentDetached)
        {
            Exception error;
            lock (_workerGate)
            {
                if (_pendingErrors.Count == 0) return;
                error = _pendingErrors.Dequeue();
            }
            try { ReportError(error); } catch { }
        }
    }

    private void DispatchMessageError(Exception error)
    {
        if (!OnMessageError.IsFunction && (!_listeners.TryGetValue("messageerror", out var listeners) || listeners.Count == 0)) return;
        var message = Engine.CreateHostObject(MessageEventTemplate,
            new MessageEventState(ScriptValue.Undefined, string.Empty, ScriptValue.Null,
                StructuredCloneCodec.CreatePorts(Engine, []), type: "messageerror", target: Source));
        InvokeParentMessageHandler(OnMessageError, message);
        if (_listeners.TryGetValue("messageerror", out var handlers))
            foreach (var handler in handlers.ToArray())
                if (handlers.Contains(handler)) InvokeParentMessageHandler(handler, message);
    }

    private void ReportError(Exception error)
    {
        if (ReferenceEquals(_lastReportedError, error) || (!OnError.IsFunction && (!_listeners.TryGetValue("error", out var listeners) || listeners.Count == 0))) return;
        _lastReportedError = error;
        DispatchError(error);
    }

    private void DispatchError(Exception error)
    {
        Engine.SetGlobalProperty("__folio_worker_error_message", ScriptValue.FromString(error.Message));
        Engine.SetGlobalProperty("__folio_worker_error_target", ScriptValue.FromString(ScriptUrl));
        Engine.SetGlobalProperty("__folio_worker_error_event_target", Source.IsObject ? Source : ScriptValue.Null);
        var message = Engine.Evaluate("({ type: 'error', message: __folio_worker_error_message, filename: __folio_worker_error_target, lineno: 0, colno: 0, target: __folio_worker_error_event_target, preventDefault: function() {} })", ScriptUrl);
        if (OnError.IsFunction) Engine.Call(OnError, ScriptValue.Undefined, [message]);
        if (_listeners.TryGetValue("error", out var listeners)) foreach (var listener in listeners.ToArray()) if (listener.IsFunction) Engine.Call(listener, ScriptValue.Undefined, [message]);
        Engine.SetGlobalProperty("__folio_worker_error_message", ScriptValue.Undefined);
        Engine.SetGlobalProperty("__folio_worker_error_target", ScriptValue.Undefined);
        Engine.SetGlobalProperty("__folio_worker_error_event_target", ScriptValue.Undefined);
    }

    private static bool TryReadScript(string url, IResourceLoader? resourceLoader, out string source, IReadOnlyDictionary<string, string>? blobSources = null, CancellationToken cancellationToken = default)
    {
        source = string.Empty;
        cancellationToken.ThrowIfCancellationRequested();
        if (blobSources is not null && blobSources.TryGetValue(url, out var blobSource)) { source = blobSource; return true; }
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = url.IndexOf(',');
            if (comma < 0) return false;
            var payload = url[(comma + 1)..];
            source = url[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase)
                ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload))
                : Uri.UnescapeDataString(payload);
            return true;
        }
        if (resourceLoader is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var response = resourceLoader.LoadAsync(new ResourceRequest(uri.AbsoluteUri, ResourceKind.Script), cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).GetAwaiter().GetResult();
        if (response?.Data is not { } data) return false;
        source = System.Text.Encoding.UTF8.GetString(data);
        return true;
    }

    private static bool TryReadModuleScript(string url, IResourceLoader? resourceLoader, out string source, HashSet<string>? seen = null, IReadOnlyDictionary<string, string>? blobSources = null, CancellationToken cancellationToken = default)
    {
        source = string.Empty;
        seen ??= new HashSet<string>(StringComparer.Ordinal);
        if (!seen.Add(url)) return true;
        if (!TryReadScript(url, resourceLoader, out source, blobSources, cancellationToken)) return false;
        var imports = new Regex("(?m)^\\s*import\\s+(?:(?:[^;\"']+?)\\s+from\\s+)?[\"']([^\"']+)[\"']\\s*;?\\s*", RegexOptions.Compiled);
        source = imports.Replace(source, match =>
        {
            var resolved = ResolveScriptUrl(url, match.Groups[1].Value);
            if (!TryReadModuleScript(resolved, resourceLoader, out var imported, seen, blobSources, cancellationToken))
                throw new InvalidOperationException($"Worker module import could not load '{resolved}'.");
            return imported + Environment.NewLine;
        });
        source = Regex.Replace(source, @"(?m)^\s*export\s+\{[^}]*\}\s*;?\s*$", string.Empty);
        source = Regex.Replace(source, @"\bexport\s+(?=(?:default\s+)?(?:const|let|var|function|class)\b)", string.Empty);
        source = source.Replace("export default ", string.Empty, StringComparison.Ordinal);
        return true;
    }

    private static string ResolveScriptUrl(string baseUrl, string reference)
    {
        if (reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return reference;
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) && Uri.TryCreate(baseUri, reference, out var resolved)
            ? resolved.AbsoluteUri
            : reference;
    }
}
