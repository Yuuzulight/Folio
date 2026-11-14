using System.Text.Json;
using Folio;

namespace Folio.Scripting.Dom;

/// <summary>Host-owned permissions and retirement for the compatibility capture surface.</summary>
/// <remarks>This does not implement a camera or microphone device backend.</remarks>
internal sealed class CaptureBindingState(IScriptEngine engine, DomContext context) : IDisposable
{
    private readonly Dictionary<long, TrackLease> _tracks = [];
    private readonly HashSet<RequestLease> _requests = [];
    private readonly Dictionary<RequestLease, MediaCaptureVideoFrame> _latestVideo = [];
    private readonly object _sampleGate = new();
    private long _nextTrack;
    private long _cameraGeneration;
    private long _microphoneGeneration;
    private bool _camera = context.Capabilities.Camera;
    private bool _microphone = context.Capabilities.Microphone;
    private bool _cameraLabels;
    private bool _microphoneLabels;
    private volatile bool _disposed;
    private ScriptValue _deviceChange;
    private bool _reportedActive;
    private static readonly HostObjectTemplate RequestTemplate = new("CaptureRequestLease");
    private static readonly HostObjectTemplate TrackTemplate = new("CaptureTrackLease");

    private sealed class RequestLease(CaptureBindingState owner, bool audio, bool video, long cameraGeneration, long microphoneGeneration, IMediaCaptureSession? session)
    {
        internal CaptureBindingState Owner { get; } = owner;
        internal bool Audio { get; } = audio;
        internal bool Video { get; } = video;
        internal long CameraGeneration { get; } = cameraGeneration;
        internal long MicrophoneGeneration { get; } = microphoneGeneration;
        internal IMediaCaptureSession? Session { get; } = session;
        internal Action<MediaCaptureVideoFrame>? VideoHandler;
        internal Action<MediaAudioFrame>? AudioHandler;
        internal Action? RevokedHandler;
        internal int Closed, Revoked;
        internal volatile bool AudioStopped, VideoStopped;
        internal bool AudioSuppressed;
        internal CaptureAudioBuffer? AudioBuffer;
        internal MediaResourceBudget.Lease? VideoReservation;
    }
    private sealed class TrackLease(CaptureBindingState owner, long id, string kind, RequestLease request, ScriptValue ended)
    {
        internal CaptureBindingState Owner { get; } = owner;
        internal long Id { get; } = id;
        internal string Kind { get; } = kind;
        internal RequestLease Request { get; } = request;
        internal ScriptValue Ended = ended;
        internal bool Enabled = true, Muted;
    }

    private bool HasGrant(bool audio, bool video, bool report)
    {
        var capabilities = context.Capabilities;
        var live = !_disposed && !context.ScriptFeaturesDisposed;
        var microphone = !audio || live && capabilities.Microphone;
        var camera = !video || live && capabilities.Camera;
        if (report)
        {
            if (!microphone) context.CheckCapability("Microphone", false);
            if (!camera) context.CheckCapability("Camera", false);
        }
        return microphone && camera;
    }

    private bool HostPermits(bool audio, bool video)
    {
        if (!HasGrant(audio, video, report: true)) return false;
        var camera = _cameraGeneration;
        var microphone = _microphoneGeneration;
        bool allowed;
        try { allowed = context.MediaCapturePermission?.Invoke(new MediaCaptureRequest(audio, video)) ?? true; }
        catch { allowed = false; }
        if (!allowed)
        {
            if (audio) context.CheckCapability("Microphone", false);
            if (video) context.CheckCapability("Camera", false);
        }
        // A host permission callback may revoke and regrant reentrantly.
        return allowed && (!video || camera == _cameraGeneration) && (!audio || microphone == _microphoneGeneration) && HasGrant(audio, video, report: true);
    }

