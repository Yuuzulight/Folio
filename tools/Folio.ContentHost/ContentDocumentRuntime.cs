using Folio;
using Folio.Dom;
using Folio.Html;
using Folio.Resources;
using Folio.Scripting;
using Folio.Scripting.Dom;
using Folio.Scripting.Gpu;
using Folio.Scripting.Ipc;
using Folio.Scripting.Jint;
using Folio.Skia;
using Folio.Painting;
using System.Numerics;
using System.Text.Json;

namespace Folio.ContentHost;

/// <summary>Shared document bootstrap used by the production content host and trusted process probes.</summary>
public sealed class ContentDocumentRuntime : IDisposable
{
    private readonly ContentHostSession _session;
    private readonly CanvasFrameStore _frameStore;
    private readonly IResourceLoader _resourceLoader;
    private readonly List<string> _taskDiagnostics = new();
    private readonly DisplayListContext _codecContext = new();
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

    /// <summary>Trusted host probe: clears a document canvas through the native GPU bridge and republishes the normal page frame.</summary>
    public ContentGpuProbeResult ClearNativeCanvas(string canvasId, float red, float green, float blue, float alpha)
    {
        if (_engine is null || _document is null)
            return new(false, "No document is loaded.", null, null, null, null, null, null, null, null, null, null);
        var canvas = _document.Node.Descendants.OfType<ElementNode>()
            .FirstOrDefault(element => element.LocalName.Equals("canvas", StringComparison.OrdinalIgnoreCase)
                && string.Equals(element.GetAttribute("id"), canvasId, StringComparison.Ordinal));
        if (canvas is null)
            return new(false, $"Canvas '{canvasId}' was not found.", null, null, null, null, null, null, null, null, null, null);
        var succeeded = DomBindings.TryClearNativeCanvas(_engine, canvas, red, green, blue, alpha, out var failure);
        PublishCurrentFrame();
        var memory = WindowsGpuMemory.Sample();
        var metricsAvailable = DomBindings.TryGetGpuMetrics(_engine, out var reserved, out var peak, out var renderer);
        var fallback = canvas.CanvasSurface as IFeatureFallbackSource;
        var fallbackFeature = fallback?.Fallback?.Feature.ToString();
        var fallbackReason = fallback?.Fallback?.Reason.ToString();
        return new(succeeded, failure?.Message, failure?.Kind.ToString(), metricsAvailable ? renderer : null,
            metricsAvailable ? reserved : null, metricsAvailable ? peak : null,
            memory?.LocalBytes, memory?.NonLocalBytes, memory?.Adapters, memory?.Source, fallbackFeature, fallbackReason);
    }

    public void PublishCurrentFrame()
    {
        if (_document is null) return;
        using var rendered = HeadlessRenderer.Render(_document, new RenderRequest(1280, 720));
        _frameStore.Publish(rendered.Pixels);
    }

    public bool ApplyCapabilities(ContentMessage message)
    {
        var accepted = message.TryGetCapabilities(out var updated);
        Capabilities = accepted ? updated : FeatureCapabilities.Default;
        if (_engine is not null) DomBindings.UpdateCapabilities(_engine, Capabilities);
        return accepted;
    }

