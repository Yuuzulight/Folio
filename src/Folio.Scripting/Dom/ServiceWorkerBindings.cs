namespace Folio.Scripting.Dom;

using System.Text;
using Folio.Resources;
using Folio.Scripting.Native;

/// <summary>
/// Host-owned authority for service-worker registration and retained worker handles.
/// The JavaScript compatibility bridge must consult this object at each privileged
/// operation; a captured grant is never sufficient after a revoke.
/// </summary>
internal sealed class ServiceWorkerCapabilityAuthority : IDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<ServiceWorkerCapabilityLease> _leases = [];
    private long _generation;
    private bool _enabled;
    private bool _exposed;
    private bool _disposed;
    private CancellationTokenSource _loads = new();
    private readonly Func<bool>? _livePermission;
    internal Action? Changed { get; set; }

    public ServiceWorkerCapabilityAuthority(bool enabled, Func<bool>? livePermission = null)
    {
        _enabled = enabled;
        _exposed = enabled;
        _livePermission = livePermission;
    }

    public bool Enabled
    {
        get
        {
            lock (_gate)
            {
                if (_disposed || !_enabled) return false;
                try { return _livePermission?.Invoke() ?? true; }
                catch { return false; }
            }
        }
    }
    internal bool Exposed { get { lock (_gate) return _exposed; } }
    internal CancellationToken LoadCancellation
    {
        get { lock (_gate) return Enabled ? _loads.Token : new CancellationToken(true); }
    }

    /// <summary>Acquires a generation-bound lease for one registration/worker authority.</summary>
    public bool TryAcquire(out ServiceWorkerCapabilityLease? lease)
    {
        lock (_gate)
        {
            if (!Enabled)
            {
                lease = null;
                return false;
            }

            lease = new ServiceWorkerCapabilityLease(this, _generation);
            _leases.Add(lease);
            return true;
        }
    }

    /// <summary>Changes host permission. Revocation retires every previously acquired lease.</summary>
    public void UpdateCapabilities(bool enabled)
    {
        CancellationTokenSource? retiredLoads = null;
        lock (_gate)
        {
            if (_disposed || _enabled == enabled) return;
            _enabled = enabled;
            _exposed |= enabled;
            retiredLoads = _loads;
            _loads = new CancellationTokenSource();
            _generation++;
            foreach (var lease in _leases) lease.Retire();
            _leases.Clear();
        }
        try { retiredLoads.Cancel(); } catch (AggregateException) { }
        finally { retiredLoads.Dispose(); }
        // Revocation is a security transition. A page callback is notification only and
        // must not be able to make the retired generation appear active again or abort
        // the host's capability update.
        try { Changed?.Invoke(); }
        catch { }
    }

    internal bool IsActive(ServiceWorkerCapabilityLease lease, long generation)
    {
        lock (_gate) return Enabled && generation == _generation && _leases.Contains(lease);
    }

    internal void Release(ServiceWorkerCapabilityLease lease)
    {
        lock (_gate) _leases.Remove(lease);
    }

    public void Dispose()
    {
        CancellationTokenSource retiredLoads;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _enabled = false;
            retiredLoads = _loads;
            _generation++;
            foreach (var lease in _leases) lease.Retire();
            _leases.Clear();
        }
        try { retiredLoads.Cancel(); } catch (AggregateException) { }
        finally { retiredLoads.Dispose(); }
        var changed = Changed;
        Changed = null;
        // Disposal must complete even when a page-owned notification callback throws.
        try { changed?.Invoke(); }
        catch { }
    }
}

