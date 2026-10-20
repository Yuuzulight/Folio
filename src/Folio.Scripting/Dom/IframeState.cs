using Folio;
using Folio.Dom;
using Folio.Html;
using Folio.Painting;
using Folio.Resources;

namespace Folio.Scripting.Dom;

internal sealed class IframeState : IFramePresentation, IFeatureFallbackSource
{
    public IframeState(IScriptEngine engine, IHostObjectTemplate messageEventTemplate, ElementNode element, bool enabled, IResourceLoader? resourceLoader = null, Uri? baseUri = null, Action<Uri>? navigationRequested = null, FolioOptions? parentOptions = null, Func<bool>? checkPermission = null)
    {
        Engine = engine;
        MessageEventTemplate = messageEventTemplate;
        Element = element;
        Enabled = enabled;
        ResourceLoader = resourceLoader;
        BaseUri = baseUri;
        NavigationRequested = navigationRequested;
        ParentOptions = parentOptions;
        _checkPermission = checkPermission;
        _capabilities = parentOptions?.Capabilities ?? FeatureCapabilities.Default;
        element.FramePresentation = this;
        UpdateOrigin();
    }

    public IScriptEngine Engine { get; }
    public IHostObjectTemplate MessageEventTemplate { get; }
    public ElementNode Element { get; }
    public bool Enabled { get; private set; }
    private readonly Func<bool>? _checkPermission;
    private bool PermissionGranted => Enabled && (_checkPermission?.Invoke() ?? true);
    public string Origin { get; private set; } = "null";
    public string ParentOrigin => NetworkOrigin(BaseUri);
    private bool SameOriginAllowed => !HasSandbox || SandboxTokens.Contains("allow-same-origin", StringComparer.OrdinalIgnoreCase);
    public bool CanAccessDocument => !Closed && PermissionGranted && SameOriginAllowed &&
        (Element.GetAttribute("srcdoc") is not null || string.IsNullOrWhiteSpace(Element.GetAttribute("src")) || Origin != "null" && Origin == ParentOrigin);

