using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Folio.Scripting.Ipc;

internal sealed class WindowsContentLaunch : IDisposable
{
    internal required Process Process { get; init; }
    internal required Stream Input { get; init; }
    internal required Stream Output { get; init; }
    internal required Stream Error { get; init; }
    internal WindowsJobObject? Job { get; init; }
    internal required IDisposable Security { get; init; }
    public void Dispose()
    {
        try
        {
            Job?.Terminate();
            if (!Process.HasExited) Process.Kill(entireProcessTree: true);
            Process.WaitForExit(5000);
        }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
        finally
        {
            TryDispose(Input, nameof(Input)); TryDispose(Output, nameof(Output)); TryDispose(Error, nameof(Error));
            TryDispose(Job, nameof(Job)); TryDispose(Security, nameof(Security)); TryDispose(Process, nameof(Process));
        }
    }
    private static void TryDispose(IDisposable? value, string name)
    {
        if (value is null) return;
        try { value.Dispose(); }
        catch (Exception error) when (error is not OutOfMemoryException) { Trace.TraceWarning($"Content launch cleanup failed for {name}: {error.Message}"); }
    }
}

/// <summary>Creates the Windows content process in an AppContainer and applies hard job limits before resume.</summary>
internal static class WindowsContentLauncher
{
    internal static WindowsContentLaunch Start(string executablePath, string? arguments, ContentProcessLimits limits, string framePath)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var executable = ResolveExecutable(executablePath);
        var isAssembly = Path.GetExtension(executable).Equals(".dll", StringComparison.OrdinalIgnoreCase);
        var isDotnet = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        var managedAsset = isAssembly ? executable : isDotnet ? ResolveAssetFromArguments(arguments) : null;
        if (managedAsset is not null && File.Exists(Path.ChangeExtension(managedAsset, ".exe")))
        {
            // The bundled Windows apphost has no console subsystem. LowBox console
            // startup otherwise requires changing the user's global console policy.
            executable = Path.ChangeExtension(managedAsset, ".exe");
            if (isDotnet)
                arguments = RemoveFirstArgument(arguments);
        }
        var fileName = Path.GetExtension(executable).Equals(".dll", StringComparison.OrdinalIgnoreCase) ? ResolveDotnet() : executable;
        var runtimeRoot = TrustedRuntimeRoot();
        var assetPath = managedAsset ?? executable;
        var commandLine = Path.GetExtension(executable).Equals(".dll", StringComparison.OrdinalIgnoreCase)
            ? Quote(fileName) + " " + Quote(executable) + (string.IsNullOrWhiteSpace(arguments) ? "" : " " + arguments)
            : Quote(executable) + (string.IsNullOrWhiteSpace(arguments) ? "" : " " + arguments);
        var security = AppContainerSecurity.Create(assetPath, Path.Combine(runtimeRoot, "dotnet.exe"), framePath);
        SafeFileHandle childInput = null!, parentInput = null!, parentOutput = null!, childOutput = null!, parentError = null!, childError = null!;
        try
        {
            CreatePipe(out childInput, out parentInput);
            CreatePipe(out parentOutput, out childOutput);
            CreatePipe(out parentError, out childError);
            SetInherit(parentInput, false); SetInherit(parentOutput, false); SetInherit(parentError, false);
            var handles = new[] { childInput.DangerousGetHandle(), childOutput.DangerousGetHandle(), childError.DangerousGetHandle() };
            var attrs = ProcAttributes.Create(security.AppContainerSid, handles);
            try
            {
                var startup = new StartupInfoEx { StartupInfo = new StartupInfo { Cb = Marshal.SizeOf<StartupInfoEx>(), Flags = UseStdHandles, StdInput = childInput.DangerousGetHandle(), StdOutput = childOutput.DangerousGetHandle(), StdError = childError.DangerousGetHandle() }, Attributes = attrs.Pointer };
                var command = new System.Text.StringBuilder(commandLine);
                    var profilePath = security.GetPrivateProfilePath();
                    var pathValue = Path.GetDirectoryName(fileName) + ";" + Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? "C:\\Windows", "System32");
                    var env = EnvironmentBlock.Create(("FOLIO_CONTENT_FRAME_PATH", framePath), ("TEMP", profilePath), ("TMP", profilePath), ("PATH", pathValue),
                        ("USERPROFILE", profilePath), ("LOCALAPPDATA", profilePath), ("APPDATA", profilePath),
                        ("DOTNET_ROOT", runtimeRoot), ("DOTNET_ROOT_" + RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant(), runtimeRoot),
                        ("COMPlus_EnableDiagnostics", "0"), ("DOTNET_EnableDiagnostics", "0"), ("DOTNET_EnableDiagnostics_IPC", "0"),
                        ("DOTNET_EnableDiagnostics_Debugger", "0"), ("DOTNET_EnableDiagnostics_Profiler", "0"));
                try
                {
                    if (!CreateProcess(fileName, command, IntPtr.Zero, IntPtr.Zero, true, ExtendedStartup | Suspended | UnicodeEnvironment | NoWindow,
                        env.Pointer, Path.GetDirectoryName(assetPath), ref startup, out var processInfo))
                    {
                        var error = Marshal.GetLastWin32Error();
                        throw new Win32Exception(error, $"CreateProcess AppContainer failed ({error}): {new Win32Exception(error).Message}");
                    }
                    using var processHandle = new SafeProcessHandle(processInfo.Process, ownsHandle: true);
                    Process? process = null;
                    WindowsJobObject? job = null;
                    var threadClosed = false;
                    try
                    {
                        process = Process.GetProcessById((int)processInfo.ProcessId);
                        job = WindowsJobObject.Attach(process, limits);
                        if (ResumeThread(processInfo.Thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error(), "ResumeThread failed.");
                        CloseHandle(processInfo.Thread);
                        threadClosed = true;
                        childInput.Dispose(); childOutput.Dispose(); childError.Dispose();
                        FileStream? input = null, output = null, error = null;
                        try
                        {
                            input = new FileStream(parentInput, FileAccess.Write); parentInput = null!;
                            output = new FileStream(parentOutput, FileAccess.Read); parentOutput = null!;
                            error = new FileStream(parentError, FileAccess.Read); parentError = null!;
                            return new WindowsContentLaunch { Process = process, Input = input, Output = output, Error = error, Job = job, Security = security.Detach() };
                        }
                        catch { input?.Dispose(); output?.Dispose(); error?.Dispose(); throw; }
                    }
                    catch
                    {
                        try { job?.Terminate(); } catch (Exception error) when (error is not OutOfMemoryException) { Trace.TraceWarning($"Content launch job termination failed: {error.Message}"); }
                        try { job?.Dispose(); } catch (Exception error) when (error is not OutOfMemoryException) { Trace.TraceWarning($"Content launch job cleanup failed: {error.Message}"); }
                        if (process is not null)
                        {
                            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception error) when (error is not OutOfMemoryException) { Trace.TraceWarning($"Content launch process termination failed: {error.Message}"); }
                            try { process.WaitForExit(5000); } catch (Exception error) when (error is not OutOfMemoryException) { Trace.TraceWarning($"Content launch process wait failed: {error.Message}"); }
                            try { process.Dispose(); } catch (Exception error) when (error is not OutOfMemoryException) { Trace.TraceWarning($"Content launch process cleanup failed: {error.Message}"); }
                        }
                        else { try { TerminateProcess(processInfo.Process, 0xC0000409); WaitForSingleObject(processHandle, 5000); } catch (Exception error) when (error is not OutOfMemoryException) { Trace.TraceWarning($"Content launch process termination/wait failed: {error.Message}"); } }
                        if (!threadClosed) CloseHandle(processInfo.Thread); throw;
                    }
                }
                finally { env.Dispose(); }
            }
            finally { attrs.Dispose(); }
        }
        catch
        {
            TryClose(childInput, nameof(childInput)); TryClose(childOutput, nameof(childOutput)); TryClose(childError, nameof(childError));
            TryClose(parentInput, nameof(parentInput)); TryClose(parentOutput, nameof(parentOutput)); TryClose(parentError, nameof(parentError));
            try { security.Dispose(); } catch (Exception error) when (error is not OutOfMemoryException) { Trace.TraceWarning($"Content launch security cleanup failed: {error.Message}"); }
            throw;
        }
    }
    private static void TryClose(IDisposable? value, string name)
    {
        if (value is null) return;
        try { value.Dispose(); }
        catch (Exception error) when (error is not OutOfMemoryException) { Trace.TraceWarning($"Content launch handle cleanup failed for {name}: {error.Message}"); }
    }

    private static string ResolveDotnet()
    {
        var host = Path.Combine(TrustedRuntimeRoot(), "dotnet.exe");
        return File.Exists(host) ? host : throw new FileNotFoundException("A trusted dotnet host is required for a content-host assembly.");
    }
    private static string TrustedRuntimeRoot()
    {
        var framework = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var sharedRoot = framework.Parent?.Parent?.Parent?.FullName;
        return sharedRoot is not null && File.Exists(Path.Combine(sharedRoot, "dotnet.exe"))
            ? sharedRoot : framework.FullName;
    }
    private static string ResolveExecutable(string value)
    {
        if (Path.IsPathRooted(value) || value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar)) return Path.GetFullPath(value);
        var candidate = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator).Select(p => Path.Combine(p, value)).SelectMany(p => new[] { p, p + ".exe" }).FirstOrDefault(File.Exists);
        return candidate is null ? throw new FileNotFoundException("Content executable was not found on PATH.", value) : Path.GetFullPath(candidate);
    }
    private static string? ResolveAssetFromArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return null;
        var token = arguments.Trim();
        if (token[0] == '"') { var end = token.IndexOf('"', 1); if (end > 1) token = token[1..end]; }
        else { var end = token.IndexOf(' '); if (end > 0) token = token[..end]; }
        return Path.GetExtension(token).Equals(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(token) ? Path.GetFullPath(token) : null;
    }
    private static string? RemoveFirstArgument(string? arguments)
    {
        var text = arguments?.TrimStart();
        if (string.IsNullOrEmpty(text)) return null;
        var end = text[0] == '"' ? text.IndexOf('"', 1) : text.IndexOf(' ');
        return end < 0 ? null : text[(end + 1)..].TrimStart();
    }
    private static string Quote(string path) => "\"" + path.Replace("\"", "\\\"") + "\"";
    private static SafeFileHandle CreatePipe(out SafeFileHandle child, out SafeFileHandle parent)
    {
        var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = 1 };
        if (!CreatePipe(out var read, out var write, ref security, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe failed.");
        child = read; parent = write; return parent;
    }
    private static void SetInherit(SafeFileHandle handle, bool inherit) { if (!SetHandleInformation(handle, HandleFlagInherit, inherit ? HandleFlagInherit : 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "SetHandleInformation failed."); }

    private const uint ExtendedStartup = 0x00080000, Suspended = 0x00000004, UnicodeEnvironment = 0x00000400, NoWindow = 0x08000000, UseStdHandles = 0x100;
    private const uint HandleFlagInherit = 1;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string? application, System.Text.StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInformation info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfo { public int Cb; public string? Reserved, Desktop, Title; public int X, Y, XSize, YSize, XCountChars, YCountChars; public int Fill; public uint Flags; public short Show; public short Reserved2; public IntPtr Reserved2Ptr, StdInput, StdOutput, StdError; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr Attributes; }
    private sealed class SafeProcessHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeProcessHandle(IntPtr handle, bool ownsHandle) : base(ownsHandle) => SetHandle(handle);
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class AppContainerSecurity : IDisposable
{
    private readonly string _name;
    private readonly List<string> _aclPaths;
    private int _disposed;
    internal IntPtr AppContainerSid { get; }
    internal SecurityIdentifier Sid { get; }
    private AppContainerSecurity(string name, IntPtr sid, SecurityIdentifier securityIdentifier, List<string> aclPaths) { _name = name; AppContainerSid = sid; Sid = securityIdentifier; _aclPaths = aclPaths; }

    internal static AppContainerSecurity Create(string executable, string runtime, string framePath)
    {
        var name = "Folio.Content." + Guid.NewGuid().ToString("N");
        IntPtr sid = IntPtr.Zero;
        var hr = CreateAppContainerProfile(name, "Folio content", "Folio content process", IntPtr.Zero, 0, out sid);
        // A random identity must be newly owned by this launch; never adopt or
        // remove somebody else's existing profile on an unexpected collision.
        if (hr != 0) throw new Win32Exception(hr, $"CreateAppContainerProfile failed (HRESULT 0x{hr:X8}).");
        SecurityIdentifier? sidObject = null;
        var paths = new List<string>();
        try
        {
            if (sid == IntPtr.Zero) throw new InvalidOperationException("AppContainer profile has no security identity.");
            var sidLength = GetLengthSid(sid);
            if (sidLength <= 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "GetLengthSid failed.");
            var binary = new byte[sidLength];
            Marshal.Copy(sid, binary, 0, sidLength);
            sidObject = new SecurityIdentifier(binary, 0);
            if (!HasAppPackagesRead(Path.GetDirectoryName(executable)!))
                Grant(sidObject, Path.GetDirectoryName(executable)!, paths);
            var runtimeDirectory = Path.GetDirectoryName(runtime)!;
            if (!string.Equals(runtimeDirectory, Path.GetDirectoryName(executable), StringComparison.OrdinalIgnoreCase) && !HasAppPackagesRead(runtimeDirectory)) Grant(sidObject, runtimeDirectory, paths);
            GrantTraverse(sidObject, Path.GetDirectoryName(framePath)!, paths);
            GrantFile(sidObject, framePath, paths);
            SetLowIntegrityLabel(framePath);
            return new AppContainerSecurity(name, sid, sidObject, paths);
        }
        catch
        {
            if (sidObject is not null) foreach (var path in paths) TryRemove(sidObject, path);
            var deleteResult = DeleteAppContainerProfile(name);
            if (deleteResult != 0) Trace.TraceWarning($"Content AppContainer profile cleanup failed (HRESULT 0x{deleteResult:X8}).");
            if (sid != IntPtr.Zero) FreeSid(sid);
            throw;
        }
    }

    internal IDisposable Detach() => this;
    internal string GetPrivateProfilePath()
    {
        if (GetAppContainerFolderPath(Sid.Value, out var pointer) != 0 || pointer == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot locate the AppContainer private profile.");
        try { var path = Marshal.PtrToStringUni(pointer) ?? throw new InvalidOperationException("AppContainer profile path is empty."); Directory.CreateDirectory(path); return path; }
        finally { CoTaskMemFree(pointer); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var path in _aclPaths) TryRemove(Sid, path);
        if (AppContainerSid != IntPtr.Zero) FreeSid(AppContainerSid);
        var deleteResult = DeleteAppContainerProfile(_name);
        if (deleteResult != 0) Trace.TraceWarning($"Content AppContainer profile cleanup failed (HRESULT 0x{deleteResult:X8}).");
    }
    private static void Grant(SecurityIdentifier sid, string path, List<string> paths)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        WithAclLock(() => { var info = new DirectoryInfo(path); var security = info.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(security); paths.Add(path); });
    }
    private static bool HasAppPackagesRead(string path)
    {
        try
        {
            var packages = new SecurityIdentifier("S-1-15-2-1");
            return new DirectoryInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Any(rule => rule.IdentityReference.Equals(packages) && rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute);
        }
        catch { return false; }
    }
    private static void GrantFile(SecurityIdentifier sid, string path, List<string> paths)
    {
        WithAclLock(() => { var info = new FileInfo(path); var security = info.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(sid, (FileSystemRights.Read | FileSystemRights.Write), AccessControlType.Allow)); info.SetAccessControl(security); paths.Add(path); });
    }
    private static void GrantTraverse(SecurityIdentifier sid, string path, List<string> paths)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        WithAclLock(() => { var info = new DirectoryInfo(path); var security = info.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Traverse, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(security); paths.Add(path); });
    }
    private static void TryRemove(SecurityIdentifier sid, string path)
    {
        try
        {
            WithAclLock(() => { if (Directory.Exists(path)) { var info = new DirectoryInfo(path); var security = info.GetAccessControl(); security.RemoveAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow)); security.RemoveAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Traverse, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow)); info.SetAccessControl(security); }
                else if (File.Exists(path)) { var info = new FileInfo(path); var security = info.GetAccessControl(); security.RemoveAccessRule(new FileSystemAccessRule(sid, (FileSystemRights.Read | FileSystemRights.Write), AccessControlType.Allow)); info.SetAccessControl(security); } });
        }
        catch (Exception error) { System.Diagnostics.Trace.TraceWarning($"Folio content ACL cleanup failed for '{path}': {error.Message}"); }
    }

    private static readonly object AclGate = new();
    private static void WithAclLock(Action action)
    {
        lock (AclGate)
        {
            // Multiple users and host processes can grant different per-document SIDs
            // on the same deployment directory. Preserve each other's ACEs.
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor("D:(A;;0x00100001;;;WD)", 1, out var descriptor, out _)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create ACL mutex security descriptor.");
            try
            {
                var attributes = new NativeSecurityAttributes { Length = Marshal.SizeOf<NativeSecurityAttributes>(), Descriptor = descriptor };
                var attributesPointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeSecurityAttributes>());
                try
                {
                    Marshal.StructureToPtr(attributes, attributesPointer, false);
                    using var mutex = CreateMutexEx(attributesPointer, @"Global\Folio.Content.Acl", 0, MutexModifyState | Synchronize);
                    if (mutex.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create ACL mutex.");
                    var owned = false;
                    try
                    {
                        var wait = WaitForSingleObject(mutex, 30_000);
                        owned = wait is WaitObject0 or WaitAbandoned;
                        if (wait == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot wait for ACL mutex.");
                        if (!owned) throw new TimeoutException("Timed out updating content-process asset permissions.");
                        action();
                    }
                    finally { if (owned) ReleaseMutex(mutex); }
                }
                finally { Marshal.FreeHGlobal(attributesPointer); }
            }
            finally { LocalFree(descriptor); }
        }
    }
    private static void SetLowIntegrityLabel(string path)
    {
        WithAclLock(() => SetLowIntegrityLabelCore(path));
    }
    private static void SetLowIntegrityLabelCore(string path)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor("S:(ML;;NW;;;LW)", 1, out var descriptor, out _)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create frame integrity label.");
        try
        {
            if (!GetSecurityDescriptorSacl(descriptor, out var present, out var sacl, out _) || !present || sacl == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot obtain frame integrity label.");
            var result = SetNamedSecurityInfo(path, 1, 0x10, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, sacl); if (result != 0) throw new Win32Exception(result, "Cannot set frame integrity label.");
        }
        finally { LocalFree(descriptor); }
    }

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int CreateAppContainerProfile(string name, string displayName, string description, IntPtr capabilities, uint count, out IntPtr sid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int DeleteAppContainerProfile(string name);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int GetAppContainerFolderPath(string sid, out IntPtr path);
    [DllImport("advapi32.dll")] private static extern IntPtr FreeSid(IntPtr sid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern int GetLengthSid(IntPtr sid);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetSecurityDescriptorSacl(IntPtr descriptor, out bool present, out IntPtr sacl, out bool defaulted);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int SetNamedSecurityInfo(string name, int objectType, uint securityInfo, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeWaitHandle CreateMutexEx(IntPtr attributes, string name, uint flags, uint desiredAccess);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReleaseMutex(SafeWaitHandle handle);
    [StructLayout(LayoutKind.Sequential)] private struct NativeSecurityAttributes { public int Length; public IntPtr Descriptor; public int InheritHandle; }
    private const uint MutexModifyState = 0x0001, Synchronize = 0x00100000, WaitObject0 = 0, WaitAbandoned = 0x00000080;
    [DllImport("ole32.dll")] private static extern void CoTaskMemFree(IntPtr pointer);
    [StructLayout(LayoutKind.Sequential)] private struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }
}

internal sealed class ProcAttributes : IDisposable
{
    internal IntPtr Pointer { get; private set; }
    internal static ProcAttributes Create(IntPtr sid, IntPtr[] handles)
    {
        nuint size = 0; InitializeProcThreadAttributeList(IntPtr.Zero, 3, 0, ref size);
        var memory = Marshal.AllocHGlobal(checked((int)size));
        var initialized = false;
        IntPtr securityPtr = IntPtr.Zero, handlePtr = IntPtr.Zero, childPolicy = IntPtr.Zero;
        try
        {
            if (!InitializeProcThreadAttributeList(memory, 3, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed.");
            initialized = true;
            var security = new SecurityCapabilities { AppContainerSid = sid };
            securityPtr = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityCapabilities>()); Marshal.StructureToPtr(security, securityPtr, false);
            childPolicy = Marshal.AllocHGlobal(sizeof(uint)); Marshal.WriteInt32(childPolicy, 1); handlePtr = Marshal.AllocHGlobal(IntPtr.Size * handles.Length); for (var i = 0; i < handles.Length; i++) Marshal.WriteIntPtr(handlePtr, i * IntPtr.Size, handles[i]);
            if (!UpdateProcThreadAttribute(memory, 0, SecurityCapabilitiesAttribute, securityPtr, (nuint)Marshal.SizeOf<SecurityCapabilities>(), IntPtr.Zero, IntPtr.Zero)
                || !UpdateProcThreadAttribute(memory, 0, ChildProcessPolicyAttribute, childPolicy, sizeof(uint), IntPtr.Zero, IntPtr.Zero)
                || !UpdateProcThreadAttribute(memory, 0, HandleListAttribute, handlePtr, (nuint)(IntPtr.Size * handles.Length), IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed.");
            return new ProcAttributes(memory, securityPtr, handlePtr, childPolicy);
        }
        catch { if (initialized) DeleteProcThreadAttributeList(memory); if (childPolicy != IntPtr.Zero) Marshal.FreeHGlobal(childPolicy); if (securityPtr != IntPtr.Zero) Marshal.FreeHGlobal(securityPtr); if (handlePtr != IntPtr.Zero) Marshal.FreeHGlobal(handlePtr); Marshal.FreeHGlobal(memory); throw; }
    }
    private readonly IntPtr _security, _handles, _childPolicy;
    private ProcAttributes(IntPtr pointer, IntPtr security, IntPtr handles, IntPtr childPolicy) { Pointer = pointer; _security = security; _handles = handles; _childPolicy = childPolicy; }
    public void Dispose() { if (Pointer == IntPtr.Zero) return; DeleteProcThreadAttributeList(Pointer); Marshal.FreeHGlobal(Pointer); Marshal.FreeHGlobal(_security); Marshal.FreeHGlobal(_handles); Marshal.FreeHGlobal(_childPolicy); Pointer = IntPtr.Zero; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnSize);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, ref uint value, nuint size, IntPtr previous, IntPtr returnSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    private static readonly IntPtr SecurityCapabilitiesAttribute = (IntPtr)0x00020009, HandleListAttribute = (IntPtr)0x00020002, ChildProcessPolicyAttribute = (IntPtr)0x0002000E;
    [StructLayout(LayoutKind.Sequential)] private struct SecurityCapabilities { public IntPtr AppContainerSid, Capabilities; public uint CapabilityCount, Reserved; }
}

internal sealed class EnvironmentBlock : IDisposable
{
    internal IntPtr Pointer { get; }
    private EnvironmentBlock(IntPtr pointer) => Pointer = pointer;
    internal static EnvironmentBlock Create(params (string Key, string Value)[] extras)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "SystemRoot", "WINDIR", "ProgramFiles", "ProgramFiles(x86)", "CommonProgramFiles", "CommonProgramFiles(x86)" })
            if (Environment.GetEnvironmentVariable(key) is { } value) values[key] = value;
        foreach (var extra in extras) values[extra.Key] = extra.Value;
        var block = string.Join("\0", values.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => x.Key + "=" + x.Value)) + "\0\0"; return new EnvironmentBlock(Marshal.StringToHGlobalUni(block));
    }
    public void Dispose() => Marshal.FreeHGlobal(Pointer);
}
