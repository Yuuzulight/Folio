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

    // The gc mode lays a document out and sees its fragment tree kept alive. The document is large enough (2,000 paragraphs,
    // well over a megabyte of fragments) that what layout keeps stands clear of the few kilobytes the heap's total moves by
    // between two full collections; a handful of fragments did not always.
    [Fact]
    public void MeasuresLayoutAndWhatItKeeps()
    {
        var html = string.Concat(Enumerable.Range(0, 2000).Select(i => $"<p>Item {i} <b>two</b> three</p>")) + "<div style='display: flex'><span>four</span></div>";
        var m = Benchmark.MeasureLayoutGc(html, runs: 3);
        Assert.True(m.Layout > TimeSpan.Zero, "layout time");
        Assert.True(m.Allocated > 0, "allocated");
        Assert.True(m.Retained > 256 * 1024, $"retained {m.Retained} bytes");
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
