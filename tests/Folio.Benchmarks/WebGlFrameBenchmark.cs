using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Folio.RenderTests;
using Folio.Scripting;
using Folio.Scripting.Dom;
using Folio.Scripting.Gpu;
using Folio.Scripting.Gpu.Remote;
using Folio.Scripting.Native;

namespace Folio.Benchmarks;

/// <summary>
/// <c>webgl [frames]</c>: an animated 800x600 WebGL scene (a per-frame vertex upload through shared memory, then
/// 20 draws with uniform changes) driven by requestAnimationFrame. The same page runs against the device directly,
/// through the in-process remote path, and through the separate GPU worker process. Fails when the worker's p95
/// frame is over 16.7 ms.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WebGlFrameBenchmark
{
    internal const double Target = 16.7;

    private const string Page = """
        <body style='margin:0'><canvas id='c' width='800' height='600'></canvas>
        <script>
        var gl = document.getElementById('c').getContext('webgl', { antialias: false });
        function shader(type, source) { var s = gl.createShader(type); gl.shaderSource(s, source); gl.compileShader(s); return s; }
        var program = gl.createProgram();
        gl.attachShader(program, shader(gl.VERTEX_SHADER, 'attribute vec2 p; uniform vec2 o; void main() { gl_Position = vec4(p + o, 0.0, 1.0); gl_PointSize = 3.0; }'));
        gl.attachShader(program, shader(gl.FRAGMENT_SHADER, 'precision mediump float; uniform vec4 c; void main() { gl_FragColor = c; }'));
        gl.linkProgram(program);
        gl.useProgram(program);
        var N = 4000, DRAWS = 20, PER = N / DRAWS, data = new Float32Array(N * 2), t = 0, frames = 0;
        gl.bindBuffer(gl.ARRAY_BUFFER, gl.createBuffer());
        gl.bufferData(gl.ARRAY_BUFFER, data.byteLength, gl.DYNAMIC_DRAW);
        var p = gl.getAttribLocation(program, 'p');
        gl.enableVertexAttribArray(p);
        gl.vertexAttribPointer(p, 2, gl.FLOAT, false, 0, 0);
        var offset = gl.getUniformLocation(program, 'o'), colour = gl.getUniformLocation(program, 'c');
        function frame() {
          t++;
          for (var i = 0; i < N; i++) { data[2 * i] = Math.sin(i * 0.37 + t * 0.05) * 0.9; data[2 * i + 1] = Math.cos(i * 0.11 + t * 0.03) * 0.9; }
          gl.bufferSubData(gl.ARRAY_BUFFER, 0, data);
          gl.clearColor(0, 0, 0, 1);
          gl.clear(gl.COLOR_BUFFER_BIT);
          for (var d = 0; d < DRAWS; d++) { gl.uniform2f(offset, d * 0.001, 0); gl.uniform4f(colour, d / DRAWS, 0.5, 1 - d / DRAWS, 1); gl.drawArrays(gl.POINTS, d * PER, PER); }
          frames++;
          requestAnimationFrame(frame);
        }
        requestAnimationFrame(frame);
        </script>
        """;

    internal static int Run(int frameCount)
    {
        var budget = ResourceLimits.Default.MaxGpuBytes;
        var report = new StringBuilder()
            .AppendLine($"WebGL frame benchmark: 800x600, {frameCount} frames of 4000 uploaded vertices + 20 draws; target {Target} ms p95 through the GPU worker.")
            .AppendLine()
            .AppendLine("| Path | Median ms | p95 ms | Frames drawn | GL errors | Lost |")
            .AppendLine("|---|--:|--:|--:|--:|---|");
        var failed = false;
        report.AppendLine(Row("direct", Measure(_ => FolioGpuBackend.TryCreate(budget), frameCount), ref failed, gate: false));
        report.AppendLine(Row("in-process remote", Measure(_ =>
        {
            var result = FolioGpuBackend.TryCreate(budget);
            return result.Device is { } device ? new GpuDeviceResult(LoopbackGpuDevice.Wrap(device), null) : result;
        }, frameCount), ref failed, gate: false));

        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        var host = Environment.GetEnvironmentVariable("FOLIO_CONTENT_HOST") is { Length: > 0 } configured ? Path.GetFullPath(configured)
            : Path.Combine(RepoPaths.Root, "tools", "Folio.ContentHost", "bin", configuration, "net10.0", "Folio.ContentHost.dll");
        using var worker = GpuWebGlWorker.Start(host, budget);
        using var channelJson = JsonDocument.Parse(worker.HandOff(Process.GetCurrentProcess().SafeHandle).ToJsonString());
        using var channel = RemoteGpuChannel.Adopt(channelJson.RootElement);
        report.AppendLine(Row("GPU worker process", Measure(channel.DocumentDeviceFactory, frameCount), ref failed, gate: true));
        report.AppendLine().AppendLine($"GPU worker host: {host}");
        using (var process = Process.GetProcessById(worker.ProcessId))
            report.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"GPU worker memory: peak working set {Mb(process.PeakWorkingSet64)} MB, private {Mb(process.PrivateMemorySize64)} MB of its {Mb(GpuWebGlWorker.WorkerMemoryBytes - GpuWebGlWorker.SectionBytes)} MB job limit; shared sections {Mb(GpuWebGlWorker.SectionBytes)} MB, charged to both jobs."));

        Console.WriteLine(report);
        if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary)
            File.AppendAllText(summary, "### WebGL frames\n\n" + report);
        return failed ? 1 : 0;
    }

    private static string Row(string name, Result result, ref bool failed, bool gate)
    {
        // A broken path (errors, loss, frames not running) fails whatever its timing.
        failed |= result.Errors != 0 || result.Lost || result.Frames < result.Expected || (gate && result.P95 > Target);
        return string.Create(CultureInfo.InvariantCulture,
            $"| {name} | {result.Median:F2} | {result.P95:F2} | {result.Frames} | {result.Errors} | {(result.Lost ? "yes" : "no")} |");
    }

    private static string Mb(long bytes) => (bytes / (1024.0 * 1024)).ToString("F1", CultureInfo.InvariantCulture);

    private sealed record Result(double Median, double P95, int Frames, int Expected, int Errors, bool Lost);

    private static Result Measure(Func<long, GpuDeviceResult> factory, int frameCount)
    {
        using var engine = new NativeJavaScriptEngine();
        DomBindings.ConfigureGpuFactory(engine, factory);
        var clock = new VirtualEventLoopClock();
        using var loop = new HtmlEventLoop(engine, clock);
        var options = new FolioOptions { Scripting = ScriptingPolicy.ContentProcess, Capabilities = new FeatureCapabilities { WebGl = true } };
        using var document = Document.Parse(Page, options);
        document.Paint(800, 600);
        DomBindings.Install(engine, document, eventLoop: loop);
        ScriptGlobalsInstaller.Install(loop, engine);
        try
        {
            using var runner = new ScriptRunner(document.Node, engine, loop, options);
            runner.Run();
            const int warmup = 30;
            var samples = new List<double>(frameCount);
            for (var i = 0; i < warmup + frameCount; i++)
            {
                clock.Advance(16.7);
                var start = Stopwatch.GetTimestamp();
                loop.UpdateTheRendering();
                if (i >= warmup) samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            var frames = (int)engine.Evaluate("frames").AsNumber();
            var errors = (int)engine.Evaluate("var n = 0; while (gl.getError() !== 0 && n < 99) n++; n").AsNumber();
            var lost = engine.Evaluate("gl.isContextLost()").AsBoolean();
            samples.Sort();
            return new(samples[samples.Count / 2], samples[Math.Min(samples.Count - 1, (int)Math.Ceiling(samples.Count * .95) - 1)],
                frames, warmup + frameCount, errors, lost);
        }
        finally { DomBindings.Dispose(engine); }
    }
}
