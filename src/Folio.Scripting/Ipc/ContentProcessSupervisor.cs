namespace Folio.Scripting.Ipc;

public sealed record ContentProcessLimits(long MemoryBytes = 256L * 1024 * 1024, TimeSpan? HeartbeatTimeout = null, int CpuRatePercent = 25)
{
    public TimeSpan EffectiveHeartbeatTimeout => HeartbeatTimeout ?? TimeSpan.FromSeconds(5);
}

/// <summary>Host-side lifecycle guard shared by in-process and external content transports.</summary>
public sealed class ContentProcessSupervisor : IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ContentProcessLimits _limits;
    private long _lastHeartbeat = Environment.TickCount64;
    private int _receivedHeartbeat;
    private int _stopped;

    public ContentProcessSupervisor(ContentProcessLimits? limits = null) => _limits = limits ?? new ContentProcessLimits();
    public bool IsStopped => Volatile.Read(ref _stopped) != 0;
    public event Action<string>? Stopped;
    public CancellationToken CancellationToken => _shutdown.Token;

    public void Heartbeat()
    {
        Interlocked.Exchange(ref _lastHeartbeat, Environment.TickCount64);
        Volatile.Write(ref _receivedHeartbeat, 1);
    }

    public bool CheckMemory(long workingSetBytes)
    {
        if (IsStopped) return false;
        if (workingSetBytes <= _limits.MemoryBytes) return true;
        Stop("content process memory limit exceeded");
        return false;
    }

    public bool CheckHeartbeat()
    {
        if (IsStopped) return false;
        var elapsed = Environment.TickCount64 - Interlocked.Read(ref _lastHeartbeat);
        // Process startup includes loading the runtime and opening the sandboxed
        // host. Under a saturated machine that can exceed the normal liveness
        // interval before the child has read its first heartbeat. Give only
        // that pre-handshake window a bounded grace period; once the child has
        // answered, the configured watchdog remains strict.
        var startupGrace = Volatile.Read(ref _receivedHeartbeat) == 0 && _limits.EffectiveHeartbeatTimeout > TimeSpan.Zero
            ? _limits.EffectiveHeartbeatTimeout.TotalMilliseconds + 10_000
            : _limits.EffectiveHeartbeatTimeout.TotalMilliseconds;
        if (elapsed <= startupGrace) return true;
        Stop("content process heartbeat timed out");
        return false;
    }

    public void Stop(string reason)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _shutdown.Cancel();
        Stopped?.Invoke(reason);
    }

    public void Dispose() { Stop("content process disposed"); _shutdown.Dispose(); }
}
