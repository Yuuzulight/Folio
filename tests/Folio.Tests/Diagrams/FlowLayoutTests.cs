using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Folio.Diagrams;

namespace Folio.Tests.Diagrams;

public sealed class FlowLayoutTests
{
    private sealed record Rect(double X, double Y, double W, double H)
    {
        public double Right => X + W;
        public double Bottom => Y + H;
    }

    private sealed record Parsed(double Width, double Height, double ViewWidth, double ViewHeight,
        Dictionary<string, Rect> Nodes, List<List<(double X, double Y)>> Edges, List<(Rect Box, string Text)> Labels, List<Rect> Groups, Dictionary<string, string> Shapes);

    private static readonly XNamespace Ns = "http://www.w3.org/2000/svg";

    private static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private static Parsed Parse(string source)
    {
        var svg = DiagramRenderer.Render(source);
        var root = XDocument.Parse(svg).Root!;
        var view = root.Attribute("viewBox")!.Value.Split(' ').Select(Num).ToArray();
        var nodes = new Dictionary<string, Rect>();
        var edges = new List<List<(double, double)>>();
        var boxes = new List<Rect>();
        var texts = new List<string>();
        var groups = new List<Rect>();
        var shapes = new Dictionary<string, string>();
        foreach (var e in root.Descendants())
        {
            var cls = (string?)e.Attribute("class") ?? "";
            if (cls.Contains("folio-diagram-node"))
            {
                var id = (string)e.Attribute("data-id")!;
                shapes[id] = cls.Contains("diamond") ? "diamond" : cls.Contains("circle") ? "circle" : cls.Contains("round") ? "round" : "rect";
                nodes[id] = e.Name.LocalName switch
                {
                    "rect" => new Rect(Num((string)e.Attribute("x")!), Num((string)e.Attribute("y")!), Num((string)e.Attribute("width")!), Num((string)e.Attribute("height")!)),
                    "circle" => new Rect(Num((string)e.Attribute("cx")!) - Num((string)e.Attribute("r")!), Num((string)e.Attribute("cy")!) - Num((string)e.Attribute("r")!), 2 * Num((string)e.Attribute("r")!), 2 * Num((string)e.Attribute("r")!)),
                    _ => Bounds(Numbers((string)e.Attribute("d")!)),
                };
            }
            else if (cls == "folio-diagram-subgraph")
                groups.Add(new Rect(Num((string)e.Attribute("x")!), Num((string)e.Attribute("y")!), Num((string)e.Attribute("width")!), Num((string)e.Attribute("height")!)));
            else if (cls == "folio-diagram-edge")
                edges.Add(Numbers((string)e.Attribute("d")!));
            else if (cls == "folio-diagram-edge-label-bg")
                boxes.Add(new Rect(Num((string)e.Attribute("x")!), Num((string)e.Attribute("y")!), Num((string)e.Attribute("width")!), Num((string)e.Attribute("height")!)));
            else if (cls.Contains("folio-diagram-edge-label"))
                texts.Add(e.Value);
        }
        return new Parsed(Num((string)root.Attribute("width")!), Num((string)root.Attribute("height")!), view[2], view[3],
            nodes, edges.Select(e => e.ToList()).ToList(), boxes.Zip(texts, (b, t) => (b, t)).ToList(), groups, shapes);
    }

    private static List<(double X, double Y)> Numbers(string d)
    {
        var values = Regex.Matches(d, @"-?\d+(\.\d+)?").Select(m => Num(m.Value)).ToArray();
        return Enumerable.Range(0, values.Length / 2).Select(i => (values[2 * i], values[2 * i + 1])).ToList();
    }

