namespace Folio.RenderTests;

/// <summary>
/// The conformance corpus drawn by Folio's own raster backend and by the Skia one, each compared with the same reference
/// images. The backend is a replacement for Skia only once it passes what Skia passes.
/// </summary>
public class SoftwareConformanceTests(ITestOutputHelper output)
{
    // Pages whose text the software backend draws a little softer than Skia does on Windows, which snaps the horizontal
    // stems of glyphs to whole pixels; Folio's outlines are not hinted. Each is 0.5% to 1% of its pixels past the limit.
    // A page that starts to pass is a result: remove it here.
    private static readonly HashSet<string> TextHintingGaps =
    [
        "D12/iframe-01",
        "D12/iframe-02",
        "S6/bilingual-train-timetable",
        "S6/chinese-tech-news-article",
        "S6/japanese-recipe-card",
    ];

    [Fact]
    public void PassesWhatTheSkiaBackendPasses()
    {
        // Skia draws glyphs differently on each operating system (FreeType, DirectWrite); the limits here are for Windows.
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "The text limits are for Skia on Windows.");
        var failures = new List<string>();
        var fixedGaps = new List<string>();
        int skiaPasses = 0, softwarePasses = 0, total = 0;
        var report = new System.Text.StringBuilder();
        foreach (var artifact in Conformance.Discover().Where(a => a.Corpus.Length == 0))
        {
            var skia = Conformance.Run(artifact);
            var software = Conformance.Run(artifact, software: true);
            total++;
            if (skia.Passed)
                skiaPasses++;
            if (software.Passed)
                softwarePasses++;
            report.AppendLine($"{artifact.Id}: skia {skia.Outcome}, software {software.Outcome}{(software.Passed ? "" : " - " + software.Detail)}");
            var known = TextHintingGaps.Contains(artifact.Id);
            if (skia.Passed && !software.Passed && !known)
                failures.Add($"{artifact.Id}: {software.Outcome} ({software.Detail})");
            if (known && software.Passed)
                fixedGaps.Add(artifact.Id);
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "software-conformance-report.txt"), report.ToString());
        output.WriteLine($"skia {skiaPasses}/{total}, software {softwarePasses}/{total}");
        Assert.True(failures.Count == 0, $"software passes {softwarePasses} of {total}, skia {skiaPasses}; the software backend fails what Skia passes in:\n" + string.Join("\n", failures));
        Assert.True(fixedGaps.Count == 0, "These pages pass now; remove them from TextHintingGaps: " + string.Join(", ", fixedGaps));
    }
}
