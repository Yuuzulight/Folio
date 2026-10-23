using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

public sealed class WorkerPlatformBindingsTests
{
    [Fact]
    public void InstallsDomFreeEncodingBase64AndUriGlobals()
    {
        using var engine = new JintScriptEngine();
        WorkerPlatformBindings.Install(engine, new Uri("https://example.test/app/worker.js"));
        Assert.Equal("f+A=", engine.Evaluate("btoa('\u007fà')").AsString());
        Assert.Equal("A😀é", engine.Evaluate("new TextDecoder().decode(new TextEncoder().encode('A😀é'))").AsString());
        Assert.Equal("https://example.test/app/assets/a.js?x=1", engine.Evaluate("new URL('assets/a.js?x=1','https://example.test/app/worker.js').href").AsString());
        Assert.Equal("a=1&b=two", engine.Evaluate("new URLSearchParams('?a=1&b=two').toString()").AsString());
        Assert.Equal("TypeError", engine.Evaluate("try { new URL('relative'); 'ok'; } catch (e) { e.name; }").AsString());
    }

    [Fact]
    public void EncoderAndRandomUseNativeBoundsAndReturnIdentity()
    {
        using var engine = new JintScriptEngine();
        WorkerPlatformBindings.Install(engine);
        Assert.Equal(2, engine.Evaluate("new TextEncoder().encode('é').length").AsNumber());
        Assert.True(engine.Evaluate("(() => { const x = new Uint16Array(3); return crypto.getRandomValues(x) === x && x.byteLength === 6; })()").AsBoolean());
        Assert.Equal("QuotaExceededError", engine.Evaluate("try { crypto.getRandomValues(new Uint8Array(65537)); 'ok'; } catch (e) { e.name; }").AsString());
        Assert.NotEqual("TypeError", engine.Evaluate("typeof BigInt64Array === 'function' ? (crypto.getRandomValues(new BigInt64Array(1)) === undefined ? 'bad' : 'ok') : 'missing'").AsString());
        Assert.Equal("TypeMismatchError", engine.Evaluate("try { crypto.getRandomValues(new Float32Array(1)); 'ok'; } catch (e) { e.name; }").AsString());
        Assert.Equal(0, engine.Evaluate("new TextEncoder().encode().length").AsNumber());
        Assert.Equal("", engine.Evaluate("new TextDecoder().decode()").AsString());
        Assert.Equal("RangeError", engine.Evaluate("try { new TextDecoder('gbk'); 'ok'; } catch (e) { e.name; }").AsString());
    }

    [Fact]
    public void BlobFormDataAndConsoleUseExplicitHostCallbacks()
    {
        using var engine = new JintScriptEngine();
        var calls = new List<(string Level, string Message)>();
        var urls = new List<string>();
        WorkerPlatformBindings.Install(engine, reportConsole: (level, message) => calls.Add((level, message)), createBlobUrl: text => { urls.Add(text); return "blob:test"; });
        Assert.Equal("blob:test", engine.Evaluate("URL.createObjectURL(new Blob(['worker']))").AsString());
        Assert.Equal("worker", Assert.Single(urls));
        Assert.Equal("v", engine.Evaluate("(() => { const f=new FormData(); f.append('k','v'); return f.get('k'); })()").AsString());
        engine.Evaluate("console.warn('worker', 3)");
        Assert.Equal(("warn", "worker 3"), Assert.Single(calls));
    }

    [Fact]
    public void InstallerHelpersAreRemovedAndLaterGlobalTamperingDoesNotChangeNativeCallbacks()
    {
        using var engine = new JintScriptEngine();
        WorkerPlatformBindings.Install(engine);
        Assert.True(engine.Evaluate("typeof __folio_worker_platform === 'undefined'").AsBoolean());
        Assert.Equal("A😀", engine.Evaluate("TextEncoder = function() {}; new TextDecoder().decode(new Uint8Array([65,240,159,152,128]))").AsString());
    }

    [Fact]
    public void DecoderRetainsIncompleteUtf8AcrossStreamChunksAndFlushesReplacement()
    {
        using var engine = new JintScriptEngine();
        WorkerPlatformBindings.Install(engine);
        Assert.Equal("😀", engine.Evaluate("(() => { const d=new TextDecoder(); return d.decode(new Uint8Array([240,159]),{stream:true}) + d.decode(new Uint8Array([152,128]),{stream:false}); })()").AsString());
        Assert.Equal("�", engine.Evaluate("new TextDecoder().decode(new Uint8Array([240]),{stream:false})").AsString());
        Assert.Equal("TypeError", engine.Evaluate("try { new TextDecoder('utf-8',{fatal:true}).decode(new Uint8Array([255])); 'ok'; } catch (e) { e.name; }").AsString());
        Assert.Equal("x", engine.Evaluate("(() => { const d=new TextDecoder(); return d.decode(new Uint8Array([239]),{stream:true}) + d.decode(new Uint8Array([187,191,120]),{stream:false}); })()").AsString());
        Assert.Equal("\ufeffx", engine.Evaluate("new TextDecoder('utf-8',{ignoreBOM:true}).decode(new Uint8Array([239,187,191,120]))").AsString());
    }

    [Fact]
    public void BlobUsesUtf8ByteSizeAndSupportsSliceAndArrayBuffer()
    {
        using var engine = new JintScriptEngine();
        WorkerPlatformBindings.Install(engine);
        Assert.Equal("2:2", engine.Evaluate("(() => { const b=new Blob(['é']); return b.size+':'+b.slice(0,2).size; })()").AsString());
        engine.Evaluate("(() => { const b=new Blob(['é']); b.arrayBuffer().then(x=>globalThis.blobLength=x.byteLength); b.text().then(x=>globalThis.blobText=x); })()");
        engine.PerformMicrotaskCheckpoint();
        Assert.Equal(2, engine.Evaluate("blobLength").AsNumber());
        Assert.Equal("é", engine.Evaluate("blobText").AsString());
    }
}
