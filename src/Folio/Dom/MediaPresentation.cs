namespace Folio.Dom;

/// <summary>Media state shared with the renderer, input router and accessibility tree.</summary>
public interface IMediaPresentation
{
    bool Controls { get; }
    bool Paused { get; }
    bool Muted { get; }
    double CurrentTime { get; }
    double Duration { get; }
    double Volume { get; }
    IMediaPoster? Poster { get; }
    IReadOnlyList<IMediaCue> ActiveCues { get; }
    void TogglePlayPause();
    void SeekBy(double seconds);
    void ToggleMute();
    void SetVolume(double volume);
}

public interface IMediaPoster
{
    int Width { get; }
    int Height { get; }
    ReadOnlyMemory<byte> Pixels { get; }
}

/// <summary>A current decoded video frame, separate from the poster retained on failure.</summary>
internal interface IMediaFrameSource
{
    IMediaPoster? CurrentFrame { get; }
}

public interface IMediaCue
{
    string Text { get; }
}
