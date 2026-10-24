using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

/// <summary>
/// Floating-point and depth images, and several colour attachments (#636): textures uploaded and sampled as floats, float render targets
/// read back exactly, depth textures rendered into and sampled, and a draw into three attachments read one by one.
/// </summary>
public class WebGlFloatTests
{
    private const string Helpers = """
        function target(w, h, internal, format, type, ext) {
            var t = gl.createTexture(); gl.bindTexture(gl.TEXTURE_2D, t);
            gl.texImage2D(gl.TEXTURE_2D, 0, internal, w, h, 0, format, type, null);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
            var f = gl.createFramebuffer(); gl.bindFramebuffer(gl.FRAMEBUFFER, f);
            gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, t, 0);
            return { t: t, f: f };
        }
        function floats(x, y) { var o = new Float32Array(4); gl.readPixels(x, y, 1, 1, gl.RGBA, gl.FLOAT, o); return Array.from(o).join(','); }
        var ES3_VS = '#version 300 es\nin vec2 a_pos; void main() { gl_Position = vec4(a_pos, 0.0, 1.0); }';
        function es3(fs) { return program(ES3_VS, '#version 300 es\nprecision highp float; ' + fs); }
        """;

    private static WebGlPipelineTests.GlPage Page(int size = 4)
    {
        var page = new WebGlPipelineTests.GlPage(size);
        page.Run(Helpers);
        return page;
    }

    // ---- float render targets ----

    [Fact]
    public void AFloatTextureAsARenderTargetHoldsValuesOutsideZeroToOne()
    {
        using var page = Page();
        page.Run("""
            gl.getExtension('EXT_color_buffer_float');
            var rt = target(2, 2, gl.RGBA32F, gl.RGBA, gl.FLOAT);
            gl.viewport(0, 0, 2, 2);
            var p = es3('out vec4 c; void main() { c = vec4(2.5, -1.0, 100.25, 0.5); }');
            attribute(p, 'a_pos', QUAD, 2);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            """);
        Assert.Equal("2.5,-1,100.25,0.5", page.Run("floats(0, 0)"));
        Assert.Equal("2.5,-1,100.25,0.5", page.Run("floats(1, 1)"));
        Assert.Equal("0", page.Run("String(gl.getError())"));
    }

    [Fact]
    public void AHalfFloatTargetKeepsValuesToHalfPrecision()
    {
        using var page = Page();
        page.Run("""
            gl.getExtension('EXT_color_buffer_float');
            var rt = target(2, 2, gl.RGBA16F, gl.RGBA, gl.HALF_FLOAT);
            gl.viewport(0, 0, 2, 2);
            var p = es3('out vec4 c; void main() { c = vec4(1024.0, -0.5, 0.1, 65504.0); }');
            attribute(p, 'a_pos', QUAD, 2);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            """);
        var parts = page.Run("floats(0, 0)").Split(',').Select(double.Parse).ToArray();
        Assert.Equal(1024, parts[0]);
        Assert.Equal(-0.5, parts[1]);
        Assert.Equal(0.1, parts[2], 3);   // 0.1 rounded to a half: 0.0999755859375
        Assert.NotEqual(0.1, parts[2]);
        Assert.Equal(65504, parts[3]);
    }

    [Fact]
    public void ASingleChannelFloatTextureReadsBackItsRedChannel()
    {
        using var page = Page();
        page.Run("""
            gl.getExtension('EXT_color_buffer_float');
            var rt = target(2, 2, gl.R32F, gl.RED, gl.FLOAT);
            gl.viewport(0, 0, 2, 2);
            var p = es3('out vec4 c; void main() { c = vec4(7.75, 3.0, 2.0, 1.0); }');
            attribute(p, 'a_pos', QUAD, 2);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            """);
        Assert.Equal("7.75,0,0,1", page.Run("floats(0, 0)"));
    }

