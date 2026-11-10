using Folio.Scripting.Dom;
using Folio.Scripting.Jint;
using Folio.Scripting.Native;

namespace Folio.Scripting.Tests;

public sealed class WebRtcTransportEventTests
{
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
