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
    private readonly Queue<ClonePayload> _pendingParentMessages = new();
    private readonly Queue<Exception> _pendingParentCloneErrors = new();
    private readonly HtmlEventLoop? _parentEventLoop;
    private readonly Queue<Exception> _pendingErrors = new();
    private bool _released;
    private volatile bool _closedByWorker;
    private volatile bool _parentDetached;
    private volatile bool _workerExecuting;
    private ScriptValue _onError = ScriptValue.Null;
    private bool _errorReported;
    private Document? _workerDocument;
    private readonly HtmlEventLoop? _workerEventLoop;
    private readonly BlockingCollection<Action>? _workerCommands;
    private Thread? _workerThread;
    private int _workerThreadId;
    private long _executionStartedTick;
    private Thread? _watchdogThread;
    public WorkerState(IScriptEngine engine, IHostObjectTemplate messageEventTemplate, IHostObjectTemplate messagePortTemplate, string scriptUrl, bool enabled, int maxMessageBytes, IScriptEngine? workerEngine = null, IResourceLoader? resourceLoader = null, long maxWorkerMemoryBytes = 64L * 1024 * 1024, int maxWebAssemblyBytes = 64 * 1024 * 1024, FeatureCapabilities? capabilities = null, TimeSpan maxWorkerExecutionTime = default, Action? releaseWorker = null, bool moduleWorker = false, IReadOnlyDictionary<string, string>? blobSources = null, HtmlEventLoop? parentEventLoop = null)
    {
        Engine = engine;
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
            _workerCommands = new BlockingCollection<Action>();
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
        EnsureCloneHelpers(Engine);
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
                    EnsureCloneHelpers(WorkerEngine);
                    ScriptGlobalsInstaller.Install(_workerEventLoop!, WorkerEngine);
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
                    WorkerEngine.Evaluate("globalThis.postMessage = function(value) { return __folio_worker_bridge.postMessage(value); };", ScriptUrl);
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
                    WorkerEngine.Evaluate("globalThis.__folio_worker_listeners = {}; globalThis.addEventListener = function(type, handler) { if (typeof handler !== 'function') return; (globalThis.__folio_worker_listeners[type] || (globalThis.__folio_worker_listeners[type] = [])).push(handler); }; globalThis.removeEventListener = function(type, handler) { var list = globalThis.__folio_worker_listeners[type] || []; globalThis.__folio_worker_listeners[type] = list.filter(function(item) { return item !== handler; }); };", ScriptUrl);
                    WorkerEngine.SetGlobalProperty("self", WorkerEngine.Evaluate("globalThis"));
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
                    if (_workerCommands!.TryTake(out var command, TimeSpan.FromMilliseconds(1)))
                        command();
                    if (!Terminated && !_workerEventLoop!.IsIdle())
                        ExecuteWithWatchdog(() => _workerEventLoop.RunOneTurn());
                    else if (!Terminated)
                        Thread.Sleep(1);
                }
                catch (InvalidOperationException) { break; }
                catch (Exception error) { if (!_closedByWorker) { _scriptError = error; lock (_workerGate) _pendingErrors.Enqueue(error); } }
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
        try { if (WorkerEngine is not null) DomBindings.Dispose(WorkerEngine); } catch { }
        _workerEventLoop?.Dispose();
        WorkerEngine?.Dispose();
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

    private void RunOnWorker(Action action, bool enforceWatchdog = true)
    {
        if (WorkerEngine is null || Environment.CurrentManagedThreadId == _workerThreadId)
        {
            action();
            return;
        }

        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? failure = null;
        try
        {
            _workerCommands!.Add(() =>
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
            });
        }
        catch (InvalidOperationException) { return; }
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
            var transferred = TransferList(Engine, transfer);
            var payload = PrepareClone(Engine, value);
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
                DrainErrors();
            }
            foreach (var item in transferred) Engine.TryDetachArrayBuffer(item);
            return true;
        }
        if (Enabled && !Terminated && OnMessage.IsFunction)
        {
            var message = Engine.CreateHostObject(MessageEventTemplate, new MessageEventState(value, Origin, Source));
            if (OnMessage.IsFunction) Engine.Call(OnMessage, ScriptValue.Undefined, [message]);
            if (_listeners.TryGetValue("message", out var listeners)) foreach (var listener in listeners.ToArray()) if (listener.IsFunction) Engine.Call(listener, ScriptValue.Undefined, [message]);
        }
        return true;
    }

    private void DeliverToWorker(ClonePayload payload)
    {
        RunOnWorker(() =>
        {
            // Commands already execute serially on the worker owner thread.
            // Holding the queue lock across script/host execution would prevent
            // the parent from reporting a timed-out command until it returned.
            if (Terminated) return;
            try
            {
                var message = MaterializeClone(WorkerEngine!, payload);
                WorkerEngine!.SetGlobalProperty("__folio_worker_message_data", message);
                WorkerEngine!.SetGlobalProperty("__folio_worker_message_event", WorkerEngine!.Evaluate("({ type: 'message', data: __folio_worker_message_data, target: self })", ScriptUrl));
                ExecuteWithWatchdog(() => WorkerEngine!.Evaluate("(function() { var event = __folio_worker_message_event; if (typeof onmessage === 'function') onmessage(event); var listeners = (__folio_worker_listeners.message || []).slice(); listeners.forEach(function(listener) { listener(event); }); })()", ScriptUrl));
                WorkerEngine!.SetGlobalProperty("__folio_worker_message_data", ScriptValue.Undefined);
            }
            catch (Exception error) { if (!_closedByWorker) { _scriptError = error; lock (_workerGate) _pendingErrors.Enqueue(error); } }
        });
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
        if (value.IsString && (value.AsString()?.Length ?? 0) > MaxMessageBytes) return;
        try
        {
            var transferred = TransferList(WorkerEngine!, transfer);
            var payload = PrepareClone(WorkerEngine!, value);
            foreach (var item in transferred) WorkerEngine!.TryDetachArrayBuffer(item);
            lock (_workerGate) _pendingParentMessages.Enqueue(payload);
            QueueParentTask(DrainParentMessages, "worker message");
        }
        catch (Exception error)
        {
            lock (_workerGate) _pendingParentCloneErrors.Enqueue(error);
            QueueParentTask(DrainParentMessages, "worker message error");
        }
    }

    private void QueueParentTask(Action action, string description)
    {
        if (_parentDetached || _parentEventLoop is null) return;
        try { _parentEventLoop.QueueTask(TaskSource.Generic, action, description); }
        catch (ObjectDisposedException) { }
    }

    private void DrainParentMessages()
    {
        if (_parentDetached) return;
        while (!_parentDetached)
        {
            Exception? cloneError = null;
            ClonePayload next = null!;
            lock (_workerGate)
            {
                if (_pendingParentCloneErrors.Count > 0)
                    cloneError = _pendingParentCloneErrors.Dequeue();
                else if (_pendingParentMessages.Count > 0)
                    next = _pendingParentMessages.Dequeue();
                else
                    return;
            }
            if (cloneError is not null) { DispatchMessageError(cloneError); continue; }
            ScriptValue value;
            try
            {
                value = MaterializeClone(Engine, next);
            }
            catch (Exception error)
            {
                DispatchMessageError(error);
                continue;
            }
        var message = Engine.CreateHostObject(MessageEventTemplate, new MessageEventState(value, Origin, Source));
        if (OnMessage.IsFunction) Engine.Call(OnMessage, ScriptValue.Undefined, [message]);
        if (_listeners.TryGetValue("message", out var listeners)) foreach (var listener in listeners.ToArray()) if (listener.IsFunction) Engine.Call(listener, ScriptValue.Undefined, [message]);
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

    private static IReadOnlyList<ScriptValue> TransferList(IScriptEngine engine, ScriptValue transfer)
    {
        if (transfer.IsNullOrUndefined) return [];
        if (!engine.TryGetArrayItems(transfer, out var items))
            throw WebIdlException.DomException("DataCloneError", "The transfer list must be an array.");
        foreach (var item in items)
            if (!engine.TryGetArrayBufferBytes(item, out _) && !TryGetPort(engine, item, out _))
                throw WebIdlException.DomException("DataCloneError", "Only ArrayBuffer values may be transferred.");
        return items;
    }

    private ClonePayload PrepareClone(IScriptEngine source, ScriptValue value)
    {
        if (value.IsNullOrUndefined || !value.IsObject) return ClonePayload.MakePrimitive(value);
        if (TryGetPort(source, value, out var port)) return ClonePayload.MakePort(port, nested: false);
        var nestedPort = source.GetProperty(value, "port");
        if (TryGetPort(source, nestedPort, out var nested)) return ClonePayload.MakePort(nested, nested: true);
        if (source.TryGetArrayBufferBytes(value, out var buffer)) return ClonePayload.MakeBuffer(buffer);
        try
        {
            source.SetGlobalProperty("__folio_clone_value", value);
            var json = source.Evaluate("__folio_encode_clone(__folio_clone_value)").AsString();
            source.SetGlobalProperty("__folio_clone_value", ScriptValue.Undefined);
            if (json is null) throw new InvalidOperationException("The value cannot be cloned.");
            return ClonePayload.MakeJson(json);
        }
        catch (Exception error)
        {
            throw WebIdlException.DomException("DataCloneError", error.Message);
        }
    }

    private ScriptValue MaterializeClone(IScriptEngine destination, ClonePayload payload)
    {
        if (payload.Kind == ClonePayloadKind.Primitive) return payload.Value;
        if (payload.Kind == ClonePayloadKind.Port)
        {
            var transferred = payload.Port!.TransferTo(destination);
            var portValue = destination.CreateHostObject(_messagePortTemplate, transferred);
            if (!payload.NestedPort) return portValue;
            destination.SetGlobalProperty("__folio_transferred_port", portValue);
            var nested = destination.Evaluate("({ port: __folio_transferred_port })", ScriptUrl);
            destination.SetGlobalProperty("__folio_transferred_port", ScriptValue.Undefined);
            return nested;
        }
        if (payload.Kind == ClonePayloadKind.Buffer) return destination.CreateArrayBuffer(payload.Buffer!);
        destination.SetGlobalProperty("__folio_clone_json", ScriptValue.FromString(payload.Json!));
        var clone = destination.Evaluate("__folio_decode_clone(__folio_clone_json)");
        destination.SetGlobalProperty("__folio_clone_json", ScriptValue.Undefined);
        return clone;
    }

    private static void EnsureCloneHelpers(IScriptEngine engine) => engine.EvaluateHostScript(StructuredCloneScript, "folio:structured-clone");

    private static bool TryGetPort(IScriptEngine engine, ScriptValue value, out MessagePortEndpoint port)
    {
        port = null!;
        if (!value.IsObject) return false;
        var endpoint = engine.GetProperty(value, "_endpoint");
        if (!engine.TryGetHostTarget(endpoint, out var target) || target is not MessagePortEndpoint found)
            return false;
        port = found;
        return true;
    }

    private enum ClonePayloadKind { Primitive, Json, Buffer, Port }

    private sealed class ClonePayload
    {
        private ClonePayload(ClonePayloadKind kind, ScriptValue value = default, string? json = null, byte[]? buffer = null, MessagePortEndpoint? port = null, bool nestedPort = false)
        {
            Kind = kind;
            Value = value;
            Json = json;
            Buffer = buffer;
            Port = port;
            NestedPort = nestedPort;
        }

        public ClonePayloadKind Kind { get; }
        public ScriptValue Value { get; }
        public string? Json { get; }
        public byte[]? Buffer { get; }
        public MessagePortEndpoint? Port { get; }
        public bool NestedPort { get; }
        public static ClonePayload MakePrimitive(ScriptValue value) => new(ClonePayloadKind.Primitive, value: value);
        public static ClonePayload MakeJson(string json) => new(ClonePayloadKind.Json, json: json);
        public static ClonePayload MakeBuffer(byte[] buffer) => new(ClonePayloadKind.Buffer, buffer: buffer);
        public static ClonePayload MakePort(MessagePortEndpoint port, bool nested) => new(ClonePayloadKind.Port, port: port, nestedPort: nested);
    }

    internal const string StructuredCloneScript = """
        (function() {
          if (globalThis.__folio_encode_clone) return;
          globalThis.__folio_encode_clone = function(root) {
            const seen = new Map(), records = [];
            function scalar(v) {
              if (v === undefined) return { u: 1 };
              if (typeof v === 'number' && Number.isNaN(v)) return { n: 'NaN' };
              if (v === Infinity) return { n: 'Infinity' };
              if (v === -Infinity) return { n: '-Infinity' };
              if (typeof v === 'bigint') return { i: String(v) };
              return { p: v };
            }
            function visit(v) {
              if (v === null || typeof v !== 'object') {
                if (typeof v === 'function' || typeof v === 'symbol') throw new Error('Value cannot be cloned');
                return scalar(v);
              }
              if (seen.has(v)) return { r: seen.get(v) };
              const id = records.length, rec = { t: 'o' }; seen.set(v, id); records.push(rec);
              if (v instanceof ArrayBuffer) { rec.t = 'ab'; rec.b = Array.from(new Uint8Array(v)); }
              else if (ArrayBuffer.isView(v)) { rec.t = 'ta'; rec.c = v.constructor.name; rec.b = Array.from(new Uint8Array(v.buffer, v.byteOffset, v.byteLength)); }
              else if (v instanceof Date) { rec.t = 'date'; rec.v = v.getTime(); }
              else if (v instanceof RegExp) { rec.t = 're'; rec.s = v.source; rec.f = v.flags; rec.i = v.lastIndex; }
              else if (v instanceof Map) { rec.t = 'map'; rec.e = Array.from(v, x => [visit(x[0]), visit(x[1])]); }
              else if (v instanceof Set) { rec.t = 'set'; rec.e = Array.from(v, visit); }
              else if (v instanceof Error) { rec.t = 'error'; rec.n = v.name; rec.m = v.message; rec.s = v.stack; }
              else if (v instanceof Function) throw new Error('Function cannot be cloned');
              else if (Array.isArray(v)) { rec.t = 'array'; rec.e = v.map(visit); }
              else { rec.p = Object.keys(v).map(k => [k, visit(v[k])]); }
              return { r: id };
            }
            return JSON.stringify({ root: visit(root), records });
          };
          globalThis.__folio_decode_clone = function(text) {
            const data = JSON.parse(text), records = data.records || [], objects = records.map(r => {
              if (r.t === 'ab') return new Uint8Array(r.b || []).buffer;
              if (r.t === 'ta') { const b = new Uint8Array(r.b || []).buffer; return r.c === 'DataView' ? new DataView(b) : new globalThis[r.c](b); }
              if (r.t === 'date') return new Date(r.v);
              if (r.t === 're') { const x = new RegExp(r.s, r.f); x.lastIndex = r.i || 0; return x; }
              if (r.t === 'map') return new Map();
              if (r.t === 'set') return new Set();
              if (r.t === 'error') { const x = new Error(r.m); x.name = r.n; x.stack = r.s; return x; }
              return r.t === 'array' ? [] : {};
            });
            function read(x) { if (x && x.r !== undefined) return objects[x.r]; if (x && x.u) return undefined; if (x && x.n) return x.n === 'NaN' ? NaN : Number(x.n); if (x && x.i) return BigInt(x.i); return x ? x.p : undefined; }
            records.forEach((r, i) => { const o = objects[i]; if (r.t === 'map') r.e.forEach(x => o.set(read(x[0]), read(x[1]))); else if (r.t === 'set') r.e.forEach(x => o.add(read(x))); else if (r.t === 'array') r.e.forEach((x, j) => o[j] = read(x)); else if (r.t === 'o') r.p.forEach(x => o[x[0]] = read(x[1])); });
            return read(data.root);
          };
        })();
        """;

    private void DispatchMessageError(Exception error)
    {
        if (!OnMessageError.IsFunction && (!_listeners.TryGetValue("messageerror", out var listeners) || listeners.Count == 0)) return;
        Engine.SetGlobalProperty("__folio_worker_messageerror", ScriptValue.FromString(error.Message));
        Engine.SetGlobalProperty("__folio_worker_message_target", Source.IsObject ? Source : ScriptValue.Null);
        var message = Engine.Evaluate("({ type: 'messageerror', message: __folio_worker_messageerror, target: __folio_worker_message_target })", ScriptUrl);
        Engine.SetGlobalProperty("__folio_worker_messageerror", ScriptValue.Undefined);
        Engine.SetGlobalProperty("__folio_worker_message_target", ScriptValue.Undefined);
        if (OnMessageError.IsFunction) Engine.Call(OnMessageError, ScriptValue.Undefined, [message]);
        if (_listeners.TryGetValue("messageerror", out var handlers)) foreach (var handler in handlers.ToArray()) if (handler.IsFunction) Engine.Call(handler, ScriptValue.Undefined, [message]);
    }

    private void ReportError(Exception error)
    {
        if (_errorReported || (!OnError.IsFunction && (!_listeners.TryGetValue("error", out var listeners) || listeners.Count == 0))) return;
        _errorReported = true;
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
