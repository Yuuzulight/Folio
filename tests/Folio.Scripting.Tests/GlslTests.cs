using System.Numerics;
using Folio.Scripting.WebGl;

namespace Folio.Scripting.Tests;

/// <summary>The GLSL ES front end and interpreter behind WebGL (#529): what shaders compute, and what a bad shader reports.</summary>
public class GlslTests
{
    private static GlslShader Compile(string source, GlslStage stage = GlslStage.Fragment)
    {
        var result = GlslCompiler.Compile(stage, source);
        Assert.True(result.Ok, result.InfoLog);
        return result.Shader!;
    }

    /// <summary>Runs the body of a fragment shader's main and returns gl_FragColor.</summary>
    private static Vector4 Frag(string body, string declarations = "")
    {
        var shader = Compile($"precision mediump float;\n{declarations}\nvoid main() {{\n{body}\n}}");
        var context = shader.CreateContext();
        shader.Main(context);
        return context.G[shader.FragOutputSlots[0]].C0;
    }

    private static void AssertNear(float expected, float actual, float tolerance = 1e-4f) =>
        Assert.True(MathF.Abs(expected - actual) <= tolerance, $"expected {expected}, got {actual}");

    private static void AssertNear(Vector4 expected, Vector4 actual, float tolerance = 1e-4f)
    {
        AssertNear(expected.X, actual.X, tolerance);
        AssertNear(expected.Y, actual.Y, tolerance);
        AssertNear(expected.Z, actual.Z, tolerance);
        AssertNear(expected.W, actual.W, tolerance);
    }

    private static string Error(string source, GlslStage stage = GlslStage.Fragment)
    {
        var result = GlslCompiler.Compile(stage, source);
        Assert.False(result.Ok, "expected a compile error");
        return result.InfoLog;
    }

    // ---- values and operators ----

    [Fact]
    public void ArithmeticFollowsPrecedenceAndScalarsSpreadOverVectors()
    {
        AssertNear(new Vector4(7, 7, 7, 7), Frag("gl_FragColor = vec4(1.0 + 2.0 * 3.0);"));
        AssertNear(new Vector4(2, 4, 6, 8), Frag("gl_FragColor = vec4(1.0, 2.0, 3.0, 4.0) * 2.0;"));
        AssertNear(new Vector4(0.5f, 1, 1.5f, 2), Frag("gl_FragColor = 1.0 / vec4(2.0, 1.0, 0.6666667, 0.5);"));
        AssertNear(new Vector4(3, 3, 3, 3), Frag("gl_FragColor = vec4(float(7 / 2));"));
        AssertNear(new Vector4(1, 1, 1, 1), Frag("gl_FragColor = vec4(float(7 - 3 * 2));"));
    }

    [Fact]
    public void IntegerDivisionTruncatesAndModuloIsAvailableInEs3()
    {
        AssertNear(new Vector4(-2, 0, 0, 1), Frag("int a = -7; int b = 3; gl_FragColor = vec4(float(a / b), 0.0, 0.0, 1.0);"));
        var shader = Compile("#version 300 es\nprecision mediump float;\nout vec4 o;\nvoid main() { o = vec4(float(17 % 5), float(1 << 3), float(6 & 3), 1.0); }");
        var context = shader.CreateContext();
        shader.Main(context);
        AssertNear(new Vector4(2, 8, 2, 1), context.G[shader.FragOutputSlots[0]].C0);
    }

    [Fact]
    public void ComparisonAndLogicProduceBooleansAndShortCircuit()
    {
        AssertNear(new Vector4(1, 0, 1, 1), Frag("gl_FragColor = vec4(float(1.0 < 2.0), float(3 >= 4), float(true && !false), float(false ^^ true));"));
        AssertNear(new Vector4(1, 0, 0, 0), Frag("float x = 0.0; bool r = true || (x++ > 0.0); gl_FragColor = vec4(float(r), x, 0.0, 0.0);"));
        AssertNear(new Vector4(1, 1, 0, 1), Frag("gl_FragColor = vec4(float(vec2(1.0) == vec2(1.0)), float(vec3(1.0, 2.0, 3.0) != vec3(1.0, 2.0, 4.0)), float(mat2(1.0) == mat2(2.0)), 1.0);"));
    }

