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
    private bool _disposed;

    internal IsolatedServiceWorkerRuntime(IScriptEngine pageEngine, DomContext context, IScriptEngineFactory factory, IResourceLoader? loader, Uri? baseUri)
    {
        _pageEngine = pageEngine;
        _context = context;
        _factory = factory;
        _loader = loader;
        _baseUri = baseUri;
        _onFetch = ScriptValue.Null;
        _onMessage = ScriptValue.Null;
        _onInstall = ScriptValue.Null;
        _onActivate = ScriptValue.Null;
        _lastOutgoing = ScriptValue.Undefined;
        _clients = ScriptValue.Undefined;
        CreateEngine();
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
            ["status"] = root.TryGetProperty("status", out var status) && status.TryGetInt32(out var statusCode) ? ScriptValue.FromNumber(statusCode) : ScriptValue.FromNumber(200),
            ["contentType"] = contentType is null ? ScriptValue.Null : ScriptValue.FromString(contentType),
        });
    }

    internal ScriptValue DispatchMessageValue(ScriptValue data)
    {
        if (_disposed || _engine is null) return ScriptValue.Undefined;
        return DispatchMessage(data);
    }

    internal void DispatchLifecycleValue(string type)
    {
        if (_disposed || _engine is null) return;
        DispatchLifecycle(type);
    }

    internal string? Load(string source, string url)
    {
        if (_disposed) return "InvalidStateError";
        Retire();
        CreateEngine();
        try
        {
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
            return error.Message;
        }
    }

    internal void Retire()
    {
        if (_disposed) return;
        _engine?.Dispose();
        _engine = null;
        _fetchListeners.Clear();
        _messageListeners.Clear();
        _installListeners.Clear();
        _activateListeners.Clear();
        _onFetch = ScriptValue.Null;
        _onMessage = ScriptValue.Null;
        _onInstall = ScriptValue.Null;
        _onActivate = ScriptValue.Null;
        _lastOutgoing = ScriptValue.Undefined;
    }

    internal string? Dispatch(string url)
    {
        if (_disposed || _engine is null) return null;
        try
        {
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
                    return JsonSerializer.Serialize(new { ok = true, body = Convert.ToBase64String(bytes), status = status.IsNumber ? (int)status.AsNumber() : 200, contentType = type.IsString ? type.AsString() : null });
                }
            }
            return JsonSerializer.Serialize(new { ok = false, error = "no-response" });
        }
        catch (Exception error)
        {
            return JsonSerializer.Serialize(new { ok = false, error = error.Message });
        }
    }

    private void CreateEngine()
    {
        _engine = _factory.CreateIsolated();
        _engine.Configure(new ScriptLimits { MaxStatements = 100_000, MemoryLimitBytes = 64 * 1024 * 1024 });
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
            .DefineMethod("claim", (_, _, _) => ScriptValue.Undefined);
        _clients = _engine.CreateHostObject(clientsTemplate, this);
        var worker = new HostObjectTemplate("NativeServiceWorkerGlobal")
            .DefineProperty("clients", (_, _) => _clients)
            .DefineProperty("onmessage", (_, _) => _onMessage, (_, _, value) => _onMessage = value)
            .DefineMethod("postMessage", (_, _, args) =>
            {
                _lastOutgoing = args.Length > 0 ? args[0] : ScriptValue.Undefined;
                return ScriptValue.Undefined;
            })
            .DefineMethod("dispatchMessage", (_, _, args) => DispatchMessage(args.Length > 0 ? args[0] : ScriptValue.Undefined))
            .DefineMethod("skipWaiting", (_, _, _) => ScriptValue.Undefined)
            .DefineMethod("importScripts", (_, _, args) =>
            {
                if (_loader is null) return ScriptValue.Undefined;
                foreach (var argument in args)
                {
                    var reference = argument.AsString() ?? string.Empty;
                    var resolved = _baseUri is not null && Uri.TryCreate(_baseUri, reference, out var absolute) ? absolute.AbsoluteUri : reference;
                    var response = _loader.LoadAsync(new ResourceRequest(resolved, ResourceKind.Script), CancellationToken.None).GetAwaiter().GetResult();
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
            })
            .DefineMethod("dispatchLifecycle", (_, _, args) =>
            {
                DispatchLifecycle(args.Length > 0 ? args[0].AsString() ?? string.Empty : string.Empty);
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
    }

    private ScriptValue DispatchMessage(ScriptValue data)
    {
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
        return CopyToPage(_lastOutgoing);
    }

    private void DispatchLifecycle(string type)
    {
        if (_engine is null) return;
        var listeners = string.Equals(type, "install", StringComparison.Ordinal) ? _installListeners : _activateListeners;
        var handler = string.Equals(type, "install", StringComparison.Ordinal) ? _onInstall : _onActivate;
        var eventValue = _engine.CreatePlainObject(new Dictionary<string, ScriptValue>(StringComparer.Ordinal)
        {
            ["type"] = ScriptValue.FromString(type),
            ["target"] = _engine.GetGlobalProperty("self"),
            ["waitUntil"] = _engine.Evaluate("(function(value) { return value; })", "folio:service-worker-wait-until"),
        });
        if (handler.IsFunction) _engine.Call(handler, ScriptValue.Undefined, [eventValue]);
        foreach (var listener in listeners.ToArray()) _engine.Call(listener, ScriptValue.Undefined, [eventValue]);
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
        Retire();
    }
}
