using System.Globalization;
using Folio.Dom;
using Folio.Resources;
using System.Buffers.Binary;

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
    public WebGlContextState? WebGlContext { get; set; }
}

internal sealed class MediaElementState
{
    public MediaElementState(IScriptEngine engine, IHostObjectTemplate eventTemplate, ElementNode element, bool enabled, IResourceLoader? resourceLoader = null, Uri? baseUri = null)
    {
        Engine = engine;
        EventTemplate = eventTemplate;
        Element = element;
        Enabled = enabled;
        _resourceLoader = resourceLoader;
        _baseUri = baseUri;
        var bytes = LoadSourceBytes(element.GetAttribute("src"), resourceLoader, baseUri);
        Duration = double.TryParse(element.GetAttribute("data-duration"), out var duration) ? Math.Max(0, duration) : ParseWavDuration(bytes);
        SourceBytes = bytes?.LongLength ?? (long.TryParse(element.GetAttribute("data-size"), out var size) ? Math.Max(0, size) : 0);
    }

    public ElementNode Element { get; }
    public IScriptEngine Engine { get; }
    public IHostObjectTemplate EventTemplate { get; }
    public bool Enabled { get; }
    public double CurrentTime { get; set; }
    public double Duration { get; private set; }
    public bool Paused { get; set; } = true;
    public bool Ended { get; set; }
    public double Volume { get; set; } = 1;
    public bool Muted { get; set; }
    public bool Autoplay { get; set; }
    public bool Loop { get; set; }
    public bool Controls { get; set; }
    public string Preload { get; set; } = "metadata";
    public bool DefaultMuted { get; set; }
    public string CrossOrigin { get; set; } = "";
    public double PlaybackRate { get; set; } = 1;
    public double DefaultPlaybackRate { get; set; } = 1;
    public bool PreservesPitch { get; set; } = true;
    public ScriptValue SrcObject { get; set; } = ScriptValue.Null;
    public int ReadyState => Enabled && Error is null ? 4 : 0;
    public int NetworkState => Enabled && Error is null ? 1 : 0;
    public string? Error { get; set; }
    public long SourceBytes { get; private set; }
    public List<MediaTextTrackState> TextTracks { get; } = [];
    public void RejectForBudget() => Error = "Media resource exceeds the host budget.";
    public ScriptValue OnLoadedMetadata { get; set; } = ScriptValue.Null;
    public ScriptValue OnPlay { get; set; } = ScriptValue.Null;
    public ScriptValue OnPause { get; set; } = ScriptValue.Null;
    public ScriptValue OnEnded { get; set; } = ScriptValue.Null;
    private readonly Dictionary<string, List<ScriptValue>> _listeners = new(StringComparer.Ordinal);
    private readonly IResourceLoader? _resourceLoader;
    private readonly Uri? _baseUri;
    public void AddEventListener(string type, ScriptValue listener)
    {
        if (!listener.IsFunction) return;
        if (!_listeners.TryGetValue(type, out var list)) _listeners[type] = list = [];
        if (!list.Contains(listener)) list.Add(listener);
    }
    public void RemoveEventListener(string type, ScriptValue listener)
    {
        if (_listeners.TryGetValue(type, out var list)) list.Remove(listener);
    }
    public void Dispatch(ScriptValue handler, string type)
    {
        var eventValue = Engine.CreateHostObject(EventTemplate, new MediaEventState(type));
        if (handler.IsFunction) Engine.Call(handler, ScriptValue.Undefined, [eventValue]);
        if (_listeners.TryGetValue(type, out var list)) foreach (var listener in list.ToArray()) if (listener.IsFunction) Engine.Call(listener, ScriptValue.Undefined, [eventValue]);
    }

    public void Play()
    {
        if (!Enabled || Error is not null) { Error ??= "Media playback is unavailable."; return; }
        Paused = false;
        Ended = false;
        Dispatch(OnPlay, "play");
    }

    public void Pause() { Paused = true; Dispatch(OnPause, "pause"); }

    public void Load()
    {
        var bytes = LoadSourceBytes(Element.GetAttribute("src"), _resourceLoader, _baseUri);
        if (bytes is not null)
        {
            SourceBytes = bytes.LongLength;
            Duration = ParseWavDuration(bytes);
        }
        CurrentTime = 0;
        Paused = true;
        Ended = false;
        Error = null;
        Dispatch(OnLoadedMetadata, "loadedmetadata");
    }

