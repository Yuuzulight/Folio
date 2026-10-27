using System.Runtime.InteropServices;
using Folio.Scripting.Gpu;

namespace Folio.Scripting.Dom;

/// <summary>Owns one native GPU device for one document and its aggregate budget.</summary>
internal sealed class GpuDocumentBridge : IDisposable
{
    private readonly IGpuDevice _device;
    private readonly Dictionary<CanvasElementState, IGpuSurface> _surfaces = new(ReferenceEqualityComparer.Instance);
    private readonly object _gate = new();
    private int _disposed;

    private GpuDocumentBridge(IGpuDevice device) => _device = device;

    internal IGpuDevice Device => _device;
    internal long ReservedBytes => _device.ReservedBytes;
    internal long PeakReservedBytes => _device.PeakReservedBytes;
    internal string RendererIdentity => _device.RendererIdentity;
    internal INativeWebGlFactory? WebGlFactory => _device as INativeWebGlFactory;

    internal static GpuDocumentBridge? TryCreate(long maxBytes, out GpuFailure? failure, Func<long, GpuDeviceResult>? factory = null)
    {
        if (factory is not null)
        {
            var injected = factory(maxBytes);
            if (injected.Device is { } injectedDevice) { failure = null; return new GpuDocumentBridge(injectedDevice); }
            failure = injected.Failure;
            return null;
        }
        var path = NativeLibraryPath();
        if (path is null) { failure = new(GpuFailureKind.Unavailable, "The ANGLE native library was not found."); return null; }
        var result = AngleGpuBackend.TryCreate(path, maxBytes);
        if (result.Device is { } device) { failure = null; return new GpuDocumentBridge(device); }
        failure = result.Failure;
        return null;
    }

    internal bool TryClear(CanvasElementState canvas, float red, float green, float blue, float alpha, out GpuFailure? failure)
    {
        failure = null;
        if (Volatile.Read(ref _disposed) != 0)
        {
            failure = new(GpuFailureKind.Unavailable, "The document GPU device is disposed.");
            return false;
        }
        var width = canvas.Width;
        var height = canvas.Height;
        if (width <= 0 || height <= 0 || !canvas.SizeAllowed)
        {
            failure = new(GpuFailureKind.BudgetExceeded, "The canvas dimensions exceed the image limits.");
            return false;
        }
        IGpuSurface? surface;
        lock (_gate)
        {
            if (_surfaces.TryGetValue(canvas, out surface) && (surface.Width != width || surface.Height != height))
            {
                surface.Dispose();
                _surfaces.Remove(canvas);
                surface = null;
            }
            if (surface is null)
            {
                var result = _device.TryCreateSurface(width, height);
                if (!result.Succeeded || result.Surface is null)
                {
                    failure = result.Failure;
                    return false;
                }
                surface = result.Surface;
                _surfaces.Add(canvas, surface);
                canvas.NativeSurfaceInvalidated = () => Release(canvas);
            }
        }
        var raster = canvas.Surface();
        if (raster is null)
        {
            Release(canvas);
            failure = new(GpuFailureKind.Unavailable, "The canvas has no paintable raster surface.");
            return false;
        }
        if (!surface.ClearColor(red, green, blue, alpha, out failure)) return false;
        var pixels = new byte[checked(width * height * 4)];
        if (!surface.TryReadPixels(pixels, out failure)) return false;
        raster.WritePixels(0, 0, raster.Width, raster.Height, pixels);
        canvas.MarkNativeFrame();
        canvas.Touch();
        return true;
    }

    private void Release(CanvasElementState canvas)
    {
        lock (_gate)
        {
            if (_surfaces.Remove(canvas, out var surface)) surface.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            lock (_gate)
            {
                foreach (var pair in _surfaces.ToArray())
                {
                    pair.Value.Dispose();
                    pair.Key.ClearNativeFrame();
                }
                _surfaces.Clear();
            }
            _device.Dispose();
        }
    }

    private static string? NativeLibraryPath()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            Architecture.X86 => "win-x86",
            _ => null,
        };
        if (rid is null) return null;
        var flattened = Path.Combine(AppContext.BaseDirectory, "av_libglesv2.dll");
        if (File.Exists(flattened)) return flattened;
        var path = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", "av_libglesv2.dll");
        return File.Exists(path) ? path : null;
    }
}
