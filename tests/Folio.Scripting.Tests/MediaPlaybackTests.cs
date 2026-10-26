using Folio.Dom;
using Folio.Imaging;
using Folio.Resources;
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
    public void DecoderReceivesMediaContentTypeFromDataUrl()
    {
        using var session = new Session();
        Assert.Equal("application/octet-stream", session.Decoder.ContentType);
    }

    [Fact]
    public void MediaElementSelectsTheFirstSupportedSourceChild()
    {
        using var engine = new JintScriptEngine();
        var decoder = new Decoder();
        using var document = Document.Parse("<audio id='clip'><source src='data:video/mp4;base64,AQID' type='video/mp4'><source src='data:audio/wav;base64,AQID' type='audio/wav'></audio>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Media = true, Sound = true },
            MediaDecoder = decoder
        });
        DomBindings.Install(engine, document);
        try
        {
            Assert.Equal("audio/wav", decoder.ContentType);
            Assert.True(engine.Evaluate("document.getElementById('clip').readyState === 4 && document.getElementById('clip').currentSrc.startsWith('data:audio/wav')").AsBoolean());
        }
        finally
        {
            DomBindings.Dispose(engine);
            engine.Dispose();
            document.Dispose();
        }
    }

    [Fact]
    public void PlayAndPauseAreIdempotentAndDoNotDuplicateLifecycleEvents()
    {
        using var session = new Session();
        Assert.Equal("prs", session.Engine.Evaluate("let events=''; clip.onplay=()=>events+='p'; clip.onplaying=()=>events+='r'; clip.onpause=()=>events+='s'; clip.play(); clip.play(); clip.pause(); clip.pause(); events").AsString());
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

    [Fact]
    public void RenderingClockAppliesPlaybackRateToMediaPosition()
    {
        using var session = new Session();
        session.Engine.Evaluate("clip.playbackRate = 2; clip.play()");
        session.AdvanceTo(250);
        Assert.Equal(0.5, session.Engine.Evaluate("clip.currentTime").AsNumber(), 6);
        Assert.False(session.Engine.Evaluate("clip.paused").AsBoolean());
    }

    [Fact]
    public void PosterDecodedPixelsAreCountedByTheSharedMediaBudget()
    {
        const string poster = "data:image/svg+xml;base64,PHN2ZyB3aWR0aD0nMicgaGVpZ2h0PScyJz48cmVjdCB3aWR0aD0nMicgaGVpZ2h0PScyJyBmaWxsPScjZmYwMDAwJy8+PC9zdmc+";
        using var engine = new JintScriptEngine();
        using var document = Document.Parse($"<video id='clip' poster='{poster}'></video>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Media = true },
            Limits = new ResourceLimits { MaxMediaBytes = 8, MaxMediaDecoders = 1 }
        });
        DomBindings.Install(engine, document);
        try
        {
            var element = document.Node.Descendants.OfType<Folio.Dom.ElementNode>().Single(node => node.LocalName == "video");
            var media = Assert.IsType<MediaElementState>(element.MediaPresentation);
            Assert.Null(media.Poster);
            Assert.Equal(FallbackReason.ResourceLimit, media.Fallback?.Reason);
        }
        finally
        {
            DomBindings.Dispose(engine);
            engine.Dispose();
            document.Dispose();
        }
    }

    [Fact]
    public void PosterSharingTheStaticMediaSourceIsNotRetainedTwice()
    {
        const string source = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
        using var engine = new JintScriptEngine();
        using var document = Document.Parse($"<video id='clip' src='{source}' poster='{source}'></video>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Media = true },
            // The encoded source plus one decoded pixel fits; retaining a second poster copy would not.
            Limits = new ResourceLimits { MaxMediaBytes = 80, MaxMediaDecoders = 1 }
        });
        DomBindings.Install(engine, document);
        var element = document.Node.Descendants.OfType<Folio.Dom.ElementNode>().Single(node => node.LocalName == "video");
        var media = Assert.IsType<MediaElementState>(element.MediaPresentation);
        Assert.Null(media.Fallback);
        Assert.NotNull(media.Poster);
        Assert.Equal(media.Pixels, media.Poster!.Pixels);
    }

    [Fact]
    public void ExternalPosterUsesImageResourceKind()
    {
        const string posterUrl = "https://media.example/poster.png";
        using var engine = new JintScriptEngine();
        var loader = new PosterImageLoader();
        using var document = Document.Parse($"<video id='clip' poster='{posterUrl}'></video>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Media = true },
            BaseUri = new Uri("https://media.example/page.html"),
            ResourceLoader = loader
        });
        DomBindings.Install(engine, document);
        var element = document.Node.Descendants.OfType<Folio.Dom.ElementNode>().Single(node => node.LocalName == "video");
        var media = Assert.IsType<MediaElementState>(element.MediaPresentation);
        Assert.NotNull(media.Poster);
        Assert.Contains(ResourceKind.Image, loader.Kinds);
        Assert.DoesNotContain(ResourceKind.Media, loader.Kinds);
    }

    [Fact]
    public void CapturedVideoRespectsTheSharedMediaByteBudget()
    {
        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<video id='clip'></video>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Media = true },
            Limits = ResourceLimits.Default with { MaxMediaBytes = 1024 }
        });
        DomBindings.Install(engine, document);
        Assert.True(engine.Evaluate("const clip = document.getElementById('clip'); clip.srcObject = new MediaStream([new MediaStreamTrack('video')]); clip.error !== null && clip.readyState === 0").AsBoolean());
        var element = document.Node.Descendants.OfType<Folio.Dom.ElementNode>().Single(node => node.LocalName == "video");
        var media = Assert.IsType<MediaElementState>(element.MediaPresentation);
        Assert.Equal(FallbackReason.ResourceLimit, media.Fallback?.Reason);
    }

    [Fact]
    public void TimedVideoFramesFollowThePlaybackClock()
    {
        using var engine = new JintScriptEngine();
        var decoder = new TimedDecoder();
        var clock = new VirtualEventLoopClock();
        using var loop = new HtmlEventLoop(engine, clock);
        using var document = Document.Parse("<video id='clip' src='data:video/test;base64,AQID'></video>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Media = true }, MediaDecoder = decoder
        });
        DomBindings.Install(engine, document, eventLoop: loop);
        try
        {
            clock.SetNow(0);
            loop.UpdateTheRendering();
            engine.Evaluate("var clip=document.getElementById('clip'); clip.play()");
            var element = document.Node.Descendants.OfType<ElementNode>().Single(node => node.LocalName == "video");
            var media = Assert.IsType<MediaElementState>(element.MediaPresentation);
            Assert.Equal(255, media.CurrentFrame!.Pixels.Span[0]);
            clock.SetNow(600);
            loop.UpdateTheRendering();
            Assert.Equal(0, media.CurrentFrame!.Pixels.Span[0]);
            Assert.Equal(255, media.CurrentFrame.Pixels.Span[2]);
        }
        finally
        {
            DomBindings.Dispose(engine);
            engine.Dispose();
            document.Dispose();
        }
    }

    [Fact]
    public void RetainedVideoFrameSequenceCountsAgainstTheSharedBudget()
    {
        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<video id='first' src='data:video/test;base64,AQID'></video><video id='second' src='data:video/test;base64,AQID'></video>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Media = true },
            Limits = ResourceLimits.Default with { MaxMediaBytes = 14, MaxMediaDecoders = 2 },
            MediaDecoder = new TimedDecoder()
        });
        DomBindings.Install(engine, document);
        try
        {
            var media = document.Node.Descendants.OfType<ElementNode>().Where(node => node.LocalName == "video")
                .Select(node => Assert.IsType<MediaElementState>(node.MediaPresentation)).ToArray();
            Assert.Null(media[0].Fallback);
            Assert.Equal(FallbackReason.ResourceLimit, media[1].Fallback?.Reason);
        }
        finally
        {
            DomBindings.Dispose(engine);
            engine.Dispose();
            document.Dispose();
        }
    }

    [Fact]
    public void AudioAndVideoAdvanceFromTheSamePlaybackClock()
    {
        using var engine = new JintScriptEngine();
        var decoder = new SynchronizedDecoder();
        var output = new Output();
        var clock = new VirtualEventLoopClock();
        using var loop = new HtmlEventLoop(engine, clock);
        using var document = Document.Parse("<video id='clip' src='data:video/test;base64,AQID'></video>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Media = true, Sound = true },
            MediaDecoder = decoder,
            MediaAudioOutput = output
        });
        DomBindings.Install(engine, document, eventLoop: loop);
        try
        {
            clock.SetNow(0);
            loop.UpdateTheRendering();
            engine.Evaluate("document.getElementById('clip').play()");
            var element = document.Node.Descendants.OfType<ElementNode>().Single(node => node.LocalName == "video");
            var media = Assert.IsType<MediaElementState>(element.MediaPresentation);
            Assert.Equal(160, output.Frames.Sum(frame => frame.Samples.Length));

            clock.SetNow(600);
            loop.UpdateTheRendering();

            Assert.Equal(.6, media.CurrentTime, 6);
            Assert.Equal(4800, output.Frames.Sum(frame => frame.Samples.Length));
            Assert.Equal(0, media.CurrentFrame!.Pixels.Span[0]);
            Assert.Equal(255, media.CurrentFrame.Pixels.Span[2]);
        }
        finally
        {
            DomBindings.Dispose(engine);
            engine.Dispose();
            document.Dispose();
        }
    }

    private sealed class Decoder : IMediaDecoder
    {
        public string? ContentType { get; private set; }
        public MediaDecodeResult? Decode(ReadOnlyMemory<byte> data, string? type, bool video, int budget)
        {
            ContentType = type;
            return new(1, null, new MediaAudioFrame(8000, 1, Enumerable.Range(0, 8000).Select(index => index / 8000f).ToArray()));
        }
    }

    private sealed class TimedDecoder : IMediaDecoder
    {
        public MediaDecodeResult? Decode(ReadOnlyMemory<byte> data, string? type, bool video, int budget)
        {
            var red = new TestImage([255, 0, 0, 255]);
            var blue = new TestImage([0, 0, 255, 255]);
            return new MediaDecodeResult(1, red, null)
            {
                VideoFrames = [new MediaVideoFrame(0, red), new MediaVideoFrame(.5, blue)]
            };
        }
    }

    private sealed class SynchronizedDecoder : IMediaDecoder
    {
        public MediaDecodeResult? Decode(ReadOnlyMemory<byte> data, string? type, bool video, int budget)
        {
            var red = new TestImage([255, 0, 0, 255]);
            var blue = new TestImage([0, 0, 255, 255]);
            return new MediaDecodeResult(1, red, new MediaAudioFrame(8000, 1,
                Enumerable.Range(0, 8000).Select(index => index / 8000f).ToArray()))
            {
                VideoFrames = [new MediaVideoFrame(0, red), new MediaVideoFrame(.5, blue)]
            };
        }
    }

    private sealed class TestImage(byte[] pixels) : IImageHandle
    {
        public int Width => 1;
        public int Height => 1;
        public ReadOnlyMemory<byte> Pixels => pixels;
    }

    private sealed class PosterImageLoader : IResourceLoader
    {
        private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==");
        public List<ResourceKind> Kinds { get; } = [];
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken = default)
        {
            Kinds.Add(request.Kind);
            return Task.FromResult<ResourceResponse?>(request.Kind == ResourceKind.Image
                ? ResourceResponse.Loaded(Png, "image/png")
                : ResourceResponse.Refused("wrong resource kind"));
        }
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
        public Decoder Decoder { get; } = new();
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
                MediaDecoder = Decoder, MediaAudioOutput = Output
            });
            DomBindings.Install(Engine, _document, eventLoop: _loop);
            Engine.Evaluate("var clip = document.getElementById('clip')");
            AdvanceTo(0);
        }
        public void AdvanceTo(double milliseconds) { _clock.SetNow(milliseconds); _loop.UpdateTheRendering(); }
        public void Dispose() { DomBindings.Dispose(Engine); _loop.Dispose(); Engine.Dispose(); _document.Dispose(); }
    }
}
