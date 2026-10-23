using System.Diagnostics;
using System.Text;
using Folio.Painting;
using Folio.RenderTests;
using Folio.Skia;
using Folio.Typography;
using SkiaSharp;

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

    public static string ScrollHtml => Typical;

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
    public static (TimeSpan Median, TimeSpan P95, bool PaintOnly) Measure(string html, int frames)
    {
        using var document = Document.Parse(html, Options);
        document.Paint(800, 600, 1, new HarfBuzzShaper(), 0);
        using var bitmap = new SKBitmap(new SKImageInfo(800, 600, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        var paintOnly = true;
        var times = new List<TimeSpan>();
        for (var i = -10; i < frames; i++) // ten warm-up frames
        {
            var start = Stopwatch.GetTimestamp();
            var time = (i + 10) / 60.0;
            var list = document.PaintFrame(time);
            paintOnly &= list is not null;
            list ??= document.Paint(800, 600, 1, new HarfBuzzShaper(), time).List;
            canvas.Clear(SKColors.White);
            DisplayListPlayer.Replay(list, new SkiaCanvas(canvas, false));
            canvas.Flush();
            if (i >= 0)
                times.Add(Stopwatch.GetElapsedTime(start));
        }
        times.Sort();
        return (times[times.Count / 2], times[Math.Min(times.Count - 1, (int)(times.Count * 0.95))], paintOnly);
    }

    /// <summary>
    /// What replaying one display list into a surface costs on each raster backend (#568, #575): the median of
    /// <paramref name="frames"/> replays of the typical dashboard's list, Skia and Folio's own software backend.
    /// </summary>
    public static (TimeSpan Skia, TimeSpan Software) MeasureBackends(string html, int width, int height, int frames)
    {
        using var document = Document.Parse(html, Options);
        var list = document.Paint(width, height, 1, new HarfBuzzShaper(), 0).List;
        TimeSpan Median(IRasterBackend backend)
        {
            using var surface = backend.CreateSurface(width, height) ?? throw new InvalidOperationException("The backend made no surface.");
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

    /// <summary>
    /// Hover restyle + repaint frame time (docs/study/18-memory-and-performance.md, M3 target 4 ms):
    /// moving pointer between cards toggles :hover state, runs Update() and rasters the display list.
    /// </summary>
    public static (TimeSpan Median, TimeSpan P95) MeasureHover(string html, int steps)
    {
        using var document = Document.Parse(html, Options);
        document.Paint(800, 600, 1, new HarfBuzzShaper(), 0);
        using var bitmap = new SKBitmap(new SKImageInfo(800, 600, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        var times = new List<TimeSpan>();
        for (var i = -10; i < steps; i++)
        {
            var start = Stopwatch.GetTimestamp();
            var pos = (i % 2 == 0) ? new System.Numerics.Vector2(100, 100) : new System.Numerics.Vector2(300, 100);
            document.Input.HandlePointerMove(new Folio.PointerEvent(pos));
            document.Update();
            canvas.Clear(SKColors.White);
            if (document.DisplayList is { } list)
                DisplayListPlayer.Replay(list, new SkiaCanvas(canvas, false));
            canvas.Flush();
            if (i >= 0)
                times.Add(Stopwatch.GetElapsedTime(start));
        }
        times.Sort();
        return (times[times.Count / 2], times[Math.Min(times.Count - 1, (int)(times.Count * 0.95))]);
    }

    /// <summary>
    /// Scroll frame time (docs/study/18-memory-and-performance.md, M3 target 8 ms):
    /// viewport scroll replays the display list with offset into viewport and rasters.
    /// </summary>
    public static (TimeSpan Median, TimeSpan P95) MeasureScroll(string html, int steps)
    {
        using var document = Document.Parse(html, Options);
        var (list, contentHeight) = document.Paint(800, 600, 1, new HarfBuzzShaper(), 0);
        using var bitmap = new SKBitmap(new SKImageInfo(800, 600, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        var times = new List<TimeSpan>();
        for (var i = -10; i < steps; i++)
        {
            var start = Stopwatch.GetTimestamp();
            var scrollTop = Math.Clamp(i * 10f, 0, Math.Max(0, contentHeight - 600));
            canvas.Clear(SKColors.White);
            canvas.Save();
            canvas.Translate(0, -scrollTop);
            DisplayListPlayer.Replay(list, new SkiaCanvas(canvas, false), new Folio.Painting.RectF(0, scrollTop, 800, 600));
            canvas.Restore();
            canvas.Flush();
            if (i >= 0)
                times.Add(Stopwatch.GetElapsedTime(start));
        }
        times.Sort();
        return (times[times.Count / 2], times[Math.Min(times.Count - 1, (int)(times.Count * 0.95))]);
    }
}
