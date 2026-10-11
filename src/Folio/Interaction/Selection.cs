using Folio.Dom;
using Folio.Interaction;

namespace Folio.Interaction;

/// <summary>
/// A DOM Range (https://dom.spec.whatwg.org/#ranges) boundary point: a node and a child/character offset.
/// </summary>
internal readonly record struct RangePoint(Node Node, int Offset) : IComparable<RangePoint>
{
    public int CompareTo(RangePoint other)
    {
        if (Node == other.Node)
            return Offset.CompareTo(other.Offset);

        // Position in document tree order
        var pos = CompareDocumentPosition(Node, other.Node);
        if ((pos & DocumentPosition.Preceding) != 0)
            return 1;
        if ((pos & DocumentPosition.Following) != 0)
            return -1;
        if ((pos & DocumentPosition.Contains) != 0)
        {
            // Node contains other.Node: other is a descendant.
            // Find which child of Node is an inclusive ancestor of other.Node.
            var childIndex = FindChildIndexContaining(Node, other.Node);
            return Offset <= childIndex ? -1 : 1;
        }
        if ((pos & DocumentPosition.ContainedBy) != 0)
        {
            // Node is contained by other.Node: Node is a descendant.
            var childIndex = FindChildIndexContaining(other.Node, Node);
            return childIndex < other.Offset ? -1 : 1;
        }
        return 0;
    }

    [Flags]
    private enum DocumentPosition
    {
        Disconnected = 1,
        Preceding = 2,
        Following = 4,
        Contains = 8,
        ContainedBy = 16,
    }

    private static DocumentPosition CompareDocumentPosition(Node a, Node b)
    {
        if (a == b)
            return 0;

        // Collect ancestors
        var aChain = new List<Node>();
        for (Node? curr = a; curr is not null; curr = curr.Parent)
            aChain.Add(curr);
        aChain.Reverse();

        var bChain = new List<Node>();
        for (Node? curr = b; curr is not null; curr = curr.Parent)
            bChain.Add(curr);
        bChain.Reverse();

        if (aChain.Count == 0 || bChain.Count == 0 || aChain[0] != bChain[0])
            return DocumentPosition.Disconnected;

        // If one is ancestor of another
        if (a.IsInclusiveAncestorOf(b))
            return DocumentPosition.Contains | DocumentPosition.Preceding;
        if (b.IsInclusiveAncestorOf(a))
            return DocumentPosition.ContainedBy | DocumentPosition.Following;

        // Find least common ancestor
        var minLen = Math.Min(aChain.Count, bChain.Count);
        var diffIdx = 0;
        while (diffIdx < minLen && aChain[diffIdx] == bChain[diffIdx])
            diffIdx++;

        var parent = (ContainerNode)aChain[diffIdx - 1];
        var childA = aChain[diffIdx];
        var childB = bChain[diffIdx];

        for (var c = parent.FirstChild; c is not null; c = c.NextSibling)
        {
            if (c == childA)
                return DocumentPosition.Following;
            if (c == childB)
                return DocumentPosition.Preceding;
        }

        return DocumentPosition.Disconnected;
    }

    private static int FindChildIndexContaining(Node parent, Node descendant)
    {
        if (parent is not ContainerNode container)
            return 0;
        var idx = 0;
        for (var child = container.FirstChild; child is not null; child = child.NextSibling, idx++)
        {
            if (child == descendant || child.IsInclusiveAncestorOf(descendant))
                return idx;
        }
        return idx;
    }
}

/// <summary>
/// A DOM Range (https://dom.spec.whatwg.org/#ranges) representing a contiguous run of content between two points.
/// </summary>
internal sealed class DomRange
{
    public RangePoint Start { get; }
    public RangePoint End { get; }

    public DomRange(RangePoint start, RangePoint end)
    {
        if (start.CompareTo(end) <= 0)
        {
            Start = start;
            End = end;
        }
        else
        {
            Start = end;
            End = start;
        }
    }

    public bool IsCollapsed => Start.Node == End.Node && Start.Offset == End.Offset;

    public override string ToString() => $"Range({Start.Node}:{Start.Offset} -> {End.Node}:{End.Offset})";
}

/// <summary>
/// A document selection (https://www.w3.org/TR/selection-api/) with an anchor (drag start) and focus (drag end).
/// </summary>
internal sealed class Selection
{
    public RangePoint Anchor { get; }
    public RangePoint Focus { get; }

    public Selection(RangePoint anchor, RangePoint focus)
    {
        Anchor = anchor;
        Focus = focus;
    }

    public DomRange Range => new(Anchor, Focus);

    public bool IsCollapsed => Anchor.Node == Focus.Node && Anchor.Offset == Focus.Offset;

    public bool IsBackwards => Anchor.CompareTo(Focus) > 0;
}
