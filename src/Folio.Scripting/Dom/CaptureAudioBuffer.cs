using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Folio.Scripting.Dom;

/// <summary>One bounded device history, with independent non-destructive consumer cursors.</summary>
/// <remarks>Providers publish immutable snapshots. No device callback queues document tasks.
/// Oldest blocks are dropped after 250 ms (source time or wall time), 32 blocks, or 256 KiB.
/// A slow consumer skips missing sequences; it never replays the provider's current block.
/// Timestamps are source-relative seconds, assigned once at ingress because the public
/// provider contract has no device timestamp. Closed sessions cannot publish again.</remarks>
internal sealed class CaptureAudioBuffer(MediaResourceBudget budget, Func<double>? clock = null) : IDisposable
{
    private const double HistorySeconds = .25;
    private const int MaxBytes = 256 * 1024, MaxBlocks = 32;
    private readonly object _gate = new();
    private readonly Queue<Block> _blocks = new();
    private readonly Func<double> _clock = clock ?? (() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
    private MediaResourceBudget.Lease? _lease = budget.AcquireBytes();
    private long _sequence;
    private long _captureSequence;
    private WeakReference<float[]>? _legacyArray;
    private int _legacyOffset, _legacyLength, _legacyRate, _legacyChannels;
    private long _bytes;
    private double _end;
    private bool _closed;
    private sealed record Block(long Sequence, double Timestamp, double Arrived, MediaAudioFrame Frame)
    {
        internal double Duration => (double)Frame.Samples.Length / Frame.Channels / Frame.SampleRate;
        internal long Bytes => (long)Frame.Samples.Length * sizeof(float) + 64;
    }

    internal sealed class Cursor
    {
        internal long Sequence;
        internal int Offset;
        internal double Timestamp { get; set; }
    }

    internal Cursor CreateCursor()
    {
        lock (_gate)
        {
            Trim(_clock());
            return new Cursor { Sequence = _blocks.TryPeek(out var first) ? first.Sequence : _sequence + 1, Timestamp = _end };
        }
    }

    internal void Seed(MediaAudioFrame frame)
    {
        lock (_gate) if (_sequence == 0) Push(frame);
    }

    internal void Push(MediaAudioFrame frame, bool retain = true)
    {
        // Bound arithmetic and validation work before inspecting provider memory.
        if (frame.SampleRate is <= 0 or > 384000 || frame.Channels is <= 0 or > 32 ||
            frame.Samples.Length == 0 || frame.Samples.Length % frame.Channels != 0 ||
            (long)frame.Samples.Length * sizeof(float) + 64 > MaxBytes) return;
        foreach (var sample in frame.Samples.Span) if (!float.IsFinite(sample)) return;
        lock (_gate)
        {
            if (_closed) return;
            // Authoritative capture sequence survives serialized copies. Keep its high
            // water mark even after clear/eviction/budget drops, until this request closes.
            if (frame.CaptureSequence > 0)
            {
                if (frame.CaptureSequence <= _captureSequence) return;
                _captureSequence = frame.CaptureSequence;
            }
            else if (MemoryMarshal.TryGetArray(frame.Samples, out var segment) && segment.Array is { } array)
            {
                if (_legacyArray is not null && _legacyArray.TryGetTarget(out var previous) &&
                    ReferenceEquals(previous, array) && _legacyOffset == segment.Offset &&
                    _legacyLength == segment.Count && _legacyRate == frame.SampleRate && _legacyChannels == frame.Channels) return;
                // Remember identity without retaining an unbudgeted array after eviction.
                _legacyArray = new(array);
                _legacyOffset = segment.Offset;
                _legacyLength = segment.Count;
                _legacyRate = frame.SampleRate;
                _legacyChannels = frame.Channels;
            }
            else if (_blocks.LastOrDefault() is { } last && last.Frame.SampleRate == frame.SampleRate &&
                last.Frame.Channels == frame.Channels && last.Frame.Samples.Equals(frame.Samples)) return;
            var duration = (double)frame.Samples.Length / frame.Channels / frame.SampleRate;
            var block = new Block(++_sequence, _end, _clock(), frame);
            _end += duration;
            // Oversize blocks are gaps, not a reason to retain an unbounded snapshot.
            if (!retain || duration > HistorySeconds) { ClearCore(); return; }
            Trim(block.Arrived);
            while (_blocks.Count > 0 && (_blocks.Count >= MaxBlocks || _bytes + block.Bytes > MaxBytes ||
                _end - _blocks.Peek().Timestamp > HistorySeconds)) DropOldest();
            while (_lease is not null && !_lease.Resize(_bytes + block.Bytes) && _blocks.Count > 0) DropOldest();
            if (_lease is null || !_lease.Resize(_bytes + block.Bytes)) return;
            _blocks.Enqueue(block);
            _bytes += block.Bytes;
        }
    }

    internal bool Read(Cursor cursor, double seconds, out MediaAudioFrame frame)
    {
        lock (_gate)
        {
            frame = default;
            if (_closed || !double.IsFinite(seconds) || seconds <= 0) return false;
            Trim(_clock());
            foreach (var block in _blocks)
            {
                if (block.Sequence < cursor.Sequence) continue;
                if (block.Sequence != cursor.Sequence) { cursor.Sequence = block.Sequence; cursor.Offset = 0; }
                var count = Math.Min(block.Frame.Samples.Length - cursor.Offset,
                    (int)Math.Min(int.MaxValue / block.Frame.Channels, Math.Max(1, Math.Floor(seconds * block.Frame.SampleRate))) * block.Frame.Channels);
                cursor.Timestamp = block.Timestamp + (double)cursor.Offset / block.Frame.Channels / block.Frame.SampleRate;
                frame = block.Frame with { Samples = block.Frame.Samples.Slice(cursor.Offset, count) };
                cursor.Offset += count;
                if (cursor.Offset == block.Frame.Samples.Length) { cursor.Sequence++; cursor.Offset = 0; }
                return true;
            }
            return false;
        }
    }

    internal void Skip(Cursor cursor)
    {
        lock (_gate) { cursor.Sequence = _sequence + 1; cursor.Offset = 0; cursor.Timestamp = _end; }
    }

    internal void Clear() { lock (_gate) ClearCore(); }
    private void ClearCore() { _blocks.Clear(); _bytes = 0; _lease?.Resize(0); }
    private void DropOldest() { _bytes -= _blocks.Dequeue().Bytes; _lease?.Resize(_bytes); }
    private void Trim(double now)
    {
        while (_blocks.Count > 0 && now - _blocks.Peek().Arrived >= HistorySeconds) DropOldest();
    }
    public void Dispose()
    {
        lock (_gate) { _closed = true; ClearCore(); _lease?.Dispose(); _lease = null; }
    }
}

/// <summary>Reader lifetime belongs to one media element; history belongs to the capture request.</summary>
internal sealed class CaptureAudioReader(CaptureAudioBuffer buffer, Func<bool> live) : IDisposable
{
    private readonly CaptureAudioBuffer.Cursor _cursor = buffer.CreateCursor();
    private bool _disposed;
    internal double Timestamp => _cursor.Timestamp;
    internal bool IsLive => !_disposed && live();
    internal bool Read(double seconds, out MediaAudioFrame frame)
    {
        frame = default;
        if (_disposed || !live()) { buffer.Skip(_cursor); return false; }
        return buffer.Read(_cursor, seconds, out frame);
    }
    internal void Skip() => buffer.Skip(_cursor);
    public void Dispose() { _disposed = true; Skip(); }
}

/// <summary>Reader for camera video frames; lifetime belongs to one sender or element.</summary>
internal sealed class CaptureVideoReader(Func<MediaCaptureVideoFrame?> getFrame, Func<bool> live) : IDisposable
{
    private bool _disposed;
    internal bool IsLive => !_disposed && live();
    internal bool Read(out MediaCaptureVideoFrame frame)
    {
        frame = default;
        if (_disposed || !live()) return false;
        var current = getFrame();
        if (current is not { IsValid: true } valid) return false;
        frame = valid;
        return true;
    }
    public void Dispose() => _disposed = true;
}
