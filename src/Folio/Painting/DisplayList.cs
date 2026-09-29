using Folio.Css;
using Folio.Style;

namespace Folio.Painting;

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
