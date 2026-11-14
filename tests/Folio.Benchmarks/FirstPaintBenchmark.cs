using System.Diagnostics;
using System.Globalization;
using Folio.Painting;
using Folio.Painting.Software;
using Folio.RenderTests;
using Folio.Typography;

namespace Folio.Benchmarks;

/// <summary>
/// A headless stand-in for the visible-window first-paint benchmark (tools/Folio.WinForms.Benchmarks, #722): for each S1 and S3
/// artifact, a new document is parsed, laid out at 1920x1080 and 100%, rasterized in full into a new surface and copied into a
/// bitmap, as a new <c>FolioView</c> does on its first paint, in a warm process. It leaves out the window itself (GDI+ and
/// message dispatch), so it is for comparing changes to the pipeline, not for qualifying the budget: that is the visible run.
/// Copy is a managed-array copy of the surface, which understates the real copy into a GDI+ bitmap; a long page's scrollbar
/// re-layout (the document gets narrower once the bar appears) is not modeled; and nothing here is a budget qualification.
/// <c>firstpaint [rounds]</c> alternates the blur's repeated-line shortcut off and on in one process, so both see the same JIT and
/// machine state; they produce the same bytes (GaussianBlurDeduplicationTests).
/// </summary>
internal static class FirstPaintBenchmark
{
    private const int Width = 1920, Height = 1080, RunsPerArtifact = 5;

    private sealed record Artifact(string Name, string Html);

    private sealed record Round(double Median, double P95, Dictionary<string, Sample> Artifacts);

    private readonly record struct Sample(double Parse, double Layout, double Raster, double Copy)
    {
        public double Total => Parse + Layout + Raster + Copy;
    }

