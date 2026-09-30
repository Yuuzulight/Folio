using System.Globalization;
using System.Numerics;
using Folio.Css;

namespace Folio.Svg;

/// <summary>A rectangle in SVG user units.</summary>
internal readonly record struct SvgRect(float X, float Y, float Width, float Height);

/// <summary>Which viewport dimension a percentage refers to (https://www.w3.org/TR/SVG2/coords.html#Units).</summary>
internal enum SvgAxis
{
    Horizontal,
    Vertical,

    /// <summary>The normalised diagonal: √((width² + height²) / 2).</summary>
    Other,
}

/// <summary>
/// SVG attribute grammars that are not CSS: lengths with optional units, number lists, transform lists
/// (https://www.w3.org/TR/css-transforms-1/#svg-syntax), viewBox and preserveAspectRatio
/// (https://www.w3.org/TR/SVG2/coords.html), and the basic shapes as path segments (https://www.w3.org/TR/SVG2/shapes.html).
/// Everything is in user units and independent of the DOM, so anything that draws through SVG geometry can use it.
/// </summary>
internal static class SvgGeometry
{
    // Cubic approximation of a quarter ellipse.
    private const float K = 0.5522848f;

    /// <summary>A length attribute: a number with an optional unit, or a percentage; null when missing or invalid.</summary>
    public static (float Value, bool Percent)? ParseLength(string? text, float fontSize)
    {
        if (text is null)
            return null;
        var scanner = new NumberScanner(text.Trim());
        if (scanner.Number() is not { } number)
            return null;
        var unit = text.Trim()[scanner.Position..];
        var scale = unit.ToLowerInvariant() switch
        {
            "" or "px" => 1f,
            "%" => 1f,
            "em" => fontSize,
            "ex" => fontSize / 2,
            "in" => 96f,
            "cm" => 96 / 2.54f,
            "mm" => 96 / 25.4f,
            "q" => 96 / 101.6f,
            "pt" => 96 / 72f,
            "pc" => 16f,
            _ => float.NaN,
        };
        return float.IsNaN(scale) ? null : (number * scale, unit == "%");
    }

    /// <summary>A length attribute in user units, a percentage taken of the viewport along <paramref name="axis"/>.</summary>
    public static float Length(string? text, SvgAxis axis, Vector2 viewport, float fontSize, float fallback = 0)
    {
        if (ParseLength(text, fontSize) is not { } length)
            return fallback;
        if (!length.Percent)
            return length.Value;
        var basis = axis switch
        {
            SvgAxis.Horizontal => viewport.X,
            SvgAxis.Vertical => viewport.Y,
            _ => Diagonal(viewport),
        };
        return length.Value / 100 * basis;
    }

