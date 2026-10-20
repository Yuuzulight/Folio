using Folio.Resources;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

public sealed class DisposedFeatureCapabilityTests
{
    [Fact]
    public void RetainedAndNewFrameElementsCannotRestartChildContextsAfterBindingDisposal()
    {
        using var engine = new JintScriptEngine();
        var loader = new Loader();
        using var document = Document.Parse("<iframe id='frame' srcdoc='<p>child</p>'></iframe>", new FolioOptions
        {
            BaseUri = new Uri("https://frame.example/"),
            ResourceLoader = loader,
            Capabilities = new FeatureCapabilities { Iframes = true }
        });
        DomBindings.Install(engine, document);
        Assert.True(engine.Evaluate("var frame=document.getElementById('frame'),retainedWindow=frame.contentWindow;frame.contentDocument !== null").AsBoolean());
        DomBindings.Dispose(engine);
        Assert.True(engine.Evaluate("frame.srcdoc='<script src=\"https://frame.example/restarted.js\"></script>';frame.contentDocument === null && retainedWindow.document === null && document.createElement('iframe').contentDocument === null").AsBoolean());
        Assert.Equal(0, loader.Loads);
    }

    [Fact]
    public void NewMediaElementCannotDecodeThroughRetainedDocumentAfterBindingDisposal()
    {
        using var engine = new JintScriptEngine();
        var decoder = new Decoder();
        using var document = Document.Parse("<p>media</p>", new FolioOptions
        {
            MediaDecoder = decoder,
            Capabilities = new FeatureCapabilities { Media = true, Sound = true }
        });
        DomBindings.Install(engine, document);
        DomBindings.Dispose(engine);
        engine.Evaluate("var clip=document.createElement('audio');clip.src='data:application/octet-stream;base64,AQID';clip.load();clip.play().catch(()=>{});");
        engine.PerformMicrotaskCheckpoint();
        Assert.Equal(0, decoder.Decodes);
    }

    private sealed class Loader : IResourceLoader
    {
        public int Loads { get; private set; }
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            Loads++;
            return Task.FromResult<ResourceResponse?>(ResourceResponse.Refused("unexpected load"));
        }
    }

    private sealed class Decoder : IMediaDecoder
    {
        public int Decodes { get; private set; }
        public MediaDecodeResult? Decode(ReadOnlyMemory<byte> data, string? contentType, bool video, int maxOutputBytes)
        {
            Decodes++;
            return new(1, null, new MediaAudioFrame(8000, 1, new float[8000]));
        }
    }
}
