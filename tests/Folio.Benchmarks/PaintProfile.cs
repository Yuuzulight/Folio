using System.Diagnostics;
using System.Globalization;
using Folio.Painting;
using Folio.RenderTests;
using Folio.Typography;

namespace Folio.Benchmarks;

/// <summary>
/// <c>paintprofile &lt;S3/artifact&gt; [loads]</c> parses, lays out and rasterizes one conformance artifact at 1920x1080 over and
/// over in this process and prints min / median / max for layout and raster: a loop to attach <c>dotnet-trace</c> to, or to
/// compare a rasterizer change before and after without the one-time JIT and glyph-cache cost of a first paint.
/// </summary>
internal static class PaintProfile
{
    public static int Run(string[] args)
    {
        var id = args.Length > 0 ? args[0] : "S3/server-inventory-wide";
        var loads = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 30;
        var html = File.ReadAllText(Path.Combine(RepoPaths.Tests, "conformance", id.Replace('/', Path.DirectorySeparatorChar), "index.html"));
        var options = new FolioOptions().WithDefaultFonts();
        var layout = new List<double>();
        var raster = new List<double>();
        for (var load = 0; load < loads; load++)
        {
            using var document = Document.Parse(html, options);
            var t0 = Stopwatch.GetTimestamp();
            var (list, _) = document.Paint(1920, 1080, 1, ComplexShaper.Instance, 0);
            var t1 = Stopwatch.GetTimestamp();
            using var surface = new SoftwareRasterSurface(1920, 1080);
            if (list is not null) RetainedScroll.Repaint(list, surface, 1, 0, new Paint(new Rgba(1, 1, 1, 1)));
            var t2 = Stopwatch.GetTimestamp();
            if (load == 0) continue;
            layout.Add(Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds);
            raster.Add(Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds);
        }
        string Spread(List<double> values) { var v = values.Order().ToList(); return string.Create(CultureInfo.InvariantCulture, $"{v[0],7:F2} {v[v.Count / 2],7:F2} {v[^1],7:F2}"); }
        Console.WriteLine($"{id}, {loads - 1} warm loads (ms): min / median / max");
        Console.WriteLine($"  layout {Spread(layout)}");
        Console.WriteLine($"  raster {Spread(raster)}");
        return 0;
    }
}
