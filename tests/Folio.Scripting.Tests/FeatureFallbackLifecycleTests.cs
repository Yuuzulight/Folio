using Folio.Dom;
using Folio.Hosting;
using Folio.Resources;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

public sealed class FeatureFallbackLifecycleTests
{
    [Fact]
    public void MediaAttributeChangesReloadSourceAndPoster()
    {
        var decoder = new AudioDecoder();
        var firstPoster = "data:image/svg+xml," + Uri.EscapeDataString("<svg xmlns='http://www.w3.org/2000/svg' width='10' height='8'><rect width='10' height='8' fill='#000000'/></svg>");
        var secondPoster = "data:image/svg+xml," + Uri.EscapeDataString("<svg xmlns='http://www.w3.org/2000/svg' width='21' height='13'><rect width='21' height='13' fill='#000000'/></svg>");
        using var document = Document.Parse($"<video id='v' src='data:application/octet-stream,first' poster='{firstPoster}'></video>",
            new FolioOptions { Capabilities = new FeatureCapabilities { Media = true, Sound = true }, MediaDecoder = decoder });
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        var media = Assert.IsType<MediaElementState>(document.GetElementById("v")!.Node.MediaPresentation);
        Assert.Null(media.Fallback);

        engine.Evaluate("var v=document.getElementById('v'); v.setAttribute('src', 'data:application/octet-stream,second'); v.currentTime=.5; v.play(); v.setAttribute('poster', '" + secondPoster + "')");
        Assert.Null(media.Fallback);
        Assert.Equal((21, 13), (media.Poster!.Width, media.Poster.Height));
        Assert.Equal(2, decoder.Calls);
        Assert.True(engine.Evaluate("v.currentTime === .5 && !v.paused").AsBoolean());
        engine.Evaluate("v.poster='" + firstPoster + "'");
        Assert.Equal((10, 8), (media.Poster!.Width, media.Poster.Height));
        Assert.Equal(2, decoder.Calls);
        Assert.True(engine.Evaluate("v.currentTime === .5 && !v.paused").AsBoolean());

        engine.Evaluate("v.removeAttribute('src'); v.removeAttribute('poster')");
        Assert.Null(media.Fallback);
        Assert.Null(media.Poster);
        Assert.True(engine.Evaluate("v.error === null && v.readyState === 0 && v.networkState === 0 && v.paused").AsBoolean());
    }

    [Fact]
    public void RevocationFromResourceLoaderCannotCommitMedia()
    {
        using var engine = new JintScriptEngine();
        var loader = new CallbackLoader(() => DomBindings.UpdateCapabilities(engine, new FeatureCapabilities { Sound = true }));
        using var document = Document.Parse("<audio id='a' src='https://example.test/a'></audio>",
            new FolioOptions { Capabilities = new FeatureCapabilities { Media = true, Sound = true }, ResourceLoader = loader, MediaDecoder = new AudioDecoder() });
        DomBindings.Install(engine, document);
        var media = Assert.IsType<MediaElementState>(document.GetElementById("a")!.Node.MediaPresentation);
        Assert.Equal(new FeatureFallback(FallbackFeature.Audio, FallbackReason.HostDenied), media.Fallback);
        Assert.Equal("Media is disabled by the host.", media.Error);
    }

    [Fact]
    public void RevocationFromDecoderCannotCommitMedia()
    {
        using var engine = new JintScriptEngine();
        var decoder = new AudioDecoder(() => DomBindings.UpdateCapabilities(engine, new FeatureCapabilities { Sound = true }));
        using var document = Document.Parse("<audio id='a' src='data:application/octet-stream,a'></audio>",
            new FolioOptions { Capabilities = new FeatureCapabilities { Media = true, Sound = true }, MediaDecoder = decoder });
        DomBindings.Install(engine, document);
        var media = Assert.IsType<MediaElementState>(document.GetElementById("a")!.Node.MediaPresentation);
        Assert.Equal(new FeatureFallback(FallbackFeature.Audio, FallbackReason.HostDenied), media.Fallback);
        Assert.Equal("Media is disabled by the host.", media.Error);
    }

    [Fact]
    public void DisposalFromResourceLoaderCannotCommitMedia()
    {
        using var engine = new JintScriptEngine();
        var loader = new CallbackLoader(() => DomBindings.Dispose(engine));
        using var document = Document.Parse("<audio id='a' src='https://example.test/a'></audio>",
            new FolioOptions { Capabilities = new FeatureCapabilities { Media = true, Sound = true }, ResourceLoader = loader, MediaDecoder = new AudioDecoder() });
        DomBindings.Install(engine, document);
        var media = Assert.IsType<MediaElementState>(document.GetElementById("a")!.Node.MediaPresentation);
        Assert.Equal(new FeatureFallback(FallbackFeature.Audio, FallbackReason.HostDenied), media.Fallback);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DecoderRevokeAndRegrantKeepsNewLoadEvenWhenOldDecoderThrows(bool throwAfterCallback)
    {
        using var engine = new JintScriptEngine();
        var output = new Output();
        var decoder = new ReentrantDecoder(() =>
        {
            DomBindings.UpdateCapabilities(engine, new FeatureCapabilities { Sound = true });
            DomBindings.UpdateCapabilities(engine, new FeatureCapabilities { Media = true, Sound = true });
        }, throwAfterCallback);
        using var document = Document.Parse("<audio id='a' src='data:application/octet-stream,a'></audio>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Media = true, Sound = true },
            MediaDecoder = decoder, MediaAudioOutput = output
        });
        DomBindings.Install(engine, document);
        Assert.Equal(2, decoder.Calls);
        Assert.True(engine.Evaluate("var a=document.getElementById('a'); a.error===null && a.readyState===4").AsBoolean());
        engine.Evaluate("a.play()");
        Assert.NotEmpty(output.Frames);
        Assert.All(output.Frames.SelectMany(frame => frame.Samples.ToArray()), value => Assert.Equal(.5f, value));
    }

