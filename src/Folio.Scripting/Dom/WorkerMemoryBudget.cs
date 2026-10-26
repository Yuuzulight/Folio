namespace Folio.Scripting.Dom;

/// <summary>Document-wide reservation ledger for isolated worker VM heaps.</summary>
internal sealed class WorkerMemoryBudget(long maximum)
{
    private readonly object _gate = new();
    private readonly long _maximum = Math.Max(0, maximum);
    private long _reserved;

    public bool TryAcquire(long bytes)
    {
        bytes = Math.Max(0, bytes);
        lock (_gate)
        {
            if (bytes > _maximum - _reserved) return false;
            _reserved += bytes;
            return true;
        }
    }

    public void Release(long bytes)
    {
        lock (_gate) _reserved = Math.Max(0, _reserved - Math.Max(0, bytes));
    }

    internal long ReservedBytes { get { lock (_gate) return _reserved; } }
    internal long Maximum => _maximum;
}
