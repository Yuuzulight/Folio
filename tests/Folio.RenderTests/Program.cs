using Xunit.MicrosoftTestingPlatform;
using Xunit.Runner.InProc.SystemConsole;

namespace Folio.RenderTests;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "approve")
            return Approve(args[1..]);
        if (args.Length > 0 && args[0] == "conformance")
            return ConformanceReport(args[1..]);

        // The rest mirrors the entry point xUnit generates.
        if (args.Any(arg => arg is "--server" or "--internal-msbuild-node"))
            return TestPlatformTestFramework.RunAsync(args, SelfRegisteredExtensions.AddSelfRegisteredExtensions).GetAwaiter().GetResult();
        return ConsoleRunner.Run(args).GetAwaiter().GetResult();
    }

    // folio-test conformance [category|category/name]...: renders the corpus and prints pass rates per category (also to
    // the GitHub Actions job summary). Fails only on a crash, hang or limit hit, which M1 allows nowhere.
    private static int ConformanceReport(string[] filters)
    {
        var results = Conformance.Discover()
            .Where(a => filters.Length == 0 || filters.Any(f => a.Id == f || a.Id.StartsWith(f.TrimEnd('/') + "/", StringComparison.Ordinal)))
            .Select(Conformance.Run)
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

        var approved = targets.SelectMany(GoldenSuite.Repo.Approve).ToList();
        foreach (var test in approved)
            Console.WriteLine($"approved {test}");
        if (approved.Count == 0)
            Console.Error.WriteLine($"No actual images for {string.Join(", ", targets)} in {GoldenSuite.Repo.Output}; run the golden tests first.");
        return approved.Count == 0 ? 1 : 0;
    }
}
