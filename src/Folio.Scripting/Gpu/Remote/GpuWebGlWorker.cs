using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Folio.Scripting.Ipc;
using Microsoft.Win32.SafeHandles;

namespace Folio.Scripting.Gpu.Remote;

/// <summary>
/// Parent-owned GPU worker for one content process: a separate, invisible
/// <c>Folio.ContentHost.exe --gpu-webgl</c> in its own AppContainer and job. The parent creates the
/// direct pipe pair and the two shared sections, duplicates the worker's ends into the worker and,
/// once, the document's ends into the content child, then closes its own copies so each end lives
/// in exactly one process. Disposing terminates the worker job; the page sees the pipe close.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class GpuWebGlWorker : IDisposable
{
    internal const int UploadBytes = 16 * 1024 * 1024, ReadbackBytes = 16 * 1024 * 1024;
    internal static readonly TimeSpan CommandDeadline = TimeSpan.FromSeconds(10);
    private readonly WindowsContentLaunch _launch;
    private readonly string _leasePath;
    private GpuSharedHandles.SafeFileMappingHandle? _upload, _readback;
    private SafeFileHandle? _commandsWrite, _repliesRead;
    private int _disposed;

    private GpuWebGlWorker(WindowsContentLaunch launch, string leasePath) { _launch = launch; _leasePath = leasePath; }

    internal int ProcessId => _launch.Process.Id;
    internal bool HasExited => _launch.Process.HasExited;

    internal static GpuWebGlWorker Start(string contentHostPath, long deviceBudget)
    {
        // The launcher grants this empty file (and traverse on its own folder) to the worker's
        // AppContainer; it carries no data. A private folder keeps that grant off %TEMP% itself.
        var leaseDirectory = Path.Combine(Path.GetTempPath(), $"folio-gpu-{Guid.NewGuid():N}");
        Directory.CreateDirectory(leaseDirectory);
        var leasePath = Path.Combine(leaseDirectory, "lease.bin");
        File.WriteAllBytes(leasePath, []);
        WindowsContentLaunch? launch = null;
        GpuWebGlWorker? worker = null;
        try
        {
            launch = WindowsContentLauncher.Start(contentHostPath, "--gpu-webgl", new ContentProcessLimits(512L * 1024 * 1024, CpuRatePercent: 50), leasePath);
            worker = new GpuWebGlWorker(launch, leasePath);
            worker.Connect(deviceBudget);
            // Drain stderr with a fixed buffer; worker diagnostics are never accumulated.
            _ = Task.Run(async () => { try { var buffer = new byte[1024]; while (await launch.Error.ReadAsync(buffer) != 0) { } } catch (IOException) { } catch (ObjectDisposedException) { } });
            return worker;
        }
        catch
        {
            if (worker is not null) worker.Dispose();
            else { launch?.Dispose(); DeleteLease(leasePath); }
            throw;
        }
    }

    private void Connect(long deviceBudget)
    {
        var target = _launch.Process.SafeHandle;
        _upload = GpuSharedHandles.CreateSection(UploadBytes);
        _readback = GpuSharedHandles.CreateSection(ReadbackBytes);
        var (commandsRead, commandsWrite) = GpuSharedHandles.CreatePipe();
        var (repliesRead, repliesWrite) = GpuSharedHandles.CreatePipe();
        _commandsWrite = commandsWrite;
        _repliesRead = repliesRead;
        using (commandsRead)
        using (repliesWrite)
        {
            var startup = new GpuWorkerStartup(
                GpuSharedHandles.DuplicatePipe(commandsRead, target), GpuSharedHandles.DuplicatePipe(repliesWrite, target),
                GpuSharedHandles.DuplicateSection(_upload, target), UploadBytes,
                GpuSharedHandles.DuplicateSection(_readback, target), ReadbackBytes,
                deviceBudget, (int)CommandDeadline.TotalMilliseconds);
            _launch.Input.Write(startup.Encode());
            _launch.Input.Flush();
        }
    }

    /// <summary>
    /// Duplicates the document's ends into <paramref name="child"/> and describes them for the load
    /// payload. One-shot: afterwards the parent holds no channel handle at all.
    /// </summary>
    internal JsonObject HandOff(SafeHandle child)
    {
        if (_commandsWrite is null || _repliesRead is null || _upload is null || _readback is null)
            throw new InvalidOperationException("The GPU channel was already handed off.");
        try
        {
            return new JsonObject
            {
                ["commands"] = GpuSharedHandles.DuplicatePipe(_commandsWrite, child),
                ["replies"] = GpuSharedHandles.DuplicatePipe(_repliesRead, child),
                ["upload"] = GpuSharedHandles.DuplicateSection(_upload, child),
                ["uploadBytes"] = UploadBytes,
                ["readback"] = GpuSharedHandles.DuplicateSection(_readback, child),
                ["readbackBytes"] = ReadbackBytes,
                ["worker"] = GpuSharedHandles.DuplicateForWait(_launch.Process.SafeHandle, child),
                ["deadlineMilliseconds"] = (int)(CommandDeadline.TotalMilliseconds * 1.5),
            };
        }
        finally { ReleaseParentCopies(); }
    }

    private void ReleaseParentCopies()
    {
        _commandsWrite?.Dispose(); _commandsWrite = null;
        _repliesRead?.Dispose(); _repliesRead = null;
        _upload?.Dispose(); _upload = null;
        _readback?.Dispose(); _readback = null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { ReleaseParentCopies(); _launch.Dispose(); }
        finally { DeleteLease(_leasePath); }
    }

    private static void DeleteLease(string leasePath)
    {
        try { Directory.Delete(Path.GetDirectoryName(leasePath)!, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
