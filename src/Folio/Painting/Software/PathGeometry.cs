using System.Numerics;

namespace Folio.Painting.Software;

/// <summary>A flattened subpath: points joined by straight lines, closed or open.</summary>
internal readonly record struct Subpath(Vector2[] Points, bool Closed);

/// <summary>Flattens curves to line segments, and turns a stroke into polygons whose union is the stroked area.</summary>
internal static class PathGeometry
{
    private const int MaxCurveSegments = 400;

    /// <summary>The path's subpaths as polylines, curves flattened so that no point of the polyline is further than <paramref name="tolerance"/> from them.</summary>
    public static List<Subpath> Flatten(PathData path, float tolerance)
    {
        var result = new List<Subpath>();
        var points = new List<Vector2>();
        var start = Vector2.Zero;
        var current = Vector2.Zero;

        void Finish(bool closed)
        {
            if (points.Count > 0)
                result.Add(new Subpath([.. points], closed));
            points.Clear();
        }

        foreach (var command in path.Commands)
        {
            switch (command.Verb)
            {
                case PathVerb.MoveTo:
                    Finish(false);
                    start = current = command.P1;
                    points.Add(current);
                    break;
                case PathVerb.LineTo:
                    if (points.Count == 0)
                        points.Add(current);
                    points.Add(command.P1);
                    current = command.P1;
                    break;
                case PathVerb.CubicTo:
                {
                    if (points.Count == 0)
                        points.Add(current);
                    var (p0, p1, p2, p3) = (current, command.P1, command.P2, command.P3);
                    var d1 = (p0 - 2 * p1 + p2).Length();
                    var d2 = (p1 - 2 * p2 + p3).Length();
                    var segments = (int)Math.Clamp(MathF.Ceiling(MathF.Sqrt(0.75f * Math.Max(d1, d2) / tolerance)), 1, MaxCurveSegments);
                    for (var i = 1; i <= segments; i++)
                    {
                        var t = (float)i / segments;
                        var u = 1 - t;
                        points.Add(u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3);
                    }
                    current = p3;
                    break;
                }
                case PathVerb.Close:
                    if (points.Count > 0)
                    {
                        Finish(true);
                        current = start;
                    }
                    break;
            }
        }
        Finish(false);
        return result;
    }

    /// <summary>Polygons for the fill of a path: every subpath is implicitly closed, those under three points dropped.</summary>
    public static List<Vector2[]> Polygons(IEnumerable<Subpath> subpaths, in Matrix33 transform)
    {
        var polygons = new List<Vector2[]>();
        foreach (var subpath in subpaths)
        {
            if (subpath.Points.Length < 3)
                continue;
            var points = new Vector2[subpath.Points.Length];
            for (var i = 0; i < points.Length; i++)
                points[i] = transform.Apply(subpath.Points[i]);
            polygons.Add(points);
        }
        return polygons;
    }

    // ---- stroking ----

    /// <summary>
    /// The stroke of subpaths (in the space the path is drawn in) as polygons that all wind the same way, so that filling them
    /// non-zero gives their union: a rectangle per segment, a join per corner, a cap at each open end.
    /// </summary>
    public static List<Vector2[]> Stroke(IReadOnlyList<Subpath> subpaths, in Stroke stroke, float tolerance)
    {
        var polygons = new List<Vector2[]>();
        var half = stroke.Width / 2;
        if (!(half > 0) || !float.IsFinite(half))
            return polygons;

        foreach (var subpath in subpaths)
        {
            var points = Deduplicate(subpath.Points, subpath.Closed);
            var closed = subpath.Closed && points.Count > 2;
            if (stroke.Dashes is { Count: > 0 } dashes && dashes.All(d => d >= 0 && float.IsFinite(d)) && dashes.Sum() > 0)
            {
                foreach (var piece in Dash(points, closed, dashes, stroke.DashOffset))
                    StrokeOne(polygons, Deduplicate([.. piece], false), false, half, stroke, tolerance);
            }
            else
                StrokeOne(polygons, points, closed, half, stroke, tolerance);
        }
        return polygons;
    }

    private static List<Vector2> Deduplicate(Vector2[] points, bool closed)
    {
        var result = new List<Vector2>(points.Length);
        foreach (var p in points)
        {
            if (result.Count == 0 || (p - result[^1]).LengthSquared() > 1e-12f)
                result.Add(p);
        }
        if (closed && result.Count > 1 && (result[0] - result[^1]).LengthSquared() <= 1e-12f)
            result.RemoveAt(result.Count - 1);
        return result;
    }

