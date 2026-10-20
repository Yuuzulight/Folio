using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

public sealed class CapabilityDenialTests
{
    [Theory]
    [InlineData("WebGL", "document.createElement('canvas').getContext('webgl2')")]
    [InlineData("Media", "document.createElement('audio').play().catch(()=>{})")]
    [InlineData("Sound", "document.getElementById('audio').play().catch(()=>{})")]
    [InlineData("Workers", "new Worker('data:text/javascript,onmessage=function(){}')")]
    [InlineData("WebAssembly", "new WebAssembly.Memory({initial:1})")]
    [InlineData("Iframes", "document.createElement('iframe').contentDocument")]
    [InlineData("Camera", "navigator.mediaDevices.getUserMedia({video:true}).catch(()=>{})")]
    [InlineData("Microphone", "navigator.mediaDevices.getUserMedia({audio:true}).catch(()=>{})")]
    [InlineData("WebRTC", "new RTCPeerConnection()")]
    [InlineData("ServiceWorkers", "navigator.serviceWorker")]
    public void EveryHostGrantReportsOneNativeDenialPerDocument(string feature, string operation)
    {
        using var engine = new JintScriptEngine();
        var options = new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Media = feature == "Sound" },
            MediaDecoder = new Decoder()
        };
        using var document = Document.Parse("<audio id='audio' src='data:application/octet-stream;base64,AQID'></audio>", options);
        using var loop = new HtmlEventLoop(engine);
        DomBindings.Install(engine, document, eventLoop: loop);
        using var runner = new ScriptRunner(document, engine, loop, options);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            engine.Evaluate("try { " + operation + "; } catch(error) {};");
            loop.SpinUntilIdle();
        }
        var denial = Assert.Single(runner.Diagnostics, diagnostic => diagnostic.Code == DiagnosticCode.CapabilityDenied);
        Assert.Equal(feature, denial.Feature);
    }

    private sealed class Decoder : IMediaDecoder
    {
        public MediaDecodeResult? Decode(ReadOnlyMemory<byte> data, string? contentType, bool video, int maxOutputBytes) =>
            new(1, null, new MediaAudioFrame(8000, 1, new float[8000]));
    }
}
