using System.Numerics;
using Folio.Painting;

namespace Folio.Scripting.Dom;

/// <summary>
/// A canvas path (HTML §4.12.5.1.8 "building paths"): subpaths of lines and cubic curves, whose points are stored already
/// mapped by the transform the caller passes to each method. A context passes its current transform, so its path is in
/// canvas coordinates as the spec says; a <c>Path2D</c> passes the identity, and is mapped when it is used.
/// </summary>
internal sealed class CanvasPath
{
    private enum Kind { Move, Line, Cubic, Close }

    private readonly record struct Segment(Kind Kind, Vector2 P1 = default, Vector2 P2 = default, Vector2 P3 = default);

    private readonly List<Segment> _segments = [];
    private Vector2 _start;
    private Vector2 _current;
    private bool _hasSubpath;

    public CanvasPath()
    {
    }

    public CanvasPath(CanvasPath other)
    {
        _segments.AddRange(other._segments);
        (_start, _current, _hasSubpath) = (other._start, other._current, other._hasSubpath);
    }

    public bool IsEmpty => _segments.Count == 0;

    public void Clear()
    {
        _segments.Clear();
        _hasSubpath = false;
    }

    // Canvas path construction is a per-frame hot path. Avoid allocating a
    // params array for every move, curve, arc, and ellipse validation.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static bool Finite(double a, double b) => double.IsFinite(a) && double.IsFinite(b);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static bool Finite(double a, double b, double c, double d) => double.IsFinite(a) && double.IsFinite(b) && double.IsFinite(c) && double.IsFinite(d);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static bool Finite(double a, double b, double c, double d, double e) => Finite(a, b, c, d) && double.IsFinite(e);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static bool Finite(double a, double b, double c, double d, double e, double f) => Finite(a, b, c, d, e) && double.IsFinite(f);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static bool Finite(double a, double b, double c, double d, double e, double f, double g) => Finite(a, b, c, d, e, f) && double.IsFinite(g);

    private static Vector2 Map(Matrix3x2 m, double x, double y) => Vector2.Transform(new Vector2((float)x, (float)y), m);

    // A new subpath at the point, as moveTo makes one.
    private void Begin(Vector2 point)
    {
        _segments.Add(new Segment(Kind.Move, point));
        (_start, _current, _hasSubpath) = (point, point, true);
    }

    // "Ensure there is a subpath for (x, y)": start one there when the path has none.
    private bool Ensure(Matrix3x2 m, double x, double y)
    {
        if (_hasSubpath)
            return false;
        Begin(Map(m, x, y));
        return true;
    }

    public void MoveTo(Matrix3x2 m, double x, double y)
    {
        if (Finite(x, y))
            Begin(Map(m, x, y));
    }

    public void LineTo(Matrix3x2 m, double x, double y)
    {
        if (!Finite(x, y) || Ensure(m, x, y))
            return;
        AddLine(Map(m, x, y));
    }

    private void AddLine(Vector2 point)
    {
        _segments.Add(new Segment(Kind.Line, point));
        _current = point;
    }

    private void AddCubic(Vector2 c1, Vector2 c2, Vector2 end)
    {
        _segments.Add(new Segment(Kind.Cubic, c1, c2, end));
        _current = end;
    }

    public void ClosePath()
    {
        if (!_hasSubpath)
            return;
        _segments.Add(new Segment(Kind.Close));
        // The next segment starts a new subpath where this one began.
        _current = _start;
        _segments.Add(new Segment(Kind.Move, _start));
    }

    public void QuadraticCurveTo(Matrix3x2 m, double cpx, double cpy, double x, double y)
    {
        if (!Finite(cpx, cpy, x, y))
            return;
        Ensure(m, cpx, cpy);
        var (c, end) = (Map(m, cpx, cpy), Map(m, x, y));
        AddCubic(_current + (c - _current) * (2f / 3f), end + (c - end) * (2f / 3f), end);
    }

    public void BezierCurveTo(Matrix3x2 m, double c1x, double c1y, double c2x, double c2y, double x, double y)
    {
        if (!Finite(c1x, c1y, c2x, c2y, x, y))
            return;
        Ensure(m, c1x, c1y);
        AddCubic(Map(m, c1x, c1y), Map(m, c2x, c2y), Map(m, x, y));
    }

    public void Rect(Matrix3x2 m, double x, double y, double w, double h)
    {
        if (!Finite(x, y, w, h))
            return;
        Begin(Map(m, x, y));
        AddLine(Map(m, x + w, y));
        AddLine(Map(m, x + w, y + h));
        AddLine(Map(m, x, y + h));
        ClosePath();
    }

