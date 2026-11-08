namespace Folio.Scripting.Gpu.Remote;

/// <summary>
/// Producer side of the upload arena shared with the GPU worker. Positions are logical and only
/// grow; the physical offset is <c>position % capacity</c>. An allocation never wraps: if it does
/// not fit before the end of the arena, the rest of that lap is skipped. Space is reclaimed only
/// when the worker acknowledges the batch that published it, so the worker can never observe a
/// region being rewritten while it may still read it.
/// </summary>
internal sealed class GpuUploadRing
{
    internal const int Alignment = 16;
    private readonly GpuArena _storage;
    private readonly Queue<(int Sequence, long End)> _published = new();
    private long _head, _tail, _batchStart;

    internal GpuUploadRing(GpuArena storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (storage.Length < Alignment || storage.Length % Alignment != 0)
            throw new ArgumentException("The upload arena must be a non-empty multiple of the alignment.", nameof(storage));
        _storage = storage;
    }

    internal long Capacity => _storage.Length;
    internal long Head => _head;
    internal long InFlightBytes => _head - _tail;
    internal long BatchStart => _batchStart;

    /// <summary>Copies <paramref name="data"/> into the arena; false means flush and wait for acknowledgements first.</summary>
    internal bool TryWrite(ReadOnlySpan<byte> data, out long logicalOffset)
    {
        logicalOffset = 0;
        if (data.Length > Capacity) throw new ArgumentException("Upload exceeds the arena capacity.", nameof(data));
        var length = (data.Length + Alignment - 1) & ~(Alignment - 1);
        var start = _head;
        var physical = start % Capacity;
        if (physical + length > Capacity) start += Capacity - physical; // skip to the next lap
        if (start + length - _tail > Capacity) return false;
        _storage.Write(start % Capacity, data);
        _head = start + length;
        logicalOffset = start;
        return true;
    }

    /// <summary>Closes the upload window of the batch being sealed: [BatchStart, Head).</summary>
    internal (long Start, long End) PublishBatch(int sequence)
    {
        var window = (_batchStart, _head);
        _published.Enqueue((sequence, _head));
        _batchStart = _head;
        return window;
    }

    /// <summary>The worker has finished with every batch up to and including <paramref name="sequence"/>.</summary>
    internal void Acknowledge(int sequence)
    {
        while (_published.TryPeek(out var batch) && batch.Sequence <= sequence)
        {
            _published.Dequeue();
            _tail = batch.End;
        }
    }
}
