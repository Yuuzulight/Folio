using System.Diagnostics;
using System.Text;
using Folio.Painting;
using Folio.RenderTests;
#if !FOLIO_AOT_PROBE
using Folio.Skia;
#endif
using Folio.Typography;

namespace Folio.Benchmarks;

/// <summary>
/// Paint-only animation frames (docs/study/18-memory-and-performance.md, M2 target 8 ms): a dashboard whose cards,
/// bars and badges animate opacity, transforms, filters and colours forever, drawn frame after frame at 60 frames a
/// second. A frame is what <c>FolioView</c> does per tick: restyle the animated elements, rebuild the display list,
/// replay it into an 800x600 viewport. The typical dashboard (8 cards of 4 bars, about 40 animated elements) is held
/// to the target; the busy one (24 cards of 6 bars, about 190) is reported for comparison.
/// </summary>
internal static class FrameBenchmark
{
    public static readonly TimeSpan Target = TimeSpan.FromMilliseconds(8);
    public static readonly TimeSpan HoverTarget = TimeSpan.FromMilliseconds(4);
    public static readonly TimeSpan ScrollTarget = TimeSpan.FromMilliseconds(8);
    private static readonly RectF Viewport = new(0, 0, 800, 600);
    private static readonly Paint Background = new(new Rgba(1, 1, 1, 1));

    private static readonly FolioOptions Options = new()
    {
        Fonts = new FontSettings
        {
            Source = new FontFolderSource(Path.Combine(RepoPaths.Tests, "fonts")),
            GenericFamilies = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase) { ["sans-serif"] = ["Source Sans 3", "Folio Box"] },
        },
    };

    public static string Typical => Html(8, 4);

    public static string Busy => Html(24, 6);

    public static string HoverHtml => Typical;

    public static string ScrollHtml => Html(24, 4);

    private static string Html(int cardCount, int barCount)
    {
        var cards = new StringBuilder();
        for (var i = 0; i < cardCount; i++)
        {
            cards.Append($"<div class=card style='animation-delay: {-i * 0.13:0.00}s'><h3>Metric {i + 1}</h3><p class=value>{(i * 7919) % 10000:N0}</p>");
            cards.Append("<div class=bars>");
            for (var b = 0; b < barCount; b++)
                cards.Append($"<i style='height: {20 + (i * 13 + b * 29) % 60}px; animation-delay: {-b * 0.2:0.0}s'></i>");
            cards.Append("</div><span class=badge>live</span></div>");
        }
        return $$"""
            <!DOCTYPE html><style>
            body { margin: 0; padding: 20px; font-family: sans-serif; background: #f4f6fb; }
            .grid { display: grid; grid-template-columns: repeat(4, 1fr); gap: 14px; }
            .card { position: relative; background: #fff; border-radius: 12px; padding: 12px; box-shadow: 0 2px 8px rgba(0,0,0,.08);
                    animation: float 3s ease-in-out infinite alternate; }
            .card:hover { background: #e8ecf8; box-shadow: 0 4px 16px rgba(0,0,0,.15); }
            .value { font-size: 22px; font-weight: 700; margin: 4px 0; }
            .bars { display: flex; align-items: flex-end; gap: 4px; height: 80px; }
            .bars i { flex: 1; background: #5b5bd6; border-radius: 3px 3px 0 0; transform-origin: bottom; animation: pulse 1.2s linear infinite alternate; }
            .bars i:hover { background: #3b3bc0; }
            .badge { position: absolute; top: 10px; right: 10px; padding: 1px 6px; border-radius: 9px; color: #fff; font-size: 11px;
                     animation: glow 2s infinite; }
            @keyframes float { from { transform: translateY(0) rotate(0deg); opacity: .85 } to { transform: translateY(-6px) rotate(.5deg); opacity: 1 } }
            @keyframes pulse { from { transform: scaleY(.4); filter: saturate(.5) } to { transform: scaleY(1); filter: saturate(1.2) } }
            @keyframes glow { 0%, 100% { background-color: #16a36a; box-shadow: 0 0 0 0 rgba(22,163,106,.5) } 50% { background-color: #e0445a; box-shadow: 0 0 0 6px rgba(22,163,106,0) } }
            </style><div class=grid>{{cards}}</div>
            """;
    }

    /// <summary>The median and 95th percentile frame times, and whether every frame took the paint-only path.</summary>
    public static (TimeSpan Median, TimeSpan P95, bool PaintOnly) Measure(string html, int frames) => Steady(() => MeasurePass(html, frames));

    private static (TimeSpan Median, TimeSpan P95, bool PaintOnly) MeasurePass(string html, int frames)
    {
        using var document = Document.Parse(html, Options);
        var shaper = SoftwareRasterBackend.Instance.TextShaper;
        document.Paint(800, 600, 1, shaper, 0);
        using var surface = new SoftwareRasterSurface(800, 600);
        var canvas = surface.Canvas;
        Replay(canvas, document.DisplayList!, Viewport);
        var paintOnly = true;
        var times = new List<TimeSpan>();
        for (var i = -10; i < frames; i++) // ten warm-up frames
        {
            var start = Stopwatch.GetTimestamp();
            var time = (i + 10) / 60.0;
            var list = document.PaintFrame(time);
            paintOnly &= list is not null;
            list ??= document.Paint(800, 600, 1, shaper, time).List;
            if (document.LastDamage is { } changed && changed.Width > 0 && changed.Height > 0)
            {
                var damage = DisplayListPlayer.SnapToDevicePixels(changed, 1);
                canvas.Save();
                canvas.ClipRoundedRect(new RoundedRect(damage, default));
                Replay(canvas, list, damage);
                canvas.Restore();
            }
            else if (document.LastDamage is { Width: <= 0, Height: <= 0 })
            {
                // The retained bitmap already contains this unchanged frame.
            }
            else
            {
                Replay(canvas, list, Viewport);
            }
            if (i >= 0)
                times.Add(Stopwatch.GetElapsedTime(start));
        }
        times.Sort();
        return (times[times.Count / 2], times[Math.Min(times.Count - 1, (int)(times.Count * 0.95))], paintOnly);
    }

