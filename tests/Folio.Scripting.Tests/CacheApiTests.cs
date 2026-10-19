using Folio.Resources;
using Folio.Scripting;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

public sealed class CacheApiTests
{
    [Fact]
    public void LegacyEntriesMigrateAtomicallyAndRetainTheirBackup()
    {
        var storage = new Storage();
        const string key = "test:service-worker-cache/offline/%2Fold";
        storage.Values[key] = "{\"bytes\":[65,0,255],\"status\":201,\"statusText\":\"Created\",\"headers\":[[\"content-type\",\"application/octet-stream\"]]}";
        using (var page = new Page(storage))
        {
            page.Run("let migrated = ''; caches.open('offline').then(c => c.match('/old')).then(r => r.bytes()).then(bytes => migrated = Array.from(bytes).join(','));");
            Assert.Equal("65,0,255", page.Js("migrated").AsString());
        }
        Assert.Contains(CacheStorageState.StorageKey("test"), storage.Values.Keys);
        Assert.Contains(key, storage.Values.Keys);
        using var reopened = new Page(storage.Copy());
        reopened.Run("let found = false; caches.match('/old').then(r => found = r.status === 201);");
        Assert.True(reopened.Js("found").AsBoolean());
    }

    [Fact]
    public void FailedLegacyMigrationPreservesOriginalEntriesAndCanBeRetried()
    {
        var storage = new Storage();
        const string key = "test:service-worker-cache/offline/%2Fold";
        storage.Values[key] = "{\"bytes\":[65],\"status\":200,\"statusText\":\"OK\",\"headers\":[]}";
        storage.FailNextWrite = true;
        using var page = new Page(storage);
        page.Run("let failure = ''; caches.keys().catch(error => failure = error.name);");
        Assert.Equal("UnknownError", page.Js("failure").AsString());
        Assert.Single(storage.Values);
        page.Run("let retried = ''; caches.match('/old').then(r => r.text()).then(text => retried = text);");
        Assert.Equal("A", page.Js("retried").AsString());
        using var retry = new Page(storage.Copy());
        retry.Run("let restored = ''; caches.match('/old').then(r => r.text()).then(text => restored = text);");
        Assert.Equal("A", retry.Js("restored").AsString());
    }

    [Fact]
    public void LegacyVaryWithoutRequestHeadersFailsWithoutDroppingData()
    {
        var storage = new Storage();
        const string key = "test:service-worker-cache/offline/%2Fold";
        storage.Values[key] = "{\"bytes\":[65],\"status\":200,\"statusText\":\"OK\",\"headers\":[[\"vary\",\"accept-language\"]]}";
        using var page = new Page(storage);
        page.Run("let failure = ''; caches.keys().catch(error => failure = error.name);");
        Assert.Equal("UnknownError", page.Js("failure").AsString());
        Assert.Single(storage.Values);
        Assert.Contains(key, storage.Values.Keys);
    }

    [Fact]
    public void HostCallbackCannotPublishAReentrantCacheMutation()
    {
        var storage = new Storage();
        var state = CacheStorageState.For(storage, "reentrant");
        var cache = state.Open("stable", 65536);
        string? failure = null;
        storage.Writing = () =>
        {
            try { state.Open("nested", 65536); }
            catch (WebIdlException error) { failure = error.Name; }
        };
        var request = new CacheStorageState.Request("https://cache.example/a", "GET", []);
        state.Put(cache, [new(request, 200, "OK", [], [42], false, "", "default")], false, 65536);
        Assert.Equal("UnknownError", failure);
        Assert.False(state.Has("nested"));
        Assert.Single(state.Match(cache, null, request, new(false, false, false), true));
    }

    [Fact]
    public void EmptyCacheNamesPersistAndDeletingANameDoesNotInvalidateExistingHandles()
    {
        var storage = new Storage();
        using (var page = new Page(storage))
        {
            page.Run("""
                let oldCache, freshCache, oldText = '', freshMissing = false;
                caches.open('empty').then(() => caches.open('data')).then(cache => {
                    oldCache = cache; return cache.put('/old', new Response('old'));
                }).then(() => caches.delete('data')).then(() => oldCache.put('/later', new Response('later')))
                  .then(() => caches.open('data')).then(cache => {
                      freshCache = cache; return cache.match('/later');
                  }).then(response => { freshMissing = response === undefined; return oldCache.match('/later'); })
                  .then(response => response.text()).then(text => oldText = text);
                """);
            Assert.True(page.Js("freshMissing && oldText === 'later'").AsBoolean());
        }
        using var reopened = new Page(storage.Copy());
        reopened.Run("let names; caches.keys().then(value => names = value);");
        Assert.True(reopened.Js("names.join(',') === 'empty,data'").AsBoolean());
    }

