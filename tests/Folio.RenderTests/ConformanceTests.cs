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

    // Rows of "text": dark 8px bands every 20px from `from` down, each 10px further down below row `shiftFrom` by `shift`.
    private static PixelBuffer Lines(int shift = 0, int shiftFrom = 0, int dx = 0)
    {
        var page = ImageComparerTests.Solid(200, 300, 255, 255, 255);
        for (var line = 0; line < 14; line++)
        {
            var top = 10 + 20 * line + (10 + 20 * line >= shiftFrom ? shift : 0);
            for (var y = top; y < top + 8 && y < 300; y++)
            {
                for (var x = 20 + dx + line * 3; x < 150 + dx; x++)
                    page.Pixel(x, y)[0] = page.Pixel(x, y)[1] = page.Pixel(x, y)[2] = 40;
            }
        }
        return page;
    }

    [Theory]
    [InlineData(3, 0, "mostly shifted down 3 px")]
    [InlineData(-2, 0, "mostly shifted up 2 px")]
    [InlineData(0, 4, "mostly shifted right 4 px")]
    public void DiagnosisNamesAUniformShift(int dy, int dx, string expected)
    {
        var (reference, actual) = (Lines(), Lines(dy, shiftFrom: 100, dx: dx));
        var diff = ImageComparer.Compare(reference, actual, Tolerance.Conformance).Diff;

        var diagnosis = Conformance.Diagnose(reference, actual, diff);

        Assert.EndsWith(expected, diagnosis);
    }

    [Fact]
    public void DiagnosisCallsLocalDifferencesScattered()
    {
        var (reference, actual) = (Lines(), Lines());
        for (var y = 50; y < 60; y++)
            actual.Pixel(170, y)[0] = actual.Pixel(170, y)[1] = actual.Pixel(170, y)[2] = 0;
        var diff = ImageComparer.Compare(reference, actual, Tolerance.Conformance).Diff;

        Assert.Equal("from row 50, within x 170-170, y 50-59; scattered", Conformance.Diagnose(reference, actual, diff));
    }
}
