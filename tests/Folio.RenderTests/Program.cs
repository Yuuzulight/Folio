using Xunit.MicrosoftTestingPlatform;
using Xunit.Runner.InProc.SystemConsole;

namespace Folio.RenderTests;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "approve")
            return Approve(args[1..]);

        // The rest mirrors the entry point xUnit generates.
        if (args.Any(arg => arg is "--server" or "--internal-msbuild-node"))
            return TestPlatformTestFramework.RunAsync(args, SelfRegisteredExtensions.AddSelfRegisteredExtensions).GetAwaiter().GetResult();
        return ConsoleRunner.Run(args).GetAwaiter().GetResult();
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
