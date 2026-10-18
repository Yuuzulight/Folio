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
    private readonly List<NativeBuffer> _buffers = [];
    private IntPtr _waveOut;
    private int _sampleRate;
    private int _channels;
    private bool _started;
    private bool _disposed;

    public WinMmMediaAudioOutput(uint deviceId = WaveMapper) => _deviceId = deviceId;

    public void Submit(MediaAudioFrame frame)
    {
        if (frame.SampleRate <= 0 || frame.Channels is < 1 or > 2 || frame.Samples.IsEmpty)
            return;

        lock (_gate)
        {
            ThrowIfDisposed();
            DrainCompleted();
            EnsureDevice(frame.SampleRate, frame.Channels);
            var samples = frame.Samples.Span;
            var pcm = new short[samples.Length];
            for (var i = 0; i < samples.Length; i++)
                pcm[i] = (short)Math.Clamp((int)MathF.Round(samples[i] * short.MaxValue), short.MinValue, short.MaxValue);

            var buffer = new NativeBuffer(pcm);
            var prepared = waveOutPrepareHeader(_waveOut, buffer.Header, (uint)Marshal.SizeOf<WaveHeader>()) == 0;
            if (!prepared || waveOutWrite(_waveOut, buffer.Header, (uint)Marshal.SizeOf<WaveHeader>()) != 0)
            {
                if (prepared)
                    waveOutUnprepareHeader(_waveOut, buffer.Header, (uint)Marshal.SizeOf<WaveHeader>());
                buffer.Dispose();
                throw new InvalidOperationException("The Windows wave-out device rejected an audio buffer.");
            }
            _buffers.Add(buffer);
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_waveOut != IntPtr.Zero)
                waveOutRestart(_waveOut);
            _started = true;
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_waveOut != IntPtr.Zero)
                waveOutPause(_waveOut);
            _started = false;
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (_waveOut == IntPtr.Zero)
                return;
            waveOutReset(_waveOut);
            DrainCompleted(force: true);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_waveOut != IntPtr.Zero)
            {
                waveOutReset(_waveOut);
                DrainCompleted(force: true);
                waveOutClose(_waveOut);
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
            Stop();
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
        var result = waveOutOpen(out _waveOut, _deviceId, ref format, IntPtr.Zero, IntPtr.Zero, CallbackNull);
        if (result != 0)
            throw new InvalidOperationException($"Unable to open the Windows wave-out device (error {result}).");
        _sampleRate = sampleRate;
        _channels = channels;
        if (_started)
            waveOutRestart(_waveOut);
    }

    private void DrainCompleted(bool force = false)
    {
        for (var i = _buffers.Count - 1; i >= 0; i--)
        {
            var buffer = _buffers[i];
            var header = Marshal.PtrToStructure<WaveHeader>(buffer.Header);
            if (force || (header.Flags & WhdrDone) != 0)
            {
                if (_waveOut != IntPtr.Zero)
                    waveOutUnprepareHeader(_waveOut, buffer.Header, (uint)Marshal.SizeOf<WaveHeader>());
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
            Header = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
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
    private struct WaveFormat
    {
        public ushort FormatTag, Channels;
        public uint SamplesPerSecond, AverageBytesPerSecond;
        public ushort BlockAlign, BitsPerSample, ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength, BytesRecorded, User, Flags, Loops;
        public IntPtr Next, Reserved;
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
