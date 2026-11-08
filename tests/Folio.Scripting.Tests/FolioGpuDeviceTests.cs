using Folio.Scripting.Gpu;

namespace Folio.Scripting.Tests;

public sealed class FolioGpuDeviceTests
{
    [Fact]
    public void NegativeBudgetReportsTypedBudgetExceededResult()
    {
        var result = FolioGpuBackend.TryCreate(-1, GpuRendererPreference.WarpOnly);
        Assert.False(result.Succeeded);
        Assert.Equal(GpuFailureKind.BudgetExceeded, result.Failure?.Kind);
    }

    [Fact]
    public void WarpDeviceClearsAndReadsStraightRgba()
    {
        if (!OperatingSystem.IsWindows()) return;

        var result = FolioGpuBackend.TryCreate(64 * 1024 * 1024, GpuRendererPreference.WarpOnly);
        Assert.True(result.Succeeded, result.Failure?.Message);
        using var device = result.Device!;
        Assert.Equal(GpuRendererKind.Warp, device.RendererKind);
        Assert.Contains("Warp", device.RendererIdentity, StringComparison.OrdinalIgnoreCase);

        var surfaceResult = device.TryCreateSurface(4, 3);
        Assert.True(surfaceResult.Succeeded, surfaceResult.Failure?.Message);
        using var surface = surfaceResult.Surface!;

        // Clear with (0.25f, 0.5f, 0.75f, 0.5f)
        Assert.True(surface.ClearColor(0.25f, 0.5f, 0.75f, 0.5f, out var clearFailure), clearFailure?.Message);
        var pixels = new byte[4 * 3 * 4];
        Assert.True(surface.TryReadPixels(pixels, out var readFailure), readFailure?.Message);

        // 0.25f * 255 = ~64, 0.5f * 255 = ~128, 0.75f * 255 = ~191, 0.5f * 255 = ~128
        Assert.InRange(pixels[0], 63, 65);
        Assert.InRange(pixels[1], 127, 129);
        Assert.InRange(pixels[2], 190, 192);
        Assert.InRange(pixels[3], 127, 129);

        // Full red clear, then blue partial rect clear
        Assert.True(surface.ClearColor(1, 0, 0, 1, out clearFailure), clearFailure?.Message);
        Assert.True(surface.ClearColorRect(0, 0, 1, 1, 0, 0, 4, 1, out clearFailure), clearFailure?.Message);
        Assert.True(surface.TryReadPixels(pixels, out readFailure), readFailure?.Message);

        // First row (y = 0) is blue (0, 0, 255, 255)
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixels.AsSpan(0, 4).ToArray());
        // Last row (y = 2) is red (255, 0, 0, 255)
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, pixels.AsSpan((3 - 1) * 4 * 4, 4).ToArray());
    }

    [Fact]
    public void BudgetEnforcementLimitsSurfaceAndResourceAllocations()
    {
        if (!OperatingSystem.IsWindows()) return;

        // 4x4 surface = 64 bytes. Total budget = 100 bytes.
        var result = FolioGpuBackend.TryCreate(100, GpuRendererPreference.WarpOnly);
        Assert.True(result.Succeeded);
        using var device = result.Device!;

        var surfaceResult = device.TryCreateSurface(4, 4);
        Assert.True(surfaceResult.Succeeded);
        using var surface = surfaceResult.Surface!;
        Assert.Equal(64, device.ReservedBytes);

        // Another 4x4 surface needs 64 bytes, 64 + 64 = 128 > 100 -> BudgetExceeded
        var failResult = device.TryCreateSurface(4, 4);
        Assert.False(failResult.Succeeded);
        Assert.Equal(GpuFailureKind.BudgetExceeded, failResult.Failure?.Kind);

        // Resource set allocation of 32 bytes should succeed (64 + 32 = 96 <= 100)
        Assert.True(surface.Resources.TryAllocateBuffer(32, out var res1, out _));
        Assert.NotNull(res1);
        Assert.Equal(96, device.ReservedBytes);

        // Another 16 bytes should exceed budget (96 + 16 = 112 > 100)
        Assert.False(surface.Resources.TryAllocateBuffer(16, out var res2, out var fail));
        Assert.Null(res2);
        Assert.Equal(GpuFailureKind.BudgetExceeded, fail?.Kind);

        // Disposing res1 frees 32 bytes back to 64 bytes
        res1!.Dispose();
        Assert.Equal(64, device.ReservedBytes);
    }

    [Fact]
    public void GpuDocumentBridgeClearsCanvasWithFolioGpuBackend()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new Folio.Scripting.Jint.JintScriptEngine();
        Assert.True(Folio.Scripting.Dom.DomBindings.ConfigureGpuFactory(engine, maxBytes => FolioGpuBackend.TryCreate(maxBytes, GpuRendererPreference.WarpOnly)));
        using var document = Folio.Document.Parse("<canvas id='c' width='4' height='4'></canvas>", new Folio.FolioOptions
        {
            Capabilities = new Folio.FeatureCapabilities { WebGl = true },
            Raster = Folio.Painting.SoftwareRasterBackend.Instance,
        });
        Folio.Scripting.Dom.DomBindings.Install(engine, document);
        var canvas = document.Node.Descendants.OfType<Folio.Dom.ElementNode>().Single(e => e.LocalName == "canvas");
        Assert.True(Folio.Scripting.Dom.DomBindings.TryClearNativeCanvas(engine, canvas, 0.25f, 0.5f, 0.75f, 1f, out var failure), failure?.Message);
        Assert.True(Folio.Scripting.Dom.DomBindings.TryGetGpuMetrics(engine, out var reserved, out _, out _));
        Assert.Equal(64, reserved); // 4 * 4 * 4 = 64 bytes
        Folio.Scripting.Dom.DomBindings.Dispose(engine);
    }

    // A GPU reset under a direct (in-process) context: the page's next call (through the remote path, its next
    // synchronization point) loses the context with its lifecycle event, getError reports CONTEXT_LOST_WEBGL once, and later calls add no errors.
    [Fact]
    public void ARemovedDeviceLosesTheContextWithItsLifecycleEvent()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new Folio.Scripting.Native.NativeJavaScriptEngine();
        FolioGpuDevice? device = null;
        Assert.True(Folio.Scripting.Dom.DomBindings.ConfigureGpuFactory(engine, maxBytes =>
        {
            var result = FolioGpuBackend.TryCreate(maxBytes, GpuRendererPreference.WarpOnly);
            device = result.Device as FolioGpuDevice;
            return result;
        }));
        using var document = Folio.Document.Parse("<canvas id='c' width='4' height='4'></canvas>", new Folio.FolioOptions
        {
            Capabilities = new Folio.FeatureCapabilities { WebGl = true },
        });
        Folio.Scripting.Dom.DomBindings.Install(engine, document);
        try
        {
            Assert.Equal("false|0|0", engine.Evaluate("""
                var lost = 0;
                var gl = document.getElementById('c').getContext('webgl');
                gl.oncontextlost = function() { lost++; };
                gl.clear(gl.COLOR_BUFFER_BIT);
                gl.isContextLost() + '|' + gl.getError() + '|' + lost
                """).AsString());
            device!.MarkRemoved();
            Assert.Equal("1|true|37442|0|1", engine.Evaluate(
                "gl.clear(gl.COLOR_BUFFER_BIT); var lostNow = gl.isContextLost(); lost + '|' + lostNow + '|' + gl.getError() + '|' + gl.getError() + '|' + lost").AsString());
        }
        finally { Folio.Scripting.Dom.DomBindings.Dispose(engine); }
    }
}
