using Folio;
using Folio.Dom;
using Folio.Resources;
using Folio.Scripting.Ipc;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;
using Folio.Scripting.Gpu;
using System.Runtime.InteropServices;
using System.Text;

namespace Folio.Scripting.Tests;

public sealed class CapabilityMatrixTests
{
    internal static FeatureCapabilities CapabilitiesFor(string row) => row switch
    {
        "webgl" => new FeatureCapabilities { WebGl = true },
        "media" => new FeatureCapabilities { Media = true },
        "sound" => new FeatureCapabilities { Sound = true },
        "camera" => new FeatureCapabilities { Camera = true },
        "microphone" => new FeatureCapabilities { Microphone = true },
        "workers" => new FeatureCapabilities { Workers = true },
        "webassembly" => new FeatureCapabilities { WebAssembly = true },
        "iframes" => new FeatureCapabilities { Iframes = true },
        "webrtc" => new FeatureCapabilities { WebRtc = true },
        "service-workers" => new FeatureCapabilities { ServiceWorkers = true },
        "all" => new FeatureCapabilities
        {
            WebGl = true, Media = true, Sound = true, Camera = true, Microphone = true,
            Workers = true, WebAssembly = true, Iframes = true, WebRtc = true, ServiceWorkers = true
        },
        "none" => FeatureCapabilities.Default,
        _ => throw new InvalidOperationException($"Unknown capability matrix row '{row}'.")
    };

    private sealed class ServiceWorkerLoader : IResourceLoader
    {
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<ResourceResponse?>(request.Kind == ResourceKind.Script
                ? ResourceResponse.Loaded(Encoding.UTF8.GetBytes("self.addEventListener('fetch', function(e) { e.respondWith(new Response('offline')); });"), "application/javascript")
                : ResourceResponse.Refused("unsupported"));
    }

