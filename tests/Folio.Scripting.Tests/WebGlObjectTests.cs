using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

/// <summary>
/// The objects of the WebGL API and what hangs off them (#635): buffers, textures and programs as real objects with an identity and a type,
/// the extensions pages ask for, polygon offset, texture levels and copies from the framebuffer.
/// </summary>
public class WebGlObjectTests
{
    private const string FlatQuad = "attribute vec2 a_pos; uniform float u_z; void main() { gl_Position = vec4(a_pos, u_z, 1.0); }";

    // ---- identity and types ----

    [Fact]
    public void EveryResourceIsAnObjectOfItsOwnInterfaceAndTheSameOneEachTimeItIsAsked()
    {
        using var page = new WebGlPipelineTests.GlPage();
        Assert.Equal("true,true,true,true,true,true,true,true,true", page.Run("""
            var b = gl.createBuffer(), t = gl.createTexture(), f = gl.createFramebuffer(), r = gl.createRenderbuffer(), v = gl.createVertexArray();
            var s = gl.createShader(gl.VERTEX_SHADER), p = gl.createProgram(), m = gl.createSampler(), q = gl.createQuery();
            [b instanceof WebGLBuffer, t instanceof WebGLTexture, f instanceof WebGLFramebuffer, r instanceof WebGLRenderbuffer,
             v instanceof WebGLVertexArrayObject, s instanceof WebGLShader, p instanceof WebGLProgram, m instanceof WebGLSampler,
             q instanceof WebGLQuery].join()
            """));
        Assert.Equal("false,false", page.Run("[b instanceof WebGLTexture, typeof b === 'number'].join()"));
        Assert.Equal("true,true,true", page.Run("""
            gl.bindBuffer(gl.ARRAY_BUFFER, b); gl.bindTexture(gl.TEXTURE_2D, t); gl.bindFramebuffer(gl.FRAMEBUFFER, f);
            [gl.getParameter(gl.ARRAY_BUFFER_BINDING) === b, gl.getParameter(gl.TEXTURE_BINDING_2D) === t, gl.getParameter(gl.FRAMEBUFFER_BINDING) === f].join()
            """));
        Assert.Equal("false", page.Run("String(gl.createBuffer() === gl.createBuffer())"));
        Assert.Equal("true", page.Run("var weak = new WeakMap(); weak.set(b, 7); String(weak.get(gl.getParameter(gl.ARRAY_BUFFER_BINDING)) === 7)"));
        Assert.Equal("0", page.Run("String(gl.getError())"));
    }

    [Fact]
    public void ResourceInterfacesCannotBeConstructedByScripts()
    {
        using var page = new WebGlPipelineTests.GlPage();
        Assert.Equal("TypeError", page.Run("try { new WebGLBuffer(); 'no error' } catch (e) { e.name }"));
    }

    [Fact]
    public void TheContextIsTheSameObjectEveryTimeAndBothContextInterfacesMatchIt()
    {
        using var page = new WebGlPipelineTests.GlPage();
        Assert.Equal("true,true,true", page.Run("""
            [canvas.getContext('webgl') === gl, gl instanceof WebGLRenderingContext, gl instanceof WebGL2RenderingContext].join()
            """));
        Assert.Equal("4", page.Run("String(WebGLRenderingContext.TRIANGLES)"));
    }

    [Fact]
    public void ANumberOrTheWrongKindOfObjectWhereAnObjectIsExpectedIsATypeError()
    {
        using var page = new WebGlPipelineTests.GlPage();
        Assert.Equal("TypeError,TypeError,TypeError", page.Run("""
            function name(fn) { try { fn(); return 'none'; } catch (e) { return e.name; } }
            [name(() => gl.bindBuffer(gl.ARRAY_BUFFER, 5)), name(() => gl.bindBuffer(gl.ARRAY_BUFFER, gl.createTexture())), name(() => gl.useProgram({}))].join()
            """));
    }

