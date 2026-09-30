using Folio.Skia;
using Folio.Typography;

namespace Folio.RenderTests;

public static class TestRenderer
{
    /// <summary>Test pages use the bundled box font for every family, so images never depend on system fonts.</summary>
    public static FolioOptions Options { get; } = new()
    {
        Fonts = new FontSettings
        {
            Source = new FontFolderSource(Path.Combine(RepoPaths.Tests, "fonts")),
            GenericFamilies = new[] { "serif", "sans-serif", "monospace", "system-ui", "cursive", "fantasy", "emoji" }
                .ToDictionary(g => g, _ => (IReadOnlyList<string>)["Folio Box"], StringComparer.OrdinalIgnoreCase),
        },
    };

    /// <summary>Renders a test page in an 800x600 viewport.</summary>
    public static PixelBuffer Render(string htmlPath)
    {
        using var document = Document.Parse(File.ReadAllText(htmlPath), Options);
        using var result = HeadlessRenderer.Render(document, new RenderRequest(800, 600));
        return new PixelBuffer(result.Width, result.Height, result.Pixels.ToArray());
    }
}
