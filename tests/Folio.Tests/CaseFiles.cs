using System.Globalization;
using System.Text;

namespace Folio.Tests;

/// <summary>
/// Reads the text case files used by the parser tests:
/// "=== name", optional "%directive" lines, input lines (with \uXXXX escapes), "---", expected lines.
/// </summary>
internal static class CaseFiles
{
    public sealed record Case(string Id, List<string> Directives, string Input, List<string> Expected);

    public static Dictionary<string, Case> Load(params string[] directory)
    {
        var cases = new Dictionary<string, Case>();
        var dir = Path.Combine([AppContext.BaseDirectory, .. directory]);
        foreach (var file in Directory.GetFiles(dir, "*.txt").Where(f => Path.GetFileName(f) != "README.txt"))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith("=== ", StringComparison.Ordinal))
                    continue;
                var id = $"{Path.GetFileNameWithoutExtension(file)}: {lines[i][4..]}";

                var directives = new List<string>();
                for (i++; lines[i].StartsWith('%'); i++)
                    directives.Add(lines[i][1..]);

                var input = new List<string>();
                for (; lines[i] != "---"; i++)
                    input.Add(lines[i]);

                var expected = new List<string>();
                for (i++; i < lines.Length && !lines[i].StartsWith("=== ", StringComparison.Ordinal); i++)
                {
                    if (lines[i].Length > 0)
                        expected.Add(lines[i]);
                }
                i--;

                cases.Add(id, new Case(id, directives, Unescape(string.Join('\n', input)), expected));
            }
        }
        return cases;
    }

    public static string Unescape(string text)
    {
        var result = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 5 < text.Length && text[i + 1] == 'u')
            {
                result.Append((char)int.Parse(text.AsSpan(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                i += 5;
            }
            else
            {
                result.Append(text[i]);
            }
        }
        return result.ToString();
    }

    /// <summary>Quotes-safe escaping for dumps: \\ \" \n \t, and \uXXXX for controls and U+FFFD..U+FFFF.</summary>
    public static string Escape(string text)
    {
        var result = new StringBuilder();
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': result.Append(@"\\"); break;
                case '"': result.Append("\\\""); break;
                case '\n': result.Append(@"\n"); break;
                case '\t': result.Append(@"\t"); break;
                case < ' ' or (>= '\u007F' and <= '\u009F') or '\u00AD' or >= '\uFFFD':
                    result.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}");
                    break;
                default: result.Append(c); break;
            }
        }
        return result.ToString();
    }
}
