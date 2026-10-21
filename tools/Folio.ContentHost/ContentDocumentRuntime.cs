using Folio;
using Folio.Dom;
using Folio.Html;
using Folio.Resources;
using Folio.Scripting;
using Folio.Scripting.Dom;
using Folio.Scripting.Ipc;
using Folio.Scripting.Jint;
using Folio.Skia;
using System.Text.Json;

namespace Folio.ContentHost;

/// <summary>Shared document bootstrap used by the production content host and trusted process probes.</summary>
public sealed class ContentDocumentRuntime : IDisposable
{
    private readonly ContentHostSession _session;
    private readonly CanvasFrameStore _frameStore;
    private readonly IResourceLoader _resourceLoader;
    private readonly List<string> _taskDiagnostics = new();
    private JintScriptEngine? _engine;
    private HtmlEventLoop? _eventLoop;
    private ScriptRunner? _runner;
    private Document? _document;

    public ContentDocumentRuntime(ContentHostSession session, CanvasFrameStore frameStore)
    {
        _session = session;
        _frameStore = frameStore;
        _resourceLoader = new BrokerResourceLoader(session);
    }

    public FeatureCapabilities Capabilities { get; private set; } = FeatureCapabilities.Default;
    public Document? Document => _document;
    public JintScriptEngine? Engine => _engine;

    public bool ApplyCapabilities(ContentMessage message)
    {
        var accepted = message.TryGetCapabilities(out var updated);
        Capabilities = accepted ? updated : FeatureCapabilities.Default;
        if (_engine is not null) DomBindings.UpdateCapabilities(_engine, Capabilities);
        return accepted;
    }

    public Task<ContentDocumentResult> ExecuteAsync(ContentMessage message, CancellationToken cancellationToken)
    {
        var script = message.Payload.TryGetProperty("script", out var source) ? source.GetString() ?? "" : "";
        var url = message.Payload.TryGetProperty("url", out var location) ? location.GetString() ?? "content" : "content";
        var replacesDocument = message.Payload.TryGetProperty("html", out var htmlElement);
        if (replacesDocument || message.Payload.EnumerateObject().Any(property => property.Name.Equals("capabilities", StringComparison.OrdinalIgnoreCase)))
        {
            Capabilities = message.TryGetCapabilities(out var loaded) ? loaded : FeatureCapabilities.Default;
            if (!replacesDocument && _engine is not null) DomBindings.UpdateCapabilities(_engine, Capabilities);
        }
        if (replacesDocument)
        {
            DisposeDocument();
            var options = new FolioOptions
            {
                Scripting = ScriptingPolicy.ContentProcess,
                Capabilities = Capabilities,
                BaseUri = Uri.TryCreate(url, UriKind.Absolute, out var baseUri) ? baseUri : null,
                ResourceLoader = _resourceLoader,
                ArtifactId = message.Payload.TryGetProperty("artifactId", out var artifact) ? artifact.GetString() ?? "default" : "default",
                ArtifactStorage = message.Payload.TryGetProperty("storageEnabled", out var storageEnabled) && storageEnabled.GetBoolean() ? new BrokerArtifactStorage(_session) : null,
                Limits = message.Payload.TryGetProperty("limits", out var limits) ? limits.Deserialize<ResourceLimits>() ?? ResourceLimits.Default : ResourceLimits.Default,
                MediaCaptureActivityChanged = active => _ = _session.SendAsync(ContentMessageKind.MediaCaptureActivity, new { active }, cancellationToken),
                Raster = new SkiaRasterBackend()
            };
            _session.SetStorageLimit(options.Limits.MaxServiceWorkerCacheBytes);
            _document = Document.Parse(htmlElement.GetString() ?? "", options);
            _engine = new JintScriptEngine();
            _eventLoop = new HtmlEventLoop(_engine);
            _taskDiagnostics.Clear();
            _eventLoop.UncaughtException += error => _taskDiagnostics.Add(error.GetType().Name + ": " + error.Message);
            DomBindings.Install(_engine, _document, eventLoop: _eventLoop);
            ScriptGlobalsInstaller.Install(_eventLoop, _engine);
            _runner = new ScriptRunner(_document, _engine, _eventLoop, options);
            _runner.Run();
        }
        _engine ??= new JintScriptEngine();
        _eventLoop?.SpinUntilIdle();
        var result = _engine.Evaluate(script, url);
        _eventLoop?.SpinUntilIdle();
        var frame = _document?.Paint(1280, 720).List;
        var canvasCommands = _document?.Node.Descendants.OfType<ElementNode>().Count(element => element.CanvasSurface is not null) ?? 0;
        if (_document is not null && canvasCommands > 0)
        {
            using var rendered = HeadlessRenderer.Render(_document, new RenderRequest(1280, 720));
            _frameStore.Publish(rendered.Pixels);
        }
        return Task.FromResult(new ContentDocumentResult(result.ToString(), frame?.Items.Count ?? 0, canvasCommands,
            _runner?.Diagnostics.Select(diagnostic => diagnostic.Message).Concat(_taskDiagnostics).ToArray() ?? _taskDiagnostics.ToArray()));
    }

    public void Dispose()
    {
        DisposeDocument();
    }

    private void DisposeDocument()
    {
        _runner?.Dispose(); _runner = null;
        _engine?.Dispose(); _engine = null;
        _eventLoop?.Dispose(); _eventLoop = null;
        _document?.Dispose(); _document = null;
    }

    private sealed class BrokerResourceLoader(ContentHostSession session) : IResourceLoader
    {
        public async Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken) =>
            await session.RequestResourceAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

public sealed record ContentDocumentResult(string Value, int DisplayItems, int CanvasCommands, string[] Diagnostics);
