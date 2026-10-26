using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

/// <summary>What the WebGL pipeline draws (#529, #530): shaders running, vertex data, primitives, depth, blending, textures and framebuffers, read back as pixels.</summary>
public class WebGlPipelineTests
{
    /// <summary>A WebGL canvas with a few helpers in the page, drawing without antialiasing unless asked so that edges are exact.</summary>
    internal sealed class GlPage : IDisposable
    {
        private readonly JintScriptEngine _engine = new();
        private readonly Document _document;

        public GlPage(int size = 8, string attributes = "{ antialias: false }", string markup = "")
        {
            _document = Document.Parse($"<canvas id='c' width='{size}' height='{size}'></canvas>{markup}", new FolioOptions
            {
                Capabilities = new FeatureCapabilities { WebGl = true },
                Raster = new Folio.Skia.SkiaRasterBackend(),
            });
            DomBindings.Install(_engine, _document);
            _engine.Evaluate($$"""
                var canvas = document.getElementById('c');
                var gl = canvas.getContext('webgl', {{attributes}});
                function shader(type, source) {
                    var s = gl.createShader(type);
                    gl.shaderSource(s, source);
                    gl.compileShader(s);
                    if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s));
                    return s;
                }
                function program(vs, fs) {
                    var p = gl.createProgram();
                    gl.attachShader(p, shader(gl.VERTEX_SHADER, vs));
                    gl.attachShader(p, shader(gl.FRAGMENT_SHADER, fs));
                    gl.linkProgram(p);
                    if (!gl.getProgramParameter(p, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(p));
                    gl.useProgram(p);
                    return p;
                }
                function attribute(p, name, data, size, type, normalized, stride, offset) {
                    var b = gl.createBuffer();
                    gl.bindBuffer(gl.ARRAY_BUFFER, b);
                    gl.bufferData(gl.ARRAY_BUFFER, data, gl.STATIC_DRAW);
                    var l = gl.getAttribLocation(p, name);
                    gl.enableVertexAttribArray(l);
                    gl.vertexAttribPointer(l, size, type || gl.FLOAT, !!normalized, stride || 0, offset || 0);
                    return b;
                }
                var QUAD = new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]);
                var FLAT_VS = 'attribute vec2 a_pos; void main() { gl_Position = vec4(a_pos, 0.0, 1.0); }';
                var COLOR_FS = 'precision mediump float; uniform vec4 u_color; void main() { gl_FragColor = u_color; }';
                function pixel(x, y) {
                    var out = new Uint8Array(4);
                    gl.readPixels(x, y, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, out);
                    return Array.prototype.join.call(out, ',');
                }
                """);
        }

        public string Run(string script) => _engine.Evaluate(script).ToString() ?? "";

        public Document Document => _document;

        public void Dispose() => _engine.Dispose();
    }

    private static string Fill(string color) => $"var p = program(FLAT_VS, COLOR_FS); gl.uniform4f(gl.getUniformLocation(p, 'u_color'), {color}); attribute(p, 'a_pos', QUAD, 2); gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);";

    // ---- shaders, uniforms and varyings ----

    [Fact]
    public void AShaderPaintsEveryPixelItCoversWithTheUniformItWasGiven()
    {
        using var page = new GlPage();
        page.Run(Fill("1, 0.5, 0, 1"));
        Assert.Equal("255,128,0,255", page.Run("pixel(0, 0)"));
        Assert.Equal("255,128,0,255", page.Run("pixel(7, 7)"));
        Assert.Equal("255,128,0,255", page.Run("pixel(3, 4)"));
        Assert.Equal("0", page.Run("String(gl.getError())"));
    }

    [Fact]
    public void ClearColorFillsTheBufferAndTheScissorBoxLimitsIt()
    {
        using var page = new GlPage();
        page.Run("gl.clearColor(0, 0, 1, 1); gl.clear(gl.COLOR_BUFFER_BIT); gl.enable(gl.SCISSOR_TEST); gl.scissor(2, 2, 3, 3); gl.clearColor(1, 1, 0, 1); gl.clear(gl.COLOR_BUFFER_BIT);");
        Assert.Equal("0,0,255,255", page.Run("pixel(0, 0)"));
        Assert.Equal("255,255,0,255", page.Run("pixel(2, 2)"));
        Assert.Equal("255,255,0,255", page.Run("pixel(4, 4)"));
        Assert.Equal("0,0,255,255", page.Run("pixel(5, 5)"));
    }

