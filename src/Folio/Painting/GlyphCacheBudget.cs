namespace Folio.Painting;

/// <summary>
/// One retained-memory budget for every native font's outline, flattening, mask, hint-state, bitmap and colour-layer cache. Registrations
/// are weak: neither a font nor its cache is kept alive by accounting. All cache operations take Sync before changing
/// their contents, so cross-font eviction has one reentrant lock order, including nested outline/hint lookups.
/// </summary>
internal static class GlyphCacheBudget
{
    internal const long Limit = 8L * 1024 * 1024;
    internal const int MaxRegions = 256;
    private const long RegionOverhead = 512;
    private const long FixedOverhead = 32 * 1024;
    internal static readonly object Sync = new();
    private static long _identity, _clock;
    private static long _bytes = FixedOverhead;
    private static readonly Dictionary<long, Registration> Regions = [];

    internal abstract class Region
    {
        internal long CacheIdentity { get; } = Interlocked.Increment(ref _identity);
        // Must release backing arrays as well as entries; Dictionary.Clear alone retains its capacity.
        internal abstract void ClearCachedValues();
    }

    private sealed class Registration(Region cache, long accessed)
    {
        internal readonly WeakReference<Region> Cache = new(cache);
        internal long Accessed = accessed;
        internal long Bytes;
    }

    internal static (long Bytes, int RegionCount) Usage
    {
        get
        {
            lock (Sync)
            {
                RemoveDead();
                return (_bytes, Regions.Count);
            }
        }
    }

    internal static void Touch(Region cache)
    {
        lock (Sync)
        {
            if (Regions.TryGetValue(cache.CacheIdentity, out var registration))
            { registration.Accessed = ++_clock; return; }
            RemoveDead();
            while (Regions.Count >= MaxRegions) EvictOldest();
            Regions.Add(cache.CacheIdentity, new Registration(cache, ++_clock));
            _bytes += RegionOverhead;
            Trim();
        }
    }

    internal static void Update(Region cache, long retainedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(retainedBytes);
        lock (Sync)
        {
            if (retainedBytes > Limit) { cache.ClearCachedValues(); retainedBytes = 0; }
            Touch(cache);
            var registration = Regions[cache.CacheIdentity];
            _bytes += retainedBytes - registration.Bytes;
            registration.Bytes = retainedBytes;
            Trim();
        }
    }

    private static void Trim()
    {
        while (_bytes > Limit && Regions.Count > 0) EvictOldest();
    }

    private static void EvictOldest()
    {
        var oldest = Regions.MinBy(pair => pair.Value.Accessed);
        if (oldest.Value.Cache.TryGetTarget(out var cache)) cache.ClearCachedValues();
        _bytes -= RegionOverhead + oldest.Value.Bytes;
        Regions.Remove(oldest.Key);
    }

    private static void RemoveDead()
    {
        List<long>? dead = null;
        foreach (var pair in Regions)
            if (!pair.Value.Cache.TryGetTarget(out _)) (dead ??= []).Add(pair.Key);
        if (dead is null) return;
        foreach (var identity in dead)
        {
            _bytes -= RegionOverhead + Regions[identity].Bytes;
            Regions.Remove(identity);
        }
    }
}
