using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Folio.ContentHost;
using Folio.Scripting.Ipc;

var framePath = Environment.GetEnvironmentVariable("FOLIO_CONTENT_FRAME_PATH");
if (string.IsNullOrWhiteSpace(framePath))
    throw new InvalidOperationException("FOLIO_CONTENT_FRAME_PATH is required.");

using var frameStore = new CanvasFrameStore(framePath, 1280, 720, 1280 * 4);
await using var session = new ContentHostSession(Console.OpenStandardInput(), Console.OpenStandardOutput());
using var runtime = new ContentDocumentRuntime(session, frameStore);
session.MessageReceived += async (message, cancellationToken) =>
{
    if (message.Kind != ContentMessageKind.Load)
        return;

    var probe = message.Payload.TryGetProperty("probe", out var probeValue) ? probeValue.GetString() : null;
    try
    {
    ContentDocumentResult? runtimeResult = null;
    if (message.Payload.TryGetProperty("html", out _))
        runtimeResult = await runtime.ExecuteAsync(message, cancellationToken);
    switch (probe)
    {
        case "frame":
            PublishFrame();
            await Reply(message, new { probe, ready = true }, cancellationToken);
            break;
        case "gpu":
            var canvasId = message.Payload.TryGetProperty("canvasId", out var canvasValue)
                ? canvasValue.GetString() ?? "gpu-canvas" : "gpu-canvas";
            var red = ReadColor(message.Payload, "red", 1);
            var green = ReadColor(message.Payload, "green", 0);
            var blue = ReadColor(message.Payload, "blue", 0);
            var alpha = ReadColor(message.Payload, "alpha", 1);
            var gpu = runtime.ClearNativeCanvas(canvasId, red, green, blue, alpha);
            await Reply(message, new
            {
                probe,
                gpu = new { succeeded = gpu.Succeeded, failureMessage = gpu.FailureMessage, failureKind = gpu.FailureKind,
                    rendererIdentity = gpu.RendererIdentity, reservedBytes = gpu.ReservedBytes, peakReservedBytes = gpu.PeakReservedBytes,
                    nativeLocalBytes = gpu.NativeLocalBytes, nativeNonLocalBytes = gpu.NativeNonLocalBytes,
                    nativeAdapterCount = gpu.NativeAdapterCount, nativeMemorySource = gpu.NativeMemorySource,
                    fallbackFeature = gpu.FallbackFeature, fallbackReason = gpu.FallbackReason },
                runtime = runtimeResult is null ? null : new { value = runtimeResult.Value }
            }, cancellationToken);
            break;
        case "forge":
            await ForgeFrame(message, cancellationToken);
            break;
        case "sandbox":
            await Reply(message, await Sandbox(message.Payload, cancellationToken, runtimeResult), cancellationToken);
            break;
        case "memory":
            await ExhaustMemory(message.Payload, cancellationToken);
            break;
        case "hang":
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            break;
        default:
            await Reply(message, new { error = "unknown probe" }, cancellationToken);
            break;
    }
    }
    catch (Exception error) when (error is not OutOfMemoryException)
    {
        await session.SendAsync(ContentMessageKind.Diagnostic,
            new { sequence = message.Sequence, error = error.GetType().Name, message = error.Message }, cancellationToken);
    }
};

await session.RunAsync();

void PublishFrame()
{
    var pixels = new byte[1280 * 720 * 4];
    for (var i = 0; i < pixels.Length; i += 4)
    {
        pixels[i] = 32;
        pixels[i + 1] = 128;
        pixels[i + 2] = 224;
        pixels[i + 3] = 255;
    }
    frameStore.Publish(pixels);
}

static float ReadColor(JsonElement payload, string name, float fallback) =>
    payload.TryGetProperty(name, out var value) && value.TryGetSingle(out var result) ? result : fallback;

