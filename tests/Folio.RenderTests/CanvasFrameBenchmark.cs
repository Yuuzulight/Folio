using Folio.Painting;
using Folio.Scripting;
using Folio.Scripting.Dom;
using Folio.Scripting.Native;
using Folio.Typography;
#if !FOLIO_AOT_PROBE
using Folio.Scripting.Jint;
#endif

namespace Folio.RenderTests;

/// <summary>
/// The script-driven canvas frame budget. Compiled into the RenderTests <c>canvas-frames</c> command (tiered JIT, either
/// engine) and into tools/Folio.CanvasFrameProbe (NativeAOT, native engine only: <c>FOLIO_AOT_PROBE</c>).
/// </summary>
internal static class CanvasFrameBenchmark
{
    public static int Run(int frameCount)
    {
#if FOLIO_AOT_PROBE
        const bool native = true;
#else
        var native = string.Equals(Environment.GetEnvironmentVariable("FOLIO_CONFORMANCE_ENGINE"), "native", StringComparison.OrdinalIgnoreCase);
#endif
        // Each artifact runs several times in a fresh document and the gate uses the median of the runs' p95s:
        // a busy machine (another process, a shared CI runner) only ever adds time to some runs, while a real
        // regression slows them all. A run cannot simply measure longer instead, because a page's animation may
        // end and the idle frames after it would dilute the percentile.
        const int runs = 4;
        Console.WriteLine($"Script-driven canvas frame benchmark ({runs} runs x {frameCount} frames, 16.7 ms virtual ticks)");
        Console.WriteLine($"Script engine: {(native ? "native Folio" : "Jint compatibility")}");
        Console.WriteLine("Each column is the median across runs of that run's p95.");
        Console.WriteLine("| Artifact | Script/update p95 ms | Display-list p95 ms | Host paint p95 ms | Total p95 ms | Target |");
        Console.WriteLine("|---|--:|--:|--:|--:|---:|");
        var failed = false;
        foreach (var artifact in Conformance.Discover().Where(a => a.Category is "D5" or "D7"))
        {
            var scriptRuns = new List<double>(runs);
            var buildRuns = new List<double>(runs);
            var paintRuns = new List<double>(runs);
            var totalRuns = new List<double>(runs);
            for (var run = 0; run < runs; run++)
            {
                var scriptSamples = new List<double>(frameCount);
                var buildSamples = new List<double>(frameCount);
                var paintSamples = new List<double>(frameCount);
                var totalSamples = new List<double>(frameCount);
                var html = File.ReadAllText(Path.Combine(artifact.Folder, "index.html"));
                using var document = Document.Parse(html, Conformance.Options);
                document.Paint(Conformance.ManifestOf(artifact).Viewport, 600);
#if FOLIO_AOT_PROBE
                using var engine = new NativeJavaScriptEngine();
#else
                using var engine = native
                    ? (IScriptEngine)new NativeJavaScriptEngine()
                    : new JintScriptEngine();
#endif
                var clock = new VirtualEventLoopClock();
                using var loop = new HtmlEventLoop(engine, clock);
                DomBindings.Install(engine, document, eventLoop: loop);
                ScriptGlobalsInstaller.Install(loop, engine);
                using var runner = new ScriptRunner(document.Node, engine, loop, Conformance.Options);
                runner.Run();
                loop.UpdateTheRendering();
                using var surface = new SoftwareRasterSurface(800, 600);
                var shaper = ComplexShaper.Instance;
                DisplayList BuildFrame()
                {
                    // Canvas scripts mutate the backing surface without changing layout. Reuse the
                    // retained layout/display-list path so this gate measures a frame update rather
                    // than rebuilding the entire document tree on every canvas draw.
                    return document.PaintFrame(clock.NowMs / 1000d)
                        ?? document.Paint(Conformance.ManifestOf(artifact).Viewport, 600, 1, shaper).List;
                }
                void Replay(DisplayList displayList, bool fullPaint = false)
                {
                    if (!fullPaint && document.LastDamage is { } damage)
                    {
                        if (damage.Width <= 0 || damage.Height <= 0)
                            return;
                        var clipped = new RectF(
                            Math.Max(0, damage.X),
                            Math.Max(0, damage.Y),
                            Math.Min(800, damage.Right) - Math.Max(0, damage.X),
                            Math.Min(600, damage.Bottom) - Math.Max(0, damage.Y));
                        if (clipped.Width <= 0 || clipped.Height <= 0)
                            return;
                        surface.Canvas.Save();
                        surface.Canvas.ClipRoundedRect(new RoundedRect(clipped, default));
                        surface.Canvas.FillRoundedRect(new RoundedRect(clipped, default), new Paint(new Rgba(1f, 1f, 1f, 1f)));
                        DisplayListPlayer.Replay(displayList, surface.Canvas, clipped);
                        surface.Canvas.Restore();
                        return;
                    }
                    surface.Canvas.FillRoundedRect(new RoundedRect(new RectF(0, 0, 800, 600), default), new Paint(new Rgba(1f, 1f, 1f, 1f)));
                    DisplayListPlayer.Replay(displayList, surface.Canvas, new RectF(0, 0, 800, 600));
                }
                Replay(BuildFrame(), fullPaint: true);
                for (var warmup = 0; warmup < 5; warmup++)
                {
                    clock.Advance(16.7);
                    loop.UpdateTheRendering();
                    Replay(BuildFrame());
                }
                // Measure from a collected heap: otherwise the first collection after page load, which
                // promotes everything the warm-up filled (text layouts, glyphs, caches), lands in the window.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                for (var i = 0; i < frameCount; i++)
                {
                    clock.Advance(16.7);
                    var totalStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    var scriptStart = totalStart;
                    loop.UpdateTheRendering();
                    scriptSamples.Add(System.Diagnostics.Stopwatch.GetElapsedTime(scriptStart).TotalMilliseconds);
                    var buildStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    var displayList = BuildFrame();
                    buildSamples.Add(System.Diagnostics.Stopwatch.GetElapsedTime(buildStart).TotalMilliseconds);
                    var paintStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    Replay(displayList);
                    paintSamples.Add(System.Diagnostics.Stopwatch.GetElapsedTime(paintStart).TotalMilliseconds);
                    totalSamples.Add(System.Diagnostics.Stopwatch.GetElapsedTime(totalStart).TotalMilliseconds);
                }
                scriptRuns.Add(Percentile95(scriptSamples));
                buildRuns.Add(Percentile95(buildSamples));
                paintRuns.Add(Percentile95(paintSamples));
                totalRuns.Add(Percentile95(totalSamples));
            }
            var scriptP95 = Median(scriptRuns);
            var buildP95 = Median(buildRuns);
            var paintP95 = Median(paintRuns);
            var totalP95 = Median(totalRuns);
            failed |= totalP95 > 16.7;
            Console.WriteLine($"| {artifact.Id} | {scriptP95:F2} | {buildP95:F2} | {paintP95:F2} | {totalP95:F2} | 16.70 ms |");
        }
        return failed ? 1 : 0;
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        return values.Count % 2 == 1 ? values[values.Count / 2] : (values[values.Count / 2 - 1] + values[values.Count / 2]) / 2;
    }

    private static double Percentile95(List<double> samples)
    {
        samples.Sort();
        return samples[Math.Min(samples.Count - 1, (int)Math.Ceiling(samples.Count * .95) - 1)];
    }
}
