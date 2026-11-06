using System.Diagnostics;
using System.Drawing;
using Folio;
using Folio.Typography;
using Folio.Resources;
using Folio.WinForms;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var form = new Form { ClientSize = new Size(240, 90), ShowInTaskbar = false, Opacity = 0, Location = new Point(-32000, -32000) };
        using var view = new FolioView { Dock = DockStyle.Fill };
        if (view.Options.Fonts.Source is not BundledFontSource) throw new Exception("The default control has no bundled font source.");
        form.Controls.Add(view);
        form.Show();
        view.Options = new FolioOptions { ColorScheme = ColorScheme.Light };
        view.LoadHtml("<body style='margin:0;background:white;color:black'>Default view text</body>");
        PumpUntil(() => HasInk(view), "Default control text did not render.");
        Console.WriteLine("PASS: transitive Folio.WinForms package renders visible default text.");

        view.Options = new FolioOptions { ResourceInterceptor = new OfflineStyle() };
        view.LoadHtml("<link rel=stylesheet href='https://offline.invalid/style.css'><body style='margin:0;background:white'>Intercepted text</body>");
        PumpUntil(() => HasGreenText(view), "The public control dropped the host's resource interceptor.");
        Console.WriteLine("PASS: replacing options preserves default fonts and host resource interception.");

        var host = Path.Combine(AppContext.BaseDirectory, "Folio", "Folio.ContentHost.exe");
        if (!File.Exists(host)) return; // Native host is tested in the post-publish CI pass.
        view.Options = new FolioOptions { Fonts = BundledFontSource.Settings, Scripting = ScriptingPolicy.ContentProcess, ContentProcessPath = host };
        string? stopped = null;
        view.ScriptStopped += (_, e) => stopped = e.Reason;
        view.LoadHtml("<body style='margin:0'><canvas id=c width=240 height=90></canvas><script>var c=document.getElementById('c').getContext('2d');c.fillStyle='blue';c.fillRect(0,0,240,90);</script></body>");
        var children = new List<Process>();
        try
        {
            PumpUntil(() =>
            {
                if (stopped is not null) throw new Exception(stopped);
                using var bitmap = new Bitmap(240, 90);
                view.DrawToBitmap(bitmap, new Rectangle(0, 0, 240, 90));
                return bitmap.GetPixel(8, 8).ToArgb() == Color.Blue.ToArgb();
            }, "The deployed native host did not execute the canvas script.");
            children.AddRange(OwnedProcesses(host));
            if (children.Count != 1) throw new Exception($"Expected one owned content host, found {children.Count}.");
            view.Dispose();
            PumpUntil(() => children.All(p => p.HasExited), "Disposing the public control left an owned process running.");
            Console.WriteLine("PASS: deployed standalone host executes scripts and exits after control disposal.");
            CheckCapture(host);
        }
        finally
        {
            view.Dispose();
            if (children.Count == 0) children.AddRange(OwnedProcesses(host));
            foreach (var process in children)
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
                process.Dispose();
            }
        }
    }

    private static void CheckCapture(string host)
    {
        var oldProvider = new EmptyCapture();
        var currentProvider = new EmptyCapture();
        using var form = new Form { ClientSize = new Size(240, 90), ShowInTaskbar = false, Opacity = 0, Location = new Point(-32000, -32000) };
        using var view = new FolioView { Dock = DockStyle.Fill, Options = new FolioOptions
        {
            Scripting = ScriptingPolicy.ContentProcess, ContentProcessPath = host,
            MediaCaptureProvider = oldProvider, ArtifactId = "prewarm"
        } };
        form.Controls.Add(view);
        form.Show(); // Prewarms using the original services and artifact scope.
        var uiThread = Environment.CurrentManagedThreadId;
        var decisions = 0;
        var allow = true;
        view.Options = new FolioOptions
        {
            Scripting = ScriptingPolicy.ContentProcess, ContentProcessPath = host,
            MediaCaptureProvider = currentProvider, ArtifactId = "current",
            Capabilities = new FeatureCapabilities { Camera = true },
            MediaCapturePermission = _ =>
            {
                if (Environment.CurrentManagedThreadId != uiThread) throw new Exception("Permission callback is not on the UI thread.");
                decisions++;
                return allow;
            }
        };
        const string html = "<canvas id=c width=240 height=90></canvas><script>navigator.mediaDevices.getUserMedia({video:true}).catch(function(){var x=document.getElementById('c').getContext('2d');x.fillStyle='blue';x.fillRect(0,0,240,90);});</script>";
        bool Blue()
        {
            using var bitmap = new Bitmap(240, 90);
            view.DrawToBitmap(bitmap, new Rectangle(0, 0, 240, 90));
            return bitmap.GetPixel(15, 15).ToArgb() == Color.Blue.ToArgb();
        }
        try
        {
            view.LoadHtml(html);
            PumpUntil(() => decisions == 1 && Blue(), "The replacement capture services did not answer.");
            if (oldProvider.Opens != 0 || currentProvider.Opens != 1) throw new Exception("Prewarming retained the old capture provider.");
            allow = false;
            view.LoadHtml(html);
            PumpUntil(() => decisions == 2 && Blue(), "The UI permission callback was not consulted on reload.");
            if (currentProvider.Opens != 1) throw new Exception("A denied capture reached the provider.");
        }
        finally
        {
            view.Dispose();
            PumpUntil(() => { var children = OwnedProcesses(host); try { return children.Count == 0; } finally { children.ForEach(p => p.Dispose()); } }, "Capture checks left an owned process running.");
        }
        Console.WriteLine("PASS: prewarm service changes, UI-thread capture permission, denial and process cleanup.");
    }

    private sealed class EmptyCapture : IMediaCaptureProvider
    {
        internal int Opens;
        public IMediaCaptureSession? Open(MediaCaptureRequest request) { Opens++; return null; }
    }

    private sealed class OfflineStyle : IResourceInterceptor
    {
        public ResourceResponse? Intercept(ResourceRequest request) =>
            Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) && uri.Host == "offline.invalid"
                ? ResourceResponse.Loaded(System.Text.Encoding.UTF8.GetBytes("body { color: green }"), "text/css") : null;
    }

    private static List<Process> OwnedProcesses(string host)
    {
        var result = new List<Process>();
        foreach (var process in Process.GetProcessesByName("Folio.ContentHost"))
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, host, StringComparison.OrdinalIgnoreCase)) result.Add(process);
                else process.Dispose();
            }
            catch (System.ComponentModel.Win32Exception) { process.Dispose(); }
            catch (InvalidOperationException) { process.Dispose(); }
        }
        return result;
    }

    private static bool HasGreenText(FolioView view)
    {
        using var bitmap = new Bitmap(240, 90);
        view.DrawToBitmap(bitmap, new Rectangle(0, 0, 240, 90));
        for (var y = 0; y < 40; y++)
        for (var x = 0; x < 220; x++)
        {
            var c = bitmap.GetPixel(x, y);
            if (c.G > 70 && c.R < 60 && c.B < 60) return true;
        }
        return false;
    }

    private static bool HasInk(FolioView view)
    {
        using var bitmap = new Bitmap(240, 90);
        view.DrawToBitmap(bitmap, new Rectangle(0, 0, 240, 90));
        var ink = false;
        var paper = false;
        for (var y = 0; y < 40; y++)
        for (var x = 0; x < 220; x++)
        {
            var c = bitmap.GetPixel(x, y);
            ink |= c.R < 100 && c.G < 100 && c.B < 100;
            paper |= c.R > 240 && c.G > 240 && c.B > 240;
            if (ink && paper) return true;
        }
        return false;
    }

    private static void PumpUntil(Func<bool> ready, string failure)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(15))
        {
            Application.DoEvents();
            if (ready()) return;
            Thread.Sleep(10);
        }
        throw new Exception(failure);
    }
}
