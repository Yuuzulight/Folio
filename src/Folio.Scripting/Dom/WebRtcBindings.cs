using Folio;
using Folio.Scripting.Rtc;

namespace Folio.Scripting.Dom;

internal sealed class WebRtcBindingState : IDisposable
{
    private readonly IScriptEngine _engine;
    internal readonly DomContext _context;
    private readonly object _gate = new();
    private readonly Dictionary<long, PeerLease> _peers = [];
    private long _nextId;
    private bool _disposed;
    internal int ActivePeerCount { get { lock (_gate) return _peers.Count; } }
    internal WebRtcBindingState(IScriptEngine engine, DomContext context) { _engine = engine; _context = context; }
    private bool PermissionGranted() => !_disposed && _context.Capabilities.WebRtc && !_context.ScriptFeaturesDisposed;
    private void EnsurePermission() { if (!PermissionGranted()) { _context.CheckCapability("WebRTC", false); throw WebIdlException.DomException("NotSupportedError", "WebRTC is disabled by the host."); } }

    internal ScriptValue Acquire()
    {
        EnsurePermission();
        lock (_gate)
        {
            if (_disposed) throw WebIdlException.DomException("InvalidStateError", "The document is closed.");
            if (_peers.Count >= _context.MaxWebRtcConnections) throw WebIdlException.DomException("QuotaExceededError", "The WebRTC peer connection limit has been exceeded.");
            var lease = new PeerLease(this, ++_nextId);
            _peers.Add(lease.Id, lease);
            try { return _engine.CreateHostObject(PeerTemplate, lease); }
            catch { _peers.Remove(lease.Id); lease.Dispose(); throw; }
        }
    }

    internal bool IsLive(ScriptValue value)
    {
        if (!_engine.TryGetHostTarget(value, out var target) || target is not PeerLease lease || !ReferenceEquals(lease.Owner, this)) return false;
        if (!_context.CheckCapability("WebRTC", PermissionGranted())) return false;
        lock (_gate) return !_disposed && _peers.ContainsKey(lease.Id);
    }

    internal void Register(ScriptValue value, ScriptValue callback)
    {
        if (!callback.IsFunction || !_engine.TryGetHostTarget(value, out var target) || target is not PeerLease lease || !ReferenceEquals(lease.Owner, this)) return;
        lock (_gate) if (!_disposed && _peers.TryGetValue(lease.Id, out var current) && PermissionGranted()) current.CloseCallback = callback;
    }

    internal void RegisterTransport(ScriptValue value, ScriptValue callback)
    {
        if (!callback.IsFunction || !_engine.TryGetHostTarget(value, out var target) || target is not PeerLease lease ||
            !ReferenceEquals(lease.Owner, this)) return;
        lock (_gate)
        {
            if (!PermissionGranted() || !_peers.ContainsKey(lease.Id) || lease.TransportRegistered) return;
            lease.TransportRegistered = true;
            lease.TransportCallback = callback;
        }
        void Queue(string kind, string state)
        {
            // Transport callbacks run on socket/timer threads. Only touch the
            // script engine from its document event loop, after rechecking life.
            var loop = _context.OwnerEventLoop;
            if (loop is null) return;
            try
            {
                loop.QueueTask(TaskSource.Generic, () =>
                {
                    ScriptValue handler;
                    lock (_gate)
                    {
                        if (!PermissionGranted() || !_peers.ContainsKey(lease.Id)) return;
                        handler = lease.TransportCallback;
                    }
                    if (handler.IsFunction)
                        _engine.Call(handler, ScriptValue.Undefined, [ScriptValue.FromString(kind), ScriptValue.FromString(state)]);
                }, "WebRTC transport state");
            }
            catch (ObjectDisposedException) { }
        }
        lease.Controller.OnConnectionStateChange += state => Queue("connectionState", state);
        lease.Controller.OnIceConnectionStateChange += state => Queue("iceConnectionState", state);
        lease.Controller.OnIceGatheringStateChange += state => Queue("iceGatheringState", state);
        lease.Controller.OnDataChannel += channel =>
        {
            var wrapped = new ChannelLease(this, lease, channel);
            QueuePeer(lease, () => _engine.Call(lease.TransportCallback, ScriptValue.Undefined,
                [ScriptValue.FromString("datachannel"), _engine.CreateHostObject(ChannelTemplate, wrapped)]));
        };
    }

    private void QueuePeer(PeerLease peer, Action action)
    {
        try
        {
            _context.OwnerEventLoop?.QueueTask(TaskSource.Generic, () =>
            {
                lock (_gate) if (!PermissionGranted() || !_peers.ContainsKey(peer.Id)) return;
                action();
            }, "WebRTC data channel");
        }
        catch (ObjectDisposedException) { }
    }

    private ScriptValue CreateChannel(ScriptValue value, string label, string protocol)
    {
        EnsurePermission();
        if (_context.OwnerEventLoop is null || !_engine.TryGetHostTarget(value, out var target) || target is not PeerLease peer ||
            !ReferenceEquals(peer.Owner, this) || !IsLive(value))
            throw WebIdlException.DomException("InvalidStateError", "No live WebRTC transport.");
        var channel = peer.Controller.CreateDataChannel(label, protocol);
        return _engine.CreateHostObject(ChannelTemplate, new ChannelLease(this, peer, channel));
    }

