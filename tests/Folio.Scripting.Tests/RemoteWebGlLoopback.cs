using System.Runtime.CompilerServices;
using Folio.Scripting.Dom;
using Folio.Scripting.Gpu;
using Folio.Scripting.Gpu.Remote;

namespace Folio.Scripting.Tests;

/// <summary>
/// FOLIO_WEBGL_REMOTE=loopback runs every WebGL test of this assembly through the remote
/// command-buffer path (encoder, framing, dispatcher) instead of calling the device directly.
/// FOLIO_WEBGL_REMOTE=worker goes further: each device's WebGL runs in its own real GPU worker
/// process (the content host's <c>--gpu-webgl</c>, sandboxed), with the device's budget.
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
        return WorkerGpuDevice.Start(ContentHostTestPaths.Resolve(), budget);
    }
}