    [Fact]
    public void FloatBlendingAddsWithoutClamping()
    {
        using var page = Page();
        page.Run("""
            gl.getExtension('EXT_color_buffer_float'); gl.getExtension('EXT_float_blend');
            var rt = target(2, 2, gl.RGBA32F, gl.RGBA, gl.FLOAT);
            gl.viewport(0, 0, 2, 2);
            gl.clearBufferfv(gl.COLOR, 0, [3, 4, 5, 6]);
            gl.enable(gl.BLEND); gl.blendFunc(gl.ONE, gl.ONE);
            var p = es3('out vec4 c; void main() { c = vec4(10.0, 20.0, 30.0, 40.0); }');
            attribute(p, 'a_pos', QUAD, 2);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            """);
        Assert.Equal("13,24,35,46", page.Run("floats(0, 0)"));
    }

    [Fact]
    public void AFloatColorAttachmentNeedsAnExtensionToBeComplete()
    {
        using var page = Page();
        page.Run("var rt = target(2, 2, gl.RGBA32F, gl.RGBA, gl.FLOAT);");
        Assert.Equal("36054", page.Run("String(gl.checkFramebufferStatus(gl.FRAMEBUFFER))"));   // INCOMPLETE_ATTACHMENT
        Assert.Equal("36053", page.Run("gl.getExtension('EXT_color_buffer_float'); String(gl.checkFramebufferStatus(gl.FRAMEBUFFER))"));   // COMPLETE
        Assert.Equal("36053", page.Run("""
            var eight = target(2, 2, gl.RGBA8, gl.RGBA, gl.UNSIGNED_BYTE);
            String(gl.checkFramebufferStatus(gl.FRAMEBUFFER))
            """));
    }

    [Fact]
    public void ReadingAFloatBufferAsBytesIsInvalid()
    {
        using var page = Page();
        page.Run("gl.getExtension('EXT_color_buffer_float'); var rt = target(2, 2, gl.RGBA32F, gl.RGBA, gl.FLOAT); gl.clearBufferfv(gl.COLOR, 0, [1, 2, 3, 4]);");
        Assert.Equal("1282", page.Run("gl.readPixels(0, 0, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array(4)); String(gl.getError())"));
        Assert.Equal("0", page.Run("String(gl.getError())"));
    }

    [Fact]
    public void ClearBufferTakesValuesOutsideTheFixedPointRange()
    {
        using var page = Page();
        page.Run("gl.getExtension('EXT_color_buffer_float'); var rt = target(2, 2, gl.RGBA32F, gl.RGBA, gl.FLOAT); gl.clearBufferfv(gl.COLOR, 0, [-4.5, 8, 1e6, 0.25]);");
        Assert.Equal("-4.5,8,1000000,0.25", page.Run("floats(1, 1)"));
    }

    // ---- float textures ----

    private const string TwoTexelFloatTexture = """
        gl.getExtension('OES_texture_float');
        var src = gl.createTexture(); gl.bindTexture(gl.TEXTURE_2D, src);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA32F, 2, 1, 0, gl.RGBA, gl.FLOAT, new Float32Array([0, 0, 0, 1,   4, 8, -12, 1]));
        """;

    private const string SampleToFloatTarget = """
        gl.getExtension('EXT_color_buffer_float');
        var rt = target(2, 2, gl.RGBA32F, gl.RGBA, gl.FLOAT);
        gl.viewport(0, 0, 2, 2);
        var p = es3('uniform sampler2D s; uniform vec2 at; out vec4 c; void main() { c = texture(s, at); }');
        attribute(p, 'a_pos', QUAD, 2);
        gl.activeTexture(gl.TEXTURE0);
        """;

    [Fact]
    public void AFloatTextureIsSampledExactlyWithNearestFiltering()
    {
        using var page = Page();
        page.Run(TwoTexelFloatTexture + """
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST); gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
            gl.bindTexture(gl.TEXTURE_2D, null);
            """ + SampleToFloatTarget + """
            gl.bindTexture(gl.TEXTURE_2D, src);
            gl.uniform1i(gl.getUniformLocation(p, 's'), 0);
            gl.uniform2f(gl.getUniformLocation(p, 'at'), 0.75, 0.5);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            """);
        Assert.Equal("4,8,-12,1", page.Run("floats(0, 0)"));
    }