    /// <summary>
    /// A list of lengths separated by white space and/or commas, in user units (text x, y, dx and dy); null when missing
    /// or when any item is invalid.
    /// </summary>
    public static List<float>? Lengths(string? text, SvgAxis axis, Vector2 viewport, float fontSize)
    {
        if (text is null)
            return null;
        var values = new List<float>();
        foreach (var item in text.Split([' ', '\t', '\n', '\r', '\f', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            if (ParseLength(item, fontSize) is null)
                return null;
            values.Add(Length(item, axis, viewport, fontSize));
        }
        return values.Count > 0 ? values : null;
    }

    /// <summary>The normalised diagonal of a viewport, what non-directional percentages (radii, stroke widths) refer to.</summary>
    public static float Diagonal(Vector2 viewport) => MathF.Sqrt((viewport.X * viewport.X + viewport.Y * viewport.Y) / 2);

    /// <summary>Numbers separated by white space and/or a comma, up to the first thing that is not one.</summary>
    public static List<float> Numbers(string text)
    {
        var scanner = new NumberScanner(text);
        var numbers = new List<float>();
        while (scanner.Number(separated: numbers.Count > 0) is { } n)
            numbers.Add(n);
        return numbers;
    }

    /// <summary>viewBox: min-x, min-y, width and height; null when invalid or when a size is negative.</summary>
    public static SvgRect? ParseViewBox(string? text)
    {
        if (text is null)
            return null;
        var scanner = new NumberScanner(text);
        var n = new float[4];
        for (var i = 0; i < 4; i++)
        {
            if (scanner.Number(separated: i > 0) is not { } value)
                return null;
            n[i] = value;
        }
        scanner.SkipSpace();
        return scanner.AtEnd && n[2] >= 0 && n[3] >= 0 ? new SvgRect(n[0], n[1], n[2], n[3]) : null;
    }

    /// <summary>
    /// The transform that maps a view box into a viewport (https://www.w3.org/TR/SVG2/coords.html#ComputingAViewportsTransform),
    /// with <paramref name="preserveAspectRatio"/> as written: <c>none</c>, or an alignment followed by <c>meet</c>
    /// (the default) or <c>slice</c>. Anything else is the default, <c>xMidYMid meet</c>.
    /// </summary>
    public static Matrix3x2 ViewBoxTransform(SvgRect viewBox, string? preserveAspectRatio, SvgRect viewport)
    {
        var (align, slice) = ParseAspectRatio(preserveAspectRatio);
        var (sx, sy) = (viewport.Width / viewBox.Width, viewport.Height / viewBox.Height);
        if (align != "none")
            sx = sy = slice ? Math.Max(sx, sy) : Math.Min(sx, sy);
        var (tx, ty) = (viewport.X - viewBox.X * sx, viewport.Y - viewBox.Y * sy);
        var (freeX, freeY) = (viewport.Width - viewBox.Width * sx, viewport.Height - viewBox.Height * sy);
        if (align.StartsWith("xMid", StringComparison.Ordinal))
            tx += freeX / 2;
        else if (align.StartsWith("xMax", StringComparison.Ordinal))
            tx += freeX;
        if (align.EndsWith("YMid", StringComparison.Ordinal))
            ty += freeY / 2;
        else if (align.EndsWith("YMax", StringComparison.Ordinal))
            ty += freeY;
        return new Matrix3x2(sx, 0, 0, sy, tx, ty);
    }

    private static readonly HashSet<string> Alignments =
        ["none", "xMinYMin", "xMidYMin", "xMaxYMin", "xMinYMid", "xMidYMid", "xMaxYMid", "xMinYMax", "xMidYMax", "xMaxYMax"];

    private static (string Align, bool Slice) ParseAspectRatio(string? text)
    {
        var words = (text ?? "").Split([' ', '\t', '\n', '\r', '\f'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is 0 or > 2 || words.Length == 2 && words[1] is not ("meet" or "slice") || !Alignments.Contains(words[0]))
            return ("xMidYMid", false);
        return (words[0], words is [_, "slice"]);
    }

    /// <summary>
    /// A transform attribute (https://www.w3.org/TR/css-transforms-1/#svg-transform): matrix, translate, scale, rotate
    /// (optionally about a point), skewX and skewY, in row-vector form. Null when the list is invalid, which makes the
    /// attribute ineffective.
    /// </summary>
    public static Matrix3x2? ParseTransform(string? text)
    {
        if (text is null)
            return null;
        var scanner = new NumberScanner(text);
        var result = Matrix3x2.Identity;
        scanner.SkipSpace();
        var first = true;
        while (!scanner.AtEnd)
        {
            if (!first)
                scanner.SkipSeparator();
            first = false;
            if (scanner.Name() is not { } name || !scanner.Take('('))
                return null;
            var args = new List<float>();
            while (scanner.Number(separated: args.Count > 0) is { } n)
                args.Add(n);
            if (!scanner.Take(')'))
                return null;
            Matrix3x2? op = (name, args.Count) switch
            {
                ("matrix", 6) => new Matrix3x2(args[0], args[1], args[2], args[3], args[4], args[5]),
                ("translate", 1 or 2) => Matrix3x2.CreateTranslation(args[0], args.Count > 1 ? args[1] : 0),
                ("scale", 1 or 2) => Matrix3x2.CreateScale(args[0], args.Count > 1 ? args[1] : args[0]),
                ("rotate", 1) => Matrix3x2.CreateRotation(Radians(args[0])),
                ("rotate", 3) => Matrix3x2.CreateRotation(Radians(args[0]), new Vector2(args[1], args[2])),
                ("skewX", 1) => Matrix3x2.CreateSkew(Radians(args[0]), 0),
                ("skewY", 1) => Matrix3x2.CreateSkew(0, Radians(args[0])),
                _ => null,
            };
            if (op is not { } m)
                return null;
            // The functions apply right to left: the last one maps the points first.
            result = m * result;
            scanner.SkipSpace();
        }
        return result;
    }

    private static float Radians(float degrees) => degrees * MathF.PI / 180;

    /// <summary>
    /// A rectangle with corners of radii <paramref name="rx"/> and <paramref name="ry"/>, already clamped to half the
    /// width and height, from the top edge clockwise (https://www.w3.org/TR/SVG2/shapes.html#RectElement).
    /// </summary>
    public static List<PathSegment> Rect(float x, float y, float width, float height, float rx, float ry)
    {
        if (rx <= 0 || ry <= 0)
            return [new('M', new(x, y)), new('L', new(x + width, y)), new('L', new(x + width, y + height)), new('L', new(x, y + height)), new('Z')];
        var (r, b) = (x + width, y + height);
        return
        [
            new('M', new(x + rx, y)),
            new('L', new(r - rx, y)),
            new('C', new(r - rx * (1 - K), y), new(r, y + ry * (1 - K)), new(r, y + ry)),
            new('L', new(r, b - ry)),
            new('C', new(r, b - ry * (1 - K)), new(r - rx * (1 - K), b), new(r - rx, b)),
            new('L', new(x + rx, b)),
            new('C', new(x + rx * (1 - K), b), new(x, b - ry * (1 - K)), new(x, b - ry)),
            new('L', new(x, y + ry)),
            new('C', new(x, y + ry * (1 - K)), new(x + rx * (1 - K), y), new(x + rx, y)),
            new('Z'),
        ];
    }

    /// <summary>
    /// An ellipse from its rightmost point, clockwise on the screen (https://www.w3.org/TR/SVG2/shapes.html#EllipseElement).
    /// </summary>
    public static List<PathSegment> Ellipse(float cx, float cy, float rx, float ry) =>
    [
        new('M', new(cx + rx, cy)),
        new('C', new(cx + rx, cy + ry * K), new(cx + rx * K, cy + ry), new(cx, cy + ry)),
        new('C', new(cx - rx * K, cy + ry), new(cx - rx, cy + ry * K), new(cx - rx, cy)),
        new('C', new(cx - rx, cy - ry * K), new(cx - rx * K, cy - ry), new(cx, cy - ry)),
        new('C', new(cx + rx * K, cy - ry), new(cx + rx, cy - ry * K), new(cx + rx, cy)),
        new('Z'),
    ];

    /// <summary>
    /// polyline and polygon: points from a number list; an odd number drops the last one
    /// (https://www.w3.org/TR/SVG2/shapes.html#DataTypePoints, rendered up to the error). Null for no points.
    /// </summary>
    public static List<PathSegment>? Polyline(string? points, bool closed)
    {
        var numbers = Numbers(points ?? "");
        if (numbers.Count < 2)
            return null;
        var segments = new List<PathSegment>();
        for (var i = 0; i + 1 < numbers.Count; i += 2)
            segments.Add(new PathSegment(i == 0 ? 'M' : 'L', new Vector2(numbers[i], numbers[i + 1])));
        if (closed)
            segments.Add(new PathSegment('Z'));
        return segments;
    }

    /// <summary>An angle in degrees: a number, or a number with deg, rad, grad or turn; null when invalid.</summary>
    public static float? ParseAngle(string? text)
    {
        if (text is null)
            return null;
        text = text.Trim();
        var scanner = new NumberScanner(text);
        if (scanner.Number() is not { } number)
            return null;
        return text[scanner.Position..].ToLowerInvariant() switch
        {
            "" or "deg" => number,
            "rad" => number * 180 / MathF.PI,
            "grad" => number * 0.9f,
            "turn" => number * 360,
            _ => null,
        };
    }

    /// <summary>
    /// The vertices of normalised path segments where markers go (https://www.w3.org/TR/SVG2/painting.html#MarkerElement):
    /// each subpath's start and every segment's end, with the directions of the segments coming in and going out (null
    /// where there is none, or it has no length). At a closed subpath's start and end, the closing segment comes in and
    /// the first one goes out.
    /// </summary>
    public static List<(Vector2 Point, Vector2? In, Vector2? Out)> Vertices(IReadOnlyList<PathSegment> segments)
    {
        var vertices = new List<(Vector2 Point, Vector2? In, Vector2? Out)>();
        var (current, start, first) = (Vector2.Zero, Vector2.Zero, 0);
        static Vector2? Direction(params Vector2[] candidates) => candidates.Where(v => v != Vector2.Zero).Select(v => (Vector2?)v).FirstOrDefault();
        void To(Vector2 point, Vector2? outgoing, Vector2? incoming)
        {
            if (vertices.Count > 0 && vertices[^1].Out is null)
                vertices[^1] = vertices[^1] with { Out = outgoing };
            vertices.Add((point, incoming, null));
            current = point;
        }
        foreach (var segment in segments)
        {
            switch (segment.Verb)
            {
                case 'M':
                    vertices.Add((segment.P1, null, null));
                    (current, start, first) = (segment.P1, segment.P1, vertices.Count - 1);
                    break;
                case 'L':
                {
                    var d = Direction(segment.P1 - current);
                    To(segment.P1, d, d);
                    break;
                }
                case 'C':
                    To(segment.P3, Direction(segment.P1 - current, segment.P2 - current, segment.P3 - current),
                        Direction(segment.P3 - segment.P2, segment.P3 - segment.P1, segment.P3 - current));
                    break;
                default:
                {
                    var d = Direction(start - current) ?? vertices[^1].In;
                    To(start, d, d);
                    // The closed subpath's start and end vertex: the closing segment in, the first one out.
                    vertices[first] = vertices[first] with { In = vertices[^1].In };
                    vertices[^1] = vertices[^1] with { Out = vertices[first].Out };
                    break;
                }
            }
        }
        return vertices;
    }

    /// <summary>
    /// The angle, in degrees clockwise on the screen, that orient="auto" gives a marker at a vertex: the bisector of the
    /// incoming and outgoing directions, or whichever there is.
    /// </summary>
    public static float MarkerAngle(Vector2? incoming, Vector2? outgoing)
    {
        static float Degrees(Vector2 v) => MathF.Atan2(v.Y, v.X) * 180 / MathF.PI;
        if (incoming is not { } i)
            return outgoing is { } only ? Degrees(only) : 0;
        if (outgoing is not { } o)
            return Degrees(i);
        var (a, b) = (Degrees(i), Degrees(o));
        if (MathF.Abs(b - a) > 180)
            b += b < a ? 360 : -360;
        return (a + b) / 2;
    }

    /// <summary>The box around a rectangle mapped by a transform.</summary>
    public static SvgRect Transform(SvgRect rect, Matrix3x2 matrix)
    {
        Vector2[] corners =
        [
            Vector2.Transform(new(rect.X, rect.Y), matrix), Vector2.Transform(new(rect.X + rect.Width, rect.Y), matrix),
            Vector2.Transform(new(rect.X, rect.Y + rect.Height), matrix), Vector2.Transform(new(rect.X + rect.Width, rect.Y + rect.Height), matrix),
        ];
        var (min, max) = (corners.Aggregate(Vector2.Min), corners.Aggregate(Vector2.Max));
        return new SvgRect(min.X, min.Y, max.X - min.X, max.Y - min.Y);
    }

    /// <summary>The box around two boxes, either of which may be missing.</summary>
    public static SvgRect? Union(SvgRect? a, SvgRect? b)
    {
        if (a is not { } x)
            return b;
        if (b is not { } y)
            return x;
        var (left, top) = (Math.Min(x.X, y.X), Math.Min(x.Y, y.Y));
        return new SvgRect(left, top, Math.Max(x.X + x.Width, y.X + y.Width) - left, Math.Max(x.Y + x.Height, y.Y + y.Height) - top);
    }

    /// <summary>
    /// The tight bounding box of normalised path segments (https://www.w3.org/TR/SVG2/coords.html#BoundingBoxes): end
    /// points, and where cubic curves turn; null for a path with no points.
    /// </summary>
    public static SvgRect? Bounds(IReadOnlyList<PathSegment> segments)
    {
        var (min, max) = (new Vector2(float.PositiveInfinity), new Vector2(float.NegativeInfinity));
        var (current, start) = (Vector2.Zero, Vector2.Zero);
        void Add(Vector2 p) => (min, max) = (Vector2.Min(min, p), Vector2.Max(max, p));
        foreach (var segment in segments)
        {
            switch (segment.Verb)
            {
                case 'M':
                    Add(current = start = segment.P1);
                    break;
                case 'L':
                    Add(current = segment.P1);
                    break;
                case 'Z':
                    current = start;
                    break;
                case 'C':
                    // Extremes where the derivative of either coordinate is zero, from the quadratic it makes.
                    var (p0, p1, p2, p3) = (current, segment.P1, segment.P2, segment.P3);
                    Add(p3);
                    for (var axis = 0; axis < 2; axis++)
                    {
                        float C(Vector2 v) => axis == 0 ? v.X : v.Y;
                        var (a, b, c) = (3 * (-C(p0) + 3 * C(p1) - 3 * C(p2) + C(p3)), 6 * (C(p0) - 2 * C(p1) + C(p2)), 3 * (C(p1) - C(p0)));
                        foreach (var t in Roots(a, b, c))
                        {
                            if (t is > 0 and < 1)
                            {
                                var u = 1 - t;
                                Add(u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3);
                            }
                        }
                    }
                    current = p3;
                    break;
            }
        }
        return float.IsFinite(min.X) ? new SvgRect(min.X, min.Y, max.X - min.X, max.Y - min.Y) : null;

        static IEnumerable<float> Roots(float a, float b, float c)
        {
            if (MathF.Abs(a) < 1e-9f)
                return MathF.Abs(b) < 1e-9f ? [] : [-c / b];
            var d = b * b - 4 * a * c;
            if (d < 0)
                return [];
            var root = MathF.Sqrt(d);
            return [(-b + root) / (2 * a), (-b - root) / (2 * a)];
        }
    }

    /// <summary>A scanner for SVG numbers (https://www.w3.org/TR/SVG2/paths.html#PathDataBNF, number), names and punctuation.</summary>
    private sealed class NumberScanner(string text)
    {
        public int Position { get; private set; }

        public bool AtEnd => Position >= text.Length;

        public void SkipSpace()
        {
            while (!AtEnd && text[Position] is ' ' or '\t' or '\n' or '\r' or '\f')
                Position++;
        }

        // White space, then at most one comma and more white space.
        public void SkipSeparator()
        {
            SkipSpace();
            if (!AtEnd && text[Position] == ',')
            {
                Position++;
                SkipSpace();
            }
        }

        public bool Take(char c)
        {
            SkipSpace();
            if (AtEnd || text[Position] != c)
                return false;
            Position++;
            return true;
        }

        public string? Name()
        {
            SkipSpace();
            var start = Position;
            while (!AtEnd && char.IsAsciiLetter(text[Position]))
                Position++;
            return Position > start ? text[start..Position] : null;
        }

        /// <param name="separated">Whether a comma may come before the number (it follows another).</param>
        public float? Number(bool separated = false)
        {
            var mark = Position;
            if (separated)
                SkipSeparator();
            else
                SkipSpace();
            var start = Position;
            if (!AtEnd && text[Position] is '+' or '-')
                Position++;
            var digits = Digits();
            if (!AtEnd && text[Position] == '.')
            {
                Position++;
                digits += Digits();
            }
            if (digits == 0)
            {
                Position = mark;
                return null;
            }
            // An exponent needs digits: "1em" is one followed by a unit.
            if (!AtEnd && text[Position] is 'e' or 'E')
            {
                var exponent = Position++;
                if (!AtEnd && text[Position] is '+' or '-')
                    Position++;
                if (Digits() == 0)
                    Position = exponent;
            }
            var value = float.Parse(text.AsSpan(start, Position - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            return float.IsFinite(value) ? value : null;
        }

        private int Digits()
        {
            var start = Position;
            while (!AtEnd && char.IsAsciiDigit(text[Position]))
                Position++;
            return Position - start;
        }
    }
}
