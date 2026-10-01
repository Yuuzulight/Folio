using Folio.Css;
using Folio.Style;

namespace Folio.Tests.Style;

public class InterpolationTests
{
    [Fact]
    public void NumbersLengthsAndIntegersInterpolate()
    {
        Assert.Equal(2.5f, Interpolation.Lerp(0f, 10f, 0.25));
        Assert.Equal(new LengthPercentage(5, 25), Interpolation.Lerp(new LengthPercentage(0, 50), new LengthPercentage(10, 0), 0.5));
        Assert.Equal(3, Interpolation.Lerp(1, 4, 0.6)); // integers round
        Assert.Equal(SizeValue.Of(new LengthPercentage(15)), Interpolation.Lerp(SizeValue.Of(new LengthPercentage(10)), SizeValue.Of(new LengthPercentage(20)), 0.5));
    }

    [Fact]
    public void KeywordsAndCalcAreDiscrete()
    {
        Assert.Null(Interpolation.Lerp(SizeValue.Auto, SizeValue.Of(new LengthPercentage(20)), 0.5));
        Assert.Null(Interpolation.Lerp(TextAlign.Left, TextAlign.Right, 0.5));
        Assert.Null(Interpolation.Lerp(new LengthPercentage(0, 0, new CalcNumber(1)), new LengthPercentage(10), 0.5));
    }

    [Fact]
    public void ColoursInterpolatePremultiplied()
    {
        // From transparent black to opaque red, half way is half-transparent red, not a dark red.
        var half = (CssColor)Interpolation.Lerp(CssColor.Transparent, new CssColor(1, 0, 0, 1), 0.5)!;
        Assert.Equal(new CssColor(1, 0, 0, 0.5f), half);
        Assert.Null(Interpolation.Lerp(CssColor.CurrentColor, CssColor.Black, 0.5));
    }

    [Fact]
    public void ShadowListsPadWithTransparentShadows()
    {
        var shadow = new Shadow(0, 8, 20, 0, new CssColor(0, 0, 0, 0.4f), false);

        var half = (List<Shadow>)Interpolation.Lerp((IReadOnlyList<Shadow>)[], (IReadOnlyList<Shadow>)[shadow], 0.5)!;

        Assert.Equal("0px 4px 10px 0px rgba(0, 0, 0, 0.2)", Assert.Single(half).ToString());
        Assert.Null(Interpolation.Lerp((IReadOnlyList<Shadow>)[shadow with { Inset = true }], (IReadOnlyList<Shadow>)[shadow], 0.5));
    }

    [Fact]
    public void TransformListsPairUpOrAreDiscrete()
    {
        var rotate = new TransformList([new RotateOp(0, 0, 1, 90)]);
        Assert.Equal("rotate(45deg)", Interpolation.Lerp(TransformList.None, rotate, 0.5)!.ToString());
        Assert.Equal("translate(5px, 0px) rotate(45deg)", Interpolation.Lerp(
            new TransformList([new TranslateOp(new LengthPercentage(10), LengthPercentage.Zero, 0)]),
            new TransformList([new TranslateOp(LengthPercentage.Zero, LengthPercentage.Zero, 0), new RotateOp(0, 0, 1, 90)]), 0.5)!.ToString());
        // A rotation and a scale do not pair up.
        Assert.Null(Interpolation.Lerp(rotate, new TransformList([new ScaleOp(2, 2, 1)]), 0.5));
    }
}
