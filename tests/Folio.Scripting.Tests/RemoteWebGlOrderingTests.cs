using System.Runtime.Versioning;
using Folio.Scripting.Dom;
using Folio.Scripting.Gpu;
using Folio.Scripting.Gpu.Remote;
using Folio.Scripting.Native;

namespace Folio.Scripting.Tests;

/// <summary>
/// Stage 3 gate: errors of queued calls must be observable exactly as with direct execution,
/// in call order, interleaved with the bindings' own validation errors and synchronous failures.
/// </summary>
public sealed class RemoteWebGlOrderingTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    // No getError between the calls: native failures of queued calls, validation errors and a
    // synchronous readback failure all have to come out of getError in the order they happened.
    private const string MixedErrors = """
        var gl = document.getElementById('c').getContext(KIND, { antialias: false });
        var buffer = gl.createBuffer();
        gl.drawArrays(gl.TRIANGLES, 0, 3);
        gl.bindBuffer(0x1234, buffer);
        gl.drawArrays(99, 0, 3);
        gl.clear(0x7);
        gl.texParameteri(gl.TEXTURE_2D, 0x1234, 0);
        gl.readPixels(0, 0, -1, 1, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array(4));
        gl.viewport(0, 0, -1, -1);
        gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
        gl.bufferData(gl.ARRAY_BUFFER, 16, gl.STATIC_DRAW);
        gl.drawArrays(gl.POINTS, 0, 1000000);
        gl.lineWidth(0);
        var errors = [];
        for (var e; (e = gl.getError()) !== 0 && errors.length < 70;) errors.push(e);
        errors.join(',') + '|' + gl.isContextLost() + '|' + gl.getError();
        """;

    [Theory(Skip = "Only supported on Windows.", SkipUnless = nameof(IsWindows))]
    [InlineData("webgl")]
    [InlineData("webgl2")]
    [SupportedOSPlatform("windows")]
    public void ErrorSequenceMatchesDirectExecution(string kind)
    {
        var direct = Run(kind, MixedErrors, remote: false);
        var remote = Run(kind, MixedErrors, remote: true);
        Assert.Equal(direct, remote);
        var codes = direct.Split('|')[0].Split(',', StringSplitOptions.RemoveEmptyEntries);
        // The native context keeps one pending error (the first wins), so direct execution reports
        // that error plus the bindings' own; the remote path must reproduce exactly the same.
        Assert.True(codes.Distinct().Count() >= 2, $"The scenario should raise several kinds of error: {direct}");
        Assert.EndsWith("|false|0", direct);
    }

    [Theory(Skip = "Only supported on Windows.", SkipUnless = nameof(IsWindows))]
    [InlineData("webgl")]
    [InlineData("webgl2")]
    [SupportedOSPlatform("windows")]
    public void ErrorsDoNotLeakBetweenContextsSharingASession(string kind)
    {
        const string script = """
            var first = document.getElementById('c').getContext(KIND);
            var second = document.getElementById('d').getContext(KIND);
            first.drawArrays(99, 0, 3);
            second.clearColor(0, 0, 0, 1);
            second.clear(second.COLOR_BUFFER_BIT);
            [second.getError(), first.getError(), first.getError()].join(',');
            """;
        Assert.Equal(Run(kind, script, remote: false), Run(kind, script, remote: true));
    }

    [Theory(Skip = "Only supported on Windows.", SkipUnless = nameof(IsWindows))]
    [InlineData("webgl")]
    [InlineData("webgl2")]
    [SupportedOSPlatform("windows")]
    public void BudgetFailureOfAQueuedUploadLosesTheContextBeforeLaterErrors(string kind)
    {
        // The document budget is 8 MiB: the queued 32 MiB upload fails in the worker, which must
        // lose the context and report OUT_OF_MEMORY ahead of the later validation error.
        const string script = """
            var canvas = document.getElementById('c');
            var lostEvents = 0;
            canvas.addEventListener('webglcontextlost', function(e) { lostEvents++; });
            var gl = canvas.getContext(KIND);
            gl.bindBuffer(gl.ARRAY_BUFFER, gl.createBuffer());
            gl.bufferData(gl.ARRAY_BUFFER, 32 * 1024 * 1024, gl.STATIC_DRAW);
            gl.viewport(0, 0, -1, -1);
            var viewport = gl.getParameter(gl.VIEWPORT);
            gl.clear(gl.COLOR_BUFFER_BIT);
            var errors = [];
            for (var e; (e = gl.getError()) !== 0 && errors.length < 9;) errors.push(e);
            errors.join(',') + '|' + gl.isContextLost() + '|' + lostEvents + '|' + viewport;
            """;
        var direct = Run(kind, script, remote: false, maxGpuBytes: 8 * 1024 * 1024);
        Assert.Equal(direct, Run(kind, script, remote: true, maxGpuBytes: 8 * 1024 * 1024));
        Assert.Contains("1285", direct); // OUT_OF_MEMORY
        Assert.Contains("|true|", direct);
    }

    [SupportedOSPlatform("windows")]
    private static string Run(string kind, string script, bool remote, int? maxGpuBytes = null)
    {
        using var engine = new NativeJavaScriptEngine();
        DomBindings.ConfigureGpuFactory(engine, maxBytes =>
        {
            var result = FolioGpuBackend.TryCreate(maxBytes, GpuRendererPreference.WarpOnly);
            return remote && result.Device is { } device ? new GpuDeviceResult(LoopbackGpuDevice.Wrap(device), null) : result;
        });
        using var document = Document.Parse("<canvas id='c' width='4' height='4'></canvas><canvas id='d' width='4' height='4'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true },
            Limits = maxGpuBytes is { } budget ? ResourceLimits.Default with { MaxGpuBytes = budget } : ResourceLimits.Default,
        });
        DomBindings.Install(engine, document);
        try { return engine.Evaluate(script.Replace("KIND", "'" + kind + "'")).AsString() ?? ""; }
        finally { DomBindings.Dispose(engine); }
    }
}
