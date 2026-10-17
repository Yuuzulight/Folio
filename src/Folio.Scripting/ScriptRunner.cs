using Folio.Dom;
using Folio.Diagrams;
using Folio.Resources;
using Folio.Scripting.Dom;

namespace Folio.Scripting;

/// <summary>
/// Orchestrates script element execution and document lifecycle state transitions (HTML §7.1.4, §8.1, issue #456).
/// Drives readyState changes ('loading' -> 'interactive' -> 'complete'), resource fetching via <see cref="IResourceLoader"/>,
/// script element execution order, dynamic script execution, and firing of readyStateChange, DOMContentLoaded, and load events.
/// </summary>
public sealed class ScriptRunner : IDisposable
{
    private readonly DocumentNode _document;
    private readonly IScriptEngine _engine;
    private readonly HtmlEventLoop _eventLoop;
    private readonly FolioOptions _options;
    private readonly List<Diagnostic> _diagnostics;
    private readonly Uri _baseUri;
    private readonly HashSet<ElementNode> _pendingScripts = new(ReferenceEqualityComparer.Instance);
    private bool _diagramStandInInstalled;
    private bool _disposed;

    public ScriptRunner(Document document, IScriptEngine engine, HtmlEventLoop eventLoop, FolioOptions? options = null, List<Diagnostic>? diagnostics = null)
        : this(document.Node, engine, eventLoop, options, diagnostics) { }

    internal ScriptRunner(
        DocumentNode document,
        IScriptEngine engine,
        HtmlEventLoop eventLoop,
        FolioOptions? options = null,
        List<Diagnostic>? diagnostics = null)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _eventLoop = eventLoop ?? throw new ArgumentNullException(nameof(eventLoop));
        _options = options ?? new FolioOptions();
        _diagnostics = diagnostics ?? [];
        _baseUri = _options.BaseUri ?? new Uri("about:blank");

