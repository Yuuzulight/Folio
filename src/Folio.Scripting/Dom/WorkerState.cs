namespace Folio.Scripting.Dom;

internal sealed class WorkerState
{
    public WorkerState(IScriptEngine engine, IHostObjectTemplate messageEventTemplate, string scriptUrl, bool enabled, int maxMessageBytes)
    {
        Engine = engine;
        MessageEventTemplate = messageEventTemplate;
        ScriptUrl = scriptUrl;
        Enabled = enabled;
        MaxMessageBytes = Math.Max(0, maxMessageBytes);
    }

    public IScriptEngine Engine { get; }
    public IHostObjectTemplate MessageEventTemplate { get; }
    public string ScriptUrl { get; }
    public bool Enabled { get; }
    public int MaxMessageBytes { get; }
    public bool Terminated { get; private set; }
    public ScriptValue OnMessage { get; set; } = ScriptValue.Null;
    public ScriptValue OnError { get; set; } = ScriptValue.Null;
    public void Terminate() => Terminated = true;
    public bool PostMessage(ScriptValue value)
    {
        if (value.IsString && (value.AsString()?.Length ?? 0) > MaxMessageBytes) return false;
        if (Enabled && !Terminated && OnMessage.IsFunction)
        {
            var message = Engine.CreateHostObject(MessageEventTemplate, new MessageEventState(value, "", ScriptValue.Null));
            Engine.Call(OnMessage, ScriptValue.Undefined, [message]);
        }
        return true;
    }
}