    [Fact]
    public void SwizzlesReadAndWriteInAnyLetterSet()
    {
        AssertNear(new Vector4(3, 2, 1, 1), Frag("vec3 v = vec3(1.0, 2.0, 3.0); gl_FragColor = vec4(v.zyx, 1.0);"));
        AssertNear(new Vector4(0.5f, 0.25f, 0.75f, 1), Frag("vec4 c = vec4(0.5, 0.25, 0.75, 1.0); gl_FragColor = vec4(c.rgb, c.a);"));
        AssertNear(new Vector4(9, 8, 3, 4), Frag("vec4 v = vec4(1.0, 2.0, 3.0, 4.0); v.xy = vec2(9.0, 8.0); gl_FragColor = v;"));
        AssertNear(new Vector4(2, 2, 3, 1), Frag("vec4 v = vec4(1.0, 2.0, 3.0, 1.0); v.xy = v.yy; gl_FragColor = v;"));
        AssertNear(new Vector4(1, 5, 3, 4), Frag("vec4 v = vec4(1.0, 2.0, 3.0, 4.0); v[1] = 5.0; gl_FragColor = v;"));
        AssertNear(new Vector4(7, 2, 3, 4), Frag("vec4 v = vec4(1.0, 2.0, 3.0, 4.0); v.x += 6.0; gl_FragColor = v;"));
    }

    [Fact]
    public void ConstructorsTakeComponentsFromTheirArguments()
    {
        AssertNear(new Vector4(1, 2, 3, 4), Frag("gl_FragColor = vec4(vec2(1.0, 2.0), 3.0, 4.0);"));
        AssertNear(new Vector4(1, 2, 3, 4), Frag("gl_FragColor = vec4(1.0, vec3(2.0, 3.0, 4.0));"));
        AssertNear(new Vector4(1, 2, 3, 3), Frag("vec3 a = vec3(1.0, 2.0, 3.0); gl_FragColor = vec4(a, 3.0);"));
        AssertNear(new Vector4(1, 0, 2, 1), Frag("gl_FragColor = vec4(float(true), float(0), float(2), float(-3.7 < 0.0));"));
        AssertNear(new Vector4(-3, 2, 0, 0), Frag("gl_FragColor = vec4(float(int(-3.7)), float(int(2.9)), 0.0, 0.0);"));
        AssertNear(new Vector4(1, 0, 0, 1), Frag("gl_FragColor = vec4(float(bool(5.0)), float(bool(0.0)), 0.0, 1.0);"));
    }

    [Fact]
    public void MatricesMultiplyVectorsAndEachOtherColumnMajor()
    {
        // mat2(a, b, c, d) fills columns: [a b] and [c d].
        AssertNear(new Vector4(1 * 5 + 3 * 6, 2 * 5 + 4 * 6, 0, 1), Frag("mat2 m = mat2(1.0, 2.0, 3.0, 4.0); vec2 r = m * vec2(5.0, 6.0); gl_FragColor = vec4(r, 0.0, 1.0);"));
        AssertNear(new Vector4(1 * 5 + 2 * 6, 3 * 5 + 4 * 6, 0, 1), Frag("mat2 m = mat2(1.0, 2.0, 3.0, 4.0); vec2 r = vec2(5.0, 6.0) * m; gl_FragColor = vec4(r, 0.0, 1.0);"));
        AssertNear(new Vector4(2, 3, 0, 1), Frag("mat3 m = mat3(2.0); vec3 r = m * vec3(1.0, 1.5, 0.0); gl_FragColor = vec4(r.xy, 0.0, 1.0);"));
        AssertNear(new Vector4(7, 10, 15, 22), Frag("mat2 a = mat2(1.0, 2.0, 3.0, 4.0); mat2 p = a * a; gl_FragColor = vec4(p[0], p[1]);"));
        AssertNear(new Vector4(3, 4, 9, 9), Frag("mat2 m = mat2(1.0, 2.0, 3.0, 4.0); m[1] = vec2(9.0); gl_FragColor = vec4(m[0] + vec2(2.0, 2.0), m[1]);"));
        AssertNear(new Vector4(4, 0, 0, 0), Frag("mat4 m = mat4(1.0); m[2][2] = 4.0; gl_FragColor = vec4(m[2][2], m[0][1], m[3][3] - 1.0, 0.0);"));
        AssertNear(new Vector4(1, 3, 2, 4), Frag("mat2 t = transpose(mat2(1.0, 2.0, 3.0, 4.0)); gl_FragColor = vec4(t[0], t[1]);"));
        AssertNear(new Vector4(-2, 0, 0, 0), Frag("gl_FragColor = vec4(determinant(mat2(1.0, 2.0, 3.0, 4.0)), 0.0, 0.0, 0.0);"));
        AssertNear(new Vector4(1, 1, 0, 1), Frag("mat4 m = mat4(2.0, 0.0, 0.0, 0.0, 0.0, 4.0, 0.0, 0.0, 0.0, 0.0, 8.0, 0.0, 0.0, 0.0, 0.0, 1.0); mat4 i = inverse(m); gl_FragColor = vec4(m[0][0] * i[0][0], m[1][1] * i[1][1], i[2][2] * 8.0 - 1.0, 1.0);"));
    }

