using Folio.Dom;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

public sealed class CaptureCapabilityTests
{
    [Fact]
    public void HostProviderSuppliesVideoFrameAndIsDisposedWhenTrackStops()
    {
        var provider = new TestCaptureProvider(new MediaCaptureVideoFrame(2, 1, new byte[] { 255, 0, 0, 255, 0, 255, 0, 255 }));
        using var fixture = new Installed(new FeatureCapabilities { Camera = true, Media = true }, provider);
        fixture.Engine.Evaluate("var video=document.createElement('video');document.body.appendChild(video);navigator.mediaDevices.getUserMedia({video:true}).then(s=>{stream=s;video.srcObject=s;result=typeof s.getVideoTracks()[0]._captureToken+':'+video.videoWidth+':'+video.videoHeight;});");
        fixture.Loop.SpinUntilIdle();
        Assert.Equal("object:2:1", fixture.Value("result"));
        Assert.False(provider.Session!.Disposed);
        fixture.Engine.Evaluate("stream.getTracks()[0].stop();");
        Assert.True(provider.Session.Disposed);
    }

    [Fact]
    public void ConfiguredProviderDeniesWhenItCannotOpenADevice()
    {
        using var fixture = new Installed(new FeatureCapabilities { Camera = true }, new TestCaptureProvider(null));
        fixture.Request(false, true);
        Assert.Equal("NotAllowedError", fixture.Value("result"));
    }

    [Fact]
    public void HostMicrophoneSamplesReachAudioOutput()
    {
        var samples = new float[960];
        var provider = new TestCaptureProvider(null, new MediaAudioFrame(48000, 1, samples));
        var output = new TestAudioOutput();
        using var fixture = new Installed(new FeatureCapabilities { Microphone = true, Media = true, Sound = true }, provider, output);
        fixture.Engine.Evaluate("var audio=document.createElement('audio');document.body.appendChild(audio);navigator.mediaDevices.getUserMedia({audio:true}).then(s=>{audio.srcObject=s;audio.play();});");
        fixture.Loop.SpinUntilIdle();
        Assert.NotEmpty(output.Frames);
        Assert.Equal(48000, output.Frames[0].SampleRate);
    }

    [Fact]
    public void HostFrameBytesAreChargedAgainstMediaBudget()
    {
        var provider = new TestCaptureProvider(new MediaCaptureVideoFrame(8, 8, new byte[8 * 8 * 4]));
        using var fixture = new Installed(new FeatureCapabilities { Camera = true, Media = true }, provider, limits: ResourceLimits.Default with { MaxMediaBytes = 64 });
        fixture.Engine.Evaluate("var video=document.createElement('video');document.body.appendChild(video);navigator.mediaDevices.getUserMedia({video:true}).then(s=>{video.srcObject=s;result=video.error;});");
        fixture.Loop.SpinUntilIdle();
        Assert.NotEqual("undefined", fixture.Value("String(result)"));
        Assert.Equal("0", fixture.Value("String(video.readyState)"));
    }

    [Fact]
    public void FrameReadsDoNotRepollHostPermission()
    {
        var provider = new TestCaptureProvider(new MediaCaptureVideoFrame(1, 1, new byte[] { 1, 2, 3, 255 }));
        var permissionCalls = 0;
        using var fixture = new Installed(new FeatureCapabilities { Camera = true, Media = true }, provider, permission: _ => { permissionCalls++; return true; });
        fixture.Request(false, true);
        fixture.Engine.Evaluate("var video=document.createElement('video');document.body.appendChild(video);video.srcObject=stream;");
        var element = fixture.Document.Node.Descendants.OfType<ElementNode>().Single(node => node.LocalName == "video");
        var media = Assert.IsType<MediaElementState>(element.MediaPresentation);
        _ = media.Pixels;
        _ = media.Pixels;
        Assert.Equal(1, permissionCalls);
    }

    [Fact]
    public void HostRevocationEndsTracksWithoutAPropertyPoll()
    {
        var provider = new TestCaptureProvider(new MediaCaptureVideoFrame(1, 1, new byte[] { 1, 2, 3, 255 }));
        using var fixture = new Installed(new FeatureCapabilities { Camera = true }, provider);
        fixture.Request(false, true);
        fixture.Engine.Evaluate("var ended=0;stream.getVideoTracks()[0].onended=()=>ended++;");
        provider.Session!.Revoke();
        Assert.Equal("ended:1", fixture.Value("stream.getVideoTracks()[0].readyState+':'+ended"));
        Assert.Equal(new[] { true, false }, fixture.Activity);
    }

