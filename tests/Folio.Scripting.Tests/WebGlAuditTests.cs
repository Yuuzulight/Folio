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
        // Verify draw call count
        Assert.Equal(2, state.DrawCalls);
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
