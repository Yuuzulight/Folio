using Folio.Css;
using Folio.Dom;
using Folio.Style;

namespace Folio.Layout;

/// <summary>
/// A box of the box tree (docs/study/05-box-tree.md, option B): built from the DOM and computed styles, read by layout.
/// Anonymous boxes have no node; pseudo-element boxes carry their originating element and the pseudo-element.
/// </summary>
internal abstract class Box(ComputedStyle style, Node? node, PseudoElement pseudoElement = PseudoElement.None)
{
    public ComputedStyle Style { get; } = style;

    /// <summary>The generating element (or, for a pseudo-element, its originating element); null when anonymous.</summary>
    public Node? Node { get; } = node;

    public PseudoElement PseudoElement { get; } = pseudoElement;

    public bool IsAnonymous => Node is null;

    public Box? Parent { get; internal set; }

    public List<Box> Children { get; } = [];

    /// <summary>Filled by layout: cached results for this box.</summary>
    public object? LayoutCache { get; set; }

    /// <summary>Taken out of flow: floated, or absolutely or fixed positioned.</summary>
    public bool IsFloat => Style.Box.Float != FloatSide.None && !IsAbsolutelyPositioned;

    public bool IsAbsolutelyPositioned => Style.Box.Position is Position.Absolute or Position.Fixed;

    /// <summary>
    /// A transform property is set on a transformable box (https://www.w3.org/TR/css-transforms-1/#transformable-element):
    /// not a non-atomic inline box, a marker or a table column. The table grid box shares its wrapper's style and leaves
    /// the transform to the wrapper.
    /// </summary>
    public bool IsTransformed => Style.Transform.IsTransformed
        && this is not (InlineBox or MarkerBox or TablePartBox { Part: TablePart.Table or TablePart.Column or TablePart.ColumnGroup });

    public void Add(Box child)
    {
        child.Parent = this;
        Children.Add(child);
    }
}

/// <summary>A block container: either block-level children or one inline formatting context, never both.</summary>
internal class BlockContainerBox(ComputedStyle style, Node? node, PseudoElement pseudoElement = PseudoElement.None) : Box(style, node, pseudoElement)
{
    public InlineFormattingContext? Inline { get; set; }

    /// <summary>The outside list marker of a list item.</summary>
    public MarkerBox? Marker { get; set; }

    /// <summary>Inline-level from the outside (inline-block): placed as an atomic inline.</summary>
    public bool IsAtomicInline { get; init; }
}

/// <summary>A non-atomic inline box (e.g. span); its content lives in the enclosing inline formatting context.</summary>
internal sealed class InlineBox(ComputedStyle style, Node? node, PseudoElement pseudoElement = PseudoElement.None) : Box(style, node, pseudoElement);

internal enum ReplacedKind
{
    Image,
    Svg,
    Canvas,
    Media,
    Frame,
    FormControl,
}

/// <summary>An atomic replaced element: its content comes from outside CSS (image, SVG, canvas, form control, ...).</summary>
internal sealed class ReplacedBox(ComputedStyle style, Node node, ReplacedKind kind) : Box(style, node)
{
    public ReplacedKind Kind { get; } = kind;
    public bool IsAtomicInline { get; init; }

    /// <summary>For images: the decoded image, or null when it could not be loaded or decoded.</summary>
    public Imaging.DecodedImage? Image { get; init; }

    /// <summary>Image pixels per CSS pixel, from the chosen srcset candidate.</summary>
    public float Density { get; init; } = 1;

    /// <summary>The natural width and height in CSS pixels (https://www.w3.org/TR/css-images-3/#natural-dimensions), if any.</summary>
    public (float Width, float Height)? NaturalSize => Image is { } image ? (image.Width / Density, image.Height / Density) : null;
}

internal sealed class FlexContainerBox(ComputedStyle style, Node? node, PseudoElement pseudoElement = PseudoElement.None) : Box(style, node, pseudoElement)
{
    public bool IsAtomicInline { get; init; }
}

internal sealed class GridContainerBox(ComputedStyle style, Node? node, PseudoElement pseudoElement = PseudoElement.None) : Box(style, node, pseudoElement)
{
    public bool IsAtomicInline { get; init; }
}

/// <summary>The table wrapper (https://www.w3.org/TR/css-tables-3/#table-wrapper-box): captions and the table grid box.</summary>
internal sealed class TableWrapperBox(ComputedStyle style, Node? node) : Box(style, node)
{
    public bool IsAtomicInline { get; init; }
}

internal enum TablePart
{
    Table,
    RowGroup,
    HeaderGroup,
    FooterGroup,
    Row,
    Cell,
    ColumnGroup,
    Column,
    Caption,
}

/// <summary>A table-internal box; cells and captions are block containers.</summary>
internal sealed class TablePartBox(ComputedStyle style, Node? node, TablePart part, PseudoElement pseudoElement = PseudoElement.None) : BlockContainerBox(style, node, pseudoElement)
{
    public TablePart Part { get; } = part;
}

/// <summary>An outside list marker (https://www.w3.org/TR/css-lists-3/#marker-pseudo); inside markers are inline text.</summary>
internal sealed class MarkerBox(ComputedStyle style, Node node, string text) : Box(style, node, PseudoElement.Marker)
{
    public string Text { get; } = text;
}

internal enum InlineItemKind
{
    Text,
    OpenBox,
    CloseBox,
    Atomic,
    ForcedBreak,
    BreakOpportunity,
    Float,
    OutOfFlow,
}

/// <summary>
/// One item of an inline formatting context (study 05, inline content representation). Text items index
/// <see cref="InlineFormattingContext.Text"/>; box items reference their box.
/// </summary>
/// <param name="Continuation">For open/close items of an inline box split by a block: this part is not the box's first/last.</param>
internal readonly record struct InlineItem(InlineItemKind Kind, int Start, int Length, Box? Box, ComputedStyle Style, bool Continuation = false);

/// <summary>A paragraph's inline content: a flat item list over one text buffer (white space already processed, phase I).</summary>
internal sealed class InlineFormattingContext
{
    public string Text { get; set; } = "";
    public List<InlineItem> Items { get; } = [];
}
