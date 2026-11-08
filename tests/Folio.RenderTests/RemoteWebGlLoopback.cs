using System.Runtime.CompilerServices;
using Folio.Scripting.Dom;
using Folio.Scripting.Gpu;
using Folio.Scripting.Gpu.Remote;

namespace Folio.RenderTests;

/// <summary>
/// FOLIO_WEBGL_REMOTE=loopback runs this assembly's WebGL (tests and the conformance runner) through the remote
/// command-buffer path (encoder, framing, dispatcher) instead of calling the device directly.
/// FOLIO_WEBGL_REMOTE=worker runs each device's WebGL in its own real GPU worker process.
/// </summary>
internal static class RemoteWebGlLoopback
{
    [ModuleInitializer]
    internal static void Install()
    {
        switch (Environment.GetEnvironmentVariable("FOLIO_WEBGL_REMOTE"))
        {
            case "loopback":
                GpuDocumentBridge.DeviceDecorator = LoopbackGpuDevice.Wrap;
                break;
            case "worker" when OperatingSystem.IsWindows():
                GpuDocumentBridge.DeviceDecorator = InWorker;
                break;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IGpuDevice InWorker(IGpuDevice device)
    {
        if (device is not FolioGpuDevice native) return device;
        var budget = native.MaxBytes;
        native.Dispose();
        return WorkerGpuDevice.Start(ContentHost(), budget);
    }

    private static string ContentHost()
    {
        if (Environment.GetEnvironmentVariable("FOLIO_CONTENT_HOST") is { Length: > 0 } configured) return Path.GetFullPath(configured);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        return Path.Combine(RepoPaths.Root, "tools", "Folio.ContentHost", "bin", configuration, "net10.0", "Folio.ContentHost.dll");
    }
}
