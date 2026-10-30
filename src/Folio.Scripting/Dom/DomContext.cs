using System.Diagnostics.CodeAnalysis;
using Folio.Dom;
using Folio.Resources;
using Folio.Scripting.Gpu;

namespace Folio.Scripting.Dom;

/// <summary>
/// Context bridging Folio's DOM nodes with the host script engine and wrapper cache (study 02, study 17, #451).
/// </summary>
internal sealed class DomContext : IFetchContext
{
    private readonly Dictionary<string, IHostObjectTemplate> _htmlElementTemplates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ElementNode, (double X, double Y)> _scrollOffsets = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ElementNode, CanvasElementState> _canvases = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ElementNode, MediaElementState> _media = new(ReferenceEqualityComparer.Instance);
    private double _lastMediaTick = double.NaN;
    private readonly Dictionary<ElementNode, IframeState> _iframes = new(ReferenceEqualityComparer.Instance);
    private readonly MediaResourceBudget _mediaBudget;
    private IResourceLoader? _effectiveMediaLoader;
    private int _workerCount;
    private int _iframeCount;
    private readonly List<WorkerState> _workers = [];
    private readonly WorkerMemoryBudget _workerMemoryBudget;
    private readonly Dictionary<string, WorkerState> _sharedWorkers = new(StringComparer.Ordinal);
    private readonly object _workersGate = new();
    private CssomRegistry? _cssom;
    private readonly HashSet<string> _capabilityDenials = new(StringComparer.Ordinal);
    internal volatile bool ScriptFeaturesDisposed;
    internal CaptureBindingState? CaptureState { get; set; }
    internal WebRtcBindingState? WebRtcState { get; set; }
    internal ServiceWorkerCapabilityAuthority? ServiceWorkerState { get; set; }
    internal Action? DisposeServiceWorker { get; set; }
    internal Action? DisposeWebAssembly { get; set; }
    private GpuDocumentBridge? _gpu;
    private GpuFailure? _gpuCreationFailure;
    private readonly Func<long, GpuDeviceResult>? _gpuFactory;
    internal GpuDocumentBridge? Gpu => _gpu;
    internal INativeWebGlFactory? GetWebGlFactory(out GpuFailure? failure)
    {
        if (ScriptFeaturesDisposed || !Capabilities.WebGl || !Document.WebGlAllowed)
        {
            failure = new(GpuFailureKind.Unavailable, "Native WebGL access is denied for this document.");
            return null;
        }
        if (_gpu is null && _gpuCreationFailure is null)
            _gpu = GpuDocumentBridge.TryCreate(MaxGpuBytes, out _gpuCreationFailure, _gpuFactory);
        failure = _gpuCreationFailure;
        return _gpu?.WebGlFactory;
    }
    internal void ReportWebGlDiagnostic(string message) => Document.DiagnosticSink?.Invoke(new Diagnostic(DiagnosticCode.ScriptError, Severity.Warning, message, null, "webgl"));
    internal void LoseCanvasContexts()
    {
        foreach (var canvas in _canvases.Values.ToArray())
        {
            canvas.LoseNativeWebGlContext();
            try { canvas.WebGlContext?.Lose(); }
            catch (Exception error) { try { OwnerEventLoop?.ReportException(error); } catch { } }
        }
    }
    private int _ownerThread = Environment.CurrentManagedThreadId;
    internal void EnterDocumentTurn(IScriptEngine engine)
    {
        // The content transport serializes document turns, but async dispatch
        // may resume the next turn on a different physical thread.
        if (ReferenceEquals(engine, Engine)) Volatile.Write(ref _ownerThread, Environment.CurrentManagedThreadId);
    }
    internal HtmlEventLoop? OwnerEventLoop { get; set; }
    private readonly object _workerDiagnosticsGate = new();
    private readonly Queue<(Diagnostic Diagnostic, ScriptMessageBudget.Reservation Reservation)> _workerDiagnostics = new();

    internal void ReportWorkerDiagnostic(Diagnostic diagnostic)
    {
        if (ScriptFeaturesDisposed) return;
        if (Environment.CurrentManagedThreadId == Volatile.Read(ref _ownerThread))
        {
            if (diagnostic.Code == DiagnosticCode.CapabilityDenied) CheckCapability(diagnostic.Feature!, false);
            else Document.DiagnosticSink?.Invoke(diagnostic);
            return;
        }
        bool schedule;
        lock (_workerDiagnosticsGate)
        {
            if (ScriptFeaturesDisposed || _workerDiagnostics.Count >= 64) return;
            var message = diagnostic.Message.Length > 4096 ? diagnostic.Message[..4096] : diagnostic.Message;
            ScriptMessageBudget.Reservation reservation;
            try { reservation = ScriptMessageBudget.For(Engine, MaxWorkerMemoryBytes).Reserve(System.Text.Encoding.UTF8.GetByteCount(message) + 64); }
            catch (WebIdlException) { return; }
            schedule = _workerDiagnostics.Count == 0;
            _workerDiagnostics.Enqueue((diagnostic with { Message = message }, reservation));
        }
        if (schedule && OwnerEventLoop is { } loop)
        {
            try { loop.QueueTask(TaskSource.Generic, () => { EnterDocumentTurn(Engine); DrainWorkerDiagnostics(); }, "worker diagnostics"); }
            catch (ObjectDisposedException) { ClearWorkerDiagnostics(); }
        }
    }

