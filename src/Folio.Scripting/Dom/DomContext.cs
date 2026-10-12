using System.Diagnostics.CodeAnalysis;
using Folio.Dom;

namespace Folio.Scripting.Dom;

/// <summary>
/// Context bridging Folio's DOM nodes with the host script engine and wrapper cache (study 02, study 17, #451).
/// </summary>
internal sealed class DomContext
{
    private readonly Dictionary<string, IHostObjectTemplate> _htmlElementTemplates = new(StringComparer.OrdinalIgnoreCase);

    public DomContext(IScriptEngine engine, DocumentNode document, ScriptWrapperCache? wrapperCache = null)
    {
        Engine = engine ?? throw new ArgumentNullException(nameof(engine));
        Document = document ?? throw new ArgumentNullException(nameof(document));
        WrapperCache = wrapperCache ?? new ScriptWrapperCache();

        Templates = new DomNodeTemplates(this);
    }

    public IScriptEngine Engine { get; }
    public DocumentNode Document { get; }
    public ScriptWrapperCache WrapperCache { get; }
    public DomNodeTemplates Templates { get; }

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
