using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Folio.Scripting.Ipc;

/// <summary>Windows process containment used by the content host. It is a no-op on non-Windows hosts.</summary>
internal sealed class WindowsJobObject : IDisposable
{
    private readonly SafeFileHandle _handle;

    private WindowsJobObject(SafeFileHandle handle) => _handle = handle;

    public static WindowsJobObject? TryAttach(Process process, long memoryBytes)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed.");
        try
        {
            var limits = new ExtendedLimitInformation
            {
                BasicLimitInformation = new BasicLimitInformation
                {
                    LimitFlags = LimitKillOnJobClose | LimitJobMemory
                },
                JobMemoryLimit = (UIntPtr)Math.Max(1, memoryBytes)
            };
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<ExtendedLimitInformation>()))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject failed.");
            if (!AssignProcessToJobObject(handle, process.Handle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject failed.");
            return new WindowsJobObject(handle);
        }
        catch
        {
            handle.Dispose();
            // A process may already belong to a parent job (for example under CI). In that case
            // Windows refuses a second assignment; the supervisor still enforces the portable limits.
            return null;
        }
    }

    public void Dispose() => _handle.Dispose();

    private const uint LimitKillOnJobClose = 0x00002000;
    private const uint LimitJobMemory = 0x00000200;
    private const int JobObjectExtendedLimitInformation = 9;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimitInformation info, int length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
}