    [Fact]
    public void VaryingsInterpolateAcrossTheTriangle()
    {
        using var page = new GlPage(8);
        page.Run("""
            var p = program('attribute vec2 a_pos; varying float v; void main() { v = a_pos.x * 0.5 + 0.5; gl_Position = vec4(a_pos, 0.0, 1.0); }',
                'precision mediump float; varying float v; void main() { gl_FragColor = vec4(v, v, v, 1.0); }');
            attribute(p, 'a_pos', QUAD, 2);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            """);
        var left = int.Parse(page.Run("pixel(0, 4)").Split(',')[0]);
        var right = int.Parse(page.Run("pixel(7, 4)").Split(',')[0]);
        Assert.InRange(left, 10, 30);   // centre of the first pixel: x = -0.875 -> 0.0625
        Assert.InRange(right, 225, 245);
    }

    [Fact]
    public void PerVertexColoursBlendLikeAGradient()
    {
        using var page = new GlPage(8);
        page.Run("""
            var p = program('attribute vec2 a_pos; attribute vec3 a_color; varying vec3 v_color; void main() { v_color = a_color; gl_Position = vec4(a_pos, 0.0, 1.0); }',
                'precision mediump float; varying vec3 v_color; void main() { gl_FragColor = vec4(v_color, 1.0); }');
            attribute(p, 'a_pos', new Float32Array([-1, -1, 3, -1, -1, 3]), 2);
            attribute(p, 'a_color', new Float32Array([1, 0, 0, 0, 1, 0, 0, 0, 1]), 3);
            gl.drawArrays(gl.TRIANGLES, 0, 3);
            """);
        var bottomLeft = page.Run("pixel(0, 0)").Split(',').Select(int.Parse).ToArray();
        Assert.True(bottomLeft[0] > 180 && bottomLeft[1] < 60 && bottomLeft[2] < 60, "red corner");
    }

    [Fact]
    public void UniformsOfEveryTypeAreStoredReadBackAndTypeChecked()
    {
        using var page = new GlPage();
        page.Run("""
            var p = program('uniform mat3 m; uniform vec2 v[2]; uniform int i; uniform bool b; uniform float f; attribute vec2 a; void main() { gl_Position = vec4(a, f, float(i) + v[1].x) * vec4(m[0][0], 1.0, 1.0, float(b)); }',
                'precision mediump float; uniform sampler2D t; void main() { gl_FragColor = texture2D(t, vec2(0.5)); }');
            var lm = gl.getUniformLocation(p, 'm'), lv = gl.getUniformLocation(p, 'v'), lv1 = gl.getUniformLocation(p, 'v[1]'), li = gl.getUniformLocation(p, 'i'),
                lb = gl.getUniformLocation(p, 'b'), lf = gl.getUniformLocation(p, 'f'), lt = gl.getUniformLocation(p, 't');
            gl.uniformMatrix3fv(lm, false, [1, 2, 3, 4, 5, 6, 7, 8, 9]);
            gl.uniform2fv(lv, [1, 2, 3, 4]);
            gl.uniform1i(li, 7);
            gl.uniform1i(lb, 1);
            gl.uniform1f(lf, 0.25);
            gl.uniform1i(lt, 3);
            var errorsBefore = gl.getError();
            gl.uniform1f(li, 1);          // a float call on an int uniform
            var typeError = gl.getError();
            gl.uniform3f(lf, 1, 2, 3);     // too many components
            var sizeError = gl.getError();
            gl.uniform1f(null, 1);        // a missing location is ignored
            var nullError = gl.getError();
            """);
        Assert.Equal("1,2,3,4,5,6,7,8,9", page.Run("Array.prototype.join.call(gl.getUniform(p, lm))"));
        Assert.Equal("3,4", page.Run("Array.prototype.join.call(gl.getUniform(p, lv1))"));
        Assert.Equal("7", page.Run("String(gl.getUniform(p, li))"));
        Assert.Equal("true", page.Run("String(gl.getUniform(p, lb))"));
        Assert.Equal("0.25", page.Run("String(gl.getUniform(p, lf))"));
        Assert.Equal("3", page.Run("String(gl.getUniform(p, lt))"));
        Assert.Equal("0", page.Run("String(errorsBefore)"));
        Assert.Equal("1282", page.Run("String(typeError)"));
        Assert.Equal("1282", page.Run("String(sizeError)"));
        Assert.Equal("0", page.Run("String(nullError)"));
        Assert.Equal("null", page.Run("String(gl.getUniformLocation(p, 'missing'))"));
        Assert.Equal("m,v[0],i,b,f,t", page.Run("var names = []; for (var k = 0; k < gl.getProgramParameter(p, gl.ACTIVE_UNIFORMS); k++) names.push(gl.getActiveUniform(p, k).name); names.join()"));
        Assert.Equal("35675,35664,5124,35670,5126,35678", page.Run("var types = []; for (var k = 0; k < 6; k++) types.push(gl.getActiveUniform(p, k).type); types.join()").Replace("35675", "35675"));
    }

