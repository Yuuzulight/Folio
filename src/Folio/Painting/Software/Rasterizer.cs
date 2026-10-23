using System.Numerics;

namespace Folio.Painting.Software;

/// <summary>A rectangle of whole pixels: Left and Top inclusive, Right and Bottom exclusive.</summary>
internal readonly record struct IntRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public bool IsEmpty => Right <= Left || Bottom <= Top;

    public static IntRect Intersect(IntRect a, IntRect b) => new(Math.Max(a.Left, b.Left), Math.Max(a.Top, b.Top), Math.Min(a.Right, b.Right), Math.Min(a.Bottom, b.Bottom));
}

/// <summary>How much of each pixel of a rectangle a shape covers, 0 to 255.</summary>
internal sealed class Coverage(IntRect bounds, byte[] data)
{
    public IntRect Bounds { get; } = bounds;

    public byte[] Data { get; } = data;

    public byte At(int x, int y) => Data[(y - Bounds.Top) * Bounds.Width + (x - Bounds.Left)];
}

/// <summary>
/// Turns closed polygons into pixel coverage. Each pixel row is sampled at sixteen heights; at each, the places the
/// outline crosses the sample line are sorted and walked with the fill rule, and each inside span adds its exact
/// horizontal overlap with the pixels it touches. Vertical resolution is thus 1/16 and horizontal is exact.
/// </summary>
internal static class Rasterizer
{
    private const int Samples = 16;

    private struct Edge
    {
        public float X0, Y0, Y1, Slope;
        public int Direction;
    }

    /// <summary>
    /// The coverage of the polygons (device coordinates, each implicitly closed) inside <paramref name="clip"/>, or null when none.
    /// With <paramref name="snapEdges"/>, the ends of every edge but those of an upright rectangle are moved to the nearest quarter
    /// pixel in y, as Skia's analytic scan converter does for a path, so that slanted edges land where Skia puts them.
    /// </summary>
    public static Coverage? Fill(IReadOnlyList<Vector2[]> polygons, FillRule rule, IntRect clip, bool snapEdges = false)
    {
        var minX = float.MaxValue;
        var minY = float.MaxValue;
        var maxX = float.MinValue;
        var maxY = float.MinValue;
        var edgeCount = 0;
        var usable = new List<Vector2[]>(polygons.Count);
        foreach (var polygon in polygons)
        {
            // A polygon with a point that is not a number (a degenerate stroke, say) is left out, not the whole shape.
            if (polygon.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y)))
                continue;
            foreach (var p in polygon)
            {
                minX = Math.Min(minX, p.X);
                maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y);
                maxY = Math.Max(maxY, p.Y);
            }
            edgeCount += polygon.Length;
            usable.Add(polygon);
        }
        if (edgeCount == 0)
            return null;
        polygons = usable;

        var bounds = IntRect.Intersect(clip, new IntRect((int)MathF.Floor(minX), (int)MathF.Floor(minY), (int)MathF.Ceiling(maxX), (int)MathF.Ceiling(maxY)));
        if (bounds.IsEmpty)
            return null;

        var edges = new List<Edge>(edgeCount);
        foreach (var polygon in polygons)
        {
            var snap = snapEdges && !IsUprightRectangle(polygon);
            for (var i = 0; i < polygon.Length; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Length];
                if (snap)
                {
                    a.Y = SnapY(a.Y);
                    b.Y = SnapY(b.Y);
                }
                if (a.Y == b.Y)
                    continue;
                var direction = 1;
                if (a.Y > b.Y)
                {
                    (a, b) = (b, a);
                    direction = -1;
                }
                if (b.Y <= bounds.Top || a.Y >= bounds.Bottom)
                    continue;
                edges.Add(new Edge { X0 = a.X, Y0 = a.Y, Y1 = b.Y, Slope = (b.X - a.X) / (b.Y - a.Y), Direction = direction });
            }
        }
        if (edges.Count == 0)
            return null;
        edges.Sort((p, q) => p.Y0.CompareTo(q.Y0));

        var width = bounds.Width;
        var data = new byte[width * bounds.Height];
        var row = new float[width + 2];
        var runs = new float[width + 2];
        var active = new List<int>();
        var crossings = new List<(float X, int Direction)>();
        var next = 0;

        for (var y = bounds.Top; y < bounds.Bottom; y++)
        {
            var touched = false;
            for (var s = 0; s < Samples; s++)
            {
                var ys = y + (s + 0.5f) / Samples;
                while (next < edges.Count && edges[next].Y0 <= ys)
                    active.Add(next++);
                crossings.Clear();
                for (var k = active.Count - 1; k >= 0; k--)
                {
                    var e = edges[active[k]];
                    if (e.Y1 <= ys)
                    {
                        active.RemoveAt(k);
                        continue;
                    }
                    crossings.Add((e.X0 + (ys - e.Y0) * e.Slope - bounds.Left, e.Direction));
                }
                if (crossings.Count < 2)
                    continue;
                crossings.Sort((p, q) => p.X.CompareTo(q.X));

                var winding = 0;
                var spanStart = 0f;
                var inside = false;
                foreach (var (x, direction) in crossings)
                {
                    winding += direction;
                    var nowInside = rule == FillRule.EvenOdd ? (winding & 1) != 0 : winding != 0;
                    if (nowInside && !inside)
                        spanStart = x;
                    else if (!nowInside && inside)
                    {
                        AddSpan(row, runs, width, spanStart, x, 1f / Samples);
                        touched = true;
                    }
                    inside = nowInside;
                }
            }

            if (!touched)
                continue;
            var running = 0f;
            var offset = (y - bounds.Top) * width;
            for (var x = 0; x < width; x++)
            {
                running += runs[x];
                var c = row[x] + running;
                data[offset + x] = c >= 0.998f ? (byte)255 : c <= 0.002f ? (byte)0 : (byte)MathF.Round(c * 255);
                row[x] = 0;
                runs[x] = 0;
            }
            runs[width] = 0;
            runs[width + 1] = 0;
            row[width] = 0;
            row[width + 1] = 0;
        }
        return new Coverage(bounds, data);
    }

    private static float SnapY(float y) => MathF.Floor(y * 4 + 0.5f) / 4;

    private static bool IsUprightRectangle(Vector2[] polygon)
    {
        if (polygon.Length != 4)
            return false;
        for (var i = 0; i < 4; i++)
        {
            var (a, b) = (polygon[i], polygon[(i + 1) % 4]);
            if ((a.X == b.X) == (a.Y == b.Y))
                return false;
        }
        return true;
    }

    private static void AddSpan(float[] row, float[] runs, int width, float from, float to, float weight)
    {
        if (from < 0)
            from = 0;
        if (to > width)
            to = width;
        if (to <= from)
            return;
        var a = (int)from;
        var b = (int)to;
        if (a == b)
        {
            row[a] += (to - from) * weight;
            return;
        }
        row[a] += (a + 1 - from) * weight;
        runs[a + 1] += weight;
        runs[b] -= weight;
        row[b] += (to - b) * weight;
    }
}
