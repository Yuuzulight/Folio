using System.Text;

namespace Folio.Typography;

/// <summary>
/// Word boundaries (https://www.unicode.org/reports/tr29/#Word_Boundaries), for double-click selection and
/// <c>text-transform: capitalize</c>.
/// </summary>
internal static class WordBoundaries
{
    /// <summary>The UTF-16 offsets of the word boundaries in <paramref name="text"/>, including 0 and its length.</summary>
    public static List<int> Find(string text)
    {
        var runes = new List<(int Offset, int CodePoint, WordBreak Break)>();
        for (var i = 0; i < text.Length;)
        {
            Rune.DecodeFromUtf16(text.AsSpan(i), out var rune, out var length);
            runes.Add((i, rune.Value, UnicodeData.Word(rune.Value)));
            i += length;
        }

        var boundaries = new List<int> { 0 };
        for (var i = 1; i < runes.Count; i++)
        {
            if (IsBoundary(runes, i))
                boundaries.Add(runes[i].Offset);
        }
        if (text.Length > 0)
            boundaries.Add(text.Length);
        return boundaries;
    }

    private static bool Ignorable(WordBreak b) => b is WordBreak.Extend or WordBreak.Format or WordBreak.ZWJ;

    private static bool AHLetter(WordBreak b) => b is WordBreak.ALetter or WordBreak.Hebrew_Letter;

    private static bool MidNumLetQ(WordBreak b) => b is WordBreak.MidNumLet or WordBreak.Single_Quote;

    private static bool Newline(WordBreak b) => b is WordBreak.Newline or WordBreak.CR or WordBreak.LF;

    // Whether there is a boundary before runes[i], by rules WB3 to WB999 in order.
    private static bool IsBoundary(List<(int Offset, int CodePoint, WordBreak Break)> runes, int i)
    {
        var (before, after) = (runes[i - 1].Break, runes[i].Break);
        if (before == WordBreak.CR && after == WordBreak.LF)
            return false; // WB3
        if (Newline(before) || Newline(after))
            return true; // WB3a, WB3b
        if (before == WordBreak.ZWJ && (UnicodeData.Emoji(runes[i].CodePoint) & EmojiProperties.ExtendedPictographic) != 0)
            return false; // WB3c
        if (before == WordBreak.WSegSpace && after == WordBreak.WSegSpace)
            return false; // WB3d
        if (Ignorable(after))
            return false; // WB4: extenders attach to what precedes them

        // WB4: the rules below see through extenders, except those that follow a line break or start the text.
        var left = Previous(runes, i);
        var l1 = left >= 0 && !Newline(runes[left].Break) ? runes[left].Break : before;
        var l2 = left >= 0 && Previous(runes, left) is var l && l >= 0 ? runes[l].Break : (WordBreak?)null;
        var r2 = Next(runes, i) is var r && r < runes.Count ? runes[r].Break : (WordBreak?)null;

        if (AHLetter(l1) && AHLetter(after))
            return false; // WB5
        if (AHLetter(l1) && (after == WordBreak.MidLetter || MidNumLetQ(after)) && r2 is { } a && AHLetter(a))
            return false; // WB6
        if (l2 is { } b && AHLetter(b) && (l1 == WordBreak.MidLetter || MidNumLetQ(l1)) && AHLetter(after))
            return false; // WB7
        if (l1 == WordBreak.Hebrew_Letter && after == WordBreak.Single_Quote)
            return false; // WB7a
        if (l1 == WordBreak.Hebrew_Letter && after == WordBreak.Double_Quote && r2 == WordBreak.Hebrew_Letter)
            return false; // WB7b
        if (l2 == WordBreak.Hebrew_Letter && l1 == WordBreak.Double_Quote && after == WordBreak.Hebrew_Letter)
            return false; // WB7c
        if (l1 == WordBreak.Numeric && after == WordBreak.Numeric)
            return false; // WB8
        if (AHLetter(l1) && after == WordBreak.Numeric)
            return false; // WB9
        if (l1 == WordBreak.Numeric && AHLetter(after))
            return false; // WB10
        if (l2 == WordBreak.Numeric && (l1 == WordBreak.MidNum || MidNumLetQ(l1)) && after == WordBreak.Numeric)
            return false; // WB11
        if (l1 == WordBreak.Numeric && (after == WordBreak.MidNum || MidNumLetQ(after)) && r2 == WordBreak.Numeric)
            return false; // WB12
        if (l1 == WordBreak.Katakana && after == WordBreak.Katakana)
            return false; // WB13
        if ((AHLetter(l1) || l1 is WordBreak.Numeric or WordBreak.Katakana or WordBreak.ExtendNumLet) && after == WordBreak.ExtendNumLet)
            return false; // WB13a
        if (l1 == WordBreak.ExtendNumLet && (AHLetter(after) || after is WordBreak.Numeric or WordBreak.Katakana))
            return false; // WB13b
        if (l1 == WordBreak.Regional_Indicator && after == WordBreak.Regional_Indicator)
        {
            // WB15, WB16: regional indicators pair up from the start of their run.
            var count = 0;
            for (var j = left; j >= 0 && runes[j].Break == WordBreak.Regional_Indicator; j = Previous(runes, j))
                count++;
            return count % 2 == 0;
        }
        return true; // WB999
    }

    // The nearest rune before i that is not an extender, or -1.
    private static int Previous(List<(int Offset, int CodePoint, WordBreak Break)> runes, int i)
    {
        var j = i - 1;
        while (j >= 0 && Ignorable(runes[j].Break))
            j--;
        return j;
    }

    // The nearest rune after i that is not an extender, or runes.Count.
    private static int Next(List<(int Offset, int CodePoint, WordBreak Break)> runes, int i)
    {
        var j = i + 1;
        while (j < runes.Count && Ignorable(runes[j].Break))
            j++;
        return j;
    }
}