    [Fact]
    public void MatchingUsesCanonicalUrlsMethodsVaryAndQueryOptions()
    {
        using var page = new Page();
        page.Run("""
            let good = '', noLanguage = false, ignored = '', search = '', noPost = false, post = '';
            caches.open('variants').then(async cache => {
                await cache.put(new Request('/asset?q=1#saved', {headers:{'accept-language':'en'}}),
                    new Response('english', {headers:{vary:'Accept-Language'}}));
                good = await (await cache.match(new Request('https://cache.example/asset?q=1#other', {headers:{'accept-language':'en'}}))).text();
                noLanguage = await cache.match('/asset?q=1') === undefined;
                ignored = await (await cache.match('/asset?q=1', {ignoreVary:true})).text();
                search = await (await cache.match('/asset?q=2', {ignoreSearch:true,ignoreVary:true})).text();
                noPost = await cache.match(new Request('/asset?q=1', {method:'POST'})) === undefined;
                post = await (await cache.match(new Request('/asset?q=1', {method:'POST'}), {ignoreMethod:true,ignoreVary:true})).text();
            });
            """);
        Assert.True(page.Js("good === 'english' && noLanguage && ignored === 'english' && search === 'english' && noPost && post === 'english'").AsBoolean());
    }

    [Fact]
    public void FailedAddAllDoesNotPublishAnEarlierSuccessfulFetch()
    {
        var storage = new Storage();
        using var page = new Page(storage, loader: new Loader());
        page.Run("""
            let failed = false, entries = -1;
            caches.open('batch').then(async cache => {
                try { await cache.addAll(['/good', '/refused']); } catch (_) { failed = true; }
                entries = (await cache.keys()).length;
            });
            """);
        Assert.True(page.Js("failed && entries === 0").AsBoolean());
    }

    [Fact]
    public void QuotaIncludesPersistedCachesThatHaveNotBeenOpened()
    {
        var storage = new Storage();
        using (var first = new Page(storage, quota: 100000))
            first.Run("caches.open('old').then(cache => cache.put('/large', new Response('x'.repeat(4000))));");
        using var second = new Page(storage.Copy(), quota: 6000);
        second.Run("""
            let refused = false;
            caches.open('new').then(cache => cache.put('/other', new Response('y'.repeat(4000))))
                .catch(error => refused = error.name === 'QuotaExceededError');
            """);
        Assert.True(second.Js("refused").AsBoolean());
    }

    [Fact]
    public void CacheStorageBridgeCannotBypassTheHostQuota()
    {
        using var page = new Page(new Storage());
        Assert.True(page.Js("typeof __folio_artifact_storage === 'undefined'").AsBoolean());
    }

    [Fact]
    public void AllMethodsPreserveOrderingVariantsAndIndependentResponseBodies()
    {
        using var page = new Page(loader: new Loader());
        page.Run("""
            let complete = false;
            caches.open('first').then(async cache => {
                const other = await caches.open('second');
                await cache.add('/good');
                await cache.put('/value', new Response('first'));
                await other.put('/value', new Response('second'));
                const one = await cache.match('/value'), two = await cache.match('/value');
                one.headers.set('test', 'changed');
                const independent = (await one.text()) === 'first' && (await two.text()) === 'first' && two.headers.get('test') === null;
                const all = await cache.matchAll();
                const keys = await cache.keys();
                const order = keys.map(request => request.url).join(',');
                const global = await (await caches.match('/value')).text();
                const named = await (await caches.match('/value', {cacheName:'second'})).text();
                const missing = await caches.match('/value', {cacheName:'missing'});
                const deleted = await cache.delete('/value');
                const absent = await cache.match('/value');
                complete = independent && all.length === 2 && all.every(value => value instanceof Response) &&
                    order === 'https://cache.example/good,https://cache.example/value' && global === 'first' && named === 'second' &&
                    missing === undefined && deleted && absent === undefined && await caches.has('first') &&
                    (await caches.keys()).join(',') === 'first,second' && await caches.delete('second') && !await caches.delete('second');
            });
            """);
        Assert.True(page.Js("complete").AsBoolean());
    }

