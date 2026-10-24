using Folio.Scripting;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;
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
        Capabilities = new FeatureCapabilities { WebGl = true },
        Scripting = ScriptingPolicy.ContentProcess,
        Raster = new SkiaRasterBackend(),
    };

    /// <summary>Renders a test page in an 800x600 viewport.</summary>
    public static PixelBuffer Render(string htmlPath)
    {
        var html = File.ReadAllText(htmlPath);
        using var document = Document.Parse(html, Options);
        JintScriptEngine? engine = null;
        HtmlEventLoop? loop = null;
        ScriptRunner? runner = null;
        try
        {
            if (html.Contains("<script", StringComparison.OrdinalIgnoreCase))
            {
                engine = new JintScriptEngine();
                loop = new HtmlEventLoop(engine);
                DomBindings.Install(engine, document, eventLoop: loop);
                ScriptGlobalsInstaller.Install(loop, engine);
                runner = new ScriptRunner(document.Node, engine, loop, Options);
                runner.Run();
                loop.PerformMicrotaskCheckpoint();
                loop.UpdateTheRendering();
            }
            using var result = HeadlessRenderer.Render(document, new RenderRequest(800, 600));
            return new PixelBuffer(result.Width, result.Height, result.Pixels.ToArray());
        }
        finally
        {
            runner?.Dispose();
            loop?.Dispose();
            engine?.Dispose();
        }
    }
}
