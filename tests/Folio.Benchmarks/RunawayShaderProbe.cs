using System.Diagnostics;
using System.Runtime.Versioning;
using Folio.Scripting.Ipc;

namespace Folio.Benchmarks;

/// <summary>
/// <c>runaway-shader [check]</c>: one deliberately runaway fragment shader drawn through the GPU worker in a real
/// content process. On real hardware this can make the OS reset the GPU driver for every application, so it is
/// never part of CI or any test run; run it by hand, once, with the user's approval and nothing else on the GPU.
/// <c>check</c> only compiles and links the shader. Prints a timeline and fails unless the page stays alive,
/// reports the loss, is restored on a fresh worker and draws again.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class RunawayShaderProbe
{
    private const string Page = """
        <canvas id='c' width='256' height='256'></canvas>
        <script>
        var lost = 0, restored = 0;
        var gl = document.getElementById('c').getContext('webgl', { antialias: false, preserveDrawingBuffer: true });
        gl.oncontextlost = function() { lost++; };
        gl.oncontextrestored = function() { restored++; };
        function program(fragment) {
          var p = gl.createProgram();
          var v = gl.createShader(gl.VERTEX_SHADER); gl.shaderSource(v, 'attribute vec2 a; void main() { gl_Position = vec4(a, 0.0, 1.0); }'); gl.compileShader(v);
          var f = gl.createShader(gl.FRAGMENT_SHADER); gl.shaderSource(f, fragment); gl.compileShader(f);
          gl.attachShader(p, v); gl.attachShader(p, f); gl.bindAttribLocation(p, 0, 'a'); gl.linkProgram(p);
          return { program: p, compiled: gl.getShaderParameter(f, gl.COMPILE_STATUS), log: gl.getShaderInfoLog(f) || '', linked: gl.getProgramParameter(p, gl.LINK_STATUS) };
        }
        // 2e9 dependent iterations per pixel on a 256x256 target: far beyond any OS GPU timeout.
        var runaway = program('precision highp float; uniform float k; void main() { float x = 0.0; for (int i = 0; i < 2000000000; i++) { x = fract(x * 1.0001 + k); } gl_FragColor = vec4(x, 0.0, 0.0, 1.0); }');
        var quad = gl.createBuffer(); gl.bindBuffer(gl.ARRAY_BUFFER, quad); gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
        gl.enableVertexAttribArray(0); gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);
        var pixel = new Uint8Array(4);
        </script>
        """;

    private const string Compiled = "runaway.compiled + '|' + runaway.linked + '|' + runaway.log";
    private const string Draw = "gl.useProgram(runaway.program); gl.uniform1f(gl.getUniformLocation(runaway.program, 'k'), 0.5); var t0 = performance.now(); gl.drawArrays(gl.TRIANGLES, 0, 3); gl.readPixels(0, 0, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, pixel); Math.round(performance.now() - t0) + ' ms in page|lost ' + gl.isContextLost() + '|error ' + gl.getError() + '|events ' + lost";
    private const string State = "gl.isContextLost() + '|' + lost + '|' + restored";
    private const string DrawGreen = "gl.clearColor(0, 1, 0, 1); gl.clear(gl.COLOR_BUFFER_BIT); gl.readPixels(1, 1, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, pixel); Array.from(pixel).join(',')";

    internal static async Task<int> RunAsync(bool checkOnly)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        var host = Environment.GetEnvironmentVariable("FOLIO_CONTENT_HOST") is { Length: > 0 } configured ? Path.GetFullPath(configured)
            : Path.Combine(Folio.RenderTests.RepoPaths.Root, "tools", "Folio.ContentHost", "bin", configuration, "net10.0", "Folio.ContentHost.dll");
        var watch = Stopwatch.StartNew();
        void Log(string line) => Console.WriteLine($"{watch.Elapsed.TotalSeconds,7:F2}s  {line}");
        ContentProcessClient.GpuWorkerOverride = true;
        try
        {
            // The draw blocks the page's script until the GPU answers or is lost; allow for that in the heartbeat.
            await using var client = ContentProcessClient.Start(host, limits: new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromSeconds(60)));
            Log($"host {host}");
            var compiled = await Value(client, new { html = Page, capabilities = new Folio.FeatureCapabilities { WebGl = true }, script = Compiled }, TimeSpan.FromSeconds(30));
            Log($"compiled|linked|log: {compiled}");
            var first = client.GpuWorkerProcessId;
            Log($"content process {client.Process.Id}, GPU worker {first}");
            if (checkOnly || !compiled.StartsWith("true|true", StringComparison.Ordinal)) return compiled.StartsWith("true|true", StringComparison.Ordinal) ? 0 : 1;

            Log("drawing the runaway shader once");
            var drawStart = watch.Elapsed;
            string drawn;
            try { drawn = await Value(client, new { script = Draw }, TimeSpan.FromSeconds(60)); }
            catch (Exception error) { drawn = "no frame: " + error.Message; }
            Log($"draw returned after {(watch.Elapsed - drawStart).TotalSeconds:F2}s: {drawn}");
            Log($"first worker exited: {(first is { } id ? Exited(id) : "n/a")}, content process alive: {!client.Process.HasExited}");

            var restoredState = await Poll(client, State, "false|1|1", TimeSpan.FromSeconds(30));
            Log($"lost|events|restored: {restoredState}; worker now {client.GpuWorkerProcessId}");
            var green = restoredState.StartsWith("false|1|1", StringComparison.Ordinal)
                ? await Value(client, new { script = DrawGreen }, TimeSpan.FromSeconds(30)) : "skipped";
            Log($"draw after restore: {green}");
            var passed = !client.Process.HasExited && restoredState.StartsWith("false|1|1", StringComparison.Ordinal)
                && green == "0,255,0,255" && client.GpuWorkerProcessId != first;
            Log(passed ? "PASS: page alive, loss reported, restored on a fresh worker, drawing again" : "FAIL");
            return passed ? 0 : 1;
        }
        finally { ContentProcessClient.GpuWorkerOverride = null; }
    }

    private static string Exited(int processId)
    {
        try { using var process = Process.GetProcessById(processId); return process.HasExited ? "yes" : "no"; }
        catch (ArgumentException) { return "yes"; }
    }

    private static async Task<string> Poll(ContentProcessClient client, string script, string prefix, TimeSpan limit)
    {
        var watch = Stopwatch.StartNew();
        var state = "";
        while (watch.Elapsed < limit)
        {
            try { state = await Value(client, new { script }, TimeSpan.FromSeconds(30)); }
            catch (Exception error) { state = "no frame: " + error.Message; }
            if (state.StartsWith(prefix, StringComparison.Ordinal)) break;
            await Task.Delay(100);
        }
        return state;
    }

    private static async Task<string> Value(ContentProcessClient client, object payload, TimeSpan timeout)
    {
        var completion = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask Handler(ContentMessage message, CancellationToken _)
        {
            if (message.Kind == ContentMessageKind.Frame && message.Payload.GetProperty("sequence").GetInt64() > 0) completion.TrySetResult(message);
            else if (message.Kind == ContentMessageKind.Diagnostic) completion.TrySetException(new InvalidOperationException(message.Payload.ToString()));
            return ValueTask.CompletedTask;
        }
        client.MessageReceived += Handler;
        try
        {
            await client.SendAsync(ContentMessageKind.Load, payload);
            var frame = await completion.Task.WaitAsync(timeout);
            return frame.Payload.GetProperty("value").GetString() ?? "";
        }
        finally { client.MessageReceived -= Handler; }
    }
}
