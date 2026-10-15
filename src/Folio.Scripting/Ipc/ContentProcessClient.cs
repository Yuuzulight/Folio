using System.Diagnostics;

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

    private ContentProcessClient(Process process, ContentProcessSupervisor supervisor, WindowsJobObject? job)
    {
        _process = process;
        _supervisor = supervisor;
        _job = job;
        _session = new ContentHostSession(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, supervisor);
        _reader = _session.RunAsync(_shutdown.Token).AsTask();
        _watchdog = WatchdogAsync(_shutdown.Token);
        _supervisor.Stopped += OnStopped;
    }

    public Process Process => _process;
    public ContentProcessSupervisor Supervisor => _supervisor;
    public event EventHandler<ContentProcessStoppedEventArgs>? ScriptStopped;
    public event Func<ContentMessage, CancellationToken, ValueTask>? MessageReceived
    {
        add => _session.MessageReceived += value;
        remove => _session.MessageReceived -= value;
    }

    public static ContentProcessClient Start(string executablePath, string? arguments = null, ContentProcessLimits? limits = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) throw new ArgumentException("An executable path is required.", nameof(executablePath));
        var startInfo = new ProcessStartInfo(executablePath, arguments ?? "")
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
        var client = new ContentProcessClient(process, new ContentProcessSupervisor(effectiveLimits), job);
        process.Exited += (_, _) => client._supervisor.Stop("content process exited");
        return client;
    }

    public ValueTask SendAsync(ContentMessageKind kind, object payload, CancellationToken cancellationToken = default) =>
        _session.SendAsync(kind, payload, cancellationToken);

    public Task Completion => _reader;

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
        try { await _reader.ConfigureAwait(false); } catch (OperationCanceledException) { }
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
