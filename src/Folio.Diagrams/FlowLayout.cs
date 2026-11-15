namespace Folio.Diagrams;

/// <summary>
/// Layered layout and orthogonal edge routing for flowcharts. Everything is computed on an abstract
/// (main, cross) axis pair, where the main axis runs along the flow (x for LR/RL, y for TB/BT), and only
/// mapped to x/y at the end. The result is a pure function of the input, so output is deterministic.
/// </summary>
internal static class FlowLayout
{
    /// <summary>Space kept free around the content.</summary>
    public const double Margin = 16;

    /// <summary>Largest width and height an SVG is displayed at; bigger content is scaled down, never clipped.</summary>
    public const double MaxWidth = 1600, MaxHeight = 1600;

    private const double NodeHeight = 44, MinNodeWidth = 100, MinLayerGap = 56, NodeGap = 28, DummyGap = 14, DummySize = 6;
    private const double LabelPadding = 12, LabelHeight = 20, LabelClearance = 28, LoopSize = 22, PairOffset = 11;
    private const double GroupPadding = 14, GroupTitle = 20;

    internal sealed record NodeSpec(string Label, string Shape);
    internal sealed record EdgeSpec(int From, int To, string Label);
    internal sealed record Box(double X, double Y, double Width, double Height);
    internal sealed record Route(IReadOnlyList<(double X, double Y)> Points, Box? Label);
    internal sealed record Result(IReadOnlyList<Box> Nodes, IReadOnlyList<Route> Routes, IReadOnlyList<Box?> Groups, double Width, double Height);

    /// <summary>Estimated text width; the layout has no font metrics, so this errs on the wide side.</summary>
    public static double TextWidth(string text, double perChar)
    {
        var width = 0.0;
        foreach (var c in text) width += c >= 0x2E80 ? perChar * 1.75 : perChar;
        return width;
    }

    public static (double Width, double Height) NodeSize(NodeSpec node)
    {
        var text = TextWidth(node.Label, 8);
        return node.Shape switch
        {
            "diamond" => (Math.Max(120, text * 1.3 + 40), 64),
            "circle" => (Math.Max(60, text + 24), Math.Max(60, text + 24)),
            _ => (Math.Max(MinNodeWidth, text + 32), NodeHeight),
        };
    }

    /// <summary>
    /// Distance from a node's centre to its outline along the main axis, on the line <paramref name="offset"/> away from
    /// the centre on the cross axis. <paramref name="halfMain"/> and <paramref name="halfCross"/> are half the node's size on each axis.
    /// </summary>
    public static double Reach(string shape, double halfMain, double halfCross, double offset)
    {
        var o = Math.Min(Math.Abs(offset), halfCross);
        switch (shape)
        {
            case "diamond":
                return halfMain * (1 - o / halfCross);
            case "circle":
                return Math.Sqrt(Math.Max(0, halfCross * halfCross - o * o));
            case "round":
                var r = Math.Min(halfMain, halfCross);
                var flat = Math.Max(0, halfCross - r);
                var d = Math.Max(0, o - flat);
                return halfMain - r + Math.Sqrt(Math.Max(0, r * r - d * d));
            default:
                // ponytail: the 8 px corner radius of plain boxes is ignored; ports stay near the middle of a side
                return halfMain;
        }
    }

    private sealed class Item
    {
        public int Node = -1;
        public int Layer;
        public double Main, Cross, MainCentre, CrossCentre;
        public readonly List<Item> Preds = [], Succs = [];
        public bool Dummy => Node < 0;
    }