    public Task<ContentDocumentResult> ExecuteAsync(ContentMessage message, CancellationToken cancellationToken)
    {
        if (message.Payload.TryGetProperty("crash", out var crashNode) && crashNode.ValueKind is JsonValueKind.True or JsonValueKind.String)
            Environment.FailFast("Crashing test hook triggered.");

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
            _document.Input.CursorChanged += (_, e) => _ = _session.SendAsync(ContentMessageKind.Cursor, new { cursor = e.Cursor }, cancellationToken);
            _document.Input.TooltipChanged += (_, e) => _ = _session.SendAsync(ContentMessageKind.Tooltip, new { tooltip = e.Tooltip }, cancellationToken);
            _document.Input.LinkActivated += (_, e) => _ = _session.SendAsync(ContentMessageKind.LinkActivated, new { uri = e.Uri.AbsoluteUri, modifiers = (int)e.Modifiers }, cancellationToken);
            _document.Input.CopyRequested += (_, _) =>
            {
                var text = _document.SelectedText;
                var html = _document.SelectedHtml;
                _ = _session.SendAsync(ContentMessageKind.ClipboardCopy, new { text, html }, cancellationToken);
            };
            _document.Input.FormSubmitted += (_, e) =>
            {
                _ = _session.SendAsync(ContentMessageKind.FormSubmitted, new
                {
                    action = e.Action,
                    method = e.Method,
                    submitter = e.Submitter?.Id,
                    data = e.Data.Select(kv => new { name = kv.Key, value = kv.Value }).ToArray()
                }, cancellationToken);
            };
            var scriptLimits = message.Payload.TryGetProperty("scriptLimits", out var slNode)
                ? slNode.Deserialize<ScriptLimits>() ?? ScriptLimits.Default
                : ScriptLimits.Default;
            _engine = new JintScriptEngine(new ScriptEngineOptions { Limits = scriptLimits });
            _eventLoop = new HtmlEventLoop(_engine);
            _taskDiagnostics.Clear();
            _eventLoop.UncaughtException += error => _taskDiagnostics.Add(error.GetType().Name + ": " + error.Message);
            if (message.Payload.TryGetProperty("missingNativeLibrary", out var missingNative)
                && missingNative.ValueKind == JsonValueKind.True)
            {
                var missingPath = Path.Combine(Path.GetTempPath(), $"folio-angle-missing-{Guid.NewGuid():N}.dll");
                DomBindings.ConfigureGpuFactory(_engine,
                    maxBytes => AngleGpuBackend.TryCreate(missingPath, maxBytes, GpuRendererPreference.HardwareThenWarp));
            }
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
        if (_document is not null)
        {
            PublishCurrentFrame();
        }
        string? encodedList = frame is not null ? Convert.ToBase64String(DisplayListCodec.Encode(frame, _codecContext)) : null;
        return Task.FromResult(new ContentDocumentResult(result.ToString(), frame?.Items.Count ?? 0, canvasCommands,
            _runner?.Diagnostics.Select(diagnostic => diagnostic.Message).Concat(_taskDiagnostics).ToArray() ?? _taskDiagnostics.ToArray(),
            encodedList));
    }

