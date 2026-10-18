using System.Diagnostics.CodeAnalysis;
using Folio.Dom;
using Folio.Resources;

namespace Folio.Scripting.Dom;

/// <summary>
/// Context bridging Folio's DOM nodes with the host script engine and wrapper cache (study 02, study 17, #451).
/// </summary>
internal sealed class DomContext
{
    private readonly Dictionary<string, IHostObjectTemplate> _htmlElementTemplates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ElementNode, (double X, double Y)> _scrollOffsets = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ElementNode, CanvasElementState> _canvases = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ElementNode, MediaElementState> _media = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ElementNode, IframeState> _iframes = new(ReferenceEqualityComparer.Instance);
    private long _mediaBytesAllocated;
    private int _workerCount;
    private int _iframeCount;
    private readonly List<WorkerState> _workers = [];
    private readonly object _workersGate = new();
    private CssomRegistry? _cssom;

    /// <summary>The document's CSSOM sheets and rules.</summary>
    public CssomRegistry Cssom => _cssom ??= new CssomRegistry(this);

    /// <summary>The script functions behind <c>document.styleSheets</c> and an element's <c>sheet</c>, set when the CSSOM is installed.</summary>
    public ScriptValue CssomStyleSheets { get; set; }
    public ScriptValue CssomSheetOf { get; set; }
    private long _fetchedBytes;
    private readonly Dictionary<int, byte[]> _fetchBodies = [];
    private int _nextFetchBody;

    /// <summary>Keeps a fetched body here, out of script's memory, until the page reads it (or drops it): returns its handle.</summary>
    public int StoreFetchBody(byte[] data)
    {
        _fetchBodies[++_nextFetchBody] = data;
        return _nextFetchBody;
    }

    public byte[]? FetchBody(int handle) => _fetchBodies.GetValueOrDefault(handle);

    public void FreeFetchBody(int handle) => _fetchBodies.Remove(handle);

    /// <summary>The most one document may fetch in all (study 16).</summary>
    public const long MaxFetchedBytes = 64L * 1024 * 1024;

    /// <summary>
    /// A fetch() load: a data: URL or the host's loader, one response within the loader's size cap and all of them within
    /// <see cref="MaxFetchedBytes"/>. Refusals are reported as a diagnostic.
    /// </summary>
    public (ResourceResponse? Response, string? Error) Fetch(string url)
    {
        string? error = null;
        ResourceResponse? response = null;
        var allowed = url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        if (!allowed)
            error = "Only data:, http: and https: URLs can be fetched.";
        else
        {
            var loaded = new Folio.Resources.ResourceLoader(allowedFolders: null, maxBytes: Folio.Resources.ResourceLoader.MaxResourceBytes, host: ResourceLoader)
                .Load(new ResourceRequest(url, ResourceKind.Fetch));
            if (!loaded.Succeeded)
                error = loaded.Error ?? "The load failed.";
            else if (_fetchedBytes + loaded.Data!.Length > MaxFetchedBytes)
                error = "This document has already fetched the most bytes it may.";
            else
            {
                _fetchedBytes += loaded.Data.Length;
                response = loaded;
            }
        }

        if (response is null)
            Document.DiagnosticSink?.Invoke(new Diagnostic(DiagnosticCode.ResourceNotLoaded, Severity.Warning, $"fetch('{(url.Length > 120 ? url[..120] + "..." : url)}') failed: {error}", null, "fetch"));
        return (response, error);
    }

