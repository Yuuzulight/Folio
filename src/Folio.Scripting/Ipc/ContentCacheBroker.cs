using System.Text.Json;
using System.Collections.Concurrent;
using Folio.Scripting.Dom;

namespace Folio.Scripting.Ipc;

// Only semantic cache operations cross the trust boundary. A child never gets
// permission to replace the artifact's serialized snapshot or choose its quota.
internal sealed class ContentCacheBroker(IArtifactStorage storage, string artifact, long quota) : IDisposable
{
    internal sealed record Request(string Operation, string Context, string? Handle = null, string? Name = null,
        CacheStorageState.Request? RequestValue = null, CacheStorageState.Query? Query = null,
        CacheStorageState.Entry[]? Entries = null, bool Batch = false, bool First = false,
        string? Key = null, string? Value = null, string[]? Handles = null);
    internal sealed record Reply(JsonElement Value, string? ErrorKind = null, string? ErrorName = null, string? ErrorMessage = null);
    private ICacheStorageBackend? _state;
    private Uri? _baseUri;
    internal void SetDocumentUri(Uri? uri) => _baseUri = uri;
    private readonly Dictionary<string, Dictionary<string, CacheStorageHandle>> _contexts = new(StringComparer.Ordinal);
    private readonly string _registrationPrefix = artifact + ":service-worker-registration/";
    private int _handles;

