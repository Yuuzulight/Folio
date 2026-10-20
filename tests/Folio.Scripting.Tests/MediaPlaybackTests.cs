using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

public sealed class MediaPlaybackTests
{
    [Fact]
    public void MutedPlaybackSubmitsNothingAndUnmuteUsesCurrentPositionAndVolume()
    {
        using var session = new Session();
        session.Engine.Evaluate("clip.muted = true; clip.play()");
        session.AdvanceTo(100);
        Assert.Empty(session.Output.Frames);
        session.Engine.Evaluate("clip.volume = 0.5; clip.muted = false");
        session.AdvanceTo(120);
        var frame = Assert.Single(session.Output.Frames);
        Assert.Equal(0.05f, frame.Samples.Span[0], 5);
        session.Engine.Evaluate("clip.volume = 0");
        session.AdvanceTo(140);
        Assert.Single(session.Output.Frames);
    }

    [Fact]
    public void ScriptSeekFlushesQueuedAudioAndMovesCursorInBothDirections()
    {
        using var session = new Session();
        session.Engine.Evaluate("clip.play(); clip.currentTime = 0.5");
        session.Output.Frames.Clear();
        session.AdvanceTo(20);
        Assert.Equal(0.5f, session.Output.Frames[0].Samples.Span[0], 5);
        session.Engine.Evaluate("clip.currentTime = 0.1");
        session.Output.Frames.Clear();
        session.AdvanceTo(40);
        Assert.Equal(0.1f, session.Output.Frames[0].Samples.Span[0], 5);
        session.Engine.Evaluate("clip.fastSeek(0)");
        session.Output.Frames.Clear();
        session.AdvanceTo(60);
        Assert.Equal(0, session.Output.Frames[0].Samples.Span[0]);
        Assert.True(session.Output.Events.Count(item => item == "flush") >= 3);
    }

    [Fact]
    public void EndOfStreamDrainsFinalSamplesReplayRewindsAndTeardownStopsOutput()
    {
        var session = new Session();
        session.Engine.Evaluate("clip.play()");
        session.AdvanceTo(1000);
        Assert.Equal(8000, session.Output.Frames.Sum(frame => frame.Samples.Length));
        Assert.DoesNotContain("stop", session.Output.Events);
        Assert.True(session.Engine.Evaluate("clip.ended && clip.paused").AsBoolean());
        session.Engine.Evaluate("clip.play()");
        Assert.Equal(0, session.Output.Frames[^1].Samples.Span[0]);
        session.Dispose();
        Assert.Equal("stop", session.Output.Events[^1]);
    }

    [Fact]
    public void VideoWithDecodedAudioStartsTheSameOutputLifecycle()
    {
        using var session = new Session(video: true);
        session.Engine.Evaluate("clip.play()");
        Assert.Equal("start", session.Output.Events[^1]);
        Assert.Single(session.Output.Frames);
    }

    [Fact]
    public void LoopKeepsQueuedTailAndNonFinitePositionsOrVolumesAreRejected()
    {
        using var session = new Session();
        session.Engine.Evaluate("clip.loop = true; clip.play()");
        session.Output.Events.Clear();
        session.AdvanceTo(1000);
        Assert.DoesNotContain("flush", session.Output.Events);
        Assert.DoesNotContain("stop", session.Output.Events);
        Assert.Equal(8160, session.Output.Frames.Sum(frame => frame.Samples.Length));
        Assert.Equal(0, session.Output.Frames[^1].Samples.Span[0]);
        Assert.ThrowsAny<Exception>(() => session.Engine.Evaluate("clip.currentTime = Infinity"));
        Assert.ThrowsAny<Exception>(() => session.Engine.Evaluate("clip.volume = NaN"));
    }

    private sealed class Decoder : IMediaDecoder
    {
        public MediaDecodeResult? Decode(ReadOnlyMemory<byte> data, string? type, bool video, int budget) =>
            new(1, null, new MediaAudioFrame(8000, 1, Enumerable.Range(0, 8000).Select(index => index / 8000f).ToArray()));
    }

    private sealed class Output : IMediaAudioOutput, IMediaAudioOutputLifecycle
    {
        public List<MediaAudioFrame> Frames { get; } = [];
        public List<string> Events { get; } = [];
        public void Submit(MediaAudioFrame frame) => Frames.Add(frame);
        public void Start() => Events.Add("start");
        public void Pause() => Events.Add("pause");
        public void Flush() => Events.Add("flush");
        public void Stop() => Events.Add("stop");
    }

    private sealed class Session : IDisposable
    {
        public JintScriptEngine Engine { get; } = new();
        public Output Output { get; } = new();
        private readonly VirtualEventLoopClock _clock = new();
        private readonly HtmlEventLoop _loop;
        private readonly Document _document;
        public Session(bool video = false)
        {
            _loop = new HtmlEventLoop(Engine, _clock);
            var tag = video ? "video" : "audio";
            _document = Document.Parse($"<{tag} id='clip' src='data:application/octet-stream;base64,AQID'></{tag}>", new FolioOptions
            {
                Capabilities = new FeatureCapabilities { Media = true, Sound = true },
                MediaDecoder = new Decoder(), MediaAudioOutput = Output
            });
            DomBindings.Install(Engine, _document, eventLoop: _loop);
            Engine.Evaluate("var clip = document.getElementById('clip')");
            AdvanceTo(0);
        }
        public void AdvanceTo(double milliseconds) { _clock.SetNow(milliseconds); _loop.UpdateTheRendering(); }
        public void Dispose() { DomBindings.Dispose(Engine); _loop.Dispose(); Engine.Dispose(); _document.Dispose(); }
    }
}
