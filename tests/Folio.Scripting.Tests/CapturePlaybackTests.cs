using Folio.Dom;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;
using Folio.Scripting.Native;

namespace Folio.Scripting.Tests;

public sealed class CapturePlaybackTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DelayedFirstBlockAndSubsequentBlocksPlayOnceWithoutFiniteDuration(bool native)
    {
        using var fixture = new Installed(native);
        fixture.Open("audio.srcObject=stream;audio.play();");
        var media = fixture.Media("a");
        Assert.Empty(fixture.Output.Frames);
        Assert.Equal(0, media.Duration);
        fixture.Session.PushAudio(Block(.25f, 1));
        media.AdvancePlayback(.02);
        fixture.Session.PushAudio(Block(.5f, 2));
        media.AdvancePlayback(.02);
        media.AdvancePlayback(.02);
        Assert.Equal(2, fixture.Output.Frames.Count);
        Assert.Equal(.25f, fixture.Output.Frames[0].Samples.Span[0]);
        Assert.Equal(.5f, fixture.Output.Frames[1].Samples.Span[0]);
        Assert.Equal(.06, media.CurrentTime, 6);
        Assert.False(media.Ended);
        Assert.Equal(1, fixture.Output.Starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RehydratedCaptureSequenceDoesNotReplayEvenAfterDisableOrLoad(bool native)
    {
        using var fixture = new Installed(native);
        fixture.Open("audio.srcObject=stream;audio.play();");
        var media = fixture.Media("a");
        fixture.Session.PushAudio(Block(.1f, 1));
        media.AdvancePlayback(.02);
        fixture.Session.PushAudio(Block(.1f, 1)); // distinct immutable array, same device block
        media.AdvancePlayback(.02);
        fixture.Engine.Evaluate("stream.getAudioTracks()[0].enabled=false;stream.getAudioTracks()[0].enabled=true;audio.load();audio.play();");
        fixture.Session.PushAudio(Block(.1f, 1));
        media.AdvancePlayback(.02);
        Assert.Single(fixture.Output.Frames);
        fixture.Session.PushAudio(Block(.2f, 2));
        media.AdvancePlayback(.02);
        Assert.Equal(2, fixture.Output.Frames.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoConsumersShareOneBudgetedHistoryWithoutDestructiveReads(bool native)
    {
        // Exactly one 20-sample block plus metadata. Per-consumer copies would exhaust it.
        using var fixture = new Installed(native, maxBytes: 144);
        fixture.Open("audio.srcObject=stream;other.srcObject=stream;audio.play();other.play();");
        fixture.Session.PushAudio(Block(.4f, 1));
        fixture.Media("a").AdvancePlayback(.02);
        fixture.Media("b").AdvancePlayback(.02);
        Assert.Equal(2, fixture.Output.Frames.Count);
        Assert.All(fixture.Output.Frames, frame => Assert.All(frame.Samples.ToArray(), value => Assert.Equal(.4f, value)));
        fixture.Media("a").AdvancePlayback(.02);
        fixture.Media("b").AdvancePlayback(.02);
        Assert.Equal(2, fixture.Output.Frames.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MutedConsumerDropsItsHistoryWhileOtherConsumerContinues(bool native)
    {
        using var fixture = new Installed(native);
        fixture.Open("audio.srcObject=stream;other.srcObject=stream;audio.play();other.play();audio.muted=true;");
        fixture.Session.PushAudio(Block(.1f, 1));
        fixture.Media("a").AdvancePlayback(.02);
        fixture.Media("b").AdvancePlayback(.02);
        fixture.Engine.Evaluate("audio.muted=false;");
        fixture.Media("a").AdvancePlayback(.02);
        Assert.Single(fixture.Output.Frames);
        fixture.Session.PushAudio(Block(.2f, 2));
        fixture.Media("a").AdvancePlayback(.02);
        fixture.Media("b").AdvancePlayback(.02);
        Assert.Equal(3, fixture.Output.Frames.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TrackDisableDropsQueuedAndDisabledSamplesIncludingBetweenTicks(bool native)
    {
        using var fixture = new Installed(native);
        fixture.Open("audio.srcObject=stream;audio.play();");
        var media = fixture.Media("a");
        fixture.Session.PushAudio(Block(.1f, 1));
        fixture.Engine.Evaluate("stream.getAudioTracks()[0].enabled=false;");
        fixture.Session.PushAudio(Block(.2f, 2));
        fixture.Engine.Evaluate("stream.getAudioTracks()[0].enabled=true;");
        fixture.Session.PushAudio(Block(.2f, 2)); // transport repeat of a disabled block
        media.AdvancePlayback(.02);
        Assert.Empty(fixture.Output.Frames);
        fixture.Session.PushAudio(Block(.3f, 3));
        media.AdvancePlayback(.02);
        Assert.Single(fixture.Output.Frames);
        Assert.Equal(.3f, fixture.Output.Frames[0].Samples.Span[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StopAndRevokeSilencePendingAndLateDeviceCallbacks(bool native)
    {
        using var fixture = new Installed(native);
        fixture.Open("audio.srcObject=stream;audio.play();");
        var media = fixture.Media("a");
        fixture.Session.PushAudio(Block(.1f, 1));
        var late = fixture.Session.AudioCallbacks;
        fixture.Engine.Evaluate("stream.getAudioTracks()[0].stop();");
        late?.Invoke(Block(.2f, 2));
        media.AdvancePlayback(.02);
        Assert.Empty(fixture.Output.Frames);
        fixture.Open("audio.srcObject=stream;audio.play();");
        fixture.Session.PushAudio(Block(.3f, 1));
        fixture.Session.Revoke();
        media.AdvancePlayback(.02);
        Assert.Empty(fixture.Output.Frames);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PauseAndVolumeZeroDropBacklogAndResumeWithFreshAudio(bool native)
    {
        using var fixture = new Installed(native);
        fixture.Open("audio.srcObject=stream;audio.play();audio.pause();");
        var media = fixture.Media("a");
        fixture.Session.PushAudio(Block(.1f, 1));
        fixture.Engine.Evaluate("audio.play();");
        media.AdvancePlayback(.02);
        Assert.Empty(fixture.Output.Frames);
        fixture.Engine.Evaluate("audio.volume=0;");
        fixture.Session.PushAudio(Block(.2f, 2));
        media.AdvancePlayback(.02);
        fixture.Engine.Evaluate("audio.volume=0.5;");
        fixture.Session.PushAudio(Block(.4f, 3));
        media.AdvancePlayback(.02);
        Assert.Single(fixture.Output.Frames);
        Assert.All(fixture.Output.Frames[0].Samples.ToArray(), sample => Assert.Equal(.2f, sample));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisabledAndStoppedVideoIsOpaqueBlackAndMissingRealFrameHasNoGradient(bool native)
    {
        using var fixture = new Installed(native);
        fixture.Open("video.srcObject=stream;", video: true);
        var media = fixture.Media("v");
        AssertBlack(media.Pixels);
        fixture.Session.PushVideo(new(1, 1, new byte[] { 9, 8, 7, 255 }));
        Assert.Equal(new byte[] { 9, 8, 7, 255 }, media.Pixels.ToArray());
        fixture.Engine.Evaluate("stream.getVideoTracks()[0].enabled=false;");
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, media.Pixels.ToArray());
        fixture.Engine.Evaluate("stream.getVideoTracks()[0].enabled=true;");
        Assert.Equal(new byte[] { 9, 8, 7, 255 }, media.Pixels.ToArray());
        fixture.Engine.Evaluate("stream.getVideoTracks()[0].stop();");
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, media.Pixels.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolutionGrowthRechecksBudgetAndDoesNotExposeUnreservedPixels(bool native)
    {
        using var fixture = new Installed(native, maxBytes: 32, initialVideo: new(1, 1, new byte[] { 1, 2, 3, 255 }));
        fixture.Open("video.srcObject=stream;", video: true);
        var media = fixture.Media("v");
        Assert.Equal(4, media.Pixels.Length);
        fixture.Session.PushVideo(new(8, 8, new byte[256]));
        Assert.Empty(media.Pixels.ToArray());
        Assert.NotNull(media.Error);
        Assert.Equal(FallbackReason.ResourceLimit, media.Fallback?.Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SoundRevokeDropsBacklogAndRequiresPlayToRearmOutput(bool native)
    {
        using var fixture = new Installed(native);
        fixture.Open("audio.srcObject=stream;audio.play();");
        var media = fixture.Media("a");
        fixture.Session.PushAudio(Block(.1f, 1));
        media.AdvancePlayback(.02);
        DomBindings.UpdateCapabilities(fixture.Engine, new FeatureCapabilities { Media = true, Microphone = true });
        fixture.Session.PushAudio(Block(.2f, 2));
        media.AdvancePlayback(.02);
        DomBindings.UpdateCapabilities(fixture.Engine, new FeatureCapabilities { Media = true, Microphone = true, Sound = true });
        fixture.Session.PushAudio(Block(.3f, 3));
        media.AdvancePlayback(.02);
        Assert.Single(fixture.Output.Frames);
        fixture.Engine.Evaluate("audio.play();");
        fixture.Session.PushAudio(Block(.4f, 4));
        media.AdvancePlayback(.02);
        Assert.Equal(2, fixture.Output.Frames.Count);
        Assert.Equal(.4f, fixture.Output.Frames[^1].Samples.Span[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DetachAndDocumentRetirementReleaseReadersAndRejectLateCallbacks(bool native)
    {
        using var fixture = new Installed(native);
        fixture.Open("audio.srcObject=stream;audio.play();");
        var media = fixture.Media("a");
        var late = fixture.Session.AudioCallbacks;
        fixture.Session.PushAudio(Block(.1f, 1));
        fixture.Engine.Evaluate("audio.srcObject=null;");
        media.AdvancePlayback(.02);
        Assert.Empty(fixture.Output.Frames);
        fixture.Engine.Evaluate("audio.srcObject=stream;audio.play();");
        DomBindings.Dispose(fixture.Engine);
        late?.Invoke(Block(.2f, 2));
        media.AdvancePlayback(.02);
        // Reattachment may play the still-fresh initial block once; retirement adds none.
        Assert.Single(fixture.Output.Frames);
    }

    private static MediaAudioFrame Block(float value, long sequence) =>
        new(1000, 1, Enumerable.Repeat(value, 20).ToArray()) { CaptureSequence = sequence };

    private static void AssertBlack(ReadOnlyMemory<byte> memory)
    {
        Assert.NotEmpty(memory.ToArray());
        var pixels = memory.Span;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            Assert.Equal(0, pixels[i]);
            Assert.Equal(0, pixels[i + 1]);
            Assert.Equal(0, pixels[i + 2]);
            Assert.Equal(255, pixels[i + 3]);
        }
    }

    private sealed class Installed : IDisposable
    {
        internal IScriptEngine Engine { get; }
        private readonly HtmlEventLoop _loop;
        private readonly Document _document;
        private readonly Provider _provider;
        internal Output Output { get; } = new();
        internal Session Session => _provider.Latest!;
        internal Installed(bool native, int maxBytes = 4 * 1024 * 1024, MediaCaptureVideoFrame? initialVideo = null)
        {
            Engine = native ? new NativeJavaScriptEngine() : new JintScriptEngine();
            _provider = new Provider(initialVideo);
            _document = Document.Parse("<body><audio id='a'></audio><audio id='b'></audio><video id='v'></video></body>", new FolioOptions
            {
                Capabilities = new FeatureCapabilities { Media = true, Sound = true, Camera = true, Microphone = true },
                Limits = ResourceLimits.Default with { MaxMediaBytes = maxBytes },
                MediaCaptureProvider = _provider, MediaAudioOutput = Output
            });
            _loop = new HtmlEventLoop(Engine);
            DomBindings.Install(Engine, _document, eventLoop: _loop);
            Engine.Evaluate("var audio=document.getElementById('a'),other=document.getElementById('b'),video=document.getElementById('v'),stream;");
        }
        internal void Open(string script, bool video = false)
        {
            Engine.Evaluate("navigator.mediaDevices.getUserMedia({" + (video ? "video" : "audio") + ":true}).then(s=>{stream=s;" + script + "});");
            _loop.SpinUntilIdle();
        }
        internal MediaElementState Media(string id) => Assert.IsType<MediaElementState>(_document.GetElementById(id)!.Node.MediaPresentation);
        public void Dispose() { DomBindings.Dispose(Engine); _loop.Dispose(); Engine.Dispose(); _document.Dispose(); }
    }

    private sealed class Provider(MediaCaptureVideoFrame? initialVideo) : IMediaCaptureProvider
    {
        internal Session? Latest;
        public IMediaCaptureSession Open(MediaCaptureRequest request) => Latest = new Session { CurrentVideoFrame = initialVideo };
    }
    private sealed class Session : IMediaCaptureSession
    {
        public MediaCaptureVideoFrame? CurrentVideoFrame { get; set; }
        public MediaAudioFrame? CurrentAudioFrame { get; private set; }
        public event Action<MediaCaptureVideoFrame>? VideoFrameAvailable;
        public event Action<MediaAudioFrame>? AudioFrameAvailable;
        public event Action? Revoked;
        internal Action<MediaAudioFrame>? AudioCallbacks => AudioFrameAvailable;
        internal void PushAudio(MediaAudioFrame frame) { CurrentAudioFrame = frame; AudioFrameAvailable?.Invoke(frame); }
        internal void PushVideo(MediaCaptureVideoFrame frame) { CurrentVideoFrame = frame; VideoFrameAvailable?.Invoke(frame); }
        internal void Revoke() => Revoked?.Invoke();
        public void Dispose() { }
    }
    private sealed class Output : IMediaAudioOutput, IMediaAudioOutputLifecycle
    {
        internal List<MediaAudioFrame> Frames { get; } = [];
        internal int Starts;
        public void Submit(MediaAudioFrame frame) => Frames.Add(frame);
        public void Start() => Starts++;
        public void Pause() { }
        public void Flush() { }
        public void Stop() { }
    }
}
