using Folio.Scripting.Dom;
using Folio.Scripting.Gpu;
using Folio.Scripting.Jint;
using Folio.Dom;
using Folio.Painting;
using Folio.Skia;
using System.Runtime.InteropServices;

namespace Folio.Scripting.Tests;

[Collection("Content process round trips")]
public sealed class GpuDocumentBridgeTests
{
    [Fact]
    public void ForeignAndNonCanvasElementsAreRejectedBeforeGpuOrCanvasStateCreation()
    {
        using var engine = new JintScriptEngine();
        var factoryCalls = 0;
        Assert.True(DomBindings.ConfigureGpuFactory(engine, _ =>
        {
            factoryCalls++;
            return AngleGpuBackend.TryCreate(Path.Combine(Path.GetTempPath(), "missing-angle.dll"), 1024, GpuRendererPreference.WarpOnly);
        }));
        using var document = Document.Parse("<div id='d'></div><canvas id='c'></canvas>", new FolioOptions { Capabilities = new FeatureCapabilities { WebGl = true } });
        using var foreignDocument = Document.Parse("<canvas id='foreign'></canvas>", new FolioOptions { Capabilities = new FeatureCapabilities { WebGl = true } });
        DomBindings.Install(engine, document);
        var div = document.Node.Descendants.OfType<Folio.Dom.ElementNode>().Single(element => element.LocalName == "div");
        var foreign = foreignDocument.Node.Descendants.OfType<Folio.Dom.ElementNode>().Single(element => element.LocalName == "canvas");

        Assert.False(DomBindings.TryClearNativeCanvas(engine, div, 0, 0, 0, 1, out var nonCanvasFailure));
        Assert.Equal(GpuFailureKind.Unavailable, nonCanvasFailure?.Kind);
        Assert.False(DomBindings.TryClearNativeCanvas(engine, foreign, 0, 0, 0, 1, out var foreignFailure));
        Assert.Equal(GpuFailureKind.Unavailable, foreignFailure?.Kind);
        Assert.Equal(0, factoryCalls);
        Assert.False(DomBindings.TryGetGpuMetrics(engine, out _, out _, out _));
        DomBindings.Dispose(engine);
    }

    [Fact]
    public void WarpDocumentRetainsPerCanvasBudgetAndRecreatesAfterRevocation()
    {
        if (!OperatingSystem.IsWindows()) return;
        var nativePath = FindNativeLibrary();
        using var engine = new JintScriptEngine();
        Assert.True(DomBindings.ConfigureGpuFactory(engine, maxBytes => AngleGpuBackend.TryCreate(nativePath, maxBytes, GpuRendererPreference.WarpOnly)));
        using var document = Document.Parse("<canvas id='a' width='2' height='2'></canvas><canvas id='b' width='2' height='2'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true },
            Limits = ResourceLimits.Default with { MaxGpuBytes = 20 },
            Raster = new SkiaRasterBackend()
        });
        DomBindings.Install(engine, document);
        var canvases = document.Node.Descendants.OfType<Folio.Dom.ElementNode>().Where(element => element.LocalName == "canvas").ToArray();
        Assert.Equal(2, canvases.Length);