    public static Result Compute(IReadOnlyList<NodeSpec> specs, IReadOnlyList<EdgeSpec> edges, IReadOnlyList<int[]> groups,
        IReadOnlyList<string> groupTitles, string direction)
    {
        var horizontal = direction is "LR" or "RL";
        var mirror = direction is "RL" or "BT";
        var n = specs.Count;
        var size = specs.Select(NodeSize).ToArray();

        // 1. Layers: longest path over the edges with cycles broken by reversing DFS back edges.
        var back = FindBackEdges(n, edges);
        var layer = Layers(n, edges, back);

        // 2. Items: one per node, plus one dummy per layer an edge skips, so long edges get a lane of their own.
        var items = new List<Item>();
        for (var i = 0; i < n; i++)
        {
            var (w, h) = size[i];
            items.Add(new Item { Node = i, Layer = layer[i], Main = horizontal ? w : h, Cross = horizontal ? h : w });
        }
        var chains = new Item?[edges.Count][];
        for (var e = 0; e < edges.Count; e++)
        {
            if (edges[e].From == edges[e].To) continue;
            var (a, b) = back[e] ? (edges[e].To, edges[e].From) : (edges[e].From, edges[e].To);
            var chain = new List<Item> { items[a] };
            for (var l = layer[a] + 1; l < layer[b]; l++)
            {
                var dummy = new Item { Layer = l, Cross = DummySize };
                items.Add(dummy);
                chain.Add(dummy);
            }
            chain.Add(items[b]);
            for (var k = 0; k + 1 < chain.Count; k++) { chain[k].Succs.Add(chain[k + 1]); chain[k + 1].Preds.Add(chain[k]); }
            chains[e] = [.. chain];
        }
        var layerCount = n == 0 ? 0 : layer.Max() + 1;
        var layers = Enumerable.Range(0, layerCount).Select(l => items.Where(item => item.Layer == l).ToList()).ToArray();

        // 3. Order within layers to cut crossings: down, up, down barycentre sweeps (stable, so ties keep input order).
        Sweep(layers, down: true);
        Sweep(layers, down: false);
        Sweep(layers, down: true);

        // 4. Cross-axis positions: pack each layer, then centre it on what its predecessors say.
        foreach (var list in layers)
        {
            double? previousEnd = null;
            var desired = new double?[list.Count];
            for (var i = 0; i < list.Count; i++)
            {
                var item = list[i];
                desired[i] = item.Preds.Count == 0 ? null : item.Preds.Average(p => p.CrossCentre);
                var start = desired[i] is { } d ? d - item.Cross / 2 : previousEnd ?? 0;
                if (previousEnd is { } end)
                    start = Math.Max(start, end + (item.Dummy || list[i - 1].Dummy ? DummyGap : NodeGap));
                item.CrossCentre = start + item.Cross / 2;
                previousEnd = start + item.Cross;
            }
            var known = Enumerable.Range(0, list.Count).Where(i => desired[i] is not null).ToArray();
            // Real nodes set the shift; dummies (edge lanes) only count when a layer has nothing else, so nodes line up with their neighbours.
            var real = known.Where(i => !list[i].Dummy).ToArray();
            var anchors = real.Length > 0 ? real : known;
            var shift = anchors.Length == 0 ? 0 : anchors.Average(i => desired[i]!.Value - list[i].CrossCentre);
            foreach (var item in list) item.CrossCentre += shift;
        }

        // 5. Main-axis positions. A layer gap grows to fit the label of an edge that is drawn across it.
        var labelSize = edges.Select(e => e.Label.Length == 0 ? null : new Box(0, 0, TextWidth(e.Label, 6.8) + LabelPadding, LabelHeight)).ToArray();
        var gaps = Enumerable.Repeat(MinLayerGap, Math.Max(0, layerCount - 1)).ToArray();
        for (var e = 0; e < edges.Count; e++)
        {
            if (labelSize[e] is not { } label) continue;
            var extent = horizontal ? label.Width : label.Height;
            if (chains[e] is { } chain)
            {
                var gap = chain[0]!.Layer + (chain.Length - 2) / 2;
                gaps[gap] = Math.Max(gaps[gap], horizontal ? extent + LabelClearance : 2 * extent + LabelClearance);
            }
            else if (layer[edges[e].From] < gaps.Length)
                gaps[layer[edges[e].From]] = Math.Max(gaps[layer[edges[e].From]], LoopSize + 6 + extent + LabelClearance / 2);
        }
        var layerMain = layers.Select(list => list.Max(item => item.Main)).ToArray();
        var layerStart = new double[layerCount];
        for (var l = 1; l < layerCount; l++) layerStart[l] = layerStart[l - 1] + layerMain[l - 1] + gaps[l - 1];
        foreach (var item in items) item.MainCentre = layerStart[item.Layer] + layerMain[item.Layer] / 2;
        var totalMain = layerCount == 0 ? 0 : layerStart[^1] + layerMain[^1];

        // 6. Routes: leave a node on the side facing the flow, jog in the middle of the gap, enter the next one the same way.
        // Two edges between the same pair of nodes run side by side instead of on top of each other.
        var paired = edges.Select(e => e.From != e.To && edges.Any(o => o.From == e.To && o.To == e.From)).ToArray();
        var routes = new List<(List<(double M, double C)> Points, (double M, double C)? Label, Box? LabelBox)>();
        for (var e = 0; e < edges.Count; e++)
        {
            var points = new List<(double M, double C)>();
            (double M, double C)? labelAt = null;
            if (chains[e] is not { } chain)
            {
                var item = items[edges[e].From];
                var (hm, hc) = (item.Main / 2, item.Cross / 2);
                var out1 = item.MainCentre + Reach(specs[item.Node].Shape, hm, hc, -8);
                var out2 = item.MainCentre + Reach(specs[item.Node].Shape, hm, hc, 8);
                var far = item.MainCentre + hm + LoopSize;
                points.AddRange([(out1, item.CrossCentre - 8), (far, item.CrossCentre - 8), (far, item.CrossCentre + 8), (out2, item.CrossCentre + 8)]);
                labelAt = (far + 6 + (labelSize[e] is { } ls ? (horizontal ? ls.Width : ls.Height) / 2 : 0), item.CrossCentre);
            }
            else
            {
                var offset = !paired[e] ? 0 : back[e] ? PairOffset : -PairOffset;
                var stagger = paired[e] && back[e] ? 10 : 0;
                var middle = (chain.Length - 2) / 2;
                for (var k = 0; k + 1 < chain.Length; k++)
                {
                    var (a, b) = (chain[k]!, chain[k + 1]!);
                    var aOut = a.Dummy ? a.MainCentre : a.MainCentre + Reach(specs[a.Node].Shape, a.Main / 2, a.Cross / 2, offset);
                    var bIn = b.Dummy ? b.MainCentre : b.MainCentre - Reach(specs[b.Node].Shape, b.Main / 2, b.Cross / 2, offset);
                    var ca = a.Dummy ? a.CrossCentre : a.CrossCentre + offset;
                    var cb = b.Dummy ? b.CrossCentre : b.CrossCentre + offset;
                    // The jog sits mid-gap for LR/RL. For TB/BT it moves toward the source, which keeps the
                    // labels (on the target-side stub, or mid-edge when straight) clear of the line the branches share.
                    var gapStart = layerStart[a.Layer] + layerMain[a.Layer];
                    var mid = gapStart + (layerStart[b.Layer] - gapStart) * (horizontal ? 0.5 : 0.25) + stagger;
                    points.AddRange([(aOut, ca), (mid, ca), (mid, cb), (bIn, cb)]);
                    if (k == middle)
                        labelAt = Math.Abs(ca - cb) < 0.01 ? ((aOut + bIn) / 2, ca) : horizontal ? (mid, (ca + cb) / 2) : ((mid + bIn) / 2, cb);
                }
                points = Simplify(points);
                if (back[e]) points.Reverse();
            }
            routes.Add((points, labelAt, labelSize[e]));
        }

        // 7. Map to x/y, mirror for RL/BT, then shift everything so the content starts at the margin.
        (double X, double Y) ToXY((double M, double C) p)
        {
            var m = mirror ? totalMain - p.M : p.M;
            return horizontal ? (m, p.C) : (p.C, m);
        }
        static Box Centred((double X, double Y) centre, Box size) =>
            new(centre.X - size.Width / 2, centre.Y - size.Height / 2, size.Width, size.Height);
        var nodeBoxes = new Box[n];
        foreach (var item in items.Where(item => !item.Dummy))
        {
            var (cx, cy) = ToXY((item.MainCentre, item.CrossCentre));
            nodeBoxes[item.Node] = new Box(cx - size[item.Node].Width / 2, cy - size[item.Node].Height / 2, size[item.Node].Width, size[item.Node].Height);
        }
        var routeXY = routes.Select(r => (
            Points: r.Points.Select(ToXY).ToList(),
            Label: r.Label is { } at && r.LabelBox is { } lb ? Centred(ToXY(at), lb) : null)).ToList();
        var groupBoxes = groups.Select((members, g) =>
        {
            var boxes = members.Where(i => i >= 0 && i < n).Select(i => nodeBoxes[i]).ToArray();
            if (boxes.Length == 0) return null;
            var x = boxes.Min(b => b.X) - GroupPadding; var y = boxes.Min(b => b.Y) - GroupPadding - GroupTitle;
            var right = Math.Max(boxes.Max(b => b.X + b.Width) + GroupPadding, x + TextWidth(groupTitles[g], 8) + 16);
            return new Box(x, y, right - x, boxes.Max(b => b.Y + b.Height) + GroupPadding - y);
        }).ToList();

        var all = nodeBoxes.Concat(routeXY.Where(r => r.Label is not null).Select(r => r.Label!)).Concat(groupBoxes.OfType<Box>()).ToList();
        var pointsXY = routeXY.SelectMany(r => r.Points).ToList();
        if (all.Count == 0 && pointsXY.Count == 0) return new Result([], [], [], 2 * Margin, 2 * Margin);
        var minX = Math.Min(all.Count == 0 ? double.MaxValue : all.Min(b => b.X), pointsXY.Count == 0 ? double.MaxValue : pointsXY.Min(p => p.X));
        var minY = Math.Min(all.Count == 0 ? double.MaxValue : all.Min(b => b.Y), pointsXY.Count == 0 ? double.MaxValue : pointsXY.Min(p => p.Y));
        var maxX = Math.Max(all.Count == 0 ? double.MinValue : all.Max(b => b.X + b.Width), pointsXY.Count == 0 ? double.MinValue : pointsXY.Max(p => p.X));
        var maxY = Math.Max(all.Count == 0 ? double.MinValue : all.Max(b => b.Y + b.Height), pointsXY.Count == 0 ? double.MinValue : pointsXY.Max(p => p.Y));
        double dx = Margin - minX, dy = Margin - minY;
        Box Shift(Box b) => new(b.X + dx, b.Y + dy, b.Width, b.Height);
        return new Result(
            nodeBoxes.Select(Shift).ToList(),
            routeXY.Select(r => new Route(r.Points.Select(p => (p.X + dx, p.Y + dy)).ToList(), r.Label is null ? null : Shift(r.Label))).ToList(),
            groupBoxes.Select(b => b is null ? null : Shift(b)).ToList(),
            Math.Ceiling(maxX - minX + 2 * Margin), Math.Ceiling(maxY - minY + 2 * Margin));
    }

