namespace Folio;

/// <summary>A decoded interleaved PCM audio block supplied to the host output.</summary>
public readonly record struct MediaAudioFrame(int SampleRate, int Channels, ReadOnlyMemory<float> Samples);

/// <summary>Receives decoded media audio for a host-owned output device.</summary>
public interface IMediaAudioOutput
{
    void Submit(MediaAudioFrame frame);
}
