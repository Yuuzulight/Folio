using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Folio.Scripting.Gpu.Remote;

/// <summary>
/// A WebGL device whose work runs in a real GPU worker process started for it and owned by this process,
/// with the channel adopted here instead of handed to a content child. Disposing it ends the worker.
/// Used by the benchmarks and by FOLIO_WEBGL_REMOTE=worker test runs to exercise the actual process path.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WorkerGpuDevice : IGpuDevice, INativeWebGlFactory
{
    private readonly GpuWebGlWorker _worker;
    private readonly RemoteGpuChannel _channel;
    private readonly IGpuDevice _device;

    private WorkerGpuDevice(GpuWebGlWorker worker, RemoteGpuChannel channel, long maxBytes)
    {
        _worker = worker;
        _channel = channel;
        _device = channel.DocumentDeviceFactory(maxBytes).Device!;
    }

    internal static WorkerGpuDevice Start(string contentHostPath, long maxBytes)
    {
        var worker = GpuWebGlWorker.Start(contentHostPath, maxBytes);
        try
        {
            using var description = JsonDocument.Parse(worker.HandOff(Process.GetCurrentProcess().SafeHandle).ToJsonString());
            return new WorkerGpuDevice(worker, RemoteGpuChannel.Adopt(description.RootElement), maxBytes);
        }
        catch { worker.Dispose(); throw; }
    }

    internal GpuWebGlSession Session => _channel.Session;
    internal int ProcessId => _worker.ProcessId;
    public GpuRendererKind RendererKind => _device.RendererKind;
    public string RendererIdentity => _device.RendererIdentity;
    public long ReservedBytes => _device.ReservedBytes;
    public long PeakReservedBytes => _device.PeakReservedBytes;
    public bool IsDisposed => _device.IsDisposed;
    public GpuSurfaceResult TryCreateSurface(int width, int height) => _device.TryCreateSurface(width, height);
    public bool TryCreate(WebGlContextOptions options, out INativeWebGlContext? context, out GpuFailure? failure) =>
        ((INativeWebGlFactory)_device).TryCreate(options, out context, out failure);

    public void Dispose()
    {
        _device.Dispose();
        _channel.Dispose();
        _worker.Dispose();
    }
}
