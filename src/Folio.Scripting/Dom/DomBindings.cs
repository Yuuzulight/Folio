using Folio;
using Folio.Dom;
using Folio.Resources;
using System.Runtime.CompilerServices;
using Folio.Scripting.Gpu;

namespace Folio.Scripting.Dom;

/// <summary>
/// Installs DOM constructors, prototype chains, and the global document onto an <see cref="IScriptEngine"/> (study 02, study 17, #451).
/// </summary>
public static class DomBindings
{
    private static readonly ConditionalWeakTable<IScriptEngine, DomContext> Contexts = new();
    private static readonly ConditionalWeakTable<IScriptEngine, FactoryHolder> GpuFactories = new();

    private sealed class FactoryHolder(Func<long, GpuDeviceResult> factory)
    {
        internal Func<long, GpuDeviceResult> Factory { get; } = factory;
    }

    /// <summary>Configures a trusted GPU factory before document installation; never exposed to page script.</summary>
    internal static bool ConfigureGpuFactory(IScriptEngine engine, Func<long, GpuDeviceResult> factory)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(factory);
        if (Contexts.TryGetValue(engine, out _)) return false;
        GpuFactories.Remove(engine);
        GpuFactories.Add(engine, new FactoryHolder(factory));
        return true;
    }


    /// <summary>Changes host-granted capabilities for an installed document and applies WebGL loss/restore semantics.</summary>
    public static bool UpdateCapabilities(IScriptEngine engine, FeatureCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (!Contexts.TryGetValue(engine, out var context)) return false;
        context.UpdateCapabilities(capabilities);
        return true;
    }

    /// <summary>Trusted host hook used by conformance probes; this is intentionally not installed on the page global.</summary>
    internal static bool TryClearNativeCanvas(IScriptEngine engine, ElementNode element, float red, float green, float blue, float alpha, out GpuFailure? failure)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(element);
        if (!Contexts.TryGetValue(engine, out var context))
        {
            failure = new(GpuFailureKind.Unavailable, "No document GPU context is installed.");
            return false;
        }
        return context.TryClearNativeCanvas(element, red, green, blue, alpha, out failure);
    }

    /// <summary>Trusted diagnostics for native GPU conformance probes; no page-facing API.</summary>
    internal static bool TryGetGpuMetrics(IScriptEngine engine, out long reservedBytes, out long peakReservedBytes, out string renderer)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (!Contexts.TryGetValue(engine, out var context) || context.Gpu is not { } gpu)
        {
            reservedBytes = peakReservedBytes = 0;
            renderer = "";
            return false;
        }
        reservedBytes = gpu.ReservedBytes;
        peakReservedBytes = gpu.PeakReservedBytes;
        renderer = gpu.RendererIdentity;
        return true;
    }

    /// <summary>Stops background work owned by the document before its content process closes.</summary>
    internal static void PumpIframes(IScriptEngine engine)
    {
        if (Contexts.TryGetValue(engine, out var context)) context.PumpIframes();
    }

    internal static Folio.Imaging.IImageHandle? GetImageOf(IScriptEngine engine, ElementNode element)
    {
        return Contexts.TryGetValue(engine, out var context) ? context.ImageOf(element) : null;
    }

    public static bool Dispose(IScriptEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        MessagePortEndpoint.DisposeAll(engine);
        if (!Contexts.TryGetValue(engine, out var context)) return false;
        Contexts.Remove(engine);
        GpuFactories.Remove(engine);
        RetireFeatures(context);
        return true;
    }

    private static void RetireFeatures(DomContext context)
    {
        context.ScriptFeaturesDisposed = true;
        context.Document.SetFeatureAvailability(false, false, false, false);
        context.Document.EventDispatcher = null;
        Action[] retire =
        [
            () => context.DisposeCacheStorage?.Invoke(), context.LoseCanvasContexts, context.DisposeIframes,
            context.DisposeWorkers, context.DisposeMedia,
            () => context.CaptureState?.Dispose(), () => context.WebRtcState?.Dispose(),
            () => context.ServiceWorkerState?.Dispose(), context.DisposeGpu
        ];
        foreach (var cleanup in retire)
        {
            try { cleanup(); }
            catch (Exception error)
            {
                try { context.OwnerEventLoop?.ReportException(error); } catch { }
            }
        }
    }

    public static ScriptWrapperCache Install(IScriptEngine engine, Document document, ScriptWrapperCache? wrapperCache = null, HtmlEventLoop? eventLoop = null) =>
        Install(engine, document.Node, wrapperCache, eventLoop, element =>
        {
            var bounds = document.GetElementBounds(element);
            return bounds is { } rect ? (rect.X, rect.Y, rect.Width, rect.Height) : null;
        }, document.Capabilities, document.MaxGpuBytes, document.MaxMediaBytes, document.MaxMediaDecoders, document.MaxWorkerMessageBytes, document.MaxWebAssemblyBytes, document.MaxWorkers, document.MaxWorkerMemoryBytes, document.MaxWorkerExecutionTime, document.MaxServiceWorkerCacheBytes, document.MaxIframes, document.MaxWebRtcConnections, document.WebRtcAllowedOrigins, document.MediaCapturePermission, document.ArtifactStorage, document.ArtifactId, document.Options.ResourceLoader, document.Options.BaseUri, document.Options.MediaAudioOutput, document.Options.MediaDecoder,
        document.Options.Raster, () => document.CanvasText, document.Options.MediaCaptureActivityChanged, document.Options.IframeNavigationRequested, document.Options);
    internal static ScriptWrapperCache Install(IScriptEngine engine, DocumentNode document, ScriptWrapperCache? wrapperCache = null,
        HtmlEventLoop? eventLoop = null, Func<ElementNode, (float X, float Y, float Width, float Height)?>? layout = null,
        FeatureCapabilities? capabilities = null, int maxGpuBytes = 64 * 1024 * 1024, int maxMediaBytes = 128 * 1024 * 1024, int maxMediaDecoders = 8, int maxWorkerMessageBytes = 1 * 1024 * 1024, int maxWebAssemblyBytes = 64 * 1024 * 1024, int maxWorkers = 8, long maxWorkerMemoryBytes = 64L * 1024 * 1024, TimeSpan maxWorkerExecutionTime = default, long maxServiceWorkerCacheBytes = 64L * 1024 * 1024, int maxIframes = 16, int maxWebRtcConnections = 8, IReadOnlySet<string>? webRtcAllowedOrigins = null, Func<MediaCaptureRequest, bool>? mediaCapturePermission = null, IArtifactStorage? artifactStorage = null, string artifactId = "default", IResourceLoader? resourceLoader = null, Uri? baseUri = null, IMediaAudioOutput? mediaAudioOutput = null, IMediaDecoder? mediaDecoder = null,
        Folio.Painting.IRasterBackend? raster = null, Func<Folio.Hosting.CanvasStyleEngine?>? canvasStyles = null, Action<bool>? mediaCaptureActivityChanged = null, Action<Uri>? iframeNavigationRequested = null, FolioOptions? parentOptions = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(document);
        if (Contexts.TryGetValue(engine, out var previousContext))
        {
            RetireFeatures(previousContext);
        }

        var gpuFactory = GpuFactories.TryGetValue(engine, out var configuredFactory) ? configuredFactory.Factory : null;
        var context = new DomContext(engine, document, wrapperCache, layout, capabilities, maxGpuBytes, maxMediaBytes, maxMediaDecoders, maxWorkerMessageBytes, maxWebAssemblyBytes, maxWorkers, maxWorkerMemoryBytes, maxWorkerExecutionTime, maxServiceWorkerCacheBytes, maxIframes, maxWebRtcConnections, webRtcAllowedOrigins, mediaCapturePermission, artifactStorage, artifactId, resourceLoader, baseUri, raster, canvasStyles, mediaCaptureActivityChanged, iframeNavigationRequested, mediaAudioOutput, mediaDecoder, gpuFactory);
        if (eventLoop is not null)
            eventLoop.MediaPlaybackTick = context.AdvanceMedia;
        context.ParentOptions = parentOptions;
        context.OwnerEventLoop = eventLoop;
        try
        {
        var blobSources = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var blobStore = new HostObjectTemplate("BlobStore")
            .DefineMethod("create", (_, _, args) =>
            {
                var source = args.Length > 0 ? args[0].AsString() : null;
                if (source is null) return ScriptValue.Undefined;
                var url = "blob:folio-" + Guid.NewGuid().ToString("N");
                blobSources[url] = source;
                return ScriptValue.FromString(url);
            })
            .DefineMethod("revoke", (_, _, args) => { if (args.Length > 0) blobSources.TryRemove(args[0].AsString() ?? "", out _); return ScriptValue.Undefined; });
        engine.SetGlobalProperty("__folio_blob_store", engine.CreateHostObject(blobStore, context));
        Contexts.Remove(engine);
        Contexts.Add(engine, context);
        context.InitializeMediaElements();
        context.InitializeIframeElements();

        // 1. Install DOMException
        InstallDomException(engine);

        // 2. Install Prototype Hierarchy & Constructors
        InstallConstructors(engine);

        // 2b. Events: EventTarget joins the Node prototype chain, so it follows the constructors
        var (handlers, dispatchHostEvent) = DomEvents.Install(engine, eventLoop);

        // 3. Bind document to global scope
        var docWrapper = context.Wrap(document);
        engine.SetGlobalProperty("document", docWrapper);
        var styleHost = new HostObjectTemplate("StyleHost")
            .DefineMethod("getComputedStyle", (target, _, args) =>
            {
                if (args.Length == 0 || !context.Engine.TryGetHostTarget(args[0], out var node) || node is not ElementNode element)
                    throw WebIdlException.TypeError("getComputedStyle requires an Element.");
                return context.WrapComputedStyle(element);
            });
        engine.SetGlobalProperty("__folio_style_host", engine.CreateHostObject(styleHost, context));
        var mutationHost = new HostObjectTemplate("MutationObserverHost")
            .DefineMethod("create", (_, _, args) =>
            {
                if (args.Length < 2 || !args[0].IsFunction) throw WebIdlException.TypeError("MutationObserver callback must be callable.");
                var observer = context.MutationObservers.Create(args[0], args[1]);
                return engine.CreateHostObject(context.Templates.MutationObserverTemplate, observer);
            })
            .DefineMethod("notify", (_, _, _) => { context.MutationObservers.Notify(); return ScriptValue.Undefined; });
        engine.SetGlobalProperty("__folio_mutation_host", engine.CreateHostObject(mutationHost, context));
        var canvasHost = new HostObjectTemplate("CanvasHost")
            .DefineMethod("createPath", (_, _, args) => engine.CreateHostObject(context.Templates.Canvas.Path2D, context.Templates.Canvas.NewPath2D(args)))
            .DefineMethod("imageData", (_, _, args) => context.Templates.Canvas.ConstructImageData(args));
        engine.SetGlobalProperty("__folio_canvas_host", engine.CreateHostObject(canvasHost, context));
        var messagePortTemplate = MessagePortBindings.Install(engine, context.Templates.MessageEventTemplate,
            eventLoop, context.MaxWorkerMessageBytes, context.MaxWorkerMemoryBytes);
        WorkerBindings.Install(engine, context, context.Templates.MessageEventTemplate, messagePortTemplate,
            resourceLoader, baseUri, blobSources, eventLoop);
        UrlBindings.Install(engine);
        PlatformBindings.Install(engine, context.ReportConsole);
        engine.Evaluate("globalThis.getComputedStyle = (function(host) { return function(element, pseudo) { return host.getComputedStyle(element, pseudo); }; })(__folio_style_host); delete globalThis.__folio_style_host;");
        engine.EvaluateHostScript(@"
            if (typeof window === 'undefined') {
                globalThis.window = globalThis;
            }
            if (typeof parent === 'undefined') {
                globalThis.parent = globalThis;
            }
            if (typeof top === 'undefined') {
                globalThis.top = globalThis;
            }
            if (typeof self === 'undefined') {
                globalThis.self = globalThis;
            }
            if (typeof globalThis.postMessage !== 'function') {
                globalThis.postMessage = function(data, targetOrigin, transfer) {
                    var origin = (globalThis.location && globalThis.location.origin) || 'null';
                    if (targetOrigin && targetOrigin !== '*' && targetOrigin !== origin) return;
                    var event = new MessageEvent('message', {
                        data: data,
                        origin: origin,
                        source: globalThis,
                        ports: transfer && Array.isArray(transfer) ? transfer : []
                    });
                    globalThis.dispatchEvent(event);
                };
            }
            if (typeof globalThis.Image === 'undefined') {
                globalThis.Image = function(width, height) {
                    var img = document.createElement('img');
                    if (width !== undefined) img.width = width;
                    if (height !== undefined) img.height = height;
                    return img;
                };
            }
            globalThis.navigator = globalThis.navigator || {};
            globalThis.MediaStream = function(tracks) { this.id = 'stream-' + Math.random().toString(16).slice(2); this._tracks = tracks && typeof tracks.getTracks === 'function' ? tracks.getTracks() : (tracks ? tracks.slice() : []); this.onaddtrack = null; this.onremovetrack = null; this._listeners = {}; this.addEventListener = function(type, handler) { if (typeof handler !== 'function') return; var list = this._listeners[type] || (this._listeners[type] = []); if (list.indexOf(handler) < 0) list.push(handler); }; this.removeEventListener = function(type, handler) { var list = this._listeners[type] || []; this._listeners[type] = list.filter(function(item) { return item !== handler; }); }; this._dispatch = function(event) { var handler = event.type === 'addtrack' ? this.onaddtrack : this.onremovetrack; if (handler) handler(event); (this._listeners[event.type] || []).slice().forEach(function(listener) { listener(event); }); }; };
            Object.defineProperty(MediaStream.prototype, 'active', { get: function() { return this._tracks.some(function(track) { return track.readyState === 'live'; }); } });
            MediaStream.prototype.getTracks = function() { return this._tracks.slice(); };
            MediaStream.prototype.getTrackById = function(id) { id = String(id); for (var i = 0; i < this._tracks.length; i++) if (this._tracks[i].id === id) return this._tracks[i]; return null; };
            MediaStream.prototype.getAudioTracks = function() { return this._tracks.filter(function(track) { return track.kind === 'audio'; }); };
            MediaStream.prototype.getVideoTracks = function() { return this._tracks.filter(function(track) { return track.kind === 'video'; }); };
            MediaStream.prototype.addTrack = function(track) { if (this._tracks.indexOf(track) < 0) { this._tracks.push(track); this._dispatch({ type: 'addtrack', target: this, track: track }); } };
            MediaStream.prototype.removeTrack = function(track) { var i = this._tracks.indexOf(track); if (i >= 0) { this._tracks.splice(i, 1); this._dispatch({ type: 'removetrack', target: this, track: track }); } };
            globalThis.innerWidth = 800;
            globalThis.innerHeight = 600;
            globalThis.devicePixelRatio = 1;
            globalThis.scrollX = 0;
            globalThis.scrollY = 0;
            Object.defineProperty(globalThis, 'pageXOffset', { get: function() { return globalThis.scrollX; } });
            Object.defineProperty(globalThis, 'pageYOffset', { get: function() { return globalThis.scrollY; } });
            globalThis.scrollTo = function(x, y) { globalThis.scrollX = Number(x) || 0; globalThis.scrollY = Number(y) || 0; };
            globalThis.scrollBy = function(x, y) { globalThis.scrollX += Number(x) || 0; globalThis.scrollY += Number(y) || 0; };
            globalThis.matchMedia = function(query) {
                query = String(query);
                const min = /min-width\s*:\s*(\d+)px/.exec(query);
                const max = /max-width\s*:\s*(\d+)px/.exec(query);
                const matches = (!min || globalThis.innerWidth >= Number(min[1])) && (!max || globalThis.innerWidth <= Number(max[1]));
                const listeners = [];
                return {
                    media: query,
                    matches: matches,
                    onchange: null,
                    addListener: function(fn) { if (typeof fn === 'function') listeners.push(fn); },
                    removeListener: function(fn) { const i = listeners.indexOf(fn); if (i >= 0) listeners.splice(i, 1); },
                    addEventListener: function(type, fn) { if (type === 'change') this.addListener(fn); },
                    removeEventListener: function(type, fn) { if (type === 'change') this.removeListener(fn); },
                    dispatchEvent: function() { return true; }
                };
            };
            globalThis.ResizeObserver = function(callback) {
                if (typeof callback !== 'function') throw new TypeError('ResizeObserver callback must be callable');
                this._callback = callback;
                this._targets = [];
            };
            ResizeObserver.prototype.observe = function(target) { if (this._targets.indexOf(target) < 0) this._targets.push(target); };
            ResizeObserver.prototype.unobserve = function(target) { const i = this._targets.indexOf(target); if (i >= 0) this._targets.splice(i, 1); };
            ResizeObserver.prototype.disconnect = function() { this._targets.length = 0; };
            globalThis.IntersectionObserver = function(callback, options) {
                if (typeof callback !== 'function') throw new TypeError('IntersectionObserver callback must be callable');
                this._callback = callback;
                this.root = options && options.root || null;
                this.rootMargin = options && options.rootMargin || '0px';
                this.thresholds = options && options.threshold !== undefined ? [].concat(options.threshold) : [0];
                this._targets = [];
            };
            IntersectionObserver.prototype.observe = function(target) { if (this._targets.indexOf(target) < 0) this._targets.push(target); };
            IntersectionObserver.prototype.unobserve = function(target) { const i = this._targets.indexOf(target); if (i >= 0) this._targets.splice(i, 1); };
            IntersectionObserver.prototype.disconnect = function() { this._targets.length = 0; };
            IntersectionObserver.prototype.takeRecords = function() { return []; };
            {
                const resolved = Promise.resolve();
                const then = Promise.prototype.then;
                // The notification is a promise job, so it runs in order with the script's own promise callbacks.
                globalThis.__folio_mo_queue = function() { then.call(resolved, function() { __folio_mutation_host.notify(); }); };
                const flag = function(options, name) { return options[name] === undefined ? undefined : !!options[name]; };
                const bad = function(why) { throw new TypeError(""Failed to execute 'observe' on 'MutationObserver': "" + why); };
                globalThis.MutationObserver = class MutationObserver {
                    #host;
                    constructor(callback) {
                        if (typeof callback !== 'function') throw new TypeError(""Failed to construct 'MutationObserver': The callback provided as parameter 1 is not a function."");
                        this.#host = __folio_mutation_host.create(callback, this);
                    }
                    observe(target, options) {
                        if (options === undefined || options === null) options = {};
                        else if (typeof options !== 'object' && typeof options !== 'function') bad('The provided value is not of type MutationObserverInit.');
                        let attributes = flag(options, 'attributes'), characterData = flag(options, 'characterData');
                        const attributeOldValue = flag(options, 'attributeOldValue'), characterDataOldValue = flag(options, 'characterDataOldValue');
                        const filter = options.attributeFilter === undefined ? undefined : Array.from(options.attributeFilter, String);
                        if ((attributeOldValue !== undefined || filter !== undefined) && attributes === undefined) attributes = true;
                        if (characterDataOldValue !== undefined && characterData === undefined) characterData = true;
                        const childList = !!options.childList, subtree = !!options.subtree;
                        if (!childList && !attributes && !characterData) bad('The options object must set at least one of ""attributes"", ""characterData"", or ""childList"" to true.');
                        if (attributeOldValue && !attributes) bad('The options object may only set ""attributeOldValue"" to true when ""attributes"" is true or not present.');
                        if (filter !== undefined && !attributes) bad('The options object may only set ""attributeFilter"" when ""attributes"" is true or not present.');
                        if (characterDataOldValue && !characterData) bad('The options object may only set ""characterDataOldValue"" to true when ""characterData"" is true or not present.');
                        this.#host.observe(target, childList, subtree, !!attributes, !!attributeOldValue, !!characterData, !!characterDataOldValue, filter === undefined ? null : filter);
                    }
                    disconnect() { this.#host.disconnect(); }
                    takeRecords() { return this.#host.takeRecords(); }
                };
            }
            globalThis.Path2D = function(path) { return arguments.length ? __folio_canvas_host.createPath(path) : __folio_canvas_host.createPath(); };
            globalThis.ImageData = function(a, b, c) { return __folio_canvas_host.imageData(a, b, c); };
            globalThis.Audio = function(src) { const element = document.createElement('audio'); if (src !== undefined) element.src = String(src); return element; };
            globalThis.FormData = function(form) {
                this._entries = [];
                if (form && form.elements) for (const control of form.elements) if (control.name) this.append(control.name, control.value || '');
            };
            FormData.prototype.append = function(name, value) { this._entries.push([String(name), value]); };
            FormData.prototype.set = function(name, value) { this.delete(name); this.append(name, value); };
            FormData.prototype.get = function(name) { const e = this._entries.find(x => x[0] === String(name)); return e ? e[1] : null; };
            FormData.prototype.getAll = function(name) { return this._entries.filter(x => x[0] === String(name)).map(x => x[1]); };
            FormData.prototype.has = function(name) { return this._entries.some(x => x[0] === String(name)); };
            FormData.prototype.delete = function(name) { name = String(name); this._entries = this._entries.filter(x => x[0] !== name); };
            FormData.prototype.entries = function() { return this._entries[Symbol.iterator](); };
            FormData.prototype.keys = function() { return this._entries.map(x => x[0])[Symbol.iterator](); };
            FormData.prototype.values = function() { return this._entries.map(x => x[1])[Symbol.iterator](); };
            function makeStorage() {
                const data = Object.create(null);
                const storage = {
                    get length() { return Object.keys(data).length; },
                    key: function(index) { return Object.keys(data)[index] ?? null; },
                    getItem: function(key) { key = String(key); return Object.prototype.hasOwnProperty.call(data, key) ? data[key] : null; },
                    setItem: function(key, value) { key = String(key); value = String(value); let size = 0; for (const k of Object.keys(data)) size += k.length + data[k].length; size += key.length + value.length; if (size > 5 * 1024 * 1024) throw new DOMException('Storage quota exceeded', 'QuotaExceededError'); data[key] = value; },
                    removeItem: function(key) { delete data[String(key)]; },
                    clear: function() { for (const key of Object.keys(data)) delete data[key]; }
                };
                return new Proxy(storage, { get: function(target, key) { if (key in target) return target[key]; return target.getItem(key); }, set: function(target, key, value) { target.setItem(key, value); return true; }, deleteProperty: function(target, key) { target.removeItem(key); return true; }, ownKeys: function() { return Object.keys(data); } });
            }
            globalThis.localStorage = makeStorage();
            globalThis.sessionStorage = makeStorage();
            globalThis.Blob = function(parts, options) { this._text = Array.from(parts || []).map(String).join(''); this.type = options && options.type ? String(options.type).toLowerCase() : ''; this.size = this._text.length; };
            Blob.prototype.text = function() { return Promise.resolve(this._text); };
            Blob.prototype.arrayBuffer = function() { const bytes = new Uint8Array(this._text.length); for (let i = 0; i < this._text.length; i++) bytes[i] = this._text.charCodeAt(i) & 255; return Promise.resolve(bytes.buffer); };
            URL.createObjectURL = function(blob) { return __folio_blob_store.create(blob && blob._text); };
            URL.revokeObjectURL = function(url) { __folio_blob_store.revoke(url); };
            globalThis.Option = function(text, value, defaultSelected, selected) { const option = document.createElement('option'); option.text = text === undefined ? '' : String(text); if (value !== undefined) option.value = String(value); if (defaultSelected) option.defaultSelected = true; if (selected) option.selected = true; return option; };
        ", "folio:dom-globals");
        context.CaptureState = CaptureBindingState.Install(engine, context);
        context.WebRtcState = WebRtcBindingState.Install(engine, context);
        context.ServiceWorkerState = ServiceWorkerBindings.Install(engine, context, resourceLoader, baseUri, context.ArtifactStorage);
        WebAssemblyBindings.Install(engine, context);
        CssBindings.Install(engine);
        CssomBindings.Install(engine, context);
        var cacheFetchApi = FetchBindings.Install(engine, context);
        CacheBindings.Install(engine, context, cacheFetchApi);

        // 4. Event handler content attributes (#457): the parsed ones now, later ones as they change or are inserted.
        handlers.Scan(document, context);
        document.HandlerAttributeChanged += (element, name, value) => handlers.Changed(context.Wrap(element), name, value);
        document.NodeInserted += node => handlers.Scan(node, context);

        // 5. Trusted event dispatcher bridge (#458)
        document.EventDispatcher = (targetNode, eventType, args) =>
        {
            var targetWrapper = targetNode == document ? docWrapper : context.Wrap(targetNode);
            var jsArgs = new ScriptValue[]
            {
                ScriptValue.FromBoolean(args.Bubbles),            // 0
                ScriptValue.FromBoolean(args.Cancelable),         // 1
                ScriptValue.FromBoolean(args.Composed),           // 2
                ScriptValue.FromNumber(args.Detail),              // 3
                ScriptValue.FromNumber(args.ScreenX),             // 4
                ScriptValue.FromNumber(args.ScreenY),             // 5
                ScriptValue.FromNumber(args.ClientX),             // 6
                ScriptValue.FromNumber(args.ClientY),             // 7
                ScriptValue.FromBoolean(args.CtrlKey),            // 8
                ScriptValue.FromBoolean(args.ShiftKey),           // 9
                ScriptValue.FromBoolean(args.AltKey),             // 10
                ScriptValue.FromBoolean(args.MetaKey),            // 11
                ScriptValue.FromNumber(args.Button),              // 12
                ScriptValue.FromNumber(args.Buttons),             // 13
                args.RelatedTarget is not null ? (args.RelatedTarget == document ? docWrapper : context.Wrap(args.RelatedTarget)) : ScriptValue.Null, // 14
                ScriptValue.FromNumber(args.PointerId),           // 15
                ScriptValue.FromNumber(args.Width),               // 16
                ScriptValue.FromNumber(args.Height),              // 17
                ScriptValue.FromNumber(args.Pressure),            // 18
                ScriptValue.FromNumber(args.TangentialPressure),  // 19
                ScriptValue.FromNumber(args.TiltX),               // 20
                ScriptValue.FromNumber(args.TiltY),               // 21
                ScriptValue.FromNumber(args.Twist),               // 22
                ScriptValue.FromString(args.PointerType),         // 23
                ScriptValue.FromBoolean(args.IsPrimary),          // 24
                ScriptValue.FromNumber(args.DeltaX),              // 25
                ScriptValue.FromNumber(args.DeltaY),              // 26
                ScriptValue.FromNumber(args.DeltaZ),              // 27
                ScriptValue.FromNumber(args.DeltaMode),           // 28
                ScriptValue.FromString(args.Key),                 // 29
                ScriptValue.FromString(args.Code),                // 30
                ScriptValue.FromNumber(args.Location),            // 31
                ScriptValue.FromBoolean(args.Repeat),             // 32
                ScriptValue.FromBoolean(args.IsComposing),        // 33
                args.Data is not null ? ScriptValue.FromString(args.Data) : ScriptValue.Null, // 34
                ScriptValue.FromString(args.InputType)            // 35
            };

            var argsArray = engine.CreateArray(jsArgs);
            var result = engine.Call(dispatchHostEvent, ScriptValue.Undefined, [targetWrapper, ScriptValue.FromString(eventType), argsArray]);
            return result.IsBoolean ? result.AsBoolean() : true;
        };

        return context.WrapperCache;
        }
        catch
        {
            RetireFeatures(context);
            Contexts.Remove(engine);
            throw;
        }
    }

    internal static void InstallDomException(IScriptEngine engine)
    {
        engine.EvaluateHostScript(@"
            (function() {
                if (typeof globalThis.DOMException === 'function') return;

                const codeMap = {
                    IndexSizeError: 1,
                    DOMStringSizeError: 2,
                    HierarchyRequestError: 3,
                    WrongDocumentError: 4,
                    InvalidCharacterError: 5,
                    NoDataAllowedError: 6,
                    NoModificationAllowedError: 7,
                    NotFoundError: 8,
                    NotSupportedError: 9,
                    InUseAttributeError: 10,
                    InvalidStateError: 11,
                    SyntaxError: 12,
                    InvalidModificationError: 13,
                    NamespaceError: 14,
                    InvalidAccessError: 15,
                    SecurityError: 18,
                    NetworkError: 19,
                    AbortError: 20,
                    TimeoutError: 23,
                    QuotaExceededError: 22,
                    DataCloneError: 25
                };

                class DOMException extends Error {
                    constructor(message = '', name = 'Error') {
                        super(message);
                        this.name = name;
                        this.message = message;
                        this.code = codeMap[name] || 0;
                    }
                }

                DOMException.INDEX_SIZE_ERR = 1;
                DOMException.DOMSTRING_SIZE_ERR = 2;
                DOMException.HIERARCHY_REQUEST_ERR = 3;
                DOMException.WRONG_DOCUMENT_ERR = 4;
                DOMException.INVALID_CHARACTER_ERR = 5;
                DOMException.NO_DATA_ALLOWED_ERR = 6;
                DOMException.NO_MODIFICATION_ALLOWED_ERR = 7;
                DOMException.NOT_FOUND_ERR = 8;
                DOMException.NOT_SUPPORTED_ERR = 9;
                DOMException.INUSE_ATTRIBUTE_ERR = 10;
                DOMException.INVALID_STATE_ERR = 11;
                DOMException.SYNTAX_ERR = 12;
                DOMException.INVALID_MODIFICATION_ERR = 13;
                DOMException.NAMESPACE_ERR = 14;
                DOMException.INVALID_ACCESS_ERR = 15;
                DOMException.SECURITY_ERR = 18;
                DOMException.NETWORK_ERR = 19;
                DOMException.ABORT_ERR = 20;
                DOMException.TIMEOUT_ERR = 23;
                DOMException.QUOTA_EXCEEDED_ERR = 22;

                globalThis.DOMException = DOMException;
            })();
        ", "folio:dom-exception");
    }

    private static void InstallConstructors(IScriptEngine engine)
    {
        engine.EvaluateHostScript(@"
            (function() {
                function defClass(name, parent) {
                    const ctor = function() {
                        throw new TypeError('Illegal constructor');
                    };
                    Object.defineProperty(ctor, 'name', { value: name, configurable: true });
                    if (parent) {
                        Object.setPrototypeOf(ctor, parent);
                        ctor.prototype = Object.create(parent.prototype, {
                            constructor: { value: ctor, writable: true, configurable: true }
                        });
                    } else {
                        ctor.prototype = Object.create(Object.prototype, {
                            constructor: { value: ctor, writable: true, configurable: true }
                        });
                    }
                    globalThis[name] = ctor;
                    return ctor;
                }

                const Node = defClass('Node');
                Node.ELEMENT_NODE = 1;
                Node.ATTRIBUTE_NODE = 2;
                Node.TEXT_NODE = 3;
                Node.CDATA_SECTION_NODE = 4;
                Node.ENTITY_REFERENCE_NODE = 5;
                Node.ENTITY_NODE = 6;
                Node.PROCESSING_INSTRUCTION_NODE = 7;
                Node.COMMENT_NODE = 8;
                Node.DOCUMENT_NODE = 9;
                Node.DOCUMENT_TYPE_NODE = 10;
                Node.DOCUMENT_FRAGMENT_NODE = 11;
                Node.NOTATION_NODE = 12;

                Node.DOCUMENT_POSITION_DISCONNECTED = 0x01;
                Node.DOCUMENT_POSITION_PRECEDING = 0x02;
                Node.DOCUMENT_POSITION_FOLLOWING = 0x04;
                Node.DOCUMENT_POSITION_CONTAINS = 0x08;
                Node.DOCUMENT_POSITION_CONTAINED_BY = 0x10;
                Node.DOCUMENT_POSITION_IMPLEMENTATION_SPECIFIC = 0x20;

                const Document = defClass('Document', Node);
                const DocumentType = defClass('DocumentType', Node);
                const DocumentFragment = defClass('DocumentFragment', Node);

                const CharacterData = defClass('CharacterData', Node);
                const Text = defClass('Text', CharacterData);
                const Comment = defClass('Comment', CharacterData);

                const Element = defClass('Element', Node);
                const SVGElement = defClass('SVGElement', Element);
                const HTMLElement = defClass('HTMLElement', Element);
                const HTMLMediaElement = defClass('HTMLMediaElement', HTMLElement);
                const HTMLAudioElement = defClass('HTMLAudioElement', HTMLMediaElement);
                const HTMLVideoElement = defClass('HTMLVideoElement', HTMLMediaElement);
                const HTMLIFrameElement = defClass('HTMLIFrameElement', HTMLElement);

                const Attr = defClass('Attr', Node);

                // Specific HTML element classes
                const htmlTags = [
                    'HTMLHtmlElement',
                    'HTMLHeadElement',
                    'HTMLBodyElement',
                    'HTMLTitleElement',
                    'HTMLDivElement',
                    'HTMLSpanElement',
                    'HTMLParagraphElement',
                    'HTMLAnchorElement',
                    'HTMLInputElement',
                    'HTMLButtonElement',
                    'HTMLImageElement',
                    'HTMLOptionElement',
                    'HTMLSelectElement',
                    'HTMLTextAreaElement',
                    'HTMLTemplateElement',
                    'HTMLScriptElement',
                    'HTMLUnknownElement'
                ];
                for (const tag of htmlTags) {
                    defClass(tag, HTMLElement);
                }

                // Collections
                const NodeList = defClass('NodeList');
                NodeList.prototype[Symbol.iterator] = Array.prototype.values;
                NodeList.prototype.values = Array.prototype.values;
                NodeList.prototype.keys = Array.prototype.keys;
                NodeList.prototype.entries = Array.prototype.entries;

                const HTMLCollection = defClass('HTMLCollection');
                HTMLCollection.prototype[Symbol.iterator] = Array.prototype.values;
                HTMLCollection.prototype.values = Array.prototype.values;

                const DOMTokenList = defClass('DOMTokenList');
                DOMTokenList.prototype[Symbol.iterator] = Array.prototype.values;
                DOMTokenList.prototype.values = Array.prototype.values;
                DOMTokenList.prototype.keys = Array.prototype.keys;
                DOMTokenList.prototype.entries = Array.prototype.entries;

                const DOMStringMap = defClass('DOMStringMap');

                const CSSStyleDeclaration = defClass('CSSStyleDeclaration');

                const NamedNodeMap = defClass('NamedNodeMap');
                NamedNodeMap.prototype[Symbol.iterator] = Array.prototype.values;
                NamedNodeMap.prototype.values = Array.prototype.values;

                function DOMMatrix(init) {
                    var v = [1, 0, 0, 1, 0, 0];
                    if (Array.isArray(init)) for (var i = 0; i < 6 && i < init.length; i++) v[i] = Number(init[i]);
                    else if (init && typeof init === 'object') {
                        var names = ['a','b','c','d','e','f'], aliases = ['m11','m12','m21','m22','m41','m42'];
                        for (var j = 0; j < 6; j++) { var x = init[names[j]]; if (x === undefined) x = init[aliases[j]]; if (x !== undefined) v[j] = Number(x); }
                    } else if (arguments.length >= 6) for (var k = 0; k < 6; k++) v[k] = Number(arguments[k]);
                    this.a=v[0]; this.b=v[1]; this.c=v[2]; this.d=v[3]; this.e=v[4]; this.f=v[5];
                    this.m11=this.a; this.m12=this.b; this.m13=0; this.m14=0; this.m21=this.c; this.m22=this.d; this.m23=0; this.m24=0;
                    this.m31=0; this.m32=0; this.m33=1; this.m34=0; this.m41=this.e; this.m42=this.f; this.m43=0; this.m44=1;
                    this.is2D=true; this.isIdentity=this.a===1&&this.b===0&&this.c===0&&this.d===1&&this.e===0&&this.f===0;
                }
                DOMMatrix.prototype._set = function(a,b,c,d,e,f) { this.a=a; this.b=b; this.c=c; this.d=d; this.e=e; this.f=f; this.m11=a; this.m12=b; this.m21=c; this.m22=d; this.m41=e; this.m42=f; this.isIdentity=a===1&&b===0&&c===0&&d===1&&e===0&&f===0; return this; };
                DOMMatrix.prototype.multiply = function(other) { other = new DOMMatrix(other); return new DOMMatrix([this.a*other.a+this.c*other.b, this.b*other.a+this.d*other.b, this.a*other.c+this.c*other.d, this.b*other.c+this.d*other.d, this.a*other.e+this.c*other.f+this.e, this.b*other.e+this.d*other.f+this.f]); };
                DOMMatrix.prototype.multiplySelf = function(other) { var m=this.multiply(other); return this._set(m.a,m.b,m.c,m.d,m.e,m.f); };
                DOMMatrix.prototype.translate = function(x,y) { return this.multiplySelf(new DOMMatrix([1,0,0,1,Number(x),Number(y)])); };
                DOMMatrix.prototype.scale = function(x,y) { x=Number(x); y=y===undefined?x:Number(y); return this.multiplySelf(new DOMMatrix([x,0,0,y,0,0])); };
                DOMMatrix.prototype.rotate = function(angle) { var r=Number(angle)*Math.PI/180, c=Math.cos(r), s=Math.sin(r); return this.multiplySelf(new DOMMatrix([c,s,-s,c,0,0])); };
                DOMMatrix.prototype.inverse = function() { var det=this.a*this.d-this.b*this.c; if (det===0) throw new DOMException('The matrix is not invertible.','InvalidStateError'); return new DOMMatrix([this.d/det,-this.b/det,-this.c/det,this.a/det,(this.c*this.f-this.d*this.e)/det,(this.b*this.e-this.a*this.f)/det]); };
                DOMMatrix.prototype.toString = function() { return 'matrix('+[this.a,this.b,this.c,this.d,this.e,this.f].join(', ')+')'; };
                globalThis.DOMMatrix = DOMMatrix;
                globalThis.DOMMatrixReadOnly = DOMMatrix;
            })();
        ", "folio:dom-constructors");
    }
}