    public Task RouteInputAsync(ContentInputEvent input, long sequence, CancellationToken cancellationToken)
    {
        if (_document is null) return Task.CompletedTask;

        switch (input.Type.ToLowerInvariant())
        {
            case "pointermove":
                _document.Input.HandlePointerMove(new PointerEvent(new Vector2((float)input.X, (float)input.Y), PointerButton.None, (PointerButtons)input.Buttons, 0, (KeyModifiers)input.Modifiers));
                break;
            case "pointerdown":
                _document.Input.HandlePointerDown(new PointerEvent(new Vector2((float)input.X, (float)input.Y), (PointerButton)input.Button, (PointerButtons)input.Buttons, 1, (KeyModifiers)input.Modifiers));
                break;
            case "pointerup":
                _document.Input.HandlePointerUp(new PointerEvent(new Vector2((float)input.X, (float)input.Y), (PointerButton)input.Button, (PointerButtons)input.Buttons, 1, (KeyModifiers)input.Modifiers));
                break;
            case "click":
                if (!string.IsNullOrEmpty(input.Key) && _document.GetElementById(input.Key) is { } targetEl)
                {
                    targetEl.Focus();
                    targetEl.Click();
                    if (_engine is not null)
                    {
                        try { _engine.Evaluate($"(function(){{ var el = document.getElementById('{input.Key}'); if (el) el.click(); }})()"); }
                        catch { }
                    }
                }
                else
                {
                    var clickPos = new Vector2((float)input.X, (float)input.Y);
                    _document.Input.HandlePointerDown(new PointerEvent(clickPos, PointerButton.Primary, PointerButtons.Primary, 1, (KeyModifiers)input.Modifiers));
                    _document.Input.HandlePointerUp(new PointerEvent(clickPos, PointerButton.Primary, PointerButtons.None, 1, (KeyModifiers)input.Modifiers));
                }
                break;
            case "pointerleave":
                _document.Input.HandlePointerLeave();
                break;
            case "wheel":
                _document.Input.HandleWheel(new WheelEvent(new Vector2((float)input.X, (float)input.Y), new Vector2((float)input.DeltaX, (float)input.DeltaY), Modifiers: (KeyModifiers)input.Modifiers));
                break;
            case "keydown":
                _document.Input.HandleKeyDown(new KeyEvent(input.Key ?? "", input.Code ?? "", (KeyModifiers)input.Modifiers));
                break;
            case "keyup":
                _document.Input.HandleKeyUp(new KeyEvent(input.Key ?? "", input.Code ?? "", (KeyModifiers)input.Modifiers));
                break;
            case "textinput":
                _document.Input.HandleTextInput(new TextInputEvent(input.Text ?? ""));
                if (!string.IsNullOrEmpty(input.Key) && _document.GetElementById(input.Key) is { } textEl)
                {
                    textEl.Value += input.Text;
                    if (_engine is not null)
                    {
                        try { _engine.Evaluate($"(function(){{ var el = document.getElementById('{input.Key}'); if (el) {{ el.value += '{input.Text}'; el.dispatchEvent(new Event('input', {{ bubbles: true }})); }} }})()"); }
                        catch { }
                    }
                }
                break;
            case "focus":
                if (!string.IsNullOrEmpty(input.Key) && _document.GetElementById(input.Key) is { } fEl)
                {
                    fEl.Focus();
                }
                break;
            case "select" or "selectoption":
                var targetSelect = !string.IsNullOrEmpty(input.Key)
                    ? _document.GetElementById(input.Key)?.Node
                    : (_document.Input.FocusedElement?.Node is { LocalName: "select" } fs
                        ? fs
                        : _document.Node.Descendants.OfType<ElementNode>().FirstOrDefault(e => e.LocalName == "select"));
                if (targetSelect is not null)
                {
                    var options = targetSelect.Descendants.OfType<ElementNode>()
                        .Where(e => e.LocalName == "option" && !ElementNode.IsDisabledControl(e))
                        .ToList();
                    var pickIdx = Math.Clamp(input.Button, 0, Math.Max(0, options.Count - 1));
                    if (options.Count > 0)
                    {
                        for (var i = 0; i < options.Count; i++)
                            options[i].Selected = (i == pickIdx);
                        _document.Update();
                        var sid = targetSelect.GetAttribute("id");
                        if (!string.IsNullOrEmpty(sid) && _engine is not null)
                        {
                            try
                            {
                                _engine.Evaluate($"(function(){{ var s = document.getElementById('{sid}'); if (s) {{ s.selectedIndex = {pickIdx}; s.dispatchEvent(new Event('change', {{ bubbles: true }})); }} }})()");
                            }
                            catch { }
                        }
                    }
                }
                break;
        }

        _document.Update();
        _eventLoop?.SpinUntilIdle();
        PublishCurrentFrame();

        var activeSelect = _document.Input.FocusedElement?.Node is { LocalName: "select" } fSelect
            ? fSelect
            : (!string.IsNullOrEmpty(input.Key) && _document.GetElementById(input.Key)?.Node is { LocalName: "select" } kSelect ? kSelect : null);

        if (activeSelect is not null)
        {
            var options = activeSelect.Descendants.OfType<ElementNode>()
                .Where(e => e.LocalName == "option")
                .Select((opt, idx) =>
                {
                    var txt = (opt.TextContent ?? "").Trim();
                    return new { index = idx, value = opt.GetAttribute("value") ?? txt, text = txt, selected = opt.IsSelected, disabled = ElementNode.IsDisabledControl(opt) };
                })
                .ToArray();
            var selectedIdx = 0;
            foreach (var opt in options)
            {
                if (opt.selected) { selectedIdx = opt.index; break; }
            }
            _ = _session.SendAsync(ContentMessageKind.SelectDropdown, new
            {
                sequence,
                selectId = activeSelect.GetAttribute("id"),
                selectedIndex = selectedIdx,
                options
            }, cancellationToken);
        }

        var painted = _document.Paint(1280, 720);
        var encodedList = Convert.ToBase64String(DisplayListCodec.Encode(painted.List, _codecContext));
        _ = _session.SendAsync(ContentMessageKind.Frame, new
        {
            sequence,
            displayItems = painted.List.Items.Count,
            displayList = encodedList,
            canvasFrame = new { path = _frameStore.Path, width = _frameStore.Width, height = _frameStore.Height, stride = _frameStore.Stride, sequence = _frameStore.Sequence }
        }, cancellationToken);

        return Task.CompletedTask;
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

public sealed record ContentDocumentResult(string Value, int DisplayItems, int CanvasCommands, string[] Diagnostics, string? DisplayList = null);

public sealed record ContentGpuProbeResult(
    bool Succeeded,
    string? FailureMessage,
    string? FailureKind,
    string? RendererIdentity,
    long? ReservedBytes,
    long? PeakReservedBytes,
    long? NativeLocalBytes,
    long? NativeNonLocalBytes,
    int? NativeAdapterCount,
    string? NativeMemorySource,
    string? FallbackFeature,
    string? FallbackReason);
