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
    private int _lost, _sequence;
    internal bool IsLost => Volatile.Read(ref _lost) != 0;

    // Test seam supports a stalled stream without invoking any native GPU API.
    internal GpuWorkerBroker(Stream input, Stream output, Action terminate, TimeSpan deadline, IDisposable? owner = null)
    {
        if (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(deadline));
        _input = input; _output = output; _terminate = terminate; _deadline = deadline; _owner = owner;
    }

    internal static GpuWorkerBroker Start(string publishedContentHost, TimeSpan deadline)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var executable = Path.GetFullPath(publishedContentHost);
        if (!Path.GetFileName(executable).Equals("Folio.ContentHost.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(executable))
            throw new ArgumentException("The published Folio.ContentHost.exe is required.", nameof(publishedContentHost));
        // Existing launcher grants this empty file to the worker's fresh AppContainer.
        // It is not a GPU shared-memory channel and contains no document frame data.
        var leasePath = Path.Combine(Path.GetTempPath(), $"folio-gpu-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(leasePath, []);
        WindowsContentLaunch? launch = null;
        try
        {
            launch = WindowsContentLauncher.Start(executable, "--gpu-worker", new ContentProcessLimits(256L * 1024 * 1024), leasePath);
            var owned = new WorkerLease(launch, leasePath);
            var broker = new GpuWorkerBroker(launch.Input, launch.Output, () => launch.Job!.Terminate(), deadline, owned);
            // Drain stderr with a fixed buffer; never accumulate worker-controlled diagnostics.
            _ = Task.Run(async () => { try { var buffer = new byte[1024]; while (await launch.Error.ReadAsync(buffer) != 0) { } } catch (IOException) { } catch (ObjectDisposedException) { } });
            return broker;
        }
        catch { launch?.Dispose(); File.Delete(leasePath); throw; }
    }

    internal byte[] Exchange(byte[] request, CancellationToken cancellationToken = default)
        => ExchangeAsync(request, cancellationToken).GetAwaiter().GetResult();

    private async Task<byte[]> ExchangeAsync(byte[] request, CancellationToken cancellationToken)
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
            // Anonymous Windows pipes are synchronous. Isolate their blocking IO; the deadline
            // terminates the job to break pending IO, rather than relying on stream cancellation.
            var operation = Task.Run(() =>
            {
                _input.Write(packet); _input.Flush();
                var header = new byte[GpuWorkerProtocol.HeaderBytes];
                _output.ReadExactly(header);
                var length = GpuWorkerProtocol.Get(header, 4);
                if (GpuWorkerProtocol.Get(header, 0) != GpuWorkerProtocol.Magic ||
                    GpuWorkerProtocol.Get(header, 1) != (GpuWorkerProtocol.Get(packet, 1) | 0x40000000) ||
                    GpuWorkerProtocol.Get(header, 2) != sequence || length < 0 || length > GpuWorkerProtocol.MaxPayloadBytes ||
                    GpuWorkerProtocol.Get(header, 3) is < 0 or > 1)
                    throw new InvalidDataException("Invalid GPU worker reply.");
                var response = new byte[GpuWorkerProtocol.HeaderBytes + length];
                header.CopyTo(response, 0);
                _output.ReadExactly(response.AsSpan(GpuWorkerProtocol.HeaderBytes));
                return response;
            });
            // Observe a late failure even after the caller has timed out.
            _ = operation.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            var reply = await operation.WaitAsync(_deadline, cancellationToken).ConfigureAwait(false);
            if (GpuWorkerProtocol.Get(reply, 3) != 0) throw new IOException("GPU worker rejected the command.");
            return reply;
        }
        catch { Lose(); throw; }
        finally { _gate.Release(); }
    }

    private void Lose() { if (Interlocked.Exchange(ref _lost, 1) == 0) _terminate(); }
    public void Dispose() { Lose(); _owner?.Dispose(); }

    private sealed class WorkerLease(WindowsContentLaunch launch, string path) : IDisposable
    {
        public void Dispose() { try { launch.Dispose(); } finally { File.Delete(path); } }
    }
}
