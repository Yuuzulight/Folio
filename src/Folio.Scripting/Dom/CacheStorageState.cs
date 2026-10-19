using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Folio.Scripting.Dom;

// The host owns the committed cache, not a script-visible Map. One artifact's
// named caches are replaced with a single storage write before publishing state.
internal sealed class CacheStorageState : ICacheStorageBackend
{
    internal sealed record Request(string Url, string Method, Dictionary<string, string> Headers);
    internal sealed record Entry(Request Request, int Status, string StatusText,
        Dictionary<string, string> Headers, byte[] Bytes, bool BodyNull, string Url, string Type);
    internal sealed class Cache(CacheStorageState owner, string? name, List<Entry> entries) : CacheStorageHandle(owner)
    {
        internal string? Name = name;
        internal List<Entry> Entries = entries;
    }
    internal sealed record Query(bool IgnoreSearch, bool IgnoreMethod, bool IgnoreVary, bool RequestOnly = false);
    private sealed record StoredCache(string Name, List<Entry> Entries);
    private sealed record Snapshot(int Version, List<StoredCache> Caches);
    private sealed class HostStores
    {
        internal readonly Dictionary<string, WeakReference<CacheStorageState>> Artifacts = new(StringComparer.Ordinal);
        internal readonly HashSet<string> Initializing = new(StringComparer.Ordinal);
    }
    private static readonly ConditionalWeakTable<IArtifactStorage, HostStores> Hosts = new();
    private readonly object _gate = new();
    private bool _persisting;
    private readonly IArtifactStorage? _storage;
    private readonly string _key;
    private readonly Dictionary<string, Cache> _named = new(StringComparer.Ordinal);
    private readonly List<WeakReference<Cache>> _all = [];

    internal static string StorageKey(string artifact) => "folio/cache-v2/" + artifact.Length + ":" + artifact;

    private CacheStorageState(IArtifactStorage? storage, string artifact, Uri? baseUri = null)
    {
        _storage = storage;
        // A length-delimited namespace cannot collide with another artifact's ID.
        _key = StorageKey(artifact);
        if (storage is null) return;
        string? serialized;
        try { serialized = storage.Read(_key); }
        catch (Exception) { throw WebIdlException.DomException("UnknownError", "The host could not read Cache API storage."); }
        if (serialized is null)
        {
            ImportLegacy(storage, artifact, baseUri);
            return;
        }
        try
        {
            var snapshot = JsonSerializer.Deserialize<Snapshot>(serialized)
                ?? throw new InvalidDataException("Missing cache snapshot.");
            if (snapshot.Version != 1 || snapshot.Caches is null) throw new InvalidDataException("Unsupported cache snapshot.");
            foreach (var stored in snapshot.Caches)
            {
                if (stored.Name is null || stored.Entries is null || _named.ContainsKey(stored.Name))
                    throw new InvalidDataException("Invalid cache names.");
                foreach (var entry in stored.Entries) Validate(entry);
                var cache = new Cache(this, stored.Name, stored.Entries);
                _named.Add(stored.Name, cache);
                _all.Add(new(cache));
            }
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or NullReferenceException or WebIdlException)
        {
            throw WebIdlException.DomException("UnknownError", "The persisted Cache API data is invalid.");
        }
    }

    internal static CacheStorageState For(IArtifactStorage? storage, string artifact, Uri? baseUri = null)
    {
        if (storage is null) return new(null, artifact);
        var host = Hosts.GetValue(storage, _ => new());
        lock (host.Artifacts)
        {
            if (!host.Artifacts.TryGetValue(artifact, out var reference) || !reference.TryGetTarget(out var state))
            {
                if (!host.Initializing.Add(artifact)) throw WebIdlException.DomException("UnknownError", "The host cannot reenter cache initialization.");
                try { state = new(storage, artifact, baseUri); host.Artifacts[artifact] = new(state); }
                finally { host.Initializing.Remove(artifact); }
            }
            return state;
        }
    }

