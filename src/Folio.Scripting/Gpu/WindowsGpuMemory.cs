using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Folio.Scripting.Gpu;

/// <summary>OS process usage, kept separate from Folio's resource reservations.</summary>
internal sealed record NativeGpuMemorySample(long LocalBytes, long NonLocalBytes, int Adapters)
{
    public string Source => "DXGIAdapter3 process usage, node 0";
}

internal static class WindowsGpuMemory
{
    internal static NativeGpuMemorySample? Sample()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return SampleWindows(); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or ExternalException or OverflowException)
        { return null; }
    }

    [SupportedOSPlatform("windows")]
    private static NativeGpuMemorySample? SampleWindows()
    {
        var factoryId = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        if (CreateDXGIFactory1(ref factoryId, out var factory) < 0 || factory == IntPtr.Zero) return null;
        try
        {
            // Slots verified against Windows SDK dxgi.h and dxgi1_4.h.
            var enumerate = Method<EnumAdapters>(factory, 12);
            long local = 0, nonLocal = 0;
            var measured = 0;
            for (uint index = 0; index < 128; index++)
            {
                var result = enumerate(factory, index, out var adapter);
                if (result == unchecked((int)0x887A0002))
                    return measured == 0 ? null : new(local, nonLocal, measured);
                if (result < 0 || adapter == IntPtr.Zero) return null;
                try
                {
                    var adapterId = new Guid("645967A4-1392-4310-A798-8053CE3E93FD");
                    if (Marshal.QueryInterface(adapter, in adapterId, out var adapter3) < 0 || adapter3 == IntPtr.Zero)
                        return null;
                    try
                    {
                        var query = Method<QueryVideoMemory>(adapter3, 14);
                        if (query(adapter3, 0, 0, out var localInfo) < 0 || query(adapter3, 0, 1, out var nonLocalInfo) < 0)
                            return null;
                        local = checked(local + checked((long)localInfo.CurrentUsage));
                        nonLocal = checked(nonLocal + checked((long)nonLocalInfo.CurrentUsage));
                        measured++;
                    }
                    finally { Marshal.Release(adapter3); }
                }
                finally { Marshal.Release(adapter); }
            }
            // Do not publish a silently truncated aggregate.
            return null;
        }
        finally { Marshal.Release(factory); }
    }

    private static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), checked(slot * IntPtr.Size)));

    [DllImport("dxgi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CreateDXGIFactory1(ref Guid interfaceId, out IntPtr factory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters(IntPtr factory, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryVideoMemory(IntPtr adapter, uint node, int segment, out VideoMemoryInfo information);

    [StructLayout(LayoutKind.Sequential)]
    private struct VideoMemoryInfo
    {
        internal ulong Budget, CurrentUsage, AvailableForReservation, CurrentReservation;
    }
}
