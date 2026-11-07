using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Folio.Scripting.Gpu.Remote;

/// <summary>
/// Windows handle plumbing for the GPU channel. The parent creates unnamed sections and pipes and
/// duplicates them into the DOM child and the GPU worker; neither child can open or name anything
/// itself. Handle values cross processes only as numbers the parent chose and sent.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class GpuSharedHandles
{
    internal const long MaxSectionBytes = 64L * 1024 * 1024;
    private const uint PageReadWrite = 0x04, FileMapWrite = 0x0002, FileMapRead = 0x0004;
    private const uint DuplicateSameAccess = 0x2;

    /// <summary>Creates an unnamed, page-file backed section of <paramref name="bytes"/> bytes.</summary>
    internal static SafeFileMappingHandle CreateSection(long bytes)
    {
        if (bytes <= 0 || bytes > MaxSectionBytes) throw new ArgumentOutOfRangeException(nameof(bytes));
        var handle = CreateFileMapping(new IntPtr(-1), IntPtr.Zero, PageReadWrite, (uint)(bytes >> 32), (uint)bytes, null);
        if (handle.IsInvalid) throw new IOException("Could not create the GPU shared section.", Marshal.GetHRForLastWin32Error());
        return handle;
    }

    /// <summary>Maps a read/write view of exactly <paramref name="bytes"/> bytes.</summary>
    internal static MappedGpuArena Map(SafeFileMappingHandle section, long bytes)
    {
        if (bytes <= 0 || bytes > MaxSectionBytes) throw new ArgumentOutOfRangeException(nameof(bytes));
        var view = MapViewOfFile(section, FileMapRead | FileMapWrite, 0, 0, (nuint)bytes);
        if (view.IsInvalid) throw new IOException("Could not map the GPU shared section.", Marshal.GetHRForLastWin32Error());
        view.Initialize((ulong)bytes);
        return new MappedGpuArena(view);
    }

    /// <summary>Duplicates a section into <paramref name="target"/> with only map read/write access.</summary>
    internal static long DuplicateSection(SafeFileMappingHandle section, SafeHandle target) =>
        Duplicate(section, target, FileMapRead | FileMapWrite, 0);

    /// <summary>Duplicates a pipe end into <paramref name="target"/> with its existing access only.</summary>
    internal static long DuplicatePipe(SafeFileHandle pipe, SafeHandle target) =>
        Duplicate(pipe, target, 0, DuplicateSameAccess);

    /// <summary>Creates one anonymous pipe; both ends are non-inheritable.</summary>
    internal static (SafeFileHandle Read, SafeFileHandle Write) CreatePipe(int bufferBytes = 64 * 1024)
    {
        var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>() };
        if (!CreatePipeNative(out var read, out var write, ref attributes, bufferBytes))
            throw new IOException("Could not create the GPU pipe.", Marshal.GetHRForLastWin32Error());
        return (read, write);
    }

    /// <summary>Adopts a handle value the parent duplicated into this process.</summary>
    internal static SafeFileMappingHandle AdoptSection(long value) => new(new IntPtr(value), ownsHandle: true);
    internal static SafeFileHandle AdoptPipe(long value) => new(new IntPtr(value), ownsHandle: true);

    private static long Duplicate(SafeHandle source, SafeHandle target, uint access, uint options)
    {
        if (!DuplicateHandle(GetCurrentProcess(), source, target, out var remote, access, false, options))
            throw new IOException("Could not hand the GPU channel to the child process.", Marshal.GetHRForLastWin32Error());
        return remote.ToInt64();
    }

    internal sealed class SafeFileMappingHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeFileMappingHandle() : base(true) { }
        internal SafeFileMappingHandle(IntPtr handle, bool ownsHandle) : base(ownsHandle) => SetHandle(handle);
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    internal sealed class SafeMappedView : SafeBuffer
    {
        public SafeMappedView() : base(true) { }
        protected override bool ReleaseHandle() => UnmapViewOfFile(handle);
    }

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileMappingW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileMappingHandle CreateFileMapping(IntPtr file, IntPtr attributes, uint protect, uint sizeHigh, uint sizeLow, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeMappedView MapViewOfFile(SafeFileMappingHandle section, uint access, uint offsetHigh, uint offsetLow, nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UnmapViewOfFile(IntPtr view);
    [DllImport("kernel32.dll", EntryPoint = "CreatePipe", SetLastError = true)]
    private static extern bool CreatePipeNative(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, SafeHandle source, SafeHandle targetProcess, out IntPtr target, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
