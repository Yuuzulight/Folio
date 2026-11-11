using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Folio.Scripting.Gpu.Remote;

/// <summary>One bounded, owned pipe pump for a WebGL session, including command writes.</summary>
internal sealed class GpuWebGlPipeTransport : IGpuWebGlTransport, IDisposable
{
    private readonly Stream _commands, _replies;
    private readonly TimeSpan _deadline;
    private readonly BlockingCollection<Work> _requests = new(1);
    private readonly SemaphoreSlim _admission = new(1);
    private readonly Thread _thread;
    private readonly object _handleGate = new();
    private SafeWaitHandle? _threadHandle;
    private int _stopped;
    internal bool IsPumpAlive => _thread.IsAlive;

    internal GpuWebGlPipeTransport(Stream commands, Stream replies, TimeSpan deadline)
    {
        if (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromMinutes(2)) throw new ArgumentOutOfRangeException(nameof(deadline));
        _commands = commands; _replies = replies; _deadline = deadline;
        _thread = new Thread(Pump) { IsBackground = true, Name = "Folio WebGL pipe" };
        _thread.Start();
    }

    public byte[] Exchange(byte[] batch)
    {
        if (batch.Length > GpuCommandBatch.MaxBatchBytes) throw new InvalidDataException("GPU command batch exceeds its limit.");
        if (!_admission.Wait(0)) throw new InvalidOperationException("A GPU exchange is already outstanding.");
        try
        {
            if (Volatile.Read(ref _stopped) != 0) throw new IOException("The GPU channel is closed.");
            var work = new Work((byte[])batch.Clone());
            try { _requests.Add(work); }
            catch (InvalidOperationException) { throw new IOException("The GPU channel is closed."); }
            try
            {
                if (!work.Reply.Task.Wait(_deadline)) throw new TimeoutException("The GPU worker did not answer in time.");
            }
            catch (AggregateException) { return work.Reply.Task.GetAwaiter().GetResult(); }
            return work.Reply.Task.GetAwaiter().GetResult();
        }
        catch { Dispose(); throw; }
        finally { _admission.Release(); }
    }

    private void Pump()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var handle = OpenThread(1, false, GetCurrentThreadId()); // THREAD_TERMINATE for CancelSynchronousIo
                if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Cannot own the GPU I/O thread handle."); }
                lock (_handleGate) _threadHandle = handle;
            }
            foreach (var work in _requests.GetConsumingEnumerable())
            {
                try
                {
                    if (Volatile.Read(ref _stopped) != 0) throw new IOException("The GPU channel is closed.");
                    GpuChannelFraming.Write(_commands, work.Batch);
                    if (Volatile.Read(ref _stopped) != 0) throw new IOException("The GPU channel is closed.");
                    work.Reply.TrySetResult(GpuChannelFraming.Read(_replies, GpuWebGlWire.MaxReplyBytes)
                        ?? throw new IOException("The GPU worker closed the channel."));
                }
                catch (Exception error)
                {
                    work.Reply.TrySetException(error);
                    _ = work.Reply.Task.Exception; // Caller may already have timed out.
                }
            }
        }
        catch (Exception error)
        {
            Interlocked.Exchange(ref _stopped, 1);
            _requests.CompleteAdding();
            while (_requests.TryTake(out var work))
            { work.Reply.TrySetException(error); _ = work.Reply.Task.Exception; }
        }
        finally
        {
            // Broken-pipe cleanup must not escape a background thread or skip
            // release of the owned native thread handle.
            try { _commands.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
            try { _replies.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
            lock (_handleGate) { _threadHandle?.Dispose(); _threadHandle = null; }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0) _requests.CompleteAdding();
        // Cancellation can land between framed writes/reads. Repeat until the
        // owned pump observes stop, without waiting indefinitely for a peer.
        for (var attempt = 0; attempt < 20 && _thread.IsAlive; attempt++)
        {
            if (OperatingSystem.IsWindows())
                lock (_handleGate) if (_threadHandle is not null) CancelSynchronousIo(_threadHandle);
            if (_thread.Join(10)) break;
        }
    }

    private sealed class Work(byte[] batch)
    {
        internal byte[] Batch { get; } = batch;
        internal TaskCompletionSource<byte[]> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeWaitHandle OpenThread(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint threadId);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelSynchronousIo(SafeWaitHandle thread);
}
