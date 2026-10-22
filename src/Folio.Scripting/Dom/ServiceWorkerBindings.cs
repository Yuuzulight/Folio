namespace Folio.Scripting.Dom;

using System.Text;
using Folio.Resources;

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
        get { lock (_gate) return !_disposed && _enabled && (_livePermission?.Invoke() ?? true); }
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
        Changed?.Invoke();
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
        changed?.Invoke();
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
                return ScriptValue.FromBoolean(!authority.Enabled ? false : true);
            using var isolated = factory.CreateIsolated();
            isolated.Configure(new ScriptLimits { Timeout = context.MaxWorkerExecutionTime });
            try
            {
                var name = args[1].AsString() ?? "service-worker";
                isolated.Evaluate("globalThis.self = { addEventListener: function() {}, removeEventListener: function() {}, skipWaiting: function() { return Promise.resolve(); }, clients: { claim: function() { return Promise.resolve(); } }, importScripts: function() {} };", name);
                isolated.Evaluate("(function(self) { with (self) { " + args[0].AsString() + " } })(self);", name);
                return ScriptValue.FromBoolean(authority.Enabled);
            }
            catch (ScriptLimitExceededException) { return ScriptValue.FromBoolean(false); }
            catch (ScriptInterruptedException) { return ScriptValue.FromBoolean(false); }
            catch (Exception error) when (error.Message.Contains("timed", StringComparison.OrdinalIgnoreCase) || error.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase) || error.Message.Contains("limit", StringComparison.OrdinalIgnoreCase)) { return ScriptValue.FromBoolean(false); }
            catch { return ScriptValue.FromBoolean(true); }
        });
        engine.SetGlobalProperty("__folio_service_worker_validator", engine.CreateHostObject(validator, authority));
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
        if (artifactStorage is not null)
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
        try { InstallCompatibilityApi(engine, authority, artifactStorage is not null); }
        catch { authority.Dispose(); throw; }
        finally
        {
            engine.SetGlobalProperty("__folio_service_worker_authority", ScriptValue.Undefined);
            engine.SetGlobalProperty("__folio_service_worker_loader", ScriptValue.Undefined);
            engine.SetGlobalProperty("__folio_service_worker_validator", ScriptValue.Undefined);
            engine.SetGlobalProperty("__folio_artifact_storage", ScriptValue.Undefined);
        }
        return authority;
    }

    internal static void UpdateCapabilities(ServiceWorkerCapabilityAuthority authority, FeatureCapabilities capabilities) =>
        authority.UpdateCapabilities(capabilities.ServiceWorkers);

    private static readonly HostObjectTemplate LeaseTemplate = new HostObjectTemplate("ServiceWorkerCapabilityLease")
        .DefineMethod("active", (target, _, _) => ScriptValue.FromBoolean(((ServiceWorkerCapabilityLease)target).IsActive))
        .DefineMethod("release", (target, _, _) => { ((ServiceWorkerCapabilityLease)target).Dispose(); return ScriptValue.Undefined; });

    private static void InstallCompatibilityApi(IScriptEngine engine, ServiceWorkerCapabilityAuthority authority, bool storageEnabled)
    {
        engine.EvaluateHostScript("""
            (function(authority, registrationStorage, loader, validator) {
            globalThis.navigator = globalThis.navigator || {};
            var __folio_service_worker_registrations = [];
            globalThis.navigator.serviceWorker = (function(registrationStorage) { return {
                controller: null,
                oncontrollerchange: null,
                _listeners: [],
                addEventListener: function(type, handler) { if (type === 'controllerchange' && typeof handler === 'function' && this._listeners.indexOf(handler) < 0) this._listeners.push(handler); },
                removeEventListener: function(type, handler) { if (type === 'controllerchange') { var i = this._listeners.indexOf(handler); if (i >= 0) this._listeners.splice(i, 1); } },
                dispatchEvent: function(event) { if (event.type === 'controllerchange') { if (this.oncontrollerchange) this.oncontrollerchange(event); this._listeners.slice().forEach(function(handler) { handler(event); }); } return true; },
                ready: Promise.resolve({ scope: '/', active: null }),
                register: function(scriptURL, options) {
                    if (!authority.enabled()) return Promise.reject(new DOMException('Service workers are disabled by the host.', 'SecurityError'));
                    var url = String(scriptURL); var scope = (options && options.scope) || '/';
                    var registrationKey = 'service-worker-registration/' + encodeURIComponent(String(scope));
                    if (registrationStorage) { var persisted = registrationStorage.read(registrationKey); if (persisted) { try { var metadata = JSON.parse(persisted); if (metadata && metadata.scriptURL === url) url = String(metadata.scriptURL); } catch (_) { } } }
                    var existing = __folio_service_worker_registrations.find(function(r) { return r.scope === scope; });
                    if (existing) { if (existing.scriptURL !== url && typeof existing._setScriptUrl === 'function') existing._setScriptUrl(url); return Promise.resolve(existing); }
                    var lease=authority.acquire(); if(!lease) return Promise.reject(new DOMException('Service workers are disabled by the host.','SecurityError'));
                    function ensureLive(){if(!lease.active())throw new DOMException('The service worker is no longer active.','InvalidStateError');}
                    try { var workerListeners = [];
                    var workerScript = loader.load(url); ensureLive();
                    var workerSelfListeners = [], workerFetchListeners = [], workerLifecycleListeners = { install: [], activate: [] };
                    var workerSelf = { onmessage: null, skipWaiting: function() { ensureLive(); return Promise.resolve(); }, clients: { claim: function() { ensureLive(); navigator.serviceWorker.controller = worker; navigator.serviceWorker.dispatchEvent({ type: 'controllerchange', target: navigator.serviceWorker }); return Promise.resolve(); } }, importScripts: function() { ensureLive(); for (var i = 0; i < arguments.length; i++) { var reference = String(arguments[i]); var resolved = reference; try { resolved = new URL(reference, url).href; } catch (_) { } var imported = loader.load(resolved); ensureLive(); if (imported === null || imported === undefined) throw new Error('Service worker script could not load ' + reference); new Function('self', 'with (self) { ' + imported + ' }')(workerSelf); } }, addEventListener: function(type, handler) { if (typeof handler !== 'function') return; if (type === 'message') workerSelfListeners.push(handler); else if (type === 'fetch') workerFetchListeners.push(handler); else if (workerLifecycleListeners[type]) workerLifecycleListeners[type].push(handler); }, removeEventListener: function(type, handler) { if (type === 'message') workerSelfListeners = workerSelfListeners.filter(function(item) { return item !== handler; }); else if (type === 'fetch') workerFetchListeners = workerFetchListeners.filter(function(item) { return item !== handler; }); else if (workerLifecycleListeners[type]) workerLifecycleListeners[type] = workerLifecycleListeners[type].filter(function(item) { return item !== handler; }); }, postMessage: function(data) { ensureLive(); var event = { type: 'message', data: data, target: worker, source: null, origin: '' }; if (worker.onmessage) worker.onmessage(event); workerListeners.slice().forEach(function(handler) { ensureLive(); handler(event); }); } };
                    var scopePath = String(scope); if (scopePath.length === 0 || scopePath.charAt(0) !== '/') scopePath = '/' + scopePath; if (scopePath.charAt(scopePath.length - 1) !== '/') scopePath += '/';
                    var worker = { scriptURL: url, state: 'installing', onstatechange: null, onmessage: null, _self: workerSelf,
                        addEventListener: function(type, handler) { if (type === 'message' && typeof handler === 'function' && workerListeners.indexOf(handler) < 0) workerListeners.push(handler); },
                        removeEventListener: function(type, handler) { if (type === 'message') { var index = workerListeners.indexOf(handler); if (index >= 0) workerListeners.splice(index, 1); } },
                        postMessage: function(data) { ensureLive(); var event = { type: 'message', data: data, target: workerSelf, source: null, origin: '' }; var handled = !!workerSelf.onmessage || workerSelfListeners.length > 0; if (workerSelf.onmessage) workerSelf.onmessage(event); workerSelfListeners.slice().forEach(function(handler) { ensureLive(); handler(event); }); if (!handled) { if (worker.onmessage) worker.onmessage({ type: 'message', data: data, target: worker, source: null, origin: '' }); workerListeners.slice().forEach(function(handler) { ensureLive(); handler({ type: 'message', data: data, target: worker, source: null, origin: '' }); }); } },
                        _dispatchFetch: function(request) { if (worker._inDispatchFetch) return Promise.resolve(null); return new Promise(function(resolve, reject) { ensureLive(); var requestPath = ''; try { requestPath = new URL(request.url).pathname; } catch (_) { requestPath = String(request.url); } if (requestPath.indexOf(scopePath) !== 0) { resolve(null); return; } var settled = false; var event = { type: 'fetch', request: request, target: workerSelf, respondWith: function(value) { if (settled) return; settled = true; Promise.resolve(value).then(function(response){try{ensureLive();resolve(response);}catch(error){reject(error);}}, reject); } }; try { worker._inDispatchFetch = true; try { if (typeof workerSelf.onfetch === 'function') workerSelf.onfetch(event); workerFetchListeners.slice().forEach(function(handler) { ensureLive(); handler(event); }); } finally { worker._inDispatchFetch = false; } if (!settled) resolve(null); } catch (error) { worker._inDispatchFetch = false; reject(error); } }); }
                    };
                    if (workerScript !== null && workerScript !== undefined) { if (!validator.check(workerScript, url)) throw new DOMException('Service worker execution exceeded the host limit.', 'TimeoutError'); new Function('self', 'with (self) { ' + workerScript + ' }')(workerSelf); }
                    var installEvent = { type: 'install', target: workerSelf, waitUntil: function(promise) { return Promise.resolve(promise); } }; if (workerSelf.oninstall) workerSelf.oninstall(installEvent); workerLifecycleListeners.install.slice().forEach(function(handler) { ensureLive(); handler(installEvent); });
                    var activateEvent = { type: 'activate', target: workerSelf, waitUntil: function(promise) { return Promise.resolve(promise); } }; if (workerSelf.onactivate) workerSelf.onactivate(activateEvent); workerLifecycleListeners.activate.slice().forEach(function(handler) { ensureLive(); handler(activateEvent); });
                    ensureLive(); worker.state = 'activated'; if (worker.onstatechange) worker.onstatechange({ type: 'statechange', target: worker });
                    var registrationListeners = [];
                    var registration = { scope: scope, scriptURL: url, _setScriptUrl: function(next) { ensureLive(); url = String(next); registration.scriptURL = url; if (registrationStorage) registrationStorage.write(registrationKey, JSON.stringify({ scope: scope, scriptURL: url })); }, active: worker, installing: null, waiting: null, onupdatefound: null,
                        addEventListener: function(type, handler) { if (type === 'updatefound' && typeof handler === 'function' && registrationListeners.indexOf(handler) < 0) registrationListeners.push(handler); },
                        removeEventListener: function(type, handler) { if (type === 'updatefound') { var index = registrationListeners.indexOf(handler); if (index >= 0) registrationListeners.splice(index, 1); } },
                        update: function() { try{ensureLive();}catch(error){return Promise.reject(error);} var latest = loader.load(url); try{ensureLive();}catch(error){return Promise.reject(error);} var event = { type: 'updatefound', target: registration }; if (registration.onupdatefound) registration.onupdatefound(event); registrationListeners.slice().forEach(function(handler) { ensureLive(); handler(event); }); try{ensureLive();}catch(error){return Promise.reject(error);} if (latest === workerScript) return Promise.resolve(registration); if (latest === null || latest === undefined) return Promise.reject(new TypeError('Service worker script could not be loaded.')); workerScript = latest; worker.state = 'installing'; registration.installing = worker; if (worker.onstatechange) worker.onstatechange({ type: 'statechange', target: worker }); workerSelfListeners = []; workerFetchListeners = []; workerLifecycleListeners = { install: [], activate: [] }; workerSelf.onmessage = null; workerSelf.onfetch = null; workerSelf.oninstall = null; workerSelf.onactivate = null; if (!validator.check(workerScript, url)) throw new DOMException('Service worker execution exceeded the host limit.', 'TimeoutError'); new Function('self', 'with (self) { ' + workerScript + ' }')(workerSelf); var installEvent = { type: 'install', target: workerSelf, waitUntil: function(promise) { return Promise.resolve(promise); } }; if (workerSelf.oninstall) workerSelf.oninstall(installEvent); workerLifecycleListeners.install.slice().forEach(function(handler) { ensureLive(); handler(installEvent); }); var activateEvent = { type: 'activate', target: workerSelf, waitUntil: function(promise) { return Promise.resolve(promise); } }; if (workerSelf.onactivate) workerSelf.onactivate(activateEvent); workerLifecycleListeners.activate.slice().forEach(function(handler) { ensureLive(); handler(activateEvent); }); ensureLive(); worker.state = 'activated'; registration.installing = null; registration.active = worker; if (worker.onstatechange) worker.onstatechange({ type: 'statechange', target: worker }); navigator.serviceWorker.controller = worker; return Promise.resolve(registration); },
                        unregister: function() { try{ensureLive();}catch(error){return Promise.reject(error);} lease.release(); var i = __folio_service_worker_registrations.indexOf(registration); if (i >= 0) __folio_service_worker_registrations.splice(i, 1); if (registrationStorage) registrationStorage.delete(registrationKey); if (navigator.serviceWorker.controller === worker) { navigator.serviceWorker.controller = null; navigator.serviceWorker.dispatchEvent({ type: 'controllerchange', target: navigator.serviceWorker }); } return Promise.resolve(true); } };
                    ensureLive(); if (registrationStorage) registrationStorage.write(registrationKey, JSON.stringify({ scope: scope, scriptURL: url }));
                    __folio_service_worker_registrations.push(registration); navigator.serviceWorker.controller = worker; navigator.serviceWorker.dispatchEvent({ type: 'controllerchange', target: navigator.serviceWorker }); navigator.serviceWorker.ready = Promise.resolve(registration); return Promise.resolve(registration); } catch(error) {lease.release();return Promise.reject(error);}
                },
                getRegistration: function(scope) { var value = String(scope || '/'); return Promise.resolve(__folio_service_worker_registrations.find(function(r) { return r.scope === value; })); },
                getRegistrations: function() { return Promise.resolve(__folio_service_worker_registrations.slice()); }
            }; })(registrationStorage);
            var container=globalThis.navigator.serviceWorker;
            Object.defineProperty(globalThis.navigator,'serviceWorker',{configurable:true,get:function(){return authority.exposed()?container:undefined;}});
            authority.onchange(function(){var previous=__folio_service_worker_registrations;__folio_service_worker_registrations=[];for(var i=0;i<previous.length;i++){try{if(previous[i].active)previous[i].active.state='redundant';}catch(_){}}try{container.controller=null;container.ready=Promise.resolve(undefined);}catch(_){}});
            })(__folio_service_worker_authority, globalThis.__folio_artifact_storage, __folio_service_worker_loader, __folio_service_worker_validator);
            delete globalThis.__folio_service_worker_authority;
            delete globalThis.__folio_artifact_storage;
            delete globalThis.__folio_service_worker_loader;
            delete globalThis.__folio_service_worker_validator;
            """, "folio:service-worker-compatibility");
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
