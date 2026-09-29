namespace Folio.Dom;

internal readonly record struct QualifiedName(Atom Namespace, Atom LocalName);

/// <param name="Name">The qualified name (<c>xlink:href</c> for a prefixed attribute).</param>
/// <param name="Namespace">None for ordinary attributes; set for the parser's adjusted foreign attributes.</param>
internal readonly record struct Attribute(Atom Name, string Value, Atom Namespace = default);

/// <summary>https://dom.spec.whatwg.org/#interface-element</summary>
internal class Element(Document ownerDocument, QualifiedName name) : ContainerNode(ownerDocument)
{
    private Attribute[] _attributes = [];

    public QualifiedName Name { get; } = name;
    public string LocalName => OwnerDocument.TextOf(Name.LocalName);
    public string NamespaceUri => OwnerDocument.TextOf(Name.Namespace);

    /// <summary>Exact-size, in source order; elements rarely have more than a few.</summary>
    public ReadOnlySpan<Attribute> Attributes => _attributes;

    /// <summary>The <c>id</c> attribute, parsed when set.</summary>
    public Atom Id { get; private set; }

    /// <summary>The <c>class</c> attribute split on ASCII whitespace, parsed when set.</summary>
    public Atom[] Classes { get; private set; } = [];

    public string? GetAttribute(string name)
    {
        var atom = OwnerDocument.Find(name);
        if (atom.IsNone)
            return null;
        foreach (var attribute in _attributes)
        {
            if (attribute.Name == atom)
                return attribute.Value;
        }
        return null;
    }

    /// <summary>https://dom.spec.whatwg.org/#dom-element-setattribute (name used as given; the parser lowercases).</summary>
    public void SetAttribute(string name, string value)
    {
        var atom = OwnerDocument.Intern(name);
        var index = Array.FindIndex(_attributes, a => a.Name == atom);
        if (index >= 0)
        {
            _attributes[index] = new Attribute(atom, value);
        }
        else
        {
            Array.Resize(ref _attributes, _attributes.Length + 1);
            _attributes[^1] = new Attribute(atom, value);
        }

        Reflect(name, value);
        OnMutated(MutationKind.Attributes);
    }

    /// <summary>Sets all attributes of a new element at once (the parser; names are already unique).</summary>
    internal void SetParsedAttributes(Attribute[] attributes)
    {
        _attributes = attributes;
        foreach (var attribute in attributes)
        {
            if (attribute.Namespace.IsNone)
                Reflect(OwnerDocument.TextOf(attribute.Name), attribute.Value);
        }
    }

    private void Reflect(string name, string value)
    {
        switch (name)
        {
            case "id":
                Id = value.Length == 0 ? Atom.None : OwnerDocument.Intern(value);
                break;
            case "class":
                Classes = value.Split(AsciiWhitespace, StringSplitOptions.RemoveEmptyEntries)
                    .Distinct(StringComparer.Ordinal)
                    .Select(OwnerDocument.Intern)
                    .ToArray();
                break;
        }
    }

    // https://infra.spec.whatwg.org/#ascii-whitespace
    internal static readonly char[] AsciiWhitespace = ['\t', '\n', '\f', '\r', ' '];
}

/// <summary>
/// https://html.spec.whatwg.org/multipage/scripting.html#the-template-element: its parsed contents are kept
/// in an inert fragment, outside the tree.
/// </summary>
internal sealed class TemplateElement(Document ownerDocument, QualifiedName name) : Element(ownerDocument, name)
{
    public DocumentFragment Content { get; } = new(ownerDocument);
}
