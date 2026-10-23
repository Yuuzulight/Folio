using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

public sealed class WorkerPlatformContractTests
{
    [Fact]
    public void BlobConversionsUsePrivateCodeAfterEncoderAndDecoderPrototypeReplacement()
    {
        using var engine = new JintScriptEngine();
        var scripts = new List<string>();
        WorkerPlatformBindings.Install(engine, createBlobUrl: source => { scripts.Add(source); return "blob:test"; });
        engine.Evaluate("TextEncoder.prototype.encode=()=>new Uint8Array([88]);TextDecoder.prototype.decode=()=> 'forged';var blob=new Blob(['é']);URL.createObjectURL(blob);blob.text().then(text=>globalThis.blobResult=text);");
        engine.PerformMicrotaskCheckpoint();
        Assert.Equal("é", Assert.Single(scripts));
        Assert.Equal("é", engine.Evaluate("blobResult").AsString());
        Assert.Equal(2, engine.Evaluate("blob.size").AsNumber());
    }
    [Fact]
    public void AtobAcceptsUnpaddedBase64AndAsciiWhitespace()
    {
        using var engine = new JintScriptEngine();
        WorkerPlatformBindings.Install(engine);
        Assert.Equal("a", engine.Evaluate("atob('YQ\\f')").AsString());
    }

    [Fact]
    public void EncodeIntoRejectsNonUint8AndPrototypeSpoofedDestinations()
    {
        using var engine = new JintScriptEngine();
        WorkerPlatformBindings.Install(engine);
        Assert.Equal("TypeError", engine.Evaluate("try { new TextEncoder().encodeInto('x', new Uint16Array(2)); 'ok'; } catch (e) { e.name; }").AsString());
        Assert.Equal("TypeError", engine.Evaluate("try { const fake={}; Object.setPrototypeOf(fake, Uint8Array.prototype); new TextEncoder().encodeInto('x', fake); 'ok'; } catch (e) { e.name; }").AsString());
    }

    [Fact]
    public void RandomRejectsFloatAndSpoofedViewsAndUsesInternalOffset()
    {
        using var engine = new JintScriptEngine();
        WorkerPlatformBindings.Install(engine);
        Assert.Equal("TypeMismatchError", engine.Evaluate("try { crypto.getRandomValues(new Float32Array(1)); 'ok'; } catch (e) { e.name; }").AsString());
        Assert.Equal("TypeMismatchError", engine.Evaluate("try { const fake={byteLength:1,byteOffset:0,buffer:new ArrayBuffer(1)}; Object.setPrototypeOf(fake, Uint8Array.prototype); crypto.getRandomValues(fake); 'ok'; } catch (e) { e.name; }").AsString());
        Assert.Equal("ok", engine.Evaluate("(() => { const x=new Uint8Array(4); Object.defineProperty(Uint8Array.prototype,'byteOffset',{configurable:true,get:()=>999}); try { crypto.getRandomValues(x); return 'ok'; } finally { delete Uint8Array.prototype.byteOffset; } })()").AsString());
    }

    [Fact]
    public void DecoderOptionsCannotBeChangedThroughPublicProperties()
    {
        using var engine = new JintScriptEngine();
        WorkerPlatformBindings.Install(engine);
        Assert.Equal("TypeError", engine.Evaluate("try { const d=new TextDecoder('utf-8',{fatal:true}); d.fatal=false; d.decode(new Uint8Array([255])); 'ok'; } catch (e) { e.name; }").AsString());
        Assert.Equal("�", engine.Evaluate("(() => { const d=new TextDecoder('utf-8',{fatal:false}); d.fatal=true; return d.decode(new Uint8Array([255])); })()").AsString());
    }

    [Fact]
    public void DecoderReportsMalformedAndNonScalarUtf8InsteadOfRetainingIt()
    {
        using var engine = new JintScriptEngine();
        WorkerPlatformBindings.Install(engine);
        Assert.Contains("�", engine.Evaluate("new TextDecoder().decode(new Uint8Array([224,128]),{stream:true})").AsString());
        Assert.Contains("�", engine.Evaluate("new TextDecoder().decode(new Uint8Array([237,160,128]))").AsString());
        Assert.Contains("�", engine.Evaluate("new TextDecoder().decode(new Uint8Array([244,144,128,128]))").AsString());
    }

    [Fact]
    public void UrlOriginDoesNotExposeCredentials()
    {
        using var engine = new JintScriptEngine();
        WorkerPlatformBindings.Install(engine);
        Assert.Equal("https://example.test", engine.Evaluate("new URL('https://user:pass@example.test/a').origin").AsString());
    }

    [Fact]
    public void UrlSearchParamsUsesReplacementDecodingAndPlusEncoding()
    {
        using var engine = new JintScriptEngine();
        WorkerPlatformBindings.Install(engine);
        Assert.Equal("�", engine.Evaluate("new URLSearchParams('a=%E0%A4').get('a')").AsString());
        Assert.Equal("q=a+b", engine.Evaluate("new URLSearchParams({q:'a b'}).toString()").AsString());
    }

    [Fact]
    public void FormDataRejectsNonFormConstructorArguments()
    {
        using var engine = new JintScriptEngine();
        WorkerPlatformBindings.Install(engine);
        Assert.Equal("TypeError", engine.Evaluate("try { new FormData({a:1}); 'ok'; } catch (e) { e.name; }").AsString());
        Assert.Equal("TypeError", engine.Evaluate("try { new FormData([['a','b']]); 'ok'; } catch (e) { e.name; }").AsString());
    }

    [Fact]
    public void BlobSanitizesTypeAndPromisesExposeByteResults()
    {
        using var engine = new JintScriptEngine();
        WorkerPlatformBindings.Install(engine);
        Assert.Equal("", engine.Evaluate("new Blob(['x'],{type:'text/\\u0000plain'}).type").AsString());
        engine.Evaluate("(() => { const b=new Blob(['é'],{type:'text/plain'}); b.text().then(x=>globalThis.__blobText=x); b.arrayBuffer().then(x=>globalThis.__blobBytes=x.byteLength); })()");
        engine.PerformMicrotaskCheckpoint();
        Assert.Equal("é", engine.Evaluate("__blobText").AsString());
        Assert.Equal(2, engine.Evaluate("__blobBytes").AsNumber());
    }
}