    internal byte[] Execute(byte[] bytes)
    {
        Reply reply;
        try
        {
            var request = JsonSerializer.Deserialize<Request>(bytes) ?? throw WebIdlException.TypeError("Missing storage request.");
            if (!Guid.TryParseExact(request.Context, "N", out _)) throw WebIdlException.TypeError("Invalid storage context.");
            reply = new(JsonSerializer.SerializeToElement(Execute(request)));
        }
        catch (WebIdlException error) { reply = new(default, error.Kind.ToString(), error.Name, error.Message); }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or NullReferenceException)
        { reply = new(default, "TypeError", "TypeError", "Invalid storage request."); }
        catch (Exception) { reply = new(default, "DOMException", "UnknownError", "The host storage operation failed."); }
        // Undefined JsonElement cannot be serialized, even on error.
        if (reply.Value.ValueKind == JsonValueKind.Undefined) reply = reply with { Value = JsonSerializer.SerializeToElement<object?>(null) };
        return JsonSerializer.SerializeToUtf8Bytes(reply);
    }

    private object? Execute(Request request)
    {
        if (request.Operation == "releaseContext")
        {
            if (_contexts.Remove(request.Context, out var released)) _handles -= released.Count;
            return null;
        }
        if (request.Operation == "release")
        {
            if (_contexts.TryGetValue(request.Context, out var released))
                foreach (var handle in request.Handles ?? []) if (released.Remove(handle)) _handles--;
            return null;
        }
        if (request.Operation.StartsWith("metadata", StringComparison.Ordinal)) return Metadata(request);
        if (!_contexts.TryGetValue(request.Context, out var context))
        {
            if (_contexts.Count >= 64) throw WebIdlException.DomException("QuotaExceededError", "Too many cache contexts.");
            _contexts.Add(request.Context, context = new(StringComparer.Ordinal));
        }
        CacheStorageHandle Handle() => request.Handle is not null && context.TryGetValue(request.Handle, out var handle)
            ? handle : throw WebIdlException.TypeError("Invalid remote Cache handle.");
        string Name() => request.Name ?? throw WebIdlException.TypeError("Missing cache name.");
        var state = _state ??= CacheStorageState.For(storage, artifact, _baseUri);
        switch (request.Operation)
        {
            case "open":
                if (_handles >= 4096) throw WebIdlException.DomException("QuotaExceededError", "Too many live cache handles.");
                var opened = state.Open(Name(), quota);
                var id = Guid.NewGuid().ToString("N"); context.Add(id, opened); _handles++; return id;
            case "has": return state.Has(Name());
            case "names": return state.Names();
            case "delete": return state.Delete(Name(), quota);
            case "match":
                ValidateQuery(request);
                return state.Match(request.Handle is null ? null : Handle(), request.Name, request.RequestValue, request.Query!, request.First);
            case "put": state.Put(Handle(), request.Entries ?? throw WebIdlException.TypeError("Missing cache entries."), request.Batch, quota); return null;
            case "deleteEntry":
                ValidateQuery(request);
                return state.DeleteEntries(Handle(), request.RequestValue ?? throw WebIdlException.TypeError("Missing cache request."), request.Query!, quota);
            default: throw WebIdlException.TypeError("Unknown storage operation.");
        }
    }

    private static void ValidateQuery(Request request)
    {
        if (request.Query is null || request.RequestValue is { } value && (value.Url is null || value.Method is null || value.Headers is null
            || value.Headers.Any(pair => pair.Value is null))) throw WebIdlException.TypeError("Invalid cache query.");
    }

    private object? Metadata(Request request)
    {
        var key = request.Key ?? "";
        const string localPrefix = "folio/local-storage/";
        var registration = key.StartsWith(_registrationPrefix, StringComparison.Ordinal);
        var local = key.StartsWith(localPrefix, StringComparison.Ordinal);
        if (!registration && !local)
            throw WebIdlException.TypeError($"Invalid artifact storage scope for '{key}'.");
        var logicalPrefix = registration ? _registrationPrefix : localPrefix;
        var backingPrefix = registration ? _registrationPrefix : artifact + ":" + localPrefix;
        var suffix = key[logicalPrefix.Length..];
        if (suffix.Length > 32768 || (registration && (suffix.Contains(':') || suffix.Contains('/'))))
            throw WebIdlException.TypeError(registration ? "Invalid registration storage key." : "Invalid local storage key.");
        // Metadata is small and uses a disjoint key namespace. Serializing it on
        // the provider also prevents concurrent sessions bypassing its quota.
        lock (storage)
        {
            var keys = storage.Keys(backingPrefix).Where(k => k.StartsWith(backingPrefix, StringComparison.Ordinal)
                && (!registration || !k[backingPrefix.Length..].Contains(':') && !k[backingPrefix.Length..].Contains('/'))).Take(1025).ToArray();
            string StoredKey(string logical) => registration ? logical : artifact + ":" + logical;
            string LogicalKey(string stored) => registration ? stored : localPrefix + stored[backingPrefix.Length..];
            switch (request.Operation)
            {
                case "metadataRead": return storage.Read(StoredKey(key));
                case "metadataKeys": return keys.Where(k => k.StartsWith(StoredKey(key), StringComparison.Ordinal)).Select(LogicalKey).ToArray();
                case "metadataDelete": storage.Delete(StoredKey(key)); return null;
                case "metadataWrite":
                    var value = request.Value ?? "";
                    var maxBytes = registration ? 1024 * 1024 : 5 * 1024 * 1024;
                    var storedKey = StoredKey(key);
                    if (value.Length > 65536 || keys.Length > 1024 || !keys.Contains(storedKey) && keys.Length == 1024
                        || keys.Where(k => k != storedKey).Sum(k => (long)k.Length + (storage.Read(k)?.Length ?? 0)) + storedKey.Length + value.Length > maxBytes)
                        throw WebIdlException.DomException("QuotaExceededError", registration ? "Registration metadata exceeds its storage limit." : "Storage quota exceeded.");
                    storage.Write(storedKey, value); return null;
                default: throw WebIdlException.TypeError("Unknown metadata operation.");
            }
        }
    }

    public void Dispose() { _contexts.Clear(); _handles = 0; }
}

// This object is never exposed to script. Its artifact and quota come from the
// trusted parent's load profile, not from document-supplied request fields.
internal sealed class BrokerArtifactStorage(ContentHostSession session) : IArtifactStorage, ICacheStorageBackendProvider
{
    private readonly string _context = Guid.NewGuid().ToString("N");
    internal JsonElement Call(ContentCacheBroker.Request request) => CallAsync(request).GetAwaiter().GetResult();
    internal async Task<JsonElement> CallAsync(ContentCacheBroker.Request request)
    {
        var bytes = await session.RequestStorageAsync(JsonSerializer.SerializeToUtf8Bytes(request)).ConfigureAwait(false);
        var reply = JsonSerializer.Deserialize<ContentCacheBroker.Reply>(bytes) ?? throw new InvalidDataException("Missing storage reply.");
        if (reply.ErrorName is not null) throw reply.ErrorKind == "DOMException"
            ? WebIdlException.DomException(reply.ErrorName, reply.ErrorMessage ?? "Storage operation failed.")
            : WebIdlException.TypeError(reply.ErrorMessage ?? "Invalid storage operation.");
        return reply.Value;
    }
    public string? Read(string key) => Call(new("metadataRead", _context, Key: key)).GetString();
    public void Write(string key, string value) => Call(new("metadataWrite", _context, Key: key, Value: value));
    public void Delete(string key) => Call(new("metadataDelete", _context, Key: key));
    public IReadOnlyList<string> Keys(string prefix) => Call(new("metadataKeys", _context, Key: prefix)).Deserialize<string[]>()!;
    public ICacheStorageBackend CreateCacheBackend(string artifact, long quota) => new RemoteCacheBackend(this);

