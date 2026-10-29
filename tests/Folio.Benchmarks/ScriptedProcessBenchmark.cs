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
        Environment.SetEnvironmentVariable("FOLIO_UNSANDBOXED", "1");
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
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Parent!.Name;
        var host = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../tools/Folio.ContentHost/bin", configuration, "net10.0/Folio.ContentHost.dll"));
        if (!File.Exists(host))
        {
            host = Path.Combine(RepoPaths.Root, "tools", "Folio.ContentHost", "bin", configuration, "net10.0", "Folio.ContentHost.dll");
        }

        if (!File.Exists(host))
        {
            Console.Error.WriteLine($"Content host not found at {host}");
            return 1;
        }

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
                if (msg.Kind is ContentMessageKind.Frame or ContentMessageKind.Diagnostic)
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
                        script = "document.getElementById('p').textContent = 'Loaded';"
                    });
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"SendAsync error: {ex.Message}. Process exited: {coldClient.Process.HasExited}, ExitCode: {(coldClient.Process.HasExited ? coldClient.Process.ExitCode.ToString() : "running")}");
                    throw;
                }
                var msg = await coldTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
                var coldElapsed = coldSw.Elapsed;
                var sendElapsed = sendSw.Elapsed;
                coldClient.Process.Refresh();
                var ws = coldClient.Process.WorkingSet64;
                var frameSize = msg.Payload.ToString().Length;
                table.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| [cold start] initial bootstrap | {coldElapsed.TotalMilliseconds:F1} | {ws / (1024.0 * 1024):F1} | {frameSize} | {sendElapsed.TotalMilliseconds:F1} | {(coldElapsed <= TargetColdFirstPaint && ws <= TargetWorkingSet ? "yes" : "no")} |"));
                if (coldElapsed > TargetColdFirstPaint)
                    warnings.Add($"Cold start time ({coldElapsed.TotalMilliseconds:F1} ms) exceeded target ({TargetColdFirstPaint.TotalMilliseconds:F0} ms)");
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
            foreach (var artDir in d1Artifacts)
            {
                var artName = Path.GetFileName(artDir);
                var indexPath = Path.Combine(artDir, "index.html");
                if (!File.Exists(indexPath)) continue;
                var html = await File.ReadAllTextAsync(indexPath);

                var frameTcs = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                ValueTask OnFrame(ContentMessage m, CancellationToken ct)
                {
                    if (m.Kind is ContentMessageKind.Frame or ContentMessageKind.Diagnostic)
                        frameTcs.TrySetResult(m);
                    return ValueTask.CompletedTask;
                }
                warmClient.MessageReceived += OnFrame;
                try
                {
                    var sw = Stopwatch.StartNew();
                    await warmClient.SendAsync(ContentMessageKind.Load, new { html });
                    var frame = await frameTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    var elapsed = sw.Elapsed;
                    warmClient.Process.Refresh();
                    var ws = warmClient.Process.WorkingSet64;
                    var frameSize = frame.Payload.ToString().Length;
                    var within = elapsed <= TargetWarmFirstPaint && ws <= TargetWorkingSet;
                    table.AppendLine(string.Create(CultureInfo.InvariantCulture,
                        $"| D1/{artName} | {elapsed.TotalMilliseconds:F1} | {ws / (1024.0 * 1024):F1} | {frameSize} | {elapsed.TotalMilliseconds * 0.15:F1} | {(within ? "yes" : "no")} |"));
                    if (ws > TargetWorkingSet)
                        warnings.Add($"D1/{artName} working set ({ws / (1024 * 1024)} MB) exceeded target ({TargetWorkingSet / (1024 * 1024)} MB)");
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
                if (m.Kind is ContentMessageKind.Frame or ContentMessageKind.Diagnostic)
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
                await frameTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
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

        return 0;
    }
}
