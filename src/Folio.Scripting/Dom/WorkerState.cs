namespace Folio.Scripting.Dom;

internal sealed class WorkerState
{
    public WorkerState(IScriptEngine engine, IHostObjectTemplate messageEventTemplate, string scriptUrl, bool enabled, int maxMessageBytes, IScriptEngine? workerEngine = null)
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
            WorkerEngine.SetGlobalProperty("self", WorkerEngine.Evaluate("globalThis"));
            if (TryReadDataScript(scriptUrl, out var source)) WorkerEngine.Evaluate(source, scriptUrl);
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
        if (WorkerEngine is not null && !Terminated)
        {
            WorkerEngine.SetGlobalProperty("__folio_worker_message_data", value);
            var handler = WorkerEngine.GetGlobalProperty("onmessage");
            if (handler.IsFunction)
            {
                var message = WorkerEngine.Evaluate("({ type: 'message', data: __folio_worker_message_data, target: self })", ScriptUrl);
                WorkerEngine.Call(handler, ScriptValue.Undefined, [message]);
            }
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

    private static bool TryReadDataScript(string url, out string source)
    {
        source = string.Empty;
        if (!url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;
        var comma = url.IndexOf(',');
        if (comma < 0) return false;
        var payload = url[(comma + 1)..];
        source = url[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase)
            ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload))
            : Uri.UnescapeDataString(payload);
        return true;
    }
}
