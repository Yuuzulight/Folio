namespace Folio;

using Folio.Imaging;

/// <summary>A decoded interleaved PCM audio block supplied to the host output.</summary>
public readonly record struct MediaAudioFrame(int SampleRate, int Channels, ReadOnlyMemory<float> Samples);

/// <summary>Receives decoded media audio for a host-owned output device.</summary>
public interface IMediaAudioOutput
{
    void Submit(MediaAudioFrame frame);
}

/// <summary>Optional lifecycle controls for a host-owned media audio device.</summary>
/// <remarks>
/// Implement this alongside <see cref="IMediaAudioOutput"/> when the output owns a
/// real device. Folio keeps this separate so existing sample collectors remain valid.
/// </remarks>
public interface IMediaAudioOutputLifecycle
{
    void Start();
    void Pause();
    void Flush();
    void Stop();
}

/// <summary>The result of decoding one bounded media resource.</summary>
public sealed record MediaDecodeResult(double Duration, IImageHandle? VideoFrame, MediaAudioFrame? Audio)
{
    /// <summary>Optional timestamped video frames in presentation order. When omitted, <see cref="VideoFrame"/> is used.</summary>
    public IReadOnlyList<MediaVideoFrame>? VideoFrames { get; init; }
}

/// <summary>A bounded decoded video frame at a media-time position in seconds.</summary>
public sealed record MediaVideoFrame(double Time, IImageHandle Frame);

/// <summary>Host-supplied decoder for media formats outside Folio's built-in image and WAV support.</summary>
public interface IMediaDecoder
{
    MediaDecodeResult? Decode(ReadOnlyMemory<byte> data, string? contentType, bool video, int maxOutputBytes);
}
