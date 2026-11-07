using System.Diagnostics;
using System.Runtime.Versioning;

namespace Folio.Scripting.Gpu.Remote;

/// <summary>
/// Startup record the parent writes to the GPU worker's control pipe: the handle values it
/// duplicated into the worker, the arena sizes and the limits. Fixed size; validated in full.
/// </summary>
[SupportedOSPlatform("windows")]
internal readonly record struct GpuWorkerStartup(
    long CommandsIn, long RepliesOut, long UploadSection, int UploadBytes, long ReadbackSection, int ReadbackBytes,
    long DeviceBudget, int CommandDeadlineMilliseconds)
{
    internal const int Magic = 0x31535047, Version = 1, Bytes = 60; // GPS1

    internal byte[] Encode() => new GpuPayloadWriter().I32(Magic).I32(Version).I64(CommandsIn).I64(RepliesOut)
        .I64(UploadSection).I32(UploadBytes).I64(ReadbackSection).I32(ReadbackBytes).I64(DeviceBudget).I32(CommandDeadlineMilliseconds)
        .Written.ToArray();

    internal static GpuWorkerStartup Decode(ReadOnlySpan<byte> bytes)
    {
        var reader = new GpuPayloadReader(bytes);
        if (bytes.Length != Bytes || reader.I32() != Magic || reader.I32() != Version) throw new InvalidDataException("Invalid GPU worker startup record.");
        var startup = new GpuWorkerStartup(reader.I64(), reader.I64(), reader.I64(), reader.I32(), reader.I64(), reader.I32(), reader.I64(), reader.I32());
        if (startup.UploadBytes <= 0 || startup.UploadBytes > GpuSharedHandles.MaxSectionBytes || startup.UploadBytes % GpuUploadRing.Alignment != 0 ||
            startup.ReadbackBytes <= 0 || startup.ReadbackBytes > GpuSharedHandles.MaxSectionBytes ||
            startup.DeviceBudget <= 0 || startup.DeviceBudget > GpuSharedHandles.MaxSectionBytes * 4 ||
            startup.CommandDeadlineMilliseconds is <= 0 or > 60_000)
            throw new InvalidDataException("GPU worker startup limits are out of range.");
        return startup;
    }
}

/// <summary>
/// Main loop of <c>Folio.ContentHost.exe --gpu-webgl</c>: executes command batches from the
/// document process on a real GPU device. A batch that outlives the command deadline terminates
/// this process, which closes the pipe and loses the session for the page without hanging it.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class GpuWebGlWorkerHost
{
    internal static void Run(Stream control)
    {
        var record = new byte[GpuWorkerStartup.Bytes];
        control.ReadExactly(record);
        var startup = GpuWorkerStartup.Decode(record);
        using var commands = new FileStream(GpuSharedHandles.AdoptPipe(startup.CommandsIn), FileAccess.Read, 1);
        using var replies = new FileStream(GpuSharedHandles.AdoptPipe(startup.RepliesOut), FileAccess.Write, 1);
        using var uploadSection = GpuSharedHandles.AdoptSection(startup.UploadSection);
        using var readbackSection = GpuSharedHandles.AdoptSection(startup.ReadbackSection);
        using var upload = GpuSharedHandles.Map(uploadSection, startup.UploadBytes);
        using var readback = GpuSharedHandles.Map(readbackSection, startup.ReadbackBytes);
        var created = FolioGpuBackend.TryCreate(startup.DeviceBudget);
        using var device = created.Device;
        INativeWebGlFactory factory = device as INativeWebGlFactory ?? new UnavailableFactory(created.Failure);
        using var dispatcher = new GpuWebGlDispatcher(factory, upload, readback);
        var deadline = TimeSpan.FromMilliseconds(startup.CommandDeadlineMilliseconds);
        while (GpuChannelFraming.Read(commands, GpuCommandBatch.MaxBatchBytes) is { } batch)
        {
            var execution = Task.Run(() => dispatcher.Execute(batch));
            if (!execution.Wait(deadline)) Process.GetCurrentProcess().Kill(); // runaway work: end the worker, never the page
            GpuChannelFraming.Write(replies, execution.Result);
        }
    }

    private sealed class UnavailableFactory(GpuFailure? failure) : INativeWebGlFactory
    {
        public long ReservedBytes => 0;
        public long PeakReservedBytes => 0;
        public bool TryCreate(WebGlContextOptions options, out INativeWebGlContext? context, out GpuFailure? result)
        {
            context = null;
            result = failure ?? new(GpuFailureKind.Unavailable, "The GPU worker has no device.");
            return false;
        }
    }
}
