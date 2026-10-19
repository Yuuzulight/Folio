using Folio.Resources;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Folio.Scripting;

namespace Folio.Scripting.Dom;

internal sealed class WorkerState
{
    private readonly Action? _releaseWorker;
    private readonly IHostObjectTemplate _messagePortTemplate;
    private readonly TimeSpan _workerExecutionTime;
    private readonly IReadOnlyDictionary<string, string>? _blobSources;
    private readonly object _workerGate = new();
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
    private ScriptValue _workerListenersSnapshot;
    private Exception? _lastReportedError;
    private Document? _workerDocument;
    private readonly HtmlEventLoop? _workerEventLoop;
    private sealed record WorkerCommand(Action Execute, Action Abandon);
    private readonly BlockingCollection<WorkerCommand>? _workerCommands;
    private Thread? _workerThread;
    private int _workerThreadId;
    private long _executionStartedTick;
    private Thread? _watchdogThread;
    public WorkerState(IScriptEngine engine, IHostObjectTemplate messageEventTemplate, IHostObjectTemplate messagePortTemplate, string scriptUrl, bool enabled, int maxMessageBytes, IScriptEngine? workerEngine = null, IResourceLoader? resourceLoader = null, long maxWorkerMemoryBytes = 64L * 1024 * 1024, int maxWebAssemblyBytes = 64 * 1024 * 1024, FeatureCapabilities? capabilities = null, TimeSpan maxWorkerExecutionTime = default, Action? releaseWorker = null, bool moduleWorker = false, IReadOnlyDictionary<string, string>? blobSources = null, HtmlEventLoop? parentEventLoop = null)
    {
        Engine = engine;
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
        _blobSources = blobSources;
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
                    MessagePortBindings.Install(WorkerEngine, MessageEventTemplate, _workerEventLoop!, MaxMessageBytes, maxWorkerMemoryBytes);
                    if (capabilities?.WebAssembly == true)
                    {
                        _workerDocument = Document.Parse("<html></html>", new FolioOptions
                        {
                            BaseUri = Uri.TryCreate(scriptUrl, UriKind.Absolute, out var workerBase) ? workerBase : null,
                            Capabilities = capabilities,
                            Limits = ResourceLimits.Default with { MaxWebAssemblyBytes = Math.Max(0, maxWebAssemblyBytes), MaxWorkerMemoryBytes = Math.Max(1, maxWorkerMemoryBytes), MaxWorkerExecutionTime = _workerExecutionTime },
                            ResourceLoader = resourceLoader,
                        });
                        DomBindings.Install(WorkerEngine, _workerDocument);
                    }
                    var bridge = new HostObjectTemplate("WorkerBridge")
                        .DefineMethod("close", (_, _, _) => { CloseFromWorker(); return ScriptValue.Undefined; })
                        .DefineMethod("postMessage", (_, _, args) => { if (args.Length > 0) { if (args[0].IsFunction) throw WebIdlException.DomException("DataCloneError", "Functions cannot be cloned into a document."); DispatchToParent(args[0], args.Length > 1 ? args[1] : ScriptValue.Undefined); } return ScriptValue.Undefined; });
                    WorkerEngine.SetGlobalProperty("__folio_worker_bridge", WorkerEngine.CreateHostObject(bridge, this));
                    WorkerEngine.Evaluate("globalThis.postMessage = function(value, transfer) { return __folio_worker_bridge.postMessage(value, transfer); };", ScriptUrl);
                    WorkerEngine.Evaluate("globalThis.close = function() { return __folio_worker_bridge.close(); };", ScriptUrl);
                    var imports = new HostObjectTemplate("WorkerImports")
                        .DefineMethod("load", (_, _, args) =>
                        {
                            foreach (var argument in args)
                            {
                                var reference = argument.AsString() ?? string.Empty;
                                var resolved = ResolveScriptUrl(ScriptUrl, reference);
                                if (!TryReadScript(resolved, resourceLoader, out var imported, _blobSources, _workerLoads.Token))
                                    throw new InvalidOperationException($"Worker script could not load '{reference}'.");
                                WorkerEngine.Evaluate(imported, resolved);
                            }
                            return ScriptValue.Undefined;
                        });
                    WorkerEngine.SetGlobalProperty("__folio_worker_imports", WorkerEngine.CreateHostObject(imports, this));
                    WorkerEngine.Evaluate("globalThis.importScripts = function() { return __folio_worker_imports.load.apply(__folio_worker_imports, arguments); };", ScriptUrl);
                    _workerListenersSnapshot = WorkerEngine.EvaluateHostScript("""
                        (function() {
                            const lists = Object.create(null), apply = Reflect.apply;
                            const push = Array.prototype.push, slice = Array.prototype.slice;
                            const indexOf = Array.prototype.indexOf, splice = Array.prototype.splice;
                            globalThis.addEventListener = function(type, handler) {
                                if (typeof handler !== 'function') return;
                                const list = lists[type] || (lists[type] = []);
                                if (apply(indexOf, list, [handler]) < 0) apply(push, list, [handler]);
                            };
                            globalThis.removeEventListener = function(type, handler) {
                                const list = lists[type];
                                if (!list) return;
                                const index = apply(indexOf, list, [handler]);
                                if (index >= 0) apply(splice, list, [index, 1]);
                            };
                            return function(type, handler) {
                                const list = lists[type] || [];
                                return arguments.length > 1 ? apply(indexOf, list, [handler]) >= 0 : apply(slice, list, []);
                            };
                        })()
                        """, "folio:worker-listeners");
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
            if ((moduleWorker ? TryReadModuleScript(scriptUrl, resourceLoader, out var source, blobSources: _blobSources) : TryReadScript(scriptUrl, resourceLoader, out source, blobSources: _blobSources)))
            {
                try { RunOnWorker(() =>
                {
                    try { WorkerEngine!.Evaluate(source, scriptUrl); }
                    catch (Exception error) { if (!_closedByWorker) { _scriptError = error; lock (_workerGate) _pendingErrors.Enqueue(error); } }
                }); }
                catch (Exception error) { if (!_closedByWorker) FailWorkerCommand(error); }
                if (_parentEventLoop is null)
                {
                    DrainParentMessages();
                    DrainErrors();
                }
            }
            else
            {
                _scriptError = new InvalidOperationException($"Worker script could not load '{scriptUrl}'.");
            }
        }
    }

    public IScriptEngine Engine { get; }
    public IScriptEngine? WorkerEngine { get; }
    public IHostObjectTemplate MessageEventTemplate { get; }
    public string ScriptUrl { get; }
    public string Origin { get; }
    public bool Enabled { get; }
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
        // The worker loop marks itself terminated before cleanup. Let the
        // watchdog exit before disposing the engine it may still interrupt.
        _watchdogThread?.Join();
        while (_workerCommands is not null && _workerCommands.TryTake(out var abandoned)) abandoned.Abandon();
        try { if (WorkerEngine is not null) DomBindings.Dispose(WorkerEngine); } catch { }
        _workerEventLoop?.Dispose();
        WorkerEngine?.Dispose();
        // The page may keep its Worker object after terminate(). These are
        // handles into the worker VM, unlike the page-owned event listeners.
        _workerScope = ScriptValue.Undefined;
        _workerListenersSnapshot = ScriptValue.Undefined;
        _workerDocument?.Dispose();
        _workerDocument = null;
        lock (_loadGate)
        {
            _loadsDisposed = true;
            _workerLoads.Dispose();
        }
        lock (_workerGate)
        {
            if (_released) return;
            _releaseWorker?.Invoke();
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
        if (value.IsString && (value.AsString()?.Length ?? 0) > MaxMessageBytes) return false;
        if (_scriptError is not null && !Terminated) { ReportError(_scriptError); return true; }
        if (WorkerEngine is not null && !Terminated)
        {
            transfer = StructuredCloneCodec.NormalizeTransfer(Engine, transfer, postMessage: true);
            var prepared = StructuredCloneCodec.Prepare(Engine, value, transfer, MaxMessageBytes, MessagePortEndpoint.TryGetPort);
            using var reservation = _workerMessageBudget!.Reserve(prepared.Payload.MemoryCost);
            var payload = prepared.Commit();
            // RunOnWorker queues the command when the worker is already executing a
            // callback, so delivery cannot be stranded behind a busy event turn.
            try { DeliverToWorker(payload); }
            catch (Exception error) when (error is TimeoutException or ScriptLimitExceededException or ScriptInterruptedException)
            {
                FailWorkerCommand(error);
            }
            if (_parentEventLoop is null)
            {
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

    private void DispatchWorkerMessage(string type, ScriptValue data, IReadOnlyList<ScriptValue> ports)
    {
        ScriptValue message;
        try
        {
            message = WorkerEngine!.CreateHostObject(MessageEventTemplate,
                new MessageEventState(data, string.Empty, ScriptValue.Null,
                    StructuredCloneCodec.CreatePorts(WorkerEngine, ports), type, _workerScope));
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
            var listeners = WorkerEngine.Call(_workerListenersSnapshot, _workerScope, [ScriptValue.FromString(type)]);
            WorkerEngine.TryGetArrayItems(listeners, out var handlers);
            InvokeWorkerMessageHandler(WorkerEngine.GetGlobalProperty("on" + type), message);
            foreach (var handler in handlers)
                if (WorkerEngine.Call(_workerListenersSnapshot, _workerScope, [ScriptValue.FromString(type), handler]).AsBoolean())
                    InvokeWorkerMessageHandler(handler, message);
        }
        catch (Exception error) { QueueWorkerError(error); }
    }

    private void InvokeWorkerMessageHandler(ScriptValue handler, ScriptValue message)
    {
        if (!handler.IsFunction || Terminated) return;
        try { WorkerEngine!.Call(handler, _workerScope, [message]); }
        catch (Exception error) { QueueWorkerError(error); }
    }

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

    private static bool TryReadModuleScript(string url, IResourceLoader? resourceLoader, out string source, HashSet<string>? seen = null, IReadOnlyDictionary<string, string>? blobSources = null)
    {
        source = string.Empty;
        seen ??= new HashSet<string>(StringComparer.Ordinal);
        if (!seen.Add(url) || !TryReadScript(url, resourceLoader, out source, blobSources)) return false;
        var imports = new Regex("(?m)^\\s*import\\s+(?:(?:[^;\"']+?)\\s+from\\s+)?[\"']([^\"']+)[\"']\\s*;?\\s*", RegexOptions.Compiled);
        source = imports.Replace(source, match =>
        {
            var resolved = ResolveScriptUrl(url, match.Groups[1].Value);
            return TryReadModuleScript(resolved, resourceLoader, out var imported, seen, blobSources) ? imported + Environment.NewLine : string.Empty;
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