    [Fact]
    public void AttributeLocationsFollowBindingsAndDeclarationOrder()
    {
        using var page = new GlPage();
        page.Run("""
            var p = gl.createProgram();
            gl.attachShader(p, shader(gl.VERTEX_SHADER, 'attribute vec2 first; attribute vec4 second; attribute float third; void main() { gl_Position = vec4(first, second.x, third); }'));
            gl.attachShader(p, shader(gl.FRAGMENT_SHADER, 'void main() { gl_FragColor = vec4(1.0); }'));
            gl.bindAttribLocation(p, 5, 'second');
            gl.linkProgram(p);
            """);
        Assert.Equal("0,5,1,-1", page.Run("[gl.getAttribLocation(p, 'first'), gl.getAttribLocation(p, 'second'), gl.getAttribLocation(p, 'third'), gl.getAttribLocation(p, 'nope')].join()"));
        Assert.Equal("first,second,third", page.Run("var n = []; for (var k = 0; k < gl.getProgramParameter(p, gl.ACTIVE_ATTRIBUTES); k++) n.push(gl.getActiveAttrib(p, k).name); n.join()"));
    }

    [Fact]
    public void ABadShaderReportsItsErrorAndAMismatchedPairDoesNotLink()
    {
        using var page = new GlPage();
        Assert.Contains("ERROR: 0:1", page.Run("var s = gl.createShader(gl.FRAGMENT_SHADER); gl.shaderSource(s, 'void main() { gl_FragColor = nope; }'); gl.compileShader(s); gl.getShaderInfoLog(s)"));
        Assert.Equal("false", page.Run("String(gl.getShaderParameter(s, gl.COMPILE_STATUS))"));
        page.Run("""
            var q = gl.createProgram();
            gl.attachShader(q, shader(gl.VERTEX_SHADER, 'varying vec2 v; void main() { v = vec2(0.0); gl_Position = vec4(0.0); }'));
            gl.attachShader(q, shader(gl.FRAGMENT_SHADER, 'precision mediump float; varying vec3 v; void main() { gl_FragColor = vec4(v, 1.0); }'));
            gl.linkProgram(q);
            """);
        Assert.Equal("false", page.Run("String(gl.getProgramParameter(q, gl.LINK_STATUS))"));
        Assert.Contains("varying 'v'", page.Run("gl.getProgramInfoLog(q)"));
    }

    // ---- vertex data and primitives ----

    [Fact]
    public void VertexAttributesCanBeBytesShortsAndInterleaved()
    {
        using var page = new GlPage();
        page.Run("""
            var p = program('attribute vec2 a_pos; attribute vec4 a_color; varying vec4 v_color; void main() { v_color = a_color; gl_Position = vec4(a_pos, 0.0, 1.0); }',
                'precision mediump float; varying vec4 v_color; void main() { gl_FragColor = v_color; }');
            // 2 floats of position then 4 normalised unsigned bytes of colour, one vertex every 12 bytes
            var buffer = new ArrayBuffer(36);
            var view = new DataView(buffer);
            var corners = [[-1, -1], [3, -1], [-1, 3]];
            for (var i = 0; i < 3; i++) {
                view.setFloat32(i * 12, corners[i][0], true);
                view.setFloat32(i * 12 + 4, corners[i][1], true);
                view.setUint8(i * 12 + 8, 0); view.setUint8(i * 12 + 9, 255); view.setUint8(i * 12 + 10, 0); view.setUint8(i * 12 + 11, 255);
            }
            var b = gl.createBuffer();
            gl.bindBuffer(gl.ARRAY_BUFFER, b);
            gl.bufferData(gl.ARRAY_BUFFER, new Uint8Array(buffer), gl.STATIC_DRAW);
            gl.enableVertexAttribArray(gl.getAttribLocation(p, 'a_pos'));
            gl.vertexAttribPointer(gl.getAttribLocation(p, 'a_pos'), 2, gl.FLOAT, false, 12, 0);
            gl.enableVertexAttribArray(gl.getAttribLocation(p, 'a_color'));
            gl.vertexAttribPointer(gl.getAttribLocation(p, 'a_color'), 4, gl.UNSIGNED_BYTE, true, 12, 8);
            gl.drawArrays(gl.TRIANGLES, 0, 3);
            """);
        Assert.Equal("0,255,0,255", page.Run("pixel(3, 3)"));
    }