async ValueTask Reply(ContentMessage message, object payload, CancellationToken cancellationToken)
{
    await session.SendAsync(ContentMessageKind.Frame, new
    {
        sequence = message.Sequence,
        value = JsonSerializer.Serialize(payload),
        payload,
        canvasFrame = new { path = framePath, width = frameStore.Width, height = frameStore.Height, stride = frameStore.Stride, sequence = frameStore.Sequence }
    }, cancellationToken);
}

async ValueTask ForgeFrame(ContentMessage message, CancellationToken cancellationToken)
{
    var path = message.Payload.TryGetProperty("framePath", out var value)
        ? value.GetString()
        : null;
    await session.SendAsync(ContentMessageKind.Frame, new
    {
        sequence = message.Sequence,
        value = "forged",
        payload = new { probe = "forge" },
        canvasFrame = new { path, width = frameStore.Width, height = frameStore.Height, stride = frameStore.Stride, sequence = frameStore.Sequence }
    }, cancellationToken);
}

async Task<object> Sandbox(JsonElement payload, CancellationToken cancellationToken, ContentDocumentResult? runtimeResult)
{
    await Stage("token", cancellationToken);
    var token = ReadTokenBoundary();
    await Stage("job", cancellationToken);
    var job = ReadJobLimits();
    await Stage("file", cancellationToken);
    var environmentAbsent = true;
    if (payload.TryGetProperty("environmentKey", out var environmentKey) && environmentKey.GetString() is { } key)
        environmentAbsent = Environment.GetEnvironmentVariable(key) is null;
    var fileReadDenied = false;
    var fileWriteDenied = false;
    if (payload.TryGetProperty("filePath", out var file) && file.GetString() is { } filePath)
    {
        try { _ = File.ReadAllText(filePath); }
        catch (UnauthorizedAccessException) { fileReadDenied = true; }
        try { File.WriteAllText(filePath, "sandbox-probe"); }
        catch (UnauthorizedAccessException) { fileWriteDenied = true; }
    }

    var networkDenied = false;
    await Stage("network", cancellationToken);
    if (payload.TryGetProperty("networkPort", out var port) && port.TryGetInt32(out var networkPort))
    {
        try
        {
            using var socket = new TcpClient();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            try { await socket.ConnectAsync("127.0.0.1", networkPort, deadline.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // AppContainer loopback filtering can drop SYNs rather than return
                // WSAEACCES. The parent first proves this exact listener reachable.
                networkDenied = true;
            }
        }
        catch (SocketException error) when (error.SocketErrorCode == SocketError.AccessDenied)
        {
            networkDenied = true;
        }
    }

    var childDenied = false;
    await Stage("child", cancellationToken);
    try
    {
        using var child = Process.Start(new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? "/c exit 0" : "-c 'exit 0'",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (child is not null)
        {
            await child.WaitForExitAsync(cancellationToken);
            childDenied = false;
        }
    }
    catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode is 5 or 367)
    {
        childDenied = true;
    }

    var workerResult = runtime.Engine?.GetGlobalProperty("__folio_worker_result");
    var workerError = runtime.Engine?.GetGlobalProperty("__folio_worker_error");
    var wasmResult = runtime.Engine?.GetGlobalProperty("__folio_wasm_result");
    var wasmError = runtime.Engine?.GetGlobalProperty("__folio_wasm_error");
    var webrtcResult = runtime.Engine?.GetGlobalProperty("__folio_webrtc_result");
    var webrtcError = runtime.Engine?.GetGlobalProperty("__folio_webrtc_error");
    return new
    {
        runtimeReady = runtimeResult is not null,
        runtimeValue = runtimeResult?.Value,
        workerResult = workerResult?.IsUndefined == false ? workerResult.Value.AsString() : null,
        workerError = workerError?.IsUndefined == false ? workerError.Value.AsString() : null,
        wasmResult = wasmResult?.IsUndefined == false ? wasmResult.Value.AsString() : null,
        wasmError = wasmError?.IsUndefined == false ? wasmError.Value.AsString() : null,
        webrtcResult = webrtcResult?.IsUndefined == false ? webrtcResult.Value.AsString() : null,
        webrtcError = webrtcError?.IsUndefined == false ? webrtcError.Value.AsString() : null,
        appliedCapabilities = runtime.Capabilities,
        isAppContainer = token.IsAppContainer,
        isLowIntegrity = token.IsLowIntegrity,
        capabilityCount = token.CapabilityCount,
        environmentAbsent,
        jobMemoryBytes = job.JobMemoryBytes,
        activeProcessLimit = job.ActiveProcessLimit,
        cpuRatePercent = job.CpuRatePercent,
        uiRestrictions = job.UiRestrictions,
        fileReadDenied,
        fileWriteDenied,
        networkDenied,
        childDenied
    };
}

ValueTask Stage(string stage, CancellationToken cancellationToken) =>
    session.SendAsync(ContentMessageKind.Hello, new { stage }, cancellationToken);

static (ulong JobMemoryBytes, uint ActiveProcessLimit, uint CpuRatePercent, uint UiRestrictions) ReadJobLimits()
{
    if (!OperatingSystem.IsWindows()) return (0, 0, 0, 0);
    var extended = new JobExtendedLimitInformation();
    var extendedSize = Marshal.SizeOf<JobExtendedLimitInformation>();
    var extendedPtr = Marshal.AllocHGlobal(extendedSize);
    try
    {
        if (!NativeMethods.QueryInformationJobObject(IntPtr.Zero, 9, extendedPtr, (uint)extendedSize, out _))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        extended = Marshal.PtrToStructure<JobExtendedLimitInformation>(extendedPtr);
    }
    finally { Marshal.FreeHGlobal(extendedPtr); }
    var cpu = new JobCpuRateControlInformation();
    var cpuSize = Marshal.SizeOf<JobCpuRateControlInformation>();
    var cpuPtr = Marshal.AllocHGlobal(cpuSize);
    try
    {
        if (!NativeMethods.QueryInformationJobObject(IntPtr.Zero, 15, cpuPtr, (uint)cpuSize, out _))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        cpu = Marshal.PtrToStructure<JobCpuRateControlInformation>(cpuPtr);
    }
    finally { Marshal.FreeHGlobal(cpuPtr); }
    var ui = new JobUiRestrictions();
    var uiSize = Marshal.SizeOf<JobUiRestrictions>();
    var uiPtr = Marshal.AllocHGlobal(uiSize);
    try
    {
        if (!NativeMethods.QueryInformationJobObject(IntPtr.Zero, 4, uiPtr, (uint)uiSize, out _))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        ui = Marshal.PtrToStructure<JobUiRestrictions>(uiPtr);
    }
    finally { Marshal.FreeHGlobal(uiPtr); }
    return (extended.JobMemoryLimit.ToUInt64(), extended.Basic.ActiveProcessLimit, cpu.CpuRate / 100, ui.UiRestrictionsClass);
}

static (bool IsAppContainer, bool IsLowIntegrity, int CapabilityCount) ReadTokenBoundary()
{
    if (!OperatingSystem.IsWindows()) return (false, false, 0);
    if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), 8, out var token))
        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    using (token)
    {
        var handle = token.DangerousGetHandle();
        var appContainer = GetTokenFlag(handle, 29);
        var integritySid = GetIntegritySid(handle);
        NativeMethods.GetTokenInformation(handle, 30, IntPtr.Zero, 0, out var size);
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (!NativeMethods.GetTokenInformation(handle, 30, buffer, size, out _))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return (appContainer, string.Equals(integritySid, "S-1-16-4096", StringComparison.Ordinal), Marshal.ReadInt32(buffer));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}

