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
