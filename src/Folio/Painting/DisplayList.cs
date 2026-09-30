using Folio.Css;
using Folio.Style;

namespace Folio.Painting;

internal enum DisplayItemKind
{
    /// <summary>
    /// Fills <see cref="DisplayItem.Shape"/> with <see cref="DisplayItem.Color"/>, or with <see cref="DisplayItem.Gradient"/>,
    /// blended by <see cref="DisplayItem.Blend"/>.
    /// </summary>
    Fill,

    /// <summary>Draws the border described by <see cref="DisplayItem.Border"/> inside <see cref="DisplayItem.Shape"/>.</summary>
    Border,

    /// <summary>
    /// Fills <see cref="DisplayItem.Shape"/> with <see cref="DisplayItem.Color"/> blurred by <see cref="DisplayItem.Blur"/>,
    /// only outside <see cref="DisplayItem.Box"/> (an outer box shadow), or, when <see cref="DisplayItem.Inset"/>, inside
    /// <see cref="DisplayItem.Box"/> and outside the shape (an inset one).
    /// </summary>
    BoxShadow,

    /// <summary>Draws <see cref="DisplayItem.Glyphs"/> in <see cref="DisplayItem.Color"/>.</summary>
    Glyphs,

    /// <summary>
    /// Draws a text decoration line in <see cref="DisplayItem.LineStyle"/> across <see cref="DisplayItem.Shape"/>, whose
    /// height is the line's thickness (a wavy line reaches three times that below its top edge). With
    /// <see cref="DisplayItem.Glyphs"/>, the line leaves gaps where it would cross their ink (text-decoration-skip-ink).
    /// </summary>
    Decoration,

    /// <summary>Draws <see cref="DisplayItem.Image"/> scaled into <see cref="DisplayItem.Shape"/>.</summary>
    Image,

    /// <summary>Clips what follows to <see cref="DisplayItem.Shape"/> until the matching <see cref="Pop"/>.</summary>
    PushClip,

    /// <summary>
    /// Draws what follows into a layer, filtered by <see cref="DisplayItem.Filters"/> and composited with
    /// <see cref="DisplayItem.Opacity"/> and <see cref="DisplayItem.Blend"/> at the matching <see cref="Pop"/>. With <see cref="DisplayItem.Backdrop"/>,
    /// the layer starts as the backdrop, filtered by those and clipped to <see cref="DisplayItem.Shape"/>.
    /// </summary>
    PushLayer,

    /// <summary>Maps what follows by <see cref="DisplayItem.Transform"/>, then the transforms outside it, until the matching <see cref="Pop"/>.</summary>
    PushTransform,

    Pop,
}

/// <summary>
/// One display list command (docs/study/12-painting.md). Coordinates are CSS pixels from the canvas origin.
/// </summary>
/// <param name="Border">For borders: widths, styles and used colours (currentcolor resolved).</param>
/// <param name="Glyphs">For glyph runs: the font, size, glyph ids and baseline origins.</param>
/// <param name="Blur">For box shadows and glyph runs (text shadows): the Gaussian standard deviation in CSS px.</param>
/// <param name="Image">For images: the pixels, drawn with <paramref name="Sampling"/>.</param>
/// <param name="LineStyle">For decorations: solid, dotted, dashed or wavy (a double line is two solid ones).</param>
/// <param name="Transform">For transforms: the matrix in row-vector form, in canvas coordinates.</param>
/// <param name="Filters">For layers: the filter primitives applied to what the layer holds (filter), or null.</param>
/// <param name="Backdrop">For layers: the filter primitives applied to the backdrop (backdrop-filter), or null.</param>
/// <param name="Blend">For layers and fills: how they blend with what is under them.</param>
internal readonly record struct DisplayItem(DisplayItemKind Kind, RoundedRect Shape = default, CssColor Color = default, float Opacity = 1,
                                            BorderGroup? Border = null, GlyphRun? Glyphs = null, TextDecorationStyle LineStyle = TextDecorationStyle.Solid,
                                            Imaging.IImageHandle? Image = null, ImageSampling Sampling = ImageSampling.Smooth,
                                            float Blur = 0, bool Inset = false, RoundedRect Box = default,
                                            Gradient? Gradient = null, System.Numerics.Matrix3x2 Transform = default,
                                            IReadOnlyList<Filter>? Filters = null, IReadOnlyList<Filter>? Backdrop = null, BlendMode Blend = BlendMode.Normal);

/// <summary>Glyphs of one font at one size, each with its baseline origin on the canvas.</summary>
internal sealed record GlyphRun(Typography.IFontHandle Font, float Size, ushort[] Glyphs, System.Numerics.Vector2[] Origins);

/// <summary>A flat list of paint commands in paint order, replayed onto a canvas.</summary>
internal sealed class DisplayList
{
    public List<DisplayItem> Items { get; } = [];
}
