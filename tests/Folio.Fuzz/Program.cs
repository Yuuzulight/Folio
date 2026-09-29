using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Folio.RenderTests;
using Xunit.MicrosoftTestingPlatform;
using Xunit.Runner.InProc.SystemConsole;

namespace Folio.Fuzz;

/// <summary>
/// <c>fuzz &lt;seconds&gt; [target...]</c> mutates the seed inputs of each target for that long; anything else runs the
/// regression tests. Mutation-based and dependency-free: no coverage feedback, so it finds shallow crashes, hangs and
/// broken invariants rather than deep ones.
/// </summary>
public static class Program
{
    // An input still running after this long is reported like a crash (a hang or a quadratic path) and ends the run.
    private static readonly TimeSpan SlowInput = TimeSpan.FromSeconds(3);
    private const int MaxInputLength = 64 * 1024;
    private const int MaxFailuresPerTarget = 5;

    private sealed record Running(Target Target, byte[] Input, long Started);

    private static Running? _running;

    public static int Main(string[] args)
    {
        if (args is ["fuzz", var seconds, .. var names])
            return Fuzz(TimeSpan.FromSeconds(int.Parse(seconds)), names);

        // The rest mirrors the entry point xUnit generates.
        if (args.Any(arg => arg is "--server" or "--internal-msbuild-node"))
            return TestPlatformTestFramework.RunAsync(args, SelfRegisteredExtensions.AddSelfRegisteredExtensions).GetAwaiter().GetResult();
        return ConsoleRunner.Run(args).GetAwaiter().GetResult();
    }

    private static int Fuzz(TimeSpan budget, string[] names)
    {
        var seed = Environment.TickCount;
        Console.WriteLine($"random seed {seed}");
        var random = new Random(seed);
        var failed = false;
        using var watchdog = new Timer(_ =>
        {
            if (Volatile.Read(ref _running) is { } running && Stopwatch.GetElapsedTime(running.Started) > SlowInput)
            {
                Report(running.Target, running.Input, $"still running after {SlowInput.TotalSeconds} s");
                Environment.Exit(1);
            }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        foreach (var target in names.Length == 0 ? Targets.All : names.Select(Targets.Find))
        {
            var seeds = target.Seeds().ToList();
            var failures = seeds.Count(input => !Check(target, input));
            var runs = 0;
            for (var clock = Stopwatch.StartNew(); clock.Elapsed < budget && failures < MaxFailuresPerTarget; runs++)
            {
                if (!Check(target, Mutate(seeds[random.Next(seeds.Count)], seeds, target.Dictionary, random)))
                    failures++;
            }
            Console.WriteLine($"{target.Name}: {seeds.Count} seeds, {runs} mutated inputs, {failures} failures");
            failed |= failures > 0;
        }
        return failed ? 1 : 0;
    }

    private static bool Check(Target target, byte[] input)
    {
        Volatile.Write(ref _running, new Running(target, input, Stopwatch.GetTimestamp()));
        try
        {
            target.Run(input);
            return true;
        }
        catch (Exception e)
        {
            Report(target, input, e.ToString());
            return false;
        }
        finally
        {
            Volatile.Write(ref _running, null);
        }
    }

    private static void Report(Target target, byte[] input, string problem)
    {
        var folder = Path.Combine(RepoPaths.Tests, "fuzz-output", target.Name);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Convert.ToHexStringLower(SHA256.HashData(input))[..16]);
        File.WriteAllBytes(path, input);
        Console.WriteLine($"{target.Name} failed on {path}:\n{problem}\n");
    }

    /// <summary>A few stacked byte-level edits: bit flips, special bytes, deletions, duplications, splices, tokens.</summary>
    private static byte[] Mutate(byte[] input, List<byte[]> seeds, string[] dictionary, Random random)
    {
        var data = new List<byte>(input);
        for (var edits = 1 + random.Next(8); edits > 0; edits--)
        {
            var at = random.Next(data.Count + 1);
            var length = Math.Min(random.Next(1, 17), data.Count - at);
            switch (random.Next(dictionary.Length > 0 ? 6 : 5))
            {
                case 0 when at < data.Count:
                    data[at] ^= (byte)(1 << random.Next(8));
                    break;
                case 1 when at < data.Count:
                    data[at] = (byte)(random.Next(4) switch { 0 => 0, 1 => 0xFF, 2 => 0x80, _ => random.Next(256) });
                    break;
                case 2:
                    data.RemoveRange(at, length);
                    break;
                case 3:
                    data.InsertRange(random.Next(data.Count + 1), data.GetRange(at, length));
                    break;
                case 4:
                    var other = seeds[random.Next(seeds.Count)];
                    var from = random.Next(other.Length + 1);
                    data.RemoveRange(at, data.Count - at);
                    data.AddRange(other.AsSpan(from, Math.Min(other.Length - from, MaxInputLength / 2)));
                    break;
                case 5:
                    // Sometimes repeated, to reach nesting limits and slow paths.
                    var token = dictionary[random.Next(dictionary.Length)];
                    var count = random.Next(8) == 0 ? random.Next(2, 3000) : 1;
                    data.InsertRange(at, Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(token, count))));
                    break;
            }
        }
        return data.Count > MaxInputLength ? data.GetRange(0, MaxInputLength).ToArray() : data.ToArray();
    }
}
