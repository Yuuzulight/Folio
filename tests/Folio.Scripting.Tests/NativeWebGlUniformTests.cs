using System.Runtime.InteropServices;
using System.Text;
using Folio.Scripting.Gpu;

namespace Folio.Scripting.Tests;

[Collection("Content process round trips")]
public sealed class NativeWebGlUniformTests
{
    [Fact]
    public void UniformReadbackChecksReflectedWidthBeforeNativeWrite()
    {
        if (!OperatingSystem.IsWindows()) return;
        var rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64", Architecture.Arm64 => "win-arm64", Architecture.X86 => "win-x86",
            _ => throw new PlatformNotSupportedException(),
        };
        var path = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", "av_libglesv2.dll");
        if (!File.Exists(path)) path = Path.Combine(AppContext.BaseDirectory, "av_libglesv2.dll");
        var result = AngleGpuBackend.TryCreate(path, 64 * 1024 * 1024, GpuRendererPreference.WarpOnly);
        using var device = Assert.IsType<AngleGpuDevice>(result.Device);
        var attributes = new WebGlContextAttributes(WebGlContextVersion.WebGl2, true, false, false, false, true, true);
        Assert.True(device.TryCreateNativeWebGlContext(new(WebGlContextVersion.WebGl2, 4, 4, attributes, 64 * 1024 * 1024), out var candidate, out var failure), failure?.Message);
        using var context = Assert.IsType<AngleNativeWebGlContext>(candidate);
        Assert.True(context.TryCreateShader(0x8B31, out uint vertex, out failure), failure?.Message);
        Assert.True(context.TrySetShaderSource(vertex, Encoding.UTF8.GetBytes("#version 300 es\nvoid main(){gl_Position=vec4(0,0,0,1);}"), out failure), failure?.Message);
        Assert.True(context.TryCompileShader(vertex, out var compiled, out var log, out failure), failure?.Message);
        Assert.True(compiled, log);
        Assert.True(context.TryCreateShader(0x8B30, out uint fragment, out failure), failure?.Message);
        Assert.True(context.TrySetShaderSource(fragment, Encoding.UTF8.GetBytes("#version 300 es\nprecision highp float; uniform vec4 color; out vec4 pixel; void main(){pixel=color;}"), out failure), failure?.Message);
        Assert.True(context.TryCompileShader(fragment, out compiled, out log, out failure), failure?.Message);
        Assert.True(compiled, log);
        Assert.True(context.TryCreateProgram(out uint program, out failure), failure?.Message);
        Assert.True(context.TryAttachShader(program, vertex, out failure), failure?.Message);
        Assert.True(context.TryAttachShader(program, fragment, out failure), failure?.Message);
        Assert.True(context.TryLinkProgram(program, out var linked, out log, out failure), failure?.Message);
        Assert.True(linked, log);
        Assert.True(context.TryUseProgram(program, out failure), failure?.Message);
        Assert.True(context.TryGetUniformLocation(program, Encoding.UTF8.GetBytes("color"), out var location, out failure), failure?.Message);
        var expected = new float[] { 0.125f, 0.25f, 0.5f, 1 };
        Assert.True(context.TryUniformFloats(location, 4, expected, out failure), failure?.Message);
        var small = new float[] { 17, 19 };
        Assert.False(context.TryGetUniformFloats(program, location, small, out failure));
        Assert.Equal(new float[] { 17, 19 }, small);
        var actual = new float[4];
        Assert.True(context.TryGetUniformFloats(program, location, actual, out failure), failure?.Message);
        Assert.Equal(expected, actual);
        Assert.False(context.TryGetUniformFloats(program, location + 100_000, actual, out failure));
        Assert.Equal(expected, actual);
    }
}
