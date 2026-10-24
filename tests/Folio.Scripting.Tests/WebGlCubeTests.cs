using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

/// <summary>Cube maps (#637): the six faces, a lookup by direction, seamless filtering, mip levels and rendering into a face.</summary>
public class WebGlCubeTests
{
    // One flat colour per face: +X red, -X cyan, +Y green, -Y magenta, +Z blue, -Z yellow.
    private const string Faces = """
        var COLORS = [[255, 0, 0], [0, 255, 255], [0, 255, 0], [255, 0, 255], [0, 0, 255], [255, 255, 0]];
        function solid(size, c) {
            var d = new Uint8Array(size * size * 4);
            for (var i = 0; i < size * size; i++) { d[i * 4] = c[0]; d[i * 4 + 1] = c[1]; d[i * 4 + 2] = c[2]; d[i * 4 + 3] = 255; }
            return d;
        }
        function cube(size, filter) {
            var t = gl.createTexture(); gl.bindTexture(gl.TEXTURE_CUBE_MAP, t);
            for (var f = 0; f < 6; f++)
                gl.texImage2D(gl.TEXTURE_CUBE_MAP_POSITIVE_X + f, 0, gl.RGBA, size, size, 0, gl.RGBA, gl.UNSIGNED_BYTE, solid(size, COLORS[f]));
            gl.texParameteri(gl.TEXTURE_CUBE_MAP, gl.TEXTURE_MIN_FILTER, filter || gl.NEAREST);
            gl.texParameteri(gl.TEXTURE_CUBE_MAP, gl.TEXTURE_MAG_FILTER, filter || gl.NEAREST);
            return t;
        }
        // Looks the cube up in the direction given by a uniform and paints the pixel with it.
        function lookup(dir) {
            var p = program('attribute vec2 a_pos; void main() { gl_Position = vec4(a_pos, 0.0, 1.0); }',
                'precision mediump float; uniform samplerCube s; uniform vec3 dir; void main() { gl_FragColor = textureCube(s, dir); }');
            attribute(p, 'a_pos', QUAD, 2);
            gl.uniform1i(gl.getUniformLocation(p, 's'), 0);
            gl.uniform3f(gl.getUniformLocation(p, 'dir'), dir[0], dir[1], dir[2]);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            return pixel(2, 2);
        }
        """;

    private static WebGlPipelineTests.GlPage Page()
    {
        var page = new WebGlPipelineTests.GlPage(4);
        page.Run(Faces);
        return page;
    }

    [Fact]
    public void ADirectionPicksTheFaceItLeavesThrough()
    {
        using var page = Page();
        page.Run("cube(2);");
        Assert.Equal("255,0,0,255", page.Run("lookup([1, 0.2, -0.1])"));      // +X
        Assert.Equal("0,255,255,255", page.Run("lookup([-1, 0.3, 0.2])"));    // -X
        Assert.Equal("0,255,0,255", page.Run("lookup([0.1, 1, 0.3])"));       // +Y
        Assert.Equal("255,0,255,255", page.Run("lookup([-0.2, -1, 0.1])"));   // -Y
        Assert.Equal("0,0,255,255", page.Run("lookup([0.2, 0.1, 1])"));       // +Z
        Assert.Equal("255,255,0,255", page.Run("lookup([0.1, -0.3, -1])"));   // -Z
        Assert.Equal("0", page.Run("String(gl.getError())"));
    }