    private static Rect Bounds(List<(double X, double Y)> points) =>
        new(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X) - points.Min(p => p.X), points.Max(p => p.Y) - points.Min(p => p.Y));

    /// <summary>True when a point lies on the outline of a node of the given shape, whose bounding box is <paramref name="r"/>.</summary>
    private static bool OnOutline((double X, double Y) p, Rect r, string shape)
    {
        double cx = r.X + r.W / 2, cy = r.Y + r.H / 2, dx = Math.Abs(p.X - cx), dy = Math.Abs(p.Y - cy);
        switch (shape)
        {
            case "diamond": return Math.Abs(dx / (r.W / 2) + dy / (r.H / 2) - 1) < 0.03;
            case "circle": return Math.Abs(Math.Sqrt(dx * dx + dy * dy) - r.W / 2) < 0.6;
            case "round":
                var radius = r.H / 2; var inner = Math.Max(0, dx - (r.W / 2 - radius));
                return Math.Abs(Math.Sqrt(inner * inner + dy * dy) - radius) < 0.6 || OnBorder(p, r);
            default: return OnBorder(p, r);
        }
    }

    private static bool OnBorder((double X, double Y) p, Rect r, double tolerance = 0.02) =>
        p.X >= r.X - tolerance && p.X <= r.Right + tolerance && p.Y >= r.Y - tolerance && p.Y <= r.Bottom + tolerance &&
        (Math.Abs(p.X - r.X) <= tolerance || Math.Abs(p.X - r.Right) <= tolerance || Math.Abs(p.Y - r.Y) <= tolerance || Math.Abs(p.Y - r.Bottom) <= tolerance);

    /// <summary>True when an axis-aligned segment passes through the interior of a rectangle.</summary>
    private static bool Crosses((double X, double Y) a, (double X, double Y) b, Rect r)
    {
        var (x0, x1) = (Math.Min(a.X, b.X), Math.Max(a.X, b.X));
        var (y0, y1) = (Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y));
        const double e = 0.5;
        return x1 > r.X + e && x0 < r.Right - e && y1 > r.Y + e && y0 < r.Bottom - e;
    }

    private static bool Overlap(Rect a, Rect b) => a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

    private static string Chain(int count, string direction, string label = "") =>
        $"flowchart {direction}\n" + string.Join("\n", Enumerable.Range(0, count - 1).Select(i => $"N{i}[Step {i}] -->{label} N{i + 1}[Step {i + 1}]"));

    private const string Mixed = "flowchart LR\nA[Start] --> B[Validate]\nB --> C{Ok}\nC -->|yes| D[Store]\nC -->|no| E[Reject]\nD --> F[Index]\nE --> G[Notify]\nF --> H[Done]\nG --> H\nA --> H";

    [Theory]
    [InlineData("rect", 0, 70, 22, 70)]
    [InlineData("circle", 0, 30, 30, 30)]
    [InlineData("circle", 18, 30, 30, 24)]
    [InlineData("diamond", 0, 60, 32, 60)]
    [InlineData("diamond", 16, 60, 32, 30)]
    [InlineData("round", 0, 70, 22, 70)]
    [InlineData("round", 22, 70, 22, 48)]
    public void PortsLieOnTheNodeOutline(string shape, double offset, double halfMain, double halfCross, double expected)
        => Assert.Equal(expected, FlowLayout.Reach(shape, halfMain, halfCross, offset), 3);

    [Fact]
    public void EdgesLeaveAndEnterTheNodeBordersWithoutCrossingOtherNodes()
    {
        foreach (var source in new[] { Mixed, Mixed.Replace("LR", "TD"), "flowchart LR\nA --> B\nB --> C\nA --> C\nC --> D\nA --> D" })
        {
            var parsed = Parse(source);
            var pairs = Regex.Matches(source, @"(\w+)(?:\[[^\]]*\]|\{[^}]*\})?\s*-->(?:\|[^|]*\|)?\s*(\w+)").Select(m => (From: m.Groups[1].Value, To: m.Groups[2].Value)).ToList();
            Assert.Equal(pairs.Count, parsed.Edges.Count);
            for (var i = 0; i < pairs.Count; i++)
            {
                var points = parsed.Edges[i];
                var (from, to) = (parsed.Nodes[pairs[i].From], parsed.Nodes[pairs[i].To]);
                Assert.True(OnOutline(points[0], from, parsed.Shapes[pairs[i].From]), $"{pairs[i]} starts off the source outline");
                Assert.True(OnOutline(points[^1], to, parsed.Shapes[pairs[i].To]), $"{pairs[i]} ends off the target outline");
                for (var k = 0; k + 1 < points.Count; k++)
                {
                    Assert.True(Math.Abs(points[k].X - points[k + 1].X) < 0.01 || Math.Abs(points[k].Y - points[k + 1].Y) < 0.01, $"{pairs[i]} has a diagonal segment");
                    foreach (var (id, box) in parsed.Nodes.Where(n => n.Key != pairs[i].From && n.Key != pairs[i].To))
                        Assert.False(Crosses(points[k], points[k + 1], box), $"{pairs[i]} crosses {id}");
                }
            }
        }
    }

    [Fact]
    public void ClippingWorksForRoundDiamondAndCircleShapes()
    {
        var svg = DiagramRenderer.Render("flowchart LR\nA((Go)) --> B{Choice}\nB --> C(Round)\nB --> D[Box]");
        var root = XDocument.Parse(svg).Root!;
        var circle = root.Descendants(Ns + "circle").Single();
        var (cx, cy, r) = (Num((string)circle.Attribute("cx")!), Num((string)circle.Attribute("cy")!), Num((string)circle.Attribute("r")!));
        var first = Numbers((string)root.Descendants(Ns + "path").First(p => (string?)p.Attribute("class") == "folio-diagram-edge").Attribute("d")!)[0];
        Assert.Equal(r, Math.Sqrt((first.X - cx) * (first.X - cx) + (first.Y - cy) * (first.Y - cy)), 1);
        var diamond = Numbers((string)root.Descendants(Ns + "path").Single(p => ((string?)p.Attribute("class") ?? "").Contains("folio-diagram-diamond")).Attribute("d")!);
        var left = diamond.MinBy(p => p.X);
        var edgeEnd = Numbers((string)root.Descendants(Ns + "path").First(p => (string?)p.Attribute("class") == "folio-diagram-edge").Attribute("d")!)[^1];
        Assert.Equal(left.X, edgeEnd.X, 1);
        Assert.Equal(left.Y, edgeEnd.Y, 1);
    }

    private static bool Touches(Rect box, List<(double X, double Y)> path, double clearance)
    {
        for (var k = 0; k + 1 < path.Count; k++)
        {
            var (a, b) = (path[k], path[k + 1]);
            if (Math.Max(a.X, b.X) > box.X - clearance && Math.Min(a.X, b.X) < box.Right + clearance
                && Math.Max(a.Y, b.Y) > box.Y - clearance && Math.Min(a.Y, b.Y) < box.Bottom + clearance)
                return true;
        }
        return false;
    }

    private static double Distance(Rect box, List<(double X, double Y)> path) =>
        Enumerable.Range(0, path.Count - 1).Min(k =>
        {
            var (a, b) = (path[k], path[k + 1]);
            var dx = Math.Max(0, Math.Max(Math.Min(a.X, b.X) - box.Right, box.X - Math.Max(a.X, b.X)));
            var dy = Math.Max(0, Math.Max(Math.Min(a.Y, b.Y) - box.Bottom, box.Y - Math.Max(a.Y, b.Y)));
            return Math.Sqrt(dx * dx + dy * dy);
        });

    [Theory]
    [InlineData("LR")]
    [InlineData("TD")]
    public void EdgeLabelsSitBesideTheirEdgeClearOfEveryLineNodeAndLabel(string direction)
    {
        var parsed = Parse($"flowchart {direction}\nA[Start] -->|go| B[Middle]\nB -->|a longer label| C[End]\nA -->|skip| C\nC -->|retry| B\nA -->|accepted| D[Other]\nA -->|rejected| E[More]\nD -->|ok| C");
        Assert.Equal(parsed.Edges.Count, parsed.Labels.Count);
        for (var i = 0; i < parsed.Labels.Count; i++)
        {
            var box = parsed.Labels[i].Box;
            foreach (var (path, e) in parsed.Edges.Select((p, e) => (p, e)))
                Assert.False(Touches(box, path, 1), $"label {i} touches the line of edge {e}");
            Assert.True(Distance(box, parsed.Edges[i]) < 8, $"label {i} is not beside its own edge");
            foreach (var (id, node) in parsed.Nodes)
                Assert.False(Overlap(box, node), $"label {i} overlaps {id}");
            for (var j = 0; j < i; j++)
                Assert.False(Overlap(box, parsed.Labels[j].Box), $"labels {i} and {j} overlap");
            Assert.True(box.X >= 0 && box.Y >= 0 && box.Right <= parsed.ViewWidth && box.Bottom <= parsed.ViewHeight);
        }
        Assert.Contains("folio-diagram-edge-label-bg", DiagramRenderer.Render("flowchart LR\nA -->|x| B"));
        Assert.Contains("text-anchor=\"middle\"", DiagramRenderer.Render("flowchart LR\nA -->|x| B"));
    }

    [Theory]
    [InlineData(2, "LR")]
    [InlineData(5, "LR")]
    [InlineData(5, "TD")]
    [InlineData(12, "LR")]
    public void CanvasFollowsTheContent(int count, string direction)
    {
        var parsed = Parse(Chain(count, direction));
        var geometry = parsed.Nodes.Values.ToList();
        var points = parsed.Edges.SelectMany(e => e).ToList();
        var right = Math.Max(geometry.Max(r => r.Right), points.Max(p => p.X));
        var bottom = Math.Max(geometry.Max(r => r.Bottom), points.Max(p => p.Y));
        Assert.Equal(FlowLayout.Margin, Math.Min(geometry.Min(r => r.X), points.Min(p => p.X)), 1);
        Assert.Equal(FlowLayout.Margin, Math.Min(geometry.Min(r => r.Y), points.Min(p => p.Y)), 1);
        Assert.InRange(parsed.ViewWidth - right, FlowLayout.Margin - 0.01, FlowLayout.Margin + 1);
        Assert.InRange(parsed.ViewHeight - bottom, FlowLayout.Margin - 0.01, FlowLayout.Margin + 1);
        if (direction == "LR") Assert.True(parsed.ViewHeight < 100, "a single row must not keep empty space below");
        else Assert.True(parsed.ViewWidth < 200, "a single column must not keep empty space to the right");
    }

    [Fact]
    public void ContentBeyondTheMaximumIsScaledToFitAndNeverClipped()
    {
        var parsed = Parse(Chain(40, "LR"));
        Assert.True(parsed.ViewWidth > FlowLayout.MaxWidth, "the viewBox keeps the whole content");
        Assert.True(parsed.Width <= FlowLayout.MaxWidth && parsed.Height <= FlowLayout.MaxHeight);
        Assert.InRange((parsed.Width / parsed.Height) / (parsed.ViewWidth / parsed.ViewHeight), 0.95, 1.05);
        Assert.True(parsed.Nodes.Values.All(r => r.Right <= parsed.ViewWidth && r.Bottom <= parsed.ViewHeight));

        var tall = Parse(Chain(40, "TD"));
        Assert.True(tall.ViewHeight > FlowLayout.MaxHeight);
        Assert.True(tall.Height <= FlowLayout.MaxHeight);
        Assert.True(tall.Nodes.Values.All(r => r.Right <= tall.ViewWidth && r.Bottom <= tall.ViewHeight));
    }

    [Fact]
    public void SmallContentIsNotScaled()
    {
        var parsed = Parse(Chain(3, "LR"));
        Assert.Equal(parsed.ViewWidth, parsed.Width);
        Assert.Equal(parsed.ViewHeight, parsed.Height);
    }

    [Theory]
    [InlineData("LR")]
    [InlineData("TD")]
    [InlineData("RL")]
    [InlineData("BT")]
    public void EightNodeFlowchartsFitTheCanvasWithoutOverlaps(string direction)
    {
        var parsed = Parse(Mixed.Replace("LR", direction));
        Assert.Equal(8, parsed.Nodes.Count);
        foreach (var (id, box) in parsed.Nodes)
        {
            Assert.True(box.X >= 0 && box.Y >= 0 && box.Right <= parsed.ViewWidth && box.Bottom <= parsed.ViewHeight, $"{id} is clipped");
            foreach (var (other, otherBox) in parsed.Nodes.Where(p => string.CompareOrdinal(p.Key, id) > 0))
                Assert.False(Overlap(box, otherBox), $"{id} overlaps {other}");
        }
        foreach (var (box, _) in parsed.Labels)
            Assert.True(box.X >= 0 && box.Y >= 0 && box.Right <= parsed.ViewWidth && box.Bottom <= parsed.ViewHeight);
    }

    [Fact]
    public void FlowFollowsTheDirectionKeyword()
    {
        var lr = Parse("flowchart LR\nA --> B"); var td = Parse("flowchart TD\nA --> B"); var rl = Parse("flowchart RL\nA --> B"); var bt = Parse("flowchart BT\nA --> B");
        Assert.True(lr.Nodes["A"].X < lr.Nodes["B"].X && lr.Nodes["A"].Y == lr.Nodes["B"].Y);
        Assert.True(td.Nodes["A"].Y < td.Nodes["B"].Y && td.Nodes["A"].X == td.Nodes["B"].X);
        Assert.True(rl.Nodes["A"].X > rl.Nodes["B"].X);
        Assert.True(bt.Nodes["A"].Y > bt.Nodes["B"].Y);
        Assert.Equal(td.Nodes["A"].Y, Parse("graph\nA --> B").Nodes["A"].Y);
    }

    [Fact]
    public void CyclesAndSelfLoopsRenderWithoutOverlap()
    {
        var parsed = Parse("flowchart LR\nA --> B\nB --> A\nB --> C\nC --> C");
        Assert.Equal(4, parsed.Edges.Count);
        Assert.NotEqual(string.Join(",", parsed.Edges[0]), string.Join(",", parsed.Edges[1]));
        Assert.False(Overlap(parsed.Nodes["A"], parsed.Nodes["B"]));
        Assert.All(parsed.Edges, e => Assert.True(e.All(p => p.X >= 0 && p.Y >= 0 && p.X <= parsed.ViewWidth && p.Y <= parsed.ViewHeight)));
    }

    [Fact]
    public void SubgraphBoxesStayInsideTheCanvas()
    {
        var svg = DiagramRenderer.Render("flowchart LR\nsubgraph Input\nA[Start] --> B[Validate]\nend\nB --> C[Store]");
        var root = XDocument.Parse(svg).Root!;
        var box = root.Descendants(Ns + "rect").Single(r => ((string?)r.Attribute("class") ?? "").Contains("subgraph"));
        Assert.True(Num((string)box.Attribute("x")!) >= 0 && Num((string)box.Attribute("y")!) >= 0);
        Assert.True(Num((string)box.Attribute("x")!) + Num((string)box.Attribute("width")!) <= Num(root.Attribute("viewBox")!.Value.Split(' ')[2]));
    }

    [Fact]
    public void TheSameInputGivesByteIdenticalOutput()
    {
        Assert.Equal(DiagramRenderer.Render(Mixed), DiagramRenderer.Render(Mixed));
        Assert.Equal(DiagramRenderer.Render(Chain(9, "TD", "|x|")), DiagramRenderer.Render(Chain(9, "TD", "|x|")));
    }
    [Fact]
    public void EveryEdgeHasItsOwnPortOnTheNodeBorder()
    {
        var fanOut = Parse("flowchart LR\nA --> B\nA --> C\nA --> D");
        Assert.Equal(3, fanOut.Edges.Select(e => e[0]).Distinct().Count());
        var fanIn = Parse("flowchart TD\nA --> D\nB --> D\nC --> D");
        Assert.Equal(3, fanIn.Edges.Select(e => e[^1]).Distinct().Count());
        Assert.All(fanOut.Edges, e => Assert.True(OnBorder(e[0], fanOut.Nodes["A"])));
        Assert.All(fanIn.Edges, e => Assert.True(OnBorder(e[^1], fanIn.Nodes["D"])));
        var single = Parse("flowchart LR\nA --> B");
        Assert.Equal(single.Nodes["A"].Y + single.Nodes["A"].H / 2, single.Edges[0][0].Y, 1);
        // No two edges share a stretch of line: every segment of one edge is clear of every segment of the others.
        foreach (var parsed in new[] { fanOut, fanIn, Parse(Mixed) })
            for (var i = 0; i < parsed.Edges.Count; i++)
                for (var j = i + 1; j < parsed.Edges.Count; j++)
                    for (var k = 0; k + 1 < parsed.Edges[i].Count; k++)
                        for (var m = 0; m + 1 < parsed.Edges[j].Count; m++)
                            Assert.False(Collinear(parsed.Edges[i][k], parsed.Edges[i][k + 1], parsed.Edges[j][m], parsed.Edges[j][m + 1]), $"edges {i} and {j} share a line");
    }

    /// <summary>True when two axis-aligned segments lie on the same line and overlap by more than a point.</summary>
    private static bool Collinear((double X, double Y) a, (double X, double Y) b, (double X, double Y) c, (double X, double Y) d)
    {
        const double e = 0.5;
        if (Math.Abs(a.X - b.X) < 0.01 && Math.Abs(c.X - d.X) < 0.01 && Math.Abs(a.X - c.X) < 0.01)
            return Math.Min(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y)) - Math.Max(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y)) > e;
        if (Math.Abs(a.Y - b.Y) < 0.01 && Math.Abs(c.Y - d.Y) < 0.01 && Math.Abs(a.Y - c.Y) < 0.01)
            return Math.Min(Math.Max(a.X, b.X), Math.Max(c.X, d.X)) - Math.Max(Math.Min(a.X, b.X), Math.Min(c.X, d.X)) > e;
        return false;
    }

    [Fact]
    public void ArrowheadsAreProportionalToTheStrokeAndTheTipSitsOnTheBorder()
    {
        var svg = DiagramRenderer.Render("flowchart LR\nA --> B");
        var root = XDocument.Parse(svg).Root!;
        var marker = root.Descendants(Ns + "marker").Single();
        // markerUnits default to strokeWidth: 4 by 3 units at the stroke of 2 is 8 by 6 px.
        Assert.Equal("4", (string)marker.Attribute("markerWidth")!);
        Assert.Equal("3", (string)marker.Attribute("markerHeight")!);
        Assert.Equal("4", (string)marker.Attribute("refX")!);
        Assert.Equal("2", (string)root.Descendants(Ns + "g").First().Attribute("stroke-width")!);
        var parsed = Parse("flowchart LR\nA --> B");
        Assert.Equal(parsed.Nodes["B"].X, parsed.Edges[0][^1].X, 2);
    }

    [Fact]
    public void LongSkipEdgesTakeTheNearestFreeChannelWithFewBends()
    {
        var parsed = Parse("flowchart LR\nA --> B\nB --> C\nC --> D\nA --> D");
        var skip = parsed.Edges[3];
        Assert.True(skip.Count <= 6, "at most two jogs");
        // The channel runs outside B and C on the nearest side, not around the whole chart.
        var channelY = Enumerable.Range(0, skip.Count - 1).Where(k => Math.Abs(skip[k].Y - skip[k + 1].Y) < 0.01)
            .MaxBy(k => Math.Abs(skip[k].X - skip[k + 1].X)) is var longest ? skip[longest].Y : 0;
        var nodeTop = Math.Min(parsed.Nodes["B"].Y, parsed.Nodes["C"].Y); var nodeBottom = Math.Max(parsed.Nodes["B"].Bottom, parsed.Nodes["C"].Bottom);
        Assert.True(channelY < nodeTop || channelY > nodeBottom);
        Assert.True(Math.Min(Math.Abs(channelY - nodeTop), Math.Abs(channelY - nodeBottom)) <= 30);
        var mixed = Parse(Mixed);
        var startToDone = mixed.Edges[^1];
        Assert.True(startToDone.Count <= 6);
        Assert.True(startToDone.Max(p => p.Y) <= mixed.Nodes.Values.Max(r => r.Bottom) + 30);
    }

    [Fact]
    public void SubgraphBoxesDoNotCoverNodesOutsideTheGroup()
    {
        var source = "flowchart LR\nsubgraph Intake\nA[Receive] --> B[Check]\nend\nsubgraph Work\nC[Build] --> D[Test]\nend\nB --> C\nA --> E[Audit]\nE --> D\nD --> F[Ship]";
        var parsed = Parse(source);
        Assert.Equal(2, parsed.Groups.Count);
        var members = new[] { new[] { "A", "B" }, new[] { "C", "D" } };
        for (var g = 0; g < 2; g++)
        {
            foreach (var (id, box) in parsed.Nodes)
                Assert.Equal(members[g].Contains(id), Overlap(parsed.Groups[g], box));
            foreach (var id in members[g])
                Assert.True(parsed.Groups[g].X <= parsed.Nodes[id].X && parsed.Groups[g].Right >= parsed.Nodes[id].Right);
        }
        Assert.False(Overlap(parsed.Groups[0], parsed.Groups[1]));
    }

    [Fact]
    public void OneLineChainsParseLikeSeparateLines()
    {
        Assert.Equal(DiagramRenderer.Render("flowchart LR\nA --> B\nB --> C\nC --> D"), DiagramRenderer.Render("flowchart LR\nA --> B --> C --> D"));
        Assert.Equal(DiagramRenderer.Render("flowchart LR\nA -->|x| B\nB --> C"), DiagramRenderer.Render("flowchart LR\nA -->|x| B --> C"));
        Assert.Equal(DiagramRenderer.Render("flowchart LR\nA[Start] --> B{Ok}\nB -->|yes| C((End))"), DiagramRenderer.Render("flowchart LR\nA[Start] --> B{Ok} -->|yes| C((End))"));
        Assert.Equal(3, Parse("flowchart TD\nA --> B --> C --> D").Edges.Count);
        Assert.True(DiagramRenderer.TryRender("flowchart LR\nA --> B --> C", out _));
    }

    [Fact]
    public void NodesAreSizedFromTheBundledFontMetrics()
    {
        var measured = TextMeasure.Width("Transform data", 14, bold: true);
        Assert.True(measured > 0);
        Assert.NotEqual(TextMeasure.Estimate("Transform data", 14), measured);
        Assert.True(TextMeasure.Width("Transform data", 14, bold: true) > TextMeasure.Width("Transform data", 14, bold: false) * 0.95);
        var narrow = Parse("flowchart LR\nA[iiiiiiiiiiiiiiiiiiiiiiiiiiii] --> B[WWWWWWWWWWWWWWWWWWWWWWWW]");
        Assert.True(narrow.Nodes["A"].W < narrow.Nodes["B"].W, "narrow letters give a narrower node than wide ones of similar length");
    }

    [Fact]
    public void EveryDiagramKindSizesItsCanvasToTheContent()
    {
        foreach (var source in new[]
        {
            "sequenceDiagram\nAlice->>Bob: Hello",
            "classDiagram\nclass Base {\n+int id\n}\nBase <|-- Child",
            "stateDiagram-v2\n[*] --> Ready : boot",
            "erDiagram\nCUSTOMER ||--o{ ORDER : places",
            "timeline\ntitle History\nsection Releases\n2024 : Launch",
            "gantt\ndateFormat YYYY-MM-DD\nBuild :build, 2024-01-30, 3d",
            "pie title Sales\n\"North\" : 60\n\"South\" : 40",
        })
        {
            var root = XDocument.Parse(DiagramRenderer.Render(source)).Root!;
            var view = root.Attribute("viewBox")!.Value.Split(' ').Select(Num).ToArray();
            Assert.False(view[2] == 800 && view[3] == 400, $"{source.Split('\n')[0]} still has the fixed canvas");
            Assert.Equal(view[2], Num((string)root.Attribute("width")!));
            // Every shape lies inside the viewBox.
            foreach (var e in root.Descendants(Ns + "rect"))
            {
                Assert.True(Num((string)e.Attribute("x")!) >= view[0] && Num((string)e.Attribute("y")!) >= view[1], source);
                Assert.True(Num((string)e.Attribute("x")!) + Num((string)e.Attribute("width")!) <= view[0] + view[2], source);
                Assert.True(Num((string)e.Attribute("y")!) + Num((string)e.Attribute("height")!) <= view[1] + view[3], source);
            }
        }
        var pie = XDocument.Parse(DiagramRenderer.Render("pie\n\"A\" : 1\n\"B\" : 2")).Root!.Attribute("viewBox")!.Value.Split(' ').Select(Num).ToArray();
        Assert.True(pie[0] <= 230 - 130 && pie[1] <= 190 - 130 && pie[1] + pie[3] >= 190 + 130);
    }
}
