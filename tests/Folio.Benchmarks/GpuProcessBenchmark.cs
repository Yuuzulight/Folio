using System.Globalization;
using System.Text.Json;
using Folio.RenderTests;
using Folio.Scripting.Ipc;

namespace Folio.Benchmarks;

/// <summary>Samples a native device in the actual sandboxed content process.</summary>
internal static class GpuProcessBenchmark
{
    internal static async Task<int> RunAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("The Windows ANGLE process benchmark is unavailable on this platform.");
            return 0;
        }
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        var host = Path.Combine(RepoPaths.Root, "tools", "Folio.SandboxProbe", "bin", configuration, "net10.0", "Folio.SandboxProbe.dll");
        await using var client = ContentProcessClient.Start(host);
        var result = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask Receive(ContentMessage message, CancellationToken cancellationToken)
        {
            if (message.Kind is ContentMessageKind.Frame or ContentMessageKind.Diagnostic) result.TrySetResult(message);
            return ValueTask.CompletedTask;
        }
        void Stopped(object? sender, ContentProcessStoppedEventArgs args) =>
            result.TrySetException(new InvalidOperationException($"GPU benchmark content process stopped: {args.Reason}"));
        client.MessageReceived += Receive;
        client.ScriptStopped += Stopped;
        try
        {
            await client.SendAsync(ContentMessageKind.Load, new
            {
                probe = "gpu", canvasId = "gpu-canvas",
                html = "<body style='margin:0'><canvas id='gpu-canvas' width='640' height='480'></canvas>",
                script = "'native GPU surface benchmark'",
                capabilities = new FeatureCapabilities { WebGl = true },
                red = 0.25f, green = 0.5f, blue = 0.75f, alpha = 1f
            });
            var message = await result.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (message.Kind != ContentMessageKind.Frame)
                throw new InvalidOperationException($"GPU benchmark failed: {message.Payload}");
            var gpu = message.Payload.GetProperty("payload").GetProperty("gpu");
            if (!gpu.GetProperty("succeeded").GetBoolean())
                throw new InvalidOperationException($"Native GPU clear failed: {gpu}");
            var renderer = gpu.GetProperty("rendererIdentity").GetString() ?? "unknown";
            var source = gpu.TryGetProperty("nativeMemorySource", out var metricSource) && metricSource.ValueKind == JsonValueKind.String
                ? metricSource.GetString()! : "unavailable";
            var report = "\n### Sandboxed content-process GPU memory\n\n"
                + "Folio resource reservations and OS process usage are separate measurements. OS values are unavailable when DXGI cannot report them.\n\n"
                + "| Renderer | Reserved bytes | Peak reserved bytes | DXGI local bytes | DXGI nonlocal bytes | OS source |\n"
                + "|---|--:|--:|--:|--:|---|\n"
                + $"| {Cell(renderer)} | {Number(gpu, "reservedBytes")} | {Number(gpu, "peakReservedBytes")} | {Number(gpu, "nativeLocalBytes")} | {Number(gpu, "nativeNonLocalBytes")} | {Cell(source)} |\n";
            Console.WriteLine(report);
            if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary)
                await File.AppendAllTextAsync(summary, report);
            return 0;
        }
        finally
        {
            client.MessageReceived -= Receive;
            client.ScriptStopped -= Stopped;
        }
    }

    private static string Number(JsonElement value, string property) =>
        value.TryGetProperty(property, out var number) && number.ValueKind == JsonValueKind.Number && number.TryGetInt64(out var bytes)
            ? bytes.ToString(CultureInfo.InvariantCulture) : "unavailable";

    private static string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');
}