#if !FOLIO_AOT_PROBE
    /// <summary>
    /// What replaying one display list into a surface costs on each raster backend (#568, #575): the median of
    /// <paramref name="frames"/> replays of the typical dashboard's list, Skia and Folio's own software backend.
    /// </summary>
    public static (TimeSpan Skia, TimeSpan Software) MeasureBackends(string html, int width, int height, int frames)
    {
        using var document = Document.Parse(html, Options);
        var (list, _) = document.Paint(width, height, 1, new HarfBuzzShaper(), 0);
        if (list is null) throw new InvalidOperationException("Benchmark paint did not produce a display list.");
        TimeSpan Median(IRasterBackend backend)
        {
            using var surface = backend.CreateSurface(width, height) ?? throw new InvalidOperationException("Benchmark backend could not create a raster surface.");
            var times = new List<TimeSpan>();
            for (var i = -3; i < frames; i++) // three warm-up replays
            {
                var start = Stopwatch.GetTimestamp();
                surface.Canvas.FillRoundedRect(new RoundedRect(new RectF(0, 0, width, height), default), new Paint(new Rgba(1, 1, 1, 1)));
                DisplayListPlayer.Replay(list, surface.Canvas, new RectF(0, 0, width, height));
                surface.ReadPixels(0, 0, 1, 1); // Skia draws lazily; reading a pixel waits for it
                if (i >= 0)
                    times.Add(Stopwatch.GetElapsedTime(start));
            }
            times.Sort();
            return times[times.Count / 2];
        }
        return (Median(new SkiaRasterBackend()), Median(SoftwareRasterBackend.Instance));
    }
