using Folio.Dom;

namespace Folio.Scripting.Dom;

internal sealed record CanvasCommand(string Name, double X, double Y, double Width, double Height, string FillStyle);

internal sealed class CanvasElementState(ElementNode element)
{
    public ElementNode Element { get; } = element;
    public List<CanvasCommand> Commands { get; } = [];
    public string FillStyle { get; set; } = "#000000";
    public double LineWidth { get; set; } = 1;
    public string LineCap { get; set; } = "butt";
    public string LineJoin { get; set; } = "miter";
    public double GlobalAlpha { get; set; } = 1;
    public string GlobalCompositeOperation { get; set; } = "source-over";
    public List<double> LineDash { get; } = [];
    public Stack<CanvasStyleSnapshot> SavedStyles { get; } = [];
}

internal sealed record CanvasStyleSnapshot(string FillStyle, double LineWidth, string LineCap, string LineJoin, double GlobalAlpha, string GlobalCompositeOperation, double[] LineDash);

internal sealed class CanvasPathState
{
    public List<(string Name, double X, double Y)> Segments { get; } = [];
}
