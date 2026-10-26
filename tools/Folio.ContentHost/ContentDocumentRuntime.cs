using Folio;
using Folio.Dom;
using Folio.Html;
using Folio.Resources;
using Folio.Scripting;
using Folio.Scripting.Dom;
using Folio.Scripting.Gpu;
using Folio.Scripting.Ipc;
using Folio.Scripting.Jint;
using Folio.Skia;
using System.Text.Json;
using System.Collections.Concurrent;

namespace Folio.ContentHost;

/// <summary>Shared document bootstrap used by the production content host and trusted process probes.</summary>
public sealed class ContentDocumentRuntime : IDisposable
{
    private readonly ContentHostSession _session;
    private readonly CanvasFrameStore _frameStore;
    private readonly IResourceLoader _resourceLoader;
    private readonly List<string> _taskDiagnostics = new();
    private readonly Func<IScriptEngine> _engineFactory;
    private IScriptEngine? _engine;
    private HtmlEventLoop? _eventLoop;
    private ScriptRunner? _runner;
    private Document? _document;
    private BrokerMediaCaptureProvider? _captureProvider;

    public ContentDocumentRuntime(ContentHostSession session, CanvasFrameStore frameStore, Func<IScriptEngine>? engineFactory = null)
    {
        _session = session;
        _frameStore = frameStore;
        _resourceLoader = new BrokerResourceLoader(session);
        _engineFactory = engineFactory ?? (() => new JintScriptEngine());
    }

    public FeatureCapabilities Capabilities { get; private set; } = FeatureCapabilities.Default;
    public Document? Document => _document;
    public IScriptEngine? Engine => _engine;

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

    public void PublishCurrentFrame()
    {
        if (_document is null) return;
        using var rendered = HeadlessRenderer.Render(_document, new RenderRequest(1280, 720));
        _frameStore.Publish(rendered.Pixels);
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
            _captureProvider = new BrokerMediaCaptureProvider(_session);
            var options = new FolioOptions
            {
                Scripting = ScriptingPolicy.ContentProcess,
                Capabilities = Capabilities,
                BaseUri = Uri.TryCreate(url, UriKind.Absolute, out var baseUri) ? baseUri : null,
                ResourceLoader = _resourceLoader,
                MediaDecoder = new BrokerMediaDecoder(_session),
                MediaAudioOutput = new BrokerMediaAudioOutput(_session),
                MediaCaptureProvider = _captureProvider,
                ArtifactId = message.Payload.TryGetProperty("artifactId", out var artifact) ? artifact.GetString() ?? "default" : "default",
                ArtifactStorage = message.Payload.TryGetProperty("storageEnabled", out var storageEnabled) && storageEnabled.GetBoolean() ? new BrokerArtifactStorage(_session) : null,
                Limits = message.Payload.TryGetProperty("limits", out var limits) ? limits.Deserialize<ResourceLimits>() ?? ResourceLimits.Default : ResourceLimits.Default,
                MediaCaptureActivityChanged = active => _ = _session.SendAsync(ContentMessageKind.MediaCaptureActivity, new { active }, cancellationToken),
                Raster = new SkiaRasterBackend()
            };
            _session.SetStorageLimit(options.Limits.MaxServiceWorkerCacheBytes);
            _document = Document.Parse(htmlElement.GetString() ?? "", options);
            _engine = _engineFactory();
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
            ScriptGlobalsInstaller.Install(_eventLoop, _engine);
            _runner = new ScriptRunner(_document, _engine, _eventLoop, options);
            _runner.Run();
        }
        _engine ??= _engineFactory();
        _eventLoop?.SpinUntilIdle();
        var result = _engine.Evaluate(script, url);
        _eventLoop?.SpinUntilIdle();
        var frame = _document?.Paint(1280, 720).List;
        var canvasCommands = _document?.Node.Descendants.OfType<ElementNode>().Count(element => element.CanvasSurface is not null) ?? 0;
        if (_document is not null && canvasCommands > 0)
        {
            PublishCurrentFrame();
        }
        return Task.FromResult(new ContentDocumentResult(result.ToString(), frame?.Items.Count ?? 0, canvasCommands,
            _runner?.Diagnostics.Select(diagnostic => diagnostic.Message).Concat(_taskDiagnostics).ToArray() ?? _taskDiagnostics.ToArray()));
    }

    public void Dispose()
    {
        DisposeDocument();
    }

    private void DisposeDocument()
    {
        _captureProvider?.Dispose(); _captureProvider = null;
        _runner?.Dispose(); _runner = null;
        _engine?.Dispose(); _engine = null;
        _eventLoop?.Dispose(); _eventLoop = null;
        _document?.Dispose(); _document = null;
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

public sealed record ContentDocumentResult(string Value, int DisplayItems, int CanvasCommands, string[] Diagnostics);

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
