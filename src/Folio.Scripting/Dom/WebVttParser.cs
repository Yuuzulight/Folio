using System.Globalization;

namespace Folio.Scripting.Dom;

internal sealed record WebVttCue(double StartTime, double EndTime, string Text);

internal static class WebVttParser
{
    public static IReadOnlyList<WebVttCue> Parse(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return [];
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        if (lines.Length == 0 || !lines[0].StartsWith("WEBVTT", StringComparison.Ordinal)) return [];
        var cues = new List<WebVttCue>();
        for (var i = 1; i < lines.Length;)
        {
            while (i < lines.Length && string.IsNullOrWhiteSpace(lines[i])) i++;
            if (i >= lines.Length) break;
            if (lines[i].StartsWith("NOTE", StringComparison.Ordinal)) { while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i])) i++; continue; }
            string timing;
            if (lines[i].Contains("-->", StringComparison.Ordinal))
            {
                timing = lines[i++];
            }
            else if (i + 1 < lines.Length && lines[i + 1].Contains("-->", StringComparison.Ordinal))
            {
                timing = lines[i + 1];
                i += 2;
            }
            else
            {
                timing = "";
            }
            if (timing.Length == 0) { while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i])) i++; continue; }
            var arrow = timing.IndexOf("-->", StringComparison.Ordinal);
            var endParts = arrow >= 0 ? timing[(arrow + 3)..].Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];
            if (arrow <= 0 || endParts.Length == 0 || !TryTime(timing[..arrow].Trim(), out var start) || !TryTime(endParts[0], out var end) || end < start) { while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i])) i++; continue; }
            var text = new List<string>();
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i])) text.Add(lines[i++]);
            cues.Add(new WebVttCue(start, end, System.Net.WebUtility.HtmlDecode(string.Join('\n', text))));
        }
        return cues;
    }

    private static bool TryTime(string text, out double seconds)
    {
        seconds = 0;
        var parts = text.Split(':');
        if (parts.Length is < 2 or > 3 || !double.TryParse(parts[^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var sec) || sec >= 60) return false;
        var minIndex = parts.Length - 2;
        if (!double.TryParse(parts[minIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) || minutes >= 60) return false;
        var hours = 0d;
        if (parts.Length == 3 && (!double.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out hours) || hours < 0)) return false;
        seconds = hours * 3600 + minutes * 60 + sec;
        return seconds >= 0;
    }
}