    [Fact]
    public void PutValidatesMethodsSchemesStatusesVaryAndConsumedBodies()
    {
        using var page = new Page();
        page.Run("""
            let rejected = 0, consumed = false, empty = false;
            caches.open('validation').then(async cache => {
                const invalid = [
                    [new Request('/post', {method:'POST'}), new Response('x')],
                    [new Request('/head', {method:'HEAD'}), new Response('x')],
                    ['data:text/plain,x', new Response('x')],
                    ['/partial', new Response('x', {status:206})],
                    ['/vary', new Response('x', {headers:{vary:'accept, *'}})]
                ];
                for (const pair of invalid) {
                    try { await cache.put(pair[0], pair[1]); } catch (error) { if (error instanceof TypeError) rejected++; }
                }
                const used = new Response('used'); await used.text();
                try { await cache.put('/used', used); } catch (error) { if (error instanceof TypeError) rejected++; }
                empty = (await cache.keys()).length === 0;
                const valid = new Response('stored');
                await cache.put('/valid', valid); consumed = valid.bodyUsed;
            });
            """);
        Assert.True(page.Js("rejected === 6 && empty && consumed").AsBoolean());
    }

    [Fact]
    public void AddAllDuplicateRequestsAndNonSuccessfulResponsesLeaveNoPartialEntries()
    {
        using var page = new Page(loader: new Loader());
        page.Run("""
            let duplicate = '', after = -1;
            caches.open('duplicates').then(async cache => {
                try { await cache.addAll(['/good', '/good#same']); } catch (error) { duplicate = error.name; }
                after = (await cache.keys()).length;
            });
            """);
        Assert.True(page.Js("duplicate === 'InvalidStateError' && after === 0").AsBoolean());
    }

    [Fact]
    public void StorageWriteFailureLeavesThePreviousMemoryAndPersistentSnapshotIntact()
    {
        var storage = new Storage();
        using var page = new Page(storage);
        page.Run("caches.open('atomic').then(cache => cache.put('/x', new Response('before')));");
        var snapshot = storage.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
        storage.FailNextWrite = true;
        page.Run("""
            let failure = '', current = '';
            caches.open('atomic').then(async cache => {
                try { await cache.put('/x', new Response('after')); } catch (error) { failure = error.name; }
                current = await (await cache.match('/x')).text();
            });
            """);
        Assert.True(page.Js("failure === 'UnknownError' && current === 'before'").AsBoolean());
        Assert.Equal(snapshot, storage.Values);
        using var reopened = new Page(storage.Copy());
        reopened.Run("let saved = ''; caches.open('atomic').then(c => c.match('/x')).then(r => r.text()).then(v => saved = v);");
        Assert.Equal("before", reopened.Js("saved").AsString());
    }

    [Fact]
    public void QuotaFailureReplacesNothingAndConcurrentContextsShareTheSameLedger()
    {
        var storage = new Storage();
        using var first = new Page(storage, quota: 5000);
        using var second = new Page(storage, quota: 5000);
        first.Run("caches.open('a').then(cache => cache.put('/one', new Response('x'.repeat(2000))));");
        second.Run("""
            let refused = false, count = -1;
            caches.open('b').then(async cache => {
                try { await cache.put('/two', new Response('y'.repeat(2000))); } catch (error) { refused = error.name === 'QuotaExceededError'; }
                count = (await cache.keys()).length;
            });
            """);
        Assert.True(second.Js("refused && count === 0").AsBoolean());
        first.Run("let unchanged = 0; caches.open('a').then(c => c.match('/one')).then(r => r.text()).then(v => unchanged = v.length);");
        Assert.Equal(2000, first.Js("unchanged").AsNumber());
    }

    [Fact]
    public void PersistentMetadataVaryAndBinaryBodiesRoundTripThroughASeparateStorageInstance()
    {
        var storage = new Storage();
        using (var page = new Page(storage)) page.Run("""
            caches.open('saved').then(async cache => {
                await cache.put(new Request('/v', {headers:{'accept-language':'en'}}), new Response(new Uint8Array([0,128,255]), {status:201,statusText:'Created',headers:{vary:'accept-language','x-saved':'yes'}}));
                await cache.put(new Request('/v', {headers:{'accept-language':'fr'}}), new Response('fr', {headers:{vary:'accept-language'}}));
            });
            """);
        using var reopened = new Page(storage.Copy());
        reopened.Run("""
            let complete = false;
            caches.open('saved').then(async cache => {
                const response = await cache.match(new Request('/v', {headers:{'accept-language':'en'}}));
                const metadata = response.status === 201 && response.statusText === 'Created' && response.headers.get('x-saved') === 'yes';
                const bytes = new Uint8Array(await response.arrayBuffer());
                const keys = await cache.keys('/v', {ignoreVary:true});
                complete = metadata && bytes.join(',') === '0,128,255' && keys.length === 2 && keys[0].headers.get('accept-language') === 'en' && keys[1].headers.get('accept-language') === 'fr';
            });
            """);
        Assert.True(reopened.Js("complete").AsBoolean());
    }

