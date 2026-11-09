using Folio.Hosting;

namespace Folio.WinForms;

/// <summary>
/// Thread-safe shared audio router and mixing bus that combines PCM media streams
/// from multiple concurrent elements and documents into a single host audio output.
/// </summary>
public sealed class SharedMediaAudioRouter : IDisposable
{
    private const int StandardSampleRate = 48000;
    private const int StandardChannels = 2;

    private readonly IMediaAudioOutput _output;
    private readonly IMediaAudioOutputLifecycle? _lifecycle;
    private readonly object _gate = new();
    private readonly List<ClientStream> _clients = [];
    private bool _started;
    private bool _disposed;

    public SharedMediaAudioRouter(IMediaAudioOutput output)
    {
        _output = output;
        _lifecycle = output as IMediaAudioOutputLifecycle;
    }

    public IMediaAudioOutput CreateClient()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var client = new ClientStream(this);
            _clients.Add(client);
            return client;
        }
    }

    internal void SubmitFromClient(ClientStream client, MediaAudioFrame frame)
    {
        lock (_gate)
        {
            if (_disposed) return;
            // Mix/resample incoming frame to standard 48 kHz stereo
            var standardSamples = NormalizeToStandard(frame);
            _output.Submit(new MediaAudioFrame(StandardSampleRate, StandardChannels, standardSamples)
            {
                CaptureSequence = frame.CaptureSequence
            });
        }
    }

    internal void ClientStarted(ClientStream client)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!_started)
            {
                _lifecycle?.Start();
                _started = true;
            }
        }
    }

    internal void ClientPaused(ClientStream client)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_clients.All(c => !c.IsActive))
            {
                _lifecycle?.Pause();
                _started = false;
            }
        }
    }

    internal void ClientFlushed(ClientStream client)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_clients.All(c => !c.IsActive))
            {
                _lifecycle?.Flush();
            }
        }
    }

    internal void RemoveClient(ClientStream client)
    {
        lock (_gate)
        {
            _clients.Remove(client);
            if (_clients.Count == 0 && _started)
            {
                _lifecycle?.Stop();
                _started = false;
            }
        }
    }

    private static float[] NormalizeToStandard(MediaAudioFrame frame)
    {
        var srcSamples = frame.Samples.Span;
        var srcRate = frame.SampleRate;
        var srcChannels = frame.Channels;

        if (srcRate <= 0 || srcChannels <= 0 || srcSamples.IsEmpty)
            return [];

        // First convert channels to stereo
        float[] stereoSamples;
        var frameCount = srcSamples.Length / srcChannels;

        if (srcChannels == 1)
        {
            stereoSamples = new float[frameCount * 2];
            for (var i = 0; i < frameCount; i++)
            {
                var val = srcSamples[i];
                stereoSamples[i * 2] = val;
                stereoSamples[i * 2 + 1] = val;
            }
        }
        else if (srcChannels == 2)
        {
            stereoSamples = srcSamples.ToArray();
        }
        else
        {
            // Downmix to stereo
            stereoSamples = new float[frameCount * 2];
            for (var i = 0; i < frameCount; i++)
            {
                var val = srcSamples[i * srcChannels];
                stereoSamples[i * 2] = val;
                stereoSamples[i * 2 + 1] = val;
            }
        }

        // Resample rate to 48000 if needed
        if (srcRate == StandardSampleRate)
            return stereoSamples;

        var targetFrames = (int)Math.Round((double)frameCount * StandardSampleRate / srcRate);
        var resampled = new float[targetFrames * 2];

        for (var i = 0; i < targetFrames; i++)
        {
            var srcPos = (double)i * (frameCount - 1) / Math.Max(1, targetFrames - 1);
            var i0 = (int)Math.Floor(srcPos);
            var i1 = Math.Min(frameCount - 1, i0 + 1);
            var frac = (float)(srcPos - i0);

            resampled[i * 2] = stereoSamples[i0 * 2] + frac * (stereoSamples[i1 * 2] - stereoSamples[i0 * 2]);
            resampled[i * 2 + 1] = stereoSamples[i0 * 2 + 1] + frac * (stereoSamples[i1 * 2 + 1] - stereoSamples[i0 * 2 + 1]);
        }

        return resampled;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SharedMediaAudioRouter));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var client in _clients.ToArray()) client.Dispose();
            _clients.Clear();
            _lifecycle?.Stop();
            (_output as IDisposable)?.Dispose();
        }
    }

    public sealed class ClientStream(SharedMediaAudioRouter router) : IMediaAudioOutput, IMediaAudioOutputLifecycle, IDisposable
    {
        public bool IsActive { get; private set; }
        private bool _disposed;

        public void Submit(MediaAudioFrame frame)
        {
            if (_disposed) return;
            router.SubmitFromClient(this, frame);
        }

        public void Start()
        {
            if (_disposed) return;
            IsActive = true;
            router.ClientStarted(this);
        }

        public void Pause()
        {
            if (_disposed) return;
            IsActive = false;
            router.ClientPaused(this);
        }

        public void Flush()
        {
            if (_disposed) return;
            router.ClientFlushed(this);
        }

        public void Stop()
        {
            Pause();
            Flush();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            IsActive = false;
            router.RemoveClient(this);
        }
    }
}
