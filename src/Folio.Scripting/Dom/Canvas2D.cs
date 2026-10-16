using System.Globalization;
using Folio.Dom;
using Folio.Imaging;
using Folio.Painting;
using Folio.Resources;
using System.Buffers.Binary;

namespace Folio.Scripting.Dom;

internal sealed class MediaElementState : IImageHandle
{
    public MediaElementState(IScriptEngine engine, IHostObjectTemplate eventTemplate, ElementNode element, bool enabled, bool allowSoundAutoplay = false, IResourceLoader? resourceLoader = null, Uri? baseUri = null)
    {
        Engine = engine;
        EventTemplate = eventTemplate;
        Element = element;
        Enabled = enabled;
        AllowSoundAutoplay = allowSoundAutoplay;
        Muted = element.GetAttribute("muted") is not null;
        DefaultMuted = Muted;
        Autoplay = element.GetAttribute("autoplay") is not null;
        _resourceLoader = resourceLoader;
        _baseUri = baseUri;
        var bytes = LoadSourceBytes(element.GetAttribute("src"), resourceLoader, baseUri);
        Duration = double.TryParse(element.GetAttribute("data-duration"), out var duration) ? Math.Max(0, duration) : ParseMediaDuration(bytes);
        SourceBytes = bytes?.LongLength ?? (long.TryParse(element.GetAttribute("data-size"), out var size) ? Math.Max(0, size) : 0);
        if (Enabled && bytes is { Length: > 0 } && !LooksLikeMedia(bytes)) Error = "Media resource is corrupt or truncated.";
    }

    public ElementNode Element { get; }
    public IScriptEngine Engine { get; }
    public IHostObjectTemplate EventTemplate { get; }
    public bool Enabled { get; private set; }
    public bool AllowSoundAutoplay { get; }
    public int Width => int.TryParse(Element.GetAttribute("data-width"), out var width) ? Math.Clamp(width, 0, 4096) : 0;
    public int Height => int.TryParse(Element.GetAttribute("data-height"), out var height) ? Math.Clamp(height, 0, 4096) : 0;
    private byte[]? _framePixels;
    public ReadOnlyMemory<byte> Pixels => _framePixels ??= new byte[Math.Min((long)Width * Height * 4, 16 * 1024 * 1024)];
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
    private bool _capabilityDisabled;
    public long SourceBytes { get; private set; }
    public List<MediaTextTrackState> TextTracks { get; } = [];
    public void RejectForBudget() => Error = "Media resource exceeds the host budget.";
    public void SetCapability(bool enabled)
    {
        if (Enabled == enabled && !_capabilityDisabled) return;
        Enabled = enabled;
        if (!enabled)
        {
            _capabilityDisabled = true;
            Paused = true;
            Error = "Media is disabled by the host.";
            Dispatch(OnError, "error");
        }
        else if (_capabilityDisabled)
        {
            _capabilityDisabled = false;
            Error = null;
            Load();
        }
    }
    public ScriptValue OnLoadedMetadata { get; set; } = ScriptValue.Null;
    public ScriptValue OnPlay { get; set; } = ScriptValue.Null;
    public ScriptValue OnPause { get; set; } = ScriptValue.Null;
    public ScriptValue OnEnded { get; set; } = ScriptValue.Null;
    public ScriptValue OnError { get; set; } = ScriptValue.Null;
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

    public bool Play(bool autoplay = false)
    {
        if (!Enabled || Error is not null) { Error ??= "Media playback is unavailable."; return false; }
        if (autoplay && !Muted && !DefaultMuted && !AllowSoundAutoplay) { Error = "Audible autoplay is blocked by the host policy."; return false; }
        Paused = false;
        Ended = false;
        Dispatch(OnPlay, "play");
        return true;
    }

    public void Pause() { Paused = true; Dispatch(OnPause, "pause"); }

    public void Load()
    {
        var bytes = LoadSourceBytes(Element.GetAttribute("src"), _resourceLoader, _baseUri);
        if (bytes is not null)
        {
            SourceBytes = bytes.LongLength;
            Duration = ParseMediaDuration(bytes);
            Error = LooksLikeMedia(bytes) ? null : "Media resource is corrupt or truncated.";
        }
        CurrentTime = 0;
        Paused = true;
        Ended = false;
        if (Error is { }) Dispatch(OnError, "error");
        else Dispatch(OnLoadedMetadata, "loadedmetadata");
    }

    public string CanPlayType(string type)
    {
        if (!Enabled) return "";
        var mime = type.Trim().ToLowerInvariant().Split(';', 2)[0];
        return mime switch
        {
            "audio/wav" or "audio/x-wav" or "audio/wave" or "audio/mpeg" or "audio/mp3" or "audio/ogg" or
            "video/mp4" or "video/webm" or "video/ogg" => "maybe",
            _ => "",
        };
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
        return resourceLoader.LoadAsync(new ResourceRequest(uri.AbsoluteUri, ResourceKind.Media), CancellationToken.None).GetAwaiter().GetResult()?.Data;
    }

