using System.Diagnostics;

namespace Folio.Scripting.Ipc;

internal sealed record ContentCaptureSample(MediaCaptureVideoFrame? Video = null, MediaAudioFrame? Audio = null,
    long VideoSource = 0, long AudioSource = 0, long VideoTimestamp = 0, long AudioTimestamp = 0);

/// <summary>Owns bounded copied device samples. No provider callback writes to the protocol.</summary>
internal sealed class ContentCaptureLease : IDisposable
{
    internal const int MaxVideoBytes = 8 * 1024 * 1024;
    internal const int MaxAudioSamples = 32768;
    internal const int MaxAudioBlocks = 16;
    internal const int MaxAudioBytes = 512 * 1024;
    private readonly object _gate = new();
    private readonly Action _signal;
    private readonly Func<MediaCaptureRequest, bool> _granted;
    private readonly Queue<ContentCaptureSample> _audio = new();
    private ContentCaptureSample? _video, _publishedVideo, _publishedAudio;
    private ReadOnlyMemory<byte> _videoIdentity;
    private ReadOnlyMemory<float> _audioIdentity;
    private long _videoSource, _audioSource, _videoSequence, _audioSequence;
    private int _audioBytes, _disposed, _revoked;
    private bool _audioStopped, _videoStopped, _ready;
    private readonly Action<MediaCaptureVideoFrame> _videoHandler;
    private readonly Action<MediaAudioFrame> _audioHandler;
    private readonly Action _revokedHandler;
    internal IMediaCaptureSession Session { get; }
    internal MediaCaptureRequest Request { get; }
    internal bool Revoked => Volatile.Read(ref _revoked) != 0;
    internal MediaCaptureRequest ActiveRequest { get { lock (_gate) return new(Request.Audio && !_audioStopped, Request.Video && !_videoStopped); } }
    internal int QueuedAudioBytes { get { lock (_gate) return _audioBytes; } }
    internal int QueuedAudioBlocks { get { lock (_gate) return _audio.Count; } }

    internal ContentCaptureLease(IMediaCaptureSession session, MediaCaptureRequest request,
        Func<MediaCaptureRequest, bool> granted, Action signal)
    {
        Session = session; Request = request; _granted = granted; _signal = signal;
        _videoHandler = AcceptVideo; _audioHandler = AcceptAudio;
        _revokedHandler = () => { Interlocked.Exchange(ref _revoked, 1); _signal(); };
        try
        {
            session.VideoFrameAvailable += _videoHandler;
            session.AudioFrameAvailable += _audioHandler;
            session.Revoked += _revokedHandler;
        }
        catch { Dispose(); throw; }
    }

    private bool Live(bool audio) => _disposed == 0 && !Revoked
        && (audio ? Request.Audio && !_audioStopped : Request.Video && !_videoStopped)
        && _granted(new(audio, !audio));

    private void AcceptVideo(MediaCaptureVideoFrame frame)
    {
        if (!frame.IsValid || frame.Rgba.Length > MaxVideoBytes || frame.CaptureSequence < 0) return;
        lock (_gate)
        {
            if (!Live(false) || frame.CaptureSequence > 0 && frame.CaptureSequence <= _videoSource
                || frame.CaptureSequence == 0 && _videoSequence > 0 && frame.Rgba.Equals(_videoIdentity)) return;
            _videoSource = Math.Max(_videoSource, frame.CaptureSequence); _videoIdentity = frame.Rgba;
            var owned = frame with { Rgba = frame.Rgba.ToArray(), CaptureSequence = checked(++_videoSequence) };
            _video = new(Video: owned, VideoSource: frame.CaptureSequence, VideoTimestamp: Stopwatch.GetTimestamp());
        }
        _signal();
    }