    [Fact]
    public void NullStaysAcceptedWhereTheApiAllowsUnbinding()
    {
        using var page = new WebGlPipelineTests.GlPage();
        Assert.Equal("true,true", page.Run("""
            var b = gl.createBuffer(); gl.bindBuffer(gl.ARRAY_BUFFER, b); gl.bindBuffer(gl.ARRAY_BUFFER, null);
            gl.bindFramebuffer(gl.FRAMEBUFFER, null);
            [gl.getParameter(gl.ARRAY_BUFFER_BINDING) === null, gl.getError() === 0].join()
            """));
    }

    [Fact]
    public void ADeletedObjectIsNoLongerValidAndAForeignOneIsRejected()
    {
        using var page = new WebGlPipelineTests.GlPage(markup: "<canvas id='d' width='4' height='4'></canvas>");
        Assert.Equal("false", page.Run("var b = gl.createBuffer(); gl.deleteBuffer(b); String(gl.isBuffer(b))"));
        Assert.Equal("1282", page.Run("gl.bindBuffer(gl.ARRAY_BUFFER, b); String(gl.getError())"));
        Assert.Equal("1282,false", page.Run("""
            var other = document.getElementById('d').getContext('webgl'), foreign = other.createBuffer();
            gl.bindBuffer(gl.ARRAY_BUFFER, foreign);
            [gl.getError(), gl.isBuffer(foreign)].join()
            """));
        Assert.Equal("true", page.Run("String(other.isBuffer(foreign))"));
    }

    [Fact]
    public void ObjectsInTheFramebufferAndProgramQueriesComeBackAsTheSameObjects()
    {
        using var page = new WebGlPipelineTests.GlPage();
        Assert.Equal("true,true,true,true", page.Run("""
            var p = program(FLAT_VS, COLOR_FS), shaders = gl.getAttachedShaders(p);
            var t = gl.createTexture(), f = gl.createFramebuffer(), r = gl.createRenderbuffer();
            gl.bindTexture(gl.TEXTURE_2D, t); gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 2, 2, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
            gl.bindFramebuffer(gl.FRAMEBUFFER, f); gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, t, 0);
            [gl.getParameter(gl.CURRENT_PROGRAM) === p, shaders.length === 2 && shaders[0] instanceof WebGLShader,
             gl.getFramebufferAttachmentParameter(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.FRAMEBUFFER_ATTACHMENT_OBJECT_NAME) === t,
             gl.getParameter(gl.RENDERBUFFER_BINDING) === null].join()
            """));
    }

    // ---- uniform locations and reflection ----

    [Fact]
    public void AUniformLocationIsAnObjectThatOnlyItsOwnProgramAccepts()
    {
        using var page = new WebGlPipelineTests.GlPage();
        Assert.Equal("true,true,null", page.Run("""
            var p1 = program(FLAT_VS, COLOR_FS), l1 = gl.getUniformLocation(p1, 'u_color');
            var p2 = program(FLAT_VS, COLOR_FS);
            [l1 instanceof WebGLUniformLocation, l1 !== gl.getUniformLocation(p2, 'u_color'), String(gl.getUniformLocation(p2, 'missing'))].join()
            """));
        Assert.Equal("1282", page.Run("gl.useProgram(p2); gl.uniform4f(l1, 1, 0, 0, 1); String(gl.getError())"));
        Assert.Equal("0,1,0,0,1", page.Run("gl.useProgram(p1); gl.uniform4f(l1, 1, 0, 0, 1); gl.getError() + ',' + Array.from(gl.getUniform(p1, l1)).join()"));
        Assert.Equal("1282", page.Run("gl.getUniform(p2, l1); String(gl.getError())"));
    }

    [Fact]
    public void ActiveUniformsAndAttributesAreObjectsWithANameSizeAndType()
    {
        using var page = new WebGlPipelineTests.GlPage();
        Assert.Equal("true,u_color,1,35666,a_pos,35664", page.Run("""
            var p = program(FLAT_VS, COLOR_FS), u = gl.getActiveUniform(p, 0), a = gl.getActiveAttrib(p, 0);
            [u instanceof WebGLActiveInfo, u.name, u.size, u.type, a.name, a.type].join()
            """));
    }

