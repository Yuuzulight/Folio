using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Folio.RefCapture;

/// <summary>
/// Captures the conformance references (docs/study/19-testing.md): <c>reference.png</c> from the primary reference
/// browser and, when configured, <c>reference-secondary.png</c> from the secondary one, next to each artifact's
/// <c>index.html</c>. Review the images before committing them; they never change because Folio changed.
/// </summary>
/// <remarks>
/// <para>Usage: <c>Folio.RefCapture [category|category/name]... [--config file]</c>.</para>
/// <para>Which browsers take part is local configuration, never part of the repo: by default
/// <c>tools/Folio.RefCapture/refcapture.local.json</c> (gitignored):</para>
/// <code>
/// {
///   "primary":   { "executable": "path to the browser",
///                  "measure":    ["arguments that load {page} headless in a {width}x{height} window and print the DOM after load"],
///                  "screenshot": ["arguments that load {page} headless in a {width}x{height} window at {scale} and save {output}"] },
///   "secondary": { ...the same, or leave it out }
/// }
/// </code>
/// <para>The browser must run headless, with <c>{profile}</c> as its profile folder: a capture never opens a window
/// or uses a profile of the person at the machine.</para>
/// <para>Placeholders: <c>{page}</c> (file URL of the prepared page), <c>{output}</c> (PNG path), <c>{width}</c>,
/// <c>{height}</c>, <c>{scale}</c>, <c>{profile}</c> (an empty, temporary profile folder).</para>
/// <para>Each artifact is captured under the fixed configuration Folio's side uses (TestRenderer): the manifest
/// viewport at scale 1, every family drawn with the bundled text font, then the box font for characters it lacks (no
/// synthesised bold or italic), animations and transitions settled, no scroll bars. The window is first as tall as three quarters of its
/// width, as in Folio's full-page render; the page height measured there becomes the window height of the
/// screenshot, and Folio lays out in a viewport as tall as the reference, so <c>vh</c> agrees on both sides.</para>
/// </remarks>
public static class Program
{
    private static readonly TimeSpan BrowserTimeout = TimeSpan.FromSeconds(90);
    private const double MaxMemoryLoad = 0.85;

