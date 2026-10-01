namespace Folio.Benchmarks;

public class BenchmarkTests
{
    // Keeps the harness working between benchmark runs: every document goes through every stage.
    [Fact]
    public void MeasuresEveryDocument()
    {
        foreach (var (name, html) in Benchmark.Documents())
        {
            var m = Benchmark.Measure(name, html, iterations: 1);
            Assert.True(m.Total > TimeSpan.Zero && m.Allocated > 0, name);
        }
    }

    // The frame benchmark's animations all take the paint-only path.
    [Fact]
    public void AnimationFramesArePaintOnly()
    {
        var (median, _, paintOnly) = FrameBenchmark.Measure(FrameBenchmark.Typical, frames: 3);
        Assert.True(paintOnly);
        Assert.True(median > TimeSpan.Zero);
    }
}
