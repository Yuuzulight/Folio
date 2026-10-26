using System.Runtime.InteropServices;
using System.Text;
using Folio.Scripting.Gpu;

namespace Folio.Fuzz;

/// <summary>Runs actual ANGLE WebGL validation, including malformed UTF-8 and embedded NULs.</summary>
internal static class NativeWebGlShaderFuzz
{
    internal static readonly Target Target = new("webgl-shader", Run, Seeds,
        ["#version 300 es\n", "precision mediump float;", "void main()", "gl_Position", "gl_FragColor", "uniform", "varying", "in", "out", "for", "[", "]", "{", "}", ";", "\0"]);

    private static void Run(byte[] input)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("ANGLE shader fuzzing requires Windows.");
        var version = input.Length > 0 && (input[0] & 1) != 0 ? WebGlContextVersion.WebGl2 : WebGlContextVersion.WebGl1;
        var vertex = input.Length < 2 || (input[1] & 1) == 0;
        var path = Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", "av_libglesv2.dll");
        if (!File.Exists(path)) path = Path.Combine(AppContext.BaseDirectory, "av_libglesv2.dll");
        var result = AngleGpuBackend.TryCreate(path, 8 * 1024 * 1024, GpuRendererPreference.WarpOnly);
        using var device = result.Device ?? throw new InvalidOperationException(result.Failure?.Message);
        var attributes = new WebGlContextAttributes(version, true, false, false, false, true, true);
        if (!((INativeWebGlFactory)device).TryCreate(new(version, 1, 1, attributes, 8 * 1024 * 1024), out var candidate, out var failure))
            throw new InvalidOperationException(failure?.Message);
        using var context = candidate!;
        Check(context.TryCreateShader(vertex ? 0x8B31u : 0x8B30u, out var shader, out failure), failure);
        Check(context.TrySetShaderSource(shader, input.AsSpan(Math.Min(2, input.Length)), out failure), failure);
        Check(context.TryCompileShader(shader, out var compiled, out var log, out failure), failure);
        Check(context.TryGetShaderParameter(shader, 0x8B81, out var status, out failure), failure);
        if (status.Kind != WebGlParameterKind.Boolean || status.Boolean != compiled)
            throw new InvalidOperationException("Compilation status differs from its WebGL query.");
        Check(context.TryGetShaderInfoLog(shader, out var queriedLog, out failure), failure);
        if (queriedLog != log) throw new InvalidOperationException("Shader info log changed between queries.");
        Check(context.TryDeleteShader(shader, out failure), failure);
    }

    private static void Check(bool success, GpuFailure? failure)
    {
        if (!success) throw new InvalidOperationException(failure?.Message ?? "Native shader validation failed.");
    }

    private static IEnumerable<byte[]> Seeds()
    {
        foreach (var (version, stage, source) in new (byte, byte, string)[]
        {
            (0, 0, "void main(){gl_Position=vec4(0.0,0.0,0.0,1.0);}"),
            (0, 1, "precision mediump float;void main(){gl_FragColor=vec4(1.0);}"),
            (1, 0, "#version 300 es\nvoid main(){gl_Position=vec4(0.0,0.0,0.0,1.0);}"),
            (1, 1, "#version 300 es\nprecision mediump float;out vec4 c;void main(){c=vec4(1.0);}"),
            (1, 0, "#version 300 es\nvoid main(){gl_Position=vec4(0.0);}\0invalid"),
            (0, 1, "void main( {"),
            (1, 1, ""),
        })
            yield return new[] { version, stage }.Concat(Encoding.UTF8.GetBytes(source)).ToArray();
    }
}
