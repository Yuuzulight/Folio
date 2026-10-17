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
        ResourceLoader = resourceLoader;
        BaseUri = baseUri;
        var sandbox = SandboxTokens;
        var sameOrigin = sandbox.Length == 0 || sandbox.Contains("allow-same-origin", StringComparer.OrdinalIgnoreCase);
        Origin = sameOrigin && Uri.TryCreate(ResolveSource(element.GetAttribute("src") ?? string.Empty), UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : "null";
    }

    public IScriptEngine Engine { get; }
    public IHostObjectTemplate MessageEventTemplate { get; }
    public ElementNode Element { get; }
    public bool Enabled { get; }
    public string Origin { get; private set; }
    public string ParentOrigin => BaseUri?.GetLeftPart(UriPartial.Authority) ?? string.Empty;
    public bool ScriptsAllowed => !HasSandbox || SandboxTokens.Contains("allow-scripts", StringComparer.OrdinalIgnoreCase);
    public bool FormsAllowed => !HasSandbox || SandboxTokens.Contains("allow-forms", StringComparer.OrdinalIgnoreCase);
    public bool HasSandbox => Element.GetAttribute("sandbox") is not null;
    private string[] SandboxTokens => (Element.GetAttribute("sandbox") ?? string.Empty).Split([' ', '\t', '\n', '\f', '\r'], StringSplitOptions.RemoveEmptyEntries);
    public IResourceLoader? ResourceLoader { get; }
    public Uri? BaseUri { get; }
    public bool Closed { get; private set; }
    public ScriptValue OnMessage { get; set; } = ScriptValue.Null;
    public ScriptValue OnLoad { get; set; } = ScriptValue.Null;
    public ScriptValue OnError { get; set; } = ScriptValue.Null;
    public ScriptValue ElementObject { get; set; } = ScriptValue.Null;
    public ScriptValue Window { get; set; } = ScriptValue.Null;
    public DocumentNode? ChildDocument { get; private set; }
    private readonly List<ScriptValue> _messageListeners = [];
    private string? _loadedSource;

    private string ResolveSource(string source) => BaseUri is not null && Uri.TryCreate(BaseUri, source, out var resolved) ? resolved.AbsoluteUri : source;

    public void PostMessage(ScriptValue data, string targetOrigin, ScriptValue transfer = default)
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

    public void Invalidate()
    {
        ChildDocument = null;
        _loadedSource = null;
        var sandbox = SandboxTokens;
        var sameOrigin = sandbox.Length == 0 || sandbox.Contains("allow-same-origin", StringComparer.OrdinalIgnoreCase);
        Origin = sameOrigin && Uri.TryCreate(ResolveSource(Element.GetAttribute("src") ?? string.Empty), UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority)
            : "null";
    }

    public void LoadInlineDocument()
    {
        if (!Enabled) return;
        var inline = Element.GetAttribute("srcdoc");
        var source = ResolveSource(Element.GetAttribute("src") ?? string.Empty);
        var current = inline is not null ? "srcdoc:" + inline : "src:" + source;
        if (ChildDocument is not null && _loadedSource == current) return;
        ChildDocument = null;
        _loadedSource = current;
        var sandbox = (Element.GetAttribute("sandbox") ?? string.Empty).Split([' ', '\t', '\n', '\f', '\r'], StringSplitOptions.RemoveEmptyEntries);
        var sameOrigin = sandbox.Length == 0 || sandbox.Contains("allow-same-origin", StringComparer.OrdinalIgnoreCase);
        var originSource = inline is not null && BaseUri is not null ? BaseUri.AbsoluteUri : source;
        Origin = sameOrigin && Uri.TryCreate(originSource, UriKind.Absolute, out var childUri) ? childUri.GetLeftPart(UriPartial.Authority) : "null";
        string html;
        if (inline is not null)
        {
            html = inline;
        }
        else if (source.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase))
        {
            var comma = source.IndexOf(',');
            if (comma < 0) { DispatchError(); return; }
            var payload = source[(comma + 1)..];
            try
            {
                html = source[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase)
                    ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload))
                    : Uri.UnescapeDataString(payload);
            }
            catch (FormatException) { DispatchError(); return; }
        }
        else
        {
            if (ResourceLoader is null || !Uri.TryCreate(source, UriKind.Absolute, out var uri)) { DispatchError(); return; }
            var response = ResourceLoader.LoadAsync(new ResourceRequest(uri.AbsoluteUri, ResourceKind.Document), CancellationToken.None).GetAwaiter().GetResult();
            if (response?.Data is not { } data) { DispatchError(); return; }
            html = System.Text.Encoding.UTF8.GetString(data);
        }
        ChildDocument = TreeBuilder.Parse(html);
        if (ElementObject.IsObject)
        {
            Engine.SetGlobalProperty("__folio_iframe_load_target", ElementObject);
            try { Engine.Evaluate("__folio_iframe_load_target.dispatchEvent(new Event('load'))"); }
            finally { Engine.SetGlobalProperty("__folio_iframe_load_target", ScriptValue.Undefined); }
        }
        if (OnLoad.IsFunction)
        {
            var target = ElementObject.IsObject ? ElementObject : Window.IsObject ? Window : Engine.CreateHostObject(MessageEventTemplate, this);
            Engine.SetGlobalProperty("__folio_iframe_load_target", target);
            var eventValue = Engine.Evaluate("({ type: 'load', target: __folio_iframe_load_target })");
            Engine.SetGlobalProperty("__folio_iframe_load_target", ScriptValue.Undefined);
            Engine.Call(OnLoad, ScriptValue.Undefined, [eventValue]);
        }
    }

    private void DispatchError()
    {
        if (ElementObject.IsObject)
        {
            Engine.SetGlobalProperty("__folio_iframe_error_target", ElementObject);
            try { Engine.Evaluate("__folio_iframe_error_target.dispatchEvent(new Event('error'))"); }
            finally { Engine.SetGlobalProperty("__folio_iframe_error_target", ScriptValue.Undefined); }
        }
        if (OnError.IsFunction)
        {
            var target = ElementObject.IsObject ? ElementObject : Window.IsObject ? Window : Engine.CreateHostObject(MessageEventTemplate, this);
            Engine.SetGlobalProperty("__folio_iframe_error_target", target);
            try
            {
                var eventValue = Engine.Evaluate("({ type: 'error', target: __folio_iframe_error_target })");
                Engine.Call(OnError, ScriptValue.Undefined, [eventValue]);
            }
            finally { Engine.SetGlobalProperty("__folio_iframe_error_target", ScriptValue.Undefined); }
        }
    }
}