    internal void DrainWorkerDiagnostics()
    {
        if (Environment.CurrentManagedThreadId != Volatile.Read(ref _ownerThread)) return;
        while (true)
        {
            (Diagnostic Diagnostic, ScriptMessageBudget.Reservation Reservation) next;
            lock (_workerDiagnosticsGate)
            {
                if (_workerDiagnostics.Count == 0) return;
                next = _workerDiagnostics.Dequeue();
            }
            using (next.Reservation)
                if (!ScriptFeaturesDisposed) ReportWorkerDiagnostic(next.Diagnostic);
        }
    }

    private void ClearWorkerDiagnostics()
    {
        lock (_workerDiagnosticsGate)
            while (_workerDiagnostics.TryDequeue(out var message)) message.Reservation.Dispose();
    }

    internal bool CheckCapability(string feature, bool granted)
    {
        if (granted && !ScriptFeaturesDisposed) return true;
        if (Environment.CurrentManagedThreadId != Volatile.Read(ref _ownerThread))
        {
            ReportWorkerDiagnostic(new Diagnostic(DiagnosticCode.CapabilityDenied, Severity.Warning,
                $"{feature} is disabled by the host.", null, feature));
            return false;
        }
        if (_capabilityDenials.Add(feature))
            Document.DiagnosticSink?.Invoke(new Diagnostic(DiagnosticCode.CapabilityDenied, Severity.Warning,
                $"{feature} is disabled by the host.", null, feature));
        return false;
    }

    /// <summary>The document's CSSOM sheets and rules.</summary>
    public CssomRegistry Cssom => _cssom ??= new CssomRegistry(this);

    /// <summary>The script functions behind <c>document.styleSheets</c> and an element's <c>sheet</c>, set when the CSSOM is installed.</summary>
    public ScriptValue CssomStyleSheets { get; set; }
    public ScriptValue CssomSheetOf { get; set; }
    private long _fetchedBytes;
    private readonly Dictionary<int, byte[]> _fetchBodies = [];
    private int _nextFetchBody;

    /// <summary>Keeps a fetched body here, out of script's memory, until the page reads it (or drops it): returns its handle.</summary>
    public int StoreFetchBody(byte[] data)
    {
        _fetchBodies[++_nextFetchBody] = data;
        return _nextFetchBody;
    }

    public byte[]? FetchBody(int handle) => _fetchBodies.GetValueOrDefault(handle);

    public void FreeFetchBody(int handle) => _fetchBodies.Remove(handle);

    /// <summary>The most one document may fetch in all (study 16).</summary>
    public const long MaxFetchedBytes = 64L * 1024 * 1024;

    /// <summary>
    /// A fetch() load: a data: URL or the host's loader, one response within the loader's size cap and all of them within
    /// <see cref="MaxFetchedBytes"/>. Refusals are reported as a diagnostic.
    /// </summary>
    public (ResourceResponse? Response, string? Error) Fetch(string url, string? integrity = null)
    {
        string? error = null;
        ResourceResponse? response = null;
        var allowed = url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        if (!allowed)
            error = "Only data:, http: and https: URLs can be fetched.";
        else
        {
                var loaded = new Folio.Resources.ResourceLoader(allowedFolders: null, maxBytes: Folio.Resources.ResourceLoader.MaxResourceBytes, host: ResourceLoader)
                .Load(new ResourceRequest(url, ResourceKind.Fetch, integrity));
            if (!loaded.Succeeded)
                error = loaded.Error ?? "The load failed.";
            else if (_fetchedBytes + loaded.Data!.Length > MaxFetchedBytes)
                error = "This document has already fetched the most bytes it may.";
            else
            {
                _fetchedBytes += loaded.Data.Length;
                response = loaded;
            }
        }

        if (response is null)
            Document.DiagnosticSink?.Invoke(new Diagnostic(DiagnosticCode.ResourceNotLoaded, Severity.Warning, $"fetch('{(url.Length > 120 ? url[..120] + "..." : url)}') failed: {error}", null, "fetch"));
        return (response, error);
    }

