using Folio.Css;
using Folio.Style;

namespace Folio.Painting;

internal enum DisplayItemKind
{
    /// <summary>Fills <see cref="DisplayItem.Shape"/> with <see cref="DisplayItem.Color"/>.</summary>
    Fill,

    /// <summary>Draws the border described by <see cref="DisplayItem.Border"/> inside <see cref="DisplayItem.Shape"/>.</summary>
    Border,

    /// <summary>Draws <see cref="DisplayItem.Glyphs"/> in <see cref="DisplayItem.Color"/>.</summary>
    Glyphs,

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
/// <param name="Glyphs">For glyph runs: the font, size, glyph ids and baseline origins.</param>
internal readonly record struct DisplayItem(DisplayItemKind Kind, RoundedRect Shape = default, CssColor Color = default, float Opacity = 1,
                                            BorderGroup? Border = null, GlyphRun? Glyphs = null);

/// <summary>Glyphs of one font at one size, each with its baseline origin on the canvas.</summary>
internal sealed record GlyphRun(Typography.IFontHandle Font, float Size, ushort[] Glyphs, System.Numerics.Vector2[] Origins);

/// <summary>A flat list of paint commands in paint order, replayed onto a canvas.</summary>
internal sealed class DisplayList
{
    public List<DisplayItem> Items { get; } = [];
}
