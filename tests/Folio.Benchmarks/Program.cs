using System.Globalization;
using System.Text;
using Xunit.MicrosoftTestingPlatform;
using Xunit.Runner.InProc.SystemConsole;

namespace Folio.Benchmarks;

/// <summary>
/// <c>bench [iterations]</c> measures every benchmark document and prints a table (also to the GitHub Actions job
/// summary when run there). Documents over a target get a warning, never a failure: timings on shared machines are
/// noisy. Anything else runs the harness's own tests.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args is ["bench", .. var rest])
            return Bench(rest is [var n] ? int.Parse(n, CultureInfo.InvariantCulture) : 20);

        // The rest mirrors the entry point xUnit generates.
        if (args.Any(arg => arg is "--server" or "--internal-msbuild-node"))
            return TestPlatformTestFramework.RunAsync(args, SelfRegisteredExtensions.AddSelfRegisteredExtensions).GetAwaiter().GetResult();
        return ConsoleRunner.Run(args).GetAwaiter().GetResult();
    }

    private static int Bench(int iterations)
    {
        var table = new StringBuilder()
            .AppendLine($"Parse to layout, median of {iterations} runs; typical-artifact targets: {Benchmark.TimeTarget.TotalMilliseconds} ms and {Mb(Benchmark.MemoryTarget)} MB for the whole first paint.")
            .AppendLine()
            .AppendLine($"| Document | KB | {string.Concat(Measurement.StageNames.Select(n => n + " ms | "))}Total ms | Allocated MB | Retained MB |")
            .AppendLine($"|---|--:|{string.Concat(Measurement.StageNames.Select(_ => "--:|"))}--:|--:|--:|");
        var warnings = new List<string>();
        foreach (var (name, html) in Benchmark.Documents())
        {
            var m = Benchmark.Measure(name, html, iterations);
            table.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {m.Name} | {m.Length / 1024} | {string.Concat(m.Stages.Select(t => Ms(t) + " | "))}{Ms(m.Total)} | {Mb(m.Allocated)} | {Mb(m.Retained)} |"));
            if (m.Total > Benchmark.TimeTarget || m.Retained > Benchmark.MemoryTarget)
                warnings.Add($"{m.Name} is over a typical-artifact target: {Ms(m.Total)} ms, {Mb(m.Retained)} MB retained");
        }

        Console.WriteLine(table);
        foreach (var warning in warnings)
            Console.WriteLine($"::warning::{warning}"); // A GitHub Actions annotation; plain text elsewhere.
        if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary)
            File.AppendAllText(summary, "### Benchmarks\n\n" + table);
        return 0;
    }

    private static string Ms(TimeSpan time) => time.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture);

    private static string Mb(long bytes) => (bytes / (1024.0 * 1024)).ToString("F1", CultureInfo.InvariantCulture);
}