    // The pieces of a polyline the dash pattern leaves: an even-length pattern of dash, gap, dash, gap, ..., started offset
    // into it. A zero-length dash is a dot (the caps draw it); a zero-length gap joins its neighbours.
    private static IEnumerable<List<Vector2>> Dash(List<Vector2> points, bool closed, IReadOnlyList<float> pattern, float offset)
    {
        var lengths = pattern.Count % 2 == 1 ? [.. pattern, .. pattern] : pattern.ToArray();
        var total = lengths.Sum();
        var line = new List<Vector2>(points);
        if (closed)
            line.Add(points[0]);

        var position = ((offset % total) + total) % total;
        var index = 0;
        while (position > lengths[index])
        {
            position -= lengths[index];
            index = (index + 1) % lengths.Length;
        }
        var remaining = lengths[index] - position;
        var on = index % 2 == 0;
        var current = on ? new List<Vector2> { line[0] } : null;
        var pieces = 0;

        for (var i = 1; i < line.Count; i++)
        {
            var from = line[i - 1];
            var to = line[i];
            var length = (to - from).Length();
            var travelled = 0f;
            while (true)
            {
                var left = length - travelled;
                if (remaining > left)
                {
                    remaining -= left;
                    current?.Add(to);
                    break;
                }
                travelled += remaining;
                var point = length > 0 ? from + (to - from) * (travelled / length) : to;
                if (on)
                {
                    current!.Add(point);
                    yield return current;
                    current = null;
                    if (++pieces > 200_000)
                        yield break;
                }
                else
                    current = [point];
                on = !on;
                index = (index + 1) % lengths.Length;
                remaining = lengths[index];
            }
        }
        if (on && current is { Count: > 1 })
            yield return current;
    }

    private static void StrokeOne(List<Vector2[]> output, List<Vector2> points, bool closed, float half, in Stroke stroke, float tolerance)
    {
        if (points.Count == 0)
            return;
        if (points.Count == 1)
        {
            // A point: a round cap makes a dot and a square one a square; a butt cap draws nothing.
            if (stroke.Cap == LineCap.Round)
                output.Add(Circle(points[0], half, tolerance));
            else if (stroke.Cap == LineCap.Square)
                output.Add(Orient([points[0] + new Vector2(-half, -half), points[0] + new Vector2(half, -half), points[0] + new Vector2(half, half), points[0] + new Vector2(-half, half)]));
            return;
        }

        var segments = closed ? points.Count : points.Count - 1;
        var directions = new Vector2[segments];
        for (var i = 0; i < segments; i++)
            directions[i] = Vector2.Normalize(points[(i + 1) % points.Count] - points[i]);

        for (var i = 0; i < segments; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            var n = new Vector2(-directions[i].Y, directions[i].X) * half;
            output.Add(Orient([a + n, b + n, b - n, a - n]));
        }

        // Joins at the corners between consecutive segments.
        for (var i = closed ? 0 : 1; i < (closed ? points.Count : points.Count - 1); i++)
        {
            var d1 = directions[(i - 1 + segments) % segments];
            var d2 = directions[i % segments];
            Join(output, points[i], d1, d2, half, stroke, tolerance);
        }

        if (closed)
            return;
        Cap(output, points[0], -directions[0], half, stroke.Cap, tolerance);
        Cap(output, points[^1], directions[^1], half, stroke.Cap, tolerance);
    }

    // direction points away from the line, out past the end.
    private static void Cap(List<Vector2[]> output, Vector2 end, Vector2 direction, float half, LineCap cap, float tolerance)
    {
        if (cap == LineCap.Round)
            output.Add(Circle(end, half, tolerance));
        else if (cap == LineCap.Square)
        {
            var n = new Vector2(-direction.Y, direction.X) * half;
            var forward = direction * half;
            output.Add(Orient([end + n, end + n + forward, end - n + forward, end - n]));
        }
    }

    private static void Join(List<Vector2[]> output, Vector2 at, Vector2 d1, Vector2 d2, float half, in Stroke stroke, float tolerance)
    {
        var cross = d1.X * d2.Y - d1.Y * d2.X;
        var dot = Vector2.Dot(d1, d2);
        if (MathF.Abs(cross) < 1e-6f && dot > 0)
            return; // straight on
        if (stroke.Join == LineJoin.Round)
        {
            output.Add(Circle(at, half, tolerance));
            return;
        }

        // The outer side of the turn.
        var side = cross > 0 ? -1f : 1f;
        var n1 = new Vector2(-d1.Y, d1.X) * half * side;
        var n2 = new Vector2(-d2.Y, d2.X) * half * side;
        var o1 = at + n1;
        var o2 = at + n2;
        if (stroke.Join == LineJoin.Miter && dot > -0.9999f)
        {
            // The miter's length over the stroke width is 1 / cos(turn / 2); it is used up to the limit.
            var cosHalfTurn = MathF.Sqrt(Math.Max((1 + dot) / 2, 1e-12f));
            if (1 / cosHalfTurn <= stroke.MiterLimit)
            {
                var tip = at + (n1 + n2) / (1 + dot);
                output.Add(Orient([at, o1, tip, o2]));
                return;
            }
        }
        output.Add(Orient([at, o1, o2]));
    }

    private static Vector2[] Circle(Vector2 center, float radius, float tolerance)
    {
        var steps = (int)Math.Clamp(MathF.Ceiling(MathF.PI / MathF.Acos(Math.Max(0, 1 - tolerance / Math.Max(radius, tolerance)))), 8, 256);
        var points = new Vector2[steps];
        for (var i = 0; i < steps; i++)
        {
            var angle = 2 * MathF.PI * i / steps;
            points[i] = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
        }
        return points;
    }

    // Winding positive (a negative signed area is reversed), so that overlapping pieces add up instead of cancelling.
    private static Vector2[] Orient(Vector2[] polygon)
    {
        double area = 0;
        for (var i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Length];
            area += (double)a.X * b.Y - (double)b.X * a.Y;
        }
        if (area < 0)
            Array.Reverse(polygon);
        return polygon;
    }
}
