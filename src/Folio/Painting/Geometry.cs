using System.Numerics;

namespace Folio.Painting;

/// <summary>A rectangle in CSS pixels.</summary>
public readonly record struct RectF(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;

    /// <summary>The rectangle shrunk by the given distances (never below zero size).</summary>
    public RectF Inset(float top, float right, float bottom, float left) =>
        new(X + left, Y + top, Math.Max(0, Width - left - right), Math.Max(0, Height - top - bottom));
}

/// <summary>Elliptical corner radii (horizontal, vertical), clockwise from the top left.</summary>
public readonly record struct CornerRadii(Vector2 TopLeft, Vector2 TopRight, Vector2 BottomRight, Vector2 BottomLeft)
{
    public bool IsZero => TopLeft == Vector2.Zero && TopRight == Vector2.Zero && BottomRight == Vector2.Zero && BottomLeft == Vector2.Zero;

    /// <summary>The radii of a curve inset from this one (https://www.w3.org/TR/css-backgrounds-3/#corner-shaping).</summary>
    public CornerRadii Inset(float top, float right, float bottom, float left) => new(
        Vector2.Max(TopLeft - new Vector2(left, top), Vector2.Zero), Vector2.Max(TopRight - new Vector2(right, top), Vector2.Zero),
        Vector2.Max(BottomRight - new Vector2(right, bottom), Vector2.Zero), Vector2.Max(BottomLeft - new Vector2(left, bottom), Vector2.Zero));
}

/// <summary>A rectangle with elliptical corners; zero radii make a plain rectangle.</summary>
public readonly record struct RoundedRect(RectF Rect, CornerRadii Radii)
{
    public RoundedRect Inset(float top, float right, float bottom, float left) =>
        new(Rect.Inset(top, right, bottom, left), Radii.Inset(top, right, bottom, left));
}

/// <summary>An sRGB colour with straight (not premultiplied) alpha, each channel from 0 to 1.</summary>
public readonly record struct Rgba(float R, float G, float B, float A);

/// <summary>What a fill or stroke paints with: a solid colour, or a gradient when one is given.</summary>
/// <param name="Blur">The standard deviation, in CSS pixels, of a Gaussian blur applied to what is painted (shadows); 0 for none.</param>
/// <param name="Blend">How what is painted blends with what is under it.</param>
public readonly record struct Paint(Rgba Color, float Blur = 0, Gradient? Gradient = null, BlendMode Blend = BlendMode.Normal);

/// <summary>
/// How a source blends with its backdrop: source-over with the blend functions of
/// https://drafts.csswg.org/compositing-2/#blending, or, for <see cref="PlusLighter"/>, added and clamped.
/// </summary>
public enum BlendMode
{
    Normal,
    Multiply,
    Screen,
    Overlay,
    Darken,
    Lighten,
    ColorDodge,
    ColorBurn,
    HardLight,
    SoftLight,
    Difference,
    Exclusion,
    Hue,
    Saturation,
    Color,
    Luminosity,
    PlusLighter,
}

public enum GradientKind
{
    Linear,
    Radial,
    Conic,
}

/// <summary>A gradient colour stop: its offset along the gradient, from 0 to 1, and its colour.</summary>
public readonly record struct GradientStop(float Offset, Rgba Color);

/// <summary>
/// A gradient in canvas coordinates. Its stops run from 0 to 1: from <paramref name="Start"/> to <paramref name="End"/>
/// (linear), from <paramref name="Center"/> out to the ellipse of <paramref name="Radii"/> (radial), or around
/// <paramref name="Center"/> from <paramref name="StartAngle"/> to <paramref name="EndAngle"/>, in degrees clockwise from
/// the top (conic). Colours between stops are interpolated in sRGB with premultiplied alpha; any other interpolation is
/// already expressed in the stops. Beyond the ends the end colours continue, or with <paramref name="Repeat"/> the stops
/// repeat.
/// </summary>
public sealed record Gradient(GradientKind Kind, IReadOnlyList<GradientStop> Stops, bool Repeat = false,
                              Vector2 Start = default, Vector2 End = default, Vector2 Center = default, Vector2 Radii = default,
                              float StartAngle = 0, float EndAngle = 360);

public enum FillRule
{
    NonZero,
    EvenOdd,
}

public enum LineCap
{
    Butt,
    Round,
}

/// <summary>How a path is stroked: its width, cap, and an optional dash pattern (on, off, ... lengths).</summary>
public readonly record struct Stroke(float Width, LineCap Cap = LineCap.Butt, IReadOnlyList<float>? Dashes = null);

/// <summary>How image pixels are sampled when an image is drawn larger or smaller than its pixel size.</summary>
public enum ImageSampling
{
    /// <summary>Smoothly interpolated.</summary>
    Smooth,

