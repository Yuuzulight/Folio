using Folio;
using Folio.Dom;

namespace Folio.Scripting.Dom;

/// <summary>
/// Installs DOM constructors, prototype chains, and the global document onto an <see cref="IScriptEngine"/> (study 02, study 17, #451).
/// </summary>
public static class DomBindings
{
    public static ScriptWrapperCache Install(IScriptEngine engine, Document document, ScriptWrapperCache? wrapperCache = null, HtmlEventLoop? eventLoop = null) =>
        Install(engine, document.Node, wrapperCache, eventLoop, element =>
        {
            var bounds = document.GetElementBounds(element);
            return bounds is { } rect ? (rect.X, rect.Y, rect.Width, rect.Height) : null;
        }, document.Capabilities, document.MaxGpuBytes, document.MaxMediaBytes, document.MaxWorkerMessageBytes, document.MaxWebAssemblyBytes);
    internal static ScriptWrapperCache Install(IScriptEngine engine, DocumentNode document, ScriptWrapperCache? wrapperCache = null,
        HtmlEventLoop? eventLoop = null, Func<ElementNode, (float X, float Y, float Width, float Height)?>? layout = null,
        FeatureCapabilities? capabilities = null, int maxGpuBytes = 64 * 1024 * 1024, int maxMediaBytes = 128 * 1024 * 1024, int maxWorkerMessageBytes = 1 * 1024 * 1024, int maxWebAssemblyBytes = 64 * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(document);

        var context = new DomContext(engine, document, wrapperCache, layout, capabilities, maxGpuBytes, maxMediaBytes, maxWorkerMessageBytes, maxWebAssemblyBytes);

        // 1. Install DOMException
        InstallDomException(engine);

        // 2. Install Prototype Hierarchy & Constructors
        InstallConstructors(engine);

        // 2b. Events: EventTarget joins the Node prototype chain, so it follows the constructors
        var (handlers, dispatchHostEvent) = DomEvents.Install(engine, eventLoop);

        // 3. Bind document to global scope
        var docWrapper = context.Wrap(document);
        engine.SetGlobalProperty("document", docWrapper);
        var styleHost = new HostObjectTemplate("StyleHost")
            .DefineMethod("getComputedStyle", (target, _, args) =>
            {
                if (args.Length == 0 || !context.Engine.TryGetHostTarget(args[0], out var node) || node is not ElementNode element)
                    throw WebIdlException.TypeError("getComputedStyle requires an Element.");
                return context.WrapComputedStyle(element);
            });
        engine.SetGlobalProperty("__folio_style_host", engine.CreateHostObject(styleHost, context));
        var mutationHost = new HostObjectTemplate("MutationObserverHost")
            .DefineMethod("create", (_, _, args) =>
            {
                if (args.Length == 0) throw WebIdlException.TypeError("MutationObserver callback must be callable.");
                return engine.CreateHostObject(context.Templates.MutationObserverTemplate, context.CreateMutationObserver(args[0]));
            });
        engine.SetGlobalProperty("__folio_mutation_host", engine.CreateHostObject(mutationHost, context));
        var canvasHost = new HostObjectTemplate("CanvasHost")
            .DefineMethod("createPath", (_, _, _) => engine.CreateHostObject(context.Templates.Path2DTemplate, new CanvasPathState()));
        engine.SetGlobalProperty("__folio_canvas_host", engine.CreateHostObject(canvasHost, context));
        var workerTemplate = new HostObjectTemplate("Worker")
            .DefineProperty("onmessage", (target, _) => ((WorkerState)target).OnMessage, (target, _, value) => ((WorkerState)target).OnMessage = value)
            .DefineProperty("onerror", (target, _) => ((WorkerState)target).OnError, (target, _, value) => ((WorkerState)target).OnError = value)
            .DefineProperty("scriptURL", (target, _) => ScriptValue.FromString(((WorkerState)target).ScriptUrl))
            .DefineMethod("addEventListener", (target, _, args) => { if (args.Length > 1) ((WorkerState)target).AddEventListener(WebIdlConversions.ToDOMString(args[0]), args[1]); return ScriptValue.Undefined; })
            .DefineMethod("removeEventListener", (target, _, args) => { if (args.Length > 1) ((WorkerState)target).RemoveEventListener(WebIdlConversions.ToDOMString(args[0]), args[1]); return ScriptValue.Undefined; })
            .DefineMethod("postMessage", (target, _, args) =>
            {
                if (((WorkerState)target).Terminated)
                    throw WebIdlException.DomException(DomExceptionNames.InvalidStateError, "The worker has been terminated.");
                if (args.Length > 0 && !((WorkerState)target).PostMessage(args[0]))
                    throw WebIdlException.DomException("DataCloneError", "The worker message exceeds the host limit.");
                return ScriptValue.Undefined;
            })
            .DefineMethod("terminate", (target, _, _) => { ((WorkerState)target).Terminate(); return ScriptValue.Undefined; });
        var workerHost = new HostObjectTemplate("WorkerHost")
            .DefineMethod("create", (_, _, args) =>
            {
                if (!context.Capabilities.Workers)
                    throw WebIdlException.DomException(DomExceptionNames.NotSupportedError, "Workers are disabled by the host.");
                var url = args.Length > 0 ? WebIdlConversions.ToDOMString(args[0]) : "";
                var state = new WorkerState(engine, context.Templates.MessageEventTemplate, url, true, context.MaxWorkerMessageBytes);
                var worker = engine.CreateHostObject(workerTemplate, state);
                state.Source = worker;
                return worker;
            });
        engine.SetGlobalProperty("__folio_worker_host", engine.CreateHostObject(workerHost, context));
        engine.SetGlobalProperty("__folio_wasm_enabled", ScriptValue.FromBoolean(context.Capabilities.WebAssembly));
        engine.SetGlobalProperty("__folio_wasm_max_pages", ScriptValue.FromNumber(Math.Max(0, context.MaxWebAssemblyBytes / 65536)));
        engine.SetGlobalProperty("__folio_service_workers_enabled", ScriptValue.FromBoolean(context.Capabilities.ServiceWorkers));
        engine.SetGlobalProperty("__folio_webrtc_enabled", ScriptValue.FromBoolean(context.Capabilities.WebRtc));
        engine.Evaluate("globalThis.getComputedStyle = (function(host) { return function(element, pseudo) { return host.getComputedStyle(element, pseudo); }; })(__folio_style_host); delete globalThis.__folio_style_host;");
        engine.Evaluate(@"
            if (typeof window === 'undefined') {
                globalThis.window = globalThis;
            }
            globalThis.Worker = function(scriptURL, options) { return __folio_worker_host.create(scriptURL, options); };
            globalThis.navigator = globalThis.navigator || {};
            var __folio_service_worker_registrations = [];
            globalThis.navigator.serviceWorker = {
                controller: null,
                oncontrollerchange: null,
                _listeners: [],
                addEventListener: function(type, handler) { if (type === 'controllerchange' && typeof handler === 'function' && this._listeners.indexOf(handler) < 0) this._listeners.push(handler); },
                removeEventListener: function(type, handler) { if (type === 'controllerchange') { var i = this._listeners.indexOf(handler); if (i >= 0) this._listeners.splice(i, 1); } },
                dispatchEvent: function(event) { if (event.type === 'controllerchange') { if (this.oncontrollerchange) this.oncontrollerchange(event); this._listeners.slice().forEach(function(handler) { handler(event); }); } return true; },
                ready: Promise.resolve({ scope: '/', active: null }),
                register: function(scriptURL, options) {
                    if (!__folio_service_workers_enabled) return Promise.reject(new DOMException('Service workers are disabled by the host.', 'SecurityError'));
                    var url = String(scriptURL); var scope = (options && options.scope) || '/';
                    var existing = __folio_service_worker_registrations.find(function(r) { return r.scriptURL === url && r.scope === scope; });
                    if (existing) return Promise.resolve(existing);
                    var workerListeners = [];
                    var worker = { scriptURL: url, state: 'activated', onstatechange: null, onmessage: null,
                        addEventListener: function(type, handler) { if (type === 'message' && typeof handler === 'function' && workerListeners.indexOf(handler) < 0) workerListeners.push(handler); },
                        removeEventListener: function(type, handler) { if (type === 'message') { var index = workerListeners.indexOf(handler); if (index >= 0) workerListeners.splice(index, 1); } },
                        postMessage: function(data) { var event = { type: 'message', data: data, target: worker, source: null, origin: '' }; if (worker.onmessage) worker.onmessage(event); workerListeners.slice().forEach(function(handler) { handler(event); }); } };
                    var registrationListeners = [];
                    var registration = { scope: scope, scriptURL: url, active: worker, installing: null, waiting: null, onupdatefound: null,
                        addEventListener: function(type, handler) { if (type === 'updatefound' && typeof handler === 'function' && registrationListeners.indexOf(handler) < 0) registrationListeners.push(handler); },
                        removeEventListener: function(type, handler) { if (type === 'updatefound') { var index = registrationListeners.indexOf(handler); if (index >= 0) registrationListeners.splice(index, 1); } },
                        update: function() { var event = { type: 'updatefound', target: registration }; if (registration.onupdatefound) registration.onupdatefound(event); registrationListeners.slice().forEach(function(handler) { handler(event); }); return Promise.resolve(registration); },
                        unregister: function() { var i = __folio_service_worker_registrations.indexOf(registration); if (i >= 0) __folio_service_worker_registrations.splice(i, 1); if (navigator.serviceWorker.controller === worker) { navigator.serviceWorker.controller = null; navigator.serviceWorker.dispatchEvent({ type: 'controllerchange', target: navigator.serviceWorker }); } return Promise.resolve(true); } };
                    __folio_service_worker_registrations.push(registration); navigator.serviceWorker.controller = worker; navigator.serviceWorker.dispatchEvent({ type: 'controllerchange', target: navigator.serviceWorker }); navigator.serviceWorker.ready = Promise.resolve(registration); return Promise.resolve(registration);
                },
                getRegistration: function(scope) { var value = String(scope || '/'); return Promise.resolve(__folio_service_worker_registrations.find(function(r) { return r.scope === value; })); },
                getRegistrations: function() { return Promise.resolve(__folio_service_worker_registrations.slice()); }
            };
            globalThis.navigator.mediaDevices = {
                getUserMedia: function(constraints) {
                    if (!__folio_webrtc_enabled) return Promise.reject(new DOMException('Media devices are disabled by the host.', 'NotAllowedError'));
                    constraints = constraints || {}; var tracks = [];
                    if (constraints.audio) tracks.push(new MediaStreamTrack('audio'));
                    if (constraints.video) tracks.push(new MediaStreamTrack('video'));
                    return Promise.resolve(new MediaStream(tracks));
                }
            };
            globalThis.MediaStreamTrack = function(kind) { this.id = 'track-' + Math.random().toString(16).slice(2); this.kind = String(kind || ''); this.enabled = true; this.contentHint = ''; this._muted = false; this._constraints = {}; this.readyState = 'live'; this.onended = null; this.onmute = null; this.onunmute = null; Object.defineProperty(this, 'muted', { get: function() { return this._muted; }, set: function(next) { next = !!next; if (next !== this._muted) { this._muted = next; var handler = next ? this.onmute : this.onunmute; if (handler) handler({ type: next ? 'mute' : 'unmute', target: this, track: this }); } } }); this.stop = function() { if (this.readyState !== 'ended') { this.readyState = 'ended'; if (this.onended) this.onended({ type: 'ended', target: this, track: this }); } }; this.clone = function() { var copy = new MediaStreamTrack(this.kind); copy.enabled = this.enabled; copy.contentHint = this.contentHint; copy._muted = this._muted; copy._constraints = Object.assign({}, this._constraints); return copy; }; this.applyConstraints = function(constraints) { if (this.readyState === 'ended') return Promise.reject(new DOMException('Track has ended.', 'InvalidStateError')); this._constraints = constraints || {}; return Promise.resolve(); }; this.getConstraints = function() { return this._constraints; }; this.getSettings = function() { return { deviceId: this.id, kind: this.kind }; }; this.getCapabilities = function() { return { deviceId: [], kind: this.kind }; }; };
            globalThis.MediaStream = function(tracks) { this.id = 'stream-' + Math.random().toString(16).slice(2); this._tracks = tracks && typeof tracks.getTracks === 'function' ? tracks.getTracks() : (tracks ? tracks.slice() : []); this.onaddtrack = null; this.onremovetrack = null; };
            Object.defineProperty(MediaStream.prototype, 'active', { get: function() { return this._tracks.some(function(track) { return track.readyState === 'live'; }); } });
            MediaStream.prototype.getTracks = function() { return this._tracks.slice(); };
            MediaStream.prototype.getAudioTracks = function() { return this._tracks.filter(function(track) { return track.kind === 'audio'; }); };
            MediaStream.prototype.getVideoTracks = function() { return this._tracks.filter(function(track) { return track.kind === 'video'; }); };
            MediaStream.prototype.addTrack = function(track) { if (this._tracks.indexOf(track) < 0) { this._tracks.push(track); if (this.onaddtrack) this.onaddtrack({ type: 'addtrack', target: this, track: track }); } };
            MediaStream.prototype.removeTrack = function(track) { var i = this._tracks.indexOf(track); if (i >= 0) { this._tracks.splice(i, 1); if (this.onremovetrack) this.onremovetrack({ type: 'removetrack', target: this, track: track }); } };
            globalThis.RTCPeerConnection = function(configuration) {
                if (!__folio_webrtc_enabled) throw new DOMException('WebRTC is disabled by the host.', 'NotSupportedError');
                this.configuration = configuration || {}; this.connectionState = 'new'; this.iceConnectionState = 'new'; this.iceGatheringState = 'new'; this.signalingState = 'stable'; this.onconnectionstatechange = null; this.onsignalingstatechange = null; this.onicegatheringstatechange = null; this.oniceconnectionstatechange = null; this.onicecandidate = null; this.ontrack = null; this.onnegotiationneeded = null;
                this.localDescription = null; this.remoteDescription = null; this._senders = []; this._transceivers = []; this._dataChannels = [];
            };
            globalThis.RTCRtpSender = function() {};
            globalThis.RTCRtpReceiver = function() {};
            var __folio_audio_codecs = [{ mimeType: 'audio/opus', clockRate: 48000, channels: 2 }];
            var __folio_video_codecs = [{ mimeType: 'video/VP8', clockRate: 90000 }, { mimeType: 'video/H264', clockRate: 90000 }];
            RTCRtpSender.getCapabilities = function(kind) { return { codecs: (String(kind) === 'video' ? __folio_video_codecs : __folio_audio_codecs).slice() }; };
            RTCRtpReceiver.getCapabilities = function(kind) { return { codecs: (String(kind) === 'video' ? __folio_video_codecs : __folio_audio_codecs).slice() }; };
            globalThis.RTCIceCandidate = function(init) { init = init || {}; this.candidate = String(init.candidate || ''); this.sdpMid = init.sdpMid === undefined ? null : init.sdpMid; this.sdpMLineIndex = init.sdpMLineIndex === undefined ? null : init.sdpMLineIndex; this.usernameFragment = init.usernameFragment === undefined ? null : init.usernameFragment; };
            globalThis.RTCSessionDescription = function(init) { init = init || {}; this.type = String(init.type || ''); this.sdp = String(init.sdp || ''); };
            RTCIceCandidate.prototype.toJSON = function() { return { candidate: this.candidate, sdpMid: this.sdpMid, sdpMLineIndex: this.sdpMLineIndex, usernameFragment: this.usernameFragment }; };
            RTCSessionDescription.prototype.toJSON = function() { return { type: this.type, sdp: this.sdp }; };
            RTCPeerConnection.prototype.createOffer = function() { return Promise.resolve(new RTCSessionDescription({ type: 'offer', sdp: '' })); };
            RTCPeerConnection.prototype.createAnswer = function() { return Promise.resolve(new RTCSessionDescription({ type: 'answer', sdp: '' })); };
            RTCPeerConnection.prototype.setLocalDescription = function(description) { this.localDescription = description || null; if (this.localDescription && this.localDescription.type === 'offer') { this.signalingState = 'have-local-offer'; if (this.onsignalingstatechange) this.onsignalingstatechange({}); } this.iceGatheringState = 'complete'; if (this.onicegatheringstatechange) this.onicegatheringstatechange({}); if (this.onicecandidate) this.onicecandidate({ candidate: null }); return Promise.resolve(); };
            RTCPeerConnection.prototype.setRemoteDescription = function(description) { this.remoteDescription = description || null; if (this.remoteDescription && this.remoteDescription.type === 'answer') { this.signalingState = 'stable'; if (this.onsignalingstatechange) this.onsignalingstatechange({}); if (this.ontrack) this._transceivers.forEach(function(transceiver) { this.ontrack({ type: 'track', target: this, track: transceiver.receiver.track, transceiver: transceiver, streams: [] }); }, this); } return Promise.resolve(); };
            RTCPeerConnection.prototype.addIceCandidate = function(candidate) { if (this.connectionState === 'closed') return Promise.reject(new DOMException('The connection is closed.', 'InvalidStateError')); this.iceConnectionState = 'connected'; if (this.oniceconnectionstatechange) this.oniceconnectionstatechange({}); return Promise.resolve(); };
            RTCPeerConnection.prototype.addTrack = function(track) { var connection = this; var sender = { track: track, _parameters: { encodings: [] }, replaceTrack: function(next) { if (connection.connectionState === 'closed') return Promise.reject(new DOMException('The connection is closed.', 'InvalidStateError')); sender.track = next; return Promise.resolve(); }, getParameters: function() { return sender._parameters; }, setParameters: function(parameters) { sender._parameters = parameters || { encodings: [] }; return Promise.resolve(sender._parameters); } }; this._senders.push(sender); if (this.onnegotiationneeded) this.onnegotiationneeded({ type: 'negotiationneeded', target: this }); return sender; };
            RTCPeerConnection.prototype.getSenders = function() { return this._senders.slice(); };
            RTCPeerConnection.prototype.addTransceiver = function(kindOrTrack, init) { var connection = this; var kind = typeof kindOrTrack === 'string' ? kindOrTrack : (kindOrTrack && kindOrTrack.kind) || ''; init = init || {}; var transceiver = { mid: null, kind: String(kind), sender: { track: typeof kindOrTrack === 'object' ? kindOrTrack : null, _parameters: { encodings: [] } }, receiver: { track: new MediaStreamTrack(kind) }, direction: init.direction || 'sendrecv', currentDirection: null, stopped: false, _codecs: [], stop: function() { transceiver.stopped = true; transceiver.direction = 'inactive'; transceiver.currentDirection = null; }, setCodecPreferences: function(codecs) { transceiver._codecs = codecs ? codecs.slice() : []; }, get codecPreferences() { return transceiver._codecs.slice(); } }; transceiver.sender.replaceTrack = function(next) { if (connection.connectionState === 'closed') return Promise.reject(new DOMException('The connection is closed.', 'InvalidStateError')); transceiver.sender.track = next; return Promise.resolve(); }; transceiver.sender.getParameters = function() { return transceiver.sender._parameters; }; transceiver.sender.setParameters = function(parameters) { transceiver.sender._parameters = parameters || { encodings: [] }; return Promise.resolve(transceiver.sender._parameters); }; connection._transceivers.push(transceiver); if (this.onnegotiationneeded) this.onnegotiationneeded({ type: 'negotiationneeded', target: this }); return transceiver; };
            RTCPeerConnection.prototype.getTransceivers = function() { return this._transceivers.slice(); };
            RTCPeerConnection.prototype.getConfiguration = function() { return this.configuration; };
            RTCPeerConnection.prototype.setConfiguration = function(configuration) { if (this.connectionState === 'closed') throw new DOMException('The connection is closed.', 'InvalidStateError'); this.configuration = configuration || {}; };
            RTCPeerConnection.prototype.getStats = function() { return Promise.resolve(new Map()); };
            RTCPeerConnection.prototype.restartIce = function() { this.iceGatheringState = 'new'; this.iceConnectionState = 'new'; if (this.onicegatheringstatechange) this.onicegatheringstatechange({}); if (this.oniceconnectionstatechange) this.oniceconnectionstatechange({}); };
            RTCPeerConnection.prototype.createDataChannel = function(label, options) {
                options = options || {}; var listeners = {}; var channel = { label: String(label || ''), readyState: 'open', bufferedAmount: 0, binaryType: 'blob', ordered: options.ordered !== false, maxPacketLifeTime: options.maxPacketLifeTime === undefined ? null : Number(options.maxPacketLifeTime), maxRetransmits: options.maxRetransmits === undefined ? null : Number(options.maxRetransmits), onmessage: null, _onopen: null, onclose: null, onerror: null,
                    addEventListener: function(type, handler) { if (typeof handler === 'function') (listeners[type] || (listeners[type] = [])).push(handler); },
                    removeEventListener: function(type, handler) { var list = listeners[type] || []; listeners[type] = list.filter(function(item) { return item !== handler; }); },
                    _dispatch: function(event) { (listeners[event.type] || []).slice().forEach(function(handler) { handler.call(channel, event); }); },
                    send: function(data) { if (channel.readyState !== 'open') throw new DOMException('Data channel is closed.', 'InvalidStateError'); channel.bufferedAmount = 0; var event = { type: 'message', target: channel, data: data }; if (channel.onmessage) channel.onmessage(event); channel._dispatch(event); },
                    close: function() { if (channel.readyState !== 'closed') { channel.readyState = 'closed'; var event = { type: 'close', target: channel }; if (channel.onclose) channel.onclose(event); channel._dispatch(event); } } };
                Object.defineProperty(channel, 'onopen', { get: function() { return channel._onopen; }, set: function(handler) { channel._onopen = handler; if (typeof handler === 'function') handler({ type: 'open', target: channel }); } });
                this._dataChannels.push(channel); return channel;
            };
            RTCPeerConnection.prototype.close = function() { this.connectionState = 'closed'; if (this.onconnectionstatechange) this.onconnectionstatechange({}); };
            function __folio_wasm_read_u32(bytes, cursor) { var value = 0; var shift = 0; while (cursor.i < bytes.length) { var part = bytes[cursor.i++]; value |= (part & 127) << shift; if ((part & 128) === 0) break; shift += 7; } return value >>> 0; }
            function __folio_wasm_read_name(bytes, cursor) { var length = __folio_wasm_read_u32(bytes, cursor); var text = ''; for (var i = 0; i < length && cursor.i < bytes.length; i++) text += String.fromCharCode(bytes[cursor.i++]); return text; }
            function __folio_wasm_skip_limits(bytes, cursor) { var flags = bytes[cursor.i++]; __folio_wasm_read_u32(bytes, cursor); if (flags & 1) __folio_wasm_read_u32(bytes, cursor); }
            function __folio_wasm_read_s32(bytes, cursor) { var value = 0; var shift = 0; var part = 0; do { part = bytes[cursor.i++]; value |= (part & 127) << shift; shift += 7; } while (part & 128); if (shift < 32 && (part & 64)) value |= (~0 << shift); return value | 0; }
            function __folio_wasm_skip_control(bytes, cursor, stopAtElse) { var depth = 1; while (cursor.i < bytes.length) { var op = bytes[cursor.i++]; if (op === 0x02 || op === 0x03 || op === 0x04) { cursor.i++; depth++; } else if (op === 0x0b) { depth--; if (depth === 0) { cursor.i--; return; } } else if (op === 0x05 && depth === 1 && stopAtElse) return; } }
            function __folio_wasm_functions(bytes) {
                var result = []; if (!bytes || bytes.length < 8) return result; var cursor = { i: 8 };
                while (cursor.i < bytes.length) { var section = bytes[cursor.i++]; var size = __folio_wasm_read_u32(bytes, cursor); var end = Math.min(bytes.length, cursor.i + size); if (section !== 10) { cursor.i = end; continue; } var count = __folio_wasm_read_u32(bytes, cursor); for (var i = 0; i < count && cursor.i < end; i++) { var bodySize = __folio_wasm_read_u32(bytes, cursor); result.push(bytes.slice(cursor.i, Math.min(end, cursor.i + bodySize))); cursor.i += bodySize; } cursor.i = end; }
                return result;
            }
            function __folio_wasm_globals(bytes) {
                var result = []; if (!bytes || bytes.length < 8) return result; var cursor = { i: 8 };
                while (cursor.i < bytes.length) { var section = bytes[cursor.i++]; var size = __folio_wasm_read_u32(bytes, cursor); var end = Math.min(bytes.length, cursor.i + size); if (section !== 6) { cursor.i = end; continue; } var count = __folio_wasm_read_u32(bytes, cursor); for (var i = 0; i < count && cursor.i < end; i++) { cursor.i += 2; var opcode = bytes[cursor.i++]; var value = opcode === 0x41 ? __folio_wasm_read_s32(bytes, cursor) : 0; while (cursor.i < end && bytes[cursor.i++] !== 0x0b) {} result.push(value | 0); } cursor.i = end; }
                return result;
            }
            function __folio_wasm_memories(bytes) {
                var result = []; if (!bytes || bytes.length < 8) return result; var cursor = { i: 8 };
                while (cursor.i < bytes.length) { var section = bytes[cursor.i++]; var size = __folio_wasm_read_u32(bytes, cursor); var end = Math.min(bytes.length, cursor.i + size); if (section !== 5) { cursor.i = end; continue; } var count = __folio_wasm_read_u32(bytes, cursor); for (var i = 0; i < count && cursor.i < end; i++) { var flags = bytes[cursor.i++]; var initial = __folio_wasm_read_u32(bytes, cursor); var maximum = flags & 1 ? __folio_wasm_read_u32(bytes, cursor) : __folio_wasm_max_pages; result.push({ initial: initial, maximum: maximum }); } cursor.i = end; }
                return result;
            }
            function __folio_wasm_tables(bytes) {
                var result = []; if (!bytes || bytes.length < 8) return result; var cursor = { i: 8 };
                while (cursor.i < bytes.length) { var section = bytes[cursor.i++]; var size = __folio_wasm_read_u32(bytes, cursor); var end = Math.min(bytes.length, cursor.i + size); if (section !== 4) { cursor.i = end; continue; } var count = __folio_wasm_read_u32(bytes, cursor); for (var i = 0; i < count && cursor.i < end; i++) { cursor.i++; var flags = bytes[cursor.i++]; var initial = __folio_wasm_read_u32(bytes, cursor); var maximum = flags & 1 ? __folio_wasm_read_u32(bytes, cursor) : 100000; result.push({ initial: initial, maximum: maximum }); } cursor.i = end; }
                return result;
            }
            function __folio_wasm_execute(body, argumentsObject, instanceExports, importedFunctions) {
                var cursor = { i: 0 }; var localGroups = __folio_wasm_read_u32(body, cursor); var locals = []; for (var argumentIndex = 0; argumentIndex < argumentsObject.length; argumentIndex++) locals.push(Number(argumentsObject[argumentIndex]) | 0); for (var group = 0; group < localGroups; group++) { var localCount = __folio_wasm_read_u32(body, cursor); cursor.i++; for (var localIndex = 0; localIndex < localCount; localIndex++) locals.push(0); }
                var stack = []; while (cursor.i < body.length) { var opcode = body[cursor.i++]; if (opcode === 0x01) { } else if (opcode === 0x1a) { stack.pop(); } else if (opcode === 0x41) stack.push(__folio_wasm_read_s32(body, cursor)); else if (opcode === 0x20) { var getIndex = __folio_wasm_read_u32(body, cursor); stack.push(locals[getIndex] || 0); } else if (opcode === 0x21 || opcode === 0x22) { var setIndex = __folio_wasm_read_u32(body, cursor); var setValue = stack.pop() || 0; locals[setIndex] = setValue; if (opcode === 0x22) stack.push(setValue); } else if (opcode === 0x45) stack.push((stack.pop() || 0) === 0 ? 1 : 0); else if (opcode === 0x46 || opcode === 0x47) { var equalityRight = stack.pop() || 0; var equalityLeft = stack.pop() || 0; stack.push((opcode === 0x46 ? equalityLeft === equalityRight : equalityLeft !== equalityRight) ? 1 : 0); } else if (opcode >= 0x48 && opcode <= 0x4f) { var compareRight = stack.pop() || 0; var compareLeft = stack.pop() || 0; var unsigned = (opcode - 0x48) % 2 === 1; var leftValue = unsigned ? compareLeft >>> 0 : compareLeft; var rightValue = unsigned ? compareRight >>> 0 : compareRight; var compare = opcode === 0x48 || opcode === 0x49 ? leftValue < rightValue : opcode === 0x4a || opcode === 0x4b ? leftValue > rightValue : opcode === 0x4c || opcode === 0x4d ? leftValue <= rightValue : leftValue >= rightValue; stack.push(compare ? 1 : 0); } else if (opcode === 0x67 || opcode === 0x68 || opcode === 0x69) { var bits = (stack.pop() || 0) | 0; stack.push((opcode === 0x67 ? Math.clz32(bits) : opcode === 0x68 ? (bits === 0 ? 32 : Math.clz32((bits & -bits) - 1)) : bits.toString(2).split('1').length - 1) | 0); } else if (opcode === 0x77 || opcode === 0x78) { var rotateRight = (stack.pop() || 0) & 31; var rotateValue = (stack.pop() || 0) | 0; stack.push((opcode === 0x77 ? (rotateValue << rotateRight) | (rotateValue >>> (32 - rotateRight)) : (rotateValue >>> rotateRight) | (rotateValue << (32 - rotateRight))) | 0); } else if (opcode === 0xc0 || opcode === 0xc1) { var extension = (stack.pop() || 0) | 0; stack.push((opcode === 0xc0 ? (extension << 24) >> 24 : (extension << 16) >> 16) | 0); } else if (opcode === 0x04) { cursor.i++; var condition = stack.pop() || 0; if (!condition) __folio_wasm_skip_control(body, cursor, true); } else if (opcode === 0x05) { __folio_wasm_skip_control(body, cursor, false); } else if (opcode === 0x0f) { return stack.length ? stack[stack.length - 1] : undefined; } else if (opcode === 0x10) { var importedIndex = __folio_wasm_read_u32(body, cursor); if (!importedFunctions || typeof importedFunctions[importedIndex] !== 'function') throw new WebAssembly.CompileError('WebAssembly function import is unavailable.'); stack.push(importedFunctions[importedIndex]()); } else if (opcode === 0x3f || opcode === 0x40) { __folio_wasm_read_u32(body, cursor); var growMemory = instanceExports && instanceExports.mem; if (!growMemory) throw new WebAssembly.RuntimeError('WebAssembly memory is unavailable.'); if (opcode === 0x3f) stack.push((growMemory.buffer.byteLength / 65536) | 0); else { var growDelta = stack.pop() || 0; var previousPages = (growMemory.buffer.byteLength / 65536) | 0; growMemory.grow(growDelta); stack.push(previousPages); } } else if (opcode === 0x2c || opcode === 0x2d || opcode === 0x2e || opcode === 0x2f || opcode === 0x3a || opcode === 0x3b) { __folio_wasm_read_u32(body, cursor); var narrowOffset = __folio_wasm_read_u32(body, cursor); var narrowMemory = instanceExports && instanceExports.mem; if (!narrowMemory) throw new WebAssembly.RuntimeError('WebAssembly memory is unavailable.'); var narrowBytes = new Uint8Array(narrowMemory.buffer); var width = opcode === 0x2c || opcode === 0x2d || opcode === 0x3a ? 1 : 2; if (opcode === 0x3a || opcode === 0x3b) { var narrowValue = stack.pop() || 0; var narrowAddress = (stack.pop() || 0) + narrowOffset; if (narrowAddress < 0 || narrowAddress + width > narrowBytes.length) throw new WebAssembly.RuntimeError('WebAssembly memory access out of bounds.'); narrowBytes[narrowAddress] = narrowValue & 255; if (width === 2) narrowBytes[narrowAddress + 1] = (narrowValue >>> 8) & 255; } else { var narrowAddress = (stack.pop() || 0) + narrowOffset; if (narrowAddress < 0 || narrowAddress + width > narrowBytes.length) throw new WebAssembly.RuntimeError('WebAssembly memory access out of bounds.'); var narrowValue = width === 1 ? narrowBytes[narrowAddress] : narrowBytes[narrowAddress] | (narrowBytes[narrowAddress + 1] << 8); if (opcode === 0x2c && (narrowValue & 0x80)) narrowValue |= 0xFFFFFF00; if (opcode === 0x2e && (narrowValue & 0x8000)) narrowValue |= 0xFFFF0000; stack.push(narrowValue | 0); } } else if (opcode === 0x28 || opcode === 0x36) { var alignment = __folio_wasm_read_u32(body, cursor); var offset = __folio_wasm_read_u32(body, cursor); var memory = instanceExports && instanceExports.mem; if (!memory) throw new WebAssembly.RuntimeError('WebAssembly memory is unavailable.'); var bytes = new Uint8Array(memory.buffer); if (opcode === 0x28) { var address = (stack.pop() || 0) + offset; if (address < 0 || address + 4 > bytes.length) throw new WebAssembly.RuntimeError('WebAssembly memory access out of bounds.'); stack.push((bytes[address] | (bytes[address + 1] << 8) | (bytes[address + 2] << 16) | (bytes[address + 3] << 24)) | 0); } else { var value = stack.pop() || 0; var address = (stack.pop() || 0) + offset; if (address < 0 || address + 4 > bytes.length) throw new WebAssembly.RuntimeError('WebAssembly memory access out of bounds.'); bytes[address] = value & 255; bytes[address + 1] = (value >>> 8) & 255; bytes[address + 2] = (value >>> 16) & 255; bytes[address + 3] = (value >>> 24) & 255; } } else if (opcode >= 0x6a && opcode <= 0x76) { var right = stack.pop() || 0; var left = stack.pop() || 0; if ((opcode === 0x6d || opcode === 0x6e || opcode === 0x6f || opcode === 0x70) && right === 0) throw new WebAssembly.RuntimeError('WebAssembly integer division by zero.'); var arithmetic = opcode === 0x6a ? left + right : opcode === 0x6b ? left - right : opcode === 0x6c ? left * right : opcode === 0x6d ? Math.trunc(left / right) : opcode === 0x6e ? Math.floor((left >>> 0) / (right >>> 0)) : opcode === 0x6f ? left % right : opcode === 0x70 ? (left >>> 0) % (right >>> 0) : opcode === 0x71 ? left & right : opcode === 0x72 ? left | right : opcode === 0x73 ? left ^ right : opcode === 0x74 ? left << (right & 31) : opcode === 0x75 ? left >> (right & 31) : left >>> (right & 31); stack.push(arithmetic | 0); } else if (opcode === 0x0b) return stack.length ? stack[stack.length - 1] : undefined; else throw new WebAssembly.CompileError('Unsupported WebAssembly instruction.'); }
                return undefined;
            }
            function __folio_wasm_metadata(bytes, wanted) {
                var result = []; if (!bytes || bytes.length < 8) return result; var cursor = { i: 8 };
                while (cursor.i < bytes.length) {
                    var section = bytes[cursor.i++]; var size = __folio_wasm_read_u32(bytes, cursor); var end = Math.min(bytes.length, cursor.i + size);
                    if (section !== wanted) { cursor.i = end; continue; }
                    var count = __folio_wasm_read_u32(bytes, cursor);
                    for (var n = 0; n < count && cursor.i < end; n++) {
                        var moduleName = ''; if (wanted === 2) moduleName = __folio_wasm_read_name(bytes, cursor);
                        var name = __folio_wasm_read_name(bytes, cursor); var kind = bytes[cursor.i++]; var index = __folio_wasm_read_u32(bytes, cursor);
                        result.push(wanted === 2 ? { module: moduleName, name: name, kind: kind, index: index } : { name: name, kind: kind, index: index });
                        if (wanted === 2) { if (kind === 1) { cursor.i++; __folio_wasm_skip_limits(bytes, cursor); } else if (kind === 2) __folio_wasm_skip_limits(bytes, cursor); else if (kind === 3) { cursor.i += 2; } else if (kind === 4) __folio_wasm_read_u32(bytes, cursor); }
                    }
                    cursor.i = end;
                }
                return result;
            }
            function __folio_wasm_custom_sections(bytes, wantedName) {
                var result = []; var cursor = { i: 8 };
                while (cursor.i < bytes.length) {
                    var section = bytes[cursor.i++]; var size = __folio_wasm_read_u32(bytes, cursor); var start = cursor.i; var end = Math.min(bytes.length, start + size);
                    if (section === 0) { var nameLength = __folio_wasm_read_u32(bytes, cursor); var name = ''; for (var n = 0; n < nameLength && cursor.i < end; n++) name += String.fromCharCode(bytes[cursor.i++]); if (name === String(wantedName)) result.push(bytes.slice(cursor.i, end)); }
                    cursor.i = end;
                }
                return result;
            }
            globalThis.WebAssembly = {
                CompileError: function(message) { this.name = 'CompileError'; this.message = String(message || ''); },
                RuntimeError: function(message) { this.name = 'RuntimeError'; this.message = String(message || ''); },
                validate: function(bytes) {
                    if (!__folio_wasm_enabled || !bytes || bytes.length < 8) return false;
                    return bytes[0] === 0 && bytes[1] === 97 && bytes[2] === 115 && bytes[3] === 109 && bytes[4] === 1 && bytes[5] === 0 && bytes[6] === 0 && bytes[7] === 0;
                },
                compile: function(bytes) {
                    if (!this.validate(bytes)) return Promise.reject(new WebAssembly.CompileError('Invalid WebAssembly module'));
                    return Promise.resolve(new WebAssembly.Module(bytes));
                },
                instantiate: function(bytes, imports) {
                    return this.compile(bytes).then(function(module) { return { module: module, instance: new WebAssembly.Instance(module, imports) }; });
                },
                Memory: function(options) {
                    if (!__folio_wasm_enabled) throw new DOMException('WebAssembly is disabled by the host.', 'NotSupportedError');
                    options = options || {}; this._pages = Number(options.initial) || 0;
                    this._maximum = options.maximum === undefined ? __folio_wasm_max_pages : Number(options.maximum);
                    if (this._pages < 0 || this._pages > this._maximum || this._pages > __folio_wasm_max_pages) throw new RangeError('WebAssembly memory limit exceeded.');
                    this.buffer = new ArrayBuffer(this._pages * 65536);
                },
                Module: function(bytes) {
                    if (!__folio_wasm_enabled) throw new DOMException('WebAssembly is disabled by the host.', 'NotSupportedError');
                    if (!WebAssembly.validate(bytes)) throw new WebAssembly.CompileError('Invalid WebAssembly module');
                    this.bytes = bytes; this._exports = __folio_wasm_metadata(bytes, 7); this._imports = __folio_wasm_metadata(bytes, 2); this._functions = __folio_wasm_functions(bytes); this._globals = __folio_wasm_globals(bytes); this._memories = __folio_wasm_memories(bytes); this._tables = __folio_wasm_tables(bytes); this._functionImportCount = this._imports.filter(function(entry) { return entry.kind === 0; }).length;
                },
                Instance: function(module, imports) {
                    if (!(module instanceof WebAssembly.Module)) throw new TypeError('WebAssembly.Instance requires a Module.');
                    var importedFunctions = []; var importedMemories = []; var importedTables = []; var importedGlobals = []; module._imports.forEach(function(entry) { if (!imports || !imports[entry.module] || imports[entry.module][entry.name] === undefined) throw new TypeError('Missing WebAssembly import: ' + entry.module + '.' + entry.name); if (entry.kind === 0) { if (typeof imports[entry.module][entry.name] !== 'function') throw new TypeError('WebAssembly function import is not callable.'); importedFunctions.push(imports[entry.module][entry.name]); } else if (entry.kind === 1) { if (!(imports[entry.module][entry.name] instanceof WebAssembly.Table)) throw new TypeError('WebAssembly table import is not a Table.'); importedTables.push(imports[entry.module][entry.name]); } else if (entry.kind === 2) { if (!(imports[entry.module][entry.name] instanceof WebAssembly.Memory)) throw new TypeError('WebAssembly memory import is not a Memory.'); importedMemories.push(imports[entry.module][entry.name]); } else if (entry.kind === 3) { if (!(imports[entry.module][entry.name] instanceof WebAssembly.Global)) throw new TypeError('WebAssembly global import is not a Global.'); importedGlobals.push(imports[entry.module][entry.name]); } });
                    this.module = module; this.exports = {};
                    module._exports.forEach(function(entry) { if (entry.kind === 0) { var functionIndex = entry.index - module._functionImportCount; this.exports[entry.name] = entry.index < module._functionImportCount ? importedFunctions[entry.index] : functionIndex >= 0 && module._functions[functionIndex] ? function() { return __folio_wasm_execute(module._functions[functionIndex], arguments, this, importedFunctions); } : function() { throw new WebAssembly.CompileError('WebAssembly function body is unavailable.'); }; } else if (entry.kind === 1 && importedTables[entry.index]) this.exports[entry.name] = importedTables[entry.index]; else if (entry.kind === 1 && module._tables[entry.index - importedTables.length]) { var table = module._tables[entry.index - importedTables.length]; this.exports[entry.name] = new WebAssembly.Table({ initial: table.initial, maximum: table.maximum }); } else if (entry.kind === 2 && importedMemories[entry.index]) this.exports[entry.name] = importedMemories[entry.index]; else if (entry.kind === 2 && module._memories[entry.index - importedMemories.length]) { var memory = module._memories[entry.index - importedMemories.length]; this.exports[entry.name] = new WebAssembly.Memory({ initial: memory.initial, maximum: memory.maximum }); } else if (entry.kind === 3 && importedGlobals[entry.index]) this.exports[entry.name] = importedGlobals[entry.index]; else if (entry.kind === 3 && module._globals[entry.index - importedGlobals.length] !== undefined) this.exports[entry.name] = new WebAssembly.Global({ value: 'i32', mutable: false }, module._globals[entry.index - importedGlobals.length]); }, this);
                },
                Table: function(options) {
                    if (!__folio_wasm_enabled) throw new DOMException('WebAssembly is disabled by the host.', 'NotSupportedError');
                    options = options || {}; this._maximum = options.maximum === undefined ? 100000 : Number(options.maximum); this._entries = [];
                    var initial = Number(options.initial) || 0; if (initial < 0 || initial > this._maximum || initial > 100000) throw new RangeError('WebAssembly table limit exceeded.');
                    for (var i = 0; i < initial; i++) this._entries.push(null);
                },
                Global: function(options, value) {
                    if (!__folio_wasm_enabled) throw new DOMException('WebAssembly is disabled by the host.', 'NotSupportedError');
                    options = options || {}; this.type = String(options.value || 'i32'); this.mutable = !!options.mutable; this._value = value === undefined ? 0 : value;
                    Object.defineProperty(this, 'value', { get: function() { return this._value; }, set: function(next) { if (!this.mutable) throw new TypeError('Immutable WebAssembly.Global.'); this._value = next; } });
                }
            };
            WebAssembly.Memory.prototype.grow = function(delta) {
                delta = Number(delta) || 0; var previous = this._pages; var next = previous + delta;
                if (delta < 0 || next > this._maximum || next > __folio_wasm_max_pages) throw new RangeError('WebAssembly memory limit exceeded.');
                this._pages = next; this.buffer = new ArrayBuffer(next * 65536); return previous;
            };
            Object.defineProperty(WebAssembly.Table.prototype, 'length', { get: function() { return this._entries.length; } });
            WebAssembly.Table.prototype.grow = function(delta, value) {
                delta = Number(delta) || 0; var previous = this._entries.length; var next = previous + delta;
                if (delta < 0 || next > this._maximum || next > 100000) throw new RangeError('WebAssembly table limit exceeded.');
                for (var i = previous; i < next; i++) this._entries.push(value === undefined ? null : value); return previous;
            };
            WebAssembly.Table.prototype.get = function(index) { index = Number(index); if (index < 0 || index >= this._entries.length) throw new RangeError('WebAssembly table index out of range.'); return this._entries[index]; };
            WebAssembly.Table.prototype.set = function(index, value) { index = Number(index); if (index < 0 || index >= this._entries.length) throw new RangeError('WebAssembly table index out of range.'); this._entries[index] = value; };
            WebAssembly.Global.prototype.valueOf = function() { return this.value; };
            WebAssembly.Module.exports = function(module) {
                if (!(module instanceof WebAssembly.Module)) throw new TypeError('WebAssembly.Module.exports requires a Module.');
                return module._exports.slice();
            };
            WebAssembly.Module.imports = function(module) {
                if (!(module instanceof WebAssembly.Module)) throw new TypeError('WebAssembly.Module.imports requires a Module.');
                return module._imports.slice();
            };
            WebAssembly.Module.customSections = function(module, name) {
                if (!(module instanceof WebAssembly.Module)) throw new TypeError('WebAssembly.Module.customSections requires a Module.');
                return __folio_wasm_custom_sections(module.bytes, name);
            };
            globalThis.innerWidth = 800;
            globalThis.innerHeight = 600;
            globalThis.devicePixelRatio = 1;
            globalThis.scrollX = 0;
            globalThis.scrollY = 0;
            Object.defineProperty(globalThis, 'pageXOffset', { get: function() { return globalThis.scrollX; } });
            Object.defineProperty(globalThis, 'pageYOffset', { get: function() { return globalThis.scrollY; } });
            globalThis.scrollTo = function(x, y) { globalThis.scrollX = Number(x) || 0; globalThis.scrollY = Number(y) || 0; };
            globalThis.scrollBy = function(x, y) { globalThis.scrollX += Number(x) || 0; globalThis.scrollY += Number(y) || 0; };
            globalThis.matchMedia = function(query) {
                query = String(query);
                const min = /min-width\s*:\s*(\d+)px/.exec(query);
                const max = /max-width\s*:\s*(\d+)px/.exec(query);
                const matches = (!min || globalThis.innerWidth >= Number(min[1])) && (!max || globalThis.innerWidth <= Number(max[1]));
                const listeners = [];
                return {
                    media: query,
                    matches: matches,
                    onchange: null,
                    addListener: function(fn) { if (typeof fn === 'function') listeners.push(fn); },
                    removeListener: function(fn) { const i = listeners.indexOf(fn); if (i >= 0) listeners.splice(i, 1); },
                    addEventListener: function(type, fn) { if (type === 'change') this.addListener(fn); },
                    removeEventListener: function(type, fn) { if (type === 'change') this.removeListener(fn); },
                    dispatchEvent: function() { return true; }
                };
            };
            globalThis.CSS = globalThis.CSS || {};
            globalThis.CSS.supports = function(property, value) {
                if (arguments.length === 1) return /\(|\)/.test(String(property));
                property = String(property).trim().toLowerCase();
                value = String(value).trim();
                return property.length > 0 && value.length > 0 && !/[{};]/.test(value);
            };
            globalThis.CSS.escape = function(value) {
                value = String(value);
                return value.replace(/[^a-zA-Z0-9_-]/g, function(c) { return String.fromCharCode(92) + c; }).replace(/^[0-9]/, function(c) { return String.fromCharCode(92) + '3' + c + ' '; });
            };
            globalThis.fetch = function(input) {
                const url = String(input);
                if (url.slice(0, 5).toLowerCase() !== 'data:')
                    return Promise.reject(new TypeError('The requested URL is not allowed by Folio.'));
                const comma = url.indexOf(',');
                const header = comma < 0 ? '' : url.slice(5, comma);
                const payload = comma < 0 ? '' : url.slice(comma + 1);
                const body = header.toLowerCase().indexOf(';base64') >= 0
                    ? (typeof atob === 'function' ? atob(payload) : payload)
                    : decodeURIComponent(payload);
                return Promise.resolve({
                    ok: true, status: 200, url: url,
                    text: function() { return Promise.resolve(body); },
                    json: function() { return Promise.resolve(JSON.parse(body)); }
                });
            };
            globalThis.ResizeObserver = function(callback) {
                if (typeof callback !== 'function') throw new TypeError('ResizeObserver callback must be callable');
                this._callback = callback;
                this._targets = [];
            };
            ResizeObserver.prototype.observe = function(target) { if (this._targets.indexOf(target) < 0) this._targets.push(target); };
            ResizeObserver.prototype.unobserve = function(target) { const i = this._targets.indexOf(target); if (i >= 0) this._targets.splice(i, 1); };
            ResizeObserver.prototype.disconnect = function() { this._targets.length = 0; };
            globalThis.IntersectionObserver = function(callback, options) {
                if (typeof callback !== 'function') throw new TypeError('IntersectionObserver callback must be callable');
                this._callback = callback;
                this.root = options && options.root || null;
                this.rootMargin = options && options.rootMargin || '0px';
                this.thresholds = options && options.threshold !== undefined ? [].concat(options.threshold) : [0];
                this._targets = [];
            };
            IntersectionObserver.prototype.observe = function(target) { if (this._targets.indexOf(target) < 0) this._targets.push(target); };
            IntersectionObserver.prototype.unobserve = function(target) { const i = this._targets.indexOf(target); if (i >= 0) this._targets.splice(i, 1); };
            IntersectionObserver.prototype.disconnect = function() { this._targets.length = 0; };
            IntersectionObserver.prototype.takeRecords = function() { return []; };
            globalThis.MutationObserver = function(callback) {
                return __folio_mutation_host.create(callback);
            };
            globalThis.Path2D = function() { return __folio_canvas_host.createPath(); };
            globalThis.Audio = function(src) { const element = document.createElement('audio'); if (src !== undefined) element.src = String(src); return element; };
            globalThis.FormData = function(form) {
                this._entries = [];
                if (form && form.elements) for (const control of form.elements) if (control.name) this.append(control.name, control.value || '');
            };
            FormData.prototype.append = function(name, value) { this._entries.push([String(name), value]); };
            FormData.prototype.set = function(name, value) { this.delete(name); this.append(name, value); };
            FormData.prototype.get = function(name) { const e = this._entries.find(x => x[0] === String(name)); return e ? e[1] : null; };
            FormData.prototype.getAll = function(name) { return this._entries.filter(x => x[0] === String(name)).map(x => x[1]); };
            FormData.prototype.has = function(name) { return this._entries.some(x => x[0] === String(name)); };
            FormData.prototype.delete = function(name) { name = String(name); this._entries = this._entries.filter(x => x[0] !== name); };
            FormData.prototype.entries = function() { return this._entries[Symbol.iterator](); };
            FormData.prototype.keys = function() { return this._entries.map(x => x[0])[Symbol.iterator](); };
            FormData.prototype.values = function() { return this._entries.map(x => x[1])[Symbol.iterator](); };
            function makeStorage() {
                const data = Object.create(null);
                const storage = {
                    get length() { return Object.keys(data).length; },
                    key: function(index) { return Object.keys(data)[index] ?? null; },
                    getItem: function(key) { key = String(key); return Object.prototype.hasOwnProperty.call(data, key) ? data[key] : null; },
                    setItem: function(key, value) { key = String(key); value = String(value); let size = 0; for (const k of Object.keys(data)) size += k.length + data[k].length; size += key.length + value.length; if (size > 5 * 1024 * 1024) throw new DOMException('Storage quota exceeded', 'QuotaExceededError'); data[key] = value; },
                    removeItem: function(key) { delete data[String(key)]; },
                    clear: function() { for (const key of Object.keys(data)) delete data[key]; }
                };
                return new Proxy(storage, { get: function(target, key) { if (key in target) return target[key]; return target.getItem(key); }, set: function(target, key, value) { target.setItem(key, value); return true; }, deleteProperty: function(target, key) { target.removeItem(key); return true; }, ownKeys: function() { return Object.keys(data); } });
            }
            globalThis.localStorage = makeStorage();
            globalThis.sessionStorage = makeStorage();
            globalThis.URLSearchParams = function(init) {
                this._pairs = [];
                if (typeof init === 'string') for (const part of init.replace(/^\?/, '').split('&')) if (part) { const i = part.indexOf('='); this.append(decodeURIComponent(i < 0 ? part : part.slice(0, i)), decodeURIComponent(i < 0 ? '' : part.slice(i + 1))); }
            };
            URLSearchParams.prototype.append = function(k,v) { this._pairs.push([String(k), String(v)]); };
            URLSearchParams.prototype.set = function(k,v) { this.delete(k); this.append(k,v); };
            URLSearchParams.prototype.get = function(k) { const p = this._pairs.find(x => x[0] === String(k)); return p ? p[1] : null; };
            URLSearchParams.prototype.has = function(k) { return this._pairs.some(x => x[0] === String(k)); };
            URLSearchParams.prototype.delete = function(k) { k = String(k); this._pairs = this._pairs.filter(x => x[0] !== k); };
            URLSearchParams.prototype.toString = function() { return this._pairs.map(x => encodeURIComponent(x[0]) + '=' + encodeURIComponent(x[1])).join('&'); };
            globalThis.URL = function(value, base) { const raw = String(value); const match = /^(?:(\w+):\/\/([^\/]+))?([^?#]*)(\?[^#]*)?(#.*)?$/.exec(raw); this.protocol = match && match[1] ? match[1] + ':' : ''; this.host = match && match[2] ? match[2] : ''; this.origin = this.protocol && this.host ? this.protocol + '//' + this.host : ''; this.pathname = match && match[3] ? match[3] : '/'; this.search = match && match[4] ? match[4] : ''; this.hash = match && match[5] ? match[5] : ''; this.href = this.origin + this.pathname + this.search + this.hash; this.searchParams = new URLSearchParams(this.search); };
            globalThis.Option = function(text, value, defaultSelected, selected) { const option = document.createElement('option'); option.text = text === undefined ? '' : String(text); if (value !== undefined) option.value = String(value); if (defaultSelected) option.defaultSelected = true; if (selected) option.selected = true; return option; };
            globalThis.btoa = function(value) { const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/'; const bytes = String(value); let out = ''; for (let i = 0; i < bytes.length; i += 3) { const a = bytes.charCodeAt(i), b = i + 1 < bytes.length ? bytes.charCodeAt(i + 1) : 0, c = i + 2 < bytes.length ? bytes.charCodeAt(i + 2) : 0; out += alphabet[a >> 2] + alphabet[((a & 3) << 4) | (b >> 4)] + (i + 1 < bytes.length ? alphabet[((b & 15) << 2) | (c >> 6)] : '=') + (i + 2 < bytes.length ? alphabet[c & 63] : '='); } return out; };
            globalThis.atob = function(value) { const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/'; const input = String(value).replace(/=+$/, ''); let out = ''; for (let i = 0; i < input.length; i += 4) { const a = alphabet.indexOf(input[i]), b = alphabet.indexOf(input[i + 1]), c = alphabet.indexOf(input[i + 2]), d = alphabet.indexOf(input[i + 3]); out += String.fromCharCode((a << 2) | (b >> 4)); if (c >= 0) out += String.fromCharCode(((b & 15) << 4) | (c >> 2)); if (d >= 0) out += String.fromCharCode(((c & 3) << 6) | d); } return out; };
            globalThis.crypto = { getRandomValues: function(array) { for (let i = 0; i < array.length; i++) array[i] = Math.floor(Math.random() * 256); return array; } };
            globalThis.TextEncoder = function() {};
            TextEncoder.prototype.encode = function(value) { const text = String(value); const bytes = new Uint8Array(text.length); for (let i = 0; i < text.length; i++) bytes[i] = text.charCodeAt(i) & 255; return bytes; };
            globalThis.TextDecoder = function() {};
            TextDecoder.prototype.decode = function(value) { let text = ''; for (let i = 0; i < value.length; i++) text += String.fromCharCode(value[i]); return text; };
            globalThis.structuredClone = function(value) { return JSON.parse(JSON.stringify(value)); };
            globalThis.console = globalThis.console || {};
            for (const method of ['log','info','warn','error','debug','dir','trace']) if (typeof console[method] !== 'function') console[method] = function() {};
        ");

        // 4. Event handler content attributes (#457): the parsed ones now, later ones as they change or are inserted.
        handlers.Scan(document, context);
        document.HandlerAttributeChanged += (element, name, value) => handlers.Changed(context.Wrap(element), name, value);
        document.NodeInserted += node => handlers.Scan(node, context);

        // 5. Trusted event dispatcher bridge (#458)
        document.EventDispatcher = (targetNode, eventType, args) =>
        {
            var targetWrapper = targetNode == document ? docWrapper : context.Wrap(targetNode);
            var jsArgs = new ScriptValue[]
            {
                ScriptValue.FromBoolean(args.Bubbles),            // 0
                ScriptValue.FromBoolean(args.Cancelable),         // 1
                ScriptValue.FromBoolean(args.Composed),           // 2
                ScriptValue.FromNumber(args.Detail),              // 3
                ScriptValue.FromNumber(args.ScreenX),             // 4
                ScriptValue.FromNumber(args.ScreenY),             // 5
                ScriptValue.FromNumber(args.ClientX),             // 6
                ScriptValue.FromNumber(args.ClientY),             // 7
                ScriptValue.FromBoolean(args.CtrlKey),            // 8
                ScriptValue.FromBoolean(args.ShiftKey),           // 9
                ScriptValue.FromBoolean(args.AltKey),             // 10
                ScriptValue.FromBoolean(args.MetaKey),            // 11
                ScriptValue.FromNumber(args.Button),              // 12
                ScriptValue.FromNumber(args.Buttons),             // 13
                args.RelatedTarget is not null ? (args.RelatedTarget == document ? docWrapper : context.Wrap(args.RelatedTarget)) : ScriptValue.Null, // 14
                ScriptValue.FromNumber(args.PointerId),           // 15
                ScriptValue.FromNumber(args.Width),               // 16
                ScriptValue.FromNumber(args.Height),              // 17
                ScriptValue.FromNumber(args.Pressure),            // 18
                ScriptValue.FromNumber(args.TangentialPressure),  // 19
                ScriptValue.FromNumber(args.TiltX),               // 20
                ScriptValue.FromNumber(args.TiltY),               // 21
                ScriptValue.FromNumber(args.Twist),               // 22
                ScriptValue.FromString(args.PointerType),         // 23
                ScriptValue.FromBoolean(args.IsPrimary),          // 24
                ScriptValue.FromNumber(args.DeltaX),              // 25
                ScriptValue.FromNumber(args.DeltaY),              // 26
                ScriptValue.FromNumber(args.DeltaZ),              // 27
                ScriptValue.FromNumber(args.DeltaMode),           // 28
                ScriptValue.FromString(args.Key),                 // 29
                ScriptValue.FromString(args.Code),                // 30
                ScriptValue.FromNumber(args.Location),            // 31
                ScriptValue.FromBoolean(args.Repeat),             // 32
                ScriptValue.FromBoolean(args.IsComposing),        // 33
                args.Data is not null ? ScriptValue.FromString(args.Data) : ScriptValue.Null, // 34
                ScriptValue.FromString(args.InputType)            // 35
            };

            var argsArray = engine.CreateArray(jsArgs);
            var result = engine.Call(dispatchHostEvent, ScriptValue.Undefined, [targetWrapper, ScriptValue.FromString(eventType), argsArray]);
            return result.IsBoolean ? result.AsBoolean() : true;
        };

        return context.WrapperCache;
    }

    private static void InstallDomException(IScriptEngine engine)
    {
        engine.Evaluate(@"
            (function() {
                if (typeof globalThis.DOMException === 'function') return;

                const codeMap = {
                    IndexSizeError: 1,
                    DOMStringSizeError: 2,
                    HierarchyRequestError: 3,
                    WrongDocumentError: 4,
                    InvalidCharacterError: 5,
                    NoDataAllowedError: 6,
                    NoModificationAllowedError: 7,
                    NotFoundError: 8,
                    NotSupportedError: 9,
                    InUseAttributeError: 10,
                    InvalidStateError: 11,
                    SyntaxError: 12,
                    InvalidModificationError: 13,
                    NamespaceError: 14,
                    InvalidAccessError: 15,
                    SecurityError: 18,
                    NetworkError: 19,
                    AbortError: 20,
                    TimeoutError: 23,
                    QuotaExceededError: 22
                };

                class DOMException extends Error {
                    constructor(message = '', name = 'Error') {
                        super(message);
                        this.name = name;
                        this.message = message;
                        this.code = codeMap[name] || 0;
                    }
                }

                DOMException.INDEX_SIZE_ERR = 1;
                DOMException.DOMSTRING_SIZE_ERR = 2;
                DOMException.HIERARCHY_REQUEST_ERR = 3;
                DOMException.WRONG_DOCUMENT_ERR = 4;
                DOMException.INVALID_CHARACTER_ERR = 5;
                DOMException.NO_DATA_ALLOWED_ERR = 6;
                DOMException.NO_MODIFICATION_ALLOWED_ERR = 7;
                DOMException.NOT_FOUND_ERR = 8;
                DOMException.NOT_SUPPORTED_ERR = 9;
                DOMException.INUSE_ATTRIBUTE_ERR = 10;
                DOMException.INVALID_STATE_ERR = 11;
                DOMException.SYNTAX_ERR = 12;
                DOMException.INVALID_MODIFICATION_ERR = 13;
                DOMException.NAMESPACE_ERR = 14;
                DOMException.INVALID_ACCESS_ERR = 15;
                DOMException.SECURITY_ERR = 18;
                DOMException.NETWORK_ERR = 19;
                DOMException.ABORT_ERR = 20;
                DOMException.TIMEOUT_ERR = 23;
                DOMException.QUOTA_EXCEEDED_ERR = 22;

                globalThis.DOMException = DOMException;
            })();
        ");
    }

    private static void InstallConstructors(IScriptEngine engine)
    {
        engine.Evaluate(@"
            (function() {
                function defClass(name, parent) {
                    const ctor = function() {
                        throw new TypeError('Illegal constructor');
                    };
                    Object.defineProperty(ctor, 'name', { value: name, configurable: true });
                    if (parent) {
                        Object.setPrototypeOf(ctor, parent);
                        ctor.prototype = Object.create(parent.prototype, {
                            constructor: { value: ctor, writable: true, configurable: true }
                        });
                    } else {
                        ctor.prototype = Object.create(Object.prototype, {
                            constructor: { value: ctor, writable: true, configurable: true }
                        });
                    }
                    globalThis[name] = ctor;
                    return ctor;
                }

                const Node = defClass('Node');
                Node.ELEMENT_NODE = 1;
                Node.ATTRIBUTE_NODE = 2;
                Node.TEXT_NODE = 3;
                Node.CDATA_SECTION_NODE = 4;
                Node.ENTITY_REFERENCE_NODE = 5;
                Node.ENTITY_NODE = 6;
                Node.PROCESSING_INSTRUCTION_NODE = 7;
                Node.COMMENT_NODE = 8;
                Node.DOCUMENT_NODE = 9;
                Node.DOCUMENT_TYPE_NODE = 10;
                Node.DOCUMENT_FRAGMENT_NODE = 11;
                Node.NOTATION_NODE = 12;

                Node.DOCUMENT_POSITION_DISCONNECTED = 0x01;
                Node.DOCUMENT_POSITION_PRECEDING = 0x02;
                Node.DOCUMENT_POSITION_FOLLOWING = 0x04;
                Node.DOCUMENT_POSITION_CONTAINS = 0x08;
                Node.DOCUMENT_POSITION_CONTAINED_BY = 0x10;
                Node.DOCUMENT_POSITION_IMPLEMENTATION_SPECIFIC = 0x20;

                const Document = defClass('Document', Node);
                const DocumentType = defClass('DocumentType', Node);
                const DocumentFragment = defClass('DocumentFragment', Node);

                const CharacterData = defClass('CharacterData', Node);
                const Text = defClass('Text', CharacterData);
                const Comment = defClass('Comment', CharacterData);

                const Element = defClass('Element', Node);
                const SVGElement = defClass('SVGElement', Element);
                const HTMLElement = defClass('HTMLElement', Element);
                const HTMLMediaElement = defClass('HTMLMediaElement', HTMLElement);
                const HTMLAudioElement = defClass('HTMLAudioElement', HTMLMediaElement);
                const HTMLVideoElement = defClass('HTMLVideoElement', HTMLMediaElement);
                const HTMLIFrameElement = defClass('HTMLIFrameElement', HTMLElement);

                const Attr = defClass('Attr', Node);

                // Specific HTML element classes
                const htmlTags = [
                    'HTMLHtmlElement',
                    'HTMLHeadElement',
                    'HTMLBodyElement',
                    'HTMLTitleElement',
                    'HTMLDivElement',
                    'HTMLSpanElement',
                    'HTMLParagraphElement',
                    'HTMLAnchorElement',
                    'HTMLInputElement',
                    'HTMLButtonElement',
                    'HTMLImageElement',
                    'HTMLOptionElement',
                    'HTMLSelectElement',
                    'HTMLTextAreaElement',
                    'HTMLTemplateElement',
                    'HTMLScriptElement',
                    'HTMLUnknownElement'
                ];
                for (const tag of htmlTags) {
                    defClass(tag, HTMLElement);
                }

                // Collections
                const NodeList = defClass('NodeList');
                NodeList.prototype[Symbol.iterator] = Array.prototype.values;
                NodeList.prototype.values = Array.prototype.values;
                NodeList.prototype.keys = Array.prototype.keys;
                NodeList.prototype.entries = Array.prototype.entries;

                const HTMLCollection = defClass('HTMLCollection');
                HTMLCollection.prototype[Symbol.iterator] = Array.prototype.values;
                HTMLCollection.prototype.values = Array.prototype.values;

                const DOMTokenList = defClass('DOMTokenList');
                DOMTokenList.prototype[Symbol.iterator] = Array.prototype.values;
                DOMTokenList.prototype.values = Array.prototype.values;
                DOMTokenList.prototype.keys = Array.prototype.keys;
                DOMTokenList.prototype.entries = Array.prototype.entries;

                const DOMStringMap = defClass('DOMStringMap');

                const CSSStyleDeclaration = defClass('CSSStyleDeclaration');

                const NamedNodeMap = defClass('NamedNodeMap');
                NamedNodeMap.prototype[Symbol.iterator] = Array.prototype.values;
                NamedNodeMap.prototype.values = Array.prototype.values;
            })();
        ");
    }
}