    public static int Run(int rounds, string? only = null)
    {
        var artifacts = new List<Artifact>();
        foreach (var category in new[] { "S1", "S3" })
            foreach (var path in Directory.GetFiles(Path.Combine(RepoPaths.Tests, "conformance", category), "index.html", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                artifacts.Add(new Artifact($"{category}/{Path.GetFileName(Path.GetDirectoryName(path))}", File.ReadAllText(path)));
        var options = new FolioOptions().WithDefaultFonts();
        for (var i = 0; i < 3; i++)
            Once("<p>Warm up the renderer.</p>", options); // as the visible harness does
        var results = new[] { new List<Round>(), new List<Round>() };
        try
        {
            for (var round = 0; round < rounds; round++)
                for (var variant = 0; variant < 2; variant++)
                {
                    if (only is not null && (only == "on") != (variant == 1))
                        continue;
                    GaussianBlur.Deduplicate = variant == 1;
                    var medians = new Dictionary<string, Sample>();
                    foreach (var artifact in artifacts)
                    {
                        Once(artifact.Html, options);
                        var samples = Enumerable.Range(0, RunsPerArtifact).Select(_ => Once(artifact.Html, options)).ToList();
                        medians[artifact.Name] = new Sample(Mid(samples, s => s.Parse), Mid(samples, s => s.Layout), Mid(samples, s => s.Raster), Mid(samples, s => s.Copy));
                    }
                    var totals = medians.Values.Select(s => s.Total).Order().ToList();
                    results[variant].Add(new Round(Median(totals), Percentile(totals, 0.95), medians));
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"round {round + 1} {(variant == 1 ? "shortcut on " : "shortcut off")}: corpus median {results[variant][^1].Median:F1} ms, p95 {results[variant][^1].P95:F1} ms"));
                }
        }
        finally { GaussianBlur.Deduplicate = true; }

        if (only is not null)
        {
            // One variant in this process (a fresh JIT, as the visible harness has): the table is that variant's.
            var variant = only == "on" ? 1 : 0;
            Console.WriteLine($"| Artifact | Total | Parse | Layout | Raster | Copy |  (median over {rounds} rounds, ms; Copy is a managed-array copy, understated)");
            foreach (var artifact in artifacts)
            {
                double Of(Func<Sample, double> pick) => Median(results[variant].Select(r => pick(r.Artifacts[artifact.Name])));
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"| {artifact.Name} | {Of(s => s.Total):F1} | {Of(s => s.Parse):F1} | {Of(s => s.Layout):F1} | {Of(s => s.Raster):F1} | {Of(s => s.Copy):F1} |"));
            }
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"PROCESS corpus median {results[variant][0].Median:F1} p95 {results[variant][0].P95:F1}"));
            return 0;
        }
        string Spread(IEnumerable<double> values) { var v = values.Order().ToList(); return string.Create(CultureInfo.InvariantCulture, $"{Median(v):F1} (min {v[0]:F1}, max {v[^1]:F1})"); }
        Console.WriteLine();
        Console.WriteLine($"Corpus over {artifacts.Count} S1/S3 artifacts at {Width}x{Height}, median of each artifact's {RunsPerArtifact} runs, {rounds} rounds, ms:");
        Console.WriteLine("| | Corpus median | Corpus p95 (nearest rank) |");
        Console.WriteLine("|---|--:|--:|");
        Console.WriteLine($"| blur shortcut off (before) | {Spread(results[0].Select(r => r.Median))} | {Spread(results[0].Select(r => r.P95))} |");
        Console.WriteLine($"| blur shortcut on (after) | {Spread(results[1].Select(r => r.Median))} | {Spread(results[1].Select(r => r.P95))} |");
        Console.WriteLine();
        Console.WriteLine("| Artifact | Before total | After total | Before raster | After raster | Layout | Copy |");
        Console.WriteLine("|---|--:|--:|--:|--:|--:|--:|");
        foreach (var artifact in artifacts)
        {
            double Of(int variant, Func<Sample, double> pick) => Median(results[variant].Select(r => pick(r.Artifacts[artifact.Name])));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"| {artifact.Name} | {Of(0, s => s.Total):F1} | {Of(1, s => s.Total):F1} | {Of(0, s => s.Raster):F1} | {Of(1, s => s.Raster):F1} | {Of(1, s => s.Layout):F1} | {Of(1, s => s.Copy):F1} |"));
        }
        Console.WriteLine();
        Console.WriteLine("Copy is a managed-array copy (understated against a GDI+ bitmap); a long page's scrollbar re-layout is not modeled; this is not a budget qualification (the visible run is).");
        return 0;

        static double Mid(List<Sample> samples, Func<Sample, double> pick) => Median(samples.Select(pick));

        static double Percentile(List<double> sorted, double p) => sorted[(int)Math.Ceiling(sorted.Count * p) - 1];
    }

    // One median everywhere: the middle value, or the mean of the two middle ones for an even count.
    private static double Median(IEnumerable<double> values)
    {
        var v = values.Order().ToList();
        return v.Count % 2 == 0 ? (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2 : v[v.Count / 2];
    }

    /// <summary><c>firstpaint [rounds] [on|off]</c>; false with a message when the arguments are not that.</summary>
    public static bool TryParse(string[] args, out int rounds, out string? only)
    {
        (rounds, only) = (5, null);
        if (args.Length > 2 || args.Length > 0 && !int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out rounds) || rounds < 1 || args.Length == 2 && args[1] is not ("on" or "off"))
        {
            Console.Error.WriteLine("usage: firstpaint [rounds >= 1] [on|off]");
            return false;
        }
        only = args.Length == 2 ? args[1] : null;
        return true;
    }

    // One first paint, as the control does it: a new document, laid out and painted, a new surface drawn in full and copied into a bitmap.
    private static Sample Once(string html, FolioOptions options)
    {
        GC.Collect();
        var t0 = Stopwatch.GetTimestamp();
        using var document = Document.Parse(html, options);
        var t1 = Stopwatch.GetTimestamp();
        var (list, _) = document.Paint(Width, Height, 1, ComplexShaper.Instance, 0);
        var t2 = Stopwatch.GetTimestamp();
        using var surface = new SoftwareRasterSurface(Width, Height);
        if (list is not null)
            RetainedScroll.Repaint(list, surface, 1, 0, new Paint(new Rgba(1, 1, 1, 1)));
        var t3 = Stopwatch.GetTimestamp();
        var (row, frame) = (new byte[Width * 4], new byte[Width * Height * 4]);
        for (var y = 0; y < Height; y++)
        {
            surface.ReadBgraRow(y, row);
            Buffer.BlockCopy(row, 0, frame, y * row.Length, row.Length);
        }
        var t4 = Stopwatch.GetTimestamp();
        GC.KeepAlive(frame);
        static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;
        return new Sample(Ms(t0, t1), Ms(t1, t2), Ms(t2, t3), Ms(t3, t4));
    }
}
