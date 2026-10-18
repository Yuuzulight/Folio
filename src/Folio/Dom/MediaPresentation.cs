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
    IReadOnlyList<IMediaCue> ActiveCues { get; }
    void TogglePlayPause();
    void SeekBy(double seconds);
    void ToggleMute();
    void SetVolume(double volume);
}

public interface IMediaCue
{
    string Text { get; }
}
