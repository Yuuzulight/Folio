using Folio.Dom;
using Folio.Html;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;
using Folio.Scripting.Ipc;
using Folio.Resources;
using System.Text;
using System.Text.Json;

namespace Folio.Scripting.Tests;

public sealed class DomStyleDeclarationTests
{
    private sealed class WorkerResourceLoader : IResourceLoader
    {
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<ResourceResponse?>(request.Kind == ResourceKind.Script
                ? ResourceResponse.Loaded(Encoding.UTF8.GetBytes("onmessage = function(e) { postMessage(e.data + 2); };"), "application/javascript")
                : ResourceResponse.Refused("unsupported"));
    }

    [Fact]
    public void InlineStyleIsLiveAndSupportsDeclarationOperations()
    {
        var document = TreeBuilder.Parse("<!doctype html><html><body><div id='box' style='color: red; margin: 1px 2px; --accent: blue'></div></body></html>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        Assert.Equal("red", engine.Evaluate("box = document.getElementById('box'); box.style.color").AsString());
        Assert.Equal(3, engine.Evaluate("box.style.length").AsNumber());
        Assert.Equal("color", engine.Evaluate("box.style.item(0)").AsString());
        Assert.Equal("blue", engine.Evaluate("box.style.getPropertyValue('--accent')").AsString());
        Assert.Equal("1px", engine.Evaluate("box.style.marginTop").AsString());

        engine.Evaluate("box.style.setProperty('background-color', 'rgb(1, 2, 3)'); box.style.setProperty('margin-top', '4px', 'important');");
        Assert.Contains("background-color", engine.Evaluate("box.getAttribute('style')").AsString());
        Assert.Equal("rgb(1, 2, 3)", engine.Evaluate("box.style.backgroundColor").AsString());
        Assert.Equal("important", engine.Evaluate("box.style.getPropertyPriority('margin-top')").AsString());
        Assert.Contains("background-color", engine.Evaluate("box.getAttribute('style')").AsString());

        Assert.Equal("red", engine.Evaluate("box.style.removeProperty('color')").AsString());
        Assert.True(engine.Evaluate("box.style.color === undefined").AsBoolean());
    }

    [Fact]
    public void MutationObserverReceivesChildListRecords()
    {
        var document = TreeBuilder.Parse("<html><body></body></html>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        var result = engine.Evaluate(@"
            let seen = '';
            const observer = new MutationObserver(records => { seen = records[0].type + ':' + records[0].addedNodes.length; });
            observer.observe(document.body, { childList: true, subtree: true });
            document.body.appendChild(document.createElement('div'));
            seen;");
        Assert.Equal("childList:1", result.AsString());
    }

    [Fact]
    public void MutationObserverReceivesAttributeRecords()
    {
        var document = TreeBuilder.Parse("<html><body><div id='target'></div></body></html>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        var result = engine.Evaluate(@"
            let seen = '';
            const observer = new MutationObserver(records => { seen = records[0].type + ':' + records[0].attributeName; });
            observer.observe(document.body, { attributes: true, subtree: true });
            document.getElementById('target').setAttribute('data-state', 'ready');
            seen;");
        Assert.Equal("attributes:data-state", result.AsString());
    }

    [Fact]
    public void CanvasElementExposesBasicTwoDimensionalContext()
    {
        var document = TreeBuilder.Parse("<canvas id='chart' width='640' height='480'></canvas>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        Assert.Equal(640, engine.Evaluate("const canvas = document.getElementById('chart'); canvas.width").AsNumber());
        Assert.Equal("#000000", engine.Evaluate("const ctx = canvas.getContext('2d'); ctx.fillStyle").AsString());
        Assert.Equal("#f00", engine.Evaluate("ctx.fillStyle = '#f00'; ctx.fillRect(1, 2, 30, 40); ctx.fillStyle").AsString());
        Assert.Equal("object", engine.Evaluate("const path = new Path2D(); path.moveTo(0, 0); path.lineTo(10, 10); typeof path").AsString());
        engine.Evaluate("ctx.beginPath(); ctx.moveTo(0, 0); ctx.lineTo(10, 10); ctx.closePath(); ctx.stroke();");
        engine.Evaluate("ctx.beginPath(); ctx.rect(0, 0, 20, 20); ctx.quadraticCurveTo(5, 5, 10, 10); ctx.bezierCurveTo(1, 2, 3, 4, 5, 6); ctx.arc(10, 10, 4, 0, 3.14); ctx.fill();");
        engine.Evaluate("const p = new Path2D(); p.rect(0, 0, 2, 2); p.arc(1, 1, 1, 0, 6.28);");
        engine.Evaluate("const gradient = ctx.createLinearGradient(0, 0, 10, 10); gradient.addColorStop(0, 'red'); gradient.addColorStop(1, 'blue'); ctx.fillStyle = gradient; ctx.fillText('hello', 1, 2);");
        Assert.True(engine.Evaluate("ctx.measureText('hello').width > 0").AsBoolean());
        Assert.Equal(4, engine.Evaluate("ctx.createImageData(1, 1).data.length").AsNumber());
        Assert.StartsWith("data:image/png;base64,", engine.Evaluate("canvas.toDataURL()").AsString());
        Assert.True(engine.Evaluate("canvas.getContext('webgl') === null").AsBoolean());
    }

    [Fact]
    public void WebGlContextIsCapabilityGatedAndTracksBoundedState()
    {
        using var denied = new JintScriptEngine();
        var deniedDocument = Document.Parse("<canvas id='c'></canvas>");
        DomBindings.Install(denied, deniedDocument);
        Assert.True(denied.Evaluate("document.getElementById('c').getContext('webgl2') === null").AsBoolean());

        using var allowed = new JintScriptEngine();
        var allowedDocument = Document.Parse("<canvas id='c'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }
        });
        DomBindings.Install(allowed, allowedDocument);
        Assert.True(allowed.Evaluate("const gl = document.getElementById('c').getContext('webgl2'); gl !== null && gl.ARRAY_BUFFER === 34962").AsBoolean());
        Assert.True(allowed.Evaluate("const glAgain = document.getElementById('c').getContext('webgl2'); glAgain.getParameter(glAgain.VERSION) === 'WebGL 2.0 Folio'").AsBoolean());
        Assert.True(allowed.Evaluate("gl.drawingBufferWidth === 300 && gl.drawingBufferHeight === 150 && gl.getContextAttributes().depth === true").AsBoolean());
        Assert.True(allowed.Evaluate("gl.viewport(1, 2, 120, 80); const vp = gl.getParameter(gl.VIEWPORT); gl.scissor(3, 4, 50, 60); const sc = gl.getParameter(gl.SCISSOR_BOX); const precision = gl.getShaderPrecisionFormat(gl.VERTEX_SHADER, gl.HIGH_FLOAT); vp[0] === 1 && vp[3] === 80 && sc[2] === 50 && precision.precision === 23").AsBoolean());
        Assert.Equal(0, allowed.Evaluate("gl.enable(gl.DEPTH_TEST); gl.clearDepth(0.5); gl.depthFunc(gl.LEQUAL); gl.clear(gl.DEPTH_BUFFER_BIT); gl.getError()").AsNumber());
        Assert.Equal(0, allowed.Evaluate("gl.enable(gl.BLEND); gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA); gl.blendEquation(gl.FUNC_ADD); gl.getError()").AsNumber());
        Assert.Equal(0, allowed.Evaluate("gl.enable(gl.STENCIL_TEST); gl.stencilFunc(0x0207, 1, 0xff); gl.stencilOp(0x1e00, 0x1e00, 0x1e00); gl.clearStencil(0); gl.getError()").AsNumber());
        Assert.True(allowed.Evaluate("gl.getParameter(gl.MAX_TEXTURE_SIZE) === 4096 && gl.getParameter(gl.VERSION).includes('WebGL 2.0') && gl.getSupportedExtensions()[0] === 'WEBGL_lose_context'").AsBoolean());
        Assert.True(allowed.Evaluate("gl.drawBuffers([gl.COLOR_ATTACHMENT0]); gl.readBuffer(gl.BACK); gl.getParameter(gl.MAX_DRAW_BUFFERS) === 4 && gl.getParameter(gl.DRAW_BUFFER0) === gl.COLOR_ATTACHMENT0").AsBoolean());
        Assert.True(allowed.Evaluate("const sync = gl.fenceSync(gl.SYNC_GPU_COMMANDS_COMPLETE, 0); gl.isSync(sync) && gl.clientWaitSync(sync, 0, 0) === gl.CONDITION_SATISFIED; gl.deleteSync(sync); !gl.isSync(sync)").AsBoolean());
        Assert.True(allowed.Evaluate("const query = gl.createQuery(); gl.beginQuery(gl.ANY_SAMPLES_PASSED, query); gl.endQuery(gl.ANY_SAMPLES_PASSED); gl.isQuery(query) && gl.getQueryParameter(query, gl.QUERY_RESULT_AVAILABLE) && gl.getQueryParameter(query, gl.QUERY_RESULT) === 1; gl.deleteQuery(query); !gl.isQuery(query)").AsBoolean());
        Assert.Equal(0, allowed.Evaluate("const b = gl.createBuffer(); gl.bindBuffer(gl.ARRAY_BUFFER, b); gl.bufferData(gl.ARRAY_BUFFER, 32, gl.STATIC_DRAW); gl.getError()").AsNumber());
        Assert.Equal(0, allowed.Evaluate("gl.bufferSubData(gl.ARRAY_BUFFER, 4, 8); gl.getError()").AsNumber());
        Assert.True(allowed.Evaluate("const readback = new Uint8Array(4); gl.getBufferSubData(gl.ARRAY_BUFFER, 4, readback); gl.getError() === 0 && readback[0] === 0").AsBoolean());
        Assert.Equal(0x0501, allowed.Evaluate("gl.bufferSubData(gl.ARRAY_BUFFER, 30, 8); gl.getError()").AsNumber());
        Assert.Equal(0, allowed.Evaluate("const typed = new Float32Array(4); gl.bufferData(gl.ARRAY_BUFFER, typed, gl.STATIC_DRAW); gl.getError()").AsNumber());
        Assert.Equal(0, allowed.Evaluate("const tex = gl.createTexture(); gl.activeTexture(gl.TEXTURE0); gl.bindTexture(gl.TEXTURE_2D, tex); gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR); gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE); gl.getTexParameter(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER) === gl.LINEAR ? gl.getError() : 0x0500").AsNumber());
        Assert.Equal(0, allowed.Evaluate("gl.generateMipmap(gl.TEXTURE_2D); gl.texSubImage2D(gl.TEXTURE_2D, 0, 0, 0, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array(4)); gl.getError()").AsNumber());
        Assert.Equal(0x0502, allowed.Evaluate("gl.deleteBuffer(b); gl.bufferData(gl.ARRAY_BUFFER, 4, gl.STATIC_DRAW); gl.getError()").AsNumber());
        Assert.True(allowed.Evaluate("const s = gl.createShader(gl.VERTEX_SHADER); gl.shaderSource(s, 'void main(){}'); gl.compileShader(s); gl.getShaderParameter(s, gl.COMPILE_STATUS)").AsBoolean());
        Assert.Equal(0, allowed.Evaluate("const ds = gl.createShader(gl.VERTEX_SHADER); gl.deleteShader(ds); const dp = gl.createProgram(); gl.deleteProgram(dp); gl.getError()").AsNumber());
        Assert.True(allowed.Evaluate("const checkBuffer = gl.createBuffer(); gl.isBuffer(checkBuffer) && (gl.deleteBuffer(checkBuffer), !gl.isBuffer(checkBuffer))").AsBoolean());
        Assert.True(allowed.Evaluate("const vao = gl.createVertexArray(); gl.bindVertexArray(vao); gl.isVertexArray(vao) && (gl.deleteVertexArray(vao), !gl.isVertexArray(vao))").AsBoolean());
        Assert.True(allowed.Evaluate("const vs = gl.createShader(gl.VERTEX_SHADER); gl.shaderSource(vs, 'void main(){}'); gl.compileShader(vs); const fs = gl.createShader(gl.FRAGMENT_SHADER); gl.shaderSource(fs, 'void main(){}'); gl.compileShader(fs); const prog = gl.createProgram(); gl.attachShader(prog, vs); gl.attachShader(prog, fs); gl.linkProgram(prog); gl.getProgramParameter(prog, gl.LINK_STATUS)").AsBoolean());
        Assert.Equal("", allowed.Evaluate("gl.getShaderInfoLog(vs) + gl.getProgramInfoLog(prog)").AsString());
        Assert.True(allowed.Evaluate("gl.useProgram(prog); const loc = gl.getAttribLocation(prog, 'a_position'); const uniform = gl.getUniformLocation(prog, 'u_value'); gl.enableVertexAttribArray(loc); gl.vertexAttribPointer(loc, 3, gl.FLOAT, false, 0, 0); gl.uniform1f(uniform, 1); loc === 0 && uniform === 0 && gl.getUniform(prog, uniform) === 1 && gl.getError() === 0").AsBoolean());
        Assert.True(allowed.Evaluate("gl.getVertexAttrib(loc, gl.VERTEX_ATTRIB_ARRAY_ENABLED) && gl.getVertexAttrib(loc, gl.VERTEX_ATTRIB_ARRAY_SIZE) === 3 && gl.getVertexAttrib(loc, gl.VERTEX_ATTRIB_ARRAY_TYPE) === gl.FLOAT; gl.drawElements(gl.TRIANGLES, 3, gl.UNSIGNED_SHORT, 0); gl.getError() === 0").AsBoolean());
        Assert.Equal(0, allowed.Evaluate("gl.drawArrays(gl.TRIANGLES, 0, 3); gl.getError()").AsNumber());
        Assert.Equal(0, allowed.Evaluate("const t = gl.createTexture(); gl.bindTexture(gl.TEXTURE_2D, t); gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 2, 2, 0, gl.RGBA, gl.UNSIGNED_BYTE, null); const f = gl.createFramebuffer(); gl.bindFramebuffer(gl.FRAMEBUFFER, f); const p = new Uint8Array(16); p.fill(255); gl.readPixels(0, 0, 2, 2, gl.RGBA, gl.UNSIGNED_BYTE, p); gl.getError()").AsNumber());
        Assert.True(allowed.Evaluate("gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, t, 0); gl.checkFramebufferStatus(gl.FRAMEBUFFER) === gl.FRAMEBUFFER_COMPLETE && gl.getFramebufferAttachmentParameter(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.FRAMEBUFFER_ATTACHMENT_OBJECT_TYPE) === gl.TEXTURE && gl.getFramebufferAttachmentParameter(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.FRAMEBUFFER_ATTACHMENT_OBJECT_NAME) === t").AsBoolean());
        Assert.Equal(0, allowed.Evaluate("p[0]").AsNumber());
        Assert.Equal(0, allowed.Evaluate("gl.clearColor(1, 0.25, 0, 1); gl.clear(gl.COLOR_BUFFER_BIT); gl.getError()").AsNumber());
        Assert.True(allowed.Evaluate("let contextEvents = ''; gl.oncontextlost = e => contextEvents += e.type; gl.oncontextrestored = e => contextEvents += e.type; const ext = gl.getExtension('WEBGL_lose_context'); ext.loseContext(); gl.isContextLost() && contextEvents === 'webglcontextlost'").AsBoolean());
        Assert.Equal(0x9242, allowed.Evaluate("gl.getError()").AsNumber());
        Assert.False(allowed.Evaluate("ext.restoreContext(); gl.isContextLost() && contextEvents === 'webglcontextlostwebglcontextrestored'").AsBoolean());
        Assert.Equal("webglcontextlostwebglcontextrestored", allowed.Evaluate("contextEvents").AsString());
        var canvas = allowedDocument.Node.Descendants.OfType<ElementNode>().Single(element => element.LocalName == "canvas");
        Assert.Contains(canvas.CanvasSurface!.Commands, command => command.Name == "fillRect" && command.FillStyle.StartsWith("rgba(255", StringComparison.Ordinal));

        using var limited = new JintScriptEngine();
        var limitedDocument = Document.Parse("<canvas id='c'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true },
            Limits = ResourceLimits.Default with { MaxGpuBytes = 8 }
        });
        DomBindings.Install(limited, limitedDocument);
        Assert.Equal(0x0505, limited.Evaluate("const g = document.getElementById('c').getContext('webgl2'); const x = g.createBuffer(); g.bindBuffer(g.ARRAY_BUFFER, x); g.bufferData(g.ARRAY_BUFFER, 9, g.STATIC_DRAW); g.getError()").AsNumber());
        Assert.Equal(0, limited.Evaluate("const a = g.createBuffer(); g.bindBuffer(g.ARRAY_BUFFER, a); g.bufferData(g.ARRAY_BUFFER, 8, g.STATIC_DRAW); g.deleteBuffer(a); const b = g.createBuffer(); g.bindBuffer(g.ARRAY_BUFFER, b); g.bufferData(g.ARRAY_BUFFER, 8, g.STATIC_DRAW); g.getError()").AsNumber());
        Assert.Equal(0, limited.Evaluate("g.bufferData(g.ARRAY_BUFFER, 8, g.STATIC_DRAW); g.bufferData(g.ARRAY_BUFFER, 8, g.STATIC_DRAW); g.getError()").AsNumber());
    }

    [Fact]
    public void MediaElementsExposeCapabilityGatedPlaybackState()
    {
        using var denied = new JintScriptEngine();
        var deniedDocument = Document.Parse("<video id='v'></video>");
        DomBindings.Install(denied, deniedDocument);
        Assert.True(denied.Evaluate("const v = document.getElementById('v'); v.paused && v.readyState === 0 && v.canPlayType('video/mp4') === ''").AsBoolean());

        using var allowed = new JintScriptEngine();
        var allowedDocument = Document.Parse("<video id='v' data-duration='12' data-width='640' data-height='360'></video>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Media = true }
        });
        DomBindings.Install(allowed, allowedDocument);
        Assert.True(allowed.Evaluate("const v = document.getElementById('v'); let events = ''; let eventType = ''; v.onloadedmetadata = e => { events += 'm'; eventType = e.type; }; v.onplay = e => { events += 'p'; eventType = e.type; }; v.onpause = e => { events += 's'; eventType = e.type; }; v.onended = e => { events += 'e'; eventType = e.type; }; v.load(); v.paused && v instanceof HTMLMediaElement && v instanceof HTMLVideoElement && v.duration === 12 && v.readyState === 4 && events === 'm' && eventType === 'loadedmetadata'").AsBoolean());
        Assert.True(allowed.Evaluate("let observed = ''; const listener = e => observed = e.type; v.addEventListener('play', listener); v.play(); v.removeEventListener('play', listener); events = 'm'; observed === 'play'").AsBoolean());
        Assert.True(allowed.Evaluate("v.currentTime = 4; v.volume = 2; v.muted = true; v.play() instanceof Promise && !v.paused && v.currentTime === 4 && v.volume === 1 && v.muted").AsBoolean());
        Assert.True(allowed.Evaluate("v.preload = 'auto'; v.defaultMuted = true; v.crossOrigin = 'anonymous'; v.fastSeek(6.6); v.setSinkId('default') instanceof Promise && v.preload === 'auto' && v.defaultMuted && v.crossOrigin === 'anonymous' && v.currentTime === 7").AsBoolean());
        Assert.True(allowed.Evaluate("const captured = v.captureStream(); captured instanceof MediaStream && v.videoWidth === 640 && v.videoHeight === 360").AsBoolean());
        Assert.Equal("maybe", allowed.Evaluate("v.canPlayType('video/mp4')").AsString());
        Assert.True(allowed.Evaluate("v.pause(); v.paused").AsBoolean());
        Assert.True(allowed.Evaluate("events === 'mps' && eventType === 'pause'").AsBoolean());
        Assert.True(allowed.Evaluate("v.currentTime = 12; events === 'mpse'").AsBoolean());
        Assert.True(allowed.Evaluate("v.load(); v.currentTime === 0 && v.paused").AsBoolean());
        Assert.True(allowed.Evaluate("const captions = v.addTextTrack('captions', 'English', 'en'); captions.kind === 'captions' && captions.label === 'English'; captions.mode = 'showing'; captions.addCue(1, 2, 'Hello'); captions.mode === 'showing' && captions.cues[0].startTime === 1 && captions.cues[0].endTime === 2 && captions.cues[0].text === 'Hello'").AsBoolean());
        Assert.True(allowed.Evaluate("v.playbackRate = 1.5; v.defaultPlaybackRate = 1.25; v.preservesPitch = false; v.playbackRate === 1.5 && v.defaultPlaybackRate === 1.25 && !v.preservesPitch && v.buffered.length === 1 && v.buffered.start(0) === 0 && v.buffered.end(0) === 12").AsBoolean());
        Assert.True(allowed.Evaluate("const audio = new Audio('/sound.ogg'); audio instanceof HTMLAudioElement && audio.src === '/sound.ogg'").AsBoolean());
        Assert.True(allowed.Evaluate("const stream = new MediaStream([]); v.srcObject = stream; v.srcObject === stream").AsBoolean());

        using var limited = new JintScriptEngine();
        DomBindings.Install(limited, Document.Parse("<video id='v' data-size='1024'></video>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Media = true },
            Limits = ResourceLimits.Default with { MaxMediaBytes = 128 }
        }));
        Assert.True(limited.Evaluate("const v = document.getElementById('v'); v.readyState === 0 && v.error !== null; v.play(); v.paused").AsBoolean());

        using var aggregate = new JintScriptEngine();
        DomBindings.Install(aggregate, Document.Parse("<video id='a' data-size='80'></video><video id='b' data-size='80'></video>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Media = true },
            Limits = ResourceLimits.Default with { MaxMediaBytes = 128 }
        }));
        Assert.True(aggregate.Evaluate("const a = document.getElementById('a'); const b = document.getElementById('b'); a.error === null && b.error !== null").AsBoolean());
    }

    [Fact]
    public void WorkerIsCapabilityGatedAndDeliversBoundedMessages()
    {
        using var denied = new JintScriptEngine();
        DomBindings.Install(denied, Document.Parse("<html></html>"));
        Assert.ThrowsAny<Exception>(() => denied.Evaluate("new Worker('data:text/javascript,')"));

        using var allowed = new JintScriptEngine();
        DomBindings.Install(allowed, Document.Parse("<html></html>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Workers = true }
        }));
        Assert.True(allowed.Evaluate("const w = new Worker('https://worker.example/worker.js'); let seen = 0; let workerOrigin = ''; let sourceMatches = false; let eventType = ''; w.onmessage = e => { seen = e.data; workerOrigin = e.origin; sourceMatches = e.source === w && e.target === w; eventType = e.type; }; w.postMessage(42); seen === 42 && workerOrigin === 'https://worker.example' && sourceMatches && eventType === 'message'").AsBoolean());
        Assert.True(allowed.Evaluate("const isolated = new Worker('data:text/javascript,' + encodeURIComponent('onmessage = function(e) { postMessage(e.data + 1); };')); let isolatedSeen = 0; isolated.onmessage = e => isolatedSeen = e.data; isolated.postMessage(41); isolatedSeen === 42").AsBoolean());
        Assert.True(allowed.Evaluate("const broken = new Worker('data:text/javascript,' + encodeURIComponent('throw new Error(\\\"boom\\\");')); let workerError = ''; broken.onerror = e => workerError = e.message; broken.postMessage(1); workerError === 'boom'").AsBoolean());
        Assert.True(allowed.Evaluate("let listenerSeen = 0; const listener = e => listenerSeen = e.data; w.addEventListener('message', listener); w.postMessage(43); w.removeEventListener('message', listener); listenerSeen === 43").AsBoolean());
        Assert.Equal("https://worker.example/worker.js", allowed.Evaluate("w.scriptURL").AsString());
        Assert.ThrowsAny<Exception>(() => allowed.Evaluate("w.terminate(); w.postMessage(7)"));

        using var limited = new JintScriptEngine();
        DomBindings.Install(limited, Document.Parse("<html></html>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Workers = true },
            Limits = ResourceLimits.Default with { MaxWorkerMessageBytes = 4 }
        }));
        Assert.ThrowsAny<Exception>(() => limited.Evaluate("const w = new Worker('data:text/javascript,'); w.postMessage('12345')"));
        Assert.ThrowsAny<Exception>(() => limited.Evaluate("w.terminate(); w.postMessage('ok')"));
    }

    [Fact]
    public void WorkerLoadsScriptThroughConfiguredResourceLoader()
    {
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, Document.Parse("<html></html>", new FolioOptions
        {
            BaseUri = new Uri("https://worker.example/app/"),
            ResourceLoader = new WorkerResourceLoader(),
            Capabilities = new FeatureCapabilities { Workers = true }
        }));
        Assert.True(engine.Evaluate("const loaded = new Worker('worker.js'); let result = 0; loaded.onmessage = e => result = e.data; loaded.postMessage(40); result === 42").AsBoolean());
    }

    [Fact]
    public void WebAssemblySurfaceValidatesModulesOnlyWhenGranted()
    {
        using var denied = new JintScriptEngine();
        DomBindings.Install(denied, Document.Parse("<html></html>"));
        Assert.False(denied.Evaluate("WebAssembly.validate(new Uint8Array([0,97,115,109,1,0,0,0]))").AsBoolean());

        using var allowed = new JintScriptEngine();
        DomBindings.Install(allowed, Document.Parse("<html></html>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebAssembly = true }
        }));
        Assert.True(allowed.Evaluate("WebAssembly.validate(new Uint8Array([0,97,115,109,1,0,0,0]))").AsBoolean());
        Assert.False(allowed.Evaluate("WebAssembly.validate(new Uint8Array([1,2,3]))").AsBoolean());
        Assert.True(allowed.Evaluate("const module = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0])); const instance = new WebAssembly.Instance(module, {}); module instanceof WebAssembly.Module && instance.exports !== null && WebAssembly.Module.exports(module).length === 0 && WebAssembly.Module.imports(module).length === 0").AsBoolean());
        Assert.True(allowed.Evaluate("const exported = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,7,7,1,3,114,117,110,0,0])); const exportedInstance = new WebAssembly.Instance(exported, {}); WebAssembly.Module.exports(exported)[0].name === 'run' && WebAssembly.Module.exports(exported)[0].kind === 0 && typeof exportedInstance.exports.run === 'function'").AsBoolean());
        Assert.Equal(42, allowed.Evaluate("const executable = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,1,5,1,96,0,1,127,3,2,1,0,7,7,1,3,114,117,110,0,0,10,6,1,4,0,65,42,11])); new WebAssembly.Instance(executable, {}).exports.run()").AsNumber());
        Assert.Equal(7, allowed.Evaluate("const adding = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,1,7,1,96,2,127,127,1,127,3,2,1,0,7,7,1,3,115,117,109,0,0,10,9,1,7,0,32,0,32,1,106,11])); new WebAssembly.Instance(adding, {}).exports.sum(3, 4)").AsNumber());
        Assert.Equal(-1, allowed.Evaluate("const subtracting = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,1,7,1,96,2,127,127,1,127,3,2,1,0,7,8,1,4,115,117,98,116,0,0,10,9,1,7,0,32,0,32,1,107,11])); new WebAssembly.Instance(subtracting, {}).exports.subt(3, 4)").AsNumber());
        Assert.Equal(1, allowed.Evaluate("const comparing = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,1,7,1,96,2,127,127,1,127,3,2,1,0,7,6,1,2,108,116,0,0,10,9,1,7,0,32,0,32,1,72,11])); new WebAssembly.Instance(comparing, {}).exports.lt(3, 4)").AsNumber());
        Assert.Equal(1, allowed.Evaluate("const equality = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,1,7,1,96,2,127,127,1,127,3,2,1,0,7,6,1,2,101,113,0,0,10,9,1,7,0,32,0,32,1,70,11])); new WebAssembly.Instance(equality, {}).exports.eq(4, 4)").AsNumber());
        Assert.Equal(31, allowed.Evaluate("const bitcount = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,1,6,1,96,1,127,1,127,3,2,1,0,7,9,1,5,99,108,122,51,50,0,0,10,7,1,5,0,32,0,103,11])); new WebAssembly.Instance(bitcount, {}).exports.clz32(1)").AsNumber());
        Assert.Equal(2, allowed.Evaluate("const rotate = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,1,7,1,96,2,127,127,1,127,3,2,1,0,7,7,1,3,114,111,116,0,0,10,9,1,7,0,32,0,32,1,119,11])); new WebAssembly.Instance(rotate, {}).exports.rot(1, 1)").AsNumber());
        Assert.Equal(-1, allowed.Evaluate("const extend = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,1,5,1,96,0,1,127,3,2,1,0,7,6,1,2,101,56,0,0,10,8,1,6,0,65,255,1,192,11])); new WebAssembly.Instance(extend, {}).exports.e8()").AsNumber());
        Assert.Equal(42, allowed.Evaluate("const locals = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,1,5,1,96,0,1,127,3,2,1,0,7,7,1,3,114,117,110,0,0,10,15,1,13,1,1,127,65,41,33,0,32,0,65,1,106,11])); new WebAssembly.Instance(locals, {}).exports.run()").AsNumber());
        Assert.ThrowsAny<Exception>(() => allowed.Evaluate("const imported = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,2,10,1,3,101,110,118,2,102,110,0,0])); new WebAssembly.Instance(imported, {})"));
        Assert.True(allowed.Evaluate("const importedInstance = new WebAssembly.Instance(imported, { env: { fn: function() {} } }); WebAssembly.Module.imports(imported)[0].module === 'env' && WebAssembly.Module.imports(imported)[0].name === 'fn'").AsBoolean());
        Assert.Equal(9, allowed.Evaluate("const reexport = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,2,10,1,3,101,110,118,2,102,110,0,0,7,7,1,2,102,110,0,0])); new WebAssembly.Instance(reexport, { env: { fn: function() { return 9; } } }).exports.fn()").AsNumber());
        Assert.True(allowed.Evaluate("const memoryImport = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,2,12,1,3,101,110,118,3,109,101,109,2,0,1,7,7,1,3,109,101,109,2,0])); const suppliedMemory = new WebAssembly.Memory({ initial: 1 }); new WebAssembly.Instance(memoryImport, { env: { mem: suppliedMemory } }).exports.mem === suppliedMemory").AsBoolean());
        Assert.True(allowed.Evaluate("const localMemory = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,5,3,1,0,1,7,7,1,3,109,101,109,2,0])); const localMemoryInstance = new WebAssembly.Instance(localMemory, {}); localMemoryInstance.exports.mem instanceof WebAssembly.Memory && localMemoryInstance.exports.mem.buffer.byteLength === 65536").AsBoolean());
        Assert.Equal(7, allowed.Evaluate("const memoryCode = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,1,5,1,96,0,1,127,3,2,1,0,5,3,1,0,1,7,14,2,4,108,111,97,100,0,0,3,109,101,109,2,0,10,9,1,7,0,65,0,40,0,0,11])); const memoryInstance = new WebAssembly.Instance(memoryCode, {}); new Uint8Array(memoryInstance.exports.mem.buffer)[0] = 7; memoryInstance.exports.load()").AsNumber());
        Assert.Equal(42, allowed.Evaluate("const memoryStore = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,1,5,1,96,0,1,127,3,2,1,0,5,3,1,0,1,7,14,2,4,115,116,111,114,0,0,3,109,101,109,2,0,10,16,1,14,0,65,0,65,42,54,0,0,65,0,40,0,0,11])); new WebAssembly.Instance(memoryStore, {}).exports.stor()").AsNumber());
        Assert.Equal(42, allowed.Evaluate("const memoryNarrowStore = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,1,5,1,96,0,1,127,3,2,1,0,5,3,1,0,1,7,14,2,4,115,116,111,114,0,0,3,109,101,109,2,0,10,16,1,14,0,65,0,65,42,58,0,0,65,0,40,0,0,11])); new WebAssembly.Instance(memoryNarrowStore, {}).exports.stor()").AsNumber());
        Assert.Equal(1, allowed.Evaluate("const memorySize = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,1,5,1,96,0,1,127,3,2,1,0,5,3,1,0,1,7,14,2,4,115,105,122,101,0,0,3,109,101,109,2,0,10,6,1,4,0,63,0,11])); new WebAssembly.Instance(memorySize, {}).exports.size()").AsNumber());
        Assert.Equal(1, allowed.Evaluate("const memoryGrow = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,1,5,1,96,0,1,127,3,2,1,0,5,3,1,0,1,7,14,2,4,103,114,111,119,0,0,3,109,101,109,2,0,10,8,1,6,0,65,1,64,0,11])); new WebAssembly.Instance(memoryGrow, {}).exports.grow()").AsNumber());
        Assert.Equal(42, allowed.Evaluate("const branching = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,1,6,1,96,1,127,1,127,3,2,1,0,7,8,1,4,112,105,99,107,0,0,10,13,1,11,0,32,0,4,127,65,42,5,65,7,11])); new WebAssembly.Instance(branching, {}).exports.pick(1)").AsNumber());
        Assert.Equal(42, allowed.Evaluate("const control = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,1,5,1,96,0,1,127,3,2,1,0,7,7,1,3,114,117,110,0,0,10,10,1,8,0,65,99,26,65,42,15,11])); new WebAssembly.Instance(control, {}).exports.run()").AsNumber());
        Assert.Equal(9, allowed.Evaluate("const callImport = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,1,5,1,96,0,1,127,2,10,1,3,101,110,118,2,102,110,0,0,3,2,1,0,7,7,1,3,114,117,110,0,1,10,6,1,4,0,16,0,11])); new WebAssembly.Instance(callImport, { env: { fn: function() { return 9; } } }).exports.run()").AsNumber());
        Assert.Equal(13, allowed.Evaluate("const callImportArg = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,1,6,1,96,1,127,1,127,2,10,1,3,101,110,118,2,102,110,0,0,3,2,1,0,7,7,1,3,114,117,110,0,1,10,8,1,6,0,65,4,16,0,11])); new WebAssembly.Instance(callImportArg, { env: { fn: function(value) { return value + 9; } } }).exports.run()").AsNumber());
        Assert.Equal(7, allowed.Evaluate("const localCall = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,1,5,1,96,0,1,127,3,3,2,0,0,7,7,1,3,114,117,110,0,0,10,11,2,4,0,16,1,11,4,0,65,7,11])); new WebAssembly.Instance(localCall, {}).exports.run()").AsNumber());
        Assert.Equal(12, allowed.Evaluate("const indirect = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,1,5,1,96,0,1,127,2,13,1,3,101,110,118,3,116,97,98,1,112,0,1,3,2,1,0,7,7,1,3,114,117,110,0,0,10,9,1,7,0,65,0,17,0,0,11])); const indirectTable = new WebAssembly.Table({ initial: 1 }); indirectTable.set(0, function() { return 12; }); new WebAssembly.Instance(indirect, { env: { tab: indirectTable } }).exports.run()").AsNumber());
        Assert.Equal(9, allowed.Evaluate("const globalCode = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,1,5,1,96,0,1,127,2,10,1,3,101,110,118,1,103,3,127,1,3,2,1,0,7,7,1,3,114,117,110,0,0,10,10,1,8,0,65,9,36,0,35,0,11])); const globalValue = new WebAssembly.Global({ value: 'i32', mutable: true }, 2); new WebAssembly.Instance(globalCode, { env: { g: globalValue } }).exports.run()").AsNumber());
        Assert.True(allowed.Evaluate("const bulk = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,1,5,1,96,0,1,127,3,2,1,0,5,3,1,0,1,7,14,2,4,102,105,108,108,0,0,3,109,101,109,2,0,10,29,1,27,0,65,0,65,7,65,4,252,11,0,65,4,65,0,65,4,252,10,0,0,65,4,40,0,0,11])); const bulkInstance = new WebAssembly.Instance(bulk, {}); bulkInstance.exports.fill() === 117901063 && new Uint8Array(bulkInstance.exports.mem.buffer)[7] === 7").AsBoolean());
        Assert.Equal(11, allowed.Evaluate("const selectCode = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,1,5,1,96,0,1,127,3,2,1,0,7,7,1,3,114,117,110,0,0,10,11,1,9,0,65,11,65,22,65,1,27,11])); new WebAssembly.Instance(selectCode, {}).exports.run()").AsNumber());
        Assert.Equal(1, allowed.Evaluate("const branchCode = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,1,5,1,96,0,1,127,3,2,1,0,7,7,1,3,114,117,110,0,0,10,12,1,10,0,65,1,65,1,13,0,65,99,11])); new WebAssembly.Instance(branchCode, {}).exports.run()").AsNumber());
        Assert.Equal(7, allowed.Evaluate("const tableBranch = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,1,5,1,96,0,1,127,3,2,1,0,7,7,1,3,114,117,110,0,0,10,14,1,12,0,65,7,65,0,14,1,0,0,65,99,11])); new WebAssembly.Instance(tableBranch, {}).exports.run()").AsNumber());
        Assert.ThrowsAny<Exception>(() => allowed.Evaluate("const trapCode = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,1,5,1,96,0,1,127,3,2,1,0,7,7,1,3,114,117,110,0,0,10,5,1,3,0,0,11])); new WebAssembly.Instance(trapCode, {}).exports.run()"));
        Assert.True(allowed.Evaluate("const tableImport = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,2,13,1,3,101,110,118,3,116,97,98,1,112,0,1,7,7,1,3,116,97,98,1,0])); const suppliedTable = new WebAssembly.Table({ initial: 1 }); new WebAssembly.Instance(tableImport, { env: { tab: suppliedTable } }).exports.tab === suppliedTable").AsBoolean());
        Assert.True(allowed.Evaluate("const localTable = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,4,4,1,112,0,1,7,7,1,3,116,97,98,1,0])); const localTableInstance = new WebAssembly.Instance(localTable, {}); localTableInstance.exports.tab instanceof WebAssembly.Table && localTableInstance.exports.tab.length === 1").AsBoolean());
        Assert.True(allowed.Evaluate("const globalImport = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,2,10,1,3,101,110,118,1,103,3,127,0,7,6,1,1,103,3,0])); const suppliedGlobal = new WebAssembly.Global({ value: 'i32', mutable: true }, 7); const globalInstance = new WebAssembly.Instance(globalImport, { env: { g: suppliedGlobal } }); globalInstance.exports.g === suppliedGlobal && globalInstance.exports.g.value === 7").AsBoolean());
        Assert.True(allowed.Evaluate("const localGlobal = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,6,6,1,127,0,65,42,11,7,6,1,1,103,3,0])); const localGlobalInstance = new WebAssembly.Instance(localGlobal, {}); localGlobalInstance.exports.g instanceof WebAssembly.Global && localGlobalInstance.exports.g.value === 42 && localGlobalInstance.exports.g.mutable === false").AsBoolean());
        Assert.True(allowed.Evaluate("const custom = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,0,7,4,109,101,116,97,42])); WebAssembly.Module.customSections(custom, 'meta').length === 1 && WebAssembly.Module.customSections(custom, 'meta')[0][0] === 42 && WebAssembly.Module.customSections(custom, 'other').length === 0").AsBoolean());
        Assert.ThrowsAny<Exception>(() => allowed.Evaluate("new WebAssembly.Instance(memoryImport, { env: { mem: new WebAssembly.Table({ initial: 1 }) } })"));
        Assert.ThrowsAny<Exception>(() => allowed.Evaluate("new WebAssembly.Instance(tableImport, { env: { tab: new WebAssembly.Memory({ initial: 1 }) } })"));
        Assert.True(allowed.Evaluate("const memory = new WebAssembly.Memory({ initial: 1, maximum: 2 }); memory.buffer.byteLength === 65536 && memory.grow(1) === 1 && memory.buffer.byteLength === 131072").AsBoolean());
        Assert.True(allowed.Evaluate("const table = new WebAssembly.Table({ initial: 1, maximum: 2 }); table.set(0, 7); table.get(0) === 7 && table.grow(1) === 1 && table.length === 2").AsBoolean());
        Assert.True(allowed.Evaluate("const global = new WebAssembly.Global({ value: 'i32', mutable: true }, 4); global.valueOf() === 4; global.value = 9; global.value === 9").AsBoolean());
        Assert.ThrowsAny<Exception>(() => allowed.Evaluate("new WebAssembly.Memory({ initial: 1, maximum: 2 }).grow(2)"));
    }

    [Fact]
    public void ServiceWorkerRegistrationIsCapabilityGated()
    {
        using var denied = new JintScriptEngine();
        DomBindings.Install(denied, Document.Parse("<html></html>"));
        Assert.True(denied.Evaluate("typeof navigator.serviceWorker.register === 'function'").AsBoolean());
        Assert.True(denied.Evaluate("navigator.serviceWorker.controller === null").AsBoolean());

        using var allowed = new JintScriptEngine();
        DomBindings.Install(allowed, Document.Parse("<html></html>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { ServiceWorkers = true }
        }));
        Assert.True(allowed.Evaluate("navigator.serviceWorker.register('/sw.js').then(r => r.scriptURL === '/sw.js')").IsObject);
        allowed.Evaluate("let registrationCount = 0; let registration; navigator.serviceWorker.register('/sw.js').then(r => { registration = r; return navigator.serviceWorker.getRegistrations(); }).then(rs => registrationCount = rs.length);");
        allowed.PerformMicrotaskCheckpoint();
        Assert.Equal(1, allowed.Evaluate("registrationCount").AsNumber());
        Assert.True(allowed.Evaluate("registration.active.scriptURL === '/sw.js' && registration.active.state === 'activated'; registration.active.postMessage('ping'); true").AsBoolean());
        Assert.True(allowed.Evaluate("let workerMessage = ''; const workerListener = e => workerMessage = e.data; registration.active.addEventListener('message', workerListener); registration.active.postMessage('pong'); registration.active.removeEventListener('message', workerListener); workerMessage === 'pong'").AsBoolean());
        Assert.True(allowed.Evaluate("navigator.serviceWorker.controller === registration.active").AsBoolean());
        Assert.True(allowed.Evaluate("let controllerChanges = 0; let listenerChanges = 0; navigator.serviceWorker.oncontrollerchange = e => { if (e.type === 'controllerchange' && e.target === navigator.serviceWorker) controllerChanges++; }; const listener = e => listenerChanges++; navigator.serviceWorker.addEventListener('controllerchange', listener); registration.unregister(); navigator.serviceWorker.removeEventListener('controllerchange', listener); controllerChanges === 1 && listenerChanges === 1 && navigator.serviceWorker.controller === null").AsBoolean());
        Assert.True(allowed.Evaluate("let updateFound = false; registration.onupdatefound = e => updateFound = e.type === 'updatefound' && e.target === registration; registration.update(); updateFound").AsBoolean());
        Assert.True(allowed.Evaluate("let updateEvents = 0; const updateListener = e => { if (e.type === 'updatefound' && e.target === registration) updateEvents++; }; registration.addEventListener('updatefound', updateListener); registration.update(); registration.removeEventListener('updatefound', updateListener); updateEvents === 1").AsBoolean());
        allowed.Evaluate("let readyScope = ''; navigator.serviceWorker.ready.then(r => readyScope = r.scope);");
        allowed.PerformMicrotaskCheckpoint();
        Assert.Equal("/", allowed.Evaluate("readyScope").AsString());
        Assert.True(allowed.Evaluate("registration.unregister()").IsObject);
        allowed.PerformMicrotaskCheckpoint();
        Assert.True(allowed.Evaluate("navigator.serviceWorker.controller === null").AsBoolean());
    }

    [Fact]
    public void WebRtcPeerConnectionIsCapabilityGated()
    {
        using var denied = new JintScriptEngine();
        DomBindings.Install(denied, Document.Parse("<html></html>"));
        Assert.ThrowsAny<Exception>(() => denied.Evaluate("new RTCPeerConnection()"));
        Assert.True(denied.Evaluate("typeof navigator.mediaDevices.getUserMedia === 'function'").AsBoolean());

        using var allowed = new JintScriptEngine();
        DomBindings.Install(allowed, Document.Parse("<html></html>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebRtc = true }
        }));
        Assert.True(allowed.Evaluate("const pc = new RTCPeerConnection(); let iceEvents = 0; let stateEvents = 0; let candidates = 0; pc.onicegatheringstatechange = () => iceEvents++; pc.onicecandidate = e => candidates++; pc.onconnectionstatechange = () => stateEvents++; pc.createOffer().then(x => x.type === 'offer' && x instanceof RTCSessionDescription)").IsObject);
        Assert.True(allowed.Evaluate("const candidate = new RTCIceCandidate({ candidate: 'candidate:1', sdpMid: '0', sdpMLineIndex: 0 }); const description = new RTCSessionDescription({ type: 'offer', sdp: '' }); candidate.candidate === 'candidate:1' && description.type === 'offer'").AsBoolean());
        Assert.True(allowed.Evaluate("JSON.stringify(candidate.toJSON()).includes('candidate:1') && description.toJSON().type === 'offer'").AsBoolean());
        Assert.True(allowed.Evaluate("RTCRtpSender.getCapabilities('audio').codecs[0].mimeType === 'audio/opus' && RTCRtpReceiver.getCapabilities('video').codecs.length === 2").AsBoolean());
        Assert.True(allowed.Evaluate("navigator.mediaDevices.getUserMedia({ audio: true }).then(stream => stream instanceof MediaStream && stream.getTracks().length === 1 && stream.getAudioTracks().length === 1)").IsObject);
        Assert.True(allowed.Evaluate("const track = new MediaStreamTrack('audio'); const stream = new MediaStream([track]); const copied = new MediaStream(stream); let changes = ''; stream.onaddtrack = e => changes += e.type + e.track.kind; stream.onremovetrack = e => changes += e.type === 'removetrack' ? '-' : '?'; track.onmute = e => changes += e.type[0]; track.onunmute = e => changes += e.type[0]; track.onended = e => changes += e.type[0]; const second = new MediaStreamTrack('video'); stream.addTrack(second); stream.removeTrack(second); track.muted = true; track.muted = false; track.enabled = false; track.contentHint = 'speech'; stream.getAudioTracks().length === 1 && copied.getTracks().length === 1 && stream.active; track.applyConstraints({ width: 640 }); const clone = track.clone(); track.getConstraints().width === 640 && track.getSettings().kind === 'audio' && track.getCapabilities().kind === 'audio'; track.stop(); let rejected = false; track.applyConstraints({ width: 320 }).catch(() => rejected = true); clone.readyState === 'live' && !clone.enabled && clone.contentHint === 'speech' && clone.getConstraints().width === 640 && track.readyState === 'ended' && stream.id.length > 0 && changes === 'addtrackvideo-mue'").AsBoolean());
        allowed.PerformMicrotaskCheckpoint();
        Assert.True(allowed.Evaluate("rejected").AsBoolean());
        Assert.True(allowed.Evaluate("pc.iceGatheringState === 'new' && pc.getConfiguration() !== null; pc.setLocalDescription({ type: 'offer' }); pc.iceGatheringState === 'complete'; pc.restartIce(); pc.iceGatheringState === 'new'").AsBoolean());
        Assert.True(allowed.Evaluate("const remote = pc.addTransceiver('video'); let remoteKind = ''; pc.ontrack = e => remoteKind = e.track.kind; pc.signalingState === 'have-local-offer' && candidates === 1; pc.setRemoteDescription({ type: 'answer' }); pc.signalingState === 'stable' && remoteKind === 'video'").AsBoolean());
        Assert.True(allowed.Evaluate("let negotiations = 0; pc.onnegotiationneeded = e => { if (e.type === 'negotiationneeded' && e.target === pc) negotiations++; }; const sender = pc.addTrack({ kind: 'audio' }); sender.setParameters({ encodings: [{ maxBitrate: 64000 }] }); pc.getSenders().length === 1 && sender.track.kind === 'audio' && sender.getParameters().encodings.length === 1 && negotiations === 1").AsBoolean());
        Assert.True(allowed.Evaluate("const transceiver = pc.addTransceiver('video', { direction: 'recvonly' }); transceiver.setCodecPreferences([{ mimeType: 'video/VP8' }]); transceiver.kind === 'video' && transceiver.direction === 'recvonly' && transceiver.codecPreferences.length === 1 && pc.getTransceivers().length === 2; transceiver.stop(); transceiver.direction === 'inactive'").AsBoolean());
        Assert.True(allowed.Evaluate("const sending = pc.addTransceiver('audio'); sending.sender.setParameters({ encodings: [{ maxBitrate: 32000 }] }); sending.sender.getParameters().encodings.length === 1; sending.sender.replaceTrack({ kind: 'audio' })").IsObject);
        allowed.PerformMicrotaskCheckpoint();
        Assert.True(allowed.Evaluate("sending.sender.track.kind === 'audio'").AsBoolean());
        Assert.True(allowed.Evaluate("pc.setConfiguration({ iceServers: [] }); pc.getConfiguration().iceServers.length === 0; pc.getStats().then(stats => stats instanceof Map)").IsObject);
        Assert.True(allowed.Evaluate("const dc = pc.createDataChannel('events'); let received = 0; let opened = 0; let closed = false; let eventTypes = ''; dc.onopen = e => { opened++; eventTypes += e.type; }; dc.onmessage = e => { received = e.data; eventTypes += e.target === dc ? e.type : '?'; }; dc.onclose = e => { closed = true; eventTypes += e.type; }; dc.send(7); received === 7 && opened === 1 && eventTypes === 'openmessage' && dc.bufferedAmount === 0 && dc.binaryType === 'blob'; dc.close(); closed && dc.readyState === 'closed' && eventTypes === 'openmessageclose'").AsBoolean());
        Assert.True(allowed.Evaluate("const configured = pc.createDataChannel('configured', { ordered: false, maxRetransmits: 2 }); let observed = 0; const handler = e => { observed = e.data; }; configured.addEventListener('message', handler); configured.send('ok'); configured.removeEventListener('message', handler); configured.send('ignored'); configured.ordered === false && configured.maxRetransmits === 2 && observed === 'ok'").AsBoolean());
        Assert.Equal("closed", allowed.Evaluate("pc.close(); pc.connectionState").AsString());
        allowed.Evaluate("let replaceRejected = false; sender.replaceTrack({ kind: 'video' }).catch(() => replaceRejected = true);");
        allowed.PerformMicrotaskCheckpoint();
        Assert.True(allowed.Evaluate("replaceRejected").AsBoolean());
        Assert.True(allowed.Evaluate("iceEvents >= 1 && stateEvents === 1").AsBoolean());
    }

    [Fact]
    public void IframeContentWindowMessagingIsCapabilityGated()
    {
        using var denied = new JintScriptEngine();
        DomBindings.Install(denied, Document.Parse("<iframe id='f' src='/child.html'></iframe>"));
        Assert.True(denied.Evaluate("const f = document.getElementById('f'); f.src === '/child.html' && f.contentDocument === null").AsBoolean());

        using var allowed = new JintScriptEngine();
        DomBindings.Install(allowed, Document.Parse("<iframe id='f' src='https://child.example/app'></iframe>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Iframes = true }
        }));
        Assert.True(allowed.Evaluate("const f = document.getElementById('f'); let seen = 0; let messageOrigin = ''; let messageSource = false; let messageTarget = false; f.contentWindow.onmessage = x => { seen = x.data; messageOrigin = x.origin; messageSource = x.source === f.contentWindow; messageTarget = x.target === f.contentWindow; }; f.contentWindow.postMessage(8, 'https://other.example'); seen === 0").AsBoolean());
        Assert.True(allowed.Evaluate("f.contentWindow.postMessage(9, 'https://child.example'); seen === 9 && messageOrigin === 'https://child.example' && messageSource && messageTarget && f.contentWindow.origin === 'https://child.example'").AsBoolean());
        Assert.True(allowed.Evaluate("let listenerSeen = 0; const listener = e => listenerSeen = e.data; f.contentWindow.addEventListener('message', listener); f.contentWindow.postMessage(10, 'https://child.example'); f.contentWindow.removeEventListener('message', listener); listenerSeen === 10").AsBoolean());
        Assert.True(allowed.Evaluate("f.contentWindow.close(); f.contentWindow.closed").AsBoolean());
        Assert.True(allowed.Evaluate("f instanceof HTMLIFrameElement").AsBoolean());
    }

    [Fact]
    public void CanvasImageDataArrayMutationsAreAppliedByPutImageData()
    {
        var document = TreeBuilder.Parse("<canvas id='chart' width='1' height='1'></canvas>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        var result = engine.Evaluate(@"
            const ctx = document.getElementById('chart').getContext('2d');
            const image = ctx.createImageData(1, 1);
            image.data[0] = 255; image.data[3] = 255;
            ctx.putImageData(image, 0, 0);
            const copy = ctx.getImageData(0, 0, 1, 1);
            `${copy.data[0]},${copy.data[3]}`;");

        Assert.Equal("255,255", result.AsString());
    }

    [Fact]
    public void CanvasSupportsFunctionalColorsAndArcTo()
    {
        var document = TreeBuilder.Parse("<canvas id='chart'></canvas>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        var result = engine.Evaluate(@"
            const ctx = document.getElementById('chart').getContext('2d');
            ctx.fillStyle = 'hsl(120, 100%, 50%)';
            ctx.beginPath(); ctx.moveTo(1, 1); ctx.arcTo(10, 1, 10, 10, 2); ctx.fill();
            ctx.fillStyle = 'rgba(255, 0, 0, 0.5)';
            ctx.fillRect(2, 3, 4, 5);
            ctx.fillStyle;");

        Assert.Equal("rgba(255, 0, 0, 0.5)", result.AsString());
    }

    [Fact]
    public void CanvasContextSupportsStateAndTransformOperations()
    {
        var document = TreeBuilder.Parse("<canvas id='chart'></canvas>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        var result = engine.Evaluate(@"
            const ctx = document.getElementById('chart').getContext('2d');
            ctx.lineWidth = 3; ctx.lineCap = 'round'; ctx.globalAlpha = 0.5;
            ctx.setLineDash([2, 4]);
            ctx.save(); ctx.fillStyle = 'red'; ctx.translate(10, 20); ctx.restore();
            `${ctx.lineWidth}|${ctx.lineCap}|${ctx.globalAlpha}|${ctx.fillStyle}|${ctx.getLineDash().join(',')}`;");
        Assert.Equal("3|round|0.5|#000000|2,4", result.AsString());
    }

    [Fact]
    public void CssSupportsAndEscapeAreAvailable()
    {
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, TreeBuilder.Parse("<html></html>"));
        Assert.True(engine.Evaluate("CSS.supports('display', 'grid')").AsBoolean());
        Assert.Equal("\\31 2abc", engine.Evaluate("CSS.escape('12abc')").AsString());
    }

    [Fact]
    public void CssStyleSheetsExposeRulesAndMutation()
    {
        var document = TreeBuilder.Parse("<html><head><style>.a { color: red; }</style></head><body></body></html>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        Assert.Equal(1, engine.Evaluate("document.styleSheets.length").AsNumber());
        Assert.Contains(".a", engine.Evaluate("document.styleSheets[0].cssRules[0].cssText").AsString());
        Assert.Equal(".a", engine.Evaluate("document.styleSheets[0].cssRules[0].selectorText").AsString());
        Assert.Equal("red", engine.Evaluate("document.styleSheets[0].cssRules[0].style.getPropertyValue('color')").AsString());
        Assert.Equal(1, engine.Evaluate("document.styleSheets[0].insertRule('.b { color: blue; }')").AsNumber());
        Assert.Equal(2, engine.Evaluate("document.styleSheets[0].cssRules.length").AsNumber());
        engine.Evaluate("document.styleSheets[0].deleteRule(0)");
        Assert.Equal(1, engine.Evaluate("document.styleSheets[0].cssRules.length").AsNumber());
        engine.Evaluate("document.querySelector('style').textContent = '.c { color: green; }'");
        Assert.Contains(".c", engine.Evaluate("document.styleSheets[0].cssRules[0].cssText").AsString());
    }

    [Fact]
    public void FetchAllowsDataUrlsAndRejectsNetworkUrls()
    {
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, TreeBuilder.Parse("<html></html>"));
        Assert.Equal("function", engine.Evaluate("typeof fetch").AsString());
        engine.Evaluate("globalThis.__fetchBody = ''; fetch('data:text/plain,hello').then(r => r.text()).then(v => globalThis.__fetchBody = v);");
        engine.PerformMicrotaskCheckpoint();
        Assert.Equal("hello", engine.Evaluate("__fetchBody").AsString());
        engine.Evaluate("globalThis.__fetchRejected = false; fetch('https://example.test/').catch(e => globalThis.__fetchRejected = e instanceof TypeError);");
        engine.PerformMicrotaskCheckpoint();
        Assert.True(engine.Evaluate("__fetchRejected").AsBoolean());
    }

    [Fact]
    public void InvalidPropertyValueDoesNotReplaceExistingValue()
    {
        var document = TreeBuilder.Parse("<div id='box' style='width: 10px'></div>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        engine.Evaluate("const box = document.getElementById('box'); box.style.width = 'not-a-length';");
        Assert.Equal("10px", engine.Evaluate("box.style.width").AsString());
    }

    [Fact]
    public void ComputedStyleAndInnerTextAreExposed()
    {
        var document = TreeBuilder.Parse("<div id='box'>Hello<br>world<span>!</span></div>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        Assert.Equal("Hello\nworld!", engine.Evaluate("const box = document.getElementById('box'); box.innerText").AsString());
        Assert.Equal("inline", engine.Evaluate("getComputedStyle(box).display").AsString());
        Assert.Equal("", engine.Evaluate("getComputedStyle(box).getPropertyPriority('display')").AsString());
    }

    [Fact]
    public void GeometryAndViewportApisAreExposed()
    {
        var document = TreeBuilder.Parse("<div id='box' style='width: 20px; height: 10px'></div>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        Assert.Equal(20, engine.Evaluate("const box = document.getElementById('box'); box.getBoundingClientRect().width").AsNumber());
        Assert.Equal(10, engine.Evaluate("box.offsetHeight").AsNumber());
        Assert.Equal(800, engine.Evaluate("innerWidth").AsNumber());
        Assert.Equal(1, engine.Evaluate("devicePixelRatio").AsNumber());
        Assert.True(engine.Evaluate("document.elementFromPoint(5, 5) === box").AsBoolean());
        Assert.Equal(2, engine.Evaluate("scrollTo(2, 3); scrollX").AsNumber());
        Assert.Equal(4, engine.Evaluate("scrollBy(1, 1); scrollY").AsNumber());
        Assert.Equal(3, engine.Evaluate("pageXOffset").AsNumber());
    }

    [Fact]
    public void ScrollPropertiesAndMethodsAreExposed()
    {
        var document = TreeBuilder.Parse("<div id='box' style='width: 20px; height: 10px'></div>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        engine.Evaluate("const box = document.getElementById('box'); box.scrollTo(3, 4); box.scrollBy(2, 1);");
        Assert.Equal(5, engine.Evaluate("box.scrollLeft").AsNumber());
        Assert.Equal(5, engine.Evaluate("box.scrollTop").AsNumber());
    }

    [Fact]
    public void MatchMediaAndViewportValuesAreExposed()
    {
        var document = TreeBuilder.Parse("<div></div>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        Assert.True(engine.Evaluate("matchMedia('(min-width: 400px)').matches").AsBoolean());
        Assert.False(engine.Evaluate("matchMedia('(min-width: 900px)').matches").AsBoolean());
        Assert.Equal("(max-width: 900px)", engine.Evaluate("matchMedia('(max-width: 900px)').media").AsString());
    }

    [Fact]
    public void ObserverApisCanBeConstructedAndManaged()
    {
        var document = TreeBuilder.Parse("<div id='box'></div>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        Assert.True(engine.Evaluate("const box = document.getElementById('box'); const ro = new ResizeObserver(()=>{}); ro.observe(box); ro.unobserve(box); ro.disconnect(); true").AsBoolean());
        Assert.Equal("0.5px", engine.Evaluate("const io = new IntersectionObserver(()=>{}, {threshold: [0, .5]}); io.observe(box); io.thresholds[1] + 'px'").AsString());
    }

    [Fact]
    public void FormDataSupportsEntryOperations()
    {
        var document = TreeBuilder.Parse("<div></div>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        Assert.Equal("a,b", engine.Evaluate("const fd = new FormData(); fd.append('tag','a'); fd.append('tag','b'); fd.getAll('tag').join(',')").AsString());
        Assert.Equal("c", engine.Evaluate("fd.set('tag','c'); fd.get('tag')").AsString());
        Assert.True(engine.Evaluate("fd.has('tag'); fd.delete('tag'); !fd.has('tag')").AsBoolean());
    }

    [Fact]
    public void StorageApisSupportKeyValueOperations()
    {
        var document = TreeBuilder.Parse("<div></div>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        Assert.Equal(1, engine.Evaluate("localStorage.setItem('answer', 42); localStorage.length").AsNumber());
        Assert.Equal("42", engine.Evaluate("localStorage.getItem('answer')").AsString());
        Assert.Equal("answer", engine.Evaluate("localStorage.key(0)").AsString());
        Assert.Equal(0, engine.Evaluate("localStorage.removeItem('answer'); localStorage.length").AsNumber());
        Assert.Equal("named", engine.Evaluate("localStorage.named = 'named'; localStorage.named").AsString());
        Assert.True(engine.Evaluate("delete localStorage.named; localStorage.getItem('named') === null").AsBoolean());
        Assert.True(engine.Evaluate("try { localStorage.setItem('large', 'x'.repeat(5 * 1024 * 1024)); false; } catch (error) { error.name === 'QuotaExceededError' && localStorage.getItem('large') === null; }").AsBoolean());
    }

    [Fact]
    public void UrlSearchParamsSupportsBasicParsingAndMutation()
    {
        var document = TreeBuilder.Parse("<div></div>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        Assert.Equal("one", engine.Evaluate("const params = new URLSearchParams('?a=one&b=two'); params.get('a')").AsString());
        Assert.Equal("a=one&b=changed", engine.Evaluate("params.set('b','changed'); params.toString()").AsString());
        Assert.Equal("https:", engine.Evaluate("const url = new URL('https://example.test/path?a=one#top'); url.protocol").AsString());
        Assert.Equal("one", engine.Evaluate("url.searchParams.get('a')").AsString());
    }

    [Fact]
    public void ScriptingUtilityApisAreAvailable()
    {
        var document = TreeBuilder.Parse("<div></div>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        Assert.Equal("hello", engine.Evaluate("atob(btoa('hello'))").AsString());
        Assert.Equal(4, engine.Evaluate("const bytes = new Uint8Array(4); crypto.getRandomValues(bytes); bytes.length").AsNumber());
    }

    [Fact]
    public void InputValidationApisReflectRequiredAndEmailRules()
    {
        var document = TreeBuilder.Parse("<input id='email' type='email' required>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        Assert.False(engine.Evaluate("const input = document.getElementById('email'); input.checkValidity()").AsBoolean());
        engine.Evaluate("input.value = 'person@example.com';");
        Assert.True(engine.Evaluate("input.reportValidity()").AsBoolean());
    }

    [Fact]
    public void InputValueAndSelectionApisAreExposed()
    {
        var document = TreeBuilder.Parse("<input id='number' type='number' value='2' step='0.5'>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        Assert.Equal(2, engine.Evaluate("const input = document.getElementById('number'); input.valueAsNumber").AsNumber());
        Assert.Equal("2.5", engine.Evaluate("input.stepUp(); input.value").AsString());
        Assert.Equal(3, engine.Evaluate("input.selectionStart").AsNumber());
    }

    [Fact]
    public void TextareaAndOptionApisAreExposed()
    {
        var document = TreeBuilder.Parse("<textarea id='notes' value='hello'></textarea><select id='choice'></select>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        Assert.Equal("hello", engine.Evaluate("const notes = document.getElementById('notes'); notes.defaultValue = 'hello'; notes.defaultValue").AsString());
        Assert.Equal("world", engine.Evaluate("notes.value = 'world'; notes.value").AsString());
        Assert.Equal("yes", engine.Evaluate("const option = new Option('Yes','yes',true,true); option.value").AsString());
    }

    [Fact]
    public void EncodingCloneAndConsoleUtilitiesAreAvailable()
    {
        var document = TreeBuilder.Parse("<div></div>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        Assert.Equal(5, engine.Evaluate("const encoded = new TextEncoder().encode('hello'); console.log(encoded.length); encoded.length").AsNumber());
        Assert.Equal("hello", engine.Evaluate("new TextDecoder().decode(encoded)").AsString());
        Assert.Equal(7, engine.Evaluate("const copy = structuredClone({ value: 7 }); copy.value").AsNumber());
    }

    [Fact]
    public void SelectOptionsAndMultipleAreExposed()
    {
        var document = TreeBuilder.Parse("<select id='choice'><option value='a'>A</option><option value='b'>B</option></select>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        Assert.Equal(2, engine.Evaluate("const select = document.getElementById('choice'); select.length").AsNumber());
        Assert.Equal("b", engine.Evaluate("select.selectedIndex = 1; select.value").AsString());
        Assert.Equal(1, engine.Evaluate("select.multiple = true; select.options.length - 1").AsNumber());
    }

    [Fact]
    public void ContentProtocolRoundTripsAndRejectsMalformedFrames()
    {
        using var payload = JsonDocument.Parse("{\"artifactId\":\"a1\"}");
        var encoded = ContentProtocol.Encode(new ContentMessage(ContentMessageKind.Load, 7, payload.RootElement.Clone()));
        Assert.True(ContentProtocol.TryDecode(encoded, out var decoded));
        Assert.Equal(ContentMessageKind.Load, decoded!.Kind);
        Assert.Equal(7, decoded.Sequence);
        Assert.Equal(ContentMessage.CurrentVersion, decoded.Version);
        Assert.False(ContentProtocol.TryDecode(encoded[..^1], out _));
        Assert.False(ContentProtocol.TryDecode(new byte[] { 0xff, 0xff, 0xff, 0x7f }, out _));
        using var mismatchedPayload = JsonDocument.Parse("{}");
        var mismatched = ContentProtocol.Encode(new ContentMessage(ContentMessageKind.Hello, 1, mismatchedPayload.RootElement.Clone(), 999));
        Assert.False(ContentProtocol.TryDecode(mismatched, out _));
        var reader = new ContentProtocolReader();
        Assert.Empty(reader.Push(encoded[..2].AsMemory()));
        Assert.Single(reader.Push(encoded[2..].AsMemory()));
    }

    [Fact]
    public void ContentProtocolCarriesFeatureCapabilities()
    {
        using var payload = JsonDocument.Parse("{\"capabilities\":{\"webGl\":true,\"media\":true,\"workers\":false,\"webAssembly\":false,\"iframes\":false,\"webRtc\":false,\"serviceWorkers\":false}}");
        var message = new ContentMessage(ContentMessageKind.Capabilities, 9, payload.RootElement.Clone());

        Assert.True(message.TryGetCapabilities(out var capabilities));
        Assert.True(capabilities.WebGl);
        Assert.True(capabilities.Media);
        Assert.False(capabilities.Workers);
        Assert.True(ContentProtocol.TryDecode(ContentProtocol.Encode(message), out var decoded));
        Assert.True(decoded!.TryGetCapabilities(out var roundTripped));
        Assert.Equal(capabilities, roundTripped);
    }

    [Fact]
    public void FormElementApisExposeControlsAndValidation()
    {
        var document = TreeBuilder.Parse("<form id='form'><input name='name' required value='Ada'></form>");
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);

        Assert.Equal(1, engine.Evaluate("const form = document.getElementById('form'); form.elements.length").AsNumber());
        Assert.True(engine.Evaluate("form.checkValidity()").AsBoolean());
        Assert.Equal("Ada", engine.Evaluate("new FormData(form).get('name')").AsString());
        Assert.True(engine.Evaluate("const details = document.createElement('details'); details.open = true; details.open").AsBoolean());
        Assert.True(engine.Evaluate("document.getElementById('form').focus(); document.activeElement === form").AsBoolean());
    }

    [Fact]
    public async Task ContentTransportRoundTripsOverAStream()
    {
        using var payload = JsonDocument.Parse("{\"ok\":true}");
        await using var stream = new MemoryStream();
        await ContentTransport.WriteAsync(stream, new ContentMessage(ContentMessageKind.Heartbeat, 3, payload.RootElement.Clone()), TestContext.Current.CancellationToken);
        stream.Position = 0;
        var message = await ContentTransport.ReadAsync(stream, TestContext.Current.CancellationToken);
        Assert.Equal(ContentMessageKind.Heartbeat, message!.Kind);
        Assert.Equal(3, message.Sequence);
    }

    [Fact]
    public void ContentSupervisorStopsAfterHeartbeatTimeout()
    {
        using var supervisor = new ContentProcessSupervisor(new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromMilliseconds(-1)));
        string? reason = null;
        supervisor.Stopped += value => reason = value;
        Assert.False(supervisor.CheckHeartbeat());
        Assert.True(supervisor.IsStopped);
        Assert.Contains("heartbeat", reason, StringComparison.OrdinalIgnoreCase);
        using var memorySupervisor = new ContentProcessSupervisor(new ContentProcessLimits(MemoryBytes: 10));
        Assert.False(memorySupervisor.CheckMemory(11));
    }

    [Fact]
    public async Task ContentHostSessionAcknowledgesHeartbeatAndStops()
    {
        using var payload = JsonDocument.Parse("{}");
        var input = new MemoryStream(ContentProtocol.Encode(new ContentMessage(ContentMessageKind.Heartbeat, 8, payload.RootElement.Clone())));
        var output = new MemoryStream();
        var session = new ContentHostSession(input, output);
        await session.RunAsync(TestContext.Current.CancellationToken);
        output.Position = 0;
        var response = await ContentTransport.ReadAsync(output, TestContext.Current.CancellationToken);
        Assert.Equal(ContentMessageKind.Heartbeat, response!.Kind);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task ContentProcessClientRunsScriptThroughContentHost()
    {
        var host = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/Folio.ContentHost/bin/Debug/net10.0/Folio.ContentHost.dll"));
        Assert.True(File.Exists(host), $"Content host was not built: {host}");
        await using var client = ContentProcessClient.Start("dotnet", $"\"{host}\"", new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromSeconds(10)));
        var received = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += (message, _) =>
        {
            if (message.Kind == ContentMessageKind.Frame) received.TrySetResult(message);
            return ValueTask.CompletedTask;
        };
        await client.SendAsync(ContentMessageKind.Load, new { script = "1 + 2", url = "test" }, TestContext.Current.CancellationToken);
        var frame = await received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(ContentMessageKind.Frame, frame.Kind);
        Assert.Equal("3", frame.Payload.GetProperty("value").GetString());

        var domResult = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += (message, _) =>
        {
            if (message.Kind == ContentMessageKind.Frame && message.Sequence > frame.Sequence) domResult.TrySetResult(message);
            return ValueTask.CompletedTask;
        };
        await client.SendAsync(ContentMessageKind.Load, new { html = "<body><p id='x'>before</p></body>", script = "document.getElementById('x').textContent = 'after'; document.getElementById('x').textContent", url = "test" }, TestContext.Current.CancellationToken);
        var domFrame = await domResult.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("after", domFrame.Payload.GetProperty("value").GetString());
    }

    [Fact]
    public async Task ContentProcessStopsAnInfiniteScriptWithDiagnostic()
    {
        var host = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/Folio.ContentHost/bin/Debug/net10.0/Folio.ContentHost.dll"));
        await using var client = ContentProcessClient.Start("dotnet", $"\"{host}\"");
        var diagnostic = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += (message, _) =>
        {
            if (message.Kind == ContentMessageKind.Diagnostic) diagnostic.TrySetResult(message);
            return ValueTask.CompletedTask;
        };
        await client.SendAsync(ContentMessageKind.Load, new { script = "while (true) {}", url = "hostile" }, TestContext.Current.CancellationToken);
        var result = await diagnostic.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Contains(result.Payload.GetProperty("code").GetString(), new[] { "Timeout", "Statements" });
    }

    [Fact]
    public void ContentPayloadsDecodeDisplayAndInputMessages()
    {
        using var display = JsonDocument.Parse("{\"documentRevision\":4,\"viewportWidth\":800,\"viewportHeight\":600,\"commands\":[{\"op\":\"fill\"}]}");
        var displayMessage = new ContentMessage(ContentMessageKind.Frame, 1, display.RootElement.Clone());
        Assert.True(displayMessage.TryGetDisplayList(out var frame));
        Assert.Equal(4, frame!.DocumentRevision);
        using var input = JsonDocument.Parse("{\"type\":\"pointerdown\",\"x\":12.5,\"y\":4}");
        var inputMessage = new ContentMessage(ContentMessageKind.Input, 2, input.RootElement.Clone());
        Assert.True(inputMessage.TryGetInput(out var eventData));
        Assert.Equal("pointerdown", eventData!.Type);
        Assert.Equal(12.5, eventData.X);
    }

    [Fact]
    public async Task ContentHostBlocksResourcesByDefault()
    {
        using var payload = JsonDocument.Parse("{\"url\":\"https://example.test/a.css\"}");
        var input = new MemoryStream(ContentProtocol.Encode(new ContentMessage(ContentMessageKind.ResourceRequest, 9, payload.RootElement.Clone())));
        var output = new MemoryStream();
        var session = new ContentHostSession(input, output);
        await session.RunAsync(TestContext.Current.CancellationToken);
        output.Position = 0;
        var response = await ContentTransport.ReadAsync(output, TestContext.Current.CancellationToken);
        Assert.Equal(ContentMessageKind.ResourceResponse, response!.Kind);
        Assert.Equal(0, response.Payload.GetProperty("status").GetInt32());
        await session.DisposeAsync();
    }
}