    public DomContext(IScriptEngine engine, DocumentNode document, ScriptWrapperCache? wrapperCache = null,
        Func<ElementNode, (float X, float Y, float Width, float Height)?>? layout = null,
        FeatureCapabilities? capabilities = null, int maxGpuBytes = 64 * 1024 * 1024, int maxMediaBytes = 128 * 1024 * 1024, int maxMediaDecoders = 8, int maxWorkerMessageBytes = 1 * 1024 * 1024, int maxWebAssemblyBytes = 64 * 1024 * 1024, int maxWorkers = 8, long maxWorkerMemoryBytes = 64L * 1024 * 1024, long maxAggregateWorkerMemoryBytes = 512L * 1024 * 1024, TimeSpan maxWorkerExecutionTime = default, long maxServiceWorkerCacheBytes = 64L * 1024 * 1024, int maxIframes = 16, int maxWebRtcConnections = 8, IReadOnlySet<string>? webRtcAllowedOrigins = null, Func<MediaCaptureRequest, bool>? mediaCapturePermission = null, IMediaCaptureProvider? mediaCaptureProvider = null, IArtifactStorage? artifactStorage = null, string artifactId = "default", IResourceLoader? resourceLoader = null, Uri? baseUri = null,
        Folio.Painting.IRasterBackend? raster = null, Func<Folio.Hosting.CanvasStyleEngine?>? canvasStyles = null, Action<bool>? mediaCaptureActivityChanged = null, Action<Uri>? iframeNavigationRequested = null, IMediaAudioOutput? mediaAudioOutput = null, IMediaDecoder? mediaDecoder = null, Func<long, GpuDeviceResult>? gpuFactory = null, bool formsAllowed = true)
    {
        Engine = engine ?? throw new ArgumentNullException(nameof(engine));
        Document = document ?? throw new ArgumentNullException(nameof(document));
        WrapperCache = wrapperCache ?? new ScriptWrapperCache();
        Layout = layout;
        Capabilities = capabilities ?? FeatureCapabilities.Default;
        Document.SetFeatureAvailability(Capabilities.WebGl, Capabilities.Media, Capabilities.Sound, Capabilities.Iframes);
        MaxGpuBytes = Math.Max(0, maxGpuBytes);
        _gpuFactory = gpuFactory;
        MaxMediaBytes = Math.Max(0, maxMediaBytes);
        MaxMediaDecoders = Math.Max(0, maxMediaDecoders);
        _mediaBudget = new MediaResourceBudget(MaxMediaBytes, MaxMediaDecoders);
        MaxWorkerMessageBytes = Math.Max(0, maxWorkerMessageBytes);
        MaxWorkers = Math.Max(0, maxWorkers);
        MaxWorkerMemoryBytes = Math.Max(1, maxWorkerMemoryBytes);
        _workerMemoryBudget = new WorkerMemoryBudget(maxAggregateWorkerMemoryBytes <= 0 ? MaxWorkerMemoryBytes : maxAggregateWorkerMemoryBytes);
        MaxWorkerExecutionTime = maxWorkerExecutionTime <= TimeSpan.Zero ? TimeSpan.FromSeconds(5) : maxWorkerExecutionTime;
        MaxServiceWorkerCacheBytes = Math.Max(0, maxServiceWorkerCacheBytes);
        MaxIframes = Math.Max(0, maxIframes);
        MaxWebRtcConnections = Math.Max(0, maxWebRtcConnections);
        WebRtcAllowedOrigins = webRtcAllowedOrigins ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        MediaCapturePermission = mediaCapturePermission;
        MediaCaptureProvider = mediaCaptureProvider;
        MediaCaptureActivityChanged = mediaCaptureActivityChanged;
        FormsAllowed = formsAllowed;
        MediaAudioOutput = mediaAudioOutput;
        MediaDecoder = mediaDecoder;
        IframeNavigationRequested = iframeNavigationRequested;
        ArtifactStorage = artifactStorage;
        ArtifactId = string.IsNullOrWhiteSpace(artifactId) ? "default" : artifactId;
        MaxWebAssemblyBytes = Math.Max(0, maxWebAssemblyBytes);
        ResourceLoader = resourceLoader;
        BaseUri = baseUri;
        RasterBackend = raster;
        _canvasStyles = canvasStyles;
        ParentOptions = null;

        Templates = new DomNodeTemplates(this);
    }

    public IScriptEngine Engine { get; }
    public DocumentNode Document { get; }
    internal FolioOptions? ParentOptions { get; set; }
    public ScriptWrapperCache WrapperCache { get; }
    public Func<ElementNode, (float X, float Y, float Width, float Height)?>? Layout { get; }
    private volatile FeatureCapabilities _capabilities = FeatureCapabilities.Default;
    public FeatureCapabilities Capabilities { get => _capabilities; private set => _capabilities = value; }
    public int MaxGpuBytes { get; }
    public int MaxMediaBytes { get; }
    public int MaxMediaDecoders { get; }
    public int MaxWorkerMessageBytes { get; }
    public int MaxWorkers { get; }
    public long MaxWorkerMemoryBytes { get; }
    public long MaxAggregateWorkerMemoryBytes => _workerMemoryBudget.Maximum;
    internal long ReservedWorkerMemoryBytes => _workerMemoryBudget.ReservedBytes;
    internal WorkerMemoryBudget WorkerMemoryBudget => _workerMemoryBudget;
    public TimeSpan MaxWorkerExecutionTime { get; }
    public long MaxServiceWorkerCacheBytes { get; }
    public int MaxIframes { get; }
    public int MaxWebRtcConnections { get; }
    public IReadOnlySet<string> WebRtcAllowedOrigins { get; }
    public Func<MediaCaptureRequest, bool>? MediaCapturePermission { get; }
    public IMediaCaptureProvider? MediaCaptureProvider { get; }
    public Action<bool>? MediaCaptureActivityChanged { get; }
    public bool FormsAllowed { get; }
    public IMediaAudioOutput? MediaAudioOutput { get; }
    public IMediaDecoder? MediaDecoder { get; }
    public Action<Uri>? IframeNavigationRequested { get; }
    public IArtifactStorage? ArtifactStorage { get; }
    public string ArtifactId { get; }
    public bool TryAcquireWorker()
    {
        if (MaxWorkers <= 0) return false;
        var count = Interlocked.Increment(ref _workerCount);
        if (count <= MaxWorkers) return true;
        Interlocked.Decrement(ref _workerCount);
        return false;
    }
    internal bool TryAcquireWorkerMemory() => _workerMemoryBudget.TryAcquire(MaxWorkerMemoryBytes);
    internal void ReleaseWorkerMemory() => _workerMemoryBudget.Release(MaxWorkerMemoryBytes);
    public void ReleaseWorker() { if (Interlocked.Decrement(ref _workerCount) < 0) Interlocked.Exchange(ref _workerCount, 0); }
    internal void UnregisterWorker(WorkerState worker)
    {
        lock (_workersGate) _workers.Remove(worker);
    }
    public void RegisterWorker(WorkerState worker)
    {
        FeatureCapabilities capabilities;
        lock (_workersGate)
        {
            capabilities = ScriptFeaturesDisposed ? FeatureCapabilities.Default : Capabilities;
            worker.PublishCapabilities(capabilities);
            _workers.Add(worker);
        }
        if (!capabilities.Workers) worker.Terminate();
    }

