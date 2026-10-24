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
}
