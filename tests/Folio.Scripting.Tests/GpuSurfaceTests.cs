using System.Text.Json;
using System.Runtime.InteropServices;
using Folio.Hosting;
using Folio.Scripting.Gpu;
using Folio.Scripting.Ipc;

namespace Folio.Scripting.Tests;

/// <summary>Evidence for the native GPU surface contract and its trusted content-host path.</summary>
[Collection("Content process round trips")]
public sealed class GpuSurfaceTests
{
    [Fact]
    public void FactoryReportsUnavailableWithoutAForcedNativeSuccess()
    {
        var result = AngleGpuBackend.TryCreate(
            Path.Combine(Path.GetTempPath(), $"folio-missing-angle-{Guid.NewGuid():N}.dll"),
            64 * 1024 * 1024,
            GpuRendererPreference.WarpOnly);

        if (OperatingSystem.IsWindows())
        {
            Assert.False(result.Succeeded);
            Assert.Equal(GpuFailureKind.Unavailable, result.Failure?.Kind);
        }
        else
        {
            Assert.False(result.Succeeded);
            Assert.Equal(GpuFailureKind.Unavailable, result.Failure?.Kind);
        }
    }

    [Fact]
    public void WarpSurfaceClearsReadbacksAndReleasesAggregateBudget()
    {
        if (!OperatingSystem.IsWindows())
        {
            var unavailable = AngleGpuBackend.TryCreate("av_libglesv2.dll", 2 * 1024 * 1024, GpuRendererPreference.WarpOnly);
            Assert.False(unavailable.Succeeded);
            Assert.Equal(GpuFailureKind.Unavailable, unavailable.Failure?.Kind);
            return;
        }
        using var device = CreateWarpDevice(2 * 1024 * 1024);
        Assert.Equal(GpuRendererKind.Warp, device.RendererKind);
        Assert.Contains("Warp", device.RendererIdentity, StringComparison.OrdinalIgnoreCase);

        var surfaceResult = device.TryCreateSurface(8, 8);
        Assert.True(surfaceResult.Succeeded, surfaceResult.Failure?.Message);
        using var surface = surfaceResult.Surface!;
        Assert.True(surface.ClearColor(1, 0, 0, 1, out var clearFailure), clearFailure?.Message);
        var pixels = new byte[8 * 8 * 4];
        Assert.True(surface.TryReadPixels(pixels, out var readFailure), readFailure?.Message);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, pixels.AsSpan(0, 4).ToArray());
        Assert.True(device.PeakReservedBytes >= device.ReservedBytes);
        surface.Dispose();
        Assert.Equal(0, device.ReservedBytes);
    }

    [Fact]
    public void NativeSurfaceBudgetRefusalIsTyped()
    {
        if (!OperatingSystem.IsWindows())
        {
            var unavailable = AngleGpuBackend.TryCreate("av_libglesv2.dll", 64, GpuRendererPreference.WarpOnly);
            Assert.False(unavailable.Succeeded);
            Assert.Equal(GpuFailureKind.Unavailable, unavailable.Failure?.Kind);
            return;
        }
        using var device = CreateWarpDevice(64);
        var result = device.TryCreateSurface(4, 4);
        Assert.True(result.Succeeded, result.Failure?.Message);
        using var surface = result.Surface!;
        Assert.False(surface.Resources.TryAllocateBuffer(1, out _, out var failure));
        Assert.Equal(GpuFailureKind.BudgetExceeded, failure?.Kind);
    }

    [Fact]
    public async Task ContentHostGpuProbePublishesNativeCanvasPixels()
    {
        if (!OperatingSystem.IsWindows())
        {
            var unavailable = AngleGpuBackend.TryCreate("av_libglesv2.dll", 64 * 1024 * 1024, GpuRendererPreference.WarpOnly);
            Assert.False(unavailable.Succeeded);
            Assert.Equal(GpuFailureKind.Unavailable, unavailable.Failure?.Kind);
            return;
        }
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        var host = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/Folio.SandboxProbe/bin", configuration, "net10.0/Folio.SandboxProbe.dll"));
        await using var client = ContentProcessClient.Start("dotnet", $"\"{host}\"", new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromSeconds(15)));
        var response = await Exchange(client, new
        {
            probe = "gpu",
            canvasId = "gpu-canvas",
            html = "<body style='margin:0;background:rgb(0,255,0)'><canvas id='gpu-canvas' style='position:absolute;left:20px;top:20px' width='40' height='40'></canvas><div style='position:absolute;left:40px;top:30px;width:40px;height:40px;background:rgb(0,0,255)'></div>",
            script = "var c=document.getElementById('gpu-canvas'); var gl=c.getContext('webgl'); gl !== null",
            capabilities = new FeatureCapabilities { WebGl = true },
            red = 1f,
            green = 0f,
            blue = 0f,
            alpha = 1f
        });
        var probe = JsonDocument.Parse(response.Payload.GetProperty("value").GetString()!).RootElement;
        Assert.True(probe.GetProperty("gpu").GetProperty("succeeded").GetBoolean(),
            probe.GetProperty("gpu").GetProperty("failureMessage").GetString());
        var renderer = probe.GetProperty("gpu").GetProperty("rendererIdentity").GetString();
        Assert.True(renderer?.Contains("Hardware", StringComparison.OrdinalIgnoreCase) == true
            || renderer?.Contains("Warp", StringComparison.OrdinalIgnoreCase) == true,
            $"Unexpected native renderer identity: {renderer}");
        // CanvasFrameStore carries the renderer's premultiplied BGRA8 bytes.
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(response, 25, 25));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(response, 45, 35));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(response, 10, 10));
    }

    [Fact]
    public async Task ContentHostGpuProbeReportsMissingCanvasAndKeepsFrame()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var client = StartProbe();
        var response = await Exchange(client, new
        {
            probe = "gpu",
            canvasId = "missing-canvas",
            html = "<body style='margin:0;background:rgb(0,255,0)'><canvas id='actual-canvas' width='40' height='40'></canvas>",
            script = "document.getElementById('actual-canvas').getContext('2d') !== null",
            capabilities = FeatureCapabilities.Default
        });
        var gpu = Gpu(response);
        Assert.False(gpu.GetProperty("succeeded").GetBoolean());
        Assert.Contains("missing-canvas", gpu.GetProperty("failureMessage").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(response, 50, 10));
    }

    [Fact]
    public async Task ContentHostGpuProbeReportsNativeFactoryFallbackForGrantedCanvas()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var client = StartProbe();
        var response = await Exchange(client, new
        {
            probe = "gpu", canvasId = "gpu-canvas", missingNativeLibrary = true,
            html = "<body style='margin:0;background:rgb(0,255,0)'><canvas id='gpu-canvas' width='40' height='40'></canvas>",
            script = "var c=document.getElementById('gpu-canvas'); c.getContext('webgl') !== null",
            capabilities = new FeatureCapabilities { WebGl = true }, red = 1f, green = 0f, blue = 0f, alpha = 1f
        });
        var gpu = Gpu(response);
        Assert.False(gpu.GetProperty("succeeded").GetBoolean());
        Assert.Equal("Unavailable", gpu.GetProperty("failureKind").GetString());
        Assert.Equal("GraphicsUnavailable", gpu.GetProperty("fallbackReason").GetString());
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(response, 50, 10));
    }

    [Fact]
    public async Task ContentHostGpuProbeReportsBudgetRefusalAndReleasesDocumentOnReplacement()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var client = StartProbe(documentLimits: ResourceLimits.Default with { MaxGpuBytes = 64 });
        var refused = await Exchange(client, new
        {
            probe = "gpu", canvasId = "gpu-canvas",
            html = "<body style='margin:0;background:rgb(0,0,255)'><canvas id='gpu-canvas' width='8' height='8'></canvas>",
            script = "var c=document.getElementById('gpu-canvas'); c.getContext('webgl') !== null",
            capabilities = new FeatureCapabilities { WebGl = true }, red = 1f, green = 0f, blue = 0f, alpha = 1f
        });
        Assert.Equal("BudgetExceeded", Gpu(refused).GetProperty("failureKind").GetString());
        Assert.Equal("ResourceLimit", Gpu(refused).GetProperty("fallbackReason").GetString());
        var restored = await Exchange(client, new
        {
            probe = "gpu", canvasId = "gpu-canvas",
            html = "<body style='margin:0;background:rgb(0,0,255)'><canvas id='gpu-canvas' style='position:absolute;left:0;top:0' width='2' height='2'></canvas>",
            script = "var c=document.getElementById('gpu-canvas'); c.getContext('webgl') !== null",
            capabilities = new FeatureCapabilities { WebGl = true }, red = 1f, green = 0f, blue = 0f, alpha = 1f
        });
        Assert.True(Gpu(restored).GetProperty("succeeded").GetBoolean());
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(restored, 1, 1));
    }

    private static ContentProcessClient StartProbe(ResourceLimits? documentLimits = null)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        var host = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/Folio.SandboxProbe/bin", configuration, "net10.0/Folio.SandboxProbe.dll"));
        return ContentProcessClient.Start("dotnet", $"\"{host}\"", new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromSeconds(15)), documentLimits: documentLimits);
    }

    private static JsonElement Gpu(ContentMessage response) => JsonDocument.Parse(response.Payload.GetProperty("value").GetString()!).RootElement.GetProperty("gpu");

    private static IGpuDevice CreateWarpDevice(long budget)
    {
        var rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.X86 => "win-x86",
            Architecture.Arm64 => "win-arm64",
            _ => "win-x64"
        };
        var asset = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", "av_libglesv2.dll");
        if (!File.Exists(asset))
            asset = Path.Combine(AppContext.BaseDirectory, "av_libglesv2.dll");
        Assert.True(File.Exists(asset), "The pinned ANGLE native asset is required for the WARP proof.");
        var result = AngleGpuBackend.TryCreate(asset!, budget, GpuRendererPreference.WarpOnly);
        Assert.True(result.Succeeded, result.Failure?.Message);
        return result.Device!;
    }

    private static byte[] Pixel(ContentMessage frame, int x, int y)
    {
        var descriptor = frame.Payload.GetProperty("canvasFrame");
        using var store = new CanvasFrameStore(descriptor.GetProperty("path").GetString()!, descriptor.GetProperty("width").GetInt32(), descriptor.GetProperty("height").GetInt32(), descriptor.GetProperty("stride").GetInt32());
        var pixels = store.ReadLatest(out _);
        return pixels.AsSpan(y * store.Stride + x * 4, 4).ToArray();
    }

    private static async Task<ContentMessage> Exchange(ContentProcessClient client, object payload)
    {
        var received = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask Handler(ContentMessage message, CancellationToken _) { if (message.Kind is ContentMessageKind.Frame or ContentMessageKind.Diagnostic) received.TrySetResult(message); return ValueTask.CompletedTask; }
        client.MessageReceived += Handler;
        try
        {
            await client.SendAsync(ContentMessageKind.Load, payload, TestContext.Current.CancellationToken);
            var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.Equal(ContentMessageKind.Frame, message.Kind);
            return message;
        }
        finally { client.MessageReceived -= Handler; }
    }
}
