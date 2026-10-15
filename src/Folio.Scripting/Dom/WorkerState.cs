namespace Folio.Scripting.Dom;

internal sealed class WorkerState
{
    public WorkerState(IScriptEngine engine, string scriptUrl, bool enabled)
    {
        Engine = engine;
        ScriptUrl = scriptUrl;
        Enabled = enabled;
    }

    public IScriptEngine Engine { get; }
    public string ScriptUrl { get; }
    public bool Enabled { get; }
    public bool Terminated { get; private set; }
    public ScriptValue OnMessage { get; set; } = ScriptValue.Null;
    public ScriptValue OnError { get; set; } = ScriptValue.Null;
    public void Terminate() => Terminated = true;
    public void PostMessage(ScriptValue value)
    {
        if (Enabled && !Terminated && OnMessage.IsFunction)
            Engine.Call(OnMessage, ScriptValue.Undefined, [value]);
    }
}
