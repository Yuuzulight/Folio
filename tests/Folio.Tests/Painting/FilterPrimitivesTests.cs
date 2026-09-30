using System.Numerics;
using Folio.Css;
using Folio.Painting;
using Folio.Style;

namespace Folio.Tests.Painting;

/// <summary>Filter functions as primitives, against the equivalents in Filter Effects 1 §13.1.</summary>
public class FilterPrimitivesTests
{
    private static List<Filter> Filters(string filter, string color = "black")
    {
        var (source, block) = CssParser.ParseBlockContents($"color: {color}; filter: {filter}");
        var cascaded = new Dictionary<PropertyId, CssValue>();
        foreach (var declaration in block.Declarations)
        {
            foreach (var (id, value) in Properties.Parse(source, declaration)!)
                cascaded[id] = value;
        }
        var style = StyleBuilder.Compute(cascaded, new ComputeContext(ComputedStyle.Initial, 16, 800, 600));
        return [.. FilterPrimitives.Of(style.Effects.Filter, style.Inherited.Color) ?? []];
    }

    private static float[] Matrix(string filter) => [.. Filters(filter).Single().Matrix!.Select(v => MathF.Round(v, 4))];

    [Fact]
    public void GrayscaleMixesTowardsLuminance()
    {
        Assert.Equal([0.2126f, 0.7152f, 0.0722f, 0, 0, 0.2126f, 0.7152f, 0.0722f, 0, 0, 0.2126f, 0.7152f, 0.0722f, 0, 0, 0, 0, 0, 1, 0], Matrix("grayscale(1)"));
        Assert.Equal([0.6063f, 0.3576f, 0.0361f, 0, 0, 0.1063f, 0.8576f, 0.0361f, 0, 0, 0.1063f, 0.3576f, 0.5361f, 0, 0, 0, 0, 0, 1, 0], Matrix("grayscale(50%)"));
    }

    [Fact]
    public void SepiaUsesTheSpecCoefficients() =>
        Assert.Equal([0.393f, 0.769f, 0.189f, 0, 0, 0.349f, 0.686f, 0.168f, 0, 0, 0.272f, 0.534f, 0.131f, 0, 0, 0, 0, 0, 1, 0], Matrix("sepia(1)"));

    [Fact]
    public void AmountsAboveOneAreClampedWhereTheSpecSaysSo()
    {
        Assert.Equal(Matrix("grayscale(1)"), Matrix("grayscale(2)"));
        Assert.Equal(Matrix("sepia(1)"), Matrix("sepia(300%)"));
        Assert.Equal(Matrix("invert(1)"), Matrix("invert(5)"));
        Assert.Equal(Matrix("opacity(1)"), Matrix("opacity(2)"));
        Assert.Equal(3, Matrix("brightness(3)")[0]);
        Assert.Equal(2.574f, Matrix("saturate(3)")[0]);
    }

    [Fact]
    public void SaturateAndHueRotateAreTheFeColorMatrixShorthands()
    {
        Assert.Equal([0.6065f, 0.3575f, 0.036f, 0, 0, 0.1065f, 0.8575f, 0.036f, 0, 0, 0.1065f, 0.3575f, 0.536f, 0, 0, 0, 0, 0, 1, 0], Matrix("saturate(0.5)"));
        // At 90 degrees the cosine terms drop out and the sine terms add to the luminance rows.
        Assert.Equal([0, 0, 1, 0, 0, 0.356f, 0.855f, -0.211f, 0, 0, -0.574f, 1.43f, 0.144f, 0, 0, 0, 0, 0, 1, 0], Matrix("hue-rotate(90deg)"));
        Assert.Equal(Matrix("hue-rotate(0deg)"), Matrix("saturate(1)"));
    }

    [Fact]
    public void ComponentTransfersAreAffineMatrices()
    {
        // invert: a table of [amount, 1 - amount].
        Assert.Equal([0.5f, 0, 0, 0, 0.25f, 0, 0.5f, 0, 0, 0.25f, 0, 0, 0.5f, 0, 0.25f, 0, 0, 0, 1, 0], Matrix("invert(25%)"));
        // opacity: an alpha table of [0, amount].
        Assert.Equal([1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0.5f, 0], Matrix("opacity(0.5)"));
        // brightness: a slope; contrast: a slope about the middle.
        Assert.Equal([2, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 1, 0], Matrix("brightness(2)"));
        Assert.Equal([0.5f, 0, 0, 0, 0.25f, 0, 0.5f, 0, 0, 0.25f, 0, 0, 0.5f, 0, 0.25f, 0, 0, 0, 1, 0], Matrix("contrast(50%)"));
    }

    [Fact]
    public void BlurAndDropShadowKeepTheirStandardDeviation()
    {
        var filters = Filters("blur(2em) drop-shadow(3px 4px 5px) drop-shadow(1px 1px rgb(255 0 0 / 0.5))", "rgb(0, 0, 255)");

        Assert.Equal(new Filter(FilterKind.Blur, 32), filters[0]);
        Assert.Equal(new Filter(FilterKind.DropShadow, 5, Offset: new Vector2(3, 4), Color: new Rgba(0, 0, 1, 1)), filters[1]);
        Assert.Equal(new Filter(FilterKind.DropShadow, 0, Offset: new Vector2(1, 1), Color: new Rgba(1, 0, 0, 0.5f)), filters[2]);
    }
}