        // Hook dynamic script insertions into connected document
        _document.NodeInserted += OnNodeInserted;
        _document.DiagnosticSink = _diagnostics.Add;
    }

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    /// <summary>
    /// Executes the script element document lifecycle for a parsed document.
    /// </summary>
    public void Run()
    {
        if (_options.Scripting == ScriptingPolicy.Disabled)
            return;

        InstallGlobals();

        // 1. Initial readyState is loading.
        _document.ReadyState = "loading";

        // 2. Document parsing finished -> transition to interactive and dispatch readystatechange.
        SetReadyState("interactive");

        // 3. Collect and partition parser scripts in document order.
        var parserScripts = CollectParserScripts();

        // Execute normal (inline and non-defer, non-async, non-module external) scripts in document order.
        foreach (var script in parserScripts.NormalScripts)
        {
            ExecuteScriptElement(script);
        }

        // Execute deferred scripts in document order.
        foreach (var script in parserScripts.DeferredScripts)
        {
            ExecuteScriptElement(script);
        }

        // Execute module scripts in document order.
        foreach (var script in parserScripts.ModuleScripts)
        {
            ExecuteScriptElement(script);
        }

        // 4. Fire DOMContentLoaded event on document (bubbles to window).
        DispatchDOMContentLoaded();

        // Async scripts do not hold the parser or DOMContentLoaded: each is a networking task that runs once the
        // document is parsed, in document order here since the host's loader answers at once, and the load event waits for them.
        foreach (var script in parserScripts.AsyncScripts)
        {
            var captured = script;
            _eventLoop.QueueTask(TaskSource.Networking, () => ExecuteScriptElement(captured), "async script");
        }

        // 5. Run any pending asynchronous or dynamic scripts that were queued in the event loop.
        _eventLoop.SpinUntilIdle();

        // 6. Transition readyState to complete and dispatch readystatechange.
        SetReadyState("complete");

        // 7. Fire load event on window (does not bubble).
        DispatchWindowLoad();

        // Spin event loop once more for any tasks scheduled during load.
        _eventLoop.SpinUntilIdle();
    }

    private void InstallGlobals()
    {
        // Setup window, self, globalThis aliases
        _engine.Evaluate(@"
            if (typeof window === 'undefined') {
                globalThis.window = globalThis;
            }
            if (typeof self === 'undefined') {
                globalThis.self = globalThis;
            }
        ");

        // Setup location
        var locationTemplate = new HostObjectTemplate("Location")
            .DefineProperty("href", (_, _) => ScriptValue.FromString(_baseUri.AbsoluteUri))
            .DefineProperty("origin", (_, _) => ScriptValue.FromString(GetOrigin(_baseUri)))
            .DefineProperty("protocol", (_, _) => ScriptValue.FromString(_baseUri.Scheme + ":"))
            .DefineProperty("host", (_, _) => ScriptValue.FromString(GetHostWithPort(_baseUri)))
            .DefineProperty("hostname", (_, _) => ScriptValue.FromString(_baseUri.DnsSafeHost))
            .DefineProperty("port", (_, _) => ScriptValue.FromString(_baseUri.IsDefaultPort || _baseUri.Port <= 0 ? "" : _baseUri.Port.ToString()))
            .DefineProperty("pathname", (_, _) => ScriptValue.FromString(_baseUri.AbsolutePath))
            .DefineProperty("search", (_, _) => ScriptValue.FromString(_baseUri.Query))
            .DefineProperty("hash", (_, _) => ScriptValue.FromString(_baseUri.Fragment))
            .DefineMethod("toString", (_, _, _) => ScriptValue.FromString(_baseUri.AbsoluteUri));

        var locationObj = _engine.CreateHostObject(locationTemplate, new object());
        _engine.SetGlobalProperty("location", locationObj);

        // Setup navigator
        var navigatorTemplate = new HostObjectTemplate("Navigator")
            .DefineProperty("userAgent", (_, _) => ScriptValue.FromString("Folio/1.0"))
            .DefineProperty("platform", (_, _) => ScriptValue.FromString("Win32"))
            .DefineProperty("language", (_, _) => ScriptValue.FromString("en-US"))
            .DefineProperty("languages", (_, _) => _engine.CreateArray([ScriptValue.FromString("en-US")]))
            .DefineProperty("onLine", (_, _) => ScriptValue.FromBoolean(true));

        var navigatorObj = _engine.CreateHostObject(navigatorTemplate, new object());
        _engine.SetGlobalProperty("navigator", navigatorObj);

    }

    private static string GetOrigin(Uri uri)
    {
        if (uri.IsAbsoluteUri && (uri.Scheme == "http" || uri.Scheme == "https"))
        {
            return uri.IsDefaultPort ? $"{uri.Scheme}://{uri.DnsSafeHost}" : $"{uri.Scheme}://{uri.DnsSafeHost}:{uri.Port}";
        }
        return "null";
    }

    private static string GetHostWithPort(Uri uri)
    {
        if (!uri.IsAbsoluteUri)
            return "";
        return uri.IsDefaultPort || uri.Port <= 0 ? uri.DnsSafeHost : $"{uri.DnsSafeHost}:{uri.Port}";
    }

    private void SetReadyState(string newState)
    {
        _document.ReadyState = newState;
        DispatchEventOnTarget("document", "readystatechange", bubbles: false, cancelable: false);
    }

    private void DispatchDOMContentLoaded()
    {
        // In WHATWG HTML: DOMContentLoaded bubbles from document up to window
        DispatchEventOnTarget("document", "DOMContentLoaded", bubbles: true, cancelable: false);
    }

    private void DispatchWindowLoad()
    {
        // In WHATWG HTML: load event on window does not bubble
        DispatchEventOnTarget("window", "load", bubbles: false, cancelable: false);
    }

    private void DispatchEventOnTarget(string targetExpression, string eventName, bool bubbles, bool cancelable)
    {
        var bubblesStr = bubbles ? "true" : "false";
        var cancelableStr = cancelable ? "true" : "false";
        try
        {
            _engine.Evaluate($$"""
                (function() {
                    const target = {{targetExpression}};
                    if (target && typeof target.dispatchEvent === 'function') {
                        target.dispatchEvent(new Event('{{eventName}}', { bubbles: {{bubblesStr}}, cancelable: {{cancelableStr}} }));
                    }
                })();
            """, $"folio:event:{eventName}");
        }
        catch (Exception ex)
        {
            RecordScriptError(ex, null);
        }
    }

    private sealed record ScriptBatches(
        List<ElementNode> NormalScripts,
        List<ElementNode> DeferredScripts,
        List<ElementNode> ModuleScripts,
        List<ElementNode> AsyncScripts
    );

    private ScriptBatches CollectParserScripts()
    {
        var normal = new List<ElementNode>();
        var deferred = new List<ElementNode>();
        var modules = new List<ElementNode>();
        var asyncScripts = new List<ElementNode>();

        for (Node? node = _document.FirstChild; node is not null; node = node.NextInTree(_document))
        {
            if (node is ElementNode script && script.Name.Namespace == Namespaces.Html && script.LocalName == "script")
            {
                if (!IsJavaScriptType(script))
                    continue;

                var isModule = IsModuleScript(script);
                var isAsync = script.GetAttribute("async") is not null;
                var isDefer = script.GetAttribute("defer") is not null;

                if (isModule)
                {
                    modules.Add(script);
                }
                else if (isDefer && !isAsync)
                {
                    deferred.Add(script);
                }
                else if (isAsync && !string.IsNullOrEmpty(script.GetAttribute("src")))
                {
                    asyncScripts.Add(script);
                }
                else
                {
                    // Classic script (inline, external, or async without defer)
                    normal.Add(script);
                }
            }
        }

        return new ScriptBatches(normal, deferred, modules, asyncScripts);
    }

    private static bool IsJavaScriptType(ElementNode script)
    {
        var type = script.GetAttribute("type")?.Trim();
        if (string.IsNullOrEmpty(type))
            return true;

        return type.Equals("text/javascript", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("application/javascript", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("text/ecmascript", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("application/ecmascript", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("module", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsModuleScript(ElementNode script)
    {
        var type = script.GetAttribute("type")?.Trim();
        return string.Equals(type, "module", StringComparison.OrdinalIgnoreCase);
    }

    private void ExecuteScriptElement(ElementNode script)
    {
        if (script.ScriptAlreadyStarted)
            return;

        script.ScriptAlreadyStarted = true;

        var isModule = IsModuleScript(script);
        var src = script.GetAttribute("src");

        string? scriptSource = null;
        string scriptUrl = _baseUri.AbsoluteUri;

        if (!string.IsNullOrEmpty(src))
        {
            // Resolve script URL against document base
            var resolved = ResourceLoader.Resolve(_baseUri.AbsoluteUri, src);
            if (resolved is null)
            {
                _diagnostics.Add(new Diagnostic(
                    DiagnosticCode.ResourceNotLoaded,
                    Severity.Warning,
                    $"Could not resolve script URL '{src}'.",
                    null,
                    "script"
                ));
                FireScriptEvent(script, "error");
                return;
            }

            scriptUrl = resolved;

            if (IsConfiguredDiagramLibrary(scriptUrl) && CanUseDiagramStandIn())
            {
                if (!_diagramStandInInstalled)
                {
                    DiagramBindings.Install(_engine);
                    _diagramStandInInstalled = true;
                }
                script.ScriptAlreadyStarted = true;
                _diagnostics.Add(new Diagnostic(DiagnosticCode.FeatureRouted, Severity.Info,
                    "Replaced the configured diagram library with Folio.Diagrams.", null, "diagrams"));
                FireScriptEvent(script, "load");
                return;
            }

            if (IsConfiguredDiagramLibrary(scriptUrl))
            {
                var unsupported = FindUnsupportedDiagramBlock();
                _diagnostics.Add(new Diagnostic(DiagnosticCode.FeatureRouted, Severity.Info,
                    unsupported is null
                        ? "Ran the configured diagram library because Folio.Diagrams could not be selected."
                        : $"Ran the configured diagram library because Folio.Diagrams does not support the diagram at source position {unsupported.Position}: {unsupported.Message}",
                    null, "diagrams"));
            }

            var integrity = script.GetAttribute("integrity");
            var request = new ResourceRequest(resolved, ResourceKind.Script, integrity);

            var response = LoadResource(request);
            if (!response.Succeeded || response.Data is null)
            {
                _diagnostics.Add(new Diagnostic(
                    DiagnosticCode.ResourceNotLoaded,
                    Severity.Warning,
                    $"Failed to load script '{resolved}': {response.Error ?? "Unknown error"}",
                    null,
                    "script"
                ));
                FireScriptEvent(script, "error");
                return;
            }

            // The host's loader may check too; the element's integrity is checked here whatever loader answered.
            if (!SubresourceIntegrity.Matches(response.Data, integrity))
            {
                _diagnostics.Add(new Diagnostic(
                    DiagnosticCode.ResourceNotLoaded,
                    Severity.Warning,
                    $"Script '{resolved}' does not match its integrity metadata and was not run.",
                    null,
                    "script"
                ));
                FireScriptEvent(script, "error");
                return;
            }

            if (response.Data.Length > 0)
                ExecuteSource(System.Text.Encoding.UTF8.GetString(response.Data), scriptUrl, isModule ? null : script);
            FireScriptEvent(script, "load");
            return;
        }

        scriptSource = script.TextContent ?? "";
        if (string.IsNullOrEmpty(scriptSource))
            return;

        // An inline script runs where it is and fires no load event.
        ExecuteSource(scriptSource, scriptUrl, isModule ? null : script);
    }

    private bool IsConfiguredDiagramLibrary(string url) =>
        _options.DiagramLibraryUrls.Contains(url);

    private bool CanUseDiagramStandIn()
    {
        var blocks = _document.Descendants.OfType<ElementNode>()
            .Where(HasDiagramBlock)
            .ToList();
        if (blocks.Count == 0)
            return false;
        return blocks.All(block => DiagramRenderer.TryRender(block.TextContent ?? "", out _));
    }

    private DiagramRenderer.DiagramDiagnostic? FindUnsupportedDiagramBlock()
    {
        foreach (var block in _document.Descendants.OfType<ElementNode>().Where(HasDiagramBlock))
        {
            if (!DiagramRenderer.TryRender(block.TextContent ?? "", out _, out var diagnostic))
                return diagnostic;
        }

        return null;
    }

    private static bool HasDiagramBlock(ElementNode element)
    {
        var classes = element.GetAttribute("class") ?? "";
        return classes.Split([' ', '\t', '\n', '\f', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Contains("mermaid", StringComparer.OrdinalIgnoreCase)
            || element.GetAttribute("data-diagram") is not null;
    }

    // https://html.spec.whatwg.org/multipage/scripting.html#execute-the-script-element: an external script fires load at its
    // element once it has run, or error when it could not be loaded; neither bubbles.
    private void FireScriptEvent(ElementNode script, string type)
    {
        try
        {
            _document.EventDispatcher?.Invoke(script, type, new EventDispatchArgs { Bubbles = false, Cancelable = false, Composed = false });
        }
        catch (Exception ex)
        {
            RecordScriptError(ex, null);
        }
    }

    private ResourceResponse LoadResource(ResourceRequest request)
    {
        var loader = new ResourceLoader(
            allowedFolders: null,
            maxBytes: ResourceLoader.MaxResourceBytes,
            host: _options.ResourceLoader
        );
        return loader.Load(request);
    }

    private void ExecuteSource(string source, string url, ElementNode? currentScript)
    {
        var previousScript = _document.CurrentScript;
        _document.CurrentScript = currentScript;

        try
        {
            _engine.Evaluate(source, url);
        }
        catch (Exception ex)
        {
            RecordScriptError(ex, url);
        }
        finally
        {
            _document.CurrentScript = previousScript;
        }
    }

    private void RecordScriptError(Exception ex, string? url)
    {
        SourceLocation? loc = null;
        var message = ex.Message;

        if (ex is ScriptRuntimeException rtEx)
        {
            if (rtEx.LineNumber > 0)
                loc = new SourceLocation(rtEx.LineNumber, rtEx.ColumnNumber > 0 ? rtEx.ColumnNumber : 1);
        }

        _diagnostics.Add(new Diagnostic(
            DiagnosticCode.ScriptError,
            Severity.Error,
            message,
            loc,
            "script"
        ));

        // Dispatch 'error' event on window
        try
        {
            var messageLiteral = System.Text.Json.JsonSerializer.Serialize(message);
            var filename = System.Text.Json.JsonSerializer.Serialize(url ?? "");
            var line = loc?.Line ?? 0;
            var column = loc?.Column ?? 0;
            _engine.Evaluate($$"""
                (function() {
                    if (typeof window !== 'undefined' && typeof window.dispatchEvent === 'function') {
                        window.dispatchEvent(new ErrorEvent('error', { message: {{messageLiteral}}, filename: {{filename}}, lineno: {{line}}, colno: {{column}}, cancelable: true }));
                    }
                })();
            """, "folio:script-error");
        }
        catch
        {
            // Ignore failure in error dispatch
        }
    }

    private void OnNodeInserted(Node node)
    {
        if (_options.Scripting == ScriptingPolicy.Disabled || _disposed)
            return;

        if (!node.IsConnected)
            return;

        if (node is ElementNode element)
        {
            CheckAndRunDynamicScript(element);
            foreach (var descendant in element.Descendants.OfType<ElementNode>())
            {
                CheckAndRunDynamicScript(descendant);
            }
        }
    }

    private void CheckAndRunDynamicScript(ElementNode element)
    {
        if (element.Name.Namespace == Namespaces.Html && element.LocalName == "script")
        {
            if (element.ScriptAlreadyStarted)
                return;

            if (!IsJavaScriptType(element))
                return;

            // An inline script runs at once; an external one is fetched and run as a task, after the inserting script
            // finishes (it is async by default), then fires load or error.
            if (string.IsNullOrEmpty(element.GetAttribute("src")))
            {
                ExecuteScriptElement(element);
                return;
            }
            _eventLoop.QueueTask(TaskSource.Networking, () => ExecuteScriptElement(element), "inserted script");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _document.NodeInserted -= OnNodeInserted;
        DomBindings.Dispose(_engine);
    }
}
