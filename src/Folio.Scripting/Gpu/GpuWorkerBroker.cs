using System.Collections.Concurrent;
using Folio.Scripting.Ipc;

namespace Folio.Scripting.Gpu;

/// <summary>Parent-owned session. A terminal fault kills this worker job, never the content job.</summary>
internal sealed class GpuWorkerBroker : IDisposable
{
    private readonly Stream _input, _output;
    private readonly Action _terminate;
    private readonly IDisposable? _owner;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly TimeSpan _deadline;
    private int _lost, _sequence, _disposed;
    private readonly BlockingCollection<ExchangeWork> _requests = new(1);
    private readonly Thread _ioThread;
    internal bool IsIoThreadAlive => _ioThread.IsAlive;
    internal bool IsLost => Volatile.Read(ref _lost) != 0;

    // Test seam supports a stalled stream without invoking any native GPU API.
    internal GpuWorkerBroker(Stream input, Stream output, Action terminate, TimeSpan deadline, IDisposable? owner = null)
    {
        if (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(deadline));
        _input = input; _output = output; _terminate = terminate; _deadline = deadline; _owner = owner;
        _ioThread = new Thread(RunIo) { IsBackground = true, Name = "Folio GPU pipe" };
        _ioThread.Start();
    }

    internal static GpuWorkerBroker Start(string publishedContentHost, TimeSpan deadline)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var executable = Path.GetFullPath(publishedContentHost);
        if (!Path.GetFileName(executable).Equals("Folio.ContentHost.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(executable))
            throw new ArgumentException("The published Folio.ContentHost.exe is required.", nameof(publishedContentHost));
        // Existing launcher grants this empty file to the worker's fresh AppContainer.
        // It is not a GPU shared-memory channel and contains no document frame data.
        // A private folder: the launcher grants traverse on the lease's folder, never %TEMP% itself.
        var leaseDirectory = Path.Combine(Path.GetTempPath(), $"folio-gpu-{Guid.NewGuid():N}");
        Directory.CreateDirectory(leaseDirectory);
        var leasePath = Path.Combine(leaseDirectory, "lease.bin");
        File.WriteAllBytes(leasePath, []);
        WindowsContentLaunch? launch = null;
        try
        {
            launch = WindowsContentLauncher.Start(executable, "--gpu-worker", new ContentProcessLimits(256L * 1024 * 1024), leasePath);
            var owned = new WorkerLease(launch, leasePath);
            var broker = new GpuWorkerBroker(launch.Input, launch.Output, () => launch.Job!.Terminate(), deadline, owned);
            // Drain stderr with a fixed buffer; never accumulate worker-controlled diagnostics.
            new Thread(() =>
            {
                try { var buffer = new byte[1024]; while (launch.Error.Read(buffer) != 0) { } }
                catch (IOException) { } catch (ObjectDisposedException) { }
            }) { IsBackground = true, Name = "Folio GPU diagnostics" }.Start();
            return broker;
        }
        catch { launch?.Dispose(); Directory.Delete(leaseDirectory, recursive: true); throw; }
    }

    internal byte[] Exchange(byte[] request, CancellationToken cancellationToken = default)
    {
        if (request.Length != GpuWorkerProtocol.RequestBytes || GpuWorkerProtocol.Get(request, 0) != GpuWorkerProtocol.Magic)
            throw new ArgumentException("Invalid GPU request.", nameof(request));
        // Reject concurrent producers instead of queuing unbounded work behind a stuck driver.
        if (!_gate.Wait(0)) throw new InvalidOperationException("A GPU request is already outstanding.");
        try
        {
            if (IsLost) throw new IOException("GPU context lost.");
            cancellationToken.ThrowIfCancellationRequested();
            var packet = (byte[])request.Clone();
            var sequence = checked(++_sequence);
            GpuWorkerProtocol.Put(packet, 2, sequence);
            var work = new ExchangeWork(packet, sequence);
            _requests.Add(work);
            // A synchronous wait enforces deadline/cancellation even when all shared
            // pool threads are occupied. One persistent owned thread handles the
            // anonymous pipes; no per-command thread or unbounded work queue.
            try
            {
                if (!work.Completion.Task.Wait(_deadline, cancellationToken))
                    throw new TimeoutException("GPU worker reply deadline expired.");
            }
            catch (AggregateException) { return work.Completion.Task.GetAwaiter().GetResult(); }
            return work.Completion.Task.GetAwaiter().GetResult();
        }
        catch { Lose(); throw; }
        finally { _gate.Release(); }
    }

    private void RunIo()
    {
        foreach (var work in _requests.GetConsumingEnumerable())
        {
            try
            {
                if (IsLost) throw new IOException("GPU context lost.");
                _input.Write(work.Packet); _input.Flush();
                var header = new byte[GpuWorkerProtocol.HeaderBytes];
                _output.ReadExactly(header);
                var length = GpuWorkerProtocol.Get(header, 4);
                if (GpuWorkerProtocol.Get(header, 0) != GpuWorkerProtocol.Magic ||
                    GpuWorkerProtocol.Get(header, 1) != (GpuWorkerProtocol.Get(work.Packet, 1) | 0x40000000) ||
                    GpuWorkerProtocol.Get(header, 2) != work.Sequence || length < 0 || length > GpuWorkerProtocol.MaxPayloadBytes ||
                    GpuWorkerProtocol.Get(header, 3) is < 0 or > 1)
                    throw new InvalidDataException("Invalid GPU worker reply.");
                var response = new byte[GpuWorkerProtocol.HeaderBytes + length];
                header.CopyTo(response, 0);
                _output.ReadExactly(response.AsSpan(GpuWorkerProtocol.HeaderBytes));
                if (GpuWorkerProtocol.Get(response, 3) != 0) throw new IOException("GPU worker rejected the command.");
                work.Completion.TrySetResult(response);
            }
            catch (Exception error)
            {
                work.Completion.TrySetException(error);
                // Observe failures even if the caller has already timed out.
                _ = work.Completion.Task.Exception;
            }
        }
    }

    private sealed class ExchangeWork(byte[] packet, int sequence)
    {
        internal byte[] Packet { get; } = packet;
        internal int Sequence { get; } = sequence;
        internal TaskCompletionSource<byte[]> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void Lose()
    {
        if (Interlocked.Exchange(ref _lost, 1) != 0) return;
        _requests.CompleteAdding();
        _terminate(); // Closing the worker job breaks a blocked synchronous pipe operation.
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { Lose(); } finally { _owner?.Dispose(); }
    }

    private sealed class WorkerLease(WindowsContentLaunch launch, string path) : IDisposable
    {
        public void Dispose() { try { launch.Dispose(); } finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } }
    }
}
