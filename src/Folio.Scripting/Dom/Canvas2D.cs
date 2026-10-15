using Folio.Dom;

namespace Folio.Scripting.Dom;

internal sealed record CanvasCommand(string Name, double X, double Y, double Width, double Height, string FillStyle);

internal sealed class CanvasElementState(ElementNode element)
{
    public ElementNode Element { get; } = element;
    public List<CanvasCommand> Commands { get; } = [];
    public string FillStyle { get; set; } = "#000000";
    public string StrokeStyle { get; set; } = "#000000";
    public string Font { get; set; } = "10px sans-serif";
    public string TextAlign { get; set; } = "start";
    public string TextBaseline { get; set; } = "alphabetic";
    public string ShadowColor { get; set; } = "rgba(0, 0, 0, 0)";
    public double ShadowBlur { get; set; }
    public double ShadowOffsetX { get; set; }
    public double ShadowOffsetY { get; set; }
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

internal sealed class CanvasGradientState(double x0, double y0, double x1, double y1)
{
    public (double X0, double Y0, double X1, double Y1) Geometry { get; } = (x0, y0, x1, y1);
    public List<(double Offset, string Color)> Stops { get; } = [];
}

internal sealed record CanvasTextMetricsState(double Width);

internal sealed class CanvasImageDataState(int width, int height)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int[] Data { get; } = new int[Math.Min(4_000_000, Math.Max(0, width * height * 4))];
}
