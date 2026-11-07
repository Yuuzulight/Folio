using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Folio.WinForms;

/// <summary>Event-driven PCM16 mono, 48 kHz input. All waveIn calls belong to the capture worker.</summary>
internal sealed class WinMmMicrophoneCapture : ICaptureDevice
{
    private readonly IWaveInApi _api;
    private readonly AutoResetEvent _ready = new(false);
    private readonly List<Buffer> _buffers = [];
    private IntPtr _handle;
    private bool _disposed;
    // If a broken driver refuses to release its buffers, retain ownership rather than freeing
    // memory or an event still referenced by the driver. Normal reset/unprepare never uses this.
    private static readonly List<WinMmMicrophoneCapture> Quarantined = [];
    internal WinMmMicrophoneCapture() : this(new WaveInApi()) { }
    internal WinMmMicrophoneCapture(IWaveInApi api) => _api = api;
    internal const int SampleRate = 48000, BlockBytes = 1920, BufferCount = 4;

    public void Open()
    {
        lock (Quarantined)
            if (_api is WaveInApi && Quarantined.Any(item => item._api is WaveInApi))
                throw new IOException("A previous waveIn driver failed to release capture buffers.");
        var format = new WaveFormat { Tag = 1, Channels = 1, SamplesPerSecond = SampleRate,
            AverageBytesPerSecond = SampleRate * 2, BlockAlign = 2, BitsPerSample = 16 };
        Check(_api.Open(out _handle, ref format, _ready.SafeWaitHandle.DangerousGetHandle()));
        for (var i = 0; i < BufferCount; i++)
        {
            var buffer = new Buffer();
            _buffers.Add(buffer);
            Check(_api.Prepare(_handle, buffer.Header));
            buffer.Prepared = true;
            Check(_api.Add(_handle, buffer.Header));
        }
        Check(_api.Start(_handle));
    }

    public void Pump(CancellationToken cancellation, Action<MediaCaptureVideoFrame> video, Action<MediaAudioFrame> audio)
    {
        WaitHandle[] waits = [cancellation.WaitHandle, _ready];
        var lastData = Environment.TickCount64;
        while (!cancellation.IsCancellationRequested)
        {
            if (WaitHandle.WaitAny(waits, 250) == 0) return;
            foreach (var buffer in _buffers)
            {
                cancellation.ThrowIfCancellationRequested();
                var header = Marshal.PtrToStructure<WaveHeader>(buffer.Header);
                if ((header.Flags & 1) == 0) continue; // WHDR_DONE
                if (header.BytesRecorded > BlockBytes || (header.BytesRecorded & 1) != 0)
                    throw new InvalidDataException("Invalid waveIn block length.");
                if (header.BytesRecorded != 0)
                {
                    var bytes = new byte[(int)header.BytesRecorded];
                    Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);
                    audio(DecodePcm16(bytes));
                    lastData = Environment.TickCount64;
                }
                cancellation.ThrowIfCancellationRequested();
                Check(_api.Add(_handle, buffer.Header));
            }
            // A disconnected driver may stop returning buffers without an error notification.
            if (Environment.TickCount64 - lastData > 5000) throw new IOException("Microphone stopped returning buffers.");
        }
    }

    internal static MediaAudioFrame DecodePcm16(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length > BlockBytes || (bytes.Length & 1) != 0)
            throw new InvalidDataException("Invalid PCM16 block.");
        var samples = new float[bytes.Length / 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(i * 2, 2)) / 32768f;
        return new MediaAudioFrame(SampleRate, 1, samples);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var safe = true;
        if (_handle != IntPtr.Zero)
        {
            safe = _api.Reset(_handle) == 0;
            foreach (var buffer in _buffers)
            {
                if (!buffer.Prepared) continue;
                if (_api.Unprepare(_handle, buffer.Header) == 0) buffer.Prepared = false;
                else safe = false;
            }
            if (safe) safe = _api.Close(_handle) == 0;
        }
        if (!safe)
        {
            lock (Quarantined) Quarantined.Add(this);
            return;
        }
        _handle = IntPtr.Zero;
        foreach (var buffer in _buffers) buffer.Dispose();
        _buffers.Clear();
        _ready.Dispose();
    }

    private static void Check(uint result) { if (result != 0) throw new IOException($"waveIn failed ({result})."); }

    private sealed class Buffer : IDisposable
    {
        internal readonly IntPtr Data = Marshal.AllocHGlobal(BlockBytes);
        internal readonly IntPtr Header;
        internal bool Prepared;
        internal Buffer()
        {
            try
            {
                Header = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
                Marshal.StructureToPtr(new WaveHeader { Data = Data, BufferLength = BlockBytes }, Header, false);
            }
            catch { Marshal.FreeHGlobal(Data); throw; }
        }
        public void Dispose() { Marshal.FreeHGlobal(Header); Marshal.FreeHGlobal(Data); }
    }
}

// WAVEFORMATEX has WORD packing (18 bytes); WAVEHDR uses pointer-sized DWORD_PTR fields.
[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormat
{
    internal ushort Tag, Channels;
    internal uint SamplesPerSecond, AverageBytesPerSecond;
    internal ushort BlockAlign, BitsPerSample, ExtraSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WaveHeader
{
    internal IntPtr Data;
    internal uint BufferLength, BytesRecorded;
    internal UIntPtr User;
    internal uint Flags, Loops;
    internal IntPtr Next;
    internal UIntPtr Reserved;
}

internal interface IWaveInApi
{
    uint Open(out IntPtr handle, ref WaveFormat format, IntPtr ready);
    uint Prepare(IntPtr handle, IntPtr header);
    uint Add(IntPtr handle, IntPtr header);
    uint Start(IntPtr handle);
    uint Reset(IntPtr handle);
    uint Unprepare(IntPtr handle, IntPtr header);
    uint Close(IntPtr handle);
}

internal sealed class WaveInApi : IWaveInApi
{
    private static readonly uint HeaderSize = (uint)Marshal.SizeOf<WaveHeader>();
    public uint Open(out IntPtr handle, ref WaveFormat format, IntPtr ready)
        => waveInOpen(out handle, uint.MaxValue, ref format, ready, UIntPtr.Zero, 0x00050000); // WAVE_MAPPER, CALLBACK_EVENT
    public uint Prepare(IntPtr h, IntPtr p) => waveInPrepareHeader(h, p, HeaderSize);
    public uint Add(IntPtr h, IntPtr p) => waveInAddBuffer(h, p, HeaderSize);
    public uint Start(IntPtr h) => waveInStart(h);
    public uint Reset(IntPtr h) => waveInReset(h);
    public uint Unprepare(IntPtr h, IntPtr p) => waveInUnprepareHeader(h, p, HeaderSize);
    public uint Close(IntPtr h) => waveInClose(h);
    [DllImport("winmm.dll")] private static extern uint waveInOpen(out IntPtr handle, uint device, ref WaveFormat format, IntPtr callback, UIntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveInPrepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInAddBuffer(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInStart(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveInReset(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveInUnprepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInClose(IntPtr handle);
}