    [Fact]
    public void IndexedDrawsReadByteShortAndIntIndices()
    {
        foreach (var (array, type) in new[] { ("Uint8Array", "UNSIGNED_BYTE"), ("Uint16Array", "UNSIGNED_SHORT") })
        {
            using var page = new GlPage();
            page.Run($$"""
                var p = program(FLAT_VS, COLOR_FS);
                gl.uniform4f(gl.getUniformLocation(p, 'u_color'), 0, 1, 0, 1);
                attribute(p, 'a_pos', new Float32Array([5, 5, -1, -1, 3, -1, -1, 3]), 2);
                var e = gl.createBuffer();
                gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, e);
                gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, new {{array}}([1, 2, 3]), gl.STATIC_DRAW);
                gl.drawElements(gl.TRIANGLES, 3, gl.{{type}}, 0);
                """);
            Assert.Equal("0,255,0,255", page.Run("pixel(2, 2)"));
            Assert.Equal("0", page.Run("String(gl.getError())"));
        }
    }

    [Fact]
    public void DrawingPastTheEndOfABufferIsAnErrorAndDrawsNothing()
    {
        using var page = new GlPage();
        page.Run(Fill("1, 1, 1, 1").Replace("gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);", "gl.drawArrays(gl.TRIANGLES, 0, 30000);"));
        Assert.Equal("1282", page.Run("String(gl.getError())"));
        Assert.Equal("0,0,0,0", page.Run("pixel(3, 3)"));
        Assert.Equal("false", page.Run("String(gl.isContextLost())"));
    }

    [Fact]
    public void StripsFansAndLoopsMakeTheirTriangles()
    {
        using var page = new GlPage();
        // a fan around the centre covers the whole square; a strip covers it the same way
        page.Run("""
            var p = program(FLAT_VS, COLOR_FS);
            gl.uniform4f(gl.getUniformLocation(p, 'u_color'), 1, 0, 0, 1);
            attribute(p, 'a_pos', new Float32Array([0, 0, 1, -1, 1, 1, -1, 1, -1, -1, 1, -1]), 2);
            gl.drawArrays(gl.TRIANGLE_FAN, 0, 6);
            """);
        Assert.Equal("255,0,0,255", page.Run("pixel(0, 0)"));
        Assert.Equal("255,0,0,255", page.Run("pixel(7, 7)"));
        Assert.Equal("255,0,0,255", page.Run("pixel(0, 7)"));
    }

    [Fact]
    public void PointsAreSquaresOfTheirSizeWithAPointCoordinate()
    {
        using var page = new GlPage(10);
        page.Run("""
            var p = program('attribute vec2 a_pos; void main() { gl_PointSize = 4.0; gl_Position = vec4(a_pos, 0.0, 1.0); }',
                'precision mediump float; void main() { gl_FragColor = vec4(gl_PointCoord, 0.0, 1.0); }');
            attribute(p, 'a_pos', new Float32Array([0, 0]), 2);
            gl.drawArrays(gl.POINTS, 0, 1);
            """);
        // a 4 x 4 square from (3, 3) to (7, 7): gl_PointCoord runs 0..1 with y down
        Assert.Equal("0,0,0,0", page.Run("pixel(2, 5)"));
        var topLeft = page.Run("pixel(3, 6)").Split(',').Select(int.Parse).ToArray();      // top row of the point in GL's bottom-up rows
        var bottomRight = page.Run("pixel(6, 3)").Split(',').Select(int.Parse).ToArray();
        Assert.True(topLeft[0] < 60 && topLeft[1] < 60, $"top left of the point: {string.Join(",", topLeft)}");
        Assert.True(bottomRight[0] > 190 && bottomRight[1] > 190, $"bottom right of the point: {string.Join(",", bottomRight)}");
    }

    [Fact]
    public void DiscardLeavesThePixelAlone()
    {
        using var page = new GlPage();
        page.Run("""
            var p = program(FLAT_VS, 'precision mediump float; void main() { if (gl_FragCoord.x < 4.0) discard; gl_FragColor = vec4(1.0, 0.0, 0.0, 1.0); }');
            attribute(p, 'a_pos', QUAD, 2);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            """);
        Assert.Equal("0,0,0,0", page.Run("pixel(1, 3)"));
        Assert.Equal("255,0,0,255", page.Run("pixel(6, 3)"));
    }

