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

/// <summary>What a fill or stroke paints with. Solid colours for now; gradients and image patterns come in M2.</summary>
/// <param name="Blur">The standard deviation, in CSS pixels, of a Gaussian blur applied to what is painted (shadows); 0 for none.</param>
public readonly record struct Paint(Rgba Color, float Blur = 0);

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

/// <summary>Options for a compositing layer; the layer is blended back when popped.</summary>
public readonly record struct LayerOptions(float Opacity);

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
