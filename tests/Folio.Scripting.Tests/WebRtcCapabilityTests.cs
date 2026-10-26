using Folio.Dom;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;
using Folio.Scripting.Native;

namespace Folio.Scripting.Tests;

public sealed class WebRtcCapabilityTests
{
    [Fact]
    public void BufferThresholdCallbackCannotDeliverAfterReentrantRevocation()
    {
        using var fixture = new Fixture(true, 1);
        var control = new HostObjectTemplate("HostRtcControl").DefineMethod("revoke", (_, _, _) =>
        {
            fixture.Context.UpdateCapabilities(FeatureCapabilities.Default);
            fixture.State.UpdateCapabilities();
            fixture.Context.UpdateCapabilities(new FeatureCapabilities { WebRtc = true });
            return ScriptValue.Undefined;
        });
        fixture.Engine.SetGlobalProperty("hostControl", fixture.Engine.CreateHostObject(control, new object()));
        fixture.Engine.Evaluate("var peer=new RTCPeerConnection(),channel=peer.createDataChannel('x'),delivered=0,rejected=false;channel.onmessage=()=>delivered++;channel.bufferedAmount=1;channel.onbufferedamountlow=()=>hostControl.revoke();try{channel.send('after-revoke');}catch(e){rejected=e.name==='InvalidStateError';}");
        Assert.True(fixture.Engine.Evaluate("rejected && delivered === 0").AsBoolean());
        Assert.Equal(0, fixture.State.ActivePeerCount);
    }

    [Fact]
    public void DeniedConstructionReportsCapabilityAndDoesNotAcquirePeer()
    {
        using var fixture = new Fixture(false, 1);
        Assert.ThrowsAny<Exception>(() => fixture.Engine.Evaluate("new RTCPeerConnection()"));
        Assert.Equal(0, fixture.State.ActivePeerCount);
        Assert.Equal(1, fixture.Denials);
    }

    [Fact]
    public void QuotaIsReleasedByCloseAndRetainedReferencesDieOnRevocation()
    {
        using var fixture = new Fixture(true, 1);
        fixture.Engine.Evaluate("var first = new RTCPeerConnection(); first.close(); var second = new RTCPeerConnection();");
        Assert.Equal(1, fixture.State.ActivePeerCount);
        fixture.Context.UpdateCapabilities(new Folio.FeatureCapabilities { WebRtc = false });
        fixture.State.UpdateCapabilities();
        Assert.Equal(0, fixture.State.ActivePeerCount);
        AssertAsyncRejected(fixture, "first.createOffer()");
        AssertAsyncRejected(fixture, "second.createOffer()");
    }

    [Fact]
    public void RetainedConstructorCannotForgeAuthorityAfterRegrant()
    {
        using var fixture = new Fixture(true, 2);
        fixture.Engine.Evaluate("var Constructor = RTCPeerConnection; var first = new Constructor(); globalThis.__folio_webrtc_authority = { acquire: function() { throw new Error('forged'); } }; ");
        fixture.Context.UpdateCapabilities(new Folio.FeatureCapabilities { WebRtc = false });
        fixture.State.UpdateCapabilities();
        fixture.Context.UpdateCapabilities(new Folio.FeatureCapabilities { WebRtc = true });
        fixture.State.UpdateCapabilities();
        fixture.Engine.Evaluate("var second = new Constructor();");
        Assert.Equal(1, fixture.State.ActivePeerCount);
        AssertAsyncRejected(fixture, "first.createOffer()");
        Assert.True(fixture.Engine.Evaluate("second.connectionState === 'new'").AsBoolean());
    }