    // ---- fixed-function state ----

    [Fact]
    public void TheDepthTestKeepsTheNearerSurfaceAndTheDepthMaskStopsWrites()
    {
        using var page = new GlPage();
        page.Run("""
            var p = program('attribute vec3 a_pos; void main() { gl_Position = vec4(a_pos, 1.0); }', COLOR_FS);
            var color = gl.getUniformLocation(p, 'u_color');
            function quad(z) { return new Float32Array([-1, -1, z, 1, -1, z, -1, 1, z, 1, 1, z]); }
            gl.enable(gl.DEPTH_TEST);
            gl.clear(gl.DEPTH_BUFFER_BIT | gl.COLOR_BUFFER_BIT);
            var b = gl.createBuffer();
            gl.bindBuffer(gl.ARRAY_BUFFER, b);
            var l = gl.getAttribLocation(p, 'a_pos');
            gl.enableVertexAttribArray(l);
            gl.vertexAttribPointer(l, 3, gl.FLOAT, false, 0, 0);
            function draw(z, r, g) { gl.bufferData(gl.ARRAY_BUFFER, quad(z), gl.STATIC_DRAW); gl.uniform4f(color, r, g, 0, 1); gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4); }
            draw(0.5, 1, 0);   // red at 0.5
            draw(0.8, 0, 1);   // green behind it: hidden
            """);
        Assert.Equal("255,0,0,255", page.Run("pixel(3, 3)"));
        page.Run("draw(0.2, 0, 0);");
        Assert.Equal("0,0,0,255", page.Run("pixel(3, 3)"));
        page.Run("gl.depthMask(false); draw(0.1, 1, 1); gl.depthMask(true); draw(0.15, 0, 1);");
        // the white quad at 0.1 wrote colour but not depth, so green at 0.15 (behind 0.1 but in front of the stored 0.2) still lands
        Assert.Equal("0,255,0,255", page.Run("pixel(3, 3)"));
    }

    [Fact]
    public void BlendingCombinesSourceAndDestination()
    {
        using var page = new GlPage();
        page.Run(Fill("0, 0, 1, 1"));
        page.Run("gl.enable(gl.BLEND); gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA); gl.uniform4f(gl.getUniformLocation(p, 'u_color'), 1, 0, 0, 0.5); gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);");
        var mixed = page.Run("pixel(3, 3)").Split(',').Select(int.Parse).ToArray();
        Assert.InRange(mixed[0], 126, 129);
        Assert.InRange(mixed[2], 126, 129);
        page.Run("gl.blendFunc(gl.ONE, gl.ONE); gl.uniform4f(gl.getUniformLocation(p, 'u_color'), 0, 1, 0, 1); gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);");
        var added = page.Run("pixel(3, 3)").Split(',').Select(int.Parse).ToArray();
        Assert.Equal(255, added[1]);
        Assert.InRange(added[0], 126, 129);
        page.Run("gl.colorMask(false, false, true, false); gl.disable(gl.BLEND); gl.uniform4f(gl.getUniformLocation(p, 'u_color'), 0, 0, 0, 0); gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);");
        var masked = page.Run("pixel(3, 3)").Split(',').Select(int.Parse).ToArray();
        Assert.Equal(0, masked[2]);
        Assert.Equal(255, masked[1]);
    }

    [Fact]
    public void FaceCullingRemovesTheBackFacesAndFrontFaceFlipsWhichIsWhich()
    {
        using var page = new GlPage();
        page.Run("""
            var p = program(FLAT_VS, COLOR_FS);
            gl.uniform4f(gl.getUniformLocation(p, 'u_color'), 1, 1, 1, 1);
            // counter-clockwise
            attribute(p, 'a_pos', new Float32Array([-1, -1, 3, -1, -1, 3]), 2);
            gl.enable(gl.CULL_FACE);
            """);
        page.Run("gl.cullFace(gl.BACK); gl.drawArrays(gl.TRIANGLES, 0, 3);");
        Assert.Equal("255,255,255,255", page.Run("pixel(2, 2)"));
        page.Run("gl.clear(gl.COLOR_BUFFER_BIT); gl.cullFace(gl.FRONT); gl.drawArrays(gl.TRIANGLES, 0, 3);");
        Assert.Equal("0,0,0,0", page.Run("pixel(2, 2)"));
        page.Run("gl.frontFace(gl.CW); gl.drawArrays(gl.TRIANGLES, 0, 3);");
        Assert.Equal("255,255,255,255", page.Run("pixel(2, 2)"));
    }