    /// <summary>arcTo (HTML §4.12.5.1.8); false when the radius is negative (the caller throws IndexSizeError).</summary>
    public bool ArcTo(Matrix3x2 m, double x1, double y1, double x2, double y2, double radius)
    {
        if (!Finite(x1, y1, x2, y2, radius))
            return true;
        if (radius < 0)
            return false;
        Ensure(m, x1, y1);
        if (!Matrix3x2.Invert(m, out var inverse))
            return true;
        // The current point in the coordinates the arguments are in.
        var from = Vector2.Transform(_current, inverse);
        var (p0, p1, p2) = (new Vector2D(from.X, from.Y), new Vector2D(x1, y1), new Vector2D(x2, y2));
        var (d0, d2) = (p0 - p1, p2 - p1);
        var cross = d0.Cross(d2);
        if (p0 == p1 || p1 == p2 || radius == 0 || Math.Abs(cross) < 1e-12)
        {
            AddLine(Map(m, x1, y1));
            return true;
        }
        var (u0, u2) = (d0.Normalized(), d2.Normalized());
        var cos = Math.Clamp(u0.Dot(u2), -1, 1);
        // The tangent points lie this far from p1 along each line; the circle's centre along the bisector.
        var tangent = radius / Math.Tan(Math.Acos(cos) / 2);
        var start = p1 + u0 * tangent;
        var end = p1 + u2 * tangent;
        var bisector = (u0 + u2).Normalized();
        var centre = p1 + bisector * (radius / Math.Sin(Math.Acos(cos) / 2));
        var startAngle = Math.Atan2(start.Y - centre.Y, start.X - centre.X);
        var endAngle = Math.Atan2(end.Y - centre.Y, end.X - centre.X);
        AddLine(Map(m, start.X, start.Y));
        // The short way round from the first tangent point to the second.
        var turn = (endAngle - startAngle) % (2 * Math.PI);
        if (turn > Math.PI)
            turn -= 2 * Math.PI;
        else if (turn < -Math.PI)
            turn += 2 * Math.PI;
        AppendArc(m, centre.X, centre.Y, radius, radius, 0, startAngle, startAngle + turn, anticlockwise: turn < 0, connect: false);
        return true;
    }

    /// <summary>arc (HTML §4.12.5.1.8); false when the radius is negative.</summary>
    public bool Arc(Matrix3x2 m, double x, double y, double radius, double start, double end, bool anticlockwise)
    {
        if (!Finite(x, y, radius, start, end))
            return true;
        if (radius < 0)
            return false;
        AppendArc(m, x, y, radius, radius, 0, start, end, anticlockwise, connect: true);
        return true;
    }

    /// <summary>ellipse (HTML §4.12.5.1.8); false when a radius is negative.</summary>
    public bool Ellipse(Matrix3x2 m, double x, double y, double rx, double ry, double rotation, double start, double end, bool anticlockwise)
    {
        if (!Finite(x, y, rx, ry, rotation, start, end))
            return true;
        if (rx < 0 || ry < 0)
            return false;
        AppendArc(m, x, y, rx, ry, rotation, start, end, anticlockwise, connect: true);
        return true;
    }