    [Fact]
    public void DeviceChangeListenerCannotReviveApprovalAcrossRevokeAndRegrant()
    {
        using var fixture = new Installed(new FeatureCapabilities { Camera = true });
        var control = new HostObjectTemplate("HostCaptureControl").DefineMethod("revoke", (_, _, _) =>
        {
            DomBindings.UpdateCapabilities(fixture.Engine, FeatureCapabilities.Default);
            DomBindings.UpdateCapabilities(fixture.Engine, new FeatureCapabilities { Camera = true });
            return ScriptValue.Undefined;
        });
        fixture.Engine.SetGlobalProperty("hostControl", fixture.Engine.CreateHostObject(control, new object()));
        fixture.Engine.Evaluate("navigator.mediaDevices.ondevicechange=()=>{navigator.mediaDevices.ondevicechange=null;hostControl.revoke();};");
        fixture.Request(false, true);
        Assert.Equal("NotAllowedError", fixture.Value("result"));
        Assert.Empty(fixture.Activity);
    }

    [Theory]
    [InlineData(false, false, false, true, false, "NotAllowedError")]
    [InlineData(false, false, true, true, true, "NotAllowedError")]
    [InlineData(true, false, false, false, true, "video")]
    [InlineData(false, true, false, true, false, "audio")]
    [InlineData(true, false, true, true, false, "NotAllowedError")]
    [InlineData(false, true, true, false, true, "NotAllowedError")]
    [InlineData(true, true, false, true, true, "audio,video")]
    public void CapturePermissionsAreIndependentOfPeerConnections(bool camera, bool microphone, bool rtc, bool audio, bool video, string expected)
    {
        using var fixture = new Installed(new FeatureCapabilities { Camera = camera, Microphone = microphone, WebRtc = rtc });
        fixture.Request(audio, video);
        Assert.Equal(expected, fixture.Value("result"));
    }

    [Fact]
    public void DenialsAreNativeDeduplicatedAndCannotBeGrantedThroughLegacyGlobals()
    {
        using var fixture = new Installed(FeatureCapabilities.Default);
        fixture.Engine.Evaluate("__folio_webrtc_enabled=true;__folio_media_permission={check:()=>true};__folio_capture_tracks=[];__folio_capture_devices_granted=true;");
        fixture.Request(true, true);
        fixture.Request(true, true);
        Assert.Equal("NotAllowedError", fixture.Value("result"));
        var denials = fixture.Runner.Diagnostics.Where(d => d.Code == DiagnosticCode.CapabilityDenied).ToArray();
        Assert.Equal(new[] { "Camera", "Microphone" }, denials.Select(d => d.Feature).Order().ToArray());
        Assert.Empty(fixture.Activity);
    }

    [Fact]
    public void CameraRevocationEndsOnlyCameraTracksAndRegrantCannotReviveThem()
    {
        using var fixture = new Installed(new FeatureCapabilities { Camera = true, Microphone = true });
        fixture.Request(true, true);
        fixture.Engine.Evaluate("var camera=stream.getVideoTracks()[0],microphone=stream.getAudioTracks()[0],ended=0;camera.onended=()=>ended++;");
        DomBindings.UpdateCapabilities(fixture.Engine, new FeatureCapabilities { Microphone = true });
        Assert.Equal("ended:live:1", fixture.Value("camera.readyState+':'+microphone.readyState+':'+ended"));
        Assert.Equal(new[] { true }, fixture.Activity);
        DomBindings.UpdateCapabilities(fixture.Engine, new FeatureCapabilities { Camera = true, Microphone = true });
        Assert.Equal("ended", fixture.Value("camera.readyState='live';camera.readyState"));
        Assert.Equal("audio", fixture.Value("microphone.getSettings().kind"));
        DomBindings.UpdateCapabilities(fixture.Engine, FeatureCapabilities.Default);
        Assert.Equal(new[] { true, false }, fixture.Activity);
    }

