using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

/// <summary>Derivatives of any expression and the texture level chosen from a computed coordinate (#637): the neighbouring pixels of a 2x2 quad are shaded and compared.</summary>
public class WebGlDerivativeTests
{
    private const string Vertex = "attribute vec2 a_pos; varying vec2 v; void main() { v = a_pos * 0.5 + 0.5; gl_Position = vec4(a_pos, 0.0, 1.0); }";

    private static string Fragment(string body, string declarations = "") =>
        $"#extension GL_OES_standard_derivatives : enable\\nprecision highp float; varying vec2 v; {declarations} void main() {{ {body} }}";

    private static WebGlPipelineTests.GlPage Draw(string body, string declarations = "")
    {
        var page = new WebGlPipelineTests.GlPage(8);
        page.Run($$"""
            var p = program('{{Vertex}}', '{{Fragment(body, declarations)}}');
            attribute(p, 'a_pos', QUAD, 2);
            gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            """);
        return page;
    }

    private static int Red(WebGlPipelineTests.GlPage page, int x, int y) => int.Parse(page.Run($"pixel({x}, {y})").Split(',')[0]);

    private static int Green(WebGlPipelineTests.GlPage page, int x, int y) => int.Parse(page.Run($"pixel({x}, {y})").Split(',')[1]);

    [Fact]
    public void ADerivativeOfAnExpressionBuiltFromALocalIsTheDifferenceBetweenNeighbours()
    {
        // d = v.x squared; v.x is (x + 0.5) / 8 at the pixel centres, so the pair difference is exact.
        using var page = Draw("float d = v.x * v.x; gl_FragColor = vec4(dFdx(d) * 8.0, 0.0, 0.0, 1.0);");
        Assert.Equal(64, Red(page, 0, 3));    // (1.5^2 - 0.5^2) / 64 * 8
        Assert.Equal(64, Red(page, 1, 3));    // the pair shares one derivative
        Assert.Equal(191, Red(page, 2, 3));   // (3.5^2 - 2.5^2) / 64 * 8
        Assert.Equal(191, Red(page, 3, 3));
        Assert.Equal(255, Red(page, 7, 3));   // (7.5^2 - 6.5^2) / 64 * 8 = 1.75, clamped
    }

    [Fact]
    public void FwidthAddsTheChangeAlongBothAxes()
    {
        using var page = Draw("float d = v.x + 2.0 * v.y; gl_FragColor = vec4(fwidth(d) * 2.0, 0.0, 0.0, 1.0);");
        // |1/8| + |2/8| = 0.375, doubled, in every pixel.
        Assert.Equal(191, Red(page, 2, 2));
        Assert.Equal(191, Red(page, 5, 6));
    }

    [Fact]
    public void BothAxesAreAvailableInOneShader()
    {
        using var page = Draw("float d = v.x * v.y; gl_FragColor = vec4(dFdx(d) * 16.0, dFdy(d) * 16.0, 0.0, 1.0);");
        // pixel (1, 2): dFdx = v.y / 8 = 0.3125 / 8, dFdy = v.x / 8 = 0.1875 / 8
        Assert.InRange(Red(page, 1, 2), 158, 160);
        Assert.InRange(Green(page, 1, 2), 95, 97);
        // pixel (4, 5): dFdx = 5.5 / 64, dFdy = 4.5 / 64
        Assert.InRange(Red(page, 4, 5), 21 * 8 - 40, 255);
        Assert.InRange(Green(page, 4, 5), 100, 255);
    }

    [Fact]
    public void ADerivativeInsideAFunctionUsesTheFunctionsOwnArgument()
    {
        using var page = Draw("gl_FragColor = vec4(slope(v.x) * 8.0, 0.0, 0.0, 1.0);", "float slope(float x) { float y = x * x; return dFdx(y); }");
        Assert.Equal(64, Red(page, 0, 3));
        Assert.Equal(191, Red(page, 3, 3));
    }