    /// <summary>Marks the edges that close a cycle in a depth-first walk (nodes and edges taken in input order).</summary>
    private static bool[] FindBackEdges(int n, IReadOnlyList<EdgeSpec> edges)
    {
        var back = new bool[edges.Count];
        var outgoing = Enumerable.Range(0, n).Select(_ => new List<int>()).ToArray();
        for (var e = 0; e < edges.Count; e++)
            if (edges[e].From != edges[e].To) outgoing[edges[e].From].Add(e);
        var state = new int[n]; // 0 new, 1 on the walk, 2 done
        var stack = new Stack<(int Node, int Next)>();
        for (var root = 0; root < n; root++)
        {
            if (state[root] != 0) continue;
            state[root] = 1;
            stack.Push((root, 0));
            while (stack.Count > 0)
            {
                var (u, k) = stack.Pop();
                if (k >= outgoing[u].Count) { state[u] = 2; continue; }
                stack.Push((u, k + 1));
                var e = outgoing[u][k];
                var v = edges[e].To;
                if (state[v] == 1) back[e] = true;
                else if (state[v] == 0) { state[v] = 1; stack.Push((v, 0)); }
            }
        }
        return back;
    }

    private static int[] Layers(int n, IReadOnlyList<EdgeSpec> edges, bool[] back)
    {
        var successors = Enumerable.Range(0, n).Select(_ => new List<int>()).ToArray();
        var incoming = new int[n];
        for (var e = 0; e < edges.Count; e++)
        {
            if (edges[e].From == edges[e].To) continue;
            var (a, b) = back[e] ? (edges[e].To, edges[e].From) : (edges[e].From, edges[e].To);
            successors[a].Add(b);
            incoming[b]++;
        }
        var layer = new int[n];
        var queue = new Queue<int>(Enumerable.Range(0, n).Where(i => incoming[i] == 0));
        while (queue.Count > 0)
        {
            var u = queue.Dequeue();
            foreach (var v in successors[u])
            {
                layer[v] = Math.Max(layer[v], layer[u] + 1);
                if (--incoming[v] == 0) queue.Enqueue(v);
            }
        }
        return layer;
    }

