using Folio;
using Folio.Scripting.Ipc;

namespace Folio.Scripting.Tests;

public sealed class CapabilityMatrixTests
{
    [Fact]
    public void CapabilityRowEnablesOnlyTheRequestedFeatureAndKeepsLimitsActive()
    {
        var row = Environment.GetEnvironmentVariable("FOLIO_CAPABILITY_MATRIX") ?? "none";
        var capabilities = row switch
        {
            "webgl" => new FeatureCapabilities { WebGl = true },
            "media" => new FeatureCapabilities { Media = true },
            "workers" => new FeatureCapabilities { Workers = true },
            "webassembly" => new FeatureCapabilities { WebAssembly = true },
            "iframes" => new FeatureCapabilities { Iframes = true },
            "webrtc" => new FeatureCapabilities { WebRtc = true },
            "service-workers" => new FeatureCapabilities { ServiceWorkers = true },
            "all" => new FeatureCapabilities
            {
                WebGl = true, Media = true, Workers = true, WebAssembly = true,
                Iframes = true, WebRtc = true, ServiceWorkers = true
            },
            "none" => FeatureCapabilities.Default,
            _ => throw new InvalidOperationException($"Unknown capability matrix row '{row}'.")
        };

        Assert.Equal(row, Environment.GetEnvironmentVariable("FOLIO_CAPABILITY_MATRIX") ?? "none");
        Assert.False(new ContentProcessSupervisor(new ContentProcessLimits(MemoryBytes: 1)).CheckMemory(2));
        Assert.False(new ContentProcessSupervisor(new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromMilliseconds(-1))).CheckHeartbeat());
        if (row == "all")
            Assert.True(capabilities.WebGl && capabilities.Media && capabilities.Workers && capabilities.WebAssembly
                && capabilities.Iframes && capabilities.WebRtc && capabilities.ServiceWorkers);
        else if (row != "none")
            Assert.Equal(1, new[] { capabilities.WebGl, capabilities.Media, capabilities.Workers, capabilities.WebAssembly,
                capabilities.Iframes, capabilities.WebRtc, capabilities.ServiceWorkers }.Count(value => value));
        else
            Assert.Equal(0, new[] { capabilities.WebGl, capabilities.Media, capabilities.Workers, capabilities.WebAssembly,
                capabilities.Iframes, capabilities.WebRtc, capabilities.ServiceWorkers }.Count(value => value));
    }
}
