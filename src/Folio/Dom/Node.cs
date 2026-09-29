namespace Folio.Dom;

[Flags]
internal enum NodeFlags : byte
{
    None = 0,
    NeedsStyle = 1 << 0,
    DescendantNeedsStyle = 1 << 1,
}

internal enum MutationKind
{
    ChildList,
    Attributes,
    CharacterData,
}

/// <summary>A DOM node (https://dom.spec.whatwg.org/#interface-node) linked to its parent and siblings.</summary>
internal abstract class Node
{
    protected Node(DocumentNode? ownerDocument) => OwnerDocument = ownerDocument ?? (DocumentNode)this;

    public DocumentNode OwnerDocument { get; }
    public ContainerNode? Parent { get; internal set; }
    public Node? PreviousSibling { get; internal set; }
    public Node? NextSibling { get; internal set; }
    public NodeFlags Flags { get; set; }

    /// <summary>https://dom.spec.whatwg.org/#dom-node-textcontent (getter).</summary>
    public virtual string? TextContent => null;

    /// <summary>The node after this one in tree order, staying inside <paramref name="root"/>.</summary>
    public Node? NextInTree(Node root)
    {
        if (this is ContainerNode { FirstChild: { } child })
            return child;
        for (Node? node = this; node is not null && node != root; node = node.Parent)
        {
            if (node.NextSibling is { } next)
                return next;
        }
        return null;
    }

    public bool IsInclusiveAncestorOf(Node node)
    {
        for (Node? n = node; n is not null; n = n.Parent)
        {
            if (n == this)
                return true;
        }
        return false;
    }

    /// <summary>
    /// The single hook every mutation calls (docs/study/02-dom.md): marks the affected element for restyle and its
    /// ancestors as having a dirty descendant.
    /// </summary>
    internal void OnMutated(MutationKind kind)
    {
        Node? target = kind == MutationKind.CharacterData ? Parent : this;
        if (target is null)
            return;

        target.Flags |= NodeFlags.NeedsStyle;
        for (var ancestor = target.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if ((ancestor.Flags & NodeFlags.DescendantNeedsStyle) != 0)
                break;
            ancestor.Flags |= NodeFlags.DescendantNeedsStyle;
        }
    }
}

/// <summary>A node that can have children: <see cref="DocumentNode"/>, <see cref="DocumentFragment"/>, <see cref="Element"/>.</summary>
internal abstract class ContainerNode(DocumentNode? ownerDocument) : Node(ownerDocument)
{
    public Node? FirstChild { get; private set; }
    public Node? LastChild { get; private set; }

    public IEnumerable<Node> Children
    {
        get
        {
            for (var child = FirstChild; child is not null; child = child.NextSibling)
                yield return child;
        }
    }

    public override string? TextContent
    {
        get
        {
            var text = new System.Text.StringBuilder();
            for (var node = FirstChild; node is not null; node = node.NextInTree(this))
            {
                if (node is Text t)
                    text.Append(t.Data);
            }
            return text.ToString();
        }
    }

    public T AppendChild<T>(T node) where T : Node => InsertBefore(node, null);

    /// <summary>https://dom.spec.whatwg.org/#concept-node-pre-insert</summary>
    public T InsertBefore<T>(T node, Node? child) where T : Node
    {
        EnsurePreInsertionValidity(node, child);

        if (child == node)
            child = node.NextSibling;

        if (node is DocumentFragment fragment)
        {
            while (fragment.FirstChild is { } moved)
            {
                fragment.Unlink(moved);
                Link(moved, child);
            }
            fragment.OnMutated(MutationKind.ChildList);
        }
        else
        {
            if (node.Parent is { } oldParent)
            {
                oldParent.Unlink(node);
                oldParent.OnMutated(MutationKind.ChildList);
            }
            Link(node, child);
        }

        OnMutated(MutationKind.ChildList);
        return node;
    }

    /// <summary>https://dom.spec.whatwg.org/#concept-node-pre-remove</summary>
    public T RemoveChild<T>(T child) where T : Node
    {
        if (child.Parent != this)
            throw new ArgumentException("The node is not a child of this node.", nameof(child));

        Unlink(child);
        OnMutated(MutationKind.ChildList);
        return child;
    }

    // https://dom.spec.whatwg.org/#concept-node-ensure-pre-insertion-validity
    private void EnsurePreInsertionValidity(Node node, Node? child)
    {
        if (node.OwnerDocument != OwnerDocument)
            throw new ArgumentException("The node belongs to another document.", nameof(node));
        if (node is DocumentNode)
            throw new InvalidOperationException("A document cannot be inserted.");
        if (node.IsInclusiveAncestorOf(this))
            throw new InvalidOperationException("A node cannot be inserted into itself or its descendants.");
        if (child is not null && child.Parent != this)
            throw new ArgumentException("The reference node is not a child of this node.", nameof(child));
        if (node is DocumentType && this is not DocumentNode)
            throw new InvalidOperationException("A doctype can only be a child of a document.");
        if (this is DocumentNode document)
            document.EnsureChildAllowed(node, child);
    }

    private void Link(Node node, Node? before)
    {
        node.Parent = this;
        node.NextSibling = before;
        node.PreviousSibling = before is null ? LastChild : before.PreviousSibling;

        if (node.PreviousSibling is { } previous)
            previous.NextSibling = node;
        else
            FirstChild = node;

        if (before is not null)
            before.PreviousSibling = node;
        else
            LastChild = node;
    }

    private void Unlink(Node node)
    {
        if (node.PreviousSibling is { } previous)
            previous.NextSibling = node.NextSibling;
        else
            FirstChild = node.NextSibling;

        if (node.NextSibling is { } next)
            next.PreviousSibling = node.PreviousSibling;
        else
            LastChild = node.PreviousSibling;

        node.Parent = null;
        node.PreviousSibling = null;
        node.NextSibling = null;
    }
}
