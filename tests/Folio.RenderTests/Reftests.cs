namespace Folio.RenderTests;

public class Reftests
{
    [Theory(Skip = "The headless renderer does not exist yet.")]
    [InlineData("css-backgrounds/background-color-001")]
    public void RendersLikeItsReference(string name)
    {
        var result = ReftestRunner.Run(name);

        Assert.True(result.Passed, $"{result.DifferingPixels} pixels differ; images in {RenderOutput.Root}");
    }
}
