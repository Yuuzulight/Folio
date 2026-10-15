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
    private readonly List<ScriptValue> _messageListeners = [];

    public void PostMessage(ScriptValue data, string targetOrigin)
    {
        if (!Enabled || Closed || (!OnMessage.IsFunction && _messageListeners.Count == 0) || (targetOrigin != "*" && targetOrigin != Origin)) return;
        var message = Engine.CreateHostObject(MessageEventTemplate, new MessageEventState(data, Origin, Window, target: Window));
        if (OnMessage.IsFunction) Engine.Call(OnMessage, ScriptValue.Undefined, [message]);
        foreach (var listener in _messageListeners.ToArray())
            if (listener.IsFunction) Engine.Call(listener, ScriptValue.Undefined, [message]);
    }

    public void AddMessageListener(ScriptValue listener)
    {
        if (listener.IsFunction && !_messageListeners.Contains(listener)) _messageListeners.Add(listener);
    }

    public void RemoveMessageListener(ScriptValue listener) => _messageListeners.Remove(listener);

    public void Close() => Closed = true;
}
