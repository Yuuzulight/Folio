using System.Text.Json;

namespace Folio.Scripting.Dom;

internal static class CacheBindings
{
    internal static void Install(IScriptEngine engine, DomContext context, ScriptValue fetchApi, bool registerDisposal = true)
    {
        ICacheStorageBackend? state = null;
        ICacheStorageBackend State() => state ??= context.ArtifactStorage is ICacheStorageBackendProvider provider
            ? provider.CreateCacheBackend(context.ArtifactId, context.MaxServiceWorkerCacheBytes)
            : CacheStorageState.For(context.ArtifactStorage, context.ArtifactId, context.BaseUri);
        if (registerDisposal) context.DisposeCacheStorage = () => state?.Dispose();
        var handle = new HostObjectTemplate("FolioCacheHandle");
        var quota = context.MaxServiceWorkerCacheBytes;
        CacheStorageHandle Cache(ScriptValue value) =>
            engine.TryGetHostTarget(value, out var target) && target is CacheStorageHandle cache && ReferenceEquals(cache.Owner, State())
                ? cache : throw WebIdlException.TypeError("Illegal Cache invocation.");
        string Text(ScriptValue value, string name) => engine.GetProperty(value, name).AsString() ?? "";
        Dictionary<string, string> Headers(ScriptValue value)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!engine.TryGetArrayItems(value, out var pairs)) throw WebIdlException.TypeError("Invalid cache headers.");
            foreach (var pair in pairs)
            {
                if (!engine.TryGetArrayItems(pair, out var items) || items.Count != 2) throw WebIdlException.TypeError("Invalid cache header.");
                result[(items[0].AsString() ?? "").ToLowerInvariant()] = items[1].AsString() ?? "";
            }
            return result;
        }
        CacheStorageState.Request Request(ScriptValue value) =>
            new(Text(value, "url"), Text(value, "method"), Headers(engine.GetProperty(value, "headers")));
        CacheStorageState.Query Query(ScriptValue value) => new(
            engine.GetProperty(value, "ignoreSearch").AsBoolean(), engine.GetProperty(value, "ignoreMethod").AsBoolean(), engine.GetProperty(value, "ignoreVary").AsBoolean());
        CacheStorageState.Entry Entry(ScriptValue value)
        {
            if (!engine.TryGetBytes(engine.GetProperty(value, "bytes"), out var bytes)) throw WebIdlException.TypeError("Invalid cache response body.");
            return new(Request(engine.GetProperty(value, "request")), (int)engine.GetProperty(value, "status").AsNumber(),
                Text(value, "statusText"), Headers(engine.GetProperty(value, "headers")), bytes.ToArray(), engine.GetProperty(value, "bodyNull").AsBoolean(), Text(value, "url"), Text(value, "type"));
        }
        var bridge = new HostObjectTemplate("FolioCacheStore")
            .DefineMethod("open", (_, _, args) => engine.CreateHostObject(handle, State().Open(args[0].AsString() ?? "", quota)))
            .DefineMethod("has", (_, _, args) => ScriptValue.FromBoolean(State().Has(args[0].AsString() ?? "")))
            .DefineMethod("names", (_, _, _) => engine.CreateArray(State().Names().Select(ScriptValue.FromString).ToArray()))
            .DefineMethod("deleteName", (_, _, args) => ScriptValue.FromBoolean(State().Delete(args[0].AsString() ?? "", quota)))
            .DefineMethod("query", (_, _, args) =>
            {
                var cache = args[0].IsNullOrUndefined ? null : Cache(args[0]);
                var request = args[1].IsNullOrUndefined ? null : Request(args[1]);
                var name = engine.GetProperty(args[2], "cacheName");
                var mode = args[3].AsString();
                var entries = State().Match(cache, name.IsUndefined ? null : name.AsString(), request, Query(args[2]) with { RequestOnly = mode == "keys" }, mode == "first");
                return ScriptValue.FromString(mode == "keys" ? JsonSerializer.Serialize(entries.Select(entry => entry.Request)) : JsonSerializer.Serialize(entries));
            })
            .DefineMethod("put", (_, _, args) =>
            {
                if (!engine.TryGetArrayItems(args[1], out var entries)) throw WebIdlException.TypeError("Invalid cache batch.");
                State().Put(Cache(args[0]), entries.Select(Entry).ToArray(), args[2].AsBoolean(), quota);
                return ScriptValue.Undefined;
            })
            .DefineMethod("deleteEntry", (_, _, args) => ScriptValue.FromBoolean(State().DeleteEntries(Cache(args[0]), Request(args[1]), Query(args[2]), quota)));
        var guarded = new HostObjectTemplate("FolioCacheStore");
        foreach (var name in bridge.MethodNames)
        {
            bridge.TryGetMethod(name, out var callback);
            guarded.DefineMethod(name, (target, receiver, args) =>
            {
                try { return engine.CreateArray([ScriptValue.FromBoolean(true), callback(target, receiver, args)]); }
                catch (WebIdlException error)
                {
                    return engine.CreateArray([ScriptValue.FromBoolean(false), ScriptValue.FromString(error.Kind.ToString()),
                        ScriptValue.FromString(error.Name), ScriptValue.FromString(error.Message)]);
                }
            });
        }
        engine.SetGlobalProperty("__folio_cache_store", engine.CreateHostObject(guarded, context));
        engine.SetGlobalProperty("__folio_cache_fetch_api", fetchApi);
        engine.EvaluateHostScript(Source, "folio:cache");
        engine.Evaluate("delete globalThis.__folio_cache_store; delete globalThis.__folio_cache_fetch_api; delete globalThis.__folio_artifact_storage;");
    }

    private const string Source = """
        (function(rawHost, api) {
            'use strict';
            const apply = Reflect.apply, create = Object.create, DOMExceptionCtor = DOMException;
            const host = create(null);
            function bind(name, callback) {
                host[name] = function() {
                    const result = callback(...arguments);
                    if (result[0]) return result[1];
                    throw result[1] === 'DOMException' ? new DOMExceptionCtor(result[3], result[2]) : new TypeErrorCtor(result[3]);
                };
            }
            bind('open', rawHost.open);
            bind('has', rawHost.has);
            bind('names', rawHost.names);
            bind('deleteName', rawHost.deleteName);
            bind('query', rawHost.query);
            bind('put', rawHost.put);
            bind('deleteEntry', rawHost.deleteEntry);
            const PromiseCtor = Promise, StringCtor = String, TypeErrorCtor = TypeError;
            const then = Promise.prototype.then, resolve = Promise.resolve, reject = Promise.reject, all = Promise.all;
            const arrayFrom = Array.from, ArrayCtor = Array, parse = JSON.parse;
            const weakGet = WeakMap.prototype.get, weakSet = WeakMap.prototype.set;
            const fetchResource = fetch, states = new WeakMap(), INTERNAL = {};
            const settled = value => PromiseCtor.resolve(value);
            const failed = error => PromiseCtor.reject(error);
            const after = (promise, callback) => promise.then(callback);
            function invoke(callback) { try { return settled(callback()); } catch (error) { return failed(error); } }
            function state(cache) {
                const value = states.get(cache);
                if (!value || value === INTERNAL) throw new TypeErrorCtor('Illegal Cache invocation');
                return value;
            }
            function storage(value) {
                if (states.get(value) !== INTERNAL) throw new TypeErrorCtor('Illegal CacheStorage invocation');
            }
            function options(value) {
                if (value !== undefined && value !== null && typeof value !== 'object' && typeof value !== 'function')
                    throw new TypeErrorCtor('Cache query options must be an object');
                const result = create(null);
                result.ignoreSearch = !!(value && value.ignoreSearch);
                result.ignoreMethod = !!(value && value.ignoreMethod);
                result.ignoreVary = !!(value && value.ignoreVary);
                const name = value && value.cacheName;
                result.cacheName = name === undefined || value == null ? undefined : StringCtor(name);
                return result;
            }
            function request(value) { return api.request(value); }
            function validate(request) {
                if (request.method !== 'GET' || !/^https?:\/\//i.test(request.url))
                    throw new TypeErrorCtor('Only HTTP(S) GET requests can be cached');
            }
            function entry(request, response, fetchOnly) {
                validate(request);
                return api.response(request, response, fetchOnly);
            }
            function responses(text) {
                const values = parse(text), result = [];
                for (let i = 0; i < values.length; i++) result[i] = api.restore(values[i]);
                return result;
            }
            class Cache {
                constructor(token, value) {
                    if (token !== INTERNAL) throw new TypeErrorCtor('Illegal constructor');
                    states.set(this, value);
                }
                match(input, opts) { if (!arguments.length) return failed(new TypeErrorCtor('A request is required')); return invoke(() => responses(host.query(state(this), request(input), options(opts), "first"))[0]); }
                matchAll(input, opts) { return invoke(() => responses(host.query(state(this), input === undefined ? undefined : request(input), options(opts), "all"))); }
                keys(input, opts) {
                    return invoke(() => {
                        const records = parse(host.query(state(this), input === undefined ? undefined : request(input), options(opts), "keys"));
                        const result = [];
                        for (let i = 0; i < records.length; i++) result[i] = api.restoreRequest(records[i]);
                        return result;
                    });
                }
                put(input, response) {
                    if (arguments.length < 2) return failed(new TypeErrorCtor('A request and response are required'));
                    return invoke(() => {
                        const cache = state(this), value = entry(request(input), response, false);
                        host.put(cache, [value], false);
                    });
                }
                delete(input, opts) { if (!arguments.length) return failed(new TypeErrorCtor('A request is required')); return invoke(() => host.deleteEntry(state(this), request(input), options(opts))); }
                add(input) { if (!arguments.length) return failed(new TypeErrorCtor('A request is required')); return apply(nativeAddAll, this, [[input]]); }
                addAll(inputs) {
                    let cache, requests;
                    try {
                        cache = state(this); requests = ArrayCtor.from(inputs);
                        for (let i = 0; i < requests.length; i++) { requests[i] = request(requests[i]); validate(requests[i]); }
                    } catch (error) { return failed(error); }
                    const pending = [];
                    for (let i = 0; i < requests.length; i++) {
                        const current = requests[i];
                        pending[i] = after(fetchResource(api.restoreRequestLower(current)), response => entry(current, response, true));
                    }
                    return after(PromiseCtor.all(pending), entries => { host.put(cache, entries, true); });
                }
            }
            const nativeAddAll = Cache.prototype.addAll;
            class CacheStorage {
                constructor(token) {
                    if (token !== INTERNAL) throw new TypeErrorCtor('Illegal constructor');
                    states.set(this, INTERNAL);
                }
                open(name) { if (!arguments.length) return failed(new TypeErrorCtor("A cache name is required")); return invoke(() => { storage(this); return new Cache(INTERNAL, host.open(StringCtor(name))); }); }
                has(name) { if (!arguments.length) return failed(new TypeErrorCtor("A cache name is required")); return invoke(() => { storage(this); return host.has(StringCtor(name)); }); }
                keys() { return invoke(() => { storage(this); return host.names(); }); }
                delete(name) { if (!arguments.length) return failed(new TypeErrorCtor("A cache name is required")); return invoke(() => { storage(this); return host.deleteName(StringCtor(name)); }); }
                match(input, opts) { if (!arguments.length) return failed(new TypeErrorCtor('A request is required')); return invoke(() => { storage(this); return responses(host.query(undefined, request(input), options(opts), "first"))[0]; }); }
            }
            globalThis.Cache = Cache;
            globalThis.CacheStorage = CacheStorage;
            Object.defineProperty(Cache.prototype, Symbol.toStringTag, {value:'Cache',configurable:true});
            Object.defineProperty(CacheStorage.prototype, Symbol.toStringTag, {value:'CacheStorage',configurable:true});
            globalThis.caches = new CacheStorage(INTERNAL);
        })(__folio_cache_store, __folio_cache_fetch_api);
        """;
}
