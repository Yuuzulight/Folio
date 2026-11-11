using System.Diagnostics;
using Folio.WinForms;

internal static class Program
{
    private const double MiB = 1024 * 1024;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1 || !Directory.Exists(args[0]))
        {
            Console.Error.WriteLine("Pass the conformance corpus directory containing S1 and S3.");
            return 2;
        }

        Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        using var window = new Form
        {
            ClientSize = new Size(1920, 1080),
            FormBorderStyle = FormBorderStyle.None,
            Text = "Folio static first-paint benchmark"
        };
        window.Show();
        window.Update();
        if (window.ClientSize != new Size(1920, 1080) || window.DeviceDpi != 96)
            throw new InvalidOperationException("The benchmark needs a 1920×1080 client area at 96 DPI.");
        using var process = Process.GetCurrentProcess();

        for (var warmup = 0; warmup < 3; warmup++)
            Measure(window, process, "<p>Warm up the renderer.</p>");

        var corpus = new List<Sample>();
        Console.WriteLine("Artifact | First paint (ms) | LoadHtml (ms) | OnPaint to Rendered (ms) | After-paint delta (MiB) | Post-GC delta (MiB)");
        foreach (var category in new[] { "S1", "S3" })
        {
            var files = Directory.GetFiles(Path.Combine(args[0], category), "index.html", SearchOption.AllDirectories);
            if (files.Length == 0)
                throw new InvalidOperationException($"No artifacts in {category}.");
            foreach (var path in files.Order(StringComparer.Ordinal))
            {
                var html = File.ReadAllText(path);
                var runs = new Sample[5];
                for (var index = 0; index < runs.Length; index++)
                    runs[index] = Measure(window, process, html);
                var sample = Summarize(runs, 0.5);
                corpus.Add(sample);
                Print($"{category}/{Path.GetFileName(Path.GetDirectoryName(path))}", sample);
            }
        }
        Print("Corpus median", Summarize(corpus, 0.5));
        Print("Corpus p95 (nearest rank)", Summarize(corpus, 0.95));
        return 0;
    }

    private static Sample Measure(Form window, Process process, string html)
    {
        Collect();
        var baseline = WorkingSet(process);
        double elapsed;
        double loadElapsed;
        double paintElapsed;
        long loaded;
        using (var view = new MeasuredFolioView { Dock = DockStyle.Fill })
        {
            window.Controls.Add(view);
            long finished = 0;
            long paintStarted = 0;
            Exception? failure = null;
            view.Rendered += (_, _) =>
            {
                if (finished != 0) return;
                finished = Stopwatch.GetTimestamp();
                paintStarted = view.PaintStarted;
            };
            view.RenderFailed += (_, error) => failure = error.Exception;
            var start = Stopwatch.GetTimestamp();
            view.LoadHtml(html);
            loadElapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            view.Update();
            if (failure is not null)
                throw new InvalidOperationException("The control failed to paint.", failure);
            if (finished == 0 || paintStarted == 0)
                throw new InvalidOperationException("The visible control did not paint the document.");
            elapsed = Stopwatch.GetElapsedTime(start, finished).TotalMilliseconds;
            paintElapsed = Stopwatch.GetElapsedTime(paintStarted, finished).TotalMilliseconds;
            loaded = WorkingSet(process);
        }
        Collect();
        return new Sample(elapsed, loadElapsed, paintElapsed, (loaded - baseline) / MiB, (WorkingSet(process) - baseline) / MiB);
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static long WorkingSet(Process process)
    {
        process.Refresh();
        return process.WorkingSet64;
    }

    private static Sample Summarize(IReadOnlyCollection<Sample> samples, double percentile)
    {
        double Select(IEnumerable<double> values)
        {
            var sorted = values.Order().ToArray();
            var rank = (int)Math.Ceiling(sorted.Length * percentile) - 1;
            return percentile == 0.5 && sorted.Length % 2 == 0
                ? (sorted[rank] + sorted[rank + 1]) / 2
                : sorted[rank];
        }

        return new Sample(Select(samples.Select(sample => sample.Milliseconds)),
            Select(samples.Select(sample => sample.LoadMilliseconds)), Select(samples.Select(sample => sample.PaintMilliseconds)),
            Select(samples.Select(sample => sample.LoadedMiB)), Select(samples.Select(sample => sample.RetiredMiB)));
    }

    private static void Print(string name, Sample sample) =>
        Console.WriteLine($"{name} | {sample.Milliseconds:F2} | {sample.LoadMilliseconds:F2} | {sample.PaintMilliseconds:F2} | {sample.LoadedMiB:F2} | {sample.RetiredMiB:F2}");

    private readonly record struct Sample(double Milliseconds, double LoadMilliseconds, double PaintMilliseconds,
        double LoadedMiB, double RetiredMiB);

    private sealed class MeasuredFolioView : FolioView
    {
        public long PaintStarted { get; private set; }

        protected override void OnPaint(PaintEventArgs e)
        {
            PaintStarted = Stopwatch.GetTimestamp();
            base.OnPaint(e);
        }
    }
}
