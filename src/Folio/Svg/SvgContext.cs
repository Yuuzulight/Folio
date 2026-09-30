using Folio.Dom;
using Folio.Style;

namespace Folio.Svg;

/// <summary>
/// What building an SVG render tree needs besides the element: the layout context text is shaped with, the elements
/// <c>url(#id)</c> references point to, and the references being followed, so cycles can be broken.
/// </summary>
internal sealed partial class SvgContext(Layout.LayoutContext layout, DocumentNode document)
{
    private Dictionary<string, ElementNode>? _ids;

    public Layout.LayoutContext Layout { get; } = layout;

    /// <summary>The clip paths being built, outermost first: one met again is a reference cycle.</summary>
    public HashSet<ElementNode> Clipping { get; } = [];

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
        if (_ids is null)
        {
            _ids = new Dictionary<string, ElementNode>(StringComparer.Ordinal);
            for (Node? node = document; node is not null; node = node.NextInTree(document))
            {
                if (node is ElementNode element && element.GetAttribute("id") is { Length: > 0 } id)
                    _ids.TryAdd(id, element);
            }
        }
        return _ids.GetValueOrDefault(reference[1..]);
    }
}
