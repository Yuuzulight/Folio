using Folio.Resources;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

public sealed class MediaBudgetTests
{
    [Fact]
    public void CapabilityAndDecoderCountAreCheckedBeforeLoadingOrDecoding()
    {
        var loader = new Loader();
        var decoder = new Decoder();
        using var denied = new JintScriptEngine();
        using var deniedDocument = Document.Parse("<audio src='https://media.example/a'></audio>", Options(loader, decoder, media: false));
        DomBindings.Install(denied, deniedDocument);
        Assert.Equal(0, loader.Requests);
        Assert.Equal(0, decoder.Calls);

        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<audio id='a' src='https://media.example/a'></audio><audio id='b' src='https://media.example/b'></audio>", Options(loader, decoder, decoders: 1));
        DomBindings.Install(engine, document);
        Assert.Equal(1, loader.Requests);
        Assert.Equal(1, decoder.Calls);
        Assert.True(engine.Evaluate("document.getElementById('a').error === null && document.getElementById('b').error !== null").AsBoolean());
    }

    [Fact]
    public void DecodedMemoryIsSharedAcrossElementsAndReloadReturnsCapacity()
    {
        var decoder = new Decoder();
        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<audio id='a' src='https://media.example/a'></audio><audio id='b' src='https://media.example/b'></audio>", Options(new Loader(), decoder, bytes: 80));
        DomBindings.Install(engine, document);
        // Each source is 4 bytes and its PCM is 64 bytes; only one fits in 80 bytes.
        Assert.Equal(new[] { 76, 8 }, decoder.OutputLimits);
        Assert.True(engine.Evaluate("var a = document.getElementById('a'); var b = document.getElementById('b'); a.error === null && b.error !== null").AsBoolean());
        engine.Evaluate("a.src = ''; b.load()");
        Assert.True(engine.Evaluate("b.error === null && b.readyState === 4").AsBoolean());
        Assert.Equal(76, decoder.OutputLimits[^1]);
    }

    [Fact]
    public void DisablingReleasesReservationsAndReloadCannotBypassDisabledCapability()
    {
        var loader = new Loader();
        var decoder = new Decoder();
        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<audio id='a' src='https://media.example/a'></audio>", Options(loader, decoder, decoders: 1));
        DomBindings.Install(engine, document);
        Assert.Equal(1, decoder.Calls);
        DomBindings.UpdateCapabilities(engine, FeatureCapabilities.Default);
        engine.Evaluate("document.getElementById('a').load()");
        Assert.Equal(1, loader.Requests);
        Assert.Equal(1, decoder.Calls);
        DomBindings.UpdateCapabilities(engine, new FeatureCapabilities { Media = true });
        Assert.Equal(2, decoder.Calls);
        Assert.True(engine.Evaluate("document.getElementById('a').error === null").AsBoolean());
    }

    [Theory]
    [InlineData(139, false)]
    [InlineData(140, true)]
    public void BuiltInWavCountsExpandedPcmBeforeAllocation(int budget, bool ready)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8); writer.Write(68); writer.Write("WAVEfmt "u8);
            writer.Write(16); writer.Write((short)1); writer.Write((short)1);
            writer.Write(8000); writer.Write(16000); writer.Write((short)2); writer.Write((short)16);
            writer.Write("data"u8); writer.Write(32); writer.Write(new byte[32]);
        }
        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<audio id='a' src='data:audio/wav;base64," + Convert.ToBase64String(stream.ToArray()) + "'></audio>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Media = true },
            Limits = ResourceLimits.Default with { MaxMediaBytes = budget }
        });
        DomBindings.Install(engine, document);
        Assert.Equal(ready, engine.Evaluate("document.getElementById('a').error === null").AsBoolean());
    }

    [Fact]
    public void SourceThatConsumesEntireBudgetNeverOpensDecoder()
    {
        var decoder = new Decoder();
        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<audio id='a' src='https://media.example/a'></audio>", Options(new Loader(), decoder, bytes: 4));
        DomBindings.Install(engine, document);
        Assert.Equal(0, decoder.Calls);
        Assert.True(engine.Evaluate("document.getElementById('a').error !== null").AsBoolean());
    }

    private static FolioOptions Options(Loader loader, Decoder decoder, bool media = true, int bytes = 1024, int decoders = 8) => new()
    {
        Capabilities = new FeatureCapabilities { Media = media },
        ResourceLoader = loader, MediaDecoder = decoder,
        Limits = ResourceLimits.Default with { MaxMediaBytes = bytes, MaxMediaDecoders = decoders }
    };
    private sealed class Loader : IResourceLoader
    {
        public int Requests;
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken token)
        { Requests++; return Task.FromResult<ResourceResponse?>(ResourceResponse.Loaded(new byte[4], "audio/test")); }
    }
    private sealed class Decoder : IMediaDecoder
    {
        public int Calls;
        public List<int> OutputLimits { get; } = [];
        public MediaDecodeResult? Decode(ReadOnlyMemory<byte> data, string? type, bool video, int maxOutputBytes)
        {
            Calls++; OutputLimits.Add(maxOutputBytes);
            // Deliberately violate a too-small host output budget to verify Folio checks the returned frame.
            return new(1, null, new MediaAudioFrame(8000, 1, new float[16]));
        }
    }
}
