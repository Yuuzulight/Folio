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
    private readonly ConcurrentDictionary<string, IMediaCaptureSession> _mediaCaptureSessions = new();
    private readonly Stream _input;
    private readonly Stream _error;
    private readonly CanvasFrameStore _frameStore;
    private readonly IDisposable _security;
    private event Func<ContentMessage, CancellationToken, ValueTask>? _messageReceived;

    private ContentProcessClient(Process process, Stream input, Stream output, Stream error, CanvasFrameStore frameStore, string framePath, ContentProcessSupervisor supervisor, WindowsJobObject? job, IDisposable security, IResourceLoader? resourceLoader, ContentResourcePolicy? resourcePolicy, IArtifactStorage? storage, IMediaDecoder? mediaDecoder, IMediaAudioOutput? mediaAudioOutput, IMediaCaptureProvider? mediaCaptureProvider, string artifact, ResourceLimits documentLimits)
    {
        _process = process;
        _supervisor = supervisor;
        _job = job;
        _input = input; _error = error; _frameStore = frameStore; _security = security; _frameStorePath = framePath;
        _artifact = artifact; _documentLimits = documentLimits; _storageEnabled = storage is not null;
        _mediaCaptureProvider = mediaCaptureProvider;
        _session = new ContentHostSession(output, input, supervisor, resourcePolicy, resourceLoader, mediaDecoder, mediaAudioOutput);
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
    private readonly string _frameStorePath;

    /// <summary>Starts a content process without a resource loader.</summary>
    public static ContentProcessClient Start(string executablePath, string? arguments, ContentProcessLimits? limits) =>
        Start(executablePath, arguments, limits, null, null);

    /// <summary>Starts a content process with the original resource-broker API.</summary>
    public static ContentProcessClient Start(string executablePath, string? arguments, ContentProcessLimits? limits, IResourceLoader? resourceLoader, ContentResourcePolicy? resourcePolicy) =>
        Start(executablePath, arguments, limits, resourceLoader, resourcePolicy, null, "default", null);

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
                if (!string.Equals(path, FramePath, StringComparison.Ordinal) || (width, height, stride) != (_frameStore.Width, _frameStore.Height, _frameStore.Stride)) throw new InvalidDataException();
            }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or FormatException or KeyNotFoundException or JsonException) { _supervisor.Stop("invalid content frame descriptor"); return; }
        }
        if (_messageReceived is { } handler) await handler(message, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask HandleMediaCaptureAsync(ContentMessage message, CancellationToken cancellationToken)
    {
        JsonObject result = new() { ["ok"] = false };
        try
        {
            var action = message.Payload.TryGetProperty("action", out var actionValue) ? actionValue.GetString() : null;
            if (action == "open" && _mediaCaptureProvider is not null
                && message.Payload.TryGetProperty("audio", out var audio) && audio.ValueKind is JsonValueKind.True or JsonValueKind.False
                && message.Payload.TryGetProperty("video", out var video) && video.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                var session = _mediaCaptureProvider.Open(new MediaCaptureRequest(audio.GetBoolean(), video.GetBoolean()));
                if (session is not null)
                {
                    var token = Guid.NewGuid().ToString("N");
                    _mediaCaptureSessions[token] = session;
                    session.VideoFrameAvailable += frame => SendCaptureActivity(token, new JsonObject { ["video"] = new JsonObject { ["width"] = frame.Width, ["height"] = frame.Height, ["rgba"] = Convert.ToBase64String(frame.Rgba.ToArray()) } });
                    session.AudioFrameAvailable += audio => SendCaptureActivity(token, new JsonObject { ["audio"] = new JsonObject { ["sampleRate"] = audio.SampleRate, ["channels"] = audio.Channels, ["samples"] = JsonSerializer.SerializeToNode(audio.Samples.ToArray()) } });
                    session.Revoked += () => SendCaptureActivity(token, new JsonObject { ["revoked"] = true });
                    result = CaptureWire.Read(session, token);
                }
            }
            else if (action == "current" && message.Payload.TryGetProperty("token", out var tokenValue)
                && _mediaCaptureSessions.TryGetValue(tokenValue.GetString() ?? "", out var current))
                result = CaptureWire.Read(current, tokenValue.GetString()!);
            else if (action == "close" && message.Payload.TryGetProperty("token", out var closeToken)
                && _mediaCaptureSessions.TryRemove(closeToken.GetString() ?? "", out var retired))
            {
                retired.Dispose();
                result["ok"] = true;
            }
        }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException or ArgumentException)
        {
            result = new JsonObject { ["ok"] = false, ["error"] = error.Message };
        }
        await _session.SendAsync(ContentMessageKind.MediaCaptureResponse, new { requestId = message.Sequence, result }, cancellationToken).ConfigureAwait(false);
    }

    private async void SendCaptureActivity(string token, JsonObject sample)
    {
        sample["captureToken"] = token;
        try { await _session.SendAsync(ContentMessageKind.MediaCaptureActivity, sample).ConfigureAwait(false); }
        catch (InvalidOperationException) { }
        catch (IOException) { }
    }

    private static class CaptureWire
    {
        public static JsonObject Read(IMediaCaptureSession session, string token)
        {
            var result = new JsonObject { ["ok"] = true, ["token"] = token };
            if (session.CurrentVideoFrame is { } video && video.IsValid)
                result["video"] = new JsonObject { ["width"] = video.Width, ["height"] = video.Height, ["rgba"] = Convert.ToBase64String(video.Rgba.ToArray()) };
            if (session.CurrentAudioFrame is { } audio)
                result["audio"] = new JsonObject { ["sampleRate"] = audio.SampleRate, ["channels"] = audio.Channels, ["samples"] = JsonSerializer.SerializeToNode(audio.Samples.ToArray()) };
            return result;
        }
    }

    public ValueTask SendAsync(ContentMessageKind kind, object payload, CancellationToken cancellationToken = default)
    {
        if (kind == ContentMessageKind.Load)
        {
            var profile = JsonSerializer.SerializeToNode(payload) as JsonObject ?? throw new ArgumentException("Load payload must be an object.", nameof(payload));
            profile["artifactId"] = _artifact;
            profile["storageEnabled"] = _storageEnabled;
            profile["limits"] = JsonSerializer.SerializeToNode(_documentLimits);
            var url = profile["url"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
            _session.SetStorageDocumentUri(Uri.TryCreate(url, UriKind.Absolute, out var baseUri) ? baseUri : null);
            payload = profile;
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
        foreach (var pair in _mediaCaptureSessions)
            if (_mediaCaptureSessions.TryRemove(pair.Key, out var capture))
                try { capture.Dispose(); }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    lock (_standardError)
                    {
                        _standardError.AppendLine($"Capture session cleanup threw {error.GetType().Name}.");
                        if (_standardError.Length > 8192) _standardError.Remove(0, _standardError.Length - 8192);
                    }
                }
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
        Release(_job);
        Release(_input);
        Release(_error);
        Release(_frameStore);
        Release(_security);
        Release(_process);
        CleanupFrame(FramePath);
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