    [Fact]
    public void ShaderPrecisionComesBackAsAnObject()
    {
        using var page = new WebGlPipelineTests.GlPage();
        Assert.Equal("true,127,127,23", page.Run("""
            var f = gl.getShaderPrecisionFormat(gl.FRAGMENT_SHADER, gl.HIGH_FLOAT);
            [f instanceof WebGLShaderPrecisionFormat, f.rangeMin, f.rangeMax, f.precision].join()
            """));
        Assert.Equal("null,1280", page.Run("[String(gl.getShaderPrecisionFormat(12, gl.HIGH_FLOAT)), gl.getError()].join()"));
    }

    // ---- extensions ----

    [Fact]
    public void ExtensionsAreListedAndHandedOutOncePerName()
    {
        using var page = new WebGlPipelineTests.GlPage();
        Assert.Equal("true,true,true,null", page.Run("""
            var names = gl.getSupportedExtensions();
            [names.includes('ANGLE_instanced_arrays') && names.includes('OES_vertex_array_object') && names.includes('WEBGL_lose_context'),
             gl.getExtension('ANGLE_instanced_arrays') === gl.getExtension('angle_instanced_arrays'),
             names.every(n => gl.getExtension(n) !== null), String(gl.getExtension('WEBGL_no_such_thing'))].join()
            """));
    }

    [Fact]
    public void InstancedArraysDrawsEachInstanceWithItsOwnAttributeValue()
    {
        using var page = new WebGlPipelineTests.GlPage(8);
        page.Run("""
            var ext = gl.getExtension('ANGLE_instanced_arrays');
            var p = program('attribute vec2 a_pos; attribute float a_offset; void main() { gl_Position = vec4(a_pos * 0.25 + vec2(a_offset, 0.0), 0.0, 1.0); }', 'precision mediump float; void main() { gl_FragColor = vec4(1.0, 0.0, 0.0, 1.0); }');
            attribute(p, 'a_pos', QUAD, 2);
            attribute(p, 'a_offset', new Float32Array([-0.5, 0.5]), 1);
            ext.vertexAttribDivisorANGLE(gl.getAttribLocation(p, 'a_offset'), 1);
            ext.drawArraysInstancedANGLE(gl.TRIANGLE_STRIP, 0, 4, 2);
            """);
        Assert.Equal("255,0,0,255", page.Run("pixel(2, 4)"));
        Assert.Equal("255,0,0,255", page.Run("pixel(5, 4)"));
        Assert.Equal("0,0,0,0", page.Run("pixel(0, 4)"));
        Assert.Equal("0", page.Run("String(gl.getError())"));
    }

    [Fact]
    public void VertexArrayObjectsKeepTheirOwnAttributeSetup()
    {
        using var page = new WebGlPipelineTests.GlPage(8);
        page.Run("""
            var ext = gl.getExtension('OES_vertex_array_object');
            var p = program(FLAT_VS, COLOR_FS);
            gl.uniform4f(gl.getUniformLocation(p, 'u_color'), 0, 1, 0, 1);
            var vao = ext.createVertexArrayOES();
            ext.bindVertexArrayOES(vao);
            attribute(p, 'a_pos', QUAD, 2);
            ext.bindVertexArrayOES(null);
            ext.bindVertexArrayOES(vao);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            """);
        Assert.Equal("0,255,0,255", page.Run("pixel(4, 4)"));
        Assert.Equal("true,true", page.Run("[ext.isVertexArrayOES(vao), vao instanceof WebGLVertexArrayObject].join()"));
    }

    [Fact]
    public void BlendMinMaxKeepsTheLargerOrSmallerChannel()
    {
        using var page = new WebGlPipelineTests.GlPage();
        page.Run("""
            var ext = gl.getExtension('EXT_blend_minmax');
            gl.clearColor(0.2, 0.8, 0.5, 1); gl.clear(gl.COLOR_BUFFER_BIT);
            gl.enable(gl.BLEND); gl.blendEquation(ext.MAX_EXT); gl.blendFunc(gl.ONE, gl.ONE);
            """ + Fill("0.6, 0.4, 0.5, 1"));
        Assert.Equal("153,204,128,255", page.Run("pixel(3, 3)"));
        page.Run("gl.blendEquation(ext.MIN_EXT); gl.clearColor(0.2, 0.8, 0.5, 1); gl.disable(gl.BLEND); gl.clear(gl.COLOR_BUFFER_BIT); gl.enable(gl.BLEND); gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);");
        Assert.Equal("51,102,128,255", page.Run("pixel(3, 3)"));
    }

