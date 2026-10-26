using Folio;
using Folio.Dom;
using Folio.Html;
using Folio.Painting;
using Folio.Resources;
using System.Runtime.CompilerServices;

namespace Folio.Scripting.Dom;

internal sealed class IframeState : IFramePresentation, IFeatureFallbackSource
{
    private sealed class AggregateBudget
    {
        private readonly object _gate = new();
        private readonly int _limit;
        private int _active;
        public AggregateBudget(int limit) => _limit = Math.Max(0, limit);
        public bool TryAcquire()
        {
            lock (_gate)
            {
                if (_active >= _limit) return false;
                _active++;
                return true;
            }
        }
        public void Release() { lock (_gate) _active = Math.Max(0, _active - 1); }
    }

    private static readonly ConditionalWeakTable<FolioOptions, AggregateBudget> AggregateBudgets = new();
    private static AggregateBudget GetOrCreateBudget(FolioOptions options) =>
        AggregateBudgets.GetValue(options, static value => new AggregateBudget(value.Limits.MaxIframes));
    private static void AssociateBudget(FolioOptions options, AggregateBudget budget)
    {
        AggregateBudgets.Remove(options);
        AggregateBudgets.Add(options, budget);
    }

    public IframeState(IScriptEngine engine, IHostObjectTemplate messageEventTemplate, ElementNode element, bool enabled, IResourceLoader? resourceLoader = null, Uri? baseUri = null, Action<Uri>? navigationRequested = null, FolioOptions? parentOptions = null, Func<bool>? checkPermission = null, HtmlEventLoop? parentLoop = null)
    {
        Engine = engine;
        MessageEventTemplate = messageEventTemplate;
        Element = element;
        Enabled = enabled;
        ResourceLoader = resourceLoader;
        BaseUri = baseUri;
        NavigationRequested = navigationRequested;
        ParentOptions = parentOptions;
        ParentLoop = parentLoop;
        _aggregateBudget = parentOptions is null ? null : GetOrCreateBudget(parentOptions);
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
    private bool PermissionGranted
    {
        get
        {
            if (!Enabled) return false;
            try { return _checkPermission?.Invoke() ?? true; }
            catch { return false; }
        }
    }
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

    private bool AllowsFeaturePolicy(string feature)
    {
        var raw = Element.GetAttribute("allow");
        if (string.IsNullOrWhiteSpace(raw)) return false;
        foreach (var policy in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = policy.Split([' ', '\t', '\n', '\f', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 || !string.Equals(tokens[0], feature, StringComparison.OrdinalIgnoreCase)) continue;
            // An omitted allowlist is the iframe's normal source allowlist.
            if (tokens.Length == 1) return true;
            if (tokens.Skip(1).Any(token => string.Equals(token, "'none'", StringComparison.OrdinalIgnoreCase))) return false;
            foreach (var token in tokens.Skip(1))
            {
                if (token == "*") return true;
                if (string.Equals(token, "'self'", StringComparison.OrdinalIgnoreCase)
                    && Origin != "null" && Origin == ParentOrigin) return true;
                if (string.Equals(token, "'src'", StringComparison.OrdinalIgnoreCase) && Origin != "null") return true;
                if (Uri.TryCreate(token.Trim('\''), UriKind.Absolute, out var allowed)
                    && Origin != "null" && NetworkOrigin(allowed) == Origin) return true;
            }
            return false;
        }
        return false;
    }

    private FeatureCapabilities ChildCapabilities
    {
        get
        {
            return new FeatureCapabilities
            {
                WebGl = _capabilities.WebGl && AllowsFeaturePolicy("webgl"),
                Media = _capabilities.Media && AllowsFeaturePolicy("media"),
                Sound = _capabilities.Sound && AllowsFeaturePolicy("sound"),
                Camera = _capabilities.Camera && AllowsFeaturePolicy("camera"),
                Microphone = _capabilities.Microphone && AllowsFeaturePolicy("microphone"),
                AllowSoundAutoplay = _capabilities.AllowSoundAutoplay && AllowsFeaturePolicy("autoplay"),
                Workers = _capabilities.Workers && AllowsFeaturePolicy("workers"),
                WebAssembly = _capabilities.WebAssembly && AllowsFeaturePolicy("webassembly"),
                Iframes = _capabilities.Iframes && AllowsFeaturePolicy("iframes"),
                WebRtc = _capabilities.WebRtc && AllowsFeaturePolicy("webrtc"),
                ServiceWorkers = _capabilities.ServiceWorkers && AllowsFeaturePolicy("service-workers"),
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

    private ResourceResponse? LoadDocumentResource(Uri uri)
    {
        var request = new ResourceRequest(uri.AbsoluteUri, ResourceKind.Document);
        if (ParentOptions?.ResourceInterceptor?.Intercept(request) is { } intercepted)
            return intercepted;
        return ResourceLoader?.LoadAsync(request, CancellationToken.None).GetAwaiter().GetResult();
    }
    private HtmlEventLoop? ParentLoop { get; }
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
    private ScriptValue _childOnMessage = ScriptValue.Null;
    private ScriptValue _childParentWindow = ScriptValue.Undefined;
    private readonly List<ScriptValue> _childMessageListeners = [];
    private readonly AggregateBudget? _aggregateBudget;
    private bool _aggregateSlot;
    private string? _loadedSource;
    private bool _loadDispatched;
    private long _loadGeneration;
    private FeatureFallback? _fallback;
    public FeatureFallback? Fallback => Closed ? new(FallbackFeature.Iframes, FallbackReason.Closed) :
        !PermissionGranted ? new(FallbackFeature.Iframes, FallbackReason.HostDenied) : _fallback;

    private string ResolveSource(string source) => string.IsNullOrWhiteSpace(source) ? "about:blank"
        : BaseUri is not null && Uri.TryCreate(BaseUri, source, out var resolved) ? resolved.AbsoluteUri : source;

    private void InstallChildWindowBridge(IScriptEngine childEngine)
    {
        var bridgeTemplate = new HostObjectTemplate("IframeParentWindow")
            .DefineMethod("postMessage", (target, _, args) =>
            {
                var bridge = (ChildWindowBridge)target;
                if (args.Length > 0)
                    bridge.State.PostMessageFromChild(bridge.Engine, args[0], args.Length > 1 ? WebIdlConversions.ToDOMString(args[1]) : "*", args.Length > 2 ? args[2] : ScriptValue.Undefined);
                return ScriptValue.Undefined;
            })
            .DefineMethod("setOnMessage", (target, _, args) => { ((ChildWindowBridge)target).State.SetChildOnMessage(args.Length > 0 ? args[0] : ScriptValue.Null); return ScriptValue.Undefined; })
            .DefineMethod("addMessageListener", (target, _, args) => { if (args.Length > 0) ((ChildWindowBridge)target).State.AddChildMessageListener(args[0]); return ScriptValue.Undefined; })
            .DefineMethod("removeMessageListener", (target, _, args) => { if (args.Length > 0) ((ChildWindowBridge)target).State.RemoveChildMessageListener(args[0]); return ScriptValue.Undefined; });
        var bridge = childEngine.CreateHostObject(bridgeTemplate, new ChildWindowBridge(this, childEngine));
        _childParentWindow = bridge;
        childEngine.SetGlobalProperty("__folio_iframe_parent", bridge);
        childEngine.EvaluateHostScript("""
            (function(parentWindow) {
                globalThis.parent = parentWindow;
                globalThis.top = parentWindow;
                globalThis.addEventListener = function(type, handler) { if (type === 'message') parentWindow.addMessageListener(handler); };
                globalThis.removeEventListener = function(type, handler) { if (type === 'message') parentWindow.removeMessageListener(handler); };
                Object.defineProperty(globalThis, 'onmessage', { configurable: true, get: function() { return globalThis.__folio_iframe_onmessage || null; }, set: function(value) { globalThis.__folio_iframe_onmessage = value; parentWindow.setOnMessage(value); } });
            })(__folio_iframe_parent);
            delete globalThis.__folio_iframe_parent;
            """, "folio:iframe-window");
    }

    private sealed class ChildWindowBridge(IframeState state, IScriptEngine engine)
    {
        public IframeState State { get; } = state;
        public IScriptEngine Engine { get; } = engine;
    }

    public void PostMessage(ScriptValue data, string targetOrigin, ScriptValue transfer = default)
    {
        if (!PermissionGranted || Closed || (targetOrigin != "*" && targetOrigin != Origin)) return;
        if (_childEngine is not null)
        {
            DispatchToChild(data, transfer, targetOrigin);
            return;
        }
        if (!OnMessage.IsFunction && _messageListeners.Count == 0) return;
        var message = Engine.CreateHostObject(MessageEventTemplate, new MessageEventState(data, Origin, Window, target: Window, ports: StructuredCloneCodec.CreatePorts(Engine, [])));
        if (OnMessage.IsFunction) Engine.Call(OnMessage, ScriptValue.Undefined, [message]);
        foreach (var listener in _messageListeners.ToArray())
            if (listener.IsFunction) Engine.Call(listener, ScriptValue.Undefined, [message]);
    }

    private void PostMessageFromChild(IScriptEngine childEngine, ScriptValue data, string targetOrigin, ScriptValue transfer)
    {
        if (!PermissionGranted || Closed || (targetOrigin != "*" && targetOrigin != ParentOrigin)) return;
        if (!OnMessage.IsFunction && _messageListeners.Count == 0) return;
        try
        {
            var payload = StructuredCloneCodec.Encode(childEngine, data, transfer, MaxMessageBytes, MessagePortEndpoint.TryGetPort);
            var decoded = StructuredCloneCodec.Decode(Engine, payload,
                (port, destination) => port.ClaimTransfer(destination, ParentLoop, MaxMessageBytes).GetWrapper());
            var message = Engine.CreateHostObject(MessageEventTemplate, new MessageEventState(decoded.Value, Origin, Window, target: Window, ports: StructuredCloneCodec.CreatePorts(Engine, decoded.Ports)));
            if (OnMessage.IsFunction) Engine.Call(OnMessage, ScriptValue.Undefined, [message]);
            foreach (var listener in _messageListeners.ToArray())
                if (listener.IsFunction) Engine.Call(listener, ScriptValue.Undefined, [message]);
            if (ParentLoop is null) MessagePortEndpoint.DrainAll(Engine);
        }
        catch { }
    }

    private void DispatchToChild(ScriptValue data, ScriptValue transfer, string targetOrigin)
    {
        if (_childEngine is null || (!_childOnMessage.IsFunction && _childMessageListeners.Count == 0)) return;
        try
        {
            var payload = StructuredCloneCodec.Encode(Engine, data, transfer, MaxMessageBytes, MessagePortEndpoint.TryGetPort);
            var child = _childEngine;
            var decoded = StructuredCloneCodec.Decode(child, payload,
                (port, destination) => port.ClaimTransfer(destination, _childLoop, MaxMessageBytes).GetWrapper());
            // The receiver is the child's own Window/global object.  Leaving target
            // undefined would make MessageEventState fall back to source (the
            // parent's WindowProxy), which is observably wrong in the child.
            var childWindow = child.Evaluate("globalThis");
            var message = child.CreateHostObject(MessageEventTemplate, new MessageEventState(decoded.Value, ParentOrigin, _childParentWindow, target: childWindow, ports: StructuredCloneCodec.CreatePorts(child, decoded.Ports)));
            if (_childOnMessage.IsFunction) child.Call(_childOnMessage, ScriptValue.Undefined, [message]);
            foreach (var listener in _childMessageListeners.ToArray())
                if (listener.IsFunction) child.Call(listener, ScriptValue.Undefined, [message]);
            if (ParentLoop is null) MessagePortEndpoint.DrainAll(Engine);
            if (_childLoop is null) MessagePortEndpoint.DrainAll(child);
        }
        catch { }
    }

    private void SetChildOnMessage(ScriptValue value) => _childOnMessage = value.IsFunction ? value : ScriptValue.Null;
    private void AddChildMessageListener(ScriptValue value) { if (value.IsFunction && !_childMessageListeners.Contains(value)) _childMessageListeners.Add(value); }
    private void RemoveChildMessageListener(ScriptValue value) => _childMessageListeners.Remove(value);

    private int MaxMessageBytes => ParentOptions?.Limits.MaxWorkerMessageBytes ?? 1 * 1024 * 1024;

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
        _childOnMessage = ScriptValue.Null;
        _childParentWindow = ScriptValue.Undefined;
        _childMessageListeners.Clear();
        RetireChild();
        ReleaseAggregateSlot();
    }

    internal bool TryAcquireAggregateSlot()
    {
        if (_aggregateBudget is null || _aggregateSlot) return true;
        if (!_aggregateBudget.TryAcquire()) return false;
        _aggregateSlot = true;
        return true;
    }

    private void ReleaseAggregateSlot()
    {
        if (!_aggregateSlot) return;
        _aggregateSlot = false;
        _aggregateBudget?.Release();
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
        _childOnMessage = ScriptValue.Null;
        _childParentWindow = ScriptValue.Undefined;
        _childMessageListeners.Clear();
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
            try { response = LoadDocumentResource(uri); }
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
            MediaCaptureProvider = parent.MediaCaptureProvider,
            MediaDecoder = parent.MediaDecoder,
            MediaAudioOutput = parent.MediaAudioOutput,
            Scripting = ScriptsAllowed ? parent.Scripting : ScriptingPolicy.Disabled,
            DiagramLibraryUrls = parent.DiagramLibraryUrls,
            ContentProcessPath = parent.ContentProcessPath,
            Raster = parent.Raster,
            ReducedMotion = parent.ReducedMotion,
            FormsAllowed = FormsAllowed,
            ResourceLoader = parent.ResourceLoader,
            ResourceInterceptor = parent.ResourceInterceptor,
            UserStyleSheet = parent.UserStyleSheet,
            WebRtcAllowedOrigins = parent.WebRtcAllowedOrigins,
            CollectDiagnostics = parent.CollectDiagnostics,
        };
        if (_aggregateBudget is not null)
            AssociateBudget(options, _aggregateBudget);
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
                InstallChildWindowBridge(childEngine);
                if (!IsCurrent()) return;
                childRunner = new ScriptRunner(childDocument, childEngine, childLoop, options);
                _childRunner = childRunner;

                var parentWindowTemplate = new HostObjectTemplate("WindowProxy")
                    .DefineProperty("closed", (target, _) => ScriptValue.FromBoolean(((IframeState)target).Closed))
                    .DefineProperty("origin", (target, _) => ScriptValue.FromString(((IframeState)target).ParentOrigin))
                    .DefineMethod("postMessage", (target, _, args) =>
                    {
                        if (args.Length > 0)
                        {
                            var data = args[0];
                            var targetOrigin = args.Length > 1 ? WebIdlConversions.ToDOMString(args[1]) : "*";
                            var transfer = args.Length > 2 ? args[2] : ScriptValue.Undefined;
                            ((IframeState)target).PostMessageToParent(data, targetOrigin, transfer);
                        }
                        return ScriptValue.Undefined;
                    });
                var parentProxy = childEngine.CreateHostObject(parentWindowTemplate, this);
                childEngine.SetGlobalProperty("__folio_parent_window", parentProxy);
                childEngine.EvaluateHostScript("""
                    globalThis.parent = __folio_parent_window;
                    globalThis.top = __folio_parent_window;
                    delete globalThis.__folio_parent_window;
                """, "folio:iframe-parent");

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

    internal void PumpChildLoop() => _childLoop?.SpinUntilIdle();

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