    [Fact]
    public void OriginAllowlistIsCheckedByNativeAuthority()
    {
        using var fixture = new Fixture(true, 2, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "turn://allowed.example:3478" });
        fixture.Engine.Evaluate("new RTCPeerConnection({ iceServers: [{ urls: 'turn://allowed.example:3478' }] })");
        Assert.ThrowsAny<Exception>(() => fixture.Engine.Evaluate("new RTCPeerConnection({ iceServers: [{ urls: 'turn://denied.example:3478' }] })"));
        Assert.Equal(1, fixture.State.ActivePeerCount);
    }

    [Fact]
    public void LoopbackPeerNegotiationDeliversDataAndStats()
    {
        using var fixture = new Fixture(true, 2);
        fixture.Engine.Evaluate("var a=new RTCPeerConnection(), b=new RTCPeerConnection(); var got=null; b.ondatachannel=function(e){ e.channel.onmessage=function(x){ got=x.data; }; }; a.setLocalDescription({type:'offer',sdp:''}); b.setRemoteDescription({type:'offer',sdp:''}); b.setLocalDescription({type:'answer',sdp:''}); a.setRemoteDescription({type:'answer',sdp:''}); var ch=a.createDataChannel('chat'); ch.send('hello');");
        Assert.Equal("hello", fixture.Engine.Evaluate("got").AsString());
        fixture.Engine.Evaluate("a.getStats().then(x=>{ statsHas=x.has('data-channels'); statsCount=x.size; })");
        fixture.Engine.PerformMicrotaskCheckpoint();
        Assert.True(fixture.Engine.Evaluate("statsHas").AsBoolean());
        Assert.True(fixture.Engine.Evaluate("statsCount >= 2").AsBoolean());
    }

    [Fact]
    public void NativeEngineLoopbackPeerNegotiationDeliversData()
    {
        using var engine = new NativeJavaScriptEngine();
        using var document = Document.Parse("<html></html>", new Folio.FolioOptions { Capabilities = new Folio.FeatureCapabilities { WebRtc = true } });
        DomBindings.Install(engine, document);
        var context = new DomContext(engine, document.Node, capabilities: new Folio.FeatureCapabilities { WebRtc = true }, maxWebRtcConnections: 2);
        using var state = WebRtcBindings.Install(engine, context);
        engine.Evaluate("var a=new RTCPeerConnection(), b=new RTCPeerConnection(); var got=''; b.ondatachannel=function(e){ e.channel.onmessage=function(x){ got=x.data; }; }; a.setLocalDescription({type:'offer',sdp:''}); b.setRemoteDescription({type:'offer',sdp:''}); b.setLocalDescription({type:'answer',sdp:''}); a.setRemoteDescription({type:'answer',sdp:''}); var ch=a.createDataChannel('chat'); ch.send('hello');");
        Assert.Equal("hello", engine.Evaluate("got").AsString());
    }

    [Fact]
    public void OffersContainAStableSessionDescription()
    {
        using var fixture = new Fixture(true, 1);
        fixture.Engine.Evaluate("var peer=new RTCPeerConnection();peer.createDataChannel('chat');var offerSdp='';peer.createOffer().then(o=>offerSdp=o.sdp);");
        fixture.Engine.PerformMicrotaskCheckpoint();
        var sdp = fixture.Engine.Evaluate("offerSdp").AsString();
        Assert.Contains("v=0", sdp);
        Assert.Contains("m=application", sdp);
    }

    [Fact]
    public void ClosingRecipientInvalidatesBothLoopbackChannelEndpoints()
    {
        using var fixture = new Fixture(true, 2);
        fixture.Engine.Evaluate("var a=new RTCPeerConnection(), b=new RTCPeerConnection(); var remotes=[]; b.ondatachannel=function(e){ remotes.push(e.channel); }; var beforePair=a.createDataChannel('before-pair'); a.setLocalDescription({type:'offer',sdp:''}); b.setRemoteDescription({type:'offer',sdp:''}); b.setLocalDescription({type:'answer',sdp:''}); a.setRemoteDescription({type:'answer',sdp:''}); var source=a.createDataChannel('after-pair');");

        Assert.True(fixture.Engine.Evaluate("remotes.length === 2").AsBoolean());
        fixture.Engine.Evaluate("var deliveries=[];remotes[0].onmessage=e=>deliveries.push('before:'+e.data);remotes[1].onmessage=e=>deliveries.push('after:'+e.data);beforePair.send('one');source.send('two');beforePair.onmessage=e=>deliveries.push('return-before:'+e.data);source.onmessage=e=>deliveries.push('return-after:'+e.data);remotes[0].send('three');remotes[1].send('four');");
        Assert.Equal("before:one,after:two,return-before:three,return-after:four", fixture.Engine.Evaluate("deliveries.join(',')").AsString());
        fixture.Engine.Evaluate("b.close();");
        Assert.True(fixture.Engine.Evaluate("(function(){ var sourceFailed=false, beforeFailed=false, beforeRemoteFailed=false, remoteFailed=false; try { source.send('after-close'); } catch (_) { sourceFailed=true; } try { beforePair.send('after-close'); } catch (_) { beforeFailed=true; } try { remotes[0].send('after-close'); } catch (_) { beforeRemoteFailed=true; } try { remotes[1].send('after-close'); } catch (_) { remoteFailed=true; } return sourceFailed && beforeFailed && beforeRemoteFailed && remoteFailed; })()").AsBoolean());

        fixture.Engine.Evaluate("a.close();");
        Assert.Equal(0, fixture.State.ActivePeerCount);
    }

    [Fact]
    public void NativeRevocationClosesDespiteScriptMonkeypatches()
    {
        using var fixture = new Fixture(true, 1);
        fixture.Engine.Evaluate("var peer=new RTCPeerConnection(); var channel=peer.createDataChannel('x'); peer.close=function(){}; channel.close=function(){};");
        fixture.Context.UpdateCapabilities(new Folio.FeatureCapabilities { WebRtc = false });
        fixture.State.UpdateCapabilities();
        Assert.True(fixture.Engine.Evaluate("peer.connectionState === 'closed' && channel.readyState === 'closed'").AsBoolean());
        Assert.Equal(0, fixture.State.ActivePeerCount);
    }

    [Fact]
    public void ChannelOwnershipAndRetirementArePrivate()
    {
        using var fixture = new Fixture(true, 2);
        fixture.Engine.Evaluate("var owner=new RTCPeerConnection(), other=new RTCPeerConnection(); var channel=owner.createDataChannel('x'); channel._connection=other; owner._dataChannels=[]; channel.close=function(){}; WeakMap.prototype.get=function(){return null;};");
        fixture.Context.UpdateCapabilities(new Folio.FeatureCapabilities { WebRtc = false });
        fixture.State.UpdateCapabilities();
        Assert.True(fixture.Engine.Evaluate("(function(){ try { channel.send('forged'); return false; } catch (_) { return owner.connectionState === 'closed' && channel.readyState === 'closed'; } })()").AsBoolean());
        Assert.Equal(0, fixture.State.ActivePeerCount);
    }

    private sealed class Fixture : IDisposable
    {
        public JintScriptEngine Engine { get; } = new();
        public Document Document { get; }
        public DomContext Context { get; }
        public WebRtcBindingState State { get; }
        public int Denials { get; private set; }

        public Fixture(bool granted, int maxPeers, IReadOnlySet<string>? origins = null)
        {
            Document = Document.Parse("<html></html>", new Folio.FolioOptions());
            DomBindings.Install(Engine, Document);
            var capabilities = new Folio.FeatureCapabilities { WebRtc = granted };
            Context = new DomContext(Engine, Document.Node, capabilities: capabilities, maxWebRtcConnections: maxPeers, webRtcAllowedOrigins: origins);
            Document.Node.DiagnosticSink = diagnostic => { if (diagnostic.Code == DiagnosticCode.CapabilityDenied) Denials++; };
            State = WebRtcBindings.Install(Engine, Context);
        }

        public void Dispose()
        {
            State.Dispose();
            Engine.Dispose();
            Document.Dispose();
        }
    }

    private static void AssertAsyncRejected(Fixture fixture, string expression)
    {
        fixture.Engine.Evaluate($"asyncRejected=false; Promise.resolve().then(function(){{ return {expression}; }}).then(function(){{ asyncRejected=false; }}, function(){{ asyncRejected=true; }});");
        fixture.Engine.PerformMicrotaskCheckpoint();
        fixture.Engine.PerformMicrotaskCheckpoint();
        Assert.True(fixture.Engine.Evaluate("asyncRejected").AsBoolean());
    }
}
