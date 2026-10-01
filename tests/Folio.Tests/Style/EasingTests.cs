using Folio.Css;
using Folio.Style;

namespace Folio.Tests.Style;

public class EasingTests
{
    private static Easing Parse(string text)
    {
        var (source, values) = CssParser.ParseComponentValues(text);
        var reader = new ValueReader(source, values);
        var easing = AnimationProperties.EasingFunction(reader);
        Assert.True(easing is not null && reader.AtEnd, text);
        return easing!;
    }

    [Theory]
    [InlineData("ease", 0.5, 0.8024)]
    [InlineData("ease-in-out", 0.5, 0.5)]
    [InlineData("cubic-bezier(0, 0, 1, 1)", 0.3, 0.3)]
    [InlineData("ease-in", 0, 0)]
    [InlineData("ease-in", 1, 1)]
    public void CubicBeziersFollowTheCurve(string text, double input, double output) =>
        Assert.Equal(output, Parse(text).Apply(input), 3);

    [Fact]
    public void CubicBeziersExtendAlongTheirEndTangents()
    {
        // ease starts with slope 0.1 / 0.25 and ends flat.
        Assert.Equal(-0.2, Parse("ease").Apply(-0.5), 6);
        Assert.Equal(1, Parse("ease").Apply(1.5), 6);
    }

    [Theory]
    [InlineData("steps(4)", 0.24, 0)]
    [InlineData("steps(4)", 0.25, 0.25)]
    [InlineData("steps(4)", 1, 1)]
    [InlineData("steps(4, jump-start)", 0, 0.25)]
    [InlineData("step-start", 0.5, 1)]
    [InlineData("steps(2, jump-none)", 0.5, 1)]
    [InlineData("steps(2, jump-none)", 0.49, 0)]
    [InlineData("steps(3, jump-both)", 0, 0.25)]
    [InlineData("steps(3, jump-both)", 1, 1)]
    public void StepsJumpAtTheirPositions(string text, double input, double output) =>
        Assert.Equal(output, Parse(text).Apply(input), 6);

    [Fact]
    public void TheBeforeFlagHoldsTheStepBeforeAJump()
    {
        Assert.Equal(1, Parse("step-start").Apply(0));
        Assert.Equal(0, Parse("step-start").Apply(0, before: true));
    }

    [Theory]
    [InlineData(0.1, 0.125)]
    [InlineData(0.3, 0.25)]
    [InlineData(0.7, 0.625)]
    [InlineData(1.2, 1.25)]
    public void LinearInterpolatesBetweenItsPoints(double input, double output) =>
        Assert.Equal(output, Parse("linear(0, 0.25 20% 40%, 1)").Apply(input), 6);

    [Fact]
    public void LinearSpreadsMissingInputsAndKeepsThemInOrder()
    {
        // 0.5 has no input: it lands halfway between 0% and 100%. An input below an earlier one moves up to it.
        Assert.Equal(0.5, Parse("linear(0, 0.5, 1)").Apply(0.5), 6);
        Assert.Equal("linear(0 0%, 1 60%, 0.5 60%, 1 100%)", Parse("linear(0, 1 60%, 0.5 30%, 1)").ToString());
        Assert.Equal(0.42, Parse("linear").Apply(0.42), 6);
    }
}
