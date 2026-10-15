using Folio.Dom;

namespace Folio.Scripting.Dom;

internal sealed record CanvasCommand(string Name, double X, double Y, double Width, double Height, string FillStyle);

internal sealed class CanvasElementState(ElementNode element)
{
    public ElementNode Element { get; } = element;
    public List<CanvasCommand> Commands { get; } = [];
    public string FillStyle { get; set; } = "#000000";
}

internal sealed class CanvasPathState
{
    public List<(string Name, double X, double Y)> Segments { get; } = [];
}
