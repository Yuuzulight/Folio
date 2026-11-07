using Folio.Scripting.Dom;
using Folio.Scripting.Jint;
using Folio.Scripting.Native;

namespace Folio.Scripting.Tests;

public sealed class CaptureDeviceLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BackgroundRevocationRunsOnTheDocumentLoopAndUnsubscribes(bool native)
    {
        var provider = new Provider();
        var callbacks = new List<int>();
        using IScriptEngine engine = native ? new NativeJavaScriptEngine() : new JintScriptEngine();
        using var document = Document.Parse("<body></body>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Camera = true }, MediaCaptureProvider = provider,
            MediaCaptureActivityChanged = _ => callbacks.Add(Environment.CurrentManagedThreadId)
        });
        using var loop = new HtmlEventLoop(engine);
        DomBindings.Install(engine, document, eventLoop: loop);
        engine.Evaluate("var stream;var ended=0;navigator.mediaDevices.getUserMedia({video:true}).then(s=>{stream=s;s.getTracks()[0].onended=()=>ended++;});");
        loop.SpinUntilIdle();
        Assert.Single(callbacks);
        // Revoke from a dedicated "device" thread, as native capture callbacks do. A pool
        // thread could be the very thread that owns the document (xUnit runs tests on the
        // pool), which would legitimately take the inline owner-thread path.
        var device = new Thread(provider.Session.Revoke) { IsBackground = true };
        device.Start();
        device.Join();
        Assert.Single(callbacks);
        var owner = Environment.CurrentManagedThreadId;
        loop.SpinUntilIdle();
        Assert.Equal(1, engine.Evaluate("ended").AsNumber());
        Assert.Equal(owner, callbacks[^1]);
        Assert.Equal(1, provider.Session.Disposals);
        Assert.Equal(0, provider.Session.Subscribers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StoppingCameraKeepsMicrophoneAndClosingReleasesEverySubscription(bool native)
    {
        var provider = new Provider();
        using IScriptEngine engine = native ? new NativeJavaScriptEngine() : new JintScriptEngine();
        using var document = Document.Parse("<body></body>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Camera = true, Microphone = true }, MediaCaptureProvider = provider
        });
        using var loop = new HtmlEventLoop(engine);
        DomBindings.Install(engine, document, eventLoop: loop);
        engine.Evaluate("var stream;navigator.mediaDevices.getUserMedia({video:true,audio:true}).then(s=>stream=s);");
        loop.SpinUntilIdle();
        engine.Evaluate("stream.getVideoTracks()[0].stop();");
        Assert.Equal(1, provider.Session.VideoStops);
        Assert.Equal(0, provider.Session.AudioStops);
        Assert.Equal(0, provider.Session.Disposals);
        engine.Evaluate("stream.getAudioTracks()[0].stop();");
        Assert.Equal(1, provider.Session.AudioStops);
        Assert.Equal(1, provider.Session.Disposals);
        Assert.Equal(0, provider.Session.Subscribers);
        provider.Session.Revoke();
        Assert.Equal(1, provider.Session.Disposals);
    }

    [Fact]
    public void InvalidDeviceFrameDimensionsDoNotOverflowValidation() =>
        Assert.False(new MediaCaptureVideoFrame(int.MaxValue, int.MaxValue, ReadOnlyMemory<byte>.Empty).IsValid);

    private sealed class Provider : IMediaCaptureProvider
    {
        internal Session Session { get; } = new();
        public IMediaCaptureSession Open(MediaCaptureRequest request) => Session;
    }

    private sealed class Session : IMediaCaptureSession, IMediaCaptureTrackControl
    {
        internal int Disposals, AudioStops, VideoStops;
        public MediaCaptureVideoFrame? CurrentVideoFrame => null;
        public MediaAudioFrame? CurrentAudioFrame => null;
        public event Action<MediaCaptureVideoFrame>? VideoFrameAvailable;
        public event Action<MediaAudioFrame>? AudioFrameAvailable;
        public event Action? Revoked;
        internal int Subscribers => (VideoFrameAvailable?.GetInvocationList().Length ?? 0)
            + (AudioFrameAvailable?.GetInvocationList().Length ?? 0) + (Revoked?.GetInvocationList().Length ?? 0);
        public void StopAudio() => AudioStops++;
        public void StopVideo() => VideoStops++;
        public void Dispose() => Disposals++;
        internal void Revoke() => Revoked?.Invoke();
    }
}
