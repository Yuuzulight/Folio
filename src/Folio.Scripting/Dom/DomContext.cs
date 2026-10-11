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

    public IHostObjectTemplate GetHtmlElementTemplate(string tagName)
    {
        var local = tagName.ToLowerInvariant();
        var interfaceName = local switch
        {
            "div" => "HTMLDivElement",
            "span" => "HTMLSpanElement",
            "p" => "HTMLParagraphElement",
            "a" => "HTMLAnchorElement",
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
}

/// <summary>Represents a live NodeList backed by a ContainerNode.</summary>
internal sealed class LiveNodeList(ContainerNode container)
{
    public ContainerNode Container { get; } = container;
}

/// <summary>Represents a live HTMLCollection backed by a ContainerNode.</summary>
internal sealed class LiveHtmlCollection(ContainerNode container)
{
    public ContainerNode Container { get; } = container;
}