    public static int Main(string[] args)
    {
        var root = FindRoot();
        var configIndex = Array.IndexOf(args, "--config");
        var configPath = configIndex >= 0 && configIndex + 1 < args.Length
            ? args[configIndex + 1]
            : Path.Combine(root, "tools", "Folio.RefCapture", "refcapture.local.json");
        var filters = args.Where((_, i) => configIndex < 0 || (i != configIndex && i != configIndex + 1)).ToArray();
        if (!File.Exists(configPath))
        {
            Console.Error.WriteLine($"No configuration at {configPath}; see the comment on Folio.RefCapture.Program for its format.");
            return 2;
        }

        var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(configPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException($"{configPath}: empty configuration.");
        var browsers = new (string File, Browser? Browser)[] { ("reference.png", config.Primary), ("reference-secondary.png", config.Secondary) }
            .Where(b => b.Browser is not null).ToArray();
        if (browsers.Any(b => !b.Browser!.Measure.Any(a => a.Contains("{profile}")) || !b.Browser.Screenshot.Any(a => a.Contains("{profile}"))))
        {
            Console.Error.WriteLine("Both argument lists must run the browser headless with {profile} as its profile folder, so a capture never touches a browser the user has open.");
            return 2;
        }
        var fonts = Path.Combine(root, "tests", "fonts");
        string Face(string family, string file, int weight, string style) =>
            $"@font-face {{ font-family: \"{family}\"; font-weight: {weight}; font-style: {style}; " +
            $"src: url(data:font/{(file.EndsWith(".otf") ? "otf" : "ttf")};base64,{Convert.ToBase64String(File.ReadAllBytes(Path.Combine(fonts, file)))}) " +
            $"format(\"{(file.EndsWith(".otf") ? "opentype" : "truetype")}\"); }}";
        var faces = string.Join('\n',
            Face("Source Sans 3", "SourceSans3-Regular.ttf", 400, "normal"),
            Face("Source Sans 3", "SourceSans3-Bold.ttf", 700, "normal"),
            Face("Source Sans 3", "SourceSans3-It.ttf", 400, "italic"),
            Face("Noto Color Emoji", "NotoColorEmoji.ttf", 400, "normal"),
            Face("Noto Sans JP", "NotoSansJP-Regular.otf", 400, "normal"),
            Face("Noto Sans JP", "NotoSansJP-Bold.otf", 700, "normal"),
            Face("Noto Sans SC", "NotoSansSC-Regular.otf", 400, "normal"),
            Face("Noto Sans SC", "NotoSansSC-Bold.otf", 700, "normal"),
            Face("Noto Sans Arabic", "NotoSansArabic-Regular.ttf", 400, "normal"),
            Face("Noto Sans Arabic", "NotoSansArabic-Bold.ttf", 700, "normal"),
            Face("Noto Sans Hebrew", "NotoSansHebrew-Regular.ttf", 400, "normal"),
            Face("Noto Sans Hebrew", "NotoSansHebrew-Bold.ttf", 700, "normal"),
            Face("Folio Box", "FolioBox.ttf", 400, "normal"));

        var corpus = Path.Combine(root, "tests", "conformance");
        var failures = 0;
        foreach (var index in Directory.GetFiles(corpus, "index.html", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var folder = Path.GetDirectoryName(index)!;
            var id = Path.GetRelativePath(corpus, folder).Replace('\\', '/');
            if (filters.Length > 0 && !filters.Any(f => id == f || id.StartsWith(f.TrimEnd('/') + "/", StringComparison.Ordinal)))
                continue;

            var width = Viewport(folder);
            var html = File.ReadAllText(index) + Injected(faces);
            foreach (var (file, browser) in browsers)
            {
                WaitForMemory();
                try
                {
                    var (w, h) = Capture(browser!, html, width, Path.Combine(folder, file));
                    Console.WriteLine($"{id}/{file}: {w}x{h}");
                }
                catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
                {
                    failures++;
                    Console.Error.WriteLine($"{id}/{file}: {e.Message}");
                }
            }
        }
        return failures == 0 ? 0 : 1;
    }

    private static (int Width, int Height) Capture(Browser browser, string html, int width, string destination)
    {
        var work = Directory.CreateTempSubdirectory("folio-refcapture-");
        try
        {
            var page = Path.Combine(work.FullName, "page.html");
            var profile = Directory.CreateDirectory(Path.Combine(work.FullName, "profile")).FullName;
            var output = Path.Combine(work.FullName, "shot.png");
            string[] Arguments(IEnumerable<string> template, int height) => template.Select(a => a
                .Replace("{page}", new Uri(page).AbsoluteUri).Replace("{output}", output).Replace("{profile}", profile)
                .Replace("{width}", width.ToString()).Replace("{height}", height.ToString()).Replace("{scale}", "1")).ToArray();

            File.WriteAllText(page, html + MeasureScript);
            var dom = Run(browser.Executable, Arguments(browser.Measure, width * 3 / 4));
            var match = Regex.Match(dom, "data-folio-height=\"(\\d+)\"");
            if (!match.Success)
                throw new InvalidOperationException("The measure run printed no page height; check the measure arguments.");
            var height = Math.Max(1, int.Parse(match.Groups[1].Value));

            File.WriteAllText(page, html);
            Run(browser.Executable, Arguments(browser.Screenshot, height));
            if (!File.Exists(output))
                throw new InvalidOperationException("The screenshot run saved no image; check the screenshot arguments.");
            File.Copy(output, destination, overwrite: true);
            return (width, height);
        }
        finally
        {
            try { work.Delete(recursive: true); }
            catch (IOException) { } // a browser helper process may still hold the profile for a moment
        }
    }

    private static string Run(string executable, string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {executable}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(BrowserTimeout))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"The browser did not finish within {BrowserTimeout.TotalSeconds} s.");
        }
        return stdout.Result;
    }

    // The fixed capture configuration; appended after </html>, so it lands at the end of the body and wins the cascade.
    // The families match Folio.RenderTests.Conformance.FontFamilies: the bundled text font, the emoji and script fonts
    // for characters it lacks, then the box font for anything left, so no system font is ever used.
    private static string Injected(string faces) => $$"""

        <style>
        {{faces}}
        *, *::before, *::after { font-family: "Source Sans 3", "Noto Color Emoji", "Noto Sans JP", "Noto Sans SC", "Noto Sans Arabic", "Noto Sans Hebrew", "Folio Box" !important; font-synthesis: none !important;
          animation-duration: 0s !important; animation-delay: 0s !important; transition: none !important; caret-color: transparent !important; }
        html { scrollbar-width: none !important; }
        </style>
        """;

    private const string MeasureScript = """

        <script>
        function folioMeasure() {
          var d = document.documentElement, b = document.body;
          d.setAttribute("data-folio-height", Math.ceil(Math.max(d.scrollHeight, b ? b.scrollHeight : 0)));
        }
        folioMeasure();
        addEventListener("load", folioMeasure);
        </script>
        """;

    private static int Viewport(string folder)
    {
        var manifest = Path.Combine(folder, "manifest.json");
        if (!File.Exists(manifest))
            return 800;
        using var json = JsonDocument.Parse(File.ReadAllText(manifest));
        return json.RootElement.TryGetProperty("viewport", out var viewport) ? viewport.GetInt32() : 800;
    }

    // Other work shares the machine; each browser run needs a few hundred MB.
    private static void WaitForMemory()
    {
        while (true)
        {
            GC.Collect();
            var info = GC.GetGCMemoryInfo();
            var load = (double)info.MemoryLoadBytes / info.TotalAvailableMemoryBytes;
            if (load < MaxMemoryLoad)
                return;
            Console.WriteLine($"Memory load {load:P0}; waiting.");
            Thread.Sleep(TimeSpan.FromSeconds(10));
        }
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Folio.slnx")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException($"No Folio.slnx above {AppContext.BaseDirectory}.");
    }

    private sealed record Browser(string Executable, string[] Measure, string[] Screenshot);

    private sealed record Config(Browser? Primary, Browser? Secondary);
}