    [Fact]
    public void ThirtyTwoBitIndicesDrawWhenTheExtensionIsEnabled()
    {
        using var page = new WebGlPipelineTests.GlPage();
        page.Run("""
            gl.getExtension('OES_element_index_uint');
            var p = program(FLAT_VS, COLOR_FS);
            gl.uniform4f(gl.getUniformLocation(p, 'u_color'), 0, 0, 1, 1);
            attribute(p, 'a_pos', QUAD, 2);
            var ib = gl.createBuffer(); gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, ib);
            gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, new Uint32Array([0, 1, 2, 3]), gl.STATIC_DRAW);
            gl.drawElements(gl.TRIANGLE_STRIP, 4, gl.UNSIGNED_INT, 0);
            """);
        Assert.Equal("0,0,255,255", page.Run("pixel(4, 4)"));
        Assert.Equal("0", page.Run("String(gl.getError())"));
    }

    [Fact]
    public void StandardDerivativesCompileUnderTheExtensionDirective()
    {
        using var page = new WebGlPipelineTests.GlPage();
        Assert.Equal("true", page.Run("""
            gl.getExtension('OES_standard_derivatives');
            var p = program(FLAT_VS, '#extension GL_OES_standard_derivatives : enable\nprecision mediump float; void main() { float d = fwidth(gl_FragCoord.x); gl_FragColor = vec4(d > 0.0 ? 1.0 : 0.0, 0.0, 0.0, 1.0); }');
            String(gl.getProgramParameter(p, gl.LINK_STATUS))
            """));
    }

    [Fact]
    public void AFragmentShaderCanWriteItsOwnDepthUnderFragDepth()
    {
        using var page = new WebGlPipelineTests.GlPage();
        page.Run("""
            gl.getExtension('EXT_frag_depth');
            var p = program(FLAT_VS, '#extension GL_EXT_frag_depth : enable\nprecision mediump float; uniform vec4 u_color; void main() { gl_FragColor = u_color; gl_FragDepthEXT = 0.25; }');
            attribute(p, 'a_pos', QUAD, 2);
            gl.enable(gl.DEPTH_TEST); gl.depthFunc(gl.LESS);
            gl.uniform4f(gl.getUniformLocation(p, 'u_color'), 1, 0, 0, 1);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            """);
        Assert.Equal("255,0,0,255", page.Run("pixel(4, 4)"));
        // The geometry is at depth 0.5 (z = 0), nearer than 0.25 never; a second draw at the geometry's own depth must now fail the test.
        page.Run("""
            var q = program(FLAT_VS, COLOR_FS);
            attribute(q, 'a_pos', QUAD, 2);
            gl.uniform4f(gl.getUniformLocation(q, 'u_color'), 0, 1, 0, 1);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            """);
        Assert.Equal("255,0,0,255", page.Run("pixel(4, 4)"));
    }

    [Fact]
    public void TheDebugRendererExtensionNamesTheRenderer()
    {
        using var page = new WebGlPipelineTests.GlPage();
        Assert.Equal("Folio,Folio software rasteriser", page.Run("""
            var info = gl.getExtension('WEBGL_debug_renderer_info');
            [gl.getParameter(info.UNMASKED_VENDOR_WEBGL), gl.getParameter(info.UNMASKED_RENDERER_WEBGL)].join()
            """));
    }

