using Folio.Resources;

namespace Folio.Scripting.Dom;

internal sealed class WorkerState
{
    public WorkerState(IScriptEngine engine, IHostObjectTemplate messageEventTemplate, string scriptUrl, bool enabled, int maxMessageBytes, IScriptEngine? workerEngine = null, IResourceLoader? resourceLoader = null)
    {
        Engine = engine;
        MessageEventTemplate = messageEventTemplate;
        ScriptUrl = scriptUrl;
        Origin = Uri.TryCreate(scriptUrl, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : "null";
        Enabled = enabled;
        MaxMessageBytes = Math.Max(0, maxMessageBytes);
        WorkerEngine = workerEngine;
        if (WorkerEngine is not null)
        {
            var bridge = new HostObjectTemplate("WorkerBridge")
                .DefineMethod("postMessage", (_, _, args) => { if (args.Length > 0) { if (args[0].IsFunction) throw WebIdlException.DomException("DataCloneError", "Functions cannot be cloned into a document."); DispatchToParent(args[0]); } return ScriptValue.Undefined; });
            WorkerEngine.SetGlobalProperty("__folio_worker_bridge", WorkerEngine.CreateHostObject(bridge, this));
            WorkerEngine.Evaluate("globalThis.postMessage = function(value) { return __folio_worker_bridge.postMessage(value); };", ScriptUrl);
            var imports = new HostObjectTemplate("WorkerImports")
                .DefineMethod("load", (_, _, args) =>
                {
                    foreach (var argument in args)
                    {
                        var reference = argument.AsString() ?? string.Empty;
                        var resolved = ResolveScriptUrl(ScriptUrl, reference);
                        if (!TryReadScript(resolved, resourceLoader, out var imported))
                            throw new InvalidOperationException($"Worker script could not load '{reference}'.");
                        WorkerEngine.Evaluate(imported, resolved);
                    }
                    return ScriptValue.Undefined;
                });
            WorkerEngine.SetGlobalProperty("__folio_worker_imports", WorkerEngine.CreateHostObject(imports, this));
            WorkerEngine.Evaluate("globalThis.importScripts = function() { return __folio_worker_imports.load.apply(null, arguments); };", ScriptUrl);
            WorkerEngine.Evaluate("globalThis.__folio_worker_listeners = {}; globalThis.addEventListener = function(type, handler) { if (typeof handler !== 'function') return; (globalThis.__folio_worker_listeners[type] || (globalThis.__folio_worker_listeners[type] = [])).push(handler); }; globalThis.removeEventListener = function(type, handler) { var list = globalThis.__folio_worker_listeners[type] || []; globalThis.__folio_worker_listeners[type] = list.filter(function(item) { return item !== handler; }); };", ScriptUrl);
            WorkerEngine.SetGlobalProperty("self", WorkerEngine.Evaluate("globalThis"));
            if (TryReadScriptWithImports(scriptUrl, resourceLoader, out var source))
            {
                try { WorkerEngine.Evaluate(source, scriptUrl); }
                catch (Exception error) { _scriptError = error; }
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
    private Exception? _scriptError;
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
        WorkerEngine?.Dispose();
    }
    public bool PostMessage(ScriptValue value)
    {
        if (value.IsString && (value.AsString()?.Length ?? 0) > MaxMessageBytes) return false;
        if (_scriptError is not null && !Terminated) { DispatchError(_scriptError); return true; }
        if (WorkerEngine is not null && !Terminated)
        {
            WorkerEngine.SetGlobalProperty("__folio_worker_message_data", value);
            WorkerEngine.SetGlobalProperty("__folio_worker_message_event", WorkerEngine.Evaluate("({ type: 'message', data: __folio_worker_message_data, target: self })", ScriptUrl));
            WorkerEngine.Evaluate("(function() { var event = __folio_worker_message_event; if (typeof onmessage === 'function') onmessage(event); var listeners = (__folio_worker_listeners.message || []).slice(); listeners.forEach(function(listener) { listener(event); }); })()", ScriptUrl);
            WorkerEngine.SetGlobalProperty("__folio_worker_message_data", ScriptValue.Undefined);
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

    private void DispatchToParent(ScriptValue value)
    {
        if (value.IsString && (value.AsString()?.Length ?? 0) > MaxMessageBytes) return;
        var message = Engine.CreateHostObject(MessageEventTemplate, new MessageEventState(value, Origin, Source));
        if (OnMessage.IsFunction) Engine.Call(OnMessage, ScriptValue.Undefined, [message]);
        if (_listeners.TryGetValue("message", out var listeners)) foreach (var listener in listeners.ToArray()) if (listener.IsFunction) Engine.Call(listener, ScriptValue.Undefined, [message]);
    }

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

    private static bool TryReadScript(string url, IResourceLoader? resourceLoader, out string source)
    {
        source = string.Empty;
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

    private static bool TryReadScriptWithImports(string url, IResourceLoader? resourceLoader, out string source, HashSet<string>? seen = null)
    {
        source = string.Empty;
        seen ??= new HashSet<string>(StringComparer.Ordinal);
        if (!seen.Add(url) || !TryReadScript(url, resourceLoader, out source)) return false;
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
            if (!TryReadScriptWithImports(resolved, resourceLoader, out var imported, seen)) return false;
            source = source.Remove(start, close - start + 1).Insert(start, imported);
            search = start + imported.Length;
        }
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
