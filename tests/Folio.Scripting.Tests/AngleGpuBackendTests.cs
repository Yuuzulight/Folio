using System.Runtime.InteropServices;
using Folio.Scripting.Gpu;

namespace Folio.Scripting.Tests;

[Collection("Content process round trips")]
public sealed class AngleGpuBackendTests
{
    [Fact]
    public void NonWindowsReportsTypedUnavailableResult()
    {
        if (OperatingSystem.IsWindows()) return;
        var result = AngleGpuBackend.TryCreate("av_libglesv2.dll", 64 * 1024 * 1024, GpuRendererPreference.WarpOnly);
        Assert.False(result.Succeeded);
        Assert.Equal(GpuFailureKind.Unavailable, result.Failure?.Kind);
    }

    [Fact]
    public void MissingNativeLibraryReportsTypedUnavailableResult()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), $"folio-angle-missing-{Guid.NewGuid():N}.dll");
        var result = AngleGpuBackend.TryCreate(path, 64 * 1024 * 1024, GpuRendererPreference.WarpOnly);
        Assert.False(result.Succeeded);
        Assert.Equal(GpuFailureKind.Unavailable, result.Failure?.Kind);
    }

    [Fact]
    public void WarpSurfaceClearsAndReadsStraightRgba()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var device = CreateDevice(GpuRendererPreference.WarpOnly, 64 * 1024 * 1024);
        Assert.Equal(GpuRendererKind.Warp, device.RendererKind);
        Assert.Contains("Warp", device.RendererIdentity, StringComparison.OrdinalIgnoreCase);
        var surfaceResult = device.TryCreateSurface(4, 3);
        Assert.True(surfaceResult.Succeeded, surfaceResult.Failure?.Message);
        using var surface = surfaceResult.Surface!;
        Assert.True(surface.ClearColor(0.25f, 0.5f, 0.75f, 0.5f, out var clearFailure), clearFailure?.Message);
        var pixels = new byte[4 * 3 * 4];
        Assert.True(surface.TryReadPixels(pixels, out var readFailure), readFailure?.Message);
        Assert.Equal(new byte[] { 64, 128, 191, 128 }, pixels.AsSpan(0, 4).ToArray());
        Assert.Equal(new byte[] { 64, 128, 191, 128 }, pixels.AsSpan((3 - 1) * 4 * 4, 4).ToArray());
        Assert.True(surface.ClearColor(1, 0, 0, 1, out clearFailure), clearFailure?.Message);
        Assert.True(surface.ClearColorRect(0, 0, 1, 1, 0, 0, 4, 1, out clearFailure), clearFailure?.Message);
        Assert.True(surface.TryReadPixels(pixels, out readFailure), readFailure?.Message);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, pixels.AsSpan(0, 4).ToArray());
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixels.AsSpan((3 - 1) * 4 * 4, 4).ToArray());
    }

    [Fact]
    public void NativeResourcesShareBudgetAndReleaseOnDispose()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var device = CreateDevice(GpuRendererPreference.WarpOnly, 2 * 1024 * 1024);
        using var surface = device.TryCreateSurface(8, 8).Surface!;
        var baseline = device.ReservedBytes;
        Assert.True(surface.Resources.TryAllocateTexture(8, 8, out var texture, out var textureFailure), textureFailure?.Message);
        Assert.True(surface.Resources.TryAllocateBuffer(1024, out var buffer, out var bufferFailure), bufferFailure?.Message);
        Assert.True(surface.Resources.TryAllocateRenderbuffer(8, 8, 4, out var renderbuffer, out var renderbufferFailure), renderbufferFailure?.Message);
        Assert.True(device.ReservedBytes > baseline);
        texture!.Dispose(); buffer!.Dispose(); renderbuffer!.Dispose();
        Assert.Equal(baseline, device.ReservedBytes);
        surface.Dispose();
        Assert.Equal(0, device.ReservedBytes);
    }

    [Fact]
    public void BudgetRejectsSurfaceAndLargeResourceWithoutNativeFallback()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var device = CreateDevice(GpuRendererPreference.WarpOnly, 64);
        var surface = device.TryCreateSurface(4, 4);
        Assert.True(surface.Succeeded, surface.Failure?.Message);
        using (surface.Surface!)
        {
            Assert.False(surface.Surface!.Resources.TryAllocateBuffer(1, out _, out var failure));
            Assert.Equal(GpuFailureKind.BudgetExceeded, failure?.Kind);
        }
        Assert.Equal(0, device.ReservedBytes);
    }

    [Fact]
    public void RetainedSurfaceRejectsOperationsAfterDeviceDisposal()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var device = CreateDevice(GpuRendererPreference.WarpOnly, 64 * 1024 * 1024);
        var surface = device.TryCreateSurface(2, 2).Surface!;
        device.Dispose();
        Assert.False(surface.ClearColor(1, 0, 0, 1, out var clearFailure));
        Assert.Equal(GpuFailureKind.Unavailable, clearFailure?.Kind);
        Assert.False(surface.Resources.TryAllocateBuffer(4, out _, out var resourceFailure));
        Assert.Equal(GpuFailureKind.Unavailable, resourceFailure?.Kind);
        surface.Dispose();
    }

    [Fact]
    public void TwoWarpDevicesRemainIndependentWhenTheFirstIsDisposed()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var first = CreateDevice(GpuRendererPreference.WarpOnly, 64 * 1024 * 1024);
        using var second = CreateDevice(GpuRendererPreference.WarpOnly, 64 * 1024 * 1024);
        using var surface = second.TryCreateSurface(2, 2).Surface!;
        first.Dispose();
        Assert.True(surface.ClearColor(0, 1, 0, 1, out var clearFailure), clearFailure?.Message);
        var pixels = new byte[16];
        Assert.True(surface.TryReadPixels(pixels, out var readFailure), readFailure?.Message);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, pixels[..4]);
    }

    [Fact]
    public void NativeWarpCompilesLinksAndReportsShaderDiagnostics()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var device = (AngleGpuDevice)CreateDevice(GpuRendererPreference.WarpOnly, 64 * 1024 * 1024);
        var attributes = new WebGlContextAttributes(WebGlContextVersion.WebGl2, true, false, false, false, true, true);
        var options = new WebGlContextOptions(WebGlContextVersion.WebGl2, 32, 32, attributes, 64 * 1024 * 1024);
        Assert.True(device.TryCreateNativeWebGlContext(options, out var context, out var contextFailure), contextFailure?.Message);
        var nativeContext = context!;
        using (nativeContext)
        {
            var vertexSource = System.Text.Encoding.UTF8.GetBytes("#version 300 es\nvoid main(){gl_Position=vec4(0.0);}\n");
            var fragmentSource = System.Text.Encoding.UTF8.GetBytes("#version 300 es\nprecision mediump float; out vec4 color; void main(){color=vec4(1.0);}\n");
            Assert.True(nativeContext.TryCreateShader(0x8B31, out uint vertex, out var failure), failure?.Message);
            Assert.True(nativeContext.TrySetShaderSource(vertex, vertexSource, out failure), failure?.Message);
            Assert.True(nativeContext.TryCompileShader(vertex, out var vertexCompiled, out var vertexLog, out failure), failure?.Message);
            Assert.True(vertexCompiled, vertexLog);
            Assert.True(nativeContext.TryCreateShader(0x8B30, out uint fragment, out failure), failure?.Message);
            Assert.True(nativeContext.TrySetShaderSource(fragment, fragmentSource, out failure), failure?.Message);
            Assert.True(nativeContext.TryCompileShader(fragment, out var fragmentCompiled, out var fragmentLog, out failure), failure?.Message);
            Assert.True(fragmentCompiled, fragmentLog);
            Assert.True(nativeContext.TryCreateProgram(out uint program, out failure), failure?.Message);
            Assert.True(nativeContext.TryAttachShader(program, vertex, out failure), failure?.Message);
            Assert.True(nativeContext.TryAttachShader(program, fragment, out failure), failure?.Message);
            Assert.True(nativeContext.TryLinkProgram(program, out var linked, out var linkLog, out failure), failure?.Message);
            Assert.True(linked, linkLog);
            Assert.True(nativeContext.TryDeleteProgram(program, out failure), failure?.Message);
            Assert.True(nativeContext.TryDeleteShader(vertex, out failure), failure?.Message);
            Assert.True(nativeContext.TryDeleteShader(fragment, out failure), failure?.Message);

            Assert.True(nativeContext.TryCreateShader(0x8B30, out uint malformed, out failure), failure?.Message);
            Assert.True(nativeContext.TrySetShaderSource(malformed, "void main(){"u8.ToArray(), out failure), failure?.Message);
            Assert.True(nativeContext.TryCompileShader(malformed, out var malformedCompiled, out var malformedLog, out failure), failure?.Message);
            Assert.False(malformedCompiled);
            Assert.NotEmpty(malformedLog);
            Assert.True(nativeContext.TrySetShaderSource(malformed, "void main(){\0}"u8.ToArray(), out failure), failure?.Message);
            Assert.True(nativeContext.TryCompileShader(malformed, out _, out _, out failure), failure?.Message);
            Assert.True(nativeContext.TryDeleteShader(malformed, out failure), failure?.Message);

            Assert.True(nativeContext.TryCreateShader(0x8B30, out uint oversizedShader, out failure), failure?.Message);
            var oversized = new byte[options.MaxShaderSourceBytes + 1];
            Assert.False(nativeContext.TrySetShaderSource(oversizedShader, oversized, out failure));
            Assert.Equal(GpuFailureKind.BudgetExceeded, failure?.Kind);
            Assert.True(nativeContext.TryDeleteShader(oversizedShader, out failure), failure?.Message);
        }
    }

    [Fact]
    public void NativeWarpUploadsBufferAndDrawsTriangleToReadback()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var device = (AngleGpuDevice)CreateDevice(GpuRendererPreference.WarpOnly, 64 * 1024 * 1024);
        var attrs = new WebGlContextAttributes(WebGlContextVersion.WebGl2, true, false, false, false, true, true);
        Assert.True(device.TryCreateNativeWebGlContext(new(WebGlContextVersion.WebGl2, 32, 32, attrs, 64 * 1024 * 1024), out var context, out var contextFailure), contextFailure?.Message);
        var nativeContext = Assert.IsType<AngleNativeWebGlContext>(context);
        using (nativeContext)
        {
            var vs = System.Text.Encoding.UTF8.GetBytes("#version 300 es\nlayout(location=0) in vec2 p; void main(){gl_Position=vec4(p,0.0,1.0);}\n");
            var fs = System.Text.Encoding.UTF8.GetBytes("#version 300 es\nprecision mediump float; out vec4 c; void main(){c=vec4(1.0,0.0,0.0,1.0);}\n");
            Assert.True(nativeContext.TryCreateShader(0x8B31, out uint vertex, out var failure), failure?.Message);
            Assert.True(nativeContext.TrySetShaderSource(vertex, vs, out failure), failure?.Message);
            Assert.True(nativeContext.TryCompileShader(vertex, out var vertexOk, out var vertexLog, out failure), failure?.Message);
            Assert.True(vertexOk, vertexLog);
            Assert.True(nativeContext.TryCreateShader(0x8B30, out uint fragment, out failure), failure?.Message);
            Assert.True(nativeContext.TrySetShaderSource(fragment, fs, out failure), failure?.Message);
            Assert.True(nativeContext.TryCompileShader(fragment, out var fragmentOk, out var fragmentLog, out failure), failure?.Message);
            Assert.True(fragmentOk, fragmentLog);
            Assert.True(nativeContext.TryCreateProgram(out uint program, out failure), failure?.Message);
            Assert.True(nativeContext.TryAttachShader(program, vertex, out failure), failure?.Message);
            Assert.True(nativeContext.TryAttachShader(program, fragment, out failure), failure?.Message);
            Assert.True(nativeContext.TryLinkProgram(program, out var linked, out var linkLog, out failure), failure?.Message);
            Assert.True(linked, linkLog);
            Assert.True(nativeContext.TryUseProgram(program, out failure), failure?.Message);
            Assert.True(nativeContext.TryCreateVertexArray(out uint vao, out failure), failure?.Message);
            Assert.True(nativeContext.TryBindVertexArray(vao, out failure), failure?.Message);
            Assert.True(nativeContext.TryCreateBuffer(out uint buffer, out failure), failure?.Message);
            Assert.True(nativeContext.TryBindBuffer(0x8892, buffer, out failure), failure?.Message);
            var vertices = new byte[24];
            BitConverter.TryWriteBytes(vertices.AsSpan(0, 4), -0.8f); BitConverter.TryWriteBytes(vertices.AsSpan(4, 4), -0.8f);
            BitConverter.TryWriteBytes(vertices.AsSpan(8, 4), 0.8f); BitConverter.TryWriteBytes(vertices.AsSpan(12, 4), -0.8f);
            BitConverter.TryWriteBytes(vertices.AsSpan(16, 4), 0f); BitConverter.TryWriteBytes(vertices.AsSpan(20, 4), 0.8f);
            Assert.True(nativeContext.TryUploadBuffer(0x8892, vertices, 0x88E4, out failure), failure?.Message);
            Assert.True(nativeContext.TryVertexAttribPointer(0, 2, 0x1406, false, 8, 0, out failure), failure?.Message);
            Assert.True(nativeContext.TryEnableVertexAttrib(0, true, out failure), failure?.Message);
            Assert.True(nativeContext.TrySetViewport(0, 0, 32, 32, out failure), failure?.Message);
            Assert.True(nativeContext.TryClear(0, 0, 0, 1, 0x4000, out failure), failure?.Message);
            Assert.True(nativeContext.TryDrawArrays(0x0004, 0, 3, out failure), failure?.Message);
            var pixels = new byte[32 * 32 * 4];
            Assert.True(nativeContext.TryReadPixels(0, 0, 32, 32, pixels, out failure), failure?.Message);
            Assert.Contains((byte)255, pixels.Where((value, index) => index % 4 == 0));
            Assert.True(nativeContext.TryDeleteBuffer(buffer, out failure), failure?.Message);
            Assert.True(nativeContext.TryDeleteVertexArray(vao, out failure), failure?.Message);
            Assert.True(nativeContext.TryDeleteProgram(program, out failure), failure?.Message);
            Assert.True(nativeContext.TryDeleteShader(vertex, out failure), failure?.Message);
            Assert.True(nativeContext.TryDeleteShader(fragment, out failure), failure?.Message);
        }
    }

    [Theory]
    [InlineData(0x881A /* RGBA16F */)]
    [InlineData(0x8814 /* RGBA32F */)]
    public void FloatRenderbufferStorageUsesACompleteFallbackAndReleasesReservations(uint format)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var device = (AngleGpuDevice)CreateDevice(GpuRendererPreference.WarpOnly, 64 * 1024 * 1024);
        var attrs = new WebGlContextAttributes(WebGlContextVersion.WebGl2, true, false, false, false, true, true);
        Assert.True(device.TryCreateNativeWebGlContext(new(WebGlContextVersion.WebGl2, 2, 2, attrs, 64 * 1024 * 1024), out var context, out var createFailure), createFailure?.Message);
        using var native = Assert.IsType<AngleNativeWebGlContext>(context);
        Assert.True(native.TryCreateRenderbuffer(out var renderbuffer, out var failure), failure?.Message);
        Assert.True(native.TryBindRenderbuffer(0x8D41, renderbuffer, out failure), failure?.Message);
        var before = device.ReservedBytes;
        Assert.True(native.TryRenderbufferStorage(0x8D41, format, 2, 2, out failure), failure?.Message);
        Assert.True(device.ReservedBytes > before);
        Assert.True(native.TryCreateFramebuffer(out var framebuffer, out failure), failure?.Message);
        Assert.True(native.TryBindFramebuffer(0x8D40, framebuffer, out failure), failure?.Message);
        Assert.True(native.TryFramebufferRenderbuffer(0x8D40, 0x8CE0, 0x8D41, renderbuffer, out failure), failure?.Message);
        Assert.True(native.TryCheckFramebufferStatus(0x8D40, out var status, out failure), failure?.Message);
        Assert.Equal(0x8CD5u /* FRAMEBUFFER_COMPLETE */, status);
        Assert.True(native.TryClear(0.25f, 0.5f, 0.75f, 1, 0x4000, out failure), failure?.Message);
        var pixels = new byte[16];
        Assert.True(native.TryReadPixels(0, 0, 2, 2, pixels, out failure), failure?.Message);
        Assert.InRange(pixels[0], 62, 65);
        Assert.InRange(pixels[1], 127, 129);
        Assert.InRange(pixels[2], 190, 193);
        Assert.True(native.TryRenderbufferStorage(0x8D41, 0x8058 /* RGBA8 */, 2, 2, out failure), failure?.Message);
        Assert.True(device.ReservedBytes > before);
    }

    [Fact]
    public async Task ConcurrentSurfaceDisposalAndUseIsBoundedAndSafe()
    {
        if (!OperatingSystem.IsWindows()) return;
        var cancellationToken = TestContext.Current.CancellationToken;
        using var device = CreateDevice(GpuRendererPreference.WarpOnly, 64 * 1024 * 1024);
        var surfaceResult = device.TryCreateSurface(8, 8);
        Assert.True(surfaceResult.Succeeded, surfaceResult.Failure?.Message);
        var surface = surfaceResult.Surface!;
        using var start = new Barrier(2);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var useCompletion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeCompletion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var useThread = new Thread(() =>
        {
            try
            {
                start.SignalAndWait(timeout.Token);
                for (var i = 0; i < 100; i++)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    surface.ClearColor(0, 1, 0, 1, out _);
                    surface.TryReadPixels(new byte[8 * 8 * 4], out _);
                }
                useCompletion.TrySetResult(null);
            }
            catch (Exception error) { useCompletion.TrySetException(error); }
        }) { IsBackground = true, Name = "Folio ANGLE surface use" };
        var disposeThread = new Thread(() =>
        {
            try
            {
                start.SignalAndWait(timeout.Token);
                surface.Dispose();
                surface.Dispose();
                disposeCompletion.TrySetResult(null);
            }
            catch (Exception error) { disposeCompletion.TrySetException(error); }
        }) { IsBackground = true, Name = "Folio ANGLE surface disposal" };
        useThread.Start();
        disposeThread.Start();
        try
        {
            await Task.WhenAll(useCompletion.Task, disposeCompletion.Task).WaitAsync(timeout.Token);
        }
        finally
        {
            timeout.Cancel();
            var useJoined = useThread.Join(TimeSpan.FromSeconds(1));
            var disposeJoined = disposeThread.Join(TimeSpan.FromSeconds(1));
            Assert.True(useJoined, "The ANGLE surface-use worker did not terminate.");
            Assert.True(disposeJoined, "The ANGLE surface-disposal worker did not terminate.");
        }
        Assert.False(surface.ClearColor(1, 0, 0, 1, out var failure));
        Assert.Equal(GpuFailureKind.Unavailable, failure?.Kind);
    }

    [Fact]
    public void NegativeBudgetIsRejectedBeforeNativeLoad()
    {
        var result = AngleGpuBackend.TryCreate("missing", -1, GpuRendererPreference.WarpOnly);
        Assert.False(result.Succeeded);
        Assert.Equal(GpuFailureKind.BudgetExceeded, result.Failure?.Kind);
    }

    private static IGpuDevice CreateDevice(GpuRendererPreference preference, long budget)
    {
        var path = FindNativeLibrary();
        var result = AngleGpuBackend.TryCreate(path, budget, preference);
        Assert.True(result.Succeeded, result.Failure?.Message);
        return result.Device!;
    }

    private static string FindNativeLibrary()
    {
        var rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            Architecture.X86 => "win-x86",
            _ => throw new PlatformNotSupportedException($"Unsupported Windows architecture: {RuntimeInformation.ProcessArchitecture}"),
        };
        var roots = new[] { AppContext.BaseDirectory, Path.GetDirectoryName(typeof(AngleGpuBackendTests).Assembly.Location)! };
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            var canonical = Path.Combine(root, "runtimes", rid, "native", "av_libglesv2.dll");
            if (File.Exists(canonical)) return canonical;
            var flattened = Path.Combine(root, "av_libglesv2.dll");
            if (File.Exists(flattened)) return flattened;
        }
        throw new FileNotFoundException("The ANGLE native asset was not restored for this test target.", "av_libglesv2.dll");
    }

    [Fact]
    public void NativeWarpAntialiasAllocatesResolvesAndResizes()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var device = (AngleGpuDevice)CreateDevice(GpuRendererPreference.WarpOnly, 64 * 1024 * 1024);
        var attributes = new WebGlContextAttributes(WebGlContextVersion.WebGl2, true, true, true, true, true, true);
        var options = new WebGlContextOptions(WebGlContextVersion.WebGl2, 8, 8, attributes, 64 * 1024 * 1024);
        Assert.True(device.TryCreateNativeWebGlContext(options, out var context, out var createFailure), createFailure?.Message);
        var nativeContext = Assert.IsType<AngleNativeWebGlContext>(context);
        using (nativeContext)
        {
            Assert.True(nativeContext.Attributes.Antialias);
            Assert.True(nativeContext.TryClear(0.2f, 0.4f, 0.8f, 1, 0x4100, out var clearFailure), clearFailure?.Message);
            var pixels = new byte[8 * 8 * 4];
            Assert.True(nativeContext.TryReadPixels(0, 0, 8, 8, pixels, out var readFailure), readFailure?.Message);
            Assert.Equal(new byte[] { 51, 102, 204, (byte)255 }, pixels[..4]);
            Assert.True(nativeContext.TryResize(4, 6, out var resizeFailure), resizeFailure?.Message);
            Assert.Equal(4, nativeContext.Width);
            Assert.Equal(6, nativeContext.Height);
        }
        Assert.Equal(0, device.ReservedBytes);
    }

}
