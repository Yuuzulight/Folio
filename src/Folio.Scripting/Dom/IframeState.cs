using Folio.Dom;

namespace Folio.Scripting.Dom;

internal sealed class IframeState
{
    public IframeState(IScriptEngine engine, IHostObjectTemplate messageEventTemplate, ElementNode element, bool enabled)
    {
        Engine = engine;
        MessageEventTemplate = messageEventTemplate;
        Element = element;
        Enabled = enabled;
        Origin = Uri.TryCreate(element.GetAttribute("src"), UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : "null";
    }

    public IScriptEngine Engine { get; }
    public IHostObjectTemplate MessageEventTemplate { get; }
    public ElementNode Element { get; }
    public bool Enabled { get; }
    public string Origin { get; }
    public bool Closed { get; private set; }
    public ScriptValue OnMessage { get; set; } = ScriptValue.Null;
    public ScriptValue Window { get; set; } = ScriptValue.Null;

    public void PostMessage(ScriptValue data, string targetOrigin)
    {
        if (!Enabled || Closed || !OnMessage.IsFunction || (targetOrigin != "*" && targetOrigin != Origin)) return;
        var message = Engine.CreateHostObject(MessageEventTemplate, new MessageEventState(data, Origin, Window, target: Window));
        Engine.Call(OnMessage, ScriptValue.Undefined, [message]);
    }

    public void Close() => Closed = true;
}
