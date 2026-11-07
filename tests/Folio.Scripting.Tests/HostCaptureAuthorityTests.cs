using Folio.Scripting.Ipc;

namespace Folio.Scripting.Tests;

[Collection("Content process round trips")]
public sealed class HostCaptureAuthorityTests
{
    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task RawChildRequestsCannotBypassHostGrantsOrPermission(bool grant, bool allow, bool throws)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        var probe = Probe(configuration);
        var provider = new Provider();
        var decisions = 0;
        var client = ContentProcessClient.StartWithOptions(probe, new FolioOptions
        {
            MediaCaptureProvider = provider,
            MediaCapturePermission = _ => { Interlocked.Increment(ref decisions); if (throws) throw new InvalidOperationException("private-host-detail"); return allow; }
        }, new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromSeconds(15)));
        try
        {
            var frame = await Exchange(client, new { probe = "capture-request", capabilities = new FeatureCapabilities { Camera = grant } });
            var result = frame.Payload.GetProperty("payload").GetProperty("capture");
            Assert.Equal(grant && allow && !throws, result.GetProperty("ok").GetBoolean());
            Assert.Equal(grant ? 1 : 0, decisions);
            Assert.Equal(grant && allow && !throws ? 1 : 0, provider.Opens);
            Assert.DoesNotContain("private-host-detail", result.GetRawText());
        }
        finally { await client.DisposeAsync(); }
        if (provider.Session is { } session)
        {
            Assert.Equal(1, session.Disposals);
            Assert.Equal(0, session.Subscribers);
        }
    }

    [Fact]
    public async Task PermissionRevokedWhileProviderOpensRetiresTheSessionEvenIfChildIgnoresIt()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        var probe = Probe(configuration);
        var provider = new Provider();
        await using var client = ContentProcessClient.StartWithOptions(probe,
            new FolioOptions { MediaCaptureProvider = provider }, new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromSeconds(15)));
        provider.Opening = () => client.UpdateCapabilitiesAsync(FeatureCapabilities.Default).AsTask().GetAwaiter().GetResult();
        var frame = await Exchange(client, new { probe = "capture-request", capabilities = new FeatureCapabilities { Camera = true } });
        Assert.False(frame.Payload.GetProperty("payload").GetProperty("capture").GetProperty("ok").GetBoolean());
        Assert.Equal(1, provider.Opens);
        Assert.Equal(1, provider.Session!.Disposals);
        Assert.Equal(0, provider.Session.Subscribers);
    }

    [Fact]
    public async Task NormalContentProcessUsesTheHostPermissionCallback()
    {
        var provider = new Provider();
        var decisions = 0;
        await using var client = ContentProcessClient.StartWithOptions(ContentHostTestPaths.Resolve(), new FolioOptions
        {
            MediaCaptureProvider = provider,
            MediaCapturePermission = _ => { Interlocked.Increment(ref decisions); return false; }
        }, new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromSeconds(15)));
        var frame = await Exchange(client, new
        {
            html = "<script>var result='pending';navigator.mediaDevices.getUserMedia({video:true}).then(()=>result='opened',e=>result=e.name);</script>",
            script = "result", capabilities = new FeatureCapabilities { Camera = true }
        });
        Assert.Equal("NotAllowedError", frame.Payload.GetProperty("value").GetString());
        Assert.Equal(1, decisions);
        Assert.Equal(0, provider.Opens);
    }

    [Fact]
    public async Task DisposalCancelsAPendingHostPermissionDecision()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Provider();
        var client = ContentProcessClient.StartWithOptions(Probe(configuration), new FolioOptions { MediaCaptureProvider = provider },
            mediaCapturePermission: async (_, token) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return true;
            });
        try
        {
            await client.SendAsync(ContentMessageKind.Load, new { probe = "capture-request", capabilities = new FeatureCapabilities { Camera = true } }, TestContext.Current.CancellationToken);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        }
        finally { await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken); }
        Assert.Equal(0, provider.Opens);
    }

    private static string Probe(string configuration) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../../tools/Folio.SandboxProbe/bin", configuration, "net10.0/Folio.SandboxProbe.dll"));

    private static async Task<ContentMessage> Exchange(ContentProcessClient client, object payload)
    {
        var response = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask Receive(ContentMessage message, CancellationToken _)
        {
            if (message.Kind == ContentMessageKind.Frame && message.Payload.GetProperty("sequence").GetInt64() > 0) response.TrySetResult(message);
            return ValueTask.CompletedTask;
        }
        client.MessageReceived += Receive;
        try
        {
            await client.SendAsync(ContentMessageKind.Load, payload, TestContext.Current.CancellationToken);
            return await response.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        }
        finally { client.MessageReceived -= Receive; }
    }

    private sealed class Provider : IMediaCaptureProvider
    {
        internal int Opens;
        internal Session? Session;
        internal Action? Opening;
        public IMediaCaptureSession? Open(MediaCaptureRequest request)
        {
            Interlocked.Increment(ref Opens);
            Opening?.Invoke();
            return Session = new Session();
        }
    }

    private sealed class Session : IMediaCaptureSession
    {
        internal int Disposals;
        public MediaCaptureVideoFrame? CurrentVideoFrame => null;
        public MediaAudioFrame? CurrentAudioFrame => null;
        public event Action<MediaCaptureVideoFrame>? VideoFrameAvailable;
        public event Action<MediaAudioFrame>? AudioFrameAvailable;
        public event Action? Revoked;
        internal int Subscribers => (VideoFrameAvailable?.GetInvocationList().Length ?? 0)
            + (AudioFrameAvailable?.GetInvocationList().Length ?? 0) + (Revoked?.GetInvocationList().Length ?? 0);
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }
}
