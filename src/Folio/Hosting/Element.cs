using Folio.Css;
using Folio.Dom;

namespace Folio;

/// <summary>
/// A read-only view of one element of a <see cref="Document"/> (docs/architecture.md, public API sketch). The same
/// element always gives the same <see cref="Element"/>.
/// </summary>
public sealed class Element
{
    private readonly Document _document;

    internal Element(Document document, ElementNode node)
    {
        _document = document;
        Node = node;
    }

    internal ElementNode Node { get; }

    /// <summary>The element's local name, lowercase for HTML elements.</summary>
    public string LocalName => Node.LocalName;

    public string NamespaceUri => Node.NamespaceUri;

    /// <summary>The id attribute, or empty.</summary>
    public string Id => GetAttribute("id") ?? "";

    /// <summary>The text of every descendant text node, in tree order.</summary>
    public string TextContent => Node.TextContent ?? "";

    /// <summary>An attribute's value, or null; names of HTML elements' attributes match ASCII case-insensitively.</summary>
    public string? GetAttribute(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Node.GetAttribute(Node.Name.Namespace == Namespaces.Html ? name.ToLowerInvariant() : name);
    }

    /// <summary>The attributes, in the order they were parsed.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Attributes
    {
        get
        {
            var document = Node.OwnerDocument;
            var list = new List<KeyValuePair<string, string>>(Node.Attributes.Length);
            foreach (var attribute in Node.Attributes)
                list.Add(new(document.TextOf(attribute.Name), attribute.Value));
            return list;
        }
    }

    /// <summary>The parent element, or null for the root element.</summary>
    public Element? Parent => Node.Parent is ElementNode parent ? _document.Wrap(parent) : null;

    /// <summary>The child elements, in order.</summary>
    public IReadOnlyList<Element> Children => Node.Children.OfType<ElementNode>().Select(_document.Wrap).ToList();

    /// <summary>The first descendant matching a selector list (https://dom.spec.whatwg.org/#dom-parentnode-queryselector).</summary>
    /// <exception cref="ArgumentException">The selector list is not valid.</exception>
    public Element? QuerySelector(string selectors) => _document.Query(Node, selectors).FirstOrDefault();

    /// <summary>Every descendant matching a selector list, in tree order.</summary>
    /// <exception cref="ArgumentException">The selector list is not valid.</exception>
    public IReadOnlyList<Element> QuerySelectorAll(string selectors) => _document.Query(Node, selectors).ToList();

    public override string ToString() => $"<{LocalName}>";
}