    [Fact]
    public void AnImageOnAFaceIsFoundWhereTheFaceCoordinatesSayItIs()
    {
        using var page = Page();
        // The +Z face (looking down -Z toward +Z): s runs along +X, t along -Y; a 2x2 image with a different colour in each texel.
        page.Run("""
            var t = cube(2);
            gl.texImage2D(gl.TEXTURE_CUBE_MAP_POSITIVE_Z, 0, gl.RGBA, 2, 2, 0, gl.RGBA, gl.UNSIGNED_BYTE,
                new Uint8Array([255, 0, 0, 255,  0, 255, 0, 255,   0, 0, 255, 255,  255, 255, 255, 255]));
            """);
        Assert.Equal("255,0,0,255", page.Run("lookup([-0.5, 0.5, 1])"));   // s < 0.5 (x negative), t < 0.5 (y positive): texel (0,0)
        Assert.Equal("0,255,0,255", page.Run("lookup([0.5, 0.5, 1])"));    // texel (1,0)
        Assert.Equal("0,0,255,255", page.Run("lookup([-0.5, -0.5, 1])"));  // texel (0,1)
        Assert.Equal("255,255,255,255", page.Run("lookup([0.5, -0.5, 1])"));
    }

    [Fact]
    public void LinearFilteringBlendsAcrossTheEdgeBetweenTwoFaces()
    {
        using var page = Page();
        page.Run("cube(2, gl.LINEAR);");
        // On the +X face, at its +Z edge (s = 0 there): a seamless lookup mixes in the +Z face's blue with the +X face's red.
        var seam = page.Run("lookup([1, 0, 0.9999])").Split(',').Select(int.Parse).ToArray();
        Assert.True(seam[0] > 30 && seam[2] > 30, string.Join(",", seam));
        // Well inside a face it is the face's own colour.
        Assert.Equal("255,0,0,255", page.Run("lookup([1, 0, 0])"));
    }

    [Fact]
    public void MipLevelsOfACubeMapAreGeneratedForEveryFace()
    {
        using var page = Page();
        page.Run("""
            var t = cube(4, gl.NEAREST_MIPMAP_NEAREST);
            gl.texParameteri(gl.TEXTURE_CUBE_MAP, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
            gl.generateMipmap(gl.TEXTURE_CUBE_MAP);
            """);
        Assert.Equal("0", page.Run("String(gl.getError())"));
        Assert.Equal("255,0,0,255", page.Run("lookup([1, 0.1, 0.1])"));
        // Replace level 2 (1x1) of the -Y face and read it back with an explicit level.
        page.Run("""
            gl.texImage2D(gl.TEXTURE_CUBE_MAP_NEGATIVE_Y, 2, gl.RGBA, 1, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array([9, 99, 199, 255]));
            var q = program('#version 300 es\nin vec2 a_pos; void main() { gl_Position = vec4(a_pos, 0.0, 1.0); }',
                '#version 300 es\nprecision mediump float; uniform samplerCube s; out vec4 c; void main() { c = textureLod(s, vec3(0.1, -1.0, 0.1), 2.0); }');
            attribute(q, 'a_pos', QUAD, 2);
            gl.uniform1i(gl.getUniformLocation(q, 's'), 0);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            """);
        Assert.Equal("9,99,199,255", page.Run("pixel(2, 2)"));
        Assert.Equal("0", page.Run("String(gl.getError())"));
    }

    [Fact]
    public void ACubeMapMissingAFaceIsIncompleteAndReadsBlack()
    {
        using var page = Page();
        page.Run("""
            var t = gl.createTexture(); gl.bindTexture(gl.TEXTURE_CUBE_MAP, t);
            for (var f = 0; f < 5; f++)
                gl.texImage2D(gl.TEXTURE_CUBE_MAP_POSITIVE_X + f, 0, gl.RGBA, 2, 2, 0, gl.RGBA, gl.UNSIGNED_BYTE, solid(2, COLORS[f]));
            gl.texParameteri(gl.TEXTURE_CUBE_MAP, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
            gl.texParameteri(gl.TEXTURE_CUBE_MAP, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
            """);
        Assert.Equal("0,0,0,255", page.Run("lookup([1, 0, 0])"));
    }

    [Fact]
    public void CubeFacesMustBeSquare()
    {
        using var page = Page();
        Assert.Equal("1281", page.Run("""
            var t = gl.createTexture(); gl.bindTexture(gl.TEXTURE_CUBE_MAP, t);
            gl.texImage2D(gl.TEXTURE_CUBE_MAP_POSITIVE_X, 0, gl.RGBA, 2, 4, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
            String(gl.getError())
            """));
    }