    private ScriptValue Request(bool audio, bool video)
    {
        if (!audio && !video) throw WebIdlException.TypeError("Capture requires audio or video.");
        if (!HostPermits(audio, video)) throw WebIdlException.DomException("NotAllowedError", "The host denied camera or microphone capture.");
        IMediaCaptureSession? session = null;
        if (context.MediaCaptureProvider is { } provider)
        {
            try { session = provider.Open(new MediaCaptureRequest(audio, video)); }
            catch { session = null; }
            if (session is null)
                throw WebIdlException.DomException("NotAllowedError", "The host has no permitted capture device.");
        }
        var approval = new RequestLease(this, audio, video, _cameraGeneration, _microphoneGeneration, session);
        approval.AudioBuffer = audio ? new CaptureAudioBuffer(context.MediaBudget) : null;
        approval.VideoReservation = video ? context.MediaBudget.AcquireBytes() : null;
        _requests.Add(approval);
        if (session is not null)
        {
            try
            {
                approval.VideoHandler = frame =>
                {
                    if (!frame.IsValid || frame.Rgba.Length > 16 * 1024 * 1024) return;
                    lock (_sampleGate)
                    {
                        if (_disposed || Volatile.Read(ref approval.Closed) != 0 || Volatile.Read(ref approval.Revoked) != 0 || approval.VideoStopped) return;
                        _latestVideo.Remove(approval);
                        if (approval.VideoReservation?.Resize(frame.Rgba.Length) == true) _latestVideo[approval] = frame;
                        else approval.VideoReservation?.Resize(0);
                    }
                };
                approval.AudioHandler = frame =>
                {
                    lock (_sampleGate)
                    {
                        if (!_disposed && Volatile.Read(ref approval.Closed) == 0 && Volatile.Read(ref approval.Revoked) == 0 && !approval.AudioStopped)
                            approval.AudioBuffer?.Push(frame, retain: !approval.AudioSuppressed);
                    }
                };
                approval.RevokedHandler = () =>
                {
                    if (Interlocked.Exchange(ref approval.Revoked, 1) != 0 || Volatile.Read(ref approval.Closed) != 0 || _disposed) return;
                    lock (_sampleGate) { approval.AudioBuffer?.Dispose(); _latestVideo.Remove(approval); approval.VideoReservation?.Dispose(); }
                    if (context.IsDocumentOwnerThread) RetireRequest(approval);
                    else if (context.OwnerEventLoop is { } loop)
                    {
                        try { loop.QueueTask(TaskSource.Generic, () => { context.EnterDocumentTurn(engine); if (!_disposed) RetireRequest(approval); }, "capture device revoked"); }
                        catch (ObjectDisposedException) { }
                    }
                };
                session.VideoFrameAvailable += approval.VideoHandler;
                session.AudioFrameAvailable += approval.AudioHandler;
                session.Revoked += approval.RevokedHandler;
                // Seed once after subscription. Callback history is authoritative thereafter;
                // polling CurrentAudioFrame on every turn would replay the same snapshot.
                var initial = audio ? session.CurrentAudioFrame : null;
                lock (_sampleGate)
                    if (initial is { } snapshot) approval.AudioBuffer?.Seed(snapshot);
            }
            catch { CloseRequest(approval); throw WebIdlException.DomException("NotAllowedError", "The host capture device could not be monitored."); }
        }
        var labelsChanged = audio && !_microphoneLabels || video && !_cameraLabels;
        if (audio) _microphoneLabels = true;
        if (video) _cameraLabels = true;
        if (labelsChanged) NotifyDeviceChange();
        if (Volatile.Read(ref approval.Closed) != 0 || Volatile.Read(ref approval.Revoked) != 0 || video && approval.CameraGeneration != _cameraGeneration || audio && approval.MicrophoneGeneration != _microphoneGeneration || !HasGrant(audio, video, true))
        {
            CloseRequest(approval);
            throw WebIdlException.DomException("NotAllowedError", "The capture permission has been retired.");
        }
        return engine.CreateHostObject(RequestTemplate, approval);
    }