/// <summary>Native service-worker adapters used by the compatibility JavaScript bridge.</summary>
internal static class ServiceWorkerBindings
{
    /// <summary>Installs the loader and bounded validator while returning the host-owned capability authority.</summary>
    internal static ServiceWorkerCapabilityAuthority Install(IScriptEngine engine, DomContext context,
        IResourceLoader? resourceLoader, Uri? baseUri, IArtifactStorage? artifactStorage = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(context);
        var authority = new ServiceWorkerCapabilityAuthority(context.Capabilities.ServiceWorkers,
            () => !context.ScriptFeaturesDisposed && context.Capabilities.ServiceWorkers);
        var loader = new HostObjectTemplate("ServiceWorkerLoader").DefineMethod("load", (_, _, args) =>
        {
            if (!authority.Enabled || resourceLoader is null || args.Length == 0) return ScriptValue.Null;
            var url = WebIdlConversions.ToDOMString(args[0]);
            if (baseUri is not null && Uri.TryCreate(baseUri, url, out var resolved)) url = resolved.AbsoluteUri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return ScriptValue.Null;
            var cancellation = authority.LoadCancellation;
            cancellation.ThrowIfCancellationRequested();
            var response = resourceLoader.LoadAsync(new ResourceRequest(uri.AbsoluteUri, ResourceKind.Script), cancellation).GetAwaiter().GetResult();
            cancellation.ThrowIfCancellationRequested();
            return response?.Data is { } data ? ScriptValue.FromString(Encoding.UTF8.GetString(data)) : ScriptValue.Null;
        });
        engine.SetGlobalProperty("__folio_service_worker_loader", engine.CreateHostObject(loader, authority));

        var validator = new HostObjectTemplate("ServiceWorkerValidator").DefineMethod("check", (_, _, args) =>
        {
            if (!authority.Enabled || args.Length < 2 || !args[0].IsString || engine is not IScriptEngineFactory factory)
                return ScriptValue.FromString(!authority.Enabled ? "error" : "ok");
            using var isolated = factory.CreateIsolated();
            isolated.Configure(new ScriptLimits { Timeout = context.MaxWorkerExecutionTime });
            try
            {
                var name = args[1].AsString() ?? "service-worker";
                isolated.Evaluate("globalThis.self = { addEventListener: function() {}, removeEventListener: function() {}, skipWaiting: function() { return Promise.resolve(); }, clients: { claim: function() { return Promise.resolve(); } }, importScripts: function() {} };", name);
                isolated.Evaluate("(function(self) { with (self) { " + args[0].AsString() + " } })(self);", name);
                return ScriptValue.FromString(authority.Enabled ? "ok" : "error");
            }
            catch (ScriptLimitExceededException) { return ScriptValue.FromString("timeout"); }
            catch (ScriptInterruptedException) { return ScriptValue.FromString("timeout"); }
            catch (Exception error) when (error.Message.Contains("timed", StringComparison.OrdinalIgnoreCase) || error.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase) || error.Message.Contains("limit", StringComparison.OrdinalIgnoreCase)) { return ScriptValue.FromString("timeout"); }
            catch { return ScriptValue.FromString("error"); }
        });
        engine.SetGlobalProperty("__folio_service_worker_validator", engine.CreateHostObject(validator, authority));
        // The isolated runtime is the Folio-owned native-engine bridge. Jint also
        // implements IScriptEngineFactory, but it must stay on the compatibility
        // worker scope below; routing it through the native bridge loses the
        // compatibility worker's `self`/controller bindings and response path.
        IsolatedServiceWorkerRuntime? nativeRuntime = engine is NativeJavaScriptEngine && engine is IScriptEngineFactory factory
            ? new IsolatedServiceWorkerRuntime(engine, context, factory, resourceLoader, baseUri)
            : null;
        var authorityTemplate = new HostObjectTemplate("ServiceWorkerCapabilityAuthority")
            .DefineMethod("enabled", (_, _, _) => ScriptValue.FromBoolean(context.CheckCapability("ServiceWorkers", authority.Enabled)))
            .DefineMethod("exposed", (_, _, _) => ScriptValue.FromBoolean(authority.Exposed || context.CheckCapability("ServiceWorkers", false)))
            .DefineMethod("onchange", (_, _, args) =>
            {
                if (args.Length > 0 && args[0].IsFunction)
                {
                    var callback = args[0];
                    authority.Changed = () =>
                    {
                        nativeRuntime?.Retire();
                        try { engine.Call(callback, ScriptValue.Undefined, []); }
                        catch (Exception error) { context.OwnerEventLoop?.ReportException(error); }
                    };
                }
                return ScriptValue.Undefined;
            })
            .DefineMethod("acquire", (_, _, _) =>
            {
                if (!authority.TryAcquire(out var lease) || lease is null) return ScriptValue.Null;
                try { return engine.CreateHostObject(LeaseTemplate, lease); }
                catch { lease.Dispose(); throw; }
            });
        engine.SetGlobalProperty("__folio_service_worker_authority", engine.CreateHostObject(authorityTemplate, authority));
        if (nativeRuntime is not null)
        {
            context.DisposeServiceWorker = nativeRuntime.Dispose;
            var nativeTemplate = new HostObjectTemplate("IsolatedServiceWorkerRuntime")
                .DefineMethod("run", (_, _, args) =>
                {
                    if (args.Length < 2) return ScriptValue.FromString("error");
                    var error = nativeRuntime.Load(args[0].AsString() ?? string.Empty, args[1].AsString() ?? "service-worker");
                    return error is null ? ScriptValue.Null : ScriptValue.FromString(error);
                })
                .DefineMethod("dispatch", (_, _, args) =>
                {
                    if (args.Length == 0) return ScriptValue.Null;
                    return nativeRuntime.DispatchValue(args[0].AsString() ?? string.Empty);
                })
                .DefineMethod("dispatchMessage", (_, _, args) =>
                {
                    return nativeRuntime.DispatchMessageValue(args.Length > 0 ? args[0] : ScriptValue.Undefined);
                })
                .DefineMethod("dispatchLifecycle", (_, _, args) =>
                {
                    nativeRuntime.DispatchLifecycleValue(args.Length > 0 ? args[0].AsString() ?? string.Empty : string.Empty);
                    return ScriptValue.Undefined;
                });
            engine.SetGlobalProperty("__folio_service_worker_isolated", engine.CreateHostObject(nativeTemplate, nativeRuntime));
        }
        if (artifactStorage is not null && context.Capabilities.ServiceWorkers)
        {
            string StorageKey(ScriptValue value)
            {
                if (!context.CheckCapability("ServiceWorkers", authority.Enabled))
                    throw WebIdlException.DomException("SecurityError", "Service workers are disabled by the host.");
                var key = value.AsString() ?? "";
                if (!key.StartsWith("service-worker-registration/", StringComparison.Ordinal))
                    throw WebIdlException.DomException("SecurityError", "Invalid service worker storage namespace.");
                return context.ArtifactId + ":" + key;
            }
            var storage = new HostObjectTemplate("ArtifactStorage")
                .DefineMethod("read", (_, _, args) => args.Length == 0 ? ScriptValue.Null : (artifactStorage.Read(StorageKey(args[0])) is { } value ? ScriptValue.FromString(value) : ScriptValue.Null))
                .DefineMethod("write", (_, _, args) => { if (args.Length > 1) artifactStorage.Write(StorageKey(args[0]), args[1].AsString() ?? ""); return ScriptValue.Undefined; })
                .DefineMethod("delete", (_, _, args) => { if (args.Length > 0) artifactStorage.Delete(StorageKey(args[0])); return ScriptValue.Undefined; });
            engine.SetGlobalProperty("__folio_artifact_storage", engine.CreateHostObject(storage, authority));
        }
        engine.SetGlobalProperty("__folio_service_worker_base", ScriptValue.FromString(baseUri?.AbsoluteUri ?? ""));
        try { InstallCompatibilityApi(engine, authority, artifactStorage is not null && context.Capabilities.ServiceWorkers, artifactStorage is ICacheStorageBackendProvider); }
        catch { authority.Dispose(); throw; }
        finally
        {
            engine.SetGlobalProperty("__folio_service_worker_authority", ScriptValue.Undefined);
            engine.SetGlobalProperty("__folio_service_worker_loader", ScriptValue.Undefined);
            engine.SetGlobalProperty("__folio_service_worker_validator", ScriptValue.Undefined);
            engine.SetGlobalProperty("__folio_artifact_storage", ScriptValue.Undefined);
            engine.SetGlobalProperty("__folio_service_worker_base", ScriptValue.Undefined);
        }
        return authority;
    }

    internal static void UpdateCapabilities(ServiceWorkerCapabilityAuthority authority, FeatureCapabilities capabilities) =>
        authority.UpdateCapabilities(capabilities.ServiceWorkers);

    private static readonly HostObjectTemplate LeaseTemplate = new HostObjectTemplate("ServiceWorkerCapabilityLease")
        .DefineMethod("active", (target, _, _) => ScriptValue.FromBoolean(((ServiceWorkerCapabilityLease)target).IsActive))
        .DefineMethod("release", (target, _, _) => { ((ServiceWorkerCapabilityLease)target).Dispose(); return ScriptValue.Undefined; });

    private static void InstallCompatibilityApi(IScriptEngine engine, ServiceWorkerCapabilityAuthority authority, bool storageEnabled, bool brokeredStorage)
    {
        var source = """
            (function(authority, registrationStorage, loader, validator, base, isolatedRuntime, brokeredStorage) {
            globalThis.navigator = globalThis.navigator || {};
            var __folio_service_worker_registrations = [];
            globalThis.navigator.serviceWorker = (function(registrationStorage, isolatedRuntime) {
                var registrationIndexKey = 'service-worker-registration/__index__';
                function persistedScopes() {
                    if (!registrationStorage) return [];
                    try { var raw = registrationStorage.read(registrationIndexKey); var parsed = raw ? JSON.parse(raw) : []; return Array.isArray(parsed) && parsed.length ? parsed : ['/']; } catch (_) { return ['/']; }
                }
                function rememberScope(scope) {
                    if (!registrationStorage) return;
                    var scopes = persistedScopes(); if (scopes.indexOf(scope) < 0) scopes.push(scope);
                    try { registrationStorage.write(registrationIndexKey, JSON.stringify(scopes)); } catch (_) { }
                }
                function forgetScope(scope) {
                    if (!registrationStorage) return;
                    var scopes = persistedScopes().filter(function(item) { return item !== scope; });
                    try { registrationStorage.write(registrationIndexKey, JSON.stringify(scopes)); } catch (_) { }
                }
                return {
                _restorePersisted: function() {
                    var container = this;
                    persistedScopes().forEach(function(scope) {
                        try {
                            var saved = registrationStorage.read('service-worker-registration/' + encodeURIComponent(String(scope)));
                            var metadata = saved ? JSON.parse(saved) : null;
                            if (metadata && metadata.scriptURL) container.register(metadata.scriptURL, { scope: metadata.scope || scope });
                        } catch (_) { }
                    });
                },
                controller: null,
                oncontrollerchange: null,
                _listeners: [],
                addEventListener: function(type, handler) { if (type === 'controllerchange' && typeof handler === 'function' && this._listeners.indexOf(handler) < 0) this._listeners.push(handler); },
                removeEventListener: function(type, handler) { if (type === 'controllerchange') { var i = this._listeners.indexOf(handler); if (i >= 0) this._listeners.splice(i, 1); } },
                dispatchEvent: function(event) { if (event.type === 'controllerchange') { if (this.oncontrollerchange) this.oncontrollerchange(event); this._listeners.slice().forEach(function(handler) { handler(event); }); } return true; },
                ready: Promise.resolve({ scope: '/', active: null }),
                register: function(scriptURL, options) {
                    if (!authority.enabled()) return Promise.reject(new DOMException('Service workers are disabled by the host.', 'SecurityError'));
                    var url = String(scriptURL); try { if (base) url = new URL(url, base).href; } catch (_) { }
                    // Service-worker scripts are same-origin with their registering document;
                    // fetch dispatch applies the same boundary later, but registration must
                    // reject a foreign script before loading or retaining a lease.
                    try {
                        var pageOrigin = base ? new URL(base).origin : '';
                        var scriptOrigin = new URL(url).origin;
                        if (pageOrigin && scriptOrigin && pageOrigin !== scriptOrigin)
                            return Promise.reject(new DOMException('The service-worker script is cross-origin.', 'SecurityError'));
                    } catch (_) { }
                    var scope = (options && options.scope) || '/';
                    try {
                        var scopeText = String(scope);
                        // Relative and root scopes are already constrained by the registering
                        // document. Only inspect an explicitly absolute scope here; the URL
                        // implementation used by compatibility fixtures does not expose a
                        // reliable origin for every relative URL form.
                        if (scopeText.indexOf('://') >= 0) {
                            var scopeUrl = new URL(scopeText);
                            var scopeOrigin = base ? new URL(base).origin : new URL(url).origin;
                            if (scopeUrl.origin && scopeOrigin && scopeUrl.origin !== scopeOrigin)
                                return Promise.reject(new DOMException('The service-worker scope is cross-origin.', 'SecurityError'));
                            scope = scopeUrl.pathname;
                        } else scope = scopeText;
                    } catch (_) { scope = String(scope); }
                    var registrationKey = 'service-worker-registration/' + encodeURIComponent(String(scope));
                    if (registrationStorage) { var persisted = registrationStorage.read(registrationKey); if (persisted) { try { var metadata = JSON.parse(persisted); if (metadata && metadata.scriptURL === url) url = String(metadata.scriptURL); } catch (_) { } } }
                    var existing = __folio_service_worker_registrations.find(function(r) { return r.scope === scope; });
                    if (existing) { if (existing.scriptURL !== url && typeof existing._setScriptUrl === 'function') existing._setScriptUrl(url); return Promise.resolve(existing); }
                    var lease=authority.acquire(); if(!lease) return Promise.reject(new DOMException('Service workers are disabled by the host.','SecurityError'));
                    function ensureLive(){if(!lease.active())throw new DOMException('The service worker is no longer active.','InvalidStateError');}
                    try { var workerListeners = [];
                    // The worker global deliberately hides navigator, but clients.claim()
                    // still updates the page's controller through this closed-over handle.
                    var pageServiceWorkerContainer = globalThis.navigator.serviceWorker;
                    var workerScript = loader.load(url); ensureLive();
                    var workerSelfListeners = [], workerFetchListeners = [], workerLifecycleListeners = { install: [], activate: [] };
                    var runWorkerScript;
                    var nativeDispatchMessage = null;
                    var nativeDispatchFetch = null;
                    var nativeDispatchFetchSync = null;
                    var workerSelf = { onmessage: null, onfetch: null, oninstall: null, onactivate: null, document: undefined, window: undefined, navigator: undefined, skipWaiting: function() { ensureLive(); return Promise.resolve(); }, clients: { claim: function() { ensureLive(); navigator.serviceWorker.controller = worker; navigator.serviceWorker.dispatchEvent({ type: 'controllerchange', target: navigator.serviceWorker }); return Promise.resolve(); } }, importScripts: function() { ensureLive(); for (var i = 0; i < arguments.length; i++) { var reference = String(arguments[i]); var resolved = reference; try { resolved = new URL(reference, url).href; } catch (_) { } var imported = loader.load(resolved); ensureLive(); if (imported === null || imported === undefined) throw new Error('Service worker script could not load ' + reference); runWorkerScript(imported, resolved); } }, addEventListener: function(type, handler) { if (typeof handler !== 'function') return; if (type === 'message') workerSelfListeners.push(handler); else if (type === 'fetch') workerFetchListeners.push(handler); else if (workerLifecycleListeners[type]) workerLifecycleListeners[type].push(handler); }, removeEventListener: function(type, handler) { if (type === 'message') workerSelfListeners = workerSelfListeners.filter(function(item) { return item !== handler; }); else if (type === 'fetch') workerFetchListeners = workerFetchListeners.filter(function(item) { return item !== handler; }); else if (workerLifecycleListeners[type]) workerLifecycleListeners[type] = workerLifecycleListeners[type].filter(function(item) { return item !== handler; }); }, postMessage: function(data) { ensureLive(); var event = { type: 'message', data: data, target: worker, source: null, origin: '' }; if (worker.onmessage) worker.onmessage(event); workerListeners.slice().forEach(function(handler) { ensureLive(); handler(event); }); } };
                    workerSelf.self = workerSelf;
                    workerSelf.clients.claim = function() { ensureLive(); pageServiceWorkerContainer.controller = worker; pageServiceWorkerContainer.dispatchEvent({ type: 'controllerchange', target: pageServiceWorkerContainer }); return Promise.resolve(); };
                    workerSelf.globalThis = workerSelf;
                    ['Promise','URL','Request','Response','Headers','Blob','TextEncoder','TextDecoder','ArrayBuffer','Uint8Array','Map','Set','WeakMap','WeakSet','Object','Array','String','Number','Boolean','Error','TypeError','RangeError','SyntaxError','RegExp','Reflect','Function','Date','Math','JSON','console','crypto','atob','btoa','fetch'].forEach(function(name) { if (globalThis[name] !== undefined) workerSelf[name] = globalThis[name]; });
                    runWorkerScript = function(source, filename) {
                        if (isolatedRuntime !== null && isolatedRuntime !== undefined) {
                            var nativeRuntime = isolatedRuntime;
                            var nativeError = nativeRuntime.run(source, filename);
                            if (nativeError) throw new Error(nativeError);
                            nativeDispatchMessage = function(data) { return nativeRuntime.dispatchMessage(data); };
                            nativeDispatchFetch = function(request) {
                                var raw = nativeRuntime.dispatch(request.url);
                                if (!raw) return Promise.resolve(null);
                                if (!raw.ok) return raw.error === 'no-response' ? Promise.resolve(null) : Promise.reject(new Error(raw.error || 'service-worker-fetch-failed'));
                                // Pass a scalar descriptor across the isolated-engine
                                // promise bridge; host objects lose their properties there.
                                return Promise.resolve('folio-response:' + raw.bodyBase64 + ':' + (raw.status || 200) + ':' + encodeURIComponent(raw.contentType || ''));
                            };
                            nativeDispatchFetchSync = function(request) {
                                var raw = nativeRuntime.dispatch(request.url);
                                if (!raw) return null;
                                if (!raw.ok) { if (raw.error === 'no-response') return null; throw new Error(raw.error || 'service-worker-fetch-failed'); }
                                return new Response(raw.body, { status: raw.status || 200, headers: { 'content-type': raw.contentType || '' } });
                            };
                            nativeRuntime.dispatchLifecycle('install');
                            nativeRuntime.dispatchLifecycle('activate');
                            workerSelf._nativeDispatch = function(request) {
                                var raw;
                                try { raw = nativeRuntime.dispatch(request.url); }
                                catch (error) { return Promise.reject(error); }
                                if (!raw) return Promise.resolve(null);
                                try {
                                    if (!raw.ok) return raw.error === 'no-response' ? Promise.resolve(null) : Promise.reject(new Error(raw.error || 'service-worker-fetch-failed'));
                                    return Promise.resolve(new Response(raw.body, { status: raw.status || 200, headers: { 'content-type': raw.contentType || '' } }));
                                } catch (error) { return Promise.reject(error); }
                            };
                            return;
                        }
                        // A plain `with (scope)` still falls through to the page
                        // global for names absent from the worker object. The
                        // `has` trap makes the scope an exhaustive environment
                        // record, so page-only globals (and arbitrary expandos)
                        // resolve as worker-side `undefined` instead of leaking.
                        var isolatedScope = typeof Proxy === 'function'
                            ? new Proxy(workerSelf, { has: function() { return true; } })
                            : workerSelf;
                        return new Function('scope', 'with (scope) { ' + source + '\n }')(isolatedScope);
                    };
                    var scopePath = String(scope); if (scopePath.length === 0 || scopePath.charAt(0) !== '/') scopePath = '/' + scopePath; if (scopePath.charAt(scopePath.length - 1) !== '/') scopePath += '/';
                    var originOf = function(value) { var text = String(value), scheme = text.indexOf('://'); if (scheme < 1) return ''; var end = text.indexOf('/', scheme + 3); return text.slice(0, end < 0 ? text.length : end).toLowerCase(); };
                    var worker = { scriptURL: url, state: 'installing', onstatechange: null, onmessage: null, _self: workerSelf,
                        addEventListener: function(type, handler) { if (type === 'message' && typeof handler === 'function' && workerListeners.indexOf(handler) < 0) workerListeners.push(handler); },
                        removeEventListener: function(type, handler) { if (type === 'message') { var index = workerListeners.indexOf(handler); if (index >= 0) workerListeners.splice(index, 1); } },
                        postMessage: function(data) { ensureLive(); if (nativeDispatchMessage) { var outgoing = nativeDispatchMessage(data); if (outgoing !== undefined && outgoing !== null) { var responseEvent = { type: 'message', data: outgoing, target: worker, source: null, origin: '' }; if (worker.onmessage) worker.onmessage(responseEvent); workerListeners.slice().forEach(function(handler) { ensureLive(); handler(responseEvent); }); } return; } if (typeof workerSelf._nativePostMessage === 'function') { var outgoing = workerSelf._nativePostMessage(data); if (outgoing !== undefined && outgoing !== null) { var responseEvent = { type: 'message', data: outgoing, target: worker, source: null, origin: '' }; if (worker.onmessage) worker.onmessage(responseEvent); workerListeners.slice().forEach(function(handler) { ensureLive(); handler(responseEvent); }); } return; } var event = { type: 'message', data: data, target: workerSelf, source: null, origin: '' }; var handled = !!workerSelf.onmessage || workerSelfListeners.length > 0; if (workerSelf.onmessage) workerSelf.onmessage(event); workerSelfListeners.slice().forEach(function(handler) { ensureLive(); handler(event); }); if (!handled) { if (worker.onmessage) worker.onmessage({ type: 'message', data: data, target: worker, source: null, origin: '' }); workerListeners.slice().forEach(function(handler) { ensureLive(); handler({ type: 'message', data: data, target: worker, source: null, origin: '' }); }); } },
                        _retire: function() { workerSelf.onmessage = null; workerSelf.onfetch = null; workerSelf.oninstall = null; workerSelf.onactivate = null; workerSelf._nativeDispatch = null; workerSelf._nativePostMessage = null; workerSelfListeners = []; workerFetchListeners = []; workerLifecycleListeners = { install: [], activate: [] }; workerListeners = []; worker.onmessage = null; worker.state = 'redundant'; },
                        _dispatchFetchSync: function(request) { ensureLive(); var requestPath = ''; var requestOrigin = originOf(request.url); var workerOrigin = originOf(url); try { var requestUrl = new URL(request.url); requestPath = requestUrl.pathname; } catch (_) { var requestText = String(request.url); var authorityEnd = requestText.indexOf('://'); var pathStart = authorityEnd < 0 ? requestText.indexOf('/') : requestText.indexOf('/', authorityEnd + 3); requestPath = pathStart < 0 ? '/' : requestText.slice(pathStart); } if ((requestOrigin && workerOrigin && requestOrigin !== workerOrigin) || requestPath.indexOf(scopePath) !== 0) return null; if (nativeDispatchFetchSync) return nativeDispatchFetchSync(request); return undefined; },
                        _dispatchFetch: function(request) { return new Promise(function(resolve, reject) { ensureLive(); var sync = typeof worker._dispatchFetchSync === 'function' ? worker._dispatchFetchSync(request) : undefined; if (sync !== undefined) { resolve(sync); return; } var requestPath = ''; var requestOrigin = originOf(request.url); var workerOrigin = originOf(url); try { var requestUrl = new URL(request.url); requestPath = requestUrl.pathname; } catch (_) { var requestText = String(request.url); var authorityEnd = requestText.indexOf('://'); var pathStart = authorityEnd < 0 ? requestText.indexOf('/') : requestText.indexOf('/', authorityEnd + 3); requestPath = pathStart < 0 ? '/' : requestText.slice(pathStart); } if ((requestOrigin && workerOrigin && requestOrigin !== workerOrigin) || requestPath.indexOf(scopePath) !== 0) { resolve(null); return; } if (nativeDispatchFetch) { nativeDispatchFetch(request).then(resolve, reject); return; } if (typeof workerSelf._nativeDispatch === 'function') { workerSelf._nativeDispatch(request).then(resolve, reject); return; } var settled = false; var event = { type: 'fetch', request: request, target: workerSelf, respondWith: function(value) { if (settled) return; settled = true; Promise.resolve(value).then(function(response){try{ensureLive();resolve(response);}catch(error){reject(error);}}, reject); } }; try { if (typeof workerSelf.onfetch === 'function') workerSelf.onfetch(event); workerFetchListeners.slice().forEach(function(handler) { ensureLive(); handler(event); }); if (!settled) resolve(null); } catch (error) { reject(error); } }); }
                    };
                    if (workerScript !== null && workerScript !== undefined) { var validation = validator.check(workerScript, url); if (validation === 'timeout') throw new DOMException('Service worker execution exceeded the host limit.', 'TimeoutError'); if (validation !== 'ok') throw new DOMException('Service worker script failed validation.', 'SyntaxError'); runWorkerScript(workerScript, url); }
                    var installEvent = { type: 'install', target: workerSelf, waitUntil: function(promise) { return Promise.resolve(promise); } }; if (workerSelf.oninstall) workerSelf.oninstall(installEvent); workerLifecycleListeners.install.slice().forEach(function(handler) { ensureLive(); handler(installEvent); });
                    var activateEvent = { type: 'activate', target: workerSelf, waitUntil: function(promise) { return Promise.resolve(promise); } }; if (workerSelf.onactivate) workerSelf.onactivate(activateEvent); workerLifecycleListeners.activate.slice().forEach(function(handler) { ensureLive(); handler(activateEvent); });
                    ensureLive(); worker.state = 'activated'; if (worker.onstatechange) worker.onstatechange({ type: 'statechange', target: worker });
                    var registrationListeners = [];
                    var registration = { scope: scope, scriptURL: url, _setScriptUrl: function(next) { ensureLive(); url = String(next); registration.scriptURL = url; if (registrationStorage) { registrationStorage.write(registrationKey, JSON.stringify({ scope: scope, scriptURL: url })); rememberScope(scope); } }, active: worker, installing: null, waiting: null, onupdatefound: null,
                        addEventListener: function(type, handler) { if (type === 'updatefound' && typeof handler === 'function' && registrationListeners.indexOf(handler) < 0) registrationListeners.push(handler); },
                        removeEventListener: function(type, handler) { if (type === 'updatefound') { var index = registrationListeners.indexOf(handler); if (index >= 0) registrationListeners.splice(index, 1); } },
                        update: function() { try{ensureLive();}catch(error){return Promise.reject(error);} var latest = loader.load(url); try{ensureLive();}catch(error){return Promise.reject(error);} var event = { type: 'updatefound', target: registration }; if (registration.onupdatefound) registration.onupdatefound(event); registrationListeners.slice().forEach(function(handler) { ensureLive(); handler(event); }); try{ensureLive();}catch(error){return Promise.reject(error);} if (latest === workerScript) return Promise.resolve(registration); if (latest === null || latest === undefined) return Promise.reject(new TypeError('Service worker script could not be loaded.')); workerScript = latest; worker.state = 'installing'; registration.installing = worker; if (worker.onstatechange) worker.onstatechange({ type: 'statechange', target: worker }); workerSelfListeners = []; workerFetchListeners = []; workerLifecycleListeners = { install: [], activate: [] }; workerSelf.onmessage = null; workerSelf.onfetch = null; workerSelf.oninstall = null; workerSelf.onactivate = null; var updateValidation = validator.check(workerScript, url); if (updateValidation === 'timeout') throw new DOMException('Service worker execution exceeded the host limit.', 'TimeoutError'); if (updateValidation !== 'ok') throw new DOMException('Service worker script failed validation.', 'SyntaxError'); runWorkerScript(workerScript, url); var installEvent = { type: 'install', target: workerSelf, waitUntil: function(promise) { return Promise.resolve(promise); } }; if (workerSelf.oninstall) workerSelf.oninstall(installEvent); workerLifecycleListeners.install.slice().forEach(function(handler) { ensureLive(); handler(installEvent); }); var activateEvent = { type: 'activate', target: workerSelf, waitUntil: function(promise) { return Promise.resolve(promise); } }; if (workerSelf.onactivate) workerSelf.onactivate(activateEvent); workerLifecycleListeners.activate.slice().forEach(function(handler) { ensureLive(); handler(activateEvent); }); ensureLive(); worker.state = 'activated'; registration.installing = null; registration.active = worker; if (worker.onstatechange) worker.onstatechange({ type: 'statechange', target: worker }); navigator.serviceWorker.controller = worker; return Promise.resolve(registration); },
                    unregister: function() { try{ensureLive();}catch(error){return Promise.reject(error);} worker._retire(); lease.release(); var i = __folio_service_worker_registrations.indexOf(registration); if (i >= 0) __folio_service_worker_registrations.splice(i, 1); if (registrationStorage) registrationStorage.delete(registrationKey); forgetScope(scope); if (navigator.serviceWorker.controller === worker) { navigator.serviceWorker.controller = null; navigator.serviceWorker.dispatchEvent({ type: 'controllerchange', target: navigator.serviceWorker }); } return Promise.resolve(true); } };
                    ensureLive(); if (registrationStorage) { registrationStorage.write(registrationKey, JSON.stringify({ scope: scope, scriptURL: url })); rememberScope(scope); }
                    __folio_service_worker_registrations.push(registration); navigator.serviceWorker.controller = worker; navigator.serviceWorker.dispatchEvent({ type: 'controllerchange', target: navigator.serviceWorker }); navigator.serviceWorker.ready = Promise.resolve(registration); return Promise.resolve(registration); } catch(error) {lease.release();return Promise.reject(error);}
                },
                getRegistration: function(scope) { var value = String(scope || '/'); return Promise.resolve(__folio_service_worker_registrations.find(function(r) { return r.scope === value; })); },
                getRegistrations: function() { return Promise.resolve(__folio_service_worker_registrations.slice()); }
            }; })(registrationStorage, isolatedRuntime);
            var container=globalThis.navigator.serviceWorker;
            if (registrationStorage) { container._restorePersisted(); try { delete container._restorePersisted; } catch (_) { } }
            Object.defineProperty(globalThis.navigator,'serviceWorker',{configurable:true,get:function(){return authority.exposed()?container:undefined;}});
            authority.onchange(function(){var previous=__folio_service_worker_registrations;__folio_service_worker_registrations=[];for(var i=0;i<previous.length;i++){try{if(previous[i].active){if(typeof previous[i].active._retire==='function')previous[i].active._retire();else previous[i].active.state='redundant';}}catch(_){}}try{container.controller=null;container.ready=Promise.resolve(undefined);}catch(_){}});
            })(__folio_service_worker_authority, globalThis.__folio_artifact_storage, __folio_service_worker_loader, __folio_service_worker_validator, __folio_service_worker_base, typeof __folio_service_worker_isolated === 'undefined' ? null : __folio_service_worker_isolated, %BROKERED%);
            delete globalThis.__folio_service_worker_authority;
            delete globalThis.__folio_artifact_storage;
            delete globalThis.__folio_service_worker_loader;
            delete globalThis.__folio_service_worker_validator;
            """.Replace("%BROKERED%", brokeredStorage ? "true" : "false", StringComparison.Ordinal);
        engine.EvaluateHostScript(source, "folio:service-worker-compatibility");
    }
}

/// <summary>A generation-bound service-worker authority retained by a compatibility object.</summary>
internal sealed class ServiceWorkerCapabilityLease : IDisposable
{
    private readonly ServiceWorkerCapabilityAuthority _authority;
    private readonly long _generation;
    private int _retired;

    internal ServiceWorkerCapabilityLease(ServiceWorkerCapabilityAuthority authority, long generation)
    {
        _authority = authority;
        _generation = generation;
    }

    public bool IsActive => Volatile.Read(ref _retired) == 0 && _authority.IsActive(this, _generation);

    internal void Retire() => Interlocked.Exchange(ref _retired, 1);

    public void Dispose()
    {
        Retire();
        _authority.Release(this);
    }
}
