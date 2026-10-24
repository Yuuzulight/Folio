namespace Folio.RenderTests;

public class WebGlGoldenTests
{
    [Theory]
    [InlineData("webgl/triangle")]
    [InlineData("webgl/indexed-mesh")]
    [InlineData("webgl/instanced-draw")]
    [InlineData("webgl/textured-quad")]
    [InlineData("webgl/render-to-texture")]
    [InlineData("webgl/mipmapped-minification")]
    public void WebGlSceneMatchesGolden(string test)
    {
        Assert.Null(GoldenSuite.Repo.Run(test));
    }
}
