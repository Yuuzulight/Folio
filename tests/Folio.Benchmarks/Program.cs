using System.Globalization;
using System.Text;
using Xunit.MicrosoftTestingPlatform;
using Xunit.Runner.InProc.SystemConsole;

namespace Folio.Benchmarks;

/// <summary>
/// <c>bench [iterations]</c> measures every benchmark document and prints a table (also to the GitHub Actions job
/// summary when run there). Documents over a target get a warning, never a failure: timings on shared machines are
/// noisy. <c>gc [runs]</c> shows what layout leaves to the garbage collector on the large generated documents (#397).
/// Anything else runs the harness's own tests.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args is ["bench", .. var rest])
            return Bench(rest is [var n] ? Math.Max(1, int.Parse(n, CultureInfo.InvariantCulture)) : 20);
        if (args is ["cold"])
            return Cold();
        if (args is ["gc", .. var gcRest])
            return Gc(gcRest is [var n] ? Math.Max(3, int.Parse(n, CultureInfo.InvariantCulture)) : 7);

        // The rest mirrors the entry point xUnit generates.
        if (args.Any(arg => arg is "--server" or "--internal-msbuild-node"))
            return TestPlatformTestFramework.RunAsync(args, SelfRegisteredExtensions.AddSelfRegisteredExtensions).GetAwaiter().GetResult();
        return ConsoleRunner.Run(args).GetAwaiter().GetResult();
    }

    private static int Bench(int iterations)
    {
        var table = new StringBuilder()
            .AppendLine($"Parse to display list, median of {iterations} runs; typical-artifact targets: {Benchmark.TimeTarget.TotalMilliseconds} ms and {Mb(Benchmark.MemoryTarget)} MB for the whole first paint.")
            .AppendLine()
            .AppendLine($"| Document | KB | {string.Concat(Measurement.StageNames.Select(n => n + " ms | "))}Total ms | Allocated MB | Style allocated MB | Retained MB |")
            .AppendLine($"|---|--:|{string.Concat(Measurement.StageNames.Select(_ => "--:|"))}--:|--:|--:|--:|");
        var warnings = new List<string>();
        foreach (var (name, html) in Benchmark.Documents())
        {
            var m = Benchmark.Measure(name, html, iterations);
            table.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {m.Name} | {m.Length / 1024} | {string.Concat(m.Stages.Select(t => Ms(t) + " | "))}{Ms(m.Total)} | {Mb(m.Allocated)} | {Mb(m.StyleAllocated)} | {Mb(m.Retained)} |"));
            if (m.Total > Benchmark.TimeTarget || m.Retained > Benchmark.MemoryTarget)
                warnings.Add($"{m.Name} is over a typical-artifact target: {Ms(m.Total)} ms, {Mb(m.Retained)} MB retained");
        }

        table.AppendLine().AppendLine($"Paint-only animation frames, {iterations * 10} frames each: restyle + display list + raster at 800x600; target {Ms(FrameBenchmark.Target)} ms for the typical dashboard.")
            .AppendLine().AppendLine("| Document | Median ms | p95 ms | Paint-only |").AppendLine("|---|--:|--:|---|");
        foreach (var (name, html, held) in new[] { ("typical dashboard", FrameBenchmark.Typical, true), ("busy dashboard", FrameBenchmark.Busy, false) })
        {
            var frames = FrameBenchmark.Measure(html, iterations * 10);
            table.AppendLine($"| {name} | {Ms(frames.Median)} | {Ms(frames.P95)} | {(frames.PaintOnly ? "yes" : "no")} |");
            if (held && (frames.Median > FrameBenchmark.Target || !frames.PaintOnly))
                warnings.Add($"Paint-only animation frames of the {name} are over the target or not paint-only: median {Ms(frames.Median)} ms");
        }

        Console.WriteLine(table);
        foreach (var warning in warnings)
            Console.WriteLine($"::warning::{warning}"); // A GitHub Actions annotation; plain text elsewhere.
        if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary)
            File.AppendAllText(summary, "### Benchmarks\n\n" + table);
        return 0;
    }

    // gc: the layout stage alone, with the collections it causes and what its fragment tree keeps alive; medians of the
    // runs after two warm-up runs, each run with the previous one's documents gone.
    private static int Gc(int runs)
    {
        var table = new StringBuilder()
            .AppendLine($"Layout stage, median of {runs - 2} runs after 2 warm-up runs.")
            .AppendLine()
            .AppendLine("| Document | Layout ms | GC pause ms | gen0 | gen1 | gen2 | Allocated MB | Retained MB |")
            .AppendLine("|---|--:|--:|--:|--:|--:|--:|--:|");
        foreach (var (name, html) in Benchmark.Documents().Where(d => d.Name is "generated/table-5000" or "generated/nested-div-20000"))
        {
            var m = Benchmark.MeasureLayoutGc(html, runs);
            table.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {name} | {Ms(m.Layout)} | {Ms(m.Pause)} | {m.Gen0} | {m.Gen1} | {m.Gen2} | {Mb(m.Allocated)} | {Mb(m.Retained)} |"));
        }
        table.AppendLine().AppendLine($"Whole render (parse to raster at 1000x800, system fonts), median of {runs - 2} renders after 2 warm-up renders.")
            .AppendLine().AppendLine("| Document | Render ms | GC pause ms | gen0 | gen1 | gen2 | Allocated MB |").AppendLine("|---|--:|--:|--:|--:|--:|--:|");
        foreach (var (name, html) in Benchmark.Documents().Where(d => d.Name is "generated/table-5000" or "generated/nested-div-20000"))
        {
            var m = Benchmark.MeasureRenderGc(html, runs);
            table.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {name} | {Ms(m.Layout)} | {Ms(m.Pause)} | {m.Gen0} | {m.Gen1} | {m.Gen2} | {Mb(m.Allocated)} |"));
        }
        Console.WriteLine(table);
        return 0;
    }

    // cold: the first document in a fresh process (no warm-up), parse and style, with the JIT work they cause. Run it
    // in a new process each time; the bench command warms up first and so never shows this.
    private static int Cold()
    {
        var (jitStart, methodsStart) = (System.Runtime.JitInfo.GetCompilationTime(), System.Runtime.JitInfo.GetCompiledMethodCount());
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var document = Folio.Html.TreeBuilder.Parse("<!DOCTYPE html><title>t</title><style>p { color: #333; margin: 1em 0 } .a { display: flex; gap: 4px }</style><p class=a>Hello <b>world</b></p>");
        var parsed = System.Diagnostics.Stopwatch.GetTimestamp();
        var (jitParsed, methodsParsed) = (System.Runtime.JitInfo.GetCompilationTime(), System.Runtime.JitInfo.GetCompiledMethodCount());
        Folio.Style.StyleResolver.Resolve(document, new Folio.Css.MediaContext(800, 600));
        var styled = System.Diagnostics.Stopwatch.GetTimestamp();
        var (jitStyled, methodsStyled) = (System.Runtime.JitInfo.GetCompilationTime(), System.Runtime.JitInfo.GetCompiledMethodCount());
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"parse {Ms(System.Diagnostics.Stopwatch.GetElapsedTime(start, parsed))} ms ({methodsParsed - methodsStart} methods, {Ms(jitParsed - jitStart)} ms JIT), " +
            $"style {Ms(System.Diagnostics.Stopwatch.GetElapsedTime(parsed, styled))} ms ({methodsStyled - methodsParsed} methods, {Ms(jitStyled - jitParsed)} ms JIT)"));
        return 0;
    }

    private static string Ms(TimeSpan time) => time.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture);

    private static string Mb(long bytes) => (bytes / (1024.0 * 1024)).ToString("F1", CultureInfo.InvariantCulture);
}