    public DomContext(IScriptEngine engine, DocumentNode document, ScriptWrapperCache? wrapperCache = null,
        Func<ElementNode, (float X, float Y, float Width, float Height)?>? layout = null,
        FeatureCapabilities? capabilities = null, int maxGpuBytes = 64 * 1024 * 1024, int maxMediaBytes = 128 * 1024 * 1024, int maxMediaDecoders = 8, int maxWorkerMessageBytes = 1 * 1024 * 1024, int maxWebAssemblyBytes = 64 * 1024 * 1024, int maxWorkers = 8, long maxWorkerMemoryBytes = 64L * 1024 * 1024, TimeSpan maxWorkerExecutionTime = default, long maxServiceWorkerCacheBytes = 64L * 1024 * 1024, int maxIframes = 16, int maxWebRtcConnections = 8, IReadOnlySet<string>? webRtcAllowedOrigins = null, Func<MediaCaptureRequest, bool>? mediaCapturePermission = null, IArtifactStorage? artifactStorage = null, string artifactId = "default", IResourceLoader? resourceLoader = null, Uri? baseUri = null,
        Folio.Painting.IRasterBackend? raster = null, Func<Folio.Hosting.CanvasStyleEngine?>? canvasStyles = null, Action<bool>? mediaCaptureActivityChanged = null, Action<Uri>? iframeNavigationRequested = null)
    {
        Engine = engine ?? throw new ArgumentNullException(nameof(engine));
        Document = document ?? throw new ArgumentNullException(nameof(document));
        WrapperCache = wrapperCache ?? new ScriptWrapperCache();
        Layout = layout;
        Capabilities = capabilities ?? FeatureCapabilities.Default;
        MaxGpuBytes = Math.Max(0, maxGpuBytes);
        MaxMediaBytes = Math.Max(0, maxMediaBytes);
        MaxMediaDecoders = Math.Max(0, maxMediaDecoders);
        MaxWorkerMessageBytes = Math.Max(0, maxWorkerMessageBytes);
        MaxWorkers = Math.Max(0, maxWorkers);
        MaxWorkerMemoryBytes = Math.Max(1, maxWorkerMemoryBytes);
        MaxWorkerExecutionTime = maxWorkerExecutionTime <= TimeSpan.Zero ? TimeSpan.FromSeconds(5) : maxWorkerExecutionTime;
        MaxServiceWorkerCacheBytes = Math.Max(0, maxServiceWorkerCacheBytes);
        MaxIframes = Math.Max(0, maxIframes);
        MaxWebRtcConnections = Math.Max(0, maxWebRtcConnections);
        WebRtcAllowedOrigins = webRtcAllowedOrigins ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        MediaCapturePermission = mediaCapturePermission;
        MediaCaptureActivityChanged = mediaCaptureActivityChanged;
        IframeNavigationRequested = iframeNavigationRequested;
        ArtifactStorage = artifactStorage;
        ArtifactId = string.IsNullOrWhiteSpace(artifactId) ? "default" : artifactId;
        MaxWebAssemblyBytes = Math.Max(0, maxWebAssemblyBytes);
        ResourceLoader = resourceLoader;
        BaseUri = baseUri;
        RasterBackend = raster;
        _canvasStyles = canvasStyles;

        Templates = new DomNodeTemplates(this);
    }

    public IScriptEngine Engine { get; }
    public DocumentNode Document { get; }
    public ScriptWrapperCache WrapperCache { get; }
    public Func<ElementNode, (float X, float Y, float Width, float Height)?>? Layout { get; }
    public FeatureCapabilities Capabilities { get; private set; }
    public int MaxGpuBytes { get; }
    public int MaxMediaBytes { get; }
    public int MaxMediaDecoders { get; }
    public int MaxWorkerMessageBytes { get; }
    public int MaxWorkers { get; }
    public long MaxWorkerMemoryBytes { get; }
    public TimeSpan MaxWorkerExecutionTime { get; }
    public long MaxServiceWorkerCacheBytes { get; }
    public int MaxIframes { get; }
    public int MaxWebRtcConnections { get; }
    public IReadOnlySet<string> WebRtcAllowedOrigins { get; }
    public Func<MediaCaptureRequest, bool>? MediaCapturePermission { get; }
    public Action<bool>? MediaCaptureActivityChanged { get; }
    public Action<Uri>? IframeNavigationRequested { get; }
    public IArtifactStorage? ArtifactStorage { get; }
    public string ArtifactId { get; }
    public bool TryAcquireWorker()
    {
        if (MaxWorkers <= 0) return false;
        var count = Interlocked.Increment(ref _workerCount);
        if (count <= MaxWorkers) return true;
        Interlocked.Decrement(ref _workerCount);
        return false;
    }
    public void ReleaseWorker() { if (Interlocked.Decrement(ref _workerCount) < 0) Interlocked.Exchange(ref _workerCount, 0); }
    public void RegisterWorker(WorkerState worker)
    {
        lock (_workersGate) _workers.Add(worker);
    }

