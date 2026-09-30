using System.Numerics;
using Folio.Css;

namespace Folio.Painting;

/// <summary>
/// Turns a computed gradient into a <see cref="Gradient"/> for a box (https://www.w3.org/TR/css-images-3/#gradients):
/// the gradient line or ending shape from the box's size, stop positions fixed up, transition hints and colour
/// interpolation other than plain sRGB expressed as extra stops, so the canvas only interpolates in sRGB.
/// </summary>
internal static class GradientGeometry
{
    // Extra stops per interpolated segment: enough for smooth Oklab, polar and hinted ramps.
    private const int Samples = 16;

    public static Gradient? Build(ComputedGradient g, RectF box, CssColor currentColor)
    {
        var (w, h) = (box.Width, box.Height);
        var origin = new Vector2(box.X, box.Y);
        switch (g.Kind)
        {
            case GradientFunction.Linear:
            {
                // The gradient line runs through the centre at the angle; it is as long as the box's projection on it.
                Vector2 direction;
                if (g.ToCorner is var (x, y))
                {
                    // Corners: the line is perpendicular to the diagonal between the two other corners.
                    direction = x != 0 && y != 0 ? Vector2.Normalize(new Vector2(x * h, y * w)) : new Vector2(x, y);
                }
                else
                {
                    var radians = g.Angle * MathF.PI / 180;
                    direction = new Vector2(MathF.Sin(radians), -MathF.Cos(radians));
                }
                var length = MathF.Abs(w * direction.X) + MathF.Abs(h * direction.Y);
                var center = origin + new Vector2(w / 2, h / 2);
                var start = center - direction * length / 2;
                var stops = Stops(g, length, currentColor, out var first, out var last);
                if (stops is null)
                    return null;
                return new Gradient(GradientKind.Linear, stops, g.Repeating, start + direction * first, start + direction * last);
            }
            case GradientFunction.Radial:
            {
                var center = origin + new Vector2(g.At.X.Resolve(w), g.At.Y.Resolve(h));
                var radii = Radii(g, center - origin, w, h);
                var stops = Stops(g, radii.X, currentColor, out var first, out var last);
                if (stops is null)
                    return null;
                // The canvas measures radial offsets from the centre: stops are re-measured over 0 to the last one, and
                // those before the centre are cut off there.
                // ponytail: a repeating radial gradient whose first stop is past the centre repeats from the centre.
                last = Math.Max(last, 0.001f);
                var span = last - first;
                var fromCenter = stops.Select(s => s with { Offset = Math.Clamp((first + s.Offset * span) / last, 0, 1) }).ToList();
                var scale = radii.X > 0 ? radii.Y / radii.X : 1;
                return new Gradient(GradientKind.Radial, fromCenter, g.Repeating, Center: center, Radii: new Vector2(last, last * scale));
            }
            default:
            {
                var center = origin + new Vector2(g.At.X.Resolve(w), g.At.Y.Resolve(h));
                // Positions are percentages of a full turn.
                var stops = Stops(g, 360, currentColor, out var first, out var last);
                if (stops is null)
                    return null;
                return new Gradient(GradientKind.Conic, stops, g.Repeating, Center: center, StartAngle: g.Angle + first, EndAngle: g.Angle + last);
            }
        }
    }

    // The ending shape's radii (css-images-3 §3.2.1): explicit, or from an extent keyword and the centre.
    private static Vector2 Radii(ComputedGradient g, Vector2 c, float w, float h)
    {
        if (g.SizeX is { } sx)
        {
            var rx = sx.Resolve(w);
            return g.Circle ? new Vector2(rx, rx) : new Vector2(rx, (g.SizeY ?? sx).Resolve(h));
        }
        var (dx0, dx1, dy0, dy1) = (MathF.Abs(c.X), MathF.Abs(w - c.X), MathF.Abs(c.Y), MathF.Abs(h - c.Y));
        var closest = new Vector2(MathF.Min(dx0, dx1), MathF.Min(dy0, dy1));
        var farthest = new Vector2(MathF.Max(dx0, dx1), MathF.Max(dy0, dy1));
        switch (g.Extent)
        {
            case "closest-side":
                return g.Circle ? new Vector2(MathF.Min(closest.X, closest.Y)) : closest;
            case "farthest-side":
                return g.Circle ? new Vector2(MathF.Max(farthest.X, farthest.Y)) : farthest;
            default:
                var corner = g.Extent == "closest-corner" ? closest : farthest;
                if (g.Circle)
                    return new Vector2(corner.Length());
                // An ellipse with the sides' aspect ratio that passes through the corner.
                var sides = g.Extent == "closest-corner" ? closest : farthest;
                if (sides.X <= 0 || sides.Y <= 0)
                    return new Vector2(corner.Length());
                return sides * MathF.Sqrt(2);
        }
    }

