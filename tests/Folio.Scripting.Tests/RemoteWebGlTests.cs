using System.Runtime.Versioning;
using Folio.Scripting.Gpu;
using Folio.Scripting.Gpu.Remote;

namespace Folio.Scripting.Tests;

public sealed class RemoteWebGlTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    private const uint ArrayBuffer = 0x8892, StaticDraw = 0x88E4, ArrayBufferBinding = 0x8894, InvalidOperation = 0x0502;

    [Fact]
    public void EveryNativeContextCallHasAnOpcode()
    {
        var ops = Enum.GetNames<GpuWebGlOp>().ToHashSet();
        var methods = typeof(INativeWebGlContext).GetMethods().Where(method => method.Name.StartsWith("Try", StringComparison.Ordinal)).ToArray();
        Assert.Equal(140, methods.Length);
        foreach (var method in methods)
        {
            var name = method.Name[3..];
            if (name == "ReadBuffer" && method.GetParameters().Length > 2) name = "GetBufferSubData";
            Assert.Contains(name, ops);
        }
    }

    [Fact(Skip = "Only supported on Windows.", SkipUnless = nameof(IsWindows))]
    [SupportedOSPlatform("windows")]
    public void CallsWithoutOutputsAreBatchedUntilASynchronizationPoint()
    {
        using var pair = Pair.Create();
        using var context = pair.Context();
        var before = pair.Exchanges;
        for (var i = 0; i < 100; i++) Assert.True(context.TrySetClearColor(i / 100f, 0, 0, 1, out _));
        Assert.True(context.TryClear(0, 0, 0, 1, 0x4000, out _));
        Assert.Equal(before, pair.Exchanges);
        Assert.True(context.TryGetError(out var error, out _));
        Assert.Equal(0u, error);
        Assert.Equal(before + 1, pair.Exchanges);
    }

    [Fact(Skip = "Only supported on Windows.", SkipUnless = nameof(IsWindows))]
    [SupportedOSPlatform("windows")]
    public void FailureOfAQueuedCallIsReportedAtTheNextSynchronizationPoint()
    {
        using var pair = Pair.Create();
        using var context = pair.Context();
        // Handle 999 was never created: accepted when queued, rejected by the worker.
        Assert.True(context.TryBindBuffer(ArrayBuffer, new WebGlBufferHandle(999), out var failure));
        Assert.Null(failure);
        var deferred = Assert.Single(((IDeferredGpuFailures)context).TakeDeferredFailures());
        Assert.Equal(InvalidOperation, deferred.ApiError);
        Assert.Empty(((IDeferredGpuFailures)context).TakeDeferredFailures());
        Assert.False(context.IsLost);
        Assert.True(context.TryGetError(out _, out _)); // the session is still usable
    }

    [Fact(Skip = "Only supported on Windows.", SkipUnless = nameof(IsWindows))]
    [SupportedOSPlatform("windows")]
    public void LargeUploadsGoThroughTheRingAndBindingsReportClientHandles()
    {
        using var pair = Pair.Create();
        using var context = pair.Context(WebGlContextVersion.WebGl2);
        Assert.True(context.TryCreateBuffer(out var buffer, out _));
        Assert.True(context.TryBindBuffer(ArrayBuffer, buffer, out _));
        var data = new byte[96 * 1024];
        new Random(5).NextBytes(data);
        Assert.True(context.TryUploadBuffer(ArrayBuffer, data, StaticDraw, out _));
        var copy = new byte[data.Length];
        Assert.True(context.TryReadBuffer(ArrayBuffer, 0, copy, out var failure), failure?.Message);
        Assert.Equal(data, copy);
        Assert.True(context.TryGetParameter(ArrayBufferBinding, out var binding, out _));
        Assert.Equal(buffer.Value, unchecked((uint)binding.Int32));
        Assert.Empty(((IDeferredGpuFailures)context).TakeDeferredFailures());
    }

    [Fact(Skip = "Only supported on Windows.", SkipUnless = nameof(IsWindows))]
    [SupportedOSPlatform("windows")]
    public void MalformedReplyLosesTheSessionAndEveryContext()
    {
        using var pair = Pair.Create();
        using var first = pair.Context();
        using var second = pair.Context();
        pair.CorruptNextReply = true;
        Assert.False(first.TryGetError(out _, out var failure));
        Assert.Equal(GpuFailureKind.Unavailable, failure!.Kind);
        Assert.True(first.IsLost);
        Assert.True(second.IsLost);
        Assert.False(second.TrySetClearColor(0, 0, 0, 1, out _)); // nothing further is queued
    }

    [Fact(Skip = "Only supported on Windows.", SkipUnless = nameof(IsWindows))]
    [SupportedOSPlatform("windows")]
    public void MalformedBatchLosesTheWorkerSessionWithoutThrowing()
    {
        using var pair = Pair.Create();
        var reply = pair.Dispatcher.Execute(new byte[64]);
        Assert.True(pair.Dispatcher.IsLost);
        Assert.Equal(GpuWebGlWire.StatusLost, BitConverter.ToInt32(reply, 8));
    }

    [SupportedOSPlatform("windows")]
    private sealed class Pair : IGpuWebGlTransport, IDisposable
    {
        private readonly IGpuDevice _device;
        internal GpuWebGlDispatcher Dispatcher { get; }
        internal GpuWebGlSession Session { get; }
        internal int Exchanges { get; private set; }
        internal bool CorruptNextReply { get; set; }

        private Pair(IGpuDevice device)
        {
            _device = device;
            var upload = new ArrayGpuArena(1024 * 1024);
            var readback = new ArrayGpuArena(1024 * 1024);
            Dispatcher = new GpuWebGlDispatcher((INativeWebGlFactory)device, upload, readback);
            Session = new GpuWebGlSession(this, upload, readback);
        }

        internal static Pair Create()
        {
            var result = FolioGpuBackend.TryCreate(32 * 1024 * 1024, GpuRendererPreference.WarpOnly);
            return new(result.Device ?? throw new InvalidOperationException(result.Failure?.Message));
        }

        internal INativeWebGlContext Context(WebGlContextVersion version = WebGlContextVersion.WebGl1)
        {
            var attributes = new WebGlContextAttributes(version, true, false, false, false, true, true);
            Assert.True(Session.TryCreate(new(version, 4, 4, attributes, 16 * 1024 * 1024), out var context, out var failure), failure?.Message);
            return context!;
        }

        public byte[] Exchange(byte[] batch)
        {
            Exchanges++;
            var reply = Dispatcher.Execute(batch);
            if (CorruptNextReply) { CorruptNextReply = false; reply[0] ^= 0xFF; }
            return reply;
        }

        public void Dispose() { Session.Dispose(); Dispatcher.Dispose(); _device.Dispose(); }
    }
}
