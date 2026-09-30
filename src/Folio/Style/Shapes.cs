using Folio.Css;

namespace Folio.Style;

/// <summary>https://drafts.csswg.org/css-masking-1/#typedef-geometry-box (fill-box, stroke-box and view-box map to CSS boxes when used).</summary>
internal enum GeometryBox { BorderBox, PaddingBox, ContentBox, MarginBox, FillBox, StrokeBox, ViewBox }

/// <summary>
/// A computed basic shape (https://drafts.csswg.org/css-shapes-1/#basic-shape-functions). rect() and xywh() compute
/// to <see cref="InsetShape"/>.
/// </summary>
internal abstract record BasicShape;

/// <summary><c>inset()</c>: offsets from the reference box's edges and the corner radii of the inset rectangle.</summary>
internal sealed record InsetShape(LengthPercentage Top, LengthPercentage Right, LengthPercentage Bottom, LengthPercentage Left,
                                  CornerRadius TopLeft, CornerRadius TopRight, CornerRadius BottomRight, CornerRadius BottomLeft) : BasicShape
{
    public override string ToString()
    {
        var radii = new[] { TopLeft, TopRight, BottomRight, BottomLeft };
        var round = radii.All(r => r == default) ? "" : radii.Distinct().Count() == 1 ? $" round {TopLeft}" : $" round {string.Join(" ", radii)}";
        return $"inset({Top} {Right} {Bottom} {Left}{round})";
    }
}

/// <summary>A circle or ellipse radius: a length-percentage, or closest-side (null) or farthest-side.</summary>
internal readonly record struct ShapeRadius(LengthPercentage? Length, bool FarthestSide = false)
{
    public override string ToString() => Length?.ToString() ?? (FarthestSide ? "farthest-side" : "closest-side");
}

/// <summary><c>circle()</c> and <c>ellipse()</c> (a circle has one radius, <see cref="RadiusY"/> null), about a centre.</summary>
internal sealed record EllipseShape(ShapeRadius RadiusX, ShapeRadius? RadiusY, BackgroundPosition Center) : BasicShape
{
    public override string ToString() => RadiusY is { } y ? $"ellipse({RadiusX} {y} at {Center})" : $"circle({RadiusX} at {Center})";
}

/// <summary><c>polygon()</c>: its vertices and fill rule.</summary>
internal sealed record PolygonShape(bool EvenOdd, IReadOnlyList<BackgroundPosition> Points) : BasicShape
{
    public bool Equals(PolygonShape? other) => other is not null && EvenOdd == other.EvenOdd && Points.SequenceEqual(other.Points);

    public override int GetHashCode() => HashCode.Combine(EvenOdd, Points.Count);

    public override string ToString() => $"polygon({(EvenOdd ? "evenodd, " : "")}{string.Join(", ", Points)})";
}

/// <summary><c>path()</c>: SVG path data, in px from the reference box's top-left corner.</summary>
internal sealed record PathShape(bool EvenOdd, string Data, IReadOnlyList<PathSegment> Segments) : BasicShape
{
    public bool Equals(PathShape? other) => other is not null && EvenOdd == other.EvenOdd && Data == other.Data;

    public override int GetHashCode() => HashCode.Combine(EvenOdd, Data);

    public override string ToString() => $"path({(EvenOdd ? "evenodd, " : "")}\"{Data}\")";
}

/// <summary>
/// A computed <c>clip-path</c>: a basic shape in a reference box (the border box when <see cref="Box"/> is null), the
/// box's own shape, or neither for none.
/// </summary>
internal sealed record ClipPath(BasicShape? Shape, GeometryBox? Box)
{
    public static ClipPath None { get; } = new(null, null);

    public bool IsNone => Shape is null && Box is null;

    public override string ToString()
    {
        var box = Box is { } b ? string.Concat(b.ToString().Select((c, i) => char.IsUpper(c) && i > 0 ? $"-{char.ToLowerInvariant(c)}" : $"{char.ToLowerInvariant(c)}")) : null;
        return IsNone ? "none" : string.Join(" ", new[] { Shape?.ToString(), box }.Where(t => t is not null));
    }
}