    [Fact]
    public void ArtifactIdsAndNamesCannotCollideAcrossSharedHostStorage()
    {
        var storage = new Storage();
        using var first = new Page(storage, artifact: "a");
        using var second = new Page(storage, artifact: "a:b");
        using var third = new Page(storage, artifact: "a/b");
        first.Run("caches.open('b:c').then(c => c.put('/x', new Response('first')));");
        second.Run("caches.open('c').then(c => c.put('/x', new Response('second')));");
        third.Run("caches.open('c').then(c => c.put('/x', new Response('third')));");
        first.Run("let value; caches.match('/x').then(r => r.text()).then(v => value = v);");
        second.Run("let value; caches.match('/x').then(r => r.text()).then(v => value = v);");
        third.Run("let value; caches.match('/x').then(r => r.text()).then(v => value = v);");
        Assert.Equal("first", first.Js("value").AsString());
        Assert.Equal("second", second.Js("value").AsString());
        Assert.Equal("third", third.Js("value").AsString());
        Assert.Equal(3, storage.Values.Count);
    }

    [Fact]
    public void MalformedPersistentDataFailsClosedInsteadOfDisappearingFromQuota()
    {
        var storage = new Storage();
        using (var first = new Page(storage)) first.Run("caches.open('saved').then(c => c.put('/x', new Response('data')));");
        var copy = storage.Copy();
        copy.Values[copy.Values.Keys.Single()] = "{bad snapshot";
        using var reopened = new Page(copy);
        reopened.Run("let failure; caches.open('new').catch(error => failure = error.name);");
        Assert.Equal("UnknownError", reopened.Js("failure").AsString());
        Assert.Equal("{bad snapshot", copy.Values.Values.Single());
    }

    [Fact]
    public void BatchStorageFailureAndQuotaRejectionPublishNoEntries()
    {
        var storage = new Storage();
        using var page = new Page(storage, loader: new Loader());
        page.Run("caches.open('batch');");
        var prior = storage.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
        storage.FailNextWrite = true;
        page.Run("""
            let failed = '', count = -1;
            caches.open('batch').then(async cache => {
                try { await cache.addAll(['/good', '/other']); } catch (error) { failed = error.name; }
                count = (await cache.keys()).length;
            });
            """);
        Assert.True(page.Js("failed === 'UnknownError' && count === 0").AsBoolean());
        Assert.Equal(prior, storage.Values);
        using var small = new Page(quota: 1000);
        small.Run("""
            let rejected = '', old = '';
            caches.open('small').then(async cache => {
                await cache.put('/x', new Response('before'));
                globalThis.__folio_service_worker_cache_bytes = Infinity;
                globalThis.DOMException = function() { throw new Error('overridden'); };
                try { await cache.put('/x', new Response('x'.repeat(2000))); } catch (error) { rejected = error.name; }
                old = await (await cache.match('/x')).text();
            });
            """);
        Assert.True(small.Js("rejected === 'QuotaExceededError' && old === 'before'").AsBoolean());
    }

    [Fact]
    public void CacheConstructorsAndReceiverBrandsAreCheckedAndAddUsesItsInternalAlgorithm()
    {
        using var page = new Page(loader: new Loader());
        page.Run("""
            let illegal = 0, good = '';
            try { new Cache(); } catch (error) { if (error instanceof TypeError) illegal++; }
            try { new CacheStorage(); } catch (error) { if (error instanceof TypeError) illegal++; }
            caches.open('brands').then(async cache => {
                try { await CacheStorage.prototype.has.call(cache, 'brands'); } catch (error) { if (error instanceof TypeError) illegal++; }
                try { await Cache.prototype.match.call(caches, '/x'); } catch (error) { if (error instanceof TypeError) illegal++; }
                cache.addAll = () => { throw new Error('public override'); };
                await cache.add('/good');
                good = await (await cache.match('/good')).text();
            });
            """);
        Assert.True(page.Js("illegal === 4 && good === 'success' && Object.prototype.toString.call(caches) === '[object CacheStorage]'").AsBoolean());
    }

