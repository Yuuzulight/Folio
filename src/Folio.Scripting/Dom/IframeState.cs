using Folio.Dom;

namespace Folio.Scripting.Dom;

internal sealed class IframeState
{
    public IframeState(IScriptEngine engine, ElementNode element, bool enabled)
    {
        Engine = engine;
        Element = element;
        Enabled = enabled;
    }

    public IScriptEngine Engine { get; }
    public ElementNode Element { get; }
    public bool Enabled { get; }
    public bool Closed { get; private set; }
    public ScriptValue OnMessage { get; set; } = ScriptValue.Null;

    public void PostMessage(ScriptValue data)
    {
        if (!Enabled || Closed || !OnMessage.IsFunction) return;
        Engine.Call(OnMessage, ScriptValue.Undefined, [data]);
    }

    public void Close() => Closed = true;
}
