using Folio;
using Folio.Scripting.Ipc;

namespace Folio.Scripting.Tests;

[Collection("Content process round trips")]
public sealed class ContentCaptureBridgeTests
{
    [Fact]
    public async Task ContentProcessForwardsCaptureFramesAndRevocation()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        var host = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/Folio.ContentHost/bin", configuration, "net10.0/Folio.ContentHost.dll"));
        var provider = new Provider();
        await using var client = ContentProcessClient.Start(host, limits: new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromSeconds(15)), mediaCaptureProvider: provider);

        var first = await Exchange(new
        {
            html = "<script>navigator.mediaDevices.getUserMedia({video:true}).then(s=>{globalThis.stream=s;globalThis.result='opened';},e=>globalThis.result=e.name);</script>",
            capabilities = new FeatureCapabilities { Camera = true, Media = true },
            script = "result"
        });
        Assert.Equal("opened", first.Payload.GetProperty("value").GetString());
        Assert.NotNull(provider.Session);

        provider.Session!.Push(new MediaCaptureVideoFrame(1, 1, new byte[] { 9, 8, 7, 255 }));
        var second = await Exchange(new { script = "stream.getVideoTracks()[0].readyState" });
        Assert.Equal("live", second.Payload.GetProperty("value").GetString());
        provider.Session.Revoke();
        var third = await Exchange(new { script = "stream.getVideoTracks()[0].readyState" });
        Assert.Equal("ended", third.Payload.GetProperty("value").GetString());
        Assert.True(provider.Session.Disposed);

        async Task<ContentMessage> Exchange(object payload)
        {
            var completion = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            ValueTask Handler(ContentMessage message, CancellationToken _)
            {
                if (message.Kind == ContentMessageKind.Frame) completion.TrySetResult(message);
                else if (message.Kind == ContentMessageKind.Diagnostic) completion.TrySetException(new InvalidOperationException(message.Payload.ToString()));
                return ValueTask.CompletedTask;
            }
            client.MessageReceived += Handler;
            try { await client.SendAsync(ContentMessageKind.Load, payload, TestContext.Current.CancellationToken); return await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken); }
            finally { client.MessageReceived -= Handler; }
        }
    }

    private sealed class Provider : IMediaCaptureProvider
    {
        public Session? Session { get; private set; }
        public IMediaCaptureSession? Open(MediaCaptureRequest request) => Session = new Session();
    }

    private sealed class Session : IMediaCaptureSession
    {
        private MediaCaptureVideoFrame? _frame;
        public bool Disposed { get; private set; }
        public MediaCaptureVideoFrame? CurrentVideoFrame => Disposed ? null : _frame;
        public MediaAudioFrame? CurrentAudioFrame => null;
        public event Action<MediaCaptureVideoFrame>? VideoFrameAvailable;
        public event Action<MediaAudioFrame>? AudioFrameAvailable { add { } remove { } }
        public event Action? Revoked;
        public void Push(MediaCaptureVideoFrame frame) { _frame = frame; VideoFrameAvailable?.Invoke(frame); }
        public void Revoke() => Revoked?.Invoke();
        public void Dispose() => Disposed = true;
    }
}
