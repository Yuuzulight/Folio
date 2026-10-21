using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Folio.Scripting.Ipc;

internal sealed class WindowsJobObject : IDisposable
{
    private readonly SafeFileHandle _handle;
    private SafeFileHandle? _completionPort;
    private CancellationTokenSource? _monitorCancellation;
    private Task? _monitor;
    private WindowsJobObject(SafeFileHandle handle) => _handle = handle;

    internal static WindowsJobObject Attach(Process process, ContentProcessLimits limits)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid) throw Error("CreateJobObject failed.");
        try
        {
            var extended = new ExtendedLimitInformation
            {
                BasicLimitInformation = new BasicLimitInformation { LimitFlags = KillOnClose | JobMemory | ActiveProcess, ActiveProcessLimit = 1 },
                JobMemoryLimit = (UIntPtr)Math.Max(1, limits.MemoryBytes),
            };
            Set(handle, JobObjectExtendedLimitInformation, extended);
            var cpu = new CpuRateControlInformation { ControlFlags = CpuEnable | CpuHardCap, CpuRate = (uint)Math.Clamp(limits.CpuRatePercent, 1, 100) * 100 };
            Set(handle, JobObjectCpuRateControlInformation, cpu);
            var ui = new UiRestrictions { UiRestrictionsClass = UiHandles | UiNoClipboard | UiGlobalAtoms | UiDesktop | UiDisplaySettings | UiExitWindows | UiSystemParameters };
            Set(handle, JobObjectBasicUiRestrictions, ui);
            if (!AssignProcessToJobObject(handle, process.Handle)) throw Error("AssignProcessToJobObject failed.");
            return new WindowsJobObject(handle);
        }
        catch { handle.Dispose(); throw; }
    }

    internal void Terminate(uint exitCode = 0xC0000409) => TerminateJobObject(_handle, exitCode);
    internal void MonitorMemory(Action callback)
    {
        var port = CreateIoCompletionPort(new IntPtr(-1), IntPtr.Zero, IntPtr.Zero, 1);
        if (port.IsInvalid) throw Error("CreateIoCompletionPort failed.");
        try
        {
            var completion = new CompletionPortAssociation { CompletionKey = new UIntPtr(1), CompletionPort = port.DangerousGetHandle() };
            Set(_handle, JobObjectAssociateCompletionPortInformation, completion);
            _completionPort = port;
            _monitorCancellation = new CancellationTokenSource();
            _monitor = Task.Run(() => MonitorLoop(callback, _monitorCancellation.Token));
        }
        catch { port.Dispose(); throw; }
    }
    private void MonitorLoop(Action callback, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!GetQueuedCompletionStatus(_completionPort!, out var message, out _, out _, 250)) continue;
                if (message == JobMemoryLimitMessage || message == ProcessMemoryLimitMessage) { callback(); return; }
            }
        }
        catch (ObjectDisposedException) { }
    }
    public void Dispose()
    {
        _monitorCancellation?.Cancel(); _completionPort?.Dispose();
        try { _monitor?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _monitorCancellation?.Dispose(); _handle.Dispose();
    }
    private static void Set<T>(SafeFileHandle h, int c, T value) where T : struct
    {
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        try { Marshal.StructureToPtr(value, memory, false); if (!SetInformationJobObject(h, c, memory, Marshal.SizeOf<T>())) throw Error("SetInformationJobObject failed."); }
        finally { Marshal.FreeHGlobal(memory); }
    }
    private static Win32Exception Error(string message) => new(Marshal.GetLastWin32Error(), message);

    private const uint KillOnClose = 0x2000, JobMemory = 0x200, ActiveProcess = 0x8;
    private const uint CpuEnable = 1, CpuHardCap = 4;
    private const uint UiHandles = 1, UiNoClipboard = 6, UiSystemParameters = 8, UiDisplaySettings = 0x10, UiGlobalAtoms = 0x20, UiDesktop = 0x40, UiExitWindows = 0x80;
    private const int JobObjectExtendedLimitInformation = 9, JobObjectBasicUiRestrictions = 4, JobObjectCpuRateControlInformation = 15;
    private const int JobObjectAssociateCompletionPortInformation = 7, JobMemoryLimitMessage = 10, ProcessMemoryLimitMessage = 9;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, IntPtr info, int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateIoCompletionPort(IntPtr file, IntPtr existing, IntPtr key, uint threads);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetQueuedCompletionStatus(SafeFileHandle port, out uint message, out UIntPtr key, out UIntPtr overlapped, uint timeout);

    [StructLayout(LayoutKind.Sequential)] private struct BasicLimitInformation { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimitInformation { public BasicLimitInformation BasicLimitInformation; public IoCounters IoInfo; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct CpuRateControlInformation { public uint ControlFlags, CpuRate; }
    [StructLayout(LayoutKind.Sequential)] private struct UiRestrictions { public uint UiRestrictionsClass; }
    [StructLayout(LayoutKind.Sequential)] private struct CompletionPortAssociation { public UIntPtr CompletionKey; public IntPtr CompletionPort; }
}
