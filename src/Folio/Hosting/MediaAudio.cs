namespace Folio;

using Folio.Imaging;

/// <summary>A decoded interleaved PCM audio block supplied to the host output.</summary>
public readonly record struct MediaAudioFrame(int SampleRate, int Channels, ReadOnlyMemory<float> Samples);

/// <summary>Receives decoded media audio for a host-owned output device.</summary>
public interface IMediaAudioOutput
{
    void Submit(MediaAudioFrame frame);
}

/// <summary>The result of decoding one bounded media resource.</summary>
public sealed record MediaDecodeResult(double Duration, IImageHandle? VideoFrame, MediaAudioFrame? Audio);

/// <summary>Host-supplied decoder for media formats outside Folio's built-in image and WAV support.</summary>
public interface IMediaDecoder
{
    MediaDecodeResult? Decode(ReadOnlyMemory<byte> data, string? contentType, bool video, int maxOutputBytes);
}
