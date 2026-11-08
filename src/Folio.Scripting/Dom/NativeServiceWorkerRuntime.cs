using System.Diagnostics;
using System.Text.Json;
using System.Text;
using Folio.Resources;
namespace Folio.Scripting.Dom;

/// <summary>Owns the isolated engine used by one service-worker registration.</summary>
internal sealed class IsolatedServiceWorkerRuntime : IDisposable
{
    private readonly DomContext _context;
    private readonly IScriptEngine _pageEngine;
    private readonly IScriptEngineFactory _factory;
    private readonly IResourceLoader? _loader;
    private readonly Uri? _baseUri;
    private IScriptEngine? _engine;
    private ScriptValue _onFetch;
    private ScriptValue _onMessage;
    private ScriptValue _onInstall;
    private ScriptValue _onActivate;
    private readonly List<ScriptValue> _fetchListeners = [];
    private readonly List<ScriptValue> _messageListeners = [];
    private readonly List<ScriptValue> _installListeners = [];
    private readonly List<ScriptValue> _activateListeners = [];
    private ScriptValue _lastOutgoing;
    private ScriptValue _clients;
    private ScriptValue _waitUntil;
    private ScriptValue _beginLifecycle;
    private ScriptValue _endLifecycle;
    private ScriptValue _lifecycleStatus;
    private bool _disposed;
    private CancellationTokenSource? _loads;
    private readonly CancellationToken _authorityCancellation;
    private Uri? _scriptUri;
    internal bool SkipWaitingRequested { get; private set; }
    internal bool ClaimRequested { get; private set; }
    private ScriptValue _lifecycleResolve;
    private ScriptValue _lifecycleReject;
    private ScriptValue _promiseFactory;
    private bool _lifecyclePending;
    private string _phase = "installing";
    private long _operationStarted;

    internal IsolatedServiceWorkerRuntime(IScriptEngine pageEngine, DomContext context, IScriptEngineFactory factory, IResourceLoader? loader, Uri? baseUri, CancellationToken authorityCancellation = default)
    {
        _pageEngine = pageEngine;
        _context = context;
        _factory = factory;
        _loader = loader;
        _baseUri = baseUri;
        _authorityCancellation = authorityCancellation;
        _promiseFactory = _pageEngine.EvaluateHostScript("(function(start) { return new Promise(function(resolve, reject) { start(resolve, reject); }); })", "folio:worker-lifecycle-promise");
        _onFetch = ScriptValue.Null;
        _onMessage = ScriptValue.Null;
        _onInstall = ScriptValue.Null;
        _onActivate = ScriptValue.Null;
        _lastOutgoing = ScriptValue.Undefined;
        _clients = ScriptValue.Undefined;
        _waitUntil = ScriptValue.Undefined;
    }

