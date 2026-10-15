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

internal sealed class MediaElementState
{
    public MediaElementState(IScriptEngine engine, ElementNode element, bool enabled)
    {
        Engine = engine;
        Element = element;
        Enabled = enabled;
        Duration = double.TryParse(element.GetAttribute("data-duration"), out var duration) ? Math.Max(0, duration) : 0;
        SourceBytes = long.TryParse(element.GetAttribute("data-size"), out var size) ? Math.Max(0, size) : 0;
    }

    public ElementNode Element { get; }
    public IScriptEngine Engine { get; }
    public bool Enabled { get; }
    public double CurrentTime { get; set; }
    public double Duration { get; }
    public bool Paused { get; set; } = true;
    public bool Ended { get; set; }
    public double Volume { get; set; } = 1;
    public bool Muted { get; set; }
    public bool Autoplay { get; set; }
    public bool Loop { get; set; }
    public bool Controls { get; set; }
    public double PlaybackRate { get; set; } = 1;
    public double DefaultPlaybackRate { get; set; } = 1;
    public bool PreservesPitch { get; set; } = true;
    public ScriptValue SrcObject { get; set; } = ScriptValue.Null;
    public int ReadyState => Enabled && Error is null ? 4 : 0;
    public int NetworkState => Enabled && Error is null ? 1 : 0;
    public string? Error { get; set; }
    public long SourceBytes { get; }
    public List<MediaTextTrackState> TextTracks { get; } = [];
    public void RejectForBudget() => Error = "Media resource exceeds the host budget.";
    public ScriptValue OnLoadedMetadata { get; set; } = ScriptValue.Null;
    public ScriptValue OnPlay { get; set; } = ScriptValue.Null;
    public ScriptValue OnPause { get; set; } = ScriptValue.Null;
    public ScriptValue OnEnded { get; set; } = ScriptValue.Null;
    public void Dispatch(ScriptValue handler)
    {
        if (handler.IsFunction) Engine.Call(handler, ScriptValue.Undefined, ReadOnlySpan<ScriptValue>.Empty);
    }

    public void Play()
    {
        if (!Enabled || Error is not null) { Error ??= "Media playback is unavailable."; return; }
        Paused = false;
        Ended = false;
        Dispatch(OnPlay);
    }

    public void Pause() { Paused = true; Dispatch(OnPause); }

    public void Load()
    {
        CurrentTime = 0;
        Paused = true;
        Ended = false;
        Error = null;
        Dispatch(OnLoadedMetadata);
    }
}

internal sealed class MediaTextTrackState(string kind, string label, string language)
{
    public string Kind { get; } = kind;
    public string Label { get; } = label;
    public string Language { get; } = language;
    public string Mode { get; set; } = "disabled";
    public List<TextTrackCueState> Cues { get; } = [];
}

internal sealed class TextTrackCueState
{
    public TextTrackCueState(double startTime, double endTime, string text)
    {
        StartTime = Math.Max(0, startTime);
        EndTime = Math.Max(StartTime, endTime);
        Text = text;
    }
    public double StartTime { get; }
    public double EndTime { get; }
    public string Text { get; }
}

internal sealed class MediaTimeRangesState(IReadOnlyList<(double Start, double End)> ranges)
{
    public IReadOnlyList<(double Start, double End)> Ranges { get; } = ranges;
}

internal sealed class WebGlContextState
{
    private readonly CanvasElementState _canvas;
    private readonly int _maxGpuBytes;
    private uint _nextHandle = 1;
    public Dictionary<uint, byte[]> Buffers { get; } = [];
    public Dictionary<uint, (int Type, string Source, bool Compiled)> Shaders { get; } = [];
    public HashSet<uint> Programs { get; } = [];
    public Dictionary<uint, List<uint>> ProgramShaders { get; } = [];
    public HashSet<uint> LinkedPrograms { get; } = [];
    public Dictionary<uint, (int Width, int Height, byte[] Pixels)> Textures { get; } = [];
    public HashSet<uint> Framebuffers { get; } = [];
    public uint? BoundTexture { get; set; }
    public uint? BoundFramebuffer { get; set; }
    public uint? BoundArrayBuffer { get; set; }
    public uint? CurrentProgram { get; set; }
    public int DrawCalls { get; private set; }
    public int Error { get; private set; }
    public bool Lost { get; private set; }
    public int AllocatedBytes { get; private set; }
    public float ClearR { get; private set; }
    public float ClearG { get; private set; }
    public float ClearB { get; private set; }
    public float ClearA { get; private set; } = 1;
    public WebGlContextState(CanvasElementState canvas, int maxGpuBytes) { _canvas = canvas; _maxGpuBytes = Math.Max(0, maxGpuBytes); }
    public uint CreateHandle() => _nextHandle++;
    public void AddDrawCall() => DrawCalls++;
    public void SetError(int error) { if (Error == 0) Error = error; }
    public int TakeError() { var error = Error; Error = 0; return error; }
    public void Lose() { Lost = true; SetError(0x9242); }
    public void Restore() { Lost = false; }
    public bool TryAllocate(int bytes)
    {
        if (bytes < 0 || bytes > _maxGpuBytes - AllocatedBytes) { SetError(0x0505); return false; }
        AllocatedBytes += bytes;
        return true;
    }
    public bool ReplaceBuffer(uint handle, int bytes)
    {
        var oldBytes = Buffers.TryGetValue(handle, out var existing) ? existing.Length : 0;
        var nextTotal = AllocatedBytes - oldBytes + bytes;
        if (bytes < 0 || bytes > _maxGpuBytes || nextTotal > _maxGpuBytes) { SetError(0x0505); return false; }
        AllocatedBytes = nextTotal;
        Buffers[handle] = new byte[bytes];
        return true;
    }
    public void DeleteBuffer(uint handle)
    {
        if (Buffers.Remove(handle, out var buffer)) AllocatedBytes = Math.Max(0, AllocatedBytes - buffer.Length);
        if (BoundArrayBuffer == handle) BoundArrayBuffer = null;
    }
    public void DeleteTexture(uint handle)
    {
        if (Textures.Remove(handle, out var texture)) AllocatedBytes = Math.Max(0, AllocatedBytes - texture.Pixels.Length);
        if (BoundTexture == handle) BoundTexture = null;
    }
    public bool ReplaceTexture(uint handle, int width, int height, int bytes)
    {
        var oldBytes = Textures.TryGetValue(handle, out var existing) ? existing.Pixels.Length : 0;
        var nextTotal = AllocatedBytes - oldBytes + bytes;
        if (bytes < 0 || bytes > _maxGpuBytes || nextTotal > _maxGpuBytes) { SetError(0x0505); return false; }
        AllocatedBytes = nextTotal;
        Textures[handle] = (width, height, new byte[bytes]);
        return true;
    }
    public void DeleteShader(uint handle) => Shaders.Remove(handle);
    public void DeleteProgram(uint handle)
    {
        Programs.Remove(handle);
        ProgramShaders.Remove(handle);
        LinkedPrograms.Remove(handle);
        if (CurrentProgram == handle) CurrentProgram = null;
    }
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