    [Fact]
    public void LinearFilteringOfFloatsNeedsItsExtension()
    {
        using var page = Page();
        page.Run(TwoTexelFloatTexture + """
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR); gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
            """ + SampleToFloatTarget + """
            gl.bindTexture(gl.TEXTURE_2D, src);
            gl.uniform1i(gl.getUniformLocation(p, 's'), 0);
            gl.uniform2f(gl.getUniformLocation(p, 'at'), 0.5, 0.5);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            """);
        Assert.Equal("0,0,0,1", page.Run("floats(0, 0)"));   // incomplete: reads black
        page.Run("gl.getExtension('OES_texture_float_linear'); gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);");
        Assert.Equal("2,4,-6,1", page.Run("floats(0, 0)"));
    }

    [Fact]
    public void AHalfFloatTextureTakesHalfBitsAndFiltersLinearly()
    {
        using var page = Page();
        // 0x3C00 is 1.0 and 0x4000 is 2.0 as half floats.
        page.Run("""
            var src = gl.createTexture(); gl.bindTexture(gl.TEXTURE_2D, src);
            gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA16F, 2, 1, 0, gl.RGBA, gl.HALF_FLOAT, new Uint16Array([0x3C00, 0x3C00, 0x3C00, 0x3C00,   0x4000, 0x4000, 0x4000, 0x4000]));
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR); gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
            """ + SampleToFloatTarget + """
            gl.bindTexture(gl.TEXTURE_2D, src);
            gl.uniform1i(gl.getUniformLocation(p, 's'), 0);
            gl.uniform2f(gl.getUniformLocation(p, 'at'), 0.5, 0.5);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            """);
        Assert.Equal("1.5,1.5,1.5,1.5", page.Run("floats(0, 0)"));
    }

    [Fact]
    public void ASingleChannelFloatTextureSamplesWithOpaqueAlphaAndNoOtherColour()
    {
        using var page = Page();
        page.Run("""
            var src = gl.createTexture(); gl.bindTexture(gl.TEXTURE_2D, src);
            gl.texImage2D(gl.TEXTURE_2D, 0, gl.R32F, 1, 1, 0, gl.RED, gl.FLOAT, new Float32Array([9.5]));
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST); gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
            """ + SampleToFloatTarget + """
            gl.bindTexture(gl.TEXTURE_2D, src);
            gl.uniform1i(gl.getUniformLocation(p, 's'), 0);
            gl.uniform2f(gl.getUniformLocation(p, 'at'), 0.5, 0.5);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            """);
        Assert.Equal("9.5,0,0,1", page.Run("floats(0, 0)"));
    }

    [Fact]
    public void AnUploadTooShortForItsSizeIsRejected()
    {
        using var page = Page();
        Assert.Equal("1282", page.Run("""
            var t = gl.createTexture(); gl.bindTexture(gl.TEXTURE_2D, t);
            gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA32F, 2, 2, 0, gl.RGBA, gl.FLOAT, new Float32Array(8));
            String(gl.getError())
            """));
    }

    [Fact]
    public void ABadFormatAndTypeCombinationIsInvalidEnum()
    {
        using var page = Page();
        Assert.Equal("1280", page.Run("""
            var t = gl.createTexture(); gl.bindTexture(gl.TEXTURE_2D, t);
            gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 1, 1, 0, gl.RGBA, gl.UNSIGNED_INT, null);
            String(gl.getError())
            """));
    }

    // ---- depth textures ----

    private const string DepthPass = """
        gl.getExtension('WEBGL_depth_texture');
        var depth = gl.createTexture(); gl.bindTexture(gl.TEXTURE_2D, depth);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.DEPTH_COMPONENT24, 4, 4, 0, gl.DEPTH_COMPONENT, gl.UNSIGNED_INT, null);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST); gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
        var fbo = gl.createFramebuffer(); gl.bindFramebuffer(gl.FRAMEBUFFER, fbo);
        gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.DEPTH_ATTACHMENT, gl.TEXTURE_2D, depth, 0);
        """;

