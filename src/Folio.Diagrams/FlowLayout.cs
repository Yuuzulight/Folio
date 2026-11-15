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
    private const double LabelHeight = 20, LabelPadding = 12, LabelClearance = 28, LoopSize = 22, LaneSpacing = 12, PortSpacing = 14;
    private const double GroupPadding = 14, GroupTitle = 20, GroupSpacing = 2 * GroupPadding + GroupTitle;
    private const double LabelFontSize = 12, NodeFontSize = 14;

    internal sealed record NodeSpec(string Label, string Shape);
    internal sealed record EdgeSpec(int From, int To, string Label);
    internal sealed record Box(double X, double Y, double Width, double Height);
    internal sealed record Route(IReadOnlyList<(double X, double Y)> Points, Box? Label);
    internal sealed record Result(IReadOnlyList<Box> Nodes, IReadOnlyList<Route> Routes, IReadOnlyList<Box?> Groups, double Width, double Height);

    public static double TextWidth(string text, double size) => TextMeasure.Width(text, size, bold: true);

    public static (double Width, double Height) NodeSize(NodeSpec node)
    {
        var text = TextWidth(node.Label, NodeFontSize);
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
        public int Node = -1, Group = -1;
        public int Layer;
        public double Main, Cross, MainCentre, CrossCentre;
        public readonly List<Item> Preds = [], Succs = [];
        public bool Dummy => Node < 0;
    }

    private sealed class Step
    {
        public int Edge, Index;
        public Item From = null!, To = null!;
        public double OutOffset, InOffset, FromCross, ToCross, Lane;
        public bool Jogs => Math.Abs(FromCross - ToCross) > 0.01;
    }

    public static Result Compute(IReadOnlyList<NodeSpec> specs, IReadOnlyList<EdgeSpec> edges, IReadOnlyList<int[]> groups,
        IReadOnlyList<string> groupTitles, string direction)
    {
        var horizontal = direction is "LR" or "RL";
        var mirror = direction is "RL" or "BT";
        var n = specs.Count;
        var size = specs.Select(NodeSize).ToArray();
        var groupOf = Enumerable.Repeat(-1, n).ToArray();
        for (var g = groups.Count - 1; g >= 0; g--)
            foreach (var i in groups[g]) if (i >= 0 && i < n) groupOf[i] = g;

        // 1. Layers: longest path over the edges with cycles broken by reversing DFS back edges.
        var back = FindBackEdges(n, edges);
        var layer = Layers(n, edges, back);

        // 2. Items: one per node, plus one dummy per layer an edge skips, so long edges have a place in every layer.
        var items = new List<Item>();
        for (var i = 0; i < n; i++)
        {
            var (w, h) = size[i];
            items.Add(new Item { Node = i, Group = groupOf[i], Layer = layer[i], Main = horizontal ? w : h, Cross = horizontal ? h : w });
        }
        var chains = new Item[edges.Count][];
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

        // 3. Order within layers to cut crossings: down, up, down barycentre sweeps (stable, so ties keep input order),
        //    keeping the members of a subgraph next to each other.
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
                // Lanes of long edges are placed later, so only real predecessors say where a node belongs when it has any.
                var guides = item.Preds.Any(p => !p.Dummy) ? item.Preds.Where(p => !p.Dummy).ToList() : item.Preds;
                desired[i] = guides.Count == 0 ? null : guides.Average(p => p.CrossCentre);
                var start = desired[i] is { } d ? d - item.Cross / 2 : previousEnd ?? 0;
                if (previousEnd is { } end)
                {
                    var previous = list[i - 1];
                    var gap = item.Dummy || previous.Dummy ? DummyGap : NodeGap;
                    if (item.Group != previous.Group) gap += GroupSpacing;
                    start = Math.Max(start, end + gap);
                }
                item.CrossCentre = start + item.Cross / 2;
                previousEnd = start + item.Cross;
            }
            var known = Enumerable.Range(0, list.Count).Where(i => desired[i] is not null).ToArray();
            // Real nodes set the shift; dummies only count when a layer has nothing else, so nodes line up with their neighbours.
            var real = known.Where(i => !list[i].Dummy).ToArray();
            var anchors = real.Length > 0 ? real : known;
            var shift = anchors.Length == 0 ? 0 : anchors.Average(i => desired[i]!.Value - list[i].CrossCentre);
            foreach (var item in list) item.CrossCentre += shift;
        }

        // 4b. A subgraph's box spans every layer its members sit in, so nodes of other layers that fall inside its cross range move away.
        KeepGroupsClear(groups, groupOf, items, layers);

        // 5. Channels for long edges: each runs straight through the layers it skips, on the nearest line that is clear of nodes.
        AssignChannels(chains, items, layers);

        // 6. Ports: every edge gets its own spot along the node border, ordered by where the edge is going.
        var steps = new List<Step>();
        for (var e = 0; e < edges.Count; e++)
            if (chains[e] is { } c)
                for (var k = 0; k + 1 < c.Length; k++) steps.Add(new Step { Edge = e, Index = k, From = c[k], To = c[k + 1] });
        foreach (var item in items.Where(i => !i.Dummy))
        {
            var half = item.Cross / 2;
            var outs = steps.Where(s => s.From == item).OrderBy(s => s.To.CrossCentre).ThenBy(s => s.Edge).ToList();
            var ins = steps.Where(s => s.To == item).OrderBy(s => s.From.CrossCentre).ThenBy(s => s.Edge).ToList();
            for (var i = 0; i < outs.Count; i++) outs[i].OutOffset = PortOffset(i, outs.Count, half);
            for (var i = 0; i < ins.Count; i++) ins[i].InOffset = PortOffset(i, ins.Count, half);
        }
        foreach (var s in steps)
        {
            s.FromCross = s.From.CrossCentre + s.OutOffset;
            s.ToCross = s.To.CrossCentre + s.InOffset;
        }

        // A jog of a few pixels looks like a mistake: when the end that is alone on its side can move, it lines up with the other end.
        foreach (var s in steps)
        {
            var difference = s.FromCross - s.ToCross;
            if (Math.Abs(difference) is < 0.01 or > 10) continue;
            if (!s.To.Dummy && steps.Count(t => t.To == s.To) == 1 && Math.Abs(s.FromCross - s.To.CrossCentre) <= s.To.Cross * 0.3)
            { s.InOffset = s.FromCross - s.To.CrossCentre; s.ToCross = s.FromCross; }
            else if (!s.From.Dummy && steps.Count(t => t.From == s.From) == 1 && Math.Abs(s.ToCross - s.From.CrossCentre) <= s.From.Cross * 0.3)
            { s.OutOffset = s.ToCross - s.From.CrossCentre; s.FromCross = s.ToCross; }
        }

        // 7. Main-axis positions. A layer gap grows to fit its jog lanes and the label of an edge drawn across it.
        var labelSize = edges.Select(e => e.Label.Length == 0 ? null : new Box(0, 0, TextWidth(e.Label, LabelFontSize) + LabelPadding, LabelHeight)).ToArray();
        var gaps = Enumerable.Repeat(MinLayerGap, Math.Max(0, layerCount - 1)).ToArray();
        var need = new double[gaps.Length];
        foreach (var byGap in steps.Where(s => s.Jogs).GroupBy(s => s.From.Layer))
            need[byGap.Key] += LaneSpacing * (byGap.Count() + 1);
        for (var e = 0; e < edges.Count; e++)
        {
            if (labelSize[e] is not { } label) continue;
            var extent = horizontal ? label.Width : label.Height;
            var gap = chains[e] is { } chain ? chain[0].Layer + (chain.Length - 2) / 2 : layer[edges[e].From];
            if (gap < need.Length) need[gap] += chains[e] is null ? extent + LoopSize + 6 : horizontal ? extent + LabelClearance : LabelHeight + 12;
        }
        for (var g = 0; g < gaps.Length; g++) gaps[g] = Math.Max(gaps[g], need[g]);
        var layerMain = layers.Select(list => list.Max(item => item.Main)).ToArray();
        var layerStart = new double[layerCount];
        for (var l = 1; l < layerCount; l++) layerStart[l] = layerStart[l - 1] + layerMain[l - 1] + gaps[l - 1];
        foreach (var item in items) item.MainCentre = layerStart[item.Layer] + layerMain[item.Layer] / 2;
        var totalMain = layerCount == 0 ? 0 : layerStart[^1] + layerMain[^1];

        // 8. Jog lanes: edges that change cross position inside a gap each get a lane of their own, ordered so they do not cross.
        foreach (var byGap in steps.Where(s => s.Jogs).GroupBy(s => s.From.Layer))
        {
            var up = byGap.Where(s => s.ToCross < s.FromCross).OrderBy(s => s.FromCross).ThenBy(s => s.Edge);
            var down = byGap.Where(s => s.ToCross > s.FromCross).OrderByDescending(s => s.FromCross).ThenBy(s => s.Edge);
            var ordered = up.Concat(down).ToList();
            var start = layerStart[byGap.Key] + layerMain[byGap.Key];
            var length = layerStart[byGap.Key + 1] - start;
            for (var i = 0; i < ordered.Count; i++) ordered[i].Lane = start + length * (i + 1) / (ordered.Count + 1);
        }

        // 9. Routes: leave a node on the side facing the flow, jog in the step's lane, enter the next one the same way.
        var routes = new List<(List<(double M, double C)> Points, Box? LabelBox)>();
        for (var e = 0; e < edges.Count; e++)
        {
            var points = new List<(double M, double C)>();
            if (chains[e] is null)
            {
                var item = items[edges[e].From];
                var (hm, hc) = (item.Main / 2, item.Cross / 2);
                var shape = specs[item.Node].Shape;
                var far = item.MainCentre + hm + LoopSize;
                points.AddRange([(item.MainCentre + Reach(shape, hm, hc, -8), item.CrossCentre - 8), (far, item.CrossCentre - 8),
                    (far, item.CrossCentre + 8), (item.MainCentre + Reach(shape, hm, hc, 8), item.CrossCentre + 8)]);
            }
            else
            {
                foreach (var s in steps.Where(s => s.Edge == e))
                {
                    var aOut = s.From.Dummy ? s.From.MainCentre : s.From.MainCentre + Reach(specs[s.From.Node].Shape, s.From.Main / 2, s.From.Cross / 2, s.OutOffset);
                    var bIn = s.To.Dummy ? s.To.MainCentre : s.To.MainCentre - Reach(specs[s.To.Node].Shape, s.To.Main / 2, s.To.Cross / 2, s.InOffset);
                    points.AddRange([(aOut, s.FromCross), (s.Jogs ? s.Lane : aOut, s.FromCross), (s.Jogs ? s.Lane : aOut, s.ToCross), (bIn, s.ToCross)]);
                }
                points = Simplify(points);
                if (back[e]) points.Reverse();
            }
            routes.Add((points, labelSize[e]));
        }

        // 10. Map to x/y and mirror for RL/BT.
        (double X, double Y) ToXY((double M, double C) p)
        {
            var m = mirror ? totalMain - p.M : p.M;
            return horizontal ? (m, p.C) : (p.C, m);
        }
        var nodeBoxes = new Box[n];
        foreach (var item in items.Where(item => !item.Dummy))
        {
            var (cx, cy) = ToXY((item.MainCentre, item.CrossCentre));
            nodeBoxes[item.Node] = new Box(cx - size[item.Node].Width / 2, cy - size[item.Node].Height / 2, size[item.Node].Width, size[item.Node].Height);
        }
        var paths = routes.Select(r => r.Points.Select(ToXY).ToList()).ToList();

        // 11. Labels sit beside their edge on a clear side; a plate over the line is the fallback.
        var obstacles = nodeBoxes.Select(b => Inflate(b, 4)).ToList();
        for (var e = 0; e < edges.Count; e++)
            for (var k = 0; k + 1 < paths[e].Count; k++)
                obstacles.Add(SegmentBox(paths[e][k], paths[e][k + 1], 3));
        foreach (var path in paths.Where(p => p.Count > 1))
            obstacles.Add(Inflate(SegmentBox(path[^1], Toward(path[^1], path[^2], 10), 0), 5));
        var labels = new Box?[edges.Count];
        for (var e = 0; e < edges.Count; e++)
        {
            if (routes[e].LabelBox is not { } size0) continue;
            labels[e] = PlaceLabel(paths[e], size0, obstacles);
            obstacles.Add(Inflate(labels[e]!, 2));
        }

        var groupBoxes = groups.Select((members, g) =>
        {
            var boxes = members.Where(i => i >= 0 && i < n).Select(i => nodeBoxes[i]).ToArray();
            if (boxes.Length == 0) return null;
            var x = boxes.Min(b => b.X) - GroupPadding; var y = boxes.Min(b => b.Y) - GroupPadding - GroupTitle;
            var right = Math.Max(boxes.Max(b => b.X + b.Width) + GroupPadding, x + TextWidth(groupTitles[g], NodeFontSize) + 16);
            return new Box(x, y, right - x, boxes.Max(b => b.Y + b.Height) + GroupPadding - y);
        }).ToList();

        // 12. Shift everything so the content starts at the margin.
        var all = nodeBoxes.Concat(labels.OfType<Box>()).Concat(groupBoxes.OfType<Box>()).ToList();
        var pointsXY = paths.SelectMany(p => p).ToList();
        if (all.Count == 0 && pointsXY.Count == 0) return new Result([], [], [], 2 * Margin, 2 * Margin);
        var minX = Math.Min(all.Count == 0 ? double.MaxValue : all.Min(b => b.X), pointsXY.Count == 0 ? double.MaxValue : pointsXY.Min(p => p.X));
        var minY = Math.Min(all.Count == 0 ? double.MaxValue : all.Min(b => b.Y), pointsXY.Count == 0 ? double.MaxValue : pointsXY.Min(p => p.Y));
        var maxX = Math.Max(all.Count == 0 ? double.MinValue : all.Max(b => b.X + b.Width), pointsXY.Count == 0 ? double.MinValue : pointsXY.Max(p => p.X));
        var maxY = Math.Max(all.Count == 0 ? double.MinValue : all.Max(b => b.Y + b.Height), pointsXY.Count == 0 ? double.MinValue : pointsXY.Max(p => p.Y));
        double dx = Margin - minX, dy = Margin - minY;
        Box Shift(Box b) => new(b.X + dx, b.Y + dy, b.Width, b.Height);
        return new Result(
            nodeBoxes.Select(Shift).ToList(),
            paths.Select((p, e) => new Route(p.Select(q => (q.X + dx, q.Y + dy)).ToList(), labels[e] is null ? null : Shift(labels[e]!))).ToList(),
            groupBoxes.Select(b => b is null ? null : Shift(b)).ToList(),
            Math.Ceiling(maxX - minX + 2 * Margin), Math.Ceiling(maxY - minY + 2 * Margin));
    }

    /// <summary>
    /// Moves nodes that are not in a group out of the group's box: the box covers the cross range of all its members, in every layer
    /// from the first to the last of them. Layers are then packed again, so nothing overlaps.
    /// </summary>
    private static void KeepGroupsClear(IReadOnlyList<int[]> groups, int[] groupOf, List<Item> items, List<Item>[] layers)
    {
        for (var pass = 0; pass < 8; pass++)
        {
            var moved = false;
            for (var g = 0; g < groups.Count; g++)
            {
                var members = items.Where(i => !i.Dummy && groupOf[i.Node] == g).ToList();
                if (members.Count == 0) continue;
                var low = members.Min(i => i.CrossCentre - i.Cross / 2) - GroupPadding - GroupTitle;
                var high = members.Max(i => i.CrossCentre + i.Cross / 2) + GroupPadding;
                var middle = (low + high) / 2;
                foreach (var item in items.Where(i => !i.Dummy && groupOf[i.Node] != g && i.Layer >= members.Min(m => m.Layer) && i.Layer <= members.Max(m => m.Layer)))
                {
                    if (item.CrossCentre + item.Cross / 2 <= low - 8 || item.CrossCentre - item.Cross / 2 >= high + 8) continue;
                    item.CrossCentre = item.CrossCentre >= middle ? high + 8 + item.Cross / 2 : low - 8 - item.Cross / 2;
                    moved = true;
                }
            }
            foreach (var list in layers)
            {
                var ordered = list.OrderBy(i => i.CrossCentre).ToList();
                for (var i = 1; i < ordered.Count; i++)
                {
                    var gap = ordered[i].Dummy || ordered[i - 1].Dummy ? DummyGap : NodeGap;
                    if (ordered[i].Group != ordered[i - 1].Group) gap += GroupSpacing;
                    var least = ordered[i - 1].CrossCentre + ordered[i - 1].Cross / 2 + gap + ordered[i].Cross / 2;
                    if (ordered[i].CrossCentre < least - 0.01) { ordered[i].CrossCentre = least; moved = true; }
                }
                list.Clear();
                list.AddRange(ordered);
            }
            if (!moved) break;
        }
    }

    private static double PortOffset(int index, int count, double halfCross)
    {
        if (count == 1) return 0;
        var span = Math.Min(halfCross * 1.3, PortSpacing * (count - 1));
        return -span / 2 + span * index / (count - 1);
    }

    /// <summary>
    /// Puts every dummy of an edge that skips layers on one cross position: the edge's own line when that is clear of the
    /// nodes in the layers it passes, else the nearest free line above or below them.
    /// </summary>
    private static void AssignChannels(Item[][] chains, List<Item> items, List<Item>[] layers)
    {
        var taken = new List<(double Cross, int From, int To)>();
        foreach (var chain in chains)
        {
            if (chain is null || chain.Length < 3) continue;
            var (a, b) = (chain[0], chain[^1]);
            int from = a.Layer + 1, to = b.Layer - 1;
            var blockers = Enumerable.Range(from, to - from + 1).SelectMany(l => layers[l]).Where(i => !i.Dummy).ToList();
            bool Free(double y) => blockers.All(i => Math.Abs(y - i.CrossCentre) > i.Cross / 2 + 10)
                && taken.All(t => t.To < from || t.From > to || Math.Abs(t.Cross - y) >= LaneSpacing);
            var candidates = new List<double>();
            foreach (var y in new[] { a.CrossCentre, b.CrossCentre }) if (Free(y)) candidates.Add(y);
            if (candidates.Count == 0)
            {
                var top = blockers.Count == 0 ? a.CrossCentre : blockers.Min(i => i.CrossCentre - i.Cross / 2);
                var bottom = blockers.Count == 0 ? a.CrossCentre : blockers.Max(i => i.CrossCentre + i.Cross / 2);
                var above = top - 18; while (!Free(above)) above -= LaneSpacing;
                var below = bottom + 18; while (!Free(below)) below += LaneSpacing;
                candidates.Add(above); candidates.Add(below);
            }
            var best = candidates.MinBy(y => Math.Abs(y - a.CrossCentre) + Math.Abs(y - b.CrossCentre));
            foreach (var dummy in chain.Skip(1).Take(chain.Length - 2)) dummy.CrossCentre = best;
            taken.Add((best, from, to));
        }
    }

    private static Box Inflate(Box b, double by) => new(b.X - by, b.Y - by, b.Width + 2 * by, b.Height + 2 * by);

    private static Box SegmentBox((double X, double Y) a, (double X, double Y) b, double by) =>
        new(Math.Min(a.X, b.X) - by, Math.Min(a.Y, b.Y) - by, Math.Abs(a.X - b.X) + 2 * by, Math.Abs(a.Y - b.Y) + 2 * by);

    private static (double X, double Y) Toward((double X, double Y) from, (double X, double Y) to, double distance)
    {
        var (dx, dy) = (to.X - from.X, to.Y - from.Y);
        var length = Math.Sqrt(dx * dx + dy * dy);
        return length < 0.001 ? from : (from.X + dx / length * Math.Min(distance, length), from.Y + dy / length * Math.Min(distance, length));
    }

    private static bool Hits(Box a, Box b) => a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    /// <summary>
    /// A label rectangle beside one of the edge's segments (nearest the middle of the edge first), padded away from the line, on the
    /// first side where it touches no node, no edge and no other label. A plate centred on the line is the fallback.
    /// </summary>
    private static Box PlaceLabel(List<(double X, double Y)> path, Box size, List<Box> obstacles)
    {
        var segments = Enumerable.Range(0, path.Count - 1).Select(i => (A: path[i], B: path[i + 1])).ToList();
        var lengths = segments.Select(s => Math.Abs(s.A.X - s.B.X) + Math.Abs(s.A.Y - s.B.Y)).ToList();
        var total = lengths.Sum();
        var before = 0.0;
        var ranked = new List<(int Index, double Distance)>();
        for (var i = 0; i < segments.Count; i++)
        {
            ranked.Add((i, Math.Abs(before + lengths[i] / 2 - total / 2)));
            before += lengths[i];
        }
        var order = ranked.Where(r => lengths[r.Index] >= 12).OrderBy(r => r.Distance).Select(r => r.Index).ToList();
        for (var pass = 0; pass < 2; pass++)
            foreach (var i in order)
            {
                var (a, b) = segments[i];
                var isHorizontal = Math.Abs(a.Y - b.Y) < 0.01;
                var fits = isHorizontal ? lengths[i] >= size.Width : lengths[i] >= size.Height;
                if (pass == 0 && !fits) continue;
                foreach (var along in new[] { 0.5, 0.25, 0.75 })
                    foreach (var side in new[] { -1, 1 })
                    {
                        var (cx, cy) = isHorizontal
                            ? (a.X + (b.X - a.X) * along, a.Y + side * (size.Height / 2 + 4))
                            : (a.X + side * (size.Width / 2 + 4), a.Y + (b.Y - a.Y) * along);
                        var candidate = new Box(cx - size.Width / 2, cy - size.Height / 2, size.Width, size.Height);
                        if (!obstacles.Any(o => Hits(o, candidate))) return candidate;
                    }
            }
        // Fallback: no clear side, so the label gets a plate on the line, at the middle of the longest segment.
        var longest = Enumerable.Range(0, segments.Count).MaxBy(i => lengths[i]);
        var (p, q) = segments.Count == 0 ? (path[0], path[0]) : segments[longest];
        return new Box((p.X + q.X) / 2 - size.Width / 2, (p.Y + q.Y) / 2 - size.Height / 2, size.Width, size.Height);
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
            layers[l] = Regroup([.. layers[l].OrderBy(Key)]);
            for (var i = 0; i < layers[l].Count; i++) position[layers[l][i]] = i;
        }
    }

    /// <summary>Gathers the members of each subgraph next to the first of them, keeping the order otherwise.</summary>
    private static List<Item> Regroup(List<Item> list)
    {
        var result = new List<Item>();
        var done = new HashSet<int>();
        foreach (var item in list)
        {
            if (item.Group < 0) result.Add(item);
            else if (done.Add(item.Group)) result.AddRange(list.Where(o => o.Group == item.Group));
        }
        return result;
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
