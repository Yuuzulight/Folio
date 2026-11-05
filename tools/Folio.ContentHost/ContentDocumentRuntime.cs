using Folio;
using Folio.Dom;
using Folio.Html;
using Folio.Resources;
using Folio.Scripting;
using Folio.Scripting.Dom;
using Folio.Scripting.Gpu;
using Folio.Scripting.Ipc;
using Folio.Scripting.Native;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Folio.ContentHost;

/// <summary>Shared document bootstrap used by the production content host and trusted process probes.</summary>
public sealed class ContentDocumentRuntime : IDisposable
{
    private readonly ContentHostSession _session;
    private CanvasFrameStore _frameStore;
    private readonly string _framePath;
    private ContentViewport _viewport = new(1280, 720);
    private readonly IResourceLoader _resourceLoader;
    private readonly List<string> _taskDiagnostics = new();
    private readonly Func<IScriptEngine> _engineFactory;
    private IScriptEngine? _engine;
    private HtmlEventLoop? _eventLoop;
    private ScriptRunner? _runner;
    private Document? _document;
    private BrokerMediaCaptureProvider? _captureProvider;
    private Folio.Painting.SoftwareRasterSurface _surface = new(1280, 720);

    public ContentDocumentRuntime(ContentHostSession session, CanvasFrameStore frameStore, Func<IScriptEngine>? engineFactory = null)
    {
        _session = session;
        _frameStore = frameStore;
        _framePath = frameStore.FilePath;
        _resourceLoader = new BrokerResourceLoader(session);
        // The shipped content host uses Folio's native engine.  The optional
        // Jint oracle is loaded only by an explicitly configured test host.
        _engineFactory = engineFactory ?? (() => new NativeJavaScriptEngine());
    }

    public FeatureCapabilities Capabilities { get; private set; } = FeatureCapabilities.Default;
    public Document? Document => _document;
    public IScriptEngine? Engine => _engine;
    public CanvasFrameStore FrameStore => _frameStore;
    public long ViewportRevision => _viewport.Revision;