    [Fact]
    public void ADepthOnlyFramebufferIsCompleteAndRecordsTheDepthOfWhatIsDrawn()
    {
        using var page = Page();
        page.Run(DepthPass + """
            var status = gl.checkFramebufferStatus(gl.FRAMEBUFFER);
            gl.drawBuffers([gl.NONE]);
            gl.viewport(0, 0, 4, 4);
            gl.enable(gl.DEPTH_TEST); gl.depthFunc(gl.LESS); gl.clearDepth(1.0); gl.clear(gl.DEPTH_BUFFER_BIT);
            var p = program('attribute vec2 a_pos; void main() { gl_Position = vec4(a_pos, -0.5, 1.0); }', 'precision mediump float; void main() { gl_FragColor = vec4(1.0); }');
            attribute(p, 'a_pos', QUAD, 2);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            """);
        Assert.Equal("36053", page.Run("String(status)"));
        Assert.Equal("0", page.Run("String(gl.getError())"));
        // Sample the depth texture on the canvas: ndc z -0.5 is depth 0.25.
        page.Run("""
            gl.bindFramebuffer(gl.FRAMEBUFFER, null); gl.viewport(0, 0, 4, 4); gl.disable(gl.DEPTH_TEST);
            var q = es3('uniform sampler2D s; out vec4 c; void main() { c = vec4(texture(s, vec2(0.5)).rrr, 1.0); }');
            attribute(q, 'a_pos', QUAD, 2);
            gl.activeTexture(gl.TEXTURE0); gl.bindTexture(gl.TEXTURE_2D, depth); gl.uniform1i(gl.getUniformLocation(q, 's'), 0);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            """);
        var shade = int.Parse(page.Run("pixel(1, 1)").Split(',')[0]);
        Assert.InRange(shade, 63, 65);   // 0.25 * 255
    }

    [Fact]
    public void ADepthTextureKeepsTheNearestSurfaceAcrossDraws()
    {
        using var page = Page();
        page.Run(DepthPass + """
            gl.drawBuffers([gl.NONE]);
            gl.viewport(0, 0, 4, 4);
            gl.enable(gl.DEPTH_TEST); gl.depthFunc(gl.LESS); gl.clear(gl.DEPTH_BUFFER_BIT);
            var p = program('attribute vec2 a_pos; uniform float z; void main() { gl_Position = vec4(a_pos, z, 1.0); }', 'precision mediump float; void main() { gl_FragColor = vec4(1.0); }');
            attribute(p, 'a_pos', QUAD, 2);
            var z = gl.getUniformLocation(p, 'z');
            gl.uniform1f(z, 0.5); gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            gl.uniform1f(z, -0.5); gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            gl.uniform1f(z, 0.0); gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            gl.bindFramebuffer(gl.FRAMEBUFFER, null); gl.viewport(0, 0, 4, 4); gl.disable(gl.DEPTH_TEST);
            var q = es3('uniform sampler2D s; out vec4 c; void main() { c = vec4(texture(s, vec2(0.5)).rrr, 1.0); }');
            attribute(q, 'a_pos', QUAD, 2);
            gl.activeTexture(gl.TEXTURE0); gl.bindTexture(gl.TEXTURE_2D, depth); gl.uniform1i(gl.getUniformLocation(q, 's'), 0);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            """);
        var shade = int.Parse(page.Run("pixel(1, 1)").Split(',')[0]);
        Assert.InRange(shade, 63, 65);
    }

