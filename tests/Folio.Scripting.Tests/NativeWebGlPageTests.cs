using System.Runtime.InteropServices;
using Folio.Dom;
using Folio.Scripting.Dom;
using Folio.Scripting.Gpu;
using Folio.Scripting.Jint;
using Folio.Skia;

namespace Folio.Scripting.Tests;

[Collection("Content process round trips")]
public sealed class NativeWebGlPageTests
{
    [Fact]
    public void TextureUploadBeyondGpuBudgetIsRefused()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='1' height='1'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true },
            Limits = ResourceLimits.Default with { MaxGpuBytes = 8 },
            Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false});var t=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D,t);gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,2,2,0,gl.RGBA,gl.UNSIGNED_BYTE,new Uint8Array(16));");
            Assert.Equal(0x0505, engine.Evaluate("gl.getError()").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void PresentationIgnoresAndRestoresPagePixelPacking()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='3' height='2'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("var canvas=document.getElementById('c');var gl=canvas.getContext('webgl2',{antialias:false,depth:false});gl.pixelStorei(gl.PACK_ALIGNMENT,8);gl.clearColor(1,0,0,1);gl.clear(gl.COLOR_BUFFER_BIT);");
            var element = document.Node.Descendants.OfType<ElementNode>().Single(node => node.LocalName == "canvas");
            var state = Assert.IsType<CanvasElementState>(element.CanvasSurface);
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, state.Snapshot()!.Pixels.Span[^4..].ToArray());
            Assert.Equal(8, engine.Evaluate("gl.getParameter(gl.PACK_ALIGNMENT)").AsNumber());
            engine.Evaluate("gl.pixelStorei(0x0D02,8);gl.pixelStorei(0x0D03,1);gl.pixelStorei(0x0D04,1);gl.clearColor(0,0,1,1);gl.clear(gl.COLOR_BUFFER_BIT);");
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, state.Snapshot()!.Pixels.Span[..4].ToArray());
            Assert.Equal(0, engine.Evaluate("gl.getError()").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 4)]
    [InlineData(4, 0)]
    public void ZeroSizeContextSurvivesResizeWithoutFallback(int width, int height)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse($"<canvas id='c' width='{width}' height='{height}'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("var canvas=document.getElementById('c');var gl=canvas.getContext('webgl2',{antialias:false,depth:false});");
            Assert.False(engine.Evaluate("gl === null").AsBoolean());
            Assert.Equal(width, engine.Evaluate("gl.drawingBufferWidth").AsNumber());
            Assert.Equal(height, engine.Evaluate("gl.drawingBufferHeight").AsNumber());
            var element = document.Node.Descendants.OfType<ElementNode>().Single(node => node.LocalName == "canvas");
            var state = Assert.IsType<CanvasElementState>(element.CanvasSurface);
            Assert.Null(state.Fallback);
            Assert.Null(state.Snapshot());
            engine.Evaluate("canvas.width=4;canvas.height=4;gl.clearColor(1,0,0,1);gl.clear(gl.COLOR_BUFFER_BIT);");
            Assert.True(engine.Evaluate("canvas.getContext('webgl2') === gl").AsBoolean());
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, state.Snapshot()!.Pixels.Span[..4].ToArray());
            engine.Evaluate("canvas.width=0;gl.clear(gl.COLOR_BUFFER_BIT);");
            Assert.Equal(0, engine.Evaluate("gl.drawingBufferWidth").AsNumber());
            Assert.Null(state.Fallback);
            Assert.Null(state.Snapshot());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void MalformedPageShaderKeepsInfoLogAndReportsHostDiagnostic()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='4' height='4'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        var diagnostics = new List<Diagnostic>();
        document.Node.DiagnosticSink = diagnostics.Add;
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false});var shader=gl.createShader(gl.VERTEX_SHADER);gl.shaderSource(shader,'#version 300 es\\nvoid main( {');gl.compileShader(shader);");
            Assert.False(engine.Evaluate("gl.getShaderParameter(shader,gl.COMPILE_STATUS)").AsBoolean());
            Assert.False(string.IsNullOrWhiteSpace(engine.Evaluate("gl.getShaderInfoLog(shader)").AsString()));
            Assert.Equal(0, engine.Evaluate("gl.getError()").AsNumber());
            Assert.Contains(diagnostics, diagnostic => diagnostic.Feature == "webgl" && diagnostic.Message.Contains("compilation failed", StringComparison.Ordinal));
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Theory]
    [InlineData("triangle", "webgl2", 300)]
    [InlineData("triangle", "webgl2", 100)]
    [InlineData("triangle", "webgl", 100)]
    [InlineData("indexed", "webgl2", 300)]
    [InlineData("indexed", "webgl", 100)]
    [InlineData("instanced", "webgl2", 300)]
    public void PageDrawMatchesIndependentChromeGolden(string mode, string kind, int language)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='32' height='32'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            var fixtures = Path.Combine(AppContext.BaseDirectory, "Native", "Fixtures", "WebGl");
            engine.SetGlobalProperty("mode", ScriptValue.FromString(mode));
            engine.SetGlobalProperty("contextKind", ScriptValue.FromString(kind));
            engine.SetGlobalProperty("shaderLanguage", ScriptValue.FromNumber(language));
            engine.Evaluate(File.ReadAllText(Path.Combine(fixtures, "draw.js")));
            var canvasNode = document.Node.Descendants.OfType<ElementNode>().Single(node => node.LocalName == "canvas");
            var state = Assert.IsType<CanvasElementState>(canvasNode.CanvasSurface);
            var image = Assert.IsAssignableFrom<Folio.Imaging.IImageHandle>(state.Snapshot());
            using var golden = SkiaSharp.SKBitmap.Decode(Path.Combine(fixtures, mode + ".png"));
            Assert.NotNull(golden);
            Assert.Equal(32, golden.Width);
            Assert.Equal(32, golden.Height);
            var actual = image.Pixels.Span;
            for (var y = 0; y < golden.Height; y++)
                for (var x = 0; x < golden.Width; x++)
                {
                    var expected = golden.GetPixel(x, y);
                    var offset = (y * 32 + x) * 4;
                    Assert.Equal(new byte[] { expected.Red, expected.Green, expected.Blue, expected.Alpha }, actual.Slice(offset, 4).ToArray());
                }
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeTextureUploadAndFramebufferAttachmentAreRealObjects()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='2' height='2'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("""
                var canvas=document.getElementById('c');
                var gl=canvas.getContext('webgl2',{antialias:false,depth:false});
                var texture=gl.createTexture();
                gl.bindTexture(gl.TEXTURE_2D,texture);
                gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,gl.NEAREST);
                gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,2,2,0,gl.RGBA,gl.UNSIGNED_BYTE,
                    new Uint8Array([255,0,0,255, 0,255,0,255, 0,0,255,255, 255,255,255,255]));
                var framebuffer=gl.createFramebuffer();
                gl.bindFramebuffer(gl.FRAMEBUFFER,framebuffer);
                gl.framebufferTexture2D(gl.FRAMEBUFFER,gl.COLOR_ATTACHMENT0,gl.TEXTURE_2D,texture,0);
                var status=gl.checkFramebufferStatus(gl.FRAMEBUFFER);
                var pixels=new Uint8Array(4);
                gl.readPixels(0,0,1,1,gl.RGBA,gl.UNSIGNED_BYTE,pixels);
                """);
            Assert.Equal(0x8CD5, engine.Evaluate("status").AsNumber());
            Assert.Equal(0, engine.Evaluate("gl.getError()").AsNumber());
            Assert.Equal(255, engine.Evaluate("pixels[0]").AsNumber());
            Assert.Equal(0, engine.Evaluate("gl.isTexture(texture) ? 0 : 1").AsNumber());
            Assert.Equal(0, engine.Evaluate("gl.isFramebuffer(framebuffer) ? 0 : 1").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeTextureIsSampledByTheRealShaderPipeline()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='8' height='8'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("""
                var canvas=document.getElementById('c');
                var gl=canvas.getContext('webgl2',{antialias:false,depth:false,preserveDrawingBuffer:true});
                function makeShader(type, source) { var s=gl.createShader(type); gl.shaderSource(s,source); gl.compileShader(s); if(!gl.getShaderParameter(s,gl.COMPILE_STATUS)) throw Error(gl.getShaderInfoLog(s)); return s; }
                var program=gl.createProgram();
                gl.attachShader(program,makeShader(gl.VERTEX_SHADER,'#version 300 es\nlayout(location=0) in vec2 p; layout(location=1) in vec2 uv; out vec2 v; void main(){gl_Position=vec4(p,0,1);v=uv;}'));
                gl.attachShader(program,makeShader(gl.FRAGMENT_SHADER,'#version 300 es\nprecision highp float; in vec2 v; uniform sampler2D tex; out vec4 outColor; void main(){outColor=texture(tex,v);}'));
                gl.linkProgram(program); gl.useProgram(program);
                var tex=gl.createTexture(); gl.activeTexture(gl.TEXTURE0); gl.bindTexture(gl.TEXTURE_2D,tex);
                gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,gl.NEAREST); gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAG_FILTER,gl.NEAREST);
                gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,1,1,0,gl.RGBA,gl.UNSIGNED_BYTE,new Uint8Array([255,64,16,255]));
                gl.uniform1i(gl.getUniformLocation(program,'tex'),0);
                var buffer=gl.createBuffer(); gl.bindBuffer(gl.ARRAY_BUFFER,buffer);
                gl.bufferData(gl.ARRAY_BUFFER,new Float32Array([-1,-1,0,0, 1,-1,1,0, 0,1,.5,1]),gl.STATIC_DRAW);
                gl.vertexAttribPointer(0,2,gl.FLOAT,false,16,0); gl.enableVertexAttribArray(0);
                gl.vertexAttribPointer(1,2,gl.FLOAT,false,16,8); gl.enableVertexAttribArray(1);
                gl.viewport(0,0,8,8); gl.drawArrays(gl.TRIANGLES,0,3);
                var pixels=new Uint8Array(4); gl.readPixels(4,4,1,1,gl.RGBA,gl.UNSIGNED_BYTE,pixels);
                """);
            Assert.Equal(0, engine.Evaluate("gl.getError()").AsNumber());
            Assert.Equal(255, engine.Evaluate("pixels[0]").AsNumber());
            Assert.Equal(64, engine.Evaluate("pixels[1]").AsNumber());
            Assert.Equal(16, engine.Evaluate("pixels[2]").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeTextureSubImageUpdatesExistingStorage()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='2' height='2'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("""
                var canvas=document.getElementById('c');
                var gl=canvas.getContext('webgl2',{antialias:false,depth:false,preserveDrawingBuffer:true});
                function sh(t,s){var x=gl.createShader(t);gl.shaderSource(x,s);gl.compileShader(x);if(!gl.getShaderParameter(x,gl.COMPILE_STATUS))throw Error(gl.getShaderInfoLog(x));return x;}
                var p=gl.createProgram();gl.attachShader(p,sh(gl.VERTEX_SHADER,'#version 300 es\nvoid main(){gl_Position=vec4(-1.0,-1.0,0.0,1.0);if(gl_VertexID==1)gl_Position=vec4(3.0,-1.0,0.0,1.0);if(gl_VertexID==2)gl_Position=vec4(-1.0,3.0,0.0,1.0);}'));gl.attachShader(p,sh(gl.FRAGMENT_SHADER,'#version 300 es\nprecision highp float;uniform sampler2D t;out vec4 c;void main(){c=texelFetch(t,ivec2(0),0);}'));gl.linkProgram(p);gl.useProgram(p);
                var t=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D,t);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,gl.NEAREST);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAG_FILTER,gl.NEAREST);
                gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,1,1,0,gl.RGBA,gl.UNSIGNED_BYTE,new Uint8Array([255,0,0,255]));
                gl.texSubImage2D(gl.TEXTURE_2D,0,0,0,1,1,0,gl.RGBA,gl.UNSIGNED_BYTE,new Uint8Array([0,255,0,255]));
                gl.viewport(0,0,2,2);gl.drawArrays(gl.TRIANGLES,0,3);var px=new Uint8Array(4);gl.readPixels(1,1,1,1,gl.RGBA,gl.UNSIGNED_BYTE,px);
                """);
            Assert.Equal(0, engine.Evaluate("gl.getError()").AsNumber());
            Assert.Equal(0, engine.Evaluate("px[0]").AsNumber());
            Assert.Equal(255, engine.Evaluate("px[1]").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeTextureUploadsExpandPackedColorTypes()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='1' height='1'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("""
                var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false});
                var errors=[];
                var pixels=[];
                function upload(internalFormat, format, type, value) {
                    var t=gl.createTexture(); gl.bindTexture(gl.TEXTURE_2D,t);
                    gl.texImage2D(gl.TEXTURE_2D,0,internalFormat,1,1,0,format,type,new Uint16Array([value]));
                    errors.push(gl.getError());
                    var f=gl.createFramebuffer(); gl.bindFramebuffer(gl.FRAMEBUFFER,f);
                    gl.framebufferTexture2D(gl.FRAMEBUFFER,gl.COLOR_ATTACHMENT0,gl.TEXTURE_2D,t,0);
                    var p=new Uint8Array(4); gl.readPixels(0,0,1,1,gl.RGBA,gl.UNSIGNED_BYTE,p); pixels.push([p[0],p[1],p[2],p[3]]);
                }
                upload(0x8D62,gl.RGB,gl.UNSIGNED_SHORT_5_6_5,0xF800);
                upload(gl.RGBA,gl.RGBA,gl.UNSIGNED_SHORT_4_4_4_4,0xF00F);
                upload(gl.RGBA,gl.RGBA,gl.UNSIGNED_SHORT_5_5_5_1,0xF801);
                var sub=gl.createTexture(); gl.bindTexture(gl.TEXTURE_2D,sub);
                gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,1,1,0,gl.RGBA,gl.UNSIGNED_BYTE,new Uint8Array([0,0,0,255]));
                gl.texSubImage2D(gl.TEXTURE_2D,0,0,0,1,1,0,gl.RGBA,gl.UNSIGNED_SHORT_4_4_4_4,new Uint16Array([0xF00F]));
                errors.push(gl.getError());
                """);
            Assert.Equal(0, engine.Evaluate("errors[0]").AsNumber());
            Assert.Equal(0, engine.Evaluate("errors[1]").AsNumber());
            Assert.Equal(0, engine.Evaluate("errors[2]").AsNumber());
            Assert.Equal(0, engine.Evaluate("errors[3]").AsNumber());
            Assert.Equal(255, engine.Evaluate("pixels[0][0]").AsNumber());
            Assert.Equal(0, engine.Evaluate("pixels[0][1]").AsNumber());
            Assert.Equal(0, engine.Evaluate("pixels[0][2]").AsNumber());
            Assert.Equal(255, engine.Evaluate("pixels[1][0]").AsNumber());
            Assert.Equal(0, engine.Evaluate("pixels[1][1]").AsNumber());
            Assert.Equal(0, engine.Evaluate("pixels[1][2]").AsNumber());
            Assert.Equal(255, engine.Evaluate("pixels[1][3]").AsNumber());
            Assert.Equal(255, engine.Evaluate("pixels[2][0]").AsNumber());
            Assert.Equal(255, engine.Evaluate("pixels[2][3]").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeTextureUploadsReportWebGlErrorsBeforeCallingAngle()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='1' height='1'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false});var t=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D,t);gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,1,1,0,0xDEAD,gl.UNSIGNED_BYTE,new Uint8Array(4));var badFormat=gl.getError();gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,2,2,0,gl.RGBA,gl.UNSIGNED_BYTE,new Uint8Array(4));var badLength=gl.getError();");
            Assert.Equal(0x0500, engine.Evaluate("badFormat").AsNumber());
            Assert.Equal(0x0501, engine.Evaluate("badLength").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeTextureSamplingReadsAnExplicitMipmapLevel()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='2' height='2'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("""
                var canvas=document.getElementById('c');
                var gl=canvas.getContext('webgl2',{antialias:false,depth:false,preserveDrawingBuffer:true});
                function shader(kind,source){var s=gl.createShader(kind);gl.shaderSource(s,source);gl.compileShader(s);if(!gl.getShaderParameter(s,gl.COMPILE_STATUS))throw Error(gl.getShaderInfoLog(s));return s;}
                var p=gl.createProgram();
                gl.attachShader(p,shader(gl.VERTEX_SHADER,'#version 300 es\nvoid main(){gl_Position=vec4(-1.0,-1.0,0.0,1.0);if(gl_VertexID==1)gl_Position=vec4(3.0,-1.0,0.0,1.0);if(gl_VertexID==2)gl_Position=vec4(-1.0,3.0,0.0,1.0);}'));
                gl.attachShader(p,shader(gl.FRAGMENT_SHADER,'#version 300 es\nprecision highp float;uniform sampler2D tex;out vec4 color;void main(){color=textureLod(tex,vec2(0.25),1.0);}'));
                gl.linkProgram(p);if(!gl.getProgramParameter(p,gl.LINK_STATUS))throw Error(gl.getProgramInfoLog(p));gl.useProgram(p);
                var t=gl.createTexture();gl.activeTexture(gl.TEXTURE0);gl.bindTexture(gl.TEXTURE_2D,t);
                gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,gl.NEAREST_MIPMAP_NEAREST);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAG_FILTER,gl.NEAREST);
                var level0=new Uint8Array(4*4*4);for(var i=0;i<level0.length;i+=4){level0[i]=255;level0[i+3]=255;}
                gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,4,4,0,gl.RGBA,gl.UNSIGNED_BYTE,level0);
                gl.generateMipmap(gl.TEXTURE_2D);
                gl.uniform1i(gl.getUniformLocation(p,'tex'),0);gl.viewport(0,0,2,2);gl.drawArrays(gl.TRIANGLES,0,3);
                var px=new Uint8Array(4);gl.readPixels(1,1,1,1,gl.RGBA,gl.UNSIGNED_BYTE,px);
                """);
            Assert.Equal(0, engine.Evaluate("gl.getError()").AsNumber());
            Assert.Equal(255, engine.Evaluate("px[0]").AsNumber());
            Assert.Equal(0, engine.Evaluate("px[1]").AsNumber());
            Assert.Equal(0, engine.Evaluate("px[2]").AsNumber());
            Assert.Equal(255, engine.Evaluate("px[3]").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeGeneratedMipLevelsExposeDimensions()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='2' height='2'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true },
            Limits = ResourceLimits.Default with { MaxGpuBytes = 256 },
            Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false});var t=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D,t);gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,4,4,0,gl.RGBA,gl.UNSIGNED_BYTE,new Uint8Array(64));var before=gl.getError();gl.generateMipmap(gl.TEXTURE_2D);var after=gl.getError();var w=gl.getTexLevelParameter(gl.TEXTURE_2D,1,gl.TEXTURE_WIDTH);var h=gl.getTexLevelParameter(gl.TEXTURE_2D,2,gl.TEXTURE_HEIGHT);");
            Assert.Equal(0, engine.Evaluate("before").AsNumber());
            Assert.Equal(0, engine.Evaluate("after").AsNumber());
            Assert.Equal(2, engine.Evaluate("w").AsNumber());
            Assert.Equal(1, engine.Evaluate("h").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeTextureMinificationUsesGeneratedMipLevel()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='2' height='2'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("""
                var canvas=document.getElementById('c');
                var gl=canvas.getContext('webgl2',{antialias:false,depth:false,preserveDrawingBuffer:true});
                function shader(kind,source){var s=gl.createShader(kind);gl.shaderSource(s,source);gl.compileShader(s);if(!gl.getShaderParameter(s,gl.COMPILE_STATUS))throw Error(gl.getShaderInfoLog(s));return s;}
                var p=gl.createProgram();
                gl.attachShader(p,shader(gl.VERTEX_SHADER,'#version 300 es\nlayout(location=0) in vec2 a;out vec2 uv;void main(){gl_Position=vec4(a,0,1);uv=(a+1.0)*8.0;}'));
                gl.attachShader(p,shader(gl.FRAGMENT_SHADER,'#version 300 es\nprecision highp float;in vec2 uv;uniform sampler2D tex;out vec4 color;void main(){color=texture(tex,uv);}'));
                gl.linkProgram(p);if(!gl.getProgramParameter(p,gl.LINK_STATUS))throw Error(gl.getProgramInfoLog(p));gl.useProgram(p);
                var t=gl.createTexture();gl.activeTexture(gl.TEXTURE0);gl.bindTexture(gl.TEXTURE_2D,t);
                gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,gl.LINEAR_MIPMAP_NEAREST);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAG_FILTER,gl.LINEAR);
                var pixels=new Uint8Array(4*4*4);for(var y=0;y<4;y++)for(var x=0;x<4;x++){var i=(y*4+x)*4;var c=((x+y)&1)?255:0;pixels[i]=c;pixels[i+2]=255-c;pixels[i+3]=255;}
                gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,4,4,0,gl.RGBA,gl.UNSIGNED_BYTE,pixels);gl.generateMipmap(gl.TEXTURE_2D);gl.uniform1i(gl.getUniformLocation(p,'tex'),0);
                var b=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,b);gl.bufferData(gl.ARRAY_BUFFER,new Float32Array([-1,-1,3,-1,-1,3]),gl.STATIC_DRAW);gl.vertexAttribPointer(0,2,gl.FLOAT,false,0,0);gl.enableVertexAttribArray(0);gl.viewport(0,0,2,2);gl.drawArrays(gl.TRIANGLES,0,3);
                var px=new Uint8Array(4);gl.readPixels(1,1,1,1,gl.RGBA,gl.UNSIGNED_BYTE,px);
                """);
            Assert.Equal(0, engine.Evaluate("gl.getError()").AsNumber());
            Assert.InRange(engine.Evaluate("px[0]").AsNumber(), 120, 136);
            Assert.Equal(0, engine.Evaluate("px[1]").AsNumber());
            Assert.InRange(engine.Evaluate("px[2]").AsNumber(), 120, 136);
            Assert.Equal(255, engine.Evaluate("px[3]").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeGeneratedMipLevelsRespectTheGpuBudget()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='2' height='2'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true },
            Limits = ResourceLimits.Default with { MaxGpuBytes = 99 },
            Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false});var t=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D,t);gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,4,4,0,gl.RGBA,gl.UNSIGNED_BYTE,new Uint8Array(64));var before=gl.getError();gl.generateMipmap(gl.TEXTURE_2D);var after=gl.getError();");
            Assert.Equal(0, engine.Evaluate("before").AsNumber());
            Assert.Equal(0x0505, engine.Evaluate("after").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeCubeMapTextureIsSampledByTheRealShaderPipeline()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='2' height='2'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("""
                var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false,preserveDrawingBuffer:true});
                function sh(t,s){var x=gl.createShader(t);gl.shaderSource(x,s);gl.compileShader(x);if(!gl.getShaderParameter(x,gl.COMPILE_STATUS))throw Error(gl.getShaderInfoLog(x));return x;}
                var p=gl.createProgram();gl.attachShader(p,sh(gl.VERTEX_SHADER,'#version 300 es\nvoid main(){gl_Position=vec4(-1.0,-1.0,0.0,1.0);if(gl_VertexID==1)gl_Position=vec4(3.0,-1.0,0.0,1.0);if(gl_VertexID==2)gl_Position=vec4(-1.0,3.0,0.0,1.0);}'));gl.attachShader(p,sh(gl.FRAGMENT_SHADER,'#version 300 es\nprecision highp float;uniform samplerCube t;out vec4 c;void main(){c=texture(t,vec3(1.0,0.0,0.0));}'));gl.linkProgram(p);gl.useProgram(p);
                var t=gl.createTexture();gl.bindTexture(gl.TEXTURE_CUBE_MAP,t);gl.texParameteri(gl.TEXTURE_CUBE_MAP,gl.TEXTURE_MIN_FILTER,gl.NEAREST);gl.texParameteri(gl.TEXTURE_CUBE_MAP,gl.TEXTURE_MAG_FILTER,gl.NEAREST);
                var faces=[gl.TEXTURE_CUBE_MAP_POSITIVE_X,gl.TEXTURE_CUBE_MAP_NEGATIVE_X,gl.TEXTURE_CUBE_MAP_POSITIVE_Y,gl.TEXTURE_CUBE_MAP_NEGATIVE_Y,gl.TEXTURE_CUBE_MAP_POSITIVE_Z,gl.TEXTURE_CUBE_MAP_NEGATIVE_Z];for(var i=0;i<6;i++)gl.texImage2D(faces[i],0,gl.RGBA,1,1,0,gl.RGBA,gl.UNSIGNED_BYTE,new Uint8Array(i==0?[12,220,33,255]:[0,0,0,255]));
                gl.uniform1i(gl.getUniformLocation(p,'t'),0);gl.viewport(0,0,2,2);gl.drawArrays(gl.TRIANGLES,0,3);var px=new Uint8Array(4);gl.readPixels(1,1,1,1,gl.RGBA,gl.UNSIGNED_BYTE,px);
                """);
            Assert.Equal(0, engine.Evaluate("gl.getError()").AsNumber());
            Assert.Equal(12, engine.Evaluate("px[0]").AsNumber());
            Assert.Equal(220, engine.Evaluate("px[1]").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeWebGl2ClearBufferuivClearsIntegerColorAttachment()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='2' height='2'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("""
                var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false,preserveDrawingBuffer:true});var t=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D,t);gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA8UI,1,1,0,gl.RGBA_INTEGER,gl.UNSIGNED_BYTE,new Uint8Array([0,0,0,255]));var f=gl.createFramebuffer();gl.bindFramebuffer(gl.FRAMEBUFFER,f);gl.framebufferTexture2D(gl.FRAMEBUFFER,gl.COLOR_ATTACHMENT0,gl.TEXTURE_2D,t,0);var status=gl.checkFramebufferStatus(gl.FRAMEBUFFER);var before=gl.getError();gl.clearBufferuiv(gl.COLOR,0,new Uint32Array([7,11,13,255]));var clearError=gl.getError();
                """);
            Assert.Equal(0x8CD5, engine.Evaluate("status").AsNumber());
            Assert.Equal(0, engine.Evaluate("before").AsNumber());
            Assert.Equal(0, engine.Evaluate("clearError").AsNumber());
            Assert.Equal(0, engine.Evaluate("gl.getError()").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeWebGl2ClearBufferfiClearsDepthStencilAttachment()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='2' height='2'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false,preserveDrawingBuffer:true});var r=gl.createRenderbuffer();gl.bindRenderbuffer(gl.RENDERBUFFER,r);gl.renderbufferStorage(gl.RENDERBUFFER,gl.DEPTH24_STENCIL8,2,2);var f=gl.createFramebuffer();gl.bindFramebuffer(gl.FRAMEBUFFER,f);gl.framebufferRenderbuffer(gl.FRAMEBUFFER,gl.DEPTH_STENCIL_ATTACHMENT,gl.RENDERBUFFER,r);var status=gl.checkFramebufferStatus(gl.FRAMEBUFFER);var before=gl.getError();gl.clearBufferfi(gl.DEPTH_STENCIL,0,1,0);var clearError=gl.getError();");
            Assert.Equal(0x8CD5, engine.Evaluate("status").AsNumber());
            Assert.Equal(0, engine.Evaluate("before").AsNumber());
            Assert.Equal(0, engine.Evaluate("clearError").AsNumber());
            Assert.Equal(0, engine.Evaluate("gl.getError()").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeWebGl2ClearBufferfvClearsColorAttachment()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='2' height='2'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false,preserveDrawingBuffer:true});var t=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D,t);gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,2,2,0,gl.RGBA,gl.UNSIGNED_BYTE,new Uint8Array(16));var f=gl.createFramebuffer();gl.bindFramebuffer(gl.FRAMEBUFFER,f);gl.framebufferTexture2D(gl.FRAMEBUFFER,gl.COLOR_ATTACHMENT0,gl.TEXTURE_2D,t,0);gl.clearBufferfv(gl.COLOR,0,new Float32Array([0.2,0.8,0.3,1]));var px=new Uint8Array(4);gl.readPixels(1,1,1,1,gl.RGBA,gl.UNSIGNED_BYTE,px);");
            Assert.Equal(0, engine.Evaluate("gl.getError()").AsNumber());
            Assert.InRange(engine.Evaluate("px[0]").AsNumber(), 40, 65);
            Assert.InRange(engine.Evaluate("px[1]").AsNumber(), 195, 225);
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeWebGl2SupportsMultisampleRenderbufferStorage()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='2' height='2'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false,preserveDrawingBuffer:true});var r=gl.createRenderbuffer();gl.bindRenderbuffer(gl.RENDERBUFFER,r);gl.renderbufferStorageMultisample(gl.RENDERBUFFER,4,gl.RGBA8,2,2);var f=gl.createFramebuffer();gl.bindFramebuffer(gl.FRAMEBUFFER,f);gl.framebufferRenderbuffer(gl.FRAMEBUFFER,gl.COLOR_ATTACHMENT0,gl.RENDERBUFFER,r);var status=gl.checkFramebufferStatus(gl.FRAMEBUFFER);gl.clearColor(0,1,0,1);gl.clear(gl.COLOR_BUFFER_BIT);");
            Assert.Equal(0x8CD5, engine.Evaluate("status").AsNumber());
            Assert.Equal(0, engine.Evaluate("gl.getError()").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeWebGl2BlitsBetweenFramebufferAttachments()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='2' height='2'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false,preserveDrawingBuffer:true});function tex(){var t=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D,t);gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,2,2,0,gl.RGBA,gl.UNSIGNED_BYTE,new Uint8Array(16));return t;}var st=tex(),dt=tex();var sf=gl.createFramebuffer();gl.bindFramebuffer(gl.FRAMEBUFFER,sf);gl.framebufferTexture2D(gl.FRAMEBUFFER,gl.COLOR_ATTACHMENT0,gl.TEXTURE_2D,st,0);gl.viewport(0,0,2,2);gl.clearColor(1,0,0,1);gl.clear(gl.COLOR_BUFFER_BIT);var df=gl.createFramebuffer();gl.bindFramebuffer(gl.FRAMEBUFFER,df);gl.framebufferTexture2D(gl.FRAMEBUFFER,gl.COLOR_ATTACHMENT0,gl.TEXTURE_2D,dt,0);gl.bindFramebuffer(gl.READ_FRAMEBUFFER,sf);gl.bindFramebuffer(gl.DRAW_FRAMEBUFFER,df);gl.blitFramebuffer(0,0,2,2,0,0,2,2,gl.COLOR_BUFFER_BIT,gl.NEAREST);gl.bindFramebuffer(gl.FRAMEBUFFER,df);var px=new Uint8Array(4);gl.readPixels(1,1,1,1,gl.RGBA,gl.UNSIGNED_BYTE,px);");
            Assert.Equal(0, engine.Evaluate("gl.getError()").AsNumber());
            Assert.Equal(255, engine.Evaluate("px[0]").AsNumber());
            Assert.Equal(0, engine.Evaluate("px[1]").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeCopyTextureImageCanBeSampledAfterFramebufferClear()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='2' height='2'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("""
                var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false,preserveDrawingBuffer:true});gl.viewport(0,0,2,2);gl.clearColor(0.1,0.7,0.2,1);gl.clear(gl.COLOR_BUFFER_BIT);
                var t=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D,t);gl.copyTexImage2D(gl.TEXTURE_2D,0,gl.RGBA,0,0,2,2,0);gl.clearColor(0.2,0.1,0.8,1);gl.clear(gl.COLOR_BUFFER_BIT);gl.copyTexSubImage2D(gl.TEXTURE_2D,0,0,0,0,0,2,2);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,gl.NEAREST);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAG_FILTER,gl.NEAREST);
                var p=gl.createProgram();function sh(k,s){var x=gl.createShader(k);gl.shaderSource(x,s);gl.compileShader(x);if(!gl.getShaderParameter(x,gl.COMPILE_STATUS))throw Error(gl.getShaderInfoLog(x));return x;}gl.attachShader(p,sh(gl.VERTEX_SHADER,'#version 300 es\nvoid main(){gl_Position=vec4(-1,-1,0,1);if(gl_VertexID==1)gl_Position=vec4(3,-1,0,1);if(gl_VertexID==2)gl_Position=vec4(-1,3,0,1);}'));gl.attachShader(p,sh(gl.FRAGMENT_SHADER,'#version 300 es\nprecision highp float;uniform sampler2D t;out vec4 c;void main(){c=texelFetch(t,ivec2(0),0);}'));gl.linkProgram(p);gl.useProgram(p);gl.uniform1i(gl.getUniformLocation(p,'t'),0);gl.drawArrays(gl.TRIANGLES,0,3);var px=new Uint8Array(4);gl.readPixels(1,1,1,1,gl.RGBA,gl.UNSIGNED_BYTE,px);
                """);
            Assert.Equal(0, engine.Evaluate("gl.getError()").AsNumber());
            Assert.InRange(engine.Evaluate("px[0]").AsNumber(), 35, 65);
            Assert.InRange(engine.Evaluate("px[1]").AsNumber(), 15, 40);
            Assert.InRange(engine.Evaluate("px[2]").AsNumber(), 190, 225);
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeImmutableTextureStorageCanBeUpdatedAndSampled()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='2' height='2'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false,preserveDrawingBuffer:true});var t=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D,t);gl.texStorage2D(gl.TEXTURE_2D,1,gl.RGBA8,1,1);gl.texSubImage2D(gl.TEXTURE_2D,0,0,0,1,1,0,gl.RGBA,gl.UNSIGNED_BYTE,new Uint8Array([31,173,77,255]));var p=gl.createProgram();function sh(k,s){var x=gl.createShader(k);gl.shaderSource(x,s);gl.compileShader(x);if(!gl.getShaderParameter(x,gl.COMPILE_STATUS))throw Error(gl.getShaderInfoLog(x));return x;}gl.attachShader(p,sh(gl.VERTEX_SHADER,'#version 300 es\\nvoid main(){gl_Position=vec4(-1,-1,0,1);if(gl_VertexID==1)gl_Position=vec4(3,-1,0,1);if(gl_VertexID==2)gl_Position=vec4(-1,3,0,1);}'));gl.attachShader(p,sh(gl.FRAGMENT_SHADER,'#version 300 es\\nprecision highp float;uniform sampler2D t;out vec4 c;void main(){c=texelFetch(t,ivec2(0),0);}'));gl.linkProgram(p);gl.useProgram(p);gl.uniform1i(gl.getUniformLocation(p,'t'),0);gl.viewport(0,0,2,2);gl.drawArrays(gl.TRIANGLES,0,3);var px=new Uint8Array(4);gl.readPixels(1,1,1,1,gl.RGBA,gl.UNSIGNED_BYTE,px);");
            Assert.Equal(0, engine.Evaluate("gl.getError()").AsNumber());
            Assert.Equal(31, engine.Evaluate("px[0]").AsNumber());
            Assert.Equal(173, engine.Evaluate("px[1]").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeTextureMipmapGenerationUsesTheRealContext()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='2' height='2'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false});var t=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D,t);gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,2,2,0,gl.RGBA,gl.UNSIGNED_BYTE,new Uint8Array(16));gl.generateMipmap(gl.TEXTURE_2D);");
            Assert.Equal(0, engine.Evaluate("gl.getError()").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeRenderbufferAttachmentUsesTheRealContext()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='2' height='2'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false});var rb=gl.createRenderbuffer();gl.bindRenderbuffer(gl.RENDERBUFFER,rb);gl.renderbufferStorage(gl.RENDERBUFFER,gl.RGBA4,2,2);var fb=gl.createFramebuffer();gl.bindFramebuffer(gl.FRAMEBUFFER,fb);gl.framebufferRenderbuffer(gl.FRAMEBUFFER,gl.COLOR_ATTACHMENT0,gl.RENDERBUFFER,rb);var status=gl.checkFramebufferStatus(gl.FRAMEBUFFER);");
            Assert.Equal(0x8CD5, engine.Evaluate("status").AsNumber());
            Assert.Equal(0, engine.Evaluate("gl.getError()").AsNumber());
            Assert.Equal(0, engine.Evaluate("gl.isRenderbuffer(rb) ? 0 : 1").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeWebGl2AcceptsTheCoreRenderbufferFormatMatrix()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='2' height='2'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend()
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false});var formats=[gl.RGBA8,gl.RGB8,gl.RGB565,gl.DEPTH_COMPONENT16,gl.DEPTH_COMPONENT24,gl.DEPTH_COMPONENT32F,gl.DEPTH24_STENCIL8,gl.DEPTH32F_STENCIL8,gl.STENCIL_INDEX8];var errors=[];for(var i=0;i<formats.length;i++){var rb=gl.createRenderbuffer();gl.bindRenderbuffer(gl.RENDERBUFFER,rb);gl.renderbufferStorage(gl.RENDERBUFFER,formats[i],2,2);errors.push(gl.getError());}");
            Assert.Equal(9, engine.Evaluate("errors.length").AsNumber());
            for (var index = 0; index < 9; index++)
                Assert.True(engine.Evaluate($"errors[{index}]").AsNumber() == 0, $"renderbuffer format index {index}");
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeReadPixelsSupportsRgbByteRowsAndPackAlignment()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='2' height='1'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false});gl.clearColor(1,0,0,1);gl.clear(gl.COLOR_BUFFER_BIT);gl.pixelStorei(gl.PACK_ALIGNMENT,1);var p=new Uint8Array(6);gl.readPixels(0,0,2,1,gl.RGB,gl.UNSIGNED_BYTE,p);");
            Assert.Equal(0, engine.Evaluate("gl.getError()").AsNumber());
            Assert.Equal(255, engine.Evaluate("p[0]").AsNumber());
            Assert.Equal(0, engine.Evaluate("p[1]").AsNumber());
            Assert.Equal(0, engine.Evaluate("p[2]").AsNumber());
            Assert.Equal(255, engine.Evaluate("p[3]").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeReadPixelsSupportsPackedRgb565()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='1' height='1'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false});gl.clearColor(1,0,0,1);gl.clear(gl.COLOR_BUFFER_BIT);var p=new Uint16Array(1);gl.readPixels(0,0,1,1,gl.RGB,gl.UNSIGNED_SHORT_5_6_5,p);var e=gl.getError();");
            Assert.Equal(0, engine.Evaluate("e").AsNumber());
            Assert.Equal(0xF800, engine.Evaluate("p[0]").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeReadPixelsSupportsPackedRgbaFormats()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='1' height='1'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false});gl.clearColor(1,0,0,1);gl.clear(gl.COLOR_BUFFER_BIT);var a=new Uint16Array(1);var b=new Uint16Array(1);gl.readPixels(0,0,1,1,gl.RGBA,gl.UNSIGNED_SHORT_4_4_4_4,a);gl.readPixels(0,0,1,1,gl.RGBA,gl.UNSIGNED_SHORT_5_5_5_1,b);");
            Assert.Equal(0, engine.Evaluate("gl.getError()").AsNumber());
            Assert.Equal(0xF00F, engine.Evaluate("a[0]").AsNumber());
            Assert.Equal(0xF801, engine.Evaluate("b[0]").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeWebGl2SupportsThreeDimensionalTextureStorage()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='2' height='2'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false});var t=gl.createTexture();gl.bindTexture(gl.TEXTURE_3D,t);gl.texImage3D(gl.TEXTURE_3D,0,gl.RGBA,2,2,2,0,gl.RGBA,gl.UNSIGNED_BYTE,new Uint8Array(32));gl.generateMipmap(gl.TEXTURE_3D);");
            Assert.Equal(0, engine.Evaluate("gl.getError()").AsNumber());
            Assert.Equal(0, engine.Evaluate("gl.isTexture(t) ? 0 : 1").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeWebGl2ImmutableStorageCanBeUpdatedForVolumes()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='2' height='2'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("""
                var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false,preserveDrawingBuffer:true});
                var t=gl.createTexture();gl.bindTexture(gl.TEXTURE_3D,t);gl.texStorage3D(gl.TEXTURE_3D,1,gl.RGBA8,1,1,1);gl.texSubImage3D(gl.TEXTURE_3D,0,0,0,0,1,1,1,0,gl.RGBA,gl.UNSIGNED_BYTE,new Uint8Array([19,181,73,255]));
                var p=gl.createProgram();function sh(k,s){var x=gl.createShader(k);gl.shaderSource(x,s);gl.compileShader(x);if(!gl.getShaderParameter(x,gl.COMPILE_STATUS))throw Error(gl.getShaderInfoLog(x));return x;}
                gl.attachShader(p,sh(gl.VERTEX_SHADER,'#version 300 es\nvoid main(){gl_Position=vec4(-1,-1,0,1);if(gl_VertexID==1)gl_Position=vec4(3,-1,0,1);if(gl_VertexID==2)gl_Position=vec4(-1,3,0,1);}'));gl.attachShader(p,sh(gl.FRAGMENT_SHADER,'#version 300 es\nprecision highp float;uniform highp sampler3D t;out vec4 c;void main(){c=texelFetch(t,ivec3(0),0);}'));gl.linkProgram(p);gl.useProgram(p);gl.uniform1i(gl.getUniformLocation(p,'t'),0);gl.viewport(0,0,2,2);gl.drawArrays(gl.TRIANGLES,0,3);var px=new Uint8Array(4);gl.readPixels(1,1,1,1,gl.RGBA,gl.UNSIGNED_BYTE,px);
                """);
            Assert.Equal(0, engine.Evaluate("gl.getError()").AsNumber());
            Assert.Equal(19, engine.Evaluate("px[0]").AsNumber());
            Assert.Equal(181, engine.Evaluate("px[1]").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeWebGl2SamplesThreeDimensionalTextures()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='8' height='8'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("""
                var canvas=document.getElementById('c'); var gl=canvas.getContext('webgl2',{antialias:false,depth:false,preserveDrawingBuffer:true});
                function sh(t,s){var x=gl.createShader(t);gl.shaderSource(x,s);gl.compileShader(x);if(!gl.getShaderParameter(x,gl.COMPILE_STATUS))throw Error(gl.getShaderInfoLog(x));return x;}
                var p=gl.createProgram();gl.attachShader(p,sh(gl.VERTEX_SHADER,'#version 300 es\nlayout(location=0) in vec2 a;out vec3 v;void main(){gl_Position=vec4(a,0,1);v=vec3((a+1.0)*0.5,0.5);}'));gl.attachShader(p,sh(gl.FRAGMENT_SHADER,'#version 300 es\nprecision highp float;in vec3 v;uniform highp sampler3D tex;out vec4 c;void main(){c=texture(tex,v);}'));gl.linkProgram(p);gl.useProgram(p);
                var t=gl.createTexture();gl.activeTexture(gl.TEXTURE0);gl.bindTexture(gl.TEXTURE_3D,t);gl.texParameteri(gl.TEXTURE_3D,gl.TEXTURE_MIN_FILTER,gl.NEAREST);gl.texParameteri(gl.TEXTURE_3D,gl.TEXTURE_MAG_FILTER,gl.NEAREST);gl.texImage3D(gl.TEXTURE_3D,0,gl.RGBA,1,1,1,0,gl.RGBA,gl.UNSIGNED_BYTE,new Uint8Array([255,32,8,255]));gl.uniform1i(gl.getUniformLocation(p,'tex'),0);
                var b=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,b);gl.bufferData(gl.ARRAY_BUFFER,new Float32Array([-1,-1,1,-1,0,1]),gl.STATIC_DRAW);gl.vertexAttribPointer(0,2,gl.FLOAT,false,0,0);gl.enableVertexAttribArray(0);gl.viewport(0,0,8,8);gl.drawArrays(gl.TRIANGLES,0,3);var px=new Uint8Array(4);gl.readPixels(4,4,1,1,gl.RGBA,gl.UNSIGNED_BYTE,px);
                """);
            Assert.Equal(0, engine.Evaluate("gl.getError()").AsNumber());
            Assert.Equal(255, engine.Evaluate("px[0]").AsNumber());
            Assert.Equal(32, engine.Evaluate("px[1]").AsNumber());
            Assert.Equal(8, engine.Evaluate("px[2]").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeWebGl2SubImageUpdatesThreeDimensionalTextureStorage()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='8' height='8'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("""
                var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false,preserveDrawingBuffer:true});
                function sh(t,s){var x=gl.createShader(t);gl.shaderSource(x,s);gl.compileShader(x);if(!gl.getShaderParameter(x,gl.COMPILE_STATUS))throw Error(gl.getShaderInfoLog(x));return x;}
                var p=gl.createProgram();gl.attachShader(p,sh(gl.VERTEX_SHADER,'#version 300 es\nvoid main(){gl_Position=vec4(-1.0,-1.0,0.0,1.0);if(gl_VertexID==1)gl_Position=vec4(3.0,-1.0,0.0,1.0);if(gl_VertexID==2)gl_Position=vec4(-1.0,3.0,0.0,1.0);}'));gl.attachShader(p,sh(gl.FRAGMENT_SHADER,'#version 300 es\nprecision highp float;uniform highp sampler3D t;out vec4 c;void main(){c=texelFetch(t,ivec3(0),0);}'));gl.linkProgram(p);gl.useProgram(p);
                var t=gl.createTexture();gl.bindTexture(gl.TEXTURE_3D,t);gl.texImage3D(gl.TEXTURE_3D,0,gl.RGBA,1,1,1,0,gl.RGBA,gl.UNSIGNED_BYTE,new Uint8Array([255,0,0,255]));gl.texSubImage3D(gl.TEXTURE_3D,0,0,0,0,1,1,1,0,gl.RGBA,gl.UNSIGNED_BYTE,new Uint8Array([0,0,255,255]));gl.uniform1i(gl.getUniformLocation(p,'t'),0);gl.viewport(0,0,8,8);gl.drawArrays(gl.TRIANGLES,0,3);var px=new Uint8Array(4);gl.readPixels(4,4,1,1,gl.RGBA,gl.UNSIGNED_BYTE,px);
                """);
            Assert.Equal(0, engine.Evaluate("gl.getError()").AsNumber());
            Assert.Equal(0, engine.Evaluate("px[0]").AsNumber());
            Assert.Equal(255, engine.Evaluate("px[2]").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeWebGl2AttachesTextureArrayLayerToFramebuffer()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='2' height='2'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false,preserveDrawingBuffer:true});var t=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D_ARRAY,t);gl.texImage3D(gl.TEXTURE_2D_ARRAY,0,gl.RGBA,1,1,2,0,gl.RGBA,gl.UNSIGNED_BYTE,new Uint8Array([255,0,0,255,0,0,255,255]));var f=gl.createFramebuffer();gl.bindFramebuffer(gl.FRAMEBUFFER,f);gl.framebufferTextureLayer(gl.FRAMEBUFFER,gl.COLOR_ATTACHMENT0,t,0,1);var status=gl.checkFramebufferStatus(gl.FRAMEBUFFER);gl.viewport(0,0,2,2);gl.clearColor(0,1,0,1);gl.clear(gl.COLOR_BUFFER_BIT);var px=new Uint8Array(4);gl.readPixels(0,0,1,1,gl.RGBA,gl.UNSIGNED_BYTE,px);");
            Assert.Equal(0x8CD5, engine.Evaluate("status").AsNumber());
            Assert.Equal(0, engine.Evaluate("gl.getError()").AsNumber());
            Assert.Equal(0, engine.Evaluate("px[0]").AsNumber());
            Assert.Equal(255, engine.Evaluate("px[1]").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeWebGl2SamplesTwoDimensionalArrayTextures()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='8' height='8'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("""
                var canvas=document.getElementById('c'); var gl=canvas.getContext('webgl2',{antialias:false,depth:false,preserveDrawingBuffer:true});
                function sh(t,s){var x=gl.createShader(t);gl.shaderSource(x,s);gl.compileShader(x);if(!gl.getShaderParameter(x,gl.COMPILE_STATUS))throw Error(gl.getShaderInfoLog(x));return x;}
                var p=gl.createProgram();gl.attachShader(p,sh(gl.VERTEX_SHADER,'#version 300 es\nlayout(location=0) in vec2 a;out vec3 v;void main(){gl_Position=vec4(a,0,1);v=vec3((a+1.0)*0.5,0.5);}'));gl.attachShader(p,sh(gl.FRAGMENT_SHADER,'#version 300 es\nprecision highp float;in vec3 v;uniform highp sampler2DArray tex;out vec4 c;void main(){c=texture(tex,v);}'));gl.linkProgram(p);gl.useProgram(p);
                var t=gl.createTexture();gl.activeTexture(gl.TEXTURE0);gl.bindTexture(gl.TEXTURE_2D_ARRAY,t);gl.texParameteri(gl.TEXTURE_2D_ARRAY,gl.TEXTURE_MIN_FILTER,gl.NEAREST);gl.texParameteri(gl.TEXTURE_2D_ARRAY,gl.TEXTURE_MAG_FILTER,gl.NEAREST);gl.texImage3D(gl.TEXTURE_2D_ARRAY,0,gl.RGBA,1,1,1,0,gl.RGBA,gl.UNSIGNED_BYTE,new Uint8Array([16,128,240,255]));gl.uniform1i(gl.getUniformLocation(p,'tex'),0);
                var b=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,b);gl.bufferData(gl.ARRAY_BUFFER,new Float32Array([-1,-1,1,-1,0,1]),gl.STATIC_DRAW);gl.vertexAttribPointer(0,2,gl.FLOAT,false,0,0);gl.enableVertexAttribArray(0);gl.viewport(0,0,8,8);gl.drawArrays(gl.TRIANGLES,0,3);var px=new Uint8Array(4);gl.readPixels(4,4,1,1,gl.RGBA,gl.UNSIGNED_BYTE,px);
                """);
            Assert.Equal(0, engine.Evaluate("gl.getError()").AsNumber());
            Assert.Equal(16, engine.Evaluate("px[0]").AsNumber());
            Assert.Equal(128, engine.Evaluate("px[1]").AsNumber());
            Assert.Equal(240, engine.Evaluate("px[2]").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeWebGl2SamplesFloatingPointTextures()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='8' height='8'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("""
                var canvas=document.getElementById('c'); var gl=canvas.getContext('webgl2',{antialias:false,depth:false,preserveDrawingBuffer:true});
                function sh(t,s){var x=gl.createShader(t);gl.shaderSource(x,s);gl.compileShader(x);if(!gl.getShaderParameter(x,gl.COMPILE_STATUS))throw Error(gl.getShaderInfoLog(x));return x;}
                var p=gl.createProgram();gl.attachShader(p,sh(gl.VERTEX_SHADER,'#version 300 es\nlayout(location=0) in vec2 a;out vec2 v;void main(){gl_Position=vec4(a,0,1);v=(a+1.0)*0.5;}'));gl.attachShader(p,sh(gl.FRAGMENT_SHADER,'#version 300 es\nprecision highp float;in vec2 v;uniform sampler2D tex;out vec4 c;void main(){c=texture(tex,v);}'));gl.linkProgram(p);gl.useProgram(p);
                var t=gl.createTexture();gl.activeTexture(gl.TEXTURE0);gl.bindTexture(gl.TEXTURE_2D,t);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,gl.NEAREST);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAG_FILTER,gl.NEAREST);gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA32F,1,1,0,gl.RGBA,gl.FLOAT,new Float32Array([1,.25,.75,1]));gl.uniform1i(gl.getUniformLocation(p,'tex'),0);
                var b=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,b);gl.bufferData(gl.ARRAY_BUFFER,new Float32Array([-1,-1,1,-1,0,1]),gl.STATIC_DRAW);gl.vertexAttribPointer(0,2,gl.FLOAT,false,0,0);gl.enableVertexAttribArray(0);gl.viewport(0,0,8,8);gl.drawArrays(gl.TRIANGLES,0,3);var px=new Uint8Array(4);gl.readPixels(4,4,1,1,gl.RGBA,gl.UNSIGNED_BYTE,px);
                """);
            Assert.Equal(0, engine.Evaluate("gl.getError()").AsNumber());
            Assert.Equal(255, engine.Evaluate("px[0]").AsNumber());
            Assert.InRange(engine.Evaluate("px[1]").AsNumber(), 63, 65);
            Assert.InRange(engine.Evaluate("px[2]").AsNumber(), 190, 192);
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeWebGl2SamplesHalfFloatingPointTextures()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='8' height='8'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("""
                var canvas=document.getElementById('c'); var gl=canvas.getContext('webgl2',{antialias:false,depth:false,preserveDrawingBuffer:true});
                function sh(t,s){var x=gl.createShader(t);gl.shaderSource(x,s);gl.compileShader(x);if(!gl.getShaderParameter(x,gl.COMPILE_STATUS))throw Error(gl.getShaderInfoLog(x));return x;}
                var p=gl.createProgram();gl.attachShader(p,sh(gl.VERTEX_SHADER,'#version 300 es\nlayout(location=0) in vec2 a;out vec2 v;void main(){gl_Position=vec4(a,0,1);v=(a+1.0)*0.5;}'));gl.attachShader(p,sh(gl.FRAGMENT_SHADER,'#version 300 es\nprecision highp float;in vec2 v;uniform sampler2D tex;out vec4 c;void main(){c=texture(tex,v);}'));gl.linkProgram(p);gl.useProgram(p);
                var t=gl.createTexture();gl.activeTexture(gl.TEXTURE0);gl.bindTexture(gl.TEXTURE_2D,t);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,gl.NEAREST);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAG_FILTER,gl.NEAREST);gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA16F,1,1,0,gl.RGBA,gl.HALF_FLOAT,new Uint16Array([0x3c00,0x3400,0x3a00,0x3c00]));gl.uniform1i(gl.getUniformLocation(p,'tex'),0);
                var b=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,b);gl.bufferData(gl.ARRAY_BUFFER,new Float32Array([-1,-1,1,-1,0,1]),gl.STATIC_DRAW);gl.vertexAttribPointer(0,2,gl.FLOAT,false,0,0);gl.enableVertexAttribArray(0);gl.viewport(0,0,8,8);gl.drawArrays(gl.TRIANGLES,0,3);var px=new Uint8Array(4);gl.readPixels(4,4,1,1,gl.RGBA,gl.UNSIGNED_BYTE,px);
                """);
            Assert.Equal(0, engine.Evaluate("gl.getError()").AsNumber());
            Assert.Equal(255, engine.Evaluate("px[0]").AsNumber());
            Assert.InRange(engine.Evaluate("px[1]").AsNumber(), 63, 65);
            Assert.InRange(engine.Evaluate("px[2]").AsNumber(), 190, 192);
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeTextureResourcesRetireAcrossCapabilityLossAndRestore()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='2' height='2'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false});var lost=0,restored=0;gl.oncontextlost=function(e){if(e.type==='webglcontextlost')lost++;};gl.oncontextrestored=function(e){if(e.type==='webglcontextrestored')restored++;};var t=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D,t);gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,2,2,0,gl.RGBA,gl.UNSIGNED_BYTE,new Uint8Array(16));var rb=gl.createRenderbuffer();gl.bindRenderbuffer(gl.RENDERBUFFER,rb);gl.renderbufferStorage(gl.RENDERBUFFER,gl.RGBA4,2,2);");
            Assert.Equal(0, engine.Evaluate("gl.getError()").AsNumber());
            Assert.True(engine.Evaluate("gl.isTexture(t) && gl.isRenderbuffer(rb)").AsBoolean());
            Assert.True(DomBindings.UpdateCapabilities(engine, FeatureCapabilities.Default));
            Assert.True(engine.Evaluate("gl.isContextLost()").AsBoolean());
            Assert.Equal(1, engine.Evaluate("lost").AsNumber());
            Assert.False(engine.Evaluate("gl.isTexture(t) || gl.isRenderbuffer(rb)").AsBoolean());
            Assert.True(DomBindings.UpdateCapabilities(engine, new FeatureCapabilities { WebGl = true }));
            Assert.False(engine.Evaluate("gl.isContextLost()").AsBoolean());
            Assert.Equal(1, engine.Evaluate("restored").AsNumber());
            Assert.False(engine.Evaluate("gl.isTexture(t) || gl.isRenderbuffer(rb)").AsBoolean());
            Assert.True(engine.Evaluate("gl.createTexture() !== null").AsBoolean());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeLoseContextExtensionRecreatesContext()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='1' height='1'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false});var loss=gl.getExtension('WEBGL_lose_context');var old=gl.createBuffer();loss.loseContext();");
            Assert.True(engine.Evaluate("gl.isContextLost()").AsBoolean());
            Assert.True(engine.Evaluate("gl.createBuffer() === null").AsBoolean());
            engine.Evaluate("loss.restoreContext();");
            Assert.False(engine.Evaluate("gl.isContextLost()").AsBoolean());
            Assert.False(engine.Evaluate("gl.createBuffer() === null").AsBoolean());
            Assert.False(engine.Evaluate("gl.isBuffer(old)").AsBoolean());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void HostGpuResetLosesAndRestoresGrantedContexts()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='1' height='1'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false});var lost=0,restored=0;gl.oncontextlost=()=>lost++;gl.oncontextrestored=()=>restored++;var old=gl.createBuffer();");
            Assert.True(DomBindings.NotifyGpuDeviceLost(engine));
            Assert.True(engine.Evaluate("gl.isContextLost()").AsBoolean());
            Assert.Equal(1, engine.Evaluate("lost").AsNumber());
            Assert.False(engine.Evaluate("gl.isBuffer(old)").AsBoolean());
            Assert.True(DomBindings.NotifyGpuDeviceRestored(engine));
            Assert.False(engine.Evaluate("gl.isContextLost()").AsBoolean());
            Assert.Equal(1, engine.Evaluate("restored").AsNumber());
            Assert.True(engine.Evaluate("gl.createBuffer() !== null").AsBoolean());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeReadPixelsSupportsFloatOutput()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='1' height='1'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false});gl.clearColor(1,.25,0,1);gl.clear(gl.COLOR_BUFFER_BIT);var p=new Float32Array(4);gl.readPixels(0,0,1,1,gl.RGBA,gl.FLOAT,p);");
            Assert.Equal(0, engine.Evaluate("gl.getError()").AsNumber());
            Assert.Equal(1, engine.Evaluate("p[0]").AsNumber());
            Assert.InRange(engine.Evaluate("p[1]").AsNumber(), .24, .26);
            Assert.Equal(0, engine.Evaluate("p[2]").AsNumber());
            Assert.Equal(1, engine.Evaluate("p[3]").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeTextureQueriesReportTrackedParametersAndLevels()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='2' height='2'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false});var t=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D,t);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,gl.NEAREST);gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,2,1,0,gl.RGBA,gl.UNSIGNED_BYTE,new Uint8Array(8));");
            Assert.Equal(0x2600, engine.Evaluate("gl.getTexParameter(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER)").AsNumber());
            Assert.Equal(2, engine.Evaluate("gl.getTexLevelParameter(gl.TEXTURE_2D,0,gl.TEXTURE_WIDTH)").AsNumber());
            Assert.Equal(1, engine.Evaluate("gl.getTexLevelParameter(gl.TEXTURE_2D,0,gl.TEXTURE_HEIGHT)").AsNumber());
            Assert.Equal(0, engine.Evaluate("gl.getError()").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeDepthTextureCanCompleteAFramebuffer()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='2' height='2'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false});var color=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D,color);gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,2,2,0,gl.RGBA,gl.UNSIGNED_BYTE,new Uint8Array(16));var depth=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D,depth);gl.texImage2D(gl.TEXTURE_2D,0,gl.DEPTH_COMPONENT16,2,2,0,gl.DEPTH_COMPONENT,gl.UNSIGNED_SHORT,new Uint16Array(4));var fb=gl.createFramebuffer();gl.bindFramebuffer(gl.FRAMEBUFFER,fb);gl.framebufferTexture2D(gl.FRAMEBUFFER,gl.COLOR_ATTACHMENT0,gl.TEXTURE_2D,color,0);gl.framebufferTexture2D(gl.FRAMEBUFFER,gl.DEPTH_ATTACHMENT,gl.TEXTURE_2D,depth,0);var status=gl.checkFramebufferStatus(gl.FRAMEBUFFER);");
            Assert.Equal(0x8CD5, engine.Evaluate("status").AsNumber());
            Assert.Equal(0, engine.Evaluate("gl.getError()").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeDepthTextureSupportsShadowComparisonSampling()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='8' height='8'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("""
                var canvas=document.getElementById('c'); var gl=canvas.getContext('webgl2',{antialias:false,depth:false,preserveDrawingBuffer:true});
                function sh(t,s){var x=gl.createShader(t);gl.shaderSource(x,s);gl.compileShader(x);if(!gl.getShaderParameter(x,gl.COMPILE_STATUS))throw Error(gl.getShaderInfoLog(x));return x;}
                var p=gl.createProgram();gl.attachShader(p,sh(gl.VERTEX_SHADER,'#version 300 es\nlayout(location=0) in vec2 a;out vec2 v;void main(){gl_Position=vec4(a,0,1);v=vec2(.5);}'));gl.attachShader(p,sh(gl.FRAGMENT_SHADER,'#version 300 es\nprecision highp float;in vec2 v;uniform highp sampler2DShadow tex;out vec4 c;void main(){float x=texture(tex,vec3(v,.5));c=vec4(x,x,x,1);}'));gl.linkProgram(p);gl.useProgram(p);
                var t=gl.createTexture();gl.activeTexture(gl.TEXTURE0);gl.bindTexture(gl.TEXTURE_2D,t);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,gl.NEAREST);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAG_FILTER,gl.NEAREST);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_COMPARE_MODE,gl.COMPARE_REF_TO_TEXTURE);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_COMPARE_FUNC,gl.LEQUAL);gl.texImage2D(gl.TEXTURE_2D,0,gl.DEPTH_COMPONENT16,1,1,0,gl.DEPTH_COMPONENT,gl.UNSIGNED_SHORT,new Uint16Array([0x8000]));gl.uniform1i(gl.getUniformLocation(p,'tex'),0);
                var b=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,b);gl.bufferData(gl.ARRAY_BUFFER,new Float32Array([-1,-1,1,-1,0,1]),gl.STATIC_DRAW);gl.vertexAttribPointer(0,2,gl.FLOAT,false,0,0);gl.enableVertexAttribArray(0);gl.viewport(0,0,8,8);gl.drawArrays(gl.TRIANGLES,0,3);var px=new Uint8Array(4);gl.readPixels(4,4,1,1,gl.RGBA,gl.UNSIGNED_BYTE,px);
                """);
            Assert.Equal(0, engine.Evaluate("gl.getError()").AsNumber());
            Assert.InRange(engine.Evaluate("px[0]").AsNumber(), 250, 255);
            Assert.InRange(engine.Evaluate("px[1]").AsNumber(), 250, 255);
            Assert.InRange(engine.Evaluate("px[2]").AsNumber(), 250, 255);
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void NativeWebGl2SamplesUnsignedIntegerTextures()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='8' height='8'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("""
                var canvas=document.getElementById('c'); var gl=canvas.getContext('webgl2',{antialias:false,depth:false,preserveDrawingBuffer:true});
                function sh(t,s){var x=gl.createShader(t);gl.shaderSource(x,s);gl.compileShader(x);if(!gl.getShaderParameter(x,gl.COMPILE_STATUS))throw Error(gl.getShaderInfoLog(x));return x;}
                var p=gl.createProgram();gl.attachShader(p,sh(gl.VERTEX_SHADER,'#version 300 es\nlayout(location=0) in vec2 a;out vec2 v;void main(){gl_Position=vec4(a,0,1);v=(a+1.0)*0.5;}'));gl.attachShader(p,sh(gl.FRAGMENT_SHADER,'#version 300 es\nprecision highp float;in vec2 v;uniform highp usampler2D tex;out vec4 c;void main(){uvec4 x=texture(tex,v);c=vec4(x)/255.0;}'));gl.linkProgram(p);gl.useProgram(p);
                var t=gl.createTexture();gl.activeTexture(gl.TEXTURE0);gl.bindTexture(gl.TEXTURE_2D,t);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,gl.NEAREST);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAG_FILTER,gl.NEAREST);gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA8UI,1,1,0,gl.RGBA_INTEGER,gl.UNSIGNED_BYTE,new Uint8Array([16,128,240,255]));gl.uniform1i(gl.getUniformLocation(p,'tex'),0);
                var b=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,b);gl.bufferData(gl.ARRAY_BUFFER,new Float32Array([-1,-1,1,-1,0,1]),gl.STATIC_DRAW);gl.vertexAttribPointer(0,2,gl.FLOAT,false,0,0);gl.enableVertexAttribArray(0);gl.viewport(0,0,8,8);gl.drawArrays(gl.TRIANGLES,0,3);var px=new Uint8Array(4);gl.readPixels(4,4,1,1,gl.RGBA,gl.UNSIGNED_BYTE,px);
                """);
            Assert.Equal(0, engine.Evaluate("gl.getError()").AsNumber());
            Assert.InRange(engine.Evaluate("px[0]").AsNumber(), 15, 17);
            Assert.InRange(engine.Evaluate("px[1]").AsNumber(), 127, 129);
            Assert.InRange(engine.Evaluate("px[2]").AsNumber(), 239, 241);
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Theory]
    [InlineData("webgl", false)]
    [InlineData("webgl", true)]
    [InlineData("webgl2", false)]
    [InlineData("webgl2", true)]
    public void PageContextPreservesIdentityAttributesPixelsAndResize(string kind, bool antialias)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='4' height='4'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate($"var canvas=document.getElementById('c'); var gl=canvas.getContext('{kind}',{{antialias:{(antialias ? "true" : "false")},depth:false,preserveDrawingBuffer:true}});");
            Assert.False(engine.Evaluate("gl === null").AsBoolean());
            Assert.True(engine.Evaluate("gl.canvas === canvas").AsBoolean());
            Assert.True(engine.Evaluate($"canvas.getContext('{kind}') === gl").AsBoolean());
            Assert.True(engine.Evaluate($"canvas.getContext('{kind}', {{get antialias() {{throw Error('options must be ignored');}}}}) === gl").AsBoolean());
            var other = kind == "webgl" ? "webgl2" : "webgl";
            Assert.True(engine.Evaluate($"canvas.getContext('{other}') === null").AsBoolean());
            Assert.True(engine.Evaluate("canvas.getContext('2d') === null").AsBoolean());
            Assert.Equal(antialias, engine.Evaluate("gl.getContextAttributes().antialias").AsBoolean());
            Assert.False(engine.Evaluate("gl.getContextAttributes().depth").AsBoolean());
            Assert.True(engine.Evaluate("gl.getParameter(gl.VERSION).startsWith('WebGL ')").AsBoolean());
            Assert.True(engine.Evaluate("gl.getParameter(gl.MAX_VERTEX_ATTRIBS)>0").AsBoolean());
            Assert.True(engine.Evaluate("gl.getParameter(gl.SCISSOR_TEST)===false").AsBoolean());
            Assert.True(engine.Evaluate("var initialViewport=gl.getParameter(gl.VIEWPORT);initialViewport[2]===4 && initialViewport[3]===4").AsBoolean());
            Assert.True(engine.Evaluate("gl.colorMask(true,false,true,false);var mask=gl.getParameter(gl.COLOR_WRITEMASK);mask.length===4 && mask[0]===true && mask[1]===false;gl.colorMask(true,true,true,true);mask[0]===true && mask[1]===false").AsBoolean());
            Assert.Equal(0, engine.Evaluate("gl.vertexAttrib2f(0,.25,.5);gl.getError()").AsNumber());
            Assert.Equal(0, engine.Evaluate("gl.getVertexAttrib(0,gl.CURRENT_VERTEX_ATTRIB);gl.getError()").AsNumber());
            Assert.Equal("object", engine.Evaluate("typeof gl.getVertexAttrib(0,gl.CURRENT_VERTEX_ATTRIB)").AsString());
            Assert.True(engine.Evaluate("var attribute=gl.getVertexAttrib(0,gl.CURRENT_VERTEX_ATTRIB);attribute[0]===.25 && attribute[1]===.5 && attribute[2]===0 && attribute[3]===1").AsBoolean());
            engine.Evaluate("gl.clearColor(.25,.5,.75,1);gl.scissor(0,0,1,1);gl.clear(gl.COLOR_BUFFER_BIT);");
            engine.Evaluate("gl.uniform1f(null, 1);");
            Assert.Equal(0, engine.Evaluate("gl.getError()").AsNumber());
            var canvasNode = document.Node.Descendants.OfType<ElementNode>().Single(node => node.LocalName == "canvas");
            var state = Assert.IsType<CanvasElementState>(canvasNode.CanvasSurface);
            var image = Assert.IsAssignableFrom<Folio.Imaging.IImageHandle>(state.Snapshot());
            var pixels = image.Pixels.Span;
            for (var offset = 0; offset < pixels.Length; offset += 4)
                Assert.Equal(new byte[] { 64, 128, 191, 255 }, pixels.Slice(offset, 4).ToArray());
            engine.Evaluate("canvas.width=5;");
            Assert.True(engine.Evaluate($"canvas.getContext('{kind}') === gl").AsBoolean());
            Assert.Equal(5, engine.Evaluate("gl.drawingBufferWidth").AsNumber());
            engine.Evaluate("gl.clearColor(0,1,0,1);gl.clear(gl.COLOR_BUFFER_BIT);");
            Assert.Equal(new byte[] { 0, 255, 0, 255 }, state.Snapshot()!.Pixels.Span[..4].ToArray());
            Assert.True(DomBindings.UpdateCapabilities(engine, FeatureCapabilities.Default));
            Assert.True(engine.Evaluate("gl.isContextLost()").AsBoolean());
            Assert.True(engine.Evaluate("gl.getContextAttributes() === null").AsBoolean());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void UnsignedIntegerVertexAttributeConstantReachesTheNativeShader()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='1' height='1'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("""
                var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false});
                var vs=gl.createShader(gl.VERTEX_SHADER);
                gl.shaderSource(vs,'#version 300 es\nlayout(location=0) in uvec4 value; flat out uvec4 v; void main(){v=value;gl_Position=vec4(-1,-1,0,1);if(gl_VertexID==1)gl_Position=vec4(3,-1,0,1);if(gl_VertexID==2)gl_Position=vec4(-1,3,0,1);}');
                gl.compileShader(vs);
                var fs=gl.createShader(gl.FRAGMENT_SHADER);
                gl.shaderSource(fs,'#version 300 es\nprecision highp float; flat in uvec4 v; out vec4 color; void main(){color=vec4(float(v.x)/4294967295.0,0,0,1);}');
                gl.compileShader(fs);
                var p=gl.createProgram();gl.attachShader(p,vs);gl.attachShader(p,fs);gl.linkProgram(p);gl.useProgram(p);
                var vsOk=gl.getShaderParameter(vs,gl.COMPILE_STATUS),fsOk=gl.getShaderParameter(fs,gl.COMPILE_STATUS);var linkOk=gl.getProgramParameter(p,gl.LINK_STATUS);
                gl.viewport(0,0,1,1);gl.disableVertexAttribArray(0);gl.vertexAttribI4ui(0,4294967295,0,0,1);var attribError=gl.getError();gl.drawArrays(gl.TRIANGLES,0,3);
                var pixel=new Uint8Array(4);gl.readPixels(0,0,1,1,gl.RGBA,gl.UNSIGNED_BYTE,pixel);
                """
            );
            Assert.True(engine.Evaluate("vsOk").AsBoolean(), engine.Evaluate("gl.getShaderInfoLog(vs)").AsString());
            Assert.True(engine.Evaluate("fsOk").AsBoolean(), engine.Evaluate("gl.getShaderInfoLog(fs)").AsString());
            Assert.True(engine.Evaluate("linkOk").AsBoolean(), engine.Evaluate("gl.getProgramInfoLog(p)").AsString());
            Assert.Equal(0, engine.Evaluate("attribError").AsNumber());
            Assert.Equal(0, engine.Evaluate("gl.getError()").AsNumber());
            Assert.Equal(255, engine.Evaluate("pixel[0]").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void UnsignedIntegerUniformReachesTheNativeShader()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='1' height='1'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("""
                var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false});
                function makeShader(kind,source){var s=gl.createShader(kind);gl.shaderSource(s,source);gl.compileShader(s);return s;}
                var vs=makeShader(gl.VERTEX_SHADER,'#version 300 es\nvoid main(){gl_Position=vec4(-1,-1,0,1);if(gl_VertexID==1)gl_Position=vec4(3,-1,0,1);if(gl_VertexID==2)gl_Position=vec4(-1,3,0,1);}');
                var fs=makeShader(gl.FRAGMENT_SHADER,'#version 300 es\nprecision highp float;uniform highp uint value;out vec4 color;void main(){color=vec4(float(value)/4294967295.0,0,0,1);}');
                var p=gl.createProgram();gl.attachShader(p,vs);gl.attachShader(p,fs);gl.linkProgram(p);gl.useProgram(p);
                var location=gl.getUniformLocation(p,'value');gl.uniform1ui(location,4294967295);var uniformError=gl.getError();
                gl.viewport(0,0,1,1);gl.drawArrays(gl.TRIANGLES,0,3);var pixel=new Uint8Array(4);gl.readPixels(0,0,1,1,gl.RGBA,gl.UNSIGNED_BYTE,pixel);
                """);
            Assert.True(engine.Evaluate("gl.getShaderParameter(vs,gl.COMPILE_STATUS)").AsBoolean(), engine.Evaluate("gl.getShaderInfoLog(vs)").AsString());
            Assert.True(engine.Evaluate("gl.getShaderParameter(fs,gl.COMPILE_STATUS)").AsBoolean(), engine.Evaluate("gl.getShaderInfoLog(fs)").AsString());
            Assert.True(engine.Evaluate("gl.getProgramParameter(p,gl.LINK_STATUS)").AsBoolean(), engine.Evaluate("gl.getProgramInfoLog(p)").AsString());
            Assert.Equal(0, engine.Evaluate("uniformError").AsNumber());
            Assert.Equal(0, engine.Evaluate("gl.getError()").AsNumber());
            Assert.Equal(255, engine.Evaluate("pixel[0]").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void ThreeDimensionalUnsignedIntegerTextureCanBeSampled()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='1' height='1'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("""
                var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false});
                function makeShader(kind,source){var s=gl.createShader(kind);gl.shaderSource(s,source);gl.compileShader(s);return s;}
                var vs=makeShader(gl.VERTEX_SHADER,'#version 300 es\nvoid main(){gl_Position=vec4(-1,-1,0,1);if(gl_VertexID==1)gl_Position=vec4(3,-1,0,1);if(gl_VertexID==2)gl_Position=vec4(-1,3,0,1);}');
                var fs=makeShader(gl.FRAGMENT_SHADER,'#version 300 es\nprecision highp float;uniform highp usampler3D tex;out vec4 color;void main(){color=vec4(texture(tex,vec3(.5)))/255.0;}');
                var p=gl.createProgram();gl.attachShader(p,vs);gl.attachShader(p,fs);gl.linkProgram(p);gl.useProgram(p);
                var t=gl.createTexture();gl.activeTexture(gl.TEXTURE0);gl.bindTexture(gl.TEXTURE_3D,t);gl.texParameteri(gl.TEXTURE_3D,gl.TEXTURE_MIN_FILTER,gl.NEAREST);gl.texParameteri(gl.TEXTURE_3D,gl.TEXTURE_MAG_FILTER,gl.NEAREST);
                gl.texImage3D(gl.TEXTURE_3D,0,gl.RGBA8UI,1,1,1,0,gl.RGBA_INTEGER,gl.UNSIGNED_BYTE,new Uint8Array([16,128,240,255]));gl.uniform1i(gl.getUniformLocation(p,'tex'),0);gl.viewport(0,0,1,1);gl.drawArrays(gl.TRIANGLES,0,3);var pixel=new Uint8Array(4);gl.readPixels(0,0,1,1,gl.RGBA,gl.UNSIGNED_BYTE,pixel);
                """);
            Assert.True(engine.Evaluate("gl.getShaderParameter(vs,gl.COMPILE_STATUS)").AsBoolean(), engine.Evaluate("gl.getShaderInfoLog(vs)").AsString());
            Assert.True(engine.Evaluate("gl.getShaderParameter(fs,gl.COMPILE_STATUS)").AsBoolean(), engine.Evaluate("gl.getShaderInfoLog(fs)").AsString());
            Assert.True(engine.Evaluate("gl.getProgramParameter(p,gl.LINK_STATUS)").AsBoolean(), engine.Evaluate("gl.getProgramInfoLog(p)").AsString());
            Assert.Equal(0, engine.Evaluate("gl.getError()").AsNumber());
            Assert.Equal(16, engine.Evaluate("pixel[0]").AsNumber());
            Assert.Equal(128, engine.Evaluate("pixel[1]").AsNumber());
            Assert.Equal(240, engine.Evaluate("pixel[2]").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void UniformBufferObjectFeedsTheNativeShader()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='1' height='1'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("""
                var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false});
                function makeShader(kind,source){var s=gl.createShader(kind);gl.shaderSource(s,source);gl.compileShader(s);return s;}
                var vs=makeShader(gl.VERTEX_SHADER,'#version 300 es\nvoid main(){gl_Position=vec4(-1,-1,0,1);if(gl_VertexID==1)gl_Position=vec4(3,-1,0,1);if(gl_VertexID==2)gl_Position=vec4(-1,3,0,1);}');
                var fs=makeShader(gl.FRAGMENT_SHADER,'#version 300 es\nprecision highp float;layout(std140) uniform Params { vec4 color; };out vec4 outColor;void main(){outColor=color;}');
                var p=gl.createProgram();gl.attachShader(p,vs);gl.attachShader(p,fs);gl.linkProgram(p);gl.useProgram(p);
                var block=gl.getUniformBlockIndex(p,'Params');var eBlock=gl.getError();var indices=gl.getUniformIndices(p,['color']);var eIndices=gl.getError();var offsets=gl.getActiveUniforms(p,indices,gl.UNIFORM_OFFSET);var eOffsets=gl.getError();var blockIndices=gl.getActiveUniforms(p,indices,gl.UNIFORM_BLOCK_INDEX);var eBlockIndices=gl.getError();var types=gl.getActiveUniforms(p,indices,gl.UNIFORM_TYPE);var eTypes=gl.getError();var b=gl.createBuffer();gl.bindBuffer(gl.UNIFORM_BUFFER,b);gl.bufferData(gl.UNIFORM_BUFFER,new Float32Array([.2,.4,.6,1]),gl.STATIC_DRAW);gl.bindBufferBase(gl.UNIFORM_BUFFER,0,b);gl.uniformBlockBinding(p,block,0);var bindError=gl.getError();
                gl.viewport(0,0,1,1);gl.drawArrays(gl.TRIANGLES,0,3);var pixel=new Uint8Array(4);gl.readPixels(0,0,1,1,gl.RGBA,gl.UNSIGNED_BYTE,pixel);gl.bufferSubData(gl.UNIFORM_BUFFER,0,new Float32Array([.8,.1,.3,1]));var updateError=gl.getError();gl.drawArrays(gl.TRIANGLES,0,3);var updatedPixel=new Uint8Array(4);gl.readPixels(0,0,1,1,gl.RGBA,gl.UNSIGNED_BYTE,updatedPixel);
                """);
            Assert.True(engine.Evaluate("gl.getShaderParameter(fs,gl.COMPILE_STATUS)").AsBoolean(), engine.Evaluate("gl.getShaderInfoLog(fs)").AsString());
            Assert.True(engine.Evaluate("gl.getProgramParameter(p,gl.LINK_STATUS)").AsBoolean(), engine.Evaluate("gl.getProgramInfoLog(p)").AsString());
            Assert.NotEqual(uint.MaxValue, (uint)engine.Evaluate("block").AsNumber());
            Assert.Equal(0, engine.Evaluate("bindError").AsNumber());
            Assert.Equal(0, engine.Evaluate("eBlock").AsNumber());
            Assert.Equal(0, engine.Evaluate("eIndices").AsNumber());
            Assert.Equal(0, engine.Evaluate("eOffsets").AsNumber());
            Assert.Equal(0, engine.Evaluate("eBlockIndices").AsNumber());
            Assert.Equal(0, engine.Evaluate("eTypes").AsNumber());
            Assert.Equal(0, engine.Evaluate("block").AsNumber());
            Assert.Equal(0, engine.Evaluate("indices[0]").AsNumber());
            Assert.Equal(0, engine.Evaluate("offsets[0]").AsNumber());
            Assert.Equal(0, engine.Evaluate("blockIndices[0]").AsNumber());
            Assert.Equal(35666, engine.Evaluate("types[0]").AsNumber());
            Assert.Equal(51, engine.Evaluate("pixel[0]").AsNumber());
            Assert.Equal(102, engine.Evaluate("pixel[1]").AsNumber());
            Assert.Equal(153, engine.Evaluate("pixel[2]").AsNumber());
            Assert.Equal(0, engine.Evaluate("updateError").AsNumber());
            Assert.Equal(204, engine.Evaluate("updatedPixel[0]").AsNumber());
            Assert.Equal(26, engine.Evaluate("updatedPixel[1]").AsNumber());
            Assert.Equal(76, engine.Evaluate("updatedPixel[2]").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void TransformFeedbackCapturesVaryingAndFeedsTheNextDraw()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='1' height='1'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("""
                var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false});
                function makeShader(kind,source){var s=gl.createShader(kind);gl.shaderSource(s,source);gl.compileShader(s);return s;}
                var vs=makeShader(gl.VERTEX_SHADER,'#version 300 es\nout float value;void main(){value=float(gl_VertexID+1);gl_Position=vec4(-1,-1,0,1);}');
                var fs=makeShader(gl.FRAGMENT_SHADER,'#version 300 es\nprecision highp float;out vec4 color;void main(){color=vec4(1);}');
                var p=gl.createProgram();gl.attachShader(p,vs);gl.attachShader(p,fs);gl.transformFeedbackVaryings(p,['value'],gl.SEPARATE_ATTRIBS);gl.linkProgram(p);
                var b=gl.createBuffer();gl.bindBuffer(gl.TRANSFORM_FEEDBACK_BUFFER,b);gl.bufferData(gl.TRANSFORM_FEEDBACK_BUFFER,new Float32Array([0,0,0]),gl.DYNAMIC_DRAW);
                var tf=gl.createTransformFeedback();gl.bindTransformFeedback(gl.TRANSFORM_FEEDBACK,tf);gl.bindBufferBase(gl.TRANSFORM_FEEDBACK_BUFFER,0,b);gl.useProgram(p);gl.beginTransformFeedback(gl.POINTS);gl.drawArrays(gl.POINTS,0,3);gl.endTransformFeedback();
                var outData=new Float32Array(3);gl.bindBuffer(gl.TRANSFORM_FEEDBACK_BUFFER,b);gl.getBufferSubData(gl.TRANSFORM_FEEDBACK_BUFFER,0,outData);var afterRead=gl.getError();gl.bindBufferBase(gl.TRANSFORM_FEEDBACK_BUFFER,0,null);
                var nextVs=makeShader(gl.VERTEX_SHADER,'#version 300 es\nlayout(location=0) in float inputValue;flat out float value;void main(){value=inputValue;gl_Position=vec4(-1,-1,0,1);if(gl_VertexID==1)gl_Position=vec4(3,-1,0,1);if(gl_VertexID==2)gl_Position=vec4(-1,3,0,1);}');
                var nextFs=makeShader(gl.FRAGMENT_SHADER,'#version 300 es\nprecision highp float;flat in float value;out vec4 color;void main(){color=vec4(value/3.0,0,0,1);}');
                var next=gl.createProgram();gl.attachShader(next,nextVs);gl.attachShader(next,nextFs);gl.linkProgram(next);var afterLink=gl.getError();gl.useProgram(next);var afterUse=gl.getError();gl.bindBuffer(gl.ARRAY_BUFFER,b);var afterBind=gl.getError();gl.vertexAttribPointer(0,1,gl.FLOAT,false,4,0);var afterPointer=gl.getError();gl.enableVertexAttribArray(0);var afterEnable=gl.getError();gl.drawArrays(gl.TRIANGLES,0,3);var afterDraw=gl.getError();
                var pixel=new Uint8Array(4);gl.readPixels(0,0,1,1,gl.RGBA,gl.UNSIGNED_BYTE,pixel);var error=gl.getError();
                """);
            Assert.True(engine.Evaluate("gl.getShaderParameter(vs,gl.COMPILE_STATUS)").AsBoolean(), engine.Evaluate("gl.getShaderInfoLog(vs)").AsString());
            Assert.True(engine.Evaluate("gl.getShaderParameter(fs,gl.COMPILE_STATUS)").AsBoolean(), engine.Evaluate("gl.getShaderInfoLog(fs)").AsString());
            Assert.True(engine.Evaluate("gl.getProgramParameter(p,gl.LINK_STATUS)").AsBoolean(), engine.Evaluate("gl.getProgramInfoLog(p)").AsString());
            Assert.Equal(0, engine.Evaluate("error").AsNumber());
            Assert.Equal(0, engine.Evaluate("afterRead").AsNumber());
            Assert.Equal(0, engine.Evaluate("afterLink").AsNumber());
            Assert.Equal(0, engine.Evaluate("afterUse").AsNumber());
            Assert.Equal(0, engine.Evaluate("afterBind").AsNumber());
            Assert.Equal(0, engine.Evaluate("afterPointer").AsNumber());
            Assert.Equal(0, engine.Evaluate("afterEnable").AsNumber());
            Assert.Equal(0, engine.Evaluate("afterDraw").AsNumber());
            Assert.Equal(1, engine.Evaluate("outData[0]").AsNumber());
            Assert.Equal(2, engine.Evaluate("outData[1]").AsNumber());
            Assert.Equal(3, engine.Evaluate("outData[2]").AsNumber());
            Assert.Equal("value", engine.Evaluate("gl.getTransformFeedbackVarying(p,0).name").AsString());
            Assert.True(engine.Evaluate("gl.getProgramParameter(next,gl.LINK_STATUS)").AsBoolean(), engine.Evaluate("gl.getProgramInfoLog(next)").AsString());
            Assert.Equal(255, engine.Evaluate("pixel[0]").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void IntegerVertexAttributeFeedsUnsignedShaderInput()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='1' height='1'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("""
                var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false});
                function makeShader(kind,source){var s=gl.createShader(kind);gl.shaderSource(s,source);gl.compileShader(s);return s;}
                var vs=makeShader(gl.VERTEX_SHADER,'#version 300 es\nlayout(location=0) in uint inputValue;flat out uint value;void main(){value=inputValue;gl_Position=vec4(-1,-1,0,1);if(gl_VertexID==1)gl_Position=vec4(3,-1,0,1);if(gl_VertexID==2)gl_Position=vec4(-1,3,0,1);}');
                var fs=makeShader(gl.FRAGMENT_SHADER,'#version 300 es\nprecision highp float;flat in uint value;out vec4 color;void main(){color=vec4(float(value)/4294967295.0,0,0,1);}');
                var p=gl.createProgram();gl.attachShader(p,vs);gl.attachShader(p,fs);gl.linkProgram(p);gl.useProgram(p);
                var b=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,b);gl.bufferData(gl.ARRAY_BUFFER,new Uint32Array([4294967295,4294967295,4294967295]),gl.STATIC_DRAW);gl.vertexAttribIPointer(0,1,gl.UNSIGNED_INT,4,0);gl.enableVertexAttribArray(0);gl.drawArrays(gl.TRIANGLES,0,3);
                var pixel=new Uint8Array(4);gl.readPixels(0,0,1,1,gl.RGBA,gl.UNSIGNED_BYTE,pixel);var error=gl.getError();
                """);
            Assert.True(engine.Evaluate("gl.getShaderParameter(vs,gl.COMPILE_STATUS)").AsBoolean(), engine.Evaluate("gl.getShaderInfoLog(vs)").AsString());
            Assert.True(engine.Evaluate("gl.getShaderParameter(fs,gl.COMPILE_STATUS)").AsBoolean(), engine.Evaluate("gl.getShaderInfoLog(fs)").AsString());
            Assert.True(engine.Evaluate("gl.getProgramParameter(p,gl.LINK_STATUS)").AsBoolean(), engine.Evaluate("gl.getProgramInfoLog(p)").AsString());
            Assert.Equal(0, engine.Evaluate("error").AsNumber());
            Assert.Equal(255, engine.Evaluate("pixel[0]").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void IntegerVertexAttributePreservesSignedShaderInput()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='1' height='1'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("""
                var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false});
                function makeShader(kind,source){var s=gl.createShader(kind);gl.shaderSource(s,source);gl.compileShader(s);return s;}
                var vs=makeShader(gl.VERTEX_SHADER,'#version 300 es\nlayout(location=0) in ivec4 inputValue;flat out int value;void main(){value=inputValue.x;gl_Position=vec4(-1,-1,0,1);if(gl_VertexID==1)gl_Position=vec4(3,-1,0,1);if(gl_VertexID==2)gl_Position=vec4(-1,3,0,1);}');
                var fs=makeShader(gl.FRAGMENT_SHADER,'#version 300 es\nprecision highp float;flat in int value;out vec4 color;void main(){color=vec4(value==-2147483648?1.0:0.0,0,0,1);}');
                var p=gl.createProgram();gl.attachShader(p,vs);gl.attachShader(p,fs);gl.linkProgram(p);gl.useProgram(p);
                var b=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,b);gl.bufferData(gl.ARRAY_BUFFER,new Int32Array([-2147483648,-2147483648,-2147483648]),gl.STATIC_DRAW);gl.vertexAttribIPointer(0,1,gl.INT,4,0);gl.enableVertexAttribArray(0);gl.drawArrays(gl.TRIANGLES,0,3);
                var pixel=new Uint8Array(4);gl.readPixels(0,0,1,1,gl.RGBA,gl.UNSIGNED_BYTE,pixel);var error=gl.getError();
                """);
            Assert.True(engine.Evaluate("gl.getShaderParameter(vs,gl.COMPILE_STATUS)").AsBoolean(), engine.Evaluate("gl.getShaderInfoLog(vs)").AsString());
            Assert.True(engine.Evaluate("gl.getShaderParameter(fs,gl.COMPILE_STATUS)").AsBoolean(), engine.Evaluate("gl.getShaderInfoLog(fs)").AsString());
            Assert.True(engine.Evaluate("gl.getProgramParameter(p,gl.LINK_STATUS)").AsBoolean(), engine.Evaluate("gl.getProgramInfoLog(p)").AsString());
            Assert.Equal(0, engine.Evaluate("error").AsNumber());
            Assert.Equal(255, engine.Evaluate("pixel[0]").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void SignedIntegerTextureFeedsIsamplerShader()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var engine = new JintScriptEngine();
        ConfigureWarp(engine);
        using var document = Document.Parse("<canvas id='c' width='1' height='1'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }, Raster = new SkiaRasterBackend(),
        });
        DomBindings.Install(engine, document);
        try
        {
            engine.Evaluate("""
                var gl=document.getElementById('c').getContext('webgl2',{antialias:false,depth:false});
                function makeShader(kind,source){var s=gl.createShader(kind);gl.shaderSource(s,source);gl.compileShader(s);return s;}
                var vs=makeShader(gl.VERTEX_SHADER,'#version 300 es\nvoid main(){gl_Position=vec4(-1,-1,0,1);if(gl_VertexID==1)gl_Position=vec4(3,-1,0,1);if(gl_VertexID==2)gl_Position=vec4(-1,3,0,1);}');
                var fs=makeShader(gl.FRAGMENT_SHADER,'#version 300 es\nprecision highp float;uniform highp isampler2D tex;out vec4 color;void main(){ivec4 x=texelFetch(tex,ivec2(0),0);color=vec4(x.r==-16?1.0:0.0,x.g==-128?1.0:0.0,0,1);}');
                var p=gl.createProgram();gl.attachShader(p,vs);gl.attachShader(p,fs);gl.linkProgram(p);gl.useProgram(p);
                var t=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D,t);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,gl.NEAREST);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAG_FILTER,gl.NEAREST);gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA8I,1,1,0,gl.RGBA_INTEGER,gl.BYTE,new Int8Array([-16,-128,7,127]));gl.uniform1i(gl.getUniformLocation(p,'tex'),0);gl.drawArrays(gl.TRIANGLES,0,3);
                var pixel=new Uint8Array(4);gl.readPixels(0,0,1,1,gl.RGBA,gl.UNSIGNED_BYTE,pixel);var error=gl.getError();
                """);
            Assert.True(engine.Evaluate("gl.getShaderParameter(vs,gl.COMPILE_STATUS)").AsBoolean(), engine.Evaluate("gl.getShaderInfoLog(vs)").AsString());
            Assert.True(engine.Evaluate("gl.getShaderParameter(fs,gl.COMPILE_STATUS)").AsBoolean(), engine.Evaluate("gl.getShaderInfoLog(fs)").AsString());
            Assert.True(engine.Evaluate("gl.getProgramParameter(p,gl.LINK_STATUS)").AsBoolean(), engine.Evaluate("gl.getProgramInfoLog(p)").AsString());
            Assert.Equal(0, engine.Evaluate("error").AsNumber());
            Assert.Equal(255, engine.Evaluate("pixel[0]").AsNumber());
            Assert.Equal(255, engine.Evaluate("pixel[1]").AsNumber());
        }
        finally { DomBindings.Dispose(engine); }
    }

    private static void ConfigureWarp(JintScriptEngine engine)
    {
        var rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64", Architecture.Arm64 => "win-arm64", Architecture.X86 => "win-x86",
            _ => throw new PlatformNotSupportedException(),
        };
        var path = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", "av_libglesv2.dll");
        if (!File.Exists(path)) path = Path.Combine(AppContext.BaseDirectory, "av_libglesv2.dll");
        Assert.True(DomBindings.ConfigureGpuFactory(engine, bytes => AngleGpuBackend.TryCreate(path, bytes, GpuRendererPreference.WarpOnly)));
    }
}
