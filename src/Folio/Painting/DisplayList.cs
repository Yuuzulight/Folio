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

    /// <summary>Draws <see cref="DisplayItem.Glyphs"/> in <see cref="DisplayItem.Color"/>, or with <see cref="DisplayItem.Gradient"/>.</summary>
    Glyphs,

    /// <summary>
    /// Draws a text decoration line in <see cref="DisplayItem.LineStyle"/> across <see cref="DisplayItem.Shape"/>, whose
    /// height is the line's thickness (a wavy line reaches three times that below its top edge). With
    /// <see cref="DisplayItem.Glyphs"/>, the line leaves gaps where it would cross their ink (text-decoration-skip-ink).
    /// </summary>
    Decoration,

    /// <summary>Draws <see cref="DisplayItem.Image"/> scaled into <see cref="DisplayItem.Shape"/>.</summary>
    Image,

    /// <summary>Fills <see cref="DisplayItem.Path"/> by <see cref="DisplayItem.Rule"/> with <see cref="DisplayItem.Color"/>, or with <see cref="DisplayItem.Gradient"/>.</summary>
    FillPath,

    /// <summary>Strokes <see cref="DisplayItem.Path"/> as <see cref="DisplayItem.Stroke"/> says, with <see cref="DisplayItem.Color"/>, or with <see cref="DisplayItem.Gradient"/>.</summary>
    StrokePath,

    /// <summary>
    /// Clips what follows to <see cref="DisplayItem.Shape"/>, or to <see cref="DisplayItem.Path"/> filled by
    /// <see cref="DisplayItem.Rule"/> when there is one, until the matching <see cref="Pop"/>.
    /// </summary>
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
/// <remarks>
/// What most commands use (fills, borders, glyphs, clips, pops) is stored in the item; what only some use (images,
/// shadows, gradients, transforms, filters, paths) is stored in one shared object, made only for items that have any of
/// it. Large documents hold tens of thousands of items, and keeping the item small keeps the list small (#196).
/// </remarks>
internal readonly record struct DisplayItem
{
    /// <param name="Border">For borders: widths, styles and used colours (currentcolor resolved).</param>
    /// <param name="Glyphs">For glyph runs: the font, size, glyph ids and baseline origins.</param>
    /// <param name="Blur">For box shadows and glyph runs (text shadows): the Gaussian standard deviation in CSS px.</param>
    /// <param name="Image">For images: the pixels, drawn with <paramref name="Sampling"/>.</param>
    /// <param name="LineStyle">For decorations: solid, dotted, dashed or wavy (a double line is two solid ones).</param>
    /// <param name="Transform">For transforms: the matrix in row-vector form, in canvas coordinates.</param>
    /// <param name="Projection">For transforms under perspective: a projective matrix used instead of <paramref name="Transform"/> (see <see cref="ICanvas.Transform(in System.Numerics.Matrix4x4)"/>).</param>
    /// <param name="Filters">For layers: the filter primitives applied to what the layer holds (filter), or null.</param>
    /// <param name="Backdrop">For layers: the filter primitives applied to the backdrop (backdrop-filter), or null.</param>
    /// <param name="Blend">For layers and fills: how they blend with what is under them.</param>
    /// <param name="Path">For clips: a path to clip to instead of <paramref name="Shape"/>, filled by <paramref name="Rule"/>; for path fills and strokes, the path.</param>
    /// <param name="Stroke">For path strokes: the width, caps and dashes.</param>
    public DisplayItem(DisplayItemKind Kind, RoundedRect Shape = default, CssColor Color = default, float Opacity = 1,
                       BorderGroup? Border = null, GlyphRun? Glyphs = null, TextDecorationStyle LineStyle = TextDecorationStyle.Solid,
                       Imaging.IImageHandle? Image = null, ImageSampling Sampling = ImageSampling.Smooth,
                       float Blur = 0, bool Inset = false, RoundedRect Box = default,
                       Gradient? Gradient = null, System.Numerics.Matrix3x2 Transform = default,
                       IReadOnlyList<Filter>? Filters = null, IReadOnlyList<Filter>? Backdrop = null, BlendMode Blend = BlendMode.Normal,
                       PathData? Path = null, FillRule Rule = FillRule.NonZero, Stroke? Stroke = null,
                       System.Numerics.Matrix4x4? Projection = null)
    {
        (this.Kind, this.Shape, this.Color, this.Opacity, this.Border, this.Glyphs, this.LineStyle, this.Blend) =
            (Kind, Shape, Color, Opacity, Border, Glyphs, LineStyle, Blend);
        if (Image is not null || Sampling != ImageSampling.Smooth || Blur != 0 || Inset || Box != default || Gradient is not null
            || Transform != default || Filters is not null || Backdrop is not null || Path is not null || Rule != FillRule.NonZero
            || Stroke is not null || Projection is not null)
            rare = new Rare(Image, Sampling, Blur, Inset, Box, Gradient, Transform, Filters, Backdrop, Path, Rule, Stroke, Projection);
    }

    public DisplayItemKind Kind { get; init; }
    public RoundedRect Shape { get; init; }
    public CssColor Color { get; init; }
    public float Opacity { get; init; }
    public BorderGroup? Border { get; init; }
    public GlyphRun? Glyphs { get; init; }
    public TextDecorationStyle LineStyle { get; init; }
    public BlendMode Blend { get; init; }

    private readonly Rare? rare;

    public Imaging.IImageHandle? Image { get => rare?.Image; init => rare = (rare ?? Rare.None) with { Image = value }; }
    public ImageSampling Sampling { get => rare?.Sampling ?? ImageSampling.Smooth; init => rare = (rare ?? Rare.None) with { Sampling = value }; }
    public float Blur { get => rare?.Blur ?? 0; init => rare = (rare ?? Rare.None) with { Blur = value }; }
    public bool Inset { get => rare?.Inset ?? false; init => rare = (rare ?? Rare.None) with { Inset = value }; }
    public RoundedRect Box { get => rare?.Box ?? default; init => rare = (rare ?? Rare.None) with { Box = value }; }
    public Gradient? Gradient { get => rare?.Gradient; init => rare = (rare ?? Rare.None) with { Gradient = value }; }
    public System.Numerics.Matrix3x2 Transform { get => rare?.Transform ?? default; init => rare = (rare ?? Rare.None) with { Transform = value }; }
    public IReadOnlyList<Filter>? Filters { get => rare?.Filters; init => rare = (rare ?? Rare.None) with { Filters = value }; }
    public IReadOnlyList<Filter>? Backdrop { get => rare?.Backdrop; init => rare = (rare ?? Rare.None) with { Backdrop = value }; }
    public PathData? Path { get => rare?.Path; init => rare = (rare ?? Rare.None) with { Path = value }; }
    public FillRule Rule { get => rare?.Rule ?? FillRule.NonZero; init => rare = (rare ?? Rare.None) with { Rule = value }; }
    public Stroke? Stroke { get => rare?.Stroke; init => rare = (rare ?? Rare.None) with { Stroke = value }; }
    public System.Numerics.Matrix4x4? Projection { get => rare?.Projection; init => rare = (rare ?? Rare.None) with { Projection = value }; }

    // What only some commands use; compared by value, like the item.
    private sealed record Rare(Imaging.IImageHandle? Image = null, ImageSampling Sampling = ImageSampling.Smooth, float Blur = 0, bool Inset = false,
                               RoundedRect Box = default, Gradient? Gradient = null, System.Numerics.Matrix3x2 Transform = default,
                               IReadOnlyList<Filter>? Filters = null, IReadOnlyList<Filter>? Backdrop = null, PathData? Path = null,
                               FillRule Rule = FillRule.NonZero, Stroke? Stroke = null, System.Numerics.Matrix4x4? Projection = null)
    {
        public static readonly Rare None = new();
    }
}

/// <summary>Glyphs of one font at one size, each with its baseline origin on the canvas.</summary>
internal sealed record GlyphRun(Typography.IFontHandle Font, float Size, ushort[] Glyphs, System.Numerics.Vector2[] Origins);

/// <summary>A flat list of paint commands in paint order, replayed onto a canvas.</summary>
internal sealed class DisplayList
{
    public List<DisplayItem> Items { get; } = [];
}