    [Fact]
    public void ADepthTextureWithColourIsAShadowMapForALaterPass()
    {
        using var page = Page(8);
        // Pass 1: a nearer square (left half) into the depth texture. Pass 2: a far full-screen quad lit only where the map says nothing is nearer.
        page.Run(DepthPass + """
            var color = target(4, 4, gl.RGBA8, gl.RGBA, gl.UNSIGNED_BYTE);
            gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.DEPTH_ATTACHMENT, gl.TEXTURE_2D, depth, 0);
            gl.viewport(0, 0, 4, 4);
            gl.enable(gl.DEPTH_TEST); gl.depthFunc(gl.LESS); gl.clear(gl.DEPTH_BUFFER_BIT | gl.COLOR_BUFFER_BIT);
            var p = program('attribute vec2 a_pos; void main() { gl_Position = vec4(a_pos.x * 0.5 - 0.5, a_pos.y, -0.5, 1.0); }', COLOR_FS);
            attribute(p, 'a_pos', QUAD, 2);
            gl.uniform4f(gl.getUniformLocation(p, 'u_color'), 1, 1, 1, 1);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            var status = gl.checkFramebufferStatus(gl.FRAMEBUFFER);
            gl.bindFramebuffer(gl.FRAMEBUFFER, null); gl.viewport(0, 0, 8, 8); gl.disable(gl.DEPTH_TEST);
            var q = es3('uniform sampler2D s; out vec4 c; void main() { vec2 uv = gl_FragCoord.xy / 8.0; float nearest = texture(s, uv).r; c = nearest < 0.5 ? vec4(0.0, 0.0, 0.0, 1.0) : vec4(1.0, 1.0, 0.0, 1.0); }');
            attribute(q, 'a_pos', QUAD, 2);
            gl.activeTexture(gl.TEXTURE0); gl.bindTexture(gl.TEXTURE_2D, depth); gl.uniform1i(gl.getUniformLocation(q, 's'), 0);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            """);
        Assert.Equal("36053", page.Run("String(status)"));
        Assert.Equal("0,0,0,255", page.Run("pixel(1, 4)"));       // inside the shadow
        Assert.Equal("255,255,0,255", page.Run("pixel(6, 4)"));   // lit
    }

    [Fact]
    public void AComparisonSamplerTestsTheReferenceAgainstTheStoredDepth()
    {
        using var page = Page(8);
        page.Run(DepthPass + """
            var color = target(4, 4, gl.RGBA8, gl.RGBA, gl.UNSIGNED_BYTE);
            gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.DEPTH_ATTACHMENT, gl.TEXTURE_2D, depth, 0);
            gl.viewport(0, 0, 4, 4);
            gl.enable(gl.DEPTH_TEST); gl.depthFunc(gl.LESS); gl.clear(gl.DEPTH_BUFFER_BIT | gl.COLOR_BUFFER_BIT);
            var p = program('attribute vec2 a_pos; void main() { gl_Position = vec4(a_pos.x * 0.5 - 0.5, a_pos.y, -0.5, 1.0); }', COLOR_FS);
            attribute(p, 'a_pos', QUAD, 2);
            gl.uniform4f(gl.getUniformLocation(p, 'u_color'), 1, 1, 1, 1);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            gl.bindFramebuffer(gl.FRAMEBUFFER, null); gl.viewport(0, 0, 8, 8); gl.disable(gl.DEPTH_TEST);
            gl.bindTexture(gl.TEXTURE_2D, depth);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_COMPARE_MODE, gl.COMPARE_REF_TO_TEXTURE);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_COMPARE_FUNC, gl.LEQUAL);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR); gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
            var q = es3('uniform sampler2DShadow s; out vec4 c; void main() { float lit = texture(s, vec3(gl_FragCoord.xy / 8.0, 0.5)); c = vec4(lit, lit, lit, 1.0); }');
            attribute(q, 'a_pos', QUAD, 2);
            gl.activeTexture(gl.TEXTURE0); gl.uniform1i(gl.getUniformLocation(q, 's'), 0);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            """);
        Assert.Equal("0,0,0,255", page.Run("pixel(1, 4)"));       // the reference is behind the stored depth: shadowed
        Assert.Equal("255,255,255,255", page.Run("pixel(6, 4)"));   // nothing stored there: lit
        var edge = int.Parse(page.Run("pixel(3, 4)").Split(',')[0]);
        Assert.InRange(edge, 1, 254);   // across the edge the four-texel comparison blends
    }

