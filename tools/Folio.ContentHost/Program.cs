using System.Numerics;
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
CanvasFrameStore? frameStore = null;
var framePath = Path.Combine(Path.GetTempPath(), $"folio-frame-{Environment.ProcessId}.bin");
var viewportWidth = 1280;
var viewportHeight = 720;
await using var session = new ContentHostSession(Console.OpenStandardInput(), Console.OpenStandardOutput());

async ValueTask PublishFrame(long requestSequence, string? value, CancellationToken cancellationToken)
{
    var store = frameStore;
    if (document is not null && store is not null)
    {
        using var rendered = HeadlessRenderer.Render(document, new RenderRequest(viewportWidth, viewportHeight));
        store.Publish(rendered.Pixels);
    }
    await session.SendAsync(ContentMessageKind.Frame, new
    {
        requestSequence,
        value,
        documentRevision = requestSequence,
        viewportWidth,
        viewportHeight,
        // The host currently consumes the shared pixel frame. Keep this field
        // present for protocol consumers that also understand display lists.
        commands = Array.Empty<object>(),
        canvasFrame = store is null ? null : new { path = framePath, width = store.Width, height = store.Height, stride = store.Stride, sequence = store.Sequence }
    }, cancellationToken);
}

session.MessageReceived += async (message, cancellationToken) =>
{
    if (message.Kind == ContentMessageKind.Hello)
    {
        await session.SendAsync(ContentMessageKind.Hello, new { protocol = ContentMessage.CurrentVersion }, cancellationToken);
        return;
    }

    if (message.Kind == ContentMessageKind.Input && document is not null)
    {
        var input = System.Text.Json.JsonSerializer.Deserialize<ContentInputEvent>(message.Payload.GetRawText(),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (input is null) return;

        var position = new Vector2((float)input.X, (float)input.Y);
        var modifiers = (KeyModifiers)input.Modifiers;
        switch (input.Type)
        {
            case "pointermove":
                document.Input.HandlePointerMove(new PointerEvent(position, Buttons: (PointerButtons)input.Buttons, Modifiers: modifiers));
                break;
            case "pointerdown":
                document.Input.HandlePointerDown(new PointerEvent(position, (PointerButton)input.Button, (PointerButtons)input.Buttons, Modifiers: modifiers));
                break;
            case "pointerup":
                document.Input.HandlePointerUp(new PointerEvent(position, (PointerButton)input.Button, (PointerButtons)input.Buttons, Modifiers: modifiers));
                break;
            case "pointerleave":
                document.Input.HandlePointerLeave();
                break;
            case "wheel":
                document.Input.HandleWheel(new WheelEvent(position, new Vector2((float)input.DeltaX, (float)input.DeltaY), modifiers));
                break;
            case "keydown":
                document.Input.HandleKeyDown(new KeyEvent(input.Key ?? "", input.Code ?? "", modifiers));
                break;
            case "keyup":
                document.Input.HandleKeyUp(new KeyEvent(input.Key ?? "", input.Code ?? "", modifiers));
                break;
            case "textinput":
                document.Input.HandleTextInput(new TextInputEvent(input.Text ?? "", modifiers));
                break;
        }

        await PublishFrame(message.Sequence, null, cancellationToken);
        return;
    }

    if (message.Kind != ContentMessageKind.Load) return;
    var script = message.Payload.TryGetProperty("script", out var source) ? source.GetString() ?? "" : "";
    var url = message.Payload.TryGetProperty("url", out var location) ? location.GetString() ?? "content" : "content";
    try
    {
        viewportWidth = message.Payload.TryGetProperty("viewportWidth", out var width) && width.TryGetInt32(out var parsedWidth)
            ? Math.Clamp(parsedWidth, 1, 4096) : 1280;
        viewportHeight = message.Payload.TryGetProperty("viewportHeight", out var height) && height.TryGetInt32(out var parsedHeight)
            ? Math.Clamp(parsedHeight, 1, 4096) : 720;
        framePath = message.Payload.TryGetProperty("framePath", out var requestedPath) && requestedPath.GetString() is { Length: > 0 } path
            ? path : Path.Combine(Path.GetTempPath(), $"folio-frame-{Environment.ProcessId}.bin");
        frameStore?.Dispose();
        frameStore = new CanvasFrameStore(framePath, viewportWidth, viewportHeight, viewportWidth * 4);
        if (message.Payload.TryGetProperty("html", out var htmlElement))
        {
            document?.Dispose();
            document = Document.Parse(htmlElement.GetString() ?? "", new FolioOptions { Scripting = ScriptingPolicy.ContentProcess });
            DomBindings.Install(engine, document.Node);
        }
        var result = engine.Evaluate(script, url);
        await PublishFrame(message.Sequence, result.ToString(), cancellationToken);
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

try
{
    await session.RunAsync();
}
finally
{
    document?.Dispose();
    frameStore?.Dispose();
    try { File.Delete(framePath); } catch { }
}
