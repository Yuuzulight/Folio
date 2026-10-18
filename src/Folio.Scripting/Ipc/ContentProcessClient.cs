using System.Diagnostics;
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

    private ContentProcessClient(Process process, ContentProcessSupervisor supervisor, WindowsJobObject? job, IResourceLoader? resourceLoader, ContentResourcePolicy? resourcePolicy)
    {
        _process = process;
        _supervisor = supervisor;
        _job = job;
        _session = new ContentHostSession(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, supervisor, resourcePolicy, resourceLoader);
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

    /// <param name="executablePath">The content-host executable or assembly.</param>
    /// <param name="arguments">Additional launch arguments.</param>
    /// <param name="limits">Process memory and heartbeat limits.</param>
    /// <param name="resourceLoader">The trusted host loader, which decides what resources are allowed. Without one, brokered resources are refused.</param>
    /// <param name="resourcePolicy">An optional additional restriction applied before calling the loader.</param>
    public static ContentProcessClient Start(string executablePath, string? arguments = null, ContentProcessLimits? limits = null, IResourceLoader? resourceLoader = null, ContentResourcePolicy? resourcePolicy = null)
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
        var client = new ContentProcessClient(process, new ContentProcessSupervisor(effectiveLimits), job, resourceLoader, resourcePolicy);
        process.Exited += (_, _) => client._supervisor.Stop("content process exited");
        return client;
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    public ValueTask SendAsync(ContentMessageKind kind, object payload, CancellationToken cancellationToken = default) =>
        _session.SendAsync(kind, payload, cancellationToken);

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
