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
                .DefineMethod("postMessage", (_, _, args) => { if (args.Length > 0) DispatchToParent(args[0]); return ScriptValue.Undefined; });
            WorkerEngine.SetGlobalProperty("__folio_worker_bridge", WorkerEngine.CreateHostObject(bridge, this));
            WorkerEngine.Evaluate("globalThis.postMessage = function(value) { return __folio_worker_bridge.postMessage(value); };", ScriptUrl);
            WorkerEngine.Evaluate("globalThis.__folio_worker_listeners = {}; globalThis.addEventListener = function(type, handler) { if (typeof handler !== 'function') return; (globalThis.__folio_worker_listeners[type] || (globalThis.__folio_worker_listeners[type] = [])).push(handler); }; globalThis.removeEventListener = function(type, handler) { var list = globalThis.__folio_worker_listeners[type] || []; globalThis.__folio_worker_listeners[type] = list.filter(function(item) { return item !== handler; }); };", ScriptUrl);
            WorkerEngine.SetGlobalProperty("self", WorkerEngine.Evaluate("globalThis"));
            if (TryReadScript(scriptUrl, resourceLoader, out var source))
            {
                try { WorkerEngine.Evaluate(source, scriptUrl); }
                catch (Exception error) { _scriptError = error; }
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
    public void Terminate() { Terminated = true; WorkerEngine?.Dispose(); }
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
}