    public void DisposeWorkers()
    {
        WorkerState[] workers;
        lock (_workersGate)
        {
            workers = _workers.ToArray();
            _workers.Clear();
        }
        foreach (var worker in workers) worker.Terminate();
    }
    public int MaxWebAssemblyBytes { get; }
    public IResourceLoader? ResourceLoader { get; }
    public Uri? BaseUri { get; }
    public Folio.Painting.IRasterBackend? RasterBackend { get; }
    private readonly Func<Folio.Hosting.CanvasStyleEngine?>? _canvasStyles;
    private readonly Dictionary<ElementNode, (string Source, Folio.Imaging.IImageHandle? Image)> _images = new(ReferenceEqualityComparer.Instance);
    public DomNodeTemplates Templates { get; }
    public ElementNode? FocusedElement { get; private set; }
    public void Focus(ElementNode element) => FocusedElement = element;
    public void Blur(ElementNode element) { if (ReferenceEquals(FocusedElement, element)) FocusedElement = null; }

    public void UpdateCapabilities(FeatureCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var wasWebGlEnabled = Capabilities.WebGl;
        var wasMediaEnabled = Capabilities.Media;
        Capabilities = capabilities;
        if (wasMediaEnabled != capabilities.Media)
            foreach (var media in _media.Values) media.SetCapability(capabilities.Media);
        if (wasWebGlEnabled == capabilities.WebGl) return;
        foreach (var canvas in Document.Descendants.OfType<ElementNode>().Where(element => element.LocalName == "canvas"))
        {
            if (canvas.CanvasSurface is not CanvasElementState state) continue;
            if (state.WebGlContext is { } context)
            {
                if (capabilities.WebGl) context.Restore();
                else context.Lose();
            }
        }
    }

    public (double X, double Y) GetScroll(ElementNode element) => _scrollOffsets.TryGetValue(element, out var value) ? value : (0, 0);
    public void SetScroll(ElementNode element, double x, double y) => _scrollOffsets[element] = (Math.Max(0, x), Math.Max(0, y));

    private MutationObserverRegistry? _mutationObservers;

    /// <summary>The document's mutation observers (https://dom.spec.whatwg.org/#mutation-observers).</summary>
    public MutationObserverRegistry MutationObservers => _mutationObservers ??= new MutationObserverRegistry(this);

    public ScriptValue WrapMutationRecord(MutationRecordState record) => WrapperCache.GetOrCreate(Engine, Templates.MutationRecordTemplate, record);

    public CanvasElementState GetCanvas(ElementNode element) =>
        _canvases.TryGetValue(element, out var state) ? state : (_canvases[element] = new CanvasElementState(element, RasterBackend, _canvasStyles?.Invoke()));