    [Fact]
    public void AnisotropyIsAParameterOfTheTexture()
    {
        using var page = new WebGlPipelineTests.GlPage();
        Assert.Equal("16,4", page.Run("""
            var ext = gl.getExtension('EXT_texture_filter_anisotropic'), t = gl.createTexture();
            gl.bindTexture(gl.TEXTURE_2D, t); gl.texParameterf(gl.TEXTURE_2D, ext.TEXTURE_MAX_ANISOTROPY_EXT, 4);
            [gl.getParameter(ext.MAX_TEXTURE_MAX_ANISOTROPY_EXT), gl.getTexParameter(gl.TEXTURE_2D, ext.TEXTURE_MAX_ANISOTROPY_EXT)].join()
            """));
    }

    [Fact]
    public void LosingTheContextThroughTheExtensionStillWorks()
    {
        using var page = new WebGlPipelineTests.GlPage();
        Assert.Equal("true,false", page.Run("""
            var lose = gl.getExtension('WEBGL_lose_context');
            lose.loseContext(); var lost = gl.isContextLost(); lose.restoreContext();
            [lost, gl.isContextLost()].join()
            """));
    }

    // ---- polygon offset ----

    private static string Fill(string color) => $"var p = program(FLAT_VS, COLOR_FS); gl.uniform4f(gl.getUniformLocation(p, 'u_color'), {color}); attribute(p, 'a_pos', QUAD, 2); gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);";

    private static string CoplanarDraws(string setup) => $$"""
        var p = program('{{FlatQuad}}', COLOR_FS);
        attribute(p, 'a_pos', QUAD, 2);
        var color = gl.getUniformLocation(p, 'u_color');
        gl.enable(gl.DEPTH_TEST); gl.depthFunc(gl.LESS);
        gl.uniform4f(color, 0, 1, 0, 1); gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
        {{setup}}
        gl.uniform4f(color, 1, 0, 0, 1); gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
        """;

    [Fact]
    public void ACoplanarSurfaceFailsTheDepthTestWithoutAnOffset()
    {
        using var page = new WebGlPipelineTests.GlPage();
        page.Run(CoplanarDraws(""));
        Assert.Equal("0,255,0,255", page.Run("pixel(4, 4)"));
    }

    [Fact]
    public void PolygonOffsetPullsACoplanarSurfaceInFront()
    {
        using var page = new WebGlPipelineTests.GlPage();
        page.Run(CoplanarDraws("gl.enable(gl.POLYGON_OFFSET_FILL); gl.polygonOffset(0, -2);"));
        Assert.Equal("255,0,0,255", page.Run("pixel(4, 4)"));
    }

    [Fact]
    public void PolygonOffsetDoesNothingWhileItIsDisabled()
    {
        using var page = new WebGlPipelineTests.GlPage();
        page.Run(CoplanarDraws("gl.polygonOffset(-1, -2);"));
        Assert.Equal("0,255,0,255", page.Run("pixel(4, 4)"));
    }

    [Fact]
    public void PolygonOffsetPushingBackwardLeavesTheNearerSurfaceInFront()
    {
        using var page = new WebGlPipelineTests.GlPage();
        page.Run(CoplanarDraws("gl.depthFunc(gl.LEQUAL); gl.enable(gl.POLYGON_OFFSET_FILL); gl.polygonOffset(0, 8);"));
        Assert.Equal("0,255,0,255", page.Run("pixel(4, 4)"));
    }

    [Fact]
    public void ThePolygonOffsetFactorScalesWithTheSlopeOfTheSurface()
    {
        using var page = new WebGlPipelineTests.GlPage();
        // A tilted quad: the second draw has the same tilt, so only the slope term can pull it in front.
        page.Run("""
            var p = program('attribute vec2 a_pos; void main() { gl_Position = vec4(a_pos, a_pos.x * 0.5, 1.0); }', COLOR_FS);
            attribute(p, 'a_pos', QUAD, 2);
            var color = gl.getUniformLocation(p, 'u_color');
            gl.enable(gl.DEPTH_TEST); gl.depthFunc(gl.LESS);
            gl.uniform4f(color, 0, 1, 0, 1); gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            gl.enable(gl.POLYGON_OFFSET_FILL); gl.polygonOffset(-1, 0);
            gl.uniform4f(color, 1, 0, 0, 1); gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            """);
        Assert.Equal("255,0,0,255", page.Run("pixel(4, 4)"));
    }

    // ---- texture levels and copies ----

