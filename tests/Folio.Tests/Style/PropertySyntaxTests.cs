using Folio.Style;

namespace Folio.Tests.Style;

public class PropertySyntaxTests
{
    [Theory]
    [InlineData("*")]
    [InlineData("<length>")]
    [InlineData(" <length> | <percentage> ")]
    [InlineData("<color>#")]
    [InlineData("<number>+ | none")]
    [InlineData("<transform-list>")]
    [InlineData("small | medium | large")]
    public void ParsesValidSyntaxStrings(string text) => Assert.NotNull(PropertySyntax.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("<size>")]
    [InlineData("<length>++")]
    [InlineData("<transform-list>+")]
    [InlineData("inherit")]
    [InlineData("<length> |")]
    [InlineData("* | <length>")]
    [InlineData("1px")]
    public void RejectsInvalidSyntaxStrings(string text) => Assert.Null(PropertySyntax.Parse(text));

    [Theory]
    [InlineData("<length>", "2em", "32px")]
    [InlineData("<length>", "50%", null)]
    [InlineData("<length-percentage>", "50%", "50%")]
    [InlineData("<length-percentage>", "calc(10px + 50%)", "calc(50% + 10px)")]
    [InlineData("<percentage>", "25%", "25%")]
    [InlineData("<integer>", "3", "3")]
    [InlineData("<integer>", "3.5", null)]
    [InlineData("<number>+", "1 2", "1 2")]
    [InlineData("<number>#", "1, 2", "1, 2")]
    [InlineData("<number>", "1 2", null)]
    [InlineData("<resolution>", "192dpi", "2dppx")]
    [InlineData("<color>", "currentcolor", "currentcolor")]
    [InlineData("<custom-ident>", "brand", "brand")]
    [InlineData("<custom-ident>", "initial", null)]
    [InlineData("<transform-function>", "rotate(10deg)", "rotate(10deg)")]
    [InlineData("<transform-list>", "scale(2) translate(1px)", "scale(2) translate(1px)")]
    [InlineData("<url>", "url(a.png)", "url(a.png)")]
    [InlineData("<image>", "linear-gradient(red, blue)", "linear-gradient(red, blue)")]
    [InlineData("auto | <length>", "auto", "auto")]
    [InlineData("auto | <length>", "AUTO", null)]
    public void ComputesMatchingValues(string syntax, string value, string? expected) =>
        Assert.Equal(expected, PropertySyntax.Parse(syntax)!.Compute(value, new ComputeContext(ComputedStyle.Initial, 16, 800, 600)));

    [Fact]
    public void WithoutAnElementOnlyAbsoluteLengthsCompute()
    {
        var length = PropertySyntax.Parse("<length>")!;

        Assert.Equal("96px", length.Compute("1in", null));
        Assert.Null(length.Compute("1em", null));
        Assert.Null(length.Compute("10vw", null));
    }
}
