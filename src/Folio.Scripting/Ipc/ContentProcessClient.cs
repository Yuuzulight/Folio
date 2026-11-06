using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private readonly bool _storageEnabled;
    private readonly IMediaCaptureProvider? _mediaCaptureProvider;
    private readonly ConcurrentDictionary<string, CaptureLease> _mediaCaptureSessions = new();
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
    /// <remarks>Permission runs on the client's reader thread. A UI host can supply an override that marshals to its UI thread.</remarks>
    public static ContentProcessClient StartWithOptions(string executablePath, FolioOptions options, ContentProcessLimits? limits = null,
        Func<MediaCaptureRequest, CancellationToken, ValueTask<bool>>? mediaCapturePermission = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var client = Start(executablePath, limits: limits, resourceLoader: options.ResourceLoader,
            artifactStorage: options.ArtifactStorage, artifactId: options.ArtifactId, documentLimits: options.Limits,
            mediaDecoder: options.MediaDecoder, mediaAudioOutput: options.MediaAudioOutput, mediaCaptureProvider: options.MediaCaptureProvider);
        client._mediaCapturePermission = mediaCapturePermission ?? (options.MediaCapturePermission is { } permission
            ? (request, _) => ValueTask.FromResult(permission(request)) : null);
        return client;
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
            isolated = null;
            return client;
        }
        catch { isolated?.Dispose(); frameStore.Dispose(); CleanupFrame(framePath); throw; }
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
    private void OnProcessExited(object? sender, EventArgs args) => _supervisor.Stop("content process exited");
    private async ValueTask ValidateAndDispatchAsync(ContentMessage message, CancellationToken cancellationToken)
    {
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

    private async ValueTask HandleMediaCaptureAsync(ContentMessage message, CancellationToken cancellationToken)
    {
        JsonObject result = new() { ["ok"] = false };
        string? openedToken = null;
        try
        {
            var action = message.Payload.TryGetProperty("action", out var actionValue) ? actionValue.GetString() : null;
            if (action == "open" && _mediaCaptureProvider is not null
                && message.Payload.TryGetProperty("audio", out var audio) && audio.ValueKind is JsonValueKind.True or JsonValueKind.False
                && message.Payload.TryGetProperty("video", out var video) && video.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                var request = new MediaCaptureRequest(audio.GetBoolean(), video.GetBoolean());
                var generation = Volatile.Read(ref _captureGeneration);
                if (!CaptureGranted(request) || (_mediaCapturePermission is { } permit && !await permit(request, cancellationToken).ConfigureAwait(false))
                    || generation != Volatile.Read(ref _captureGeneration) || !CaptureGranted(request))
                    throw new InvalidOperationException("The host denied camera or microphone capture.");
                var session = _mediaCaptureProvider.Open(request);
                if (session is not null)
                {
                    var token = Guid.NewGuid().ToString("N");
                    var lease = new CaptureLease(session, request,
                        frame => SendCaptureActivity(token, new JsonObject { ["video"] = new JsonObject { ["width"] = frame.Width, ["height"] = frame.Height, ["rgba"] = Convert.ToBase64String(frame.Rgba.ToArray()) } }),
                        sample => SendCaptureActivity(token, new JsonObject { ["audio"] = new JsonObject { ["sampleRate"] = sample.SampleRate, ["channels"] = sample.Channels, ["samples"] = ScriptJson.SerializeToNode(sample.Samples.ToArray()) } }),
                        () => { SendCaptureActivity(token, new JsonObject { ["revoked"] = true }); RetireCapture(token); });
                    _mediaCaptureSessions[token] = lease;
                    openedToken = token;
                    if (generation != Volatile.Read(ref _captureGeneration) || !CaptureGranted(request) || lease.Revoked || Volatile.Read(ref _disposed) != 0)
                    {
                        RetireCapture(token);
                        throw new InvalidOperationException("Capture permission changed while the device was opening.");
                    }
                    result = CaptureWire.Read(lease.Session, token);
                }
            }
            else if (action == "current" && message.Payload.TryGetProperty("token", out var tokenValue)
                && _mediaCaptureSessions.TryGetValue(tokenValue.GetString() ?? "", out var current))
                result = CaptureGranted(current.Request) ? CaptureWire.Read(current.Session, tokenValue.GetString()!) : new JsonObject { ["ok"] = false };
            else if (action == "close" && message.Payload.TryGetProperty("token", out var closeToken)
                && _mediaCaptureSessions.TryRemove(closeToken.GetString() ?? "", out var retired))
            {
                retired.Dispose();
                result["ok"] = true;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (openedToken is not null) RetireCapture(openedToken);
            result = new JsonObject { ["ok"] = false, ["error"] = "The host refused or could not open the capture session." };
        }
        await _session.SendAsync(ContentMessageKind.MediaCaptureResponse, new { requestId = message.Sequence, result }, cancellationToken).ConfigureAwait(false);
    }

    private async void SendCaptureActivity(string token, JsonObject sample)
    {
        sample["captureToken"] = token;
        try { await _session.SendAsync(ContentMessageKind.MediaCaptureActivity, sample).ConfigureAwait(false); }
        catch (InvalidOperationException) { }
        catch (IOException) { }
        catch (OperationCanceledException) { }
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

    private sealed class CaptureLease : IDisposable
    {
        internal IMediaCaptureSession Session { get; }
        internal MediaCaptureRequest Request { get; }
        internal bool Revoked => Volatile.Read(ref _revoked) != 0;
        private readonly Action<MediaCaptureVideoFrame> _video;
        private readonly Action<MediaAudioFrame> _audio;
        private readonly Action _revokedHandler;
        private int _revoked, _disposed;

        internal CaptureLease(IMediaCaptureSession session, MediaCaptureRequest request,
            Action<MediaCaptureVideoFrame> video, Action<MediaAudioFrame> audio, Action revoked)
        {
            Session = session; Request = request; _video = video; _audio = audio;
            _revokedHandler = () => { Volatile.Write(ref _revoked, 1); revoked(); };
            try
            {
                session.VideoFrameAvailable += _video;
                session.AudioFrameAvailable += _audio;
                session.Revoked += _revokedHandler;
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { Session.VideoFrameAvailable -= _video; }
            finally
            {
                try { Session.AudioFrameAvailable -= _audio; }
                finally
                {
                    try { Session.Revoked -= _revokedHandler; }
                    finally { Session.Dispose(); }
                }
            }
        }
    }

    private void TrackHostCapabilities(ContentMessageKind kind, JsonObject payload)
    {
        var authority = new JsonObject();
        foreach (var entry in payload)
            if (entry.Key.Equals("capabilities", StringComparison.OrdinalIgnoreCase)) authority[entry.Key] = entry.Value?.DeepClone();
        var message = new ContentMessage(kind, 0, ScriptJson.SerializeToElement(authority));
        if (!message.TryGetCapabilities(out var capabilities)) return;
        var previous = Interlocked.Exchange(ref _hostCapabilities, capabilities);
        Interlocked.Increment(ref _captureGeneration);
        foreach (var pair in _mediaCaptureSessions)
            if (!CaptureGranted(pair.Value.Request)) RetireCapture(pair.Key);
        if (previous.Sound && !capabilities.Sound) _session.StopMediaOutput();
    }

    private static class CaptureWire
    {
        public static JsonObject Read(IMediaCaptureSession session, string token)
        {
            var result = new JsonObject { ["ok"] = true, ["token"] = token };
            if (session.CurrentVideoFrame is { } video && video.IsValid)
                result["video"] = new JsonObject { ["width"] = video.Width, ["height"] = video.Height, ["rgba"] = Convert.ToBase64String(video.Rgba.ToArray()) };
            if (session.CurrentAudioFrame is { } audio)
                result["audio"] = new JsonObject { ["sampleRate"] = audio.SampleRate, ["channels"] = audio.Channels, ["samples"] = ScriptJson.SerializeToNode(audio.Samples.ToArray()) };
            return result;
        }
    }

    public ValueTask SendAsync(ContentMessageKind kind, object payload, CancellationToken cancellationToken = default)
    {
        if (kind == ContentMessageKind.Load)
        {
            var profile = ScriptJson.SerializeToNode(payload) as JsonObject ?? throw new ArgumentException("Load payload must be an object.", nameof(payload));
            if (profile.ContainsKey("html"))
            {
                ReleaseMediaCaptureSessions();
                if (Interlocked.Exchange(ref _hasLoadedDocument, 1) != 0) _session.StopMediaOutput();
            }
            TrackHostCapabilities(kind, profile);
            profile["artifactId"] = _artifact;
            profile["storageEnabled"] = _storageEnabled;
            profile["limits"] = JsonSerializer.SerializeToNode(_documentLimits, ContentProtocolJsonContext.Default.ResourceLimits);
            var url = profile["url"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
            _session.SetStorageDocumentUri(Uri.TryCreate(url, UriKind.Absolute, out var baseUri) ? baseUri : null);
            payload = profile;
        }
        else if (kind == ContentMessageKind.Capabilities)
        {
            var profile = ScriptJson.SerializeToNode(payload) as JsonObject ?? throw new ArgumentException("Capabilities payload must be an object.", nameof(payload));
            TrackHostCapabilities(kind, profile);
        }
        return _session.SendAsync(kind, payload, cancellationToken);
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

    private void OnStopped(string reason)
    {
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
        await DrainAsync(_stderrReader).ConfigureAwait(false);
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
