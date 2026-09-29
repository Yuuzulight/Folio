using Folio.Typography;

namespace Folio.Tests.Typography;

public class UnicodeDataTests
{
    [Fact]
    public void LooksUpProperties()
    {
        Assert.Equal(LineBreakClass.AL, UnicodeData.LineBreak('a'));
        Assert.Equal(LineBreakClass.SP, UnicodeData.LineBreak(' '));
        Assert.Equal(LineBreakClass.ID, UnicodeData.LineBreak(0x4E00));
        Assert.Equal(LineBreakClass.ID, UnicodeData.LineBreak(0x3FFFD)); // unassigned, listed as ID
        Assert.Equal(LineBreakClass.XX, UnicodeData.LineBreak(0x10FFFF));
        Assert.Equal(EastAsianWidth.W, UnicodeData.EastAsian(0x4E00));
        Assert.Equal(BidiClass.R, UnicodeData.Bidi(0x05D0));
        Assert.Equal(BidiClass.AL, UnicodeData.Bidi(0x0627));
        Assert.Equal(BidiClass.AL, UnicodeData.Bidi(0x0750 + 0x6F)); // Arabic Supplement, via the @missing defaults
        Assert.Equal(BidiClass.ET, UnicodeData.Bidi(0x20CF)); // unassigned currency symbol
        Assert.Equal(WordBreak.ALetter, UnicodeData.Word('a'));
        Assert.Equal(Script.Greek, UnicodeData.Scripts(0x03B1));
        Assert.Equal(Script.Unknown, UnicodeData.Scripts(0xE000));
        Assert.True(UnicodeData.Emoji(0x1F600).HasFlag(EmojiProperties.EmojiPresentation));
        Assert.True(UnicodeData.Emoji(0x1F3FB).HasFlag(EmojiProperties.EmojiModifier));
        Assert.Equal(EmojiProperties.None, UnicodeData.Emoji('a'));
        Assert.Equal(LineBreakClass.XX, UnicodeData.LineBreak(-1));
        Assert.Equal(LineBreakClass.XX, UnicodeData.LineBreak(0x110000));
    }

    [Fact]
    public void LooksUpBracketsAndMirrors()
    {
        Assert.Equal((')', true), UnicodeData.Bracket('(') is { } b ? ((char)b.Pair, b.Opens) : default);
        Assert.Equal(('(', false), UnicodeData.Bracket(')') is { } c ? ((char)c.Pair, c.Opens) : default);
        Assert.Null(UnicodeData.Bracket('a'));
        Assert.Equal('>', UnicodeData.Mirror('<'));
        Assert.Null(UnicodeData.Mirror('a'));
    }

    [Fact]
    public void WordBoundariesPassTheConformanceTest() =>
        UnicodeConformance.CheckBreaks("auxiliary/WordBreakTest.txt", WordBoundaries.Find);
}