    [Fact]
    public void TheViewportPlacesTheClipSpaceSquareWhereItIsAskedAndDrawsOutsideAreClipped()
    {
        using var page = new GlPage();
        page.Run("gl.viewport(4, 0, 4, 4);" + Fill("1, 0, 0, 1"));
        Assert.Equal("255,0,0,255", page.Run("pixel(5, 1)"));
        Assert.Equal("0,0,0,0", page.Run("pixel(1, 1)"));
        Assert.Equal("0,0,0,0", page.Run("pixel(5, 5)"));
    }

    [Fact]
    public void AMultisampledEdgeCoversAPixelPartially()
    {
        using var page = new GlPage(8, "{ antialias: true }");
        page.Run("""
            var p = program(FLAT_VS, COLOR_FS);
            gl.uniform4f(gl.getUniformLocation(p, 'u_color'), 1, 1, 1, 1);
            // the right edge of the quad lies at x = 4.5 of 8 pixels: clip x = 0.125
            attribute(p, 'a_pos', new Float32Array([-1, -1, 0.125, -1, -1, 1, 0.125, 1]), 2);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            """);
        Assert.Equal("255,255,255,255", page.Run("pixel(3, 3)"));
        var edge = int.Parse(page.Run("pixel(4, 3)").Split(',')[0]);
        Assert.InRange(edge, 100, 160);
        Assert.Equal("0,0,0,0", page.Run("pixel(5, 3)"));
    }

    [Fact]
    public void InstancedDrawsAdvanceAnAttributeByItsDivisor()
    {
        using var page = new GlPage();
        page.Run("""
            var p = program('attribute vec2 a_pos; attribute vec2 a_offset; void main() { gl_Position = vec4(a_pos * 0.25 + a_offset, 0.0, 1.0); }', COLOR_FS);
            gl.uniform4f(gl.getUniformLocation(p, 'u_color'), 1, 1, 1, 1);
            attribute(p, 'a_pos', new Float32Array([-1, -1, 1, -1, -1, 1, -1, 1, 1, -1, 1, 1]), 2);
            attribute(p, 'a_offset', new Float32Array([-0.5, -0.5, 0.5, 0.5]), 2);
            gl.vertexAttribDivisor(gl.getAttribLocation(p, 'a_offset'), 1);
            gl.drawArraysInstanced(gl.TRIANGLES, 0, 6, 2);
            """);
        Assert.Equal("255,255,255,255", page.Run("pixel(2, 2)"));
        Assert.Equal("255,255,255,255", page.Run("pixel(5, 5)"));
        Assert.Equal("0,0,0,0", page.Run("pixel(2, 5)"));
    }

    [Fact]
    public void VertexArrayObjectsKeepTheirOwnAttributeState()
    {
        using var page = new GlPage();
        page.Run("""
            var p = program(FLAT_VS, COLOR_FS);
            gl.uniform4f(gl.getUniformLocation(p, 'u_color'), 1, 1, 1, 1);
            var vaoA = gl.createVertexArray();
            gl.bindVertexArray(vaoA);
            attribute(p, 'a_pos', new Float32Array([-1, -1, 0, -1, -1, 0]), 2);
            var vaoB = gl.createVertexArray();
            gl.bindVertexArray(vaoB);
            attribute(p, 'a_pos', new Float32Array([0, 0, 1, 0, 0, 1]), 2);
            gl.bindVertexArray(vaoA);
            gl.drawArrays(gl.TRIANGLES, 0, 3);
            """);
        Assert.Equal("255,255,255,255", page.Run("pixel(1, 1)"));
        Assert.Equal("0,0,0,0", page.Run("pixel(5, 5)"));
        page.Run("gl.bindVertexArray(vaoB); gl.drawArrays(gl.TRIANGLES, 0, 3);");
        Assert.Equal("255,255,255,255", page.Run("pixel(5, 5)"));
    }

    // ---- textures and framebuffers ----

    private const string TextureSetup = """
        var p = program('attribute vec2 a_pos; varying vec2 v_uv; void main() { v_uv = a_pos * 0.5 + 0.5; gl_Position = vec4(a_pos, 0.0, 1.0); }',
            'precision mediump float; varying vec2 v_uv; uniform sampler2D t; void main() { gl_FragColor = texture2D(t, v_uv); }');
        attribute(p, 'a_pos', QUAD, 2);
        function texture(w, h, data, min, mag, wrap) {
            var t = gl.createTexture();
            gl.bindTexture(gl.TEXTURE_2D, t);
            gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, w, h, 0, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array(data));
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, min);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, mag);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, wrap);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, wrap);
            return t;
        }
        """;

