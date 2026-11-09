using Folio;
using Folio.WinForms;

namespace Folio.WinForms.Tests;

public sealed class SharedMediaAudioRouterTests
{
    [Fact]
    public void RouterNormalizesMonoToStereoAndResamplesSampleRate()
    {
        var output = new RecordingAudioOutput();
        using var router = new SharedMediaAudioRouter(output);
        var client = router.CreateClient();

        // 24000 Hz mono signal of 240 samples (10 ms)
        var monoSamples = new float[240];
        for (var i = 0; i < monoSamples.Length; i++) monoSamples[i] = 0.5f;

        client.Submit(new MediaAudioFrame(24000, 1, monoSamples));

        Assert.Single(output.Frames);
        var frame = output.Frames[0];
        Assert.Equal(48000, frame.SampleRate);
        Assert.Equal(2, frame.Channels);
        // Resampled from 24000 to 48000 should double sample count per channel
        Assert.Equal(480, frame.Samples.Length / 2);
        // Samples should preserve value
        Assert.All(frame.Samples.ToArray(), s => Assert.InRange(s, 0.49f, 0.51f));
    }

    [Fact]
    public void RouterCoordinatesLifecycleAcrossMultipleClients()
    {
        var output = new RecordingAudioOutput();
        using var router = new SharedMediaAudioRouter(output);
        var clientA = (SharedMediaAudioRouter.ClientStream)router.CreateClient();
        var clientB = (SharedMediaAudioRouter.ClientStream)router.CreateClient();

        Assert.Empty(output.LifecycleCalls);

        clientA.Start();
        Assert.Equal(["Start"], output.LifecycleCalls);

        clientB.Start();
        // Router was already started, no redundant call
        Assert.Equal(["Start"], output.LifecycleCalls);

        clientA.Pause();
        // Client B is still active, so router should not pause
        Assert.Equal(["Start"], output.LifecycleCalls);

        clientB.Pause();
        // All clients paused -> router pauses
        Assert.Equal(["Start", "Pause"], output.LifecycleCalls);

        clientA.Start();
        Assert.Equal(["Start", "Pause", "Start"], output.LifecycleCalls);

        clientA.Dispose();
        clientB.Dispose();
        Assert.Contains("Stop", output.LifecycleCalls);
    }

    [Fact]
    public void RouterDisposalClosesOutputAndRejectsNewClients()
    {
        var output = new RecordingAudioOutput();
        var router = new SharedMediaAudioRouter(output);
        router.Dispose();

        Assert.Throws<ObjectDisposedException>(() => router.CreateClient());
    }

    private sealed class RecordingAudioOutput : IMediaAudioOutput, IMediaAudioOutputLifecycle
    {
        public List<MediaAudioFrame> Frames { get; } = [];
        public List<string> LifecycleCalls { get; } = [];

        public void Submit(MediaAudioFrame frame) => Frames.Add(frame);
        public void Start() => LifecycleCalls.Add("Start");
        public void Pause() => LifecycleCalls.Add("Pause");
        public void Flush() => LifecycleCalls.Add("Flush");
        public void Stop() => LifecycleCalls.Add("Stop");
    }
}
