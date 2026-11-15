using System.Text;
using System.Xml;
using System.Globalization;

namespace Folio.Diagrams;

/// <summary>Small, dependency-free renderer for the diagram text used by scripted artifacts.</summary>
public static class DiagramRenderer
{
    /// <summary>Describes why a diagram could not be safely handled by the native renderer.</summary>
    public sealed record DiagramDiagnostic(string Message, int Position);

    /// <summary>Attempts to render a supported diagram description without silently accepting another language.</summary>
    public static bool TryRender(string source, out string svg)
        => TryRender(source, out svg, out _);

    /// <summary>Attempts rendering and returns a source position when known syntax is unsupported.</summary>
    public static bool TryRender(string source, out string svg, out DiagramDiagnostic? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(source);
        diagnostic = null;
        var first = source.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        if (!IsSupportedHeader(first))
        {
            svg = "";
            diagnostic = new DiagramDiagnostic("The diagram header is not supported.", 0);
            return false;
        }
        var lines = source.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!HasSupportedConstruct(first, lines.Skip(1)))
        {
            svg = "";
            var position = source.IndexOf('\n') is var newline && newline >= 0 ? newline + 1 : source.Length;
            diagnostic = new DiagramDiagnostic("The diagram contains no supported construct.", position);
            return false;
        }
        if (FirstUnsupportedLine(first, lines.Skip(1)) is { } unsupported)
        {
            svg = "";
            var position = source.IndexOf(unsupported, StringComparison.Ordinal);
            diagnostic = new DiagramDiagnostic($"Unsupported diagram syntax: {unsupported}", position < 0 ? 0 : position);
            return false;
        }
        svg = Render(source);
        return true;
    }

    private static string? FirstUnsupportedLine(string first, IEnumerable<string> body)
    {
        foreach (var line in body.Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith("%%", StringComparison.Ordinal)))
        {
            var supported = first.StartsWith("flowchart", StringComparison.OrdinalIgnoreCase) || first.StartsWith("graph", StringComparison.OrdinalIgnoreCase)
                ? line.Contains("--", StringComparison.Ordinal) || line.StartsWith("subgraph ", StringComparison.OrdinalIgnoreCase) || line is "end" || line.StartsWith("classDef ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("class ", StringComparison.OrdinalIgnoreCase)
                : first.StartsWith("sequenceDiagram", StringComparison.OrdinalIgnoreCase)
                    ? line.Contains("->", StringComparison.Ordinal) || StartsWithAny(line, StringComparison.OrdinalIgnoreCase, "participant", "actor", "activate", "deactivate", "note", "loop", "alt", "opt", "par", "critical", "break", "rect", "autonumber", "end", "else")
                    : first.StartsWith("stateDiagram", StringComparison.OrdinalIgnoreCase)
                        ? line.Contains("-->", StringComparison.Ordinal) || StartsWithAny(line, StringComparison.OrdinalIgnoreCase, "state", "note") || line == "}"
                        : first.StartsWith("classDiagram", StringComparison.OrdinalIgnoreCase)
                            ? line.Contains("<|", StringComparison.Ordinal) || line.Contains("..>", StringComparison.Ordinal) || line.Contains("-->", StringComparison.Ordinal) || StartsWithAny(line, StringComparison.OrdinalIgnoreCase, "class", "note") || line is "}" || line.Contains('{')
                            : first.StartsWith("erDiagram", StringComparison.OrdinalIgnoreCase)
                                ? line is "}" || line.Contains('{') || line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Length >= 3
                                : first.StartsWith("timeline", StringComparison.OrdinalIgnoreCase)
                                    ? StartsWithAny(line, StringComparison.OrdinalIgnoreCase, "title", "section") || line.Contains(':')
                                    : first.StartsWith("gantt", StringComparison.OrdinalIgnoreCase)
                                        ? StartsWithAny(line, StringComparison.OrdinalIgnoreCase, "dateFormat", "axisFormat", "section") || line.Split(',').Length >= 3
                                        : line.StartsWith("title ", StringComparison.OrdinalIgnoreCase) || ReadPieSlice(line) is not null;
            if (!supported) return line;
        }
        return null;
    }

    private static bool StartsWithAny(string value, StringComparison comparison, params string[] prefixes)
        => prefixes.Any(prefix => value.StartsWith(prefix, comparison));

    public static string Render(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var lines = source.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
            return Svg([], []);
        if (lines[0].StartsWith("pie", StringComparison.OrdinalIgnoreCase))
            return RenderPie(lines.Skip(1));
        if (lines[0].StartsWith("classDiagram", StringComparison.OrdinalIgnoreCase))
            return RenderClass(lines.Skip(1));
        if (lines[0].StartsWith("stateDiagram", StringComparison.OrdinalIgnoreCase))
            return RenderState(lines.Skip(1));
        if (lines[0].StartsWith("erDiagram", StringComparison.OrdinalIgnoreCase))
            return RenderEr(lines.Skip(1));
        if (lines[0].StartsWith("timeline", StringComparison.OrdinalIgnoreCase))
            return RenderTimeline(lines.Skip(1));
        if (lines[0].StartsWith("gantt", StringComparison.OrdinalIgnoreCase))
            return RenderGantt(lines.Skip(1));
        if (lines[0].StartsWith("sequenceDiagram", StringComparison.OrdinalIgnoreCase))
            return RenderSequence(lines.Skip(1));
        if (lines[0].StartsWith("flowchart", StringComparison.OrdinalIgnoreCase) || lines[0].StartsWith("graph", StringComparison.OrdinalIgnoreCase))
            return RenderFlow(lines[0], lines.Skip(1));
        return Svg([], []);
    }

    private static bool IsSupportedHeader(string first) =>
        first.StartsWith("flowchart", StringComparison.OrdinalIgnoreCase) ||
        first.StartsWith("graph", StringComparison.OrdinalIgnoreCase) ||
        first.StartsWith("sequenceDiagram", StringComparison.OrdinalIgnoreCase) ||
        first.StartsWith("classDiagram", StringComparison.OrdinalIgnoreCase) ||
        first.StartsWith("stateDiagram", StringComparison.OrdinalIgnoreCase) ||
        first.StartsWith("erDiagram", StringComparison.OrdinalIgnoreCase) ||
        first.StartsWith("timeline", StringComparison.OrdinalIgnoreCase) ||
        first.StartsWith("gantt", StringComparison.OrdinalIgnoreCase) ||
        first.StartsWith("pie", StringComparison.OrdinalIgnoreCase);

    private static bool HasSupportedConstruct(string first, IEnumerable<string> body)
    {
        var lines = body.Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith("%%", StringComparison.Ordinal)).ToArray();
        if (first.StartsWith("flowchart", StringComparison.OrdinalIgnoreCase) || first.StartsWith("graph", StringComparison.OrdinalIgnoreCase))
            return lines.Any(line => line.Contains("--", StringComparison.Ordinal));
        if (first.StartsWith("sequenceDiagram", StringComparison.OrdinalIgnoreCase))
            return lines.Any(line => line.Contains("->", StringComparison.Ordinal) ||
                line.StartsWith("participant ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("actor ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("note ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("loop ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("alt ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("opt ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("par ", StringComparison.OrdinalIgnoreCase));
        if (first.StartsWith("stateDiagram", StringComparison.OrdinalIgnoreCase))
            return lines.Any(line => line.Contains("-->", StringComparison.Ordinal));
        if (first.StartsWith("classDiagram", StringComparison.OrdinalIgnoreCase))
            return lines.Any(line => line.Contains("<|--", StringComparison.Ordinal) || line.Contains("..>", StringComparison.Ordinal) || line.Contains("class ", StringComparison.OrdinalIgnoreCase));
        if (first.StartsWith("erDiagram", StringComparison.OrdinalIgnoreCase))
            return lines.Any(line => line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Length >= 3);
        if (first.StartsWith("pie", StringComparison.OrdinalIgnoreCase))
            return lines.Any(line => ReadPieSlice(line) is not null);
        if (first.StartsWith("timeline", StringComparison.OrdinalIgnoreCase))
            return lines.Any(line => line.Contains(':'));
        if (first.StartsWith("gantt", StringComparison.OrdinalIgnoreCase))
            return lines.Any(line => line.Split(',', StringSplitOptions.TrimEntries).Length >= 3);
        return false;
    }

    private static (string Label, double Value)? ReadPieSlice(string line)
    {
        if (line.StartsWith("title ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("%%", StringComparison.Ordinal))
            return null;
        var separator = line.LastIndexOf(':');
        if (separator <= 0 || !double.TryParse(line.AsSpan(separator + 1), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value <= 0)
            return null;
        var label = line[..separator].Trim().Trim('"');
        return label.Length == 0 ? null : (label, value);
    }

    private static readonly string[] PieColours = ["#5B5BF0", "#B9B9FA", "#3A3AA8", "#8B5CF6", "#F43F8E", "#14B8A6", "#F59E0B"];

    private static string RenderPie(IEnumerable<string> lines)
    {
        var slices = new List<(string Label, double Value)>();
        foreach (var line in lines)
            if (ReadPieSlice(line) is { } slice)
                slices.Add(slice);
        if (slices.Count == 0) return Svg([], []);
        var largest = slices.Max(slice => slice.Value);
        var total = slices.Sum(slice => slice.Value / largest);
        const double cx = 230, cy = 190, radius = 130;
        var angle = -Math.PI / 2;
        var arcs = new List<string>();
        var legend = new List<string>();
        for (var i = 0; i < slices.Count; i++)
        {
            var slice = slices[i];
            var end = angle + (slice.Value / largest) / total * Math.PI * 2;
            var x1 = cx + radius * Math.Cos(angle); var y1 = cy + radius * Math.Sin(angle);
            var x2 = cx + radius * Math.Cos(end); var y2 = cy + radius * Math.Sin(end);
            var large = end - angle > Math.PI ? 1 : 0;
            var path = slices.Count == 1
                ? FormattableString.Invariant($"M {cx} {cy - radius} A {radius} {radius} 0 0 1 {cx} {cy + radius} A {radius} {radius} 0 0 1 {cx} {cy - radius} Z")
                : FormattableString.Invariant($"M {cx:0.##} {cy:0.##} L {x1:0.##} {y1:0.##} A {radius:0.##} {radius:0.##} 0 {large} 1 {x2:0.##} {y2:0.##} Z");
            var colour = PieColours[i % PieColours.Length];
            arcs.Add($"<path class=\"folio-diagram-slice\" data-label=\"{XmlEscape(slice.Label)}\" fill=\"{colour}\" stroke=\"white\" stroke-width=\"2\" d=\"{path}\"/>");
            legend.Add($"<rect class=\"folio-diagram-swatch\" x=\"450\" y=\"{58 + i * 28}\" width=\"14\" height=\"14\" rx=\"3\" fill=\"{colour}\" stroke=\"none\"/>");
            legend.Add($"<text class=\"folio-diagram-label folio-diagram-legend\" x=\"472\" y=\"{70 + i * 28}\">{XmlEscape(slice.Label)} ({slice.Value.ToString("0.##", CultureInfo.InvariantCulture)})</text>");
            angle = end;
        }
        return Svg(arcs.Concat(legend), [], null, new FlowLayout.Box(cx - radius, cy - radius, 2 * radius, 2 * radius));
    }

    private static string RenderFlow(string header, IEnumerable<string> lines)
    {
        var nodes = new List<FlowNode>();
        var edges = new List<(int From, int To, string Label, string Kind)>();
        var subgraphs = new List<(string Name, HashSet<string> Nodes)>();
        var classStyles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var nodeClasses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        (string Name, HashSet<string> Nodes)? currentSubgraph = null;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("subgraph ", StringComparison.OrdinalIgnoreCase))
            {
                currentSubgraph = (trimmed[9..].Trim(), new HashSet<string>(StringComparer.Ordinal));
                subgraphs.Add(currentSubgraph.Value);
                continue;
            }
            if (trimmed.Equals("end", StringComparison.OrdinalIgnoreCase)) { currentSubgraph = null; continue; }
            if (trimmed.StartsWith("classDef ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = trimmed[9..].Split(' ', 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && parts[0].Length > 0) classStyles[parts[0]] = parts[1];
                continue;
            }
            if (trimmed.StartsWith("class ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = trimmed[6..].Split(' ', 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 2) foreach (var id in parts[0].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) nodeClasses[id] = parts[1];
                continue;
            }
            if (!SplitFlowLine(line, out var texts, out var links)) continue;
            for (var k = 0; k < links.Count; k++)
            {
                var left = Node(texts[k]);
                var right = Node(texts[k + 1]);
                var from = Index(nodes, left);
                var to = Index(nodes, right);
                currentSubgraph?.Nodes.Add(left.Id);
                currentSubgraph?.Nodes.Add(right.Id);
                edges.Add((from, to, links[k].Label, links[k].Kind));
            }
        }
        var headerWords = header.Split([' ', '	', ';'], StringSplitOptions.RemoveEmptyEntries);
        var direction = headerWords.Length > 1 ? headerWords[1].ToUpperInvariant() : "TB";
        direction = direction switch { "LR" or "RL" or "BT" => direction, _ => "TB" };
        var groupIndexes = subgraphs.Select(group => nodes.Select((node, index) => (node, index)).Where(pair => group.Nodes.Contains(pair.node.Id)).Select(pair => pair.index).ToArray()).ToList();
        var layout = FlowLayout.Compute(
            nodes.Select(node => new FlowLayout.NodeSpec(node.Label, node.Shape)).ToList(),
            edges.Select(edge => new FlowLayout.EdgeSpec(edge.From, edge.To, edge.Label)).ToList(),
            groupIndexes, subgraphs.Select(group => group.Name).ToList(), direction);
        var content = new StringBuilder();
        for (var i = 0; i < subgraphs.Count; i++)
            if (layout.Groups[i] is { } g)
                content.Append($"<rect class=\"folio-diagram-subgraph\" data-name=\"{XmlEscape(subgraphs[i].Name)}\" fill=\"none\" x=\"{N(g.X)}\" y=\"{N(g.Y)}\" width=\"{N(g.Width)}\" height=\"{N(g.Height)}\" rx=\"10\"/><text class=\"folio-diagram-label folio-diagram-subgraph-label\" x=\"{N(g.X + 8)}\" y=\"{N(g.Y + 14)}\" fill=\"currentColor\" stroke=\"none\">{XmlEscape(subgraphs[i].Name)}</text>");
        for (var i = 0; i < nodes.Count; i++)
            content.Append(NodeSvg(nodes[i], layout.Nodes[i], nodeClasses.TryGetValue(nodes[i].Id, out var className) ? className : null, classStyles));
        for (var i = 0; i < edges.Count; i++)
        {
            var route = layout.Routes[i];
            var d = string.Join(' ', route.Points.Select((p, k) => $"{(k == 0 ? "M" : "L")} {N(p.X)} {N(p.Y)}"));
            var dash = edges[i].Kind == "-.->" ? " stroke-dasharray=\"4 3\"" : "";
            var thick = edges[i].Kind == "==>" ? " stroke-width=\"3\"" : "";
            content.Append($"<path class=\"folio-diagram-edge\" data-kind=\"{XmlEscape(edges[i].Kind)}\" fill=\"none\"{dash}{thick} d=\"{d}\" marker-end=\"url(#arrow)\"/>");
            if (route.Label is { } l)
                content.Append($"<rect class=\"folio-diagram-edge-label-bg\" x=\"{N(l.X)}\" y=\"{N(l.Y)}\" width=\"{N(l.Width)}\" height=\"{N(l.Height)}\" rx=\"4\" fill=\"white\" stroke=\"none\"/><text class=\"folio-diagram-label folio-diagram-edge-label\" x=\"{N(l.X + l.Width / 2)}\" y=\"{N(l.Y + l.Height / 2 + 4)}\" text-anchor=\"middle\" font-size=\"12\" fill=\"currentColor\" stroke=\"none\">{XmlEscape(edges[i].Label)}</text>");
        }
        return Svg([content.ToString()], [], ((int)layout.Width, (int)layout.Height));
    }

    private static string N(double value) => (Math.Round(value, 2) + 0.0).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// Splits a flowchart line into node texts and the arrows between them, so <c>A --&gt; B --&gt; C</c> and
    /// <c>A -->|x| B --> C</c> are chains. Arrows inside brackets or quotes are part of a label.
    /// </summary>
    private static bool SplitFlowLine(string line, out List<string> texts, out List<(string Kind, string Label)> links)
    {
        texts = []; links = [];
        var depth = 0; var quoted = false; var start = 0;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"') quoted = !quoted;
            if (quoted) continue;
            if (c is '[' or '(' or '{') depth++;
            else if (c is ']' or ')' or '}') depth = Math.Max(0, depth - 1);
            if (depth > 0) continue;
            var kind = new[] { "-.->", "==>", "-->", "---", "--" }.FirstOrDefault(candidate => string.CompareOrdinal(line, i, candidate, 0, candidate.Length) == 0);
            if (kind is null) continue;
            texts.Add(line[start..i].TrimEnd('<', '|', '.', '=', ' ').Trim());
            i += kind.Length;
            var label = "";
            var after = i;
            while (after < line.Length && line[after] == ' ') after++;
            if (after < line.Length && line[after] == '|' && line.IndexOf('|', after + 1) is var close and > 0)
            {
                label = line[(after + 1)..close].Trim();
                i = close + 1;
            }
            links.Add((kind, label));
            start = i;
            i--;
        }
        if (links.Count == 0) return false;
        texts.Add(line[start..].Trim());
        return true;
    }

    private static string NodeSvg(FlowNode node, FlowLayout.Box box, string? className, IReadOnlyDictionary<string, string> classStyles)
    {
        var classAttribute = className is null ? "" : $" data-class=\"{XmlEscape(className)}\"";
        var styleAttribute = className is not null && classStyles.TryGetValue(className, out var style) ? $" style=\"{XmlEscape(style)}\"" : "";
        double x = box.X, y = box.Y, w = box.Width, h = box.Height, cx = x + w / 2, cy = y + h / 2;
        var id = $" data-id=\"{XmlEscape(node.Id)}\"";
        var shape = node.Shape switch
        {
            "round" => $"<rect class=\"folio-diagram-node folio-diagram-round\"{id}{classAttribute}{styleAttribute} x=\"{N(x)}\" y=\"{N(y)}\" width=\"{N(w)}\" height=\"{N(h)}\" rx=\"{N(h / 2)}\"/>",
            "diamond" => $"<path class=\"folio-diagram-node folio-diagram-diamond\"{id}{classAttribute}{styleAttribute} d=\"M {N(cx)} {N(y)} L {N(x + w)} {N(cy)} L {N(cx)} {N(y + h)} L {N(x)} {N(cy)} Z\"/>",
            "circle" => $"<circle class=\"folio-diagram-node folio-diagram-circle\"{id}{classAttribute}{styleAttribute} cx=\"{N(cx)}\" cy=\"{N(cy)}\" r=\"{N(w / 2)}\"/>",
            _ => $"<rect class=\"folio-diagram-node folio-diagram-rect\"{id}{classAttribute}{styleAttribute} x=\"{N(x)}\" y=\"{N(y)}\" width=\"{N(w)}\" height=\"{N(h)}\" rx=\"8\"/>"
        };
        return $"{shape}<text class=\"folio-diagram-label\" x=\"{N(cx)}\" y=\"{N(cy + 5)}\" text-anchor=\"middle\" fill=\"currentColor\" stroke=\"none\">{XmlEscape(node.Label)}</text>";
    }

    private static string RenderSequence(IEnumerable<string> lines)
    {
        var participants = new List<string>();
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var messages = new List<(int From, int To, string Label, string Kind, bool Dashed)>();
        var notes = new List<(string Text, int Participant, bool Right)>();
        var blocks = new List<(string Kind, string Label, int Start, int End)>();
        var activations = new HashSet<int>();
        var openBlocks = new Stack<(string Kind, string Label, int Start)>();
        var autonumber = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Equals("autonumber", StringComparison.OrdinalIgnoreCase)) { autonumber = true; continue; }
            if (trimmed.StartsWith("participant ", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("actor ", StringComparison.OrdinalIgnoreCase))
            {
                var declaration = trimmed[(trimmed.IndexOf(' ') + 1)..].Trim();
                var asAt = declaration.IndexOf(" as ", StringComparison.OrdinalIgnoreCase);
                var key = asAt < 0 ? declaration : declaration[..asAt].Trim();
                var declaredLabel = asAt < 0 ? declaration : declaration[(asAt + 4)..].Trim();
                aliases[key] = declaredLabel; Index(participants, declaredLabel); continue;
            }
            if (trimmed.StartsWith("activate ", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("deactivate ", StringComparison.OrdinalIgnoreCase))
            {
                var key = trimmed[(trimmed.IndexOf(' ') + 1)..].Trim();
                var participantLabel = aliases.TryGetValue(key, out var alias) ? alias : key;
                var index = Index(participants, participantLabel);
                if (trimmed.StartsWith("activate ", StringComparison.OrdinalIgnoreCase)) activations.Add(index);
                continue;
            }
            foreach (var kind in new[] { "loop", "alt", "opt", "par" })
            {
                if (trimmed.StartsWith(kind + " ", StringComparison.OrdinalIgnoreCase))
                { openBlocks.Push((kind, trimmed[(kind.Length + 1)..].Trim(), messages.Count)); goto next; }
            }
            if (trimmed.Equals("end", StringComparison.OrdinalIgnoreCase) && openBlocks.Count > 0)
            { var block = openBlocks.Pop(); blocks.Add((block.Kind, block.Label, block.Start, messages.Count)); continue; }
            if (trimmed.StartsWith("else", StringComparison.OrdinalIgnoreCase) && openBlocks.Count > 0)
            { var block = openBlocks.Pop(); blocks.Add((block.Kind, block.Label, block.Start, messages.Count)); openBlocks.Push((block.Kind, trimmed.Length > 4 ? trimmed[4..].Trim() : "else", messages.Count)); continue; }
            if (trimmed.StartsWith("note ", StringComparison.OrdinalIgnoreCase))
            {
                var note = trimmed[5..].Trim(); var noteColon = note.IndexOf(':');
                if (noteColon >= 0) { var who = note[..noteColon].Trim(); var noteLabel = aliases.TryGetValue(who, out var alias) ? alias : who; notes.Add((note[(noteColon + 1)..].Trim(), Index(participants, noteLabel), note.Contains(" right of ", StringComparison.OrdinalIgnoreCase))); }
                continue;
            }
            var arrow = -1; var arrowKind = "";
            foreach (var candidate in new[] { "-->>", "->>", "--x", "-x", "-->" , "->" })
                if ((arrow = trimmed.IndexOf(candidate, StringComparison.Ordinal)) >= 0) { arrowKind = candidate; break; }
            if (arrow < 0) continue;
            var left = trimmed[..arrow].Trim();
            var rest = trimmed[(arrow + arrowKind.Length)..];
            var colon = rest.IndexOf(':');
            var right = (colon < 0 ? rest : rest[..colon]).Trim();
            var label = colon < 0 ? "" : rest[(colon + 1)..].Trim();
            left = aliases.TryGetValue(left, out var leftAlias) ? leftAlias : left;
            right = aliases.TryGetValue(right, out var rightAlias) ? rightAlias : right;
            var from = Index(participants, left); var to = Index(participants, right);
            if (autonumber) label = $"{messages.Count + 1}. {label}";
            messages.Add((from, to, label, arrowKind, arrowKind.StartsWith("--", StringComparison.Ordinal)));
            next: ;
        }
        while (openBlocks.Count > 0) { var block = openBlocks.Pop(); blocks.Add((block.Kind, block.Label, block.Start, messages.Count)); }
        double Lx(int i) => 80 + i * 180;
        var bottom = 60 + messages.Count * 48;
        var blockRight = Lx(Math.Max(1, participants.Count) - 1) + 76;
        var sb = new StringBuilder();
        foreach (var block in blocks)
            sb.Append($"<rect class=\"folio-diagram-block sequence-{XmlEscape(block.Kind)}\" x=\"12\" y=\"{36 + block.Start * 48}\" width=\"{N(blockRight - 12)}\" height=\"{Math.Max(28, (block.End - block.Start) * 48)}\" rx=\"6\"/><text class=\"folio-diagram-label sequence-block-label\" x=\"20\" y=\"{50 + block.Start * 48}\">{XmlEscape(block.Kind)} {XmlEscape(block.Label)}</text>");
        for (var i = 0; i < participants.Count; i++)
        {
            var width = Math.Clamp(TextMeasure.Width(participants[i], 14, bold: true) + 28, 90, 164);
            sb.Append($"<path class=\"folio-diagram-lifeline\" d=\"M {N(Lx(i))} 40 L {N(Lx(i))} {bottom}\"/>");
            if (activations.Contains(i)) sb.Append($"<rect class=\"folio-diagram-activation\" x=\"{N(Lx(i) - 6)}\" y=\"40\" width=\"12\" height=\"{messages.Count * 48}\"/>");
            sb.Append($"<rect class=\"folio-diagram-node sequence-participant\" x=\"{N(Lx(i) - width / 2)}\" y=\"8\" width=\"{N(width)}\" height=\"32\" rx=\"6\"/><text class=\"folio-diagram-participant\" data-role=\"participant\" x=\"{N(Lx(i))}\" y=\"29\" text-anchor=\"middle\">{XmlEscape(participants[i])}</text>");
        }
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            double y = 60 + i * 48, x1 = Lx(message.From), x2 = Lx(message.To);
            var dash = message.Dashed ? " stroke-dasharray=\"4 3\"" : "";
            var self = message.From == message.To;
            var d = self ? $"M {N(x1)} {N(y)} L {N(x1 + 36)} {N(y)} L {N(x1 + 36)} {N(y + 16)} L {N(x1)} {N(y + 16)}" : $"M {N(x1)} {N(y)} L {N(x2)} {N(y)}";
            sb.Append($"<path class=\"folio-diagram-edge sequence-message\" data-kind=\"{XmlEscape(message.Kind)}\"{dash} d=\"{d}\" marker-end=\"url(#arrow)\"/>");
            sb.Append(self
                ? $"<text class=\"folio-diagram-label sequence-message-label\" x=\"{N(x1 + 44)}\" y=\"{N(y + 12)}\">{XmlEscape(message.Label)}</text>"
                : $"<text class=\"folio-diagram-label sequence-message-label\" x=\"{N((x1 + x2) / 2)}\" y=\"{N(y - 8)}\" text-anchor=\"middle\">{XmlEscape(message.Label)}</text>");
        }
        foreach (var note in notes)
        {
            var x = Lx(note.Participant) + (note.Right ? 14 : -154);
            sb.Append($"<rect class=\"folio-diagram-note\" x=\"{N(x)}\" y=\"{bottom + 6}\" width=\"140\" height=\"26\" rx=\"4\"/><text class=\"folio-diagram-label\" x=\"{N(x + 8)}\" y=\"{bottom + 24}\">{XmlEscape(note.Text)}</text>");
        }
        return Svg([sb.ToString()], []);
    }

    private static string RenderClass(IEnumerable<string> lines)
    {
        var names = new List<string>();
        var edges = new List<(string From, string To, string Kind, string Label)>();
        var members = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string? current = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("%%", StringComparison.Ordinal)) continue;
            var relation = line.IndexOf("<|--", StringComparison.Ordinal) >= 0 ? "<|--" : line.IndexOf("..>", StringComparison.Ordinal) >= 0 ? "..>" : "-->";
            var at = line.IndexOf(relation, StringComparison.Ordinal);
            if (at >= 0)
            {
                var from = line[..at].Trim(); var to = line[(at + relation.Length)..].Split(':', 2)[0].Trim();
                if (from.Length > 0 && to.Length > 0) { names.Add(from); names.Add(to); edges.Add((from, to, relation, line.Contains(':') ? line[(line.IndexOf(':') + 1)..].Trim() : "")); }
                continue;
            }
            if (line.StartsWith("class ", StringComparison.OrdinalIgnoreCase))
            {
                current = line[6..].Split([' ', '{'], 2)[0].Trim();
                if (current.Length > 0) { names.Add(current); members.TryAdd(current, []); }
                continue;
            }
            if (line.Contains('{'))
            {
                current = line[..line.IndexOf('{')].Trim();
                if (current.Length > 0) { names.Add(current); members.TryAdd(current, []); }
                continue;
            }
            if (line == "}") { current = null; continue; }
            if (!string.IsNullOrEmpty(current) && !line.StartsWith("note ", StringComparison.OrdinalIgnoreCase)
                && members.TryGetValue(current, out var classMembers))
                classMembers.Add(line.Trim(';'));
        }
        names = names.Distinct(StringComparer.Ordinal).ToList();
        var rowsOf = names.Select(name => members.TryGetValue(name, out var values) ? values : []).ToList();
        var widths = names.Select((name, i) => rowsOf[i].Select(row => TextMeasure.Width(row, 14, false) + 24).Append(TextMeasure.Width(name, 14, true) + 28).Append(150).Max()).ToArray();
        var xs = BoxColumns(names.Count, widths, edges.Select(e => (names.IndexOf(e.From), names.IndexOf(e.To), e.Label)));
        var sb = new StringBuilder();
        for (var i = 0; i < names.Count; i++)
        {
            var rows = rowsOf[i];
            var height = rows.Count == 0 ? 32 : 40 + rows.Count * 18;
            sb.Append($"<rect class=\"folio-diagram-node class-node\" x=\"{N(xs[i])}\" y=\"40\" width=\"{N(widths[i])}\" height=\"{height}\" rx=\"6\"/>");
            sb.Append($"<text class=\"folio-diagram-label class-label\" x=\"{N(xs[i] + widths[i] / 2)}\" y=\"61\" text-anchor=\"middle\">{XmlEscape(names[i])}</text>");
            if (rows.Count > 0) sb.Append($"<path class=\"class-divider\" d=\"M {N(xs[i])} 72 L {N(xs[i] + widths[i])} 72\"/>");
            for (var row = 0; row < rows.Count; row++)
                sb.Append($"<text class=\"folio-diagram-label class-member\" x=\"{N(xs[i] + 10)}\" y=\"{90 + row * 18}\">{XmlEscape(rows[row])}</text>");
        }
        foreach (var e in edges)
        {
            var a = names.IndexOf(e.From); var b = names.IndexOf(e.To);
            if (a == b) continue;
            var dash = e.Kind == "..>" ? " stroke-dasharray=\"4 3\"" : "";
            sb.Append(BoxEdge(a, b, xs, widths, "class-edge", $" data-kind=\"{XmlEscape(e.Kind)}\" data-cardinality=\"{XmlEscape(e.Label)}\"{dash}", e.Label, "class-edge-label"));
        }
        return Svg([sb.ToString()], []);
    }

    private static string RenderState(IEnumerable<string> lines)
    {
        var states = new List<string>(); var kinds = new Dictionary<string, string>(StringComparer.Ordinal); var edges = new List<(string From, string To, string Label)>(); var notes = new List<(string State, string Text)>();
        string? composite = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("state ", StringComparison.OrdinalIgnoreCase) && !line.EndsWith('{'))
            {
                var declaration = line[6..].Trim();
                var marker = declaration.IndexOf("<<", StringComparison.Ordinal);
                if (marker >= 0 && declaration.EndsWith(">>", StringComparison.Ordinal))
                {
                    var name = declaration[..marker].Trim();
                    var kind = declaration[(marker + 2)..^2].Trim().ToLowerInvariant();
                    if (name.Length != 0 && kind is "choice" or "fork" or "join") { states.Add(name); kinds[name] = kind; continue; }
                }
            }
            if (line.StartsWith("state ", StringComparison.OrdinalIgnoreCase) && line.EndsWith('{')) { composite = line[6..^1].Trim(); states.Add(composite); continue; }
            if (line == "}") { composite = null; continue; }
            if (line.StartsWith("note ", StringComparison.OrdinalIgnoreCase)) { var colon = line.IndexOf(':'); if (colon > 5) notes.Add((line[5..colon].Trim(), line[(colon + 1)..].Trim())); continue; }
            var at = line.IndexOf("-->", StringComparison.Ordinal);
            if (at < 0) continue;
            var from = line[..at].Trim(); var remainder = line[(at + 3)..];
            var fields = remainder.Split(':', 2);
            var to = fields[0].Trim(); var label = fields.Length == 2 ? fields[1].Trim() : "";
            states.Add(from); states.Add(to); edges.Add((from, to, label));
        }
        states = states.Distinct(StringComparer.Ordinal).ToList();
        var boxes = states.Select((name, i) =>
        {
            var y = 30 + i * 64;
            var label = XmlEscape(name == "[*]" ? "start" : name);
            if (!kinds.TryGetValue(name, out var kind))
                return $"<rect class=\"folio-diagram-node state-node\" x=\"30\" y=\"{y}\" width=\"180\" height=\"40\" rx=\"20\"/><text class=\"folio-diagram-label\" x=\"120\" y=\"{y + 25}\" text-anchor=\"middle\">{label}</text>";
            if (kind == "choice")
                return $"<polygon class=\"folio-diagram-node state-choice\" data-kind=\"choice\" points=\"120,{y} 140,{y + 20} 120,{y + 40} 100,{y + 20}\"/><text class=\"folio-diagram-label\" x=\"150\" y=\"{y + 25}\">{label}</text>";
            return $"<rect class=\"folio-diagram-node state-{kind}\" data-kind=\"{kind}\" x=\"70\" y=\"{y + 16}\" width=\"100\" height=\"8\"/><text class=\"folio-diagram-label\" x=\"180\" y=\"{y + 25}\">{label}</text>";
        });
        double RightOf(string name) => kinds.TryGetValue(name, out var k) ? (k == "choice" ? 140 : 170) : 210;
        var sideTotal = edges.Count(e => { int a = states.IndexOf(e.From), b = states.IndexOf(e.To); return a != b && b != a + 1; });
        var side = 0;
        var paths = new List<string>();
        foreach (var e in edges)
        {
            int a = states.IndexOf(e.From), b = states.IndexOf(e.To);
            if (a == b) continue;
            string d; double lx, ly;
            if (b == a + 1)
            {
                d = $"M 120 {70 + a * 64} L 120 {30 + b * 64}";
                lx = 128; ly = (70 + a * 64 + 30 + b * 64) / 2.0 + 4;
            }
            else
            {
                // Other transitions leave by the right side and run down a channel of their own, so they never cross a state.
                double xc = 232 + 12 * side++, ya = 30 + a * 64 + 14, yb = 30 + b * 64 + 26;
                d = $"M {N(RightOf(e.From))} {N(ya)} L {N(xc)} {N(ya)} L {N(xc)} {N(yb)} L {N(RightOf(e.To))} {N(yb)}";
                lx = 232 + 12 * sideTotal; ly = (ya + yb) / 2 + 4;
            }
            paths.Add($"<path class=\"state-edge\" d=\"{d}\" marker-end=\"url(#arrow)\"/>{(e.Label.Length == 0 ? "" : $"<text class=\"folio-diagram-label state-transition-label\" x=\"{N(lx)}\" y=\"{N(ly)}\">{XmlEscape(e.Label)}</text>")}");
        }
        var noteSvg = notes.Select(note => $"<text class=\"folio-diagram-note\" data-state=\"{XmlEscape(note.State)}\" x=\"{232 + 12 * sideTotal + 20}\" y=\"{55 + Math.Max(0, states.IndexOf(note.State)) * 64}\">{XmlEscape(note.Text)}</text>");
        return Svg(boxes, paths.Concat(noteSvg));
    }

    private static string RenderEr(IEnumerable<string> lines)
    {
        var entities = new List<string>(); var edges = new List<(string From, string To, string Label)>(); var attributes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string? current = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim(); if (line.Length == 0 || line.StartsWith("%%", StringComparison.Ordinal)) continue;
            if (line.EndsWith('{')) { current = line[..^1].Trim(); entities.Add(current); attributes.TryAdd(current, []); continue; }
            if (line == "}") { current = null; continue; }
            var tokens = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (current is not null && tokens.Length >= 2) { attributes[current].Add(string.Join(' ', tokens)); continue; }
            if (tokens.Length >= 3 && (tokens[1].Contains('-') || tokens[1].Contains('|')))
            { entities.Add(tokens[0]); entities.Add(tokens[2]); edges.Add((tokens[0], tokens[2], tokens.Length > 4 ? string.Join(' ', tokens[4..]) : tokens[1])); }
            else if (tokens.Length >= 1 && tokens[0].Length > 0 && !tokens[0].Equals("title", StringComparison.OrdinalIgnoreCase)) entities.Add(tokens[0]);
        }
        entities = entities.Distinct(StringComparer.Ordinal).ToList();
        var rowsOf = entities.Select(name => attributes.TryGetValue(name, out var values) ? values : []).ToList();
        var widths = entities.Select((name, i) => rowsOf[i].Select(row => TextMeasure.Width(row, 14, false) + 24).Append(TextMeasure.Width(name, 14, true) + 28).Append(150).Max()).ToArray();
        var xs = BoxColumns(entities.Count, widths, edges.Select(e => (entities.IndexOf(e.From), entities.IndexOf(e.To), e.Label)));
        var sb = new StringBuilder();
        for (var i = 0; i < entities.Count; i++)
        {
            var rows = rowsOf[i];
            sb.Append($"<rect class=\"folio-diagram-node er-entity\" x=\"{N(xs[i])}\" y=\"40\" width=\"{N(widths[i])}\" height=\"{(rows.Count == 0 ? 32 : 40 + rows.Count * 18)}\" rx=\"6\"/>");
            sb.Append($"<text class=\"folio-diagram-label er-name\" x=\"{N(xs[i] + widths[i] / 2)}\" y=\"61\" text-anchor=\"middle\">{XmlEscape(entities[i])}</text>");
            if (rows.Count > 0) sb.Append($"<path class=\"class-divider\" d=\"M {N(xs[i])} 72 L {N(xs[i] + widths[i])} 72\"/>");
            for (var n = 0; n < rows.Count; n++)
                sb.Append($"<text class=\"folio-diagram-label er-attribute\" x=\"{N(xs[i] + 10)}\" y=\"{90 + n * 18}\">{XmlEscape(rows[n])}</text>");
        }
        foreach (var e in edges)
        {
            int a = entities.IndexOf(e.From), b = entities.IndexOf(e.To);
            if (a != b) sb.Append(BoxEdge(a, b, xs, widths, "er-edge", $" data-cardinality=\"{XmlEscape(e.Label)}\"", e.Label, "er-edge-label"));
        }
        return Svg([sb.ToString()], []);
    }

    private static string RenderTimeline(IEnumerable<string> lines)
    {
        var rows = new List<(string? Section, string Date, string Event)>();
        string? section = null;
        foreach (var line in lines.Select(line => line.Trim()))
        {
            if (line.StartsWith("section ", StringComparison.OrdinalIgnoreCase)) { section = line[8..].Trim(); continue; }
            var colon = line.IndexOf(':');
            if (colon > 0) rows.Add((section, line[..colon].Trim(), line[(colon + 1)..].Trim()));
        }
        var body = rows.Select((item, i) =>
        {
            var y = 48 + i * 56;
            var sectionSvg = item.Section is null || (i > 0 && rows[i - 1].Section == item.Section) ? "" : $"<text class=\"folio-diagram-section\" x=\"20\" y=\"{y - 20}\">{XmlEscape(item.Section)}</text>";
            return $"{sectionSvg}<circle class=\"folio-diagram-node timeline-event\" cx=\"50\" cy=\"{y}\" r=\"6\"/><text class=\"folio-diagram-label\" x=\"70\" y=\"{y + 5}\"><tspan class=\"timeline-date\">{XmlEscape(item.Date)}</tspan> {XmlEscape(item.Event)}</text>";
        });
        return Svg(body, []);
    }

    private static string RenderGantt(IEnumerable<string> lines)
    {
        var tasks = new List<(string Name, string Id, DateTime Start, int Days, string Dependency, string? Section)>();
        var dateFormat = "yyyy-MM-dd";
        string? section = null;
        foreach (var line in lines.Select(x => x.Trim()))
        {
            if (line.StartsWith("dateFormat ", StringComparison.OrdinalIgnoreCase)) { dateFormat = ToDateFormat(line[11..].Trim()); continue; }
            if (line.StartsWith("axisFormat ", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("section ", StringComparison.OrdinalIgnoreCase)) { section = line[8..].Trim(); continue; }
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 3) continue;
            var descriptor = parts[0].Split(':', 2, StringSplitOptions.TrimEntries);
            var name = descriptor[0].Trim();
            var id = descriptor.Length == 2 ? descriptor[1].Trim() : name;
            var dependency = "";
            DateTime start;
            if (parts.Length >= 3 && parts[1].StartsWith("after ", StringComparison.OrdinalIgnoreCase))
            {
                dependency = parts[1][6..].Trim();
                var previous = tasks.LastOrDefault(task => string.Equals(task.Id, dependency, StringComparison.OrdinalIgnoreCase));
                start = previous == default ? DateTime.MinValue : previous.Start.AddDays(previous.Days);
            }
            else if (!TryParseGanttDate(parts[^2], dateFormat, out start)) continue;
            var duration = parts[^1].EndsWith('d') && int.TryParse(parts[^1][..^1], out var d) ? d : 1;
            tasks.Add((name, id, start, Math.Max(1, duration), dependency, section));
        }
        var origin = tasks.Count == 0 ? DateTime.Today : tasks.Min(t => t.Start);
        var bars = tasks.Select((task, i) => { var x = 180 + (task.Start - origin).Days * 18; var sectionSvg = task.Section is null ? "" : $"<text class=\"folio-diagram-section gantt-section\" x=\"10\" y=\"{25 + i * 36}\">{XmlEscape(task.Section)}</text>"; return $"{sectionSvg}<text class=\"folio-diagram-label\" x=\"10\" y=\"{45 + i * 36}\">{XmlEscape(task.Name)}</text><rect class=\"folio-diagram-node gantt-task\" data-id=\"{XmlEscape(task.Id)}\" data-section=\"{XmlEscape(task.Section ?? "")}\" data-start=\"{task.Start:yyyy-MM-dd}\" data-days=\"{task.Days}\" data-after=\"{XmlEscape(task.Dependency)}\" x=\"{x}\" y=\"{28 + i * 36}\" width=\"{Math.Max(12, task.Days * 18)}\" height=\"22\"/>"; });
        return Svg(bars, []);
    }

    private static string ToDateFormat(string format) => format
        .Replace("YYYY", "yyyy", StringComparison.OrdinalIgnoreCase)
        .Replace("DD", "dd", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseGanttDate(string value, string format, out DateTime date)
    {
        try
        {
            return DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
                || DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }
        catch (FormatException)
        {
            date = default;
            return false;
        }
    }

    /// <summary>Left edges of boxes laid out in one row, with room between neighbours for the labels of the edges joining them.</summary>
    private static double[] BoxColumns(int count, double[] widths, IEnumerable<(int From, int To, string Label)> edges)
    {
        var xs = new double[count];
        var links = edges.ToList();
        var x = 30.0;
        for (var i = 0; i < count; i++)
        {
            xs[i] = x;
            var label = links.Where(l => Math.Min(l.From, l.To) == i && Math.Abs(l.From - l.To) == 1).Select(l => TextMeasure.Width(l.Label, 12, bold: false)).DefaultIfEmpty(0).Max();
            x += widths[i] + Math.Max(40, label + 24);
        }
        return xs;
    }

    /// <summary>
    /// An edge between two boxes of a row: straight between the facing sides of neighbours, otherwise over the top of the boxes
    /// in between, with its label centred above the line.
    /// </summary>
    private static string BoxEdge(int a, int b, double[] xs, double[] widths, string pathClass, string attributes, string label, string labelClass)
    {
        string d; double lx, ly;
        if (Math.Abs(a - b) == 1)
        {
            var forward = b > a;
            double x1 = forward ? xs[a] + widths[a] : xs[a], x2 = forward ? xs[b] : xs[b] + widths[b];
            d = $"M {N(x1)} 56 L {N(x2)} 56";
            lx = (x1 + x2) / 2; ly = 48;
        }
        else
        {
            double top = 40 - 14 * Math.Abs(a - b), xa = xs[a] + widths[a] / 2, xb = xs[b] + widths[b] / 2;
            d = $"M {N(xa)} 40 L {N(xa)} {N(top)} L {N(xb)} {N(top)} L {N(xb)} 40";
            lx = (xa + xb) / 2; ly = top - 6;
        }
        return $"<path class=\"{pathClass}\"{attributes} d=\"{d}\" marker-end=\"url(#arrow)\"/>"
            + (label.Length == 0 ? "" : $"<text class=\"folio-diagram-label {labelClass}\" x=\"{N(lx)}\" y=\"{N(ly)}\" text-anchor=\"middle\" font-size=\"12\">{XmlEscape(label)}</text>");
    }

    /// <summary>Default look for the diagram kinds without a layout of their own styling: presentation attributes, so any page CSS wins.</summary>
    private static readonly (string Tag, string Class, string Attributes)[] Defaults =
    [
        ("rect", "gantt-task", "rx=\"4\" fill=\"#5B5BF0\" stroke=\"none\""),
        ("rect", "state-fork", "fill=\"#5B5BF0\" stroke=\"none\""),
        ("rect", "state-join", "fill=\"#5B5BF0\" stroke=\"none\""),
        ("rect", "folio-diagram-node", "fill=\"#ECECFF\" stroke=\"#5B5BF0\""),
        ("polygon", "folio-diagram-node", "fill=\"#ECECFF\" stroke=\"#5B5BF0\""),
        ("circle", "timeline-event", "fill=\"#5B5BF0\" stroke=\"none\""),
        ("circle", "folio-diagram-node", "fill=\"#ECECFF\" stroke=\"#5B5BF0\""),
        ("rect", "folio-diagram-activation", "fill=\"#ECECFF\" stroke=\"#5B5BF0\""),
        ("rect", "folio-diagram-block", "fill=\"none\" stroke=\"#9A9AD8\" stroke-width=\"1\" stroke-dasharray=\"4 3\""),
        ("rect", "folio-diagram-note", "fill=\"#FEF3C7\" stroke=\"#F59E0B\" stroke-width=\"1\""),
        ("path", "folio-diagram-lifeline", "fill=\"none\" stroke=\"#B9B9FA\" stroke-width=\"1.5\" stroke-dasharray=\"5 4\""),
        ("path", "class-divider", "fill=\"none\" stroke=\"#B9B9FA\" stroke-width=\"1\""),
        ("path", "folio-diagram-edge", "fill=\"none\" stroke=\"#5B5BF0\""),
        ("path", "class-edge", "fill=\"none\" stroke=\"#5B5BF0\""),
        ("path", "state-edge", "fill=\"none\" stroke=\"#5B5BF0\""),
        ("path", "er-edge", "fill=\"none\" stroke=\"#5B5BF0\""),
        ("text", "folio-diagram-participant", "font-weight=\"600\""),
        ("text", "class-label", "font-weight=\"600\""),
        ("text", "er-name", "font-weight=\"600\""),
        ("text", "folio-diagram-section", "font-weight=\"600\""),
        ("text", "sequence-block-label", "font-weight=\"600\""),
        ("text", "folio-diagram-note", "font-style=\"italic\""),
        ("text", "", "fill=\"#1E1B3A\" stroke=\"none\""),
    ];

    private static string ApplyDefaults(string content) =>
        System.Text.RegularExpressions.Regex.Replace(content, @"<(rect|path|circle|polygon|text)\b([^>]*?)(/?)>", match =>
        {
            var tag = match.Groups[1].Value;
            var existing = match.Groups[2].Value;
            var classes = System.Text.RegularExpressions.Regex.Match(existing, "class=\"([^\"]*)\"").Groups[1].Value.Split(' ');
            var have = System.Text.RegularExpressions.Regex.Matches(existing, @"\s([\w-]+)=").Select(m => m.Groups[1].Value).ToHashSet();
            var added = new StringBuilder();
            foreach (var (ruleTag, ruleClass, attributes) in Defaults)
            {
                if (ruleTag != tag || (ruleClass.Length > 0 && !classes.Contains(ruleClass))) continue;
                foreach (System.Text.RegularExpressions.Match attribute in System.Text.RegularExpressions.Regex.Matches(attributes, "([\\w-]+)=\"([^\"]*)\""))
                    if (have.Add(attribute.Groups[1].Value)) added.Append(' ').Append(attribute.Value);
            }
            return added.Length == 0 ? match.Value : $"<{tag}{existing}{added}{match.Groups[3].Value}>";
        });

    /// <summary>
    /// Wraps content in the diagram SVG, sized to the content: the viewBox is the content's extent plus a margin, so nothing is
    /// clipped and no empty canvas is left, and the displayed width and height are scaled down to fit
    /// <see cref="FlowLayout.MaxWidth"/> by <see cref="FlowLayout.MaxHeight"/> when the content is larger.
    /// Flowcharts pass the exact size their layout computed; every other kind has its extent measured from its elements.
    /// </summary>
    private static string Svg(IEnumerable<string> body, IEnumerable<string> overlays, (int Width, int Height)? size = null, params FlowLayout.Box[] extra)
    {
        var content = string.Join("", body.Concat(overlays));
        if (size is null) content = ApplyDefaults(content);
        var (x, y, width, height) = size is { } s ? (0, 0, s.Width, s.Height) : Extent(content, extra);
        var markerFill = size is null ? " fill=\"#5B5BF0\"" : "";
        var scale = Math.Min(1, Math.Min(FlowLayout.MaxWidth / width, FlowLayout.MaxHeight / height));
        return $"<svg class=\"folio-diagram\" xmlns=\"http://www.w3.org/2000/svg\" width=\"{(int)Math.Round(width * scale)}\" height=\"{(int)Math.Round(height * scale)}\" viewBox=\"{x} {y} {width} {height}\"><defs><marker id=\"arrow\" markerWidth=\"4\" markerHeight=\"3\" refX=\"4\" refY=\"1.5\" orient=\"auto\"><path d=\"M0,0 L4,1.5 L0,3 z\"{markerFill}/></marker></defs><g class=\"folio-diagram-content\" fill=\"white\" stroke=\"currentColor\" stroke-width=\"2\" font-family=\"sans-serif\" font-size=\"14\">{content}</g></svg>";
    }

    /// <summary>The bounding box of the elements in <paramref name="content"/> (rects, circles, polygons, paths and text) plus the margin.</summary>
    private static (int X, int Y, int Width, int Height) Extent(string content, FlowLayout.Box[] extra)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Add(double x, double y) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
        foreach (var box in extra) { Add(box.X, box.Y); Add(box.X + box.Width, box.Y + box.Height); }
        System.Xml.Linq.XElement root;
        try { root = System.Xml.Linq.XElement.Parse($"<g xmlns=\"http://www.w3.org/2000/svg\">{content}</g>"); }
        catch (XmlException) { return (0, 0, 800, 400); }
        double Attr(System.Xml.Linq.XElement e, string name) => double.TryParse((string?)e.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        foreach (var e in root.Descendants())
        {
            switch (e.Name.LocalName)
            {
                case "rect": Add(Attr(e, "x"), Attr(e, "y")); Add(Attr(e, "x") + Attr(e, "width"), Attr(e, "y") + Attr(e, "height")); break;
                case "circle": Add(Attr(e, "cx") - Attr(e, "r"), Attr(e, "cy") - Attr(e, "r")); Add(Attr(e, "cx") + Attr(e, "r"), Attr(e, "cy") + Attr(e, "r")); break;
                case "polygon" or "path":
                    // Numbers in a path are x y pairs except after an arc command, where only the last two of its seven are a point.
                    var data = (string?)e.Attribute(e.Name.LocalName == "path" ? "d" : "points") ?? "";
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(data, @"[AMLZ]?[^AMLZ]*"))
                    {
                        var numbers = System.Text.RegularExpressions.Regex.Matches(m.Value, @"-?\d+(\.\d+)?").Select(q => double.Parse(q.Value, CultureInfo.InvariantCulture)).ToArray();
                        if (m.Value.StartsWith('A') && numbers.Length >= 7) Add(numbers[5], numbers[6]);
                        else for (var i = 0; i + 1 < numbers.Length; i += 2) Add(numbers[i], numbers[i + 1]);
                    }
                    break;
                case "text":
                    var width = TextMeasure.Width(e.Value, 14, bold: false);
                    var anchor = (string?)e.Attribute("text-anchor");
                    var left = Attr(e, "x") - (anchor == "middle" ? width / 2 : anchor == "end" ? width : 0);
                    Add(left, Attr(e, "y") - 12); Add(left + width, Attr(e, "y") + 4);
                    break;
            }
        }
        if (minX > maxX) return (0, 0, 32, 32);
        var margin = (int)FlowLayout.Margin;
        var x0 = (int)Math.Floor(minX) - margin; var y0 = (int)Math.Floor(minY) - margin;
        return (x0, y0, (int)Math.Ceiling(maxX) + margin - x0, (int)Math.Ceiling(maxY) + margin - y0);
    }

    private sealed record FlowNode(string Id, string Label, string Shape);

    private static FlowNode Node(string text)
    {
        text = text.Trim();
        var circle = text.IndexOf("((", StringComparison.Ordinal);
        if (circle >= 0 && text.EndsWith("))", StringComparison.Ordinal))
            return new FlowNode(text[..circle].Trim(), text[(circle + 2)..^2].Trim(), "circle");
        foreach (var (open, close, shape) in new[] { ('[', ']', "rect"), ('(', ')', "round"), ('{', '}', "diamond") })
        {
            var start = text.IndexOf(open);
            if (start >= 0 && text.EndsWith(close)) return new FlowNode(text[..start].Trim(), text[(start + 1)..^1].Trim(), shape);
        }
        return new FlowNode(text, text, "rect");
    }

    private static int Index(List<FlowNode> nodes, FlowNode node)
    {
        var index = nodes.FindIndex(existing => existing.Id == node.Id);
        if (index >= 0) return index;
        nodes.Add(node);
        return nodes.Count - 1;
    }

    private static int Index(List<string> names, string name)
    {
        var index = names.IndexOf(name);
        if (index >= 0) return index;
        names.Add(name);
        return names.Count - 1;
    }

    private static string XmlEscape(string value) => System.Security.SecurityElement.Escape(value) ?? "";
}