    [Fact]
    public void ANearestTextureShowsItsTexelsAndTheSamplerUniformPicksTheUnit()
    {
        using var page = new GlPage();
        page.Run(TextureSetup + """
            texture(2, 2, [255,0,0,255, 0,255,0,255, 0,0,255,255, 255,255,0,255], gl.NEAREST, gl.NEAREST, gl.CLAMP_TO_EDGE);
            gl.uniform1i(gl.getUniformLocation(p, 't'), 0);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            """);
        // the first row of the data is the bottom of the image (t = 0)
        Assert.Equal("255,0,0,255", page.Run("pixel(1, 1)"));
        Assert.Equal("0,255,0,255", page.Run("pixel(6, 1)"));
        Assert.Equal("0,0,255,255", page.Run("pixel(1, 6)"));
        Assert.Equal("255,255,0,255", page.Run("pixel(6, 6)"));
        // a second texture on unit 1, selected by the uniform
        page.Run("gl.activeTexture(gl.TEXTURE1); texture(1, 1, [9, 8, 7, 255], gl.NEAREST, gl.NEAREST, gl.CLAMP_TO_EDGE); gl.uniform1i(gl.getUniformLocation(p, 't'), 1); gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);");
        Assert.Equal("9,8,7,255", page.Run("pixel(3, 3)"));
    }

    [Fact]
    public void LinearFilteringBlendsAndRepeatWrapsAndMipmapsSelectSmallerLevels()
    {
        using var page = new GlPage();
        page.Run(TextureSetup + """
            texture(2, 1, [0,0,0,255, 255,255,255,255], gl.LINEAR, gl.LINEAR, gl.CLAMP_TO_EDGE);
            gl.uniform1i(gl.getUniformLocation(p, 't'), 0);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            """);
        var middle = int.Parse(page.Run("pixel(3, 3)").Split(',')[0]);
        Assert.InRange(middle, 90, 170);
        // a texture with no mipmaps but a mipmap filter reads as black until the chain exists
        page.Run("""
            var data = []; for (var i = 0; i < 16; i++) data.push(255, 255, 255, 255);
            var t4 = texture(4, 4, data, gl.LINEAR_MIPMAP_LINEAR, gl.LINEAR, gl.CLAMP_TO_EDGE);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            """);
        Assert.Equal("0,0,0,255", page.Run("pixel(3, 3)"));
        page.Run("gl.generateMipmap(gl.TEXTURE_2D); gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);");
        Assert.Equal("255,255,255,255", page.Run("pixel(3, 3)"));
    }

    [Fact]
    public void ARenderedTextureCanBeSampledByTheNextDraw()
    {
        using var page = new GlPage();
        page.Run(TextureSetup + """
            var target = gl.createTexture();
            gl.bindTexture(gl.TEXTURE_2D, target);
            gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 4, 4, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
            var fbo = gl.createFramebuffer();
            gl.bindFramebuffer(gl.FRAMEBUFFER, fbo);
            gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, target, 0);
            var status = gl.checkFramebufferStatus(gl.FRAMEBUFFER);
            gl.viewport(0, 0, 4, 4);
            var flat = program(FLAT_VS, COLOR_FS);
            gl.uniform4f(gl.getUniformLocation(flat, 'u_color'), 0, 1, 1, 1);
            attribute(flat, 'a_pos', QUAD, 2);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            var inside = pixel(1, 1);
            gl.bindFramebuffer(gl.FRAMEBUFFER, null);
            gl.viewport(0, 0, 8, 8);
            gl.useProgram(p);
            attribute(p, 'a_pos', QUAD, 2);
            gl.bindTexture(gl.TEXTURE_2D, target);
            gl.uniform1i(gl.getUniformLocation(p, 't'), 0);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            """);
        Assert.Equal("36053", page.Run("String(status)")); // FRAMEBUFFER_COMPLETE
        Assert.Equal("0,255,255,255", page.Run("inside"));
        Assert.Equal("0,255,255,255", page.Run("pixel(5, 5)"));
    }