    [Fact]
    public void ControlFlowRunsLoopsBranchesAndJumps()
    {
        AssertNear(new Vector4(45, 0, 0, 0), Frag("float s = 0.0; for (int i = 0; i < 10; i++) { s += float(i); } gl_FragColor = vec4(s, 0.0, 0.0, 0.0);"));
        AssertNear(new Vector4(5, 0, 0, 0), Frag("float s = 0.0; for (int i = 0; i < 100; i++) { if (i == 5) break; s += 1.0; } gl_FragColor = vec4(s, 0.0, 0.0, 0.0);"));
        AssertNear(new Vector4(25, 0, 0, 0), Frag("float s = 0.0; for (int i = 0; i < 10; i++) { if (i - (i / 2) * 2 == 0) continue; s += float(i); } gl_FragColor = vec4(s, 0.0, 0.0, 0.0);"));
        AssertNear(new Vector4(8, 0, 0, 0), Frag("float x = 1.0; while (x < 5.0) { x *= 2.0; } gl_FragColor = vec4(x, 0.0, 0.0, 0.0);"));
        AssertNear(new Vector4(3, 0, 0, 0), Frag("float x = 0.0; do { x += 1.0; } while (x < 3.0); gl_FragColor = vec4(x, 0.0, 0.0, 0.0);"));
        AssertNear(new Vector4(2, 0, 0, 0), Frag("float x = 0.0; if (x > 1.0) x = 1.0; else if (x < 1.0) x = 2.0; else x = 3.0; gl_FragColor = vec4(x, 0.0, 0.0, 0.0);"));
        AssertNear(new Vector4(5, 6, 0, 0), Frag("float x = true ? 5.0 : 6.0; float y = false ? 5.0 : 6.0; gl_FragColor = vec4(x, y, 0.0, 0.0);"));
    }

    [Fact]
    public void FunctionsTakeInOutAndInoutParametersAndReturnValues()
    {
        const string declarations = """
            float square(float x) { return x * x; }
            void split(in vec2 v, out float a, inout float b) { a = v.x; b += v.y; }
            vec3 twice(vec3 v) { return v * 2.0; }
            float fact(int n) { float r = 1.0; for (int i = 2; i <= 6; i++) { if (i > n) break; r *= float(i); } return r; }
            int pick(int a) { return a + 1; }
            float pick(float a) { return a + 0.5; }
            """;
        AssertNear(new Vector4(9, 0, 0, 0), Frag("gl_FragColor = vec4(square(3.0), 0.0, 0.0, 0.0);", declarations));
        AssertNear(new Vector4(4, 7, 0, 0), Frag("float a; float b = 5.0; split(vec2(4.0, 2.0), a, b); gl_FragColor = vec4(a, b, 0.0, 0.0);", declarations));
        AssertNear(new Vector4(2, 4, 6, 0), Frag("gl_FragColor = vec4(twice(vec3(1.0, 2.0, 3.0)), 0.0);", declarations));
        AssertNear(new Vector4(120, 0, 0, 0), Frag("gl_FragColor = vec4(fact(5), 0.0, 0.0, 0.0);", declarations));
        AssertNear(new Vector4(4, 1.5f, 0, 0), Frag("gl_FragColor = vec4(float(pick(3)), pick(1.0), 0.0, 0.0);", declarations));
    }

