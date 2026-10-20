using System.Text.Json;
using Folio;
using Folio.Scripting;
using Folio.Scripting.Ipc;
using Folio.Scripting.Jint;
using Folio.Scripting.Dom;
using Folio.Dom;
using Folio.Html;
using Folio.Skia;
using Folio.Resources;

JintScriptEngine? engine = null;
HtmlEventLoop? eventLoop = null;
ScriptRunner? runner = null;
Document? document = null;
var taskDiagnostics = new List<string>();
var capabilities = FeatureCapabilities.Default;
var framePath = Path.Combine(Path.GetTempPath(), $"folio-frame-{Environment.ProcessId}.bin");
using var frameStore = new CanvasFrameStore(framePath, 1280, 720, 1280 * 4);
await using var session = new ContentHostSession(Console.OpenStandardInput(), Console.OpenStandardOutput());
var resourceLoader = new BrokerResourceLoader(session);
void DisposeDocument()
{
    runner?.Dispose();
    runner = null;
    engine?.Dispose();
    engine = null;
    eventLoop?.Dispose();
    eventLoop = null;
    document?.Dispose();
    document = null;
}
session.MessageReceived += async (message, cancellationToken) =>
{
    if (message.Kind == ContentMessageKind.Hello)
    {
        await session.SendAsync(ContentMessageKind.Hello, new { protocol = ContentMessage.CurrentVersion }, cancellationToken);
        return;
    }

    if (message.Kind == ContentMessageKind.Capabilities)
    {
        var accepted = message.TryGetCapabilities(out var updated);
        capabilities = accepted ? updated : FeatureCapabilities.Default;
        if (engine is not null) DomBindings.UpdateCapabilities(engine, capabilities);
        await session.SendAsync(ContentMessageKind.Capabilities, new { sequence = message.Sequence, capabilities, accepted }, cancellationToken);
        return;
    }

    if (message.Kind != ContentMessageKind.Load) return;
    var script = message.Payload.TryGetProperty("script", out var source) ? source.GetString() ?? "" : "";
    var url = message.Payload.TryGetProperty("url", out var location) ? location.GetString() ?? "content" : "content";
    try
    {
        var replacesDocument = message.Payload.TryGetProperty("html", out var htmlElement);
        // Grants belong to one artifact. Omitted grants on a new document must not
        // inherit the previous artifact; script-only observation keeps its grants.
        if (replacesDocument || message.Payload.EnumerateObject().Any(property => property.Name.Equals("capabilities", StringComparison.OrdinalIgnoreCase)))
        {
            capabilities = message.TryGetCapabilities(out var loadedCapabilities)
                ? loadedCapabilities : FeatureCapabilities.Default;
            if (!replacesDocument && engine is not null) DomBindings.UpdateCapabilities(engine, capabilities);
        }
        if (replacesDocument)
        {
            DisposeDocument();
            var options = new FolioOptions
            {
                Scripting = ScriptingPolicy.ContentProcess,
                Capabilities = capabilities,
                BaseUri = Uri.TryCreate(url, UriKind.Absolute, out var baseUri) ? baseUri : null,
                ResourceLoader = resourceLoader,
                ArtifactId = message.Payload.TryGetProperty("artifactId", out var artifact) ? artifact.GetString() ?? "default" : "default",
                ArtifactStorage = message.Payload.TryGetProperty("storageEnabled", out var storageEnabled) && storageEnabled.GetBoolean() ? new BrokerArtifactStorage(session) : null,
                Limits = message.Payload.TryGetProperty("limits", out var resourceLimits) ? resourceLimits.Deserialize<ResourceLimits>() ?? ResourceLimits.Default : ResourceLimits.Default,
                MediaCaptureActivityChanged = active => _ = session.SendAsync(ContentMessageKind.MediaCaptureActivity, new { active }, cancellationToken),
                Raster = new SkiaRasterBackend()
            };
            session.SetStorageLimit(options.Limits.MaxServiceWorkerCacheBytes);
            document = Document.Parse(htmlElement.GetString() ?? "", options);
            engine = new JintScriptEngine();
            eventLoop = new HtmlEventLoop(engine);
            taskDiagnostics.Clear();
            eventLoop.UncaughtException += error => taskDiagnostics.Add(error.GetType().Name + ": " + error.Message);
            DomBindings.Install(engine, document, eventLoop: eventLoop);
            ScriptGlobalsInstaller.Install(eventLoop, engine);
            runner = new ScriptRunner(document, engine, eventLoop, options);
            runner.Run();
        }
        engine ??= new JintScriptEngine();
        // Observe the document after runnable jobs, not a snapshot taken before
        // its final promise reactions. The requested script still runs once.
        eventLoop?.SpinUntilIdle();
        var result = engine.Evaluate(script, url);
        eventLoop?.SpinUntilIdle();
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
            diagnostics = runner?.Diagnostics.Select(diagnostic => diagnostic.Message).Concat(taskDiagnostics).ToArray() ?? taskDiagnostics.ToArray(),
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

try { await session.RunAsync(); }
finally { DisposeDocument(); }

sealed class BrokerResourceLoader(ContentHostSession session) : IResourceLoader
{
    public async Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken) =>
        await session.RequestResourceAsync(request, cancellationToken).ConfigureAwait(false);
}
