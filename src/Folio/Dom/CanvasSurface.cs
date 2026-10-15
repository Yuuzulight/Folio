namespace Folio.Dom;

/// <summary>Neutral canvas paint data shared by the scripting and display-list layers.</summary>
internal interface ICanvasSurface
{
    IReadOnlyList<CanvasSurfaceCommand> Commands { get; }
}

internal sealed record CanvasSurfaceCommand(string Name, double X, double Y, double Width, double Height, string FillStyle, string? StrokeStyle = null, string Text = "", string Composite = "source-over");
