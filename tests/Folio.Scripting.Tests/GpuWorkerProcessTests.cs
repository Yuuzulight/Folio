using System.Diagnostics;
using Folio;
using Folio.Scripting.Ipc;

namespace Folio.Scripting.Tests;

/// <summary>
/// Stage 4 gate: WebGL runs in the separate GPU worker process through the direct channel, and
/// losing that worker keeps the page alive and reports a lost context.
/// </summary>
[Collection("Content process round trips")]
public sealed class GpuWorkerProcessTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    private const string Page = """
        <canvas id='c' width='4' height='4'></canvas>
        <script>
        var lostEvents = 0;
        var canvas = document.getElementById('c');
        var gl = canvas.getContext('webgl', { antialias: false, preserveDrawingBuffer: true });
        gl.oncontextlost = function() { lostEvents++; };
        gl.clearColor(1, 0, 0, 1);
        gl.clear(gl.COLOR_BUFFER_BIT);
        var pixel = new Uint8Array(4);
        gl.readPixels(1, 1, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, pixel);
        var drawn = Array.from(pixel).join(',');
        </script>
        """;

    [Fact(Skip = "Only supported on Windows.", SkipUnless = nameof(IsWindows), Timeout = 60_000)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task WebGlDrawsThroughTheSeparateGpuWorker()
    {
        ContentProcessClient.GpuWorkerOverride = true;
        try
        {
            await using var client = Start();
            Assert.Equal("255,0,0,255|false", await Value(client, new { html = Page, capabilities = Capabilities, script = "drawn + '|' + gl.isContextLost()" }, TestContext.Current.CancellationToken));
            var worker = client.GpuWorkerProcessId;
            Assert.NotNull(worker);
            Assert.NotEqual(client.Process.Id, worker);
            using var process = Process.GetProcessById(worker!.Value);
            Assert.False(process.HasExited);
            WorkerIsolationCapabilityMatrixTests.AssertRestricted(process); // its own AppContainer and job
            // The child maps the parent-committed sections, so its job's commit limit is lowered by their size.
            Assert.Equal(new ContentProcessLimits().MemoryBytes - Folio.Scripting.Gpu.Remote.GpuWebGlWorker.SectionBytes, client.JobMemoryLimit);
        }
        finally { ContentProcessClient.GpuWorkerOverride = null; }
    }

    [Fact(Skip = "Only supported on Windows.", SkipUnless = nameof(IsWindows), Timeout = 60_000)]
    public async Task KillingTheGpuWorkerKeepsThePageAliveAndLosesTheContext()
    {
        ContentProcessClient.GpuWorkerOverride = true;
        try
        {
            await using var client = Start();
            Assert.Equal("255,0,0,255", await Value(client, new { html = Page, capabilities = Capabilities, script = "drawn" }, TestContext.Current.CancellationToken));
            using (var worker = Process.GetProcessById(client.GpuWorkerProcessId!.Value))
            {
                worker.Kill();
                await worker.WaitForExitAsync(TestContext.Current.CancellationToken);
            }

            // The page notices the worker's exit by itself; poll the live page until it reports the loss.
            var watch = Stopwatch.StartNew();
            var state = "";
            while (watch.Elapsed < TimeSpan.FromSeconds(10))
            {
                state = await Value(client, new { script = "gl.isContextLost() + '|' + lostEvents + '|' + (1 + 1)" }, TestContext.Current.CancellationToken);
                if (state.StartsWith("true|1", StringComparison.Ordinal)) break;
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
            Assert.Equal("true|1|2", state);
            Assert.False(client.Process.HasExited);
        }
        finally { ContentProcessClient.GpuWorkerOverride = null; }
    }

    [Fact(Skip = "Only supported on Windows.", SkipUnless = nameof(IsWindows), Timeout = 60_000)]
    public async Task WithoutTheOptInTheWorkerIsNotStarted()
    {
        await using var client = Start();
        Assert.Equal("255,0,0,255", await Value(client, new { html = Page, capabilities = Capabilities, script = "drawn" }, TestContext.Current.CancellationToken));
        Assert.Null(client.GpuWorkerProcessId);
        Assert.Equal(new ContentProcessLimits().MemoryBytes, client.JobMemoryLimit);
    }

    private static readonly FeatureCapabilities Capabilities = new() { WebGl = true };

    private static ContentProcessClient Start() =>
        ContentProcessClient.Start(ContentHostTestPaths.Resolve(), limits: new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromSeconds(15)));

    private static async Task<string> Value(ContentProcessClient client, object payload, CancellationToken cancellation)
    {
        var completion = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask Handler(ContentMessage message, CancellationToken _)
        {
            if (message.Kind == ContentMessageKind.Frame && message.Payload.GetProperty("sequence").GetInt64() > 0) completion.TrySetResult(message);
            else if (message.Kind == ContentMessageKind.Diagnostic) completion.TrySetException(new InvalidOperationException(message.Payload.ToString()));
            return ValueTask.CompletedTask;
        }
        client.MessageReceived += Handler;
        try
        {
            await client.SendAsync(ContentMessageKind.Load, payload, cancellation);
            var frame = await completion.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellation);
            return frame.Payload.GetProperty("value").GetString() ?? "";
        }
        finally { client.MessageReceived -= Handler; }
    }
}
