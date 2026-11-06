namespace Folio.Scripting.Native;

/// <summary>Thread-safe FIFO cache bounded by both entry count and source size.</summary>
/// <remarks>Factories run outside the lock, with one lazy computation per retained key.</remarks>
internal sealed class BoundedScriptCache<TKey, TValue>(int maxEntries, int maxSourceCharacters) where TKey : notnull
{
    private readonly object _gate = new();
    private sealed class Entry(Lazy<TValue> value, int weight, LinkedListNode<TKey> node)
    {
        internal Lazy<TValue> Value { get; } = value;
        internal int Weight { get; } = weight;
        internal LinkedListNode<TKey> Node { get; } = node;
    }
    private readonly Dictionary<TKey, Entry> _entries = new();
    private readonly LinkedList<TKey> _order = new();
    private int _sourceCharacters;

    internal int Count { get { lock (_gate) return _entries.Count; } }
    internal int SourceCharacters { get { lock (_gate) return _sourceCharacters; } }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2091",
        Justification = "Every Lazy<TValue> has an explicit value factory; it never reflects for a parameterless TValue constructor.")]
    internal TValue GetOrAdd(TKey key, int sourceCharacters, Func<TKey, TValue> factory)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceCharacters);
        if (sourceCharacters > maxSourceCharacters || maxEntries <= 0) return factory(key);
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                while (_entries.Count >= maxEntries || _sourceCharacters > maxSourceCharacters - sourceCharacters)
                    Remove(_order.First!.Value);
                entry = new Entry(new Lazy<TValue>(() => factory(key), LazyThreadSafetyMode.ExecutionAndPublication),
                    sourceCharacters, _order.AddLast(key));
                _entries.Add(key, entry);
                _sourceCharacters += sourceCharacters;
            }
        }
        try { return entry.Value.Value; }
        catch
        {
            lock (_gate)
                if (_entries.TryGetValue(key, out var retained) && ReferenceEquals(retained, entry)) Remove(key);
            throw;
        }
    }

    private void Remove(TKey key)
    {
        var entry = _entries[key];
        _entries.Remove(key);
        _order.Remove(entry.Node);
        _sourceCharacters -= entry.Weight;
    }
}
