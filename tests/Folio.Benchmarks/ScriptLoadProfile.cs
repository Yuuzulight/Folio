using System.Diagnostics;
using System.Globalization;
using Folio.Dom;
using Folio.RenderTests;
using Folio.Scripting;
using Folio.Scripting.Dom;
using Folio.Scripting.Native;

namespace Folio.Benchmarks;

/// <summary>
/// <c>scriptprofile &lt;page&gt; [loads]</c> loads one pinned-library conformance page repeatedly in this process, the way the
/// content host does (parse, engine, bindings, script run, layout, raster), and prints min / median / max per phase for the
/// loads after the first. A development aid for profiling start-up cost without the IPC and process overhead; attach
/// <c>dotnet-trace</c> to it with a large load count to sample the script and layout hot paths.
/// </summary>
public static class ScriptLoadProfile
{
    // With FOLIO_ALLOC_TYPES=1: GC allocation ticks (one per ~100 KB allocated) by type, over the warm loads.
    private sealed class AllocationListener : System.Diagnostics.Tracing.EventListener
    {
        public readonly Dictionary<string, long> Ticks = new();
        public bool Active;
        protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose, (System.Diagnostics.Tracing.EventKeywords)0x1);
        }
        protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e)
        {
            if (!Active || e.EventName?.StartsWith("GCAllocationTick", StringComparison.Ordinal) != true) return;
            var name = e.Payload![e.PayloadNames!.ToList().IndexOf("TypeName")] as string ?? "?";
            lock (Ticks) Ticks[name] = Ticks.GetValueOrDefault(name) + 1;
        }
    }

    public static int Run(string[] args)
    {
        var id = args.Length > 0 ? args[0] : "D4/library-card-02";
        var loads = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 15;
        var folder = Path.Combine(RepoPaths.Tests, "conformance", id.Replace('/', Path.DirectorySeparatorChar));
        var manifestPath = Path.Combine(folder, "manifest.json");
        var page = Path.Combine(folder, "index.html");
        if (File.Exists(manifestPath))
        {
            using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (manifest.RootElement.TryGetProperty("compiled", out var name)) page = Path.Combine(folder, name.GetString()!);
        }
        var html = File.ReadAllText(page);
        var phases = new[] { "parse", "engine", "bindings", "scripts", "layout", "raster", "publish", "total", "allocMB", "gcPauseMs", "gcs" };
        var samples = phases.ToDictionary(phase => phase, _ => new List<double>());
        using var listener = Environment.GetEnvironmentVariable("FOLIO_ALLOC_TYPES") == "1" ? new AllocationListener() : null;
        using var surface = new Folio.Painting.SoftwareRasterSurface(1280, 720);
        for (var load = 0; load < loads; load++)
        {
            var timings = new Dictionary<string, double>();
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var pause = GC.GetTotalPauseDuration();
            var gen0 = GC.CollectionCount(0);
            var total = Stopwatch.GetTimestamp();
            var stamp = total;
            void Mark(string phase)
            {
                var now = Stopwatch.GetTimestamp();
                timings[phase] = Stopwatch.GetElapsedTime(stamp, now).TotalMilliseconds;
                stamp = now;
            }
            var options = new FolioOptions
            {
                Scripting = ScriptingPolicy.ContentProcess,
                Fonts = Folio.Typography.BundledFontSource.Settings,
                ResourceLoader = ConformanceCache.Loader,
                Raster = Folio.Painting.SoftwareRasterBackend.Instance,
            };
            var document = Document.Parse(html, options);
            Mark("parse");
            var engine = new NativeJavaScriptEngine();
            using var eventLoop = new HtmlEventLoop(engine);
            Mark("engine");
            DomBindings.Install(engine, document, eventLoop: eventLoop);
            ScriptGlobalsInstaller.Install(eventLoop, engine);
            Mark("bindings");
            using (var runner = new ScriptRunner(document, engine, eventLoop, options))
            {
                runner.Run();
                if (load == 0) foreach (var diagnostic in runner.Diagnostics) Console.WriteLine("diagnostic: " + diagnostic.Message);
                eventLoop.SpinUntilIdle(waitForTimers: TimeSpan.FromMilliseconds(25));
                Mark("scripts");
                var list = document.Paint(1280, 720, 1, Folio.Typography.ComplexShaper.Instance, 0).List;
                Mark("layout");
                Folio.Painting.DisplayListPlayer.Replay(list, surface.Canvas, new Folio.Painting.RectF(0, 0, 1280, 720));
                Mark("raster");
                var pixels = surface.PremultipliedPixels;
                for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
                Mark("publish");
            }
            DomBindings.Dispose(engine);
            engine.Dispose();
            document.Dispose();
            timings["total"] = Stopwatch.GetElapsedTime(total).TotalMilliseconds;
            timings["allocMB"] = (GC.GetAllocatedBytesForCurrentThread() - allocated) / 1048576.0;
            timings["gcPauseMs"] = (GC.GetTotalPauseDuration() - pause).TotalMilliseconds;
            timings["gcs"] = GC.CollectionCount(0) - gen0;
            if (load == 0) { Console.WriteLine($"first load: {timings["total"]:F1} ms (not counted)"); if (listener is not null) listener.Active = true; continue; }
            foreach (var phase in phases) samples[phase].Add(timings.GetValueOrDefault(phase));
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        }
        if (listener is not null)
        {
            listener.Active = false;
            Console.WriteLine("allocation ticks (about 100 KB each) by type, all warm loads:");
            lock (listener.Ticks)
                foreach (var pair in listener.Ticks.OrderByDescending(pair => pair.Value).Take(25))
                    Console.WriteLine($"  {pair.Value,6} {pair.Key}");
        }
        Console.WriteLine($"{id}, {loads - 1} warm loads (ms): min / median / max");
        foreach (var phase in phases)
        {
            var sorted = samples[phase].Order().ToArray();
            if (sorted.Length == 0) continue;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {phase,-9} {sorted[0],7:F2} {sorted[sorted.Length / 2],7:F2} {sorted[^1],7:F2}"));
        }
        return 0;
    }
}
