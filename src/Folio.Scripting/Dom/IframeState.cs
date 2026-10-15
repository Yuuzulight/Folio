using Folio.Dom;
using Folio.Html;
using Folio.Resources;

namespace Folio.Scripting.Dom;

internal sealed class IframeState
{
    public IframeState(IScriptEngine engine, IHostObjectTemplate messageEventTemplate, ElementNode element, bool enabled, IResourceLoader? resourceLoader = null, Uri? baseUri = null)
    {
        Engine = engine;
        MessageEventTemplate = messageEventTemplate;
        Element = element;
        Enabled = enabled;
        Origin = Uri.TryCreate(element.GetAttribute("src"), UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : "null";
        ResourceLoader = resourceLoader;
        BaseUri = baseUri;
    }

    public IScriptEngine Engine { get; }
    public IHostObjectTemplate MessageEventTemplate { get; }
    public ElementNode Element { get; }
    public bool Enabled { get; }
    public string Origin { get; }
    public IResourceLoader? ResourceLoader { get; }
    public Uri? BaseUri { get; }
    public bool Closed { get; private set; }
    public ScriptValue OnMessage { get; set; } = ScriptValue.Null;
    public ScriptValue Window { get; set; } = ScriptValue.Null;
    public DocumentNode? ChildDocument { get; private set; }
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

    public void LoadInlineDocument()
    {
        if (ChildDocument is not null || !Enabled) return;
        var source = Element.GetAttribute("src") ?? string.Empty;
        if (BaseUri is not null && Uri.TryCreate(BaseUri, source, out var resolved)) source = resolved.AbsoluteUri;
        string html;
        if (source.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase))
        {
            var comma = source.IndexOf(',');
            if (comma < 0) return;
            var payload = source[(comma + 1)..];
            html = source[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase)
                ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload))
                : Uri.UnescapeDataString(payload);
        }
        else
        {
            if (ResourceLoader is null || !Uri.TryCreate(source, UriKind.Absolute, out var uri)) return;
            var response = ResourceLoader.LoadAsync(new ResourceRequest(uri.AbsoluteUri, ResourceKind.Script), CancellationToken.None).GetAwaiter().GetResult();
            if (response?.Data is not { } data) return;
            html = System.Text.Encoding.UTF8.GetString(data);
        }
        ChildDocument = TreeBuilder.Parse(html);
    }
}