    // The arc of an ellipse as cubic curves, joined to the current point by a line when asked to, or started by a move.
    private void AppendArc(Matrix3x2 m, double cx, double cy, double rx, double ry, double rotation, double start, double end, bool anticlockwise, bool connect)
    {
        const double Tau = 2 * Math.PI;
        double sweep;
        if (!anticlockwise && end - start >= Tau)
            sweep = Tau;
        else if (anticlockwise && start - end >= Tau)
            sweep = -Tau;
        else if (!anticlockwise)
        {
            sweep = (end - start) % Tau;
            if (sweep < 0)
                sweep += Tau;
        }
        else
        {
            sweep = -((start - end) % Tau);
            if (sweep > 0)
                sweep -= Tau;
        }
        var (sin, cos) = Math.SinCos(rotation);
        Vector2 At(double angle)
        {
            var (px, py) = (rx * Math.Cos(angle), ry * Math.Sin(angle));
            return Map(m, cx + px * cos - py * sin, cy + px * sin + py * cos);
        }
        var first = At(start);
        if (connect && _hasSubpath)
            AddLine(first);
        else if (connect)
            Begin(first);
        if (rx == 0 || ry == 0)
        {
            if (connect)
                AddLine(Map(m, cx, cy));
            return;
        }
        // Quarter turns at most, each a cubic whose handles are 4/3·tan(θ/4) long.
        var pieces = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 2) - 1e-9));
        var step = sweep / pieces;
        var k = 4.0 / 3.0 * Math.Tan(step / 4);
        for (var i = 0; i < pieces; i++)
        {
            var (a0, a1) = (start + step * i, start + step * (i + 1));
            Vector2 Tangent(double angle)
            {
                var (dx, dy) = (-rx * Math.Sin(angle), ry * Math.Cos(angle));
                return Map(m, dx * cos - dy * sin, dx * sin + dy * cos) - Map(m, 0, 0);
            }
            var (p0, p3) = (At(a0), At(a1));
            AddCubic(p0 + Tangent(a0) * (float)k, p3 - Tangent(a1) * (float)k, p3);
        }
    }

    /// <summary>roundRect (HTML §4.12.5.1.8) with the radii already resolved to (x, y) pairs for the four corners, clockwise from the top left.</summary>
    public void RoundRect(Matrix3x2 m, double x, double y, double w, double h, (double X, double Y)[] radii)
    {
        // A negative size is flipped: the rectangle is the same one, and the radii go with their corners.
        if (w < 0)
        {
            x += w;
            w = -w;
            (radii[0], radii[1], radii[2], radii[3]) = (radii[1], radii[0], radii[3], radii[2]);
        }
        if (h < 0)
        {
            y += h;
            h = -h;
            (radii[0], radii[1], radii[2], radii[3]) = (radii[3], radii[2], radii[1], radii[0]);
        }
        // Radii that do not fit are scaled down together until they do.
        var scale = 1.0;
        void Fit(double a, double b, double size)
        {
            if (a + b > size && a + b > 0)
                scale = Math.Min(scale, size / (a + b));
        }
        Fit(radii[0].X, radii[1].X, w);
        Fit(radii[3].X, radii[2].X, w);
        Fit(radii[0].Y, radii[3].Y, h);
        Fit(radii[1].Y, radii[2].Y, h);
        for (var i = 0; i < 4; i++)
            radii[i] = (radii[i].X * scale, radii[i].Y * scale);
        var (tl, tr, br, bl) = (radii[0], radii[1], radii[2], radii[3]);
        Begin(Map(m, x + tl.X, y));
        AddLine(Map(m, x + w - tr.X, y));
        Corner(m, x + w - tr.X, y + tr.Y, tr, -Math.PI / 2, 0);
        AddLine(Map(m, x + w, y + h - br.Y));
        Corner(m, x + w - br.X, y + h - br.Y, br, 0, Math.PI / 2);
        AddLine(Map(m, x + bl.X, y + h));
        Corner(m, x + bl.X, y + h - bl.Y, bl, Math.PI / 2, Math.PI);
        AddLine(Map(m, x, y + tl.Y));
        Corner(m, x + tl.X, y + tl.Y, tl, Math.PI, 3 * Math.PI / 2);
        ClosePath();
    }

    private void Corner(Matrix3x2 m, double cx, double cy, (double X, double Y) radius, double from, double to)
    {
        if (radius.X <= 0 || radius.Y <= 0)
        {
            AddLine(Map(m, cx, cy));
            return;
        }
        var k = 4.0 / 3.0 * Math.Tan((to - from) / 4);
        var p3 = Map(m, cx + radius.X * Math.Cos(to), cy + radius.Y * Math.Sin(to));
        var c1 = Map(m, cx + radius.X * (Math.Cos(from) - k * Math.Sin(from)), cy + radius.Y * (Math.Sin(from) + k * Math.Cos(from)));
        var c2 = Map(m, cx + radius.X * (Math.Cos(to) + k * Math.Sin(to)), cy + radius.Y * (Math.Sin(to) - k * Math.Cos(to)));
        AddCubic(c1, c2, p3);
    }

    /// <summary>Appends SVG path data's segments (Path2D from a string).</summary>
    public void AddSegments(IReadOnlyList<Css.PathSegment> segments)
    {
        foreach (var segment in segments)
        {
            switch (segment.Verb)
            {
                case 'M':
                    Begin(segment.P1);
                    break;
                case 'L':
                    LineTo(Matrix3x2.Identity, segment.P1.X, segment.P1.Y);
                    break;
                case 'C':
                    Ensure(Matrix3x2.Identity, segment.P1.X, segment.P1.Y);
                    AddCubic(segment.P1, segment.P2, segment.P3);
                    break;
                default:
                    ClosePath();
                    break;
            }
        }
    }

    /// <summary>Appends <paramref name="other"/> mapped by <paramref name="m"/> (Path2D.addPath).</summary>
    public void AddPath(CanvasPath other, Matrix3x2 m)
    {
        foreach (var s in other._segments)
        {
            var mapped = new Segment(s.Kind, Vector2.Transform(s.P1, m), Vector2.Transform(s.P2, m), Vector2.Transform(s.P3, m));
            _segments.Add(mapped);
            _current = s.Kind switch { Kind.Move or Kind.Line => mapped.P1, Kind.Cubic => mapped.P3, _ => _current };
            if (s.Kind == Kind.Move)
                (_start, _hasSubpath) = (mapped.P1, true);
        }
    }

    /// <summary>The path as drawing data, each point mapped by <paramref name="m"/>.</summary>
    public PathData ToPathData(Matrix3x2 m)
    {
        var data = new PathData();
        for (var i = 0; i < _segments.Count; i++)
        {
            var s = _segments[i];
            switch (s.Kind)
            {
                case Kind.Move:
                    // A move that nothing follows, or another move follows, draws nothing.
                    if (i + 1 < _segments.Count && _segments[i + 1].Kind is not Kind.Move)
                    {
                        var p = Vector2.Transform(s.P1, m);
                        data.MoveTo(p.X, p.Y);
                    }
                    break;
                case Kind.Line:
                    var l = Vector2.Transform(s.P1, m);
                    data.LineTo(l.X, l.Y);
                    break;
                case Kind.Cubic:
                    data.CubicTo(Vector2.Transform(s.P1, m), Vector2.Transform(s.P2, m), Vector2.Transform(s.P3, m));
                    break;
                default:
                    data.Close();
                    break;
            }
        }
        return data;
    }

    /// <summary>The path mapped by <paramref name="m"/>, as a path of its own.</summary>
    public CanvasPath Transformed(Matrix3x2 m)
    {
        var copy = new CanvasPath();
        copy.AddPath(this, m);
        copy._hasSubpath = _hasSubpath;
        return copy;
    }

    /// <summary>Whether the point is inside the path by the rule, every open subpath counting as closed (isPointInPath).</summary>
    public bool Contains(double x, double y, FillRule rule)
    {
        var winding = 0;
        var crossings = 0;
        foreach (var (a, b) in Edges(closeOpen: true))
        {
            if ((a.Y <= y) == (b.Y <= y))
                continue;
            var t = (y - a.Y) / (b.Y - a.Y);
            if (a.X + t * (b.X - a.X) <= x)
                continue;
            crossings++;
            winding += b.Y > a.Y ? 1 : -1;
        }
        return rule == FillRule.EvenOdd ? crossings % 2 == 1 : winding != 0;
    }

    /// <summary>Whether the point is within half <paramref name="width"/> of the path's lines (isPointInStroke, ignoring caps and joins).</summary>
    public bool NearStroke(double x, double y, double width)
    {
        var reach = width / 2;
        foreach (var (a, b) in Edges(closeOpen: false))
        {
            var (dx, dy) = (b.X - a.X, b.Y - a.Y);
            var length2 = dx * dx + dy * dy;
            var t = length2 == 0 ? 0 : Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / length2, 0, 1);
            var (px, py) = (a.X + t * dx - x, a.Y + t * dy - y);
            if (px * px + py * py <= reach * reach)
                return true;
        }
        return false;
    }

    // The path's lines, its curves flattened; open subpaths closed when asked.
    private IEnumerable<(Vector2 A, Vector2 B)> Edges(bool closeOpen)
    {
        var (start, current, open) = (Vector2.Zero, Vector2.Zero, false);
        foreach (var s in _segments)
        {
            switch (s.Kind)
            {
                case Kind.Move:
                    if (open && closeOpen && current != start)
                        yield return (current, start);
                    (start, current, open) = (s.P1, s.P1, false);
                    break;
                case Kind.Line:
                    yield return (current, s.P1);
                    (current, open) = (s.P1, true);
                    break;
                case Kind.Cubic:
                    var previous = current;
                    for (var i = 1; i <= 16; i++)
                    {
                        var t = i / 16f;
                        var u = 1 - t;
                        var point = u * u * u * current + 3 * u * u * t * s.P1 + 3 * u * t * t * s.P2 + t * t * t * s.P3;
                        yield return (previous, point);
                        previous = point;
                    }
                    (current, open) = (s.P3, true);
                    break;
                default:
                    if (current != start)
                        yield return (current, start);
                    (current, open) = (start, false);
                    break;
            }
        }
        if (open && closeOpen && current != start)
            yield return (current, start);
    }

    private readonly record struct Vector2D(double X, double Y)
    {
        public static Vector2D operator -(Vector2D a, Vector2D b) => new(a.X - b.X, a.Y - b.Y);

        public static Vector2D operator +(Vector2D a, Vector2D b) => new(a.X + b.X, a.Y + b.Y);

        public static Vector2D operator *(Vector2D a, double s) => new(a.X * s, a.Y * s);

        public double Dot(Vector2D o) => X * o.X + Y * o.Y;

        public double Cross(Vector2D o) => X * o.Y - Y * o.X;

        public Vector2D Normalized()
        {
            var length = Math.Sqrt(X * X + Y * Y);
            return length == 0 ? this : new Vector2D(X / length, Y / length);
        }
    }
}