    private ScriptValue Register(ScriptValue approval, string kind, ScriptValue ended)
    {
        if (!engine.TryGetHostTarget(approval, out var value) || value is not RequestLease request || !ReferenceEquals(request.Owner, this) || !ended.IsFunction)
            throw WebIdlException.TypeError("Invalid capture approval.");
        var audio = kind == "audio";
        if (Volatile.Read(ref request.Closed) != 0 || Volatile.Read(ref request.Revoked) != 0 || kind is not ("audio" or "video") || audio && !request.Audio || !audio && !request.Video ||
            request.Video && request.CameraGeneration != _cameraGeneration || request.Audio && request.MicrophoneGeneration != _microphoneGeneration || !HasGrant(audio, !audio, report: true))
            throw WebIdlException.DomException("NotAllowedError", "The capture permission has been retired.");
        var lease = new TrackLease(this, checked(++_nextTrack), kind, request, ended);
        var token = engine.CreateHostObject(TrackTemplate, lease);
        if (!HasGrant(audio, !audio, report: true) || request.Video && request.CameraGeneration != _cameraGeneration || request.Audio && request.MicrophoneGeneration != _microphoneGeneration)
            throw WebIdlException.DomException("NotAllowedError", "The capture permission has been retired.");
        _tracks.Add(lease.Id, lease);
        UpdateActivity();
        if (!_tracks.ContainsKey(lease.Id)) throw WebIdlException.DomException("NotAllowedError", "The capture permission has been retired.");
        return token;
    }

    private TrackLease? Find(ScriptValue token) => engine.TryGetHostTarget(token, out var target) && target is TrackLease lease && ReferenceEquals(lease.Owner, this) ? lease : null;
    private bool IsLive(ScriptValue token, bool poll)
    {
        var lease = Find(token);
        if (lease is null || !_tracks.ContainsKey(lease.Id) || Volatile.Read(ref lease.Request.Revoked) != 0 || Volatile.Read(ref lease.Request.Closed) != 0) return false;
        if (!poll) return HasGrant(lease.Kind == "audio", lease.Kind == "video", report: false);
        var group = _tracks.Values.Where(track => ReferenceEquals(track.Request, lease.Request)).ToArray();
        if (!HostPermits(group.Any(track => track.Kind == "audio"), group.Any(track => track.Kind == "video")))
        {
            foreach (var track in group) Retire(track, notify: true);
            return false;
        }
        return HasGrant(lease.Kind == "audio", lease.Kind == "video", report: poll);
    }
    private void Retire(TrackLease lease, bool notify)
    {
        if (!_tracks.Remove(lease.Id)) return;
        var callback = lease.Ended;
        lease.Ended = ScriptValue.Undefined;
        if (notify && callback.IsFunction)
        {
            try { engine.Call(callback, ScriptValue.Undefined, []); }
            catch (Exception error) { context.OwnerEventLoop?.ReportException(error); }
        }
        UpdateActivity();
        var siblings = _tracks.Values.Where(track => ReferenceEquals(track.Request, lease.Request)).ToArray();
        var stopKind = !siblings.Any(track => track.Kind == lease.Kind) &&
            !(lease.Kind == "audio" ? lease.Request.AudioStopped : lease.Request.VideoStopped);
        if (stopKind)
        {
            lock (_sampleGate)
            {
                if (lease.Kind == "audio") { lease.Request.AudioStopped = true; lease.Request.AudioBuffer?.Dispose(); }
                else { lease.Request.VideoStopped = true; _latestVideo.Remove(lease.Request); lease.Request.VideoReservation?.Dispose(); }
            }
        }
        if (stopKind && lease.Request.Session is IMediaCaptureTrackControl control)
        {
            try
            {
                if (lease.Kind == "audio") control.StopAudio();
                if (lease.Kind == "video") control.StopVideo();
            }
            catch (Exception error) { context.OwnerEventLoop?.ReportException(error); }
        }
        if (siblings.Length == 0)
        {
            CloseRequest(lease.Request);
        }
    }

