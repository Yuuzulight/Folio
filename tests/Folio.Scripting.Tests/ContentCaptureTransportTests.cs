using Folio.Scripting.Ipc;

namespace Folio.Scripting.Tests;

[Collection("Content process round trips")]
public sealed class ContentCaptureTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealChildStopAudioIsIdempotentAndHandlesReentrantGrantRetirement(bool reentrant)
    {
        var provider = new Provider();
        await using var client = ContentProcessClient.StartWithOptions(ContentHostTestPaths.Resolve(),
            new FolioOptions { MediaCaptureProvider = provider }, new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromSeconds(15)));
        var first = await Exchange(client, new
        {
            html = "<script>var result='pending';navigator.mediaDevices.getUserMedia({audio:true,video:true}).then(s=>{globalThis.stream=s;result='opened';},e=>result=e.name);</script>",
            capabilities = new FeatureCapabilities { Camera = true, Microphone = true, Media = true, Sound = true }, script = "result"
        });
        Assert.Equal("opened", first.Payload.GetProperty("value").GetString());
        if (reentrant)
            provider.Source.StoppingAudio = () => client.UpdateCapabilitiesAsync(FeatureCapabilities.Default, TestContext.Current.CancellationToken).AsTask().GetAwaiter().GetResult();
        await Exchange(client, new { script = "stream.getAudioTracks()[0].stop();stream.getAudioTracks()[0].stop();'stopped'" });
        Assert.Equal(1, provider.Source.AudioStops);
        var next = await Exchange(client, new { script = "stream.getVideoTracks()[0].readyState" });
        Assert.Equal(reentrant ? "ended" : "live", next.Payload.GetProperty("value").GetString());
        Assert.Equal(reentrant ? 1 : 0, provider.Source.Disposals);
        Assert.Equal(reentrant ? 1 : 0, provider.Source.VideoStops);
    }

    [Fact]
    public void ProducerIdentityDeduplicatesFreshArraysAndWireCountersArePerKind()
    {
        var source = new Session();
        using var lease = Lease(source);
        lease.Activate(lease.Initial());
        source.Audio(Block(100, 1)); source.Audio(Block(100, 1)); source.Audio(Block(99, 9));
        source.Video(Frame(400)); source.Video(Frame(400)); source.Video(Frame(399));
        var sample = lease.Take();
        Assert.Equal(1, sample.Audio!.Value.CaptureSequence);
        Assert.Equal(1, sample.Video!.Value.CaptureSequence);
        Assert.Equal(100, sample.AudioSource); Assert.Equal(400, sample.VideoSource);
        Assert.True(sample.AudioTimestamp > 0); Assert.True(sample.VideoTimestamp > 0);
        lease.Publish(sample);
        Assert.Null(lease.Take().Audio);
        source.CurrentAudioFrame = Block(100, 1); source.CurrentVideoFrame = Frame(400);
        var polled = lease.Published();
        Assert.Equal(sample.Audio!.Value.Samples, polled.Audio!.Value.Samples);
        Assert.Equal(sample.Video!.Value.Rgba, polled.Video!.Value.Rgba);
        Assert.Null(lease.Take().Audio);
        source.Audio(Block(101, 2)); source.Video(Frame(401));
        var next = lease.Take();
        Assert.Equal(2, next.Audio!.Value.CaptureSequence); Assert.Equal(2, next.Video!.Value.CaptureSequence);
    }

    [Fact]
    public void AudioFifoHasCountAndByteBoundsWhileVideoCoalescesLatest()
    {
        var source = new Session();
        using var lease = Lease(source);
        lease.Activate(lease.Initial());
        for (var i = 1; i <= 1024; i++)
        {
            source.Audio(new(48000, 1, new float[32768]) { CaptureSequence = i });
            source.Video(Frame(i));
        }
        Assert.Equal(ContentCaptureLease.MaxAudioBytes, lease.QueuedAudioBytes);
        Assert.Equal(4, lease.QueuedAudioBlocks);
        for (var i = 1; i <= 4; i++)
        {
            var sample = lease.Take();
            Assert.Equal(i, sample.AudioSource);
            if (i == 1) Assert.Equal(1024, sample.VideoSource);
            else Assert.Null(sample.Video);
        }
        Assert.Null(lease.Take().Audio);
        for (var i = 1025; i <= 1100; i++) source.Audio(Block(i, i));
        Assert.Equal(ContentCaptureLease.MaxAudioBlocks, lease.QueuedAudioBlocks);
    }

    [Fact]
    public void StopOneKindClearsPendingAndPublishedFramesWithoutStoppingSibling()
    {
        var source = new Session();
        using var lease = Lease(source);
        lease.Activate(lease.Initial());
        source.Audio(Block(1, 1)); source.Video(Frame(1));
        Assert.True(lease.Stop(true)); Assert.True(lease.Stop(true));
        source.Audio(Block(2, 2)); source.Video(Frame(2));
        var sample = lease.Take();
        Assert.Null(sample.Audio); Assert.NotNull(sample.Video);
        Assert.Equal(new MediaCaptureRequest(false, true), lease.ActiveRequest);
        Assert.Equal(1, source.AudioStops); Assert.Equal(0, source.VideoStops);
        Assert.Equal(0, source.Disposals);
        lease.Stop(false); Assert.Null(lease.Take().Video);
    }

    [Fact]
    public void ReentrantStopAndGetterRevocationCannotPublishRetiredSamples()
    {
        var source = new Session();
        var granted = true;
        using var lease = new ContentCaptureLease(source, new(true, true), _ => granted, () => { });
        lease.Activate(lease.Initial());
        source.StoppingAudio = () => { granted = false; lease.Dispose(); };
        lease.Stop(true);
        source.Audio(Block(1, 1)); source.Video(Frame(1));
        Assert.Null(lease.Take().Audio); Assert.Null(lease.Take().Video);
        Assert.Equal(1, source.Disposals);
        var other = new Session();
        using var next = new ContentCaptureLease(other, new(true, true), _ => granted, () => { });
        granted = true;
        other.ReadingAudio = () => granted = false;
        other.CurrentAudioFrame = Block(1, 1);
        Assert.Null(next.Initial().Audio);
    }

    [Fact]
    public void LegacyIdentityCopiesBuffersAndInvalidFramesDoNotConsumeBudget()
    {
        var source = new Session();
        using var lease = Lease(source);
        lease.Activate(lease.Initial());
        var samples = new float[] { 1, 2 };
        var legacy = new MediaAudioFrame(48000, 1, samples);
        source.Audio(legacy); source.Audio(legacy); samples[0] = 9;
        source.Audio(new(48000, 1, new[] { float.NaN }) { CaptureSequence = 2 });
        source.Video(new(1, 1, new byte[3]) { CaptureSequence = 1 });
        var sample = lease.Take();
        Assert.Equal(1, sample.Audio!.Value.Samples.Span[0]); Assert.Null(sample.Video);
        Assert.Null(lease.Take().Audio);
    }

    private static ContentCaptureLease Lease(Session source) => new(source, new(true, true), _ => true, () => { });
    private static MediaAudioFrame Block(long sequence, float value) => new(48000, 1, new[] { value }) { CaptureSequence = sequence };
    private static MediaCaptureVideoFrame Frame(long sequence) => new(1, 1, new byte[] { 1, 2, 3, 255 }) { CaptureSequence = sequence };

    private static async Task<ContentMessage> Exchange(ContentProcessClient client, object payload)
    {
        var completion = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask Receive(ContentMessage message, CancellationToken _)
        {
            if (message.Kind == ContentMessageKind.Frame && message.Payload.GetProperty("sequence").GetInt64() > 0) completion.TrySetResult(message);
            if (message.Kind == ContentMessageKind.Diagnostic) completion.TrySetException(new InvalidOperationException(message.Payload.ToString()));
            return ValueTask.CompletedTask;
        }
        client.MessageReceived += Receive;
        try
        {
            await client.SendAsync(ContentMessageKind.Load, payload, TestContext.Current.CancellationToken);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        }
        finally { client.MessageReceived -= Receive; }
    }

    private sealed class Provider : IMediaCaptureProvider
    {
        internal Session Source { get; } = new() { CurrentAudioFrame = Block(77, .5f), CurrentVideoFrame = Frame(100) };
        public IMediaCaptureSession? Open(MediaCaptureRequest request) => Source;
    }

    internal sealed class Session : IMediaCaptureSession, IMediaCaptureTrackControl
    {
        private MediaAudioFrame? _audio;
        public MediaAudioFrame? CurrentAudioFrame { get { ReadingAudio?.Invoke(); return _audio; } set => _audio = value; }
        public MediaCaptureVideoFrame? CurrentVideoFrame { get; set; }
        public event Action<MediaAudioFrame>? AudioFrameAvailable;
        public event Action<MediaCaptureVideoFrame>? VideoFrameAvailable;
        public event Action? Revoked;
        internal Action? StoppingAudio, ReadingAudio;
        internal int AudioStops, VideoStops, Disposals;
        internal void Audio(MediaAudioFrame frame) { CurrentAudioFrame = frame; AudioFrameAvailable?.Invoke(frame); }
        internal void Video(MediaCaptureVideoFrame frame) { CurrentVideoFrame = frame; VideoFrameAvailable?.Invoke(frame); }
        internal void Revoke() => Revoked?.Invoke();
        public void StopAudio() { AudioStops++; StoppingAudio?.Invoke(); }
        public void StopVideo() => VideoStops++;
        public void Dispose() => Disposals++;
    }
}
