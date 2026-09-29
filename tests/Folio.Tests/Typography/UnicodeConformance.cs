using System.Globalization;
using System.Text;

namespace Folio.Tests.Typography;

/// <summary>
/// Reads the Unicode conformance test files from the local cache (.cache/unicode/&lt;version&gt;, filled by CI or by
/// hand); tests skip when the cache is missing.
/// </summary>
internal static class UnicodeConformance
{
    /// <summary>The path of a conformance file, or a skip if the cache does not have it.</summary>
    public static string File(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (System.IO.File.Exists(Path.Combine(dir.FullName, "Folio.slnx")))
            {
                var path = Path.Combine(dir.FullName, ".cache", "unicode", Folio.Typography.UnicodeData.Version, relative);
                if (System.IO.File.Exists(path))
                    return path;
                break;
            }
        }
        Assert.Skip($"{relative} is not in .cache/unicode/{Folio.Typography.UnicodeData.Version}/. The Unicode conformance tests " +
                    "need the files from https://www.unicode.org/Public/" + Folio.Typography.UnicodeData.Version + "/ucd/ there.");
        return "";
    }

    /// <summary>
    /// Parses a break test line ("÷ 0041 × 0308 ÷ ...  # comment") into its text and the UTF-16 offsets of the
    /// boundaries (÷), including 0 and the text's length; null for comment lines.
    /// </summary>
    public static (string Text, List<int> Boundaries)? ParseBreakLine(string line)
    {
        var data = line.Split('#')[0].Trim();
        if (data.Length == 0)
            return null;
        var text = new StringBuilder();
        var boundaries = new List<int>();
        foreach (var token in data.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token == "\u00F7")
                boundaries.Add(text.Length);
            else if (token != "\u00D7")
                text.Append(char.ConvertFromUtf32(int.Parse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
        }
        return (text.ToString(), boundaries);
    }

    /// <summary>Checks every line of a break test file; reports the first failures with their lines.</summary>
    public static void CheckBreaks(string relative, Func<string, List<int>> find, Func<string, bool>? skip = null)
    {
        var failures = new List<string>();
        var count = 0;
        foreach (var line in System.IO.File.ReadLines(File(relative)))
        {
            if (ParseBreakLine(line) is not { } test || skip?.Invoke(test.Text) == true)
                continue;
            count++;
            var actual = find(test.Text);
            if (!actual.SequenceEqual(test.Boundaries))
                failures.Add($"{line}\n    got [{string.Join(", ", actual)}], expected [{string.Join(", ", test.Boundaries)}]");
        }
        Assert.True(count > 100, $"Only {count} tests read from {relative}.");
        Assert.True(failures.Count == 0, $"{failures.Count} of {count} failed:\n{string.Join("\n", failures.Take(20))}");
    }
}