    private sealed class ChannelLease
    {
        internal WebRtcBindingState Owner { get; }
        internal PeerLease Peer { get; }
        internal SctpDataChannel.ChannelState Channel { get; }
        internal ScriptValue Callback;
        internal ChannelLease(WebRtcBindingState owner, PeerLease peer, SctpDataChannel.ChannelState channel)
        {
            Owner = owner; Peer = peer; Channel = channel;
            channel.OnOpen += () => Notify("open", null);
            channel.OnClose += () => Notify("close", null);
            channel.OnMessage += value => Notify("message", value);
        }
        internal void Notify(string kind, object? value) => Owner.QueuePeer(Peer, () =>
        {
            if (!Callback.IsFunction) return;
            var payload = value is string text ? ScriptValue.FromString(text)
                : value is byte[] bytes ? Owner._engine.CreateArrayBuffer(bytes) : ScriptValue.Undefined;
            Owner._engine.Call(Callback, ScriptValue.Undefined, [ScriptValue.FromString(kind), payload]);
        });
        internal void Send(ScriptValue value)
        {
            lock (Owner._gate)
                if (!Owner.PermissionGranted() || !Owner._peers.ContainsKey(Peer.Id))
                    throw WebIdlException.DomException("InvalidStateError", "WebRTC peer is closed.");
            if (value.IsString) { Channel.Send(value.AsString() ?? ""); return; }
            if (!Owner._engine.TryGetBufferSourceInfo(value, out var info))
                throw new ArgumentException("Data channel send requires a string or attached buffer source.");
            if (info.ByteLength > SctpDataChannel.MaxUnfragmentedMessageBytes)
                throw WebIdlException.DomException("OperationError", "Data channel message requires SCTP fragmentation.");
            var bytes = new byte[info.ByteLength];
            if (!Owner._engine.TryCopyBufferSource(value, 0, bytes))
                throw new ArgumentException("Cannot read data channel buffer source.");
            Channel.Send(bytes);
        }
    }

    private static readonly HostObjectTemplate ChannelTemplate = new HostObjectTemplate("RTCDataChannelTransport")
        .DefineProperty("label", (target, _) => ScriptValue.FromString(((ChannelLease)target).Channel.Label))
        .DefineProperty("protocol", (target, _) => ScriptValue.FromString(((ChannelLease)target).Channel.Protocol))
        .DefineProperty("id", (target, _) => ScriptValue.FromNumber(((ChannelLease)target).Channel.Id))
        .DefineProperty("readyState", (target, _) => ScriptValue.FromString(((ChannelLease)target).Channel.ReadyState))
        .DefineMethod("register", (target, _, args) =>
        {
            var lease = (ChannelLease)target;
            if (args.Length > 0 && args[0].IsFunction)
            {
                lease.Callback = args[0];
                if (lease.Channel.ReadyState == "open") lease.Notify("open", null);
            }
            return ScriptValue.Undefined;
        })
        .DefineMethod("send", (target, _, args) => { ((ChannelLease)target).Send(args.Length > 0 ? args[0] : ScriptValue.Undefined); return ScriptValue.Undefined; })
        .DefineMethod("close", (target, _, _) => { ((ChannelLease)target).Channel.Close(); return ScriptValue.Undefined; });

    internal void Release(ScriptValue value)
    {
        if (!_engine.TryGetHostTarget(value, out var target) || target is not PeerLease lease || !ReferenceEquals(lease.Owner, this)) return;
        lock (_gate) if (_peers.Remove(lease.Id)) { lease.Dispose(); }
    }