    [Fact]
    public void ALinearFilterMakesADepthTextureIncomplete()
    {
        using var page = Page();
        page.Run(DepthPass + """
            gl.drawBuffers([gl.NONE]); gl.clearDepth(0.5); gl.clear(gl.DEPTH_BUFFER_BIT);
            gl.bindFramebuffer(gl.FRAMEBUFFER, null); gl.viewport(0, 0, 4, 4);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR); gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
            var q = es3('uniform sampler2D s; out vec4 c; void main() { c = vec4(texture(s, vec2(0.5)).rrr, 1.0); }');
            attribute(q, 'a_pos', QUAD, 2);
            gl.activeTexture(gl.TEXTURE0); gl.bindTexture(gl.TEXTURE_2D, depth); gl.uniform1i(gl.getUniformLocation(q, 's'), 0);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            """);
        Assert.Equal("0,0,0,255", page.Run("pixel(1, 1)"));
    }

    [Fact]
    public void ADepthStencilRenderbufferSupportsBothTests()
    {
        using var page = Page();
        page.Run("""
            var color = target(4, 4, gl.RGBA8, gl.RGBA, gl.UNSIGNED_BYTE);
            var rb = gl.createRenderbuffer(); gl.bindRenderbuffer(gl.RENDERBUFFER, rb);
            gl.renderbufferStorage(gl.RENDERBUFFER, gl.DEPTH24_STENCIL8, 4, 4);
            gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.DEPTH_STENCIL_ATTACHMENT, gl.RENDERBUFFER, rb);
            var status = gl.checkFramebufferStatus(gl.FRAMEBUFFER);
            gl.viewport(0, 0, 4, 4);
            gl.clearColor(0, 0, 0, 1); gl.clearStencil(0); gl.clear(gl.COLOR_BUFFER_BIT | gl.STENCIL_BUFFER_BIT);
            gl.enable(gl.STENCIL_TEST); gl.stencilFunc(gl.ALWAYS, 1, 0xff); gl.stencilOp(gl.REPLACE, gl.REPLACE, gl.REPLACE);
            """ + Fillish("0, 0, 1, 1") + """
            gl.stencilFunc(gl.EQUAL, 1, 0xff); gl.stencilOp(gl.KEEP, gl.KEEP, gl.KEEP);
            gl.uniform4f(gl.getUniformLocation(p, 'u_color'), 1, 0, 0, 1);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            """);
        Assert.Equal("36053", page.Run("String(status)"));
        Assert.Equal("255,0,0,255", page.Run("""
            var out = new Uint8Array(4); gl.readPixels(1, 1, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, out); Array.from(out).join(',')
            """));
    }

    private static string Fillish(string color) =>
        $"var p = program(FLAT_VS, COLOR_FS); gl.uniform4f(gl.getUniformLocation(p, 'u_color'), {color}); attribute(p, 'a_pos', QUAD, 2); gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);";

    // ---- several colour attachments ----

    private const string ThreeTargets = """
        gl.getExtension('EXT_color_buffer_float');
        var tex = [];
        var fbo = gl.createFramebuffer(); gl.bindFramebuffer(gl.FRAMEBUFFER, fbo);
        for (var i = 0; i < 3; i++) {
            var t = gl.createTexture(); gl.bindTexture(gl.TEXTURE_2D, t);
            gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA32F, 2, 2, 0, gl.RGBA, gl.FLOAT, null);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST); gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
            gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0 + i, gl.TEXTURE_2D, t, 0);
            tex.push(t);
        }
        gl.viewport(0, 0, 2, 2);
        function readAttachment(i) { gl.readBuffer(gl.COLOR_ATTACHMENT0 + i); return floats(0, 0); }
        """;

