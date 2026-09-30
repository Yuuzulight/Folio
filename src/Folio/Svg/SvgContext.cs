using Folio.Dom;

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
