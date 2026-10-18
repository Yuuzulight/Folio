using System.Reflection;
using System.Runtime.InteropServices;
using Folio.WinForms;

namespace Folio.Scripting.Tests;

public sealed class NativeMediaTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool HasOutputDevice => IsWindows && waveOutGetNumDevs() > 0;

    [DllImport("winmm.dll")]
    private static extern uint waveOutGetNumDevs();

    [Fact(Skip = "Windows audio output device required", SkipUnless = nameof(HasOutputDevice))]
    public void NativeDeviceAcceptsSilentPcmAndLifecycleCommands()
    {
        using var output = new WinMmMediaAudioOutput();
        output.Start();
        output.Submit(new(48000, 2, new float[1920]));
        output.Pause();
        output.Flush();
        output.Start();
        output.Submit(new(48000, 2, new float[1920]));
        output.Stop();
    }

    [Fact(Skip = "Windows system codecs required", SkipUnless = nameof(IsWindows))]
    public void ConcurrentDecodesBalanceNativeStartupAndCleanup()
    {
        Parallel.For(0, 8, _ => Assert.NotNull(new MediaFoundationAudioDecoder().Decode(Wav(), "audio/wav", false, 1024 * 1024)));
    }

    [Fact(Skip = "Windows system codecs required", SkipUnless = nameof(IsWindows))]
    public void NativeAudioReachesEndOfStreamWithActualFormatAndSamples()
    {
        var result = new MediaFoundationAudioDecoder().Decode(Wav(), "audio/wav", false, 1024 * 1024);
        Assert.NotNull(result);
        Assert.NotNull(result.Audio);
        Assert.Equal(8000, result.Audio.Value.SampleRate);
        Assert.Equal(1, result.Audio.Value.Channels);
        Assert.Equal(800, result.Audio.Value.Samples.Length);
        Assert.Equal(0.1, result.Duration, 5);
        Assert.All(result.Audio.Value.Samples.ToArray(), value => Assert.InRange(value, 0.24f, 0.26f));
    }

    [Fact(Skip = "Windows system codecs required", SkipUnless = nameof(IsWindows))]
    public void CompressedAudioAndVideoUseSystemCodecs()
    {
        var decoder = new MediaFoundationAudioDecoder();
        var audio = decoder.Decode(File.ReadAllBytes(Fixture("tone.mp3")), "audio/mpeg", false, 1024 * 1024);
        Assert.NotNull(audio?.Audio);
        Assert.Equal(22050, audio.Audio.Value.SampleRate);
        Assert.True(audio.Duration >= 0.09);
        Assert.Contains(audio.Audio.Value.Samples.ToArray(), sample => Math.Abs(sample) > 0.01f);
        var video = decoder.Decode(File.ReadAllBytes(Fixture("red.mp4")), "video/mp4", true, 1024 * 1024);
        Assert.NotNull(video?.VideoFrame);
        Assert.Equal(64, video.VideoFrame.Width);
        Assert.Equal(48, video.VideoFrame.Height);
        Assert.Equal(1, video.Duration, 2);
        var pixel = video.VideoFrame.Pixels.Span;
        Assert.InRange(pixel[0], (byte)240, (byte)255);
        Assert.InRange(pixel[1], (byte)0, (byte)15);
        Assert.Equal(255, pixel[3]);
    }

    [Fact(Skip = "Windows system codecs required", SkipUnless = nameof(IsWindows))]
    public void CorruptInputsAndInsufficientOutputBudgetAreRejected()
    {
        var decoder = new MediaFoundationAudioDecoder();
        Assert.Null(decoder.Decode(new byte[64], null, false, 1024));
        Assert.Null(decoder.Decode(Wav(), "audio/wav", false, 2000));
        var video = File.ReadAllBytes(Fixture("red.mp4"));
        Assert.Null(decoder.Decode(video, "video/mp4", true, video.Length - 1));
        Assert.Null(decoder.Decode(video, "video/mp4", true, 2048));
        Assert.Null(decoder.Decode(video.AsMemory(0, 32), "video/mp4", true, 1024 * 1024));
        // Decode after failures to check native startup/shutdown remains balanced.
        Assert.NotNull(decoder.Decode(Wav(), "audio/wav", false, 1024 * 1024));
    }

    [Fact]
    public void WaveHeaderMatchesWindowsPointerSizeAndOffsets()
    {
        var type = typeof(WinMmMediaAudioOutput).GetNestedType("WaveHeader", BindingFlags.NonPublic)!;
        Assert.Equal(IntPtr.Size == 8 ? 48 : 32, Marshal.SizeOf(type));
        Assert.Equal(IntPtr.Size == 8 ? 24 : 16, Marshal.OffsetOf(type, "Flags").ToInt32());
    }

    [Fact]
    public void WaveOutputOwnsBuffersUntilResetAndBoundsItsQueue()
    {
        var api = new FakeWaveOut();
        using var output = new WinMmMediaAudioOutput(api, 0, 16);
        var frame = new MediaAudioFrame(8000, 1, new float[] { float.NaN, 2, -2, 0.25f });
        output.Submit(frame);
        Assert.Equal(new short[] { 0, 32767, -32768, 8192 }, api.LastSamples);
        Assert.Equal(new[] { "open", "pause", "prepare", "write" }, api.Calls);
        output.Start();
        output.Submit(frame);
        Assert.Throws<InvalidOperationException>(() => output.Submit(frame));
        output.Pause();
        output.Flush();
        Assert.Equal(2, api.Calls.Count(value => value == "unprepare"));
        output.Start();
        output.Submit(frame);
        output.Stop();
        Assert.Equal("close", api.Calls[^1]);
    }

    [Fact]
    public void WriteFailureUnpreparesBeforeFreeingAndDisposalIsIdempotent()
    {
        var api = new FakeWaveOut { FailWrite = true };
        var output = new WinMmMediaAudioOutput(api, 0, 1024);
        Assert.Throws<InvalidOperationException>(() => output.Submit(new(8000, 1, new float[4])));
        Assert.Equal("unprepare", api.Calls[^1]);
        output.Dispose();
        output.Dispose();
        Assert.Equal(1, api.Calls.Count(value => value == "close"));
        Assert.Throws<ObjectDisposedException>(() => output.Start());
    }

    [Fact]
    public void DriverOwnedFailedBufferIsRetainedUntilResetAndFormatChangeKeepsPlaying()
    {
        var api = new FakeWaveOut { FailWrite = true, RefuseUnprepare = true };
        using var output = new WinMmMediaAudioOutput(api, 0, 8);
        Assert.Throws<InvalidOperationException>(() => output.Submit(new(8000, 1, new float[4])));
        Assert.Throws<InvalidOperationException>(() => output.Submit(new(8000, 1, new float[4])));
        api.RefuseUnprepare = false;
        api.FailWrite = false;
        output.Flush();
        output.Start();
        output.Submit(new(8000, 1, new float[4]));
        output.Flush();
        var pauseCount = api.Calls.Count(call => call == "pause");
        output.Submit(new(16000, 1, new float[4]));
        Assert.Equal(pauseCount, api.Calls.Count(call => call == "pause"));
        Assert.Equal(2, api.Calls.Count(call => call == "open"));
    }

    private sealed class FakeWaveOut : WinMmMediaAudioOutput.IWaveOutApi
    {
        public List<string> Calls { get; } = [];
        public short[] LastSamples { get; private set; } = [];
        public bool FailWrite { get; set; }
        public bool RefuseUnprepare { get; set; }
        public uint Open(out IntPtr handle, uint id, ref WinMmMediaAudioOutput.WaveFormat format)
        { Calls.Add("open"); handle = new IntPtr(1); return 0; }
        public uint Prepare(IntPtr handle, IntPtr header, uint size) { Calls.Add("prepare"); return 0; }
        public uint Write(IntPtr handle, IntPtr header, uint size)
        {
            Calls.Add("write");
            var length = Marshal.ReadInt32(header, IntPtr.Size);
            LastSamples = new short[length / 2];
            Marshal.Copy(Marshal.ReadIntPtr(header), LastSamples, 0, LastSamples.Length);
            return FailWrite ? 1u : 0;
        }
        public uint Unprepare(IntPtr handle, IntPtr header, uint size) { Calls.Add("unprepare"); return RefuseUnprepare ? 33u : 0; }
        public uint Pause(IntPtr handle) { Calls.Add("pause"); return 0; }
        public uint Restart(IntPtr handle) { Calls.Add("restart"); return 0; }
        public uint Reset(IntPtr handle) { Calls.Add("reset"); return 0; }
        public uint Close(IntPtr handle) { Calls.Add("close"); return 0; }
    }

    private static string Fixture(string file) => Path.Combine(AppContext.BaseDirectory, "Native", "Fixtures", file);

    internal static byte[] Wav()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36 + 1600); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(8000);
        writer.Write(16000); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(1600);
        for (var i = 0; i < 800; i++) writer.Write((short)8192);
        return stream.ToArray();
    }
}
