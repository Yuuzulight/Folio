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
                               string? Replacement = null, InlineBox? Inline = null, bool Turned = false)
{
    /// <summary>
    /// In vertical text: how far the alphabetic baseline is from the central one, towards the under side, by the metrics
    /// of the style's first available font (half its ascent minus its descent).
    /// </summary>
    public float Central { get; init; }
}

/// <summary>What layout needs besides the box tree: the fonts text is measured with.</summary>
internal sealed class LayoutContext(FontCollection fonts, ITextShaper? shaper = null)
{
    public FontCollection Fonts { get; } = fonts;

    /// <summary>Shapes the runs SimpleShaper cannot (complex scripts, marks); without one, SimpleShaper does all.</summary>
    public ITextShaper? Shaper { get; } = shaper;

    /// <summary>Min-content and max-content widths computed so far (see <see cref="IntrinsicSizes"/>).</summary>
    public Dictionary<Box, (float Min, float Max)> Intrinsic { get; } = [];

    /// <summary>Where images the layout refers to load from (SVG feImage); none means they do not load.</summary>
    public Imaging.ImageLoader? Images { get; init; }

    /// <summary>Where images in SVG images load from: data: URLs only, since an SVG image loads nothing from outside.</summary>
    public Imaging.ImageLoader DataUrlImages => _dataUrlImages ??= new(Resources.ResourceLoader.DataUrlsOnly, null);

    private Imaging.ImageLoader? _dataUrlImages;

    /// <summary>Grid items laid out so far, by the space they were laid out in and, for subgrids, the tracks lent to them (see GridLayout).</summary>
    public Dictionary<(Box Box, ConstraintSpace Space, (AdoptedTracks? Columns, AdoptedTracks? Rows) Lent), Fragment> GridItems { get; } = [];

    /// <summary>
    /// The tracks a grid lends each of its subgrids, per axis, once it has sized them (see GridLayout): a subgrid laid
    /// out without an entry for an axis has no tracks there, as with none.
    /// </summary>
    public Dictionary<Box, (AdoptedTracks? Columns, AdoptedTracks? Rows)> Subgrids { get; } = [];

    /// <summary>
    /// The content of clip path and mask elements built so far, by element and the viewport it was built for, with its
    /// node count (see Svg.SvgContext.ReferencedContent).
    /// </summary>
    public Dictionary<(Dom.ElementNode Element, System.Numerics.Vector2 Viewport), (IReadOnlyList<Svg.SvgRenderNode> Nodes, int Count)> SvgContent { get; } = [];

    /// <summary>How many nodes of clip path and mask content the references laid out so far have used.</summary>
    public int SvgReferencedNodes { get; set; }

    /// <summary>The SVG filter elements in use, each once per bounding box and viewport (see Svg.SvgFilterReference).</summary>
    public Dictionary<(Dom.ElementNode Element, Svg.SvgRect? Bounds, System.Numerics.Vector2 Viewport), Svg.SvgFilterReference> SvgFilters { get; } = [];