    // Escaped newlines: these are pasted between quotes in the page's script.
    private const string LevelShader = "#version 300 es\\nprecision mediump float; uniform sampler2D s; uniform float lod; out vec4 c; void main() { c = textureLod(s, vec2(0.5), lod); }";
    private const string LevelVertex = "#version 300 es\\nin vec2 a_pos; void main() { gl_Position = vec4(a_pos, 0.0, 1.0); }";

    private static string Solid(int size, int r, int g, int b) =>
        $"new Uint8Array(Array.from({{ length: {size * size} }}, () => [{r}, {g}, {b}, 255]).flat())";

    [Fact]
    public void EachMipLevelHoldsTheImageGivenForIt()
    {
        using var page = new WebGlPipelineTests.GlPage();
        page.Run($$"""
            var p = program('{{LevelVertex}}', '{{LevelShader}}');
            attribute(p, 'a_pos', QUAD, 2);
            var t = gl.createTexture(); gl.bindTexture(gl.TEXTURE_2D, t);
            gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 4, 4, 0, gl.RGBA, gl.UNSIGNED_BYTE, {{Solid(4, 255, 0, 0)}});
            gl.texImage2D(gl.TEXTURE_2D, 1, gl.RGBA, 2, 2, 0, gl.RGBA, gl.UNSIGNED_BYTE, {{Solid(2, 0, 255, 0)}});
            gl.texImage2D(gl.TEXTURE_2D, 2, gl.RGBA, 1, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, {{Solid(1, 0, 0, 255)}});
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST_MIPMAP_NEAREST);
            gl.uniform1i(gl.getUniformLocation(p, 's'), 0);
            var lod = gl.getUniformLocation(p, 'lod');
            """);
        Assert.Equal("255,0,0,255", page.Run("gl.uniform1f(lod, 0); gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4); pixel(4, 4)"));
        Assert.Equal("0,255,0,255", page.Run("gl.uniform1f(lod, 1); gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4); pixel(4, 4)"));
        Assert.Equal("0,0,255,255", page.Run("gl.uniform1f(lod, 2); gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4); pixel(4, 4)"));
        Assert.Equal("0", page.Run("String(gl.getError())"));
    }

    [Fact]
    public void ATextureMissingALevelIsIncompleteForAMipmappedFilter()
    {
        using var page = new WebGlPipelineTests.GlPage();
        page.Run($$"""
            var p = program('{{LevelVertex}}', '{{LevelShader}}');
            attribute(p, 'a_pos', QUAD, 2);
            var t = gl.createTexture(); gl.bindTexture(gl.TEXTURE_2D, t);
            gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 4, 4, 0, gl.RGBA, gl.UNSIGNED_BYTE, {{Solid(4, 255, 0, 0)}});
            gl.texImage2D(gl.TEXTURE_2D, 2, gl.RGBA, 1, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, {{Solid(1, 0, 0, 255)}});
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST_MIPMAP_NEAREST);
            gl.uniform1i(gl.getUniformLocation(p, 's'), 0);
            gl.uniform1f(gl.getUniformLocation(p, 'lod'), 0);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            """);
        Assert.Equal("0,0,0,255", page.Run("pixel(4, 4)"));
    }

    [Fact]
    public void TexSubImageWritesIntoTheLevelItNames()
    {
        using var page = new WebGlPipelineTests.GlPage();
        page.Run($$"""
            var p = program('{{LevelVertex}}', '{{LevelShader}}');
            attribute(p, 'a_pos', QUAD, 2);
            var t = gl.createTexture(); gl.bindTexture(gl.TEXTURE_2D, t);
            gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 2, 2, 0, gl.RGBA, gl.UNSIGNED_BYTE, {{Solid(2, 255, 0, 0)}});
            gl.texImage2D(gl.TEXTURE_2D, 1, gl.RGBA, 1, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, {{Solid(1, 0, 0, 0)}});
            gl.texSubImage2D(gl.TEXTURE_2D, 1, 0, 0, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, {{Solid(1, 255, 255, 0)}});
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST_MIPMAP_NEAREST);
            gl.uniform1i(gl.getUniformLocation(p, 's'), 0);
            gl.uniform1f(gl.getUniformLocation(p, 'lod'), 1);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            """);
        Assert.Equal("255,255,0,255", page.Run("pixel(4, 4)"));
        Assert.Equal("1281", page.Run("gl.texSubImage2D(gl.TEXTURE_2D, 5, 0, 0, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array(4)); String(gl.getError())"));
    }

