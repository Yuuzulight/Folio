namespace Folio.RenderTests;

public class Reftests
{
    [Theory]
    [InlineData("css-backgrounds/background-color-001")]
    [InlineData("css-text-decor/text-decoration-line-001")]
    [InlineData("css-text-decor/text-decoration-propagation-001")]
    [InlineData("css-text/hyphens-manual-001")]
    [InlineData("css-text/text-align-justify-001")]
    public void RendersLikeItsReference(string name)
    {
        var result = ReftestRunner.Run(name);

        Assert.True(result.Passed, $"{result.DifferingPixels} pixels differ; images in {RenderOutput.Root}");
    }
}
