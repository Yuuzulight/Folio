using System.Collections;
using System.Collections.Concurrent;

namespace Folio.Scripting.Dom;

/// <summary>Worker-owned object URLs with live access to the creator's URL registry.</summary>
internal sealed class WorkerBlobSources(IReadOnlyDictionary<string, string>? parent, ScriptMessageBudget budget)
    : IReadOnlyDictionary<string, string>, IDisposable
{
    private readonly ConcurrentDictionary<string, (string Source, ScriptMessageBudget.Reservation Reservation)> _sources = new(StringComparer.Ordinal);
    public string Create(string source)
    {
        var reservation = budget.Reserve(source.Length * 2L + 64);
        try
        {
            var url = "blob:folio-" + Guid.NewGuid().ToString("N");
            if (!_sources.TryAdd(url, (source, reservation))) throw new InvalidOperationException("Duplicate object URL.");
            return url;
        }
        catch { reservation.Dispose(); throw; }
    }
    public void Revoke(string url)
    {
        if (_sources.TryRemove(url, out var entry)) entry.Reservation.Dispose();
    }
    public bool TryGetValue(string key, out string value)
    {
        if (_sources.TryGetValue(key, out var entry)) { value = entry.Source; return true; }
        if (parent?.TryGetValue(key, out value!) == true) return true;
        value = null!;
        return false;
    }
    public string this[string key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException();
    public bool ContainsKey(string key) => TryGetValue(key, out _);
    public IEnumerable<string> Keys => _sources.Keys.Concat(parent?.Keys ?? []).Distinct(StringComparer.Ordinal);
    public IEnumerable<string> Values => Keys.Select(key => this[key]);
    public int Count => Keys.Count();
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => Keys.Select(key => new KeyValuePair<string, string>(key, this[key])).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Dispose() { foreach (var key in _sources.Keys) Revoke(key); }
}
