using Folio.Scripting;
using Folio.Scripting.Ipc;
using Folio.Scripting.Native;
using Folio.ContentHost;

// Dispatch before creating DOM, frame stores, script engines or content-session state.
if (args.Length == 1 && args[0] == "--gpu-webgl" && OperatingSystem.IsWindows())
{
    // Separate GPU worker: executes WebGL command batches for one content process.
    Folio.Scripting.Gpu.Remote.GpuWebGlWorkerHost.Run(Console.OpenStandardInput());
    return;
}
if (args.Length != 0 && args[0] == "--gpu-worker")
{
    if (args.Length != 1) throw new ArgumentException("Unexpected GPU worker arguments.");
    Folio.Scripting.Gpu.GpuWorkerServer.Run(Console.OpenStandardInput(), Console.OpenStandardOutput());
    return;
}

var framePath = Environment.GetEnvironmentVariable("FOLIO_CONTENT_FRAME_PATH")
    ?? Path.Combine(Path.GetTempPath(), $"folio-frame-{Environment.ProcessId}.bin");
using var frameStore = new CanvasFrameStore(framePath, 1280, 720, 1280 * 4);
await using var session = new ContentHostSession(Console.OpenStandardInput(), Console.OpenStandardOutput());
Func<IScriptEngine>? engineFactory = string.Equals(Environment.GetEnvironmentVariable("FOLIO_SCRIPT_ENGINE"), "jint", StringComparison.OrdinalIgnoreCase)
    ? CreateOracleEngine
    : null;
if (engineFactory is null)
{
    _ = Task.Run(NativeJavaScriptEngine.Prime);
}
using var runtime = new ContentDocumentRuntime(session, frameStore, engineFactory);
using var runtimeGate = new SemaphoreSlim(1, 1);
using var ticking = new CancellationTokenSource();

async Task SendFrame(long sequence, ContentDocumentResult? result, CancellationToken cancellationToken)
{
    await session.SendAsync(ContentMessageKind.Frame, new ContentFramePayload(sequence,
        result?.Value ?? "", result?.DisplayItems ?? 0, result?.CanvasCommands ?? 0,
        result?.Diagnostics ?? [], result?.Timings, runtime.Capabilities,
        new ContentCanvasDescriptor(runtime.FrameStore.FilePath, runtime.FrameStore.Width, runtime.FrameStore.Height,
            runtime.FrameStore.Stride, runtime.FrameStore.Sequence, runtime.ViewportRevision)), cancellationToken);
}

async Task TickAsync()
{
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(16));
    try
    {
        while (await timer.WaitForNextTickAsync(ticking.Token))
        {
            await runtimeGate.WaitAsync(ticking.Token);
            try { if (runtime.Tick()) await SendFrame(0, null, ticking.Token); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                await session.SendAsync(ContentMessageKind.Diagnostic, new Dictionary<string, object?> { ["code"] = "Runtime", ["message"] = error.Message }, ticking.Token);
                runtime.StopDocument();
            }
            finally { runtimeGate.Release(); }
        }
    }
    catch (OperationCanceledException) when (ticking.IsCancellationRequested) { }
}

static IScriptEngine CreateOracleEngine()
{
#if FOLIO_ORACLE_HOST
    var type = Type.GetType("Folio.Scripting.Jint.JintScriptEngine, Folio.Scripting.Jint", throwOnError: true)
        ?? throw new InvalidOperationException("The Jint oracle is not present in this production host.");
    return (IScriptEngine)Activator.CreateInstance(type)!;
#else
    throw new InvalidOperationException("The Jint oracle is not present in this production host.");
#endif
}

session.MessageReceived += async (message, cancellationToken) =>
{
    await runtimeGate.WaitAsync(cancellationToken);
    try
    {
        if (message.Kind == ContentMessageKind.Hello)
        {
            await session.SendAsync(ContentMessageKind.Hello, new Dictionary<string, object?> { ["protocol"] = ContentMessage.CurrentVersion }, cancellationToken);
            return;
        }
        if (message.Kind == ContentMessageKind.Capabilities)
        {
            var accepted = runtime.ApplyCapabilities(message);
            await session.SendAsync(ContentMessageKind.Capabilities, new Dictionary<string, object?> { ["sequence"] = message.Sequence, ["capabilities"] = runtime.Capabilities, ["accepted"] = accepted }, cancellationToken);
            return;
        }
        if (message.Kind == ContentMessageKind.MediaCaptureActivity)
        {
            runtime.ApplyMediaCaptureActivity(message);
            return;
        }
        if (message.Kind == ContentMessageKind.Viewport)
        {
            var viewport = System.Text.Json.JsonSerializer.Deserialize(message.Payload, ContentProtocolJsonContext.Default.ContentViewport)
                ?? throw new InvalidDataException("Missing viewport.");
            runtime.ConfigureViewport(viewport);
            if (runtime.Document is not null) await SendFrame(message.Sequence, null, cancellationToken);
            else await session.SendAsync(ContentMessageKind.Viewport, new Dictionary<string, object?> { ["sequence"] = message.Sequence, ["accepted"] = true }, cancellationToken);
            return;
        }
        if (message.Kind == ContentMessageKind.Input && message.TryGetInput(out var input) && input is not null)
        {
            runtime.ApplyInput(input);
            await SendFrame(message.Sequence, null, cancellationToken);
            return;
        }
        if (message.Kind != ContentMessageKind.Load) return;
        try
        {
            var result = await runtime.ExecuteAsync(message, cancellationToken);
            await SendFrame(message.Sequence, result, cancellationToken);
        }
        catch (Exception exception)
        {
            var code = exception switch
            {
                ScriptLimitExceededException limit => limit.LimitName,
                ScriptInterruptedException => "Interrupted",
                ScriptRuntimeException => "Runtime",
                InvalidOperationException error when error.Message.Contains("evaluation step limit", StringComparison.OrdinalIgnoreCase) => "Statements",
                _ => "Host"
            };
            await session.SendAsync(ContentMessageKind.Diagnostic, new Dictionary<string, object?> { ["sequence"] = message.Sequence, ["code"] = code, ["message"] = exception.Message }, cancellationToken);
        }
    }
    finally { runtimeGate.Release(); }
};

var tickTask = TickAsync();
try { await session.RunAsync(); }
finally { ticking.Cancel(); await tickTask; }
