using System.Globalization;
using System.Numerics;
using Folio.Css;
using Folio.Imaging;
using Folio.Painting;

namespace Folio.Scripting.Dom;

/// <summary>A canvas gradient (HTML §4.12.5.1.4): its geometry in canvas coordinates and its colour stops.</summary>
internal sealed class CanvasGradient
{
    public enum GradientShape { Linear, Radial, Conic }

    public GradientShape Shape { get; init; }

    public double X0 { get; init; }

    public double Y0 { get; init; }

    public double R0 { get; init; }

    public double X1 { get; init; }

    public double Y1 { get; init; }

    public double R1 { get; init; }

    /// <summary>For a conic gradient: the start angle in radians.</summary>
    public double Angle { get; init; }

    /// <summary>The stops in the order they were added; equal offsets keep that order.</summary>
    public List<(double Offset, Rgba Color)> Stops { get; } = [];

    public void AddColorStop(double offset, Rgba color)
    {
        // Inserted after the last stop with an offset no greater than this one, so equal offsets stay in order.
        var index = Stops.FindLastIndex(stop => stop.Offset <= offset) + 1;
        Stops.Insert(index, (offset, color));
    }

    /// <summary>The drawing gradient, its stops scaled by <paramref name="alpha"/>; transparent black when there are no stops.</summary>
    public Gradient Build(float alpha)
    {
        var stops = Stops.Count == 0
            ? new[] { new GradientStop(0, default), new GradientStop(1, default) }
            : Stops.Select(s => new GradientStop((float)s.Offset, s.Color with { A = s.Color.A * alpha })).ToArray();
        if (stops.Length == 1)
            stops = [stops[0], stops[0] with { Offset = 1 }];
        switch (Shape)
        {
            case GradientShape.Linear:
                return new Gradient(GradientKind.Linear, stops, Start: new Vector2((float)X0, (float)Y0), End: new Vector2((float)X1, (float)Y1));
            case GradientShape.Conic:
                // Canvas angles start at 3 o'clock and the drawing gradient's at 12 o'clock, both clockwise.
                var degrees = (float)(Angle * 180 / Math.PI) + 90;
                return new Gradient(GradientKind.Conic, stops, Center: new Vector2((float)X0, (float)Y0), StartAngle: degrees, EndAngle: degrees + 360);
            default:
                var start = new Vector2((float)X0, (float)Y0);
                var end = new Vector2((float)X1, (float)Y1);
                return new Gradient(GradientKind.Radial, stops, Center: end, Radii: new Vector2((float)R1, (float)R1), Focus: start, FocusRadius: (float)R0);
        }
    }
}

/// <summary>A canvas pattern (HTML §4.12.5.1.5): an image and how it repeats.</summary>
internal sealed class CanvasPattern(IImageHandle image, bool repeatX, bool repeatY)
{
    public IImageHandle Image { get; } = image;

    public bool RepeatX { get; } = repeatX;

    public bool RepeatY { get; } = repeatY;

    /// <summary>setTransform: maps the pattern's space to the canvas's (canvas coordinates are those at fill time).</summary>
    public Matrix3x2 Transform { get; set; } = Matrix3x2.Identity;

    public ImagePattern Build(Matrix3x2 current) => new(Image, RepeatX, RepeatY, Transform * current);
}

/// <summary>What fills or strokes: a colour, a gradient or a pattern, with the text a script reads back.</summary>
internal sealed class PaintSource
{
    /// <summary>The longest text a style property takes (a colour, a font, a filter list); longer is ignored as invalid.</summary>
    public const int MaxText = 4096;

    public static readonly PaintSource Black = new(new Rgba(0, 0, 0, 1));

    public PaintSource(Rgba color) => Color = color;

    public PaintSource(CanvasGradient gradient) => Gradient = gradient;

    public PaintSource(CanvasPattern pattern) => Pattern = pattern;

    public Rgba Color { get; }

    /// <summary>The gradient or pattern object a script assigned, so reading the style gives it back.</summary>
    public ScriptValue? Script { get; init; }

    public CanvasGradient? Gradient { get; }

    public CanvasPattern? Pattern { get; }

    public string Text => Serialize(Color);

    /// <summary>The paint with <paramref name="alpha"/> (globalAlpha) applied; patterns and gradients are placed under <paramref name="transform"/>.</summary>
    public Paint ToPaint(float alpha, Matrix3x2 transform) => this switch
    {
        { Gradient: { } g } => new Paint(new Rgba(0, 0, 0, 1), Gradient: g.Build(alpha)),
        { Pattern: { } p } => new Paint(new Rgba(0, 0, 0, alpha), Pattern: p.Build(transform)),
        _ => new Paint(Color with { A = Color.A * alpha }),
    };

    /// <summary>A colour as canvas reads it back: #rrggbb when opaque, else rgba() (HTML §4.12.5.1.9 serialization).</summary>
    public static string Serialize(Rgba color)
    {
        static int Byte(float v) => (int)MathF.Round(Math.Clamp(v, 0, 1) * 255);
        if (color.A >= 1)
            return string.Create(CultureInfo.InvariantCulture, $"#{Byte(color.R):x2}{Byte(color.G):x2}{Byte(color.B):x2}");
        var alpha = Math.Round(Math.Clamp(color.A, 0, 1), 3).ToString("0.###", CultureInfo.InvariantCulture);
        return string.Create(CultureInfo.InvariantCulture, $"rgba({Byte(color.R)}, {Byte(color.G)}, {Byte(color.B)}, {alpha})");
    }

    /// <summary>A colour from CSS text; null if it is not one. <c>currentcolor</c> is <paramref name="current"/>.</summary>
    public static Rgba? ParseColor(string text, Rgba current)
    {
        // Longer text is not a colour a script means; refusing it keeps a script from making the CSS parser chew on megabytes.
        if (text.Length > MaxText)
            return null;
        if (Folio.Css.ColorText.Parse(text) is not { } color)
            return null;
        if (color.IsCurrentColor)
            return current;
        return new Rgba(color.R, color.G, color.B, color.A);
    }
}
