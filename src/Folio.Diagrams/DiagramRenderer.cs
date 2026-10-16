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
        svg = Render(source);
        return true;
    }

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
            return lines.Any(line => line.Contains("->>", StringComparison.Ordinal));
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
        foreach (var line in lines)
        {
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
            edges.Add((from, to, edgeLabel, kind));
        }
        var body = nodes.Select((node, i) => NodeSvg(node, i));
        var overlays = edges.Select(edge => $"<path class=\"folio-diagram-edge\" data-kind=\"{XmlEscape(edge.Kind)}\" d=\"M {110 + edge.From * 180} 84 L {110 + edge.To * 180} 40\" marker-end=\"url(#arrow)\"/>{(edge.Label.Length == 0 ? "" : $"<text class=\"folio-diagram-label folio-diagram-edge-label\" x=\"{100 + Math.Min(edge.From, edge.To) * 180}\" y=\"72\">{XmlEscape(edge.Label)}</text>")}");
        return Svg(body, overlays);
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

    private static string NodeSvg(FlowNode node, int index)
    {
        var x = 40 + index * 180;
        var shape = node.Shape switch
        {
            "round" => $"<rect class=\"folio-diagram-node folio-diagram-round\" x=\"{x}\" y=\"40\" width=\"140\" height=\"44\" rx=\"22\"/>",
            "diamond" => $"<path class=\"folio-diagram-node folio-diagram-diamond\" d=\"M {x + 70} 30 L {x + 140} 62 L {x + 70} 94 L {x} 62 Z\"/>",
            "circle" => $"<circle class=\"folio-diagram-node folio-diagram-circle\" cx=\"{x + 70}\" cy=\"62\" r=\"28\"/>",
            _ => $"<rect class=\"folio-diagram-node folio-diagram-rect\" x=\"{x}\" y=\"40\" width=\"140\" height=\"44\" rx=\"8\"/>"
        };
        return $"{shape}<text class=\"folio-diagram-label\" x=\"{x + 70}\" y=\"67\">{XmlEscape(node.Label)}</text>";
    }

    private static string RenderSequence(IEnumerable<string> lines)
    {
        var participants = new List<string>();
        var messages = new List<(int From, int To, string Label)>();
        foreach (var line in lines)
        {
            var arrow = line.IndexOf("->>", StringComparison.Ordinal);
            if (arrow < 0) continue;
            var left = line[..arrow].Trim();
            var rest = line[(arrow + 3)..];
            var colon = rest.IndexOf(':');
            var right = (colon < 0 ? rest : rest[..colon]).Trim();
            var label = colon < 0 ? "" : rest[(colon + 1)..].Trim();
            var from = Index(participants, left);
            var to = Index(participants, right);
            messages.Add((from, to, label));
        }
        var marks = participants.Select((name, i) => $"<text class=\"folio-diagram-participant\" x=\"{80 + i * 180}\" y=\"30\">{XmlEscape(name)}</text><path class=\"folio-diagram-lifeline\" d=\"M {80 + i * 180} 40 L {80 + i * 180} {60 + messages.Count * 48}\"/>");
        var arrows = messages.Select((message, i) => $"<path class=\"folio-diagram-edge\" d=\"M {80 + message.From * 180} {60 + i * 48} L {80 + message.To * 180} {60 + i * 48}\" marker-end=\"url(#arrow)\"/><text class=\"folio-diagram-label\" x=\"{90 + Math.Min(message.From, message.To) * 180}\" y=\"{54 + i * 48}\">{XmlEscape(message.Label)}</text>");
        return Svg(marks, arrows);
    }

    private static string RenderClass(IEnumerable<string> lines)
    {
        var names = new List<string>();
        var edges = new List<(string From, string To, string Kind)>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("%%", StringComparison.Ordinal)) continue;
            var relation = line.IndexOf("<|--", StringComparison.Ordinal) >= 0 ? "<|--" : line.IndexOf("..>", StringComparison.Ordinal) >= 0 ? "..>" : "-->";
            var at = line.IndexOf(relation, StringComparison.Ordinal);
            if (at >= 0)
            {
                var from = line[..at].Trim(); var to = line[(at + relation.Length)..].Split(':', 2)[0].Trim();
                if (from.Length > 0 && to.Length > 0) { names.Add(from); names.Add(to); edges.Add((from, to, relation)); }
                continue;
            }
            if (line.StartsWith("class ", StringComparison.OrdinalIgnoreCase)) names.Add(line[6..].Split([' ', '{'], 2)[0]);
            else if (line.StartsWith("class ", StringComparison.OrdinalIgnoreCase) == false && line.Contains('{')) names.Add(line[..line.IndexOf('{')].Trim());
        }
        names = names.Distinct(StringComparer.Ordinal).ToList();
        var boxes = names.Select((name, i) => $"<rect class=\"folio-diagram-node class-node\" x=\"{30 + i * 180}\" y=\"40\" width=\"150\" height=\"54\" rx=\"6\"/><text class=\"folio-diagram-label class-label\" x=\"{105 + i * 180}\" y=\"72\">{XmlEscape(name)}</text>");
        var paths = edges.Select(e => { var a = names.IndexOf(e.From); var b = names.IndexOf(e.To); return $"<path class=\"class-edge\" d=\"M {105 + a * 180} 40 L {105 + b * 180} 94\" data-kind=\"{XmlEscape(e.Kind)}\" marker-end=\"url(#arrow)\"/>"; });
        return Svg(boxes, paths);
    }

    private static string RenderState(IEnumerable<string> lines)
    {
        var states = new List<string>(); var edges = new List<(string From, string To)>();
        foreach (var raw in lines)
        {
            var line = raw.Trim(); var at = line.IndexOf("-->", StringComparison.Ordinal);
            if (at < 0) continue;
            var from = line[..at].Trim(); var to = line[(at + 3)..].Split(':', 2)[0].Trim();
            states.Add(from); states.Add(to); edges.Add((from, to));
        }
        states = states.Distinct(StringComparer.Ordinal).ToList();
        var boxes = states.Select((name, i) => $"<rect class=\"folio-diagram-node state-node\" x=\"30\" y=\"{30 + i * 64}\" width=\"180\" height=\"40\" rx=\"20\"/><text class=\"folio-diagram-label\" x=\"120\" y=\"{55 + i * 64}\">{XmlEscape(name == "[*]" ? "start" : name)}</text>");
        var paths = edges.Select(e => { var a = states.IndexOf(e.From); var b = states.IndexOf(e.To); return $"<path class=\"state-edge\" d=\"M 120 {70 + a * 64} L 120 {30 + b * 64}\" marker-end=\"url(#arrow)\"/>"; });
        return Svg(boxes, paths);
    }

    private static string RenderEr(IEnumerable<string> lines)
    {
        var entities = new List<string>(); var edges = new List<(string From, string To, string Label)>();
        foreach (var raw in lines)
        {
            var line = raw.Trim(); if (line.Length == 0 || line.StartsWith("%%", StringComparison.Ordinal)) continue;
            var tokens = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length >= 3 && (tokens[1].Contains('-') || tokens[1].Contains('|')))
            { entities.Add(tokens[0]); entities.Add(tokens[2]); edges.Add((tokens[0], tokens[2], tokens.Length > 4 ? string.Join(' ', tokens[4..]) : tokens[1])); }
            else if (tokens.Length >= 1 && tokens[0].Length > 0 && !tokens[0].Equals("title", StringComparison.OrdinalIgnoreCase)) entities.Add(tokens[0]);
        }
        entities = entities.Distinct(StringComparer.Ordinal).ToList();
        var boxes = entities.Select((name, i) => $"<rect class=\"folio-diagram-node er-entity\" x=\"{30 + i * 180}\" y=\"40\" width=\"150\" height=\"50\"/><text class=\"folio-diagram-label\" x=\"{105 + i * 180}\" y=\"70\">{XmlEscape(name)}</text>");
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
        var tasks = new List<(string Name, string Id, DateTime Start, int Days, string Dependency)>();
        var dateFormat = "yyyy-MM-dd";
        foreach (var line in lines.Select(x => x.Trim()))
        {
            if (line.StartsWith("dateFormat ", StringComparison.OrdinalIgnoreCase)) { dateFormat = ToDateFormat(line[11..].Trim()); continue; }
            if (line.StartsWith("axisFormat ", StringComparison.OrdinalIgnoreCase)) continue;
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
            tasks.Add((name, id, start, Math.Max(1, duration), dependency));
        }
        var origin = tasks.Count == 0 ? DateTime.Today : tasks.Min(t => t.Start);
        var bars = tasks.Select((task, i) => { var x = 180 + (task.Start - origin).Days * 18; return $"<text class=\"folio-diagram-label\" x=\"10\" y=\"{45 + i * 36}\">{XmlEscape(task.Name)}</text><rect class=\"folio-diagram-node gantt-task\" data-id=\"{XmlEscape(task.Id)}\" data-start=\"{task.Start:yyyy-MM-dd}\" data-days=\"{task.Days}\" data-after=\"{XmlEscape(task.Dependency)}\" x=\"{x}\" y=\"{28 + i * 36}\" width=\"{Math.Max(12, task.Days * 18)}\" height=\"22\"/>"; });
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
