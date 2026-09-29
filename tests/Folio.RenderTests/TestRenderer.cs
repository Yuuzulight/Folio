namespace Folio.RenderTests;

public static class TestRenderer
{
    // Becomes a call to Folio.Skia's headless renderer once it exists.
    public static PixelBuffer Render(string htmlPath) =>
        throw new NotImplementedException($"The headless renderer does not exist yet: {htmlPath}");
}
