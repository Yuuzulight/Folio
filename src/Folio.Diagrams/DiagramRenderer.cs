using System.Text;
using System.Xml;

namespace Folio.Diagrams;

/// <summary>Small, dependency-free renderer for the diagram text used by scripted artifacts.</summary>
public static class DiagramRenderer
{
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
        return lines[0].StartsWith("sequenceDiagram", StringComparison.OrdinalIgnoreCase)
            ? RenderSequence(lines.Skip(1))
            : RenderFlow(lines);
    }

    private static string RenderPie(IEnumerable<string> lines)
    {
        var slices = lines.Select(line => line.Split(':', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 && double.TryParse(parts[1], out _))
            .Select(parts => (Label: parts[0].Trim('"'), Value: double.Parse(parts[1])))
            .ToList();
        var total = slices.Sum(slice => slice.Value);
        var bars = slices.Select((slice, i) => $"<rect x=\"40\" y=\"{40 + i * 36}\" width=\"{Math.Max(1, 500 * slice.Value / Math.Max(1, total)):0.##}\" height=\"24\"/><text x=\"560\" y=\"{58 + i * 36}\">{XmlEscape(slice.Label)} ({slice.Value:0.##})</text>");
        return Svg(bars, []);
    }

    private static string RenderFlow(IEnumerable<string> lines)
    {
        var nodes = new List<(string Id, string Label)>();
        var edges = new List<(int From, int To)>();
        foreach (var line in lines)
        {
            var arrow = line.IndexOf("-->", StringComparison.Ordinal);
            var markerLength = 3;
            if (arrow < 0)
            {
                arrow = line.IndexOf("--", StringComparison.Ordinal);
                markerLength = 2;
            }
            if (arrow < 0) continue;
            var left = Node(line[..arrow].TrimEnd('<', '|'));
            var right = Node(line[(arrow + markerLength)..].TrimStart('>', '|'));
            var from = Index(nodes, left);
            var to = Index(nodes, right);
            edges.Add((from, to));
        }
        return Svg(nodes.Select((node, i) => $"<rect x=\"{40 + i * 180}\" y=\"40\" width=\"140\" height=\"44\" rx=\"8\"/><text x=\"{110 + i * 180}\" y=\"67\">{XmlEscape(node.Label)}</text>"),
            edges.Select(edge => $"<path d=\"M {110 + edge.From * 180} 84 L {110 + edge.To * 180} 40\" marker-end=\"url(#arrow)\"/>"));
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
        var marks = participants.Select((name, i) => $"<text x=\"{80 + i * 180}\" y=\"30\">{XmlEscape(name)}</text><path d=\"M {80 + i * 180} 40 L {80 + i * 180} {60 + messages.Count * 48}\"/>");
        var arrows = messages.Select((message, i) => $"<path d=\"M {80 + message.From * 180} {60 + i * 48} L {80 + message.To * 180} {60 + i * 48}\" marker-end=\"url(#arrow)\"/><text x=\"{90 + Math.Min(message.From, message.To) * 180}\" y=\"{54 + i * 48}\">{XmlEscape(message.Label)}</text>");
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
        var boxes = names.Select((name, i) => $"<rect class=\"class-node\" x=\"{30 + i * 180}\" y=\"40\" width=\"150\" height=\"54\" rx=\"6\"/><text class=\"class-label\" x=\"{105 + i * 180}\" y=\"72\">{XmlEscape(name)}</text>");
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
        var boxes = states.Select((name, i) => $"<rect class=\"state-node\" x=\"30\" y=\"{30 + i * 64}\" width=\"180\" height=\"40\" rx=\"20\"/><text x=\"120\" y=\"{55 + i * 64}\">{XmlEscape(name == "[*]" ? "start" : name)}</text>");
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
        var boxes = entities.Select((name, i) => $"<rect class=\"er-entity\" x=\"{30 + i * 180}\" y=\"40\" width=\"150\" height=\"50\"/><text x=\"{105 + i * 180}\" y=\"70\">{XmlEscape(name)}</text>");
        var paths = edges.Select(e => { var a = entities.IndexOf(e.From); var b = entities.IndexOf(e.To); return $"<path class=\"er-edge\" d=\"M {105 + a * 180} 90 L {105 + b * 180} 40\" data-cardinality=\"{XmlEscape(e.Label)}\" marker-end=\"url(#arrow)\"/>"; });
        return Svg(boxes, paths);
    }

    private static string RenderTimeline(IEnumerable<string> lines)
    {
        var items = lines.Select(line => line.Trim()).Where(line => line.Contains(':')).Select(line => line.Split(':', 2)).ToList();
        var body = items.Select((item, i) => $"<circle class=\"timeline-event\" cx=\"50\" cy=\"{40 + i * 48}\" r=\"6\"/><text x=\"70\" y=\"{45 + i * 48}\"><tspan class=\"timeline-date\">{XmlEscape(item[0].Trim())}</tspan> {XmlEscape(item[1].Trim())}</text>");
        return Svg(body, []);
    }

    private static string RenderGantt(IEnumerable<string> lines)
    {
        var tasks = new List<(string Name, DateTime Start, int Days)>();
        foreach (var line in lines.Select(x => x.Trim()))
        {
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 3 || !DateTime.TryParse(parts[^2], out var start)) continue;
            var duration = parts[^1].EndsWith('d') && int.TryParse(parts[^1][..^1], out var d) ? d : 1;
            tasks.Add((parts[0].Split(':')[0].Trim(), start, Math.Max(1, duration)));
        }
        var origin = tasks.Count == 0 ? DateTime.Today : tasks.Min(t => t.Start);
        var bars = tasks.Select((task, i) => { var x = 180 + (task.Start - origin).Days * 18; return $"<text x=\"10\" y=\"{45 + i * 36}\">{XmlEscape(task.Name)}</text><rect class=\"gantt-task\" x=\"{x}\" y=\"{28 + i * 36}\" width=\"{Math.Max(12, task.Days * 18)}\" height=\"22\"/>"; });
        return Svg(bars, []);
    }

    private static string Svg(IEnumerable<string> body, IEnumerable<string> overlays)
    {
        var content = string.Join("", body.Concat(overlays));
        return $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 800 400\"><defs><marker id=\"arrow\" markerWidth=\"8\" markerHeight=\"8\" refX=\"7\" refY=\"4\" orient=\"auto\"><path d=\"M0,0 L8,4 L0,8 z\"/></marker></defs><g fill=\"white\" stroke=\"currentColor\" font-family=\"sans-serif\" font-size=\"14\">{content}</g></svg>";
    }

    private static (string Id, string Label) Node(string text)
    {
        text = text.Trim();
        var open = text.IndexOf('[');
        if (open >= 0 && text.EndsWith(']')) return (text[..open].Trim(), text[(open + 1)..^1]);
        return (text, text);
    }

    private static int Index(List<(string Id, string Label)> nodes, (string Id, string Label) node)
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