#endif

    /// <summary>
    /// Hover restyle + repaint frame time (docs/study/18-memory-and-performance.md, M3 target 4 ms):
    /// moving pointer between cards toggles :hover state, runs Update() and rasters the display list.
    /// </summary>
    public static (TimeSpan Median, TimeSpan P95) MeasureHover(string html, int steps) => Steady(() => MeasureHoverPass(html, steps));

    private static (TimeSpan Median, TimeSpan P95) MeasureHoverPass(string html, int steps)
    {
        using var document = Document.Parse(html, Options);
        document.Paint(800, 600, 1, SoftwareRasterBackend.Instance.TextShaper, 0);
        using var surface = new SoftwareRasterSurface(800, 600);
        var canvas = surface.Canvas;
        Replay(canvas, document.DisplayList!, Viewport);
        var times = new List<TimeSpan>();
        for (var i = -10; i < steps; i++)
        {
            var start = Stopwatch.GetTimestamp();
            var pos = (i % 2 == 0) ? new System.Numerics.Vector2(100, 100) : new System.Numerics.Vector2(300, 100);
            document.Input.HandlePointerMove(new Folio.PointerEvent(pos));
            // HandlePointerMove updates the document when the hovered target changes. Do not call Update again:
            // measuring a second no-op repaint would double-count work that a real host never performs.
            if (document.DisplayList is { } list)
            {
                // Document.Update already computes LastDamage for the host paint path. Recomputing the
                // item-by-item diff here would benchmark the same work twice and would not match FolioView.
                var damage = document.LastDamage;
                if (damage is { } rect && rect.Width > 0 && rect.Height > 0)
                {
                    canvas.Save();
                    canvas.ClipRoundedRect(new RoundedRect(rect, default));
                    Replay(canvas, list, rect);
                    canvas.Restore();
                }
                else if (damage is not { Width: <= 0, Height: <= 0 })
                {
                    Replay(canvas, list, Viewport);
                }
            }
            if (i >= 0)
                times.Add(Stopwatch.GetElapsedTime(start));
        }
        times.Sort();
        return (times[times.Count / 2], times[Math.Min(times.Count - 1, (int)(times.Count * 0.95))]);
    }

    /// <summary>
    /// Scroll frame time (docs/study/18-memory-and-performance.md, M3 target 8 ms): a step of the scroll bar, 1 to 60
    /// device pixels, as <c>FolioView</c> does it. With <paramref name="retained"/> that is <see cref="RetainedScroll"/>:
    /// the rendered rows move and only the exposed ones are painted; without, the whole viewport is repainted (background fill included, as FolioView does; the row before this one filled none). Both rows include the surface-to-bitmap copy FolioView does after every changed frame.
    /// </summary>
    public static (TimeSpan Median, TimeSpan P95) MeasureScroll(string html, int steps, bool retained = true) => Steady(() => MeasureScrollPass(html, steps, retained));

    private static (TimeSpan Median, TimeSpan P95) MeasureScrollPass(string html, int steps, bool retained)
    {
        using var document = Document.Parse(html, Options);
        var (list, contentHeight) = document.Paint(800, 600, 1, SoftwareRasterBackend.Instance.TextShaper, 0);
        var scrollRange = (int)(contentHeight - Viewport.Height);
        if (scrollRange <= 60)
            throw new InvalidOperationException("The scroll fixture must extend beyond the viewport.");
        using var surface = new SoftwareRasterSurface(800, 600);
        RetainedScroll.Repaint(list!, surface, 1, 0, Background);
        var times = new List<TimeSpan>();
        var (rowBuffer, frame) = (new byte[800 * 4], new byte[800 * 4 * 600]);
        var (top, direction) = (0, 1);
        for (var i = -10; i < steps; i++)
        {
            var step = 1 + Math.Max(0, i) * 17 % 60;
            var next = top + direction * step;
            if (next < 0 || next > scrollRange)
            {
                direction = -direction;
                next = top + direction * step;
            }
            var start = Stopwatch.GetTimestamp();
            if (!retained)
                RetainedScroll.Repaint(list!, surface, 1, next, Background);
            else if (!RetainedScroll.TryScroll(list!, surface, 1, top, next, Background))
                throw new InvalidOperationException($"The scroll fixture no longer takes the retained path (step {top} -> {next}); the row would time a full repaint.");
            // FolioView then copies the surface into the bitmap it draws, swizzling every row.
            foreach (var row in Enumerable.Range(0, surface.Height))
            {
                surface.ReadBgraRow(row, rowBuffer);
                Buffer.BlockCopy(rowBuffer, 0, frame, row * rowBuffer.Length, rowBuffer.Length);
            }
            if (i >= 0)
                times.Add(Stopwatch.GetElapsedTime(start));
            top = next;
        }
        times.Sort();
        return (times[times.Count / 2], times[Math.Min(times.Count - 1, (int)(times.Count * 0.95))]);
    }

    /// <summary>
    /// Runs a measurement twice and discards it, then returns a third. Under .NET tiered compilation a short run mostly
    /// times unoptimised first-tier code (the typical frame reads ~37 ms on a first pass, ~8 ms on the third); the app
    /// hosting FolioView is a long-running JIT process, so steady state is what the budget is about.
    /// </summary>
    private static T Steady<T>(Func<T> pass)
    {
        pass();
        pass();
        return pass();
    }

    private static void Replay(ICanvas canvas, DisplayList list, RectF area)
    {
        canvas.FillRoundedRect(new RoundedRect(area, default), Background);
        DisplayListPlayer.Replay(list, canvas, area);
    }
}
