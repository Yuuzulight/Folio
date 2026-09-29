namespace Folio.Dom;

/// <summary>https://dom.spec.whatwg.org/#concept-document-mode</summary>
internal enum DocumentMode
{
    NoQuirks,
    Quirks,
    LimitedQuirks,
}

/// <summary>https://dom.spec.whatwg.org/#interface-document</summary>
internal sealed class Document : ContainerNode
{
    private readonly AtomTable _atoms;
    private readonly Dictionary<string, Atom> _localIds = new(StringComparer.Ordinal);
    private readonly List<string> _localTexts = [];

    public Document() : this(AtomTable.Shared)
    {
    }

    internal Document(AtomTable atoms) : base(null) => _atoms = atoms;

    public Element? DocumentElement => Children.OfType<Element>().FirstOrDefault();

    public DocumentMode Mode { get; set; }

    public override string? TextContent => null;

    /// <summary>Interns in the shared table, or in this document once the shared table is full.</summary>
    public Atom Intern(string text)
    {
        if (_atoms.TryIntern(text, out var atom))
            return atom;
        if (_localIds.TryGetValue(text, out atom))
            return atom;

        _localTexts.Add(text);
        atom = new Atom(-_localTexts.Count);
        _localIds.Add(text, atom);
        return atom;
    }

    /// <summary>The atom for an already interned string, or <see cref="Atom.None"/>. Never adds.</summary>
    public Atom Find(string text)
    {
        var atom = _atoms.Find(text);
        return atom.IsNone ? _localIds.GetValueOrDefault(text) : atom;
    }

    public string TextOf(Atom atom) => atom.Value >= 0 ? _atoms.Text(atom) : _localTexts[-atom.Value - 1];

    public Element CreateElement(Atom ns, string localName) =>
        ns == Namespaces.Html && localName == "template"
            ? new TemplateElement(this, new QualifiedName(ns, Intern(localName)))
            : new Element(this, new QualifiedName(ns, Intern(localName)));

    public Text CreateText(string data) => new(this, data);

    public Comment CreateComment(string data) => new(this, data);

    public DocumentType CreateDocumentType(string name, string publicId, string systemId) => new(this, name, publicId, systemId);

    public DocumentFragment CreateFragment() => new(this);

    // The document rules of https://dom.spec.whatwg.org/#concept-node-ensure-pre-insertion-validity (step 6).
    internal void EnsureChildAllowed(Node node, Node? child)
    {
        switch (node)
        {
            case Text:
                throw new InvalidOperationException("Text cannot be a child of a document.");
            case DocumentFragment fragment:
                var elements = fragment.Children.Count(n => n is Element);
                if (elements > 1 || fragment.Children.Any(n => n is Text))
                    throw new InvalidOperationException("A document can have only one element and no text.");
                if (elements == 1)
                    EnsureElementAllowed(child);
                break;
            case Element:
                EnsureElementAllowed(child);
                break;
            case DocumentType:
                if (Children.Any(n => n is DocumentType) || ElementBefore(child))
                    throw new InvalidOperationException("A document can have only one doctype, before its element.");
                break;
        }
    }

    private void EnsureElementAllowed(Node? child)
    {
        if (DocumentElement is not null || (child is not null && DoctypeAtOrAfter(child)))
            throw new InvalidOperationException("A document can have only one element, after its doctype.");
    }

    private bool ElementBefore(Node? child)
    {
        for (var node = child is null ? LastChild : child.PreviousSibling; node is not null; node = node.PreviousSibling)
        {
            if (node is Element)
                return true;
        }
        return false;
    }

    private static bool DoctypeAtOrAfter(Node child)
    {
        for (Node? node = child; node is not null; node = node.NextSibling)
        {
            if (node is DocumentType)
                return true;
        }
        return false;
    }
}

/// <summary>https://dom.spec.whatwg.org/#interface-documentfragment</summary>
internal sealed class DocumentFragment(Document ownerDocument) : ContainerNode(ownerDocument);

/// <summary>https://dom.spec.whatwg.org/#interface-documenttype</summary>
internal sealed class DocumentType(Document ownerDocument, string name, string publicId, string systemId) : Node(ownerDocument)
{
    public string Name { get; } = name;
    public string PublicId { get; } = publicId;
    public string SystemId { get; } = systemId;
}

/// <summary>https://dom.spec.whatwg.org/#interface-characterdata</summary>
internal abstract class CharacterData(Document ownerDocument, string data) : Node(ownerDocument)
{
    public string Data
    {
        get;
        set
        {
            field = value;
            OnMutated(MutationKind.CharacterData);
        }
    } = data;

    public override string? TextContent => Data;
}

internal sealed class Text(Document ownerDocument, string data) : CharacterData(ownerDocument, data);

internal sealed class Comment(Document ownerDocument, string data) : CharacterData(ownerDocument, data);
