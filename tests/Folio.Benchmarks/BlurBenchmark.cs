using System.Diagnostics;
using System.Globalization;
using Folio.Painting;
using Folio.Painting.Software;
using Folio.Typography;

namespace Folio.Benchmarks;

/// <summary>
/// <c>blur [runs]</c>: the cost of Gaussian blur on its own and in paint. Each row is the median of <c>runs</c> timed runs after
/// two discarded ones, in ms: direct <c>GaussianBlur.Apply</c> calls on a noisy RGBA image (so no repeated-line shortcut applies)
/// at several sigmas, and the paint (display list to raster, 1920x1080) of pages built around box-shadow, text-shadow and
/// filter: blur. Run it once per build and compare the rows; the process prints one line per row (<c>ROW name ms</c>).
/// </summary>
internal static class BlurBenchmark
{
    public static int Run(int runs)
    {
        var options = new FolioOptions().WithDefaultFonts();
        var rng = new Random(7);
        foreach (var (w, h, sigma) in new[] { (512, 512, 2f), (512, 512, 4f), (512, 512, 8f), (512, 512, 16f), (1920, 1080, 8f) })
        {
            var image = new byte[w * h * 4];
            rng.NextBytes(image);
            Row($"apply {w}x{h} sigma {sigma:F0}", runs, () => GaussianBlur.Apply(image, w, h, 4, sigma, sigma));
        }
        foreach (var (name, html) in Pages())
        {
            using var document = Document.Parse(html, options);
            var (list, _) = document.Paint(1920, 1080, 1, ComplexShaper.Instance, 0);
            using var surface = new SoftwareRasterSurface(1920, 1080);
            var paint = new Paint(new Rgba(1, 1, 1, 1));
            Row($"paint {name}", runs, () => RetainedScroll.Repaint(list!, surface, 1, 0, paint));
        }
        return 0;
    }

    private static void Row(string name, int runs, Action work)
    {
        work();
        work();
        var times = new double[runs];
        for (var i = 0; i < runs; i++)
        {
            GC.Collect();
            var start = Stopwatch.GetTimestamp();
            work();
            times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        Array.Sort(times);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"ROW {name} | {times[runs / 2]:F2}"));
    }

    private static IEnumerable<(string, string)> Pages()
    {
        var cards = string.Concat(Enumerable.Range(0, 24).Select(i =>
            $"<div style=\"display:inline-block;width:220px;height:120px;margin:16px;background:#fff;border-radius:12px;box-shadow:0 8px 32px rgba(0,0,0,.25), 0 2px 6px rgba(20,40,200,.3)\">card {i}</div>"));
        yield return ("box-shadow cards", $"<body style=\"background:#eef\">{cards}</body>");
        var text = string.Concat(Enumerable.Range(0, 12).Select(i =>
            $"<h2 style=\"margin:8px;font-size:40px;color:#fff;text-shadow:0 0 8px #06f, 2px 2px 10px #000\">Shadowed heading number {i}</h2>"));
        yield return ("text-shadow headings", $"<body style=\"background:#223\">{text}</body>");
        var filters = string.Concat(Enumerable.Range(0, 6).Select(i =>
            $"<div style=\"display:inline-block;width:300px;height:200px;margin:12px;background:linear-gradient(135deg,#f06,#09f);filter:blur({3 + i * 2}px)\"><p style=\"color:#fff;font-size:32px\">Blur {i}</p></div>"));
        yield return ("filter blur", $"<body>{filters}</body>");
    }
}