    private void CloseRequest(RequestLease request)
    {
        if (Interlocked.Exchange(ref request.Closed, 1) != 0) return;
        _requests.Remove(request);
        if (request.Session is { } session)
        {
            try { session.VideoFrameAvailable -= request.VideoHandler; } catch { }
            try { session.AudioFrameAvailable -= request.AudioHandler; } catch { }
            try { session.Revoked -= request.RevokedHandler; } catch { }
            try { session.Dispose(); } catch { }
        }
        lock (_sampleGate) { _latestVideo.Remove(request); request.AudioBuffer?.Dispose(); request.VideoReservation?.Dispose(); }
    }

    private void RetireRequest(RequestLease request)
    {
        foreach (var track in _tracks.Values.Where(track => ReferenceEquals(track.Request, request)).ToArray())
            Retire(track, notify: true);
        CloseRequest(request);
    }

    internal MediaCaptureVideoFrame? CurrentVideoFrame(ScriptValue track)
    {
        var lease = Find(track);
        if (lease is null || lease.Kind != "video" || !IsLive(track, poll: false)) return null;
        MediaCaptureVideoFrame? pushed = null;
        lock (_sampleGate) if (_latestVideo.TryGetValue(lease.Request, out var retained)) pushed = retained;
        var frame = pushed ?? lease.Request.Session?.CurrentVideoFrame;
        return frame is { IsValid: true } ? frame : null;
    }

    internal CaptureAudioReader? OpenAudioReader(ScriptValue track)
    {
        var lease = Find(track);
        if (lease is null || lease.Kind != "audio" || !IsLive(track, poll: false) || lease.Request.AudioBuffer is not { } buffer) return null;
        return new CaptureAudioReader(buffer, () => IsLive(track, poll: false) && lease.Enabled && !lease.Muted);
    }

    internal CaptureVideoReader? OpenVideoReader(ScriptValue track)
    {
        var lease = Find(track);
        if (lease is null || lease.Kind != "video" || !IsLive(track, poll: false)) return null;
        return new CaptureVideoReader(() => CurrentVideoFrame(track), () => IsLive(track, poll: false) && lease.Enabled && !lease.Muted);
    }

    internal string? TrackKind(ScriptValue track) => Find(track)?.Kind;

