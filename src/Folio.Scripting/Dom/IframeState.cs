using Folio.Dom;

namespace Folio.Scripting.Dom;

internal sealed class IframeState
{
    public IframeState(IScriptEngine engine, ElementNode element, bool enabled)
    {
        Engine = engine;
        Element = element;
        Enabled = enabled;
        Origin = Uri.TryCreate(element.GetAttribute("src"), UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : "null";
    }

    public IScriptEngine Engine { get; }
    public ElementNode Element { get; }
    public bool Enabled { get; }
    public string Origin { get; }
    public bool Closed { get; private set; }
    public ScriptValue OnMessage { get; set; } = ScriptValue.Null;

    public void PostMessage(ScriptValue data, string targetOrigin)
    {
        if (!Enabled || Closed || !OnMessage.IsFunction || (targetOrigin != "*" && targetOrigin != Origin)) return;
        Engine.Call(OnMessage, ScriptValue.Undefined, [data]);
    }

    public void Close() => Closed = true;
}