    [Fact]
    public void SrcObjectChangeDuringDecodeReturnsOldDecoderCapacity()
    {
        Document? active = null;
        var changed = false;
        var decoder = new AudioDecoder(() =>
        {
            if (changed) return;
            changed = true;
            var media = Assert.IsType<MediaElementState>(active!.GetElementById("a")!.Node.MediaPresentation);
            media.SetSrcObject(ScriptValue.Null);
        });
        using var document = Document.Parse("<audio id='a' src='data:application/octet-stream,a'></audio><audio id='b' src='data:application/octet-stream,b'></audio>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Media = true, Sound = true },
            MediaDecoder = decoder, Limits = ResourceLimits.Default with { MaxMediaDecoders = 1 }
        });
        active = document;
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        Assert.Equal(2, decoder.Calls);
        Assert.True(engine.Evaluate("document.getElementById('b').error===null && document.getElementById('b').readyState===4").AsBoolean());
    }

    [Fact]
    public void HtmlMediaAttributeNamesAreCaseInsensitiveAndSvgNamesKeepTheirCase()
    {
        using var document = Document.Parse("<video id='v'></video>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Media = true, Sound = true }, MediaDecoder = new AudioDecoder()
        });
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        Assert.True(engine.Evaluate("""
            var v=document.getElementById('v'); v.setAttribute('sRc','data:application/octet-stream,a');
            v.error===null && v.hasAttribute('SRC') && v.getAttribute('SRC')===v.src
            """).AsBoolean());
        var poster = "data:image/svg+xml," + Uri.EscapeDataString("<svg xmlns='http://www.w3.org/2000/svg' width='11' height='9'><rect width='11' height='9' fill='#000000'/></svg>");
        engine.Evaluate("v.setAttribute('POSTER','" + poster + "')");
        var media = Assert.IsType<MediaElementState>(document.GetElementById("v")!.Node.MediaPresentation);
        Assert.Equal((11, 9), (media.Poster!.Width, media.Poster.Height));
        engine.Evaluate("v.removeAttribute('SRC'); v.toggleAttribute('POSTER',false)");
        Assert.Null(media.Fallback);
        Assert.Null(media.Poster);
        Assert.True(engine.Evaluate("v.error === null && v.readyState === 0 && v.networkState === 0 && v.paused").AsBoolean());
        Assert.True(engine.Evaluate("""
            var svg=document.createElementNS('http://www.w3.org/2000/svg','svg');
            svg.setAttribute('viewBox','0 0 10 10');
            svg.getAttribute('viewBox')==='0 0 10 10' && svg.getAttribute('viewbox')===null
            """).AsBoolean());
    }

    private sealed class CallbackLoader(Action callback) : IResourceLoader
    {
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            callback();
            return Task.FromResult<ResourceResponse?>(new ResourceResponse(new byte[] { 1, 2, 3 }, "application/octet-stream", null));
        }
    }

    private class AudioDecoder(Action? callback = null) : IMediaDecoder
    {
        public int Calls { get; private set; }
        public MediaDecodeResult? Decode(ReadOnlyMemory<byte> data, string? contentType, bool video, int maxOutputBytes)
        {
            Calls++;
            callback?.Invoke();
            return new MediaDecodeResult(1, null, new MediaAudioFrame(8000, 1, new float[8]));
        }
    }

    private sealed class ReentrantDecoder(Action callback, bool throwAfterCallback) : IMediaDecoder
    {
        public int Calls { get; private set; }
        public MediaDecodeResult? Decode(ReadOnlyMemory<byte> data, string? contentType, bool video, int maxOutputBytes)
        {
            var call = ++Calls;
            if (call == 1)
            {
                callback();
                if (throwAfterCallback) throw new InvalidOperationException("old decoder failed after its replacement loaded");
            }
            return new(1, null, new MediaAudioFrame(8000, 1, Enumerable.Repeat(call == 1 ? .25f : .5f, 8).ToArray()));
        }
    }

    private sealed class Output : IMediaAudioOutput
    {
        public List<MediaAudioFrame> Frames { get; } = [];
        public void Submit(MediaAudioFrame frame) => Frames.Add(frame);
    }
}
