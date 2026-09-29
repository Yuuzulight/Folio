using System.Globalization;
using System.Text;
using static Folio.Typography.LineBreakClass;

namespace Folio.Typography;

internal enum BreakKind : byte
{
    None,
    Allowed,
    Mandatory,
}

/// <summary>
/// Line break opportunities per the Unicode line breaking algorithm (https://www.unicode.org/reports/tr14/), with
/// its default tailoring: rules LB1 to LB31 applied in order to each position.
/// </summary>
internal static class LineBreaker
{
    /// <summary>
    /// The break opportunity before each UTF-16 offset of <paramref name="text"/> (index = offset, length + 1
    /// entries): None inside a character and at 0, Mandatory after hard line breaks, and at the end.
    /// </summary>
    public static BreakKind[] Find(string text)
    {
        var chars = new List<Char>(text.Length);
        for (var i = 0; i < text.Length;)
        {
            Rune.DecodeFromUtf16(text.AsSpan(i), out var rune, out var length);
            chars.Add(new Char(i, rune.Value, Resolve(rune)));
            i += length;
        }

        // LB9 and LB10: combining marks and ZWJ take the class of the character they follow, unless it is a space
        // or a line break, in which case they are alphabetic.
        for (var i = 0; i < chars.Count; i++)
        {
            var c = chars[i];
            if (c.Class is CM or ZWJ)
            {
                var attaches = i > 0 && chars[i - 1].Class is not (BK or CR or LF or NL or SP or ZW);
                chars[i] = c with { Class = attaches ? chars[i - 1].Class : AL, Base = attaches ? chars[i - 1].Base : c.CodePoint, Attached = attaches, IsZwj = c.Class == ZWJ };
            }
        }

        var result = new BreakKind[text.Length + 1];
        for (var i = 1; i < chars.Count; i++)
            result[chars[i].Offset] = Before(chars, i);
        if (text.Length > 0)
            result[text.Length] = BreakKind.Mandatory; // LB3
        return result;
    }

    // A character with its resolved class. Base is the code point whose properties it stands for: its own, or for
    // an attached mark, the character it attaches to.
    private readonly record struct Char(int Offset, int CodePoint, LineBreakClass Class)
    {
        public int Base { get; init; } = CodePoint;
        public bool Attached { get; init; }
        public bool IsZwj { get; init; }
    }

    // LB1: resolve the classes the algorithm does not use directly.
    private static LineBreakClass Resolve(Rune rune) => UnicodeData.LineBreak(rune.Value) switch
    {
        AI or SG or XX => AL,
        SA => Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark ? CM : AL,
        CJ => NS,
        var c => c,
    };

    private static bool EastAsian(int codePoint) => UnicodeData.EastAsian(codePoint) is Typography.EastAsianWidth.F or Typography.EastAsianWidth.W or Typography.EastAsianWidth.H;

    private static bool Initial(int codePoint) => CharUnicodeInfo.GetUnicodeCategory(codePoint) == UnicodeCategory.InitialQuotePunctuation;

    private static bool Final(int codePoint) => CharUnicodeInfo.GetUnicodeCategory(codePoint) == UnicodeCategory.FinalQuotePunctuation;

    private const int DottedCircle = 0x25CC;