    private void SetTrackState(ScriptValue token, bool enabled, bool muted)
    {
        var lease = Find(token);
        if (lease is null) return;
        lock (_sampleGate)
        {
            lease.Enabled = enabled;
            lease.Muted = muted;
            // Each capture track has a request identity. Disabling it discards history
            // immediately, including disable/enable transitions between document ticks.
            if (lease.Kind == "audio") { lease.Request.AudioSuppressed = !enabled || muted; lease.Request.AudioBuffer?.Clear(); }
        }
    }
    private void UpdateActivity()
    {
        var active = _tracks.Count != 0;
        if (active == _reportedActive) return;
        _reportedActive = active;
        try { context.MediaCaptureActivityChanged?.Invoke(active); } catch { }
    }
    internal void UpdateCapabilities()
    {
        var camera = !_disposed && !context.ScriptFeaturesDisposed && context.Capabilities.Camera;
        var microphone = !_disposed && !context.ScriptFeaturesDisposed && context.Capabilities.Microphone;
        var changed = camera != _camera || microphone != _microphone;
        if (_camera && !camera) { _cameraGeneration++; _cameraLabels = false; }
        if (_microphone && !microphone) { _microphoneGeneration++; _microphoneLabels = false; }
        _camera = camera;
        _microphone = microphone;
        var retiring = _tracks.Values.Where(track => track.Kind == "audio" ? !microphone : !camera).ToArray();
        foreach (var track in retiring) Retire(track, notify: true);
        if (changed) NotifyDeviceChange();
    }
    private void NotifyDeviceChange()
    {
        if (_deviceChange.IsFunction)
        {
            try { engine.Call(_deviceChange, ScriptValue.Undefined, []); }
            catch (Exception error) { context.OwnerEventLoop?.ReportException(error); }
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UpdateCapabilities();
        foreach (var track in _tracks.Values.ToArray()) Retire(track, notify: true);
        foreach (var request in _requests.ToArray()) CloseRequest(request);
        _deviceChange = ScriptValue.Undefined;
    }

    internal static CaptureBindingState Install(IScriptEngine engine, DomContext context)
    {
        var state = new CaptureBindingState(engine, context);
        var host = new HostObjectTemplate("CaptureAuthority")
            .DefineMethod("request", (_, _, a) => state.Request(a.Length > 0 && a[0].AsBoolean(), a.Length > 1 && a[1].AsBoolean()))
            .DefineMethod("register", (_, _, a) => state.Register(a[0], a[1].AsString() ?? "", a[2]))
            .DefineMethod("live", (_, _, a) => ScriptValue.FromBoolean(a.Length > 0 && state.IsLive(a[0], a.Length > 1 && a[1].AsBoolean())))
            .DefineMethod("state", (_, _, a) => { state.SetTrackState(a[0], a[1].AsBoolean(), a[2].AsBoolean()); return ScriptValue.Undefined; })
            .DefineMethod("stop", (_, _, a) => { if (a.Length > 0 && state.Find(a[0]) is { } track) state.Retire(track, notify: false); return ScriptValue.Undefined; })
            .DefineMethod("devices", (_, _, _) => ScriptValue.FromString(ScriptJson.Serialize(new[]
            {
                new Dictionary<string, object?> { ["deviceId"] = "folio-audio", ["kind"] = "audioinput", ["label"] = "Folio audio" },
                new Dictionary<string, object?> { ["deviceId"] = "folio-video", ["kind"] = "videoinput", ["label"] = "Folio video" }
            }.Where(device => (string?)device["kind"] == "audioinput" ? state._microphoneLabels && state.HasGrant(true, false, false) : state._cameraLabels && state.HasGrant(false, true, false)))))
            .DefineMethod("onchange", (_, _, a) => { state._deviceChange = a.Length > 0 && a[0].IsFunction ? a[0] : ScriptValue.Undefined; return ScriptValue.Undefined; });
        engine.SetGlobalProperty("__folio_capture_authority", engine.CreateHostObject(host, new object()));
        try { engine.EvaluateHostScript(Source, "folio:capture-permissions"); }
        catch { state.Dispose(); throw; }
        finally { engine.SetGlobalProperty("__folio_capture_authority", ScriptValue.Undefined); }
        return state;
    }

    private const string Source = """
        (function(host){
          'use strict';
          const global=globalThis, apply=Reflect.apply, slice=Array.prototype.slice, indexOf=Array.prototype.indexOf,
            push=Array.prototype.push, splice=Array.prototype.splice, assign=Object.assign, define=Object.defineProperties,
            S=String, resolve=Promise.resolve.bind(Promise), reject=Promise.reject.bind(Promise), parse=JSON.parse;
          const records=new WeakMap(), get=WeakMap.prototype.get, set=WeakMap.prototype.set;
          const state=track=>{const value=apply(get,records,[track]);if(!value)throw new TypeError('Illegal invocation.');return value;};
          function dispatch(target,type){const value=state(target),event={type,target,track:target};const handler=target['on'+type];if(typeof handler==='function')try{handler.call(target,event);}catch(_){}for(const fn of apply(slice,value.listeners[type]||[],[]))try{fn.call(target,event);}catch(_){}}
          function ended(track,notify){const value=state(track);if(value.ended)return;value.ended=true;if(notify)dispatch(track,'ended');}
          function live(track){const value=state(track);if(value.ended)return false;if(value.token&&!host.live(value.token,true)){ended(track,true);return false;}return true;}
          function constraintError(kind,constraints){
            if(!constraints||typeof constraints!=='object')return false;
            const limits=kind==='video'?{width:[1,640],height:[1,360],frameRate:[1,30],aspectRatio:[0.01,640]}:{sampleRate:[8000,48000],channelCount:[1,2],sampleSize:[8,32]};
            for(const name of Object.keys(limits)){const value=constraints[name];if(value===undefined)continue;const range=limits[name];if(value&&typeof value==='object'){for(const key of ['min','max','exact']){const number=value[key];if(number!==undefined&&(typeof number!=='number'||!isFinite(number)||number<0||key==='min'&&number>range[1]||key==='max'&&number<range[0]||key==='exact'&&(number<range[0]||number>range[1])))return true;}}}
            const device=constraints.deviceId,exact=device&&typeof device==='object'?device.exact:device;
            return exact!==undefined&&S(exact)!==(kind==='audio'?'folio-audio':'folio-video');
          }
          let next=0;
          function Track(kind){
            if(!new.target)throw new TypeError('MediaStreamTrack requires new.');
            kind=S(kind||'');const value={kind,id:'track-'+(++next),enabled:true,muted:false,ended:false,token:null,constraints:{},listeners:Object.create(null)};
            apply(set,records,[this,value]);this.onended=null;this.onmute=null;this.onunmute=null;this.contentHint='';this._captureToken=null;
            define(this,{id:{get:()=>value.id},kind:{get:()=>value.kind},readyState:{get:()=>value.ended?'ended':'live'},enabled:{get:()=>value.enabled,set:v=>{v=!!v;if(v!==value.enabled){value.enabled=v;if(value.token)host.state(value.token,v,value.muted);}}},muted:{get:()=>value.muted,set:v=>{v=!!v;if(v!==value.muted){value.muted=v;if(value.token)host.state(value.token,value.enabled,v);dispatch(this,v?'mute':'unmute');}}}});
          }
          Track.prototype.addEventListener=function(type,fn){if(typeof fn!=='function')return;const listeners=state(this).listeners,key=S(type),list=listeners[key]||(listeners[key]=[]);if(apply(indexOf,list,[fn])<0)apply(push,list,[fn]);};
          Track.prototype.removeEventListener=function(type,fn){const list=state(this).listeners[S(type)]||[],i=apply(indexOf,list,[fn]);if(i>=0)apply(splice,list,[i,1]);};
          Track.prototype.stop=function(){const value=state(this);ended(this,false);if(value.token)host.stop(value.token);};
          Track.prototype.applyConstraints=function(constraints){if(!live(this))return resolve();if(constraints!==undefined&&constraints!==null&&typeof constraints!=='object')return reject(new TypeError('Track constraints must be an object.'));if(constraintError(this.kind,constraints))return reject(new DOMException('The requested media constraint is not supported.','OverconstrainedError'));state(this).constraints=constraints||{};return resolve();};
          Track.prototype.getConstraints=function(){return assign({},state(this).constraints);};
          Track.prototype.getSettings=function(){if(!live(this)){const value=state(this),settings={deviceId:value.id};const facing=value.constraints.facingMode;if(typeof facing==='string')settings.facingMode=facing;else if(facing&&facing.exact!==undefined)settings.facingMode=facing.exact;return settings;}const value=state(this),settings={deviceId:value.id,kind:value.kind};if(value.kind==='video')assign(settings,{width:640,height:360,frameRate:30});else if(value.kind==='audio')assign(settings,{sampleRate:48000,channelCount:2});for(const name of ['width','height','frameRate','facingMode','sampleRate','channelCount']){const item=value.constraints[name];if(typeof item==='number'||typeof item==='string'||typeof item==='boolean')settings[name]=item;else if(item&&typeof item==='object'&&item.exact!==undefined)settings[name]=item.exact;}return settings;};
          Track.prototype.getCapabilities=function(){return this.kind==='video'?{deviceId:[],kind:this.kind,width:{min:1,max:640},height:{min:1,max:360},frameRate:{min:1,max:30}}:this.kind==='audio'?{deviceId:[],kind:this.kind,sampleRate:{min:8000,max:48000},channelCount:{min:1,max:2}}:{deviceId:[],kind:this.kind};};
          function captured(kind,approval,constraints){const track=new Track(kind),value=state(track);value.constraints=constraints||{};value.token=host.register(approval,kind,()=>ended(track,true));track._captureToken=value.token;return track;}
          Track.prototype.clone=function(){const value=state(this);let copy;if(value.token&&live(this)){const approval=host.request(value.kind==='audio',value.kind==='video');copy=captured(value.kind,approval,assign({},value.constraints));}else{copy=new Track(value.kind);state(copy).constraints=assign({},value.constraints);if(value.ended)ended(copy,false);}copy.enabled=this.enabled;copy.contentHint=this.contentHint;return copy;};
          global.MediaStreamTrack=Track;
          // Keep the existing stream collection API; capture authorization resides in the private track records.
          const Stream=global.MediaStream;
          const deviceListeners=[];const devices={ondevicechange:null,
            addEventListener(type,fn){if(type==='devicechange'&&typeof fn==='function'&&apply(indexOf,deviceListeners,[fn])<0)apply(push,deviceListeners,[fn]);},
            removeEventListener(type,fn){const i=apply(indexOf,deviceListeners,[fn]);if(type==='devicechange'&&i>=0)apply(splice,deviceListeners,[i,1]);},
            dispatchEvent(event){if(event.type==='devicechange'){if(typeof devices.ondevicechange==='function')devices.ondevicechange(event);for(const fn of apply(slice,deviceListeners,[]))fn(event);}return true;},
            enumerateDevices(){return resolve(parse(host.devices()));},
            getSupportedConstraints(){return {audio:true,video:true,deviceId:true,groupId:true,width:true,height:true,aspectRatio:true,frameRate:true,facingMode:true,sampleRate:true,sampleSize:true,echoCancellation:true,autoGainControl:true,noiseSuppression:true,channelCount:true,latency:true,resizeMode:true,pan:true,tilt:true,zoom:true,torch:true,focusMode:true,whiteBalanceMode:true,exposureMode:true,exposureCompensation:true,colorTemperature:true,iso:true,brightness:true,contrast:true,saturation:true,sharpness:true,focusDistance:true};},
            getUserMedia(constraints){
              constraints=constraints||{};for(const kind of ['audio','video']){const value=constraints[kind];if(value!==undefined&&value!==false&&value!==true&&(typeof value!=='object'||value===null))return reject(new TypeError('The '+kind+' constraint must be a boolean or object.'));}
              if(!constraints.audio&&!constraints.video)return reject(new TypeError('getUserMedia requires audio or video.'));
              if(constraintError('audio',constraints.audio)||constraintError('video',constraints.video))return reject(new DOMException('The requested media constraint is not supported.','OverconstrainedError'));
              const tracks=[];try{const approval=host.request(!!constraints.audio,!!constraints.video);if(constraints.audio)tracks.push(captured('audio',approval,typeof constraints.audio==='object'?constraints.audio:{}));if(constraints.video)tracks.push(captured('video',approval,typeof constraints.video==='object'?constraints.video:{}));return resolve(new Stream(tracks));}catch(error){for(const track of tracks){const value=state(track);ended(track,false);if(value.token)host.stop(value.token);}return reject(error);}
            }
          };
          host.onchange(()=>devices.dispatchEvent({type:'devicechange',target:devices}));
          global.navigator.mediaDevices=devices;
        })(__folio_capture_authority);
        """;
}