    /// <summary>Nearest pixel, so enlarged pixels stay square (image-rendering: pixelated and crisp-edges).</summary>
    Pixelated,
}

/// <summary>
/// Options for a compositing layer; the layer is blended back when popped. What is drawn into it is filtered by
/// <paramref name="Filters"/>, then composited with <paramref name="Opacity"/> and <paramref name="Blend"/>. With <paramref name="Backdrop"/>, the
/// layer starts as what lies under it, filtered by those filters (their blur reading mirrored edges at the clip) and
/// clipped to <paramref name="BackdropClip"/>, instead of empty (https://drafts.csswg.org/filter-effects-2/#backdrop-filter-operation).
/// </summary>
public readonly record struct LayerOptions(float Opacity, IReadOnlyList<Filter>? Filters = null, IReadOnlyList<Filter>? Backdrop = null,
                                           RoundedRect BackdropClip = default, BlendMode Blend = BlendMode.Normal);

public enum FilterKind
{
    /// <summary>A Gaussian blur of <see cref="Filter.StdDeviation"/>.</summary>
    Blur,

    /// <summary>A colour matrix (<see cref="Filter.Matrix"/>).</summary>
    ColorMatrix,

    /// <summary>
    /// A drop shadow: the alpha moved by <see cref="Filter.Offset"/>, blurred by <see cref="Filter.StdDeviation"/> and
    /// filled with <see cref="Filter.Color"/>, drawn under the input.
    /// </summary>
    DropShadow,
}

/// <summary>
/// One filter primitive (https://drafts.csswg.org/filter-effects-1/#FilterPrimitivesOverview), applied to the output
/// of the one before it, with results clamped. A colour matrix has 20 values, row by row: R', G', B' and A' from R, G,
/// B, A and 1, on straight-alpha sRGB colours from 0 to 1. Lengths are CSS pixels.
/// </summary>
public sealed record Filter(FilterKind Kind, float StdDeviation = 0, IReadOnlyList<float>? Matrix = null, Vector2 Offset = default, Rgba Color = default);

public enum PathVerb
{
    MoveTo,
    LineTo,
    CubicTo,
    Close,
}

/// <summary>One path command; <see cref="CubicTo"/> uses all three points, the others only the first.</summary>
public readonly record struct PathCommand(PathVerb Verb, Vector2 P1 = default, Vector2 P2 = default, Vector2 P3 = default);

/// <summary>A path of lines and cubic curves in CSS pixels.</summary>
public sealed class PathData
{
    private readonly List<PathCommand> _commands = [];

    public IReadOnlyList<PathCommand> Commands => _commands;

    public PathData MoveTo(float x, float y) => Add(new(PathVerb.MoveTo, new(x, y)));

    public PathData LineTo(float x, float y) => Add(new(PathVerb.LineTo, new(x, y)));

    public PathData CubicTo(Vector2 control1, Vector2 control2, Vector2 end) => Add(new(PathVerb.CubicTo, control1, control2, end));

    public PathData Close() => Add(new(PathVerb.Close));

    /// <summary>Adds a closed rounded rectangle, clockwise from the end of the top-left corner.</summary>
    public PathData AddRoundedRect(in RoundedRect shape)
    {
        // Cubic approximation of a quarter ellipse.
        const float K = 0.5522848f;
        var (r, c) = (shape.Rect, shape.Radii);
        MoveTo(r.X + c.TopLeft.X, r.Y);
        LineTo(r.Right - c.TopRight.X, r.Y);
        CubicTo(new(r.Right - c.TopRight.X * (1 - K), r.Y), new(r.Right, r.Y + c.TopRight.Y * (1 - K)), new(r.Right, r.Y + c.TopRight.Y));
        LineTo(r.Right, r.Bottom - c.BottomRight.Y);
        CubicTo(new(r.Right, r.Bottom - c.BottomRight.Y * (1 - K)), new(r.Right - c.BottomRight.X * (1 - K), r.Bottom), new(r.Right - c.BottomRight.X, r.Bottom));
        LineTo(r.X + c.BottomLeft.X, r.Bottom);
        CubicTo(new(r.X + c.BottomLeft.X * (1 - K), r.Bottom), new(r.X, r.Bottom - c.BottomLeft.Y * (1 - K)), new(r.X, r.Bottom - c.BottomLeft.Y));
        LineTo(r.X, r.Y + c.TopLeft.Y);
        CubicTo(new(r.X, r.Y + c.TopLeft.Y * (1 - K)), new(r.X + c.TopLeft.X * (1 - K), r.Y), new(r.X + c.TopLeft.X, r.Y));
        return Close();
    }

    private PathData Add(PathCommand command)
    {
        _commands.Add(command);
        return this;
    }
}