    [Fact]
    public void ADrawWritesEachOutputToItsOwnAttachment()
    {
        using var page = Page();
        page.Run(ThreeTargets + """
            gl.drawBuffers([gl.COLOR_ATTACHMENT0, gl.COLOR_ATTACHMENT1, gl.COLOR_ATTACHMENT2]);
            var p = es3('layout(location = 0) out vec4 c0; layout(location = 1) out vec4 c1; layout(location = 2) out vec4 c2; void main() { c0 = vec4(1.0, 0.0, 0.0, 1.0); c1 = vec4(0.0, 2.5, 0.0, 1.0); c2 = vec4(0.0, 0.0, -3.0, 1.0); }');
            attribute(p, 'a_pos', QUAD, 2);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            var status = gl.checkFramebufferStatus(gl.FRAMEBUFFER);
            """);
        Assert.Equal("36053", page.Run("String(status)"));
        Assert.Equal("1,0,0,1", page.Run("readAttachment(0)"));
        Assert.Equal("0,2.5,0,1", page.Run("readAttachment(1)"));
        Assert.Equal("0,0,-3,1", page.Run("readAttachment(2)"));
        Assert.Equal("0", page.Run("String(gl.getError())"));
    }

    [Fact]
    public void AnOutputNotInTheDrawBuffersGoesNowhere()
    {
        using var page = Page();
        page.Run(ThreeTargets + """
            gl.drawBuffers([gl.COLOR_ATTACHMENT0, gl.COLOR_ATTACHMENT1, gl.COLOR_ATTACHMENT2]);
            gl.clearBufferfv(gl.COLOR, 1, [9, 9, 9, 9]);
            gl.drawBuffers([gl.COLOR_ATTACHMENT0, gl.NONE, gl.COLOR_ATTACHMENT2]);
            var p = es3('layout(location = 0) out vec4 c0; layout(location = 1) out vec4 c1; layout(location = 2) out vec4 c2; void main() { c0 = vec4(1.0); c1 = vec4(2.0); c2 = vec4(3.0); }');
            attribute(p, 'a_pos', QUAD, 2);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            """);
        Assert.Equal("1,1,1,1", page.Run("readAttachment(0)"));
        Assert.Equal("9,9,9,9", page.Run("readAttachment(1)"));
        Assert.Equal("3,3,3,3", page.Run("readAttachment(2)"));
    }

    [Fact]
    public void GlFragDataFillsTheAttachmentsOfAWebGl1Shader()
    {
        using var page = Page();
        page.Run(ThreeTargets + """
            var ext = gl.getExtension('WEBGL_draw_buffers');
            ext.drawBuffersWEBGL([ext.COLOR_ATTACHMENT0_WEBGL, ext.COLOR_ATTACHMENT1_WEBGL]);
            var p = program(FLAT_VS, '#extension GL_EXT_draw_buffers : require\nprecision highp float; void main() { gl_FragData[0] = vec4(4.0, 0.0, 0.0, 1.0); gl_FragData[1] = vec4(0.0, 5.0, 0.0, 1.0); }');
            attribute(p, 'a_pos', QUAD, 2);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            """);
        Assert.Equal("4,0,0,1", page.Run("readAttachment(0)"));
        Assert.Equal("0,5,0,1", page.Run("readAttachment(1)"));
        Assert.Equal("0,0,0,0", page.Run("readAttachment(2)"));
        Assert.Equal("4", page.Run("String(ext.MAX_DRAW_BUFFERS_WEBGL)"));
    }

    [Fact]
    public void ClearingClearsEveryDrawBufferAndTheColorMaskStillApplies()
    {
        using var page = Page();
        page.Run(ThreeTargets + """
            gl.drawBuffers([gl.COLOR_ATTACHMENT0, gl.COLOR_ATTACHMENT1, gl.NONE]);
            gl.clearColor(1, 0.5, 0.25, 1); gl.colorMask(true, false, true, true); gl.clear(gl.COLOR_BUFFER_BIT);
            """);
        Assert.Equal("1,0,0.25,1", page.Run("readAttachment(0)"));
        Assert.Equal("1,0,0.25,1", page.Run("readAttachment(1)"));
        Assert.Equal("0,0,0,0", page.Run("readAttachment(2)"));
    }

