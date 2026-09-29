namespace Folio.Html;

/// <summary>Named character references (https://html.spec.whatwg.org/multipage/named-characters.html).</summary>
internal static partial class EntityTable
{
    public static int Count => Names.Length;

    /// <summary>
    /// Finds the longest reference name that <paramref name="input"/> starts with, as the named character
    /// reference state requires (some names match without their semicolon).
    /// </summary>
    /// <returns>The length of the matched name, or 0 when none matches.</returns>
    public static int Match(ReadOnlySpan<char> input, out string value)
    {
        // Names are sorted ordinally, so the names sharing a prefix form a range, and within it the one equal
        // to the prefix sorts first. Narrow the range one character at a time.
        int lo = 0, hi = Names.Length, best = -1, bestLength = 0;
        for (var k = 0; k < input.Length && lo < hi; k++)
        {
            var c = input[k];
            lo = FirstAtLeast(lo, hi, k, c);
            hi = FirstAtLeast(lo, hi, k, (char)(c + 1));
            if (lo < hi && Names[lo].Length == k + 1)
            {
                best = lo;
                bestLength = k + 1;
            }
        }

        value = best >= 0 ? Values[best] : "";
        return bestLength;
    }

    // First index in [lo, hi) whose name has a character at k that is >= c (shorter names sort before).
    private static int FirstAtLeast(int lo, int hi, int k, char c)
    {
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            var name = Names[mid];
            if (name.Length <= k || name[k] < c)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }
}