    private static string NetworkOrigin(Uri? uri) => uri is not null && uri.Scheme is "http" or "https" ? uri.GetLeftPart(UriPartial.Authority) : "null";
    private void UpdateOrigin()
    {
        var source = Element.GetAttribute("src") ?? "";
        var inherited = Element.GetAttribute("srcdoc") is not null || string.IsNullOrWhiteSpace(source);
        Origin = !SameOriginAllowed ? "null" : inherited ? ParentOrigin :
            Uri.TryCreate(ResolveSource(source), UriKind.Absolute, out var uri) ? NetworkOrigin(uri) : "null";
    }
    private FeatureCapabilities ChildCapabilities
    {
        get
        {
            var policies = (Element.GetAttribute("allow") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(policy => policy.Trim().Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length == 1 || parts.Length > 1 &&
                    !parts.Skip(1).Contains("'none'", StringComparer.OrdinalIgnoreCase) &&
                    parts.Skip(1).Any(origin => origin == "*" ||
                        origin == "'self'" && Origin != "null" && Origin == ParentOrigin ||
                        Uri.TryCreate(origin, UriKind.Absolute, out var allowed) && Origin != "null" && NetworkOrigin(allowed) == Origin))
                .Select(parts => parts[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new FeatureCapabilities
            {
                WebGl = _capabilities.WebGl && policies.Contains("webgl"),
                Media = _capabilities.Media && policies.Contains("media"),
                Sound = _capabilities.Sound && policies.Contains("sound"),
                Camera = _capabilities.Camera && policies.Contains("camera"),
                Microphone = _capabilities.Microphone && policies.Contains("microphone"),
                AllowSoundAutoplay = _capabilities.AllowSoundAutoplay && policies.Contains("autoplay"),
                Workers = _capabilities.Workers && policies.Contains("workers"),
                WebAssembly = _capabilities.WebAssembly && policies.Contains("webassembly"),
                Iframes = _capabilities.Iframes && policies.Contains("iframes"),
                WebRtc = _capabilities.WebRtc && policies.Contains("webrtc"),
                ServiceWorkers = _capabilities.ServiceWorkers && policies.Contains("service-workers"),
            };
        }
    }
    public bool ScriptsAllowed => !HasSandbox || SandboxTokens.Contains("allow-scripts", StringComparer.OrdinalIgnoreCase);
    public bool FormsAllowed => !HasSandbox || SandboxTokens.Contains("allow-forms", StringComparer.OrdinalIgnoreCase);
    public bool HasSandbox => Element.GetAttribute("sandbox") is not null;
    private string[] SandboxTokens => (Element.GetAttribute("sandbox") ?? string.Empty).Split([' ', '\t', '\n', '\f', '\r'], StringSplitOptions.RemoveEmptyEntries);
    public IResourceLoader? ResourceLoader { get; }
    public Uri? BaseUri { get; }
    public Action<Uri>? NavigationRequested { get; }
    public FolioOptions? ParentOptions { get; }
    public bool Closed { get; private set; }
    public ScriptValue OnMessage { get; set; } = ScriptValue.Null;
    public ScriptValue OnLoad { get; set; } = ScriptValue.Null;
    public ScriptValue OnError { get; set; } = ScriptValue.Null;
    public ScriptValue ElementObject { get; set; } = ScriptValue.Null;
    public ScriptValue Window { get; set; } = ScriptValue.Null;
    public DocumentNode? ChildDocument { get; private set; }
    private Document? ChildRenderDocument { get; set; }
    private IScriptEngine? _childEngine;
    private HtmlEventLoop? _childLoop;
    private ScriptRunner? _childRunner;
    private FeatureCapabilities _capabilities = FeatureCapabilities.Default;
    public DisplayList? FrameDisplayList { get; private set; }
    object? IFramePresentation.DisplayList => FrameDisplayList;
    object? IFramePresentation.Accessibility => ChildRenderDocument?.AccessibilityTree();
    object? IFramePresentation.ChildDocument => ChildRenderDocument;
    bool IFramePresentation.HasFocusableContent => ChildRenderDocument?.Input.GetSequentialFocusableElements().Count > 0;
    string? IFramePresentation.FallbackText => ChildRenderDocument?.AccessibilityTree() is { } tree ? string.Join(" ", Text(tree)) : null;
    private readonly List<ScriptValue> _messageListeners = [];
    private string? _loadedSource;
    private bool _loadDispatched;
    private long _loadGeneration;
    private FeatureFallback? _fallback;
    public FeatureFallback? Fallback => Closed ? new(FallbackFeature.Iframes, FallbackReason.Closed) :
        !PermissionGranted ? new(FallbackFeature.Iframes, FallbackReason.HostDenied) : _fallback;

    private string ResolveSource(string source) => string.IsNullOrWhiteSpace(source) ? "about:blank"
        : BaseUri is not null && Uri.TryCreate(BaseUri, source, out var resolved) ? resolved.AbsoluteUri : source;

    public void PostMessage(ScriptValue data, string targetOrigin, ScriptValue transfer = default)
    {
        if (!PermissionGranted || Closed || (!OnMessage.IsFunction && _messageListeners.Count == 0) || (targetOrigin != "*" && targetOrigin != Origin)) return;
        var message = Engine.CreateHostObject(MessageEventTemplate, new MessageEventState(data, Origin, Window, target: Window, ports: StructuredCloneCodec.CreatePorts(Engine, [])));
        if (OnMessage.IsFunction) Engine.Call(OnMessage, ScriptValue.Undefined, [message]);
        foreach (var listener in _messageListeners.ToArray())
            if (listener.IsFunction) Engine.Call(listener, ScriptValue.Undefined, [message]);
    }

    public void AddMessageListener(ScriptValue listener)
    {
        if (listener.IsFunction && !_messageListeners.Contains(listener)) _messageListeners.Add(listener);
    }

    public void RemoveMessageListener(ScriptValue listener) => _messageListeners.Remove(listener);

    public void Close()
    {
        _loadGeneration++;
        Closed = true;
        _fallback = new(FallbackFeature.Iframes, FallbackReason.Closed);
        OnMessage = ScriptValue.Null;
        _messageListeners.Clear();
        RetireChild();
    }

    internal void UpdateCapabilities(FeatureCapabilities capabilities)
    {
        _capabilities = capabilities;
        if (Enabled != capabilities.Iframes)
        {
            Enabled = capabilities.Iframes;
            Invalidate();
        }
        if (_childEngine is not null) DomBindings.UpdateCapabilities(_childEngine, ChildCapabilities);
    }

    private void RetireChild()
    {
        var runner = _childRunner;
        var engine = _childEngine;
        var loop = _childLoop;
        var document = ChildRenderDocument;
        _childRunner = null;
        _childEngine = null;
        _childLoop = null;
        ChildDocument = null;
        ChildRenderDocument = null;
        FrameDisplayList = null;
        Exception? failure = null;
        Action[] cleanup = [() => runner?.Dispose(), () => { if (engine is not null) DomBindings.Dispose(engine); },
            () => loop?.Dispose(), () => engine?.Dispose(), () => document?.Dispose()];
        foreach (var release in cleanup)
            try { release(); } catch (Exception error) { failure ??= error; }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public void Invalidate()
    {
        _loadGeneration++;
        OnMessage = ScriptValue.Null;
        _messageListeners.Clear();
        Closed = false;
        _loadedSource = null;
        _loadDispatched = false;
        UpdateOrigin();
        _fallback = null;
        RetireChild();
    }

    public void LoadInlineDocument(bool dispatchLoad = true)
    {
        if (Closed) { _fallback = new(FallbackFeature.Iframes, FallbackReason.Closed); return; }
        if (!PermissionGranted) { _fallback = new(FallbackFeature.Iframes, FallbackReason.HostDenied); return; }
        var inline = Element.GetAttribute("srcdoc");
        var source = ResolveSource(Element.GetAttribute("src") ?? string.Empty);
        var current = inline is not null ? "srcdoc:" + inline : "src:" + source;
        if (_loadedSource == current && _fallback is not null && ChildDocument is null)
            return;
        if (ChildDocument is not null && _loadedSource == current)
        {
            if (dispatchLoad && !_loadDispatched)
                DispatchLoad();
            return;
        }
        var generation = ++_loadGeneration;
        bool IsCurrent() => generation == _loadGeneration && !Closed && PermissionGranted &&
            (Element.GetAttribute("srcdoc") is { } latestInline ? "srcdoc:" + latestInline : "src:" + ResolveSource(Element.GetAttribute("src") ?? "")) == current;
        void Fail(FallbackReason reason)
        {
            if (!IsCurrent()) return;
            _fallback = new(FallbackFeature.Iframes, reason);
            DispatchError();
        }
        RetireChild();
        if (!IsCurrent()) return;
        _loadedSource = current;
        _loadDispatched = false;
        UpdateOrigin();
        string html;
        if (inline is not null)
        {
            html = inline;
        }
        else if (string.IsNullOrWhiteSpace(source) || source.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
        {
            // An iframe with no src is an inherited, valid empty document.
            html = "";
        }
        else if (source.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase))
        {
            var comma = source.IndexOf(',');
            if (comma < 0) { Fail(FallbackReason.SourceRefused); return; }
            var payload = source[(comma + 1)..];
            try
            {
                html = source[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase)
                    ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload))
                    : Uri.UnescapeDataString(payload);
            }
            catch (FormatException) { Fail(FallbackReason.SourceRefused); return; }
        }
        else
        {
            if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) { Fail(FallbackReason.SourceRefused); return; }
            try { NavigationRequested?.Invoke(uri); } catch { }
            if (!IsCurrent()) return;
            if (ResourceLoader is null) { Fail(FallbackReason.SourceUnavailable); return; }
            ResourceResponse? response;
            try { response = ResourceLoader.LoadAsync(new ResourceRequest(uri.AbsoluteUri, ResourceKind.Document), CancellationToken.None).GetAwaiter().GetResult(); }
            catch { Fail(FallbackReason.SourceUnavailable); return; }
            if (!IsCurrent()) return;
            if (response?.Data is not { } data) { Fail(FallbackReason.SourceUnavailable); return; }
            html = System.Text.Encoding.UTF8.GetString(data);
        }
        var parent = ParentOptions;
        var childBase = inline is not null ? BaseUri : Uri.TryCreate(source, UriKind.Absolute, out var resolvedBase) ? resolvedBase : BaseUri;
        var options = parent is null ? new FolioOptions { BaseUri = childBase } : new FolioOptions
        {
            ArtifactId = parent.ArtifactId,
            ArtifactStorage = parent.ArtifactStorage,
            BaseUri = childBase,
            Capabilities = ChildCapabilities,
            ColorScheme = parent.ColorScheme,
            Fonts = parent.Fonts,
            IframeNavigationRequested = parent.IframeNavigationRequested,
            Limits = parent.Limits with { MaxIframes = Math.Max(0, parent.Limits.MaxIframes - 1) },
            MediaCaptureActivityChanged = parent.MediaCaptureActivityChanged,
            MediaCapturePermission = parent.MediaCapturePermission,
            MediaDecoder = parent.MediaDecoder,
            MediaAudioOutput = parent.MediaAudioOutput,
            Scripting = ScriptsAllowed ? parent.Scripting : ScriptingPolicy.Disabled,
            DiagramLibraryUrls = parent.DiagramLibraryUrls,
            ContentProcessPath = parent.ContentProcessPath,
            Raster = parent.Raster,
            ReducedMotion = parent.ReducedMotion,
            ResourceLoader = parent.ResourceLoader,
            UserStyleSheet = parent.UserStyleSheet,
            WebRtcAllowedOrigins = parent.WebRtcAllowedOrigins,
            CollectDiagnostics = parent.CollectDiagnostics,
        };
        Document childDocument;
        try { childDocument = Document.Parse(html, options); }
        catch { Fail(FallbackReason.DecodeFailed); return; }
        if (!IsCurrent()) { childDocument.Dispose(); return; }
        ChildRenderDocument = childDocument;
        ChildDocument = ChildRenderDocument.Node;
        IScriptEngine? childEngine = null;
        HtmlEventLoop? childLoop = null;
        ScriptRunner? childRunner = null;
        var committed = false;
        var failed = false;
        try
        {
            if (options.Scripting != ScriptingPolicy.Disabled && Engine is IScriptEngineFactory factory)
            {
                childEngine = factory.CreateIsolated();
                if (!IsCurrent()) return;
                _childEngine = childEngine;
                childEngine.Configure(ScriptLimits.Default);
                if (!IsCurrent()) return;
                childLoop = new HtmlEventLoop(childEngine);
                _childLoop = childLoop;
                ScriptGlobalsInstaller.Install(childLoop, childEngine);
                DomBindings.Install(childEngine, childDocument, eventLoop: childLoop);
                if (!IsCurrent()) return;
                childRunner = new ScriptRunner(childDocument, childEngine, childLoop, options);
                _childRunner = childRunner;
                childRunner.Run();
            }
            committed = IsCurrent();
        }
        catch { failed = IsCurrent(); }
        finally
        {
            if (!committed)
            {
                // Reentrant navigation may already own newer fields. Retire only this startup's resources.
                if (ReferenceEquals(_childEngine, childEngine)) _childEngine = null;
                if (ReferenceEquals(_childLoop, childLoop)) _childLoop = null;
                if (ReferenceEquals(_childRunner, childRunner)) _childRunner = null;
                if (ReferenceEquals(ChildRenderDocument, childDocument))
                {
                    ChildRenderDocument = null;
                    ChildDocument = null;
                    FrameDisplayList = null;
                }
                Action[] cleanup = [() => childRunner?.Dispose(), () => { if (childEngine is not null) DomBindings.Dispose(childEngine); },
                    () => childLoop?.Dispose(), () => childEngine?.Dispose(), childDocument.Dispose];
                foreach (var release in cleanup)
                    try { release(); } catch { }
            }
        }
        if (!committed) { if (failed) Fail(FallbackReason.DecodeFailed); return; }
        _fallback = null;
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
        {
            _childLoop?.SpinUntilIdle();
            (FrameDisplayList, _) = child.Paint(Math.Max(1, width), Math.Max(1, height));
        }
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
