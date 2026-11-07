#if WINDOWS
using System.Diagnostics;
using System.Runtime.InteropServices;
using Folio.WinForms;

namespace Folio.WinForms.Tests;

// These tests never instantiate the production MF/waveIn API implementations or open hardware.
public sealed class WindowsMediaCaptureProviderTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(2);
    private static WindowsMediaCaptureProvider Provider(FakeDevice camera, FakeDevice microphone)
        => new(() => camera, () => microphone, Deadline);

    [Fact]
    public void ConstructionAndEmptyRequestHaveNoDeviceSideEffects()
    {
        var created = 0;
        var provider = new WindowsMediaCaptureProvider(() => { created++; throw new Exception(); },
            () => { created++; throw new Exception(); }, Deadline);
        Assert.Null(provider.Open(new(false, false)));
        Assert.Equal(0, created);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void OpensOnlyRequestedKindsOnOwnedWorkers(bool audio, bool video)
    {
        var camera = new FakeDevice(); var microphone = new FakeDevice();
        var caller = Environment.CurrentManagedThreadId;
        var session = Provider(camera, microphone).Open(new(audio, video));
        Assert.NotNull(session);
        session.Dispose(); session.Dispose();
        Assert.Equal(video ? 1 : 0, camera.Opens);
        Assert.Equal(audio ? 1 : 0, microphone.Opens);
        foreach (var device in new[] { camera, microphone }.Where(d => d.Opens != 0))
        {
            Assert.Equal(1, device.Disposals);
            Assert.NotEqual(caller, device.OpenThread);
            Assert.Equal(device.OpenThread, device.DisposeThread);
        }
    }

    [Fact]
    public void PartialOpenFailureRollsBackBothDevicesBeforePublishing()
    {
        var camera = new FakeDevice(); var microphone = new FakeDevice { FailOpen = true };
        Assert.Null(Provider(camera, microphone).Open(new(true, true)));
        Assert.Equal(1, camera.Disposals);
        Assert.Equal(1, microphone.Disposals);
        Assert.Equal(0, camera.Pumps);
        Assert.Equal(0, microphone.Pumps);
        Assert.Equal(camera.OpenThread, camera.DisposeThread);
        Assert.Equal(microphone.OpenThread, microphone.DisposeThread);
    }

    [Fact]
    public void FactoryFailureRollsBackAnAlreadyOpenedCamera()
    {
        var camera = new FakeDevice();
        var provider = new WindowsMediaCaptureProvider(() => camera, () => throw new IOException(), Deadline);
        Assert.Null(provider.Open(new(true, true)));
        Assert.Equal(1, camera.Disposals);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PerKindStopIsIdempotentAndOtherKindContinues(bool stopVideo)
    {
        var camera = new FakeDevice(); var microphone = new FakeDevice();
        using var session = Assert.IsType<WindowsMediaCaptureSession>(Provider(camera, microphone).Open(new(true, true)));
        camera.WaitForPump(); microphone.WaitForPump();
        var videos = 0; var audios = 0;
        session.VideoFrameAvailable += _ => videos++;
        session.AudioFrameAvailable += _ => audios++;
        if (stopVideo) { session.StopVideo(); session.StopVideo(); }
        else { session.StopAudio(); session.StopAudio(); }
        // Deliberately misbehaving fake calls its saved publisher after native disposal.
        camera.Video!(new(1, 1, new byte[] { 1, 2, 3, 255 }));
        microphone.Audio!(new(48000, 1, new float[] { .5f }));
        Assert.Equal(stopVideo ? 0 : 1, videos);
        Assert.Equal(stopVideo ? 1 : 0, audios);
        Assert.Equal(stopVideo ? 1 : 0, camera.Disposals);
        Assert.Equal(stopVideo ? 0 : 1, microphone.Disposals);
        if (stopVideo) Assert.Null(session.CurrentVideoFrame); else Assert.Null(session.CurrentAudioFrame);
    }

    [Fact]
    public void SnapshotsAndSubscribersDoNotShareMutableArrays()
    {
        var camera = new FakeDevice(); var microphone = new FakeDevice();
        using var session = Provider(camera, microphone).Open(new(true, true))!;
        camera.WaitForPump(); microphone.WaitForPump();
        byte secondVideo = 0; float secondAudio = 0;
        session.VideoFrameAvailable += f => MemoryMarshal.AsMemory(f.Rgba).Span[0] = 88;
        session.VideoFrameAvailable += f => secondVideo = f.Rgba.Span[0];
        session.AudioFrameAvailable += f => MemoryMarshal.AsMemory(f.Samples).Span[0] = 88;
        session.AudioFrameAvailable += f => secondAudio = f.Samples.Span[0];
        byte[] pixels = [1, 2, 3, 255]; float[] samples = [.25f];
        camera.Video!(new(1, 1, pixels)); microphone.Audio!(new(48000, 1, samples));
        pixels[0] = 99; samples[0] = 99;
        MemoryMarshal.AsMemory(session.CurrentVideoFrame!.Value.Rgba).Span[0] = 77;
        MemoryMarshal.AsMemory(session.CurrentAudioFrame!.Value.Samples).Span[0] = 77;
        Assert.Equal(1, secondVideo); Assert.Equal(.25f, secondAudio);
        Assert.Equal(1, session.CurrentVideoFrame!.Value.Rgba.Span[0]);
        Assert.Equal(.25f, session.CurrentAudioFrame!.Value.Samples.Span[0]);
        camera.Video!(new(1, 1, new byte[] { 9, 8, 7, 255 }));
        Assert.Equal(9, session.CurrentVideoFrame!.Value.Rgba.Span[0]); // capacity one, latest wins
    }

    [Fact]
    public void CaptureSequencesAreAuthoritativePerKindAndStableAcrossCopiedSnapshots()
    {
        var camera = new FakeDevice(); var microphone = new FakeDevice();
        using var session = Assert.IsType<WindowsMediaCaptureSession>(Provider(camera, microphone).Open(new(true, true)));
        camera.WaitForPump(); microphone.WaitForPump();
        var videoSequences = new List<long>(); var audioSequences = new List<long>();
        var secondVideoSubscriber = new List<long>(); var secondAudioSubscriber = new List<long>();
        session.VideoFrameAvailable += f => videoSequences.Add(f.CaptureSequence);
        session.VideoFrameAvailable += f => secondVideoSubscriber.Add(f.CaptureSequence);
        session.AudioFrameAvailable += f => audioSequences.Add(f.CaptureSequence);
        session.AudioFrameAvailable += f => secondAudioSubscriber.Add(f.CaptureSequence);

        // Zero is accepted legacy input. Each kind starts at one independently.
        var video = new MediaCaptureVideoFrame(1, 1, new byte[] { 1, 2, 3, 255 });
        var audio = new MediaAudioFrame(48000, 1, new float[] { .5f });
        camera.Video!(video);
        camera.Video!(video with { CaptureSequence = long.MaxValue });
        microphone.Audio!(audio);
        for (var i = 0; i < 5; i++)
        {
            var firstVideo = session.CurrentVideoFrame!.Value;
            var secondVideo = session.CurrentVideoFrame!.Value;
            var firstAudio = session.CurrentAudioFrame!.Value;
            var secondAudio = session.CurrentAudioFrame!.Value;
            Assert.Equal(2L, firstVideo.CaptureSequence);
            Assert.Equal(firstVideo.CaptureSequence, secondVideo.CaptureSequence);
            Assert.Equal(1L, firstAudio.CaptureSequence);
            Assert.Equal(firstAudio.CaptureSequence, secondAudio.CaptureSequence);
            Assert.False(firstVideo.Rgba.Equals(secondVideo.Rgba));
            Assert.False(firstAudio.Samples.Equals(secondAudio.Samples));
        }
        // Identical media bytes on a new callback still represent a new captured sample.
        microphone.Audio!(audio with { CaptureSequence = -10 });
        microphone.Audio!(audio);
        Assert.Equal(new long[] { 1, 2 }, videoSequences);
        Assert.Equal(new long[] { 1, 2, 3 }, audioSequences);
        Assert.Equal(videoSequences, secondVideoSubscriber);
        Assert.Equal(audioSequences, secondAudioSubscriber);
        Assert.Equal(3L, session.CurrentAudioFrame!.Value.CaptureSequence);

        session.StopVideo();
        camera.Video!(video);
        microphone.Audio!(audio);
        Assert.Equal(2, videoSequences.Count);
        Assert.Null(session.CurrentVideoFrame);
        Assert.Equal(4L, session.CurrentAudioFrame!.Value.CaptureSequence);
        Assert.Equal(4L, audioSequences[^1]);
    }

    [Fact]
    public void NoLateFramesAfterDisposeAndSubscriberExceptionIsIsolated()
    {
        var camera = new FakeDevice(); var microphone = new FakeDevice();
        var session = Provider(camera, microphone).Open(new(true, true))!;
        camera.WaitForPump(); microphone.WaitForPump();
        var count = 0;
        session.VideoFrameAvailable += _ => throw new Exception("subscriber");
        session.VideoFrameAvailable += _ => count++;
        session.AudioFrameAvailable += _ => count++;
        camera.Video!(new(1, 1, new byte[4]));
        Assert.Equal(1, count);
        session.Dispose(); session.Dispose();
        camera.Video!(new(1, 1, new byte[4]));
        microphone.Audio!(new(48000, 1, new float[1]));
        Assert.Equal(1, count);
        Assert.Null(session.CurrentVideoFrame); Assert.Null(session.CurrentAudioFrame);
    }

    [Fact]
    public void NativeFailureRevokesOnceAndClosesBothDevices()
    {
        var camera = new FakeDevice(); var microphone = new FakeDevice();
        using var session = Provider(camera, microphone).Open(new(true, true))!;
        camera.WaitForPump(); microphone.WaitForPump();
        using var revoked = new ManualResetEventSlim();
        var count = 0;
        session.Revoked += () => { Interlocked.Increment(ref count); revoked.Set(); };
        camera.Fail.Set();
        Assert.True(revoked.Wait(Deadline, TestContext.Current.CancellationToken));
        Assert.Equal(1, count);
        Assert.Equal(1, camera.Disposals); Assert.Equal(1, microphone.Disposals);
        Assert.Null(session.CurrentVideoFrame);
    }

    [Fact]
    public void DisposalFromSubscriberDoesNotJoinItsOwnWorker()
    {
        var camera = new FakeDevice(); var microphone = new FakeDevice();
        using var session = Provider(camera, microphone).Open(new(false, true))!;
        camera.WaitForPump();
        using var delivered = new ManualResetEventSlim();
        session.VideoFrameAvailable += _ => { session.Dispose(); delivered.Set(); };
        camera.SendVideo.Set();
        Assert.True(delivered.Wait(Deadline, TestContext.Current.CancellationToken));
        Assert.True(camera.Disposed.Wait(Deadline, TestContext.Current.CancellationToken));
        Assert.Equal(1, camera.Disposals);
    }

    [Fact]
    public void StalledNativeOpenTimesOutAndEventuallyRollsBackOnItsOwner()
    {
        using var release = new ManualResetEventSlim();
        var camera = new FakeDevice { OpenBlock = release };
        var provider = new WindowsMediaCaptureProvider(() => camera, () => throw new Exception(), TimeSpan.FromMilliseconds(20));
        try { Assert.Null(provider.Open(new(false, true))); }
        finally { release.Set(); }
        Assert.True(camera.Disposed.Wait(Deadline, TestContext.Current.CancellationToken));
        Assert.Equal(camera.OpenThread, camera.DisposeThread);
        Assert.Equal(0, camera.Pumps);
    }

    [Fact]
    public void DisposalIsBoundedForStalledWorkerAndClearsSnapshots()
    {
        using var release = new ManualResetEventSlim();
        var camera = new FakeDevice { PumpBlock = release };
        var provider = new WindowsMediaCaptureProvider(() => camera, () => throw new Exception(), TimeSpan.FromMilliseconds(20));
        using var session = provider.Open(new(false, true))!;
        camera.WaitForPump();
        var clock = Stopwatch.StartNew();
        try { session.Dispose(); Assert.True(clock.Elapsed < Deadline); }
        finally { release.Set(); }
        Assert.True(camera.Disposed.Wait(Deadline, TestContext.Current.CancellationToken));
        Assert.Null(session.CurrentVideoFrame);
    }

    private sealed class FakeDevice : ICaptureDevice
    {
        internal bool FailOpen;
        internal ManualResetEventSlim? OpenBlock, PumpBlock;
        internal readonly ManualResetEventSlim Running = new(), Disposed = new(), Fail = new(), SendVideo = new();
        internal int Opens, Pumps, Disposals, OpenThread, DisposeThread;
        internal Action<MediaCaptureVideoFrame>? Video;
        internal Action<MediaAudioFrame>? Audio;
        public void Open()
        {
            Opens++; OpenThread = Environment.CurrentManagedThreadId;
            OpenBlock?.Wait(TestContext.Current.CancellationToken);
            if (FailOpen) throw new IOException("fake open");
        }
        public void Pump(CancellationToken cancellation, Action<MediaCaptureVideoFrame> video, Action<MediaAudioFrame> audio)
        {
            Video = video; Audio = audio; Pumps++; Running.Set();
            PumpBlock?.Wait(TestContext.Current.CancellationToken);
            var which = WaitHandle.WaitAny([cancellation.WaitHandle, Fail.WaitHandle, SendVideo.WaitHandle]);
            if (which == 1) throw new IOException("fake unplug");
            if (which == 2) { video(new(1, 1, new byte[4])); cancellation.WaitHandle.WaitOne(); }
        }
        public void Dispose() { Disposals++; DisposeThread = Environment.CurrentManagedThreadId; Disposed.Set(); }
        internal void WaitForPump() => Assert.True(Running.Wait(Deadline, TestContext.Current.CancellationToken));
    }
}

public sealed class WindowsNativeCaptureFormatTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CameraCopiesPaddedTopDownAndBottomUpBgrxAsRgba(bool bottomUp)
    {
        var memory = Marshal.AllocHGlobal(24);
        try
        {
            byte[] rows = [3, 2, 1, 0, 6, 5, 4, 0, 99, 99, 99, 99, 9, 8, 7, 0, 12, 11, 10, 0, 99, 99, 99, 99];
            Marshal.Copy(rows, 0, memory, rows.Length);
            var frame = MediaFoundationCameraCapture.CopyRgb32(bottomUp ? IntPtr.Add(memory, 12) : memory, 2, 2, bottomUp ? -12 : 12);
            Assert.Equal(bottomUp ? 7 : 1, frame.Rgba.Span[0]);
            Assert.Equal(bottomUp ? 1 : 7, frame.Rgba.Span[8]);
            Assert.Equal(255, frame.Rgba.Span[3]);
            Marshal.WriteByte(memory, 0, 77);
            Assert.Equal(bottomUp ? 7 : 1, frame.Rgba.Span[0]);
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    [Fact]
    public void InvalidCameraDimensionsAndPitchFailBeforeReadingNativeMemory()
    {
        Assert.Throws<InvalidDataException>(() => MediaFoundationCameraCapture.CopyRgb32(IntPtr.Zero, 1, 1, 4));
        Assert.Throws<InvalidDataException>(() => MediaFoundationCameraCapture.CopyRgb32((IntPtr)1, 3, 1, 4));
        Assert.Throws<InvalidDataException>(() => MediaFoundationCameraCapture.ValidateSize(int.MaxValue, int.MaxValue));
        Assert.Throws<InvalidDataException>(() => MediaFoundationCameraCapture.ValidateSize(0, 1));
    }

    [Fact]
    public void Pcm16NormalizationAndWaveAbiMatchWindowsHeaders()
    {
        var frame = WinMmMicrophoneCapture.DecodePcm16(new byte[] { 0, 128, 0, 0, 255, 127 });
        Assert.Equal(48000, frame.SampleRate); Assert.Equal(1, frame.Channels);
        Assert.Equal(new float[] { -1, 0, 32767f / 32768 }, frame.Samples.ToArray());
        Assert.Throws<InvalidDataException>(() => WinMmMicrophoneCapture.DecodePcm16(new byte[1]));
        Assert.Equal(18, Marshal.SizeOf<WaveFormat>());
        Assert.Equal(IntPtr.Size == 8 ? 48 : 32, Marshal.SizeOf<WaveHeader>());
        Assert.Equal(IntPtr.Size, Marshal.OffsetOf<WaveHeader>("BufferLength").ToInt32());
    }

    [Fact]
    public void CameraAdapterCancellationSuppressesNativeResultReturnedAfterStop()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var api = new FakeCameraApi { ReadFrame = () => { cancellation.Cancel(); return new(1, 1, new byte[4]); } };
        using var camera = new MediaFoundationCameraCapture(api);
        var delivered = 0;
        camera.Open();
        Assert.Throws<OperationCanceledException>(() => camera.Pump(cancellation.Token, _ => delivered++, _ => { }));
        Assert.Equal(0, delivered);
        Assert.Equal(1, api.Opens);
    }

    [Fact]
    public void CallbackCancellationAndLateNativeNotificationsAreSafe()
    {
        using var callback = new MfCameraApi.CameraCallback();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => callback.Take(cancellation.Token));
        callback.OnReadSample(0, 0, 0x100, 0, IntPtr.Zero);
        Assert.Equal(0x100u, callback.Take(TestContext.Current.CancellationToken).Flags);
        callback.Dispose(); callback.Dispose();
        Assert.Equal(0, callback.OnReadSample(0, 0, 0, 0, IntPtr.Zero));
        Assert.Equal(0, callback.OnFlush(0));
        Assert.Equal(0, callback.OnEvent(0, IntPtr.Zero));
    }

    [Fact]
    public void CallbackDeclaresTheOfficialComInterfaceWithoutCallingNativeApis()
    {
        Assert.Equal(new Guid("deec8d99-fa1d-4d82-84c2-2c8969944867"), typeof(IWindowsCaptureSourceReaderCallback).GUID);
        Assert.Contains(typeof(IWindowsCaptureSourceReaderCallback), typeof(MfCameraApi.CameraCallback).GetInterfaces());
    }

    [Theory]
    [InlineData("open", 0)]
    [InlineData("prepare", 1)]
    [InlineData("add", 2)]
    [InlineData("start", 4)]
    [InlineData("none", 4)]
    public void WavePartialOpenRollbackUnpreparesOnlyAcquiredHeaders(string failure, int prepared)
    {
        var api = new FakeWaveApi { Failure = failure };
        var microphone = new WinMmMicrophoneCapture(api);
        Assert.Empty(api.Calls);
        if (failure == "none") microphone.Open(); else Assert.Throws<IOException>(microphone.Open);
        microphone.Dispose(); microphone.Dispose();
        Assert.Equal(prepared, api.Calls.Count(c => c == "unprepare"));
        Assert.Equal(failure == "open" ? 0 : 1, api.Calls.Count(c => c == "close"));
        if (failure != "open")
        {
            Assert.True(api.Calls.IndexOf("reset") < api.Calls.IndexOf("close"));
            Assert.Equal("close", api.Calls[^1]);
        }
    }

    private sealed class FakeCameraApi : IMfCameraApi
    {
        internal int Opens;
        internal Func<MediaCaptureVideoFrame?> ReadFrame = () => null;
        public void Open() => Opens++;
        public MediaCaptureVideoFrame? Read(CancellationToken cancellation) => ReadFrame();
        public void Dispose() { }
    }

    private sealed class FakeWaveApi : IWaveInApi
    {
        internal string Failure = "none";
        internal readonly List<string> Calls = [];
        private int _prepare, _add;
        public uint Open(out IntPtr handle, ref WaveFormat format, IntPtr ready)
        {
            Calls.Add("open");
            Assert.Equal((ushort)1, format.Tag); Assert.Equal((ushort)16, format.BitsPerSample);
            Assert.Equal(96000u, format.AverageBytesPerSecond);
            handle = Failure == "open" ? IntPtr.Zero : (IntPtr)123;
            return Failure == "open" ? 1u : 0;
        }
        public uint Prepare(IntPtr handle, IntPtr header)
        {
            Calls.Add("prepare");
            return Failure == "prepare" && ++_prepare == 2 ? 1u : 0;
        }
        public uint Add(IntPtr handle, IntPtr header)
        {
            Calls.Add("add");
            return Failure == "add" && ++_add == 2 ? 1u : 0;
        }
        public uint Start(IntPtr handle) { Calls.Add("start"); return Failure == "start" ? 1u : 0; }
        public uint Reset(IntPtr handle) { Calls.Add("reset"); return 0; }
        public uint Unprepare(IntPtr handle, IntPtr header) { Calls.Add("unprepare"); return 0; }
        public uint Close(IntPtr handle) { Calls.Add("close"); return 0; }
    }
}
#endif
