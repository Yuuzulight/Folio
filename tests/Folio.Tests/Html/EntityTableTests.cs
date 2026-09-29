using Folio.Html;

namespace Folio.Tests.Html;

public class EntityTableTests
{
    [Fact]
    public void HasEveryNamedReferenceOfTheStandard()
    {
        Assert.Equal(2231, EntityTable.Count);
    }

    [Theory]
    [InlineData("amp;", 4, "&")]
    [InlineData("amp", 3, "&")]              // legacy name without semicolon
    [InlineData("notin;x", 6, "\u2209")]
    [InlineData("notit;", 3, "\u00AC")]      // longest match is the legacy "not"
    [InlineData("Afr;", 4, "\U0001D504")]    // outside the Basic Multilingual Plane
    [InlineData("NotEqualTilde;", 14, "\u2242\u0338")] // two code points
    [InlineData("CounterClockwiseContourIntegral;", 32, "\u2233")]
    public void MatchesTheLongestName(string input, int length, string value)
    {
        Assert.Equal(length, EntityTable.Match(input, out var actual));
        Assert.Equal(value, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("zz;")]
    [InlineData("AMp;")] // names are case-sensitive
    [InlineData(";")]
    public void MatchesNothing(string input)
    {
        Assert.Equal(0, EntityTable.Match(input, out var value));
        Assert.Equal("", value);
    }
}
