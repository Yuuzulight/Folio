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
        var restoredEvents = 0;
        gl.oncontextrestored = function() { restoredEvents++; };
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

            // The page notices the worker's exit by itself (and is then restored; see below).
            Assert.Equal("1|2", await Poll(client, "lostEvents + '|' + (1 + 1)", "1|"));
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

    private const string State = "gl.isContextLost() + '|' + lostEvents + '|' + restoredEvents";
    private const string DrawGreen = "gl.clearColor(0, 1, 0, 1); gl.clear(gl.COLOR_BUFFER_BIT); gl.readPixels(1, 1, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, pixel); Array.from(pixel).join(',')";

    [Fact(Skip = "Only supported on Windows.", SkipUnless = nameof(IsWindows), Timeout = 90_000)]
    public async Task AfterTheWorkerDiesThePageIsRestoredOnAFreshWorker()
    {
        ContentProcessClient.GpuWorkerOverride = true;
        try
        {
            await using var client = Start();
            Assert.Equal("255,0,0,255", await Value(client, new { html = Page, capabilities = Capabilities, script = "drawn" }, TestContext.Current.CancellationToken));
            var first = client.GpuWorkerProcessId!.Value;
            await Kill(first);
            // Lost once, then restored once on a new worker process; the page draws again.
            Assert.Equal("false|1|1", await Poll(client, State, "false|1|1"));
            Assert.NotEqual(first, client.GpuWorkerProcessId);
            Assert.Equal("0,255,0,255", await Value(client, new { script = DrawGreen }, TestContext.Current.CancellationToken));
            Assert.False(client.Process.HasExited);
        }
        finally { ContentProcessClient.GpuWorkerOverride = null; }
    }

    [Fact(Skip = "Only supported on Windows.", SkipUnless = nameof(IsWindows), Timeout = 180_000)]
    public async Task WorkerReplacementsAreBoundedPerContentProcess()
    {
        ContentProcessClient.GpuWorkerOverride = true;
        try
        {
            await using var client = Start();
            Assert.Equal("255,0,0,255", await Value(client, new { html = Page, capabilities = Capabilities, script = "drawn" }, TestContext.Current.CancellationToken));
            for (var restore = 1; restore <= ContentProcessClient.MaxGpuRestores; restore++)
            {
                await Kill(client.GpuWorkerProcessId!.Value);
                var expected = $"false|{restore}|{restore}";
                Assert.Equal(expected, await Poll(client, State, expected));
            }
            await Kill(client.GpuWorkerProcessId!.Value);
            var final = $"true|{ContentProcessClient.MaxGpuRestores + 1}|{ContentProcessClient.MaxGpuRestores}";
            Assert.Equal(final, await Poll(client, State, final));
            await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken); // No late replacement arrives.
            Assert.Equal(final, await Value(client, new { script = State }, TestContext.Current.CancellationToken));
            Assert.Null(client.GpuWorkerProcessId);
        }
        finally { ContentProcessClient.GpuWorkerOverride = null; }
    }

    private static async Task Kill(int processId)
    {
        using var worker = Process.GetProcessById(processId);
        worker.Kill();
        await worker.WaitForExitAsync(TestContext.Current.CancellationToken);
    }

    // Polls the live page until its value starts with the expected prefix (or 20 s pass).
    private static async Task<string> Poll(ContentProcessClient client, string script, string prefix)
    {
        var watch = Stopwatch.StartNew();
        var state = "";
        while (watch.Elapsed < TimeSpan.FromSeconds(20))
        {
            state = await Value(client, new { script }, TestContext.Current.CancellationToken);
            if (state.StartsWith(prefix, StringComparison.Ordinal)) break;
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        return state;
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
