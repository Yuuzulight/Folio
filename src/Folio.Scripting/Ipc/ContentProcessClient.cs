using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Folio.Resources;
using Folio.Hosting;

namespace Folio.Scripting.Ipc;

public sealed class ContentProcessStoppedEventArgs(string reason) : EventArgs
{
    public string Reason { get; } = reason;
}

/// <summary>Host-side connection to an isolated Folio content process.</summary>
/// <remarks>
/// The child receives framed messages on standard input and writes framed messages to standard output.
/// Keeping process creation here gives FolioView and tests the same lifecycle and watchdog semantics.
/// </remarks>
public sealed class ContentProcessClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly ContentProcessSupervisor _supervisor;
    private readonly ContentHostSession _session;
    private readonly WindowsJobObject? _job;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _reader;
    private readonly Task _watchdog;
    private readonly Task _stderrReader;
    private readonly System.Text.StringBuilder _standardError = new();
    internal string StandardError { get { lock (_standardError) return _standardError.ToString(); } }
    private int _disposed;
    private readonly string _artifact;
    private readonly ResourceLimits _documentLimits;
    // Separate GPU worker (opt-in until its acceptance gate passes: docs/gpu-worker-remote-webgl-plan.md).
    private Folio.Scripting.Gpu.Remote.GpuWebGlWorker? _gpuWorker;
    private bool _gpuChannelSent, _gpuMemoryReserved;
    private int _gpuRestores;
    /// <summary>Replacement workers per content process; after these a lost WebGL context stays lost.</summary>
    internal const int MaxGpuRestores = 3;
    private string? _contentHostPath;
    /// <summary>Test seam; otherwise on by default whenever WebGL is granted (FOLIO_GPU_WORKER=0 acts as kill switch).</summary>
    internal static bool? GpuWorkerOverride { get; set; }
    internal long? JobMemoryLimit => OperatingSystem.IsWindows() ? _job?.MemoryLimit : null;
    internal int? GpuWorkerProcessId => OperatingSystem.IsWindows() ? _gpuWorker?.ProcessId : null;
    private readonly bool _storageEnabled;
    private readonly IMediaCaptureProvider? _mediaCaptureProvider;
    private const int MaxCaptureSessions = 16;
    private readonly ConcurrentDictionary<string, ContentCaptureLease> _mediaCaptureSessions = new();
    private readonly Channel<bool> _captureWake = Channel.CreateBounded<bool>(1);
    private readonly Task _capturePump;
    private Func<MediaCaptureRequest, CancellationToken, ValueTask<bool>>? _mediaCapturePermission;
    private FeatureCapabilities _hostCapabilities = FeatureCapabilities.Default;
    private long _captureGeneration;
    private int _hasLoadedDocument;
    private readonly Stream _input;
    private readonly Stream _error;
    private ContentViewport _viewport = new(1280, 720);
    private readonly SemaphoreSlim _viewportGate = new(1, 1);
    private readonly ConcurrentDictionary<string, long> _framePaths = new();
    private readonly IDisposable _security;
    private readonly object _navigationGate = new();
    private readonly Channel<NavigationDelivery> _navigationDeliveries = Channel.CreateBounded<NavigationDelivery>(
        new BoundedChannelOptions(32) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task _navigationWorker;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    // Serialises revocation notices so a host message sent after Revoke() cannot overtake one.
    private readonly SemaphoreSlim _captureRevocationGate = new(1, 1);
    private NavigationDocument? _navigationDocument;
    private HashSet<string> _webRtcAllowedOrigins = new(StringComparer.OrdinalIgnoreCase);
    private Action<Uri>? _iframeNavigationRequested;
    private Func<Action, CancellationToken, ValueTask>? _iframeNavigationDispatcher;
    private event Func<ContentMessage, CancellationToken, ValueTask>? _messageReceived;

    private ContentProcessClient(Process process, Stream input, Stream output, Stream error, CanvasFrameStore frameStore, string framePath, ContentProcessSupervisor supervisor, WindowsJobObject? job, IDisposable security, IResourceLoader? resourceLoader, ContentResourcePolicy? resourcePolicy, IArtifactStorage? storage, IMediaDecoder? mediaDecoder, IMediaAudioOutput? mediaAudioOutput, IMediaCaptureProvider? mediaCaptureProvider, string artifact, ResourceLimits documentLimits)
    {
        _process = process;
        _supervisor = supervisor;
        _job = job;
        _input = input; _error = error; _security = security; _frameStorePath = framePath;
        _framePaths.TryAdd(framePath, 0);
        _artifact = artifact; _documentLimits = documentLimits; _storageEnabled = storage is not null;
        _mediaCaptureProvider = mediaCaptureProvider;
        _session = new ContentHostSession(output, input, supervisor, resourcePolicy, resourceLoader, mediaDecoder, mediaAudioOutput);
        _session.HostCapabilities = () => _shutdown.IsCancellationRequested ? FeatureCapabilities.Default : Volatile.Read(ref _hostCapabilities);
        _session.MessageReceived += ValidateAndDispatchAsync;
        if (storage is not null) _session.ConfigureStorage(storage, artifact, documentLimits.MaxServiceWorkerCacheBytes);
        _supervisor.Stopped += OnStopped;
        _job?.MonitorMemory(() => _supervisor.Stop("content process memory limit exceeded"));
        // Complete fallible process-event setup while Start still owns the
        // launch resources, before starting any asynchronous readers.
        _process.Exited += OnProcessExited;
        _process.EnableRaisingEvents = true;
        _stderrReader = DrainErrorAsync(_shutdown.Token);
        _reader = ReadMessagesAsync();
        _watchdog = WatchdogAsync(_shutdown.Token);
        // One owned consumer, never one task per child notification. User code
        // must not hold the transport dispatcher needed by reentrant disposal.
        var navigationToken = _shutdown.Token;
        _navigationWorker = Task.Run(() => RunNavigationDeliveriesAsync(navigationToken));
        _capturePump = Task.Run(() => PumpCaptureAsync(navigationToken));
    }

    public Process Process => _process;
    public ContentProcessSupervisor Supervisor => _supervisor;
    public event EventHandler<ContentProcessStoppedEventArgs>? ScriptStopped;
    public event Func<ContentMessage, CancellationToken, ValueTask>? MessageReceived { add => _messageReceived += value; remove => _messageReceived -= value; }
    public string FramePath => _frameStorePath;
    private string _frameStorePath;

    /// <summary>Starts a content process without a resource loader.</summary>
    public static ContentProcessClient Start(string executablePath, string? arguments, ContentProcessLimits? limits) =>
        Start(executablePath, arguments, limits, null, null);

    /// <summary>Starts a content process with the original resource-broker API.</summary>
    public static ContentProcessClient Start(string executablePath, string? arguments, ContentProcessLimits? limits, IResourceLoader? resourceLoader, ContentResourcePolicy? resourcePolicy) =>
        Start(executablePath, arguments, limits, resourceLoader, resourcePolicy, null, "default", null);

    /// <summary>Starts a client with the host's resource, storage and media services. Capabilities are still supplied in each load/update message.</summary>
    /// <remarks>Permission runs on the client's reader thread. A UI host can supply an override that marshals to its UI thread.
    /// The navigation dispatcher must post the supplied action, rather than invoking the options callback directly.
    /// That action revalidates document lifetime and grants on the destination thread and delivers at most once.
    /// The dispatcher should complete after attempting delivery. Its pending token is cancelled on HTML replacement or shutdown;
    /// incremental script loads retain the document lifetime. Cancellation ends the client's wait even if the dispatcher ignores it.
    /// Navigation observer failures are contained, matching in-process iframe notification semantics.
    /// Navigation uses one delivery worker with at most 32 queued notifications; excess notifications are discarded.
    /// Retirement cancels queued or unclaimed deliveries. An invocation becomes in flight when its guarded action claims
    /// the current generation, immediately before calling user code outside the lifetime lock. Retirement does not abort
    /// or wait for in-flight user code, so callbacks may synchronously reload or dispose the client, including from a UI dispatcher.</remarks>
    public static ContentProcessClient StartWithOptions(string executablePath, FolioOptions options, ContentProcessLimits? limits = null,
        Func<MediaCaptureRequest, CancellationToken, ValueTask<bool>>? mediaCapturePermission = null,
        Func<Action, CancellationToken, ValueTask>? iframeNavigationDispatcher = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var origins = ContentHostPolicy.NormalizeOrigins(options.WebRtcAllowedOrigins);
        IResourceLoader? loader = options.ResourceInterceptor is { } interceptor
            ? new InterceptedHostLoader(interceptor, options.ResourceLoader) : options.ResourceLoader;
        var client = Start(executablePath, limits: limits, resourceLoader: loader,
            artifactStorage: options.ArtifactStorage, artifactId: options.ArtifactId, documentLimits: options.Limits,
            mediaDecoder: options.MediaDecoder, mediaAudioOutput: options.MediaAudioOutput, mediaCaptureProvider: options.MediaCaptureProvider);
        client._mediaCapturePermission = mediaCapturePermission ?? (options.MediaCapturePermission is { } permission
            ? (request, _) => ValueTask.FromResult(permission(request)) : null);
        client._webRtcAllowedOrigins = origins;
        client._iframeNavigationRequested = options.IframeNavigationRequested;
        client._iframeNavigationDispatcher = iframeNavigationDispatcher;
        return client;
    }

    private sealed class InterceptedHostLoader(IResourceInterceptor interceptor, IResourceLoader? next) : IResourceLoader
    {
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return interceptor.Intercept(request) is { } response ? Task.FromResult<ResourceResponse?>(response)
                : next?.LoadAsync(request, cancellationToken) ?? Task.FromResult<ResourceResponse?>(null);
        }
    }

    /// <param name="executablePath">The content-host executable or assembly.</param>
    /// <param name="arguments">Additional launch arguments.</param>
    /// <param name="limits">Process memory and heartbeat limits.</param>
    /// <param name="resourceLoader">The trusted host loader, which decides what resources are allowed. Without one, brokered resources are refused.</param>
    /// <param name="resourcePolicy">An optional additional restriction applied before calling the loader.</param>
    /// <param name="artifactStorage">Trusted persistent storage; the child can access only its artifact's cache and bounded registration metadata.</param>
    /// <param name="artifactId">Host-owned artifact scope, fixed for this connection.</param>
    /// <param name="documentLimits">Host-owned document limits, forwarded with each load.</param>
    public static ContentProcessClient Start(string executablePath, string? arguments = null, ContentProcessLimits? limits = null, IResourceLoader? resourceLoader = null, ContentResourcePolicy? resourcePolicy = null, IArtifactStorage? artifactStorage = null, string artifactId = "default", ResourceLimits? documentLimits = null, IMediaDecoder? mediaDecoder = null, IMediaAudioOutput? mediaAudioOutput = null, IMediaCaptureProvider? mediaCaptureProvider = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) throw new ArgumentException("An executable path is required.", nameof(executablePath));
        var effectiveLimits = limits ?? new ContentProcessLimits();
        var frameDirectory = Path.Combine(Path.GetTempPath(), $"folio-frame-{Guid.NewGuid():N}");
        Directory.CreateDirectory(frameDirectory);
        var framePath = Path.Combine(frameDirectory, "frame.bin");
        CanvasFrameStore frameStore;
        try { frameStore = new CanvasFrameStore(framePath, 1280, 720, 1280 * 4); }
        catch { CleanupFrame(framePath); throw; }
        WindowsContentLaunch? isolated = null;
        try
        {
            // The child owns the writable mapping. Keeping this initialization
            // mapping open would prevent it growing the file during a resize.
            frameStore.Dispose();
            if (OperatingSystem.IsWindows() && !string.Equals(Environment.GetEnvironmentVariable("FOLIO_UNSANDBOXED"), "1", StringComparison.Ordinal))
                isolated = WindowsContentLauncher.Start(executablePath, arguments, effectiveLimits, framePath);
            else
            {
                var (fileName, launchArguments) = Path.GetExtension(executablePath).Equals(".dll", StringComparison.OrdinalIgnoreCase) ? ("dotnet", $"{Quote(executablePath)}{(string.IsNullOrWhiteSpace(arguments) ? "" : $" {arguments}")}") : (executablePath, arguments ?? "");
                var startInfo = new ProcessStartInfo(fileName, launchArguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                startInfo.Environment["FOLIO_CONTENT_FRAME_PATH"] = framePath;
                var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start the content process.");
                isolated = new WindowsContentLaunch { Process = process, Input = process.StandardInput.BaseStream, Output = process.StandardOutput.BaseStream, Error = process.StandardError.BaseStream, Security = new NoopDisposable() };
            }
            var client = new ContentProcessClient(isolated.Process, isolated.Input, isolated.Output, isolated.Error, frameStore, framePath, new ContentProcessSupervisor(effectiveLimits), OperatingSystem.IsWindows() ? isolated.Job : null, isolated.Security, resourceLoader, resourcePolicy, artifactStorage, mediaDecoder, mediaAudioOutput, mediaCaptureProvider, string.IsNullOrWhiteSpace(artifactId) ? "default" : artifactId, documentLimits ?? ResourceLimits.Default);
            client._contentHostPath = isolated.Job is not null ? executablePath : null;
            isolated = null;
            return client;
        }
        catch { isolated?.Dispose(); frameStore.Dispose(); CleanupFrame(framePath); throw; }
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
    private void OnProcessExited(object? sender, EventArgs args) => _supervisor.Stop("content process exited");
    private async ValueTask ValidateAndDispatchAsync(ContentMessage message, CancellationToken cancellationToken)
    {
        if (message.Kind == ContentMessageKind.GpuRestore)
        {
            // Starting a worker takes a while; keep the inbound message path free meanwhile.
            _ = Task.Run(() => RestoreGpuChannelAsync(_shutdown.Token), CancellationToken.None);
            return;
        }
        if (message.Kind == ContentMessageKind.IframeNavigationRequested)
        {
            QueueIframeNavigation(message, cancellationToken);
            return;
        }
        if (message.Kind == ContentMessageKind.MediaCaptureRequest)
        {
            await HandleMediaCaptureAsync(message, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (message.Kind == ContentMessageKind.Frame)
        {
            try
            {
                if (!message.Payload.TryGetProperty("canvasFrame", out var frame)) throw new InvalidDataException();
                if (frame.ValueKind != JsonValueKind.Object || !frame.TryGetProperty("path", out var p) || !frame.TryGetProperty("width", out var w) || !frame.TryGetProperty("height", out var h) || !frame.TryGetProperty("stride", out var s)
                    || !w.TryGetInt32(out var width) || !h.TryGetInt32(out var height) || !s.TryGetInt32(out var stride)) throw new InvalidDataException();
                var path = p.GetString();
                var viewport = Volatile.Read(ref _viewport);
                var revision = frame.TryGetProperty("viewportRevision", out var revisionValue) ? revisionValue.GetInt64() : 0;
                if (revision >= 0 && revision < viewport.Revision) return;
                if (revision != viewport.Revision || !string.Equals(path, FramePath, StringComparison.Ordinal)
                    || (width, height, stride) != (viewport.Width, viewport.Height, viewport.Width * 4)) throw new InvalidDataException();
                foreach (var retired in _framePaths)
                    if (retired.Value < viewport.Revision)
                        try
                        {
                            File.Delete(retired.Key);
                            _framePaths.TryRemove(retired.Key, out _);
                            if (OperatingSystem.IsWindows() && _security is AppContainerSecurity security) security.ForgetFrame(retired.Key);
                        }
                        catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or FormatException or KeyNotFoundException or JsonException) { _supervisor.Stop("invalid content frame descriptor"); return; }
        }
        if (_messageReceived is { } handler) await handler(message, cancellationToken).ConfigureAwait(false);
    }

    private sealed class NavigationDocument
    {
        internal string Generation { get; } = Guid.NewGuid().ToString("N");
        internal CancellationTokenSource Cancellation { get; } = new();
    }

    private void RetireNavigationDocument()
    {
        NavigationDocument? retired;
        lock (_navigationGate) { retired = _navigationDocument; _navigationDocument = null; }
        if (retired is null) return;
        try { retired.Cancellation.Cancel(); }
        catch (AggregateException) { /* Dispatcher cancellation observers cannot prevent containment. */ }
        finally { retired.Cancellation.Dispose(); }
    }

    private sealed record NavigationDelivery(NavigationDocument Document, Uri Uri);

    private void QueueIframeNavigation(ContentMessage message, CancellationToken cancellationToken)
    {
        var payload = message.Payload;
        if (payload.ValueKind != JsonValueKind.Object
            || payload.EnumerateObject().Take(3).Count() != 2
            || !payload.TryGetProperty("generation", out var generation) || generation.ValueKind != JsonValueKind.String
            || generation.GetString() is not { Length: 32 } id
            || !payload.TryGetProperty("uri", out var value) || value.ValueKind != JsonValueKind.String
            || !ContentHostPolicy.TryNavigationUri(value.GetString(), out var uri)) return;
        lock (_navigationGate)
        {
            if (_navigationDocument is not { } current || current.Generation != id
                || cancellationToken.IsCancellationRequested || Volatile.Read(ref _disposed) != 0 || _shutdown.IsCancellationRequested
                || !Volatile.Read(ref _hostCapabilities).Iframes || _iframeNavigationRequested is null) return;
            _navigationDeliveries.Writer.TryWrite(new NavigationDelivery(current, uri!));
        }
    }

    private async Task RunNavigationDeliveriesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var notification in _navigationDeliveries.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                await DeliverIframeNavigationAsync(notification, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            while (_navigationDeliveries.Reader.TryRead(out _)) { }
        }
    }

    private async ValueTask DeliverIframeNavigationAsync(NavigationDelivery notification, CancellationToken cancellationToken)
    {
        var document = notification.Document;
        CancellationTokenSource delivery;
        lock (_navigationGate)
        {
            if (_navigationDocument != document || cancellationToken.IsCancellationRequested
                || Volatile.Read(ref _disposed) != 0 || !Volatile.Read(ref _hostCapabilities).Iframes) return;
            delivery = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, document.Cancellation.Token);
        }
        using (delivery)
        {
            var delivered = false;
            void Deliver()
            {
                Action<Uri>? callback;
                // This action is also the UI-dispatch boundary: even a dispatcher
                // that retains its action after cancellation cannot deliver stale work.
                lock (_navigationGate)
                {
                    if (delivered || delivery.IsCancellationRequested || _navigationDocument != document
                        || Volatile.Read(ref _disposed) != 0 || _shutdown.IsCancellationRequested
                        || !Volatile.Read(ref _hostCapabilities).Iframes) return;
                    delivered = true;
                    callback = _iframeNavigationRequested;
                }
                // Claiming the generation above starts an in-flight invocation.
                // No arbitrary user code runs under the retirement lock.
                try { callback?.Invoke(notification.Uri); }
                catch (Exception error) when (error is not OutOfMemoryException) { RecordNavigationFailure(error); }
            }
            try
            {
                delivery.Token.ThrowIfCancellationRequested();
                if (_iframeNavigationDispatcher is { } dispatch)
                    await dispatch(Deliver, delivery.Token).AsTask().WaitAsync(delivery.Token).ConfigureAwait(false);
                else Deliver();
            }
            catch (OperationCanceledException) when (delivery.IsCancellationRequested) { }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // Match in-process iframe notification semantics: observer failures
                // must not break resource brokerage or terminate the document.
                RecordNavigationFailure(error);
            }
        }
    }

    private void RecordNavigationFailure(Exception error)
    {
        lock (_standardError)
        {
            _standardError.AppendLine($"Iframe navigation callback threw {error.GetType().Name}.");
            if (_standardError.Length > 8192) _standardError.Remove(0, _standardError.Length - 8192);
        }
    }

    private async ValueTask HandleMediaCaptureAsync(ContentMessage message, CancellationToken cancellationToken)
    {
        JsonObject result = new() { ["ok"] = false };
        string? openedToken = null;
        ContentCaptureLease? openedLease = null;
        ContentCaptureSample? initial = null;
        try
        {
            if (message.Payload.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
            var action = message.Payload.TryGetProperty("action", out var actionValue) && actionValue.ValueKind == JsonValueKind.String ? actionValue.GetString() : null;
            if (action == "open" && _mediaCaptureProvider is not null
                && message.Payload.TryGetProperty("audio", out var audio) && audio.ValueKind is JsonValueKind.True or JsonValueKind.False
                && message.Payload.TryGetProperty("video", out var video) && video.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                var request = new MediaCaptureRequest(audio.GetBoolean(), video.GetBoolean());
                var generation = Volatile.Read(ref _captureGeneration);
                if (_mediaCaptureSessions.Count >= MaxCaptureSessions || !CaptureGranted(request)
                    || (_mediaCapturePermission is { } permit && !await permit(request, cancellationToken).ConfigureAwait(false))
                    || generation != Volatile.Read(ref _captureGeneration) || !CaptureGranted(request))
                    throw new InvalidOperationException("The host denied camera or microphone capture.");
                var session = _mediaCaptureProvider.Open(request);
                if (session is not null)
                {
                    var newToken = Guid.NewGuid().ToString("N");
                    var newLease = new ContentCaptureLease(session, request, CaptureGranted, SignalCapture);
                    _mediaCaptureSessions[newToken] = newLease;
                    openedToken = newToken; openedLease = newLease;
                    initial = newLease.Initial();
                    if (generation != Volatile.Read(ref _captureGeneration) || !CaptureGranted(request) || newLease.Revoked || Volatile.Read(ref _disposed) != 0
                        || !_mediaCaptureSessions.TryGetValue(newToken, out var current) || !ReferenceEquals(current, newLease))
                        throw new InvalidOperationException("Capture permission changed while the device was opening.");
                    result = CaptureWire.Read(initial, newToken);
                }
            }
            else if (message.Payload.TryGetProperty("token", out var tokenValue) && tokenValue.ValueKind == JsonValueKind.String
                && tokenValue.GetString() is { Length: 32 } token && _mediaCaptureSessions.TryGetValue(token, out var lease))
            {
                var generation = Volatile.Read(ref _captureGeneration);
                if (action == "current")
                {
                    var sample = lease.Published();
                    if (generation == Volatile.Read(ref _captureGeneration) && !lease.Revoked && CaptureGranted(lease.ActiveRequest)
                        && _mediaCaptureSessions.TryGetValue(token, out var current) && ReferenceEquals(current, lease))
                        result = CaptureWire.Read(sample, token);
                }
                else if (action is "stopAudio" or "stopVideo")
                {
                    var stopAudio = action == "stopAudio";
                    if (CaptureGranted(new(stopAudio, !stopAudio)) && lease.Stop(stopAudio)
                        && generation == Volatile.Read(ref _captureGeneration)
                        && _mediaCaptureSessions.TryGetValue(token, out var current) && ReferenceEquals(current, lease))
                        result["ok"] = true;
                    if (lease.ActiveRequest is { Audio: false, Video: false }) RetireCapture(token);
                }
                else if (action == "close") { RetireCapture(token); result["ok"] = true; }
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (openedToken is not null) RetireCapture(openedToken);
            openedLease = null;
            result = new JsonObject { ["ok"] = false, ["error"] = "The host refused or could not open the capture session." };
        }
        try
        {
            await _session.SendAsync(ContentMessageKind.MediaCaptureResponse, new { requestId = message.Sequence, result }, cancellationToken).ConfigureAwait(false);
            if (openedLease is not null && initial is not null) openedLease.Activate(initial);
        }
        catch { if (openedToken is not null) RetireCapture(openedToken); throw; }
    }

    private void SignalCapture() => _captureWake.Writer.TryWrite(true);

    private async Task PumpCaptureAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _captureWake.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                _captureWake.Reader.TryRead(out _);
                foreach (var pair in _mediaCaptureSessions)
                {
                    var lease = pair.Value;
                    if (lease.Revoked)
                    {
                        await SendCaptureRevocationsAsync(cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    var sample = lease.Take();
                    if (sample.Audio is null && sample.Video is null) continue;
                    if (!_mediaCaptureSessions.TryGetValue(pair.Key, out var current) || !ReferenceEquals(current, lease)) continue;
                    var active = lease.ActiveRequest;
                    sample = sample with
                    {
                        Audio = active.Audio && CaptureGranted(new(true, false)) ? sample.Audio : null,
                        Video = active.Video && CaptureGranted(new(false, true)) ? sample.Video : null
                    };
                    if (sample.Audio is null && sample.Video is null) continue;
                    await SendCaptureActivityAsync(pair.Key, CaptureWire.Read(sample, pair.Key), cancellationToken).ConfigureAwait(false);
                    lease.Publish(sample);
                    SignalCapture();
                }
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException or ObjectDisposedException)
        {
            if (!cancellationToken.IsCancellationRequested) _supervisor.Stop("capture transport failed");
        }
    }

    // Retires every revoked lease and tells the child, holding the gate across the
    // write so callers that flush before their own message observe the notice first.
    private async ValueTask SendCaptureRevocationsAsync(CancellationToken rootShutdown)
    {
        await _captureRevocationGate.WaitAsync(rootShutdown).ConfigureAwait(false);
        try
        {
            foreach (var pair in _mediaCaptureSessions)
            {
                if (!pair.Value.Revoked || !_mediaCaptureSessions.TryGetValue(pair.Key, out var current) || !ReferenceEquals(current, pair.Value)) continue;
                RetireCapture(pair.Key);
                await SendCaptureActivityAsync(pair.Key, new JsonObject { ["revoked"] = true }, rootShutdown).ConfigureAwait(false);
            }
        }
        finally { _captureRevocationGate.Release(); }
    }

    private async ValueTask SendCaptureActivityAsync(string token, JsonObject sample, CancellationToken rootShutdown)
    {
        sample["captureToken"] = token;
        // Only root shutdown can cancel an in-flight protocol frame. Track stop
        // clears queued work; it never cancels the shared stream's write token.
        await _session.SendAsync(ContentMessageKind.MediaCaptureActivity, sample, rootShutdown).ConfigureAwait(false);
    }

    private bool CaptureGranted(MediaCaptureRequest request)
    {
        var capabilities = Volatile.Read(ref _hostCapabilities);
        return Volatile.Read(ref _disposed) == 0 && !_shutdown.IsCancellationRequested
            && (request.Audio || request.Video) && (!request.Audio || capabilities.Microphone) && (!request.Video || capabilities.Camera);
    }

    private void RetireCapture(string token)
    {
        if (_mediaCaptureSessions.TryRemove(token, out var lease))
            try { lease.Dispose(); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                lock (_standardError)
                {
                    _standardError.AppendLine($"Capture session cleanup threw {error.GetType().Name}.");
                    if (_standardError.Length > 8192) _standardError.Remove(0, _standardError.Length - 8192);
                }
            }
    }

    private void TrackHostCapabilities(ContentMessageKind kind, JsonObject payload)
    {
        var authority = new JsonObject();
        foreach (var entry in payload)
            if (entry.Key.Equals("capabilities", StringComparison.OrdinalIgnoreCase)) authority[entry.Key] = entry.Value?.DeepClone();
        var message = new ContentMessage(kind, 0, ScriptJson.SerializeToElement(authority));
        if (!message.TryGetCapabilities(out var capabilities) && kind == ContentMessageKind.Load
            && !payload.ContainsKey("html")
            && !payload.Any(entry => entry.Key.Equals("capabilities", StringComparison.OrdinalIgnoreCase))) return;
        var previous = Interlocked.Exchange(ref _hostCapabilities, capabilities);
        Interlocked.Increment(ref _captureGeneration);
        foreach (var pair in _mediaCaptureSessions)
        {
            try
            {
                if (!capabilities.Microphone && pair.Value.Request.Audio) pair.Value.Stop(true);
                if (!capabilities.Camera && pair.Value.Request.Video) pair.Value.Stop(false);
                if (!CaptureGranted(pair.Value.ActiveRequest)) RetireCapture(pair.Key);
            }
            catch (Exception error) when (error is not OutOfMemoryException) { RetireCapture(pair.Key); }
        }
        if (previous.Sound && !capabilities.Sound) _session.StopMediaOutput();
    }

    private static class CaptureWire
    {
        public static JsonObject Read(ContentCaptureSample sample, string token)
        {
            var result = new JsonObject { ["ok"] = true, ["token"] = token };
            if (sample.Video is { } video)
                result["video"] = new JsonObject { ["width"] = video.Width, ["height"] = video.Height,
                    ["rgba"] = Convert.ToBase64String(video.Rgba.Span), ["captureSequence"] = video.CaptureSequence,
                    ["sourceSequence"] = sample.VideoSource, ["timestamp"] = sample.VideoTimestamp, ["timestampFrequency"] = Stopwatch.Frequency };
            if (sample.Audio is { } audio)
                result["audio"] = new JsonObject { ["sampleRate"] = audio.SampleRate, ["channels"] = audio.Channels,
                    ["samples"] = ScriptJson.SerializeToNode(audio.Samples.ToArray()), ["captureSequence"] = audio.CaptureSequence,
                    ["sourceSequence"] = sample.AudioSource, ["timestamp"] = sample.AudioTimestamp, ["timestampFrequency"] = Stopwatch.Frequency };
            return result;
        }
    }

    public async ValueTask SendAsync(ContentMessageKind kind, object payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (kind == ContentMessageKind.GpuRestore) throw new ArgumentException("GPU worker channels are host-owned.", nameof(kind));
        // A provider revocation that happened before this call must reach the child first.
        await SendCaptureRevocationsAsync(_shutdown.Token).ConfigureAwait(false);
        if (kind == ContentMessageKind.Load)
        {
            var profile = ScriptJson.SerializeToNode(payload) as JsonObject ?? throw new ArgumentException("Load payload must be an object.", nameof(payload));
            await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                if (profile.ContainsKey("html"))
                {
                    RetireNavigationDocument();
                    lock (_navigationGate) _navigationDocument = new NavigationDocument();
                    ReleaseMediaCaptureSessions();
                    if (Interlocked.Exchange(ref _hasLoadedDocument, 1) != 0) _session.StopMediaOutput();
                }
                TrackHostCapabilities(kind, profile);
                AttachGpuChannel(profile);
                profile["artifactId"] = _artifact;
                profile["storageEnabled"] = _storageEnabled;
                profile["limits"] = JsonSerializer.SerializeToNode(_documentLimits, ContentProtocolJsonContext.Default.ResourceLimits);
                // Caller-supplied policy is never authority, including on incremental loads.
                lock (_navigationGate)
                    profile["hostPolicy"] = new JsonObject
                    {
                        ["generation"] = _navigationDocument?.Generation,
                        ["iframeNavigationEnabled"] = _iframeNavigationRequested is not null,
                        ["webRtcAllowedOrigins"] = new JsonArray(_webRtcAllowedOrigins.Select(origin => (JsonNode?)JsonValue.Create(origin)).ToArray())
                    };
                var url = profile["url"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
                _session.SetStorageDocumentUri(Uri.TryCreate(url, UriKind.Absolute, out var baseUri) ? baseUri : null);
                try { await _session.SendAsync(kind, profile, cancellationToken).ConfigureAwait(false); }
                catch { RetireNavigationDocument(); throw; }
                return;
            }
            finally { _loadGate.Release(); }
        }
        else if (kind == ContentMessageKind.Capabilities)
        {
            var profile = ScriptJson.SerializeToNode(payload) as JsonObject ?? throw new ArgumentException("Capabilities payload must be an object.", nameof(payload));
            TrackHostCapabilities(kind, profile);
        }
        await _session.SendAsync(kind, payload, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Requests a capability update for the already loaded artifact without sending another load.</summary>
    /// <remarks>Completion means the request was written to the transport. The content host's applied response is delivered through <see cref="MessageReceived"/>.</remarks>
    public ValueTask UpdateCapabilitiesAsync(FeatureCapabilities capabilities, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return SendAsync(ContentMessageKind.Capabilities, new { capabilities }, cancellationToken);
    }

    public Task Completion => _reader;

    /// <summary>Changes the physical viewport without reloading the document. Each revision receives its own bounded, host-owned frame file.</summary>
    public async ValueTask UpdateViewportAsync(int width, int height, double deviceScale = 1, CancellationToken cancellationToken = default)
    {
        var requested = new ContentViewport(width, height, deviceScale);
        if (!requested.IsValid) throw new ArgumentOutOfRangeException(nameof(width), "Invalid viewport dimensions or device scale.");
        await _viewportGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = Volatile.Read(ref _viewport);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var framePath = Path.Combine(Path.GetDirectoryName(FramePath)!, $"frame-{Guid.NewGuid():N}.bin");
            using (var initialized = new CanvasFrameStore(framePath, width, height, width * 4)) { }
            _framePaths.TryAdd(framePath, checked(previous.Revision + 1));
            if (OperatingSystem.IsWindows() && _security is AppContainerSecurity security) security.GrantFrame(framePath);
            requested = requested with { Revision = checked(previous.Revision + 1), FramePath = framePath };
            Volatile.Write(ref _frameStorePath, framePath);
            Volatile.Write(ref _viewport, requested);
            try { await _session.SendAsync(ContentMessageKind.Viewport, requested, cancellationToken).ConfigureAwait(false); }
            catch { _supervisor.Stop("viewport update failed"); throw; }
        }
        finally { _viewportGate.Release(); }
    }

    private async Task ReadMessagesAsync()
    {
        try { await _session.RunAsync(_shutdown.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        {
            _supervisor.Stop("content process protocol failed");
        }
        finally
        {
            if (!_shutdown.IsCancellationRequested)
                _supervisor.Stop("content process connection closed");
        }
    }

    private async Task WatchdogAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_process.HasExited)
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested || _process.HasExited) break;
                await _session.SendAsync(ContentMessageKind.Heartbeat, new { }, cancellationToken).ConfigureAwait(false);
                if (!_supervisor.CheckHeartbeat()) break;
                long workingSet;
                try { workingSet = _process.WorkingSet64; }
                catch (InvalidOperationException) when (cancellationToken.IsCancellationRequested || _process.HasExited) { break; }
                if (!_supervisor.CheckMemory(workingSet)) break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException) when (cancellationToken.IsCancellationRequested || _process.HasExited) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested || _process.HasExited) { }
        catch (InvalidOperationException) when (cancellationToken.IsCancellationRequested || _process.HasExited) { }
    }

    private async Task DrainErrorAsync(CancellationToken cancellationToken)
    {
        var buffer = new char[2048];
        using var reader = new StreamReader(_error, System.Text.Encoding.UTF8, leaveOpen: true);
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                if (read == 0) break;
                lock (_standardError)
                {
                    _standardError.Append(buffer, 0, read);
                    if (_standardError.Length > 8192) _standardError.Remove(0, _standardError.Length - 8192);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException) when (cancellationToken.IsCancellationRequested || _process.HasExited) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested || _process.HasExited) { }
    }

    // The channel field is host-owned: a caller-supplied value is always dropped. The worker is
    // started for the first load that grants WebGL and handed to the child exactly once.
    private void AttachGpuChannel(JsonObject profile)
    {
        profile.Remove("gpuChannel");
        if (!OperatingSystem.IsWindows() || _gpuChannelSent || _contentHostPath is null) return;
        if (!(GpuWorkerOverride ?? (Environment.GetEnvironmentVariable("FOLIO_GPU_WORKER") != "0"))) return;
        if (!Volatile.Read(ref _hostCapabilities).WebGl) return;
        try
        {
            _gpuWorker ??= Folio.Scripting.Gpu.Remote.GpuWebGlWorker.Start(_contentHostPath, _documentLimits.MaxGpuBytes);
            // The child maps both shared sections; they come out of its memory budget before it can use them.
            if (!_gpuMemoryReserved) { _job?.ReserveMemory(Folio.Scripting.Gpu.Remote.GpuWebGlWorker.SectionBytes); _gpuMemoryReserved = true; }
            profile["gpuChannel"] = _gpuWorker.HandOff(_process.SafeHandle);
            _gpuChannelSent = true;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            // Without a worker the document keeps its in-process WebGL path.
            StopGpuWorker();
        }
    }

    // The child lost its channel and asks for a new one. The old worker is retired whatever its state,
    // so a child can only ever trade its worker for a fresh one, at most MaxGpuRestores times.
    private async Task RestoreGpuChannelAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _disposed) != 0 || !_gpuChannelSent || _contentHostPath is null) return;
                StopGpuWorker();
                if (_gpuRestores >= MaxGpuRestores || !Volatile.Read(ref _hostCapabilities).WebGl) return;
                _gpuRestores++;
                JsonObject channel;
                try
                {
                    _gpuWorker = Folio.Scripting.Gpu.Remote.GpuWebGlWorker.Start(_contentHostPath, _documentLimits.MaxGpuBytes);
                    channel = _gpuWorker.HandOff(_process.SafeHandle);
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                {
                    StopGpuWorker(); // The page's WebGL stays lost.
                    return;
                }
                if (Volatile.Read(ref _disposed) != 0) { StopGpuWorker(); return; } // Disposed while the worker started.
                await _session.SendAsync(ContentMessageKind.GpuRestore, new JsonObject { ["gpuChannel"] = channel }, cancellationToken).ConfigureAwait(false);
            }
            finally { _loadGate.Release(); }
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or IOException) { }
    }

    private void StopGpuWorker()
    {
        if (OperatingSystem.IsWindows()) Interlocked.Exchange(ref _gpuWorker, null)?.Dispose();
    }

    private void OnStopped(string reason)
    {
        RetireNavigationDocument();
        StopGpuWorker();
        _navigationDeliveries.Writer.TryComplete();
        // A crashed or externally terminated content process can no longer send
        // the close request for its capture streams. Release host devices at the
        // containment boundary, before notifying observers, so a process death
        // never leaves a camera or microphone session live until the caller
        // happens to dispose the client.
        ReleaseMediaCaptureSessions();
        _session.StopMediaOutput();
        try
        {
            if (!_process.HasExited) { _job?.Terminate(); _process.Kill(entireProcessTree: true); }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        _shutdown.Cancel();
        // An observer must not prevent containment or notification of other observers.
        if (ScriptStopped is { } observers)
            foreach (EventHandler<ContentProcessStoppedEventArgs> observer in observers.GetInvocationList())
                try { observer(this, new ContentProcessStoppedEventArgs(reason)); }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    lock (_standardError)
                    {
                        _standardError.AppendLine($"Stopped observer threw {error.GetType().Name}.");
                        if (_standardError.Length > 8192) _standardError.Remove(0, _standardError.Length - 8192);
                    }
                }
    }

    private void ReleaseMediaCaptureSessions()
    {
        Interlocked.Increment(ref _captureGeneration);
        foreach (var pair in _mediaCaptureSessions)
            RetireCapture(pair.Key);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        RetireNavigationDocument();
        _navigationDeliveries.Writer.TryComplete();
        StopGpuWorker();
        Exception? failure = null;
        // A failed host callback can fault the reader. Preserve that error, but
        // always retire the process and every resource owned by this connection.
        try { _supervisor.Stop("content process client disposed"); }
        catch (Exception error) { failure ??= error; }
        try { _shutdown.Cancel(); }
        catch (Exception error) { failure ??= error; }
        try
        {
            _job?.Terminate();
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception error) { failure ??= error; }
        await DrainAsync(_reader).ConfigureAwait(false);
        await DrainAsync(_watchdog).ConfigureAwait(false);
        await DrainAsync(_capturePump).ConfigureAwait(false);
        await DrainAsync(_stderrReader).ConfigureAwait(false);
        // Do not join _navigationWorker here: disposal may be running inside
        // its user callback (directly or on a dispatched UI thread). Shutdown
        // and queue completion retire it when that in-flight callback returns.
        try { await _session.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { failure ??= error; }
        ReleaseMediaCaptureSessions();
        _process.Exited -= OnProcessExited;
        await _viewportGate.WaitAsync().ConfigureAwait(false);
        Release(_job);
        Release(_input);
        Release(_error);
        Release(_security);
        Release(_process);
        foreach (var path in _framePaths.Keys) CleanupFrame(path);
        _viewportGate.Release();
        Release(_shutdown);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();

        async Task DrainAsync(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
            catch (Exception error) { failure ??= error; }
        }
        void Release(IDisposable? resource)
        {
            try { resource?.Dispose(); }
            catch (Exception error) { failure ??= error; }
        }
    }

    private static void CleanupFrame(string path)
    {
        try { File.Delete(path); Directory.Delete(Path.GetDirectoryName(path)!); } catch { }
    }

    private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
}