    private static byte[]? LoadSourceBytes(string? source, IResourceLoader? resourceLoader, Uri? baseUri)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = source.IndexOf(',');
            if (comma < 0) return null;
            try
            {
                var payload = source[(comma + 1)..];
                return source[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase)
                    ? Convert.FromBase64String(payload)
                    : System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
            }
            catch (FormatException) { return null; }
        }
        if (resourceLoader is null) return null;
        if (baseUri is not null && Uri.TryCreate(baseUri, source, out var resolved)) source = resolved.AbsoluteUri;
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) return null;
        return resourceLoader.LoadAsync(new ResourceRequest(uri.AbsoluteUri, ResourceKind.Script), CancellationToken.None).GetAwaiter().GetResult()?.Data;
    }

    private static double ParseWavDuration(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 44 || bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F' || bytes[8] != 'W' || bytes[9] != 'A' || bytes[10] != 'V' || bytes[11] != 'E') return 0;
        var sampleRate = 0; var blockAlign = 0; var dataLength = 0;
        for (var offset = 12; offset + 8 <= bytes.Length;)
        {
            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            if (chunkSize < 0 || offset + 8 + chunkSize > bytes.Length) break;
            if (bytes[offset] == 'f' && bytes[offset + 1] == 'm' && bytes[offset + 2] == 't' && bytes[offset + 3] == ' ' && chunkSize >= 16)
            {
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 12, 4));
                blockAlign = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset + 20, 2));
            }
            else if (bytes[offset] == 'd' && bytes[offset + 1] == 'a' && bytes[offset + 2] == 't' && bytes[offset + 3] == 'a') dataLength = chunkSize;
            offset += 8 + chunkSize + (chunkSize & 1);
        }
        return sampleRate > 0 && blockAlign > 0 && dataLength > 0 ? (double)dataLength / (sampleRate * blockAlign) : 0;
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
    private readonly IScriptEngine _engine;
    private readonly CanvasElementState _canvas;
    private readonly int _maxGpuBytes;
    private uint _nextHandle = 1;
    public Dictionary<uint, byte[]> Buffers { get; } = [];
    public Dictionary<uint, (int Type, string Source, bool Compiled)> Shaders { get; } = [];
    public HashSet<uint> Programs { get; } = [];
    public Dictionary<uint, List<uint>> ProgramShaders { get; } = [];
    public HashSet<uint> LinkedPrograms { get; } = [];
    public HashSet<int> EnabledVertexAttributes { get; } = [];
    public Dictionary<int, (int Size, uint Type, bool Normalized, int Stride, int Offset)> VertexAttribPointers { get; } = [];
    public Dictionary<int, double[]> UniformValues { get; } = [];
    public HashSet<uint> EnabledCapabilities { get; } = [];
    public float ClearDepth { get; set; } = 1;
    public uint DepthFunction { get; set; } = 0x0203;
    public uint BlendSourceFactor { get; set; } = 1;
    public uint BlendDestinationFactor { get; set; } = 0;
    public uint BlendEquation { get; set; } = 0x8006;
    public uint StencilFunction { get; set; } = 0x0207;
    public int StencilReference { get; set; }
    public uint StencilMask { get; set; } = 0xFFFFFFFF;
    public uint StencilFail { get; set; }
    public uint StencilDepthFail { get; set; }
    public uint StencilDepthPass { get; set; }
    public int ClearStencil { get; set; }
    public Dictionary<uint, (int Width, int Height, byte[] Pixels)> Textures { get; } = [];
    public Dictionary<uint, Dictionary<uint, int>> TextureParameters { get; } = [];
    public HashSet<uint> TextureMipmaps { get; } = [];
    public HashSet<uint> Framebuffers { get; } = [];
    public Dictionary<uint, uint> FramebufferColorAttachments { get; } = [];
    public List<uint> DrawBuffers { get; } = [];
    public uint ReadBuffer { get; set; } = 0x0405;
    public HashSet<uint> VertexArrays { get; } = [];
    public HashSet<uint> Syncs { get; } = [];
    public HashSet<uint> Queries { get; } = [];
    public Dictionary<uint, bool> QueryResults { get; } = [];
    public uint? ActiveQuery { get; set; }
    public uint? BoundTexture { get; set; }
    public uint ActiveTextureUnit { get; set; } = 0x84C0;
    public uint? BoundFramebuffer { get; set; }
    public uint? BoundArrayBuffer { get; set; }
    public uint? CurrentProgram { get; set; }
    public uint? BoundVertexArray { get; set; }
    public int DrawCalls { get; private set; }
    public int ViewportX { get; set; }
    public int ViewportY { get; set; }
    public int ViewportWidth { get; set; } = 300;
    public int ViewportHeight { get; set; } = 150;
    public int ScissorX { get; set; }
    public int ScissorY { get; set; }
    public int ScissorWidth { get; set; }
    public int ScissorHeight { get; set; }
    public int Error { get; private set; }
    public bool Lost { get; private set; }
    public int AllocatedBytes { get; private set; }
    public CanvasElementState Canvas => _canvas;
    public float ClearR { get; private set; }
    public float ClearG { get; private set; }
    public float ClearB { get; private set; }
    public float ClearA { get; private set; } = 1;
    public ScriptValue OnContextLost { get; set; } = ScriptValue.Null;
    public ScriptValue OnContextRestored { get; set; } = ScriptValue.Null;
    public WebGlContextState(IScriptEngine engine, CanvasElementState canvas, int maxGpuBytes) { _engine = engine; _canvas = canvas; _maxGpuBytes = Math.Max(0, maxGpuBytes); }
    public uint CreateHandle() => _nextHandle++;
    public void AddDrawCall() => DrawCalls++;
    public void SetError(int error) { if (Error == 0) Error = error; }
    public int TakeError() { var error = Error; Error = 0; return error; }
    public void Lose() { if (Lost) return; Lost = true; SetError(0x9242); if (OnContextLost.IsFunction) _engine.Call(OnContextLost, ScriptValue.Undefined, [_engine.Evaluate("({ type: 'webglcontextlost' })")]); }
    public void Restore() { if (!Lost) return; Lost = false; if (OnContextRestored.IsFunction) _engine.Call(OnContextRestored, ScriptValue.Undefined, [_engine.Evaluate("({ type: 'webglcontextrestored' })")]); }
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
        TextureParameters.Remove(handle);
        TextureMipmaps.Remove(handle);
        if (BoundTexture == handle) BoundTexture = null;
    }
    public void DeleteFramebuffer(uint handle)
    {
        Framebuffers.Remove(handle);
        FramebufferColorAttachments.Remove(handle);
        if (BoundFramebuffer == handle) BoundFramebuffer = null;
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
