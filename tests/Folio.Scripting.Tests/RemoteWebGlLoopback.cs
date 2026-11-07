using System.Runtime.CompilerServices;
using Folio.Scripting.Dom;
using Folio.Scripting.Gpu.Remote;

namespace Folio.Scripting.Tests;

/// <summary>
/// FOLIO_WEBGL_REMOTE=loopback runs every WebGL test of this assembly through the remote
/// command-buffer path (encoder, framing, dispatcher) instead of calling the device directly.
/// </summary>
internal static class RemoteWebGlLoopback
{
    [ModuleInitializer]
    internal static void Install()
    {
        if (Environment.GetEnvironmentVariable("FOLIO_WEBGL_REMOTE") == "loopback")
            GpuDocumentBridge.DeviceDecorator = LoopbackGpuDevice.Wrap;
    }
}