    private static void Sweep(List<Item>[] layers, bool down)
    {
        var position = new Dictionary<Item, int>();
        foreach (var list in layers)
            for (var i = 0; i < list.Count; i++) position[list[i]] = i;
        var order = down ? Enumerable.Range(1, Math.Max(0, layers.Length - 1)) : Enumerable.Range(0, Math.Max(0, layers.Length - 1)).Reverse();
        foreach (var l in order)
        {
            double Key(Item item)
            {
                var neighbours = down ? item.Preds : item.Succs;
                return neighbours.Count == 0 ? position[item] : neighbours.Average(p => position[p]);
            }
            layers[l] = [.. layers[l].OrderBy(Key)];
            for (var i = 0; i < layers[l].Count; i++) position[layers[l][i]] = i;
        }
    }

    /// <summary>Drops repeated points and the middle point of straight runs.</summary>
    private static List<(double M, double C)> Simplify(List<(double M, double C)> points)
    {
        var result = new List<(double M, double C)>();
        foreach (var p in points)
        {
            if (result.Count > 0 && Math.Abs(result[^1].M - p.M) < 0.01 && Math.Abs(result[^1].C - p.C) < 0.01) continue;
            while (result.Count > 1 && Collinear(result[^2], result[^1], p)) result.RemoveAt(result.Count - 1);
            result.Add(p);
        }
        return result;
    }

    private static bool Collinear((double M, double C) a, (double M, double C) b, (double M, double C) c) =>
        (Math.Abs(a.M - b.M) < 0.01 && Math.Abs(b.M - c.M) < 0.01) || (Math.Abs(a.C - b.C) < 0.01 && Math.Abs(b.C - c.C) < 0.01);
}
