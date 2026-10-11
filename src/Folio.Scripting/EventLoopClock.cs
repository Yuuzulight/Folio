namespace Folio.Scripting;

/// <summary>
/// Clock abstraction for the HTML event loop and performance timing (study 17, #449, #450).
/// In production, uses real high-resolution system time; in test mode, uses a controllable virtual clock.
/// </summary>
public interface IEventLoopClock
{
    /// <summary>Current time offset from timeOrigin in milliseconds.</summary>
    double NowMs { get; }

    /// <summary>Origin timestamp in UTC.</summary>
    DateTimeOffset TimeOrigin { get; }
}

/// <summary>
/// Real-time clock implementation using Stopwatch and high-resolution timer.
/// </summary>
public sealed class RealEventLoopClock : IEventLoopClock
{
    private readonly long _startTimestamp;
    private readonly DateTimeOffset _timeOrigin;

    public RealEventLoopClock()
    {
        _timeOrigin = DateTimeOffset.UtcNow;
        _startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    public DateTimeOffset TimeOrigin => _timeOrigin;

    public double NowMs => System.Diagnostics.Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
}

/// <summary>
/// Virtual clock for deterministic testing of timers, animations, and settled state.
/// Only advances when explicitly commanded.
/// </summary>
public sealed class VirtualEventLoopClock : IEventLoopClock
{
    private double _nowMs;

    public VirtualEventLoopClock(DateTimeOffset? origin = null)
    {
        TimeOrigin = origin ?? new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
        _nowMs = 0;
    }

    public DateTimeOffset TimeOrigin { get; }

    public double NowMs => _nowMs;

    public void Advance(double deltaMs)
    {
        if (deltaMs < 0)
            throw new ArgumentOutOfRangeException(nameof(deltaMs), "Cannot rewind virtual clock.");
        _nowMs += deltaMs;
    }

    public void SetNow(double nowMs)
    {
        if (nowMs < _nowMs)
            throw new ArgumentOutOfRangeException(nameof(nowMs), "Cannot rewind virtual clock.");
        _nowMs = nowMs;
    }
}
