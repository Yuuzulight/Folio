using Folio.Hosting;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

/// <summary>localStorage and sessionStorage (#468).</summary>
public class StorageApiTests
{
    private sealed class MemoryStorage : IArtifactStorage
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public bool Fail { get; set; }
        public string? Read(string key) => Values.GetValueOrDefault(key);
        public void Write(string key, string value) => Values[key] = Fail ? throw new IOException("disk full") : value;
        public void Delete(string key) => Values.Remove(key);
        public IReadOnlyList<string> Keys(string prefix) => Values.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
    }

    private sealed class Page : IDisposable
    {
        public Page(IArtifactStorage? storage = null, string artifact = "a")
        {
            Document = Document.Parse("<p></p>", new FolioOptions { ArtifactStorage = storage, ArtifactId = artifact });
            Engine = new JintScriptEngine();
            DomBindings.Install(Engine, Document);
        }

        private Document Document { get; }
        private JintScriptEngine Engine { get; }

        public string Run(string script) => Engine.Evaluate(script).ToString() ?? "";

        public void Dispose() => Engine.Dispose();
    }

    [Theory]
    [InlineData("localStorage")]
    [InlineData("sessionStorage")]
    public void StoresReadsAndRemovesValuesInOrder(string area)
    {
        using var page = new Page(new MemoryStorage());
        Assert.Equal("0", page.Run($"{area}.length.toString()"));
        Assert.Equal("null", page.Run($"String({area}.getItem('missing'))"));
        Assert.Equal("2,v1,v2", page.Run($"{area}.setItem('k1', 'v1'); {area}.setItem('k2', 'v2'); {area}.length + ',' + {area}.getItem('k1') + ',' + {area}.getItem('k2')"));
        Assert.Equal("k1,k2,null", page.Run($"{area}.key(0) + ',' + {area}.key(1) + ',' + {area}.key(2)"));
        Assert.Equal("v1b,2", page.Run($"{area}.setItem('k1', 'v1b'); {area}.getItem('k1') + ',' + {area}.length"));
        Assert.Equal("1,null", page.Run($"{area}.removeItem('k1'); {area}.removeItem('nothing'); {area}.length + ',' + {area}.getItem('k1')"));
        Assert.Equal("0", page.Run($"{area}.clear(); {area}.length.toString()"));
        Assert.Equal("TypeError", page.Run($"try {{ {area}.getItem(); 'no' }} catch (e) {{ e.name }}"));
        Assert.Equal("TypeError", page.Run($"try {{ {area}.setItem('only'); 'no' }} catch (e) {{ e.name }}"));
    }

    [Fact]
    public void KeysAndValuesAreStringified()
    {
        using var page = new Page();
        Assert.Equal("2,string", page.Run("localStorage.setItem(1, 2); localStorage.getItem('1') + ',' + typeof localStorage.getItem(1)"));
        Assert.Equal("null,[object Object],true", page.Run("localStorage.setItem('n', null); localStorage.setItem('o', {}); localStorage.setItem(true, 'b'); localStorage.getItem('n') + ',' + localStorage.getItem('o') + ',' + (localStorage.getItem('true') === 'b')"));
        Assert.Equal("undefined", page.Run("localStorage.setItem('u', undefined); localStorage.getItem('u')"));
    }

    [Fact]
    public void AWritePastFiveMegabytesThrowsAndLeavesTheStorageAlone()
    {
        using var page = new Page(new MemoryStorage());
        // Keys and values count in UTF-16 code units: 'a' (1) + 5 MB - 1 fills it exactly.
        Assert.Equal("ok", page.Run("localStorage.setItem('a', 'x'.repeat(5 * 1024 * 1024 - 1)); 'ok'"));
        Assert.Equal("QuotaExceededError,1,1", page.Run("try { localStorage.setItem('b', ''); 'no' } catch (e) { e.name + ',' + localStorage.length + ',' + (localStorage.getItem('a').length === 5 * 1024 * 1024 - 1 ? 1 : 0) }"));
        Assert.Equal("QuotaExceededError", page.Run("try { localStorage.setItem('a', 'y'.repeat(5 * 1024 * 1024)); 'no' } catch (e) { e.name }"));
        Assert.Equal("x", page.Run("localStorage.getItem('a')[0]")); // still the old value
        // Replacing with something shorter frees room.
        Assert.Equal("ok", page.Run("localStorage.setItem('a', 'z'); localStorage.setItem('b', 'y'.repeat(1000)); 'ok'"));
        // The size is of keys and values, not of the number of pairs.
        Assert.Equal("QuotaExceededError", page.Run("try { localStorage.setItem('k'.repeat(5 * 1024 * 1024), ''); 'no' } catch (e) { e.name }"));
    }

    [Fact]
    public void AFailingHostWriteLeavesTheStorageAlone()
    {
        var host = new MemoryStorage();
        using var page = new Page(host);
        page.Run("localStorage.setItem('a', '1');");
        host.Fail = true;
        Assert.Equal("QuotaExceededError,1,1", page.Run("try { localStorage.setItem('a', '2'); 'no' } catch (e) { e.name + ',' + localStorage.getItem('a') + ',' + localStorage.length }"));
        Assert.Equal("QuotaExceededError,1", page.Run("try { localStorage.setItem('b', '2'); 'no' } catch (e) { e.name + ',' + localStorage.length }"));
    }

    [Fact]
    public void NamedPropertiesReadWriteDeleteAndEnumerate()
    {
        using var page = new Page();
        Assert.Equal("bar,true,1", page.Run("localStorage.foo = 'bar'; localStorage.foo + ',' + ('foo' in localStorage) + ',' + localStorage.length"));
        Assert.Equal("bar", page.Run("localStorage.getItem('foo')"));
        Assert.Equal("5,5", page.Run("localStorage['two words'] = 5; localStorage.getItem('two words') + ',' + localStorage['two words']"));
        Assert.Equal("foo,two words", page.Run("Object.keys(localStorage).join()"));
        Assert.Equal("foo=bar;two words=5", page.Run("Object.entries(localStorage).map(function (e) { return e.join('='); }).join(';')"));
        Assert.Equal("foo,two words", page.Run("var seen = []; for (var k in localStorage) seen.push(k); seen.join()"));
        Assert.Equal("true,undefined,null,1", page.Run("var gone = delete localStorage.foo; gone + ',' + localStorage.foo + ',' + localStorage.getItem('foo') + ',' + localStorage.length"));
        // The members of the interface win over a stored name of the same spelling; the pair is still there through getItem.
        Assert.Equal("function,x,2", page.Run("localStorage.setItem('getItem', 'x'); typeof localStorage.getItem + ',' + localStorage.getItem('getItem') + ',' + localStorage.length"));
    }

    [Fact]
    public void LocalStoragePersistsThroughTheHostAndStaysWithinOneArtifact()
    {
        var host = new MemoryStorage();
        using (var first = new Page(host, "artifact-one"))
        {
            first.Run("localStorage.setItem('theme', 'dark'); localStorage.setItem('é/ ?', 'odd key');");
            first.Run("sessionStorage.setItem('tab', '3');");
        }
        using (var again = new Page(host, "artifact-one"))
        {
            Assert.Equal("2,dark,odd key", again.Run("localStorage.length + ',' + localStorage.getItem('theme') + ',' + localStorage.getItem('é/ ?')"));
            Assert.Equal("0", again.Run("sessionStorage.length.toString()")); // sessionStorage never leaves the document
            again.Run("localStorage.removeItem('theme');");
        }
        using (var other = new Page(host, "artifact-two"))
        {
            Assert.Equal("0,null", other.Run("localStorage.length + ',' + localStorage.getItem('theme')"));
            other.Run("localStorage.setItem('theme', 'light');");
        }
        using (var back = new Page(host, "artifact-one"))
            Assert.Equal("1,null,odd key", back.Run("localStorage.length + ',' + localStorage.getItem('theme') + ',' + localStorage.getItem('é/ ?')"));
        using (var longer = new Page(host, "artifact-one/extra"))
            Assert.Equal("0,light-less", longer.Run("localStorage.length + ',' + (localStorage.getItem('theme') === null ? 'light-less' : 'leak')")); // another artifact whose id starts the same
    }

    [Fact]
    public void WithoutAHostTheDataDoesNotOutliveTheDocument()
    {
        using (var first = new Page())
            first.Run("localStorage.setItem('k', 'v'); sessionStorage.setItem('s', 'w');");
        using var second = new Page();
        Assert.Equal("0,null", second.Run("localStorage.length + ',' + localStorage.getItem('k')"));
    }
}