    internal ScriptValue DispatchValue(string url)
    {
        var result = Dispatch(url);
        if (result is null) return ScriptValue.Null;
        using var parsed = JsonDocument.Parse(result);
        var root = parsed.RootElement;
        if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            return _pageEngine.CreatePlainObject(new Dictionary<string, ScriptValue>(StringComparer.Ordinal)
            {
                ["ok"] = ScriptValue.False,
                ["error"] = root.TryGetProperty("error", out var error) ? ScriptValue.FromString(error.GetString() ?? string.Empty) : ScriptValue.FromString("service-worker-failed"),
            });
        var bytes = Convert.FromBase64String(root.GetProperty("body").GetString() ?? string.Empty);
        var contentType = root.TryGetProperty("contentType", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null;
        return _pageEngine.CreatePlainObject(new Dictionary<string, ScriptValue>(StringComparer.Ordinal)
        {
            ["ok"] = ScriptValue.True,
            ["body"] = _pageEngine.CreateTypedArray(ScriptBufferSourceKind.Uint8, bytes),
            ["bodyBase64"] = ScriptValue.FromString(Convert.ToBase64String(bytes)),
            ["status"] = root.TryGetProperty("status", out var status) && status.TryGetInt32(out var statusCode) ? ScriptValue.FromNumber(statusCode) : ScriptValue.FromNumber(200),
            ["contentType"] = contentType is null ? ScriptValue.Null : ScriptValue.FromString(contentType),
        });
    }

    internal ScriptValue DispatchMessageValue(ScriptValue data)
    {
        if (_disposed || _engine is null) return ScriptValue.Undefined;
        return DispatchMessage(data);
    }

    internal ScriptValue DispatchLifecycleValue(string type)
    {
        if (_disposed || _engine is null) throw new ObjectDisposedException(nameof(IsolatedServiceWorkerRuntime));
        _phase = type == "activate" ? "activating" : "installing";
        var start = new HostObjectTemplate("LifecycleStart").DefineMethod("start", (_, _, args) =>
        {
            _lifecycleResolve = args[0];
            _lifecycleReject = args[1];
            _lifecyclePending = true;
            try { DispatchLifecycle(type); PumpLifecycle(); }
            catch { CompleteLifecycle(false); }
            return ScriptValue.Undefined;
        });
        return _pageEngine.Call(_promiseFactory, ScriptValue.Undefined,
            [_pageEngine.GetProperty(_pageEngine.CreateHostObject(start, this), "start")]);
    }

    private void PumpLifecycle()
    {
        if (!_lifecyclePending || _engine is null) return;
        var status = _engine.Call(_lifecycleStatus, ScriptValue.Undefined, []);
        if (status.IsNumber && status.AsNumber() != 0) CompleteLifecycle(status.AsNumber() > 0);
    }

    private void CompleteLifecycle(bool success)
    {
        if (!_lifecyclePending) return;
        _lifecyclePending = false;
        if (success && _phase == "activating") _phase = "activated";
        var callback = success ? _lifecycleResolve : _lifecycleReject;
        _lifecycleResolve = _lifecycleReject = ScriptValue.Undefined;
        _pageEngine.Call(callback, ScriptValue.Undefined, success ? [] : [ScriptValue.FromString("Service worker lifecycle failed.")]);
    }

    internal string? Load(string source, string url)
    {
        if (_disposed) return "InvalidStateError";
        Retire();
        try
        {
            CreateEngine();
            _phase = "installing";
            _scriptUri = Uri.TryCreate(url, UriKind.Absolute, out var scriptUri) ? scriptUri : _baseUri;
            _operationStarted = Stopwatch.GetTimestamp();
            _engine!.Evaluate(source, url);
            var globalMessage = _engine.GetGlobalProperty("onmessage");
            if (globalMessage.IsFunction) _onMessage = globalMessage;
            var globalFetch = _engine.GetGlobalProperty("onfetch");
            if (globalFetch.IsFunction) _onFetch = globalFetch;
            var globalInstall = _engine.GetGlobalProperty("oninstall");
            if (globalInstall.IsFunction) _onInstall = globalInstall;
            var globalActivate = _engine.GetGlobalProperty("onactivate");
            if (globalActivate.IsFunction) _onActivate = globalActivate;
            return null;
        }
        catch (Exception error)
        {
            Retire();
            return error.Message;
        }
    }

    internal void Retire()
    {
        var engine = _engine;
        _engine = null;
        var loads = _loads;
        _loads = null;
        try { loads?.Cancel(); } catch (AggregateException) { }
        finally { loads?.Dispose(); }
        // Drop every engine-owned handle even when engine disposal throws.
        _fetchListeners.Clear();
        _messageListeners.Clear();
        _installListeners.Clear();
        _activateListeners.Clear();
        _onFetch = ScriptValue.Null;
        _onMessage = ScriptValue.Null;
        _onInstall = ScriptValue.Null;
        _onActivate = ScriptValue.Null;
        _lastOutgoing = ScriptValue.Undefined;
        _waitUntil = ScriptValue.Undefined;
        _clients = ScriptValue.Undefined;
        _beginLifecycle = _endLifecycle = _lifecycleStatus = ScriptValue.Undefined;
        _lifecyclePending = false;
        _lifecycleResolve = _lifecycleReject = ScriptValue.Undefined;
        _scriptUri = null;
        engine?.Dispose();
    }

    internal string? Dispatch(string url)
    {
        if (_disposed || _engine is null) return null;
        try
        {
            _operationStarted = Stopwatch.GetTimestamp();
            _engine.SetGlobalProperty("__folio_service_worker_result", ScriptValue.False);
            _engine.SetGlobalProperty("__folio_service_worker_type", ScriptValue.Null);
            _engine.SetGlobalProperty("__folio_service_worker_status", ScriptValue.FromNumber(200));
            var dispatch = _engine.GetGlobalProperty("__folio_dispatch_fetch");
            _engine.Call(dispatch, ScriptValue.Undefined, [ScriptValue.FromString(url)]);
            for (var i = 0; i < 32; i++)
            {
                _engine.PerformMicrotaskCheckpoint();
                var result = _engine.GetGlobalProperty("__folio_service_worker_result");
                if (_engine.TryGetBytes(result, out var bytes))
                {
                    var type = _engine.GetGlobalProperty("__folio_service_worker_type");
                    var status = _engine.GetGlobalProperty("__folio_service_worker_status");
                    return ScriptJson.Serialize(new Dictionary<string, object?> { ["ok"] = true, ["body"] = Convert.ToBase64String(bytes), ["status"] = status.IsNumber ? (int)status.AsNumber() : 200, ["contentType"] = type.IsString ? type.AsString() : null });
                }
            }
            return ScriptJson.Serialize(new Dictionary<string, object?> { ["ok"] = false, ["error"] = "no-response" });
        }
        catch (Exception error)
        {
            return ScriptJson.Serialize(new Dictionary<string, object?> { ["ok"] = false, ["error"] = error.Message });
        }
    }

    private void CreateEngine()
    {
        _loads = CancellationTokenSource.CreateLinkedTokenSource(_authorityCancellation);
        SkipWaitingRequested = ClaimRequested = false;
        _engine = _factory.CreateIsolated();
        DomBindings.InstallDomException(_engine);
        var isolatedFetchApi = FetchBindings.Install(_engine, _context);
        CacheBindings.Install(_engine, _context, isolatedFetchApi, registerDisposal: false);
        _engine.EvaluateHostScript("""
            if (typeof URLSearchParams === 'undefined') {
                globalThis.URLSearchParams = function(init) {
                    this._text = String(init || '').replace(/^\?/, '');
                    this.get = function(name) {
                        var parts = this._text.split('&');
                        for (var i = 0; i < parts.length; i++) {
                            var pair = parts[i].split('=');
                            if (decodeURIComponent(pair[0] || '') === String(name)) return decodeURIComponent(pair[1] || '');
                        }
                        return null;
                    };
                };
            }
            if (typeof URL === 'undefined') {
                globalThis.URL = function(value, base) {
                    var raw = String(value), resolved = raw;
                    if (base && raw.indexOf('://') < 0) {
                        var root = String(base), slash = root.lastIndexOf('/');
                        resolved = raw.charAt(0) === '/' ? root.split('://')[0] + '://' + root.split('://')[1].split('/')[0] + raw : root.slice(0, slash + 1) + raw;
                    }
                    var match = /^(?:(\w+):\/\/([^\/]+))?([^?#]*)(\?[^#]*)?(#.*)?$/.exec(resolved) || [];
                    this.protocol = match[1] ? match[1] + ':' : '';
                    this.host = match[2] || '';
                    this.origin = this.protocol && this.host ? this.protocol + '//' + this.host : '';
                    this.pathname = match[3] || '/';
                    this.search = match[4] || '';
                    this.hash = match[5] || '';
                    this.href = this.origin + this.pathname + this.search + this.hash;
                    this.searchParams = new URLSearchParams(this.search);
                };
            }
            """, "folio:service-worker-url");
        _engine.EvaluateHostScript("""
            if (typeof atob === 'undefined') {
                globalThis.atob = function(value) {
                    var alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
                    var input = String(value).replace(/=+$/, ''), output = '';
                    for (var i = 0; i < input.length; i += 4) {
                        var a = alphabet.indexOf(input[i]), b = alphabet.indexOf(input[i + 1]);
                        var c = alphabet.indexOf(input[i + 2]), d = alphabet.indexOf(input[i + 3]);
                        output += String.fromCharCode((a << 2) | (b >> 4));
                        if (c >= 0) output += String.fromCharCode(((b & 15) << 4) | (c >> 2));
                        if (d >= 0) output += String.fromCharCode(((c & 3) << 6) | d);
                    }
                    return output;
                };
            }
            if (typeof btoa === 'undefined') {
                globalThis.btoa = function(value) {
                    var alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
                    var bytes = String(value), output = '';
                    for (var i = 0; i < bytes.length; i += 3) {
                        var a = bytes.charCodeAt(i), b = i + 1 < bytes.length ? bytes.charCodeAt(i + 1) : 0;
                        var c = i + 2 < bytes.length ? bytes.charCodeAt(i + 2) : 0;
                        output += alphabet[a >> 2] + alphabet[((a & 3) << 4) | (b >> 4)];
                        output += i + 1 < bytes.length ? alphabet[((b & 15) << 2) | (c >> 6)] : '=';
                        output += i + 2 < bytes.length ? alphabet[c & 63] : '=';
                    }
                    return output;
                };
            }
            """, "folio:service-worker-base64");
        var clientsTemplate = new HostObjectTemplate("NativeServiceWorkerClients")
            .DefineMethod("claim", (_, _, _) =>
            {
                if (_phase != "activating" && _phase != "activated")
                    return _engine!.Call(_engine.GetGlobalProperty("__folio_worker_rejected"), ScriptValue.Undefined, []);
                ClaimRequested = true;
                return _engine!.Call(_engine.GetGlobalProperty("__folio_worker_resolved"), ScriptValue.Undefined, []);
            });
        _clients = _engine.CreateHostObject(clientsTemplate, this);
        var worker = new HostObjectTemplate("NativeServiceWorkerGlobal")
            .DefineProperty("clients", (_, _) => _clients)
            // The isolated worker scope is exposed through `self`.  Native
            // scripts resolve Web APIs from that scope, so mirror the APIs
            // installed on the engine global rather than leaving `caches`
            // (and the response/request constructors) invisible behind the
            // host object wrapper.
            .DefineProperty("caches", (_, _) => _engine!.GetGlobalProperty("caches"))
            .DefineProperty("CacheStorage", (_, _) => _engine!.GetGlobalProperty("CacheStorage"))
            .DefineProperty("Cache", (_, _) => _engine!.GetGlobalProperty("Cache"))
            .DefineProperty("Request", (_, _) => _engine!.GetGlobalProperty("Request"))
            .DefineProperty("Response", (_, _) => _engine!.GetGlobalProperty("Response"))
            .DefineProperty("fetch", (_, _) => _engine!.GetGlobalProperty("fetch"))
            .DefineProperty("onmessage", (_, _) => _onMessage, (_, _, value) => _onMessage = value)
            .DefineMethod("postMessage", (_, _, args) =>
            {
                _lastOutgoing = args.Length > 0 ? args[0] : ScriptValue.Undefined;
                return ScriptValue.Undefined;
            })
            .DefineMethod("dispatchMessage", (_, _, args) => DispatchMessage(args.Length > 0 ? args[0] : ScriptValue.Undefined))
            .DefineMethod("skipWaiting", (_, _, _) =>
            {
                SkipWaitingRequested = true;
                return _engine!.Call(_engine.GetGlobalProperty("__folio_worker_resolved"), ScriptValue.Undefined, []);
            })
            .DefineMethod("importScripts", (_, _, args) =>
            {
                if (_loader is null) return ScriptValue.Undefined;
                foreach (var argument in args)
                {
                    var reference = argument.AsString() ?? string.Empty;
                    var resolved = _scriptUri is not null && Uri.TryCreate(_scriptUri, reference, out var absolute) ? absolute.AbsoluteUri : reference;
                    var cancellation = _loads?.Token ?? new CancellationToken(true);
                    var remaining = _context.MaxWorkerExecutionTime - Stopwatch.GetElapsedTime(_operationStarted);
                    if (remaining <= TimeSpan.Zero) throw WebIdlException.DomException("TimeoutError", "Service worker execution exceeded the host limit.");
                    var response = ServiceWorkerBindings.LoadScript(_loader, resolved, cancellation, remaining);
                    cancellation.ThrowIfCancellationRequested();
                    if (response?.Data is null) throw new InvalidOperationException($"Service worker script could not load '{resolved}'.");
                    _engine.Evaluate(System.Text.Encoding.UTF8.GetString(response.Data), resolved);
                }
                return ScriptValue.Undefined;
            })
            .DefineProperty("onfetch", (_, _) => _onFetch, (_, _, value) => _onFetch = value)
            .DefineProperty("oninstall", (_, _) => _onInstall, (_, _, value) => _onInstall = value)
            .DefineProperty("onactivate", (_, _) => _onActivate, (_, _, value) => _onActivate = value)
            .DefineMethod("addEventListener", (_, _, args) =>
            {
                if (args.Length > 1 && string.Equals(args[0].AsString(), "fetch", StringComparison.Ordinal) && args[1].IsFunction)
                    _fetchListeners.Add(args[1]);
                else if (args.Length > 1 && string.Equals(args[0].AsString(), "message", StringComparison.Ordinal) && args[1].IsFunction)
                    _messageListeners.Add(args[1]);
                else if (args.Length > 1 && string.Equals(args[0].AsString(), "install", StringComparison.Ordinal) && args[1].IsFunction)
                    _installListeners.Add(args[1]);
                else if (args.Length > 1 && string.Equals(args[0].AsString(), "activate", StringComparison.Ordinal) && args[1].IsFunction)
                    _activateListeners.Add(args[1]);
                return ScriptValue.Undefined;
            })
            .DefineMethod("removeEventListener", (_, _, args) =>
            {
                var handler = args.Length > 1 ? args[1].Raw : null;
                if (args.Length > 1 && string.Equals(args[0].AsString(), "fetch", StringComparison.Ordinal))
                    _fetchListeners.RemoveAll(item => item.Raw is not null && item.Raw.Equals(handler));
                else if (args.Length > 1 && string.Equals(args[0].AsString(), "message", StringComparison.Ordinal))
                    _messageListeners.RemoveAll(item => item.Raw is not null && item.Raw.Equals(handler));
                else if (args.Length > 1 && string.Equals(args[0].AsString(), "install", StringComparison.Ordinal))
                    _installListeners.RemoveAll(item => item.Raw is not null && item.Raw.Equals(handler));
                else if (args.Length > 1 && string.Equals(args[0].AsString(), "activate", StringComparison.Ordinal))
                    _activateListeners.RemoveAll(item => item.Raw is not null && item.Raw.Equals(handler));
                return ScriptValue.Undefined;
            })
            .DefineMethod("dispatchFetch", (_, _, args) =>
            {
                if (args.Length == 0) return ScriptValue.Undefined;
                var eventValue = args[0];
                if (_onFetch.IsFunction) _engine.Call(_onFetch, ScriptValue.Undefined, [eventValue]);
                foreach (var listener in _fetchListeners.ToArray()) _engine.Call(listener, ScriptValue.Undefined, [eventValue]);
                return ScriptValue.Undefined;
            });
        var self = _engine.CreateHostObject(worker, this);
        _engine.SetGlobalProperty("self", self);
        _engine.EvaluateHostScript("globalThis.postMessage = self.postMessage.bind(self);", "folio:service-worker-message");
        _engine.EvaluateHostScript("globalThis.importScripts = self.importScripts.bind(self); globalThis.skipWaiting = self.skipWaiting.bind(self); globalThis.clients = self.clients;", "folio:service-worker-globals");
        _engine.SetGlobalProperty("document", ScriptValue.Undefined);
        _engine.SetGlobalProperty("window", ScriptValue.Undefined);
        _engine.SetGlobalProperty("navigator", ScriptValue.Undefined);
        _engine.EvaluateHostScript("""
            (function(self) {
                globalThis.__folio_dispatch_fetch = function(url) {
                    globalThis.__folio_service_worker_result = false;
                    var event = { type: 'fetch', request: new Request(url), target: self,
                        respondWith: function(value) {
                            var resolveResponse = function(response) {
                                if (!(response instanceof Response)) { globalThis.__folio_service_worker_result = false; return; }
                                response.bytes().then(function(data) {
                                    globalThis.__folio_service_worker_type = response.headers && response.headers.get('content-type');
                                    globalThis.__folio_service_worker_status = response.status;
                                    globalThis.__folio_service_worker_result = data;
                                }, function() { globalThis.__folio_service_worker_result = false; });
                            };
                            if (value && typeof value.then === 'function') value.then(resolveResponse, function() { globalThis.__folio_service_worker_result = false; });
                            else resolveResponse(value);
                        }
                    };
                    self.dispatchFetch(event);
                };
            })(self);
            """, "folio:native-service-worker-dispatch");
        // Keep lifetime state in trusted closures, rather than worker-writable globals.
        var lifecycle = _engine.EvaluateHostScript("""
            (function() {
                var current;
                var resolvePromise = Promise.resolve.bind(Promise);
                return {
                    begin: function() {
                        var event = { pending: 0, status: 0, open: true };
                        current = event;
                        return function(value) {
                            if (!event.open && event.pending === 0) throw new DOMException('The event lifetime has ended.', 'InvalidStateError');
                            event.pending++;
                            resolvePromise(value).then(function() {
                                event.pending--;
                                if (!event.open && event.pending === 0 && event.status === 0) event.status = 1;
                            }, function() { event.pending--; event.status = -1; });
                        };
                    },
                    end: function() {
                        current.open = false;
                        if (current.pending === 0 && current.status === 0) current.status = 1;
                    },
                    status: function() { return current.status; }
                };
            })();
            """, "folio:native-service-worker-wait-until");
        _beginLifecycle = _engine.GetProperty(lifecycle, "begin");
        _endLifecycle = _engine.GetProperty(lifecycle, "end");
        _lifecycleStatus = _engine.GetProperty(lifecycle, "status");
        _engine.EvaluateHostScript("""
            globalThis.__folio_worker_rejected = function() { return Promise.reject(new DOMException('The worker is not active.', 'InvalidStateError')); };
            globalThis.__folio_worker_resolved = function() { return Promise.resolve(); };
            """, "folio:native-service-worker-promises");

        // Trusted runtime/bootstrap code is installed before the worker's execution
        // budget is armed. The limit must bound artifact handlers and dispatch, not
        // Folio's own cache/fetch bindings and bootstrap scripts; a short configured
        // worker deadline would otherwise make engine creation nondeterministically
        // fail under load.
        _engine.Configure(new ScriptLimits
        {
            MaxStatements = 100_000,
            MemoryLimitBytes = 64 * 1024 * 1024,
            Timeout = _context.MaxWorkerExecutionTime
        });
    }

    private ScriptValue DispatchMessage(ScriptValue data)
    {
        _operationStarted = Stopwatch.GetTimestamp();
        _lastOutgoing = ScriptValue.Undefined;
        var eventValue = _engine!.CreatePlainObject(new Dictionary<string, ScriptValue>(StringComparer.Ordinal)
        {
            ["type"] = ScriptValue.FromString("message"),
            ["data"] = data,
            ["target"] = _engine.GetGlobalProperty("self"),
        });
        if (_onMessage.IsFunction) _engine.Call(_onMessage, ScriptValue.Undefined, [eventValue]);
        foreach (var listener in _messageListeners.ToArray()) _engine.Call(listener, ScriptValue.Undefined, [eventValue]);
        for (var i = 0; i < 32; i++) _engine.PerformMicrotaskCheckpoint();
        PumpLifecycle();
        return CopyToPage(_lastOutgoing);
    }

    private void DispatchLifecycle(string type)
    {
        if (_engine is null) return;
        _operationStarted = Stopwatch.GetTimestamp();
        _waitUntil = _engine.Call(_beginLifecycle, ScriptValue.Undefined, []);
        var listeners = string.Equals(type, "install", StringComparison.Ordinal) ? _installListeners : _activateListeners;
        var handler = string.Equals(type, "install", StringComparison.Ordinal) ? _onInstall : _onActivate;
        var eventValue = _engine.CreatePlainObject(new Dictionary<string, ScriptValue>(StringComparer.Ordinal)
        {
            ["type"] = ScriptValue.FromString(type),
            ["target"] = _engine.GetGlobalProperty("self"),
            ["waitUntil"] = _waitUntil,
        });
        if (handler.IsFunction) _engine.Call(handler, ScriptValue.Undefined, [eventValue]);
        foreach (var listener in listeners.ToArray()) _engine.Call(listener, ScriptValue.Undefined, [eventValue]);
        _engine.Call(_endLifecycle, ScriptValue.Undefined, []);
        for (var i = 0; i < 32; i++) _engine.PerformMicrotaskCheckpoint();
    }

    private ScriptValue CopyToPage(ScriptValue value) => value.Kind switch
    {
        ScriptValueKind.Undefined => ScriptValue.Undefined,
        ScriptValueKind.Null => ScriptValue.Null,
        ScriptValueKind.Boolean => ScriptValue.FromBoolean(value.AsBoolean()),
        ScriptValueKind.Number => ScriptValue.FromNumber(value.AsNumber()),
        ScriptValueKind.String => ScriptValue.FromString(value.AsString()),
        _ => ScriptValue.Undefined,
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Retire(); }
        finally { _promiseFactory = ScriptValue.Undefined; }
    }
}
