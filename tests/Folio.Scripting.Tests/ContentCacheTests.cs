using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using System.Runtime.CompilerServices;
using Folio.Scripting.Dom;
using Folio.Scripting.Ipc;
using Folio.Resources;

namespace Folio.Scripting.Tests;

// Round-trip deadlines include the parent process. The large-payload/forced-GC
// cases must not pause unrelated fixtures through a child's script deadline.
[Collection("Content process round trips")]
public sealed class ContentCacheTests
{
    [Fact]
    public async Task FrameCarriesDocumentTaskFailuresInsteadOfSilentlyHidingThem()
    {
        await using var client = Client(new Storage());
        var completed = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += (message, _) =>
        {
            if (message.Kind == ContentMessageKind.Frame) completed.TrySetResult(message);
            return ValueTask.CompletedTask;
        };
        await client.SendAsync(ContentMessageKind.Load, new
        {
            html = "<script>queueMicrotask(() => { throw new Error('reported-task-failure'); });</script>",
            script = "42", url = "https://cache.example/"
        }, TestContext.Current.CancellationToken);
        var frame = await completed.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.Equal("42", frame.Payload.GetProperty("value").GetString());
        Assert.Contains(frame.Payload.GetProperty("diagnostics").EnumerateArray(), diagnostic => diagnostic.GetString()!.Contains("reported-task-failure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ServiceWorkerCacheWritesReachTheHostAndSurviveProcessRestart()
    {
        var storage = new Storage();
        await using (var first = Client(storage, loader: new WorkerLoader()))
            Assert.Equal("worker-body", await Load(first, "await navigator.serviceWorker.register('/sw.js'); result = await (await caches.match('/from-worker')).text();", true));
        await using var reopened = Client(storage);
        Assert.Equal("worker-body", await Load(reopened, "result = await (await caches.match('/from-worker')).text();"));
        Assert.Contains("artifact:service-worker-registration/%2F", storage.Values.Keys);
    }

    [Fact]
    public async Task BrokeredServiceWorkerCannotReadPageGlobals()
    {
        await using var client = Client(new Storage(), loader: new IsolationWorkerLoader());
        Assert.Equal("undefined:undefined", await Load(client,
            "globalThis.pageSecret = 'page-only'; await navigator.serviceWorker.register('/isolation.js'); result = await (await fetch('/offline')).text();",
            true, true));
    }

    [Fact]
    public async Task CollectedRemoteHandlesReleaseRetiredQuotaWithoutAnotherCallFromTheirDocument()
    {
        await using var pair = await Pair.Create();
        var storage = new Storage(); pair.Host.ConfigureStorage(storage, "artifact", 2048); pair.Run();
        var proxy = new BrokerArtifactStorage(pair.Child);
        using var original = proxy.CreateCacheBackend("artifact", 2048);
        var retired = RetireRemoteCache(original);
        using var other = proxy.CreateCacheBackend("artifact", 2048);
        var fresh = other.Open("fresh", 2048);
        var entry = new CacheStorageState.Entry(new("https://cache.example/new", "GET", []), 200, "OK", [], new byte[800], false, "", "default");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        bool stored = false;
        while (!stored && DateTime.UtcNow < deadline)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            try { other.Put(fresh, [entry], false, 2048); stored = true; }
            catch (WebIdlException error) when (error.Name == "QuotaExceededError") { await Task.Delay(10, TestContext.Current.CancellationToken); }
        }
        Assert.False(retired.TryGetTarget(out _));
        Assert.True(stored, "The abandoned document's retired cache still retained host quota.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<CacheStorageHandle> RetireRemoteCache(ICacheStorageBackend backend)
    {
        var cache = backend.Open("retired", 2048);
        var entry = new CacheStorageState.Entry(new("https://cache.example/old", "GET", []), 200, "OK", [], new byte[800], false, "", "default");
        backend.Put(cache, [entry], false, 2048);
        backend.Delete("retired", 2048);
        return new(cache);
    }

    [Fact]
    public async Task RealProcessMigratesLegacyRelativeRequestsUsingTheHostLoadUri()
    {
        var storage = new Storage();
        storage.Values["artifact:service-worker-cache/offline/%2Fold"] = "{\"bytes\":[65],\"status\":200,\"statusText\":\"OK\",\"headers\":[]}";
        // A longer artifact ID must not import its neighbor's legacy entries.
        storage.Values["artifact:service-worker-cache/offline:service-worker-cache/a/%2Fforeign"] = "invalid";
        await using var client = Client(storage);
        Assert.Equal("A", await Load(client, "result = await (await caches.match('/old')).text();"));
        Assert.Equal("1", await Load(client, "result = String((await caches.keys()).length);"));
    }

    [Fact]
    public async Task CacheSurvivesRealProcessRestartWithoutNetwork()
    {
        var storage = new Storage();
        await using (var first = Client(storage))
            Assert.Equal("stored", await Load(first, "await (await caches.open('offline')).put('/data', new Response('offline-body')); result = 'stored';"));
        await using var second = Client(storage);
        Assert.Equal("offline-body", await Load(second, "result = await (await (await caches.open('offline')).match('/data')).text();"));
        Assert.NotEmpty(storage.Values);
    }

    [Fact]
    public async Task ConcurrentProcessesAndInProcessShareOneAtomicCacheLedger()
    {
        var storage = new Storage();
        await using var first = Client(storage);
        await using var second = Client(storage);
        await Task.WhenAll(Load(first, "await (await caches.open('shared')).put('/a', new Response('a')); result = 'a';"),
            Load(second, "await (await caches.open('shared')).put('/b', new Response('b')); result = 'b';"));
        var state = CacheStorageState.For(storage, "artifact");
        var entries = state.Match(state.Open("shared", 65536), null, null, new(false, false, false), false);
        Assert.Equal(2, entries.Length);
        Assert.Contains(entries, entry => entry.Request.Url == "https://cache.example/a");
        Assert.Contains(entries, entry => entry.Request.Url == "https://cache.example/b");
        Assert.Equal("a:b", await Load(first, "let c = await caches.open('shared'); result = await (await c.match('/a')).text() + ':' + await (await c.match('/b')).text();"));
    }

    [Fact]
    public async Task ParentProfileOverridesCallerScopeAndQuota()
    {
        var storage = new Storage();
        await using var first = Client(storage, "a", 2048);
        Assert.Equal("QuotaExceededError", await Load(first, "try { await (await caches.open('private')).put('/a', new Response('x'.repeat(4096))); } catch (e) { result = e.name; }"));
        await using var other = Client(storage, "a:b");
        Assert.Equal("false", await Load(other, "result = String(await caches.has('private'));"));
        Assert.DoesNotContain(storage.Values.Keys, key => key.Contains("forged", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HostWriteFailureDoesNotPublishAnUncommittedResponse()
    {
        var storage = new Storage();
        await using var client = Client(storage);
        Assert.Equal("old", await Load(client, "await (await caches.open('stable')).put('/a', new Response('old')); result = 'old';"));
        storage.FailNextWrite = true;
        Assert.Equal("UnknownError:old", await Load(client, "let c = await caches.open('stable'); try { await c.put('/a', new Response('new')); } catch (e) { result = e.name + ':' + await (await c.match('/a')).text(); }"));
    }

    [Fact]
    public async Task DeletingAndReopeningRemoteCacheDoesNotResurrectRetiredEntries()
    {
        var storage = new Storage();
        await using var client = Client(storage);
        Assert.Equal("old:true", await Load(client, "let old = await caches.open('same'); await old.put('/a', new Response('old')); await caches.delete('same'); let fresh = await caches.open('same'); await old.put('/b', new Response('retired')); result = await (await old.match('/a')).text() + ':' + String(await fresh.match('/b') === undefined);"));
        await using var reopened = Client(storage);
        Assert.Equal("0", await Load(reopened, "result = String((await (await caches.open('same')).keys()).length);"));
    }

    [Fact]
    public async Task RegistrationMetadataUsesTheSameScopedHostStorage()
    {
        await using var pair = await Pair.Create();
        var storage = new Storage(); pair.Host.ConfigureStorage(storage, "a:b", 65536); pair.Run();
        var proxy = new BrokerArtifactStorage(pair.Child);
        proxy.Write("a:b:service-worker-registration/%2F", "{\"scope\":\"/\"}");
        Assert.Equal("{\"scope\":\"/\"}", proxy.Read("a:b:service-worker-registration/%2F"));
        Assert.Single(proxy.Keys("a:b:service-worker-registration/"));
        var error = Assert.Throws<WebIdlException>(() => proxy.Read("a:service-worker-registration/%2F"));
        Assert.Equal("TypeError", error.Name);
        proxy.Delete("a:b:service-worker-registration/%2F");
        Assert.Empty(storage.Values);
    }

    [Fact]
    public async Task ConnectionCloseReleasesPendingStorageWaiters()
    {
        await using var pair = await Pair.Create();
        // No reader on the host: the request remains pending until EOF.
        var request = pair.Child.RequestStorageAsync(JsonSerializer.SerializeToUtf8Bytes(new ContentCacheBroker.Request("names", Guid.NewGuid().ToString("N"))), TestContext.Current.CancellationToken);
        var reader = pair.Child.RunAsync(TestContext.Current.CancellationToken).AsTask();
        await pair.Host.DisposeAsync();
        // EOF completes the pending RPC, whose terminal stop can cancel the
        // dispatchers before or after they finish draining the empty queues.
        try { await reader.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested && pair.Child.IsConnectionClosed) { }
        catch (IOException) when (pair.Child.IsConnectionClosed) { } // Unix pipes may report reset rather than EOF.
        Assert.True(pair.Child.IsConnectionClosed);
        await Assert.ThrowsAsync<IOException>(async () => await request.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        pair.HostDisposed = true;
    }

    [Fact]
    public async Task UncertainHostCommitStopsTheRealContentProcess()
    {
        var storage = new Storage();
        CacheStorageState.For(storage, "artifact").Open("existing", 65536);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.Writing = () =>
        {
            entered.TrySetResult();
            try { if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("test provider was not released"); }
            finally { finished.TrySetResult(); }
        };
        await using var client = Client(storage);
        var stopped = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ScriptStopped += (_, args) => stopped.TrySetResult(args.Reason);
        try
        {
            await client.SendAsync(ContentMessageKind.Load, new
            {
                html = "<script>caches.open('existing').then(c => c.put('/a', new Response('pending')));</script>",
                url = "https://cache.example/"
            }, TestContext.Current.CancellationToken);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal("content storage operation did not complete", await stopped.Task.WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken));
            Assert.True(client.Supervisor.IsStopped);
            await client.Process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(client.Process.HasExited);
        }
        finally { release.Set(); await finished.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task EofDiscardsAnIncompleteUploadWithoutWriting()
    {
        await using var pair = await Pair.Create();
        var storage = new Storage(); pair.Host.ConfigureStorage(storage, "artifact", 65536); pair.RunHostOnly();
        await pair.Raw(new { operation = "begin", length = 100, hash = new string('0', 64) });
        var reply = (await ContentTransport.ReadAsync(pair.RawStream, TestContext.Current.CancellationToken))!;
        var token = reply.Payload.GetProperty("result").GetProperty("token").GetString();
        await pair.Raw(new { operation = "chunk", token, offset = 0, data = new byte[] { 1 } });
        await ContentTransport.ReadAsync(pair.RawStream, TestContext.Current.CancellationToken);
        await pair.RawStream.DisposeAsync();
        await pair.HostRun!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Empty(storage.Values);
    }

    [Fact]
    public async Task SemanticCacheTransportChunksRequestsAndRepliesBeyondFrameLimit()
    {
        await using var pair = await Pair.Create();
        var storage = new Storage();
        pair.Host.ConfigureStorage(storage, "large", 40 * 1024 * 1024);
        pair.Child.SetStorageLimit(40 * 1024 * 1024);
        pair.Run();
        using var backend = new BrokerArtifactStorage(pair.Child).CreateCacheBackend("untrusted", long.MaxValue);
        var cache = backend.Open("large", long.MaxValue);
        var body = new byte[13 * 1024 * 1024];
        body[0] = 13; body[^1] = 42;
        var entry = new CacheStorageState.Entry(new("https://cache.example/large", "GET", []), 200, "OK", [], body, false, "", "default");
        backend.Put(cache, [entry], false, long.MaxValue);
        var received = Assert.Single(backend.Match(cache, null, entry.Request, new(false, false, false), true));
        Assert.Equal(body, received.Bytes);
        Assert.Single(storage.Values);
    }

    [Fact]
    public async Task StorageServiceRunsWhileOrdinaryHostCallbackIsWaiting()
    {
        await using var pair = await Pair.Create();
        pair.Host.ConfigureStorage(new Storage(), "artifact", 65536);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.Host.MessageReceived += async (_, cancellation) => { entered.TrySetResult(); await release.Task.WaitAsync(cancellation); };
        pair.Run();
        await pair.Child.SendAsync(ContentMessageKind.Load, new { }, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        try
        {
            using var backend = new BrokerArtifactStorage(pair.Child).CreateCacheBackend("artifact", 65536);
            Assert.False(backend.Has("missing"));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public void BrokerRejectsCrossArtifactMetadataAndMalformedQueries()
    {
        var storage = new Storage();
        using var broker = new ContentCacheBroker(storage, "a", 65536);
        var context = Guid.NewGuid().ToString("N");
        var denied = JsonSerializer.Deserialize<ContentCacheBroker.Reply>(broker.Execute(JsonSerializer.SerializeToUtf8Bytes(
            new ContentCacheBroker.Request("metadataWrite", context, Key: "a:b:service-worker-registration/x", Value: "forged"))))!;
        Assert.Equal("TypeError", denied.ErrorName);
        var malformed = JsonSerializer.Deserialize<ContentCacheBroker.Reply>(broker.Execute(JsonSerializer.SerializeToUtf8Bytes(
            new ContentCacheBroker.Request("match", context))))!;
        Assert.Equal("TypeError", malformed.ErrorName);
        Assert.Empty(storage.Values);
    }

    [Theory]
    [InlineData("offset")]
    [InlineData("digest")]
    [InlineData("length")]
    public async Task MalformedStagedUploadDoesNotCommitStorage(string fault)
    {
        await using var pair = await Pair.Create();
        var storage = new Storage(); pair.Host.ConfigureStorage(storage, "artifact", 65536); pair.RunHostOnly();
        // Raw protocol traffic is deliberately hostile; the host must terminate
        // it without invoking storage, rather than accept a partial transaction.
        await pair.Raw(new { operation = "begin", length = fault == "length" ? int.MaxValue : 1, hash = new string('0', 64) });
        if (fault != "length")
        {
            var reply = (await ContentTransport.ReadAsync(pair.RawStream, TestContext.Current.CancellationToken))!;
            var token = reply.Payload.GetProperty("result").GetProperty("token").GetString();
            await pair.Raw(new { operation = "chunk", token, offset = fault == "offset" ? 1 : 0, data = new byte[] { 1 } });
            if (fault == "digest")
            {
                await ContentTransport.ReadAsync(pair.RawStream, TestContext.Current.CancellationToken);
                await pair.Raw(new { operation = "execute", token });
            }
        }
        await Assert.ThrowsAsync<InvalidDataException>(async () => await pair.HostRun!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Empty(storage.Values);
    }

    private static ContentProcessClient Client(Storage storage, string artifact = "artifact", long quota = 65536, IResourceLoader? loader = null)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        var host = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/Folio.ContentHost/bin", configuration, "net10.0/Folio.ContentHost.dll"));
        return ContentProcessClient.Start(host, limits: new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromSeconds(15)),
            artifactStorage: storage, artifactId: artifact, documentLimits: ResourceLimits.Default with { MaxServiceWorkerCacheBytes = quota }, resourceLoader: loader);
    }
    private static async Task<string> Load(ContentProcessClient client, string script, bool serviceWorkers = false, bool storageEnabled = false)
    {
        var completed = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask Handler(ContentMessage message, CancellationToken _) { if (message.Kind is ContentMessageKind.Frame or ContentMessageKind.Diagnostic) completed.TrySetResult(message); return ValueTask.CompletedTask; }
        client.MessageReceived += Handler;
        try
        {
            await client.SendAsync(ContentMessageKind.Load, new { html = "<script>var result = 'unfinished'; (async () => {" + script + "})().catch(e => result = e.name + ':' + e.message);</script>", script = "result", url = "https://cache.example/", artifactId = "forged", storageEnabled, limits = new { MaxServiceWorkerCacheBytes = long.MaxValue }, capabilities = new FeatureCapabilities { ServiceWorkers = serviceWorkers } }, TestContext.Current.CancellationToken);
            var message = await completed.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.True(message.Kind == ContentMessageKind.Frame, message.Payload.ToString());
            var value = message.Payload.GetProperty("value").GetString()!;
            Assert.True(value != "unfinished", "The document promise did not settle: " + message.Payload);
            return value;
        }
        finally { client.MessageReceived -= Handler; }
    }
    private sealed class WorkerLoader : IResourceLoader
    {
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken) => Task.FromResult<ResourceResponse?>(
            request.Kind == ResourceKind.Script && request.Url == "https://cache.example/sw.js"
                ? ResourceResponse.Loaded(System.Text.Encoding.UTF8.GetBytes("self.addEventListener('install', event => event.waitUntil(caches.open('sw').then(c => c.put('/from-worker', new Response('worker-body')))));"), "text/javascript")
                : ResourceResponse.Refused("unexpected resource"));
    }
    private sealed class IsolationWorkerLoader : IResourceLoader
    {
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken) => Task.FromResult<ResourceResponse?>(
            request.Kind == ResourceKind.Script && request.Url == "https://cache.example/isolation.js"
                ? ResourceResponse.Loaded(System.Text.Encoding.UTF8.GetBytes("self.addEventListener('fetch', event => event.respondWith(new Response(typeof pageSecret + ':' + typeof document)));"), "text/javascript")
                : request.Url == "https://cache.example/offline"
                    ? ResourceResponse.Refused("the page loader must not receive the intercepted request")
                    : ResourceResponse.Refused("unexpected resource"));
    }
    private sealed class Storage : IArtifactStorage
    {
        internal readonly ConcurrentDictionary<string, string> Values = new(StringComparer.Ordinal);
        internal bool FailNextWrite;
        internal Action? Writing;
        public string? Read(string key) => Values.GetValueOrDefault(key);
        public void Write(string key, string value) { if (FailNextWrite) { FailNextWrite = false; throw new IOException("injected"); } Writing?.Invoke(); Values[key] = value; }
        public void Delete(string key) => Values.TryRemove(key, out _);
        public IReadOnlyList<string> Keys(string prefix) => Values.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
    }
    private sealed class Pair : IAsyncDisposable
    {
        internal required ContentHostSession Host;
        internal required ContentHostSession Child;
        internal bool HostDisposed;
        private readonly CancellationTokenSource _stop = new();
        internal Task? HostRun;
        private Task? _childRun;
        internal required Stream RawStream;
        internal void RunHostOnly() => HostRun = Host.RunAsync(_stop.Token).AsTask();
        internal ValueTask Raw(object payload) => ContentTransport.WriteAsync(RawStream, new(ContentMessageKind.StorageRequest, 1, JsonSerializer.SerializeToElement(payload)), _stop.Token);
        internal static async Task<Pair> Create()
        {
            var name = "folio-cache-" + Guid.NewGuid().ToString("N");
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await Task.WhenAll(server.WaitForConnectionAsync(TestContext.Current.CancellationToken), client.ConnectAsync(TestContext.Current.CancellationToken));
            return new() { Host = new(server, server), Child = new(client, client), RawStream = client };
        }
        internal void Run()
        {
            HostRun = Host.RunAsync(_stop.Token).AsTask(); _childRun = Child.RunAsync(_stop.Token).AsTask();
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            if (HostRun is not null) try { await HostRun; } catch { }
            if (_childRun is not null) try { await _childRun; } catch { }
            if (!HostDisposed) await Host.DisposeAsync();
            await Child.DisposeAsync(); _stop.Dispose();
        }
    }
}
