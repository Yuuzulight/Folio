using System.Diagnostics;
using System.Text;
using Folio.Css;
using Folio.Html;
using Folio.Layout;
using Folio.Painting;
using Folio.RenderTests;
using Folio.Style;

namespace Folio.Benchmarks;

/// <summary>Median time of each pipeline stage, and memory, for one document.</summary>
internal sealed record Measurement(string Name, int Length, TimeSpan[] Stages, long Allocated, long Retained)
{
    public static readonly string[] StageNames = ["Parse", "Style", "Boxes", "Layout", "Display list"];

    public TimeSpan Total => Stages.Aggregate(TimeSpan.Zero, (sum, stage) => sum + stage);
}

/// <summary>
/// Times the pipeline stages that exist so far over the benchmark documents, in a warm process, and compares them with
/// the targets in docs/study/18-memory-and-performance.md. Rasterising the first frame joins when it lands.
/// </summary>
internal static class Benchmark
{
    // Parse + style + layout + first paint of a typical artifact, and the memory it adds.
    public static readonly TimeSpan TimeTarget = TimeSpan.FromMilliseconds(50);
    public const long MemoryTarget = 30L * 1024 * 1024;

    private static readonly MediaContext Media = new(1920, 1080);

    /// <summary>
    /// The generated documents, then the conformance corpus (tests/conformance) and the private corpus
    /// (FOLIO_PRIVATE_CORPUS or tests/conformance-private) when present.
    /// </summary>
    public static IEnumerable<(string Name, string Html)> Documents()
    {
        yield return ("generated/report", Report());
        yield return ("generated/table-5000", Table(5000));

        var root = RepoPaths.Tests;
        var corpora = new (string Prefix, string? Folder)[]
        {
            ("", Path.Combine(root, "conformance")),
            ("private/", Environment.GetEnvironmentVariable("FOLIO_PRIVATE_CORPUS") ?? Path.Combine(root, "conformance-private")),
        };
        foreach (var (prefix, folder) in corpora)
        {
            if (!Directory.Exists(folder))
                continue;
            foreach (var file in Directory.GetFiles(folder, "index.html", SearchOption.AllDirectories).Order())
                yield return (prefix + Path.GetRelativePath(folder, Path.GetDirectoryName(file)!).Replace('\\', '/'), File.ReadAllText(file));
        }
    }

    public static Measurement Measure(string name, string html, int iterations)
    {
        var marks = new long[Measurement.StageNames.Length + 1];
        Run(html, marks); // Warm-up: JIT and the process-wide caches (user agent stylesheet, atoms).

        var times = Measurement.StageNames.Select(_ => new TimeSpan[iterations]).ToArray();
        for (var i = 0; i < iterations; i++)
        {
            Run(html, marks);
            for (var s = 0; s < times.Length; s++)
                times[s][i] = Stopwatch.GetElapsedTime(marks[s], marks[s + 1]);
        }

        var before = GC.GetTotalMemory(forceFullCollection: true);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var result = Run(html, marks);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var retained = GC.GetTotalMemory(forceFullCollection: true) - before;
        GC.KeepAlive(result);

        return new Measurement(name, html.Length, times.Select(Median).ToArray(), allocated, retained);
    }

    /// <summary>Runs the pipeline, recording a timestamp before the first stage and after each one.</summary>
    private static object Run(string html, long[] marks)
    {
        marks[0] = Stopwatch.GetTimestamp();
        var document = TreeBuilder.Parse(html);
        marks[1] = Stopwatch.GetTimestamp();
        StyleResolver.Resolve(document, Media);
        marks[2] = Stopwatch.GetTimestamp();
        var root = BoxTreeBuilder.Build(document);
        marks[3] = Stopwatch.GetTimestamp();
        var fragment = root is null ? null : LayoutEngine.LayoutDocument(root, Media.Width, Media.Height);
        marks[4] = Stopwatch.GetTimestamp();
        var displayList = fragment is null ? null : DisplayListBuilder.Build(fragment);
        marks[5] = Stopwatch.GetTimestamp();
        return (document, displayList);
    }

    private static TimeSpan Median(TimeSpan[] values) => values.Order().ElementAt(values.Length / 2);

