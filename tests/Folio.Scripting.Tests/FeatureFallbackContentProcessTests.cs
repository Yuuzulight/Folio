using Folio.Scripting.Ipc;

namespace Folio.Scripting.Tests;

[Collection("Content process round trips")]
public sealed class FeatureFallbackContentProcessTests
{
    [Fact]
    public async Task PermissionFailuresAndRecoveryPublishRealProcessPixels()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "The real WebGL content-process pixel path is Windows-backed.");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        var host = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/Folio.ContentHost/bin", configuration, "net10.0/Folio.ContentHost.dll"));
        await using var client = ContentProcessClient.Start(host,
            limits: new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromSeconds(15)));
        var denied = await Exchange(client, ContentMessageKind.Load, new
        {
            html = "<body style='margin:0;background:white'><canvas id='c' width='220' height='80'></canvas>",
            script = "var c=document.getElementById('c'); c.getContext('webgl')===null",
            capabilities = FeatureCapabilities.Default
        });
        Assert.Equal("true", denied.Payload.GetProperty("value").GetString());
        Assert.Equal(new byte[] { 245, 240, 237, 255 }, Pixel(denied, 100, 40));

        await Exchange(client, ContentMessageKind.Capabilities, new { capabilities = new FeatureCapabilities { WebGl = true } });
        var healthy = await Exchange(client, ContentMessageKind.Load, new
        {
            script = "var gl=c.getContext('webgl'); gl.clearColor(1,0,0,1); gl.clear(gl.COLOR_BUFFER_BIT); true"
        });
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(healthy, 100, 40));

        await Exchange(client, ContentMessageKind.Capabilities, new { capabilities = FeatureCapabilities.Default });
        var revoked = await Exchange(client, ContentMessageKind.Load, new
        {
            script = "gl.clearColor(0,1,0,1); gl.clear(gl.COLOR_BUFFER_BIT); gl.isContextLost()"
        });
        Assert.Equal("true", revoked.Payload.GetProperty("value").GetString());
        Assert.Equal(new byte[] { 245, 240, 237, 255 }, Pixel(revoked, 100, 40));

        await Exchange(client, ContentMessageKind.Capabilities, new { capabilities = new FeatureCapabilities { WebGl = true } });
        var restored = await Exchange(client, ContentMessageKind.Load, new
        {
            script = "gl.clearColor(0,0,1,1); gl.clear(gl.COLOR_BUFFER_BIT); !gl.isContextLost()"
        });
        Assert.Equal("true", restored.Payload.GetProperty("value").GetString());
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(restored, 100, 40));
    }

    private static byte[] Pixel(ContentMessage frame, int x, int y)
    {
        var descriptor = frame.Payload.GetProperty("canvasFrame");
        using var reader = new CanvasFrameStore(descriptor.GetProperty("path").GetString()!,
            descriptor.GetProperty("width").GetInt32(), descriptor.GetProperty("height").GetInt32(), descriptor.GetProperty("stride").GetInt32());
        var pixels = reader.ReadLatest(out var sequence);
        Assert.Equal(descriptor.GetProperty("sequence").GetInt64(), sequence);
        Assert.True(sequence > 0);
        return pixels.AsSpan(y * reader.Stride + x * 4, 4).ToArray();
    }

    private static async Task<ContentMessage> Exchange(ContentProcessClient client, ContentMessageKind kind, object payload)
    {
        var received = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask Handler(ContentMessage message, CancellationToken _)
        {
            if (message.Kind is ContentMessageKind.Frame or ContentMessageKind.Capabilities or ContentMessageKind.Diagnostic)
                received.TrySetResult(message);
            return ValueTask.CompletedTask;
        }
        client.MessageReceived += Handler;
        try
        {
            await client.SendAsync(kind, payload, TestContext.Current.CancellationToken);
            var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.Equal(kind == ContentMessageKind.Capabilities ? ContentMessageKind.Capabilities : ContentMessageKind.Frame, message.Kind);
            return message;
        }
        finally { client.MessageReceived -= Handler; }
    }
}
