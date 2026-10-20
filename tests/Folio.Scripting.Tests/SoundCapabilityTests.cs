using Folio.Dom;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

public sealed class SoundCapabilityTests
{
    [Fact]
    public void DeniedSoundProducesNoFramesOrLifecycleStart()
    {
        using var session = new Session(sound: false);
        Assert.True(session.Media.Play());
        session.Media.AdvancePlayback(0.1);
        Assert.Empty(session.Output.Frames);
        Assert.DoesNotContain("start", session.Output.Events);
    }

    [Fact]
    public void RevocationFlushesImmediatelyAndRetainedPlaybackCannotResume()
    {
        using var session = new Session(sound: true);
        Assert.True(session.Media.Play());
        Assert.NotEmpty(session.Output.Frames);
        session.Output.Frames.Clear();
        session.Media.SetSoundCapability(false);
        var eventCount = session.Output.Events.Count;
        session.Media.AdvancePlayback(0.1);
        Assert.Empty(session.Output.Frames);
        Assert.Contains("flush", session.Output.Events);
        Assert.Contains("stop", session.Output.Events);
        Assert.True(session.Output.Events.Count >= eventCount);
        session.Media.SetSoundCapability(true);
        session.Media.AdvancePlayback(0.1);
        Assert.Empty(session.Output.Frames);
        Assert.True(session.Media.Play());
        session.Media.AdvancePlayback(0.02);
        Assert.NotEmpty(session.Output.Frames);
    }

    [Fact]
    public void MutedAndZeroVolumeRemainSilentWithSoundGranted()
    {
        using var session = new Session(sound: true);
        session.Media.SetMuted(true);
        Assert.True(session.Media.Play());
        session.Media.AdvancePlayback(0.1);
        session.Media.SetMuted(false);
        session.Media.SetVolume(0);
        session.Media.AdvancePlayback(0.1);
        Assert.Empty(session.Output.Frames);
    }

    [Fact]
    public void AutoplayPolicyDoesNotGrantSoundPermission()
    {
        using var session = new Session(sound: false, allowAutoplay: true);
        Assert.True(session.Media.Play(autoplay: true));
        Assert.Empty(session.Output.Frames);
    }

    [Fact]
    public void LivePermissionCallbackRevocationStopsPlayback()
    {
        var allowed = true;
        using var session = new Session(sound: true, check: () => allowed);
        Assert.True(session.Media.Play());
        session.Output.Frames.Clear();
        allowed = false;
        session.Media.AdvancePlayback(0.1);
        Assert.Empty(session.Output.Frames);
        Assert.Contains("stop", session.Output.Events);
    }

    [Fact]
    public void ReentrantRevocationDuringSubmissionStopsBeforeTheNextChunk()
    {
        using var session = new Session(sound: true);
        Assert.True(session.Media.Play());
        session.Output.Frames.Clear();
        session.Output.OnSubmit = () => session.Media.SetSoundCapability(false);
        session.Media.AdvancePlayback(0.2);
        Assert.Single(session.Output.Frames);
        Assert.Contains("stop", session.Output.Events);
    }

    private sealed class Session : IDisposable
    {
        private readonly JintScriptEngine _engine = new();
        private readonly Document _document;
        public Output Output { get; } = new();
        public MediaElementState Media { get; }

        public Session(bool sound, bool allowAutoplay = false, Func<bool>? check = null)
        {
            _document = Document.Parse("<audio id='media' src='data:audio/wav;base64,AQID'></audio>", new FolioOptions());
            var element = _document.Node.Descendants.OfType<ElementNode>().Single(item => item.LocalName == "audio");
            Media = new MediaElementState(_engine, new HostObjectTemplate("MediaEvent"), element, enabled: true,
                allowSoundAutoplay: allowAutoplay, decoder: new Decoder(), audioOutput: Output,
                soundEnabled: sound, checkSoundPermission: check);
        }

        public void Dispose()
        {
            Media.StopPlayback();
            _engine.Dispose();
            _document.Dispose();
        }
    }

    private sealed class Decoder : IMediaDecoder
    {
        public MediaDecodeResult? Decode(ReadOnlyMemory<byte> data, string? contentType, bool video, int maxOutputBytes) =>
            new(1, null, new MediaAudioFrame(8000, 1, Enumerable.Repeat(0.25f, 8000).ToArray()));
    }

    private sealed class Output : IMediaAudioOutput, IMediaAudioOutputLifecycle
    {
        public List<MediaAudioFrame> Frames { get; } = [];
        public List<string> Events { get; } = [];
        public Action? OnSubmit { get; set; }
        public void Submit(MediaAudioFrame frame) { Frames.Add(frame); OnSubmit?.Invoke(); }
        public void Start() => Events.Add("start");
        public void Pause() => Events.Add("pause");
        public void Flush() => Events.Add("flush");
        public void Stop() => Events.Add("stop");
    }
}
