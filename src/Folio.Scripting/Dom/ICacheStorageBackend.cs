namespace Folio.Scripting.Dom;

internal abstract class CacheStorageHandle(ICacheStorageBackend owner)
{
    internal ICacheStorageBackend Owner { get; } = owner;
}

internal interface ICacheStorageBackend : IDisposable
{
    CacheStorageHandle Open(string name, long quota);
    bool Has(string name);
    string[] Names();
    bool Delete(string name, long quota);
    CacheStorageState.Entry[] Match(CacheStorageHandle? cache, string? name, CacheStorageState.Request? request, CacheStorageState.Query query, bool first);
    void Put(CacheStorageHandle cache, IReadOnlyList<CacheStorageState.Entry> entries, bool batch, long quota);
    bool DeleteEntries(CacheStorageHandle cache, CacheStorageState.Request request, CacheStorageState.Query query, long quota);
}

internal interface ICacheStorageBackendProvider
{
    ICacheStorageBackend CreateCacheBackend(string artifact, long quota);
}