    /// <summary>Each document's elements by id, first in tree order, for url(#id) references (see Svg.SvgContext.Find).</summary>
    public Dictionary<Dom.DocumentNode, Dictionary<string, Dom.ElementNode>> Ids { get; } = [];
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
/// <param name="AnnotationRoom">
/// The free space above the box's border box that ruby annotations on its first line may reach into: the margins above
/// it and the space below the previous content's last line.
/// </param>
internal readonly record struct ConstraintSpace(
    float ContainingWidth, float? ContainingHeight, ExclusionSpace? Exclusions = null, float BfcLeft = 0, float BfcTop = 0,
    float? FixedWidth = null, float? FixedHeight = null, Style.BorderGroup? Border = null, float AnnotationRoom = 0);

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

/// <summary>A column rule's rectangle, from its multi-column container's border-box origin.</summary>
internal readonly record struct ColumnRule(float X, float Y, float Width, float Height);

/// <summary>A child fragment at an offset from its parent fragment's border-box origin.</summary>
internal readonly record struct ChildFragment(float X, float Y, Fragment Fragment);

/// <summary>
/// A fragment's children: a slice of an array, during a layout one of the large buffers of its <see cref="FragmentArena"/>
/// (#397). Lists and arrays convert to it, and collection expressions build it; LINQ sees it as a read-only list.
/// </summary>
[System.Runtime.CompilerServices.CollectionBuilder(typeof(FragmentArena), nameof(FragmentArena.Create))]
internal readonly struct ChildList : IReadOnlyList<ChildFragment>
{
    private readonly ChildFragment[]? _items;
    private readonly int _start;

    public ChildList(ChildFragment[] items, int start, int count) => (_items, _start, Count) = (items, start, count);

    public int Count { get; }

    public ChildFragment this[int index] => (uint)index < (uint)Count ? _items![_start + index] : throw new ArgumentOutOfRangeException(nameof(index));

    public ReadOnlySpan<ChildFragment> AsSpan() => _items is null ? default : _items.AsSpan(_start, Count);

    /// <summary>The children from <paramref name="start"/> on.</summary>
    public ChildList Slice(int start) =>
        (uint)start <= (uint)Count ? new ChildList(_items ?? [], _start + start, Count - start) : throw new ArgumentOutOfRangeException(nameof(start));

    public Enumerator GetEnumerator() => new(_items, _start, _start + Count);

    IEnumerator<ChildFragment> IEnumerable<ChildFragment>.GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
            yield return this[i];
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => ((IEnumerable<ChildFragment>)this).GetEnumerator();

    public static implicit operator ChildList(List<ChildFragment> list) => FragmentArena.Create(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list));

    public static implicit operator ChildList(ChildFragment[] array) => FragmentArena.Store(array);

    public struct Enumerator(ChildFragment[]? items, int start, int end)
    {
        private int _index = start - 1;

        public bool MoveNext() => ++_index < end;

        public readonly ChildFragment Current => items![_index];
    }
}

/// <summary>
/// The large buffers a layout keeps its fragments' children in (#397): a few arrays for the whole tree rather than one
/// per fragment, so collections during and after layout have far fewer objects to copy. <see cref="LayoutEngine"/>
/// opens one per layout on its thread; children made outside a layout get arrays of their own.
/// </summary>
internal sealed class FragmentArena
{
    [ThreadStatic]
    private static FragmentArena? t_current;

    // Large enough to live on the large object heap, where collections do not copy it.
    private const int ChunkSize = 16_384;

    private ChildFragment[] _chunk = [];
    private int _used;

    /// <summary>Makes a new arena current on this thread until the returned scope is disposed.</summary>
    public static Scope Open()
    {
        var previous = t_current;
        t_current = new FragmentArena();
        return new Scope(previous);
    }

    public readonly struct Scope(FragmentArena? previous) : IDisposable
    {
        public void Dispose() => t_current = previous;
    }

    /// <summary>Children copied into the current arena, or into an array of their own outside a layout.</summary>
    public static ChildList Create(ReadOnlySpan<ChildFragment> children) =>
        children.IsEmpty ? default : t_current is { } arena ? arena.Copy(children) : new ChildList(children.ToArray(), 0, children.Length);

    /// <summary>Like <see cref="Create"/>, but outside a layout the array itself is kept.</summary>
    public static ChildList Store(ChildFragment[] children) =>
        children.Length == 0 ? default : t_current is { } arena ? arena.Copy(children) : new ChildList(children, 0, children.Length);

    private ChildList Copy(ReadOnlySpan<ChildFragment> children)
    {
        // A long list gets an array of its own rather than leaving most of a buffer unused.
        if (children.Length > ChunkSize / 8)
            return new ChildList(children.ToArray(), 0, children.Length);
        if (_used + children.Length > _chunk.Length)
            (_chunk, _used) = (new ChildFragment[ChunkSize], 0);
        children.CopyTo(_chunk.AsSpan(_used));
        var list = new ChildList(_chunk, _used, children.Length);
        _used += children.Length;
        return list;
    }
}

