namespace Folio.RenderTests;

public class ConformanceTests
{
    private const string None = "(none)";

    public static TheoryData<string> Artifacts =>
        Conformance.Discover().Select(a => a.Id).ToList() is { Count: > 0 } ids ? new(ids) : new(None);

    /// <summary>
    /// M1 acceptance criterion 3: every artifact, whatever it needs, renders without an exception, hang or limit hit.
    /// Pass rates against the references are the report's job (<c>Folio.RenderTests conformance</c>), not a test's.
    /// </summary>
    [Theory]
    [MemberData(nameof(Artifacts))]
    public void RendersWithoutCrashHangOrLimit(string id)
    {
        Assert.SkipWhen(id == None, "The conformance corpus is empty.");

        var result = Conformance.Run(Conformance.Discover().Single(a => a.Id == id));

        Assert.False(result.Broke, $"{id}: {result.Outcome}: {result.Detail}");
    }

    [Fact]
    public void ReportsPassRatesPerCategory()
    {
        ConformanceArtifact Artifact(string category, string name) => new("", "root", category, name);
        var results = new ConformanceResult[]
        {
            new(Artifact("S1", "a"), ConformanceOutcome.Pass, null),
            new(Artifact("S1", "b"), ConformanceOutcome.Mismatch, "2.0% of pixels differ"),
            new(Artifact("S1", "c"), ConformanceOutcome.NoReference, null),
            new(Artifact("S3", "d"), ConformanceOutcome.PassSecondary, "review"),
            new(Artifact("D1", "e"), ConformanceOutcome.Crash, "boom"),
        };

        var report = Conformance.Report(results);

        Assert.Contains("| S1 | 3 | 1 | 0 | 1 | 1 | 0 | 0 | 0 | 50.0% of 2 |", report);
        Assert.Contains("| S3 | 1 | 0 | 1 | 0 | 0 | 0 | 0 | 0 | 100.0% of 1 |", report);
        Assert.Contains("| D1 | 1 | 0 | 0 | 0 | 0 | 1 | 0 | 0 | 0.0% of 1 |", report);
        Assert.Contains("| S1/b | Mismatch | 2.0% of pixels differ |", report);
        Assert.DoesNotContain("| S1/a |", report);
    }
}
