using System.Collections.Immutable;
using Folio.Style;

namespace Folio.Layout;

/// <summary>A float's margin box in its block formatting context's coordinates.</summary>
internal readonly record struct FloatArea(FloatSide Side, float Left, float Top, float Right, float Bottom);

/// <summary>
/// The floats placed so far in one block formatting context (study 06's exclusion space), immutable so a fragment can
/// hand the floats after it to its next sibling. Sides are physical: left and right.
/// </summary>
internal sealed class ExclusionSpace
{
    private readonly ImmutableArray<FloatArea> _floats;

    private ExclusionSpace(ImmutableArray<FloatArea> floats) => _floats = floats;

    public static ExclusionSpace Empty { get; } = new([]);

    public int Count => _floats.Length;

    public bool IsEmpty => _floats.IsEmpty;

    public ExclusionSpace Add(FloatArea area) => new(_floats.Add(area));

    /// <summary>Moves the floats added after the first <paramref name="from"/> down by <paramref name="dy"/>.</summary>
    public ExclusionSpace Translate(int from, float dy) => dy == 0 || from >= Count ? this
        : new(_floats.Select((f, i) => i < from ? f : f with { Top = f.Top + dy, Bottom = f.Bottom + dy }).ToImmutableArray());

    /// <summary>The lowest float bottom (0 when there are none), for heights that contain floats.</summary>
    public float Bottom => _floats.IsEmpty ? 0 : _floats.Max(f => f.Bottom);

    /// <summary>The top of the last float placed: a later float may not be placed higher (CSS 2.2 §9.5.1 rule 5).</summary>
    public float LastTop => _floats.IsEmpty ? float.NegativeInfinity : _floats[^1].Top;

    /// <summary>The clear edge for a <c>clear</c> value: the lowest bottom of the floats it clears, or null.</summary>
    public float? ClearEdge(Clear clear)
    {
        if (clear == Clear.None)
            return null;
        float? edge = null;
        foreach (var f in _floats)
        {
            if (clear == Clear.Both || (clear is Clear.Left or Clear.InlineStart) == (f.Side == FloatSide.Left))
                edge = Math.Max(edge ?? float.NegativeInfinity, f.Bottom);
        }
        return edge;
    }

    /// <summary>The part of [left, right] not covered by floats that overlap the band from top to bottom.</summary>
    public (float Left, float Right) Available(float top, float bottom, float left, float right)
    {
        foreach (var f in _floats)
        {
            if (!Overlaps(f, top, bottom))
                continue;
            if (f.Side == FloatSide.Left)
                left = Math.Max(left, f.Right);
            else
                right = Math.Min(right, f.Left);
        }
        return (left, right);
    }

    /// <summary>The first float bottom below <paramref name="top"/> among floats overlapping the band, or null.</summary>
    public float? NextBottom(float top, float bottom)
    {
        float? next = null;
        foreach (var f in _floats)
        {
            if (Overlaps(f, top, bottom) && f.Bottom > top)
                next = Math.Min(next ?? float.PositiveInfinity, f.Bottom);
        }
        return next;
    }

    /// <summary>
    /// Where a float's margin box goes (CSS 2.2 §9.5.1): as high as possible but not above <paramref name="minTop"/>,
    /// then as far left (or right) as possible; it moves down past floats until it fits beside them, or until no
    /// float is beside it.
    /// </summary>
    public (float X, float Y) PlaceFloat(FloatSide side, float width, float height, float minTop, float left, float right)
    {
        var y = Math.Max(minTop, LastTop);
        while (true)
        {
            var (l, r) = Available(y, y + height, left, right);
            if (r - l >= width || NextBottom(y, y + height) is not { } next)
                return (side == FloatSide.Left ? l : r - width, y);
            y = next;
        }
    }

    // A zero-height band is a line: it overlaps floats that contain it.
    private static bool Overlaps(FloatArea f, float top, float bottom) =>
        bottom > top ? f.Top < bottom && f.Bottom > top : f.Top <= top && f.Bottom > top;
}
