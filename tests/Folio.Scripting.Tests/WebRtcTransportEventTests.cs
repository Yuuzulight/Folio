using Folio.Scripting.Dom;
using Folio.Scripting.Jint;
using Folio.Scripting.Native;
using Folio.Scripting.Rtc;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Folio.Scripting.Tests;

public sealed class WebRtcTransportEventTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyRemoteAnswerCannotPublishConnectedStateOnDocumentLoop(bool native)
    {
        using IScriptEngine engine = native ? new NativeJavaScriptEngine() : new JintScriptEngine();
        using var document = Document.Parse("<html></html>", new FolioOptions { Capabilities = new FeatureCapabilities { WebRtc = true } });
        using var loop = new HtmlEventLoop(engine);
        DomBindings.Install(engine, document, eventLoop: loop);
        try
        {
            engine.Evaluate("""
                var peer=new RTCPeerConnection(), connected=0, rejected='';
                peer.onconnectionstatechange=function(){if(peer.connectionState==='connected')connected++;};
                peer.setRemoteDescription({type:'answer',sdp:''}).catch(function(error){rejected=error.name;});
                peer.addIceCandidate(null);
                """);
            engine.PerformMicrotaskCheckpoint();
            loop.SpinUntilIdle();
            Assert.Equal("TypeError", engine.Evaluate("rejected").AsString());
            Assert.True(engine.Evaluate("peer.remoteDescription === null").AsBoolean());
            Assert.Equal("new", engine.Evaluate("peer.connectionState").AsString());
            Assert.Equal("new", engine.Evaluate("peer.iceConnectionState").AsString());
            Assert.Equal(0, engine.Evaluate("connected").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void DocumentIceGatheringUsesOnlyConfiguredAndAllowedStunServers(bool native, bool configureAfterConstruction)
    {
        using var selected = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var unused = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var configured = $"stun:{selected.Client.LocalEndPoint}";
        var permissionOnly = $"stun:{unused.Client.LocalEndPoint}";
        using IScriptEngine engine = native ? new NativeJavaScriptEngine() : new JintScriptEngine();
        using var document = Document.Parse("<html></html>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebRtc = true },
            WebRtcAllowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { configured, permissionOnly }
        });
        using var loop = new HtmlEventLoop(engine);
        DomBindings.Install(engine, document, eventLoop: loop);
        try
        {
            engine.SetGlobalProperty("endpoint", ScriptValue.FromString(configured));
            engine.Evaluate("var empty = new RTCPeerConnection(); empty.createOffer(); empty.close();");
            Assert.False(selected.Client.Poll(0, SelectMode.SelectRead));
            Assert.False(unused.Client.Poll(0, SelectMode.SelectRead));

            selected.Client.ReceiveTimeout = 3000;
            var response = Task.Factory.StartNew(() =>
            {
                IPEndPoint sender = new(IPAddress.Any, 0);
                var bytes = selected.Receive(ref sender);
                Assert.True(StunMessage.TryParse(bytes, out var request));
                Assert.Equal(StunMessage.BindingRequest, request.Type);
                var address = new byte[8];
                address[1] = 1;
                BinaryPrimitives.WriteUInt16BigEndian(address.AsSpan(2), (ushort)(sender.Port ^ 0x2112));
                BinaryPrimitives.WriteUInt32BigEndian(address.AsSpan(4),
                    BinaryPrimitives.ReadUInt32BigEndian(sender.Address.GetAddressBytes()) ^ 0x2112a442);
                var reply = StunMessage.Encode(StunMessage.BindingResponse, request.TransactionId.Span,
                    [(StunMessage.XorMappedAddress, address)], [1]);
                selected.Send(reply, sender);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            engine.Evaluate("var config={iceServers:[{urls:endpoint}]}, sdp='';");
            engine.Evaluate(configureAfterConstruction
                ? "var peer=new RTCPeerConnection();peer.setConfiguration(config);"
                : "var peer=new RTCPeerConnection(config);");
            engine.Evaluate("peer.createDataChannel('stun');peer.createOffer().then(function(offer){sdp=offer.sdp;});");
            engine.PerformMicrotaskCheckpoint();
            response.GetAwaiter().GetResult();
            Assert.Contains(" typ srflx", engine.Evaluate("sdp").AsString());
            Assert.False(unused.Client.Poll(0, SelectMode.SelectRead));
            Assert.ThrowsAny<Exception>(() => engine.Evaluate("peer.setConfiguration({iceServers:[]})"));
            Assert.ThrowsAny<Exception>(() => engine.Evaluate("new RTCPeerConnection({iceTransportPolicy:'relay'})"));
            Assert.Equal("SecurityError", engine.Evaluate("""
                (function(){
                    try { new RTCPeerConnection({iceServers:[{urls:'stun:blocked.example'}]}); }
                    catch (error) { return error.name; }
                })()
                """).AsString());
            engine.Evaluate("peer.close()");
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void PausedDocumentLoopRetiresOverflowingPeerAndPreservesSibling(bool native, bool byteLimit)
    {
        using IScriptEngine engine = native ? new NativeJavaScriptEngine() : new JintScriptEngine();
        using var document = Document.Parse("<html></html>", new FolioOptions { Capabilities = new FeatureCapabilities { WebRtc = true } });
        using var loop = new HtmlEventLoop(engine);
        DomBindings.Install(engine, document, eventLoop: loop);
        try
        {
            engine.Evaluate("""
                var a=new RTCPeerConnection(), b=new RTCPeerConnection(), sibling=new RTCPeerConnection();
                var channel=a.createDataChannel('budget'), incoming=null, delivered=0, error='';
                channel.bufferedAmountLowThreshold=1048576;
                b.ondatachannel=function(e){incoming=e.channel;incoming.onmessage=function(){delivered++;};};
                a.createOffer().then(function(o){return a.setLocalDescription(o).then(function(){return b.setRemoteDescription(o);});})
                  .then(function(){return b.createAnswer();})
                  .then(function(answer){return b.setLocalDescription(answer).then(function(){return a.setRemoteDescription(answer);});})
                  .catch(function(e){error=String(e);});
                """);
            Assert.True(SpinWait.SpinUntil(() =>
            {
                engine.PerformMicrotaskCheckpoint();
                loop.RunOneTurn();
                return engine.Evaluate("incoming !== null && channel.readyState === 'open'").AsBoolean();
            }, TimeSpan.FromSeconds(10)), DomBindings.GetWebRtcDiagnostics(engine));
            loop.SpinUntilIdle();
            var message = byteLimit ? new string('x', 196608) : "x";
            engine.SetGlobalProperty("message", ScriptValue.FromString(message));
            var count = byteLimit ? 3 : WebRtcBindingState.MaxDeliveryEvents + 1;
            for (var index = 0; index < count; index++)
            {
                engine.Evaluate("channel.send(message)");
                Assert.True(SpinWait.SpinUntil(() => engine.Evaluate("channel.bufferedAmount === 0").AsBoolean(),
                    TimeSpan.FromSeconds(10)), DomBindings.GetWebRtcDiagnostics(engine));
            }
            Assert.Equal(0, engine.Evaluate("delivered").AsNumber());
            loop.SpinUntilIdle();
            Assert.Equal("", engine.Evaluate("error").AsString());
            Assert.Equal("closed", engine.Evaluate("b.connectionState").AsString());
            Assert.Equal("closed", engine.Evaluate("incoming.readyState").AsString());
            Assert.Equal("new", engine.Evaluate("sibling.connectionState").AsString());
            Assert.Equal(0, engine.Evaluate("delivered").AsNumber());
            engine.Evaluate("sibling.createDataChannel('still live')");
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Theory]
    [InlineData(false, 65536)]
    [InlineData(true, 65536)]
    [InlineData(false, 262144)]
    [InlineData(true, 262144)]
    public void PublicFragmentedBinaryRoundTripIsByteExactAndOwnsItsSource(bool native, int length)
    {
        using IScriptEngine engine = native ? new NativeJavaScriptEngine() : new JintScriptEngine();
        using var document = Document.Parse("<html></html>", new FolioOptions { Capabilities = new FeatureCapabilities { WebRtc = true } });
        using var loop = new HtmlEventLoop(engine);
        DomBindings.Install(engine, document, eventLoop: loop);
        try
        {
            var expected = Enumerable.Range(0, length).Select(index => (byte)(index % 251)).ToArray();
            engine.SetGlobalProperty("payload", engine.CreateArrayBuffer(expected));
            engine.Evaluate("""
                var a=new RTCPeerConnection(),b=new RTCPeerConnection(),received=null,reply=null,error='';
                b.ondatachannel=function(e){e.channel.onmessage=function(m){received=m.data;e.channel.send(m.data);};};
                var channel=a.createDataChannel('fragmented');
                channel.onopen=function(){channel.send(payload);new Uint8Array(payload).fill(0);};
                channel.onmessage=function(e){reply=e.data;};
                a.createOffer().then(function(o){return a.setLocalDescription(o).then(function(){return b.setRemoteDescription(o);});})
                  .then(function(){return b.createAnswer();})
                  .then(function(answer){return b.setLocalDescription(answer).then(function(){return a.setRemoteDescription(answer);});})
                  .catch(function(e){error=String(e);});
                """);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(10))
            {
                engine.PerformMicrotaskCheckpoint(); loop.RunOneTurn();
                if (engine.TryGetBufferSourceInfo(engine.Evaluate("reply"), out _)) break;
                Thread.Sleep(5);
            }
            Assert.Equal("", engine.Evaluate("error").AsString());
            var bytes = new byte[length];
            var state = engine.Evaluate("a.connectionState+':'+b.connectionState+':'+channel.readyState+':'+channel.bufferedAmount+':'+(received===null)+':'+(reply===null)+':ICE='+a.iceConnectionState+'/'+b.iceConnectionState+':signal='+a.signalingState+'/'+b.signalingState+':gather='+a.iceGatheringState+'/'+b.iceGatheringState").AsString();
            state += "; " + DomBindings.GetWebRtcDiagnostics(engine);
            Assert.True(engine.TryCopyBufferSource(engine.Evaluate("received"), 0, bytes), state); Assert.Equal(expected, bytes);
            Assert.True(engine.TryCopyBufferSource(engine.Evaluate("reply"), 0, bytes), state); Assert.Equal(expected, bytes);
            engine.Evaluate("a.close();b.close();");
            Assert.ThrowsAny<Exception>(() => engine.Evaluate("channel.send(payload);"));
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void PublicChannelExchangesStringsOverNegotiatedUdpTransport(bool native, bool binary)
    {
        using IScriptEngine engine = native ? new NativeJavaScriptEngine() : new JintScriptEngine();
        using var document = Document.Parse("<html></html>", new FolioOptions { Capabilities = new FeatureCapabilities { WebRtc = true } });
        using var loop = new HtmlEventLoop(engine);
        var diagnostics = new List<string>();
        loop.UncaughtException += error => diagnostics.Add(error.ToString());
        DomBindings.Install(engine, document, eventLoop: loop);
        try
        {
            engine.SetGlobalProperty("binaryProbe", ScriptValue.FromBoolean(binary));
            engine.Evaluate("""
                var a=new RTCPeerConnection(),b=new RTCPeerConnection(),received='',reply='',error='',lowEvents=0;
                var remoteChannel=null,remoteClosed=0,localClosed=0;
                b.ondatachannel=function(e){remoteChannel=e.channel;e.channel.onclose=function(){remoteClosed++;};e.channel.onmessage=function(m){received=binaryProbe?Array.from(new Uint8Array(m.data)).join(','):m.data;e.channel.send(binaryProbe?m.data:'echo:'+m.data);};};
                var channel=a.createDataChannel('public-udp');
                channel.onclose=function(){localClosed++;};
                channel.bufferedAmountLowThreshold=0;
                channel.onbufferedamountlow=function(){lowEvents++;};
                channel.onopen=function(){if(binaryProbe){var bytes=new Uint8Array([99,1,2,3,88]);channel.send(bytes.subarray(1,4));bytes.fill(0);}else channel.send('real transport');};
                channel.onmessage=function(e){reply=binaryProbe?Array.from(new Uint8Array(e.data)).join(','):e.data;};
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
                if (engine.Evaluate("reply").AsString() == (binary ? "1,2,3" : "echo:real transport")) break;
                Thread.Sleep(10);
            }
            Assert.Equal("", engine.Evaluate("error").AsString());
            Assert.True(engine.Evaluate("received").AsString() == (binary ? "1,2,3" : "real transport"),
                engine.Evaluate("a.connectionState+':'+b.connectionState+':'+channel.readyState+':'+reply+':'+a.signalingState+':'+b.signalingState+':'+(a.remoteDescription&&a.remoteDescription.sdp)").AsString() + string.Join("\n", diagnostics));
            Assert.Equal(binary ? "1,2,3" : "echo:real transport", engine.Evaluate("reply").AsString());
            Assert.Equal("connected", engine.Evaluate("a.connectionState").AsString());
            loop.SpinUntilIdle();
            Assert.True(engine.Evaluate("channel.bufferedAmount===0 && channel.bufferedAmountLowThreshold===0 && lowEvents>0").AsBoolean());
            Assert.ThrowsAny<Exception>(() => engine.Evaluate("channel.send(new Uint8Array(262145));"));
            Assert.Equal("open", engine.Evaluate("channel.readyState").AsString());

            // binaryType: arraybuffer by default, unknown values ignored, blob wraps binary messages.
            engine.Evaluate("var defaultType=channel.binaryType;channel.binaryType='nonsense';var keptType=channel.binaryType;channel.binaryType='blob';var blobReply=null;channel.onmessage=function(e){blobReply=e.data;};if(binaryProbe)channel.send(new Uint8Array([7,8,9]));");
            Assert.Equal("arraybuffer,arraybuffer,blob", engine.Evaluate("[defaultType,keptType,channel.binaryType].join()").AsString());
            if (binary)
            {
                var blobWait = System.Diagnostics.Stopwatch.StartNew();
                while (blobWait.Elapsed < TimeSpan.FromSeconds(5) && !engine.Evaluate("blobReply!==null").AsBoolean())
                { engine.PerformMicrotaskCheckpoint(); loop.RunOneTurn(); Thread.Sleep(10); }
                // Byte-exact Blob contents wait for the document Blob to hold bytes (it is text-only today).
                Assert.True(engine.Evaluate("blobReply instanceof Blob && blobReply.size===3").AsBoolean());
            }

            // close() resets the stream, so the remote channel closes too; each side sees one close event.
            engine.Evaluate("channel.close();");
            var closeWait = System.Diagnostics.Stopwatch.StartNew();
            while (closeWait.Elapsed < TimeSpan.FromSeconds(5) && !engine.Evaluate("localClosed===1&&remoteClosed===1").AsBoolean())
            { engine.PerformMicrotaskCheckpoint(); loop.RunOneTurn(); Thread.Sleep(10); }
            Assert.Equal("closed,closed,1,1", engine.Evaluate("[channel.readyState,remoteChannel.readyState,localClosed,remoteClosed].join()").AsString());
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
            engine.Evaluate("""
                var peer=new RTCPeerConnection(),events=[],candidates=[],offer=null;
                peer.onicegatheringstatechange=function(){events.push(peer.iceGatheringState);};
                peer.onicecandidate=function(e){events.push(e.candidate===null?'end':'candidate');if(e.candidate)candidates.push(e.candidate);};
                peer.createDataChannel('gather');
                peer.createOffer().then(function(value){offer=value;});
                """);
            engine.PerformMicrotaskCheckpoint();
            loop.SpinUntilIdle();
            Assert.Equal("new", engine.Evaluate("peer.iceGatheringState").AsString());
            Assert.Equal(0, engine.Evaluate("events.length").AsNumber());
            engine.Evaluate("peer.setLocalDescription(offer);");
            loop.SpinUntilIdle();
            Assert.Equal("complete", engine.Evaluate("peer.iceGatheringState").AsString());
            var count = (int)engine.Evaluate("candidates.length").AsNumber();
            Assert.True(count > 0);
            Assert.Equal(string.Join(',', new[] { "gathering" }.Concat(Enumerable.Repeat("candidate", count)).Concat(["complete", "end"])),
                engine.Evaluate("events.join(',')").AsString());
            var sdp = engine.Evaluate("offer.sdp").AsString();
            for (var index = 0; index < count; index++)
            {
                var candidate = $"candidates[{index}]";
                Assert.True(engine.Evaluate($"{candidate} instanceof RTCIceCandidate").AsBoolean());
                Assert.Equal("0", engine.Evaluate($"{candidate}.sdpMid").AsString());
                Assert.Equal(0, engine.Evaluate($"{candidate}.sdpMLineIndex").AsNumber());
                Assert.Contains($"a=ice-ufrag:{engine.Evaluate($"{candidate}.usernameFragment").AsString()}\r\n", sdp);
                Assert.Contains($"a={engine.Evaluate($"{candidate}.candidate").AsString()}\r\n", sdp);
            }
            engine.Evaluate("peer.setLocalDescription(offer);");
            loop.SpinUntilIdle();
            Assert.Equal(count + 3, engine.Evaluate("events.length").AsNumber());
            engine.Evaluate("peer.setRemoteDescription({type:'answer',sdp:'v=0\\r\\na=setup:active\\r\\n'});peer.addIceCandidate(null);");
            loop.SpinUntilIdle();
            Assert.Equal("new", engine.Evaluate("peer.connectionState").AsString());
            engine.Evaluate("peer.close();");
            loop.SpinUntilIdle();
            Assert.Equal("closed", engine.Evaluate("peer.connectionState").AsString());
        }
        finally { DomBindings.Dispose(engine); }
    }

    private sealed class FakeClock : TimeProvider
    {
        internal long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
        public void AdvanceSeconds(double seconds) => Ticks += (long)(seconds * TimeSpan.TicksPerSecond);
    }

    private sealed class RateRig : IDisposable
    {
        private readonly IScriptEngine _engine;
        private readonly Document _document;
        private readonly HtmlEventLoop _loop;
        private readonly WebRtcBindingState _state;
        internal FakeClock Clock { get; } = new();
        internal WebRtcBindingState.ChannelLease A { get; }
        internal WebRtcBindingState.ChannelLease B { get; }

        internal RateRig(bool native)
        {
            _engine = native ? new NativeJavaScriptEngine() : new JintScriptEngine();
            _document = Document.Parse("<html></html>", new FolioOptions { Capabilities = new FeatureCapabilities { WebRtc = true } });
            _loop = new HtmlEventLoop(_engine);
            DomBindings.Install(_engine, _document, eventLoop: _loop);
            var context = new DomContext(_engine, _document.Node, capabilities: new FeatureCapabilities { WebRtc = true }) { OwnerEventLoop = _loop };
            _state = WebRtcBindings.Install(_engine, context);
            _state.Clock = Clock;
            _engine.Evaluate("""
                var a = new RTCPeerConnection(), b = new RTCPeerConnection();
                var chA = a.createDataChannel('chA'), chB = b.createDataChannel('chB');
                var aMessages = 0, bMessages = 0;
                chA.onmessage = function() { aMessages++; };
                chB.onmessage = function() { bMessages++; };
                """);
            A = Lease("chA"); B = Lease("chB");
        }

        private WebRtcBindingState.ChannelLease Lease(string name)
        {
            Assert.True(_engine.TryGetHostTarget(_engine.Evaluate(name + ".__nativeChannel"), out var target));
            return Assert.IsType<WebRtcBindingState.ChannelLease>(target);
        }

        /// <summary>Delivers one tiny message and drains the owner loop, so the 256-event and byte caps can never be what trips.</summary>
        internal void Send(WebRtcBindingState.ChannelLease channel) { channel.Channel.DeliverMessage("x"); _loop.SpinUntilIdle(); }
        internal int Count(string name) => (int)_engine.Evaluate(name).AsNumber();
        internal string State(string peer) => _engine.Evaluate(peer + ".connectionState").AsString();

        public void Dispose() { _state.Dispose(); _loop.Dispose(); _document.Dispose(); _engine.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MessageTokenExhaustionAloneRetiresThePeer(bool native)
    {
        using var rig = new RateRig(native);
        for (var i = 0; i < 256; i++) rig.Send(rig.A); // the burst, every event delivered before the next is queued
        Assert.False(rig.A.Peer.DeliveryOverflow);
        Assert.Equal(256, rig.Count("aMessages"));
        rig.Send(rig.A); // no clock advance: token 257 is the first refusal, with an empty delivery queue
        Assert.True(rig.A.Peer.DeliveryOverflow);
        Assert.Equal(256, rig.Count("aMessages"));
        Assert.Equal("closed", rig.State("a"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SustainedThousandMessagesPerSecondIsNotThrottled(bool native)
    {
        using var rig = new RateRig(native);
        for (var i = 0; i < 3000; i++) { rig.Send(rig.A); rig.Clock.AdvanceSeconds(0.001); }
        Assert.False(rig.A.Peer.DeliveryOverflow);
        Assert.Equal(3000, rig.Count("aMessages"));
        Assert.NotEqual("closed", rig.State("a"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MessageTokensRefillWithTheClock(bool native)
    {
        using var rig = new RateRig(native);
        for (var i = 0; i < 256; i++) rig.Send(rig.A);
        rig.Clock.AdvanceSeconds(0.05); // 50 tokens
        for (var i = 0; i < 50; i++) rig.Send(rig.A);
        Assert.False(rig.A.Peer.DeliveryOverflow);
        rig.Send(rig.A); // the 51st has no token
        Assert.True(rig.A.Peer.DeliveryOverflow);
        Assert.Equal(306, rig.Count("aMessages"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FloodingPeerRetiresItselfAndSiblingIsStillDelivered(bool native)
    {
        using var rig = new RateRig(native);
        for (var i = 0; i < 257; i++) rig.Send(rig.A);
        Assert.True(rig.A.Peer.DeliveryOverflow);
        rig.Send(rig.B); // right after, same instant
        Assert.False(rig.B.Peer.DeliveryOverflow);
        Assert.Equal(1, rig.Count("bMessages"));
        Assert.Equal("closed", rig.State("a"));
        Assert.NotEqual("closed", rig.State("b"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemotePeerFloodingMessagesDoesNotExhaustMemoryOrStallEventLoop(bool native)
    {
        using IScriptEngine engine = native ? new NativeJavaScriptEngine() : new JintScriptEngine();
        using var document = Document.Parse("<html></html>", new FolioOptions { Capabilities = new FeatureCapabilities { WebRtc = true } });
        using var loop = new HtmlEventLoop(engine);
        DomBindings.Install(engine, document, eventLoop: loop);
        var context = new DomContext(engine, document.Node, capabilities: new FeatureCapabilities { WebRtc = true }) { OwnerEventLoop = loop };
        using var state = WebRtcBindings.Install(engine, context);
        var clock = new FakeClock();
        state.Clock = clock;

        engine.Evaluate("""
            var a = new RTCPeerConnection();
            var chA = a.createDataChannel('flood');
            var received = 0;
            chA.onmessage = function() { received++; };
            """);

        var chAVal = engine.Evaluate("chA.__nativeChannel");
        Assert.True(engine.TryGetHostTarget(chAVal, out var targetA));
        var leaseA = Assert.IsType<WebRtcBindingState.ChannelLease>(targetA);

        // Simulate remote peer flooding large 64 KiB binary messages
        var payload = new byte[64 * 1024];
        for (var i = 0; i < 50; i++)
        {
            leaseA.Channel.DeliverMessage(payload);
        }

        // Peer exceeded MaxDeliveryBytes (1 MiB) or message burst, so it was capped and overflowed
        Assert.True(leaseA.Peer.DeliveryOverflow);
        loop.SpinUntilIdle();
        Assert.Equal("closed", engine.Evaluate("a.connectionState").AsString());
    }
}