    [Fact]
    public void ATextureKeepsTheTargetItWasFirstBoundTo()
    {
        using var page = Page();
        Assert.Equal("1282,true", page.Run("""
            var t = gl.createTexture(); gl.bindTexture(gl.TEXTURE_2D, t);
            gl.bindTexture(gl.TEXTURE_CUBE_MAP, t);
            var error = gl.getError();
            [error, gl.getParameter(gl.TEXTURE_BINDING_CUBE_MAP) === null].join()
            """));
    }

    [Fact]
    public void ADrawCanRenderIntoOneFaceOfACubeMap()
    {
        using var page = Page();
        page.Run("""
            var t = cube(4);
            var f = gl.createFramebuffer(); gl.bindFramebuffer(gl.FRAMEBUFFER, f);
            gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_CUBE_MAP_NEGATIVE_Z, t, 0);
            var status = gl.checkFramebufferStatus(gl.FRAMEBUFFER);
            gl.viewport(0, 0, 4, 4);
            gl.clearColor(0.5, 0.25, 1, 1); gl.clear(gl.COLOR_BUFFER_BIT);
            gl.bindFramebuffer(gl.FRAMEBUFFER, null);
            gl.viewport(0, 0, 4, 4);
            """);
        Assert.Equal("36053", page.Run("String(status)"));
        Assert.Equal("128,64,255,255", page.Run("lookup([0.1, 0.1, -1])"));    // -Z was cleared
        Assert.Equal("0,0,255,255", page.Run("lookup([0.1, 0.1, 1])"));        // +Z untouched
    }

    [Fact]
    public void ADirectionComputedInTheShaderPicksTheMipLevelFromItsOwnSpread()
    {
        using var page = new WebGlPipelineTests.GlPage(8);
        page.Run(Faces);
        // A skybox-style lookup: the direction is a varying scaled in the shader (not a plain varying read), on a mipmapped cube.
        page.Run("""
            var t = gl.createTexture(); gl.bindTexture(gl.TEXTURE_CUBE_MAP, t);
            function faceLevels(f, c) {
                var sizes = [8, 4, 2, 1];
                for (var l = 0; l < 4; l++) {
                    var d = solid(sizes[l], l == 0 ? c : [l * 60, l * 60, l * 60]);
                    gl.texImage2D(gl.TEXTURE_CUBE_MAP_POSITIVE_X + f, l, gl.RGBA, sizes[l], sizes[l], 0, gl.RGBA, gl.UNSIGNED_BYTE, d);
                }
            }
            for (var f = 0; f < 6; f++) faceLevels(f, COLORS[f]);
            gl.texParameteri(gl.TEXTURE_CUBE_MAP, gl.TEXTURE_MIN_FILTER, gl.NEAREST_MIPMAP_NEAREST);
            gl.texParameteri(gl.TEXTURE_CUBE_MAP, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
            var p = program('attribute vec2 a_pos; varying vec2 v; void main() { v = a_pos; gl_Position = vec4(a_pos, 0.0, 1.0); }',
                'precision highp float; varying vec2 v; uniform samplerCube s; uniform float k; void main() { vec3 d = vec3(v * k, 1.0); gl_FragColor = textureCube(s, d); }');
            attribute(p, 'a_pos', QUAD, 2);
            gl.uniform1i(gl.getUniformLocation(p, 's'), 0);
            """);
        // k = 0: every pixel looks straight down +Z and the spread is zero: level 0, the face's own colour.
        page.Run("gl.uniform1f(gl.getUniformLocation(p, 'k'), 0.0); gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);");
        Assert.Equal("0,0,255,255", page.Run("pixel(3, 3)"));
        // Four texels of the face per pixel is level 2, whose texels are 120 grey.
        page.Run("gl.uniform1f(gl.getUniformLocation(p, 'k'), 4.0); gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);");
        Assert.Equal("120,120,120,255", page.Run("pixel(3, 3)"));
    }
}
