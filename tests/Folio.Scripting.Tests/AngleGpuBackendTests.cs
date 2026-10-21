using System.Runtime.InteropServices;
using Folio.Scripting.Gpu;

namespace Folio.Scripting.Tests;

[Collection("Content process round trips")]
public sealed class AngleGpuBackendTests
{
    [Fact]
    public void NonWindowsReportsTypedUnavailableResult()
    {
        if (OperatingSystem.IsWindows()) return;
        var result = AngleGpuBackend.TryCreate("av_libglesv2.dll", 64 * 1024 * 1024, GpuRendererPreference.WarpOnly);
        Assert.False(result.Succeeded);
        Assert.Equal(GpuFailureKind.Unavailable, result.Failure?.Kind);
    }

    [Fact]
    public void MissingNativeLibraryReportsTypedUnavailableResult()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), $"folio-angle-missing-{Guid.NewGuid():N}.dll");
        var result = AngleGpuBackend.TryCreate(path, 64 * 1024 * 1024, GpuRendererPreference.WarpOnly);
        Assert.False(result.Succeeded);
        Assert.Equal(GpuFailureKind.Unavailable, result.Failure?.Kind);
    }

    [Fact]
    public void WarpSurfaceClearsAndReadsStraightRgba()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var device = CreateDevice(GpuRendererPreference.WarpOnly, 64 * 1024 * 1024);
        Assert.Equal(GpuRendererKind.Warp, device.RendererKind);
        Assert.Contains("Warp", device.RendererIdentity, StringComparison.OrdinalIgnoreCase);
        var surfaceResult = device.TryCreateSurface(4, 3);
        Assert.True(surfaceResult.Succeeded, surfaceResult.Failure?.Message);
        using var surface = surfaceResult.Surface!;
        Assert.True(surface.ClearColor(0.25f, 0.5f, 0.75f, 0.5f, out var clearFailure), clearFailure?.Message);
        var pixels = new byte[4 * 3 * 4];
        Assert.True(surface.TryReadPixels(pixels, out var readFailure), readFailure?.Message);
        Assert.Equal(new byte[] { 64, 128, 191, 128 }, pixels.AsSpan(0, 4).ToArray());
        Assert.Equal(new byte[] { 64, 128, 191, 128 }, pixels.AsSpan((3 - 1) * 4 * 4, 4).ToArray());
        Assert.True(surface.ClearColor(1, 0, 0, 1, out clearFailure), clearFailure?.Message);
        Assert.True(surface.ClearColorRect(0, 0, 1, 1, 0, 0, 4, 1, out clearFailure), clearFailure?.Message);
        Assert.True(surface.TryReadPixels(pixels, out readFailure), readFailure?.Message);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, pixels.AsSpan(0, 4).ToArray());
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixels.AsSpan((3 - 1) * 4 * 4, 4).ToArray());
    }

    [Fact]
    public void NativeResourcesShareBudgetAndReleaseOnDispose()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var device = CreateDevice(GpuRendererPreference.WarpOnly, 2 * 1024 * 1024);
        using var surface = device.TryCreateSurface(8, 8).Surface!;
        var baseline = device.ReservedBytes;
        Assert.True(surface.Resources.TryAllocateTexture(8, 8, out var texture, out var textureFailure), textureFailure?.Message);
        Assert.True(surface.Resources.TryAllocateBuffer(1024, out var buffer, out var bufferFailure), bufferFailure?.Message);
        Assert.True(surface.Resources.TryAllocateRenderbuffer(8, 8, 4, out var renderbuffer, out var renderbufferFailure), renderbufferFailure?.Message);
        Assert.True(device.ReservedBytes > baseline);
        texture!.Dispose(); buffer!.Dispose(); renderbuffer!.Dispose();
        Assert.Equal(baseline, device.ReservedBytes);
        surface.Dispose();
        Assert.Equal(0, device.ReservedBytes);
    }

    [Fact]
    public void BudgetRejectsSurfaceAndLargeResourceWithoutNativeFallback()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var device = CreateDevice(GpuRendererPreference.WarpOnly, 64);
        var surface = device.TryCreateSurface(4, 4);
        Assert.True(surface.Succeeded, surface.Failure?.Message);
        using (surface.Surface!)
        {
            Assert.False(surface.Surface!.Resources.TryAllocateBuffer(1, out _, out var failure));
            Assert.Equal(GpuFailureKind.BudgetExceeded, failure?.Kind);
        }
        Assert.Equal(0, device.ReservedBytes);
    }

    [Fact]
    public void RetainedSurfaceRejectsOperationsAfterDeviceDisposal()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var device = CreateDevice(GpuRendererPreference.WarpOnly, 64 * 1024 * 1024);
        var surface = device.TryCreateSurface(2, 2).Surface!;
        device.Dispose();
        Assert.False(surface.ClearColor(1, 0, 0, 1, out var clearFailure));
        Assert.Equal(GpuFailureKind.Unavailable, clearFailure?.Kind);
        Assert.False(surface.Resources.TryAllocateBuffer(4, out _, out var resourceFailure));
        Assert.Equal(GpuFailureKind.Unavailable, resourceFailure?.Kind);
        surface.Dispose();
    }

    [Fact]
    public void TwoWarpDevicesRemainIndependentWhenTheFirstIsDisposed()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var first = CreateDevice(GpuRendererPreference.WarpOnly, 64 * 1024 * 1024);
        using var second = CreateDevice(GpuRendererPreference.WarpOnly, 64 * 1024 * 1024);
        using var surface = second.TryCreateSurface(2, 2).Surface!;
        first.Dispose();
        Assert.True(surface.ClearColor(0, 1, 0, 1, out var clearFailure), clearFailure?.Message);
        var pixels = new byte[16];
        Assert.True(surface.TryReadPixels(pixels, out var readFailure), readFailure?.Message);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, pixels[..4]);
    }

    [Fact]
    public async Task ConcurrentSurfaceDisposalAndUseIsBoundedAndSafe()
    {
        if (!OperatingSystem.IsWindows()) return;
        var cancellationToken = TestContext.Current.CancellationToken;
        using var device = CreateDevice(GpuRendererPreference.WarpOnly, 64 * 1024 * 1024);
        var surfaceResult = device.TryCreateSurface(8, 8);
        Assert.True(surfaceResult.Succeeded, surfaceResult.Failure?.Message);
        var surface = surfaceResult.Surface!;
        using var start = new Barrier(2);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var useCompletion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeCompletion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var useThread = new Thread(() =>
        {
            try
            {
                start.SignalAndWait(timeout.Token);
                for (var i = 0; i < 100; i++)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    surface.ClearColor(0, 1, 0, 1, out _);
                    surface.TryReadPixels(new byte[8 * 8 * 4], out _);
                }
                useCompletion.TrySetResult(null);
            }
            catch (Exception error) { useCompletion.TrySetException(error); }
        }) { IsBackground = true, Name = "Folio ANGLE surface use" };
        var disposeThread = new Thread(() =>
        {
            try
            {
                start.SignalAndWait(timeout.Token);
                surface.Dispose();
                surface.Dispose();
                disposeCompletion.TrySetResult(null);
            }
            catch (Exception error) { disposeCompletion.TrySetException(error); }
        }) { IsBackground = true, Name = "Folio ANGLE surface disposal" };
        useThread.Start();
        disposeThread.Start();
        try
        {
            await Task.WhenAll(useCompletion.Task, disposeCompletion.Task).WaitAsync(timeout.Token);
        }
        finally
        {
            timeout.Cancel();
            var useJoined = useThread.Join(TimeSpan.FromSeconds(1));
            var disposeJoined = disposeThread.Join(TimeSpan.FromSeconds(1));
            Assert.True(useJoined, "The ANGLE surface-use worker did not terminate.");
            Assert.True(disposeJoined, "The ANGLE surface-disposal worker did not terminate.");
        }
        Assert.False(surface.ClearColor(1, 0, 0, 1, out var failure));
        Assert.Equal(GpuFailureKind.Unavailable, failure?.Kind);
    }

    [Fact]
    public void NegativeBudgetIsRejectedBeforeNativeLoad()
    {
        var result = AngleGpuBackend.TryCreate("missing", -1, GpuRendererPreference.WarpOnly);
        Assert.False(result.Succeeded);
        Assert.Equal(GpuFailureKind.BudgetExceeded, result.Failure?.Kind);
    }

    private static IGpuDevice CreateDevice(GpuRendererPreference preference, long budget)
    {
        var path = FindNativeLibrary();
        var result = AngleGpuBackend.TryCreate(path, budget, preference);
        Assert.True(result.Succeeded, result.Failure?.Message);
        return result.Device!;
    }

    private static string FindNativeLibrary()
    {
        var rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            Architecture.X86 => "win-x86",
            _ => throw new PlatformNotSupportedException($"Unsupported Windows architecture: {RuntimeInformation.ProcessArchitecture}"),
        };
        var roots = new[] { AppContext.BaseDirectory, Path.GetDirectoryName(typeof(AngleGpuBackendTests).Assembly.Location)! };
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            var canonical = Path.Combine(root, "runtimes", rid, "native", "av_libglesv2.dll");
            if (File.Exists(canonical)) return canonical;
            var flattened = Path.Combine(root, "av_libglesv2.dll");
            if (File.Exists(flattened)) return flattened;
        }
        throw new FileNotFoundException("The ANGLE native asset was not restored for this test target.", "av_libglesv2.dll");
    }
}
