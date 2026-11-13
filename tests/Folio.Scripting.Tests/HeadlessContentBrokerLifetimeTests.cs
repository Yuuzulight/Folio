using System.Diagnostics;
using System.Text.Json;

namespace Folio.Scripting.Tests;

public sealed class HeadlessContentBrokerLifetimeTests
{
    [Fact]
    public async Task BrokerExitsWhenParentExitsWhileWaitingForInput()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var parent = StartParentSleeper();
        using var broker = StartBroker(parent.Id);

        await Task.Delay(300, TestContext.Current.CancellationToken);
        parent.Kill();
        await parent.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await broker.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(broker.ExitCode == 0, await broker.StandardError.ReadToEndAsync());
    }

    [Fact]
    public async Task BrokerDisposesSandboxWorkerWhenParentExitsDuringSession()
    {
        if (!OperatingSystem.IsWindows()) return;
        var host = Path.ChangeExtension(ContentHostTestPaths.Resolve(), ".exe");
        Assert.True(File.Exists(host), $"Content host executable was not built: {host}");
        var processesBefore = GetHostProcessIds(host);
        using var parent = StartParentSleeper();
        using var broker = StartBroker(parent.Id);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        await broker.StandardInput.WriteLineAsync("{\"id\":1,\"op\":\"hello\"}");
        await broker.StandardInput.FlushAsync();
        var response = await broker.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        using (var document = JsonDocument.Parse(response!))
        {
            Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
            Assert.True(document.RootElement.GetProperty("sandboxed").GetBoolean());
        }

        var workerIds = GetHostProcessIds(host).Except(processesBefore).Where(pid => pid != broker.Id).ToArray();
        Assert.NotEmpty(workerIds);
        parent.Kill();
        await parent.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await broker.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(0, broker.ExitCode);
        await WaitUntilAsync(() => workerIds.All(pid => !IsRunning(pid)), TimeSpan.FromSeconds(10));
        Assert.All(workerIds, pid => Assert.False(IsRunning(pid)));
    }

    private static Process StartParentSleeper()
    {
        var start = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 120 > nul")
        { UseShellExecute = false, CreateNoWindow = true };
        return Process.Start(start) ?? throw new InvalidOperationException("Unable to start parent process fixture.");
    }

    private static Process StartBroker(int parentPid)
    {
        var host = Path.ChangeExtension(ContentHostTestPaths.Resolve(), ".exe");
        if (!File.Exists(host)) throw new FileNotFoundException("Content host executable was not built.", host);
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("--broker");
        start.ArgumentList.Add($"--arcana-parent-pid={parentPid}");
        return Process.Start(start) ?? throw new InvalidOperationException("Unable to start Folio headless broker.");
    }

    private static HashSet<int> GetHostProcessIds(string hostPath)
    {
        var fullPath = Path.GetFullPath(hostPath);
        var result = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(hostPath)))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(Path.GetFullPath(process.MainModule?.FileName ?? ""), fullPath, StringComparison.OrdinalIgnoreCase))
                        result.Add(process.Id);
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { }
            }
        }
        return result;
    }

    private static bool IsRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Sandbox worker did not exit after its broker closed.");
            await Task.Delay(100);
        }
    }
}
