using System.Text.Json;
using Folio;
using Folio.Scripting;
using Folio.Scripting.Ipc;
using Folio.Scripting.Jint;
using Folio.Scripting.Dom;
using Folio.Dom;
using Folio.Html;
using Folio.Skia;

using var engine = new JintScriptEngine();
Document? document = null;
var framePath = Path.Combine(Path.GetTempPath(), $"folio-frame-{Environment.ProcessId}.bin");
using var frameStore = new CanvasFrameStore(framePath, 1280, 720, 1280 * 4);
await using var session = new ContentHostSession(Console.OpenStandardInput(), Console.OpenStandardOutput());
session.MessageReceived += async (message, cancellationToken) =>
{
    if (message.Kind == ContentMessageKind.Hello)
    {
        await session.SendAsync(ContentMessageKind.Hello, new { protocol = ContentMessage.CurrentVersion }, cancellationToken);
        return;
    }

    if (message.Kind != ContentMessageKind.Load) return;
    var script = message.Payload.TryGetProperty("script", out var source) ? source.GetString() ?? "" : "";
    var url = message.Payload.TryGetProperty("url", out var location) ? location.GetString() ?? "content" : "content";
    try
    {
        if (message.Payload.TryGetProperty("html", out var htmlElement))
        {
            document?.Dispose();
            document = Document.Parse(htmlElement.GetString() ?? "", new FolioOptions { Scripting = ScriptingPolicy.ContentProcess });
            DomBindings.Install(engine, document.Node);
        }
        var result = engine.Evaluate(script, url);
        var frame = document is null ? null : document.Paint(1280, 720).List;
        var canvasCommands = document?.Node.Descendants.OfType<ElementNode>().Count(element => element.CanvasSurface is not null) ?? 0;
        if (document is not null && canvasCommands > 0)
        {
            using var rendered = HeadlessRenderer.Render(document, new RenderRequest(1280, 720));
            frameStore.Publish(rendered.Pixels);
        }
        await session.SendAsync(ContentMessageKind.Frame, new
        {
            sequence = message.Sequence,
            value = result.ToString(),
            displayItems = frame?.Items.Count ?? 0,
            canvasCommands,
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
