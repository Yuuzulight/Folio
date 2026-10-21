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
    private int _stopped;

    public ContentProcessSupervisor(ContentProcessLimits? limits = null) => _limits = limits ?? new ContentProcessLimits();
    public bool IsStopped => Volatile.Read(ref _stopped) != 0;
    public event Action<string>? Stopped;
    public CancellationToken CancellationToken => _shutdown.Token;

    public void Heartbeat() => Interlocked.Exchange(ref _lastHeartbeat, Environment.TickCount64);

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
        if (Environment.TickCount64 - Interlocked.Read(ref _lastHeartbeat) <= _limits.EffectiveHeartbeatTimeout.TotalMilliseconds) return true;
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