    [Fact]
    public void PutUsesRealResponseStateAndPreservesFetchedResponseMetadata()
    {
        using var page = new Page(loader: new Loader());
        page.Run("""
            let safe = false;
            caches.open('native-state').then(async cache => {
                const response = new Response('body', {status:202,statusText:'Accepted'});
                Object.defineProperty(response, 'status', { get() { throw new Error('fake getter'); } });
                response.clone = response.arrayBuffer = () => { throw new Error('public override'); };
                await cache.put('/own', response);
                const stored = await cache.match('/own');
                await cache.add('/good');
                const fetched = await cache.match('/good');
                safe = stored.status === 202 && stored.statusText === 'Accepted' && (await stored.text()) === 'body' &&
                    fetched.url === 'https://cache.example/good' && fetched.type === 'basic';
            });
            """);
        Assert.True(page.Js("safe").AsBoolean());
    }

    [Fact]
    public void NullBodiesAndNetworkErrorResponsesKeepTheirNativeSemantics()
    {
        using var page = new Page();
        page.Run("""
            let correct = false;
            caches.open('null-body').then(async cache => {
                const empty = new Response(null, {status:204});
                await cache.put('/empty', empty);
                const error = Response.error();
                await cache.put('/error', error);
                const savedEmpty = await cache.match('/empty'), savedError = await cache.match('/error');
                const text = await savedEmpty.text();
                correct = !empty.bodyUsed && !error.bodyUsed && savedEmpty.status === 204 && text === '' &&
                    !savedEmpty.bodyUsed && savedError.status === 0 && savedError.type === 'error' && !savedError.bodyUsed;
            });
            """);
        Assert.True(page.Js("correct").AsBoolean());
    }

    [Fact]
    public void AbortedBatchRequestsRejectBeforeCallingTheLoader()
    {
        var loader = new Loader();
        using var page = new Page(loader: loader);
        page.Run("""
            let failure = '', empty = false;
            caches.open('abort').then(async cache => {
                const controller = new AbortController(); controller.abort();
                try { await cache.addAll([new Request('/good', {signal:controller.signal})]); }
                catch (error) { failure = error.name; }
                empty = (await cache.keys()).length === 0;
            });
            """);
        Assert.True(page.Js("failure === 'AbortError' && empty").AsBoolean());
        Assert.Equal(0, loader.Calls);
    }

    [Fact]
    public void NonSuccessfulFetchedResponsesRejectTheWholeBatch()
    {
        using var page = new Page(loader: new StatusLoader(), serviceWorkers: true);
        page.Run("""
            let failure = '', empty = false;
            navigator.serviceWorker.register('/sw.js').then(() => caches.open('status')).then(async cache => {
                try { await cache.addAll(['/good', '/unavailable']); } catch (error) { failure = error.name; }
                empty = (await cache.keys()).length === 0;
            });
            """);
        Assert.True(page.Js("failure === 'TypeError' && empty").AsBoolean());
    }

    [Fact]
    public void MissingArgumentsAndPrimitiveQueryOptionsRejectWithoutMutatingStores()
    {
        using var page = new Page();
        page.Run("""
            let failures = 0;
            caches.open('args').then(async cache => {
                const operations = [() => caches.open(), () => caches.has(), () => caches.delete(), () => caches.match(),
                    () => cache.match(), () => cache.put(), () => cache.add(), () => cache.delete(), () => cache.match('/x', 0)];
                for (const operation of operations) {
                    try { await operation(); } catch (error) { if (error instanceof TypeError) failures++; }
                }
            });
            """);
        Assert.Equal(9, page.Js("failures").AsNumber());
    }

    [Fact]
    public void EmptyBatchDoesNotWriteAndBatchQuotaFailureStoresNothing()
    {
        var storage = new Storage();
        using var page = new Page(storage);
        page.Run("caches.open('empty');");
        storage.FailNextWrite = true;
        page.Run("let emptySucceeded = false; caches.open('empty').then(c => c.addAll([])).then(() => emptySucceeded = true);");
        Assert.True(page.Js("emptySucceeded").AsBoolean());
        Assert.True(storage.FailNextWrite);
        using var small = new Page(quota: 1500, loader: new Loader(700));
        small.Run("""
            let quota = '', count = -1;
            caches.open('batch-quota').then(async cache => {
                try { await cache.addAll(['/good', '/other']); } catch (error) { quota = error.name; }
                count = (await cache.keys()).length;
            });
            """);
        Assert.True(small.Js("quota === 'QuotaExceededError' && count === 0").AsBoolean());
    }