    [Fact]
    public void StructsAndArraysAreValuesWithFieldsAndElements()
    {
        const string declarations = """
            struct Light { vec3 color; float power; };
            uniform float weights[3];
            float sum3(float a[3]) { return a[0] + a[1] + a[2]; }
            """;
        AssertNear(new Vector4(1, 2, 3, 4), Frag("Light l = Light(vec3(1.0, 2.0, 3.0), 4.0); gl_FragColor = vec4(l.color, l.power);", declarations));
        AssertNear(new Vector4(1, 9, 3, 4), Frag("Light l = Light(vec3(1.0, 2.0, 3.0), 4.0); l.color.y = 9.0; gl_FragColor = vec4(l.color, l.power);", declarations));
        AssertNear(new Vector4(5, 6, 0, 0), Frag("Light a = Light(vec3(0.0), 5.0); Light b = a; b.power = 6.0; gl_FragColor = vec4(a.power, b.power, 0.0, 0.0);", declarations));
        AssertNear(new Vector4(6, 3, 0, 0), Frag("float a[3]; a[0] = 1.0; a[1] = 2.0; a[2] = 3.0; gl_FragColor = vec4(sum3(a), float(a.length()), 0.0, 0.0);", declarations));
        AssertNear(new Vector4(0, 1, 0, 0), Frag("float a[2] = float[2](0.0, 1.0); gl_FragColor = vec4(a[0], a[1], 0.0, 0.0);"));
    }

    [Fact]
    public void BuiltinFunctionsComputeWhatTheSpecificationSays()
    {
        AssertNear(new Vector4(0, 1, 0, 0), Frag("gl_FragColor = vec4(sin(0.0), cos(0.0), 0.0, 0.0);"));
        AssertNear(new Vector4(8, 3, 0.5f, 2), Frag("gl_FragColor = vec4(pow(2.0, 3.0), sqrt(9.0), fract(2.5), abs(-2.0));"));
        AssertNear(new Vector4(3, -1, 0.25f, 4), Frag("gl_FragColor = vec4(max(3.0, 1.0), min(-1.0, 2.0), mod(5.25, 5.0), ceil(3.2));"));
        AssertNear(new Vector4(0.5f, 1, 0, 0.5f), Frag("gl_FragColor = vec4(clamp(0.5, 0.0, 1.0), clamp(2.0, 0.0, 1.0), clamp(-1.0, 0.0, 1.0), mix(0.0, 1.0, 0.5));"));
        AssertNear(new Vector4(0, 1, 0.5f, 0), Frag("gl_FragColor = vec4(step(1.0, 0.5), step(1.0, 1.0), smoothstep(0.0, 1.0, 0.5), smoothstep(0.0, 1.0, -1.0));"));
        AssertNear(new Vector4(5, 5, 9, 0), Frag("gl_FragColor = vec4(length(vec2(3.0, 4.0)), distance(vec2(0.0), vec2(3.0, 4.0)), dot(vec3(1.0, 2.0, 3.0), vec3(1.0, 1.0, 2.0)) + 0.0, 0.0);"));
        AssertNear(new Vector4(0, 0, 1, 0), Frag("gl_FragColor = vec4(cross(vec3(1.0, 0.0, 0.0), vec3(0.0, 1.0, 0.0)), 0.0);"));
        AssertNear(new Vector4(0.6f, 0.8f, 0, 0), Frag("gl_FragColor = vec4(normalize(vec2(3.0, 4.0)), 0.0, 0.0);"));
        AssertNear(new Vector4(1, 1, 0, 0), Frag("gl_FragColor = vec4(reflect(vec2(1.0, -1.0), vec2(0.0, 1.0)), 0.0, 0.0);"));
        AssertNear(new Vector4(0.5f, 0.25f, 0.75f, 0), Frag("gl_FragColor = vec4(mix(vec3(0.0, 0.0, 1.0), vec3(1.0, 0.5, 0.5), vec3(0.5, 0.5, 0.5)).xyz, 0.0);"));
        AssertNear(new Vector4(1, 0, 1, 0), Frag("bvec3 b = lessThan(vec3(1.0, 5.0, 0.0), vec3(2.0, 3.0, 1.0)); gl_FragColor = vec4(float(b.x), float(b.y), float(b.z), float(any(bvec2(false, false))));"));
        AssertNear(new Vector4(1, 1, 0, 0), Frag("gl_FragColor = vec4(float(all(bvec2(true, true))), float(any(bvec3(false, true, false))), float(all(bvec2(true, false))), 0.0);"));
        AssertNear(new Vector4(-1, 0, 1, 0), Frag("gl_FragColor = vec4(sign(-3.0), sign(0.0), sign(2.0), 0.0);"));
        AssertNear(new Vector4(2, 3, 0, 0), Frag("gl_FragColor = vec4(floor(2.9), round(2.6), 0.0, 0.0);"));
        AssertNear(new Vector4(MathF.PI, 90, 0, 0), Frag("gl_FragColor = vec4(radians(180.0), degrees(MathPI), 0.0, 0.0);".Replace("MathPI", "1.5707964")));
    }

