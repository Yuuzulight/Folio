using Folio.Dom;
using Folio.Html;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;
using Folio.Scripting.Ipc;
using Folio.Resources;
using System.Text;
using System.Buffers.Binary;
using System.Text.Json;

namespace Folio.Scripting.Tests;

public sealed class DomStyleDeclarationTests
{
    private sealed class ArtifactMemoryStorage : IArtifactStorage
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public string? Read(string key) => _values.GetValueOrDefault(key);
        public void Write(string key, string value) => _values[key] = value;
        public void Delete(string key) => _values.Remove(key);
        public IReadOnlyList<string> Keys(string prefix) => _values.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
    }

    private sealed class WorkerResourceLoader : IResourceLoader
    {
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<ResourceResponse?>(request.Kind == ResourceKind.Script && request.Url.EndsWith("worker.js", StringComparison.Ordinal)
                ? ResourceResponse.Loaded(Encoding.UTF8.GetBytes("self.addEventListener('message', function(e) { postMessage(e.data + 2); });"), "application/javascript")
                : ResourceResponse.Refused("unsupported"));
    }

    private sealed class ModuleWorkerResourceLoader : IResourceLoader
    {
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            var source = request.Url.EndsWith("dep.js", StringComparison.Ordinal)
                ? "export const answer = 41;"
                : "import { answer } from './dep.js'; onmessage = function() { postMessage(answer + 1); };";
            return Task.FromResult<ResourceResponse?>(request.Kind == ResourceKind.Script
                ? ResourceResponse.Loaded(Encoding.UTF8.GetBytes(source), "application/javascript")
                : ResourceResponse.Refused("wrong resource kind"));
        }
    }

    private sealed class ImportingWorkerResourceLoader : IResourceLoader
    {
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            var source = request.Url.EndsWith("helper.js", StringComparison.Ordinal)
                ? "self.addEventListener('message', function(e) { postMessage(e.data + 3); });"
                : "importScripts('helper.js');";
            return Task.FromResult<ResourceResponse?>(request.Kind == ResourceKind.Script
                ? ResourceResponse.Loaded(Encoding.UTF8.GetBytes(source), "application/javascript")
                : ResourceResponse.Refused("wrong resource kind"));
        }
    }

    private sealed class IframeResourceLoader : IResourceLoader
    {
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<ResourceResponse?>(request.Kind == ResourceKind.Document
                ? ResourceResponse.Loaded(Encoding.UTF8.GetBytes("<html><body><p id='remote'>loaded</p></body></html>"), "text/html")
                : ResourceResponse.Refused("wrong resource kind"));
    }

    private sealed class RefusingIframeResourceLoader : IResourceLoader
    {
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<ResourceResponse?>(ResourceResponse.Refused("blocked by host"));
    }

    private sealed class ServiceWorkerResourceLoader : IResourceLoader
    {
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<ResourceResponse?>(ResourceResponse.Loaded(Encoding.UTF8.GetBytes("self.skipWaiting(); self.clients.claim(); onmessage = function(e) { postMessage(e.data + 2); };"), "application/javascript"));
    }

    private sealed class ImportingServiceWorkerResourceLoader : IResourceLoader
    {
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            var source = request.Url.EndsWith("helper.js", StringComparison.Ordinal)
                ? "self.addEventListener('message', function(e) { postMessage(e.data + 4); });"
                : "importScripts('helper.js');";
            return Task.FromResult<ResourceResponse?>(request.Kind == ResourceKind.Script
                ? ResourceResponse.Loaded(Encoding.UTF8.GetBytes(source), "application/javascript")
                : ResourceResponse.Refused("wrong resource kind"));
        }
    }

    private sealed class ServiceWorkerLifecycleLoader : IResourceLoader
    {
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<ResourceResponse?>(ResourceResponse.Loaded(Encoding.UTF8.GetBytes("self.addEventListener('install', function(e) { e.waitUntil(Promise.resolve()); }); self.addEventListener('activate', function(e) { e.waitUntil(Promise.resolve()); });"), "application/javascript"));
    }

    private sealed class HungServiceWorkerLoader : IResourceLoader
    {
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<ResourceResponse?>(ResourceResponse.Loaded(Encoding.UTF8.GetBytes("while (true) {}"), "application/javascript"));
    }

    private sealed class ServiceWorkerFetchLoader : IResourceLoader
    {
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<ResourceResponse?>(request.Kind == ResourceKind.Script
                ? ResourceResponse.Loaded(Encoding.UTF8.GetBytes("self.addEventListener('fetch', function(e) { if (e.request.url.endsWith('/offline')) e.respondWith(new Response('from worker', { status: 200 })); });"), "application/javascript")
                : ResourceResponse.Loaded(Encoding.UTF8.GetBytes("from host"), "text/plain"));
    }

    private sealed class MutableServiceWorkerLoader : IResourceLoader
    {
        public int Version { get; set; } = 1;
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<ResourceResponse?>(request.Kind == ResourceKind.Script
                ? ResourceResponse.Loaded(Encoding.UTF8.GetBytes($"onmessage = function(e) {{ postMessage(e.data + {Version}); }};"), "application/javascript")
                : ResourceResponse.Refused("wrong resource kind"));
    }

    private sealed class WavResourceLoader : IResourceLoader
    {
        private readonly byte[] _wav = CreateWav();
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<ResourceResponse?>(request.Kind == ResourceKind.Media
                ? ResourceResponse.Loaded(_wav, "audio/wav")
                : ResourceResponse.Refused("wrong resource kind"));
        private static byte[] CreateWav()
        {
            var bytes = new byte[44 + 8000];
            Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0); Encoding.ASCII.GetBytes("WAVE").CopyTo(bytes, 8); Encoding.ASCII.GetBytes("fmt ").CopyTo(bytes, 12); Encoding.ASCII.GetBytes("data").CopyTo(bytes, 36);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), bytes.Length - 8); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16, 4), 16); BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20, 2), 1); BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22, 2), 1); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24, 4), 8000); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28, 4), 8000); BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32, 2), 1); BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34, 2), 8); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40, 4), 8000);
            return bytes;
        }
    }

    private sealed class MpegResourceLoader : IResourceLoader
    {
        private readonly byte[] _mpeg = CreateMpeg();
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<ResourceResponse?>(request.Kind == ResourceKind.Media
                ? ResourceResponse.Loaded(_mpeg, "audio/mpeg")
                : ResourceResponse.Refused("wrong resource kind"));
        private static byte[] CreateMpeg()
        {
            var bytes = new byte[834];
            for (var offset = 0; offset < bytes.Length; offset += 417)
            {
                bytes[offset] = 0xFF; bytes[offset + 1] = 0xFB; bytes[offset + 2] = 0x90; bytes[offset + 3] = 0x64;
            }
            return bytes;
        }
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
        Assert.Equal("", result.AsString()); // records are delivered at the microtask checkpoint, not synchronously
        engine.PerformMicrotaskCheckpoint();
        Assert.Equal("childList:1", engine.Evaluate("seen").AsString());
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
        Assert.Equal("", result.AsString()); // records are delivered at the microtask checkpoint, not synchronously
        engine.PerformMicrotaskCheckpoint();
        Assert.Equal("attributes:data-state", engine.Evaluate("seen").AsString());
    }

    [Fact]
    public void CanvasElementExposesBasicTwoDimensionalContext()
    {
        var document = Document.Parse("<canvas id='chart' width='640' height='480'></canvas>", new FolioOptions { Raster = new Folio.Skia.SkiaRasterBackend() });
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        Assert.Equal(640, engine.Evaluate("const canvas = document.getElementById('chart'); canvas.width").AsNumber());
        Assert.Equal("#000000", engine.Evaluate("const ctx = canvas.getContext('2d'); ctx.fillStyle").AsString());
        Assert.Equal("#ff0000", engine.Evaluate("ctx.fillStyle = '#f00'; ctx.fillRect(1, 2, 30, 40); ctx.fillStyle").AsString());
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
            Capabilities = new FeatureCapabilities { WebGl = true },
            Raster = new Folio.Skia.SkiaRasterBackend(),
        });
        DomBindings.Install(allowed, allowedDocument);
        Assert.True(allowed.Evaluate("const gl = document.getElementById('c').getContext('webgl2'); gl !== null && gl.ARRAY_BUFFER === 34962").AsBoolean());
        Assert.True(allowed.Evaluate("const webgl1 = document.getElementById('c').getContext('webgl'); webgl1 !== null && webgl1.getParameter(webgl1.VERSION).includes('WebGL')").AsBoolean());
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
        Assert.Equal(0, allowed.Evaluate("gl.generateMipmap(gl.TEXTURE_2D); const texels = new Uint8Array([9, 8, 7, 6]); gl.texSubImage2D(gl.TEXTURE_2D, 0, 0, 0, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, texels); gl.getError()").AsNumber());
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
        Assert.Equal(0, allowed.Evaluate("gl.vertexAttribDivisor(loc, 2); gl.getVertexAttrib(loc, gl.VERTEX_ATTRIB_ARRAY_DIVISOR) === 2 && (gl.drawArraysInstanced(gl.TRIANGLES, 0, 3, 2), gl.drawElementsInstanced(gl.TRIANGLES, 3, gl.UNSIGNED_SHORT, 0, 2), gl.getError())").AsNumber());
        Assert.True(allowed.Evaluate("const bad = gl.createShader(gl.FRAGMENT_SHADER); gl.shaderSource(bad, 'precision mediump float;'); gl.compileShader(bad); !gl.getShaderParameter(bad, gl.COMPILE_STATUS) && gl.getShaderInfoLog(bad).length > 0").AsBoolean());
        Assert.True(allowed.Evaluate("const t = gl.createTexture(); gl.bindTexture(gl.TEXTURE_2D, t); gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 2, 2, 0, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array([1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16])); const f = gl.createFramebuffer(); gl.bindFramebuffer(gl.FRAMEBUFFER, f); gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, t, 0); const p = new Uint8Array(16); gl.readPixels(0, 0, 2, 2, gl.RGBA, gl.UNSIGNED_BYTE, p); gl.getError() === 0 && p[0] === 1 && p[3] === 4").AsBoolean());
        Assert.True(allowed.Evaluate("gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, t, 0); gl.checkFramebufferStatus(gl.FRAMEBUFFER) === gl.FRAMEBUFFER_COMPLETE && gl.getFramebufferAttachmentParameter(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.FRAMEBUFFER_ATTACHMENT_OBJECT_TYPE) === gl.TEXTURE && gl.getFramebufferAttachmentParameter(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.FRAMEBUFFER_ATTACHMENT_OBJECT_NAME) === t").AsBoolean());
        Assert.Equal(1, allowed.Evaluate("p[0]").AsNumber());
        Assert.Equal(0, allowed.Evaluate("gl.clearColor(1, 0.25, 0, 1); gl.clear(gl.COLOR_BUFFER_BIT); gl.getError()").AsNumber());
        Assert.True(allowed.Evaluate("let contextEvents = ''; gl.oncontextlost = e => contextEvents += e.type; gl.oncontextrestored = e => contextEvents += e.type; const ext = gl.getExtension('WEBGL_lose_context'); ext.loseContext(); gl.isContextLost() && contextEvents === 'webglcontextlost'").AsBoolean());
        Assert.Equal(0x9242, allowed.Evaluate("gl.getError()").AsNumber());
        Assert.False(allowed.Evaluate("ext.restoreContext(); gl.isContextLost() && contextEvents === 'webglcontextlostwebglcontextrestored'").AsBoolean());
        Assert.Equal("webglcontextlostwebglcontextrestored", allowed.Evaluate("contextEvents").AsString());
        Assert.True(DomBindings.UpdateCapabilities(allowed, new FeatureCapabilities()));
        Assert.True(allowed.Evaluate("gl.isContextLost() && document.getElementById('c').getContext('webgl2') === null").AsBoolean());
        Assert.True(DomBindings.UpdateCapabilities(allowed, new FeatureCapabilities { WebGl = true }));
        Assert.True(allowed.Evaluate("!gl.isContextLost() && document.getElementById('c').getContext('webgl2') !== null").AsBoolean());
        var canvas = allowedDocument.Node.Descendants.OfType<ElementNode>().Single(element => element.LocalName == "canvas");
        // gl.clearColor(1, 0.25, 0, 1) then gl.clear(COLOR_BUFFER_BIT) filled the bitmap.
        var pixels = ((Folio.Imaging.ICanvasSurface)canvas.CanvasSurface!).Snapshot()!.Pixels.Span;
        Assert.Equal(new byte[] { 255, 64, 0, 255 }, pixels[..4].ToArray());

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
        Assert.True(allowed.Evaluate("const v = document.getElementById('v'); let events = ''; let eventType = ''; v.onloadedmetadata = e => { events += 'm'; eventType = e.type; }; v.onplay = e => { events += 'p'; eventType = e.type; }; v.onpause = e => { events += 's'; eventType = e.type; }; v.onended = e => { events += 'e'; eventType = e.type; }; let lifecycle = ''; ['loadstart','loadeddata','canplay','canplaythrough'].forEach(type => v.addEventListener(type, () => lifecycle += type[0])); v.load(); v.paused && v instanceof HTMLMediaElement && v instanceof HTMLVideoElement && v.duration === 12 && v.readyState === 4 && events === 'm' && eventType === 'loadedmetadata' && lifecycle === 'llcc'").AsBoolean());
        Assert.True(allowed.Evaluate("v.src === '/clip.mp4' && v.currentSrc === '/clip.mp4'; v.src = '/other.mp4'; v.currentSrc === '/other.mp4'").AsBoolean());
        Assert.True(allowed.Evaluate("let observed = ''; const listener = e => observed = e.type; v.addEventListener('play', listener); v.play(); v.removeEventListener('play', listener); events = 'm'; observed === 'play'").AsBoolean());
        Assert.True(allowed.Evaluate("let volumeEvents = ''; let playbackEvents = ''; v.onvolumechange = e => volumeEvents += e.type; v.onplaying = e => playbackEvents += 'p'; v.ontimeupdate = e => playbackEvents += 't'; v.onratechange = e => playbackEvents += 'r'; v.currentTime = 4; v.volume = 0.5; v.muted = true; v.playbackRate = 1.5; v.play() instanceof Promise && !v.paused && v.currentTime === 4 && v.volume === 0.5 && v.muted && volumeEvents === 'volumechangevolumechange' && playbackEvents === 'trp'").AsBoolean());
        Assert.True(allowed.Evaluate("let seekEvents = ''; v.addEventListener('seeking', e => seekEvents += e.type); v.addEventListener('seeked', e => seekEvents += e.type); v.preload = 'auto'; v.defaultMuted = true; v.crossOrigin = 'anonymous'; v.fastSeek(6.6); v.setSinkId('default') instanceof Promise && v.preload === 'auto' && v.defaultMuted && v.crossOrigin === 'anonymous' && v.currentTime === 7 && seekEvents === 'seekingseeked'").AsBoolean());
        Assert.True(allowed.Evaluate("const captured = v.captureStream(); captured instanceof MediaStream && v.videoWidth === 640 && v.videoHeight === 360").AsBoolean());
        Assert.Equal("maybe", allowed.Evaluate("v.canPlayType('video/mp4')").AsString());
        Assert.Equal("maybe", allowed.Evaluate("v.canPlayType('audio/mpeg; codecs=\"mp3\"')").AsString());
        Assert.Equal("", allowed.Evaluate("v.canPlayType('application/octet-stream')").AsString());
        Assert.True(allowed.Evaluate("v.pause(); v.paused").AsBoolean());
        Assert.True(allowed.Evaluate("events === 'mps' && eventType === 'pause'").AsBoolean());
        Assert.True(allowed.Evaluate("v.currentTime = 12; events === 'mpse'").AsBoolean());
        Assert.True(allowed.Evaluate("v.load(); v.currentTime === 0 && v.paused").AsBoolean());
        Assert.True(allowed.Evaluate("const captions = v.addTextTrack('captions', 'English', 'en'); let modeCueChanges = 0; let cueListenerChanges = 0; captions.oncuechange = () => modeCueChanges++; captions.addEventListener('cuechange', () => cueListenerChanges++); captions.addCue(1, 2, 'Hello'); v.currentTime = 1; captions.mode = 'showing'; captions.addCue(0.5, 1.5, 'Second'); captions.mode === 'showing' && captions.activeCues.length === 2 && captions.cues[0].startTime === 1 && captions.cues[0].endTime === 2 && captions.cues[0].text === 'Hello' && modeCueChanges === 2 && cueListenerChanges === 2").AsBoolean());
        Assert.ThrowsAny<Exception>(() => allowed.Evaluate("captions.addCue(2, 1, 'invalid')"));
        Assert.Equal(2, allowed.Evaluate("modeCueChanges").AsNumber());
        Assert.True(allowed.Evaluate("const removable = captions.cues[1]; let removedCueEvents = ''; removable.onexit = e => removedCueEvents += e.type; captions.removeCue(removable); captions.activeCues.length === 1 && captions.cues[0].text === 'Hello' && modeCueChanges === 3 && removedCueEvents === 'exit'").AsBoolean());
        Assert.ThrowsAny<Exception>(() => allowed.Evaluate("captions.mode = 'invalid'"));
        Assert.True(allowed.Evaluate("v.currentTime = 0; let cueEvents = ''; let cueLifecycle = ''; v.textTracks[0].oncuechange = e => cueEvents += e.type; const cue = v.textTracks[0].cues[0]; cue.onenter = e => cueLifecycle += e.type; cue.onexit = e => cueLifecycle += e.type; v.currentTime = 1.5; v.textTracks.length === 1 && v.textTracks[0].activeCues.length === 1 && v.textTracks[0].activeCues[0].text === 'Hello' && cueEvents === 'cuechange' && cueLifecycle === 'enter'; v.currentTime = 2.5; cueEvents === 'cuechangecuechange' && cueLifecycle === 'enterexit'").AsBoolean());
        Assert.True(allowed.Evaluate("v.playbackRate = 1.5; v.defaultPlaybackRate = 1.25; v.preservesPitch = false; v.playbackRate === 1.5 && v.defaultPlaybackRate === 1.25 && !v.preservesPitch && v.buffered.length === 1 && v.buffered.start(0) === 0 && v.buffered.end(0) === 12").AsBoolean());
        Assert.True(DomBindings.UpdateCapabilities(allowed, new FeatureCapabilities { Media = false }));
        Assert.True(allowed.Evaluate("v.paused && v.readyState === 0 && v.error === 'Media is disabled by the host.'").AsBoolean());
        Assert.True(DomBindings.UpdateCapabilities(allowed, new FeatureCapabilities { Media = true }));
        Assert.True(allowed.Evaluate("v.error === null && v.readyState === 4 && v.paused").AsBoolean());
        using var autoplay = new JintScriptEngine();
        DomBindings.Install(autoplay, Document.Parse("<video id='loud' autoplay></video><video id='muted' autoplay muted></video>", new FolioOptions { Capabilities = new FeatureCapabilities { Media = true } }));
        Assert.True(autoplay.Evaluate("const loud = document.getElementById('loud'); const muted = document.getElementById('muted'); loud.paused && loud.error !== null && !muted.paused && muted.error === null").AsBoolean());
        using var hostAutoplay = new JintScriptEngine();
        DomBindings.Install(hostAutoplay, Document.Parse("<video id='v' autoplay></video>", new FolioOptions { Capabilities = new FeatureCapabilities { Media = true, AllowSoundAutoplay = true } }));
        Assert.True(hostAutoplay.Evaluate("!document.getElementById('v').paused && document.getElementById('v').error === null").AsBoolean());

        using var decoded = new JintScriptEngine();
        DomBindings.Install(decoded, Document.Parse("<audio id='a' src='/tone.wav'></audio>", new FolioOptions
        {
            BaseUri = new Uri("https://media.example/"),
            ResourceLoader = new WavResourceLoader(),
            Capabilities = new FeatureCapabilities { Media = true }
        }));
        Assert.Equal(1, decoded.Evaluate("document.getElementById('a').duration").AsNumber());

        using var mpeg = new JintScriptEngine();
        DomBindings.Install(mpeg, Document.Parse("<audio id='a' src='/tone.mp3'></audio>", new FolioOptions
        {
            BaseUri = new Uri("https://media.example/"),
            ResourceLoader = new MpegResourceLoader(),
            Capabilities = new FeatureCapabilities { Media = true }
        }));
        Assert.Equal(0.052, mpeg.Evaluate("document.getElementById('a').duration").AsNumber(), 3);
        using var corrupt = new JintScriptEngine();
        DomBindings.Install(corrupt, Document.Parse("<audio id='a' src='data:audio/wav;base64,AAAA'></audio>", new FolioOptions { Capabilities = new FeatureCapabilities { Media = true } }));
        Assert.True(corrupt.Evaluate("const a = document.getElementById('a'); let errorType = ''; a.onerror = e => errorType = e.type; a.load(); a.error !== null && errorType === 'error'").AsBoolean());
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

        using var decoderLimited = new JintScriptEngine();
        DomBindings.Install(decoderLimited, Document.Parse("<video id='a'></video><video id='b'></video>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Media = true },
            Limits = ResourceLimits.Default with { MaxMediaDecoders = 1 }
        }));
        Assert.True(decoderLimited.Evaluate("document.getElementById('a').readyState === 4 && document.getElementById('b').error === 'Media decoder limit exceeded.'").AsBoolean());
    }

    [Fact]
    public void MediaElementsLoadWebVttTrackChildren()
    {
        using var engine = new JintScriptEngine();
        var vtt = "data:text/vtt,WEBVTT%0A%0Aintro%0A00:00.500%20--%3E%2000:02.000%0AHello%20%26amp%3B%20world";
        DomBindings.Install(engine, Document.Parse($"<video id='v'><track kind='captions' label='English' srclang='en' default src='{vtt}'></video>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Media = true }
        }));

        Assert.True(engine.Evaluate("const v = document.getElementById('v'); const t = v.textTracks[0]; v.currentTime = 1; t.kind === 'captions' && t.label === 'English' && t.language === 'en' && t.mode === 'showing' && t.cues.length === 1 && t.cues[0].id === 'intro' && t.cues[0].text === 'Hello & world' && t.activeCues.length === 1").AsBoolean());
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
        Assert.True(allowed.Evaluate("const queuedWorker = new Worker('data:text/javascript,' + encodeURIComponent('onmessage = function(e) { postMessage(e.data === 1 ? \\\"first\\\" : \\\"second\\\"); };')); let queuedSeen = ''; queuedWorker.onmessage = e => { queuedSeen += e.data; if (e.data === 'first') queuedWorker.postMessage(2); }; queuedWorker.postMessage(1); queuedSeen === 'firstsecond'").AsBoolean());
        Assert.True(allowed.Evaluate("const cloneWorker = new Worker('data:text/javascript,' + encodeURIComponent('onmessage = function(e) { postMessage({ nested: e.data.nested }); };')); let cloned = null; cloneWorker.onmessage = e => cloned = e.data; const original = { nested: 7 }; cloneWorker.postMessage(original); cloned.nested === 7 && cloned !== original").AsBoolean());
        Assert.True(allowed.Evaluate("const cyclicWorker = new Worker('data:text/javascript,' + encodeURIComponent('onmessage = function(e) { postMessage(e.data.self === e.data); };')); let cycleSeen = false; cyclicWorker.onmessage = e => cycleSeen = e.data; const cyclic = {}; cyclic.self = cyclic; cyclicWorker.postMessage(cyclic); cycleSeen").AsBoolean());
        Assert.True(allowed.Evaluate("cloneWorker.terminate(); cyclicWorker.terminate(); const richWorker = new Worker('data:text/javascript,' + encodeURIComponent('onmessage = function(e) { const d=e.data; postMessage(d.map instanceof Map && d.map.get(\"k\") === 3 && d.set instanceof Set && d.set.has(9) && d.date instanceof Date && d.date.getUTCFullYear() === 2020 && d.re instanceof RegExp && d.re.test(\"abc\") && d.bytes instanceof Uint8Array && d.bytes[1] === 8 && d.self === d); };')); let rich = false; richWorker.onmessage = e => rich = e.data; const richValue = { map: new Map([[\"k\", 3]]), set: new Set([9]), date: new Date('2020-01-01T00:00:00Z'), re: /abc/, bytes: new Uint8Array([4, 8]), self: null }; richValue.self = richValue; richWorker.postMessage(richValue); rich").AsBoolean());
        Assert.True(allowed.Evaluate("const transferWorker = new Worker('data:text/javascript,' + encodeURIComponent('onmessage = function(e) { postMessage(e.data.byteLength); };')); let transferredLength = 0; transferWorker.onmessage = e => transferredLength = e.data; const buffer = new ArrayBuffer(4); transferWorker.postMessage(buffer, [buffer]); transferredLength === 4 && buffer.byteLength === 0").AsBoolean());
        Assert.True(allowed.Evaluate("const portWorker = new Worker('data:text/javascript,' + encodeURIComponent('onmessage = function(e) { e.data.postMessage(77); };')); const portChannel = new MessageChannel(); let portValue = 0; portChannel.port1.onmessage = e => portValue = e.data; portWorker.postMessage(portChannel.port2, [portChannel.port2]); portValue === 77").AsBoolean());
        Assert.True(allowed.Evaluate("const broken = new Worker('data:text/javascript,' + encodeURIComponent('throw new Error(\\\"boom\\\");')); let workerError = ''; let workerFile = ''; let workerLine = -1; broken.onerror = e => { workerError = e.message; workerFile = e.filename; workerLine = e.lineno; e.preventDefault(); }; broken.postMessage(1); workerError === 'boom' && workerFile === broken.scriptURL && workerLine === 0").AsBoolean());
        Assert.True(allowed.Evaluate("const messageErrorWorker = new Worker('data:text/javascript,' + encodeURIComponent('onmessage = function() { postMessage(Symbol(\\\"bad\\\")); };')); let messageErrorType = ''; messageErrorWorker.onmessageerror = e => messageErrorType = e.type; messageErrorWorker.postMessage(1); messageErrorType === 'messageerror'").AsBoolean());
        Assert.True(allowed.Evaluate("let listenerSeen = 0; const listener = e => listenerSeen = e.data; w.addEventListener('message', listener); w.postMessage(43); w.removeEventListener('message', listener); listenerSeen === 43").AsBoolean());
        Assert.Equal("https://worker.example/worker.js", allowed.Evaluate("w.scriptURL").AsString());
        Assert.ThrowsAny<Exception>(() => allowed.Evaluate("w.terminate(); w.postMessage(7)"));
        Assert.ThrowsAny<Exception>(() => allowed.Evaluate("w.postMessage(function () {})"));

        using var limited = new JintScriptEngine();
        DomBindings.Install(limited, Document.Parse("<html></html>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Workers = true },
            Limits = ResourceLimits.Default with { MaxWorkerMessageBytes = 4 }
        }));
        Assert.ThrowsAny<Exception>(() => limited.Evaluate("const w = new Worker('data:text/javascript,'); w.postMessage('12345')"));
        Assert.ThrowsAny<Exception>(() => limited.Evaluate("w.terminate(); w.postMessage('ok')"));

        using var workerLimited = new JintScriptEngine();
        DomBindings.Install(workerLimited, Document.Parse("<html></html>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Workers = true },
            Limits = ResourceLimits.Default with { MaxWorkers = 1 }
        }));
        Assert.ThrowsAny<Exception>(() => workerLimited.Evaluate("const first = new Worker('data:text/javascript,'); new Worker('data:text/javascript,')"));

        using var busy = new JintScriptEngine();
        DomBindings.Install(busy, Document.Parse("<html></html>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Workers = true }
        }));
        Assert.True(busy.Evaluate("const runaway = new Worker('data:text/javascript,' + encodeURIComponent('while (true) {}')); true").AsBoolean());
        Assert.True(busy.Evaluate("runaway.terminate(); true").AsBoolean());

        using var memoryLimited = new JintScriptEngine();
        DomBindings.Install(memoryLimited, Document.Parse("<html></html>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Workers = true },
            Limits = ResourceLimits.Default with { MaxWorkerMemoryBytes = 32 * 1024 }
        }));
        Assert.True(memoryLimited.Evaluate("const constrained = new Worker('data:text/javascript,' + encodeURIComponent('const values = []; for (let i = 0; i < 100000; i++) values.push(i);')); let constrainedError = ''; constrained.onerror = e => constrainedError = e.message; constrained.postMessage(1); constrainedError.length > 0").AsBoolean());

        using var watchdog = new JintScriptEngine();
        DomBindings.Install(watchdog, Document.Parse("<html></html>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Workers = true },
            Limits = ResourceLimits.Default with { MaxWorkerExecutionTime = TimeSpan.FromMilliseconds(100) }
        }));
        Thread.Sleep(250);
        Assert.True(watchdog.Evaluate("const runawayWorker = new Worker('data:text/javascript,' + encodeURIComponent('while (true) {}')); let watchdogError = ''; runawayWorker.onerror = e => watchdogError = e.message; runawayWorker.postMessage(1); watchdogError.length > 0").AsBoolean());
        Assert.True(watchdog.Evaluate("1 + 1").AsNumber() == 2);

        using var selfClosing = new JintScriptEngine();
        DomBindings.Install(selfClosing, Document.Parse("<html></html>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Workers = true }
        }));
        Assert.True(selfClosing.Evaluate("const selfClosingWorker = new Worker('data:text/javascript,' + encodeURIComponent('onmessage = function() { close(); postMessage(1); };')); let selfCloseMessages = 0; let selfCloseErrors = 0; selfClosingWorker.onmessage = () => selfCloseMessages++; selfClosingWorker.onerror = () => selfCloseErrors++; selfClosingWorker.postMessage(1); selfCloseMessages === 0 && selfCloseErrors === 0").AsBoolean());

        using var moduleWorker = new JintScriptEngine();
        DomBindings.Install(moduleWorker, Document.Parse("<html></html>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Workers = true }
        }));
        Assert.True(moduleWorker.Evaluate("const modular = new Worker('data:text/javascript,' + encodeURIComponent('export const answer = 42; onmessage = function() { postMessage(answer); };'), { type: 'module' }); let moduleAnswer = 0; modular.onmessage = e => moduleAnswer = e.data; modular.postMessage(1); moduleAnswer === 42").AsBoolean());
        Assert.ThrowsAny<Exception>(() => moduleWorker.Evaluate("new Worker('data:text/javascript,', { type: 'invalid' })"));

        using var blobWorker = new JintScriptEngine();
        DomBindings.Install(blobWorker, Document.Parse("<html></html>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Workers = true }
        }));
        Assert.True(blobWorker.Evaluate("const blob = new Blob(['onmessage = function() { postMessage(43); };'], { type: 'text/javascript' }); const blobUrl = URL.createObjectURL(blob); const fromBlob = new Worker(blobUrl); let blobAnswer = 0; fromBlob.onmessage = e => blobAnswer = e.data; fromBlob.postMessage(1); blobAnswer === 43").AsBoolean());

        using var importedModuleWorker = new JintScriptEngine();
        DomBindings.Install(importedModuleWorker, Document.Parse("<html></html>", new FolioOptions
        {
            BaseUri = new Uri("https://worker.example/"),
            ResourceLoader = new ModuleWorkerResourceLoader(),
            Capabilities = new FeatureCapabilities { Workers = true }
        }));
        Assert.True(importedModuleWorker.Evaluate("const importedModule = new Worker('main.js', { type: 'module' }); let importedModuleAnswer = 0; importedModule.onmessage = e => importedModuleAnswer = e.data; importedModule.postMessage(1); importedModuleAnswer === 42").AsBoolean());

        using var closed = new JintScriptEngine();
        DomBindings.Install(closed, Document.Parse("<html></html>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Workers = true }
        }));
        Assert.True(closed.Evaluate("const owned = new Worker('data:text/javascript,' + encodeURIComponent('onmessage = function() { postMessage(1); };')); true").AsBoolean());
        Assert.True(DomBindings.Dispose(closed));
        Assert.ThrowsAny<Exception>(() => closed.Evaluate("owned.postMessage(1)"));
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
        Assert.True(engine.Evaluate("const missing = new Worker('missing.js'); let workerError = ''; missing.onerror = e => workerError = e.message; const immediateWorkerError = workerError.includes('could not load'); missing.postMessage(1); immediateWorkerError && workerError.includes('could not load')").AsBoolean());

        using var importing = new JintScriptEngine();
        DomBindings.Install(importing, Document.Parse("<html></html>", new FolioOptions
        {
            BaseUri = new Uri("https://worker.example/app/"),
            ResourceLoader = new ImportingWorkerResourceLoader(),
            Capabilities = new FeatureCapabilities { Workers = true }
        }));
        Assert.True(importing.Evaluate("const imported = new Worker('worker.js'); let importedResult = 0; imported.onmessage = e => importedResult = e.data; imported.postMessage(40); importedResult === 43").AsBoolean());
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
        Assert.False(allowed.Evaluate("WebAssembly.validate(new Uint8Array([0,97,115,109,1,0,0,0,1,2,0]))").AsBoolean());
        Assert.False(allowed.Evaluate("WebAssembly.validate(new Uint8Array([1,2,3]))").AsBoolean());
        using var capped = new JintScriptEngine();
        DomBindings.Install(capped, Document.Parse("<html></html>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebAssembly = true },
            Limits = ResourceLimits.Default with { MaxWebAssemblyBytes = 4 }
        }));
        Assert.ThrowsAny<Exception>(() => capped.Evaluate("new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0]))"));
        Assert.True(capped.Evaluate("let cappedCompileRejected = false; WebAssembly.compile(new Uint8Array([0,97,115,109,1,0,0,0])).catch(error => cappedCompileRejected = error.name === 'RangeError'); true").AsBoolean());
        capped.PerformMicrotaskCheckpoint();
        Assert.True(capped.Evaluate("cappedCompileRejected").AsBoolean());
        Assert.ThrowsAny<Exception>(() => capped.Evaluate("new WebAssembly.Table({ initial: 2 })"));
        Assert.True(allowed.Evaluate("let streamingError = ''; WebAssembly.compileStreaming(new Response(new Uint8Array([0,97,115,109,1,0,0,0]), { headers: { 'content-type': 'application/octet-stream' } })).catch(error => streamingError = error.name); true").AsBoolean());
        allowed.PerformMicrotaskCheckpoint();
        Assert.Equal("TypeError", allowed.Evaluate("streamingError").AsString());
        Assert.True(allowed.Evaluate("let failedResponseError = ''; WebAssembly.compileStreaming(new Response(new Uint8Array([0,97,115,109,1,0,0,0]), { status: 404, headers: { 'content-type': 'application/wasm' } })).catch(error => failedResponseError = error.name); true").AsBoolean());
        allowed.PerformMicrotaskCheckpoint();
        Assert.Equal("TypeError", allowed.Evaluate("failedResponseError").AsString());
        Assert.True(allowed.Evaluate("let streamedInstance = false; WebAssembly.instantiateStreaming(new Response(new Uint8Array([0,97,115,109,1,0,0,0]), { headers: { 'content-type': 'application/wasm' } }), {}).then(result => streamedInstance = result.instance instanceof WebAssembly.Instance); true").AsBoolean());
        Assert.True(allowed.Evaluate("let promisedModule = false; WebAssembly.compileStreaming(Promise.resolve(new Response(new Uint8Array([0,97,115,109,1,0,0,0]), { headers: { 'content-type': 'application/wasm' } }))).then(module => promisedModule = module instanceof WebAssembly.Module); true").AsBoolean());
        allowed.PerformMicrotaskCheckpoint();
        Assert.True(allowed.Evaluate("promisedModule").AsBoolean());
        Assert.True(allowed.Evaluate("let promisedInstance = false; WebAssembly.instantiateStreaming(Promise.resolve(new Response(new Uint8Array([0,97,115,109,1,0,0,0]), { headers: { 'content-type': 'application/wasm' } })), {}).then(result => promisedInstance = result.instance instanceof WebAssembly.Instance); true").AsBoolean());
        allowed.PerformMicrotaskCheckpoint();
        Assert.True(allowed.Evaluate("promisedInstance").AsBoolean());
        for (var checkpoint = 0; checkpoint < 8; checkpoint++) allowed.PerformMicrotaskCheckpoint();
        Assert.True(allowed.Evaluate("streamedInstance").AsBoolean());
        Assert.True(allowed.Evaluate("let compiledCacheHit = false; const cacheBytes = new Uint8Array([0,97,115,109,1,0,0,0]); WebAssembly.compile(cacheBytes).then(first => WebAssembly.compile(cacheBytes).then(second => compiledCacheHit = first === second)); true").AsBoolean());
        allowed.PerformMicrotaskCheckpoint();
        Assert.True(allowed.Evaluate("compiledCacheHit").AsBoolean());
        using var smallCache = new JintScriptEngine();
        DomBindings.Install(smallCache, Document.Parse("<html></html>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebAssembly = true },
            Limits = ResourceLimits.Default with { MaxWebAssemblyBytes = 16 }
        }));
        Assert.True(smallCache.Evaluate("let replacementCacheHit = false; let replacementCacheError = false; const firstBytes = new Uint8Array([0,97,115,109,1,0,0,0,0,3,0,65,1]); const secondBytes = new Uint8Array([0,97,115,109,1,0,0,0,0,3,0,66,2]); WebAssembly.compile(firstBytes).then(first => WebAssembly.compile(secondBytes).then(() => WebAssembly.compile(firstBytes).then(firstAgain => WebAssembly.compile(firstBytes).then(repeated => replacementCacheHit = first !== firstAgain && firstAgain === repeated)))).catch(() => replacementCacheError = true); true").AsBoolean());
        for (var checkpoint = 0; checkpoint < 32; checkpoint++) smallCache.PerformMicrotaskCheckpoint();
        Assert.False(smallCache.Evaluate("replacementCacheError").AsBoolean());
        Assert.True(smallCache.Evaluate("replacementCacheHit").AsBoolean());
        Assert.True(allowed.Evaluate("const module = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0])); const instance = new WebAssembly.Instance(module, {}); module instanceof WebAssembly.Module && instance.exports !== null && WebAssembly.Module.exports(module).length === 0 && WebAssembly.Module.imports(module).length === 0").AsBoolean());
        Assert.True(allowed.Evaluate("const wasmBytes = new Uint8Array([0,97,115,109,1,0,0,0]).buffer; let wasmArrayBuffer = false; let wasmModuleInput = false; WebAssembly.compile(wasmBytes).then(m => WebAssembly.instantiate(wasmBytes, {}).then(() => wasmArrayBuffer = true)); WebAssembly.instantiate(module, {}).then(result => wasmModuleInput = result.module === module); true").AsBoolean());
        allowed.PerformMicrotaskCheckpoint();
        Assert.True(allowed.Evaluate("wasmArrayBuffer && wasmModuleInput").AsBoolean());
        Assert.True(allowed.Evaluate("new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0]).buffer) instanceof WebAssembly.Module").AsBoolean());
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
        Assert.Equal(9, allowed.Evaluate("const reexport = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,2,10,1,3,101,110,118,2,102,110,0,0,7,6,1,2,102,110,0,0])); new WebAssembly.Instance(reexport, { env: { fn: function() { return 9; } } }).exports.fn()").AsNumber());
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
        Assert.True(allowed.Evaluate("const bulk = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,1,5,1,96,0,1,127,3,2,1,0,5,3,1,0,1,7,14,2,4,102,105,108,108,0,0,3,109,101,109,2,0,10,28,1,27,0,65,0,65,7,65,4,252,11,0,65,4,65,0,65,4,252,10,0,0,65,4,40,0,0,11])); const bulkInstance = new WebAssembly.Instance(bulk, {}); bulkInstance.exports.fill() === 117901063 && new Uint8Array(bulkInstance.exports.mem.buffer)[7] === 7").AsBoolean());
        Assert.Equal(11, allowed.Evaluate("const selectCode = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,1,5,1,96,0,1,127,3,2,1,0,7,7,1,3,114,117,110,0,0,10,11,1,9,0,65,11,65,22,65,1,27,11])); new WebAssembly.Instance(selectCode, {}).exports.run()").AsNumber());
        Assert.Equal(1, allowed.Evaluate("const branchCode = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,1,5,1,96,0,1,127,3,2,1,0,7,7,1,3,114,117,110,0,0,10,12,1,10,0,65,1,65,1,13,0,65,99,11])); new WebAssembly.Instance(branchCode, {}).exports.run()").AsNumber());
        Assert.Equal(7, allowed.Evaluate("const tableBranch = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,1,5,1,96,0,1,127,3,2,1,0,7,7,1,3,114,117,110,0,0,10,14,1,12,0,65,7,65,0,14,1,0,0,65,99,11])); new WebAssembly.Instance(tableBranch, {}).exports.run()").AsNumber());
        Assert.Equal(3, allowed.Evaluate("const ctzCode = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,1,5,1,96,0,1,127,3,2,1,0,7,7,1,3,114,117,110,0,0,10,7,1,6,0,65,8,104,11])); new WebAssembly.Instance(ctzCode, {}).exports.run()").AsNumber());
        Assert.Equal(4, allowed.Evaluate("const floatCode = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,1,5,1,96,0,1,125,3,2,1,0,7,7,1,3,114,117,110,0,0,10,15,1,13,0,67,0,0,192,63,67,0,0,32,64,146,11])); new WebAssembly.Instance(floatCode, {}).exports.run()").AsNumber());
        Assert.Equal(3, allowed.Evaluate("const sqrtCode = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,1,5,1,96,0,1,125,3,2,1,0,7,7,1,3,114,117,110,0,0,10,10,1,8,0,67,0,0,16,65,145,11])); new WebAssembly.Instance(sqrtCode, {}).exports.run()").AsNumber());
        Assert.ThrowsAny<Exception>(() => allowed.Evaluate("const overflowCode = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,1,7,1,96,2,127,127,1,127,3,2,1,0,7,7,1,3,114,117,110,0,0,10,9,1,7,0,32,0,32,1,109,11])); new WebAssembly.Instance(overflowCode, {}).exports.run(-2147483648, -1)"));
        Assert.ThrowsAny<Exception>(() => allowed.Evaluate("const trapCode = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,1,5,1,96,0,1,127,3,2,1,0,7,7,1,3,114,117,110,0,0,10,5,1,3,0,0,11])); new WebAssembly.Instance(trapCode, {}).exports.run()"));
        Assert.True(allowed.Evaluate("const tableImport = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,2,13,1,3,101,110,118,3,116,97,98,1,112,0,1,7,7,1,3,116,97,98,1,0])); const suppliedTable = new WebAssembly.Table({ initial: 1 }); new WebAssembly.Instance(tableImport, { env: { tab: suppliedTable } }).exports.tab === suppliedTable").AsBoolean());
        Assert.True(allowed.Evaluate("const localTable = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,4,4,1,112,0,1,7,7,1,3,116,97,98,1,0])); const localTableInstance = new WebAssembly.Instance(localTable, {}); localTableInstance.exports.tab instanceof WebAssembly.Table && localTableInstance.exports.tab.length === 1").AsBoolean());
        Assert.True(allowed.Evaluate("const globalImport = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,2,10,1,3,101,110,118,1,103,3,127,0,7,5,1,1,103,3,0])); const suppliedGlobal = new WebAssembly.Global({ value: 'i32', mutable: true }, 7); const globalInstance = new WebAssembly.Instance(globalImport, { env: { g: suppliedGlobal } }); globalInstance.exports.g === suppliedGlobal && globalInstance.exports.g.value === 7").AsBoolean());
        Assert.True(allowed.Evaluate("const localGlobal = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,6,6,1,127,0,65,42,11,7,5,1,1,103,3,0])); const localGlobalInstance = new WebAssembly.Instance(localGlobal, {}); localGlobalInstance.exports.g instanceof WebAssembly.Global && localGlobalInstance.exports.g.value === 42 && localGlobalInstance.exports.g.mutable === false").AsBoolean());
        Assert.True(allowed.Evaluate("const custom = new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,0,6,4,109,101,116,97,42])); WebAssembly.Module.customSections(custom, 'meta').length === 1 && WebAssembly.Module.customSections(custom, 'meta')[0][0] === 42 && WebAssembly.Module.customSections(custom, 'other').length === 0").AsBoolean());
        Assert.ThrowsAny<Exception>(() => allowed.Evaluate("new WebAssembly.Instance(memoryImport, { env: { mem: new WebAssembly.Table({ initial: 1 }) } })"));
        Assert.ThrowsAny<Exception>(() => allowed.Evaluate("new WebAssembly.Instance(tableImport, { env: { tab: new WebAssembly.Memory({ initial: 1 }) } })"));
        Assert.True(allowed.Evaluate("const memory = new WebAssembly.Memory({ initial: 1, maximum: 2 }); memory.buffer.byteLength === 65536 && memory.grow(1) === 1 && memory.buffer.byteLength === 131072").AsBoolean());
        Assert.True(allowed.Evaluate("const table = new WebAssembly.Table({ initial: 1, maximum: 2 }); table.set(0, 7); table.get(0) === 7 && table.grow(1) === 1 && table.length === 2").AsBoolean());
        Assert.ThrowsAny<Exception>(() => allowed.Evaluate("new WebAssembly.Memory({ initial: 1.5 })"));
        Assert.ThrowsAny<Exception>(() => allowed.Evaluate("new WebAssembly.Table({ initial: 1, maximum: 2.5 })"));
        Assert.ThrowsAny<Exception>(() => allowed.Evaluate("new WebAssembly.Memory({ initial: 1 }).grow(0.5)"));
        Assert.ThrowsAny<Exception>(() => allowed.Evaluate("new WebAssembly.Table({ initial: 1 }).grow(0.5)"));
        Assert.True(allowed.Evaluate("const global = new WebAssembly.Global({ value: 'i32', mutable: true }, 4); global.valueOf() === 4; global.value = 9; global.value === 9").AsBoolean());
        Assert.Equal(4, allowed.Evaluate("const floatGlobal = new WebAssembly.Global({ value: 'f64' }, '4'); floatGlobal.value").AsNumber());
        Assert.ThrowsAny<Exception>(() => allowed.Evaluate("new WebAssembly.Global({ value: 'invalid' }, 0)"));
        Assert.ThrowsAny<Exception>(() => allowed.Evaluate("new WebAssembly.Memory({ initial: 1, maximum: 2 }).grow(2)"));
    }

    [Fact]
    public void ServiceWorkerRegistrationIsCapabilityGated()
    {
        using var denied = new JintScriptEngine();
        DomBindings.Install(denied, Document.Parse("<html></html>"));
        Assert.True(denied.Evaluate("typeof navigator.serviceWorker === 'undefined'").AsBoolean());

        using var allowed = new JintScriptEngine();
        DomBindings.Install(allowed, Document.Parse("<html></html>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { ServiceWorkers = true }
        }));
        Assert.True(allowed.Evaluate("navigator.serviceWorker.register('/sw.js').then(r => r.scriptURL === '/sw.js')").IsObject);
        allowed.Evaluate("let firstRegistration; navigator.serviceWorker.register('/sw.js').then(r => firstRegistration = r);");
        allowed.PerformMicrotaskCheckpoint();
        Assert.True(allowed.Evaluate("let sameScopeResult = false; navigator.serviceWorker.register('/other.js').then(r => { const same = r === firstRegistration; return navigator.serviceWorker.getRegistration('/').then(found => sameScopeResult = same && found === r && r.scriptURL === '/other.js'); }); true").AsBoolean());
        allowed.PerformMicrotaskCheckpoint();
        Assert.True(allowed.Evaluate("sameScopeResult").AsBoolean());
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

        using var scripted = new JintScriptEngine();
        DomBindings.Install(scripted, Document.Parse("<html></html>", new FolioOptions
        {
            BaseUri = new Uri("https://worker.example/"),
            ResourceLoader = new ServiceWorkerResourceLoader(),
            Capabilities = new FeatureCapabilities { ServiceWorkers = true }
        }));
        Assert.True(scripted.Evaluate("let scriptedResult = 0; navigator.serviceWorker.register('sw.js').then(r => { r.active.onmessage = e => scriptedResult = e.data; r.active.postMessage(40); }); true").AsBoolean());
        scripted.PerformMicrotaskCheckpoint();
        Assert.Equal(42, scripted.Evaluate("scriptedResult").AsNumber());

        using var importing = new JintScriptEngine();
        DomBindings.Install(importing, Document.Parse("<html></html>", new FolioOptions
        {
            BaseUri = new Uri("https://worker.example/"),
            ResourceLoader = new ImportingServiceWorkerResourceLoader(),
            Capabilities = new FeatureCapabilities { ServiceWorkers = true }
        }));
        Assert.True(importing.Evaluate("let importedResult = 0; navigator.serviceWorker.register('sw.js').then(r => { r.active.onmessage = e => importedResult = e.data; r.active.postMessage(40); }); true").AsBoolean());
        importing.PerformMicrotaskCheckpoint();
        Assert.Equal(44, importing.Evaluate("importedResult").AsNumber());

        using var lifecycle = new JintScriptEngine();
        DomBindings.Install(lifecycle, Document.Parse("<html></html>", new FolioOptions
        {
            BaseUri = new Uri("https://worker.example/"),
            ResourceLoader = new ServiceWorkerLifecycleLoader(),
            Capabilities = new FeatureCapabilities { ServiceWorkers = true }
        }));
        Assert.True(lifecycle.Evaluate("navigator.serviceWorker.register('lifecycle.js').then(r => r.active.state === 'activated')").IsObject);

        using var intercepted = new JintScriptEngine();
        DomBindings.Install(intercepted, Document.Parse("<html></html>", new FolioOptions
        {
            BaseUri = new Uri("https://worker.example/"),
            ResourceLoader = new ServiceWorkerFetchLoader(),
            Capabilities = new FeatureCapabilities { ServiceWorkers = true }
        }));
        Assert.True(intercepted.Evaluate("let interceptedText = ''; navigator.serviceWorker.register('sw.js').then(() => fetch('/offline')).then(r => r.text()).then(text => interceptedText = text); true").AsBoolean());
        intercepted.PerformMicrotaskCheckpoint();
        Assert.Equal("from worker", intercepted.Evaluate("interceptedText").AsString());
        Assert.True(intercepted.Evaluate("let scopedText = ''; navigator.serviceWorker.register('sw.js', { scope: '/app/' }).then(() => fetch('/offline')).then(r => r.text()).then(text => scopedText = text).catch(error => scopedText = error.name); true").AsBoolean());
        intercepted.PerformMicrotaskCheckpoint();
        Assert.Equal("from host", intercepted.Evaluate("scopedText").AsString());
        Assert.True(intercepted.Evaluate("let inScopeText = ''; navigator.serviceWorker.register('sw.js', { scope: '/app/' }).then(() => fetch('/app/offline')).then(r => r.text()).then(text => inScopeText = text); true").AsBoolean());
        intercepted.PerformMicrotaskCheckpoint();
        Assert.Equal("from worker", intercepted.Evaluate("inScopeText").AsString());
        Assert.True(intercepted.Evaluate("let cacheResult = ''; caches.open('v1').then(cache => cache.put('/offline', new Response('cached'))).then(() => caches.open('v1')).then(cache => cache.match('/offline')).then(response => response.text()).then(text => cacheResult = text); true").AsBoolean());
        intercepted.PerformMicrotaskCheckpoint();
        Assert.Equal("cached", intercepted.Evaluate("cacheResult").AsString());
        using var isolatedCache = new JintScriptEngine();
        DomBindings.Install(isolatedCache, Document.Parse("<html></html>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { ServiceWorkers = true }
        }));
        Assert.True(isolatedCache.Evaluate("let hasSharedCache = false; caches.has('v1').then(value => hasSharedCache = value); true").AsBoolean());
        isolatedCache.PerformMicrotaskCheckpoint();
        Assert.False(isolatedCache.Evaluate("hasSharedCache").AsBoolean());

        var storage = new ArtifactMemoryStorage();
        using (var persisted = new JintScriptEngine())
        {
            DomBindings.Install(persisted, Document.Parse("<html></html>", new FolioOptions
            {
                ArtifactId = "artifact-a",
                ArtifactStorage = storage,
                Capabilities = new FeatureCapabilities { ServiceWorkers = true }
            }));
            persisted.Evaluate("let stored = ''; caches.open('persistent').then(cache => cache.put('/x', new Response('saved'))); true");
            persisted.PerformMicrotaskCheckpoint();
        }
        using (var restored = new JintScriptEngine())
        {
            DomBindings.Install(restored, Document.Parse("<html></html>", new FolioOptions
            {
                ArtifactId = "artifact-a",
                ArtifactStorage = storage,
                Capabilities = new FeatureCapabilities { ServiceWorkers = true }
            }));
            Assert.True(restored.Evaluate("let restoredText = ''; caches.open('persistent').then(cache => cache.match('/x')).then(response => response.text()).then(text => restoredText = text); true").AsBoolean());
            restored.PerformMicrotaskCheckpoint();
            Assert.Equal("saved", restored.Evaluate("restoredText").AsString());
        }
        using (var otherArtifact = new JintScriptEngine())
        {
            DomBindings.Install(otherArtifact, Document.Parse("<html></html>", new FolioOptions
            {
                ArtifactId = "artifact-b",
                ArtifactStorage = storage,
                Capabilities = new FeatureCapabilities { ServiceWorkers = true }
            }));
            Assert.True(otherArtifact.Evaluate("let isolated = false; caches.open('persistent').then(cache => cache.match('/x')).then(response => isolated = response === undefined); true").AsBoolean());
            otherArtifact.PerformMicrotaskCheckpoint();
            Assert.True(otherArtifact.Evaluate("isolated").AsBoolean());
        }

        using var cacheLimited = new JintScriptEngine();
        DomBindings.Install(cacheLimited, Document.Parse("<html></html>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { ServiceWorkers = true },
            Limits = ResourceLimits.Default with { MaxServiceWorkerCacheBytes = 4 }
        }));
        Assert.True(cacheLimited.Evaluate("let quotaName = ''; caches.open('small').then(cache => cache.put('/x', new Response('12345'))).catch(error => quotaName = error.name); true").AsBoolean());
        cacheLimited.PerformMicrotaskCheckpoint();
        Assert.Equal("QuotaExceededError", cacheLimited.Evaluate("quotaName").AsString());

        var mutableLoader = new MutableServiceWorkerLoader();
        using var updating = new JintScriptEngine();
        DomBindings.Install(updating, Document.Parse("<html></html>", new FolioOptions
        {
            BaseUri = new Uri("https://worker.example/"),
            ResourceLoader = mutableLoader,
            Capabilities = new FeatureCapabilities { ServiceWorkers = true }
        }));
        Assert.True(updating.Evaluate("let updatedValue = 0; let updateEvents = 0; navigator.serviceWorker.register('sw.js').then(r => { r.onupdatefound = () => updateEvents++; r.active.onmessage = e => updatedValue = e.data; registrationForUpdate = r; }); true").AsBoolean());
        updating.PerformMicrotaskCheckpoint();
        mutableLoader.Version = 2;
        Assert.True(updating.Evaluate("registrationForUpdate.update(); true").AsBoolean());
        Assert.True(updating.Evaluate("registrationForUpdate.active.onmessage = e => updatedValue = e.data; registrationForUpdate.active.postMessage(40); updateEvents === 1 && updatedValue === 42").AsBoolean());
    }

    [Fact]
    public void HungServiceWorkerIsRejectedWithoutStoppingThePage()
    {
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, Document.Parse("<html></html>", new FolioOptions
        {
            BaseUri = new Uri("https://worker.example/"),
            ResourceLoader = new HungServiceWorkerLoader(),
            Capabilities = new FeatureCapabilities { ServiceWorkers = true },
            Limits = ResourceLimits.Default with { MaxWorkerExecutionTime = TimeSpan.FromMilliseconds(100) }
        }));

        Assert.True(engine.Evaluate("let workerErrorName = ''; navigator.serviceWorker.register('/hung.js').catch(error => workerErrorName = error.name); true").AsBoolean());
        engine.PerformMicrotaskCheckpoint();
        Assert.Equal("TimeoutError", engine.Evaluate("workerErrorName").AsString());
        Assert.Equal(2, engine.Evaluate("1 + 1").AsNumber());
    }

    [Fact]
    public void ServiceWorkerRegistrationMetadataPersistsPerArtifact()
    {
        var storage = new ArtifactMemoryStorage();
        FolioOptions Options() => new()
        {
            ArtifactId = "persistent-sw",
            ArtifactStorage = storage,
            BaseUri = new Uri("https://worker.example/"),
            ResourceLoader = new ServiceWorkerResourceLoader(),
            Capabilities = new FeatureCapabilities { ServiceWorkers = true }
        };
        using (var first = new JintScriptEngine())
        {
            DomBindings.Install(first, Document.Parse("<html></html>", Options()));
            first.Evaluate("navigator.serviceWorker.register('sw.js'); true");
            first.PerformMicrotaskCheckpoint();
        }
        var persisted = storage.Read("persistent-sw:service-worker-registration/%2F");
        Assert.True(persisted is not null, string.Join(",", storage.Keys("")));
        Assert.Contains("sw.js", persisted, StringComparison.Ordinal);
        using var reopened = new JintScriptEngine();
        DomBindings.Install(reopened, Document.Parse("<html></html>", Options()));
        Assert.True(reopened.Evaluate("let restoredUrl = ''; navigator.serviceWorker.register('sw.js').then(r => restoredUrl = r.scriptURL); true").AsBoolean());
        reopened.PerformMicrotaskCheckpoint();
        Assert.Equal("sw.js", reopened.Evaluate("restoredUrl").AsString());
        Assert.True(DomBindings.Dispose(reopened));
        Assert.True(reopened.Evaluate("navigator.serviceWorker.getRegistrations().then(items => items.length === 0); true").AsBoolean());
        reopened.PerformMicrotaskCheckpoint();
        Assert.Equal(0, reopened.Evaluate("navigator.serviceWorker.getRegistrations().then(items => items.length)").AsNumber());
        Assert.NotNull(storage.Read("persistent-sw:service-worker-registration/%2F"));
        using var cleanup = new JintScriptEngine();
        DomBindings.Install(cleanup, Document.Parse("<html></html>", Options()));
        cleanup.Evaluate("navigator.serviceWorker.register('sw.js').then(r => r.unregister()); true");
        cleanup.PerformMicrotaskCheckpoint();
        Assert.Null(storage.Read("persistent-sw:service-worker-registration/%2F"));
    }

    [Fact]
    public void WebRtcPeerConnectionIsCapabilityGated()
    {
        using var denied = new JintScriptEngine();
        DomBindings.Install(denied, Document.Parse("<html></html>"));
        Assert.ThrowsAny<Exception>(() => denied.Evaluate("new RTCPeerConnection()"));
        Assert.True(denied.Evaluate("typeof navigator.mediaDevices.getUserMedia === 'function'").AsBoolean());
        Assert.True(denied.Evaluate("navigator.mediaDevices.enumerateDevices().then(devices => devices.length === 0)").IsObject);

        using var allowed = new JintScriptEngine();
        DomBindings.Install(allowed, Document.Parse("<html></html>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebRtc = true }
        }));
        Assert.True(allowed.Evaluate("const pc = new RTCPeerConnection(); let iceEvents = 0; let stateEvents = 0; let candidates = 0; pc.onicegatheringstatechange = () => iceEvents++; pc.onicecandidate = e => candidates++; pc.onconnectionstatechange = () => stateEvents++; pc.createOffer().then(x => x.type === 'offer' && x instanceof RTCSessionDescription)").IsObject);
        Assert.True(allowed.Evaluate("const eventPc = new RTCPeerConnection(); let observedPeerEvents = ''; const peerStateHandler = e => { observedPeerEvents += e.type + (e.target === eventPc ? ':' : '?'); }; eventPc.addEventListener('connectionstatechange', peerStateHandler); eventPc.addEventListener('connectionstatechange', peerStateHandler); eventPc.addEventListener('iceconnectionstatechange', peerStateHandler); eventPc.addIceCandidate(null); eventPc.removeEventListener('connectionstatechange', peerStateHandler); observedPeerEvents === 'iceconnectionstatechange:connectionstatechange:'").AsBoolean());
        Assert.True(allowed.Evaluate("let devicesBeforeCapture = -1; navigator.mediaDevices.enumerateDevices().then(devices => devicesBeforeCapture = devices.length); true").AsBoolean());
        allowed.PerformMicrotaskCheckpoint();
        Assert.Equal(0, allowed.Evaluate("devicesBeforeCapture").AsNumber());
        Assert.True(allowed.Evaluate("navigator.mediaDevices.getSupportedConstraints().audio && navigator.mediaDevices.getSupportedConstraints().width && navigator.mediaDevices.getSupportedConstraints().echoCancellation").AsBoolean());
        Assert.True(allowed.Evaluate("const candidate = new RTCIceCandidate({ candidate: 'candidate:1', sdpMid: '0', sdpMLineIndex: 0 }); const description = new RTCSessionDescription({ type: 'offer', sdp: '' }); candidate.candidate === 'candidate:1' && description.type === 'offer'").AsBoolean());
        Assert.True(allowed.Evaluate("JSON.stringify(candidate.toJSON()).includes('candidate:1') && description.toJSON().type === 'offer'").AsBoolean());
        Assert.True(allowed.Evaluate("RTCRtpSender.getCapabilities('audio').codecs[0].mimeType === 'audio/opus' && RTCRtpReceiver.getCapabilities('video').codecs.length === 2").AsBoolean());
        Assert.True(allowed.Evaluate("navigator.mediaDevices.getUserMedia({ audio: true }).then(stream => stream instanceof MediaStream && stream.getTracks().length === 1 && stream.getAudioTracks().length === 1)").IsObject);
        Assert.True(allowed.Evaluate("let emptyCaptureError = ''; navigator.mediaDevices.getUserMedia({ audio: false, video: false }).catch(error => emptyCaptureError = error.name); true").AsBoolean());
        allowed.PerformMicrotaskCheckpoint();
        Assert.Equal("TypeError", allowed.Evaluate("emptyCaptureError").AsString());
        Assert.True(allowed.Evaluate("let devicesAfterCapture = ''; navigator.mediaDevices.enumerateDevices().then(devices => devicesAfterCapture = devices.length + ':' + devices[0].kind); true").AsBoolean());
        allowed.PerformMicrotaskCheckpoint();
        Assert.Equal("2:audioinput", allowed.Evaluate("devicesAfterCapture").AsString());
        using var peerLimited = new JintScriptEngine();
        DomBindings.Install(peerLimited, Document.Parse("<html></html>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebRtc = true },
            Limits = ResourceLimits.Default with { MaxWebRtcConnections = 1 }
        }));
        Assert.True(peerLimited.Evaluate("const firstPeer = new RTCPeerConnection(); true").AsBoolean());
        Assert.ThrowsAny<Exception>(() => peerLimited.Evaluate("new RTCPeerConnection()"));
        Assert.True(peerLimited.Evaluate("firstPeer.close(); const secondPeer = new RTCPeerConnection(); secondPeer.connectionState === 'new'").AsBoolean());
        using var routing = new JintScriptEngine();
        DomBindings.Install(routing, Document.Parse("<html></html>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebRtc = true },
            WebRtcAllowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "https://stun.example", "stun:stun.example" }
        }));
        Assert.ThrowsAny<Exception>(() => routing.Evaluate("new RTCPeerConnection({ iceServers: [{ urls: 'https://blocked.example' }] })"));
        Assert.True(routing.Evaluate("const routedPeer = new RTCPeerConnection({ iceServers: [{ urls: 'https://stun.example' }] }); routedPeer.getConfiguration().iceServers.length === 1").AsBoolean());
        Assert.True(routing.Evaluate("const stunPeer = new RTCPeerConnection({ iceServers: [{ urls: 'stun:stun.example' }] }); stunPeer.getConfiguration().iceServers.length === 1").AsBoolean());
        Assert.ThrowsAny<Exception>(() => routing.Evaluate("new RTCPeerConnection({ iceServers: [{ urls: 'not a url' }] })"));
        Assert.ThrowsAny<Exception>(() => routing.Evaluate("new RTCPeerConnection({ iceTransportPolicy: 'invalid' })"));
        Assert.True(routing.Evaluate("const policyPeer = new RTCPeerConnection({ iceTransportPolicy: 'relay', bundlePolicy: 'max-bundle', rtcpMuxPolicy: 'negotiate' }); policyPeer.getConfiguration().iceTransportPolicy === 'relay' && policyPeer.getConfiguration().bundlePolicy === 'max-bundle' && policyPeer.getConfiguration().rtcpMuxPolicy === 'negotiate'").AsBoolean());
        Assert.ThrowsAny<Exception>(() => routing.Evaluate("routedPeer.setConfiguration({ iceServers: [{ urls: 'https://blocked.example' }] })"));
        Assert.True(routing.Evaluate("routedPeer.setConfiguration({ iceServers: [{ urls: 'https://stun.example' }] }); routedPeer.getConfiguration().iceServers[0].urls === 'https://stun.example'").AsBoolean());
        routing.Evaluate("routedPeer.close();");
        routing.Evaluate("stunPeer.close();");
        using var permissionDenied = new JintScriptEngine();
        DomBindings.Install(permissionDenied, Document.Parse("<html></html>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebRtc = true },
            MediaCapturePermission = request => request.Audio && !request.Video
        }));
        Assert.True(permissionDenied.Evaluate("let cameraDenied = ''; navigator.mediaDevices.getUserMedia({ video: true }).catch(error => cameraDenied = error.name); true").AsBoolean());
        permissionDenied.PerformMicrotaskCheckpoint();
        Assert.Equal("NotAllowedError", permissionDenied.Evaluate("cameraDenied").AsString());
        Assert.True(permissionDenied.Evaluate("navigator.mediaDevices.getUserMedia({ audio: true }).then(stream => stream.getAudioTracks().length === 1)").IsObject);
        Assert.True(permissionDenied.Evaluate("let capturedTrack; navigator.mediaDevices.getUserMedia({ audio: true }).then(stream => capturedTrack = stream.getAudioTracks()[0]); true").AsBoolean());
        permissionDenied.PerformMicrotaskCheckpoint();
        Assert.Equal("live", permissionDenied.Evaluate("capturedTrack.readyState").AsString());
        Assert.True(DomBindings.Dispose(permissionDenied));
        Assert.Equal("ended", permissionDenied.Evaluate("capturedTrack.readyState").AsString());
        Assert.True(allowed.Evaluate("const track = new MediaStreamTrack('audio'); const stream = new MediaStream([track]); const copied = new MediaStream(stream); let changes = ''; stream.onaddtrack = e => changes += e.type + e.track.kind; stream.onremovetrack = e => changes += e.type === 'removetrack' ? '-' : '?'; track.onmute = e => changes += e.type[0]; track.onunmute = e => changes += e.type[0]; track.onended = e => changes += e.type[0]; const second = new MediaStreamTrack('video'); stream.addTrack(second); stream.removeTrack(second); track.muted = true; track.muted = false; track.enabled = false; track.contentHint = 'speech'; stream.getAudioTracks().length === 1 && copied.getTracks().length === 1 && stream.active; track.applyConstraints({ width: 640 }); const clone = track.clone(); track.getConstraints().width === 640 && track.getSettings().kind === 'audio' && track.getSettings().width === 640 && track.getCapabilities().kind === 'audio'; track.stop(); let rejected = false; track.applyConstraints({ width: 320 }).catch(() => rejected = true); clone.readyState === 'live' && !clone.enabled && clone.contentHint === 'speech' && clone.getConstraints().width === 640 && track.readyState === 'ended' && stream.id.length > 0 && changes === 'addtrackvideo-mue'").AsBoolean());
        Assert.True(allowed.Evaluate("const eventTrack = new MediaStreamTrack('audio'); let trackEvents = ''; const onTrackEvent = e => trackEvents += e.type; eventTrack.addEventListener('mute', onTrackEvent); eventTrack.addEventListener('mute', onTrackEvent); eventTrack.addEventListener('unmute', onTrackEvent); eventTrack.addEventListener('ended', onTrackEvent); eventTrack.muted = true; eventTrack.muted = false; eventTrack.stop(); eventTrack.removeEventListener('ended', onTrackEvent); trackEvents === 'muteunmuteended'").AsBoolean());
        Assert.True(allowed.Evaluate("const observedStream = new MediaStream(); const observedTrack = new MediaStreamTrack('video'); let streamEvents = ''; const onStreamEvent = e => streamEvents += e.type; observedStream.addEventListener('addtrack', onStreamEvent); observedStream.addEventListener('addtrack', onStreamEvent); observedStream.addEventListener('removetrack', onStreamEvent); observedStream.addTrack(observedTrack); observedStream.getTrackById(observedTrack.id) === observedTrack; observedStream.removeTrack(observedTrack); observedStream.getTrackById(observedTrack.id) === null && streamEvents === 'addtrackremovetrack'").AsBoolean());
        Assert.True(allowed.Evaluate("const constrained = new MediaStreamTrack('video'); let invalidType = false; constrained.applyConstraints('bad').catch(error => invalidType = error.name === 'TypeError'); let invalidRange = false; constrained.applyConstraints({ width: { exact: -1 } }).catch(error => invalidRange = error.name === 'OverconstrainedError'); true").AsBoolean());
        allowed.PerformMicrotaskCheckpoint();
        Assert.True(allowed.Evaluate("invalidType && invalidRange").AsBoolean());
        allowed.PerformMicrotaskCheckpoint();
        Assert.True(allowed.Evaluate("rejected").AsBoolean());
        Assert.True(allowed.Evaluate("let localDescriptionRejected = false; pc.setLocalDescription({ type: 'invalid' }).catch(error => localDescriptionRejected = error.name === 'TypeError'); true").AsBoolean());
        allowed.PerformMicrotaskCheckpoint();
        Assert.True(allowed.Evaluate("localDescriptionRejected").AsBoolean());
        Assert.True(allowed.Evaluate("pc.iceGatheringState === 'new' && pc.getConfiguration() !== null; pc.setLocalDescription({ type: 'offer' }); pc.iceGatheringState === 'complete'; pc.restartIce(); pc.iceGatheringState === 'new'").AsBoolean());
        Assert.True(allowed.Evaluate("const remote = pc.addTransceiver('video'); let remoteKind = ''; let remoteStreams = 0; pc.ontrack = e => { remoteKind = e.track.kind; remoteStreams = e.streams.length; }; pc.signalingState === 'have-local-offer' && candidates === 1; pc.setRemoteDescription({ type: 'answer' }); pc.signalingState === 'stable' && remoteKind === 'video' && remoteStreams === 1").AsBoolean());
        Assert.True(allowed.Evaluate("let badDescription = false; pc.setRemoteDescription({ type: 'invalid' }).catch(error => badDescription = error.name === 'TypeError'); true").AsBoolean());
        allowed.PerformMicrotaskCheckpoint();
        Assert.True(allowed.Evaluate("badDescription").AsBoolean());
        Assert.True(allowed.Evaluate("const stoppedPc = new RTCPeerConnection(); const stoppedTransceiver = stoppedPc.addTransceiver('audio'); stoppedTransceiver.stop(); let stoppedEvents = 0; stoppedPc.ontrack = () => stoppedEvents++; stoppedPc.setRemoteDescription({ type: 'answer' }); const noStoppedTrack = stoppedEvents === 0; stoppedPc.close(); noStoppedTrack").AsBoolean());
        Assert.True(allowed.Evaluate("let closedDescriptionRejected = false; stoppedPc.setRemoteDescription({ type: 'answer' }).catch(error => closedDescriptionRejected = error.name === 'InvalidStateError'); true").AsBoolean());
        allowed.PerformMicrotaskCheckpoint();
        Assert.True(allowed.Evaluate("closedDescriptionRejected").AsBoolean());
        Assert.True(allowed.Evaluate("let connectedEvents = 0; pc.onconnectionstatechange = e => connectedEvents++; pc.setRemoteDescription({ type: 'answer' }); pc.connectionState === 'connected' && pc.iceConnectionState === 'connected' && connectedEvents === 1").AsBoolean());
        Assert.True(allowed.Evaluate("const icePc = new RTCPeerConnection(); let iceConnected = 0; icePc.onconnectionstatechange = e => iceConnected++; icePc.addIceCandidate({ candidate: 'candidate:1' }); icePc.connectionState === 'connected' && iceConnected === 1").AsBoolean());
        Assert.True(allowed.Evaluate("icePc.addIceCandidate(null).then(() => true)").IsObject);
        Assert.True(allowed.Evaluate("let badCandidate = false; icePc.addIceCandidate({ sdpMid: '0' }).catch(error => badCandidate = error.name === 'TypeError'); true").AsBoolean());
        allowed.PerformMicrotaskCheckpoint();
        Assert.True(allowed.Evaluate("badCandidate").AsBoolean());
        Assert.True(allowed.Evaluate("let negotiations = 0; pc.onnegotiationneeded = e => { if (e.type === 'negotiationneeded' && e.target === pc) negotiations++; }; const sender = pc.addTrack({ kind: 'audio' }); sender.setParameters({ encodings: [{ maxBitrate: 64000 }] }); pc.getSenders().length === 1 && sender.track.kind === 'audio' && sender.getParameters().encodings.length === 1 && negotiations === 1").AsBoolean());
        Assert.True(allowed.Evaluate("const duplicateTrack = { kind: 'video' }; const duplicatePc = new RTCPeerConnection(); duplicatePc.addTrack(duplicateTrack); let duplicateRejected = false; try { duplicatePc.addTrack(duplicateTrack); } catch (error) { duplicateRejected = error.name === 'InvalidAccessError'; } duplicateRejected").AsBoolean());
        Assert.ThrowsAny<Exception>(() => allowed.Evaluate("pc.addTrack({ kind: 'data' })"));
        Assert.True(allowed.Evaluate("let removedNegotiations = 0; pc.onnegotiationneeded = e => removedNegotiations++; const removedSender = pc.addTrack({ kind: 'audio' }); pc.removeTrack(removedSender); removedSender.track === null && pc.getSenders().length === 2 && removedNegotiations === 2").AsBoolean());
        Assert.ThrowsAny<Exception>(() => allowed.Evaluate("pc.removeTrack({ track: null })"));
        Assert.True(allowed.Evaluate("const transceiver = pc.addTransceiver('video', { direction: 'recvonly' }); transceiver.setCodecPreferences([{ mimeType: 'video/VP8' }]); transceiver.kind === 'video' && transceiver.direction === 'recvonly' && transceiver.codecPreferences.length === 1 && pc.getTransceivers().length === 2; transceiver.stop(); transceiver.direction === 'inactive'").AsBoolean());
        Assert.ThrowsAny<Exception>(() => allowed.Evaluate("pc.addTransceiver('data')"));
        Assert.ThrowsAny<Exception>(() => allowed.Evaluate("pc.addTransceiver('audio', { direction: 'invalid' })"));
        Assert.True(allowed.Evaluate("const defaultDirection = pc.addTransceiver('audio'); defaultDirection.direction === 'sendrecv'").AsBoolean());
        Assert.ThrowsAny<Exception>(() => allowed.Evaluate("defaultDirection.setCodecPreferences('bad')"));
        Assert.ThrowsAny<Exception>(() => allowed.Evaluate("defaultDirection.setCodecPreferences([{ mimeType: 'video/VP8' }])"));
        Assert.True(allowed.Evaluate("pc.getReceivers().length === pc.getTransceivers().length && pc.getReceivers()[0].track.kind === 'video'").AsBoolean());
        Assert.True(allowed.Evaluate("RTCRtpSender.getCapabilities('invalid') === null && RTCRtpReceiver.getCapabilities('invalid') === null").AsBoolean());
        Assert.True(allowed.Evaluate("const sending = pc.addTransceiver('audio'); sending.sender.setParameters({ encodings: [{ maxBitrate: 32000 }] }); sending.sender.getParameters().encodings.length === 1; sending.sender.replaceTrack({ kind: 'audio' })").IsObject);
        allowed.PerformMicrotaskCheckpoint();
        Assert.True(allowed.Evaluate("sending.sender.track.kind === 'audio'").AsBoolean());
        Assert.True(allowed.Evaluate("pc.setConfiguration({ iceServers: [] }); pc.getConfiguration().iceServers.length === 0; pc.getStats().then(stats => stats instanceof Map && stats.get('pc').type === 'peer-connection' && stats.get('pc').connectionState === 'connected' && stats.get('data-channels').dataChannelsOpened === 0 && stats.get('outbound-0').type === 'outbound-rtp' && stats.get('inbound-0').type === 'inbound-rtp')").IsObject);
        Assert.True(allowed.Evaluate("const dc = pc.createDataChannel('events'); let received = 0; let opened = 0; let closed = false; let eventTypes = ''; dc.onopen = e => { opened++; eventTypes += e.type; }; dc.onmessage = e => { received = e.data; eventTypes += e.target === dc ? e.type : '?'; }; dc.onclose = e => { closed = true; eventTypes += e.type; }; dc.send(7); received === 7 && opened === 1 && eventTypes === 'openmessage' && dc.bufferedAmount === 0 && dc.binaryType === 'blob'; dc.close(); closed && dc.readyState === 'closed' && eventTypes === 'openmessageclose'").AsBoolean());
        Assert.True(allowed.Evaluate("const configured = pc.createDataChannel('configured', { ordered: false, maxRetransmits: 2 }); let observed = ''; const handler = e => { observed += e.data; }; configured.addEventListener('message', handler); configured.addEventListener('message', handler); configured.send('ok'); configured.removeEventListener('message', handler); configured.send('ignored'); configured.ordered === false && configured.maxRetransmits === 2 && observed === 'ok'").AsBoolean());
        Assert.True(allowed.Evaluate("const negotiated = pc.createDataChannel('negotiated', { negotiated: true, id: 7, protocol: 'json' }); negotiated.id === 7 && negotiated.negotiated && negotiated.protocol === 'json' && negotiated.label === 'negotiated' && negotiated.maxPacketLifeTime === null").AsBoolean());
        Assert.ThrowsAny<Exception>(() => allowed.Evaluate("pc.createDataChannel('invalid', { negotiated: true })"));
        Assert.ThrowsAny<Exception>(() => allowed.Evaluate("pc.createDataChannel('invalid', { id: 65535 })"));
        Assert.ThrowsAny<Exception>(() => allowed.Evaluate("pc.createDataChannel('invalid', { maxRetransmits: 1.5 })"));
        Assert.ThrowsAny<Exception>(() => allowed.Evaluate("pc.createDataChannel('invalid', { maxPacketLifeTime: 1, maxRetransmits: 1 })"));
        Assert.ThrowsAny<Exception>(() => allowed.Evaluate("pc.createDataChannel('invalid', { maxRetransmits: -1 })"));
        Assert.True(allowed.Evaluate("const closingPc = new RTCPeerConnection(); const ownedChannel = closingPc.createDataChannel('owned'); let ownedClosed = false; ownedChannel.onclose = () => ownedClosed = true; closingPc.close(); ownedClosed && ownedChannel.readyState === 'closed'").AsBoolean());
        Assert.True(allowed.Evaluate("let devicesBeforeDispose = -1; navigator.mediaDevices.enumerateDevices().then(devices => devicesBeforeDispose = devices.length); true").AsBoolean());
        allowed.PerformMicrotaskCheckpoint();
        Assert.Equal(2, allowed.Evaluate("devicesBeforeDispose").AsNumber());
        Assert.True(DomBindings.Dispose(allowed));
        Assert.Equal("closed", allowed.Evaluate("pc.connectionState").AsString());
        Assert.True(allowed.Evaluate("let devicesAfterDispose = -1; navigator.mediaDevices.enumerateDevices().then(devices => devicesAfterDispose = devices.length); true").AsBoolean());
        allowed.PerformMicrotaskCheckpoint();
        Assert.Equal(0, allowed.Evaluate("devicesAfterDispose").AsNumber());
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
            Capabilities = new FeatureCapabilities { Iframes = true, Workers = true }
        }));
        Assert.True(allowed.Evaluate("const f = document.getElementById('f'); let seen = 0; let messageOrigin = ''; let messageSource = false; let messageTarget = false; f.contentWindow.onmessage = x => { seen = x.data; messageOrigin = x.origin; messageSource = x.source === f.contentWindow; messageTarget = x.target === f.contentWindow; }; f.contentWindow.postMessage(8, 'https://other.example'); seen === 0").AsBoolean());
        Assert.True(allowed.Evaluate("f.contentWindow.postMessage(9, 'https://child.example'); seen === 9 && messageOrigin === 'https://child.example' && messageSource && messageTarget && f.contentWindow.origin === 'https://child.example'").AsBoolean());
        Assert.True(allowed.Evaluate("let listenerSeen = 0; const listener = e => listenerSeen = e.data; f.contentWindow.addEventListener('message', listener); f.contentWindow.postMessage(10, 'https://child.example'); f.contentWindow.removeEventListener('message', listener); listenerSeen === 10").AsBoolean());
        Assert.True(allowed.Evaluate("const frameRelay = new Worker('data:text/javascript,' + encodeURIComponent('onmessage = function(e) { e.data.postMessage(88); };')); const frameChannel = new MessageChannel(); let framePortValue = 0; frameChannel.port1.onmessage = e => framePortValue = e.data; f.contentWindow.onmessage = e => frameRelay.postMessage(e.data, [e.data]); f.contentWindow.postMessage(frameChannel.port2, 'https://child.example', [frameChannel.port2]); framePortValue === 88").AsBoolean());
        Assert.True(allowed.Evaluate("f.contentWindow.close(); f.contentWindow.closed").AsBoolean());
        Assert.True(allowed.Evaluate("f instanceof HTMLIFrameElement").AsBoolean());
        Assert.True(allowed.Evaluate("const channel = new MessageChannel(); let channelSeen = ''; channel.port2.onmessage = e => channelSeen += e.data; channel.port1.postMessage('a'); channel.port1.postMessage('b'); channelSeen === 'ab' && channel.port1 instanceof MessagePort && channel.port2 instanceof MessagePort").AsBoolean());

        using var inline = new JintScriptEngine();
        DomBindings.Install(inline, Document.Parse("<iframe id='f' src='data:text/html,%3Chtml%3E%3Cbody%3E%3Cp%20id%3D%22child%22%3Eok%3C%2Fp%3E%3C%2Fbody%3E%3C%2Fhtml%3E'></iframe>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Iframes = true }
        }));
        Assert.True(inline.Evaluate("const child = document.getElementById('f').contentDocument; child !== null && child.getElementById('child').textContent === 'ok'").AsBoolean());
        using var loadedFrame = new JintScriptEngine();
        DomBindings.Install(loadedFrame, Document.Parse("<iframe id='f' srcdoc='<p>loaded</p>'></iframe>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Iframes = true }
        }));
        Assert.True(loadedFrame.Evaluate("const loadedFrame = document.getElementById('f'); loadedFrame.contentWindow; let loadType = ''; let loadTarget = false; let loadListeners = 0; loadedFrame.onload = e => { loadType = e.type; loadTarget = e.target === loadedFrame; }; loadedFrame.addEventListener('load', () => loadListeners++); loadedFrame.contentDocument !== null; loadType === 'load' && loadTarget && loadListeners === 1").AsBoolean());
        using var srcdoc = new JintScriptEngine();
        DomBindings.Install(srcdoc, Document.Parse("<iframe id='f' srcdoc='<p id=child>inline</p>'></iframe>", new FolioOptions
        {
            BaseUri = new Uri("https://parent.example/"),
            Capabilities = new FeatureCapabilities { Iframes = true }
        }));
        Assert.True(srcdoc.Evaluate("const srcdocFrame = document.getElementById('f'); srcdocFrame.contentDocument.getElementById('child').textContent === 'inline' && srcdocFrame.contentWindow.document === srcdocFrame.contentDocument && srcdocFrame.contentWindow.origin === 'https://parent.example'").AsBoolean());
        Assert.True(srcdoc.Evaluate("srcdocFrame.setAttribute('srcdoc', '<p id=changed>updated</p>'); srcdocFrame.contentDocument.getElementById('changed').textContent === 'updated'").AsBoolean());
        Assert.True(srcdoc.Evaluate("srcdocFrame.srcdoc === '<p id=changed>updated</p>'; srcdocFrame.allow = 'camera; microphone'; srcdocFrame.allow === 'camera; microphone'").AsBoolean());
        using var sandboxed = new JintScriptEngine();
        DomBindings.Install(sandboxed, Document.Parse("<iframe id='f' sandbox='allow-scripts' src='https://child.example/app'></iframe><iframe id='same' sandbox='allow-same-origin allow-scripts' src='https://child.example/app'></iframe>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Iframes = true }
        }));
        Assert.True(sandboxed.Evaluate("document.getElementById('f').contentWindow.origin === 'null' && document.getElementById('f').sandbox === 'allow-scripts' && document.getElementById('same').contentWindow.origin === 'https://child.example'").AsBoolean());
        Assert.True(sandboxed.Evaluate("document.getElementById('same').contentDocument === null").AsBoolean());
        Assert.True(sandboxed.Evaluate("const dynamic = document.getElementById('f'); dynamic.sandbox = 'allow-same-origin allow-scripts'; dynamic.contentDocument; dynamic.contentWindow.origin === 'https://child.example'").AsBoolean());
        Assert.True(sandboxed.Evaluate("dynamic.setAttribute('sandbox', 'allow-scripts'); dynamic.contentDocument; dynamic.contentWindow.origin === 'null'; dynamic.removeAttribute('sandbox'); dynamic.contentDocument; dynamic.contentWindow.origin === 'https://child.example'").AsBoolean());
        Assert.True(sandboxed.Evaluate("let opaqueMessageOrigin = ''; dynamic.contentWindow.onmessage = e => opaqueMessageOrigin = e.origin; dynamic.sandbox = 'allow-scripts'; dynamic.contentWindow.postMessage('opaque', '*'); opaqueMessageOrigin === 'null'").AsBoolean());
        Assert.True(sandboxed.Evaluate("dynamic.sandbox = ''; dynamic.src = 'https://other.example/app'; dynamic.contentWindow.origin === 'https://other.example'; dynamic.setAttribute('src', 'https://third.example/app'); dynamic.contentWindow.origin === 'https://third.example'").AsBoolean());

        using var remote = new JintScriptEngine();
        DomBindings.Install(remote, Document.Parse("<iframe id='f' src='/child.html'></iframe>", new FolioOptions
        {
            BaseUri = new Uri("https://child.example/"),
            ResourceLoader = new IframeResourceLoader(),
            Capabilities = new FeatureCapabilities { Iframes = true }
        }));
        Assert.True(remote.Evaluate("document.getElementById('f').contentDocument.getElementById('remote').textContent === 'loaded'").AsBoolean());

        using var crossOrigin = new JintScriptEngine();
        DomBindings.Install(crossOrigin, Document.Parse("<iframe id='f' src='https://other.example/child.html'></iframe>", new FolioOptions
        {
            BaseUri = new Uri("https://child.example/"),
            ResourceLoader = new IframeResourceLoader(),
            Capabilities = new FeatureCapabilities { Iframes = true }
        }));
        Assert.True(crossOrigin.Evaluate("document.getElementById('f').contentDocument === null && document.getElementById('f').contentWindow.document === null").AsBoolean());

        using var refused = new JintScriptEngine();
        DomBindings.Install(refused, Document.Parse("<iframe id='f' src='/blocked.html'><p id='fallback'>fallback</p></iframe>", new FolioOptions
        {
            BaseUri = new Uri("https://child.example/"),
            ResourceLoader = new RefusingIframeResourceLoader(),
            Capabilities = new FeatureCapabilities { Iframes = true }
        }));
        Assert.True(refused.Evaluate("const blocked = document.getElementById('f'); blocked.contentDocument === null && blocked.textContent.includes('fallback')").AsBoolean());

        using var limited = new JintScriptEngine();
        DomBindings.Install(limited, Document.Parse("<iframe id='one'></iframe><iframe id='two'></iframe>", new FolioOptions
        {
            Limits = ResourceLimits.Default with { MaxIframes = 1 },
            Capabilities = new FeatureCapabilities { Iframes = true }
        }));
        Assert.True(limited.Evaluate("document.getElementById('one').contentWindow.closed === false").AsBoolean());
        Assert.ThrowsAny<Exception>(() => limited.Evaluate("document.getElementById('two').contentWindow"));
    }

    [Fact]
    public void CanvasImageDataArrayMutationsAreAppliedByPutImageData()
    {
        var document = Document.Parse("<canvas id='chart' width='1' height='1'></canvas>", new FolioOptions { Raster = new Folio.Skia.SkiaRasterBackend() });
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
        var document = Document.Parse("<canvas id='chart'></canvas>", new FolioOptions { Raster = new Folio.Skia.SkiaRasterBackend() });
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
        var document = Document.Parse("<canvas id='chart'></canvas>", new FolioOptions { Raster = new Folio.Skia.SkiaRasterBackend() });
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
        Assert.Equal(0, engine.Evaluate("document.styleSheets[0].insertRule('.b { color: blue; }')").AsNumber()); // the index defaults to 0
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

    // The content host is built in the configuration the tests were built in (CI builds Release, a developer Debug):
    // .../tests/Folio.Scripting.Tests/bin/<Configuration>/net10.0/ names it.
    private static string ContentHostPath()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Parent!.Name;
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/Folio.ContentHost/bin", configuration, "net10.0/Folio.ContentHost.dll"));
    }

    [Fact]
    public async Task ContentProcessClientRunsScriptThroughContentHost()
    {
        var host = ContentHostPath();
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
    public async Task ContentProcessPublishesCanvasPixelsThroughSharedMemory()
    {
        var host = ContentHostPath();
        Assert.True(File.Exists(host), $"Content host was not built: {host}");
        await using var client = ContentProcessClient.Start("dotnet", $"\"{host}\"", new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromSeconds(10)));
        var received = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var diagnostic = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += (message, _) =>
        {
            if (message.Kind == ContentMessageKind.Frame) received.TrySetResult(message);
            if (message.Kind == ContentMessageKind.Diagnostic) diagnostic.TrySetResult(message);
            return ValueTask.CompletedTask;
        };
        await client.SendAsync(ContentMessageKind.Load, new
        {
            html = "<canvas id=c width=32 height=32></canvas>",
            script = "const c=document.getElementById('c'), x=c.getContext('2d'); x.fillStyle='red'; x.fillRect(0,0,8,8); 1",
            url = "canvas-test"
        }, TestContext.Current.CancellationToken);
        var completed = await Task.WhenAny(received.Task, diagnostic.Task).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var diagnosticMessage = diagnostic.Task.IsCompletedSuccessfully ? (await diagnostic.Task).Payload.ToString() : "The content host returned neither a frame nor a diagnostic.";
        Assert.True(ReferenceEquals(received.Task, completed), diagnosticMessage);
        var frame = await received.Task;
        var descriptor = frame.Payload.GetProperty("canvasFrame");
        var path = descriptor.GetProperty("path").GetString();
        Assert.False(string.IsNullOrWhiteSpace(path));
        using var store = new CanvasFrameStore(path!, descriptor.GetProperty("width").GetInt32(), descriptor.GetProperty("height").GetInt32(), descriptor.GetProperty("stride").GetInt32());
        var pixels = store.ReadLatest(out var sequence);
        Assert.True(sequence > 0);
        Assert.Contains(pixels, value => value != 0);
    }

    [Fact]
    public async Task ContentProcessStopsAnInfiniteScriptWithDiagnostic()
    {
        var host = ContentHostPath();
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