/// <summary>
/// The immutable result of laying out a box: its border-box size and positioned children, plus what the parent's
/// block layout needs to place it (used horizontal margins and the margins that collapse through its edges).
/// </summary>
internal sealed class Fragment(Box? box, float width, float height, ChildList children)
{
    /// <summary>The box laid out; null for the initial containing block.</summary>
    public Box? Box { get; } = box;

    public float Width { get; } = width;
    public float Height { get; } = height;
    public ChildList Children { get; } = children;

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

    /// <summary>
    /// For a ruby column (and its baseline in <see cref="Baseline"/>): how far its annotation's em box reaches above its
    /// top, and how far the annotation may overhang the text on either side.
    /// </summary>
    public float RubyOver
    {
        get => _rare?.RubyOver ?? float.NegativeInfinity;
        init { if (value != float.NegativeInfinity || _rare is not null) _rare = (_rare ?? Rare.None) with { RubyOver = value }; }
    }

    public float RubyOverhang { get => _rare?.RubyOverhang ?? 0; init { if (value != 0 || _rare is not null) _rare = (_rare ?? Rare.None) with { RubyOverhang = value }; } }

    /// <summary>For an outermost svg element: what it draws, in its content box's coordinates; null when nothing shows.</summary>
    public Svg.SvgContainerNode? Svg { get => _rare?.Svg; init { if (value is not null || _rare is not null) _rare = (_rare ?? Rare.None) with { Svg = value }; } }

    /// <summary>
    /// For a box whose clip-path is a url() reference to an SVG clipPath element: that clip path, in coordinates whose
    /// origin is the border box's top-left corner; null otherwise, and then a reference clips nothing.
    /// </summary>
    public Svg.SvgClipPath? SvgClip { get => _rare?.SvgClip; init { if (value is not null || _rare is not null) _rare = (_rare ?? Rare.None) with { SvgClip = value }; } }

    /// <summary>
    /// For a masked box with layers that reference SVG mask elements: each layer's mask, by layer index (null for the
    /// others), in coordinates whose origin is the border box's top-left corner; null when no layer does.
    /// </summary>
    public IReadOnlyList<Svg.SvgMask?>? SvgMasks { get => _rare?.SvgMasks; init { if (value is not null || _rare is not null) _rare = (_rare ?? Rare.None) with { SvgMasks = value }; } }

    /// <summary>
    /// For a box whose filter list references SVG filter elements: the list with its references resolved, in
    /// coordinates whose origin is the border box's top-left corner; null when it filters nothing.
    /// </summary>
    public Svg.SvgFilterChain? SvgFilters { get => _rare?.SvgFilters; init { if (value is not null || _rare is not null) _rare = (_rare ?? Rare.None) with { SvgFilters = value }; } }

    /// <summary>For a multi-column container: the column rules, as rectangles from its border-box origin.</summary>
    public IReadOnlyList<ColumnRule>? ColumnRules { get => _rare?.ColumnRules; init { if (value is not null || _rare is not null) _rare = (_rare ?? Rare.None) with { ColumnRules = value }; } }

    /// <summary>Positioned descendants whose containing block is further up.</summary>
    public IReadOnlyList<OutOfFlowBox> OutOfFlow { get; init; } = [];

    // What only ruby columns, SVG and multi-column containers have, kept apart: a large document has tens of thousands of
    // fragments, and most would otherwise carry seven empty fields (#196). Made only when one is set to something.
    private Rare? _rare;

    private sealed record Rare(float RubyOver = float.NegativeInfinity, float RubyOverhang = 0, Svg.SvgContainerNode? Svg = null,
                               Svg.SvgClipPath? SvgClip = null, IReadOnlyList<Svg.SvgMask?>? SvgMasks = null,
                               Svg.SvgFilterChain? SvgFilters = null, IReadOnlyList<ColumnRule>? ColumnRules = null)
    {
        public static readonly Rare None = new();
    }
}
