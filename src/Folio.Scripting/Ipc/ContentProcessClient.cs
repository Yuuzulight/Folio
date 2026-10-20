using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Folio.Resources;

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
    private readonly string _artifact;
    private readonly ResourceLimits _documentLimits;
    private readonly bool _storageEnabled;

    private ContentProcessClient(Process process, ContentProcessSupervisor supervisor, WindowsJobObject? job, IResourceLoader? resourceLoader, ContentResourcePolicy? resourcePolicy, IArtifactStorage? storage, string artifact, ResourceLimits documentLimits)
    {
        _process = process;
        _supervisor = supervisor;
        _job = job;
        _artifact = artifact; _documentLimits = documentLimits; _storageEnabled = storage is not null;
        _session = new ContentHostSession(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, supervisor, resourcePolicy, resourceLoader);
        if (storage is not null) _session.ConfigureStorage(storage, artifact, documentLimits.MaxServiceWorkerCacheBytes);
        _supervisor.Stopped += OnStopped;
        _reader = ReadMessagesAsync();
        _watchdog = WatchdogAsync(_shutdown.Token);
    }

    public Process Process => _process;
    public ContentProcessSupervisor Supervisor => _supervisor;
    public event EventHandler<ContentProcessStoppedEventArgs>? ScriptStopped;
    public event Func<ContentMessage, CancellationToken, ValueTask>? MessageReceived
    {
        add => _session.MessageReceived += value;
        remove => _session.MessageReceived -= value;
    }

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
    public static ContentProcessClient Start(string executablePath, string? arguments = null, ContentProcessLimits? limits = null, IResourceLoader? resourceLoader = null, ContentResourcePolicy? resourcePolicy = null, IArtifactStorage? artifactStorage = null, string artifactId = "default", ResourceLimits? documentLimits = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) throw new ArgumentException("An executable path is required.", nameof(executablePath));
        var (fileName, launchArguments) = Path.GetExtension(executablePath).Equals(".dll", StringComparison.OrdinalIgnoreCase)
            ? ("dotnet", $"{Quote(executablePath)}{(string.IsNullOrWhiteSpace(arguments) ? "" : $" {arguments}")}")
            : (executablePath, arguments ?? "");
        var startInfo = new ProcessStartInfo(fileName, launchArguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start the content process.");
        var effectiveLimits = limits ?? new ContentProcessLimits();
        WindowsJobObject? job = null;
        try { job = WindowsJobObject.TryAttach(process, effectiveLimits.MemoryBytes); }
        catch { process.Kill(entireProcessTree: true); process.Dispose(); throw; }
        var client = new ContentProcessClient(process, new ContentProcessSupervisor(effectiveLimits), job, resourceLoader, resourcePolicy, artifactStorage, string.IsNullOrWhiteSpace(artifactId) ? "default" : artifactId, documentLimits ?? ResourceLimits.Default);
        process.Exited += (_, _) => client._supervisor.Stop("content process exited");
        return client;
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

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
                await _session.SendAsync(ContentMessageKind.Heartbeat, new { }, cancellationToken).ConfigureAwait(false);
                if (!_supervisor.CheckHeartbeat()) break;
                if (!_supervisor.CheckMemory(_process.WorkingSet64)) break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException) when (cancellationToken.IsCancellationRequested || _process.HasExited) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested || _process.HasExited) { }
    }

    private void OnStopped(string reason)
    {
        ScriptStopped?.Invoke(this, new ContentProcessStoppedEventArgs(reason));
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        _shutdown.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        _supervisor.Stop("content process client disposed");
        _shutdown.Cancel();
        try { await _reader.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (InvalidDataException) { }
        catch (IOException) { }
        try { await _watchdog.ConfigureAwait(false); } catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        await _session.DisposeAsync().ConfigureAwait(false);
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync().ConfigureAwait(false);
        _process.Dispose();
        _job?.Dispose();
        _shutdown.Dispose();
    }
}