    private void ImportLegacy(IArtifactStorage storage, string artifact, Uri? baseUri)
    {
        var prefix = artifact + ":service-worker-cache/";
        try
        {
            foreach (var key in storage.Keys(prefix))
            {
                if (!key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var parts = key[prefix.Length..].Split('/');
                // encodeURIComponent never leaves a literal colon or slash in
                // either segment. Reject lookalike keys from a longer artifact ID.
                if (parts.Length != 2 || parts.Any(part => part.Contains(':'))) continue;
                var name = Uri.UnescapeDataString(parts[0]);
                var rawUrl = Uri.UnescapeDataString(parts[1]);
                if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
                {
                    if (baseUri is null || !Uri.TryCreate(baseUri, rawUrl, out url) || url.Scheme is not ("http" or "https"))
                        throw new InvalidDataException("Legacy relative cache requests need an HTTP(S) document base URI.");
                }
                using var record = JsonDocument.Parse(storage.Read(key) ?? throw new InvalidDataException("Missing legacy cache entry."));
                var data = record.RootElement;
                var headers = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var pair in data.GetProperty("headers").EnumerateArray())
                {
                    if (pair.GetArrayLength() != 2) throw new InvalidDataException("Invalid legacy cache header.");
                    headers.Add(pair[0].GetString()!.ToLowerInvariant(), pair[1].GetString()!);
                }
                // The prototype format did not store request headers, so Vary
                // cannot be reconstructed safely. Preserve the backup and fail.
                if (headers.TryGetValue("vary", out var vary) && !string.IsNullOrWhiteSpace(vary))
                    throw new InvalidDataException("Legacy Vary responses require explicit host migration.");
                var bytes = data.GetProperty("bytes").EnumerateArray().Select(value => value.GetByte()).ToArray();
                var entry = new Entry(new(url.AbsoluteUri, "GET", []), data.GetProperty("status").GetInt32(),
                    data.GetProperty("statusText").GetString()!, headers, bytes, false, "", "default");
                Validate(entry);
                if (!_named.TryGetValue(name, out var cache))
                {
                    cache = new(this, name, []); _named.Add(name, cache); _all.Add(new(cache));
                }
                if (cache.Entries.Any(prior => CompareUrl(prior.Request.Url, false) == CompareUrl(entry.Request.Url, false)))
                    throw new InvalidDataException("Legacy requests collide after URL canonicalization.");
                cache.Entries.Add(entry);
            }
            // A single replacement is the migration commit. Keeping old keys
            // intact preserves a backup if the host rejects this write.
            if (_named.Count != 0) Persist(_named, null, null);
        }
        catch (Exception)
        {
            throw WebIdlException.DomException("UnknownError", "Legacy Cache API data could not be migrated; the host's original entries were preserved.");
        }
    }

    internal Cache Open(string name, long quota)
    {
        lock (_gate)
        {
            CheckMutation();
            if (_named.TryGetValue(name, out var existing)) return existing;
            var cache = new Cache(this, name, []);
            var candidate = new Dictionary<string, Cache>(_named, StringComparer.Ordinal) { [name] = cache };
            Commit(candidate, cache, [], quota);
            _named.Add(name, cache);
            _all.Add(new(cache));
            return cache;
        }
    }

    internal bool Has(string name) { lock (_gate) return _named.ContainsKey(name); }
    internal string[] Names() { lock (_gate) return _named.Keys.ToArray(); }

    private Cache Own(CacheStorageHandle handle) => handle is Cache cache && ReferenceEquals(cache.Owner, this)
        ? cache : throw WebIdlException.TypeError("Illegal Cache handle.");
    CacheStorageHandle ICacheStorageBackend.Open(string name, long quota) => Open(name, quota);
    bool ICacheStorageBackend.Has(string name) => Has(name);
    string[] ICacheStorageBackend.Names() => Names();
    bool ICacheStorageBackend.Delete(string name, long quota) => Delete(name, quota);
    Entry[] ICacheStorageBackend.Match(CacheStorageHandle? cache, string? name, Request? request, Query query, bool first) =>
        Match(cache is null ? null : Own(cache), name, request, query, first);
    void ICacheStorageBackend.Put(CacheStorageHandle cache, IReadOnlyList<Entry> entries, bool batch, long quota) => Put(Own(cache), entries, batch, quota);
    bool ICacheStorageBackend.DeleteEntries(CacheStorageHandle cache, Request request, Query query, long quota) => DeleteEntries(Own(cache), request, query, quota);
    public void Dispose() { } // Named storage is shared; handles own retired stores through ordinary references.

    internal bool Delete(string name, long quota)
    {
        lock (_gate)
        {
            CheckMutation();
            if (!_named.TryGetValue(name, out var cache)) return false;
            var candidate = new Dictionary<string, Cache>(_named, StringComparer.Ordinal);
            candidate.Remove(name);
            // Deleting a name keeps existing Cache handles alive. They cannot
            // write back under this name after a subsequent open creates a cache.
            Persist(candidate, null, null);
            _named.Remove(name);
            cache.Name = null;
            return true;
        }
    }

    internal Entry[] Match(Cache? cache, string? name, Request? request, Query query, bool first)
    {
        lock (_gate)
        {
            var stores = cache is not null ? [cache] : name is null ? _named.Values.ToArray()
                : _named.TryGetValue(name, out var named) ? [named] : Array.Empty<Cache>();
            var matches = stores.SelectMany(store => store.Entries).Where(entry => Matches(entry, request, query));
            if (first) matches = matches.Take(1);
            return (query.RequestOnly ? matches.Select(entry => entry with { Bytes = [] }) : matches).ToArray();
        }
    }