    [Fact]
    public void ThePreprocessorDefinesMacrosAndSkipsInactiveBranches()
    {
        AssertNear(new Vector4(3, 8, 0, 0), Frag("gl_FragColor = vec4(float(TWO + 1), SQUARE(2.0 + 0.0) * 2.0, 0.0, 0.0);", "#define TWO 2\n#define SQUARE(x) ((x) * (x))\n"));
        AssertNear(new Vector4(1, 0, 0, 0), Frag("gl_FragColor = vec4(1.0, 0.0, 0.0, 0.0);", "#ifdef GL_ES\n#else\n#error not ES\n#endif\n"));
        AssertNear(new Vector4(2, 0, 0, 0), Frag("gl_FragColor = vec4(VALUE, 0.0, 0.0, 0.0);", "#define MODE 2\n#if MODE == 1\n#define VALUE 1.0\n#elif MODE == 2\n#define VALUE 2.0\n#else\n#define VALUE 3.0\n#endif\n"));
        AssertNear(new Vector4(4, 0, 0, 0), Frag("float a[N]; a[N - 1] = 4.0; gl_FragColor = vec4(a[3], 0.0, 0.0, 0.0);", "const int N = 4;\n"));
    }

    [Fact]
    public void DiscardEndsTheInvocationAndIsRecorded()
    {
        var shader = Compile("precision mediump float;\nvoid main() { gl_FragColor = vec4(1.0); if (gl_FragCoord.x < 0.0) discard; }");
        var context = shader.CreateContext();
        context.G[shader.FragCoordSlot] = new Val(-1, 0, 0, 1);
        shader.Main(context);
        Assert.True(context.Discarded);
        context.G[shader.FragCoordSlot] = new Val(5, 0, 0, 1);
        shader.Main(context);
        Assert.False(context.Discarded);
    }

    [Fact]
    public void UniformsAttributesAndVaryingsAreListedWithTheirTypes()
    {
        var shader = Compile(
            "attribute vec3 a_pos; attribute vec2 a_uv; uniform mat4 u_mvp; uniform float u_weights[4]; varying vec2 v_uv; void main() { v_uv = a_uv; gl_Position = u_mvp * vec4(a_pos, 1.0); }",
            GlslStage.Vertex);
        Assert.Equal(["a_pos", "a_uv"], shader.Attributes.Select(a => a.Name));
        Assert.Equal(["u_mvp", "u_weights"], shader.Uniforms.Select(u => u.Name));
        Assert.Equal(GlslBase.Mat4, shader.Uniforms.First().Type.Base);
        Assert.Equal(4, shader.Uniforms.Last().Type.ArraySize);
        Assert.Contains(shader.Variables, v => v.Name == "v_uv" && v.Storage == GlslStorage.VaryingOut);
    }

    [Fact]
    public void AVertexShaderComputesPositionFromItsUniformsAndAttributes()
    {
        var shader = Compile(
            "attribute vec2 a; uniform vec2 offset; uniform float scale; void main() { gl_Position = vec4(a * scale + offset, 0.0, 1.0); gl_PointSize = 3.0; }",
            GlslStage.Vertex);
        var context = shader.CreateContext();
        context.G[shader.Attributes.First().Slot] = new Val(1, 2, 0, 1);
        context.G[shader.Uniforms.First(u => u.Name == "offset").Slot] = new Val(10, 20, 0, 0);
        context.G[shader.Uniforms.First(u => u.Name == "scale").Slot] = new Val(2);
        shader.Main(context);
        AssertNear(new Vector4(12, 24, 0, 1), context.G[shader.PositionSlot].C0);
        AssertNear(3, context.G[shader.PointSizeSlot].X);
    }