    private void AcceptAudio(MediaAudioFrame frame)
    {
        if (frame.SampleRate is < 1 or > 192000 || frame.Channels is < 1 or > 8 || frame.Samples.Length == 0
            || frame.Samples.Length > MaxAudioSamples || frame.Samples.Length % frame.Channels != 0 || frame.CaptureSequence < 0) return;
        lock (_gate)
        {
            if (!Live(true) || frame.CaptureSequence > 0 && frame.CaptureSequence <= _audioSource
                || frame.CaptureSequence == 0 && _audioSequence > 0 && frame.Samples.Equals(_audioIdentity)) return;
            _audioSource = Math.Max(_audioSource, frame.CaptureSequence); _audioIdentity = frame.Samples;
            if (_audio.Count >= MaxAudioBlocks || _audioBytes + frame.Samples.Length * sizeof(float) > MaxAudioBytes) return;
            var samples = frame.Samples.ToArray();
            if (samples.Any(sample => !float.IsFinite(sample))) return;
            var owned = frame with { Samples = samples, CaptureSequence = checked(++_audioSequence) };
            _audio.Enqueue(new(Audio: owned, AudioSource: frame.CaptureSequence, AudioTimestamp: Stopwatch.GetTimestamp()));
            _audioBytes += samples.Length * sizeof(float);
        }
        _signal();
    }

    internal void Poll()
    {
        var request = ActiveRequest;
        // Getters are arbitrary host code. Accept* rechecks lifetime and grants
        // after each getter, including reentrant navigation or revocation.
        if (request.Video && Session.CurrentVideoFrame is { } video) AcceptVideo(video);
        if (request.Audio && Session.CurrentAudioFrame is { } audio) AcceptAudio(audio);
    }

    internal ContentCaptureSample Initial()
    {
        Poll();
        lock (_gate) return TakeLocked();
    }

    internal void Activate(ContentCaptureSample initial)
    {
        lock (_gate) { if (_disposed != 0) return; PublishLocked(initial); _ready = true; }
        _signal();
    }

    internal ContentCaptureSample Published()
    {
        Poll();
        lock (_gate)
            return new(_videoStopped ? null : _publishedVideo?.Video, _audioStopped ? null : _publishedAudio?.Audio,
                _publishedVideo?.VideoSource ?? 0, _publishedAudio?.AudioSource ?? 0,
                _publishedVideo?.VideoTimestamp ?? 0, _publishedAudio?.AudioTimestamp ?? 0);
    }

    internal ContentCaptureSample Take()
    {
        lock (_gate) return _ready && _disposed == 0 && !Revoked ? TakeLocked() : new();
    }

    private ContentCaptureSample TakeLocked()
    {
        var video = Live(false) ? _video : null; _video = null;
        ContentCaptureSample? audio = null;
        if (Live(true) && _audio.TryDequeue(out audio)) _audioBytes -= audio.Audio!.Value.Samples.Length * sizeof(float);
        return new(video?.Video, audio?.Audio, video?.VideoSource ?? 0, audio?.AudioSource ?? 0,
            video?.VideoTimestamp ?? 0, audio?.AudioTimestamp ?? 0);
    }

    internal void Publish(ContentCaptureSample sample) { lock (_gate) PublishLocked(sample); }
    private void PublishLocked(ContentCaptureSample sample)
    {
        if (_disposed != 0 || Revoked) return;
        if (!_videoStopped && sample.Video is not null)
            _publishedVideo = new(Video: sample.Video, VideoSource: sample.VideoSource, VideoTimestamp: sample.VideoTimestamp);
        if (!_audioStopped && sample.Audio is not null)
            _publishedAudio = new(Audio: sample.Audio, AudioSource: sample.AudioSource, AudioTimestamp: sample.AudioTimestamp);
    }

    internal bool Stop(bool audio)
    {
        lock (_gate)
        {
            if (_disposed != 0 || (audio ? !Request.Audio : !Request.Video)) return false;
            if (audio ? _audioStopped : _videoStopped) return true;
            if (audio) { _audioStopped = true; _audio.Clear(); _audioBytes = 0; _publishedAudio = null; _audioIdentity = default; }
            else { _videoStopped = true; _video = _publishedVideo = null; _videoIdentity = default; }
        }
        if (Session is IMediaCaptureTrackControl control)
        {
            if (audio) control.StopAudio(); else control.StopVideo();
        }
        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed != 0) return;
            _disposed = 1; _audio.Clear(); _audioBytes = 0;
            _video = _publishedVideo = _publishedAudio = null; _audioIdentity = default; _videoIdentity = default;
        }
        try { Session.VideoFrameAvailable -= _videoHandler; }
        finally
        {
            try { Session.AudioFrameAvailable -= _audioHandler; }
            finally { try { Session.Revoked -= _revokedHandler; } finally { Session.Dispose(); } }
        }
    }
}
