using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Folio.Scripting.Gpu.Remote;

/// <summary>
/// Document-process end of the direct GPU channel the parent handed over in a load payload.
/// One per content process; every document's WebGL goes through its single session. The worker
/// process handle it holds can only be waited on, and is used to notice the worker's exit.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class RemoteGpuChannel : IDisposable
{
    private readonly FileStream _commands, _replies;
    private readonly GpuWebGlPipeTransport _transport;
    private readonly GpuSharedHandles.SafeFileMappingHandle _uploadSection, _readbackSection;
    private readonly MappedGpuArena _upload, _readback;
    private readonly ManualResetEvent _workerExit;
    private readonly ManualResetEvent _stopExitWatcher = new(false);
    private readonly Thread _exitWatcher;
    private int _disposed;

    private RemoteGpuChannel(JsonElement channel)
    {
        _commands = new FileStream(GpuSharedHandles.AdoptPipe(Handle(channel, "commands")), FileAccess.Write, 1);
        _replies = new FileStream(GpuSharedHandles.AdoptPipe(Handle(channel, "replies")), FileAccess.Read, 1);
        _uploadSection = GpuSharedHandles.AdoptSection(Handle(channel, "upload"));
        _readbackSection = GpuSharedHandles.AdoptSection(Handle(channel, "readback"));
        _upload = GpuSharedHandles.Map(_uploadSection, Size(channel, "uploadBytes"));
        _readback = GpuSharedHandles.Map(_readbackSection, Size(channel, "readbackBytes"));
        var deadline = TimeSpan.FromMilliseconds(Math.Clamp(channel.GetProperty("deadlineMilliseconds").GetInt32(), 100, 120_000));
        _transport = new GpuWebGlPipeTransport(_commands, _replies, deadline);
        Session = new GpuWebGlSession(_transport, _upload, _readback);
        _workerExit = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(new IntPtr(Handle(channel, "worker")), ownsHandle: true) };
        _exitWatcher = new Thread(() =>
        {
            if (WaitHandle.WaitAny([_workerExit, _stopExitWatcher]) == 0) Session.Abandon();
        }) { IsBackground = true, Name = "Folio WebGL worker exit" };
        _exitWatcher.Start();
    }

    internal GpuWebGlSession Session { get; }

    /// <summary>Adopts the channel described by a load payload's <c>gpuChannel</c> object.</summary>
    internal static RemoteGpuChannel Adopt(JsonElement channel)
    {
        if (channel.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid GPU channel.");
        return new RemoteGpuChannel(channel);
    }

    /// <summary>A document GPU device backed by the shared session. Disposing it leaves the session open.</summary>
    internal Func<long, GpuDeviceResult> DocumentDeviceFactory => maxBytes => new(new RemoteGpuDevice(Session, maxBytes), null);

    private static long Handle(JsonElement channel, string name)
    {
        var value = channel.GetProperty(name).GetInt64();
        return value > 0 ? value : throw new InvalidDataException("Invalid GPU channel handle.");
    }

    private static long Size(JsonElement channel, string name)
    {
        var value = channel.GetProperty(name).GetInt64();
        return value is > 0 and <= GpuSharedHandles.MaxSectionBytes ? value : throw new InvalidDataException("Invalid GPU channel size.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopExitWatcher.Set();
        Session.Dispose();
        _transport.Dispose();
        _upload.Dispose(); _readback.Dispose();
        _uploadSection.Dispose(); _readbackSection.Dispose();
        if (Thread.CurrentThread != _exitWatcher) _exitWatcher.Join();
        _workerExit.Dispose();
        _stopExitWatcher.Dispose();
    }

    private sealed class RemoteGpuDevice(GpuWebGlSession session, long maxBytes) : IGpuDevice, INativeWebGlFactory
    {
        private int _disposed;
        private IGpuDevice? _surfaceDevice;
        // What the worker's device reported; unknown (Unavailable) until the worker has answered once.
        public GpuRendererKind RendererKind => _surfaceDevice?.RendererKind ?? (session.IsLost ? GpuRendererKind.Unavailable : session.RendererKind ?? GpuRendererKind.Unavailable);
        public string RendererIdentity => _surfaceDevice?.RendererIdentity ?? $"Folio GPU worker ({RendererKind})";
        public long ReservedBytes { get { session.Synchronize(); return session.ReservedBytes + (_surfaceDevice?.ReservedBytes ?? 0); } }
        public long PeakReservedBytes { get { session.Synchronize(); return session.PeakReservedBytes + (_surfaceDevice?.PeakReservedBytes ?? 0); } }
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        // The host-only GPU canvas clear (ClearNativeCanvas) does not go through the worker.
        public GpuSurfaceResult TryCreateSurface(int width, int height)
        {
            if (OperatingSystem.IsWindows())
            {
                _surfaceDevice ??= FolioGpuBackend.TryCreate(maxBytes).Device;
                if (_surfaceDevice is not null) return _surfaceDevice.TryCreateSurface(width, height);
            }
            return new(null, new GpuFailure(GpuFailureKind.Unavailable, "GPU surfaces are not available through the GPU worker."));
        }


        public bool TryCreate(WebGlContextOptions options, out INativeWebGlContext? context, out GpuFailure? failure) =>
            session.TryCreate(options, out context, out failure);
        public void Dispose()
        {
            Volatile.Write(ref _disposed, 1);
            _surfaceDevice?.Dispose();
        }
    }
}
