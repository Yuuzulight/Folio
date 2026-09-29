using System.Globalization;
using Folio.Typography;

namespace Folio.Tests.Typography;

public class BidiTests
{
    [Fact]
    public void ResolvesMixedText()
    {
        // "abc ABC 123" with ABC in Hebrew, left to right: the Hebrew and the number after it are right to left.
        int[] text = [.. "abc ".Select(c => (int)c), 0x05D0, 0x05D1, 0x05D2, ' ', '1', '2', '3'];

        var (levels, paragraph) = Bidi.Resolve(text, null);

        Assert.Equal(0, paragraph);
        Assert.Equal([0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2], levels.Select(l => (int)l));
        Assert.Equal([0, 1, 2, 3, 8, 9, 10, 7, 6, 5, 4], Bidi.Reorder(levels, 0, text.Length));
    }

    [Fact]
    public void PassesBidiTest()
    {
        var failures = new List<string>();
        var count = 0;
        string[] levels = [], order = [];
        foreach (var line in File.ReadLines(UnicodeConformance.File("BidiTest.txt")))
        {
            if (line.StartsWith("@Levels:", StringComparison.Ordinal))
            {
                levels = line[8..].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                continue;
            }
            if (line.StartsWith("@Reorder:", StringComparison.Ordinal))
            {
                order = line[9..].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                continue;
            }
            var data = line.Split('#')[0].Trim();
            if (data.Length == 0 || data.StartsWith('@'))
                continue;
            var parts = data.Split(';');
            var types = parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Enum.Parse<BidiClass>).ToArray();
            var set = int.Parse(parts[1].Trim(), CultureInfo.InvariantCulture);
            foreach (var (bit, direction) in new[] { (1, (int?)null), (2, 0), (4, 1) })
            {
                if ((set & bit) == 0)
                    continue;
                count++;
                var (actual, _) = Bidi.Resolve(types, null, direction);
                var actualLevels = actual.Select(l => l == Bidi.Removed ? "x" : l.ToString(CultureInfo.InvariantCulture)).ToArray();
                var actualOrder = Bidi.Reorder(actual, 0, actual.Length).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray();
                if (!actualLevels.SequenceEqual(levels) || !actualOrder.SequenceEqual(order))
                    failures.Add($"{data} (paragraph {direction?.ToString(CultureInfo.InvariantCulture) ?? "auto"}): levels {string.Join(" ", actualLevels)}, order {string.Join(" ", actualOrder)}; expected {string.Join(" ", levels)} / {string.Join(" ", order)}");
            }
        }
        Assert.True(count > 1000, $"Only {count} tests read.");
        Assert.True(failures.Count == 0, $"{failures.Count} of {count} failed:\n{string.Join("\n", failures.Take(20))}");
    }

    [Fact]
    public void PassesBidiCharacterTest()
    {
        var failures = new List<string>();
        var count = 0;
        foreach (var line in File.ReadLines(UnicodeConformance.File("BidiCharacterTest.txt")))
        {
            var data = line.Split('#')[0].Trim();
            if (data.Length == 0)
                continue;
            var f = data.Split(';');
            var text = f[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(h => int.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
            int? direction = f[1] switch { "0" => 0, "1" => 1, _ => null };
            count++;
            var (actual, paragraph) = Bidi.Resolve(text, direction);
            var actualLevels = string.Join(" ", actual.Select(l => l == Bidi.Removed ? "x" : l.ToString(CultureInfo.InvariantCulture)));
            var actualOrder = string.Join(" ", Bidi.Reorder(actual, 0, actual.Length));
            if (paragraph.ToString(CultureInfo.InvariantCulture) != f[2] || actualLevels != f[3].Trim() || actualOrder != f[4].Trim())
                failures.Add($"{data}\n    got {paragraph}; {actualLevels}; {actualOrder}");
        }
        Assert.True(count > 1000, $"Only {count} tests read.");
        Assert.True(failures.Count == 0, $"{failures.Count} of {count} failed:\n{string.Join("\n", failures.Take(20))}");
    }
}
