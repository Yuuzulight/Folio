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
                                        : line.StartsWith("title", StringComparison.OrdinalIgnoreCase) || line.Contains(':');
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
            return RenderFlow(lines.Skip(1));
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
            return lines.Any(line => line.Contains(':') && double.TryParse(line[(line.IndexOf(':') + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _));
        if (first.StartsWith("timeline", StringComparison.OrdinalIgnoreCase))
            return lines.Any(line => line.Contains(':'));
        if (first.StartsWith("gantt", StringComparison.OrdinalIgnoreCase))
            return lines.Any(line => line.Split(',', StringSplitOptions.TrimEntries).Length >= 3);
        return false;
    }

    private static string RenderPie(IEnumerable<string> lines)
    {
        var slices = lines.Select(line => line.Split(':', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            .Select(parts => (Label: parts[0].Trim('"'), Value: double.Parse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture)))
            .Where(slice => slice.Value > 0)
            .ToList();
        var total = slices.Sum(slice => slice.Value);
        if (total <= 0) return Svg([], []);
        const double cx = 230, cy = 190, radius = 130;
        var angle = -Math.PI / 2;
        var arcs = new List<string>();
        var legend = new List<string>();
        for (var i = 0; i < slices.Count; i++)
        {
            var slice = slices[i];
            var end = angle + slice.Value / total * Math.PI * 2;
            var x1 = cx + radius * Math.Cos(angle); var y1 = cy + radius * Math.Sin(angle);
            var x2 = cx + radius * Math.Cos(end); var y2 = cy + radius * Math.Sin(end);
            var large = end - angle > Math.PI ? 1 : 0;
            var path = $"M {cx:0.##} {cy:0.##} L {x1:0.##} {y1:0.##} A {radius:0.##} {radius:0.##} 0 {large} 1 {x2:0.##} {y2:0.##} Z";
            arcs.Add($"<path class=\"folio-diagram-slice\" data-label=\"{XmlEscape(slice.Label)}\" d=\"{path}\"/>");
            legend.Add($"<text class=\"folio-diagram-label folio-diagram-legend\" x=\"470\" y=\"{70 + i * 28}\">{XmlEscape(slice.Label)} ({slice.Value.ToString("0.##", CultureInfo.InvariantCulture)})</text>");
            angle = end;
        }
        return Svg(arcs.Concat(legend), []);
    }

    private static string RenderFlow(IEnumerable<string> lines)
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
            var arrow = FindFlowArrow(line, out var markerLength, out var kind);
            if (arrow < 0) continue;
            var rest = line[(arrow + markerLength)..].Trim();
            var edgeLabel = "";
            if (rest.StartsWith("|", StringComparison.Ordinal))
            {
                var end = rest.IndexOf('|', 1);
                if (end > 1) { edgeLabel = rest[1..end].Trim(); rest = rest[(end + 1)..].Trim(); }
            }
            var left = Node(line[..arrow].TrimEnd('<', '|', '.', '=', ' '));
            var right = Node(rest);
            var from = Index(nodes, left);
            var to = Index(nodes, right);
            currentSubgraph?.Nodes.Add(left.Id);
            currentSubgraph?.Nodes.Add(right.Id);
            edges.Add((from, to, edgeLabel, kind));
        }
        var body = nodes.Select((node, i) => NodeSvg(node, i, nodeClasses.TryGetValue(node.Id, out var className) ? className : null, classStyles));
        var groups = subgraphs.Select((group, i) =>
        {
            var indexes = nodes.Select((node, index) => (node, index)).Where(pair => group.Nodes.Contains(pair.node.Id)).Select(pair => pair.index).ToArray();
            if (indexes.Length == 0) return "";
            var left = 20 + indexes.Min() * 180; var right = 170 + indexes.Max() * 180;
            return $"<rect class=\"folio-diagram-subgraph\" data-name=\"{XmlEscape(group.Name)}\" fill=\"none\" x=\"{left}\" y=\"18\" width=\"{right - left}\" height=\"100\" rx=\"10\"/><text class=\"folio-diagram-label folio-diagram-subgraph-label\" x=\"{left + 8}\" y=\"32\">{XmlEscape(group.Name)}</text>";
        });
        var overlays = edges.Select(edge => $"<path class=\"folio-diagram-edge\" data-kind=\"{XmlEscape(edge.Kind)}\" d=\"M {110 + edge.From * 180} 84 L {110 + edge.To * 180} 40\" marker-end=\"url(#arrow)\"/>{(edge.Label.Length == 0 ? "" : $"<text class=\"folio-diagram-label folio-diagram-edge-label\" x=\"{100 + Math.Min(edge.From, edge.To) * 180}\" y=\"72\">{XmlEscape(edge.Label)}</text>")}");
        return Svg(body, groups.Concat(overlays));
    }

    private static int FindFlowArrow(string line, out int length, out string kind)
    {
        foreach (var candidate in new[] { "-.->", "==>", "-->", "---", "--" })
        {
            var index = line.IndexOf(candidate, StringComparison.Ordinal);
            if (index >= 0) { length = candidate.Length; kind = candidate; return index; }
        }
        length = 0; kind = ""; return -1;
    }

    private static string NodeSvg(FlowNode node, int index, string? className = null, IReadOnlyDictionary<string, string>? classStyles = null)
    {
        var x = 40 + index * 180;
        var classAttribute = className is null ? "" : $" data-class=\"{XmlEscape(className)}\"";
        var styleAttribute = className is not null && classStyles is not null && classStyles.TryGetValue(className, out var style) ? $" style=\"{XmlEscape(style)}\"" : "";
        var shape = node.Shape switch
        {
            "round" => $"<rect class=\"folio-diagram-node folio-diagram-round\"{classAttribute}{styleAttribute} x=\"{x}\" y=\"40\" width=\"140\" height=\"44\" rx=\"22\"/>",
            "diamond" => $"<path class=\"folio-diagram-node folio-diagram-diamond\"{classAttribute}{styleAttribute} d=\"M {x + 70} 30 L {x + 140} 62 L {x + 70} 94 L {x} 62 Z\"/>",
            "circle" => $"<circle class=\"folio-diagram-node folio-diagram-circle\"{classAttribute}{styleAttribute} cx=\"{x + 70}\" cy=\"62\" r=\"28\"/>",
            _ => $"<rect class=\"folio-diagram-node folio-diagram-rect\"{classAttribute}{styleAttribute} x=\"{x}\" y=\"40\" width=\"140\" height=\"44\" rx=\"8\"/>"
        };
        return $"{shape}<text class=\"folio-diagram-label\" x=\"{x + 70}\" y=\"67\">{XmlEscape(node.Label)}</text>";
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
        var marks = participants.Select((name, i) => $"<text class=\"folio-diagram-participant\" data-role=\"participant\" x=\"{80 + i * 180}\" y=\"30\">{XmlEscape(name)}</text><path class=\"folio-diagram-lifeline\" d=\"M {80 + i * 180} 40 L {80 + i * 180} {60 + messages.Count * 48}\"/>{(activations.Contains(i) ? $"<rect class=\"folio-diagram-activation\" x=\"{74 + i * 180}\" y=\"40\" width=\"12\" height=\"{messages.Count * 48}\"/>" : "")}");
        var arrows = messages.Select((message, i) => $"<path class=\"folio-diagram-edge sequence-message\" data-kind=\"{XmlEscape(message.Kind)}\" {(message.Dashed ? "stroke-dasharray=\"4 3\"" : "")} d=\"M {80 + message.From * 180} {60 + i * 48} L {80 + message.To * 180} {60 + i * 48}\" marker-end=\"url(#arrow)\"/><text class=\"folio-diagram-label sequence-message-label\" x=\"{90 + Math.Min(message.From, message.To) * 180}\" y=\"{54 + i * 48}\">{XmlEscape(message.Label)}</text>");
        var blockSvg = blocks.Select((block, i) => $"<rect class=\"folio-diagram-block sequence-{XmlEscape(block.Kind)}\" x=\"12\" y=\"{48 + block.Start * 48}\" width=\"760\" height=\"{Math.Max(28, (block.End - block.Start) * 48)}\"/><text class=\"folio-diagram-label sequence-block-label\" x=\"20\" y=\"{68 + block.Start * 48}\">{XmlEscape(block.Kind)} {XmlEscape(block.Label)}</text>");
        var noteSvg = notes.Select(note => $"<rect class=\"folio-diagram-note\" x=\"{(note.Right ? 110 : 0) + note.Participant * 180}\" y=\"{42 + messages.Count * 48}\" width=\"140\" height=\"26\"/><text class=\"folio-diagram-label\" x=\"{(note.Right ? 118 : 8) + note.Participant * 180}\" y=\"{60 + messages.Count * 48}\">{XmlEscape(note.Text)}</text>");
        return Svg(marks, arrows.Concat(blockSvg).Concat(noteSvg));
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
        var boxes = names.Select((name, i) =>
        {
            var rows = members.TryGetValue(name, out var values) ? values : [];
            var height = 54 + rows.Count * 18;
            var text = $"<text class=\"folio-diagram-label class-label\" x=\"{105 + i * 180}\" y=\"64\">{XmlEscape(name)}</text>";
            text += string.Join("", rows.Select((member, row) => $"<text class=\"folio-diagram-label class-member\" x=\"40\" y=\"{84 + row * 18}\">{XmlEscape(member)}</text>"));
            return $"<rect class=\"folio-diagram-node class-node\" x=\"{30 + i * 180}\" y=\"40\" width=\"150\" height=\"{height}\" rx=\"6\"/>{text}";
        });
        var paths = edges.Select(e => { var a = names.IndexOf(e.From); var b = names.IndexOf(e.To); return $"<path class=\"class-edge\" d=\"M {105 + a * 180} 40 L {105 + b * 180} 94\" data-kind=\"{XmlEscape(e.Kind)}\" data-cardinality=\"{XmlEscape(e.Label)}\" marker-end=\"url(#arrow)\"/>"; });
        return Svg(boxes, paths);
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
                return $"<rect class=\"folio-diagram-node state-node\" x=\"30\" y=\"{y}\" width=\"180\" height=\"40\" rx=\"20\"/><text class=\"folio-diagram-label\" x=\"120\" y=\"{y + 25}\">{label}</text>";
            if (kind == "choice")
                return $"<polygon class=\"folio-diagram-node state-choice\" data-kind=\"choice\" points=\"120,{y} 140,{y + 20} 120,{y + 40} 100,{y + 20}\"/><text class=\"folio-diagram-label\" x=\"150\" y=\"{y + 25}\">{label}</text>";
            return $"<rect class=\"folio-diagram-node state-{kind}\" data-kind=\"{kind}\" x=\"70\" y=\"{y + 16}\" width=\"100\" height=\"8\"/><text class=\"folio-diagram-label\" x=\"180\" y=\"{y + 25}\">{label}</text>";
        });
        var paths = edges.Select(e => { var a = states.IndexOf(e.From); var b = states.IndexOf(e.To); var y = 50 + Math.Min(a, b) * 64; return $"<path class=\"state-edge\" d=\"M 120 {70 + a * 64} L 120 {30 + b * 64}\" marker-end=\"url(#arrow)\"/>{(e.Label.Length == 0 ? "" : $"<text class=\"folio-diagram-label state-transition-label\" x=\"145\" y=\"{y}\">{XmlEscape(e.Label)}</text>")}"; });
        var noteSvg = notes.Select(note => $"<text class=\"folio-diagram-note\" data-state=\"{XmlEscape(note.State)}\" x=\"230\" y=\"{55 + Math.Max(0, states.IndexOf(note.State)) * 64}\">{XmlEscape(note.Text)}</text>");
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
        var boxes = entities.Select((name, i) => { var attrs = attributes.TryGetValue(name, out var values) ? values : []; var height = 50 + attrs.Count * 18; var text = $"<text class=\"folio-diagram-label\" x=\"{105 + i * 180}\" y=\"70\">{XmlEscape(name)}</text>" + string.Join("", attrs.Select((a, n) => $"<text class=\"folio-diagram-label er-attribute\" x=\"{40 + i * 180}\" y=\"{88 + n * 18}\">{XmlEscape(a)}</text>")); return $"<rect class=\"folio-diagram-node er-entity\" x=\"{30 + i * 180}\" y=\"40\" width=\"150\" height=\"{height}\"/>{text}"; });
        var paths = edges.Select(e => { var a = entities.IndexOf(e.From); var b = entities.IndexOf(e.To); return $"<path class=\"er-edge\" d=\"M {105 + a * 180} 90 L {105 + b * 180} 40\" data-cardinality=\"{XmlEscape(e.Label)}\" marker-end=\"url(#arrow)\"/>"; });
        return Svg(boxes, paths);
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
            var sectionSvg = item.Section is null ? "" : $"<text class=\"folio-diagram-section\" x=\"20\" y=\"{y - 20}\">{XmlEscape(item.Section)}</text>";
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

    private static string Svg(IEnumerable<string> body, IEnumerable<string> overlays)
    {
        var content = string.Join("", body.Concat(overlays));
        return $"<svg class=\"folio-diagram\" xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 800 400\"><defs><marker id=\"arrow\" markerWidth=\"8\" markerHeight=\"8\" refX=\"7\" refY=\"4\" orient=\"auto\"><path d=\"M0,0 L8,4 L0,8 z\"/></marker></defs><g class=\"folio-diagram-content\" fill=\"white\" stroke=\"currentColor\" font-family=\"sans-serif\" font-size=\"14\">{content}</g></svg>";
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
