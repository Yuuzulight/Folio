using Xunit.MicrosoftTestingPlatform;
using Xunit.Runner.InProc.SystemConsole;
using Folio.Scripting;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;
using Folio.Skia;
using Folio.Painting;
using Folio.Typography;
using Folio.Scripting.Ipc;
using SkiaSharp;

namespace Folio.RenderTests;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "approve")
            return Approve(args[1..]);
        if (args.Length > 0 && args[0] == "conformance")
            return ConformanceReport(args[1..]);
        if (args.Length > 0 && args[0] == "canvas-perf")
            return CanvasPerformance(args.Length > 1 && int.TryParse(args[1], out var n) ? Math.Max(3, n) : 10);
        if (args.Length > 0 && args[0] == "canvas-frames")
            return CanvasFrames(args.Length > 1 && int.TryParse(args[1], out var frames) ? Math.Max(3, frames) : 30);
        if (args.Length > 0 && args[0] == "canvas-shared-memory")
            return CanvasSharedMemory(args.Length > 1 && int.TryParse(args[1], out var frames) ? Math.Max(3, frames) : 10);

        // The rest mirrors the entry point xUnit generates.
        if (args.Any(arg => arg is "--server" or "--internal-msbuild-node"))
            return TestPlatformTestFramework.RunAsync(args, SelfRegisteredExtensions.AddSelfRegisteredExtensions).GetAwaiter().GetResult();
        return ConsoleRunner.Run(args).GetAwaiter().GetResult();
    }

    private static int CanvasPerformance(int iterations)
    {
        var artifacts = Conformance.Discover().Where(a => a.Category is "D5" or "D7").ToArray();
        Console.WriteLine($"Canvas conformance wall-time benchmark ({iterations} iterations per artifact)");
        Console.WriteLine("| Artifact | Median ms | p95 ms |");
        Console.WriteLine("|---|--:|--:|");
        foreach (var artifact in artifacts)
        {
            var samples = new List<double>(iterations);
            for (var i = 0; i < iterations; i++)
            {
                var start = System.Diagnostics.Stopwatch.GetTimestamp();
                _ = Conformance.Run(artifact);
                samples.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            samples.Sort();
            var median = samples[samples.Count / 2];
            var p95 = samples[Math.Min(samples.Count - 1, (int)Math.Ceiling(samples.Count * .95) - 1)];
            Console.WriteLine($"| {artifact.Id} | {median:F1} | {p95:F1} |");
        }
        return 0;
    }

    private static int CanvasFrames(int frameCount)
    {
        Console.WriteLine($"Script-driven canvas frame benchmark ({frameCount} frames, 16.7 ms virtual ticks)");
        Console.WriteLine("| Artifact | Median ms | p95 ms | Target |");
        Console.WriteLine("|---|--:|--:|---:|");
        foreach (var artifact in Conformance.Discover().Where(a => a.Category is "D5" or "D7"))
        {
            var html = File.ReadAllText(Path.Combine(artifact.Folder, "index.html"));
            using var document = Document.Parse(html, Conformance.Options);
            document.Paint(Conformance.ManifestOf(artifact).Viewport, 600);
            using var engine = new JintScriptEngine();
            var clock = new VirtualEventLoopClock();
            using var loop = new HtmlEventLoop(engine, clock);
            DomBindings.Install(engine, document, eventLoop: loop);
            ScriptGlobalsInstaller.Install(loop, engine);
            using var runner = new ScriptRunner(document.Node, engine, loop, Conformance.Options);
            runner.Run();
            loop.UpdateTheRendering();
            using var bitmap = new SKBitmap(new SKImageInfo(artifact.Category == "D5" ? 800 : 800, 600, SKColorType.Bgra8888, SKAlphaType.Premul));
            using var canvas = new SKCanvas(bitmap);
            DisplayList BuildFrame()
            {
                return document.Paint(Conformance.ManifestOf(artifact).Viewport, 600, 1, new HarfBuzzShaper()).List;
            }
            void Replay(DisplayList displayList)
            {
                canvas.Clear(SKColors.White);
                DisplayListPlayer.Replay(displayList, new SkiaCanvas(canvas), new RectF(0, 0, 800, 600));
                canvas.Flush();
            }
            for (var warmup = 0; warmup < 3; warmup++)
            {
                clock.Advance(16.7);
                loop.UpdateTheRendering();
                Replay(BuildFrame());
            }
            var samples = new List<double>(frameCount);
            for (var i = 0; i < frameCount; i++)
            {
                clock.Advance(16.7);
                loop.UpdateTheRendering();
                var displayList = BuildFrame();
                var start = System.Diagnostics.Stopwatch.GetTimestamp();
                Replay(displayList);
                samples.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            samples.Sort();
            var median = samples[samples.Count / 2];
            var p95 = samples[Math.Min(samples.Count - 1, (int)Math.Ceiling(samples.Count * .95) - 1)];
            Console.WriteLine($"| {artifact.Id} | {median:F2} | {p95:F2} | 16.70 ms |");
        }
        return 0;
    }

    private static int CanvasSharedMemory(int frameCount)
    {
        const int width = 1920, height = 1080, stride = width * 4;
        var path = Path.Combine(Path.GetTempPath(), $"folio-canvas-bench-{Guid.NewGuid():N}.frame");
        var pixels = new byte[stride * height];
        var samples = new List<double>(frameCount);
        try
        {
            using var store = new CanvasFrameStore(path, width, height, stride);
            for (var i = 0; i < frameCount; i++)
            {
                pixels[(i * 4099) % pixels.Length] = (byte)i;
                var start = System.Diagnostics.Stopwatch.GetTimestamp();
                store.Publish(pixels);
                _ = store.ReadLatest(out var sequence);
                if (sequence != i + 1) throw new InvalidDataException("Canvas frame sequence did not advance.");
                samples.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            samples.Sort();
            var p95 = samples[Math.Min(samples.Count - 1, (int)Math.Ceiling(samples.Count * .95) - 1)];
            Console.WriteLine($"Shared-memory canvas transfer benchmark ({width}x{height}, {frameCount} changing frames)");
            Console.WriteLine("| Median ms | p95 ms | Bytes/frame |");
            Console.WriteLine($"| {samples[samples.Count / 2]:F2} | {p95:F2} | {pixels.Length} |");
            return 0;
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    // folio-test conformance [category|category/name]...: renders the corpus and prints pass rates per category (also to
    // the GitHub Actions job summary). Fails on runtime/interaction failures and stale expected-failure marks.
    private static int ConformanceReport(string[] filters)
    {
        var results = Conformance.Discover()
            .Where(a => filters.Length == 0 || filters.Any(f => a.Id == f || a.Id.StartsWith(f.TrimEnd('/') + "/", StringComparison.Ordinal)))
            .Select(a => Conformance.Run(a))
            .ToList();
        var report = Conformance.Report(results);
        Console.WriteLine(report);
        if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary)
            File.AppendAllText(summary, "### Conformance\n\n" + report);
        return results.Any(r => r.Broke) ? 1 : 0;
    }

    // folio-test approve <area|area/name>: copies the last actual images over the goldens.
    private static int Approve(string[] targets)
    {
        if (targets.Length == 0)
        {
            Console.Error.WriteLine("usage: approve <area|area/name>...");
            return 2;
        }

        var approved = targets.SelectMany(target => GoldenSuite.Repo.Approve(target,
            target.Replace('\\', '/').Trim('/') is "feature-fallback" || target.Replace('\\', '/').StartsWith("feature-fallback/", StringComparison.Ordinal)
                ? FeatureFallbackRenderTests.GoldenNames : null)).ToList();
        foreach (var test in approved)
            Console.WriteLine($"approved {test}");
        if (approved.Count == 0)
            Console.Error.WriteLine($"No actual images for {string.Join(", ", targets)} in {GoldenSuite.Repo.Output}; run the golden tests first.");
        return approved.Count == 0 ? 1 : 0;
    }
}