    // The break opportunity between chars[i - 1] and chars[i].
    private static BreakKind Before(List<Char> chars, int i)
    {
        var (prev, next) = (chars[i - 1], chars[i]);
        var (a, b) = (prev.Class, next.Class);

        // The characters (not marks attached to them) before and after the position, for look-behind and look-ahead.
        int Back(int k)
        {
            k--;
            while (k >= 0 && chars[k].Attached)
                k--;
            return k;
        }
        int Ahead(int k)
        {
            k++;
            while (k < chars.Count && chars[k].Attached)
                k++;
            return k;
        }
        LineBreakClass? ClassAt(int k) => k >= 0 && k < chars.Count ? chars[k].Class : null;
        // Skips back over spaces from the character before the position: the class before them, or null at the start.
        int BeforeSpaces()
        {
            var k = Back(i);
            while (k >= 0 && chars[k].Class == SP)
                k = Back(k);
            return k;
        }
        var prevIndex = Back(i);
        var afterNext = Ahead(i);

        if (a == BK)
            return BreakKind.Mandatory; // LB4
        if (a == CR && b == LF)
            return BreakKind.None; // LB5
        if (a is CR or LF or NL)
            return BreakKind.Mandatory;
        if (b is BK or CR or LF or NL or SP or ZW)
            return BreakKind.None; // LB6, LB7
        if (ClassAt(BeforeSpaces()) == ZW)
            return BreakKind.Allowed; // LB8
        if (prev.IsZwj)
            return BreakKind.None; // LB8a
        if (next.Attached)
            return BreakKind.None; // LB9

        if (a == WJ || b == WJ)
            return BreakKind.None; // LB11
        if (a == GL)
            return BreakKind.None; // LB12
        if (b == GL && a is not (SP or HY or HH))
            return BreakKind.None; // LB12a
        if (b is CL or CP or EX or SY)
            return BreakKind.None; // LB13
        var beforeSpaces = BeforeSpaces();
        if (ClassAt(beforeSpaces) == OP)
            return BreakKind.None; // LB14
        if (beforeSpaces >= 0 && chars[beforeSpaces].Class == QU && Initial(chars[beforeSpaces].Base)
            && ClassAt(Back(beforeSpaces)) is null or BK or CR or LF or NL or OP or QU or GL or SP or ZW)
            return BreakKind.None; // LB15a
        if (b == QU && Final(next.CodePoint)
            && ClassAt(afterNext) is null or SP or GL or WJ or CL or QU or CP or EX or IS or SY or BK or CR or LF or NL or ZW)
            return BreakKind.None; // LB15b
        if (a == SP && b == IS && ClassAt(afterNext) == NU)
            return BreakKind.Allowed; // LB15c
        if (b == IS)
            return BreakKind.None; // LB15d
        if (ClassAt(beforeSpaces) is CL or CP && b == NS)
            return BreakKind.None; // LB16
        if (ClassAt(beforeSpaces) == B2 && b == B2)
            return BreakKind.None; // LB17
        if (a == SP)
            return BreakKind.Allowed; // LB18

        if (b == QU && !Initial(next.CodePoint) || a == QU && !Final(prev.Base))
            return BreakKind.None; // LB19
        if (b == QU && !EastAsian(prev.Base)
            || b == QU && (afterNext >= chars.Count || !EastAsian(chars[afterNext].Base))
            || a == QU && !EastAsian(next.CodePoint)
            || a == QU && Back(prevIndex) is var q && (q < 0 || !EastAsian(chars[q].Base)))
            return BreakKind.None; // LB19a
        if (a == CB || b == CB)
            return BreakKind.Allowed; // LB20
        if (a is HY or HH && b is AL or HL && ClassAt(Back(prevIndex)) is null or BK or CR or LF or NL or SP or ZW or CB or GL)
            return BreakKind.None; // LB20a
        if (b is BA or HH or HY or NS || a == BB)
            return BreakKind.None; // LB21
        if (a is HY or HH && b != HL && ClassAt(Back(prevIndex)) == HL)
            return BreakKind.None; // LB21a
        if (a == SY && b == HL)
            return BreakKind.None; // LB21b
        if (b == IN)
            return BreakKind.None; // LB22
        if (a is AL or HL && b == NU || a == NU && b is AL or HL)
            return BreakKind.None; // LB23
        if (a == PR && b is ID or EB or EM || a is ID or EB or EM && b == PO)
            return BreakKind.None; // LB23a
        if (a is PR or PO && b is AL or HL || a is AL or HL && b is PR or PO)
            return BreakKind.None; // LB24

        // LB25: numbers with their prefixes, suffixes and separators.
        if (b is PO or PR)
        {
            var k = a is CL or CP ? Back(prevIndex) : prevIndex;
            while (k >= 0 && chars[k].Class is SY or IS)
                k = Back(k);
            if (ClassAt(k) == NU)
                return BreakKind.None;
        }
        if (a is PO or PR && (b == NU || b == OP && ClassAt(afterNext) == NU
                              || b == OP && ClassAt(afterNext) == IS && ClassAt(Ahead(afterNext)) == NU))
            return BreakKind.None;
        if (a is HY or IS && b == NU)
            return BreakKind.None;
        if (b == NU)
        {
            var k = prevIndex;
            while (k >= 0 && chars[k].Class is SY or IS)
                k = Back(k);
            if (ClassAt(k) == NU)
                return BreakKind.None;
        }

        if (a == JL && b is JL or JV or H2 or H3 || a is JV or H2 && b is JV or JT || a is JT or H3 && b == JT)
            return BreakKind.None; // LB26
        if (a is JL or JV or JT or H2 or H3 && b == PO || a == PR && b is JL or JV or JT or H2 or H3)
            return BreakKind.None; // LB27
        if (a is AL or HL && b is AL or HL)
            return BreakKind.None; // LB28

        // LB28a: orthographic syllables of Brahmic scripts.
        bool Aksara(Char c) => c.Class is AK or AS || c.Base == DottedCircle;
        if (a == AP && Aksara(next)
            || Aksara(prev) && b is VF or VI
            || a == VI && Back(prevIndex) is var v && v >= 0 && Aksara(chars[v]) && (b == AK || next.CodePoint == DottedCircle)
            || Aksara(prev) && Aksara(next) && ClassAt(afterNext) == VF)
            return BreakKind.None;

        if (a == IS && b is AL or HL)
            return BreakKind.None; // LB29
        if (a is AL or HL or NU && b == OP && !EastAsian(next.CodePoint) || a == CP && !EastAsian(prev.Base) && b is AL or HL or NU)
            return BreakKind.None; // LB30
        if (a == RI && b == RI)
        {
            // LB30a: regional indicators pair up from the start of their run.
            var count = 0;
            for (var k = prevIndex; k >= 0 && chars[k].Class == RI; k = Back(k))
                count++;
            if (count % 2 == 1)
                return BreakKind.None;
        }
        if (b == EM && (a == EB || (UnicodeData.Emoji(prev.Base) & EmojiProperties.ExtendedPictographic) != 0 && UnicodeData.Scripts(prev.Base) == Script.Unknown))
            return BreakKind.None; // LB30b: unassigned pictographs may be emoji bases
        return BreakKind.Allowed; // LB31
    }
}