    [Fact]
    public void DrawBuffersRejectsAnAttachmentInTheWrongSlot()
    {
        using var page = Page();
        page.Run(ThreeTargets);
        Assert.Equal("1282", page.Run("gl.drawBuffers([gl.COLOR_ATTACHMENT1]); String(gl.getError())"));
        Assert.Equal("1282", page.Run("gl.drawBuffers([gl.BACK]); String(gl.getError())"));
        Assert.Equal("0", page.Run("gl.drawBuffers([gl.COLOR_ATTACHMENT0, gl.NONE, gl.COLOR_ATTACHMENT2]); String(gl.getError())"));
        Assert.Equal("36064,0,36066", page.Run("[gl.getParameter(gl.DRAW_BUFFER0), gl.getParameter(gl.DRAW_BUFFER1), gl.getParameter(gl.DRAW_BUFFER2)].join()"));
    }

    [Fact]
    public void AttachmentsOfDifferentSizesAreIncomplete()
    {
        using var page = Page();
        page.Run("""
            var a = target(2, 2, gl.RGBA8, gl.RGBA, gl.UNSIGNED_BYTE);
            var big = gl.createTexture(); gl.bindTexture(gl.TEXTURE_2D, big);
            gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, 4, 4, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
            gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT1, gl.TEXTURE_2D, big, 0);
            """);
        Assert.Equal("36057", page.Run("String(gl.checkFramebufferStatus(gl.FRAMEBUFFER))"));   // INCOMPLETE_DIMENSIONS
        Assert.Equal("36055", page.Run("gl.bindFramebuffer(gl.FRAMEBUFFER, gl.createFramebuffer()); String(gl.checkFramebufferStatus(gl.FRAMEBUFFER))"));   // MISSING_ATTACHMENT
    }

    [Fact]
    public void BlittingBetweenFloatFramebuffersCopiesTheValues()
    {
        using var page = Page();
        page.Run("""
            gl.getExtension('EXT_color_buffer_float');
            var a = target(2, 2, gl.RGBA32F, gl.RGBA, gl.FLOAT);
            gl.clearBufferfv(gl.COLOR, 0, [5.5, -6.5, 7.5, 8.5]);
            var b = target(2, 2, gl.RGBA32F, gl.RGBA, gl.FLOAT);
            gl.bindFramebuffer(gl.READ_FRAMEBUFFER, a.f); gl.bindFramebuffer(gl.DRAW_FRAMEBUFFER, b.f);
            gl.blitFramebuffer(0, 0, 2, 2, 0, 0, 2, 2, gl.COLOR_BUFFER_BIT, gl.NEAREST);
            gl.bindFramebuffer(gl.FRAMEBUFFER, b.f);
            """);
        Assert.Equal("5.5,-6.5,7.5,8.5", page.Run("floats(1, 1)"));
    }

    [Fact]
    public void AttachmentQueriesReportTheComponentType()
    {
        using var page = Page();
        page.Run("gl.getExtension('EXT_color_buffer_float'); var rt = target(2, 2, gl.RGBA32F, gl.RGBA, gl.FLOAT);");
        Assert.Equal("5126", page.Run("String(gl.getFramebufferAttachmentParameter(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.FRAMEBUFFER_ATTACHMENT_COMPONENT_TYPE))"));   // FLOAT
        Assert.Equal("true", page.Run("String(gl.getFramebufferAttachmentParameter(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.FRAMEBUFFER_ATTACHMENT_OBJECT_NAME) === rt.t)"));
        Assert.Equal("5890", page.Run("String(gl.getFramebufferAttachmentParameter(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.FRAMEBUFFER_ATTACHMENT_OBJECT_TYPE))"));   // TEXTURE
    }
}
