using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Folio.WinForms.Tests")]

namespace Folio.WinForms;

/// <summary>Opt-in Windows device capture. The embedding host must authorize every Open call.
/// Construction does not enumerate or open devices. This provider never grants permission.</summary>
public sealed class WindowsMediaCaptureProvider : IMediaCaptureProvider
{
    private readonly Func<ICaptureDevice> _camera;
    private readonly Func<ICaptureDevice> _microphone;
    private readonly TimeSpan _timeout;

    public WindowsMediaCaptureProvider() : this(() => new MediaFoundationCameraCapture(),
        () => new WinMmMicrophoneCapture(), TimeSpan.FromSeconds(3)) { }

    internal WindowsMediaCaptureProvider(Func<ICaptureDevice> camera, Func<ICaptureDevice> microphone, TimeSpan timeout)
        => (_camera, _microphone, _timeout) = (camera, microphone, timeout);

    public IMediaCaptureSession? Open(MediaCaptureRequest request)
    {
        if (!request.Audio && !request.Video) return null;
        var session = new WindowsMediaCaptureSession(_timeout);
        try
        {
            // All requested devices must open before either is allowed to publish.
            if (request.Video) session.Add(_camera(), video: true);
            if (request.Audio) session.Add(_microphone(), video: false);
            session.Start();
            return session;
        }
        catch
        {
            session.Dispose();
            return null;
        }
    }
}

// Device objects are created without side effects. Open/Pump/Dispose all execute on one owned
// background worker. Cancellation must wake Pump without a native synchronous read.
internal interface ICaptureDevice : IDisposable
{
    void Open();
    void Pump(CancellationToken cancellation, Action<MediaCaptureVideoFrame> video, Action<MediaAudioFrame> audio);
}

/// <summary>A host-owned session. StopAudio/StopVideo release only that device. Revoked means
/// the entire session has lost a device; hosts should end its tracks. Samples are defensive copies.
/// A subscriber already in flight may finish after stop; stop never waits indefinitely for user code.
/// If a native driver stalls, bounded disposal cancels publication but cannot promise device closure.</summary>
public sealed class WindowsMediaCaptureSession : IMediaCaptureSession, IMediaCaptureTrackControl
{
    private readonly object _gate = new();
    private readonly TimeSpan _timeout;
    private CaptureWorker? _camera, _microphone;
    private MediaCaptureVideoFrame? _video;
    private MediaAudioFrame? _audio;
    private long _videoSequence, _audioSequence;
    private bool _stopped, _videoStopped, _audioStopped;
    private int _revoked;

    internal WindowsMediaCaptureSession(TimeSpan timeout) => _timeout = timeout;
    public event Action<MediaCaptureVideoFrame>? VideoFrameAvailable;
    public event Action<MediaAudioFrame>? AudioFrameAvailable;
    public event Action? Revoked;

    // Capacity is exactly one sample per kind. Consumers cannot mutate the stored snapshot.
    public MediaCaptureVideoFrame? CurrentVideoFrame { get { lock (_gate) return _video is { } f ? Copy(f) : null; } }
    public MediaAudioFrame? CurrentAudioFrame { get { lock (_gate) return _audio is { } f ? Copy(f) : null; } }
    private static MediaCaptureVideoFrame Copy(MediaCaptureVideoFrame f) => f with { Rgba = f.Rgba.ToArray() };
    private static MediaAudioFrame Copy(MediaAudioFrame f) => f with { Samples = f.Samples.ToArray() };

    internal void Add(ICaptureDevice device, bool video)
    {
        var worker = new CaptureWorker(device, PublishVideo, PublishAudio, Lost);
        if (video) _camera = worker; else _microphone = worker;
        worker.Open(_timeout);
    }

    internal void Start() { _camera?.Start(); _microphone?.Start(); }

