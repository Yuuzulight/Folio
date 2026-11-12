using Folio.Hosting;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;
using Folio.Scripting.Native;

namespace Folio.Scripting.Tests;

public sealed class DocumentBlobTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BlobsHoldBytesTextIsUtf8AndRandomValuesComeFromTheHost(bool native)
    {
        using IScriptEngine engine = native ? new NativeJavaScriptEngine() : new JintScriptEngine();
        using var document = Document.Parse("<html></html>");
        using var loop = new HtmlEventLoop(engine);
        DomBindings.Install(engine, document, eventLoop: loop);
        try
        {
            engine.Evaluate("""
                var out = '';
                var bytes = new Uint8Array([7, 8, 255]);
                var blob = new Blob([bytes.buffer, 'é', new Blob(['z'])], {type: 'Application/Octet-Stream'});
                out += [blob.size, blob.type, Array.from(new TextEncoder().encode('é€😀')).join('.'),
                    new TextDecoder().decode(new Uint8Array([104, 195, 169, 240, 159, 152, 128]))].join();
                blob.arrayBuffer().then(function (buffer) { out += '|' + Array.from(new Uint8Array(buffer)).join('.'); });
                new Blob(['héllo']).text().then(function (text) { out += '|' + text; });
                blob.slice(1, 3).arrayBuffer().then(function (buffer) { out += '|' + Array.from(new Uint8Array(buffer)).join('.'); });
                var a = crypto.getRandomValues(new Uint8Array(32)), b = crypto.getRandomValues(new Uint32Array(8));
                var random = a.length === 32 && Array.from(a).some(function (v) { return v !== a[0]; }) && Array.from(b).some(function (v) { return v > 65535; });
                var refused = false; try { crypto.getRandomValues(new Float32Array(2)); } catch (e) { refused = e instanceof TypeError; }
                """);
            engine.PerformMicrotaskCheckpoint();
            loop.SpinUntilIdle();
            Assert.Equal("6,application/octet-stream,195.169.226.130.172.240.159.152.128,hé😀|7.8.255.195.169.122|héllo|8.255", engine.Evaluate("out").AsString());
            Assert.True(engine.Evaluate("random && refused").AsBoolean());
        }
        finally { DomBindings.Dispose(engine); }
    }
}
