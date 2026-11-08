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
    // A loader is allowed to ignore cancellation. Bound the host wait independently,
    // cancel its owned request on timeout, and never attach a script-evaluation continuation.
    internal static ResourceResponse? LoadScript(IResourceLoader loader, string url, CancellationToken cancellation, TimeSpan timeout)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        request.CancelAfter(timeout);
        try
        {
            request.Token.ThrowIfCancellationRequested();
            var pending = loader.LoadAsync(new ResourceRequest(url, ResourceKind.Script), request.Token);
            var response = pending.WaitAsync(request.Token).GetAwaiter().GetResult();
            request.Token.ThrowIfCancellationRequested();
            return response;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw WebIdlException.DomException("TimeoutError", "Service worker script loading exceeded the host limit.");
        }
    }

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
            var response = LoadScript(resourceLoader, uri.AbsoluteUri, cancellation, context.MaxWorkerExecutionTime);
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
        var nativeRuntimes = new HashSet<IsolatedServiceWorkerRuntime>();
        void RetireRuntimes()
        {
            var retired = nativeRuntimes.ToArray();
            nativeRuntimes.Clear();
            foreach (var runtime in retired)
            {
                try { runtime.Dispose(); }
                catch (Exception error) { context.OwnerEventLoop?.ReportException(error); }
            }
        }
        context.DisposeServiceWorker = RetireRuntimes;
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
                        RetireRuntimes();
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
        if (engine is NativeJavaScriptEngine && engine is IScriptEngineFactory nativeFactory)
        {
            var nativeTemplate = new HostObjectTemplate("IsolatedServiceWorkerRuntime")
                .DefineMethod("run", (target, _, args) =>
                {
                    if (args.Length < 2) return ScriptValue.FromString("error");
                    var error = ((IsolatedServiceWorkerRuntime)target).Load(args[0].AsString() ?? string.Empty, args[1].AsString() ?? "service-worker");
                    return error is null ? ScriptValue.Null : ScriptValue.FromString(error);
                })
                .DefineMethod("dispatch", (target, _, args) => args.Length == 0 ? ScriptValue.Null : ((IsolatedServiceWorkerRuntime)target).DispatchValue(args[0].AsString() ?? string.Empty))
                .DefineMethod("dispatchMessage", (target, _, args) => ((IsolatedServiceWorkerRuntime)target).DispatchMessageValue(args.Length > 0 ? args[0] : ScriptValue.Undefined))
                .DefineMethod("dispatchLifecycle", (target, _, args) => ((IsolatedServiceWorkerRuntime)target).DispatchLifecycleValue(args.Length > 0 ? args[0].AsString() ?? string.Empty : string.Empty))
                .DefineMethod("skipWaiting", (target, _, _) => ScriptValue.FromBoolean(((IsolatedServiceWorkerRuntime)target).SkipWaitingRequested))
                .DefineMethod("claim", (target, _, _) => ScriptValue.FromBoolean(((IsolatedServiceWorkerRuntime)target).ClaimRequested))
                .DefineMethod("retire", (target, _, _) =>
                {
                    var runtime = (IsolatedServiceWorkerRuntime)target;
                    nativeRuntimes.Remove(runtime);
                    try { runtime.Dispose(); }
                    catch (Exception error) { context.OwnerEventLoop?.ReportException(error); }
                    return ScriptValue.Undefined;
                });
            var factoryTemplate = new HostObjectTemplate("ServiceWorkerRuntimeFactory")
                .DefineMethod("create", (_, _, _) =>
                {
                    // Capture one generation. Never reacquire a fresh grant after a
                    // revoke/regrant while constructing or waiting for this worker.
                    var generationCancellation = authority.LoadCancellation;
                    generationCancellation.ThrowIfCancellationRequested();
                    var runtime = new IsolatedServiceWorkerRuntime(engine, context, nativeFactory, resourceLoader, baseUri, generationCancellation);
                    try
                    {
                        var wrapper = engine.CreateHostObject(nativeTemplate, runtime);
                        nativeRuntimes.Add(runtime);
                        return wrapper;
                    }
                    catch { runtime.Dispose(); throw; }
                });
            engine.SetGlobalProperty("__folio_service_worker_isolated", engine.CreateHostObject(factoryTemplate, authority));
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
            // Host storage failures must reach script as a catchable rejection so the
            // lifecycle can roll back, not escape the engine as a raw CLR exception.
            static WebIdlException StorageFailed() => WebIdlException.DomException("UnknownError", "Service worker registration storage failed.");
            string? Read(string key)
            {
                try { return artifactStorage.Read(key); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw StorageFailed(); }
            }
            void Write(string key, string value)
            {
                try { artifactStorage.Write(key, value); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw StorageFailed(); }
            }
            void Delete(string key)
            {
                try { artifactStorage.Delete(key); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw StorageFailed(); }
            }
            var storage = new HostObjectTemplate("ArtifactStorage")
                .DefineMethod("read", (_, _, args) => args.Length == 0 ? ScriptValue.Null : (Read(StorageKey(args[0])) is { } value ? ScriptValue.FromString(value) : ScriptValue.Null))
                .DefineMethod("write", (_, _, args) => { if (args.Length > 1) Write(StorageKey(args[0]), args[1].AsString() ?? ""); return ScriptValue.Undefined; })
                .DefineMethod("delete", (_, _, args) => { if (args.Length > 0) Delete(StorageKey(args[0])); return ScriptValue.Undefined; });
            engine.SetGlobalProperty("__folio_artifact_storage", engine.CreateHostObject(storage, authority));
        }
        engine.SetGlobalProperty("__folio_service_worker_base", ScriptValue.FromString(baseUri?.AbsoluteUri ?? ""));
        try { InstallCompatibilityApi(engine, authority, artifactStorage is not null && context.Capabilities.ServiceWorkers, artifactStorage is ICacheStorageBackendProvider); }
        catch { authority.Dispose(); RetireRuntimes(); throw; }
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
                var resolveReady;
                var readyPromise = new Promise(function(resolve) { resolveReady = resolve; });
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
                            if (metadata && metadata.scriptURL) {
                                var restored = container.register(metadata.scriptURL, { scope: metadata.scope || scope, restoring: true });
                                if (restored && typeof restored.catch === 'function') restored.catch(function() { });
                            }
                        } catch (_) { }
                    });
                },
                controller: null,
                oncontrollerchange: null,
                _listeners: [],
                addEventListener: function(type, handler) { if (type === 'controllerchange' && typeof handler === 'function' && this._listeners.indexOf(handler) < 0) this._listeners.push(handler); },
                removeEventListener: function(type, handler) { if (type === 'controllerchange') { var i = this._listeners.indexOf(handler); if (i >= 0) this._listeners.splice(i, 1); } },
                dispatchEvent: function(event) { if (event.type === 'controllerchange') { if (this.oncontrollerchange) this.oncontrollerchange(event); this._listeners.slice().forEach(function(handler) { handler(event); }); } return true; },
                ready: readyPromise,
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
                    var scope = options && options.scope;
                    if (!scope) {
                        try {
                            var scriptUrl = new URL(url, base || undefined);
                            var scriptPath = scriptUrl.pathname || '/';
                            var separator = scriptPath.lastIndexOf('/');
                            scope = separator >= 0 ? scriptPath.slice(0, separator + 1) : '/';
                        } catch (_) { scope = '/'; }
                    }
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
                        } else if (base) {
                            var relativeScopeUrl = new URL(scopeText, base);
                            var relativeScopeOrigin = new URL(base).origin;
                            if (relativeScopeUrl.origin && relativeScopeOrigin && relativeScopeUrl.origin !== relativeScopeOrigin)
                                return Promise.reject(new DOMException('The service-worker scope is cross-origin.', 'SecurityError'));
                            scope = relativeScopeUrl.pathname;
                        } else scope = scopeText;
                    } catch (_) { scope = String(scope); }
                    if (scope.length === 0 || scope.charAt(0) !== '/') scope = '/' + scope;
                    if (scope.charAt(scope.length - 1) !== '/') scope += '/';
                    // Without a Service-Worker-Allowed response header, a worker
                    // cannot control paths above its script directory.
                    try {
                        var resolvedScript = new URL(url, base || undefined);
                        var scriptPath = resolvedScript.pathname || '/';
                        var scriptSeparator = scriptPath.lastIndexOf('/');
                        var scriptDirectory = scriptSeparator >= 0 ? scriptPath.slice(0, scriptSeparator + 1) : '/';
                        if (scope.indexOf(scriptDirectory) !== 0)
                            return Promise.reject(new DOMException('The service-worker scope is outside the script directory.', 'SecurityError'));
                    } catch (_) { }
                    var registrationKey = 'service-worker-registration/' + encodeURIComponent(String(scope));
                    if (registrationStorage) { var persisted = registrationStorage.read(registrationKey); if (persisted) { try { var metadata = JSON.parse(persisted); if (metadata && metadata.scriptURL === url) url = String(metadata.scriptURL); } catch (_) { } } }
                    var existing = __folio_service_worker_registrations.find(function(r) { return r.scope === scope; });
                    if (existing && !(options && options.updating)) { if (existing.scriptURL !== url) return existing._updateTo(url); return Promise.resolve(existing); }
                    var lease=authority.acquire(); if(!lease) return Promise.reject(new DOMException('Service workers are disabled by the host.','SecurityError'));
                    function ensureLive(){if(!lease.active())throw new DOMException('The service worker is no longer active.','InvalidStateError');}
                    try { var workerListeners = [], workerStateListeners = [];
                    // The worker global deliberately hides navigator, but clients.claim()
                    // still updates the page's controller through this closed-over handle.
                    var pageServiceWorkerContainer = globalThis.navigator.serviceWorker;
                    var workerScript = loader.load(url); ensureLive();
                    if (existing && workerScript === existing._source && url === existing.scriptURL) { lease.release(); return Promise.resolve(existing); }
                    var workerSelfListeners = [], workerFetchListeners = [], workerLifecycleListeners = { install: [], activate: [] };
                    var nativeRuntime = null;
                    var skipRequested = false, claimRequested = false;
                    var runWorkerScript;
                    function claimClient() {
                        ensureLive();
                        if (!worker || (worker.state !== 'activating' && worker.state !== 'activated')) return Promise.reject(new DOMException('The worker is not active.', 'InvalidStateError'));
                        claimRequested = true;
                        if (clientInScope() && pageServiceWorkerContainer.controller !== worker) {
                            pageServiceWorkerContainer.controller = worker;
                            try { pageServiceWorkerContainer.dispatchEvent({ type: 'controllerchange', target: pageServiceWorkerContainer }); } catch (_) { }
                        }
                        return Promise.resolve();
                    }
                    function syncNativeRequests() {
                        if (!nativeRuntime) return;
                        if (nativeRuntime.skipWaiting()) { skipRequested = true; if (registration && registration.waiting === worker) activate(); }
                        if (nativeRuntime.claim() && (worker.state === 'activating' || worker.state === 'activated')) claimClient();
                    }
                    var nativeDispatchMessage = null;
                    var nativeDispatchFetch = null;
                    var nativeDispatchFetchSync = null;
                    var workerSelf = { onmessage: null, onfetch: null, oninstall: null, onactivate: null, document: undefined, window: undefined, navigator: undefined, skipWaiting: function() { ensureLive(); skipRequested = true; if (registration && registration.waiting === worker) activate(); return Promise.resolve(); }, clients: { claim: claimClient }, importScripts: function() { ensureLive(); for (var i = 0; i < arguments.length; i++) { var reference = String(arguments[i]); var resolved = reference; try { resolved = new URL(reference, url).href; } catch (_) { } var imported = loader.load(resolved); ensureLive(); if (imported === null || imported === undefined) throw new Error('Service worker script could not load ' + reference); runWorkerScript(imported, resolved); } }, addEventListener: function(type, handler) { if (typeof handler !== 'function') return; if (type === 'message') workerSelfListeners.push(handler); else if (type === 'fetch') workerFetchListeners.push(handler); else if (workerLifecycleListeners[type]) workerLifecycleListeners[type].push(handler); }, removeEventListener: function(type, handler) { if (type === 'message') workerSelfListeners = workerSelfListeners.filter(function(item) { return item !== handler; }); else if (type === 'fetch') workerFetchListeners = workerFetchListeners.filter(function(item) { return item !== handler; }); else if (workerLifecycleListeners[type]) workerLifecycleListeners[type] = workerLifecycleListeners[type].filter(function(item) { return item !== handler; }); }, postMessage: function(data) { ensureLive(); var event = { type: 'message', data: data, target: worker, source: null, origin: '' }; if (worker.onmessage) worker.onmessage(event); workerListeners.slice().forEach(function(handler) { ensureLive(); handler(event); }); } };
                    workerSelf.self = workerSelf;
                    workerSelf.clients.claim = claimClient;
                    workerSelf.globalThis = workerSelf;
                    ['DOMException','Promise','URL','Request','Response','Headers','Blob','TextEncoder','TextDecoder','ArrayBuffer','Uint8Array','Map','Set','WeakMap','WeakSet','Object','Array','String','Number','Boolean','Error','TypeError','RangeError','SyntaxError','RegExp','Reflect','Function','Date','Math','JSON','console','crypto','atob','btoa','fetch','Cache','CacheStorage','caches'].forEach(function(name) { if (globalThis[name] !== undefined) workerSelf[name] = globalThis[name]; });
                    runWorkerScript = function(source, filename) {
                        if (isolatedRuntime !== null && isolatedRuntime !== undefined) {
                            nativeRuntime = isolatedRuntime.create();
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
                    // Service-worker scopes are path-prefix matches on a path-segment
                    // boundary. A naive prefix check would let /app/ intercept
                    // /apple/... requests, which violates the scope restriction.
                    var scopeMatches = function(path, candidate) { if (candidate === '/') return path.charAt(0) === '/'; var prefix = candidate.slice(0, -1); return path === prefix || path.indexOf(candidate) === 0; };
                    var originOf = function(value) { var text = String(value), scheme = text.indexOf('://'); if (scheme < 1) return ''; var end = text.indexOf('/', scheme + 3); return text.slice(0, end < 0 ? text.length : end).toLowerCase(); };
                    var worker = { scriptURL: url, _source: workerScript, _ensureLive: ensureLive, state: 'installing', onstatechange: null, onmessage: null, _self: workerSelf,
                        addEventListener: function(type, handler) { var list = type === 'message' ? workerListeners : type === 'statechange' ? workerStateListeners : null; if (list && typeof handler === 'function' && list.indexOf(handler) < 0) list.push(handler); },
                        removeEventListener: function(type, handler) { var list = type === 'message' ? workerListeners : type === 'statechange' ? workerStateListeners : null; if (list) { var index = list.indexOf(handler); if (index >= 0) list.splice(index, 1); } },
                        postMessage: function(data) { ensureLive(); if (nativeDispatchMessage) { var outgoing = nativeDispatchMessage(data); syncNativeRequests(); if (outgoing !== undefined && outgoing !== null) { var responseEvent = { type: 'message', data: outgoing, target: worker, source: null, origin: '' }; if (worker.onmessage) worker.onmessage(responseEvent); workerListeners.slice().forEach(function(handler) { ensureLive(); handler(responseEvent); }); } return; } if (typeof workerSelf._nativePostMessage === 'function') { var outgoing = workerSelf._nativePostMessage(data); if (outgoing !== undefined && outgoing !== null) { var responseEvent = { type: 'message', data: outgoing, target: worker, source: null, origin: '' }; if (worker.onmessage) worker.onmessage(responseEvent); workerListeners.slice().forEach(function(handler) { ensureLive(); handler(responseEvent); }); } return; } var event = { type: 'message', data: data, target: workerSelf, source: null, origin: '' }; var handled = !!workerSelf.onmessage || workerSelfListeners.length > 0; if (workerSelf.onmessage) workerSelf.onmessage(event); workerSelfListeners.slice().forEach(function(handler) { ensureLive(); handler(event); }); if (!handled) { if (worker.onmessage) worker.onmessage({ type: 'message', data: data, target: worker, source: null, origin: '' }); workerListeners.slice().forEach(function(handler) { ensureLive(); handler({ type: 'message', data: data, target: worker, source: null, origin: '' }); }); } },
                        _retire: function() { if (nativeRuntime) { nativeRuntime.retire(); nativeRuntime = null; } lease.release(); nativeDispatchMessage = nativeDispatchFetch = nativeDispatchFetchSync = null; workerSelf.onmessage = null; workerSelf.onfetch = null; workerSelf.oninstall = null; workerSelf.onactivate = null; workerSelf._nativeDispatch = null; workerSelf._nativePostMessage = null; workerSelfListeners = []; workerFetchListeners = []; workerLifecycleListeners = { install: [], activate: [] }; workerListeners = []; worker.onmessage = null; if (worker.state !== 'redundant') { worker.state = 'redundant'; var event = { type: 'statechange', target: worker }; try { if (worker.onstatechange) worker.onstatechange(event); } catch (_) { } workerStateListeners.slice().forEach(function(handler) { try { handler(event); } catch (_) { } }); } worker.onstatechange = null; workerStateListeners = []; },
                        _dispatchFetchSync: function(request) { ensureLive(); var requestPath = ''; var requestOrigin = originOf(request.url); var workerOrigin = originOf(url); try { var requestUrl = new URL(request.url); requestPath = requestUrl.pathname; } catch (_) { var requestText = String(request.url); var authorityEnd = requestText.indexOf('://'); var pathStart = authorityEnd < 0 ? requestText.indexOf('/') : requestText.indexOf('/', authorityEnd + 3); requestPath = pathStart < 0 ? '/' : requestText.slice(pathStart); } if ((requestOrigin && workerOrigin && requestOrigin !== workerOrigin) || !scopeMatches(requestPath, scopePath)) return null; if (nativeDispatchFetchSync) return nativeDispatchFetchSync(request); return undefined; },
                        _dispatchFetch: function(request) { return new Promise(function(resolve, reject) { ensureLive(); var sync = typeof worker._dispatchFetchSync === 'function' ? worker._dispatchFetchSync(request) : undefined; if (sync !== undefined) { resolve(sync); return; } var requestPath = ''; var requestOrigin = originOf(request.url); var workerOrigin = originOf(url); try { var requestUrl = new URL(request.url); requestPath = requestUrl.pathname; } catch (_) { var requestText = String(request.url); var authorityEnd = requestText.indexOf('://'); var pathStart = authorityEnd < 0 ? requestText.indexOf('/') : requestText.indexOf('/', authorityEnd + 3); requestPath = pathStart < 0 ? '/' : requestText.slice(pathStart); } if ((requestOrigin && workerOrigin && requestOrigin !== workerOrigin) || !scopeMatches(requestPath, scopePath)) { resolve(null); return; } if (nativeDispatchFetch) { nativeDispatchFetch(request).then(resolve, reject); return; } if (typeof workerSelf._nativeDispatch === 'function') { workerSelf._nativeDispatch(request).then(resolve, reject); return; } var settled = false; var event = { type: 'fetch', request: request, target: workerSelf, respondWith: function(value) { if (settled) return; settled = true; Promise.resolve(value).then(function(response){try{ensureLive();resolve(response);}catch(error){reject(error);}}, reject); } }; try { if (typeof workerSelf.onfetch === 'function') workerSelf.onfetch(event); workerFetchListeners.slice().forEach(function(handler) { ensureLive(); handler(event); }); if (!settled) resolve(null); } catch (error) { reject(error); } }); }
                    };
                    var isRestoring = !!(options && options.restoring);
                    var previousActive = existing ? existing.active : null;
                    var previousWaiting = existing ? existing.waiting : null;
                    var registration = existing || { scope: scope, scriptURL: url, active: null, installing: null, waiting: null, onupdatefound: null, _listeners: [], _workers: [],
                        addEventListener: function(type, handler) { if (type === 'updatefound' && typeof handler === 'function' && this._listeners.indexOf(handler) < 0) this._listeners.push(handler); },
                        removeEventListener: function(type, handler) { if (type === 'updatefound') this._listeners = this._listeners.filter(function(item) { return item !== handler; }); },
                        unregister: function() {
                            var controlled = this._workers.indexOf(pageServiceWorkerContainer.controller) >= 0;
                            this._workers.slice().forEach(function(item) { item._retire(); }); this._workers = [];
                            this.installing = this.waiting = this.active = null;
                            var i = __folio_service_worker_registrations.indexOf(this); if (i >= 0) __folio_service_worker_registrations.splice(i, 1);
                            if (registrationStorage) registrationStorage.delete(registrationKey); forgetScope(scope);
                            if (controlled) { pageServiceWorkerContainer.controller = null; pageServiceWorkerContainer.dispatchEvent({ type: 'controllerchange', target: pageServiceWorkerContainer }); }
                            return Promise.resolve(true);
                        }
                    };
                    function state(value) { worker.state = value; var event = { type: 'statechange', target: worker }; try { if (worker.onstatechange) worker.onstatechange(event); } catch (_) { } workerStateListeners.slice().forEach(function(handler) { try { handler(event); } catch (_) { } }); }
                    function clientInScope() {
                        try { var client = new URL(base); return client.origin === new URL(url).origin && scopeMatches(client.pathname, scopePath); } catch (_) { return false; }
                    }
                    function lifecycle(type) {
                        ensureLive();
                        if (nativeRuntime) {
                            var pending = nativeRuntime.dispatchLifecycle(type);
                            syncNativeRequests();
                            return pending;
                        }
                        return new Promise(function(resolve, reject) {
                            var count = 0, open = true, failed = false;
                            function complete() { if (!open && count === 0 && !failed) resolve(); }
                            var event = { type: type, target: workerSelf, waitUntil: function(value) {
                                if (!open && count === 0) throw new DOMException('The event lifetime has ended.', 'InvalidStateError');
                                count++;
                                Promise.resolve(value).then(function() { count--; complete(); }, function(error) { count--; failed = true; reject(error); });
                            } };
                            try {
                                var handler = workerSelf['on' + type]; if (handler) handler(event);
                                workerLifecycleListeners[type].slice().forEach(function(listener) { ensureLive(); listener(event); });
                            } catch (error) { failed = true; reject(error); }
                            open = false; complete();
                        });
                    }
                    var failed = false;
                    function fail() {
                        if (failed) return;
                        failed = true;
                        var live = lease.active();
                        var rollbackActive = live && previousActive && previousActive.state !== 'redundant' ? previousActive : null;
                        var rollbackWaiting = live && previousWaiting && previousWaiting.state !== 'redundant' ? previousWaiting : null;
                        if (registration.installing === worker) registration.installing = null;
                        if (registration.waiting === worker) registration.waiting = rollbackWaiting;
                        if (registration.active === worker) registration.active = rollbackActive;
                        var changedController = pageServiceWorkerContainer.controller === worker;
                        if (changedController) pageServiceWorkerContainer.controller = rollbackActive;
                        registration._workers = registration._workers.filter(function(item) { return item !== worker; });
                        if (live && (rollbackWaiting || rollbackActive)) { var retained = rollbackWaiting || rollbackActive; registration._source = retained._source; registration.scriptURL = retained.scriptURL; }
                        else { var index = __folio_service_worker_registrations.indexOf(registration); if (index >= 0) __folio_service_worker_registrations.splice(index, 1); }
                        // Publish a consistent rollback before notifying any page callbacks.
                        worker._retire();
                        if (changedController) { try { pageServiceWorkerContainer.dispatchEvent({ type: 'controllerchange', target: pageServiceWorkerContainer }); } catch (_) { } }
                    }
                    var activationStarted = false;
                    function activate() {
                        if (activationStarted) return;
                        ensureLive(); activationStarted = true;
                        registration.waiting = null; registration.active = worker; state('activating');
                        lifecycle('activate').then(function() {
                            ensureLive();
                            if (registrationStorage) { registrationStorage.write(registrationKey, JSON.stringify({ scope: scope, scriptURL: url })); rememberScope(scope); }
                            ensureLive(); state('activated'); ensureLive();
                            if (previousActive) {
                                if (pageServiceWorkerContainer.controller === previousActive) { pageServiceWorkerContainer.controller = worker; try { pageServiceWorkerContainer.dispatchEvent({ type: 'controllerchange', target: pageServiceWorkerContainer }); } catch (_) { } }
                                previousActive._retire();
                                registration._workers = registration._workers.filter(function(item) { return item !== previousActive; });
                            }
                            if ((claimRequested || isRestoring) && clientInScope()) claimClient();
                            resolveReady(registration);
                        }).catch(fail);
                    }
                    registration.scriptURL = url;
                    registration._source = workerScript;
                    registration.installing = worker;
                    registration._workers.push(worker);
                    registration._updateTo = function(next) {
                        try {
                            var current = registration.waiting || registration.active;
                            if (!current) return Promise.reject(new DOMException('The registration has no live worker.', 'InvalidStateError'));
                            current._ensureLive();
                            if (registration.installing) return Promise.reject(new DOMException('A worker update is already pending.', 'InvalidStateError'));
                            // The new registration call owns its candidate runtime and lease.
                            return pageServiceWorkerContainer.register(next, { scope: scope, updating: true });
                        } catch (error) { return Promise.reject(error); }
                    };
                    registration.update = function() { return registration._updateTo(registration.scriptURL); };
                    if (!existing) __folio_service_worker_registrations.push(registration);
                    var updateEvent = { type: 'updatefound', target: registration };
                    if (registration.onupdatefound) registration.onupdatefound(updateEvent);
                    registration._listeners.slice().forEach(function(handler) { ensureLive(); handler(updateEvent); });
                    ensureLive();
                    if (workerScript === null || workerScript === undefined) throw new TypeError('Service worker script could not be loaded.');
                    if (workerScript !== null && workerScript !== undefined) { if (!isRestoring) { var validation = validator.check(workerScript, url); if (validation === 'timeout') throw new DOMException('Service worker execution exceeded the host limit.', 'TimeoutError'); if (validation !== 'ok') throw new DOMException('Service worker script failed validation.', 'SyntaxError'); } runWorkerScript(workerScript, url); }
                    // A page loaded while a persisted registration is active starts controlled by it, so its
                    // first fetch is routed before the replayed lifecycle completes (no controllerchange).
                    if (isRestoring && clientInScope() && !pageServiceWorkerContainer.controller) pageServiceWorkerContainer.controller = worker;
                    lifecycle('install').then(function() {
                        ensureLive(); registration.installing = null; registration.waiting = worker; state('installed'); ensureLive();
                        if (previousWaiting) { previousWaiting._retire(); registration._workers = registration._workers.filter(function(item) { return item !== previousWaiting; }); }
                        syncNativeRequests();
                        if (!previousActive || !clientInScope() || pageServiceWorkerContainer.controller !== previousActive || skipRequested) activate();
                    }).catch(fail);
                    return Promise.resolve(registration);
                    } catch(error) { if (registration && typeof fail === 'function') fail(); else { if (nativeRuntime) nativeRuntime.retire(); lease.release(); } return Promise.reject(error); }
                },
                getRegistration: function(scope) { var value = String(scope || '/'); try { if (base) { var resolved = new URL(value, base); if (resolved.origin !== new URL(base).origin) return Promise.resolve(undefined); value = resolved.pathname; } } catch (_) { } if (value.length === 0 || value.charAt(0) !== '/') value = '/' + value; if (value.charAt(value.length - 1) !== '/') value += '/'; return Promise.resolve(__folio_service_worker_registrations.find(function(r) { return r.scope === value; })); },
                getRegistrations: function() { return Promise.resolve(__folio_service_worker_registrations.slice()); }
            }; })(registrationStorage, isolatedRuntime);
            var container=globalThis.navigator.serviceWorker;
            Object.defineProperty(globalThis.navigator,'serviceWorker',{configurable:true,get:function(){return authority.exposed()?container:undefined;}});
            authority.onchange(function(){var previous=__folio_service_worker_registrations;__folio_service_worker_registrations=[];for(var i=0;i<previous.length;i++){try{var workers=previous[i]._workers||[];for(var j=0;j<workers.length;j++)workers[j]._retire();previous[i]._workers=[];previous[i].installing=previous[i].waiting=null;}catch(_){}}try{container.controller=null;container.ready=new Promise(function(resolve){resolveReady=resolve;});}catch(_){}});
            })(__folio_service_worker_authority, globalThis.__folio_artifact_storage, __folio_service_worker_loader, __folio_service_worker_validator, __folio_service_worker_base, typeof __folio_service_worker_isolated === 'undefined' ? null : __folio_service_worker_isolated, %BROKERED%);
            delete globalThis.__folio_service_worker_authority;
            delete globalThis.__folio_artifact_storage;
            delete globalThis.__folio_service_worker_loader;
            delete globalThis.__folio_service_worker_validator;
            delete globalThis.__folio_service_worker_isolated;
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