    [Fact]
    public void ARunawayLoopStopsAtTheDeadline()
    {
        var shader = Compile("precision mediump float;\nvoid main() { float x = 0.0; for (int i = 0; i < 100000000; i++) { x += 1.0; } gl_FragColor = vec4(x); }");
        var context = shader.CreateContext();
        context.Deadline = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency / 20;
        Assert.Throws<ShaderTimeoutException>(() => shader.Main(context));
    }

    // ---- what a bad shader reports ----

    private static void AssertLog(string pattern, string source)
    {
        var log = Error(source);
        Assert.True(System.Text.RegularExpressions.Regex.IsMatch(log, pattern), $"pattern '{pattern}' not in log: {log}");
    }

    [Fact]
    public void ErrorsNameTheLineAndWhatIsWrong()
    {
        AssertLog(@"^ERROR: 0:3: .*undeclared identifier", "precision mediump float;\nvoid main() {\n  gl_FragColor = vec4(missing);\n}");
        AssertLog(@"^ERROR: 0:2: ", "void main() {\n  float x = vec3(1.0);\n}");
        AssertLog(@"^ERROR: 0:\d+: .*main.*not defined", "void helper() {}");
        AssertLog(@"^ERROR: 0:2: .*syntax", "void main() {\n  float = 3.0;\n}");
        AssertLog(@"^ERROR: 0:\d+: ", "void main() { gl_FragColor = vec4(1.0) ");
        AssertLog(@"no matching overloaded function", "void main() { gl_FragColor = vec4(pow(1.0)); }");
        AssertLog(@"l-value", "void main() { 1.0 = 2.0; }");
    }

    [Fact]
    public void AttributesAreOnlyForVertexShaders()
    {
        Assert.Contains("attribute", Error("attribute vec2 a; void main() { gl_FragColor = vec4(a, 0.0, 1.0); }"));
    }

    // ---- hostile source ----

    [Fact]
    public void MalformedNumbersAreErrorsNotCrashes()
    {
        foreach (var literal in new[] { "0x", "089", "99999999999999999999", "1e999999", "0xFFFFFFFFFFFFFFFFFFFF" })
        {
            var result = GlslCompiler.Compile(GlslStage.Fragment, $"void main() {{ float x = {literal}; }}");
            Assert.True(result.Ok || result.InfoLog.StartsWith("ERROR: 0:1:", StringComparison.Ordinal), $"{literal}: {result.InfoLog}");
        }
    }

    [Fact]
    public void DeeplyNestedSourceIsRefusedBeforeItCanRunTheStackOut()
    {
        var nested = new string('(', 5000) + "1.0" + new string(')', 5000);
        Assert.Contains("nesting is too deep", Error($"void main() {{ gl_FragColor = vec4({nested}); }}"));
        var blocks = new string('{', 5000) + new string('}', 5000);
        Assert.Contains("nesting is too deep", Error($"void main() {blocks}"));
        var minus = new string('-', 5000);
        Assert.Contains("nesting is too deep", Error($"void main() {{ float x = {minus}1.0; }}"));
    }

    [Fact]
    public void MacroExpansionThatGrowsWithoutEndStops()
    {
        // Each macro is two of the next: 2^23 copies of the last.
        var names = "ABCDEFGHIJKLMNOPQRSTUVWX";
        var source = string.Concat(Enumerable.Range(0, names.Length - 1).Select(i => $"#define {names[i]} {names[i + 1]} {names[i + 1]}\n"))
            + "#define X 1\nvoid main() { float x = A; }";
        Assert.Contains("too large", Error(source));
    }

    [Fact]
    public void StorageBeyondWhatAShaderMayHoldIsAnError()
    {
        Assert.Contains("too much global storage", Error("uniform vec4 big[60000]; uniform vec4 more[60000]; void main() { gl_FragColor = big[0] + more[0]; }"));
        Assert.Contains("too many local variables", Error("void main() { vec4 a[5000]; gl_FragColor = a[0]; }"));
    }
}
