namespace Folio.Dom;

internal readonly record struct QualifiedName(Atom Namespace, Atom LocalName);

internal readonly record struct Attribute(Atom Name, string Value);

/// <summary>https://dom.spec.whatwg.org/#interface-element</summary>
internal sealed class Element(Document ownerDocument, QualifiedName name) : ContainerNode(ownerDocument)
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

        OnMutated(MutationKind.Attributes);
    }

    // https://infra.spec.whatwg.org/#ascii-whitespace
    internal static readonly char[] AsciiWhitespace = ['\t', '\n', '\f', '\r', ' '];
}
