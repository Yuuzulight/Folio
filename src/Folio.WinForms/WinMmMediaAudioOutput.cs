using System.Runtime.InteropServices;
using Folio.Hosting;

namespace Folio.WinForms;

/// <summary>Streams Folio's PCM media frames to the Windows wave-out device.</summary>
public sealed class WinMmMediaAudioOutput : IMediaAudioOutput, IMediaAudioOutputLifecycle, IDisposable
{
    private const uint CallbackNull = 0;
    private const uint WaveMapper = unchecked((uint)-1);
    private const uint WaveFormatPcm = 1;
    private const uint WhdrDone = 1;
    private readonly object _gate = new();
    private readonly uint _deviceId;
    private readonly IWaveOutApi _api;
    private readonly int _maxQueuedBytes;
    private int _queuedBytes;
    private static readonly uint HeaderSize = (uint)Marshal.SizeOf<WaveHeader>();
    private readonly List<NativeBuffer> _buffers = [];
    private IntPtr _waveOut;
    private int _sampleRate;
    private int _channels;
    private bool _started;
    private bool _disposed;

    public WinMmMediaAudioOutput(uint deviceId = WaveMapper) : this(new NativeWaveOutApi(), deviceId, 4 * 1024 * 1024) { }

    internal WinMmMediaAudioOutput(IWaveOutApi api, uint deviceId, int maxQueuedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxQueuedBytes);
        _api = api;
        _deviceId = deviceId;
        _maxQueuedBytes = maxQueuedBytes;
    }

    public void Submit(MediaAudioFrame frame)
    {
        if (frame.SampleRate is <= 0 or > 384000 || frame.Channels is < 1 or > 2 || frame.Samples.IsEmpty || frame.Samples.Length % frame.Channels != 0)
            return;

        lock (_gate)
        {
            ThrowIfDisposed();
            DrainCompleted();
            var byteCount = checked(frame.Samples.Length * sizeof(short));
            if (byteCount > _maxQueuedBytes - _queuedBytes)
                throw new InvalidOperationException("The Windows audio queue exceeds its byte limit.");
            EnsureDevice(frame.SampleRate, frame.Channels);
            var samples = frame.Samples.Span;
            var pcm = new short[samples.Length];
            for (var i = 0; i < samples.Length; i++)
                pcm[i] = float.IsNaN(samples[i]) ? (short)0 : (short)Math.Clamp(MathF.Round(samples[i] * 32768f), short.MinValue, short.MaxValue);

            var buffer = new NativeBuffer(pcm);
            var prepared = _api.Prepare(_waveOut, buffer.Header, HeaderSize) == 0;
            if (!prepared || _api.Write(_waveOut, buffer.Header, HeaderSize) != 0)
            {
                if (prepared && _api.Unprepare(_waveOut, buffer.Header, HeaderSize) != 0)
                {
                    // Retain native memory until the driver relinquishes the prepared header.
                    _buffers.Add(buffer);
                    _queuedBytes += byteCount;
                }
                else buffer.Dispose();
                throw new InvalidOperationException("The Windows wave-out device rejected an audio buffer.");
            }
            _buffers.Add(buffer);
            _queuedBytes += byteCount;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_waveOut != IntPtr.Zero)
                Check(_api.Restart(_waveOut));
            _started = true;
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_waveOut != IntPtr.Zero)
                Check(_api.Pause(_waveOut));
            _started = false;
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (_waveOut == IntPtr.Zero)
                return;
            Check(_api.Reset(_waveOut));
            DrainCompleted(force: true);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_waveOut != IntPtr.Zero)
            {
                Check(_api.Reset(_waveOut));
                DrainCompleted(force: true);
                Check(_api.Close(_waveOut));
                _waveOut = IntPtr.Zero;
            }
            _started = false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            Stop();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    private void EnsureDevice(int sampleRate, int channels)
    {
        if (_waveOut != IntPtr.Zero && (_sampleRate != sampleRate || _channels != channels))
        {
            var wasStarted = _started;
            Stop();
            _started = wasStarted;
        }
        if (_waveOut != IntPtr.Zero)
            return;

        var format = new WaveFormat
        {
            FormatTag = (ushort)WaveFormatPcm,
            Channels = (ushort)channels,
            SamplesPerSecond = (uint)sampleRate,
            BitsPerSample = 16,
            BlockAlign = (ushort)(channels * 2),
            AverageBytesPerSecond = (uint)(sampleRate * channels * 2)
        };
        var result = _api.Open(out _waveOut, _deviceId, ref format);
        if (result != 0)
            throw new InvalidOperationException($"Unable to open the Windows wave-out device (error {result}).");
        _sampleRate = sampleRate;
        _channels = channels;
        if (!_started)
            Check(_api.Pause(_waveOut));
    }

    private void DrainCompleted(bool force = false)
    {
        for (var i = _buffers.Count - 1; i >= 0; i--)
        {
            var buffer = _buffers[i];
            var header = Marshal.PtrToStructure<WaveHeader>(buffer.Header);
            if (force || (header.Flags & WhdrDone) != 0)
            {
                if (_waveOut != IntPtr.Zero && _api.Unprepare(_waveOut, buffer.Header, HeaderSize) != 0)
                {
                    if (force) throw new InvalidOperationException("The Windows audio device still owns a buffer.");
                    continue;
                }
                _queuedBytes -= checked((int)header.BufferLength);
                buffer.Dispose();
                _buffers.RemoveAt(i);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WinMmMediaAudioOutput));
    }

    private sealed class NativeBuffer : IDisposable
    {
        private readonly IntPtr _data;
        public IntPtr Header { get; }

        public NativeBuffer(short[] samples)
        {
            _data = Marshal.AllocHGlobal(Math.Max(1, samples.Length * sizeof(short)));
            try { Header = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>()); }
            catch { Marshal.FreeHGlobal(_data); throw; }
            if (samples.Length != 0)
            {
                Marshal.Copy(samples, 0, _data, samples.Length);
                Marshal.StructureToPtr(new WaveHeader { Data = _data, BufferLength = (uint)(samples.Length * sizeof(short)) }, Header, false);
            }
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(_data);
            Marshal.FreeHGlobal(Header);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct WaveFormat
    {
        public ushort FormatTag, Channels;
        public uint SamplesPerSecond, AverageBytesPerSecond;
        public ushort BlockAlign, BitsPerSample, ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength, BytesRecorded;
        public UIntPtr User;
        public uint Flags, Loops;
        public IntPtr Next, Reserved;
    }

    private static void Check(uint result)
    {
        if (result != 0) throw new InvalidOperationException($"The Windows audio device failed (error {result}).");
    }

    internal interface IWaveOutApi
    {
        uint Open(out IntPtr handle, uint deviceId, ref WaveFormat format);
        uint Prepare(IntPtr handle, IntPtr header, uint size);
        uint Write(IntPtr handle, IntPtr header, uint size);
        uint Unprepare(IntPtr handle, IntPtr header, uint size);
        uint Pause(IntPtr handle);
        uint Restart(IntPtr handle);
        uint Reset(IntPtr handle);
        uint Close(IntPtr handle);
    }

    private sealed class NativeWaveOutApi : IWaveOutApi
    {
        public uint Open(out IntPtr handle, uint deviceId, ref WaveFormat format) => waveOutOpen(out handle, deviceId, ref format, IntPtr.Zero, IntPtr.Zero, CallbackNull);
        public uint Prepare(IntPtr handle, IntPtr header, uint size) => waveOutPrepareHeader(handle, header, size);
        public uint Write(IntPtr handle, IntPtr header, uint size) => waveOutWrite(handle, header, size);
        public uint Unprepare(IntPtr handle, IntPtr header, uint size) => waveOutUnprepareHeader(handle, header, size);
        public uint Pause(IntPtr handle) => waveOutPause(handle);
        public uint Restart(IntPtr handle) => waveOutRestart(handle);
        public uint Reset(IntPtr handle) => waveOutReset(handle);
        public uint Close(IntPtr handle) => waveOutClose(handle);
    }

    [DllImport("winmm.dll", EntryPoint = "waveOutOpen")]
    private static extern uint waveOutOpen(out IntPtr handle, uint deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll", EntryPoint = "waveOutPrepareHeader")]
    private static extern uint waveOutPrepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll", EntryPoint = "waveOutWrite")]
    private static extern uint waveOutWrite(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll", EntryPoint = "waveOutUnprepareHeader")]
    private static extern uint waveOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll", EntryPoint = "waveOutPause")]
    private static extern uint waveOutPause(IntPtr handle);
    [DllImport("winmm.dll", EntryPoint = "waveOutRestart")]
    private static extern uint waveOutRestart(IntPtr handle);
    [DllImport("winmm.dll", EntryPoint = "waveOutReset")]
    private static extern uint waveOutReset(IntPtr handle);
    [DllImport("winmm.dll", EntryPoint = "waveOutClose")]
    private static extern uint waveOutClose(IntPtr handle);
}