    private static double ParseMediaDuration(byte[]? bytes)
    {
        var wav = ParseWavDuration(bytes);
        return wav > 0 ? wav : ParseMpegDuration(bytes);
    }

    private static bool LooksLikeMedia(byte[] bytes) =>
        bytes.Length >= 12 && ((bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8)) ||
        bytes.AsSpan(0, 4).SequenceEqual("OggS"u8) || bytes.AsSpan(0, 3).SequenceEqual("ID3"u8) ||
        (bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0) || bytes.AsSpan(4, 4).SequenceEqual("ftyp"u8) ||
        (bytes[0] == 0x1A && bytes[1] == 0x45 && bytes[2] == 0xDF && bytes[3] == 0xA3));

    private static double ParseMpegDuration(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 4) return 0;
        var offset = 0;
        if (bytes.Length >= 10 && bytes[0] == 'I' && bytes[1] == 'D' && bytes[2] == '3')
            offset = 10 + ((bytes[6] & 0x7F) << 21) + ((bytes[7] & 0x7F) << 14) + ((bytes[8] & 0x7F) << 7) + (bytes[9] & 0x7F);
        var samples = 0L;
        var sampleRate = 0;
        for (var cursor = offset; cursor + 4 <= bytes.Length;)
        {
            if (bytes[cursor] != 0xFF || (bytes[cursor + 1] & 0xE0) != 0xE0) { cursor++; continue; }
            var version = (bytes[cursor + 1] >> 3) & 3;
            var layer = (bytes[cursor + 1] >> 1) & 3;
            var bitrateIndex = (bytes[cursor + 2] >> 4) & 15;
            var rateIndex = (bytes[cursor + 2] >> 2) & 3;
            if (version != 3 || layer != 1 || bitrateIndex is 0 or 15 || rateIndex == 3) { cursor++; continue; }
            var bitrates = new[] { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 };
            var rates = new[] { 44100, 48000, 32000, 0 };
            var bitrate = bitrates[bitrateIndex] * 1000;
            sampleRate = rates[rateIndex];
            var frameLength = 144 * bitrate / sampleRate;
            if (frameLength < 1 || cursor + frameLength > bytes.Length) break;
            samples += 1152;
            cursor += frameLength;
        }
        return samples > 0 && sampleRate > 0 ? (double)samples / sampleRate : 0;
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
    public ScriptValue OnCueChange { get; set; } = ScriptValue.Null;
    public double CurrentTime { get; set; }
    public IReadOnlyList<TextTrackCueState> ActiveCues => Cues.Where(cue => CurrentTime >= cue.StartTime && CurrentTime < cue.EndTime).ToArray();
}

internal sealed class TextTrackCueState
{
    public TextTrackCueState(double startTime, double endTime, string text, string id = "")
    {
        StartTime = Math.Max(0, startTime);
        EndTime = Math.Max(StartTime, endTime);
        Text = text;
        Id = id;
    }
    public double StartTime { get; }
    public double EndTime { get; }
    public string Text { get; }
    public string Id { get; }
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
    public Dictionary<int, uint> VertexAttribDivisors { get; } = [];
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
    public void CopyTexturePixels(uint handle, int x, int y, int width, int height, IReadOnlyList<ScriptValue> source)
    {
        if (!Textures.TryGetValue(handle, out var texture)) return;
        var copyWidth = Math.Min(width, Math.Max(0, texture.Width - x));
        var copyHeight = Math.Min(height, Math.Max(0, texture.Height - y));
        for (var row = 0; row < copyHeight; row++)
            for (var column = 0; column < copyWidth; column++)
            {
                var sourceIndex = (row * width + column) * 4;
                var targetIndex = ((y + row) * texture.Width + x + column) * 4;
                for (var channel = 0; channel < 4 && sourceIndex + channel < source.Count && targetIndex + channel < texture.Pixels.Length; channel++)
                    texture.Pixels[targetIndex + channel] = (byte)Math.Clamp(WebIdlConversions.ToInt32(source[sourceIndex + channel]), 0, 255);
            }
        Textures[handle] = texture;
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
        if ((mask & 0x4000) == 0 || _canvas.Surface() is not { } surface)
            return;
        var color = new Rgba(ClearR, ClearG, ClearB, ClearA);
        surface.Canvas.FillPath(new PathData().AddRoundedRect(new RoundedRect(new RectF(0, 0, surface.Width, surface.Height), default)), FillRule.NonZero,
            new Paint(color, Blend: BlendMode.Copy));
        _canvas.Touch();
    }
}
