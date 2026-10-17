using Folio.Resources;
using System.Text.RegularExpressions;

namespace Folio.Scripting.Dom;

internal sealed class WorkerState
{
    private readonly Action? _releaseWorker;
    private readonly TimeSpan _workerExecutionTime;
    private readonly IReadOnlyDictionary<string, string>? _blobSources;
    private readonly object _workerGate = new();
    private bool _released;
    private volatile bool _closedByWorker;
    private volatile bool _workerExecuting;
    private Task? _startupTask;
    public WorkerState(IScriptEngine engine, IHostObjectTemplate messageEventTemplate, string scriptUrl, bool enabled, int maxMessageBytes, IScriptEngine? workerEngine = null, IResourceLoader? resourceLoader = null, long maxWorkerMemoryBytes = 64L * 1024 * 1024, TimeSpan maxWorkerExecutionTime = default, Action? releaseWorker = null, bool moduleWorker = false, IReadOnlyDictionary<string, string>? blobSources = null)
    {
        Engine = engine;
        MessageEventTemplate = messageEventTemplate;
        ScriptUrl = scriptUrl;
        Origin = Uri.TryCreate(scriptUrl, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : "null";
        Enabled = enabled;
        MaxMessageBytes = Math.Max(0, maxMessageBytes);
        WorkerEngine = workerEngine;
        _releaseWorker = releaseWorker;
        _blobSources = blobSources;
        _workerExecutionTime = maxWorkerExecutionTime <= TimeSpan.Zero ? TimeSpan.FromSeconds(5) : maxWorkerExecutionTime;
        WorkerEngine?.Configure(new ScriptLimits { MemoryLimitBytes = Math.Max(1, maxWorkerMemoryBytes), Timeout = _workerExecutionTime });
        EnsureCloneHelpers(Engine);
        if (WorkerEngine is not null) EnsureCloneHelpers(WorkerEngine);
        if (WorkerEngine is not null)
        {
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
                        if (!TryReadScript(resolved, resourceLoader, out var imported, _blobSources))
                            throw new InvalidOperationException($"Worker script could not load '{reference}'.");
                        WorkerEngine.Evaluate(imported, resolved);
                    }
                    return ScriptValue.Undefined;
                });
            WorkerEngine.SetGlobalProperty("__folio_worker_imports", WorkerEngine.CreateHostObject(imports, this));
            WorkerEngine.Evaluate("globalThis.importScripts = function() { return __folio_worker_imports.load.apply(null, arguments); };", ScriptUrl);
            WorkerEngine.Evaluate("globalThis.__folio_worker_listeners = {}; globalThis.addEventListener = function(type, handler) { if (typeof handler !== 'function') return; (globalThis.__folio_worker_listeners[type] || (globalThis.__folio_worker_listeners[type] = [])).push(handler); }; globalThis.removeEventListener = function(type, handler) { var list = globalThis.__folio_worker_listeners[type] || []; globalThis.__folio_worker_listeners[type] = list.filter(function(item) { return item !== handler; }); };", ScriptUrl);
            WorkerEngine.SetGlobalProperty("self", WorkerEngine.Evaluate("globalThis"));
            if ((moduleWorker ? TryReadModuleScript(scriptUrl, resourceLoader, out var source, blobSources: _blobSources) : TryReadScriptWithImports(scriptUrl, resourceLoader, out source, blobSources: _blobSources)))
            {
                _startupTask = Task.Run(() =>
                {
                    _workerExecuting = true;
                    try { ExecuteWithWatchdog(() => WorkerEngine.Evaluate(source, scriptUrl)); }
                    catch (Exception error) { if (!_closedByWorker) _scriptError = error; }
                    finally { _workerExecuting = false; }
                });
                try { _startupTask.Wait(TimeSpan.FromMilliseconds(500)); }
                catch (AggregateException) { }
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
    public bool Terminated { get; private set; }
    public ScriptValue OnMessage { get; set; } = ScriptValue.Null;
    public ScriptValue OnError { get; set; } = ScriptValue.Null;
    public ScriptValue Source { get; set; } = ScriptValue.Null;
    private readonly Dictionary<string, List<ScriptValue>> _listeners = new(StringComparer.Ordinal);
    private volatile Exception? _scriptError;
    public void AddEventListener(string type, ScriptValue listener)
    {
        if (!listener.IsFunction) return;
        if (!_listeners.TryGetValue(type, out var list)) _listeners[type] = list = [];
        if (!list.Contains(listener)) list.Add(listener);
    }
    public void RemoveEventListener(string type, ScriptValue listener)
    {
        if (_listeners.TryGetValue(type, out var list)) list.Remove(listener);
    }
    public void Terminate()
    {
        if (Terminated) return;
        Terminated = true;
        WorkerEngine?.Interrupt();
        try { _startupTask?.Wait(TimeSpan.FromMilliseconds(100)); }
        catch (AggregateException) { }
        WorkerEngine?.Dispose();
        if (!_released) { _released = true; _releaseWorker?.Invoke(); }
    }
    private void CloseFromWorker()
    {
        _closedByWorker = true;
        Terminated = true;
        WorkerEngine?.Interrupt();
        _ = Task.Run(() =>
        {
            while (_workerExecuting) Thread.Yield();
            WorkerEngine?.Dispose();
            lock (_workerGate)
            {
                if (_released) return;
                _released = true;
                _releaseWorker?.Invoke();
            }
        });
    }
    private void ExecuteWithWatchdog(Action action)
    {
        using var cancellation = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_workerExecutionTime, cancellation.Token).ConfigureAwait(false);
                if (!cancellation.IsCancellationRequested && !Terminated) WorkerEngine?.Interrupt();
            }
            catch (OperationCanceledException) { }
        });
        try { action(); }
        finally { cancellation.Cancel(); }
    }
    public bool PostMessage(ScriptValue value, ScriptValue transfer = default)
    {
        if (value.IsString && (value.AsString()?.Length ?? 0) > MaxMessageBytes) return false;
        if (_scriptError is not null && !Terminated) { DispatchError(_scriptError); return true; }
        if (WorkerEngine is not null && !Terminated)
        {
            if (_workerExecuting) return true;
            var transferred = TransferList(Engine, transfer);
            var message = CloneIntoWorker(value);
            lock (_workerGate)
            {
                if (Terminated || _workerExecuting) return true;
                _workerExecuting = true;
                try
                {
                    WorkerEngine.SetGlobalProperty("__folio_worker_message_data", message);
                    WorkerEngine.SetGlobalProperty("__folio_worker_message_event", WorkerEngine.Evaluate("({ type: 'message', data: __folio_worker_message_data, target: self })", ScriptUrl));
                    ExecuteWithWatchdog(() => WorkerEngine.Evaluate("(function() { var event = __folio_worker_message_event; if (typeof onmessage === 'function') onmessage(event); var listeners = (__folio_worker_listeners.message || []).slice(); listeners.forEach(function(listener) { listener(event); }); })()", ScriptUrl));
                    WorkerEngine.SetGlobalProperty("__folio_worker_message_data", ScriptValue.Undefined);
                }
                catch (Exception error) { if (!_closedByWorker) { _scriptError = error; try { DispatchError(error); } catch { } } }
                finally { _workerExecuting = false; }
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

    private void DispatchToParent(ScriptValue value, ScriptValue transfer)
    {
        if (value.IsString && (value.AsString()?.Length ?? 0) > MaxMessageBytes) return;
        var transferred = TransferList(WorkerEngine!, transfer);
        value = CloneIntoParent(value);
        var message = Engine.CreateHostObject(MessageEventTemplate, new MessageEventState(value, Origin, Source));
        if (OnMessage.IsFunction) Engine.Call(OnMessage, ScriptValue.Undefined, [message]);
        if (_listeners.TryGetValue("message", out var listeners)) foreach (var listener in listeners.ToArray()) if (listener.IsFunction) Engine.Call(listener, ScriptValue.Undefined, [message]);
        foreach (var item in transferred) WorkerEngine!.TryDetachArrayBuffer(item);
    }

    private static IReadOnlyList<ScriptValue> TransferList(IScriptEngine engine, ScriptValue transfer)
    {
        if (transfer.IsNullOrUndefined) return [];
        if (!engine.TryGetArrayItems(transfer, out var items))
            throw WebIdlException.DomException("DataCloneError", "The transfer list must be an array.");
        foreach (var item in items)
            if (!engine.TryGetArrayBufferBytes(item, out _))
                throw WebIdlException.DomException("DataCloneError", "Only ArrayBuffer values may be transferred.");
        return items;
    }

    private ScriptValue CloneIntoWorker(ScriptValue value)
    {
        if (value.IsNullOrUndefined || !value.IsObject) return value;
        if (Engine.TryGetArrayBufferBytes(value, out var buffer)) return WorkerEngine!.CreateArrayBuffer(buffer);
        try
        {
            Engine.SetGlobalProperty("__folio_clone_value", value);
            var json = Engine.Evaluate("__folio_encode_clone(__folio_clone_value)").AsString();
            Engine.SetGlobalProperty("__folio_clone_value", ScriptValue.Undefined);
            if (json is null) throw new InvalidOperationException("The value cannot be cloned.");
            WorkerEngine!.SetGlobalProperty("__folio_clone_json", ScriptValue.FromString(json));
            var clone = WorkerEngine.Evaluate("__folio_decode_clone(__folio_clone_json)");
            WorkerEngine.SetGlobalProperty("__folio_clone_json", ScriptValue.Undefined);
            return clone;
        }
        catch (Exception error)
        {
            throw WebIdlException.DomException("DataCloneError", error.Message);
        }
    }

    private ScriptValue CloneIntoParent(ScriptValue value)
    {
        if (value.IsNullOrUndefined || !value.IsObject) return value;
        if (WorkerEngine!.TryGetArrayBufferBytes(value, out var buffer)) return Engine.CreateArrayBuffer(buffer);
        try
        {
            WorkerEngine!.SetGlobalProperty("__folio_clone_value", value);
            var json = WorkerEngine.Evaluate("__folio_encode_clone(__folio_clone_value)").AsString();
            WorkerEngine.SetGlobalProperty("__folio_clone_value", ScriptValue.Undefined);
            if (json is null) throw new InvalidOperationException("The value cannot be cloned.");
            Engine.SetGlobalProperty("__folio_clone_json", ScriptValue.FromString(json));
            var clone = Engine.Evaluate("__folio_decode_clone(__folio_clone_json)");
            Engine.SetGlobalProperty("__folio_clone_json", ScriptValue.Undefined);
            return clone;
        }
        catch (Exception error)
        {
            throw WebIdlException.DomException("DataCloneError", error.Message);
        }
    }

    private static void EnsureCloneHelpers(IScriptEngine engine) => engine.Evaluate(StructuredCloneScript);

    private const string StructuredCloneScript = """
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

    private void DispatchError(Exception error)
    {
        Engine.SetGlobalProperty("__folio_worker_error_message", ScriptValue.FromString(error.Message));
        Engine.SetGlobalProperty("__folio_worker_error_target", Source);
        var message = Engine.Evaluate("({ type: 'error', message: __folio_worker_error_message, target: __folio_worker_error_target })", ScriptUrl);
        if (OnError.IsFunction) Engine.Call(OnError, ScriptValue.Undefined, [message]);
        if (_listeners.TryGetValue("error", out var listeners)) foreach (var listener in listeners.ToArray()) if (listener.IsFunction) Engine.Call(listener, ScriptValue.Undefined, [message]);
        Engine.SetGlobalProperty("__folio_worker_error_message", ScriptValue.Undefined);
        Engine.SetGlobalProperty("__folio_worker_error_target", ScriptValue.Undefined);
    }

    private static bool TryReadScript(string url, IResourceLoader? resourceLoader, out string source, IReadOnlyDictionary<string, string>? blobSources = null)
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
        var response = resourceLoader.LoadAsync(new ResourceRequest(uri.AbsoluteUri, ResourceKind.Script), CancellationToken.None).GetAwaiter().GetResult();
        if (response?.Data is not { } data) return false;
        source = System.Text.Encoding.UTF8.GetString(data);
        return true;
    }

    private static bool TryReadScriptWithImports(string url, IResourceLoader? resourceLoader, out string source, HashSet<string>? seen = null, IReadOnlyDictionary<string, string>? blobSources = null)
    {
        source = string.Empty;
        seen ??= new HashSet<string>(StringComparer.Ordinal);
        if (!seen.Add(url) || !TryReadScript(url, resourceLoader, out source, blobSources)) return false;
        const string marker = "importScripts";
        for (var search = 0; ;)
        {
            var start = source.IndexOf(marker, search, StringComparison.Ordinal);
            if (start < 0) break;
            var open = source.IndexOf('(', start + marker.Length);
            var quote = open >= 0 ? source.IndexOfAny(['\'', '\"'], open + 1) : -1;
            var closeQuote = quote >= 0 ? source.IndexOf(source[quote], quote + 1) : -1;
            var close = closeQuote >= 0 ? source.IndexOf(')', closeQuote + 1) : -1;
            if (open < 0 || quote < 0 || closeQuote < 0 || close < 0) { search = start + marker.Length; continue; }
            var reference = source[(quote + 1)..closeQuote];
            var resolved = ResolveScriptUrl(url, reference);
            if (!TryReadScriptWithImports(resolved, resourceLoader, out var imported, seen, blobSources)) return false;
            source = source.Remove(start, close - start + 1).Insert(start, imported);
            search = start + imported.Length;
        }
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