    internal bool TryGetSharedWorker(string key, out WorkerState worker)
    {
        lock (_workersGate) return _sharedWorkers.TryGetValue(key, out worker!);
    }

    internal void RegisterSharedWorker(string key, WorkerState worker)
    {
        lock (_workersGate) _sharedWorkers[key] = worker;
    }

    internal void UnregisterSharedWorker(WorkerState worker)
    {
        lock (_workersGate)
        {
            foreach (var key in _sharedWorkers.Where(pair => ReferenceEquals(pair.Value, worker)).Select(pair => pair.Key).ToArray())
                _sharedWorkers.Remove(key);
        }
    }

    public void DisposeMedia()
    {
        foreach (var media in _media.Values) media.StopPlayback();
        _media.Clear();
    }
    public void DisposeCanvases()
    {
        foreach (var canvas in _canvases.Values) canvas.Dispose();
        _canvases.Clear();
    }
    internal void DisposeGpu() { _gpu?.Dispose(); _gpu = null; _gpuCreationFailure = null; }

    public void DisposeIframes()
    {
        foreach (var frame in _iframes.Values) frame.Close();
        _iframes.Clear();
        _iframeCount = 0;
    }

    public void DisposeWorkers()
    {
        WorkerState[] workers;
        lock (_workersGate)
        {
            workers = _workers.ToArray();
            _workers.Clear();
        }
        foreach (var worker in workers) worker.Terminate();
        WorkerState[] shared;
        lock (_workersGate)
        {
            shared = _sharedWorkers.Values.Distinct(ReferenceEqualityComparer.Instance).Cast<WorkerState>().ToArray();
            _sharedWorkers.Clear();
        }
        foreach (var worker in shared)
            if (!workers.Contains(worker, ReferenceEqualityComparer.Instance)) worker.Terminate();
        ClearWorkerDiagnostics();
    }
    public int MaxWebAssemblyBytes { get; }
    public IResourceLoader? ResourceLoader { get; }
    private IResourceLoader? EffectiveMediaLoader =>
        ResourceLoader is { } loader
            ? _effectiveMediaLoader ??= new InterceptingMediaLoader(loader, () => ParentOptions?.ResourceInterceptor)
            : null;

