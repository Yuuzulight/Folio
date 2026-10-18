using Folio;
using Folio.Dom;
using Folio.Html;
using Folio.Painting;
using Folio.Resources;

namespace Folio.Scripting.Dom;

internal sealed class IframeState : IFramePresentation
{
    public IframeState(IScriptEngine engine, IHostObjectTemplate messageEventTemplate, ElementNode element, bool enabled, IResourceLoader? resourceLoader = null, Uri? baseUri = null, Action<Uri>? navigationRequested = null)
    {
        Engine = engine;
        MessageEventTemplate = messageEventTemplate;
        Element = element;
        Enabled = enabled;
        ResourceLoader = resourceLoader;
        BaseUri = baseUri;
        NavigationRequested = navigationRequested;
        element.FramePresentation = this;
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
    public Action<Uri>? NavigationRequested { get; }
    public bool Closed { get; private set; }
    public ScriptValue OnMessage { get; set; } = ScriptValue.Null;
    public ScriptValue OnLoad { get; set; } = ScriptValue.Null;
    public ScriptValue OnError { get; set; } = ScriptValue.Null;
    public ScriptValue ElementObject { get; set; } = ScriptValue.Null;
    public ScriptValue Window { get; set; } = ScriptValue.Null;
    public DocumentNode? ChildDocument { get; private set; }
    private Document? ChildRenderDocument { get; set; }
    public DisplayList? FrameDisplayList { get; private set; }
    object? IFramePresentation.DisplayList => FrameDisplayList;
    object? IFramePresentation.Accessibility => ChildRenderDocument?.AccessibilityTree();
    object? IFramePresentation.ChildDocument => ChildRenderDocument;
    bool IFramePresentation.HasFocusableContent => ChildRenderDocument?.Input.GetSequentialFocusableElements().Count > 0;
    string? IFramePresentation.FallbackText => ChildRenderDocument?.AccessibilityTree() is { } tree ? string.Join(" ", Text(tree)) : null;
    private readonly List<ScriptValue> _messageListeners = [];
    private string? _loadedSource;
    private bool _loadDispatched;

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
        ChildRenderDocument?.Dispose();
        ChildRenderDocument = null;
        FrameDisplayList = null;
        _loadedSource = null;
        _loadDispatched = false;
        var sandbox = SandboxTokens;
        var sameOrigin = sandbox.Length == 0 || sandbox.Contains("allow-same-origin", StringComparer.OrdinalIgnoreCase);
        Origin = sameOrigin && Uri.TryCreate(ResolveSource(Element.GetAttribute("src") ?? string.Empty), UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority)
            : "null";
    }

    public void LoadInlineDocument(bool dispatchLoad = true)
    {
        if (!Enabled) return;
        var inline = Element.GetAttribute("srcdoc");
        var source = ResolveSource(Element.GetAttribute("src") ?? string.Empty);
        var current = inline is not null ? "srcdoc:" + inline : "src:" + source;
        if (ChildDocument is not null && _loadedSource == current)
        {
            if (dispatchLoad && !_loadDispatched)
                DispatchLoad();
            return;
        }
        ChildDocument = null;
        ChildRenderDocument?.Dispose();
        ChildRenderDocument = null;
        FrameDisplayList = null;
        _loadedSource = current;
        _loadDispatched = false;
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
            if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) { DispatchError(); return; }
            try { NavigationRequested?.Invoke(uri); } catch { }
            if (ResourceLoader is null) { DispatchError(); return; }
            var response = ResourceLoader.LoadAsync(new ResourceRequest(uri.AbsoluteUri, ResourceKind.Document), CancellationToken.None).GetAwaiter().GetResult();
            if (response?.Data is not { } data) { DispatchError(); return; }
            html = System.Text.Encoding.UTF8.GetString(data);
        }
        ChildRenderDocument = Document.Parse(html, new FolioOptions { BaseUri = BaseUri });
        ChildDocument = ChildRenderDocument.Node;
        if (dispatchLoad)
            DispatchLoad();
    }

    private void DispatchLoad()
    {
        _loadDispatched = true;
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

    public void Render(float width, float height)
    {
        LoadInlineDocument(dispatchLoad: false);
        if (ChildRenderDocument is { } child)
            (FrameDisplayList, _) = child.Paint(Math.Max(1, width), Math.Max(1, height));
    }

    bool IFramePresentation.FocusFirst(bool reverse)
    {
        if (ChildRenderDocument is not { } child)
            return false;
        var focusable = child.Input.GetSequentialFocusableElements();
        if (focusable.Count == 0)
            return false;
        child.Input.Focus(focusable[reverse ? ^1 : 0], visible: true);
        return true;
    }

    private static IEnumerable<string> Text(Interaction.AccessibleNode node)
    {
        if (node.Role is "text" or "button" or "link" or "heading")
            yield return node.Name;
        foreach (var child in node.Children)
            foreach (var text in Text(child))
                yield return text;
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
