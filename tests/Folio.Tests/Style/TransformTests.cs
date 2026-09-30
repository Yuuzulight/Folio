using System.Numerics;
using Folio.Css;
using Folio.Style;

namespace Folio.Tests.Style;

public class TransformTests
{
    private static Vector2 Map(string declarations, Vector2 point, float width = 100, float height = 50)
    {
        var (source, block) = CssParser.ParseBlockContents(declarations);
        var cascaded = new Dictionary<PropertyId, CssValue>();
        foreach (var declaration in block.Declarations)
        {
            foreach (var (id, value) in Properties.Parse(source, declaration)!)
                cascaded[id] = value;
        }
        var style = StyleBuilder.Compute(cascaded, new ComputeContext(ComputedStyle.Initial, 16, 800, 600));
        var mapped = Vector2.Transform(point, style.Transform.Matrix2D(width, height));
        return new Vector2(MathF.Round(mapped.X, 3), MathF.Round(mapped.Y, 3));
    }

    [Fact]
    public void RotatesClockwiseOnScreen() =>
        Assert.Equal(new Vector2(0, 1), Map("transform-origin: 0 0; transform: rotate(90deg)", new Vector2(1, 0)));

    [Fact]
    public void TransformFunctionsApplyRightToLeft() =>
        Assert.Equal(new Vector2(12, 2), Map("transform-origin: 0 0; transform: translate(10px) scale(2)", new Vector2(1, 1)));

    [Fact]
    public void IndividualPropertiesComeBeforeTheTransformList()
    {
        // translate, then rotate, then scale, then transform: the point is transformed first and translated last.
        Assert.Equal(new Vector2(12, 12), Map("transform-origin: 0 0; translate: 10px 10px; transform: scale(2)", new Vector2(1, 1)));
        Assert.Equal(new Vector2(-1, 2), Map("transform-origin: 0 0; rotate: 90deg; scale: 2 1", new Vector2(1, 1))); // scaled to (2, 1), then rotated
    }

    [Fact]
    public void PercentagesResolveAgainstTheReferenceBox() =>
        Assert.Equal(new Vector2(100, 5), Map("translate: 50% 10%", Vector2.Zero, 200, 50));

    [Fact]
    public void TheOriginDefaultsToTheCentre() =>
        Assert.Equal(new Vector2(100, 50), Map("transform: rotate(180deg)", Vector2.Zero));

    [Fact]
    public void ThreeDimensionalFunctionsFlattenToTheirProjection()
    {
        Assert.Equal(new Vector2(-10, 5), Map("transform-origin: 0 0; transform: rotateY(180deg)", new Vector2(10, 5)));
        Assert.Equal(new Vector2(10, 5), Map("transform-origin: 0 0; transform: translateZ(50px)", new Vector2(10, 5)));
    }

    [Fact]
    public void MatrixAndSkewUseTheCssLayout()
    {
        Assert.Equal(new Vector2(1 + 3 + 5, 2 + 4 + 6), Map("transform-origin: 0 0; transform: matrix(1, 2, 3, 4, 5, 6)", new Vector2(1, 1)));
        Assert.Equal(new Vector2(11, 10), Map("transform-origin: 0 0; transform: skewX(45deg)", new Vector2(1, 10)));
    }

    [Fact]
    public void NoTransformIsTheIdentity()
    {
        Assert.False(ComputedStyle.Initial.Transform.IsTransformed);
        Assert.Equal(Matrix3x2.Identity, ComputedStyle.Initial.Transform.Matrix2D(100, 50));
    }
}