    public void ConfigureViewport(ContentViewport viewport)
    {
        if (!viewport.IsValid || viewport.Revision <= _viewport.Revision)
            throw new InvalidDataException("Invalid viewport update.");
        if (viewport.FramePath is null || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(viewport.FramePath)),
            Path.GetDirectoryName(_framePath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("The viewport frame must be owned by this connection.");
        if (!string.Equals(viewport.FramePath, _frameStore.FilePath, StringComparison.Ordinal))
        {
            _frameStore.Dispose();
            _frameStore = new CanvasFrameStore(viewport.FramePath, viewport.Width, viewport.Height, viewport.Width * 4);
            _surface.Dispose();
            _surface = new Folio.Painting.SoftwareRasterSurface(viewport.Width, viewport.Height);
        }
        _viewport = viewport;
        UpdateViewportGlobals();
        if (_document is not null)
        {
            PublishCurrentFrame();
            _engine?.Evaluate("window.dispatchEvent(new Event('resize'));", "folio:event:resize");
            _eventLoop?.SpinUntilIdle();
            PublishCurrentFrame();
        }
    }

    public bool Tick()
    {
        if (_document is null || _eventLoop is null || (_eventLoop.IsIdle() && !_document.AnimationsRunning(_eventLoop.Clock.NowMs / 1000))) return false;
        var animation = _eventLoop.HasAnimationFrameCallbacks || _document.AnimationsRunning(_eventLoop.Clock.NowMs / 1000);
        var turns = _eventLoop.SpinUntilIdle();
        if (turns == 0 && !animation) return false;
        _eventLoop.UpdateTheRendering();
        PublishCurrentFrame(reuseLayout: true);
        return true;
    }

    public void ApplyInput(ContentInputEvent input)
    {
        if (_document is null) return;
        var position = new System.Numerics.Vector2((float)input.X, (float)input.Y);
        var modifiers = (KeyModifiers)input.Modifiers;
        var pointer = new PointerEvent(position, (PointerButton)input.Button, (PointerButtons)input.Buttons, Modifiers: modifiers);
        switch (input.Type)
        {
            case "pointerdown": _document.Input.HandlePointerDown(pointer); break;
            case "pointerup": _document.Input.HandlePointerUp(pointer); break;
            case "pointermove": _document.Input.HandlePointerMove(pointer); break;
            case "pointerleave": _document.Input.HandlePointerLeave(); break;
            case "wheel": _document.Input.HandleWheel(new WheelEvent(position, new System.Numerics.Vector2((float)input.DeltaX, (float)input.DeltaY), modifiers)); break;
            case "keydown": _document.Input.HandleKeyDown(new KeyEvent(input.Key ?? "", input.Code ?? "", modifiers)); break;
            case "keyup": _document.Input.HandleKeyUp(new KeyEvent(input.Key ?? "", input.Code ?? "", modifiers)); break;
            case "textinput": _document.Input.HandleTextInput(new TextInputEvent(input.Text ?? "", modifiers)); break;
        }
        _eventLoop?.SpinUntilIdle();
        PublishCurrentFrame();
    }

    /// <summary>Trusted host probe: clears a document canvas through the native GPU bridge and republishes the normal page frame.</summary>
    public ContentGpuProbeResult ClearNativeCanvas(string canvasId, float red, float green, float blue, float alpha)
    {
        if (_engine is null || _document is null)
            return new(false, "No document is loaded.", null, null, null, null, null, null, null, null, null, null);
        var canvas = _document.Node.Descendants.OfType<ElementNode>()
            .FirstOrDefault(element => element.LocalName.Equals("canvas", StringComparison.OrdinalIgnoreCase)
                && string.Equals(element.GetAttribute("id"), canvasId, StringComparison.Ordinal));
        if (canvas is null)
            return new(false, $"Canvas '{canvasId}' was not found.", null, null, null, null, null, null, null, null, null, null);
        var succeeded = DomBindings.TryClearNativeCanvas(_engine, canvas, red, green, blue, alpha, out var failure);
        PublishCurrentFrame();
        var memory = WindowsGpuMemory.Sample();
        var metricsAvailable = DomBindings.TryGetGpuMetrics(_engine, out var reserved, out var peak, out var renderer);
        var fallback = canvas.CanvasSurface as IFeatureFallbackSource;
        var fallbackFeature = fallback?.Fallback?.Feature.ToString();
        var fallbackReason = fallback?.Fallback?.Reason.ToString();
        return new(succeeded, failure?.Message, failure?.Kind.ToString(), metricsAvailable ? renderer : null,
            metricsAvailable ? reserved : null, metricsAvailable ? peak : null,
            memory?.LocalBytes, memory?.NonLocalBytes, memory?.Adapters, memory?.Source, fallbackFeature, fallbackReason);
    }

    public int PublishCurrentFrame(Dictionary<string, double>? timings = null, bool reuseLayout = false)
    {
        if (_document is null) return 0;
        var width = _frameStore.Width;
        var height = _frameStore.Height;
        var scale = (float)_viewport.DeviceScale;
        var surface = _surface;
        var canvas = surface.Canvas;
        canvas.FillRoundedRect(new Folio.Painting.RoundedRect(new Folio.Painting.RectF(0, 0, width, height), default),
            _document.Options.ColorScheme == ColorScheme.Dark ? new Folio.Painting.Paint(new Folio.Painting.Rgba(18 / 255f, 18 / 255f, 18 / 255f, 1)) : new Folio.Painting.Paint(new Folio.Painting.Rgba(1, 1, 1, 1)));
        var started = Stopwatch.GetTimestamp();
        var time = (_eventLoop?.Clock.NowMs ?? 0) / 1000;
        var list = reuseLayout && !_document.Node.StructureMutated && !_document.Node.StyleMutated
            ? _document.PaintFrame(time) : null;
        list ??= _document.Paint(width / scale, height / scale, scale, Folio.Typography.ComplexShaper.Instance, time).List;
        if (timings is not null) timings["layout"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = Stopwatch.GetTimestamp();
        canvas.Save();
        try
        {
            canvas.Transform(System.Numerics.Matrix3x2.CreateScale(scale));
            Folio.Painting.DisplayListPlayer.Replay(list, canvas, new Folio.Painting.RectF(0, 0, width / scale, height / scale));
        }
        finally { canvas.Restore(); }
        if (timings is not null) timings["raster"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = Stopwatch.GetTimestamp();
        var pixels = surface.PremultipliedPixels;
        for (var i = 0; i < pixels.Length; i += 4)
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
        _frameStore.Publish(pixels);
        if (timings is not null) timings["publish"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return list.Items.Count;
    }

    public bool ApplyCapabilities(ContentMessage message)
    {
        var accepted = message.TryGetCapabilities(out var updated);
        Capabilities = accepted ? updated : FeatureCapabilities.Default;
        if (_engine is not null) DomBindings.UpdateCapabilities(_engine, Capabilities);
        return accepted;
    }

    public void ApplyMediaCaptureActivity(ContentMessage message) => _captureProvider?.Apply(message.Payload);

    public Task<ContentDocumentResult> ExecuteAsync(ContentMessage message, CancellationToken cancellationToken)
    {
        Dictionary<string, double>? timings = message.Payload.TryGetProperty("profile", out var profile) && profile.ValueKind == JsonValueKind.True ? new() : null;
        var stamp = Stopwatch.GetTimestamp();
        void Mark(string name)
        {
            if (timings is null) return;
            timings[name] = Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
            stamp = Stopwatch.GetTimestamp();
        }
        var script = message.Payload.TryGetProperty("script", out var source) ? source.GetString() ?? "" : "";
        var url = message.Payload.TryGetProperty("url", out var location) ? location.GetString() ?? "content" : "content";
        var replacesDocument = message.Payload.TryGetProperty("html", out var htmlElement);
        if (replacesDocument || message.Payload.EnumerateObject().Any(property => property.Name.Equals("capabilities", StringComparison.OrdinalIgnoreCase)))
        {
            Capabilities = message.TryGetCapabilities(out var loaded) ? loaded : FeatureCapabilities.Default;
            if (!replacesDocument && _engine is not null) DomBindings.UpdateCapabilities(_engine, Capabilities);
        }
        if (replacesDocument)
        {
            DisposeDocument();
            Mark("retire");
            _captureProvider = new BrokerMediaCaptureProvider(_session);
            var options = new FolioOptions
            {
                Scripting = ScriptingPolicy.ContentProcess,
                Fonts = ContentFonts.Settings,
                Capabilities = Capabilities,
                BaseUri = Uri.TryCreate(url, UriKind.Absolute, out var baseUri) ? baseUri : null,
                ResourceLoader = _resourceLoader,
                MediaDecoder = new BrokerMediaDecoder(_session),
                MediaAudioOutput = new BrokerMediaAudioOutput(_session),
                MediaCaptureProvider = _captureProvider,
                ArtifactId = message.Payload.TryGetProperty("artifactId", out var artifact) ? artifact.GetString() ?? "default" : "default",
                ArtifactStorage = message.Payload.TryGetProperty("storageEnabled", out var storageEnabled) && storageEnabled.GetBoolean() ? new BrokerArtifactStorage(_session) : null,
                Limits = message.Payload.TryGetProperty("limits", out var limits) ? limits.Deserialize(ContentProtocolJsonContext.Default.ResourceLimits) ?? ResourceLimits.Default : ResourceLimits.Default,
                MediaCaptureActivityChanged = active => _ = _session.SendAsync(ContentMessageKind.MediaCaptureActivity, new { active }, cancellationToken),
                Raster = Folio.Painting.SoftwareRasterBackend.Instance
            };
            _session.SetStorageLimit(options.Limits.MaxServiceWorkerCacheBytes);
            _document = Document.Parse(htmlElement.GetString() ?? "", options);
            Mark("parse");
            _engine = _engineFactory();
            Mark("engine");
            if (_engine is NativeJavaScriptEngine)
            {
                _engine.SetGlobalProperty("__folio_native_content_host", ScriptValue.True);
            }
            _eventLoop = new HtmlEventLoop(_engine);
            _taskDiagnostics.Clear();
            _eventLoop.UncaughtException += error => _taskDiagnostics.Add(error.GetType().Name + ": " + error.Message);
            if (message.Payload.TryGetProperty("missingNativeLibrary", out var missingNative)
                && missingNative.ValueKind == JsonValueKind.True)
            {
                var missingPath = Path.Combine(Path.GetTempPath(), $"folio-angle-missing-{Guid.NewGuid():N}.dll");
                DomBindings.ConfigureGpuFactory(_engine,
                    maxBytes => AngleGpuBackend.TryCreate(missingPath, maxBytes, GpuRendererPreference.HardwareThenWarp));
            }
            DomBindings.Install(_engine, _document, eventLoop: _eventLoop);
            Mark("bindings");
            UpdateViewportGlobals();
            ScriptGlobalsInstaller.Install(_eventLoop, _engine);
            Mark("globals");
            _runner = new ScriptRunner(_document, _engine, _eventLoop, options);
            _runner.Run();
            Mark("scripts");
        }
        _engine ??= _engineFactory();
        // Host resource/fetch promises schedule zero-delay tasks. Give real-time
        // timers a short bounded window to become due so a slow Windows clock
        // cannot turn a ready frame into a spurious script diagnostic.
        _eventLoop?.SpinUntilIdle(waitForTimers: TimeSpan.FromMilliseconds(25));
        Mark("tasks-before");
        var result = _engine.Evaluate(script, url);
        Mark("evaluate");
        _eventLoop?.SpinUntilIdle(waitForTimers: TimeSpan.FromMilliseconds(25));
        Mark("tasks-after");
        var canvasCommands = _document?.Node.Descendants.OfType<ElementNode>().Count(element => element.CanvasSurface is not null) ?? 0;
        var displayItems = PublishCurrentFrame(timings);
        return Task.FromResult(new ContentDocumentResult(result.ToString(), displayItems, canvasCommands,
            _runner?.Diagnostics.Select(diagnostic => diagnostic.Message).Concat(_taskDiagnostics).ToArray() ?? _taskDiagnostics.ToArray(), timings));
    }

    public void Dispose()
    {
        DisposeDocument();
        _frameStore.Dispose();
        _surface.Dispose();
    }

    public void StopDocument() => DisposeDocument();

    private void UpdateViewportGlobals()
    {
        if (_engine is null) return;
        _engine.SetGlobalProperty("innerWidth", ScriptValue.FromNumber(_viewport.Width / _viewport.DeviceScale));
        _engine.SetGlobalProperty("innerHeight", ScriptValue.FromNumber(_viewport.Height / _viewport.DeviceScale));
        _engine.SetGlobalProperty("devicePixelRatio", ScriptValue.FromNumber(_viewport.DeviceScale));
    }

    private void DisposeDocument()
    {
        if (_captureProvider is null && _engine is null && _runner is null && _eventLoop is null && _document is null)
            return;
        _captureProvider?.Dispose(); _captureProvider = null;
        if (_engine is not null)
            DomBindings.Dispose(_engine);
        _runner?.Dispose(); _runner = null;
        _engine?.Dispose(); _engine = null;
        _eventLoop?.Dispose(); _eventLoop = null;
        _document?.Dispose(); _document = null;
        // A content process replaces documents repeatedly. Promptly collect
        // retired DOM/script graphs so the process working-set target reflects
        // the live document rather than every page visited since startup.
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }

    private sealed class BrokerResourceLoader(ContentHostSession session) : IResourceLoader
    {
        public async Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken) =>
            await session.RequestResourceAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private sealed class BrokerMediaDecoder(ContentHostSession session) : IMediaDecoder
    {
        public MediaDecodeResult? Decode(ReadOnlyMemory<byte> data, string? contentType, bool video, int maxOutputBytes)
        {
            return session.RequestMediaDecode(data, contentType, video, maxOutputBytes);
        }
    }

    private sealed class BrokerMediaCaptureProvider(ContentHostSession session) : IMediaCaptureProvider, IDisposable
    {
        private readonly ConcurrentDictionary<string, BrokerMediaCaptureSession> _sessions = new();
        public IMediaCaptureSession? Open(MediaCaptureRequest request)
        {
            var result = session.RequestMediaCaptureAsync(new { action = "open", audio = request.Audio, video = request.Video }).GetAwaiter().GetResult();
            if (!result.TryGetProperty("ok", out var ok) || !ok.GetBoolean() || !result.TryGetProperty("token", out var token)) return null;
            var capture = new BrokerMediaCaptureSession(session, token.GetString()!, () => _sessions.TryRemove(token.GetString()!, out _));
            _sessions[capture.Token] = capture;
            return capture;
        }
        public void Apply(JsonElement payload)
        {
            if (payload.TryGetProperty("captureToken", out var token) && _sessions.TryGetValue(token.GetString() ?? "", out var capture)) capture.Apply(payload);
        }
        public void Dispose()
        {
            foreach (var pair in _sessions)
                if (_sessions.TryRemove(pair.Key, out var capture)) capture.Dispose();
        }
    }

    private sealed class BrokerMediaCaptureSession(ContentHostSession session, string token, Action removed) : IMediaCaptureSession
    {
        private int _disposed;
        private event Action<MediaCaptureVideoFrame>? _videoFrameAvailable;
        private event Action<MediaAudioFrame>? _audioFrameAvailable;
        private event Action? _revoked;
        public string Token => token;
        public event Action<MediaCaptureVideoFrame>? VideoFrameAvailable { add => _videoFrameAvailable += value; remove => _videoFrameAvailable -= value; }
        public event Action<MediaAudioFrame>? AudioFrameAvailable { add => _audioFrameAvailable += value; remove => _audioFrameAvailable -= value; }
        public event Action? Revoked { add => _revoked += value; remove => _revoked -= value; }
        public MediaCaptureVideoFrame? CurrentVideoFrame => Read().video;
        public MediaAudioFrame? CurrentAudioFrame => Read().audio;
        private (MediaCaptureVideoFrame? video, MediaAudioFrame? audio) Read()
        {
            if (Volatile.Read(ref _disposed) != 0) return (null, null);
            var result = session.RequestMediaCaptureAsync(new { action = "current", token }).GetAwaiter().GetResult();
            MediaCaptureVideoFrame? video = null;
            if (result.TryGetProperty("video", out var v) && v.TryGetProperty("width", out var w) && v.TryGetProperty("height", out var h) && v.TryGetProperty("rgba", out var rgba)
                && w.TryGetInt32(out var width) && h.TryGetInt32(out var height))
            {
                var bytes = rgba.GetBytesFromBase64();
                var candidate = new MediaCaptureVideoFrame(width, height, bytes);
                if (candidate.IsValid) video = candidate;
            }
            MediaAudioFrame? audio = null;
            if (result.TryGetProperty("audio", out var a) && a.TryGetProperty("sampleRate", out var rate) && a.TryGetProperty("channels", out var channels) && a.TryGetProperty("samples", out var samples)
                && rate.TryGetInt32(out var sampleRate) && channels.TryGetInt32(out var channelCount) && samples.ValueKind == JsonValueKind.Array)
                audio = new MediaAudioFrame(sampleRate, channelCount, samples.EnumerateArray().Select(x => x.GetSingle()).ToArray());
            return (video, audio);
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { session.RequestMediaCaptureAsync(new { action = "close", token }).GetAwaiter().GetResult(); } catch { }
            removed();
            _videoFrameAvailable = null; _audioFrameAvailable = null; _revoked = null;
        }
        public void Apply(JsonElement payload)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (payload.TryGetProperty("video", out var v) && v.TryGetProperty("width", out var w) && v.TryGetProperty("height", out var h) && v.TryGetProperty("rgba", out var rgba)
                && w.TryGetInt32(out var width) && h.TryGetInt32(out var height))
            {
                var frame = new MediaCaptureVideoFrame(width, height, rgba.GetBytesFromBase64());
                if (frame.IsValid) _videoFrameAvailable?.Invoke(frame);
            }
            if (payload.TryGetProperty("audio", out var a) && a.TryGetProperty("sampleRate", out var rate) && a.TryGetProperty("channels", out var channels) && a.TryGetProperty("samples", out var samples)
                && rate.TryGetInt32(out var sampleRate) && channels.TryGetInt32(out var channelCount) && samples.ValueKind == JsonValueKind.Array)
                _audioFrameAvailable?.Invoke(new MediaAudioFrame(sampleRate, channelCount, samples.EnumerateArray().Select(x => x.GetSingle()).ToArray()));
            if (payload.TryGetProperty("revoked", out var revoked) && revoked.ValueKind == JsonValueKind.True) _revoked?.Invoke();
        }
    }

    private sealed class BrokerMediaAudioOutput(ContentHostSession session) : IMediaAudioOutput, IMediaAudioOutputLifecycle
    {
        public void Submit(MediaAudioFrame frame)
        {
            if (frame.SampleRate <= 0 || frame.Channels <= 0 || frame.Samples.Length > 2_000_000) return;
            session.SendAsync(ContentMessageKind.MediaAudioSubmit, new { sampleRate = frame.SampleRate, channels = frame.Channels, samples = frame.Samples.ToArray() }).AsTask().GetAwaiter().GetResult();
        }
        public void Start() => Send("start");
        public void Pause() => Send("pause");
        public void Flush() => Send("flush");
        public void Stop() => Send("stop");
        private void Send(string action) => session.SendAsync(ContentMessageKind.MediaAudioLifecycle, new { action }).AsTask().GetAwaiter().GetResult();
    }
}

public sealed record ContentDocumentResult(string Value, int DisplayItems, int CanvasCommands, string[] Diagnostics, Dictionary<string, double>? Timings = null);

public sealed record ContentGpuProbeResult(
    bool Succeeded,
    string? FailureMessage,
    string? FailureKind,
    string? RendererIdentity,
    long? ReservedBytes,
    long? PeakReservedBytes,
    long? NativeLocalBytes,
    long? NativeNonLocalBytes,
    int? NativeAdapterCount,
    string? NativeMemorySource,
    string? FallbackFeature,
    string? FallbackReason);