    internal bool AllowOrigin(string value)
    {
        if (!PermissionGranted() || !Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        var origin = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        return _context.WebRtcAllowedOrigins.Contains(origin) || _context.WebRtcAllowedOrigins.Contains(value.TrimEnd('/'));
    }

    internal string CreateOffer(ScriptValue value, bool hasDataChannels, int audioCount, int videoCount)
    {
        if (!_engine.TryGetHostTarget(value, out var target) || target is not PeerLease lease || !ReferenceEquals(lease.Owner, this))
            return "";
        return lease.Controller.CreateOfferAsync(hasDataChannels, audioCount, videoCount).GetAwaiter().GetResult();
    }

    internal string CreateAnswer(ScriptValue value, bool hasDataChannels, int audioCount, int videoCount)
    {
        if (!_engine.TryGetHostTarget(value, out var target) || target is not PeerLease lease || !ReferenceEquals(lease.Owner, this))
            return "";
        return lease.Controller.CreateAnswerAsync(hasDataChannels, audioCount, videoCount).GetAwaiter().GetResult();
    }

    internal void SetLocalDescription(ScriptValue value, string type, string sdp)
    {
        if (_engine.TryGetHostTarget(value, out var target) && target is PeerLease lease && ReferenceEquals(lease.Owner, this))
            lease.Controller.SetLocalDescription(type, sdp);
    }

    internal void SetRemoteDescription(ScriptValue value, string type, string sdp)
    {
        if (_engine.TryGetHostTarget(value, out var target) && target is PeerLease lease && ReferenceEquals(lease.Owner, this))
            lease.Controller.SetRemoteDescription(type, sdp);
    }

    internal void AddIceCandidate(ScriptValue value, string candidate)
    {
        if (_engine.TryGetHostTarget(value, out var target) && target is PeerLease lease && ReferenceEquals(lease.Owner, this))
            lease.Controller.AddIceCandidate(candidate);
    }

    internal void UpdateCapabilities() { if (!PermissionGranted()) CloseAll(); }

    internal void CloseAll()
    {
        PeerLease[] peers;
        lock (_gate)
        {
            peers = _peers.Values.ToArray();
            _peers.Clear();
            foreach (var peer in peers) peer.Released = true;
        }
        foreach (var peer in peers)
        {
            var cb = peer.CloseCallback;
            peer.CloseCallback = ScriptValue.Undefined;
            if (!cb.IsUndefined && cb.IsFunction)
            {
                try { _engine.Call(cb, ScriptValue.Undefined, []); } catch { }
            }
            peer.Controller.Dispose();
        }
    }

    public void Dispose() { lock (_gate) _disposed = true; CloseAll(); }

    private static readonly HostObjectTemplate PeerTemplate = new("RTCPeerConnectionLease");
    private sealed class PeerLease(WebRtcBindingState owner, long id) : IDisposable
    {
        internal WebRtcBindingState Owner { get; } = owner;
        internal long Id { get; } = id;
        internal bool Released { get; set; }
        internal ScriptValue CloseCallback { get; set; } = ScriptValue.Undefined;
        internal bool TransportRegistered { get; set; }
        internal ScriptValue TransportCallback { get; set; } = ScriptValue.Undefined;
        internal RtcPeerConnectionController Controller { get; } = new(owner._context.WebRtcAllowedOrigins?.ToArray());

        public void Dispose()
        {
            Released = true;
            CloseCallback = ScriptValue.Undefined;
            TransportCallback = ScriptValue.Undefined;
            Controller.Dispose();
        }
    }

    internal static WebRtcBindingState Install(IScriptEngine engine, DomContext context)
    {
        var state = new WebRtcBindingState(engine, context);
        var authority = new HostObjectTemplate("WebRtcAuthority")
            .DefineMethod("acquire", (_, _, _) => state.Acquire())
            .DefineMethod("live", (_, _, a) => ScriptValue.FromBoolean(a.Length > 0 && state.IsLive(a[0])))
            .DefineMethod("register", (_, _, a) => { if (a.Length > 1) state.Register(a[0], a[1]); return ScriptValue.Undefined; })
            .DefineMethod("registerTransport", (_, _, a) => { if (a.Length > 1) state.RegisterTransport(a[0], a[1]); return ScriptValue.Undefined; })
            .DefineMethod("transportEnabled", (_, _, _) => ScriptValue.FromBoolean(context.OwnerEventLoop is not null))
            .DefineMethod("createChannel", (_, _, a) => state.CreateChannel(a[0], a[1].AsString() ?? "", a.Length > 2 ? a[2].AsString() ?? "" : ""))
            .DefineMethod("release", (_, _, a) => { if (a.Length > 0) state.Release(a[0]); return ScriptValue.Undefined; })
            .DefineMethod("allowOrigin", (_, _, a) => ScriptValue.FromBoolean(a.Length > 0 && state.AllowOrigin(a[0].AsString() ?? "")))
            .DefineMethod("createOffer", (_, _, a) => ScriptValue.FromString(a.Length > 0 ? state.CreateOffer(a[0], a.Length > 1 && a[1].AsBoolean(), a.Length > 2 ? (int)a[2].AsNumber() : 0, a.Length > 3 ? (int)a[3].AsNumber() : 0) : ""))
            .DefineMethod("createAnswer", (_, _, a) => ScriptValue.FromString(a.Length > 0 ? state.CreateAnswer(a[0], a.Length > 1 && a[1].AsBoolean(), a.Length > 2 ? (int)a[2].AsNumber() : 0, a.Length > 3 ? (int)a[3].AsNumber() : 0) : ""))
            .DefineMethod("setLocalDescription", (_, _, a) => { if (a.Length > 2) state.SetLocalDescription(a[0], a[1].AsString() ?? "", a[2].AsString() ?? ""); return ScriptValue.Undefined; })
            .DefineMethod("setRemoteDescription", (_, _, a) => { if (a.Length > 2) state.SetRemoteDescription(a[0], a[1].AsString() ?? "", a[2].AsString() ?? ""); return ScriptValue.Undefined; })
            .DefineMethod("addIceCandidate", (_, _, a) => { if (a.Length > 1) state.AddIceCandidate(a[0], a[1].AsString() ?? ""); return ScriptValue.Undefined; });

        engine.SetGlobalProperty("__folio_webrtc_authority", engine.CreateHostObject(authority, state));
        try { engine.EvaluateHostScript(Script, "folio://webrtc-bindings"); }
        catch { state.Dispose(); throw; }
        finally { engine.SetGlobalProperty("__folio_webrtc_authority", ScriptValue.Undefined); }
        return state;
    }

    private const string Script = @"
(function(host) {
  'use strict';
  var peers = new WeakMap();
  var retired = new WeakMap();
  var transportClosers = new WeakMap();
  function closeOwnedChannel(channel) { var nativeClose=apply(weakGet,transportClosers,[channel]);if(nativeClose){nativeClose();return;}try{channel.close();}finally{channel.readyState='closed';} }
  var channelOwners = new WeakMap(), channelClosed = new WeakMap(), channelRetiring = new WeakMap(), channelLists = new WeakMap(), channelPartners = new WeakMap();
  var apply = Reflect.apply, weakGet = WeakMap.prototype.get, weakSet = WeakMap.prototype.set, weakDelete = WeakMap.prototype.delete;
  function denied() { throw new DOMException('The WebRTC connection is closed or disabled by the host.', 'InvalidStateError'); }
  function asyncDead(self) { try { token(self); return null; } catch (error) { return Promise.reject(error); } }
  function token(self) { var value=apply(weakGet,peers,[self]); if(!value || !host.live(value)) denied(); return value; }
  function dispatch(self,type,extra) { var event=extra||{}; event.type=type; if(event.target===undefined)event.target=self; var handler=self['on'+type]; if(typeof handler==='function')handler.call(self,event); (self._listeners[type]||[]).slice().forEach(function(listener){listener.call(self,event);}); }
  function retire(self,fromHost) { if(apply(weakGet,retired,[self]))return; apply(weakSet,retired,[self,true]); var channels=[]; try { channels=apply(weakGet,channelLists,[self]).slice(); } catch (_) {} channels.forEach(function(c){ apply(weakSet,channelRetiring,[c,true]); }); try { self._folioReleased=true; self.connectionState='closed'; } catch (_) {} channels.forEach(function(c){ try { closeOwnedChannel(c); } catch (_) {} apply(weakSet,channelClosed,[c,true]); apply(weakSet,channelRetiring,[c,false]); }); var lease=apply(weakGet,peers,[self]); if(!fromHost) try { host.release(lease); } catch (_) {} apply(weakDelete,peers,[self]); var retiredIndex=__folio_webrtc_peers.indexOf(self); if(retiredIndex>=0)__folio_webrtc_peers.splice(retiredIndex,1); try { dispatch(self,'connectionstatechange',{}); } catch (_) {} }
  function validate(configuration) { configuration=configuration||{}; var iceTransportPolicy=configuration.iceTransportPolicy===undefined?'all':String(configuration.iceTransportPolicy), bundlePolicy=configuration.bundlePolicy===undefined?'balanced':String(configuration.bundlePolicy), rtcpMuxPolicy=configuration.rtcpMuxPolicy===undefined?'require':String(configuration.rtcpMuxPolicy); if(iceTransportPolicy!=='all'&&iceTransportPolicy!=='relay')throw new TypeError('Invalid iceTransportPolicy.'); if(bundlePolicy!=='balanced'&&bundlePolicy!=='max-compat'&&bundlePolicy!=='max-bundle')throw new TypeError('Invalid bundlePolicy.'); if(rtcpMuxPolicy!=='require'&&rtcpMuxPolicy!=='negotiate')throw new TypeError('Invalid rtcpMuxPolicy.'); var servers=configuration.iceServers||[]; if(!Array.isArray(servers))throw new TypeError('iceServers must be an array.'); for(var i=0;i<servers.length;i++){var urls=servers[i]&&servers[i].urls!==undefined?servers[i].urls:[]; urls=Array.isArray(urls)?urls:[urls]; for(var j=0;j<urls.length;j++){var endpoint=String(urls[j]); if(!/^(stun|stuns|turn|turns):/i.test(endpoint)||!host.allowOrigin(endpoint))throw new DOMException('The WebRTC server URL is invalid or not allowed.','SecurityError');}} return configuration; }
  var __folio_webrtc_peers=[];

globalThis.RTCPeerConnection = function(configuration) { var connection=this; configuration=validate(configuration); var lease=host.acquire(); apply(weakSet,peers,[this,lease]); apply(weakSet,channelLists,[this,[]]); __folio_webrtc_peers.push(this); try { host.register(lease,function(){retire(connection,true);});
    host.registerTransport(lease,function(property,value) {
        token(connection);
        if(property==='datachannel') { var incoming=transportChannel(connection,value); connection._dispatch('datachannel',{channel:incoming}); return; }
        if(connection[property]===value)return;
        connection[property]=value;
        var eventName=property==='connectionState'?'connectionstatechange':property==='iceConnectionState'?'iceconnectionstatechange':'icegatheringstatechange';
        connection._dispatch(eventName,{});
    });
    this.configuration = configuration; this.connectionState = 'new'; this.iceConnectionState = 'new'; this.iceGatheringState = 'new'; this.signalingState = 'stable'; this.onconnectionstatechange = null; this.onsignalingstatechange = null; this.onicegatheringstatechange = null; this.oniceconnectionstatechange = null; this.onicecandidate = null; this.ontrack = null; this.onnegotiationneeded = null; this._listeners = {};
    this.addEventListener = function(type, handler) { if (typeof handler !== 'function') return; var list = this._listeners[type] || (this._listeners[type] = []); if (list.indexOf(handler) < 0) list.push(handler); };
    this.removeEventListener = function(type, handler) { var list = this._listeners[type] || []; this._listeners[type] = list.filter(function(item) { return item !== handler; }); };
    this._dispatch = function(type, event) { event = event || {}; if (event.type === undefined) event.type = type; if (event.target === undefined) event.target = this; var handler = this['on' + type]; if (typeof handler === 'function') handler(event); (this._listeners[type] || []).slice().forEach(function(listener) { listener.call(this, event); }, this); };
    this.localDescription = null; this.remoteDescription = null; this._senders = []; this._transceivers = []; this._dataChannels = []; this._peer = null; this.ondatachannel = null; this._folioReleased = false; } catch (error) { host.release(lease); apply(weakDelete,peers,[this]); var failedIndex=__folio_webrtc_peers.indexOf(this); if(failedIndex>=0)__folio_webrtc_peers.splice(failedIndex,1); throw error; } if(!host.live(lease)) { retire(this,true); denied(); }
};
globalThis.RTCRtpSender = function() {};
globalThis.RTCRtpReceiver = function() {};
var __folio_audio_codecs = [{ mimeType: 'audio/opus', clockRate: 48000, channels: 2 }];
var __folio_video_codecs = [{ mimeType: 'video/VP8', clockRate: 90000 }, { mimeType: 'video/H264', clockRate: 90000 }];
RTCRtpSender.getCapabilities = function(kind) { var normalizedKind = String(kind); if (normalizedKind !== 'audio' && normalizedKind !== 'video') return null; return { codecs: (normalizedKind === 'video' ? __folio_video_codecs : __folio_audio_codecs).slice() }; };
RTCRtpReceiver.getCapabilities = function(kind) { var normalizedKind = String(kind); if (normalizedKind !== 'audio' && normalizedKind !== 'video') return null; return { codecs: (normalizedKind === 'video' ? __folio_video_codecs : __folio_audio_codecs).slice() }; };
globalThis.RTCIceCandidate = function(init) { init = init || {}; this.candidate = String(init.candidate || ''); this.sdpMid = init.sdpMid === undefined ? null : init.sdpMid; this.sdpMLineIndex = init.sdpMLineIndex === undefined ? null : init.sdpMLineIndex; this.usernameFragment = init.usernameFragment === undefined ? null : init.usernameFragment; };
globalThis.RTCSessionDescription = function(init) { init = init || {}; this.type = String(init.type || ''); this.sdp = String(init.sdp || ''); };
RTCIceCandidate.prototype.toJSON = function() { return { candidate: this.candidate, sdpMid: this.sdpMid, sdpMLineIndex: this.sdpMLineIndex, usernameFragment: this.usernameFragment }; };
RTCSessionDescription.prototype.toJSON = function() { return { type: this.type, sdp: this.sdp }; };
function __folio_sdp(connection, type) { var kinds=[]; connection._senders.forEach(function(sender){ if(sender.track && kinds.indexOf(sender.track.kind)<0) kinds.push(sender.track.kind); }); connection._transceivers.forEach(function(transceiver){ if(transceiver.kind && kinds.indexOf(transceiver.kind)<0) kinds.push(transceiver.kind); }); var sdp='v=0\\r\\n'+'o=- 0 0 IN IP4 127.0.0.1\\r\\n'+'s=-\\r\\n'+'t=0 0\\r\\n'; if(type==='offer' && connection._dataChannels.length) sdp+='m=application 9 DTLS/SCTP webrtc-datachannel\\r\\n'; kinds.forEach(function(kind,index){ sdp+='m='+kind+' 9 UDP/TLS/RTP/SAVPF 96\\r\\n'+'a=mid:'+index+'\\r\\n'+'a=sendrecv\\r\\n'; }); return sdp; }
RTCPeerConnection.prototype.createOffer = function() { var dead=asyncDead(this); if(dead)return dead; var lease=token(this); var offerSdp=host.createOffer(lease, this._dataChannels.length > 0, this._senders.filter(function(s){return s.track&&s.track.kind==='audio';}).length, this._senders.filter(function(s){return s.track&&s.track.kind==='video';}).length); if(typeof offerSdp!=='string'||!offerSdp) offerSdp=__folio_sdp(this,'offer'); return Promise.resolve(new RTCSessionDescription({ type: 'offer', sdp: offerSdp })); };
RTCPeerConnection.prototype.createAnswer = function() { var dead=asyncDead(this); if(dead)return dead; var lease=token(this); var answerSdp=host.createAnswer(lease, this._dataChannels.length > 0, this._senders.filter(function(s){return s.track&&s.track.kind==='audio';}).length, this._senders.filter(function(s){return s.track&&s.track.kind==='video';}).length); if(typeof answerSdp!=='string'||!answerSdp) answerSdp=__folio_sdp(this,'answer'); return Promise.resolve(new RTCSessionDescription({ type: 'answer', sdp: answerSdp })); };
RTCPeerConnection.prototype.setLocalDescription = function(description) { var dead=asyncDead(this); if(dead)return dead; if (this.connectionState === 'closed') return Promise.reject(new DOMException('The connection is closed.', 'InvalidStateError')); if (!description || (description.type !== 'offer' && description.type !== 'answer' && description.type !== 'rollback')) return Promise.reject(new TypeError('The session description type is invalid.')); var lease=token(this); this.localDescription = description; host.setLocalDescription(lease, description.type, description.sdp || ''); if (this.localDescription.type === 'offer') { this.signalingState = 'have-local-offer'; this._dispatch('signalingstatechange', {}); } this.iceGatheringState = 'complete'; this._dispatch('icegatheringstatechange', {}); this._dispatch('icecandidate', { candidate: null }); return Promise.resolve(); };
RTCPeerConnection.prototype.setRemoteDescription = function(description) { var dead=asyncDead(this); if(dead)return dead; if (this.connectionState === 'closed') return Promise.reject(new DOMException('The connection is closed.', 'InvalidStateError')); if (!description || (description.type !== 'offer' && description.type !== 'answer' && description.type !== 'rollback')) return Promise.reject(new TypeError('The session description type is invalid.')); var lease=token(this); this.remoteDescription = description; host.setRemoteDescription(lease, description.type, description.sdp || ''); if (this.remoteDescription.type === 'answer') { this.signalingState = 'stable'; this._dispatch('signalingstatechange', {}); this.iceConnectionState = 'connected'; this._dispatch('iceconnectionstatechange', {}); this.connectionState = 'connected'; this._dispatch('connectionstatechange', {}); var peer = null; for (var peerIndex = 0; peerIndex < __folio_webrtc_peers.length; peerIndex++) { var candidate = __folio_webrtc_peers[peerIndex]; if (candidate !== this && candidate.connectionState !== 'closed' && ((this.localDescription && this.localDescription.type === 'offer' && candidate.remoteDescription && candidate.remoteDescription.type === 'offer') || (this.remoteDescription && this.remoteDescription.type === 'offer' && candidate.localDescription && candidate.localDescription.type === 'offer'))) { peer = candidate; break; } } if (peer) { this._peer = peer; peer._peer = this; __folio_pair_channels(this, peer); __folio_pair_channels(peer, this); peer._senders.forEach(function(sender) { if (sender.track && this.ontrack) this._dispatch('track', { type: 'track', target: this, track: sender.track, transceiver: null, streams: [new MediaStream([sender.track])] }); }, this); } if (this.ontrack) this._transceivers.forEach(function(transceiver) { if (transceiver.stopped) return; var track = transceiver.receiver.track; this._dispatch('track', { type: 'track', target: this, track: track, transceiver: transceiver, streams: [new MediaStream([track])] }); }, this); } return Promise.resolve(); };
RTCPeerConnection.prototype.addIceCandidate = function(candidate) { var dead=asyncDead(this); if(dead)return dead; if (this.connectionState === 'closed') return Promise.reject(new DOMException('The connection is closed.', 'InvalidStateError')); if (candidate !== null && candidate !== undefined && typeof candidate !== 'string' && (!candidate || candidate.candidate === undefined || typeof candidate.candidate !== 'string')) return Promise.reject(new TypeError('The ICE candidate is invalid.')); var lease=token(this); var candStr = typeof candidate === 'string' ? candidate : (candidate && candidate.candidate ? candidate.candidate : ''); host.addIceCandidate(lease, candStr); this.iceConnectionState = 'connected'; this._dispatch('iceconnectionstatechange', {}); if (this.connectionState === 'new') { this.connectionState = 'connected'; this._dispatch('connectionstatechange', {}); } return Promise.resolve(); };
RTCPeerConnection.prototype.addTrack = function(track) { token(this); if (this.connectionState === 'closed') throw new DOMException('The connection is closed.', 'InvalidStateError'); if (!track || typeof track !== 'object' || (track.kind !== 'audio' && track.kind !== 'video')) throw new TypeError('The track is invalid.'); for (var i = 0; i < this._senders.length; i++) if (this._senders[i].track === track) throw new DOMException('The track is already attached to this connection.', 'InvalidAccessError'); var connection = this; var sender = { track: track, _parameters: { encodings: [] }, replaceTrack: function(next) { var dead=asyncDead(connection); if(dead)return dead; if (connection.connectionState === 'closed') return Promise.reject(new DOMException('The connection is closed.', 'InvalidStateError')); sender.track = next; return Promise.resolve(); }, getParameters: function() { token(connection); return sender._parameters; }, setParameters: function(parameters) { var dead=asyncDead(connection); if(dead)return dead; sender._parameters = parameters || { encodings: [] }; return Promise.resolve(sender._parameters); } }; this._senders.push(sender); this._dispatch('negotiationneeded', { type: 'negotiationneeded', target: this }); if (this._peer && typeof this._peer.ontrack === 'function') this._peer._dispatch('track', { type: 'track', target: this._peer, track: track, transceiver: null, streams: [new MediaStream([track])] }); return sender; };
RTCPeerConnection.prototype.getSenders = function() { token(this); return this._senders.slice(); };
RTCPeerConnection.prototype.removeTrack = function(sender) { token(this); if (this.connectionState === 'closed') throw new DOMException('The connection is closed.', 'InvalidStateError'); var index = this._senders.indexOf(sender); if (index < 0) throw new TypeError('The sender does not belong to this connection.'); if (sender.track !== null) { sender.track = null; this._dispatch('negotiationneeded', { type: 'negotiationneeded', target: this }); } };
RTCPeerConnection.prototype.getReceivers = function() { token(this); return this._transceivers.map(function(transceiver) { return transceiver.receiver; }); };
RTCPeerConnection.prototype.addTransceiver = function(kindOrTrack, init) { token(this); var connection = this; var kind = typeof kindOrTrack === 'string' ? kindOrTrack : (kindOrTrack && kindOrTrack.kind) || ''; if (kind !== 'audio' && kind !== 'video') throw new TypeError('The transceiver kind must be audio or video.'); if (typeof kindOrTrack === 'object' && (!kindOrTrack || kindOrTrack.kind !== kind)) throw new TypeError('The transceiver track is invalid.'); init = init || {}; var direction = init.direction === undefined ? 'sendrecv' : String(init.direction); if (direction !== 'sendrecv' && direction !== 'sendonly' && direction !== 'recvonly' && direction !== 'inactive') throw new TypeError('The transceiver direction is invalid.'); var transceiver = { mid: null, kind: String(kind), sender: { track: typeof kindOrTrack === 'object' ? kindOrTrack : null, _parameters: { encodings: [] } }, receiver: { track: new MediaStreamTrack(kind) }, direction: direction, currentDirection: null, stopped: false, _codecs: [], stop: function() { token(connection); transceiver.stopped = true; transceiver.direction = 'inactive'; transceiver.currentDirection = null; }, setCodecPreferences: function(codecs) { token(connection); if (!Array.isArray(codecs)) throw new TypeError('The codec preferences must be an array.'); for (var i = 0; i < codecs.length; i++) { var mimeType = codecs[i] && codecs[i].mimeType; if (typeof mimeType !== 'string' || mimeType.toLowerCase().indexOf(kind + '/') !== 0) throw new TypeError('The codec preference does not match the transceiver kind.'); } transceiver._codecs = codecs.slice(); }, get codecPreferences() { return transceiver._codecs.slice(); } }; transceiver.sender.replaceTrack = function(next) { var dead=asyncDead(connection); if(dead)return dead; if (connection.connectionState === 'closed') return Promise.reject(new DOMException('The connection is closed.', 'InvalidStateError')); transceiver.sender.track = next; return Promise.resolve(); }; transceiver.sender.getParameters = function() { token(connection); return transceiver.sender._parameters; }; transceiver.sender.setParameters = function(parameters) { var dead=asyncDead(connection); if(dead)return dead; transceiver.sender._parameters = parameters || { encodings: [] }; return Promise.resolve(transceiver.sender._parameters); }; connection._transceivers.push(transceiver); this._dispatch('negotiationneeded', { type: 'negotiationneeded', target: this }); return transceiver; };
RTCPeerConnection.prototype.getTransceivers = function() { token(this); return this._transceivers.slice(); };
RTCPeerConnection.prototype.getConfiguration = function() { token(this); return this.configuration; };
RTCPeerConnection.prototype.setConfiguration = function(configuration) { token(this); if (this.connectionState === 'closed') throw new DOMException('The connection is closed.', 'InvalidStateError'); this.configuration = validate(configuration); };
RTCPeerConnection.prototype.getStats = function() { var dead=asyncDead(this); if(dead)return dead; var report = new Map(); report.set('pc', { type: 'peer-connection', id: 'pc', connectionState: this.connectionState, iceConnectionState: this.iceConnectionState, signalingState: this.signalingState }); report.set('data-channels', { type: 'data-channel', id: 'data-channels', dataChannelsOpened: this._dataChannels.length, dataChannelsClosed: this._dataChannels.filter(function(channel) { return channel.readyState === 'closed'; }).length }); this._senders.forEach(function(sender, index) { report.set('outbound-' + index, { type: 'outbound-rtp', id: 'outbound-' + index, kind: sender.track && sender.track.kind ? sender.track.kind : '' }); }); this._transceivers.forEach(function(transceiver, index) { report.set('inbound-' + index, { type: 'inbound-rtp', id: 'inbound-' + index, kind: transceiver.receiver && transceiver.receiver.track ? transceiver.receiver.track.kind : transceiver.kind }); }); return Promise.resolve(report); };
RTCPeerConnection.prototype.restartIce = function() { token(this); this.iceGatheringState = 'new'; this.iceConnectionState = 'new'; this._dispatch('icegatheringstatechange', {}); this._dispatch('iceconnectionstatechange', {}); };
function __folio_pair_channels(owner, recipient) { owner._dataChannels.forEach(function(channel) { if (channel._remote) return; channel._remote = __folio_make_remote_channel(recipient, channel); if (typeof recipient.ondatachannel === 'function') recipient.ondatachannel({ type: 'datachannel', target: recipient, channel: channel._remote }); }); }
function __folio_make_remote_channel(peer, source) { var remote = { label: source.label, protocol: source.protocol, id: source.id, negotiated: source.negotiated, readyState: 'open', bufferedAmount: 0, bufferedAmountLowThreshold: 0, binaryType: source.binaryType, ordered: source.ordered, maxPacketLifeTime: source.maxPacketLifeTime, maxRetransmits: source.maxRetransmits, onmessage: null, onopen: null, onclose: null, onerror: null, _listeners: {}, _source: source, addEventListener: function(type, handler) { if (typeof handler === 'function') { var list = this._listeners[type] || (this._listeners[type] = []); if (list.indexOf(handler) < 0) list.push(handler); } }, removeEventListener: function(type, handler) { var list = this._listeners[type] || []; this._listeners[type] = list.filter(function(item) { return item !== handler; }); }, _dispatch: function(event) { (remote._listeners[event.type] || []).slice().forEach(function(handler) { handler.call(remote, event); }); }, _deliver: function(data) { var event = { type: 'message', target: remote, data: data }; if (remote.onmessage) remote.onmessage(event); remote._dispatch(event); }, send: function(data) { token(peer); token(apply(weakGet,channelOwners,[source])); if (apply(weakGet,channelClosed,[remote]) || apply(weakGet,channelClosed,[source]) || remote.readyState !== 'open') throw new DOMException('Data channel is closed.', 'InvalidStateError'); source._deliver(data); }, close: function() { if (!apply(weakGet,channelClosed,[remote])) { apply(weakSet,channelClosed,[remote,true]); apply(weakSet,channelClosed,[source,true]); remote.readyState = 'closed'; source.readyState = 'closed'; var event = { type: 'close', target: remote }; if (remote.onclose) remote.onclose(event); remote._dispatch(event); } } }; apply(weakSet,channelPartners,[source,remote]); apply(weakSet,channelPartners,[remote,source]); apply(weakSet,channelOwners,[remote,peer]); apply(weakSet,channelClosed,[remote,false]); apply(weakGet,channelLists,[peer]).push(remote); return remote; }
RTCPeerConnection.prototype.createDataChannel = function(label, options) {
    token(this); options = options || {}; var channelLabel = String(label || ''); var protocol = options.protocol === undefined ? '' : String(options.protocol); var negotiated = options.negotiated === true; var channelId = options.id === undefined ? null : Number(options.id); var lifetime = options.maxPacketLifeTime === undefined ? null : Number(options.maxPacketLifeTime); var retransmits = options.maxRetransmits === undefined ? null : Number(options.maxRetransmits); if (channelLabel.length > 65535 || protocol.length > 65535) throw new TypeError('Data channel label and protocol must be at most 65535 bytes.'); if (options.maxPacketLifeTime !== undefined && options.maxRetransmits !== undefined) throw new TypeError('maxPacketLifeTime and maxRetransmits are mutually exclusive.'); if (lifetime !== null && (!Number.isInteger(lifetime) || lifetime < 0 || lifetime > 65535) || retransmits !== null && (!Number.isInteger(retransmits) || retransmits < 0 || retransmits > 65535)) throw new TypeError('Data channel retransmission limits must be integers from 0 through 65535.'); if (channelId !== null && (!Number.isInteger(channelId) || channelId < 0 || channelId > 65534)) throw new TypeError('Data channel id must be an integer from 0 through 65534.'); if (negotiated && channelId === null) throw new TypeError('Negotiated data channels require an id.'); var listeners = {}; var channel = { _connection: this, label: channelLabel, protocol: protocol, id: channelId, negotiated: negotiated, readyState: 'open', bufferedAmount: 0, bufferedAmountLowThreshold: 0, binaryType: 'blob', ordered: options.ordered !== false, maxPacketLifeTime: lifetime, maxRetransmits: retransmits, onmessage: null, _onopen: null, onclose: null, onerror: null, onbufferedamountlow: null,
        addEventListener: function(type, handler) { if (typeof handler === 'function') { var list = listeners[type] || (listeners[type] = []); if (list.indexOf(handler) < 0) list.push(handler); } },
        removeEventListener: function(type, handler) { var list = listeners[type] || []; listeners[type] = list.filter(function(item) { return item !== handler; }); },
        _dispatch: function(event) { (listeners[event.type] || []).slice().forEach(function(handler) { handler.call(channel, event); }); },
        _deliver: function(data) { var event = { type: 'message', target: channel, data: data }; if (channel.onmessage) channel.onmessage(event); channel._dispatch(event); },
        send: function(data) { var owner=apply(weakGet,channelOwners,[channel]); token(owner); if (apply(weakGet,channelClosed,[channel]) || channel.readyState !== 'open') throw new DOMException('Data channel is closed.', 'InvalidStateError'); var wasAboveThreshold = channel.bufferedAmount > channel.bufferedAmountLowThreshold; channel.bufferedAmount = 0; if (wasAboveThreshold && channel.onbufferedamountlow) channel.onbufferedamountlow({ type: 'bufferedamountlow', target: channel }); if (wasAboveThreshold) { token(owner); channel._dispatch({ type: 'bufferedamountlow', target: channel }); } token(owner); var partner=apply(weakGet,channelPartners,[channel]); if(partner)token(apply(weakGet,channelOwners,[partner])); if(apply(weakGet,channelClosed,[channel]) || partner && apply(weakGet,channelClosed,[partner]))throw new DOMException('Data channel is closed.','InvalidStateError'); if (partner) partner._deliver(data); else channel._deliver(data); },
        close: function() { if (!apply(weakGet,channelClosed,[channel]) || apply(weakGet,channelRetiring,[channel])) { apply(weakSet,channelClosed,[channel,true]); channel.readyState = 'closed'; var event = { type: 'close', target: channel }; if (channel.onclose) channel.onclose(event); channel._dispatch(event); } } };
    Object.defineProperty(channel, 'onopen', { get: function() { return channel._onopen; }, set: function(handler) { channel._onopen = handler; if (typeof handler === 'function') handler({ type: 'open', target: channel }); } });
    apply(weakSet,channelOwners,[channel,this]); apply(weakSet,channelClosed,[channel,false]); apply(weakSet,channelRetiring,[channel,false]); apply(weakGet,channelLists,[this]).push(channel); this._dataChannels.push(channel); if (this._peer) { channel._remote = __folio_make_remote_channel(this._peer, channel); if (typeof this._peer.ondatachannel === 'function') this._peer.ondatachannel({ type: 'datachannel', target: this._peer, channel: channel._remote }); } return channel;
};
RTCPeerConnection.prototype.close = function() { retire(this,false); };
function transportChannel(owner, nativeChannel) {
    var listeners={}, channel={label:nativeChannel.label,protocol:nativeChannel.protocol,ordered:true,binaryType:'arraybuffer',bufferedAmount:0};
    Object.defineProperty(channel,'id',{get:function(){return nativeChannel.id;}});
    Object.defineProperty(channel,'readyState',{get:function(){return nativeChannel.readyState;}});
    channel.addEventListener=function(type,fn){if(typeof fn==='function')(listeners[type]||(listeners[type]=[])).push(fn);};
    channel.removeEventListener=function(type,fn){listeners[type]=(listeners[type]||[]).filter(function(item){return item!==fn;});};
    channel.send=function(data){token(owner);if(nativeChannel.readyState!=='open')throw new DOMException('Channel is not open.','InvalidStateError');nativeChannel.send(data);};
    channel.close=function(){nativeChannel.close();};
    apply(weakSet,transportClosers,[channel,function(){nativeChannel.close();}]);
    nativeChannel.register(function(type,data){token(owner);var event={type:type,target:channel};if(type==='message')event.data=data;var handler=channel['on'+type];if(typeof handler==='function')handler.call(channel,event);(listeners[type]||[]).slice().forEach(function(fn){fn.call(channel,event);});});
    apply(weakSet,channelOwners,[channel,owner]);apply(weakGet,channelLists,[owner]).push(channel);
    return channel;
}
var legacyCreateChannel=RTCPeerConnection.prototype.createDataChannel;
RTCPeerConnection.prototype.createDataChannel=function(label,options) {
    if(!host.transportEnabled())return legacyCreateChannel.call(this,label,options);
    options=options||{};
    if(options.negotiated || options.id!==undefined || options.ordered===false || options.maxRetransmits!==undefined || options.maxPacketLifeTime!==undefined)throw new DOMException('Transport channel option is not implemented.','NotSupportedError');
    var channel=transportChannel(this,host.createChannel(token(this),String(label||''),String(options.protocol||'')));
    this._dataChannels.push(channel);return channel;
};
// Empty descriptions remain solely for the legacy in-page fixtures. Real SDP
// must derive connectivity from transport events, never from accepting an answer.
var legacyRemoteDescription=RTCPeerConnection.prototype.setRemoteDescription;
var legacyIceCandidate=RTCPeerConnection.prototype.addIceCandidate;
RTCPeerConnection.prototype.setRemoteDescription=function(description) {
    if(description && description.sdp==='')return legacyRemoteDescription.call(this,description);
    var dead=asyncDead(this);if(dead)return dead;
    if(!description || (description.type!=='offer' && description.type!=='answer'))return Promise.reject(new TypeError('Invalid remote description.'));
    try {
        host.setRemoteDescription(token(this),description.type,String(description.sdp||''));
        this.remoteDescription=new RTCSessionDescription(description);
        this.signalingState=description.type==='offer'?'have-remote-offer':'stable';
        this._dispatch('signalingstatechange',{});
        return Promise.resolve();
    } catch(error) { return Promise.reject(error); }
};
RTCPeerConnection.prototype.addIceCandidate=function(candidate) {
    if(this.remoteDescription && this.remoteDescription.sdp==='')return legacyIceCandidate.call(this,candidate);
    var dead=asyncDead(this);if(dead)return dead;
    if(candidate!==null && candidate!==undefined && typeof candidate!=='string' && typeof candidate.candidate!=='string')return Promise.reject(new TypeError('Invalid ICE candidate.'));
    try {
        host.addIceCandidate(token(this),typeof candidate==='string'?candidate:(candidate&&candidate.candidate||''));
        return Promise.resolve();
    } catch(error) { return Promise.reject(error); }
};
})(__folio_webrtc_authority);
";
}

internal static class WebRtcBindings
{
    internal static WebRtcBindingState Install(IScriptEngine engine, DomContext context) => WebRtcBindingState.Install(engine, context);
}
