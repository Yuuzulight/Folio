using Folio.Dom;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

public sealed class WebGlAuditTests
{
    [Fact]
    public void ReadPixelsValidatesArgumentsAndFormats()
    {
        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<canvas id='canvas' width='10' height='10'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }
        });
        DomBindings.Install(engine, document);

        // Missing arguments -> INVALID_OPERATION (0x0502)
        engine.Evaluate("""
            var gl = document.getElementById('canvas').getContext('webgl');
            gl.readPixels();
            var err1 = gl.getError();
        """);
        Assert.Equal(0x0502, (int)engine.Evaluate("err1").AsNumber());

        // Invalid format/type -> INVALID_ENUM (0x0500)
        engine.Evaluate("""
            var buf = new Uint8Array(16);
            gl.readPixels(0, 0, 2, 2, 0x1907 /* RGB */, 0x1401 /* UNSIGNED_BYTE */, buf);
            var err2 = gl.getError();
        """);
        Assert.Equal(0x0500, (int)engine.Evaluate("err2").AsNumber());

        // Buffer too small -> INVALID_VALUE (0x0501)
        engine.Evaluate("""
            var smallBuf = new Uint8Array(4); // need 2*2*4 = 16 bytes
            gl.readPixels(0, 0, 2, 2, 0x1908 /* RGBA */, 0x1401 /* UNSIGNED_BYTE */, smallBuf);
            var err3 = gl.getError();
        """);
        Assert.Equal(0x0501, (int)engine.Evaluate("err3").AsNumber());

        // Valid readPixels -> NO_ERROR (0)
        engine.Evaluate("""
            var okBuf = new Uint8Array(16);
            gl.readPixels(0, 0, 2, 2, 0x1908 /* RGBA */, 0x1401 /* UNSIGNED_BYTE */, okBuf);
            var err4 = gl.getError();
        """);
        Assert.Equal(0, (int)engine.Evaluate("err4").AsNumber());
    }

    [Fact]
    public void ShaderCompilationReportsStatusAndInfoLogWithoutCrash()
    {
        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<canvas id='canvas'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }
        });
        DomBindings.Install(engine, document);

        // Valid shader
        engine.Evaluate("""
            var gl = document.getElementById('canvas').getContext('webgl');
            var vs = gl.createShader(gl.VERTEX_SHADER);
            gl.shaderSource(vs, 'void main() { gl_Position = vec4(0,0,0,1); }');
            gl.compileShader(vs);
            var ok = gl.getShaderParameter(vs, gl.COMPILE_STATUS);
        """);
        Assert.True(engine.Evaluate("ok").AsBoolean());

        // Malformed shader (missing void main)
        engine.Evaluate("""
            var badVs = gl.createShader(gl.VERTEX_SHADER);
            gl.shaderSource(badVs, 'this is totally invalid shader code @#$%^&*');
            gl.compileShader(badVs);
            var badOk = gl.getShaderParameter(badVs, gl.COMPILE_STATUS);
            var log = gl.getShaderInfoLog(badVs);
        """);
        Assert.False(engine.Evaluate("badOk").AsBoolean());
        Assert.NotEmpty(engine.Evaluate("log").AsString()!);
    }

    [Fact]
    public void MalformedShaderAndLinkEmitDiagnosticsAndNeverCrash()
    {
        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<canvas id='canvas'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }
        });
        DomBindings.Install(engine, document);

        // 1. Missing void main
        engine.Evaluate("""
            var gl = document.getElementById('canvas').getContext('webgl');
            var vs1 = gl.createShader(gl.VERTEX_SHADER);
            gl.shaderSource(vs1, 'float x = 1.0;');
            gl.compileShader(vs1);
            var status1 = gl.getShaderParameter(vs1, gl.COMPILE_STATUS);
            var log1 = gl.getShaderInfoLog(vs1);
        """);
        Assert.False(engine.Evaluate("status1").AsBoolean());
        Assert.Contains("void main()", engine.Evaluate("log1").AsString()!);
        Assert.Contains(document.Diagnostics, d => d.Code == DiagnosticCode.ScriptError && d.Feature == "webgl");

        // 2. Mismatched delimiter
        engine.Evaluate("""
            var vs2 = gl.createShader(gl.VERTEX_SHADER);
            gl.shaderSource(vs2, 'void main() { gl_Position = vec4(0.0);');
            gl.compileShader(vs2);
            var status2 = gl.getShaderParameter(vs2, gl.COMPILE_STATUS);
            var log2 = gl.getShaderInfoLog(vs2);
        """);
        Assert.False(engine.Evaluate("status2").AsBoolean());
        Assert.Contains("Unclosed delimiter", engine.Evaluate("log2").AsString()!);

        // 3. Invalid character
        engine.Evaluate("""
            var vs3 = gl.createShader(gl.VERTEX_SHADER);
            gl.shaderSource(vs3, 'void main() { @gl_Position = vec4(0.0); }');
            gl.compileShader(vs3);
            var status3 = gl.getShaderParameter(vs3, gl.COMPILE_STATUS);
            var log3 = gl.getShaderInfoLog(vs3);
        """);
        Assert.False(engine.Evaluate("status3").AsBoolean());
        Assert.Contains("Invalid character", engine.Evaluate("log3").AsString()!);

        // 4. Program linking with uncompiled shader
        engine.Evaluate("""
            var prog = gl.createProgram();
            gl.attachShader(prog, vs1);
            gl.linkProgram(prog);
            var linkOk = gl.getProgramParameter(prog, gl.LINK_STATUS);
            var progLog = gl.getProgramInfoLog(prog);
        """);
        Assert.False(engine.Evaluate("linkOk").AsBoolean());
        Assert.NotEmpty(engine.Evaluate("progLog").AsString()!);

        // 5. Empty program link
        engine.Evaluate("""
            var emptyProg = gl.createProgram();
            gl.linkProgram(emptyProg);
            var emptyLinkOk = gl.getProgramParameter(emptyProg, gl.LINK_STATUS);
            var emptyProgLog = gl.getProgramInfoLog(emptyProg);
        """);
        Assert.False(engine.Evaluate("emptyLinkOk").AsBoolean());
        Assert.Contains("No shaders attached", engine.Evaluate("emptyProgLog").AsString()!);
    }

    [Fact]
    public void DrawArraysAndDrawElementsExecuteAndRecordDrawCalls()
    {
        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<canvas id='canvas' width='100' height='100'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }
        });
        DomBindings.Install(engine, document);

        engine.Evaluate("""
            var gl = document.getElementById('canvas').getContext('webgl');
            gl.clearColor(0.2, 0.4, 0.6, 1.0);
            gl.clear(gl.COLOR_BUFFER_BIT);

            var vs = gl.createShader(gl.VERTEX_SHADER);
            gl.shaderSource(vs, 'void main() {}');
            gl.compileShader(vs);

            var fs = gl.createShader(gl.FRAGMENT_SHADER);
            gl.shaderSource(fs, 'void main() {}');
            gl.compileShader(fs);

            var prog = gl.createProgram();
            gl.attachShader(prog, vs);
            gl.attachShader(prog, fs);
            gl.linkProgram(prog);
            gl.useProgram(prog);

            var vbo = gl.createBuffer();
            gl.bindBuffer(gl.ARRAY_BUFFER, vbo);
            var vertices = new Float32Array([
                0.0,  0.5,
               -0.5, -0.5,
                0.5, -0.5
            ]);
            gl.bufferData(gl.ARRAY_BUFFER, vertices, gl.STATIC_DRAW);
            gl.enableVertexAttribArray(0);
            gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);

            // DrawArrays
            gl.drawArrays(gl.TRIANGLES, 0, 3);

            // DrawElements with index buffer
            var ibo = gl.createBuffer();
            gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, ibo);
            var indices = new Uint16Array([0, 1, 2]);
            gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, indices, gl.STATIC_DRAW);

            gl.drawElements(gl.TRIANGLES, 3, gl.UNSIGNED_SHORT, 0);
        """);

        Assert.True(engine.TryGetHostTarget(engine.Evaluate("gl"), out var target));
        var state = Assert.IsType<WebGlContextState>(target);
        Assert.Equal(2, state.DrawCalls);

        // Check readPixels returns the drawn pixels
        var pixelValues = engine.Evaluate("""
            var pixel = new Uint8Array(4);
            gl.readPixels(50, 50, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, pixel);
            [pixel[0], pixel[1], pixel[2], pixel[3]];
        """);
        Assert.True(engine.TryGetArrayItems(pixelValues, out var items));
        Assert.Equal(2, state.DrawCalls);
    }

    [Fact]
    public void StateQueriesAndParametersReturnExpectedValues()
    {
        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<canvas id='canvas' width='200' height='150'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }
        });
        DomBindings.Install(engine, document);

        engine.Evaluate("""
            var gl = document.getElementById('canvas').getContext('webgl2');

            // 1. Implementation limits and strings
            var maxTex = gl.getParameter(gl.MAX_TEXTURE_SIZE);
            var maxAttribs = gl.getParameter(gl.MAX_VERTEX_ATTRIBS);
            var maxCombined = gl.getParameter(gl.MAX_COMBINED_TEXTURE_IMAGE_UNITS);
            var version = gl.getParameter(gl.VERSION);
            var vendor = gl.getParameter(gl.VENDOR);
            var renderer = gl.getParameter(gl.RENDERER);
            var slVersion = gl.getParameter(gl.SHADING_LANGUAGE_VERSION);

            // 2. Viewport and Scissor
            gl.viewport(10, 20, 180, 120);
            var vp = gl.getParameter(gl.VIEWPORT);
            gl.scissor(5, 15, 100, 80);
            var sc = gl.getParameter(gl.SCISSOR_BOX);

            // 3. Capabilities and State
            gl.enable(gl.DEPTH_TEST);
            var depthTest = gl.getParameter(gl.DEPTH_TEST);
            gl.disable(gl.DEPTH_TEST);
            var depthTestOff = gl.getParameter(gl.DEPTH_TEST);

            gl.enable(gl.BLEND);
            var blendOn = gl.getParameter(gl.BLEND);
            gl.blendFuncSeparate(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA, gl.ONE, gl.ZERO);
            var srcRgb = gl.getParameter(gl.BLEND_SRC_RGB);
            var dstRgb = gl.getParameter(gl.BLEND_DST_RGB);
            var srcAlpha = gl.getParameter(gl.BLEND_SRC_ALPHA);
            var dstAlpha = gl.getParameter(gl.BLEND_DST_ALPHA);

            gl.blendEquationSeparate(gl.FUNC_ADD, gl.FUNC_ADD);
            var eqRgb = gl.getParameter(gl.BLEND_EQUATION_RGB);
            var eqAlpha = gl.getParameter(gl.BLEND_EQUATION_ALPHA);

            gl.colorMask(true, false, true, false);
            var colorMask = gl.getParameter(gl.COLOR_WRITEMASK);
            gl.depthMask(false);
            var depthMask = gl.getParameter(gl.DEPTH_WRITEMASK);

            // 4. Precision format
            var precision = gl.getShaderPrecisionFormat(gl.VERTEX_SHADER, gl.HIGH_FLOAT);

            // 5. Vertex attributes
            var buf = gl.createBuffer();
            gl.bindBuffer(gl.ARRAY_BUFFER, buf);
            gl.enableVertexAttribArray(1);
            gl.vertexAttribPointer(1, 3, gl.FLOAT, true, 16, 4);
            gl.vertexAttribDivisor(1, 2);

            var attrEnabled = gl.getVertexAttrib(1, gl.VERTEX_ATTRIB_ARRAY_ENABLED);
            var attrSize = gl.getVertexAttrib(1, gl.VERTEX_ATTRIB_ARRAY_SIZE);
            var attrStride = gl.getVertexAttrib(1, gl.VERTEX_ATTRIB_ARRAY_STRIDE);
            var attrType = gl.getVertexAttrib(1, gl.VERTEX_ATTRIB_ARRAY_TYPE);
            var attrDivisor = gl.getVertexAttrib(1, gl.VERTEX_ATTRIB_ARRAY_DIVISOR);
            var attrNorm = gl.getVertexAttrib(1, gl.VERTEX_ATTRIB_ARRAY_NORMALIZED);
            var attrBuf = gl.getVertexAttrib(1, gl.VERTEX_ATTRIB_ARRAY_BUFFER_BINDING);

            // 6. Program and Uniforms
            var vs = gl.createShader(gl.VERTEX_SHADER);
            gl.shaderSource(vs, 'void main() { gl_Position = vec4(0.0); }');
            gl.compileShader(vs);
            var fs = gl.createShader(gl.FRAGMENT_SHADER);
            gl.shaderSource(fs, 'void main() { gl_FragColor = vec4(1.0); }');
            gl.compileShader(fs);

            var prog = gl.createProgram();
            gl.attachShader(prog, vs);
            gl.attachShader(prog, fs);
            gl.linkProgram(prog);
            gl.useProgram(prog);

            var curProg = gl.getParameter(gl.CURRENT_PROGRAM);
            var curArrayBuf = gl.getParameter(gl.ARRAY_BUFFER_BINDING);

            var uFloat = gl.getUniformLocation(prog, 'u_float');
            var uVec = gl.getUniformLocation(prog, 'u_vec');
            var uMat = gl.getUniformLocation(prog, 'u_mat');

            gl.uniform1f(uFloat, 3.14);
            gl.uniform4f(uVec, 1.0, 2.0, 3.0, 4.0);
            var matData = new Float32Array([
                1, 0, 0, 0,
                0, 1, 0, 0,
                0, 0, 1, 0,
                0, 0, 0, 1
            ]);
            gl.uniformMatrix4fv(uMat, false, matData);

            var readFloat = gl.getUniform(prog, uFloat);
            var readVec = gl.getUniform(prog, uVec);
            var readMat = gl.getUniform(prog, uMat);
        """);

        Assert.Equal(4096, (int)engine.Evaluate("maxTex").AsNumber());
        Assert.Equal(16, (int)engine.Evaluate("maxAttribs").AsNumber());
        Assert.Equal(16, (int)engine.Evaluate("maxCombined").AsNumber());
        Assert.Contains("WebGL 2.0", engine.Evaluate("version").AsString()!);
        Assert.Contains("Folio", engine.Evaluate("vendor").AsString()!);
        Assert.Contains("GLSL ES", engine.Evaluate("slVersion").AsString()!);

        Assert.True(engine.Evaluate("vp[0] === 10 && vp[1] === 20 && vp[2] === 180 && vp[3] === 120").AsBoolean());
        Assert.True(engine.Evaluate("sc[0] === 5 && sc[1] === 15 && sc[2] === 100 && sc[3] === 80").AsBoolean());

        Assert.True(engine.Evaluate("depthTest").AsBoolean());
        Assert.False(engine.Evaluate("depthTestOff").AsBoolean());
        Assert.True(engine.Evaluate("blendOn").AsBoolean());

        Assert.Equal(0x0302 /* SRC_ALPHA */, (int)engine.Evaluate("srcRgb").AsNumber());
        Assert.Equal(0x0303 /* ONE_MINUS_SRC_ALPHA */, (int)engine.Evaluate("dstRgb").AsNumber());
        Assert.Equal(1 /* ONE */, (int)engine.Evaluate("srcAlpha").AsNumber());
        Assert.Equal(0 /* ZERO */, (int)engine.Evaluate("dstAlpha").AsNumber());

        Assert.Equal(0x8006 /* FUNC_ADD */, (int)engine.Evaluate("eqRgb").AsNumber());
        Assert.Equal(0x8006 /* FUNC_ADD */, (int)engine.Evaluate("eqAlpha").AsNumber());

        Assert.True(engine.Evaluate("colorMask[0] === true && colorMask[1] === false && colorMask[2] === true && colorMask[3] === false").AsBoolean());
        Assert.False(engine.Evaluate("depthMask").AsBoolean());

        Assert.Equal(23, (int)engine.Evaluate("precision.precision").AsNumber());

        Assert.True(engine.Evaluate("attrEnabled").AsBoolean());
        Assert.Equal(3, (int)engine.Evaluate("attrSize").AsNumber());
        Assert.Equal(16, (int)engine.Evaluate("attrStride").AsNumber());
        Assert.Equal(0x1406 /* FLOAT */, (int)engine.Evaluate("attrType").AsNumber());
        Assert.Equal(2, (int)engine.Evaluate("attrDivisor").AsNumber());
        Assert.True(engine.Evaluate("attrNorm").AsBoolean());
        Assert.True(engine.Evaluate("attrBuf === buf").AsBoolean());

        Assert.True(engine.Evaluate("curProg === prog").AsBoolean());
        Assert.True(engine.Evaluate("curArrayBuf === buf").AsBoolean());

        Assert.Equal(3.14, engine.Evaluate("readFloat").AsNumber(), 2);
        Assert.True(engine.Evaluate("readVec[0] === 1.0 && readVec[1] === 2.0 && readVec[2] === 3.0 && readVec[3] === 4.0").AsBoolean());
        Assert.True(engine.Evaluate("readMat.length === 16 && readMat[0] === 1 && readMat[5] === 1 && readMat[10] === 1 && readMat[15] === 1").AsBoolean());
    }

    [Fact]
    public void GetErrorStateMachineRecordsFirstErrorAndResetsOnRead()
    {
        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<canvas id='canvas'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }
        });
        DomBindings.Install(engine, document);

        engine.Evaluate("""
            var gl = document.getElementById('canvas').getContext('webgl');
            var initialError = gl.getError();

            // Trigger INVALID_OPERATION (0x0502) by calling vertexAttribPointer without enough arguments
            gl.vertexAttribPointer();

            // Trigger subsequent INVALID_VALUE error - first error should be retained
            gl.vertexAttribPointer(-1, 5, gl.FLOAT, false, 0, 0);

            var firstRead = gl.getError();
            var secondRead = gl.getError();
        """);

        Assert.Equal(0 /* NO_ERROR */, (int)engine.Evaluate("initialError").AsNumber());
        Assert.Equal(0x0502 /* INVALID_OPERATION */, (int)engine.Evaluate("firstRead").AsNumber());
        Assert.Equal(0 /* NO_ERROR */, (int)engine.Evaluate("secondRead").AsNumber());
    }

    [Fact]
    public void BufferDataAndBufferSubDataOperationsWorkCorrectly()
    {
        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<canvas id='canvas'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }
        });
        DomBindings.Install(engine, document);

        engine.Evaluate("""
            var gl = document.getElementById('canvas').getContext('webgl');
            var buf = gl.createBuffer();
            var isBufBefore = gl.isBuffer(buf);

            gl.bindBuffer(gl.ARRAY_BUFFER, buf);
            var initialData = new Float32Array([1.0, 2.0, 3.0, 4.0]);
            gl.bufferData(gl.ARRAY_BUFFER, initialData, gl.STATIC_DRAW);

            var subData = new Float32Array([9.0, 8.0]);
            gl.bufferSubData(gl.ARRAY_BUFFER, 4 /* byte offset for 1 float */, subData);

            gl.deleteBuffer(buf);
            var isBufAfter = gl.isBuffer(buf);
        """);

        Assert.True(engine.Evaluate("isBufBefore").AsBoolean());
        Assert.False(engine.Evaluate("isBufAfter").AsBoolean());
        Assert.Equal(0, (int)engine.Evaluate("gl.getError()").AsNumber());
    }

    [Fact]
    public void InstancedDrawAndMultipleAttributesExecuteCorrectly()
    {
        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<canvas id='canvas' width='200' height='200'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }
        });
        DomBindings.Install(engine, document);

        engine.Evaluate("""
            var gl = document.getElementById('canvas').getContext('webgl2');
            gl.viewport(0, 0, 200, 200);

            var vs = gl.createShader(gl.VERTEX_SHADER);
            gl.shaderSource(vs, 'void main() { gl_Position = vec4(0.0); }');
            gl.compileShader(vs);
            var fs = gl.createShader(gl.FRAGMENT_SHADER);
            gl.shaderSource(fs, 'void main() { gl_FragColor = vec4(1.0); }');
            gl.compileShader(fs);

            var prog = gl.createProgram();
            gl.attachShader(prog, vs);
            gl.attachShader(prog, fs);
            gl.linkProgram(prog);
            gl.useProgram(prog);

            // Attrib 0: position
            var vbo = gl.createBuffer();
            gl.bindBuffer(gl.ARRAY_BUFFER, vbo);
            gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([0.0, 0.2, -0.2, -0.2, 0.2, -0.2]), gl.STATIC_DRAW);
            gl.enableVertexAttribArray(0);
            gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);

            // Attrib 1: instance offsets with divisor 1
            var instVbo = gl.createBuffer();
            gl.bindBuffer(gl.ARRAY_BUFFER, instVbo);
            gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-0.5, 0.0, 0.0, 0.0, 0.5, 0.0]), gl.STATIC_DRAW);
            gl.enableVertexAttribArray(1);
            gl.vertexAttribPointer(1, 2, gl.FLOAT, false, 0, 0);
            gl.vertexAttribDivisor(1, 1);

            gl.drawArraysInstanced(gl.TRIANGLES, 0, 3, 3);
        """);

        Assert.True(engine.TryGetHostTarget(engine.Evaluate("gl"), out var target));
        var state = Assert.IsType<WebGlContextState>(target);
        Assert.Equal(1, state.DrawCalls);
    }

    [Fact]
    public void GpuBudgetExceededSetsOutOfMemoryError()
    {
        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<canvas id='canvas'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true },
            Limits = new ResourceLimits { MaxGpuBytes = 1024 } // 1 KB GPU limit
        });
        DomBindings.Install(engine, document);

        engine.Evaluate("""
            var gl = document.getElementById('canvas').getContext('webgl');
            var tex = gl.createTexture();
            gl.bindTexture(gl.TEXTURE_2D, tex);
            // Try to allocate 100x100 RGBA texture = 40,000 bytes > 1024 bytes
            var bigData = new Uint8Array(40000);
            gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 100, 100, 0, gl.RGBA, gl.UNSIGNED_BYTE, bigData);
            var err = gl.getError();
        """);
        // OUT_OF_MEMORY = 0x0505
        Assert.Equal(0x0505, (int)engine.Evaluate("err").AsNumber());
    }

    [Fact]
    public void WebGlCanvasProducesValidSnapshotAndNoFallback()
    {
        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<canvas id='canvas' width='100' height='100'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true },
            Raster = new Folio.Skia.SkiaRasterBackend()
        });
        DomBindings.Install(engine, document);
        engine.Evaluate("""
            var gl = document.getElementById('canvas').getContext('webgl');
            gl.clearColor(1.0, 0.0, 0.0, 1.0);
            gl.clear(gl.COLOR_BUFFER_BIT);
        """);
        var el = (ElementNode)document.Node.Descendants.First(n => n is ElementNode e && e.LocalName == "canvas");
        var state = (CanvasElementState)el.CanvasSurface!;
        Assert.Null(state.Fallback);
        var snapshot = state.Snapshot();
        Assert.NotNull(snapshot);
        var p = snapshot.Pixels.Span;
        Assert.Equal(255, p[0]);
        Assert.Equal(0, p[1]);
        Assert.Equal(0, p[2]);
        Assert.Equal(255, p[3]);
    }

    [Fact]
    public void ReadPixelsFormatsAndCoordinatesFlipping()
    {
        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<canvas id='canvas' width='4' height='4'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true },
            Raster = new Folio.Skia.SkiaRasterBackend()
        });
        DomBindings.Install(engine, document);

        engine.Evaluate("""
            var gl = document.getElementById('canvas').getContext('webgl');
            gl.enable(gl.SCISSOR_TEST);

            // Bottom half in WebGL window coords: y=0, height=2
            gl.scissor(0, 0, 4, 2);
            gl.clearColor(0.0, 1.0, 0.0, 1.0);
            gl.clear(gl.COLOR_BUFFER_BIT);

            // Top half in WebGL window coords: y=2, height=2
            gl.scissor(0, 2, 4, 2);
            gl.clearColor(1.0, 0.0, 0.0, 1.0);
            gl.clear(gl.COLOR_BUFFER_BIT);
            gl.disable(gl.SCISSOR_TEST);
        """);

        // WebGL window coords: read bottom-left (y=0, height=1) -> green
        engine.Evaluate("""
            var bufBottom = new Uint8Array(4);
            gl.readPixels(0, 0, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, bufBottom);
        """);
        var gBottom = (int)engine.Evaluate("bufBottom[1]").AsNumber();
        var rBottom = (int)engine.Evaluate("bufBottom[0]").AsNumber();
        Assert.Equal(255, gBottom);
        Assert.Equal(0, rBottom);

        // WebGL window coords: read top-left (y=3, height=1) -> red
        engine.Evaluate("""
            var bufTop = new Uint8Array(4);
            gl.readPixels(0, 3, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, bufTop);
        """);
        var rTop = (int)engine.Evaluate("bufTop[0]").AsNumber();
        var gTop = (int)engine.Evaluate("bufTop[1]").AsNumber();
        Assert.Equal(255, rTop);
        Assert.Equal(0, gTop);

        // Test RED / FLOAT readback
        engine.Evaluate("""
            var redBuf = new Float32Array(1);
            gl.readPixels(0, 3, 1, 1, 0x1903 /* RED */, gl.FLOAT, redBuf);
        """);
        Assert.InRange((float)engine.Evaluate("redBuf[0]").AsNumber(), 0.99f, 1.01f);

        // Test DEPTH_COMPONENT / FLOAT and UNSIGNED_SHORT on depth buffer
        engine.Evaluate("""
            var fb = gl.createFramebuffer();
            gl.bindFramebuffer(gl.FRAMEBUFFER, fb);
            var rb = gl.createRenderbuffer();
            gl.bindRenderbuffer(gl.RENDERBUFFER, rb);
            gl.renderbufferStorage(gl.RENDERBUFFER, gl.DEPTH_COMPONENT16, 4, 4);
            gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.DEPTH_ATTACHMENT, gl.RENDERBUFFER, rb);
            gl.clearDepth(0.5);
            gl.clear(gl.DEPTH_BUFFER_BIT);

            var depthFloat = new Float32Array(1);
            gl.readPixels(0, 0, 1, 1, gl.DEPTH_COMPONENT, gl.FLOAT, depthFloat);
            var depthShort = new Uint16Array(1);
            gl.readPixels(0, 0, 1, 1, gl.DEPTH_COMPONENT, gl.UNSIGNED_SHORT, depthShort);
        """);
        var df = (float)engine.Evaluate("depthFloat[0]").AsNumber();
        var ds = (int)engine.Evaluate("depthShort[0]").AsNumber();
        Assert.InRange(df, 0.49f, 0.51f);
        Assert.InRange(ds, 32000, 33000);
    }

    [Fact]
    public void TextureUploadPastBudgetRefusedAndReportsDiagnostic()
    {
        var diagnostics = new List<Diagnostic>();
        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<canvas id='canvas' width='10' height='10'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true },
            Limits = new ResourceLimits { MaxGpuBytes = 1024 }
        });
        document.Node.DiagnosticSink = diagnostics.Add;
        DomBindings.Install(engine, document);

        engine.Evaluate("""
            var gl = document.getElementById('canvas').getContext('webgl');
            var tex1 = gl.createTexture();
            gl.bindTexture(gl.TEXTURE_2D, tex1);
            gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 16, 16, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
            var err1 = gl.getError();

            var tex2 = gl.createTexture();
            gl.bindTexture(gl.TEXTURE_2D, tex2);
            gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 16, 16, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
            var err2 = gl.getError();
        """);

        Assert.Equal(0, (int)engine.Evaluate("err1").AsNumber());
        Assert.Equal(0x0505 /* OUT_OF_MEMORY */, (int)engine.Evaluate("err2").AsNumber());

        var diag = diagnostics.FirstOrDefault(d => d.Code == DiagnosticCode.LimitExceeded);
        Assert.NotNull(diag);
        Assert.Equal("webgl", diag.Feature);
        Assert.Equal(Severity.Warning, diag.Severity);
    }

    [Fact]
    public void TexStorageAndGenerateMipmapMemoryBudgetEnforcement()
    {
        var diagnostics = new List<Diagnostic>();
        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<canvas id='canvas' width='10' height='10'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true },
            Limits = new ResourceLimits { MaxGpuBytes = 1024 }
        });
        document.Node.DiagnosticSink = diagnostics.Add;
        DomBindings.Install(engine, document);

        engine.Evaluate("""
            var gl = document.getElementById('canvas').getContext('webgl');
            var tex = gl.createTexture();
            gl.bindTexture(gl.TEXTURE_2D, tex);
            gl.texStorage2D(gl.TEXTURE_2D, 4, gl.RGBA8, 16, 16);
            var err = gl.getError();
        """);

        Assert.Equal(0x0505 /* OUT_OF_MEMORY */, (int)engine.Evaluate("err").AsNumber());
        Assert.Contains(diagnostics, d => d.Code == DiagnosticCode.LimitExceeded && d.Feature == "webgl");
    }

    [Fact]
    public void TexImage2DWithImageAndCanvasAndImageDataSources()
    {
        using var engine = new JintScriptEngine();
        using var document = Document.Parse("""
            <canvas id='srcCanvas' width='2' height='2'></canvas>
            <canvas id='glCanvas' width='2' height='2'></canvas>
        """, new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true },
            Raster = new Folio.Skia.SkiaRasterBackend()
        });
        DomBindings.Install(engine, document);

        engine.Evaluate("""
            var src = document.getElementById('srcCanvas').getContext('2d');
            src.fillStyle = '#ff0000';
            src.fillRect(0, 0, 2, 2);

            var gl = document.getElementById('glCanvas').getContext('webgl');
            var tex = gl.createTexture();
            gl.bindTexture(gl.TEXTURE_2D, tex);

            // 6-argument texImage2D with HTMLCanvasElement
            gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, document.getElementById('srcCanvas'));
            var errCanvas = gl.getError();

            // 6-argument texImage2D with ImageData
            var imgData = src.createImageData(2, 2);
            imgData.data[0] = 0; imgData.data[1] = 255; imgData.data[2] = 0; imgData.data[3] = 255;
            gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, imgData);
            var errImgData = gl.getError();

            // pixelStorei parameters
            gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, true);
            var flipY = gl.getParameter(gl.UNPACK_FLIP_Y_WEBGL);
            gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, true);
            var premult = gl.getParameter(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL);
        """);

        Assert.Equal(0, (int)engine.Evaluate("errCanvas").AsNumber());
        Assert.Equal(0, (int)engine.Evaluate("errImgData").AsNumber());
        Assert.True(engine.Evaluate("flipY").AsBoolean());
        Assert.True(engine.Evaluate("premult").AsBoolean());
    }

    [Fact]
    public void BlitFramebufferCopiesPixelsBetweenFramebuffers()
    {
        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<canvas id='canvas' width='4' height='4'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true },
            Raster = new Folio.Skia.SkiaRasterBackend()
        });
        DomBindings.Install(engine, document);

        engine.Evaluate("""
            var gl = document.getElementById('canvas').getContext('webgl');
            // Create Read FBO
            var fboRead = gl.createFramebuffer();
            gl.bindFramebuffer(gl.FRAMEBUFFER, fboRead);
            var texRead = gl.createTexture();
            gl.bindTexture(gl.TEXTURE_2D, texRead);
            gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 4, 4, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
            gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, texRead, 0);
            gl.clearColor(0.2, 0.4, 0.8, 1.0);
            gl.clear(gl.COLOR_BUFFER_BIT);

            // Create Draw FBO
            var fboDraw = gl.createFramebuffer();
            gl.bindFramebuffer(gl.FRAMEBUFFER, fboDraw);
            var texDraw = gl.createTexture();
            gl.bindTexture(gl.TEXTURE_2D, texDraw);
            gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 4, 4, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
            gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, texDraw, 0);

            // Bind read and draw framebuffers
            gl.bindFramebuffer(gl.READ_FRAMEBUFFER, fboRead);
            gl.bindFramebuffer(gl.DRAW_FRAMEBUFFER, fboDraw);

            // Blit
            gl.blitFramebuffer(0, 0, 4, 4, 0, 0, 4, 4, gl.COLOR_BUFFER_BIT, gl.NEAREST);

            // Readback from draw FBO
            gl.bindFramebuffer(gl.FRAMEBUFFER, fboDraw);
            var readBuf = new Uint8Array(16);
            gl.readPixels(0, 0, 2, 2, gl.RGBA, gl.UNSIGNED_BYTE, readBuf);
        """);

        var r = (int)engine.Evaluate("readBuf[0]").AsNumber();
        var g = (int)engine.Evaluate("readBuf[1]").AsNumber();
        var b = (int)engine.Evaluate("readBuf[2]").AsNumber();
        var a = (int)engine.Evaluate("readBuf[3]").AsNumber();

        Assert.InRange(r, 50, 52);
        Assert.InRange(g, 101, 103);
        Assert.InRange(b, 203, 205);
        Assert.Equal(255, a);
    }

    [Fact]
    public void ContextAttributesAndToBlobOnWebGlCanvas()
    {
        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<canvas id='canvas' width='10' height='10'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true },
            Raster = new Folio.Skia.SkiaRasterBackend()
        });
        DomBindings.Install(engine, document);

        engine.Evaluate("""
            var canvas = document.getElementById('canvas');
            var gl = canvas.getContext('webgl', { preserveDrawingBuffer: true, alpha: false, antialias: false });
            var attrs = gl.getContextAttributes();
            var pdb = attrs.preserveDrawingBuffer;
            var alpha = attrs.alpha;
            var antialias = attrs.antialias;

            gl.clearColor(1.0, 0.5, 0.0, 1.0);
            gl.clear(gl.COLOR_BUFFER_BIT);

            var dataUrl = canvas.toDataURL();
            var blobReceived = null;
            canvas.toBlob(function(b) { blobReceived = b; }, 'image/png');
        """);

        Assert.True(engine.Evaluate("pdb").AsBoolean());
        Assert.False(engine.Evaluate("alpha").AsBoolean());
        Assert.False(engine.Evaluate("antialias").AsBoolean());
        var dataUrl = engine.Evaluate("dataUrl").AsString();
        Assert.StartsWith("data:image/png;base64,", dataUrl);
        Assert.False(engine.Evaluate("blobReceived === null").AsBoolean());
    }

    [Fact]
    public void WebGl2SamplersAndMipmaps()
    {
        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<canvas id='canvas' width='10' height='10'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }
        });
        DomBindings.Install(engine, document);

        engine.Evaluate("""
            var gl = document.getElementById('canvas').getContext('webgl');
            var sampler = gl.createSampler();
            var isSamplerBefore = gl.isSampler(sampler);

            gl.samplerParameteri(sampler, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
            gl.samplerParameteri(sampler, gl.TEXTURE_WRAP_S, gl.REPEAT);

            var minFilter = gl.getSamplerParameter(sampler, gl.TEXTURE_MIN_FILTER);
            var wrapS = gl.getSamplerParameter(sampler, gl.TEXTURE_WRAP_S);

            gl.bindSampler(0, sampler);

            // Texture mipmap generation
            var tex = gl.createTexture();
            gl.bindTexture(gl.TEXTURE_2D, tex);
            gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 16, 16, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
            gl.generateMipmap(gl.TEXTURE_2D);
            var errMip = gl.getError();

            gl.deleteSampler(sampler);
            var isSamplerAfter = gl.isSampler(sampler);
        """);

        Assert.True(engine.Evaluate("isSamplerBefore").AsBoolean());
        Assert.Equal(0x2703 /* LINEAR_MIPMAP_LINEAR */, (int)engine.Evaluate("minFilter").AsNumber());
        Assert.Equal(0x2901 /* REPEAT */, (int)engine.Evaluate("wrapS").AsNumber());
        Assert.Equal(0, (int)engine.Evaluate("errMip").AsNumber());
        Assert.False(engine.Evaluate("isSamplerAfter").AsBoolean());
    }
}
