using Folio;
using Folio.Dom;
using Folio.Resources;
using Folio.Scripting.Ipc;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;
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

    private sealed class PngResourceLoader : IResourceLoader
    {
        private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==");

        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<ResourceResponse?>(request.Kind == ResourceKind.Media
                ? ResourceResponse.Loaded(Png, "image/png")
                : ResourceResponse.Refused("wrong resource kind"));
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
    public void WebGlCapabilityRowExercisesContextLossAndGpuLimit()
    {
        var row = Environment.GetEnvironmentVariable("FOLIO_CAPABILITY_MATRIX") ?? "none";
        if (row is not ("webgl" or "all")) return;

        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, Document.Parse(
            "<canvas id='c'></canvas>",
            new FolioOptions
            {
                Capabilities = capabilitiesFor(row),
                Raster = new Folio.Skia.SkiaRasterBackend(),
                Limits = ResourceLimits.Default with { MaxGpuBytes = 1024, MaxShaderExecutionTime = TimeSpan.FromMilliseconds(50) }
            }));

        Assert.True(engine.Evaluate(@"
            const gl = document.getElementById('c').getContext('webgl2');
            const ext = gl.getExtension('WEBGL_lose_context');
            let events = '';
            gl.oncontextlost = () => { events += 'lost'; };
            gl.oncontextrestored = () => { events += 'restored'; };
            ext.loseContext();
            const wasLost = gl.isContextLost();
            const lostError = gl.getError() === 0x9242;
            ext.restoreContext();
            const wasRestored = !gl.isContextLost();

            // GPU memory counted against limits
            const buf = gl.createBuffer();
            gl.bindBuffer(gl.ARRAY_BUFFER, buf);
            gl.bufferData(gl.ARRAY_BUFFER, 512, gl.STATIC_DRAW);
            const allocOk = gl.getError() === 0;
            gl.bufferData(gl.ARRAY_BUFFER, 2048, gl.STATIC_DRAW);
            const oom = gl.getError() === 0x0505;
            gl.deleteBuffer(buf);

            // Runaway shader watchdog loses context and leaves page running
            const vs = gl.createShader(gl.VERTEX_SHADER);
            gl.shaderSource(vs, 'attribute vec2 p; void main() { gl_Position = vec4(p, 0.0, 1.0); }');
            gl.compileShader(vs);
            const fs = gl.createShader(gl.FRAGMENT_SHADER);
            gl.shaderSource(fs, 'void main() { gl_FragColor = vec4(1.0); }');
            gl.compileShader(fs);
            const prg = gl.createProgram();
            gl.attachShader(prg, vs);
            gl.attachShader(prg, fs);
            gl.linkProgram(prg);
            gl.useProgram(prg);
            const vbo = gl.createBuffer();
            gl.bindBuffer(gl.ARRAY_BUFFER, vbo);
            const verts = new Float32Array([0, 0, 1, 0, 0, 1]);
            gl.bufferData(gl.ARRAY_BUFFER, verts, gl.STATIC_DRAW);
            gl.enableVertexAttribArray(0);
            gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);
            gl.drawArrays(gl.TRIANGLES, 0, 3000000);
            const runawayLost = gl.isContextLost();
            const pageRunning = true;

            wasLost && lostError && wasRestored && events.includes('lost') && allocOk && oom && runawayLost && pageRunning
        ").AsBoolean());

        static FeatureCapabilities capabilitiesFor(string capabilityRow) => capabilityRow == "all"
            ? CapabilitiesFor("all") : new FeatureCapabilities { WebGl = true };
    }

    [Fact]
    public void MediaCapabilityRowExercisesPlaybackAndBudget()
    {
        var row = Environment.GetEnvironmentVariable("FOLIO_CAPABILITY_MATRIX") ?? "none";
        if (row is not ("media" or "all")) return;

        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, Document.Parse(
            "<video id='a' src='https://media.example/a'></video><video id='b' src='https://media.example/b'></video>",
            new FolioOptions
            {
                BaseUri = new Uri("https://media.example/"),
                Capabilities = capabilitiesFor(row),
                ResourceLoader = new PngResourceLoader(),
                Limits = ResourceLimits.Default with { MaxMediaDecoders = 1 }
            }));

        Assert.True(engine.Evaluate(@"
            const a = document.getElementById('a');
            const b = document.getElementById('b');
            const aOk = a.readyState === 4 && a.error === null;
            const bExceeded = b.error === 'Media decoder limit exceeded.';
            a.volume = 0.5;
            const volOk = a.volume === 0.5;
            a.play();
            const playOk = !a.paused;
            a.pause();
            const pauseOk = a.paused;
            aOk && bExceeded && volOk && playOk && pauseOk
        ").AsBoolean());

        static FeatureCapabilities capabilitiesFor(string capabilityRow) => capabilityRow == "all"
            ? CapabilitiesFor("all") : new FeatureCapabilities { Media = true, Sound = true };
    }

    [Fact]
    public void WorkersCapabilityRowExercisesMemoryAndWatchdog()
    {
        var row = Environment.GetEnvironmentVariable("FOLIO_CAPABILITY_MATRIX") ?? "none";
        if (row is not ("workers" or "all")) return;

        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, Document.Parse(
            "<p>workers</p>",
            new FolioOptions
            {
                Capabilities = capabilitiesFor(row),
                Limits = ResourceLimits.Default with { MaxWorkers = 1, MaxWorkerMemoryBytes = 1000 }
            }));

        Assert.True(engine.Evaluate(@"
            const w1 = new Worker('data:text/javascript,postMessage(1)');
            let secondThrew = false;
            try {
                new Worker('data:text/javascript,postMessage(2)');
            } catch (e) {
                secondThrew = e.name === 'QuotaExceededError';
            }
            w1.terminate();
            secondThrew
        ").AsBoolean());

        static FeatureCapabilities capabilitiesFor(string capabilityRow) => capabilityRow == "all"
            ? CapabilitiesFor("all") : new FeatureCapabilities { Workers = true };
    }

    [Fact]
    public void WebAssemblyCapabilityRowExercisesMemoryAndTraps()
    {
        var row = Environment.GetEnvironmentVariable("FOLIO_CAPABILITY_MATRIX") ?? "none";
        if (row is not ("webassembly" or "all")) return;

        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, Document.Parse(
            "<p>wasm</p>",
            new FolioOptions
            {
                Capabilities = capabilitiesFor(row),
                Limits = ResourceLimits.Default with { MaxWebAssemblyBytes = 65536 }
            }));

        Assert.True(engine.Evaluate(@"
            const bytes = new Uint8Array([0,97,115,109,1,0,0,0]);
            const valid = WebAssembly.validate(bytes);
            const mem = new WebAssembly.Memory({ initial: 1 });
            let growThrew = false;
            try {
                mem.grow(1);
            } catch (e) {
                growThrew = e.name === 'RangeError';
            }
            valid && growThrew
        ").AsBoolean());

        static FeatureCapabilities capabilitiesFor(string capabilityRow) => capabilityRow == "all"
            ? CapabilitiesFor("all") : new FeatureCapabilities { WebAssembly = true };
    }

    [Fact]
    public void WebRtcCapabilityRowExercisesDataChannelAndLimits()
    {
        var row = Environment.GetEnvironmentVariable("FOLIO_CAPABILITY_MATRIX") ?? "none";
        if (row is not ("webrtc" or "all")) return;

        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, Document.Parse(
            "<p>webrtc</p>",
            new FolioOptions
            {
                Capabilities = capabilitiesFor(row),
                Limits = ResourceLimits.Default with { MaxWebRtcConnections = 2 }
            }));

        Assert.True(engine.Evaluate(@"
            var pc1 = new RTCPeerConnection();
            var pc2 = new RTCPeerConnection();
            var quotaThrew = false;
            try {
                new RTCPeerConnection();
            } catch (e) {
                quotaThrew = e.name === 'QuotaExceededError';
            }
            var received = null;
            pc2.ondatachannel = function(e) {
                e.channel.onmessage = function(ev) { received = ev.data; };
            };
            pc1.setLocalDescription({type: 'offer', sdp: ''});
            pc2.setRemoteDescription({type: 'offer', sdp: ''});
            pc2.setLocalDescription({type: 'answer', sdp: ''});
            pc1.setRemoteDescription({type: 'answer', sdp: ''});
            var dc = pc1.createDataChannel('matrix');
            dc.send('hello-matrix');
            var dataOk = received === 'hello-matrix';
            pc1.close();
            pc2.close();
            var closedOk = pc1.connectionState === 'closed' && pc2.connectionState === 'closed';
            quotaThrew && dataOk && closedOk
        ").AsBoolean());

        static FeatureCapabilities capabilitiesFor(string capabilityRow) => capabilityRow == "all"
            ? CapabilitiesFor("all") : new FeatureCapabilities { WebRtc = true };
    }

    [Fact]
    public void SoundCapabilityRowExercisesAudioSubmission()
    {
        var row = Environment.GetEnvironmentVariable("FOLIO_CAPABILITY_MATRIX") ?? "none";
        if (row is not ("sound" or "all")) return;

        var decoder = new TestMediaDecoder();
        var output = new TestAudioOutput();
        using var engine = new JintScriptEngine();
        var clock = new VirtualEventLoopClock();
        using var loop = new HtmlEventLoop(engine, clock);
        DomBindings.Install(engine, Document.Parse(
            "<audio id='a' src='data:audio/wav;base64,AQID'></audio>",
            new FolioOptions
            {
                Capabilities = capabilitiesFor(row),
                MediaDecoder = decoder,
                MediaAudioOutput = output
            }), eventLoop: loop);

        engine.Evaluate("document.getElementById('a').play()");
        clock.SetNow(50);
        loop.UpdateTheRendering();
        Assert.NotEmpty(output.Frames);

        static FeatureCapabilities capabilitiesFor(string capabilityRow) => capabilityRow == "all"
            ? CapabilitiesFor("all") : new FeatureCapabilities { Media = true, Sound = true };
    }

    [Fact]
    public void CameraCapabilityRowExercisesCaptureStream()
    {
        var row = Environment.GetEnvironmentVariable("FOLIO_CAPABILITY_MATRIX") ?? "none";
        if (row is not ("camera" or "all")) return;

        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, Document.Parse(
            "<p>camera</p>",
            new FolioOptions
            {
                Capabilities = capabilitiesFor(row)
            }));

        Assert.True(engine.Evaluate(@"
            let hasStream = false;
            navigator.mediaDevices.getUserMedia({ video: true }).then(stream => {
                hasStream = stream && stream.getVideoTracks().length > 0;
            });
            true
        ").AsBoolean());
        engine.PerformMicrotaskCheckpoint();
        Assert.True(engine.Evaluate("hasStream").AsBoolean());

        static FeatureCapabilities capabilitiesFor(string capabilityRow) => capabilityRow == "all"
            ? CapabilitiesFor("all") : new FeatureCapabilities { Camera = true };
    }

    [Fact]
    public void MicrophoneCapabilityRowExercisesCaptureStream()
    {
        var row = Environment.GetEnvironmentVariable("FOLIO_CAPABILITY_MATRIX") ?? "none";
        if (row is not ("microphone" or "all")) return;

        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, Document.Parse(
            "<p>microphone</p>",
            new FolioOptions
            {
                Capabilities = capabilitiesFor(row)
            }));

        Assert.True(engine.Evaluate(@"
            let hasStream = false;
            navigator.mediaDevices.getUserMedia({ audio: true }).then(stream => {
                hasStream = stream && stream.getAudioTracks().length > 0;
            });
            true
        ").AsBoolean());
        engine.PerformMicrotaskCheckpoint();
        Assert.True(engine.Evaluate("hasStream").AsBoolean());

        static FeatureCapabilities capabilitiesFor(string capabilityRow) => capabilityRow == "all"
            ? CapabilitiesFor("all") : new FeatureCapabilities { Microphone = true };
    }

    private sealed class TestMediaDecoder : IMediaDecoder
    {
        public MediaDecodeResult? Decode(ReadOnlyMemory<byte> data, string? type, bool video, int budget) =>
            new(1, null, new MediaAudioFrame(8000, 1, new float[8000]));
    }

    private sealed class TestAudioOutput : IMediaAudioOutput
    {
        public List<MediaAudioFrame> Frames { get; } = [];
        public void Submit(MediaAudioFrame frame) => Frames.Add(frame);
    }
}
