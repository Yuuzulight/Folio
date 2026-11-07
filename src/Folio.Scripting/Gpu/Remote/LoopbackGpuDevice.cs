namespace Folio.Scripting.Gpu.Remote;

/// <summary>
/// Runs a real device's WebGL through the full remote path inside one process: every context call
/// is encoded, framed, validated and dispatched exactly as it would be for the GPU worker, with
/// array arenas standing in for the shared sections. Surface calls go straight to the device.
/// Used to prove the remote path matches direct execution before process isolation is wired.
/// </summary>
internal sealed class LoopbackGpuDevice : IGpuDevice, INativeWebGlFactory
{
    internal const int UploadBytes = 16 * 1024 * 1024, ReadbackBytes = 16 * 1024 * 1024;
    private readonly IGpuDevice _device;
    private readonly GpuWebGlDispatcher _dispatcher;
    private readonly GpuWebGlSession _session;

    private LoopbackGpuDevice(IGpuDevice device, INativeWebGlFactory factory)
    {
        _device = device;
        var upload = new ArrayGpuArena(UploadBytes);
        var readback = new ArrayGpuArena(ReadbackBytes);
        _dispatcher = new GpuWebGlDispatcher(factory, upload, readback);
        _session = new GpuWebGlSession(new Transport(_dispatcher), upload, readback);
    }

    /// <summary>Wraps devices that provide WebGL; any other device is returned unchanged.</summary>
    internal static IGpuDevice Wrap(IGpuDevice device) =>
        device is INativeWebGlFactory factory and not LoopbackGpuDevice ? new LoopbackGpuDevice(device, factory) : device;

    public GpuRendererKind RendererKind => _device.RendererKind;
    public string RendererIdentity => _device.RendererIdentity;
    // Metrics are a synchronization point: queued calls run before the device is measured.
    public long ReservedBytes { get { _session.Synchronize(); return _device.ReservedBytes; } }
    public long PeakReservedBytes { get { _session.Synchronize(); return _device.PeakReservedBytes; } }
    public bool IsDisposed => _device.IsDisposed;
    public GpuSurfaceResult TryCreateSurface(int width, int height) => _device.TryCreateSurface(width, height);
    public bool TryCreate(WebGlContextOptions options, out INativeWebGlContext? context, out GpuFailure? failure) => _session.TryCreate(options, out context, out failure);

    public void Dispose()
    {
        _session.Dispose();
        _dispatcher.Dispose();
        _device.Dispose();
    }

    private sealed class Transport(GpuWebGlDispatcher dispatcher) : IGpuWebGlTransport
    {
        public byte[] Exchange(byte[] batch) => dispatcher.Execute(batch);
    }
}
