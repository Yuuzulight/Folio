namespace Folio.RenderTests;

public class Reftests
{
    [Theory]
    [InlineData("css-backgrounds/background-color-001")]
    [InlineData("css-images/linear-gradient-hard-stops-001")]
    [InlineData("css-backgrounds/box-shadow-001")]
    [InlineData("css-ui/outline-offset-001")]
    [InlineData("css-images/image-rendering-pixelated-001")]
    [InlineData("css-text-decor/text-decoration-line-001")]
    [InlineData("css-text-decor/text-decoration-propagation-001")]
    [InlineData("css-text/hyphens-manual-001")]
    [InlineData("css-text/text-align-justify-001")]
    [InlineData("css-transforms/translate-001")]
    [InlineData("css-transforms/rotate-90deg-001")]
    [InlineData("css-transforms/scale-2-001")]
    public void RendersLikeItsReference(string name)
    {
        var result = ReftestRunner.Run(name);

        Assert.True(result.Passed, $"{result.DifferingPixels} pixels differ; images in {RenderOutput.Root}");
    }
}
