using Folio.Typography;

namespace Folio.Layout;

/// <summary>What a fragment is: a box's border box, a line box, or a run of text on a line.</summary>
internal enum FragmentKind
{
    Box,
    Line,
    Text,
}

/// <summary>
/// Glyphs of a shaped run shown by a text fragment; the baseline is <see cref="Ascent"/> below its top. Right-to-left
/// runs keep their glyphs in logical order, to be drawn from the fragment's right edge.
/// </summary>
/// <param name="Replacement">The text shown, when it is not the inline formatting context's own (an inserted ellipsis).</param>
/// <param name="Inline">The inline box the text is directly in, if any.</param>
internal sealed record TextRun(ShapedRun Run, int GlyphStart, int GlyphEnd, float Ascent, bool RightToLeft, Style.ComputedStyle Style,
                               string? Replacement = null, InlineBox? Inline = null, bool Turned = false);

/// <summary>What layout needs besides the box tree: the fonts text is measured with.</summary>
internal sealed class LayoutContext(FontCollection fonts, ITextShaper? shaper = null)
{
    public FontCollection Fonts { get; } = fonts;

    /// <summary>Shapes the runs SimpleShaper cannot (complex scripts, marks); without one, SimpleShaper does all.</summary>
    public ITextShaper? Shaper { get; } = shaper;

    /// <summary>Min-content and max-content widths computed so far (see <see cref="IntrinsicSizes"/>).</summary>
    public Dictionary<Box, (float Min, float Max)> Intrinsic { get; } = [];
}

/// <summary>
/// The input to laying out one box (docs/study/06-layout-block-and-inline.md, option B): the size of its containing
/// block, which percentages resolve against (a null height is indefinite: an auto-height containing block), and, for a
/// box that joins its parent's block formatting context, the floats in it and where the box sits in it.
/// </summary>
/// <param name="Exclusions">The floats placed so far in the block formatting context; null when there are none.</param>
/// <param name="BfcLeft">The left edge of the containing block's content box, in formatting context coordinates.</param>
/// <param name="BfcTop">The top of the box's border box, in formatting context coordinates.</param>
/// <param name="FixedWidth">A border-box width already decided by the parent's algorithm (absolute positioning).</param>
/// <param name="FixedHeight">A border-box height already decided by the parent's algorithm.</param>
/// <param name="Border">Border widths decided by the parent's algorithm (collapsed table borders), instead of the style's.</param>
internal readonly record struct ConstraintSpace(
    float ContainingWidth, float? ContainingHeight, ExclusionSpace? Exclusions = null, float BfcLeft = 0, float BfcTop = 0,
    float? FixedWidth = null, float? FixedHeight = null, Style.BorderGroup? Border = null);

/// <summary>
/// An absolutely or fixed positioned box on its way up to its containing block (study 10, option A), with its static
/// position: where its margin box would start if it were in flow, from the carrying fragment's border-box origin.
/// </summary>
internal readonly record struct OutOfFlowBox(Box Box, float StaticX, float StaticY);

/// <summary>
/// A set of adjoining margins (https://www.w3.org/TR/CSS22/box.html#collapsing-margins): they collapse to the largest
/// positive one plus the most negative one.
/// </summary>
internal readonly record struct MarginStrut(float Positive, float Negative)
{
    public static MarginStrut Of(float margin) => margin >= 0 ? new(margin, 0) : new(0, margin);

    public MarginStrut Append(MarginStrut other) => new(Math.Max(Positive, other.Positive), Math.Min(Negative, other.Negative));

    public float Resolve() => Positive + Negative;
}

/// <summary>A child fragment at an offset from its parent fragment's border-box origin.</summary>
internal readonly record struct ChildFragment(float X, float Y, Fragment Fragment);

/// <summary>
/// The immutable result of laying out a box: its border-box size and positioned children, plus what the parent's
/// block layout needs to place it (used horizontal margins and the margins that collapse through its edges).
/// </summary>
internal sealed class Fragment(Box? box, float width, float height, IReadOnlyList<ChildFragment> children)
{
    /// <summary>The box laid out; null for the initial containing block.</summary>
    public Box? Box { get; } = box;

    public float Width { get; } = width;
    public float Height { get; } = height;
    public IReadOnlyList<ChildFragment> Children { get; } = children;

    public float MarginLeft { get; init; }
    public float MarginRight { get; init; }

    /// <summary>The box's top margin collapsed with any descendant margins adjoining it.</summary>
    public MarginStrut TopMargins { get; init; }

    /// <summary>The box's bottom margin collapsed with any descendant margins adjoining it.</summary>
    public MarginStrut BottomMargins { get; init; }

    /// <summary>
    /// Its top and bottom margins adjoin (an empty block): <see cref="TopMargins"/> then holds all of them and they
    /// collapse with the siblings' on both sides.
    /// </summary>
    public bool CollapsesThrough { get; init; }

    /// <summary>
    /// The floats in the enclosing block formatting context after this box, including any it placed; for a box with
    /// an independent formatting context, the ones it was given.
    /// </summary>
    public ExclusionSpace? Exclusions { get; init; }

    public FragmentKind Kind { get; init; } = FragmentKind.Box;

    /// <summary>
    /// The border painted instead of the style's: for collapsed table borders, a cell's full resolved border centred on
    /// its edges, and no border for the table, its rows and row groups.
    /// </summary>
    public Style.BorderGroup? PaintedBorder { get; init; }

    /// <summary>No background or border is painted (an empty cell with empty-cells: hide).</summary>
    public bool SkipsDecorations { get; init; }

    /// <summary>For text fragments: the glyphs.</summary>
    public TextRun? Text { get; init; }

    /// <summary>For line boxes: the baseline, from the top of the line.</summary>
    public float Baseline { get; init; }

    /// <summary>For an outermost svg element: what it draws, in its content box's coordinates; null when nothing shows.</summary>
    public Svg.SvgContainerNode? Svg { get; init; }

    /// <summary>Positioned descendants whose containing block is further up.</summary>
    public IReadOnlyList<OutOfFlowBox> OutOfFlow { get; init; } = [];
}
