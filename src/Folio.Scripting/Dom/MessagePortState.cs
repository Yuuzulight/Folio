namespace Folio.Scripting.Dom;

internal sealed class MessagePortEndpoint(IHostObjectTemplate messageEventTemplate, IHostObjectTemplate messagePortTemplate)
{
    private MessagePortEndpoint? _peer;
    private IScriptEngine? _engine;
    private ScriptValue _onMessage = ScriptValue.Null;
    private bool _closed;
    private bool _detached;

    public IHostObjectTemplate MessageEventTemplate { get; } = messageEventTemplate;
    public IHostObjectTemplate MessagePortTemplate { get; } = messagePortTemplate;
    public ScriptValue OnMessage => _onMessage;
    public bool Closed => _closed || _detached;

    public void Attach(IScriptEngine engine) => _engine = engine;

    public void SetPeer(MessagePortEndpoint peer) => _peer = peer;

    public void SetOnMessage(ScriptValue handler)
    {
        _onMessage = handler;
    }

    public void Start()
    {
    }

    public void Close()
    {
        _closed = true;
        _onMessage = ScriptValue.Null;
    }

    public void PostMessage(ScriptValue data)
    {
        if (Closed || _peer is null || _peer.Closed) return;
        _peer.Deliver(data);
    }

    public MessagePortEndpoint TransferTo(IScriptEngine engine)
    {
        if (Closed) throw WebIdlException.DomException(DomExceptionNames.InvalidStateError, "The message port is not available.");
        var replacement = new MessagePortEndpoint(MessageEventTemplate, MessagePortTemplate);
        replacement.Attach(engine);
        replacement.SetPeer(_peer!);
        if (_peer is not null) _peer.SetPeer(replacement);
        _detached = true;
        _peer = null;
        _onMessage = ScriptValue.Null;
        return replacement;
    }

    private void Deliver(ScriptValue data)
    {
        if (Closed || _engine is null || !_onMessage.IsFunction) return;
        var target = _engine.CreateHostObject(MessagePortTemplate, this);
        var message = _engine.CreateHostObject(MessageEventTemplate, new MessageEventState(data, string.Empty, target, target: target));
        _engine.Call(_onMessage, ScriptValue.Undefined, [message]);
    }
}

internal static class MessagePortFactory
{
    public static (MessagePortEndpoint First, MessagePortEndpoint Second) Create(IHostObjectTemplate messageEventTemplate, IHostObjectTemplate messagePortTemplate, IScriptEngine engine)
    {
        var first = new MessagePortEndpoint(messageEventTemplate, messagePortTemplate);
        var second = new MessagePortEndpoint(messageEventTemplate, messagePortTemplate);
        first.SetPeer(second);
        second.SetPeer(first);
        first.Attach(engine);
        second.Attach(engine);
        return (first, second);
    }
}
