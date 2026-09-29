namespace Folio.Layout;

/// <summary>
/// The input to laying out one box (docs/study/06-layout-block-and-inline.md, option B): the size of its containing
/// block, which percentages resolve against. A null height is indefinite (an auto-height containing block).
/// </summary>
internal readonly record struct ConstraintSpace(float ContainingWidth, float? ContainingHeight);

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
}