    /// <summary>The pixels an <c>img</c> element shows, decoded from its <c>src</c> (a data: URL, or what the host's loader gives); null while it has none.</summary>
    public Folio.Imaging.IImageHandle? ImageOf(ElementNode image)
    {
        var source = image.GetAttribute("src")?.Trim() ?? "";
        if (_images.TryGetValue(image, out var known) && known.Source == source)
            return known.Image;
        Folio.Imaging.IImageHandle? decoded = null;
        if (source.Length > 0)
        {
            byte[]? bytes = null;
            if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (Folio.Resources.DataUrl.TryParse(source, out var data, out _))
                    bytes = data;
            }
            else if (ResourceLoader is { } loader)
            {
                var url = BaseUri is not null && Uri.TryCreate(BaseUri, source, out var resolved) ? resolved.AbsoluteUri : source;
                if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
                    bytes = loader.LoadAsync(new ResourceRequest(absolute.AbsoluteUri, ResourceKind.Image), CancellationToken.None).GetAwaiter().GetResult()?.Data;
            }
            decoded = bytes is null ? null : Folio.Imaging.ImageLoader.Decode(bytes);
        }
        _images[image] = (source, decoded);
        return decoded;
    }
    public MediaElementState GetMedia(ElementNode element)
    {
        if (_media.TryGetValue(element, out var existing)) return existing;
        var state = new MediaElementState(Engine, Templates.MediaEventTemplate, element, Capabilities.Media, Capabilities.AllowSoundAutoplay, ResourceLoader, BaseUri);
        if (_media.Count >= MaxMediaDecoders)
            state.RejectForBudget("Media decoder limit exceeded.");
        else if (state.SourceBytes > MaxMediaBytes - _mediaBytesAllocated)
            state.RejectForBudget();
        else
            _mediaBytesAllocated += state.SourceBytes;
        _media[element] = state;
        if (element.GetAttribute("autoplay") is not null) state.Play(autoplay: true);
        return state;
    }
    public IframeState GetIframe(ElementNode element)
    {
        if (_iframes.TryGetValue(element, out var state)) return state;
        if (Capabilities.Iframes && _iframeCount >= MaxIframes)
            throw WebIdlException.DomException(DomExceptionNames.QuotaExceededError, "The iframe limit has been exceeded.");
        if (Capabilities.Iframes) _iframeCount++;
        return _iframes[element] = new IframeState(Engine, Templates.MessageEventTemplate, element, Capabilities.Iframes, ResourceLoader, BaseUri, IframeNavigationRequested);
    }

    public ScriptValue Wrap(Node? node)
    {
        if (node is null)
            return ScriptValue.Null;

        if (WrapperCache.TryGet(node, out var existing))
            return existing;

        var template = SelectTemplate(node);
        return WrapperCache.GetOrCreate(Engine, template, node);
    }

    public Node UnwrapNode(ScriptValue value)
    {
        if (Engine.TryGetHostTarget(value, out var target) && target is Node node)
            return node;

        throw WebIdlException.TypeError("Argument is not a Node.");
    }

    public Node? UnwrapNodeOptional(ScriptValue value)
    {
        if (value.IsNullOrUndefined)
            return null;

        return UnwrapNode(value);
    }

    public ScriptValue WrapNodeList(ContainerNode container)
    {
        ArgumentNullException.ThrowIfNull(container);
        return WrapperCache.GetOrCreate(Engine, Templates.NodeListTemplate, new LiveNodeList(container));
    }

    public ScriptValue WrapHtmlCollection(ContainerNode container)
    {
        ArgumentNullException.ThrowIfNull(container);
        return WrapperCache.GetOrCreate(Engine, Templates.HtmlCollectionTemplate, new LiveHtmlCollection(container));
    }

    public ScriptValue WrapClassList(ElementNode element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return WrapperCache.GetOrCreate(Engine, Templates.DomTokenListTemplate, new LiveDomTokenList(element));
    }

    public ScriptValue WrapDataset(ElementNode element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return WrapperCache.GetOrCreate(Engine, Templates.DomStringMapTemplate, new LiveDomStringMap(element));
    }

    public ScriptValue WrapAttributes(ElementNode element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return WrapperCache.GetOrCreate(Engine, Templates.NamedNodeMapTemplate, new LiveNamedNodeMap(element));
    }

    public ScriptValue WrapStyle(ElementNode element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return WrapperCache.GetOrCreate(Engine, Templates.StyleDeclarationTemplate, new LiveStyleDeclaration(element));
    }

    public ScriptValue WrapComputedStyle(ElementNode element) =>
        WrapperCache.GetOrCreate(Engine, Templates.ComputedStyleDeclarationTemplate, new LiveComputedStyle(element));

    public ScriptValue WrapRect(DomRect rect) =>
        WrapperCache.GetOrCreate(Engine, Templates.DomRectTemplate, rect);

    private readonly Dictionary<(ElementNode, string), LiveAttr> _attrCache = new();

    public ScriptValue WrapAttr(ElementNode element, string name)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(name);

        var key = (element, name);
        if (!_attrCache.TryGetValue(key, out var liveAttr) || liveAttr.Element is null)
        {
            liveAttr = new LiveAttr(element, name);
            _attrCache[key] = liveAttr;
        }

        return WrapperCache.GetOrCreate(Engine, Templates.AttrTemplate, liveAttr);
    }

    public IHostObjectTemplate GetHtmlElementTemplate(string tagName)
    {
        var local = tagName.ToLowerInvariant();
        return local switch
        {
            "input" => Templates.HtmlInputElementTemplate,
            "button" => Templates.HtmlButtonElementTemplate,
            "a" => Templates.HtmlAnchorElementTemplate,
            "img" => Templates.HtmlImageElementTemplate,
            "option" => Templates.HtmlOptionElementTemplate,
            "select" => Templates.HtmlSelectElementTemplate,
            "textarea" => Templates.HtmlTextAreaElementTemplate,
            "template" => Templates.HtmlTemplateElementTemplate,
            "canvas" => Templates.HtmlCanvasElementTemplate,
            "video" => Templates.HtmlVideoElementTemplate,
            "audio" => Templates.HtmlAudioElementTemplate,
            "iframe" or "frame" => Templates.HtmlIFrameElementTemplate,
            "script" => Templates.HtmlScriptElementTemplate,
            _ => GetStandardHtmlElementTemplate(local),
        };
    }

    private IHostObjectTemplate GetStandardHtmlElementTemplate(string local)
    {
        var interfaceName = local switch
        {
            "div" => "HTMLDivElement",
            "span" => "HTMLSpanElement",
            "p" => "HTMLParagraphElement",
            "body" => "HTMLBodyElement",
            "head" => "HTMLHeadElement",
            "html" => "HTMLHtmlElement",
            "title" => "HTMLTitleElement",
            "template" => "HTMLTemplateElement",
            "script" => "HTMLScriptElement",
            _ => "HTMLElement",
        };

        if (!_htmlElementTemplates.TryGetValue(interfaceName, out var template))
        {
            template = new HostObjectTemplate(interfaceName, Templates.HtmlElementTemplate);
            _htmlElementTemplates[interfaceName] = template;
        }

        return template;
    }

    private IHostObjectTemplate SelectTemplate(Node node)
    {
        return node switch
        {
            DocumentNode => Templates.DocumentTemplate,
            DocumentType => Templates.DocumentTypeTemplate,
            DocumentFragment => Templates.DocumentFragmentTemplate,
            Text => Templates.TextTemplate,
            Comment => Templates.CommentTemplate,
            ElementNode el when el.Name.Namespace == Namespaces.Html => GetHtmlElementTemplate(el.LocalName),
            ElementNode el when el.Name.Namespace == Namespaces.Svg => Templates.SvgElementTemplate,
            ElementNode => Templates.ElementTemplate,
            _ => Templates.NodeTemplate,
        };
    }

    public ScriptValue WrapStaticNodeList(IReadOnlyList<Node> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        return WrapperCache.GetOrCreate(Engine, Templates.NodeListTemplate, new LiveNodeList(nodes));
    }
}

/// <summary>Represents a NodeList backed by either a ContainerNode (live) or static IReadOnlyList&lt;Node&gt;.</summary>
internal sealed class LiveNodeList
{
    public ContainerNode? Container { get; }
    public IReadOnlyList<Node>? StaticNodes { get; }

    public LiveNodeList(ContainerNode container) => Container = container;
    public LiveNodeList(IReadOnlyList<Node> staticNodes) => StaticNodes = staticNodes;

    public int Count => StaticNodes?.Count ?? Container?.Children.Count() ?? 0;

    public Node? GetItem(int index)
    {
        if (index < 0)
            return null;

        if (StaticNodes is not null)
            return index < StaticNodes.Count ? StaticNodes[index] : null;

        return Container?.Children.ElementAtOrDefault(index);
    }

    public List<Node> ToList()
    {
        if (StaticNodes is not null)
            return StaticNodes.ToList();

        return Container?.Children.ToList() ?? [];
    }
}

/// <summary>Represents a live HTMLCollection backed by a ContainerNode.</summary>
internal sealed class LiveHtmlCollection(ContainerNode container)
{
    public ContainerNode Container { get; } = container;
}
