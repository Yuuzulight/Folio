using System.Diagnostics.CodeAnalysis;
using Folio.Dom;

namespace Folio.Scripting.Dom;

/// <summary>
/// Context bridging Folio's DOM nodes with the host script engine and wrapper cache (study 02, study 17, #451).
/// </summary>
internal sealed class DomContext
{
    private readonly Dictionary<string, IHostObjectTemplate> _htmlElementTemplates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ElementNode, (double X, double Y)> _scrollOffsets = new(ReferenceEqualityComparer.Instance);
    private readonly List<MutationObserverState> _mutationObservers = [];
    private readonly Dictionary<ElementNode, CanvasElementState> _canvases = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ElementNode, MediaElementState> _media = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ElementNode, IframeState> _iframes = new(ReferenceEqualityComparer.Instance);
    private long _mediaBytesAllocated;

    public DomContext(IScriptEngine engine, DocumentNode document, ScriptWrapperCache? wrapperCache = null,
        Func<ElementNode, (float X, float Y, float Width, float Height)?>? layout = null,
        FeatureCapabilities? capabilities = null, int maxGpuBytes = 64 * 1024 * 1024, int maxMediaBytes = 128 * 1024 * 1024, int maxWorkerMessageBytes = 1 * 1024 * 1024, int maxWebAssemblyBytes = 64 * 1024 * 1024)
    {
        Engine = engine ?? throw new ArgumentNullException(nameof(engine));
        Document = document ?? throw new ArgumentNullException(nameof(document));
        WrapperCache = wrapperCache ?? new ScriptWrapperCache();
        Layout = layout;
        Capabilities = capabilities ?? FeatureCapabilities.Default;
        MaxGpuBytes = Math.Max(0, maxGpuBytes);
        MaxMediaBytes = Math.Max(0, maxMediaBytes);
        MaxWorkerMessageBytes = Math.Max(0, maxWorkerMessageBytes);
        MaxWebAssemblyBytes = Math.Max(0, maxWebAssemblyBytes);

        Templates = new DomNodeTemplates(this);
    }

    public IScriptEngine Engine { get; }
    public DocumentNode Document { get; }
    public ScriptWrapperCache WrapperCache { get; }
    public Func<ElementNode, (float X, float Y, float Width, float Height)?>? Layout { get; }
    public FeatureCapabilities Capabilities { get; }
    public int MaxGpuBytes { get; }
    public int MaxMediaBytes { get; }
    public int MaxWorkerMessageBytes { get; }
    public int MaxWebAssemblyBytes { get; }
    public DomNodeTemplates Templates { get; }
    public ElementNode? FocusedElement { get; private set; }
    public void Focus(ElementNode element) => FocusedElement = element;
    public void Blur(ElementNode element) { if (ReferenceEquals(FocusedElement, element)) FocusedElement = null; }

    public (double X, double Y) GetScroll(ElementNode element) => _scrollOffsets.TryGetValue(element, out var value) ? value : (0, 0);
    public void SetScroll(ElementNode element, double x, double y) => _scrollOffsets[element] = (Math.Max(0, x), Math.Max(0, y));

    public MutationObserverState CreateMutationObserver(ScriptValue callback)
    {
        if (!callback.IsFunction) throw WebIdlException.TypeError("MutationObserver callback must be callable.");
        var observer = new MutationObserverState(this, callback);
        _mutationObservers.Add(observer);
        return observer;
    }

    public void ObserveMutations(MutationObserverState observer, Node target) => observer.Targets.Add(target);

    public void DisconnectMutations(MutationObserverState observer)
    {
        observer.Targets.Clear();
        observer.Records.Clear();
    }

    public ScriptValue TakeMutationRecords(MutationObserverState observer)
    {
        var records = observer.Records.Select(WrapMutationRecord).ToArray();
        observer.Records.Clear();
        return Engine.CreateArray(records);
    }

    public ScriptValue WrapMutationRecord(MutationRecordState record) => WrapperCache.GetOrCreate(Engine, Templates.MutationRecordTemplate, record);

    public void NotifyChildListMutation(Node target, IReadOnlyList<Node> added, IReadOnlyList<Node> removed)
    {
        foreach (var observer in _mutationObservers.ToArray())
        {
            if (!observer.Targets.Any(observed => ReferenceEquals(observed, target) || observed.IsInclusiveAncestorOf(target))) continue;
            observer.Records.Add(new MutationRecordState(target, added, removed));
            var records = observer.Records.Select(WrapMutationRecord).ToArray();
            observer.Records.Clear();
            Engine.Call(observer.Callback, ScriptValue.Undefined, [Engine.CreateArray(records), Engine.CreateHostObject(Templates.MutationObserverTemplate, observer)]);
        }
    }

    public void NotifyAttributeMutation(ElementNode target, string name, string? oldValue)
    {
        foreach (var observer in _mutationObservers.ToArray())
        {
            if (!observer.Targets.Any(observed => ReferenceEquals(observed, target) || observed.IsInclusiveAncestorOf(target))) continue;
            observer.Records.Add(new MutationRecordState(target, Array.Empty<Node>(), Array.Empty<Node>(), "attributes", name, oldValue));
            var records = observer.Records.Select(WrapMutationRecord).ToArray();
            observer.Records.Clear();
            Engine.Call(observer.Callback, ScriptValue.Undefined, [Engine.CreateArray(records), Engine.CreateHostObject(Templates.MutationObserverTemplate, observer)]);
        }
    }

    public CanvasElementState GetCanvas(ElementNode element) => _canvases.TryGetValue(element, out var state) ? state : (_canvases[element] = new CanvasElementState(element));
    public MediaElementState GetMedia(ElementNode element)
    {
        if (_media.TryGetValue(element, out var existing)) return existing;
        var state = new MediaElementState(Engine, element, Capabilities.Media);
        if (state.SourceBytes > MaxMediaBytes - _mediaBytesAllocated)
            state.RejectForBudget();
        else
            _mediaBytesAllocated += state.SourceBytes;
        _media[element] = state;
        return state;
    }
    public IframeState GetIframe(ElementNode element) => _iframes.TryGetValue(element, out var state) ? state : (_iframes[element] = new IframeState(Engine, Templates.MessageEventTemplate, element, Capabilities.Iframes));

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
