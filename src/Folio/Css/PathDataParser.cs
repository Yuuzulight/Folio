using System.Globalization;
using System.Numerics;

namespace Folio.Css;

/// <summary>One segment of a normalized path: M and L use <see cref="P1"/>, C all three points, Z none.</summary>
internal readonly record struct PathSegment(char Verb, Vector2 P1 = default, Vector2 P2 = default, Vector2 P3 = default);

/// <summary>
/// Parses SVG path data (https://www.w3.org/TR/SVG2/paths.html#PathDataBNF) into absolute moves, lines, cubic curves
/// and closes: relative commands are made absolute, H and V become lines, quadratic curves and arcs become cubics.
/// </summary>
internal static class PathDataParser
{
    /// <summary>The segments, or null when the data is empty or has an error anywhere.</summary>
    public static IReadOnlyList<PathSegment>? Parse(string data)
    {
        var scanner = new Scanner(data);
        var segments = new List<PathSegment>();
        var (current, start, lastControl) = (Vector2.Zero, Vector2.Zero, (Vector2?)null);
        var previous = ' ';
        scanner.SkipSpace();
        if (scanner.AtEnd)
            return null;
        while (!scanner.AtEnd)
        {
            var command = scanner.Command() ?? (previous is ' ' or 'Z' or 'z' ? '\0' : previous switch { 'M' => 'L', 'm' => 'l', _ => previous });
            if (command == '\0' || (segments.Count == 0 && command is not ('M' or 'm')))
                return null;
            var relative = char.IsLower(command);
            var origin = relative ? current : Vector2.Zero;
            Vector2? Point() => scanner.Number() is { } x && scanner.Number() is { } y ? new Vector2(x, y) + origin : null;
            Vector2? control = null;
            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                    if (Point() is not { } move)
                        return null;
                    segments.Add(new PathSegment('M', move));
                    current = start = move;
                    break;
                case 'L':
                    if (Point() is not { } line)
                        return null;
                    segments.Add(new PathSegment('L', line));
                    current = line;
                    break;
                case 'H':
                    if (scanner.Number() is not { } h)
                        return null;
                    current = new Vector2(relative ? current.X + h : h, current.Y);
                    segments.Add(new PathSegment('L', current));
                    break;
                case 'V':
                    if (scanner.Number() is not { } v)
                        return null;
                    current = new Vector2(current.X, relative ? current.Y + v : v);
                    segments.Add(new PathSegment('L', current));
                    break;
                case 'C' or 'S':
                {
                    // S reflects the previous cubic's second control point, or starts at the current point.
                    var c1 = char.ToUpperInvariant(command) == 'C' ? Point()
                        : previous is 'C' or 'c' or 'S' or 's' && lastControl is { } last ? 2 * current - last : current;
                    if (c1 is not { } p1 || Point() is not { } p2 || Point() is not { } end)
                        return null;
                    segments.Add(new PathSegment('C', p1, p2, end));
                    (current, control) = (end, p2);
                    break;
                }
                case 'Q' or 'T':
                {
                    var q = char.ToUpperInvariant(command) == 'Q' ? Point()
                        : previous is 'Q' or 'q' or 'T' or 't' && lastControl is { } last ? 2 * current - last : current;
                    if (q is not { } c || Point() is not { } end)
                        return null;
                    segments.Add(new PathSegment('C', current + 2f / 3 * (c - current), end + 2f / 3 * (c - end), end));
                    (current, control) = (end, c);
                    break;
                }
                case 'A':
                    if (scanner.Number() is not { } rx || scanner.Number() is not { } ry || scanner.Number() is not { } angle
                        || scanner.Flag() is not { } large || scanner.Flag() is not { } sweep || Point() is not { } to)
                        return null;
                    Arc(segments, current, rx, ry, angle, large, sweep, to);
                    current = to;
                    break;
                default: // Z
                    segments.Add(new PathSegment('Z'));
                    current = start;
                    break;
            }
            (previous, lastControl) = (command, control);
            scanner.SkipSpace();
        }
        return segments;
    }

    // An elliptical arc as cubic curves of at most a quarter turn each, from its endpoint parameters
    // (https://www.w3.org/TR/SVG2/implnote.html#ArcImplementationNotes, including out-of-range radii).
    private static void Arc(List<PathSegment> segments, Vector2 from, float rx, float ry, float angle, bool large, bool sweep, Vector2 to)
    {
        if (from == to)
            return;
        (rx, ry) = (Math.Abs(rx), Math.Abs(ry));
        if (rx == 0 || ry == 0)
        {
            segments.Add(new PathSegment('L', to));
            return;
        }
        var phi = angle * MathF.PI / 180;
        var (cos, sin) = (MathF.Cos(phi), MathF.Sin(phi));
        var half = (from - to) / 2;
        var p = new Vector2(cos * half.X + sin * half.Y, -sin * half.X + cos * half.Y);
        var lambda = p.X * p.X / (rx * rx) + p.Y * p.Y / (ry * ry);
        if (lambda > 1)
            (rx, ry) = (rx * MathF.Sqrt(lambda), ry * MathF.Sqrt(lambda));
        var (rx2, ry2) = (rx * rx, ry * ry);
        var denominator = rx2 * p.Y * p.Y + ry2 * p.X * p.X;
        var coefficient = MathF.Sqrt(Math.Max(0, (rx2 * ry2 - denominator) / denominator)) * (large == sweep ? -1 : 1);
        var c = new Vector2(coefficient * rx * p.Y / ry, -coefficient * ry * p.X / rx);
        var center = new Vector2(cos * c.X - sin * c.Y, sin * c.X + cos * c.Y) + (from + to) / 2;
        static float Angle(Vector2 u, Vector2 v) => MathF.Atan2(u.X * v.Y - u.Y * v.X, Vector2.Dot(u, v));
        var theta = Angle(Vector2.UnitX, new Vector2((p.X - c.X) / rx, (p.Y - c.Y) / ry));
        var delta = Angle(new Vector2((p.X - c.X) / rx, (p.Y - c.Y) / ry), new Vector2((-p.X - c.X) / rx, (-p.Y - c.Y) / ry));
        if (!sweep && delta > 0)
            delta -= 2 * MathF.PI;
        else if (sweep && delta < 0)
            delta += 2 * MathF.PI;

        var count = Math.Max(1, (int)MathF.Ceiling(Math.Abs(delta) / (MathF.PI / 2) - 1e-4f));
        var step = delta / count;
        var k = 4f / 3 * MathF.Tan(step / 4);
        Vector2 Rotate(Vector2 v) => new(cos * v.X - sin * v.Y, sin * v.X + cos * v.Y);
        Vector2 At(float a) => center + Rotate(new Vector2(rx * MathF.Cos(a), ry * MathF.Sin(a)));
        Vector2 Tangent(float a) => Rotate(new Vector2(-rx * MathF.Sin(a), ry * MathF.Cos(a)));
        for (var i = 0; i < count; i++)
        {
            var (a1, a2) = (theta + i * step, theta + (i + 1) * step);
            var end = i == count - 1 ? to : At(a2);
            segments.Add(new PathSegment('C', At(a1) + k * Tangent(a1), At(a2) - k * Tangent(a2), end));
        }
    }

    private sealed class Scanner(string text)
    {
        private int _pos;

        public bool AtEnd => _pos >= text.Length;

        // Whitespace, then at most one comma and more whitespace.
        private void SkipSeparator()
        {
            SkipSpace();
            if (!AtEnd && text[_pos] == ',')
            {
                _pos++;
                SkipSpace();
            }
        }

        public void SkipSpace()
        {
            while (!AtEnd && text[_pos] is ' ' or '\t' or '\n' or '\r' or '\f')
                _pos++;
        }

        public char? Command()
        {
            if (AtEnd || "MmLlHhVvCcSsQqTtAaZz".IndexOf(text[_pos]) < 0)
                return null;
            return text[_pos++];
        }

        public float? Number()
        {
            SkipSeparator();
            var start = _pos;
            if (!AtEnd && text[_pos] is '+' or '-')
                _pos++;
            var digits = Digits();
            if (!AtEnd && text[_pos] == '.')
            {
                _pos++;
                digits += Digits();
            }
            if (digits == 0)
            {
                _pos = start;
                return null;
            }
            if (!AtEnd && text[_pos] is 'e' or 'E')
            {
                var mark = _pos++;
                if (!AtEnd && text[_pos] is '+' or '-')
                    _pos++;
                if (Digits() == 0)
                    _pos = mark;
            }
            return float.Parse(text.AsSpan(start, _pos - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        // An arc flag is a single 0 or 1, which need not be separated from what follows.
        public bool? Flag()
        {
            SkipSeparator();
            if (AtEnd || text[_pos] is not ('0' or '1'))
                return null;
            return text[_pos++] == '1';
        }

        private int Digits()
        {
            var start = _pos;
            while (!AtEnd && char.IsAsciiDigit(text[_pos]))
                _pos++;
            return _pos - start;
        }
    }
}
