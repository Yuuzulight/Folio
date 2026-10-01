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
/// replay it into an 800x600 viewport.
/// </summary>
internal static class FrameBenchmark
{
    public static readonly TimeSpan Target = TimeSpan.FromMilliseconds(8);

    private static readonly FolioOptions Options = new()
    {
        Fonts = new FontSettings
        {
            Source = new FontFolderSource(Path.Combine(RepoPaths.Tests, "fonts")),
            GenericFamilies = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase) { ["sans-serif"] = ["Source Sans 3", "Folio Box"] },
        },
    };

    public static string Html()
    {
        var cards = new StringBuilder();
        for (var i = 0; i < 24; i++)
        {
            cards.Append($"<div class=card style='animation-delay: {-i * 0.13:0.00}s'><h3>Metric {i + 1}</h3><p class=value>{(i * 7919) % 10000:N0}</p>");
            cards.Append("<div class=bars>");
            for (var b = 0; b < 6; b++)
                cards.Append($"<i style='height: {20 + (i * 13 + b * 29) % 60}px; animation-delay: {-b * 0.2:0.0}s'></i>");
            cards.Append("</div><span class=badge>live</span></div>");
        }
        return $$"""
            <!DOCTYPE html><style>
            body { margin: 0; padding: 20px; font-family: sans-serif; background: #f4f6fb; }
            .grid { display: grid; grid-template-columns: repeat(4, 1fr); gap: 14px; }
            .card { position: relative; background: #fff; border-radius: 12px; padding: 12px; box-shadow: 0 2px 8px rgba(0,0,0,.08);
                    animation: float 3s ease-in-out infinite alternate; }
            .value { font-size: 22px; font-weight: 700; margin: 4px 0; }
            .bars { display: flex; align-items: flex-end; gap: 4px; height: 80px; }
            .bars i { flex: 1; background: #5b5bd6; border-radius: 3px 3px 0 0; transform-origin: bottom; animation: pulse 1.2s linear infinite alternate; }
            .badge { position: absolute; top: 10px; right: 10px; padding: 1px 6px; border-radius: 9px; color: #fff; font-size: 11px;
                     animation: glow 2s infinite; }
            @keyframes float { from { transform: translateY(0) rotate(0deg); opacity: .85 } to { transform: translateY(-6px) rotate(.5deg); opacity: 1 } }
            @keyframes pulse { from { transform: scaleY(.4); filter: saturate(.5) } to { transform: scaleY(1); filter: saturate(1.2) } }
            @keyframes glow { 0%, 100% { background-color: #16a36a; box-shadow: 0 0 0 0 rgba(22,163,106,.5) } 50% { background-color: #e0445a; box-shadow: 0 0 0 6px rgba(22,163,106,0) } }
            </style><div class=grid>{{cards}}</div>
            """;
    }

    /// <summary>The median and 95th percentile frame times, and whether every frame took the paint-only path.</summary>
    public static (TimeSpan Median, TimeSpan P95, bool PaintOnly) Measure(int frames)
    {
        using var document = Document.Parse(Html(), Options);
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
}