    [Fact]
    public void CapabilityRowEnablesOnlyTheRequestedFeatureAndKeepsLimitsActive()
    {
        var row = Environment.GetEnvironmentVariable("FOLIO_CAPABILITY_MATRIX") ?? "none";
        var capabilities = CapabilitiesFor(row);

        Assert.Equal(row, Environment.GetEnvironmentVariable("FOLIO_CAPABILITY_MATRIX") ?? "none");
        Assert.False(new ContentProcessSupervisor(new ContentProcessLimits(MemoryBytes: 1)).CheckMemory(2));
        Assert.False(new ContentProcessSupervisor(new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromMilliseconds(-1))).CheckHeartbeat());
        var enabled = new[] { capabilities.WebGl, capabilities.Media, capabilities.Sound, capabilities.Camera,
            capabilities.Microphone, capabilities.Workers, capabilities.WebAssembly, capabilities.Iframes,
            capabilities.WebRtc, capabilities.ServiceWorkers };
        if (row == "all")
            Assert.All(enabled, Assert.True);
        else if (row != "none")
            Assert.Equal(1, enabled.Count(value => value));
        else
            Assert.Equal(0, enabled.Count(value => value));
    }

    [Fact]
    public void ServiceWorkerCapabilityRowExercisesRegistrationCacheAndShutdown()
    {
        var row = Environment.GetEnvironmentVariable("FOLIO_CAPABILITY_MATRIX") ?? "none";
        if (row is not ("service-workers" or "all")) return;

        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, Document.Parse("<html></html>", new FolioOptions
        {
            BaseUri = new Uri("https://worker.example/"),
            ResourceLoader = new ServiceWorkerLoader(),
            Capabilities = new FeatureCapabilities { ServiceWorkers = true },
            Limits = ResourceLimits.Default with { MaxServiceWorkerCacheBytes = 2048 }
        }));
        Assert.True(engine.Evaluate("navigator.serviceWorker.register('sw.js').then(() => caches.open('row').then(c => c.put('/x', new Response('12345678')))); true").AsBoolean());
        engine.PerformMicrotaskCheckpoint();
        Assert.True(engine.Evaluate("let rowHasCache = false; caches.has('row').then(v => rowHasCache = v); true").AsBoolean());
        engine.PerformMicrotaskCheckpoint();
        Assert.True(engine.Evaluate("rowHasCache").AsBoolean());
        Assert.True(DomBindings.Dispose(engine));
        Assert.Equal(0, engine.Evaluate("navigator.serviceWorker.getRegistrations().then(items => items.length)").AsNumber());
    }

    [Fact]
    public void IframeCapabilityRowExercisesRoutingSandboxAndFrameLimit()
    {
        var row = Environment.GetEnvironmentVariable("FOLIO_CAPABILITY_MATRIX") ?? "none";
        if (row is not ("iframes" or "all")) return;

        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, Document.Parse(
            "<iframe id='one' sandbox='allow-same-origin' srcdoc='<p id=child>ok</p>'></iframe><iframe id='two'></iframe>",
            new FolioOptions
            {
                BaseUri = new Uri("https://matrix.example/"),
                Capabilities = capabilitiesFor(row),
                Limits = ResourceLimits.Default with { MaxIframes = 1 }
            }));

        Assert.True(engine.Evaluate("const frame = document.getElementById('one'); frame.contentDocument.getElementById('child').textContent === 'ok' && frame.contentWindow.origin === 'https://matrix.example'").AsBoolean());
        Assert.ThrowsAny<Exception>(() => engine.Evaluate("document.getElementById('two').contentWindow"));

        static FeatureCapabilities capabilitiesFor(string capabilityRow) => capabilityRow == "all"
            ? CapabilitiesFor("all") : new FeatureCapabilities { Iframes = true };
    }

    [Fact]
    public void WebGlCapabilityRowExercisesBudgetAndContextLossLifecycle()
    {
        var row = Environment.GetEnvironmentVariable("FOLIO_CAPABILITY_MATRIX") ?? "none";
        if (row is not ("webgl" or "all") || !OperatingSystem.IsWindows()) return;

        using var engine = new JintScriptEngine();
        var rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64", Architecture.Arm64 => "win-arm64", Architecture.X86 => "win-x86",
            _ => throw new PlatformNotSupportedException(),
        };
        var path = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", "av_libglesv2.dll");
        if (!File.Exists(path)) path = Path.Combine(AppContext.BaseDirectory, "av_libglesv2.dll");
        Assert.True(DomBindings.ConfigureGpuFactory(engine, bytes => AngleGpuBackend.TryCreate(path, bytes, GpuRendererPreference.WarpOnly)));

        using var document = Document.Parse("<canvas id='c' width='1' height='1'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true },
            Limits = ResourceLimits.Default with { MaxGpuBytes = 8 },
            Raster = new Folio.Skia.SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false});var lost=0,restored=0;gl.oncontextlost=()=>lost++;gl.oncontextrestored=()=>restored++;var t=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D,t);gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,2,2,0,gl.RGBA,gl.UNSIGNED_BYTE,new Uint8Array(16));");
            Assert.Equal(0x0505, engine.Evaluate("gl.getError()").AsNumber());
            Assert.True(DomBindings.UpdateCapabilities(engine, FeatureCapabilities.Default));
            Assert.True(engine.Evaluate("gl.isContextLost() && lost === 1").AsBoolean());
            Assert.True(DomBindings.UpdateCapabilities(engine, new FeatureCapabilities { WebGl = true }));
            Assert.True(engine.Evaluate("!gl.isContextLost() && restored === 1").AsBoolean());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void MediaCapabilityRowExercisesBudgetAndRevocationLifecycle()
    {
        var row = Environment.GetEnvironmentVariable("FOLIO_CAPABILITY_MATRIX") ?? "none";
        if (row is not ("media" or "all")) return;

        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<audio id='a' src='clip'></audio>", new FolioOptions
        {
            BaseUri = new Uri("https://matrix.example/"),
            ResourceLoader = new MatrixMediaLoader(),
            MediaDecoder = new MatrixMediaDecoder(),
            Capabilities = new FeatureCapabilities { Media = true },
            Limits = ResourceLimits.Default with { MaxMediaBytes = 4 },
        });
        DomBindings.Install(engine, document);
        try
        {
            Assert.True(engine.Evaluate("const a=document.getElementById('a'); a.error !== null && a.error.includes('budget')").AsBoolean());
            Assert.True(DomBindings.UpdateCapabilities(engine, FeatureCapabilities.Default));
            Assert.True(engine.Evaluate("a.paused && a.error === 'Media is disabled by the host.'").AsBoolean());
            Assert.True(DomBindings.UpdateCapabilities(engine, new FeatureCapabilities { Media = true }));
            Assert.True(engine.Evaluate("a.paused && a.error !== null").AsBoolean());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void WebRtcCapabilityRowExercisesConnectionLimitAndRevocation()
    {
        var row = Environment.GetEnvironmentVariable("FOLIO_CAPABILITY_MATRIX") ?? "none";
        if (row is not ("webrtc" or "all")) return;

        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<html></html>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebRtc = true },
        });
        DomBindings.Install(engine, document.Node, capabilities: new FeatureCapabilities { WebRtc = true }, maxWebRtcConnections: 1);
        try
        {
            Assert.True(engine.Evaluate("var first=new RTCPeerConnection();var limited=false;try{new RTCPeerConnection();}catch(e){limited=e.name==='QuotaExceededError';}limited").AsBoolean());
            Assert.True(engine.Evaluate("first.close();var second=new RTCPeerConnection();second.connectionState==='new'").AsBoolean());
            Assert.True(DomBindings.UpdateCapabilities(engine, FeatureCapabilities.Default));
            Assert.True(engine.Evaluate("second.connectionState==='closed'").AsBoolean());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void WebAssemblyCapabilityRowExercisesMemoryLimitAndCapabilityRevocation()
    {
        var row = Environment.GetEnvironmentVariable("FOLIO_CAPABILITY_MATRIX") ?? "none";
        if (row is not ("webassembly" or "all")) return;

        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<html></html>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebAssembly = true },
        });
        DomBindings.Install(engine, document.Node, capabilities: new FeatureCapabilities { WebAssembly = true }, maxWebAssemblyBytes: 64 * 1024);
        try
        {
            Assert.True(engine.Evaluate("var allowed=new WebAssembly.Memory({initial:1});var limited=false;try{new WebAssembly.Memory({initial:2});}catch(e){limited=e.name==='RangeError';}limited && allowed.buffer.byteLength===65536").AsBoolean());
            Assert.True(DomBindings.UpdateCapabilities(engine, FeatureCapabilities.Default));
            Assert.ThrowsAny<Exception>(() => engine.Evaluate("new WebAssembly.Memory({initial:1})"));
        }
        finally { DomBindings.Dispose(engine); }
    }

    private sealed class MatrixMediaLoader : IResourceLoader
    {
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<ResourceResponse?>(request.Kind == ResourceKind.Media
                ? ResourceResponse.Loaded(new byte[] { 1, 2, 3, 4 }, "audio/x-matrix")
                : null);
    }

    private sealed class MatrixMediaDecoder : IMediaDecoder
    {
        public MediaDecodeResult? Decode(ReadOnlyMemory<byte> data, string? contentType, bool video, int maxOutputBytes) =>
            video ? null : new MediaDecodeResult(1, null, new MediaAudioFrame(8_000, 1, new float[] { .25f, -.25f }));
    }
}
