namespace Folio.RenderTests;

public class WebGlGoldenTests
{
    [Theory]
    [InlineData("webgl/triangle")]
    [InlineData("webgl/indexed-mesh")]
    [InlineData("webgl/instanced-draw")]
    public void WebGlSceneMatchesGolden(string test)
    {
        Assert.Null(GoldenSuite.Repo.Run(test));
    }
}