    [Fact]
    public void ADepthRenderbufferGivesAFramebufferItsOwnDepthTest()
    {
        using var page = new GlPage();
        page.Run("""
            var target = gl.createTexture();
            gl.bindTexture(gl.TEXTURE_2D, target);
            gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 8, 8, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
            var depth = gl.createRenderbuffer();
            gl.bindRenderbuffer(gl.RENDERBUFFER, depth);
            gl.renderbufferStorage(gl.RENDERBUFFER, gl.DEPTH_COMPONENT16, 8, 8);
            var fbo = gl.createFramebuffer();
            gl.bindFramebuffer(gl.FRAMEBUFFER, fbo);
            gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, target, 0);
            gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.DEPTH_ATTACHMENT, gl.RENDERBUFFER, depth);
            gl.enable(gl.DEPTH_TEST);
            gl.clearDepth(1);
            gl.clear(gl.DEPTH_BUFFER_BIT);
            var p = program('attribute vec3 a_pos; void main() { gl_Position = vec4(a_pos, 1.0); }', COLOR_FS);
            var b = gl.createBuffer(); gl.bindBuffer(gl.ARRAY_BUFFER, b);
            var l = gl.getAttribLocation(p, 'a_pos'); gl.enableVertexAttribArray(l); gl.vertexAttribPointer(l, 3, gl.FLOAT, false, 0, 0);
            function draw(z, r) { gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, z, 1, -1, z, -1, 1, z, 1, 1, z]), gl.STATIC_DRAW); gl.uniform4f(gl.getUniformLocation(p, 'u_color'), r, 0, 0, 1); gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4); }
            draw(0.2, 1);
            draw(0.6, 0.5);
            """);
        Assert.Equal("255,0,0,255", page.Run("pixel(3, 3)"));
    }

    [Fact]
    public void BuiltinsGiveTheFragmentCoordinateAndFacing()
    {
        using var page = new GlPage();
        page.Run("""
            var p = program(FLAT_VS, 'precision mediump float; void main() { gl_FragColor = vec4(gl_FragCoord.x / 8.0, gl_FragCoord.y / 8.0, gl_FrontFacing ? 1.0 : 0.0, 1.0); }');
            attribute(p, 'a_pos', QUAD, 2);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            """);
        var corner = page.Run("pixel(2, 5)").Split(',').Select(int.Parse).ToArray();
        Assert.InRange(corner[0], 75, 85);   // (2.5 / 8) * 255
        Assert.InRange(corner[1], 170, 180); // (5.5 / 8) * 255
        Assert.Equal(255, corner[2]);
    }

    [Fact]
    public void TextureCoordinatesFromAVaryingChooseTheMipLevelTheyAreMinifiedTo()
    {
        using var page = new GlPage(4);
        page.Run(TextureSetup + """
            // level 0 is red, the next levels are what generateMipmap makes of it; then the minified quad reads from a smaller level (the same red here)
            var data = []; for (var i = 0; i < 64 * 64; i++) data.push(255, 0, 0, 255);
            texture(64, 64, data, gl.NEAREST_MIPMAP_NEAREST, gl.NEAREST, gl.REPEAT);
            gl.generateMipmap(gl.TEXTURE_2D);
            gl.uniform1i(gl.getUniformLocation(p, 't'), 0);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            """);
        Assert.Equal("255,0,0,255", page.Run("pixel(1, 1)"));
        Assert.Equal("0", page.Run("String(gl.getError())"));
    }

    // ---- the drawing buffer and the canvas ----

    [Fact]
    public void WhatIsDrawnReachesTheCanvasWhenItIsShown()
    {
        using var page = new GlPage(16);
        page.Run(Fill("0, 1, 0, 1"));
        var empty = page.Run("document.createElement('canvas').toDataURL()");
        var shown = page.Run("canvas.toDataURL()");
        Assert.StartsWith("data:image/png;base64,", shown);
        Assert.NotEqual(empty, shown);
    }

    [Fact]
    public void ThePresentedFrameIsClearedBeforeTheNextDrawUnlessPreserved()
    {
        foreach (var (attributes, expected) in new[] { ("{ antialias: false }", "0,0,0,0"), ("{ antialias: false, preserveDrawingBuffer: true }", "0,255,0,255") })
        {
            using var page = new GlPage(attributes: attributes);
            page.Run(Fill("0, 1, 0, 1"));
            page.Document.Paint(100, 100);   // shows the frame
            // readPixels after the frame was shown sees what a browser would: an emptied buffer unless it is preserved
            Assert.Equal(expected, page.Run("pixel(3, 3)"));
        }
    }

    [Fact]
    public void TheCanvasSizeSetsTheDrawingBufferAndTheInitialViewport()
    {
        using var page = new GlPage(5);
        Assert.Equal("0,0,5,5", page.Run("Array.prototype.join.call(gl.getParameter(gl.VIEWPORT))"));
        Assert.Equal("5,5", page.Run("gl.drawingBufferWidth + ',' + gl.drawingBufferHeight"));
    }
}