    /// <summary>
    /// The stops with their positions resolved along a gradient of <paramref name="length"/> px and fixed up
    /// (css-images-3 §3.4.3): a missing first is 0 and last 100%, a position before an earlier one moves up to it, and
    /// runs without positions are spread evenly. Hints and non-sRGB interpolation become extra stops. Offsets are
    /// fractions of the span from <paramref name="first"/> to <paramref name="last"/> (in px).
    /// </summary>
    internal static IReadOnlyList<GradientStop>? Stops(ComputedGradient g, float length, CssColor currentColor, out float first, out float last)
    {
        var count = g.Stops.Count;
        var positions = new float?[count];
        for (var i = 0; i < count; i++)
            positions[i] = g.Stops[i].Position?.Resolve(length);
        var colorIndices = Enumerable.Range(0, count).Where(i => g.Stops[i].Color is not null).ToList();
        positions[colorIndices[0]] ??= 0;
        positions[colorIndices[^1]] ??= length;
        var max = float.NegativeInfinity;
        for (var i = 0; i < count; i++)
        {
            if (positions[i] is { } p)
                positions[i] = max = Math.Max(max, p);
        }
        for (var i = 0; i < count; i++)
        {
            if (positions[i] is not null)
                continue;
            var end = i;
            while (positions[end] is null)
                end++;
            var (from, to, steps) = (positions[i - 1]!.Value, positions[end]!.Value, end - i + 1);
            for (var k = i; k < end; k++)
                positions[k] = from + (to - from) * (k - i + 1) / steps;
        }

        // Colour stops with positions, hints kept as the midpoint fraction of their segment.
        var stops = new List<(float Position, CssColor Color, float? Hint)>();
        for (var i = 0; i < count; i++)
        {
            if (g.Stops[i].Color is { } color)
            {
                stops.Add((positions[i]!.Value, color.Resolve(currentColor), null));
            }
            else
            {
                var (before, after) = (positions[i - 1]!.Value, positions[i + 1]!.Value);
                var hint = after > before ? Math.Clamp((positions[i]!.Value - before) / (after - before), 0, 1) : 0.5f;
                stops[^1] = stops[^1] with { Hint = hint };
            }
        }
        (first, last) = (stops[0].Position, stops[^1].Position);
        if (g.Repeating && last - first <= 0)
        {
            // A repeating gradient with no length is its average colour; the last colour stands in for it.
            first = 0;
            last = 1;
            return [new GradientStop(0, Rgba(stops[^1].Color)), new GradientStop(1, Rgba(stops[^1].Color))];
        }
        if (last - first <= 0)
            last = first + 0.001f;

        var span = last - first;
        var result = new List<GradientStop>();
        for (var i = 0; i < stops.Count; i++)
        {
            var (position, color, hint) = stops[i];
            result.Add(new GradientStop((position - first) / span, Rgba(color)));
            if (i + 1 == stops.Count)
                break;
            var next = stops[i + 1];
            var plain = hint is null && g.Space == ColorSpace.Srgb && color.A == next.Color.A;
            if (plain || next.Position <= position)
                continue;
            for (var s = 1; s < Samples; s++)
            {
                var t = (float)s / Samples;
                // A hint H makes the midpoint colour fall at H: progress t^(log 0.5 / log H) (css-images-4 §3.4.3).
                var progress = hint is { } hh ? hh <= 0 ? 1 : hh >= 1 ? 0 : MathF.Pow(t, MathF.Log(0.5f) / MathF.Log(hh)) : t;
                var mixed = ColorResolver.Interpolate(color, next.Color, progress, g.Space, g.Hue);
                result.Add(new GradientStop((position + (next.Position - position) * t - first) / span, Rgba(mixed)));
            }
        }
        return result;
    }

    private static Rgba Rgba(CssColor c) => new(c.R, c.G, c.B, c.A);
}