    [Fact]
    public void SeveralDerivativeSitesEachGetTheirOwnNeighbours()
    {
        using var page = Draw("float a = v.x * v.x; float b = v.y * 3.0; gl_FragColor = vec4(dFdx(a) * 8.0, dFdy(b) * 2.0, abs(dFdx(a * a)) * 32.0, 1.0);");
        Assert.Equal(64, Red(page, 1, 4));                          // dFdx of x squared
        Assert.InRange(Green(page, 1, 4), 190, 192);                // dFdy of 3y = 3/8, doubled
        Assert.InRange(int.Parse(page.Run("pixel(1, 4)").Split(',')[2]), 1, 255);
    }

    [Fact]
    public void ADirectVaryingKeepsTheExactAnalyticDerivative()
    {
        using var page = Draw("gl_FragColor = vec4(dFdx(v.x) * 4.0, dFdy(v.y) * 4.0, 0.0, 1.0);");
        Assert.InRange(Red(page, 3, 3), 127, 129);     // 1/8, times 4
        Assert.InRange(Green(page, 3, 3), 127, 129);
    }

    [Fact]
    public void ATriangleEdgeStillGetsDerivativesFromPixelsOutsideIt()
    {
        using var page = new WebGlPipelineTests.GlPage(8);
        page.Run($$"""
            var p = program('{{Vertex}}', '{{Fragment("float d = v.x * 2.0; gl_FragColor = vec4(dFdx(d) * 2.0, 0.0, 0.0, 1.0);")}}');
            attribute(p, 'a_pos', new Float32Array([-1, -1, 1, -1, -1, 1]), 2);
            gl.drawArrays(gl.TRIANGLES, 0, 3);
            """);
        // On the diagonal edge the neighbour is outside the triangle; its varying is extrapolated, so the slope is the same everywhere.
        Assert.InRange(Red(page, 3, 3), 127, 129);   // 2 / 8, times 2
        Assert.InRange(Red(page, 0, 5), 127, 129);
    }

    // ---- the texture level chosen from a computed coordinate ----

    private const string MipTexture = """
        function level(l, size, r, g, b) {
            var data = new Uint8Array(size * size * 4);
            for (var i = 0; i < size * size; i++) { data[i * 4] = r; data[i * 4 + 1] = g; data[i * 4 + 2] = b; data[i * 4 + 3] = 255; }
            gl.texImage2D(gl.TEXTURE_2D, l, gl.RGBA, size, size, 0, gl.RGBA, gl.UNSIGNED_BYTE, data);
        }
        var t = gl.createTexture(); gl.bindTexture(gl.TEXTURE_2D, t);
        level(0, 8, 255, 0, 0); level(1, 4, 0, 255, 0); level(2, 2, 0, 0, 255); level(3, 1, 255, 255, 0);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST_MIPMAP_NEAREST);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
        """;

    private static string Lookup(string scale) =>
        $"var q = program('{Vertex}', 'precision highp float; varying vec2 v; uniform sampler2D s; void main() {{ vec2 uv = v * {scale}; gl_FragColor = texture2D(s, uv); }}'); attribute(q, 'a_pos', QUAD, 2); gl.uniform1i(gl.getUniformLocation(q, 's'), 0); gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);";

    [Fact]
    public void TheMipLevelFollowsTheScaleOfAComputedCoordinate()
    {
        using var page = new WebGlPipelineTests.GlPage(8);
        page.Run(MipTexture);
        // The coordinate advances 1 * scale / 8 per pixel across an 8-texel texture: 1 texel per pixel is level 0, 2 is level 1, 4 is level 2.
        page.Run(Lookup("1.0"));
        Assert.Equal("255,0,0,255", page.Run("pixel(3, 3)"));
        page.Run(Lookup("2.0"));
        Assert.Equal("0,255,0,255", page.Run("pixel(3, 3)"));
        page.Run(Lookup("4.0"));
        Assert.Equal("0,0,255,255", page.Run("pixel(3, 3)"));
        Assert.Equal("0", page.Run("String(gl.getError())"));
    }
}
