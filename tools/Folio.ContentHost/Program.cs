using Folio.Scripting;
using Folio.Scripting.Ipc;
using Folio.ContentHost;

var framePath = Environment.GetEnvironmentVariable("FOLIO_CONTENT_FRAME_PATH")
    ?? Path.Combine(Path.GetTempPath(), $"folio-frame-{Environment.ProcessId}.bin");
using var frameStore = new CanvasFrameStore(framePath, 1280, 720, 1280 * 4);
await using var session = new ContentHostSession(Console.OpenStandardInput(), Console.OpenStandardOutput());
using var runtime = new ContentDocumentRuntime(session, frameStore);

session.MessageReceived += async (message, cancellationToken) =>
{
    if (message.Kind == ContentMessageKind.Hello)
    {
        await session.SendAsync(ContentMessageKind.Hello, new { protocol = ContentMessage.CurrentVersion }, cancellationToken);
        return;
    }
    if (message.Kind == ContentMessageKind.Capabilities)
    {
        var accepted = runtime.ApplyCapabilities(message);
        await session.SendAsync(ContentMessageKind.Capabilities, new { sequence = message.Sequence, capabilities = runtime.Capabilities, accepted }, cancellationToken);
        return;
    }
    if (message.Kind == ContentMessageKind.Input)
    {
        if (message.TryGetInput(out var input) && input is not null)
        {
            try
            {
                await runtime.RouteInputAsync(input, message.Sequence, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await session.SendAsync(ContentMessageKind.Diagnostic, new { sequence = message.Sequence, code = "Input", message = exception.Message }, cancellationToken).ConfigureAwait(false);
            }
        }
        return;
    }
    if (message.Kind != ContentMessageKind.Load) return;
    try
    {
        var result = await runtime.ExecuteAsync(message, cancellationToken);
        await session.SendAsync(ContentMessageKind.Frame, new
        {
            sequence = message.Sequence,
            value = result.Value,
            displayItems = result.DisplayItems,
            canvasCommands = result.CanvasCommands,
            diagnostics = result.Diagnostics,
            appliedCapabilities = runtime.Capabilities,
            displayList = result.DisplayList,
            canvasFrame = new { path = framePath, width = frameStore.Width, height = frameStore.Height, stride = frameStore.Stride, sequence = frameStore.Sequence }
        }, cancellationToken);
    }
    catch (Exception exception)
    {
        var code = exception switch
        {
            ScriptLimitExceededException limit => limit.LimitName,
            ScriptInterruptedException => "Interrupted",
            ScriptRuntimeException => "Runtime",
            _ => "Host"
        };
        await session.SendAsync(ContentMessageKind.Diagnostic, new { sequence = message.Sequence, code, message = exception.Message }, cancellationToken);
    }
};

await session.RunAsync();