    private const string CopyShader = "precision mediump float; uniform sampler2D s; varying vec2 v; void main() { gl_FragColor = texture2D(s, v); }";
    private const string CopyVertex = "attribute vec2 a_pos; varying vec2 v; void main() { v = a_pos * 0.5 + 0.5; gl_Position = vec4(a_pos, 0.0, 1.0); }";

    [Fact]
    public void CopyTexImageTakesItsPixelsFromTheFramebuffer()
    {
        using var page = new WebGlPipelineTests.GlPage();
        page.Run("""
            gl.clearColor(0, 0, 1, 1); gl.clear(gl.COLOR_BUFFER_BIT);
            var t = gl.createTexture(); gl.bindTexture(gl.TEXTURE_2D, t);
            gl.copyTexImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 0, 0, 8, 8, 0);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
            gl.clearColor(0, 0, 0, 1); gl.clear(gl.COLOR_BUFFER_BIT);
            """ + $$"""
            var p = program('{{CopyVertex}}', '{{CopyShader}}');
            attribute(p, 'a_pos', QUAD, 2);
            gl.uniform1i(gl.getUniformLocation(p, 's'), 0);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            """);
        Assert.Equal("0,0,255,255", page.Run("pixel(3, 5)"));
        Assert.Equal("0", page.Run("String(gl.getError())"));
    }

    [Fact]
    public void CopyTexSubImageOverwritesOnlyTheRegionItNames()
    {
        using var page = new WebGlPipelineTests.GlPage();
        page.Run("""
            var t = gl.createTexture(); gl.bindTexture(gl.TEXTURE_2D, t);
            gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 8, 8, 0, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array(8 * 8 * 4).fill(255));
            gl.clearColor(1, 0, 0, 1); gl.clear(gl.COLOR_BUFFER_BIT);
            gl.copyTexSubImage2D(gl.TEXTURE_2D, 0, 0, 0, 0, 0, 4, 4);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
            """ + $$"""
            var p = program('{{CopyVertex}}', '{{CopyShader}}');
            attribute(p, 'a_pos', QUAD, 2);
            gl.uniform1i(gl.getUniformLocation(p, 's'), 0);
            gl.clearColor(0, 0, 0, 1); gl.clear(gl.COLOR_BUFFER_BIT);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            """);
        Assert.Equal("255,0,0,255", page.Run("pixel(1, 1)"));
        Assert.Equal("255,255,255,255", page.Run("pixel(6, 6)"));
        Assert.Equal("1281", page.Run("gl.copyTexSubImage2D(gl.TEXTURE_2D, 0, 6, 6, 0, 0, 4, 4); String(gl.getError())"));
    }

    [Fact]
    public void ACopyOutsideTheFramebufferReadsAsTransparentBlack()
    {
        using var page = new WebGlPipelineTests.GlPage();
        page.Run("""
            gl.clearColor(1, 1, 1, 1); gl.clear(gl.COLOR_BUFFER_BIT);
            var t = gl.createTexture(); gl.bindTexture(gl.TEXTURE_2D, t);
            gl.copyTexImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 4, 4, 8, 8, 0);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
            """ + $$"""
            var p = program('{{CopyVertex}}', '{{CopyShader}}');
            attribute(p, 'a_pos', QUAD, 2);
            gl.uniform1i(gl.getUniformLocation(p, 's'), 0);
            gl.clearColor(0, 0, 0, 0); gl.clear(gl.COLOR_BUFFER_BIT);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            """);
        Assert.Equal("255,255,255,255", page.Run("pixel(1, 1)"));
        Assert.Equal("0,0,0,0", page.Run("pixel(6, 6)"));
    }
}