    private sealed class RemoteCacheBackend(BrokerArtifactStorage storage) : ICacheStorageBackend
    {
        private readonly string _context = Guid.NewGuid().ToString("N");
        private readonly ConcurrentQueue<string> _released = new();
        private volatile bool _disposed;
        private int _releaseScheduled;
        private sealed class Handle(RemoteCacheBackend owner, string id) : CacheStorageHandle(owner)
        {
            internal string Id => id;
            ~Handle() { owner.Release(id); }
        }
        private void Release(string id)
        {
            if (_disposed || storage.IsClosed) return;
            _released.Enqueue(id);
            if (Interlocked.Exchange(ref _releaseScheduled, 1) != 0) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    do
                    {
                        var released = new List<string>();
                        while (_released.TryDequeue(out var next)) released.Add(next);
                        if (released.Count != 0 && !_disposed && !storage.IsClosed)
                            await storage.CallAsync(new("release", _context, Handles: released.ToArray())).ConfigureAwait(false);
                        Interlocked.Exchange(ref _releaseScheduled, 0);
                    } while (!_released.IsEmpty && Interlocked.Exchange(ref _releaseScheduled, 1) == 0);
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or WebIdlException)
                { Interlocked.Exchange(ref _releaseScheduled, 0); }
            });
        }
        private JsonElement Call(ContentCacheBroker.Request request)
        {
            if (_disposed) throw WebIdlException.DomException("InvalidStateError", "The cache context is closed.");
            var released = new List<string>();
            while (_released.TryDequeue(out var id)) released.Add(id);
            if (released.Count != 0) storage.Call(new("release", _context, Handles: released.ToArray()));
            return storage.Call(request);
        }
        private JsonElement WithHandle(CacheStorageHandle handle, ContentCacheBroker.Request request)
        {
            try { return Call(request); }
            finally { GC.KeepAlive(handle); }
        }
        private string Id(CacheStorageHandle value) => value is Handle handle && ReferenceEquals(handle.Owner, this)
            ? handle.Id : throw WebIdlException.TypeError("Invalid Cache handle.");
        public CacheStorageHandle Open(string name, long quota) => new Handle(this, Call(new("open", _context, Name: name)).GetString()!);
        public bool Has(string name) => Call(new("has", _context, Name: name)).GetBoolean();
        public string[] Names() => Call(new("names", _context)).Deserialize<string[]>()!;
        public bool Delete(string name, long quota) => Call(new("delete", _context, Name: name)).GetBoolean();
        public CacheStorageState.Entry[] Match(CacheStorageHandle? cache, string? name, CacheStorageState.Request? request, CacheStorageState.Query query, bool first) =>
            (cache is null ? Call(new("match", _context, Name: name, RequestValue: request, Query: query, First: first))
                : WithHandle(cache, new("match", _context, Id(cache), name, request, query, First: first))).Deserialize<CacheStorageState.Entry[]>()!;
        public void Put(CacheStorageHandle cache, IReadOnlyList<CacheStorageState.Entry> entries, bool batch, long quota) =>
            WithHandle(cache, new("put", _context, Id(cache), Entries: entries.ToArray(), Batch: batch));
        public bool DeleteEntries(CacheStorageHandle cache, CacheStorageState.Request request, CacheStorageState.Query query, long quota) =>
            WithHandle(cache, new("deleteEntry", _context, Id(cache), RequestValue: request, Query: query)).GetBoolean();
        public void Dispose()
        {
            if (_disposed) return;
            try { if (!storage.IsClosed) storage.Call(new("releaseContext", _context)); }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
            finally { _disposed = true; }
        }
    }
    private bool IsClosed => session.IsConnectionClosed;
}