    [Fact]
    public void FailedNameDeletionLeavesNamedCacheAndItsDataAccessible()
    {
        var storage = new Storage();
        using var page = new Page(storage);
        page.Run("caches.open('keep').then(c => c.put('/x', new Response('saved')));");
        storage.FailNextWrite = true;
        page.Run("""
            let failed = '', exists = false, text = '';
            caches.delete('keep').catch(error => failed = error.name).then(async () => {
                exists = await caches.has('keep'); text = await (await caches.match('/x')).text();
            });
            """);
        Assert.True(page.Js("failed === 'UnknownError' && exists && text === 'saved'").AsBoolean());
    }

    [Fact]
    public void LiveDeletedHandlesCannotBeUsedToBypassMemoryQuota()
    {
        using var page = new Page(quota: 3000);
        page.Run("""
            let oldCache, refused = '';
            caches.open('old').then(async cache => {
                oldCache = cache;
                await cache.put('/old', new Response('x'.repeat(1000)));
                await caches.delete('old');
                const fresh = await caches.open('fresh');
                try { await fresh.put('/new', new Response('y'.repeat(1000))); } catch (error) { refused = error.name; }
            });
            """);
        Assert.Equal("QuotaExceededError", page.Js("refused").AsString());
        page.Run("let retained = 0; oldCache.match('/old').then(r => r.text()).then(v => retained = v.length);");
        Assert.Equal(1000, page.Js("retained").AsNumber());
    }

    private sealed class StatusLoader : IResourceLoader
    {
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<ResourceResponse?>(ResourceResponse.Loaded(System.Text.Encoding.UTF8.GetBytes("""
                self.addEventListener('fetch', event => event.respondWith(new Response('body', {
                    status: event.request.url.endsWith('/unavailable') ? 503 : 200
                })));
                """), "text/javascript"));
    }

    private sealed class Loader(int bodyLength = 0) : IResourceLoader
    {
        internal int Calls;
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<ResourceResponse?>(new Uri(request.Url).AbsolutePath is "/good" or "/other"
                ? ResourceResponse.Loaded(System.Text.Encoding.UTF8.GetBytes(bodyLength == 0 ? "success" : new string('x', bodyLength)), "text/plain")
                : ResourceResponse.Refused("Test refusal"));
        }
    }

    private sealed class Storage : IArtifactStorage
    {
        internal readonly Dictionary<string, string> Values = new(StringComparer.Ordinal);
        internal bool FailNextWrite;
        internal Action? Writing;
        public string? Read(string key) => Values.GetValueOrDefault(key);
        public void Write(string key, string value)
        {
            if (FailNextWrite) { FailNextWrite = false; throw new IOException("Test storage failure"); }
            Writing?.Invoke();
            Values[key] = value;
        }
        public void Delete(string key) => Values.Remove(key);
        public IReadOnlyList<string> Keys(string prefix) => Values.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        internal Storage Copy() { var result = new Storage(); foreach (var pair in Values) result.Values.Add(pair.Key, pair.Value); return result; }
    }

    private sealed class Page : IDisposable
    {
        internal readonly JintScriptEngine Engine = new();
        private readonly Document _document;
        private readonly HtmlEventLoop _loop;
        internal Page(IArtifactStorage? storage = null, long quota = 64 * 1024 * 1024, IResourceLoader? loader = null, string artifact = "test", bool serviceWorkers = false)
        {
            _document = Document.Parse("<html></html>", new FolioOptions
            {
                BaseUri = new Uri("https://cache.example/app/"), ArtifactId = artifact, ArtifactStorage = storage,
                ResourceLoader = loader, Capabilities = new FeatureCapabilities { ServiceWorkers = serviceWorkers },
                Limits = ResourceLimits.Default with { MaxServiceWorkerCacheBytes = quota }
            });
            _loop = new HtmlEventLoop(Engine);
            DomBindings.Install(Engine, _document, eventLoop: _loop);
            ScriptGlobalsInstaller.Install(_loop, Engine);
        }
        internal ScriptValue Js(string source) => Engine.Evaluate(source);
        internal void Run(string source)
        {
            Engine.Evaluate(source);
            for (var i = 0; i < 50; i++) { Engine.PerformMicrotaskCheckpoint(); _loop.SpinUntilIdle(); }
        }
        public void Dispose() { Engine.Dispose(); _loop.Dispose(); _document.Dispose(); }
    }
}