    private sealed class InterceptingMediaLoader(IResourceLoader host, Func<IResourceInterceptor?> interceptor) : IResourceLoader
    {
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            if (interceptor()?.Intercept(request) is { } response)
                return Task.FromResult<ResourceResponse?>(response);
            return host.LoadAsync(request, cancellationToken);
        }
    }
    internal Action? DisposeCacheStorage { get; set; }
    public Uri? BaseUri { get; }
    public Folio.Painting.IRasterBackend? RasterBackend { get; }
    private readonly Func<Folio.Hosting.CanvasStyleEngine?>? _canvasStyles;
    private readonly Dictionary<ElementNode, (string Source, Folio.Imaging.IImageHandle? Image)> _images = new(ReferenceEqualityComparer.Instance);
    public DomNodeTemplates Templates { get; }
    public ElementNode? FocusedElement { get; private set; }
    public void Focus(ElementNode element) => FocusedElement = element;
    public void Blur(ElementNode element) { if (ReferenceEquals(FocusedElement, element)) FocusedElement = null; }

    public void UpdateCapabilities(FeatureCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var wasWebGlEnabled = Capabilities.WebGl;
        var wasMediaEnabled = Capabilities.Media;
        WorkerState[] workers;
        lock (_workersGate)
        {
            Capabilities = capabilities;
            Document.SetFeatureAvailability(capabilities.WebGl, capabilities.Media, capabilities.Sound, capabilities.Iframes);
            workers = _workers.ToArray();
            foreach (var worker in workers) worker.PublishCapabilities(capabilities);
        }
        // Joining a worker must not hold the registration lock.
        if (!capabilities.Workers)
            foreach (var worker in workers) worker.Terminate();
        CaptureState?.UpdateCapabilities();
        WebRtcState?.UpdateCapabilities();
        ServiceWorkerState?.UpdateCapabilities(capabilities.ServiceWorkers);
        _iframeCount = 0;
        foreach (var frame in _iframes.Values)
        {
            var allowed = capabilities.Iframes && _iframeCount < MaxIframes;
            if (allowed) _iframeCount++;
            frame.UpdateCapabilities(allowed ? capabilities : capabilities with { Iframes = false });
        }
        if (wasMediaEnabled != capabilities.Media)
            foreach (var media in _media.Values) media.SetCapability(capabilities.Media);
        foreach (var media in _media.Values) media.SetSoundCapability(capabilities.Sound);
        if (wasWebGlEnabled == capabilities.WebGl) return;
        if (wasWebGlEnabled && !capabilities.WebGl) DisposeGpu();
        foreach (var canvas in Document.Descendants.OfType<ElementNode>().Where(element => element.LocalName == "canvas"))
        {
            if (canvas.CanvasSurface is not CanvasElementState state) continue;
            if (!Capabilities.WebGl) state.LoseNativeWebGlContext();
            else state.RestoreNativeWebGlContext();
            if (state.WebGlContext is { } context)
            {
                try
                {
                    if (Capabilities.WebGl && !ScriptFeaturesDisposed) context.Restore();
                    else { state.MarkHostDenied(); context.Lose(); }
                }
                catch (Exception error) { try { OwnerEventLoop?.ReportException(error); } catch { } }
            }
        }
    }

    /// <summary>Notifies every live WebGL canvas that its host GPU/device was reset.</summary>
    internal void NotifyGpuDeviceLost()
    {
        if (ScriptFeaturesDisposed) return;
        foreach (var canvas in Document.Descendants.OfType<ElementNode>().Where(element => element.LocalName == "canvas"))
            if (canvas.CanvasSurface is CanvasElementState state) state.LoseNativeWebGlContext();
        DisposeGpu();
    }

    /// <summary>Recreates native WebGL contexts after a host GPU/device reset.</summary>
    internal void NotifyGpuDeviceRestored()
    {
        if (ScriptFeaturesDisposed || !Capabilities.WebGl || !Document.WebGlAllowed) return;
        foreach (var canvas in Document.Descendants.OfType<ElementNode>().Where(element => element.LocalName == "canvas"))
        {
            if (canvas.CanvasSurface is not CanvasElementState state) continue;
            try
            {
                state.RestoreNativeWebGlContext();
                state.WebGlContext?.Restore();
            }
            catch (Exception error) { try { OwnerEventLoop?.ReportException(error); } catch { } }
        }
    }

    public (double X, double Y) GetScroll(ElementNode element) => _scrollOffsets.TryGetValue(element, out var value) ? value : (0, 0);
    public void SetScroll(ElementNode element, double x, double y) => _scrollOffsets[element] = (Math.Max(0, x), Math.Max(0, y));

    private MutationObserverRegistry? _mutationObservers;

    /// <summary>The document's mutation observers (https://dom.spec.whatwg.org/#mutation-observers).</summary>
    public MutationObserverRegistry MutationObservers => _mutationObservers ??= new MutationObserverRegistry(this);

    public ScriptValue WrapMutationRecord(MutationRecordState record) => WrapperCache.GetOrCreate(Engine, Templates.MutationRecordTemplate, record);

    public CanvasElementState GetCanvas(ElementNode element) =>
        _canvases.TryGetValue(element, out var state) ? state : (_canvases[element] = new CanvasElementState(element, RasterBackend, _canvasStyles?.Invoke()));

    /// <summary>Internal host validation hook: clears a real native offscreen surface and publishes its readback through the normal canvas paint path.</summary>
    internal bool TryClearNativeCanvas(ElementNode element, float red, float green, float blue, float alpha, out GpuFailure? failure)
    {
        if (ScriptFeaturesDisposed || !ReferenceEquals(element.OwnerDocument, Document) || !string.Equals(element.LocalName, "canvas", StringComparison.OrdinalIgnoreCase))
        {
            failure = new(GpuFailureKind.Unavailable, "The element is not a live canvas in this document.");
            return false;
        }
        if (!Capabilities.WebGl || !Document.WebGlAllowed)
        {
            failure = new(GpuFailureKind.Unavailable, "Native canvas GPU access is denied for this document.");
            var denied = GetCanvas(element);
            denied.MarkWebGlDenied();
            return false;
        }
        if (_gpu is null && _gpuCreationFailure is null)
            _gpu = GpuDocumentBridge.TryCreate(MaxGpuBytes, out _gpuCreationFailure, _gpuFactory);
        var canvas = GetCanvas(element);
        if (_gpu is null)
        {
            failure = _gpuCreationFailure ?? new(GpuFailureKind.Unavailable, "The document has no native GPU device.");
            canvas.MarkGpuFailure(failure);
            return false;
        }
        var ok = _gpu.TryClear(canvas, red, green, blue, alpha, out failure);
        if (!ok && failure is not null) canvas.MarkGpuFailure(failure);
        return ok;
    }

    /// <summary>The pixels an <c>img</c> element shows, decoded from its <c>src</c> (a data: URL, or what the host's loader gives); null while it has none.</summary>
    public Folio.Imaging.IImageHandle? ImageOf(ElementNode image)
    {
        var source = image.GetAttribute("src")?.Trim() ?? "";
        if (_images.TryGetValue(image, out var known) && known.Source == source)
            return known.Image;
        Folio.Imaging.IImageHandle? decoded = null;
        if (source.Length > 0)
        {
            byte[]? bytes = null;
            if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (Folio.Resources.DataUrl.TryParse(source, out var data, out _))
                    bytes = data;
            }
            else if (ResourceLoader is { } loader)
            {
                var url = BaseUri is not null && Uri.TryCreate(BaseUri, source, out var resolved) ? resolved.AbsoluteUri : source;
                if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
                {
                    bytes = TryServiceWorkerResource(absolute.AbsoluteUri)?.Data ??
                        loader.LoadAsync(new ResourceRequest(absolute.AbsoluteUri, ResourceKind.Image), CancellationToken.None).GetAwaiter().GetResult()?.Data;
                }
            }
            decoded = bytes is null ? null : Folio.Imaging.ImageLoader.Decode(bytes);
        }
        _images[image] = (source, decoded);
        return decoded;
    }

    private sealed record ServiceWorkerResourceResult(byte[] Data, string? ContentType);

    private ServiceWorkerResourceResult? TryServiceWorkerResource(string url)
    {
        if (ServiceWorkerState is null || !Capabilities.ServiceWorkers || ScriptFeaturesDisposed)
            return null;

        const string resultName = "__folio_service_worker_image_result";
        const string typeName = "__folio_service_worker_image_type";
        Engine.SetGlobalProperty(resultName, ScriptValue.Null);
        Engine.SetGlobalProperty(typeName, ScriptValue.Null);
        try
        {
            Engine.SetGlobalProperty("__folio_service_worker_image_url", ScriptValue.FromString(url));
            Engine.Evaluate("""
                (function() {
                    var controller = navigator.serviceWorker && navigator.serviceWorker.controller;
                    if (!controller || typeof controller._dispatchFetch !== 'function') { globalThis.__folio_service_worker_image_result = false; return; }
                    try {
                        controller._dispatchFetch(new Request(globalThis.__folio_service_worker_image_url)).then(function(response) {
                            if (!(response instanceof Response)) { globalThis.__folio_service_worker_image_result = false; return; }
                            response.bytes().then(function(data) {
                                globalThis.__folio_service_worker_image_type = response.headers && response.headers.get('content-type');
                                // Keep the response as a typed array until the host extracts it. Converting
                                // binary data to a comma-separated string multiplies memory and corrupts the
                                // bridge's behavior for large media/font responses.
                                globalThis.__folio_service_worker_image_result = data;
                            }, function(error) {
                                globalThis.__folio_service_worker_image_result = false;
                                globalThis.__folio_service_worker_image_result = false;
                            });
                        }).catch(function(error) { globalThis.__folio_service_worker_image_result = false; globalThis.__folio_service_worker_debug = error && error.message; });
                    } catch (_) { globalThis.__folio_service_worker_image_result = false; }
                })();
                """, "folio:service-worker-image");
            for (var i = 0; i < 64; i++)
            {
                Engine.PerformMicrotaskCheckpoint();
                var result = Engine.GetGlobalProperty(resultName);
                if (Engine.TryGetBytes(result, out var bytes))
                {
                    var contentType = Engine.GetGlobalProperty(typeName);
                    return new ServiceWorkerResourceResult(bytes, contentType.IsString ? contentType.AsString() : null);
                }
                if (result.IsBoolean && !result.AsBoolean()) return null;
            }
            return null;
        }
        finally
        {
            Engine.SetGlobalProperty("__folio_service_worker_image_url", ScriptValue.Undefined);
            Engine.SetGlobalProperty(resultName, ScriptValue.Undefined);
            Engine.SetGlobalProperty(typeName, ScriptValue.Undefined);
        }
    }

    private sealed class ServiceWorkerResourceInterceptor(DomContext context) : Folio.Resources.IResourceInterceptor
    {
        public Folio.Resources.ResourceResponse? Intercept(Folio.Resources.ResourceRequest request)
        {
            // ResourceLoader is the common choke point for document-owned subresources. Keep the
            // service-worker bridge limited to resources whose response bytes can be consumed by
            // the existing Folio loaders; fetch()/script loading have their own lifecycle hooks.
            if (request.Kind is not (ResourceKind.Image or ResourceKind.SvgImage or ResourceKind.Stylesheet or ResourceKind.Font or ResourceKind.Media or ResourceKind.Document))
                return null;
            var resource = context.TryServiceWorkerResource(request.Url);
            if (resource is null) return null;
            var contentType = resource.ContentType ?? request.Kind switch
            {
                ResourceKind.Stylesheet => "text/css",
                ResourceKind.Font => "font/woff2",
                ResourceKind.Media => "application/octet-stream",
                ResourceKind.Document => "text/html",
                ResourceKind.SvgImage => "image/svg+xml",
                _ => "image/png",
            };
            return Folio.Resources.ResourceResponse.Loaded(resource.Data, contentType);
        }
    }

    internal Folio.Resources.IResourceInterceptor CreateServiceWorkerResourceInterceptor() => new ServiceWorkerResourceInterceptor(this);

    public MediaElementState GetMedia(ElementNode element)
    {
        if (_media.TryGetValue(element, out var existing)) return existing;
        if (ScriptFeaturesDisposed)
        {
            var retired = new MediaElementState(Engine, Templates.MediaEventTemplate, element, enabled: false, deferLoad: true);
            retired.StopPlayback();
            return retired;
        }
        var state = new MediaElementState(Engine, Templates.MediaEventTemplate, element, Capabilities.Media, Capabilities.AllowSoundAutoplay, EffectiveMediaLoader, BaseUri, MediaAudioOutput, MediaDecoder, MaxMediaBytes, _mediaBudget,
            soundEnabled: Capabilities.Sound, checkSoundPermission: () => CheckCapability("Sound", Capabilities.Sound),
            checkMediaPermission: () => CheckCapability("Media", Capabilities.Media),
            captureFrame: MediaCaptureProvider is null ? null : track => CaptureState?.CurrentVideoFrame(Engine.GetProperty(track, "_captureToken")),
            captureAudio: MediaCaptureProvider is null ? null : track => CaptureState?.CurrentAudioFrame(Engine.GetProperty(track, "_captureToken")), deferLoad: true);
        _media[element] = state;
        state.Initialize();
        if (element.GetAttribute("autoplay") is not null) state.Play(autoplay: true);
        return state;
    }

    internal void AdvanceMedia(double timestampMs)
    {
        if (double.IsNaN(_lastMediaTick))
        {
            _lastMediaTick = timestampMs;
            return;
        }

        var delta = Math.Max(0, timestampMs - _lastMediaTick) / 1000.0;
        _lastMediaTick = timestampMs;
        if (delta <= 0) return;
        foreach (var media in _media.Values)
            media.AdvancePlayback(delta);
    }

    /// <summary>Creates presentation state for media elements before the first paint, so native controls and cues render even when script never touches the element.</summary>
    internal void InitializeMediaElements()
    {
        for (Node? node = Document; node is not null; node = node.NextInTree(Document))
            if (node is ElementNode { Name.Namespace: var ns, LocalName: var name } element
                && ns == Namespaces.Html && name is ("video" or "audio"))
                _ = GetMedia(element);
    }

    internal void InitializeIframeElements()
    {
        var initialized = 0;
        for (Node? node = Document; node is not null; node = node.NextInTree(Document))
        {
            if (node is ElementNode { Name.Namespace: var ns, LocalName: "iframe" } element && ns == Namespaces.Html)
            {
                if (initialized >= MaxIframes)
                    break;
                _ = GetIframe(element);
                initialized++;
            }
        }
    }
    public IframeState GetIframe(ElementNode element)
    {
        if (_iframes.TryGetValue(element, out var state)) return state;
        if (Capabilities.Iframes && _iframeCount >= MaxIframes)
            throw WebIdlException.DomException(DomExceptionNames.QuotaExceededError, "The iframe limit has been exceeded.");
        var created = new IframeState(Engine, Templates.MessageEventTemplate, element, Capabilities.Iframes && !ScriptFeaturesDisposed, ResourceLoader, BaseUri, IframeNavigationRequested, ParentOptions,
            checkPermission: () => CheckCapability("Iframes", Capabilities.Iframes), parentLoop: OwnerEventLoop);
        if (Capabilities.Iframes && !created.TryAcquireAggregateSlot())
            throw WebIdlException.DomException(DomExceptionNames.QuotaExceededError, "The aggregate iframe limit has been exceeded.");
        if (Capabilities.Iframes) _iframeCount++;
        return _iframes[element] = created;
    }

    public ScriptValue Wrap(Node? node)
    {
        if (node is null)
            return ScriptValue.Null;

        if (WrapperCache.TryGet(node, out var existing))
            return existing;

        var template = SelectTemplate(node);
        return WrapperCache.GetOrCreate(Engine, template, node);
    }

    public Node UnwrapNode(ScriptValue value)
    {
        if (Engine.TryGetHostTarget(value, out var target) && target is Node node)
            return node;

        throw WebIdlException.TypeError("Argument is not a Node.");
    }

    public Node? UnwrapNodeOptional(ScriptValue value)
    {
        if (value.IsNullOrUndefined)
            return null;

        return UnwrapNode(value);
    }

    public ScriptValue WrapNodeList(ContainerNode container)
    {
        ArgumentNullException.ThrowIfNull(container);
        return WrapperCache.GetOrCreate(Engine, Templates.NodeListTemplate, new LiveNodeList(container));
    }

    public ScriptValue WrapHtmlCollection(ContainerNode container)
    {
        ArgumentNullException.ThrowIfNull(container);
        return WrapperCache.GetOrCreate(Engine, Templates.HtmlCollectionTemplate, new LiveHtmlCollection(container));
    }

    public ScriptValue WrapClassList(ElementNode element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return WrapperCache.GetOrCreate(Engine, Templates.DomTokenListTemplate, new LiveDomTokenList(element));
    }

    public ScriptValue WrapDataset(ElementNode element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return WrapperCache.GetOrCreate(Engine, Templates.DomStringMapTemplate, new LiveDomStringMap(element));
    }

    public ScriptValue WrapAttributes(ElementNode element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return WrapperCache.GetOrCreate(Engine, Templates.NamedNodeMapTemplate, new LiveNamedNodeMap(element));
    }

    public ScriptValue WrapStyle(ElementNode element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return WrapperCache.GetOrCreate(Engine, Templates.StyleDeclarationTemplate, new LiveStyleDeclaration(element));
    }

    public ScriptValue WrapComputedStyle(ElementNode element) =>
        WrapperCache.GetOrCreate(Engine, Templates.ComputedStyleDeclarationTemplate, new LiveComputedStyle(element));

    public ScriptValue WrapRect(DomRect rect) =>
        WrapperCache.GetOrCreate(Engine, Templates.DomRectTemplate, rect);

    private readonly Dictionary<(ElementNode, string), LiveAttr> _attrCache = new();

    public ScriptValue WrapAttr(ElementNode element, string name)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(name);

        var key = (element, name);
        if (!_attrCache.TryGetValue(key, out var liveAttr) || liveAttr.Element is null)
        {
            liveAttr = new LiveAttr(element, name);
            _attrCache[key] = liveAttr;
        }

        return WrapperCache.GetOrCreate(Engine, Templates.AttrTemplate, liveAttr);
    }

    public IHostObjectTemplate GetHtmlElementTemplate(string tagName)
    {
        var local = tagName.ToLowerInvariant();
        return local switch
        {
            "input" => Templates.HtmlInputElementTemplate,
            "button" => Templates.HtmlButtonElementTemplate,
            "a" => Templates.HtmlAnchorElementTemplate,
            "img" => Templates.HtmlImageElementTemplate,
            "option" => Templates.HtmlOptionElementTemplate,
            "select" => Templates.HtmlSelectElementTemplate,
            "textarea" => Templates.HtmlTextAreaElementTemplate,
            "template" => Templates.HtmlTemplateElementTemplate,
            "canvas" => Templates.HtmlCanvasElementTemplate,
            "video" => Templates.HtmlVideoElementTemplate,
            "audio" => Templates.HtmlAudioElementTemplate,
            "iframe" or "frame" => Templates.HtmlIFrameElementTemplate,
            "script" => Templates.HtmlScriptElementTemplate,
            _ => GetStandardHtmlElementTemplate(local),
        };
    }

    private IHostObjectTemplate GetStandardHtmlElementTemplate(string local)
    {
        var interfaceName = local switch
        {
            "div" => "HTMLDivElement",
            "span" => "HTMLSpanElement",
            "p" => "HTMLParagraphElement",
            "body" => "HTMLBodyElement",
            "head" => "HTMLHeadElement",
            "html" => "HTMLHtmlElement",
            "title" => "HTMLTitleElement",
            "template" => "HTMLTemplateElement",
            "script" => "HTMLScriptElement",
            _ => "HTMLElement",
        };

        if (!_htmlElementTemplates.TryGetValue(interfaceName, out var template))
        {
            template = new HostObjectTemplate(interfaceName, Templates.HtmlElementTemplate);
            _htmlElementTemplates[interfaceName] = template;
        }

        return template;
    }

    private IHostObjectTemplate SelectTemplate(Node node)
    {
        return node switch
        {
            DocumentNode => Templates.DocumentTemplate,
            DocumentType => Templates.DocumentTypeTemplate,
            DocumentFragment => Templates.DocumentFragmentTemplate,
            Text => Templates.TextTemplate,
            Comment => Templates.CommentTemplate,
            ElementNode el when el.Name.Namespace == Namespaces.Html => GetHtmlElementTemplate(el.LocalName),
            ElementNode el when el.Name.Namespace == Namespaces.Svg => Templates.SvgElementTemplate,
            ElementNode => Templates.ElementTemplate,
            _ => Templates.NodeTemplate,
        };
    }

    public ScriptValue WrapStaticNodeList(IReadOnlyList<Node> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        return WrapperCache.GetOrCreate(Engine, Templates.NodeListTemplate, new LiveNodeList(nodes));
    }
}

/// <summary>Represents a NodeList backed by either a ContainerNode (live) or static IReadOnlyList&lt;Node&gt;.</summary>
internal sealed class LiveNodeList
{
    public ContainerNode? Container { get; }
    public IReadOnlyList<Node>? StaticNodes { get; }

    public LiveNodeList(ContainerNode container) => Container = container;
    public LiveNodeList(IReadOnlyList<Node> staticNodes) => StaticNodes = staticNodes;

    public int Count => StaticNodes?.Count ?? Container?.Children.Count() ?? 0;

    public Node? GetItem(int index)
    {
        if (index < 0)
            return null;

        if (StaticNodes is not null)
            return index < StaticNodes.Count ? StaticNodes[index] : null;

        return Container?.Children.ElementAtOrDefault(index);
    }

    public List<Node> ToList()
    {
        if (StaticNodes is not null)
            return StaticNodes.ToList();

        return Container?.Children.ToList() ?? [];
    }
}

/// <summary>Represents a live HTMLCollection backed by a ContainerNode.</summary>
internal sealed class LiveHtmlCollection(ContainerNode container)
{
    public ContainerNode Container { get; } = container;
}
