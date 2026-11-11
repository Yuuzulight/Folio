using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Folio.RenderTests;
using Folio.Scripting.Ipc;

namespace Folio.Benchmarks;

public sealed record ScriptedMeasurement(
    string Name,
    TimeSpan FirstFrame,
    long WorkingSetBytes,
    int FrameSizeBytes,
    TimeSpan IpcLatency);

/// <summary>
/// Measures scripted document start-up, time to first frame through the content process,
/// content-process working set memory, frame payload size, and empirical script size limits.
/// Targets from study 18: warm first paint <= 50 ms, cold first paint <= 400 ms, working set <= 80 MB.
/// </summary>
public static class ScriptedProcessBenchmark
{
    public static readonly TimeSpan TargetWarmFirstPaint = TimeSpan.FromMilliseconds(50);
    public static readonly TimeSpan TargetColdFirstPaint = TimeSpan.FromMilliseconds(400);
    public const long TargetWorkingSet = 80L * 1024 * 1024; // 80 MB
    public const int ScriptSizeThreshold = 50 * 1024; // 50 KB

    public static async Task<int> RunAsync(string[]? args = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("The Windows content-process benchmark is unavailable on this platform.");
            return 0;
        }

        var prevUnsandboxed = Environment.GetEnvironmentVariable("FOLIO_UNSANDBOXED");
        Environment.SetEnvironmentVariable("FOLIO_UNSANDBOXED", null);
        try
        {
            return await RunCoreAsync(args);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FOLIO_UNSANDBOXED", prevUnsandboxed);
        }
    }

    private static async Task<int> RunCoreAsync(string[]? args)
    {
        var profile = args?.Contains("--profile", StringComparer.Ordinal) == true;
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Parent!.Name;
        var configuredHost = Environment.GetEnvironmentVariable("FOLIO_CONTENT_HOST");
        var host = configuredHost;
        if (string.IsNullOrWhiteSpace(host))
        {
            var hostRoot = Path.Combine(RepoPaths.Root, "tools", "Folio.ContentHost", "bin", configuration, "net10.0");
            // CI supplies FOLIO_CONTENT_HOST explicitly after publishing the
            // shipped Windows host. An unconfigured local run must use the
            // ordinary framework-dependent build: a stale apphost from an
            // earlier publish can fail before emitting a diagnostic (and make
            // the benchmark look like a content-process regression).
            host = Path.Combine(hostRoot, "Folio.ContentHost.dll");
            if (!File.Exists(host))
                host = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                    "../../../../../tools/Folio.ContentHost/bin", configuration, "net10.0/Folio.ContentHost.dll"));
        }
        else
        {
            host = Path.GetFullPath(host);
        }

        // On Windows, launch the apphost directly instead of spawning a new
        // dotnet host for every measurement. The apphost uses the same output
        // directory and dependencies, but removes the CLI bootstrap from the
        // cold first-frame path being measured.
        // Only use an apphost when the caller explicitly selected the shipped
        // publish layout (CI does this through FOLIO_CONTENT_HOST).  An
        // unconfigured local run must stay on the framework-dependent DLL:
        // stale apphosts in a developer bin directory can fail before the IPC
        // protocol emits its first diagnostic or frame.
        if (OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(configuredHost))
        {
            var appHost = Path.ChangeExtension(host, ".exe");
            if (File.Exists(appHost))
                host = appHost;
        }

        if (!File.Exists(host))
        {
            Console.Error.WriteLine($"Content host not found at {host}");
            return 1;
        }

        if (args?.Contains("--libraries", StringComparer.Ordinal) == true)
            return await MeasureLibrariesAsync(host);

        var table = new StringBuilder()
            .AppendLine("### Scripted Start-up, First Paint, and Content-Process Memory")
            .AppendLine()
            .AppendLine($"Targets: warm first frame <= {TargetWarmFirstPaint.TotalMilliseconds:F0} ms, cold first frame <= {TargetColdFirstPaint.TotalMilliseconds:F0} ms, working set <= {TargetWorkingSet / (1024 * 1024)} MB.")
            .AppendLine()
            .AppendLine("| Artifact | First frame ms | Working set MB | Frame payload bytes | IPC latency ms | Within targets |")
            .AppendLine("|---|--:|--:|--:|--:|---|");

        var warnings = new List<string>();

        // 1. Cold start benchmark
        var coldSw = Stopwatch.StartNew();
        await using (var coldClient = ContentProcessClient.Start(host))
        {
            var coldTcs = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            ValueTask OnMessage(ContentMessage msg, CancellationToken ct)
            {
                if (IsLoadResponse(msg))
                    coldTcs.TrySetResult(msg);
                return ValueTask.CompletedTask;
            }
            coldClient.MessageReceived += OnMessage;
            try
            {
                var sendSw = Stopwatch.StartNew();
                try
                {
                    await coldClient.SendAsync(ContentMessageKind.Load, new
                    {
                        html = "<!DOCTYPE html><title>Cold</title><p id=p>Cold test</p>",
                        profile,
                        script = "document.getElementById('p').textContent = 'Loaded';"
                    });
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"SendAsync error: {ex.Message}. Process exited: {coldClient.Process.HasExited}, ExitCode: {(coldClient.Process.HasExited ? coldClient.Process.ExitCode.ToString() : "running")}");
                    throw;
                }
                ContentMessage msg;
                try
                {
                    msg = await coldTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    ValidateFrame(msg, coldClient);
                    if (profile) Console.WriteLine($"PROFILE cold: {msg.Payload.GetProperty("timings")}");
                }
                catch (TimeoutException error)
                {
                    var state = coldClient.Process.HasExited
                        ? $"exited ({coldClient.Process.ExitCode})"
                        : "still running";
                    throw new TimeoutException($"Cold content host produced no frame; process is {state}. stderr: {coldClient.StandardError}", error);
                }
                var coldElapsed = coldSw.Elapsed;
                var sendElapsed = sendSw.Elapsed;
                coldClient.Process.Refresh();
                var ws = coldClient.Process.WorkingSet64;
                var frameSize = msg.Payload.ToString().Length;
                table.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| [cold start] initial bootstrap | {coldElapsed.TotalMilliseconds:F1} | {ws / (1024.0 * 1024):F1} | {frameSize} | {sendElapsed.TotalMilliseconds:F1} | {(coldElapsed <= TargetColdFirstPaint && ws <= TargetWorkingSet ? "yes" : "no")} |"));
                if (coldElapsed > TargetColdFirstPaint)
                    warnings.Add($"Cold start time ({coldElapsed.TotalMilliseconds:F1} ms) exceeded target ({TargetColdFirstPaint.TotalMilliseconds:F0} ms)");
                if (ws > TargetWorkingSet) warnings.Add("Cold start working set exceeded target.");
            }
            finally
            {
                coldClient.MessageReceived -= OnMessage;
            }
        }

        // 2. Warm D1 artifacts benchmark
        var d1Folder = Path.Combine(RepoPaths.Tests, "conformance", "D1");
        if (Directory.Exists(d1Folder))
        {
            var d1Artifacts = Directory.GetDirectories(d1Folder).OrderBy(d => d).ToArray();
            await using var warmClient = ContentProcessClient.Start(host);

            // Prime the process before measuring the warm corpus. The first
            // message necessarily includes process startup and is the cold
            // lifecycle measured above; counting it as a D1 warm load makes the
            // per-artifact warm target depend on process-launch contention.
            for (var w = 0; w < 2; w++)
            {
                var warmupTcs = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                ValueTask OnWarmup(ContentMessage m, CancellationToken ct)
                {
                    if (IsLoadResponse(m))
                        warmupTcs.TrySetResult(m);
                    return ValueTask.CompletedTask;
                }
                warmClient.MessageReceived += OnWarmup;
                try
                {
                    await warmClient.SendAsync(ContentMessageKind.Load, new
                    {
                        html = @"<!DOCTYPE html><title>Warm-up</title>
<style>
:root { --c: #000; }
body { font-family: system-ui, -apple-system, sans-serif; }
h1 { font-size: 24px; font-weight: bold; }
.sub { font-size: 14px; color: #64748b; }
.bld { font-weight: 600; }
.f { display: flex; color: var(--c); margin: 8px; padding: 4px; border: 1px solid #ccc; border-radius: 4px; gap: 4px; box-sizing: border-box; font: 14px sans-serif; line-height: 1.5; outline: none; }
.g { display: grid; grid-template-columns: 1fr 1fr; }
table { border-collapse: collapse; width: 100%; background: #fff; border: 1px solid #e6e8ee; border-radius: 12px; overflow: hidden; box-shadow: 0 1px 3px rgba(0, 0, 0, 0.04); }
th, td { text-align: left; padding: 11px 16px; border-bottom: 1px solid #e6e8ee; font-size: 13px; }
th { font-weight: 600; text-transform: uppercase; letter-spacing: 0.06em; white-space: nowrap; }
.num { text-align: right; font-variant-numeric: tabular-nums; }
.dept { display: inline-block; font-size: 11px; font-weight: 600; padding: 2px 8px; border-radius: 999px; background: #f1f5f9; color: #475569; text-transform: uppercase; letter-spacing: 0.04em; }
.btn[aria-expanded='true'] .chv { transform: rotate(45deg); transition: transform 0.2s; }
.person { display: flex; align-items: center; gap: 8px; }
.avatar { width: 28px; height: 28px; border-radius: 50%; display: grid; place-items: center; font-size: 11px; font-weight: 700; color: #fff; }
</style>
<h1>Product Architecture and Warm-up</h1>
<p class='sub'>Explore core modules and deployment capabilities with realistic text paragraphs and multi-word sentences.</p>
<div class='f'><button id='b' class='btn acc-header bld' aria-expanded='true' aria-controls='p1'><span class='chv'>warm</span></button></div>
<div class='g'><div>g1</div><div>g2</div></div>
<select id='dept'><option value=''>All</option></select>
<table>
  <thead><tr><th>Name</th><th>Role</th><th>Dept</th><th>City</th><th>Start</th><th>Projects</th></tr></thead>
  <tbody id='rows'><tr><td><div class='person'><span class='avatar' style='background:#2563eb'>AB</span>Alice Bob</div></td><td>Lead</td><td><span class='dept'>Engineering</span></td><td>Rotterdam</td><td class='num'>Jan 2020</td><td class='num'>10</td></tr></tbody>
</table>
<div id='p1' hidden>content</div>
<script>
document.body.dataset.warm = '1';
document.getElementById('b')?.addEventListener('click', () => {});
const buttons = document.querySelectorAll('.acc-header');
buttons.forEach(btn => {
  btn.getAttribute('aria-expanded');
  btn.setAttribute('aria-expanded', 'false');
  document.getElementById(btn.getAttribute('aria-controls'));
});
const depts = ['Engineering', 'Design', 'Sales', 'Finance', 'Operations'];
[...new Set(depts)].sort().forEach(d => {
  const opt = document.createElement('option');
  opt.value = d;
  opt.textContent = d;
  document.getElementById('dept').appendChild(opt);
});
const cities = ['Rotterdam', 'Lyon', 'Bologna', 'Tallinn', 'Busan'];
const mockPeople = [];
for (let i = 0; i < 20; i++) {
  mockPeople.push({
    name: 'Person ' + i + ' Name',
    role: 'Engineer Role ' + i,
    dept: depts[i % 5],
    city: cities[i % 5],
    start: '202' + (i % 5) + '-0' + ((i % 9) + 1) + '-15',
    projects: i + 1
  });
}
function esc(s) { return String(s).replace(/[&<>""']/g, c => c); }
function initials(name) { return name.split(' ').map(w => w[0]).join('').slice(0, 2); }
function fmtDate(iso) { const [y, m] = iso.split('-'); return m + '/' + y; }
document.getElementById('rows').innerHTML = mockPeople.map(p => `<tr><td><div class=""person""><span class=""avatar"" style=""background:#2563eb"">${initials(p.name)}</span>${esc(p.name)}</div></td><td>${esc(p.role)}</td><td><span class=""dept"">${esc(p.dept)}</span></td><td>${esc(p.city)}</td><td class=""num"">${fmtDate(p.start)}</td><td class=""num"">${p.projects}</td></tr>`).join('');
const arr = [{x: 1, y: 'a'}, {x: 2, y: 'b'}];
arr.filter(a => a.x > 0).sort((a, b) => a.y.localeCompare(b.y)).map(a => `<span>${a.y}</span>`).join('');
mockPeople.filter(p => [p.name, p.role].some(v => v.toLowerCase().includes('e'))).sort((a, b) => a.name.localeCompare(b.name));
</script>"
                    });
                    ValidateFrame(await warmupTcs.Task.WaitAsync(TimeSpan.FromSeconds(15)), warmClient);
                }
                finally
                {
                    warmClient.MessageReceived -= OnWarmup;
                }
            }

            foreach (var artDir in d1Artifacts)
            {
                var artName = Path.GetFileName(artDir);
                var indexPath = Path.Combine(artDir, "index.html");
                if (!File.Exists(indexPath)) continue;
                var html = await File.ReadAllTextAsync(indexPath);

                var frameTcs = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                ValueTask OnFrame(ContentMessage m, CancellationToken ct)
                {
                    if (IsLoadResponse(m))
                        frameTcs.TrySetResult(m);
                    return ValueTask.CompletedTask;
                }
                warmClient.MessageReceived += OnFrame;
                try
                {
                    var sw = Stopwatch.StartNew();
                    await warmClient.SendAsync(ContentMessageKind.Load, new { html, profile });
                    var frame = await frameTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    ValidateFrame(frame, warmClient);
                    var elapsed = sw.Elapsed;
                    if (profile) Console.WriteLine($"PROFILE D1/{artName}: {frame.Payload.GetProperty("timings")}");
                    warmClient.Process.Refresh();
                    var ws = warmClient.Process.WorkingSet64;
                    var frameSize = frame.Payload.ToString().Length;
                    var within = elapsed <= TargetWarmFirstPaint && ws <= TargetWorkingSet;
                    table.AppendLine(string.Create(CultureInfo.InvariantCulture,
                        $"| D1/{artName} | {elapsed.TotalMilliseconds:F1} | {ws / (1024.0 * 1024):F1} | {frameSize} | not measured | {(within ? "yes" : "no")} |"));
                    if (ws > TargetWorkingSet)
                        warnings.Add($"D1/{artName} working set ({ws / (1024 * 1024)} MB) exceeded target ({TargetWorkingSet / (1024 * 1024)} MB)");
                    if (elapsed > TargetWarmFirstPaint) warnings.Add($"D1/{artName} first frame exceeded {TargetWarmFirstPaint.TotalMilliseconds} ms.");
                }
                finally
                {
                    warmClient.MessageReceived -= OnFrame;
                }
            }
        }

        // 3. Script size threshold calibration table
        table.AppendLine().AppendLine("### Script Size Threshold Calibration (#482)")
            .AppendLine()
            .AppendLine("Evaluates script parsing + initial execution scaling against the 50 ms first paint budget.")
            .AppendLine()
            .AppendLine("| Script text size | Parse + execute ms | Within 50 ms budget | Classification |")
            .AppendLine("|---|--:|---|---|");

        int[] sampleSizes = [1 * 1024, 10 * 1024, 30 * 1024, 50 * 1024, 100 * 1024, 250 * 1024];
        await using var sizeClient = ContentProcessClient.Start(host);

        // Do not charge the calibration table for the process/JIT bootstrap.
        // The first measured row is meant to represent parse + execute scaling,
        // so prime this dedicated client with the same frame path first.
        var calibrationWarmup = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask OnCalibrationWarmup(ContentMessage message, CancellationToken cancellationToken)
        {
            if (IsLoadResponse(message))
                calibrationWarmup.TrySetResult(message);
            return ValueTask.CompletedTask;
        }
        sizeClient.MessageReceived += OnCalibrationWarmup;
        try
        {
            await sizeClient.SendAsync(ContentMessageKind.Load, new
            {
                html = "<!DOCTYPE html><title>Calibration warm-up</title><body>Warm-up</body>",
                script = "document.body.dataset.warm = '1';"
            });
            ValidateFrame(await calibrationWarmup.Task.WaitAsync(TimeSpan.FromSeconds(15)), sizeClient);
        }
        finally
        {
            sizeClient.MessageReceived -= OnCalibrationWarmup;
        }

        foreach (var size in sampleSizes)
        {
            var varCount = size / 25;
            var scriptBuilder = new StringBuilder();
            for (var i = 0; i < varCount; i++)
                scriptBuilder.Append($"var a{i} = {i} * 2; ");
            var script = scriptBuilder.ToString();

            var frameTcs = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            ValueTask OnFrame(ContentMessage m, CancellationToken ct)
            {
                if (IsLoadResponse(m))
                    frameTcs.TrySetResult(m);
                return ValueTask.CompletedTask;
            }
            sizeClient.MessageReceived += OnFrame;
            try
            {
                var sw = Stopwatch.StartNew();
                await sizeClient.SendAsync(ContentMessageKind.Load, new
                {
                    html = "<!DOCTYPE html><title>Size</title><body>Test</body>",
                    script
                });
                ValidateFrame(await frameTcs.Task.WaitAsync(TimeSpan.FromSeconds(15)), sizeClient);
                var elapsed = sw.Elapsed;
                var withinBudget = elapsed <= TargetWarmFirstPaint;
                var classification = size <= ScriptSizeThreshold ? "Scripted (Runs in ContentProcess)" : "NeedsBrowser (Exceeds 50 KB threshold)";
                table.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {size / 1024} KB | {elapsed.TotalMilliseconds:F1} | {(withinBudget ? "yes" : "no")} | {classification} |"));
            }
            finally
            {
                sizeClient.MessageReceived -= OnFrame;
            }
        }

        Console.WriteLine(table);
        foreach (var w in warnings)
            Console.WriteLine($"::warning::{w}");

        if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary)
            await File.AppendAllTextAsync(summary, "\n" + table);

        return warnings.Count == 0 ? 0 : 1;
    }

    private static bool IsLoadResponse(ContentMessage message) => message.Kind == ContentMessageKind.Diagnostic ||
        (message.Kind == ContentMessageKind.Frame && message.Payload.TryGetProperty("sequence", out var sequence) && sequence.GetInt64() > 0);

    private static async Task<int> MeasureLibrariesAsync(string host)
    {
        var problems = ConformanceCache.Problems();
        if (problems.Count != 0) throw new InvalidDataException(string.Join(Environment.NewLine, problems));
        var cache = ConformanceCache.Entries().ToDictionary(entry => entry.Url, StringComparer.Ordinal);
        var globals = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["chart.umd.min.js"] = "Chart", ["phaser.min.js"] = "Phaser", ["twind-shim.min.js"] = "twind",
            ["babel.min.js"] = "Babel", ["d3.min.js"] = "d3", ["react-dom.production.min.js"] = "ReactDOM",
            ["react.production.min.js"] = "React",
        };
        var policy = new ContentResourcePolicy
        {
            AllowedOrigins = cache.Values.Select(entry => new Uri(entry.Url).GetLeftPart(UriPartial.Authority))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
        };
        await using var client = ContentProcessClient.Start(host, resourceLoader: ConformanceCache.Loader, resourcePolicy: policy);
        async Task<ContentMessage> Load(string html, string script)
        {
            var response = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            ValueTask OnMessage(ContentMessage message, CancellationToken cancellationToken)
            {
                if (IsLoadResponse(message)) response.TrySetResult(message);
                return ValueTask.CompletedTask;
            }
            client.MessageReceived += OnMessage;
            try
            {
                await client.SendAsync(ContentMessageKind.Load, new { html, script });
                return await response.Task.WaitAsync(TimeSpan.FromSeconds(15));
            }
            finally { client.MessageReceived -= OnMessage; }
        }

        ValidateFrame(await Load("<!doctype html><p>Warm up the content process.</p>", ""), client);
        Console.WriteLine("| Artifact | First frame ms | Working set MiB | Result |");
        Console.WriteLine("|---|---:|---:|---|");
        var measured = 0;
        var failed = 0;
        var unqualified = 0;
        for (var category = 2; category <= 7; category++)
        {
            var directory = Path.Combine(RepoPaths.Tests, "conformance", $"D{category}");
            foreach (var path in Directory.EnumerateFiles(directory, "index.html", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var html = await File.ReadAllTextAsync(path);
                using var document = Document.Parse(html);
                var urls = document.QuerySelectorAll("script[src]").Select(script => script.GetAttribute("src")!).Distinct(StringComparer.Ordinal).ToArray();
                if (urls.Length == 0) continue;
                var id = $"D{category}/{Path.GetFileName(Path.GetDirectoryName(path))}";
                if (urls.Any(url => !cache.ContainsKey(url)))
                {
                    Console.WriteLine($"| {id} | — | — | Unavailable in the pinned cache |");
                    unqualified++;
                    continue;
                }
                var libraries = urls.Select(url => cache[url].File).ToArray();
                if (libraries.Contains("mermaid.min.js", StringComparer.Ordinal))
                {
                    Console.WriteLine($"| {id} | — | — | Diagram stand-in qualification pending |");
                    unqualified++;
                    continue;
                }
                if (libraries.Any(file => !globals.ContainsKey(file)))
                    throw new InvalidDataException($"{id} has a pinned library without an execution probe.");

                var probe = string.Join(" && ", libraries.Select(file => $"typeof globalThis.{globals[file]} !== 'undefined'"));
                var start = Stopwatch.GetTimestamp();
                var frame = await Load(html, probe);
                var elapsed = Stopwatch.GetElapsedTime(start);
                client.Process.Refresh();
                var workingSet = client.Process.WorkingSet64;
                measured++;
                try
                {
                    var chart = libraries.Contains("chart.umd.min.js", StringComparer.Ordinal);
                    ValidateFrame(frame, client, chart);
                    if (frame.Payload.GetProperty("value").GetString() != "true")
                        throw new InvalidDataException("A pinned library did not initialize.");
                    if (chart && frame.Payload.GetProperty("canvasCommands").GetInt32() == 0)
                        throw new InvalidDataException("The chart did not create a canvas surface.");
                    var within = elapsed <= TargetWarmFirstPaint && workingSet <= TargetWorkingSet;
                    if (!within) failed++;
                    Console.WriteLine(FormattableString.Invariant($"| {id} | {elapsed.TotalMilliseconds:F1} | {workingSet / (1024.0 * 1024):F1} | {(within ? "Pass" : "Over budget")} |"));
                }
                catch (InvalidDataException error)
                {
                    failed++;
                    Console.WriteLine($"| {id} | — | — | {error.Message.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ')} |");
                }
            }
        }
        Console.WriteLine($"Measured {measured} pinned-library artifacts. Failures: {failed}. Unqualified: {unqualified}.");
        return measured > 0 && failed == 0 && unqualified == 0 ? 0 : 1;
    }

    private static void ValidateFrame(ContentMessage message, ContentProcessClient client, bool allowChartBridge = false)
    {
        if (message.Kind != ContentMessageKind.Frame) throw new InvalidDataException($"Content host failed: {message.Payload}");
        if (message.Payload.TryGetProperty("diagnostics", out var diagnostics) && diagnostics.EnumerateArray().Any(diagnostic =>
            !allowChartBridge || diagnostic.GetString() != "Replaced the configured chart library with Folio's native canvas chart bridge."))
            throw new InvalidDataException($"Content diagnostics: {diagnostics}");
        using var store = new CanvasFrameStore(client.FramePath, 1280, 720, 1280 * 4);
        var pixels = store.ReadLatest(out var sequence);
        if (sequence <= 0 || !pixels.Any(value => value != 0)) throw new InvalidDataException("Content host did not publish rendered pixels.");
    }
}
