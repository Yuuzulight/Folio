using Folio.Scripting.Native;

namespace Folio.Scripting.Tests;

public sealed class BoundedScriptCacheTests
{
    [Fact]
    public void ConcurrentMissesShareOneFactoryAndFailedKeysCanRetry()
    {
        var cache = new BoundedScriptCache<int, int>(8, 32);
        var calls = 0;
        Parallel.For(0, 100, _ => Assert.Equal(7, cache.GetOrAdd(7, 1, key =>
        {
            Interlocked.Increment(ref calls);
            Thread.SpinWait(10000);
            return key;
        })));
        Assert.Equal(1, calls);
        Assert.Throws<InvalidDataException>(() => cache.GetOrAdd(8, 1, _ => throw new InvalidDataException()));
        Assert.Equal(8, cache.GetOrAdd(8, 1, key => key));
        Assert.Equal(2, cache.Count);
        Assert.Equal(2, cache.SourceCharacters);
    }

    [Fact]
    public void EvictsOldestEntriesToSatisfyBothLimits()
    {
        var cache = new BoundedScriptCache<string, object>(2, 5);
        var first = cache.GetOrAdd("a", 2, _ => new object());
        Assert.Same(first, cache.GetOrAdd("a", 2, _ => throw new Exception("Cache miss")));
        cache.GetOrAdd("b", 2, _ => new object());
        cache.GetOrAdd("c", 3, _ => new object());
        Assert.Equal(2, cache.Count);
        Assert.Equal(5, cache.SourceCharacters);
        Assert.NotSame(first, cache.GetOrAdd("a", 2, _ => new object()));
        Assert.Equal(2, cache.Count);
        Assert.Equal(5, cache.SourceCharacters);
    }

    [Fact]
    public void OversizedAndFailedFactoriesDoNotRetainInputs()
    {
        var cache = new BoundedScriptCache<string, object>(2, 5);
        cache.GetOrAdd("oversized", 6, _ => new object());
        Assert.Throws<InvalidDataException>(() => cache.GetOrAdd("bad", 1, _ => throw new InvalidDataException()));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.SourceCharacters);
    }

    [Fact]
    public void ConcurrentInsertionsCannotExceedRetentionLimits()
    {
        var cache = new BoundedScriptCache<int, int>(8, 17);
        Parallel.For(0, 2000, index => Assert.Equal(index, cache.GetOrAdd(index, 3, key => key)));
        Assert.InRange(cache.Count, 1, 5);
        Assert.Equal(cache.Count * 3, cache.SourceCharacters);
    }
}
