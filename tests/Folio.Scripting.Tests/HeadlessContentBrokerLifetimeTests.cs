using System.Diagnostics;
using System.Runtime.InteropServices;
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
        try
        {
            await Task.Delay(300, TestContext.Current.CancellationToken);
            parent.Kill(entireProcessTree: true);
            await parent.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await broker.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(broker.ExitCode == 0, await broker.StandardError.ReadToEndAsync());
        }
        finally { KillTreeIfRunning(parent, broker); }
    }

    [Fact]
    public async Task BrokerDisposesSandboxWorkerWhenParentExitsDuringSession()
    {
        if (!OperatingSystem.IsWindows()) return;
        var host = Path.ChangeExtension(ContentHostTestPaths.Resolve(), ".exe");
        Assert.True(File.Exists(host), $"Content host executable was not built: {host}");
        using var parent = StartParentSleeper();
        using var broker = StartBroker(parent.Id);
        try
        {
            await Task.Delay(300, TestContext.Current.CancellationToken);
            await broker.StandardInput.WriteLineAsync("{\"id\":1,\"op\":\"hello\"}".AsMemory(), TestContext.Current.CancellationToken);
            await broker.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
            var response = await broker.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            using (var document = JsonDocument.Parse(response!))
            {
                Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
                Assert.True(document.RootElement.GetProperty("sandboxed").GetBoolean());
            }

            var workerIds = GetDirectHostChildren(host, broker.Id);
            Assert.NotEmpty(workerIds);
            parent.Kill(entireProcessTree: true);
            await parent.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await broker.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(broker.ExitCode == 0, await broker.StandardError.ReadToEndAsync());
            await WaitUntilAsync(() => workerIds.All(pid => !IsRunning(pid)), TimeSpan.FromSeconds(10));
            Assert.All(workerIds, pid => Assert.False(IsRunning(pid)));
        }
        finally { KillTreeIfRunning(parent, broker); }
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

    private static int[] GetDirectHostChildren(string hostPath, int parentPid)
    {
        var result = new List<int>();
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) throw new InvalidOperationException("Unable to inspect the broker process tree.");
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Process32First(snapshot, ref entry)) throw new InvalidOperationException("Unable to inspect the broker process tree.");
            do
            {
                if (entry.ParentProcessId == (uint)parentPid && entry.ExecutableName.Equals(Path.GetFileName(hostPath), StringComparison.OrdinalIgnoreCase))
                    result.Add((int)entry.ProcessId);
            }
            while (Process32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        return result.ToArray();
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
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    private static void KillTreeIfRunning(params Process[] processes)
    {
        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint ThreadCount;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExecutableName;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
