using Folio.Scripting.Dom;
using Folio.Scripting.Jint;
using Folio.Scripting.Native;

namespace Folio.Scripting.Tests;

public sealed class WebRtcTransportEventTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublicChannelExchangesStringsOverNegotiatedUdpTransport(bool native)
    {
        using IScriptEngine engine = native ? new NativeJavaScriptEngine() : new JintScriptEngine();
        using var document = Document.Parse("<html></html>", new FolioOptions { Capabilities = new FeatureCapabilities { WebRtc = true } });
        using var loop = new HtmlEventLoop(engine);
        var diagnostics = new List<string>();
        loop.UncaughtException += error => diagnostics.Add(error.ToString());
        DomBindings.Install(engine, document, eventLoop: loop);
        try
        {
            engine.Evaluate("""
                var a=new RTCPeerConnection(),b=new RTCPeerConnection(),received='',reply='',error='';
                b.ondatachannel=function(e){e.channel.onmessage=function(m){received=m.data;e.channel.send('echo:'+m.data);};};
                var channel=a.createDataChannel('public-udp');
                channel.onopen=function(){channel.send('real transport');};
                channel.onmessage=function(e){reply=e.data;};
                a.createOffer().then(function(o){return a.setLocalDescription(o).then(function(){return b.setRemoteDescription(o);});})
                  .then(function(){return b.createAnswer();})
                  .then(function(answer){return b.setLocalDescription(answer).then(function(){return a.setRemoteDescription(answer);});})
                  .catch(function(e){error=String(e);});
                """);
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(5))
            {
                engine.PerformMicrotaskCheckpoint();
                loop.RunOneTurn();
                if (engine.Evaluate("reply").AsString() == "echo:real transport") break;
                Thread.Sleep(10);
            }
            Assert.Equal("", engine.Evaluate("error").AsString());
            Assert.True(engine.Evaluate("received").AsString() == "real transport",
                engine.Evaluate("a.connectionState+':'+b.connectionState+':'+channel.readyState+':'+reply+':'+a.signalingState+':'+b.signalingState+':'+(a.remoteDescription&&a.remoteDescription.sdp)").AsString() + string.Join("\n", diagnostics));
            Assert.Equal("echo:real transport", engine.Evaluate("reply").AsString());
            Assert.Equal("connected", engine.Evaluate("a.connectionState").AsString());
            engine.Evaluate("a.close();b.close();");
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublicPeerReceivesTransportGatheringEventsOnDocumentLoop(bool native)
    {
        using IScriptEngine engine = native ? new NativeJavaScriptEngine() : new JintScriptEngine();
        using var document = Document.Parse("<html></html>", new FolioOptions { Capabilities = new FeatureCapabilities { WebRtc = true } });
        using var loop = new HtmlEventLoop(engine);
        DomBindings.Install(engine, document, eventLoop: loop);
        var context = new DomContext(engine, document.Node, capabilities: new FeatureCapabilities { WebRtc = true })
            { OwnerEventLoop = loop };
        using var state = WebRtcBindings.Install(engine, context);
        try
        {
            engine.Evaluate("var peer=new RTCPeerConnection(),states=[];peer.onicegatheringstatechange=function(){states.push(peer.iceGatheringState);};peer.createOffer();");
            loop.SpinUntilIdle();
            Assert.Equal("complete", engine.Evaluate("peer.iceGatheringState").AsString());
            Assert.Equal("gathering,complete", engine.Evaluate("states.join(',')").AsString());
            engine.Evaluate("peer.setRemoteDescription({type:'answer',sdp:'v=0\\r\\na=setup:active\\r\\n'});peer.addIceCandidate(null);");
            loop.SpinUntilIdle();
            Assert.Equal("new", engine.Evaluate("peer.connectionState").AsString());
            engine.Evaluate("peer.close();");
            loop.SpinUntilIdle();
            Assert.Equal("closed", engine.Evaluate("peer.connectionState").AsString());
        }
        finally { DomBindings.Dispose(engine); }
    }
}
