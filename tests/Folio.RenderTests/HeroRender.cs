using Folio.Scripting;
using Folio.Scripting.Dom;
using Folio.Scripting.Native;
using Folio.Skia;
using Folio.Typography;

namespace Folio.RenderTests;

/// <summary>
/// <c>hero &lt;page.html&gt; &lt;out.png&gt; [width] [--jint]</c>: loads a page with the native script engine and the pinned
/// library cache, lets its scripts settle, and writes the Skia picture. Used to regenerate docs/assets/folio-hero.png.
/// Default: the native production engine, where Chart.js goes to the native chart bridge and Mermaid blocks to Folio.Diagrams.
/// --jint: the compatibility engine, which runs the real pinned Chart.js (Mermaid does not run on it).
/// </summary>
public static class HeroRender
{
    public static int Run(string[] args)
    {
        var width = args.Length > 2 && int.TryParse(args[2], out var w) ? w : 1200;
        var fonts = Path.Combine(RepoPaths.Tests, "fonts");
        var diagrams = !args.Contains("--jint")
            ? ConformanceCache.Entries().Where(e => e.File == "mermaid.min.js").Select(e => e.Url).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>();
        var options = new FolioOptions
        {
            Fonts = new FontSettings
            {
                Source = new FontFolderSource(fonts),
                GenericFamilies = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sans-serif"] = ["Source Sans 3"], ["system-ui"] = ["Source Sans 3"],
                    ["serif"] = ["Source Serif 4"], ["monospace"] = ["Source Code Pro"],
                    ["Arial"] = ["Source Sans 3"], // the chart bridge draws its labels in Arial
                },
            },
            ResourceLoader = ConformanceCache.Loader,
            DiagramLibraryUrls = diagrams,
            BaseUri = new Uri("https://hero.invalid/"),
            Scripting = ScriptingPolicy.ContentProcess,
            Raster = new SkiaRasterBackend(),
        };
        using var document = Document.Parse(File.ReadAllText(args[0]), options);
        document.Paint(width, 800);
        using IScriptEngine engine = args.Contains("--jint") ? new Folio.Scripting.Jint.JintScriptEngine() : new NativeJavaScriptEngine();
        var clock = new VirtualEventLoopClock();
        using var loop = new HtmlEventLoop(engine, clock);
        DomBindings.Install(engine, document, eventLoop: loop);
        ScriptGlobalsInstaller.Install(loop, engine);
        using var runner = new ScriptRunner(document.Node, engine, loop, options);
        runner.Run();
        // Settle: drain jobs and timers (mermaid renders asynchronously) in steps of virtual time.
        for (var i = 0; i < 40; i++)
        {
            loop.PerformMicrotaskCheckpoint();
            loop.UpdateTheRendering();
            clock.Advance(100);
        }
        loop.PerformMicrotaskCheckpoint();
        loop.UpdateTheRendering();
        foreach (var d in runner.Diagnostics) Console.WriteLine($"diagnostic: {d.Code}: {d.Message}");
        using var result = HeadlessRenderer.Render(document, new RenderRequest(width, null));
        new PixelBuffer(result.Width, result.Height, result.Pixels.ToArray()).SavePng(args[1]);
        Console.WriteLine($"wrote {args[1]} {result.Width}x{result.Height}");
        return 0;
    }
}