    /// <summary>A report-style artifact: custom properties, flex and grid, headings, text, lists, code and a table.</summary>
    private static string Report()
    {
        var html = new StringBuilder("""
            <!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><title>Quarterly report</title><style>
            :root { --accent: #2563eb; --muted: #64748b; --radius: 8px; --gap: 16px; }
            * { box-sizing: border-box; }
            body { margin: 0; font: 16px/1.5 system-ui, sans-serif; color: #0f172a; background: #f8fafc; }
            header { display: flex; justify-content: space-between; align-items: center; padding: 24px 32px; background: var(--accent); color: white; }
            main { max-width: 960px; margin: 0 auto; padding: 32px; }
            section + section { margin-top: 32px; }
            h2 { font-size: 1.5rem; border-bottom: 2px solid var(--accent); padding-bottom: 4px; }
            .cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); gap: var(--gap); }
            .card { padding: 16px; border-radius: var(--radius); background: white; box-shadow: 0 1px 3px rgb(0 0 0 / 0.1); }
            .card strong { display: block; font-size: 2rem; color: var(--accent); }
            p.note { color: var(--muted); font-size: 0.875rem; }
            ul li::marker { color: var(--accent); }
            pre { padding: 12px; overflow: auto; background: #0f172a; color: #e2e8f0; border-radius: var(--radius); }
            table { width: 100%; border-collapse: collapse; }
            th, td { padding: 6px 12px; text-align: left; border-bottom: 1px solid #e2e8f0; }
            tbody tr:nth-child(even) { background: #f1f5f9; }
            a:hover { text-decoration: underline; }
            @media (max-width: 600px) { header { flex-direction: column; } .cards { grid-template-columns: 1fr; } }
            </style></head><body><header><h1>Quarterly report</h1><nav><a href="#s1">Summary</a> <a href="#s2">Details</a></nav></header><main>
            """);
        for (var s = 1; s <= 6; s++)
        {
            html.Append($"<section id=\"s{s}\"><h2>Section {s}</h2><div class=\"cards\">");
            for (var c = 1; c <= 4; c++)
                html.Append($"<div class=\"card\"><strong>{s * c * 7}%</strong><span>Metric {c}</span></div>");
            html.Append("</div>");
            for (var p = 0; p < 3; p++)
                html.Append("<p>Revenue grew in <em>every</em> region, led by <strong>online sales</strong> and <a href=\"#s1\">new markets</a>. Costs stayed <code>flat</code> while margins improved.</p>");
            html.Append("<ul>");
            for (var i = 1; i <= 5; i++)
                html.Append($"<li>Finding {i}: steady growth with <b>low</b> churn.</li>");
            html.Append("</ul><pre><code>const total = rows.reduce((sum, row) =&gt; sum + row.value, 0);</code></pre>");
            html.Append("<table><thead><tr><th>Region</th><th>Q1</th><th>Q2</th><th>Change</th></tr></thead><tbody>");
            for (var r = 1; r <= 10; r++)
                html.Append($"<tr><td>Region {r}</td><td>{r * 120}</td><td>{r * 131}</td><td>+{r}%</td></tr>");
            html.Append("</tbody></table><p class=\"note\">Figures are unaudited.</p></section>");
        }
        return html.Append("</main></body></html>").ToString();
    }

    /// <summary>A large data table, the corpus 95th percentile example in study 18.</summary>
    private static string Table(int rows)
    {
        var html = new StringBuilder("""
            <!DOCTYPE html><html><head><title>Data</title><style>
            table { border-collapse: collapse; font: 13px system-ui; }
            th { position: sticky; top: 0; background: #1e293b; color: white; }
            th, td { padding: 4px 8px; border: 1px solid #cbd5e1; }
            tr:nth-child(odd) td { background: #f8fafc; }
            td.num { text-align: right; font-variant-numeric: tabular-nums; }
            </style></head><body><table><thead><tr><th>#</th><th>Name</th><th>Category</th><th>Amount</th><th>Status</th></tr></thead><tbody>
            """);
        for (var r = 1; r <= rows; r++)
            html.Append($"<tr><td class=\"num\">{r}</td><td>Item {r}</td><td>Group {r % 12}</td><td class=\"num\">{r * 37 % 10000}.00</td><td>{(r % 3 == 0 ? "Open" : "Closed")}</td></tr>");
        return html.Append("</tbody></table></body></html>").ToString();
    }
}
