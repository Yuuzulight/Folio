using System.Numerics;
using Folio.Css;
using Folio.Style;

namespace Folio.Painting;

internal readonly record struct RectF(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;

    public RectF Inset(float top, float right, float bottom, float left) =>
        new(X + left, Y + top, Math.Max(0, Width - left - right), Math.Max(0, Height - top - bottom));
}

/// <summary>Elliptical corner radii (horizontal, vertical), clockwise from the top left.</summary>
internal readonly record struct CornerRadii(Vector2 TopLeft, Vector2 TopRight, Vector2 BottomRight, Vector2 BottomLeft)
{
    public bool IsZero => TopLeft == Vector2.Zero && TopRight == Vector2.Zero && BottomRight == Vector2.Zero && BottomLeft == Vector2.Zero;

    /// <summary>The radii of a curve inset from this one (https://www.w3.org/TR/css-backgrounds-3/#corner-shaping).</summary>
    public CornerRadii Inset(float top, float right, float bottom, float left) => new(
        Vector2.Max(TopLeft - new Vector2(left, top), Vector2.Zero), Vector2.Max(TopRight - new Vector2(right, top), Vector2.Zero),
        Vector2.Max(BottomRight - new Vector2(right, bottom), Vector2.Zero), Vector2.Max(BottomLeft - new Vector2(left, bottom), Vector2.Zero));
}

internal readonly record struct RoundedRect(RectF Rect, CornerRadii Radii)
{
    public RoundedRect Inset(float top, float right, float bottom, float left) =>
        new(Rect.Inset(top, right, bottom, left), Radii.Inset(top, right, bottom, left));
}

internal enum DisplayItemKind
{
    /// <summary>Fills <see cref="DisplayItem.Shape"/> with <see cref="DisplayItem.Color"/>.</summary>
    Fill,

    /// <summary>Draws the border described by <see cref="DisplayItem.Border"/> inside <see cref="DisplayItem.Shape"/>.</summary>
    Border,

    /// <summary>Clips what follows to <see cref="DisplayItem.Shape"/> until the matching <see cref="Pop"/>.</summary>
    PushClip,

    /// <summary>Composites what follows with <see cref="DisplayItem.Opacity"/> at the matching <see cref="Pop"/>.</summary>
    PushOpacity,

    Pop,
}

/// <summary>
/// One display list command (docs/study/12-painting.md). Coordinates are CSS pixels from the canvas origin.
/// </summary>
/// <param name="Border">For borders: widths, styles and used colours (currentcolor resolved).</param>
internal readonly record struct DisplayItem(DisplayItemKind Kind, RoundedRect Shape = default, CssColor Color = default, float Opacity = 1, BorderGroup? Border = null);

/// <summary>A flat list of paint commands in paint order, replayed onto a canvas.</summary>
internal sealed class DisplayList
{
    public List<DisplayItem> Items { get; } = [];
}
