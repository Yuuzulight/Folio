namespace Folio.Dom;

/// <summary>https://dom.spec.whatwg.org/#concept-document-mode</summary>
internal enum DocumentMode
{
    NoQuirks,
    Quirks,
    LimitedQuirks,
}

/// <summary>https://dom.spec.whatwg.org/#interface-document</summary>
internal sealed class DocumentNode : ContainerNode
{
    private readonly AtomTable _atoms;
    private readonly Dictionary<string, Atom> _localIds = new(StringComparer.Ordinal);
    private readonly List<string> _localTexts = [];

    public DocumentNode() : this(AtomTable.Shared)
    {
    }

    internal DocumentNode(AtomTable atoms) : base(null) => _atoms = atoms;

    public ElementNode? DocumentElement => Children.OfType<ElementNode>().FirstOrDefault();

    public DocumentMode Mode { get; set; }

    /// <summary>
    /// Whether this is an SVG document used as an image, which loads nothing from outside
    /// (https://www.w3.org/TR/SVG2/conform.html#secure-static-mode).
    /// </summary>
    internal bool IsImage { get; set; }

    /// <summary>What the style module needs to style elements again later (typed there, so the DOM layer does not reference it).</summary>
    internal object? StyleState { get; set; }

    /// <summary>Set when child elements are inserted or removed, meaning the box tree must be rebuilt.</summary>
    internal bool StructureMutated { get; set; }

    /// <summary>Set when an attribute or text changes, meaning the whole document is styled again (#416).</summary>
    internal bool StyleMutated { get; set; }

    /// <summary>The elements a state change marked (#417): each is styled again with its subtree.</summary>
    internal HashSet<ElementNode> StyleRoots { get; } = [];

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

    public ElementNode CreateElement(Atom ns, string localName) =>
        ns == Namespaces.Html && localName == "template"
            ? new TemplateElement(this, new QualifiedName(ns, Intern(localName)))
            : new ElementNode(this, new QualifiedName(ns, Intern(localName)));

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
                var elements = fragment.Children.Count(n => n is ElementNode);
                if (elements > 1 || fragment.Children.Any(n => n is Text))
                    throw new InvalidOperationException("A document can have only one element and no text.");
                if (elements == 1)
                    EnsureElementAllowed(child);
                break;
            case ElementNode:
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
            if (node is ElementNode)
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
internal sealed class DocumentFragment(DocumentNode ownerDocument) : ContainerNode(ownerDocument);

/// <summary>https://dom.spec.whatwg.org/#interface-documenttype</summary>
internal sealed class DocumentType(DocumentNode ownerDocument, string name, string publicId, string systemId) : Node(ownerDocument)
{
    public string Name { get; } = name;
    public string PublicId { get; } = publicId;
    public string SystemId { get; } = systemId;
}

/// <summary>https://dom.spec.whatwg.org/#interface-characterdata</summary>
internal abstract class CharacterData(DocumentNode ownerDocument, string data) : Node(ownerDocument)
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

internal sealed class Text(DocumentNode ownerDocument, string data) : CharacterData(ownerDocument, data);

internal sealed class Comment(DocumentNode ownerDocument, string data) : CharacterData(ownerDocument, data);