    internal void Put(Cache cache, IReadOnlyList<Entry> entries, bool batch, long quota)
    {
        if (entries.Count == 0) return;
        lock (_gate)
        {
            CheckMutation();
            // Index by fragment-free URL so unrelated requests never trigger a
            // quadratic scan. Vary variants are bounded independently below.
            var ordered = new LinkedList<Entry>(cache.Entries);
            var groups = new Dictionary<string, List<LinkedListNode<Entry>>>(StringComparer.Ordinal);
            for (var node = ordered.First; node is not null; node = node.Next)
            {
                var key = CompareUrl(node.Value.Request.Url, false);
                if (!groups.TryGetValue(key, out var group)) groups[key] = group = [];
                group.Add(node);
            }
            var added = new HashSet<LinkedListNode<Entry>>();
            var comparisons = 0;
            foreach (var entry in entries)
            {
                Validate(entry);
                var key = CompareUrl(entry.Request.Url, false);
                if (!groups.TryGetValue(key, out var group)) groups[key] = group = [];
                foreach (var prior in group.ToArray())
                {
                    // Adversarial Vary combinations cannot monopolize the host
                    // after the content process has reached its watchdog limit.
                    if (++comparisons > 1_000_000)
                        throw WebIdlException.DomException("QuotaExceededError", "The cache batch exceeds its variant comparison limit.");
                    if (!Matches(prior.Value, entry.Request, new(false, false, false))) continue;
                    if (batch && added.Contains(prior))
                        throw WebIdlException.DomException("InvalidStateError", "The batch contains overlapping cache requests.");
                    ordered.Remove(prior); group.Remove(prior);
                }
                var next = ordered.AddLast(entry); group.Add(next); added.Add(next);
            }
            var staged = ordered.ToList();
            Commit(_named, cache, staged, quota);
            cache.Entries = staged;
        }
    }

    internal bool DeleteEntries(Cache cache, Request request, Query query, long quota)
    {
        lock (_gate)
        {
            CheckMutation();
            var staged = cache.Entries.Where(entry => !Matches(entry, request, query)).ToList();
            if (staged.Count == cache.Entries.Count) return false;
            // A deletion must remain possible when a host lowers its quota.
            Persist(_named, cache, staged);
            cache.Entries = staged;
            return true;
        }
    }

    private void Commit(Dictionary<string, Cache> names, Cache changed, List<Entry> entries, long quota)
    {
        var live = new List<Cache>();
        _all.RemoveAll(reference => !reference.TryGetTarget(out _));
        foreach (var reference in _all)
            if (reference.TryGetTarget(out var cache)) live.Add(cache);
        if (!live.Contains(changed)) live.Add(changed);
        var accounting = new Snapshot(1, live.Select(cache =>
            new StoredCache(cache.Name ?? "", ReferenceEquals(cache, changed) ? entries : cache.Entries)).ToList());
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(accounting)) > Math.Max(0, quota))
            throw WebIdlException.DomException("QuotaExceededError", "The Cache API storage quota has been exceeded.");
        Persist(names, changed, entries);
    }

    private void Persist(Dictionary<string, Cache> names, Cache? changed, List<Entry>? entries)
    {
        if (_storage is null) return;
        var snapshot = new Snapshot(1, names.Select(pair => new StoredCache(pair.Key,
            ReferenceEquals(pair.Value, changed) ? entries! : pair.Value.Entries)).ToList());
        var serialized = JsonSerializer.Serialize(snapshot);
        _persisting = true;
        try { _storage.Write(_key, serialized); }
        catch (Exception) { throw WebIdlException.DomException("UnknownError", "The host could not persist the Cache API transaction."); }
        finally { _persisting = false; }
    }

    private void CheckMutation()
    {
        if (_persisting) throw WebIdlException.DomException("UnknownError", "A host storage callback cannot reenter a cache transaction.");
    }

    private static bool Matches(Entry entry, Request? request, Query query)
    {
        if (request is null) return true;
        if (!query.IgnoreMethod && request.Method != "GET") return false;
        if (!string.Equals(CompareUrl(entry.Request.Url, query.IgnoreSearch), CompareUrl(request.Url, query.IgnoreSearch), StringComparison.Ordinal)) return false;
        if (query.IgnoreVary || !entry.Headers.TryGetValue("vary", out var vary)) return true;
        foreach (var field in vary.Split(','))
        {
            var name = field.Trim().ToLowerInvariant();
            if (name == "*") return false;
            if (name.Length != 0 && entry.Request.Headers.GetValueOrDefault(name) != request.Headers.GetValueOrDefault(name)) return false;
        }
        return true;
    }

    private static string CompareUrl(string url, bool ignoreSearch)
    {
        var fragment = url.IndexOf('#');
        if (fragment >= 0) url = url[..fragment];
        var query = ignoreSearch ? url.IndexOf('?') : -1;
        return query >= 0 ? url[..query] : url;
    }

    private static void Validate(Entry entry)
    {
        if (entry is null || entry.Request is null || entry.Request.Headers is null || entry.Headers is null || entry.Bytes is null || entry.Request.Method != "GET"
            || !Uri.TryCreate(entry.Request.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || entry.Status == 206
            || (entry.Status != 0 && entry.Status is < 200 or > 599) || entry.StatusText is null || entry.Url is null || entry.Type is null || entry.Headers.Any(pair => pair.Key is null || pair.Value is null)
            || entry.BodyNull && entry.Bytes.Length != 0
            || entry.Request.Headers.Any(pair => pair.Key is null || pair.Value is null)
            || entry.Headers.TryGetValue("vary", out var vary) && vary.Split(',').Any(field => field.Trim() == "*"))
            throw WebIdlException.TypeError("The request or response cannot be cached.");
    }
}