static bool GetTokenFlag(IntPtr token, int informationClass)
{
    NativeMethods.GetTokenInformation(token, informationClass, IntPtr.Zero, 0, out var size);
    var buffer = Marshal.AllocHGlobal((int)size);
    try
    {
        if (!NativeMethods.GetTokenInformation(token, informationClass, buffer, size, out _))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return Marshal.ReadInt32(buffer) != 0;
    }
    finally { Marshal.FreeHGlobal(buffer); }
}

static string GetIntegritySid(IntPtr token)
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    NativeMethods.GetTokenInformation(token, 25, IntPtr.Zero, 0, out var size);
    var buffer = Marshal.AllocHGlobal((int)size);
    try
    {
        if (!NativeMethods.GetTokenInformation(token, 25, buffer, size, out _))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var label = Marshal.PtrToStructure<SidAndAttributes>(buffer);
        return new SecurityIdentifier(label.Sid).Value;
    }
    finally { Marshal.FreeHGlobal(buffer); }
}

static async Task ExhaustMemory(JsonElement payload, CancellationToken cancellationToken)
{
    var requested = payload.TryGetProperty("bytes", out var value) && value.TryGetInt32(out var bytes)
        ? Math.Clamp(bytes, 16 * 1024 * 1024, 384 * 1024 * 1024)
        : 320 * 1024 * 1024;
    if (OperatingSystem.IsWindows())
    {
        var chunks = new List<IntPtr>();
        var committed = 0;
        try
        {
            while (committed < requested)
            {
                var size = Math.Min(4 * 1024 * 1024, requested - committed);
                var memory = NativeMethods.VirtualAlloc(IntPtr.Zero, (nuint)size, 0x1000 | 0x2000, 0x04);
                if (memory == IntPtr.Zero) break;
                chunks.Add(memory);
                committed += size;
                for (var offset = 0; offset < size; offset += 4096)
                    Marshal.WriteByte(memory, offset, 1);
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        finally
        {
            foreach (var memory in chunks) NativeMethods.VirtualFree(memory, UIntPtr.Zero, 0x8000);
        }
    }
    else
    {
        var chunks = new List<byte[]>();
        while (chunks.Sum(chunk => chunk.Length) < requested)
        {
            chunks.Add(new byte[Math.Min(4 * 1024 * 1024, requested - chunks.Sum(chunk => chunk.Length))]);
            chunks[^1][0] = 1;
        }
        GC.KeepAlive(chunks);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}

[StructLayout(LayoutKind.Sequential)]
struct SidAndAttributes
{
    public IntPtr Sid;
    public uint Attributes;
}

[StructLayout(LayoutKind.Sequential)]
struct JobBasicLimitInformation
{
    public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
    public uint LimitFlags;
    public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
    public uint ActiveProcessLimit;
    public UIntPtr Affinity;
    public uint PriorityClass, SchedulingClass;
}

[StructLayout(LayoutKind.Sequential)]
struct JobExtendedLimitInformation
{
    public JobBasicLimitInformation Basic;
    public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
    public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
}

[StructLayout(LayoutKind.Sequential)]
struct JobCpuRateControlInformation
{
    public uint ControlFlags, CpuRate;
}

[StructLayout(LayoutKind.Sequential)]
struct JobUiRestrictions
{
    public uint UiRestrictionsClass;
}

sealed class SafeTokenHandle : SafeHandle
{
    public SafeTokenHandle() : base(IntPtr.Zero, true) { }
    public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);
    protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
}

static class NativeMethods
{
    [DllImport("kernel32.dll")] internal static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool OpenProcessToken(IntPtr process, uint access, out SafeTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr buffer, uint length, out uint returnLength);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool QueryInformationJobObject(IntPtr job, int informationClass, IntPtr info, uint length, out uint returnLength);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr VirtualAlloc(IntPtr address, nuint size, uint allocationType, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool VirtualFree(IntPtr address, UIntPtr size, uint freeType);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
}