        Assert.True(DomBindings.TryClearNativeCanvas(engine, canvases[0], 1, 0, 0, 1, out var firstFailure), firstFailure?.Message);
        Assert.True(DomBindings.TryGetGpuMetrics(engine, out var reserved, out _, out var renderer));
        Assert.Contains("Warp", renderer, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(16, reserved);
        Assert.False(DomBindings.TryClearNativeCanvas(engine, canvases[1], 0, 1, 0, 1, out var secondFailure));
        Assert.Equal(GpuFailureKind.BudgetExceeded, secondFailure?.Kind);
        Assert.True(DomBindings.TryGetGpuMetrics(engine, out reserved, out _, out _));
        Assert.Equal(16, reserved);

        Assert.True(DomBindings.UpdateCapabilities(engine, FeatureCapabilities.Default));
        Assert.False(DomBindings.TryGetGpuMetrics(engine, out _, out _, out _));
        Assert.False(DomBindings.TryClearNativeCanvas(engine, canvases[0], 0, 0, 1, 1, out var denied));
        Assert.Equal(GpuFailureKind.Unavailable, denied?.Kind);

        Assert.True(DomBindings.UpdateCapabilities(engine, new FeatureCapabilities { WebGl = true }));
        Assert.True(DomBindings.TryClearNativeCanvas(engine, canvases[1], 0, 1, 0, 1, out var regrantFailure), regrantFailure?.Message);
        Assert.True(DomBindings.TryGetGpuMetrics(engine, out reserved, out _, out renderer));
        Assert.Contains("Warp", renderer, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(16, reserved);
        DomBindings.Dispose(engine);
        Assert.False(DomBindings.TryGetGpuMetrics(engine, out _, out _, out _));
    }

    [Fact]
    public void WarpBudgetRecoveryClearsStaleFallbackAndPublishesRecoveredCanvas()
    {
        if (!OperatingSystem.IsWindows()) return;
        var nativePath = FindNativeLibrary();
        using var engine = new JintScriptEngine();
        Assert.True(DomBindings.ConfigureGpuFactory(engine, maxBytes => AngleGpuBackend.TryCreate(nativePath, maxBytes, GpuRendererPreference.WarpOnly)));
        using var document = Document.Parse("<canvas id='a' width='2' height='2'></canvas><canvas id='b' width='2' height='2'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true },
            Limits = ResourceLimits.Default with { MaxGpuBytes = 20 },
            Raster = new SkiaRasterBackend()
        });
        DomBindings.Install(engine, document);
        var canvases = document.Node.Descendants.OfType<Folio.Dom.ElementNode>().Where(element => element.LocalName == "canvas").ToArray();
        engine.Evaluate("document.getElementById('a').getContext('2d'); document.getElementById('b').getContext('2d');");
        Assert.NotNull(canvases[0].CanvasSurface);
        Assert.NotNull(canvases[1].CanvasSurface);

        Assert.True(DomBindings.TryClearNativeCanvas(engine, canvases[0], 1, 0, 0, 1, out var firstFailure), firstFailure?.Message);
        Assert.False(DomBindings.TryClearNativeCanvas(engine, canvases[1], 0, 1, 0, 1, out var budgetFailure));
        Assert.Equal(GpuFailureKind.BudgetExceeded, budgetFailure?.Kind);
        Assert.Equal(FallbackReason.ResourceLimit, ((IFeatureFallbackSource)canvases[1].CanvasSurface!).Fallback?.Reason);

        engine.Evaluate("document.getElementById('a').width = 1;");
        Assert.True(DomBindings.TryGetGpuMetrics(engine, out var reserved, out _, out _));
        Assert.Equal(0, reserved);
        Assert.True(DomBindings.TryClearNativeCanvas(engine, canvases[1], 0, 1, 0, 1, out var recoveryFailure), recoveryFailure?.Message);
        Assert.Null(((IFeatureFallbackSource)canvases[1].CanvasSurface!).Fallback);
        var snapshot = ((Folio.Imaging.ICanvasSurface)canvases[1].CanvasSurface!).Snapshot();
        Assert.NotNull(snapshot);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, snapshot!.Pixels.Span[..4].ToArray());
        DomBindings.Dispose(engine);
    }

    [Fact]
    public void NativeBudgetRejectsBeforeRasterAllocation()
    {
        using var engine = new JintScriptEngine();
        var raster = new ThrowingRasterBackend();
        Assert.True(DomBindings.ConfigureGpuFactory(engine, _ => new GpuDeviceResult(new RefusingGpuDevice(), null)));
        using var document = Document.Parse("<canvas id='c' width='4096' height='4096'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true },
            Limits = ResourceLimits.Default with { MaxGpuBytes = 1 },
            Raster = raster
        });
        DomBindings.Install(engine, document);
        var canvas = document.Node.Descendants.OfType<Folio.Dom.ElementNode>().Single(element => element.LocalName == "canvas");

        Assert.False(DomBindings.TryClearNativeCanvas(engine, canvas, 0, 0, 0, 1, out var failure));
        Assert.Equal(GpuFailureKind.BudgetExceeded, failure?.Kind);
        Assert.Equal(0, raster.Calls);
        DomBindings.Dispose(engine);
    }

    [Fact]
    public void DeniedWebGlDoesNotCreateOrInvokeTheNativeFactory()
    {
        using var engine = new JintScriptEngine();
        var calls = 0;
        Assert.True(DomBindings.ConfigureGpuFactory(engine, _ =>
        {
            calls++;
            return AngleGpuBackend.TryCreate(Path.Combine(Path.GetTempPath(), "missing-angle.dll"), 1024, GpuRendererPreference.WarpOnly);
        }));
        using var document = Document.Parse("<canvas id='c'></canvas>", new FolioOptions());
        DomBindings.Install(engine, document);
        var canvas = document.Node.Descendants.OfType<Folio.Dom.ElementNode>().Single(element => element.LocalName == "canvas");

        Assert.False(DomBindings.TryClearNativeCanvas(engine, canvas, 1, 0, 0, 1, out var failure));
        Assert.Equal(GpuFailureKind.Unavailable, failure?.Kind);
        Assert.Equal(0, calls);
        DomBindings.Dispose(engine);
    }

    [Fact]
    public void MissingNativeFactoryProducesTypedUnavailableResultAndCanOnlyBeConfiguredBeforeInstall()
    {
        using var engine = new JintScriptEngine();
        var missing = Path.Combine(Path.GetTempPath(), $"folio-angle-missing-{Guid.NewGuid():N}.dll");
        Assert.True(DomBindings.ConfigureGpuFactory(engine, maxBytes => AngleGpuBackend.TryCreate(missing, maxBytes, GpuRendererPreference.WarpOnly)));
        using var document = Document.Parse("<canvas id='c'></canvas>", new FolioOptions { Capabilities = new FeatureCapabilities { WebGl = true } });
        DomBindings.Install(engine, document);
        var canvas = document.Node.Descendants.OfType<Folio.Dom.ElementNode>().Single(element => element.LocalName == "canvas");
        Assert.False(DomBindings.TryClearNativeCanvas(engine, canvas, 0, 1, 0, 1, out var failure));
        Assert.Equal(GpuFailureKind.Unavailable, failure?.Kind);
        Assert.False(DomBindings.ConfigureGpuFactory(engine, _ => throw new InvalidOperationException("late factory")));
        DomBindings.Dispose(engine);
    }

    private sealed class ThrowingRasterBackend : IRasterBackend
    {
        public int Calls;
        public IRasterSurface? CreateSurface(int width, int height) { Calls++; throw new InvalidOperationException("raster allocation must not be reached"); }
    }

    private sealed class RefusingGpuDevice : IGpuDevice
    {
        public GpuRendererKind RendererKind => GpuRendererKind.Unavailable;
        public string RendererIdentity => "test-refusing";
        public long ReservedBytes => 0;
        public long PeakReservedBytes => 0;
        public bool IsDisposed => false;
        public GpuSurfaceResult TryCreateSurface(int width, int height) => new(null, new(GpuFailureKind.BudgetExceeded, "test budget"));
        public void Dispose() { }
    }

    private static string FindNativeLibrary()
    {
        var rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            Architecture.X86 => "win-x86",
            _ => throw new PlatformNotSupportedException()
        };
        var path = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", "av_libglesv2.dll");
        return File.Exists(path) ? path : throw new FileNotFoundException("ANGLE native asset is unavailable.", path);
    }
}