    private void PublishVideo(MediaCaptureVideoFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || (long)frame.Width * frame.Height * 4 > 32 * 1024 * 1024
            || (long)frame.Width * frame.Height * 4 != frame.Rgba.Length) throw new InvalidDataException("Invalid camera frame.");
        Action<MediaCaptureVideoFrame>? handlers;
        lock (_gate)
        {
            if (_stopped || _videoStopped) return;
            // Native/legacy input identity is not authoritative. Assign once at ingestion;
            // polling and subscriber copies preserve this identity even with fresh arrays.
            frame = Copy(frame) with { CaptureSequence = checked(++_videoSequence) };
            _video = frame;
            handlers = VideoFrameAvailable;
        }
        if (handlers is null) return;
        foreach (Action<MediaCaptureVideoFrame> handler in handlers.GetInvocationList())
        {
            lock (_gate) { if (_stopped || _videoStopped) return; }
            try { handler(Copy(frame)); } catch { /* A subscriber must not kill capture. */ }
        }
    }

    private void PublishAudio(MediaAudioFrame frame)
    {
        if (frame.SampleRate is < 8000 or > 192000 || frame.Channels is < 1 or > 8
            || frame.Samples.Length == 0 || frame.Samples.Length > 192000 || frame.Samples.Length % frame.Channels != 0)
            throw new InvalidDataException("Invalid microphone block.");
        Action<MediaAudioFrame>? handlers;
        lock (_gate)
        {
            if (_stopped || _audioStopped) return;
            frame = Copy(frame) with { CaptureSequence = checked(++_audioSequence) };
            _audio = frame;
            handlers = AudioFrameAvailable;
        }
        if (handlers is null) return;
        foreach (Action<MediaAudioFrame> handler in handlers.GetInvocationList())
        {
            lock (_gate) { if (_stopped || _audioStopped) return; }
            try { handler(Copy(frame)); } catch { }
        }
    }

    private void Lost()
    {
        if (Interlocked.Exchange(ref _revoked, 1) != 0) return;
        Dispose();
        var handlers = Revoked;
        if (handlers is not null)
            foreach (Action handler in handlers.GetInvocationList()) try { handler(); } catch { }
    }

    public void StopVideo()
    {
        CaptureWorker? worker;
        lock (_gate) { _videoStopped = true; _video = null; worker = _camera; _camera = null; }
        worker?.Stop(_timeout);
    }

    public void StopAudio()
    {
        CaptureWorker? worker;
        lock (_gate) { _audioStopped = true; _audio = null; worker = _microphone; _microphone = null; }
        worker?.Stop(_timeout);
    }

    public void Dispose()
    {
        lock (_gate) { if (_stopped) return; _stopped = true; }
        // Cancel both before joining either, including when a subscriber calls Dispose on a worker.
        _camera?.Cancel(); _microphone?.Cancel();
        StopVideo(); StopAudio();
    }
}

internal sealed class CaptureWorker
{
    private readonly ICaptureDevice _device;
    private readonly CancellationTokenSource _stop = new();
    private readonly ManualResetEventSlim _start = new();
    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private readonly Action<MediaCaptureVideoFrame> _video;
    private readonly Action<MediaAudioFrame> _audio;
    private readonly Action _lost;
    internal CaptureWorker(ICaptureDevice device, Action<MediaCaptureVideoFrame> video, Action<MediaAudioFrame> audio, Action lost)
    {
        (_device, _video, _audio, _lost) = (device, video, audio, lost);
        _thread = new Thread(Run) { IsBackground = true, Name = "Folio native capture" };
    }
    internal void Open(TimeSpan timeout)
    {
        _thread.Start();
        if (!_opened.Task.Wait(timeout)) throw new TimeoutException("Capture open timed out.");
        _opened.Task.GetAwaiter().GetResult();
    }
    internal void Start() { try { _start.Set(); } catch (ObjectDisposedException) { } }
    internal void Cancel() { try { _stop.Cancel(); } catch (ObjectDisposedException) { } }
    internal void Stop(TimeSpan timeout)
    {
        Cancel();
        if (Thread.CurrentThread != _thread) _thread.Join(timeout);
        // Never free native buffers from a joining caller if a driver has stalled. Their owner
        // retains them until the native call returns, avoiding use-after-free on buggy drivers.
    }
    private void Run()
    {
        var failed = false;
        try
        {
            _device.Open();
            _opened.TrySetResult();
            _start.Wait(_stop.Token);
            _device.Pump(_stop.Token, _video, _audio);
            failed = !_stop.IsCancellationRequested;
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { failed = _opened.Task.IsCompletedSuccessfully; _opened.TrySetException(ex); }
        finally
        {
            try { _device.Dispose(); } catch { failed = true; }
            if (failed && !_stop.IsCancellationRequested) _lost();
            _start.Dispose();
            _stop.Dispose();
        }
    }
}