    [Fact]
    public void CallbackCannotApproveCaptureAfterReentrantRevocation()
    {
        using var engine = new JintScriptEngine();
        var options = new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Camera = true },
            MediaCapturePermission = _ => { DomBindings.UpdateCapabilities(engine, FeatureCapabilities.Default); return true; }
        };
        using var document = Document.Parse("<p>capture</p>", options);
        DomBindings.Install(engine, document);
        engine.Evaluate("var result='pending';navigator.mediaDevices.getUserMedia({video:true}).then(()=>result='allowed',e=>result=e.name);");
        engine.PerformMicrotaskCheckpoint();
        Assert.Equal("NotAllowedError", engine.Evaluate("result").AsString());
    }

    [Fact]
    public void RetainedCaptureMethodsCannotReopenAnEndedTrack()
    {
        using var fixture = new Installed(new FeatureCapabilities { Camera = true });
        fixture.Request(false, true);
        fixture.Engine.Evaluate("var camera=stream.getVideoTracks()[0],getSettings=camera.getSettings;");
        DomBindings.UpdateCapabilities(fixture.Engine, FeatureCapabilities.Default);
        DomBindings.UpdateCapabilities(fixture.Engine, new FeatureCapabilities { Camera = true });
        Assert.Equal("ended:undefined", fixture.Value("camera.readyState+':'+getSettings.call(camera).width"));
        fixture.Engine.Evaluate("var constraintsResult='pending';camera.applyConstraints({width:1280}).then(()=>constraintsResult='resolved');");
        fixture.Engine.PerformMicrotaskCheckpoint();
        Assert.Equal("resolved", fixture.Value("constraintsResult"));
        Assert.Equal("ended:undefined", fixture.Value("camera.readyState+':'+camera.getConstraints().width"));
        fixture.Request(false, true);
        Assert.Equal("video", fixture.Value("result"));
    }

    private sealed class Installed : IDisposable
    {
        public JintScriptEngine Engine { get; } = new();
        public Document Document { get; }
        public HtmlEventLoop Loop { get; }
        public ScriptRunner Runner { get; }
        public List<bool> Activity { get; } = [];
        public Installed(FeatureCapabilities capabilities, IMediaCaptureProvider? provider = null, IMediaAudioOutput? output = null, ResourceLimits? limits = null, Func<MediaCaptureRequest, bool>? permission = null)
        {
            var options = new FolioOptions { Capabilities = capabilities, Limits = limits ?? ResourceLimits.Default, MediaCaptureActivityChanged = active => Activity.Add(active), MediaCapturePermission = permission, MediaCaptureProvider = provider, MediaAudioOutput = output };
            Document = Document.Parse("<p>capture</p>", options);
            Loop = new HtmlEventLoop(Engine);
            DomBindings.Install(Engine, Document, eventLoop: Loop);
            Runner = new ScriptRunner(Document, Engine, Loop, options);
        }
        public void Request(bool audio, bool video)
        {
            Engine.Evaluate($"var result='pending';navigator.mediaDevices.getUserMedia({{audio:{audio.ToString().ToLowerInvariant()},video:{video.ToString().ToLowerInvariant()}}}).then(value=>{{globalThis.stream=value;result=value.getTracks().map(t=>t.kind).join(',');}},error=>result=error.name);");
            Loop.SpinUntilIdle();
        }
        public string? Value(string script) => Engine.Evaluate(script).AsString();
        public void Dispose() { Runner.Dispose(); Loop.Dispose(); Engine.Dispose(); Document.Dispose(); }
    }

    private sealed class TestCaptureProvider(MediaCaptureVideoFrame? frame, MediaAudioFrame? audio = null) : IMediaCaptureProvider
    {
        public TestCaptureSession? Session { get; private set; }
        public IMediaCaptureSession? Open(MediaCaptureRequest request) => frame is null && audio is null ? null : Session = new TestCaptureSession(frame, audio);
    }

    private sealed class TestCaptureSession(MediaCaptureVideoFrame? frame, MediaAudioFrame? audio) : IMediaCaptureSession
    {
        public bool Disposed { get; private set; }
        public event Action? Revoked;
        public event Action<MediaCaptureVideoFrame>? VideoFrameAvailable;
        public event Action<MediaAudioFrame>? AudioFrameAvailable;
        public MediaCaptureVideoFrame? Frame { get; set; } = frame;
        public MediaCaptureVideoFrame? CurrentVideoFrame => Disposed ? null : Frame;
        public MediaAudioFrame? CurrentAudioFrame => Disposed ? null : audio;
        public void Dispose() => Disposed = true;
        public void Revoke() => Revoked?.Invoke();
        public void PushVideo(MediaCaptureVideoFrame frame) => VideoFrameAvailable?.Invoke(frame);
        public void PushAudio(MediaAudioFrame frame) => AudioFrameAvailable?.Invoke(frame);
    }

    private sealed class TestAudioOutput : IMediaAudioOutput
    {
        public List<MediaAudioFrame> Frames { get; } = [];
        public void Submit(MediaAudioFrame frame) => Frames.Add(frame);
    }
}
