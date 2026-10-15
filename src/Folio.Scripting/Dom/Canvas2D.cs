using System.Globalization;
using Folio.Dom;

namespace Folio.Scripting.Dom;

internal sealed record CanvasCommand(string Name, double X, double Y, double Width, double Height, string FillStyle, string? StrokeStyle = null, string Text = "", string Composite = "source-over", IReadOnlyList<double>? Arguments = null);

internal sealed class CanvasElementState : ICanvasSurface
{
    public CanvasElementState(ElementNode element)
    {
        Element = element;
        element.CanvasSurface = this;
    }

    public ElementNode Element { get; }
    public List<CanvasCommand> Commands { get; } = [];
    public int[] Pixels { get; } = new int[300 * 150 * 4];
    IReadOnlyList<CanvasSurfaceCommand> ICanvasSurface.Commands => Commands.Select(command =>
        new CanvasSurfaceCommand(command.Name, command.X, command.Y, command.Width, command.Height, command.FillStyle, command.StrokeStyle, command.Text, command.Composite, command.Arguments)).ToArray();
    void ICanvasSurface.ClearCommands() => Commands.Clear();
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

internal sealed class WebGlContextState
{
    private readonly CanvasElementState _canvas;
    private uint _nextHandle = 1;
    public Dictionary<uint, byte[]> Buffers { get; } = [];
    public Dictionary<uint, (int Type, string Source, bool Compiled)> Shaders { get; } = [];
    public HashSet<uint> Programs { get; } = [];
    public Dictionary<uint, (int Width, int Height, byte[] Pixels)> Textures { get; } = [];
    public HashSet<uint> Framebuffers { get; } = [];
    public uint? BoundTexture { get; set; }
    public uint? BoundFramebuffer { get; set; }
    public uint? BoundArrayBuffer { get; set; }
    public uint? CurrentProgram { get; set; }
    public int DrawCalls { get; private set; }
    public int Error { get; private set; }
    public float ClearR { get; private set; }
    public float ClearG { get; private set; }
    public float ClearB { get; private set; }
    public float ClearA { get; private set; } = 1;
    public WebGlContextState(CanvasElementState canvas) => _canvas = canvas;
    public uint CreateHandle() => _nextHandle++;
    public void AddDrawCall() => DrawCalls++;
    public void SetError(int error) { if (Error == 0) Error = error; }
    public int TakeError() { var error = Error; Error = 0; return error; }
    public void SetClearColor(float r, float g, float b, float a) => (ClearR, ClearG, ClearB, ClearA) = (Math.Clamp(r, 0, 1), Math.Clamp(g, 0, 1), Math.Clamp(b, 0, 1), Math.Clamp(a, 0, 1));
    public void Clear(uint mask)
    {
        if ((mask & 0x4000) == 0) return;
        var color = string.Create(CultureInfo.InvariantCulture, $"rgba({ClearR * 255:0.###}, {ClearG * 255:0.###}, {ClearB * 255:0.###}, {ClearA:0.###})");
        var width = uint.TryParse(_canvas.Element.GetAttribute("width"), out var w) ? w : 300;
        var height = uint.TryParse(_canvas.Element.GetAttribute("height"), out var h) ? h : 150;
        _canvas.Commands.Add(new CanvasCommand("fillRect", 0, 0, width, height, color));
    }
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
    public string PaintStyle => $"gradient:{Geometry.X0},{Geometry.Y0},{Geometry.X1},{Geometry.Y1}|{string.Join('|', Stops.OrderBy(stop => stop.Offset).Select(stop => $"{stop.Offset}:{stop.Color}"))}";
}

internal sealed record CanvasTextMetricsState(double Width);

internal sealed class CanvasImageDataState(int width, int height)
{
    private ScriptValue _dataArray;
    private bool _hasDataArray;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int[] Data { get; } = new int[Math.Min(4_000_000, Math.Max(0, width * height * 4))];

    public ScriptValue GetDataArray(IScriptEngine engine)
    {
        if (!_hasDataArray)
        {
            _dataArray = engine.CreateArray(Data.Select(value => ScriptValue.FromNumber(value)).ToArray());
            _hasDataArray = true;
        }
        return _dataArray;
    }
}
