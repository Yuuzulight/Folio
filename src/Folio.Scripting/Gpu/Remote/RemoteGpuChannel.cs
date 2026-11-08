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
    private readonly GpuSharedHandles.SafeFileMappingHandle _uploadSection, _readbackSection;
    private readonly MappedGpuArena _upload, _readback;
    private readonly ManualResetEvent _workerExit;
    private readonly RegisteredWaitHandle _exitWait;

    private RemoteGpuChannel(JsonElement channel)
    {
        _commands = new FileStream(GpuSharedHandles.AdoptPipe(Handle(channel, "commands")), FileAccess.Write, 1);
        _replies = new FileStream(GpuSharedHandles.AdoptPipe(Handle(channel, "replies")), FileAccess.Read, 1);
        _uploadSection = GpuSharedHandles.AdoptSection(Handle(channel, "upload"));
        _readbackSection = GpuSharedHandles.AdoptSection(Handle(channel, "readback"));
        _upload = GpuSharedHandles.Map(_uploadSection, Size(channel, "uploadBytes"));
        _readback = GpuSharedHandles.Map(_readbackSection, Size(channel, "readbackBytes"));
        var deadline = TimeSpan.FromMilliseconds(Math.Clamp(channel.GetProperty("deadlineMilliseconds").GetInt32(), 100, 120_000));
        Session = new GpuWebGlSession(new PipeTransport(_commands, _replies, deadline), _upload, _readback);
        _workerExit = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(new IntPtr(Handle(channel, "worker")), ownsHandle: true) };
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_workerExit, (_, _) => Session.Abandon(), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    internal GpuWebGlSession Session { get; }

    /// <summary>Adopts the channel described by a load payload's <c>gpuChannel</c> object.</summary>
    internal static RemoteGpuChannel Adopt(JsonElement channel)
    {
        if (channel.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid GPU channel.");
        return new RemoteGpuChannel(channel);
    }

    /// <summary>A document GPU device backed by the shared session. Disposing it leaves the session open.</summary>
    internal Func<long, GpuDeviceResult> DocumentDeviceFactory => _ => new(new RemoteGpuDevice(Session), null);

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
        _exitWait.Unregister(null);
        Session.Dispose();
        _commands.Dispose(); _replies.Dispose();
        _upload.Dispose(); _readback.Dispose();
        _uploadSection.Dispose(); _readbackSection.Dispose();
        _workerExit.Dispose();
    }

    /// <summary>Synchronous exchange with a deadline; the worker enforces a shorter one itself.</summary>
    private sealed class PipeTransport(Stream commands, Stream replies, TimeSpan deadline) : IGpuWebGlTransport
    {
        public byte[] Exchange(byte[] batch)
        {
            GpuChannelFraming.Write(commands, batch);
            var reply = Task.Run(() => GpuChannelFraming.Read(replies, GpuWebGlWire.MaxReplyBytes));
            if (!reply.Wait(deadline)) throw new TimeoutException("The GPU worker did not answer in time.");
            return reply.Result ?? throw new IOException("The GPU worker closed the channel.");
        }
    }

    private sealed class RemoteGpuDevice(GpuWebGlSession session) : IGpuDevice, INativeWebGlFactory
    {
        private int _disposed;
        // What the worker's device reported; unknown (Unavailable) until the worker has answered once.
        public GpuRendererKind RendererKind => session.IsLost ? GpuRendererKind.Unavailable : session.RendererKind ?? GpuRendererKind.Unavailable;
        public string RendererIdentity => $"Folio GPU worker ({RendererKind})";
        public long ReservedBytes { get { session.Synchronize(); return session.ReservedBytes; } }
        public long PeakReservedBytes { get { session.Synchronize(); return session.PeakReservedBytes; } }
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        // Canvas surface clears are not routed through the worker; callers already handle this failure.
        public GpuSurfaceResult TryCreateSurface(int width, int height) =>
            new(null, new GpuFailure(GpuFailureKind.Unavailable, "GPU surfaces are not available through the GPU worker."));
        public bool TryCreate(WebGlContextOptions options, out INativeWebGlContext? context, out GpuFailure? failure) =>
            session.TryCreate(options, out context, out failure);
        public void Dispose() => Volatile.Write(ref _disposed, 1);
    }
}
