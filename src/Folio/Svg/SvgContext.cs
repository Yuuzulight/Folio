using System.Numerics;
using Folio.Dom;
using Folio.Style;

namespace Folio.Svg;

/// <summary>
/// What building an SVG render tree needs besides the element: the layout context text is shaped with, the elements
/// <c>url(#id)</c> references point to, and the references being followed, so cycles can be broken.
/// </summary>
internal sealed partial class SvgContext(Layout.LayoutContext layout, DocumentNode document)
{
    public Layout.LayoutContext Layout { get; } = layout;

    /// <summary>The clip paths being built, outermost first: one met again is a reference cycle.</summary>
    public HashSet<ElementNode> Clipping { get; } = [];

    /// <summary>
    /// How many nodes the content of clip paths and masks may add up to in one layout, counted once for every element or
    /// box that references them. A page with thousands of references to one large clip path would otherwise draw its
    /// content thousands of times.
    /// </summary>
    public const int MaxReferencedNodes = 100_000;

    /// <summary>
    /// The content of a clip path or mask element for this viewport, built by <paramref name="build"/> once per layout:
    /// the layout keeps it when building it followed no other reference and no <c>use</c> instance is restyling, so it
    /// cannot depend on where the reference came from. Also whether the layout's budget for referenced content
    /// (<see cref="MaxReferencedNodes"/>) is now spent, in which case the caller uses a simpler stand-in.
    /// </summary>
    public (IReadOnlyList<SvgRenderNode> Nodes, bool OverBudget) ReferencedContent(ElementNode element, Vector2 viewport, Func<List<SvgRenderNode>> build)
    {
        var cacheable = _instances.Count == 0 && Clipping.Count + Masking.Count == 1;
        if (!cacheable || !Layout.SvgContent.TryGetValue((element, viewport), out var content))
        {
            var nodes = build();
            content = (nodes, Count(nodes));
            if (cacheable)
                Layout.SvgContent[(element, viewport)] = content;
        }
        Layout.SvgReferencedNodes += content.Count;
        return (content.Nodes, Layout.SvgReferencedNodes > MaxReferencedNodes);

        static int Count(IEnumerable<SvgRenderNode> nodes)
        {
            var (count, stack) = (0, new Stack<SvgRenderNode>(nodes));
            while (stack.TryPop(out var node))
            {
                count++;
                var children = node switch
                {
                    SvgContainerNode container => container.Children,
                    SvgShapeNode { Markers: { } markers } => markers,
                    _ => [],
                };
                foreach (var child in children)
                    stack.Push(child);
            }
            return count;
        }
    }

    /// <summary>The masks being built: one met again inside its own content is a reference cycle.</summary>
    public HashSet<ElementNode> Masking { get; } = [];

    /// <summary>The markers being built: one met again inside its own content is a reference cycle.</summary>
    public HashSet<ElementNode> Marking { get; } = [];

    /// <summary>The elements <c>use</c> elements are instancing: one met again is a reference cycle.</summary>
    public HashSet<ElementNode> Using { get; } = [];

    /// <summary>
    /// How many elements one render tree may build through <c>use</c> in all, so nested instances that multiply at
    /// each level stop instead of growing exponentially.
    /// </summary>
    public const int MaxInstancedElements = 20_000;

    private int _instanced;

    // The styles of the subtrees being instanced, innermost last: they inherit from their use element.
    private readonly List<Dictionary<ElementNode, ComputedStyle>> _instances = [];

    /// <summary>An element's style: as instanced by the innermost <c>use</c> that holds it, else its own.</summary>
    public ComputedStyle? Style(ElementNode element)
    {
        for (var i = _instances.Count - 1; i >= 0; i--)
        {
            if (_instances[i].TryGetValue(element, out var style))
                return style;
        }
        return element.ComputedStyle();
    }

    /// <summary>
    /// Starts instancing a use element's target: its subtree styled again under the use element's style
    /// (https://www.w3.org/TR/SVG2/struct.html#UseStyleInheritance). False, and nothing started, when the subtree would
    /// go over <see cref="MaxInstancedElements"/> or styles cannot be computed again.
    /// </summary>
    public bool BeginInstance(ElementNode target, ComputedStyle useStyle)
    {
        var styles = new Dictionary<ElementNode, ComputedStyle>();
        var stack = new Stack<(ElementNode Element, ComputedStyle Parent)>([(target, useStyle)]);
        while (stack.TryPop(out var item))
        {
            if (++_instanced > MaxInstancedElements || StyleResolver.Restyle(item.Element, item.Parent) is not { } style)
                return false;
            styles[item.Element] = style;
            for (var child = item.Element.LastChild; child is not null; child = child.PreviousSibling)
            {
                if (child is ElementNode element)
                    stack.Push((element, style));
            }
        }
        _instances.Add(styles);
        Using.Add(target);
        return true;
    }

    /// <summary>Ends the innermost instance begun by <see cref="BeginInstance"/>.</summary>
    public void EndInstance(ElementNode target)
    {
        _instances.RemoveAt(_instances.Count - 1);
        Using.Remove(target);
    }

    /// <summary>
    /// The element a same-document URL reference (<c>#id</c>, or <c>url(#id)</c>'s text) points to: the first one in
    /// tree order with that id. References into other documents are not followed.
    /// </summary>
    public ElementNode? Find(string? reference)
    {
        if (reference is null)
            return null;
        reference = reference.Trim();
        if (reference.Length < 2 || reference[0] != '#')
            return null;
        // The index is built once per document and layout, however many render trees and clipped boxes look into it.
        if (!Layout.Ids.TryGetValue(document, out var ids))
        {
            ids = new Dictionary<string, ElementNode>(StringComparer.Ordinal);
            for (Node? node = document; node is not null; node = node.NextInTree(document))
            {
                if (node is ElementNode element && element.GetAttribute("id") is { Length: > 0 } id)
                    ids.TryAdd(id, element);
            }
            Layout.Ids[document] = ids;
        }
        return ids.GetValueOrDefault(reference[1..]);
    }
}
