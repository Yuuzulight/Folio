using Folio.Scripting.Dom;
using Folio.Scripting.Jint;
using Folio.Scripting.Native;
using Folio.Resources;
using Folio.Dom;
using Folio.Typography;
using System.Text;

namespace Folio.Scripting.Tests;

public sealed class ServiceWorkerCapabilityTests
{
    [Fact]
    public void NativeAuthorityChecksDocumentRetirementEvenBeforeItsOwnDisposal()
    {
        var documentLive = true;
        using var authority = new ServiceWorkerCapabilityAuthority(true, () => documentLive);
        Assert.True(authority.TryAcquire(out var lease));
        documentLive = false;
        Assert.False(authority.Enabled);
        Assert.False(lease!.IsActive);
        Assert.False(authority.TryAcquire(out _));
        Assert.True(authority.LoadCancellation.IsCancellationRequested);
    }

    [Fact]
    public void ThrowingLivePermissionProbeFailsClosed()
    {
        using var authority = new ServiceWorkerCapabilityAuthority(true, () => throw new InvalidOperationException("probe"));

        Assert.False(authority.Enabled);
        Assert.False(authority.TryAcquire(out var lease));
        Assert.Null(lease);
        Assert.True(authority.LoadCancellation.IsCancellationRequested);
    }

    [Fact]
    public void RevocationCancelsOldScriptLoadsAndRegrantUsesFreshCancellation()
    {
        using var authority = new ServiceWorkerCapabilityAuthority(true);
        var original = authority.LoadCancellation;
        authority.UpdateCapabilities(false);
        Assert.True(original.IsCancellationRequested);
        authority.UpdateCapabilities(true);
        var replacement = authority.LoadCancellation;
        Assert.False(replacement.IsCancellationRequested);
        Assert.True(original.IsCancellationRequested);
        authority.Dispose();
        Assert.True(replacement.IsCancellationRequested);
    }

    [Fact]
    public void ThrowingCancellationCallbackCannotPreventRetirementNotification()
    {
        using var authority = new ServiceWorkerCapabilityAuthority(true);
        var notified = false;
        authority.Changed = () => notified = true;
        using var registration = authority.LoadCancellation.Register(() => throw new InvalidOperationException("loader cancellation callback"));
        authority.UpdateCapabilities(false);
        Assert.True(notified);
        Assert.False(authority.Enabled);
    }

    [Fact]
    public void ThrowingChangeCallbackCannotPreventRevocationOrDisposal()
    {
        using var authority = new ServiceWorkerCapabilityAuthority(true);
        Assert.True(authority.TryAcquire(out var lease));
        authority.Changed = () => throw new InvalidOperationException("page callback");

        authority.UpdateCapabilities(false);

        Assert.False(authority.Enabled);
        Assert.False(lease!.IsActive);
        authority.Dispose();
    }

    [Fact]
    public void PublicApiRevocationRetiresRegistrationAndRegrantUsesFreshScript()
    {
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, Folio.Document.Parse("<p>service worker</p>", new FolioOptions
        {
            BaseUri = new Uri("https://worker.example/"),
            ResourceLoader = new TestServiceWorkerLoader(),
            Capabilities = new FeatureCapabilities { ServiceWorkers = true }
        }));

        Assert.True(engine.Evaluate("let oldRegistration; navigator.serviceWorker.register('old.js').then(r => oldRegistration = r); true").AsBoolean());
        engine.PerformMicrotaskCheckpoint();
        Assert.True(engine.Evaluate("oldRegistration && oldRegistration.active && oldRegistration.active.state === 'activated'").AsBoolean());
        DomBindings.UpdateCapabilities(engine, FeatureCapabilities.Default);
        Assert.True(engine.Evaluate("let oldError = ''; oldRegistration.update().catch(e => oldError = e.name); try { oldRegistration.active.postMessage('x'); } catch (e) { oldError = e.name; } true").AsBoolean());
        engine.PerformMicrotaskCheckpoint();
        Assert.Equal("InvalidStateError", engine.Evaluate("oldError").AsString());
        Assert.True(engine.Evaluate("oldRegistration.active.state === 'redundant' && oldRegistration.active._self.onfetch === null").AsBoolean());

        DomBindings.UpdateCapabilities(engine, new FeatureCapabilities { ServiceWorkers = true });
        Assert.True(engine.Evaluate("let newRegistration; navigator.serviceWorker.register('new.js').then(r => newRegistration = r); true").AsBoolean());
        engine.PerformMicrotaskCheckpoint();
        Assert.Equal("new.js", engine.Evaluate("newRegistration.scriptURL").AsString());
    }

    [Fact]
    public void NativeEngineRunsServiceWorkerRegistrationLifecycle()
    {
        using var engine = new NativeJavaScriptEngine();
        DomBindings.Install(engine, Folio.Document.Parse("<p>service worker</p>", new FolioOptions
        {
            BaseUri = new Uri("https://worker.example/"),
            ResourceLoader = new TestServiceWorkerLoader(),
            Capabilities = new FeatureCapabilities { ServiceWorkers = true }
        }));

        Assert.Equal("object", engine.Evaluate("let pendingRegistration = navigator.serviceWorker.register('old.js'); return typeof pendingRegistration;").AsString());
        Assert.True(engine.Evaluate("let registration; pendingRegistration.then(r => registration = r); true").AsBoolean());
        engine.PerformMicrotaskCheckpoint();
        Assert.Equal("activated", engine.Evaluate("registration.active.state").AsString());
    }

    [Fact]
    public void NativeEngineRetiresServiceWorkerOnCapabilityRevocation()
    {
        using var engine = new NativeJavaScriptEngine();
        DomBindings.Install(engine, Folio.Document.Parse("<p>service worker</p>", new FolioOptions
        {
            BaseUri = new Uri("https://worker.example/"),
            ResourceLoader = new TestServiceWorkerLoader(),
            Capabilities = new FeatureCapabilities { ServiceWorkers = true }
        }));

        Assert.Equal("ok", engine.Evaluate("let probe = {}; Object.defineProperty(probe, 'a', { get: function() { return 'ok'; } }); return probe.a;").AsString());
        Assert.True(engine.Evaluate("let registration; navigator.serviceWorker.register('old.js').then(r => registration = r); true").AsBoolean());
        engine.PerformMicrotaskCheckpoint();
        Assert.Equal("activated", engine.Evaluate("registration.active.state").AsString());

        DomBindings.UpdateCapabilities(engine, new FeatureCapabilities { ServiceWorkers = false });
        Assert.Equal("redundant", engine.Evaluate("registration.active.state").AsString());
        Assert.Equal("object", engine.Evaluate("typeof navigator.serviceWorker").AsString());
        Assert.True(engine.Evaluate("let errorName = ''; navigator.serviceWorker.register('new.js').catch(error => errorName = error.name); true").AsBoolean());
        engine.PerformMicrotaskCheckpoint();
        Assert.Equal("SecurityError", engine.Evaluate("errorName").AsString());
    }

    [Fact]
    public void NativeEngineServiceWorkerControllerInterceptsResource()
    {
        using var document = Folio.Document.Parse("<p>service worker</p>", new FolioOptions
        {
            BaseUri = new Uri("https://worker.example/"),
            ResourceLoader = new TestServiceWorkerLoader("self.addEventListener('fetch', e => e.respondWith(Promise.resolve(new Response('old'))));"),
            Capabilities = new FeatureCapabilities { ServiceWorkers = true }
        });
        using var engine = new NativeJavaScriptEngine();
        DomBindings.Install(engine, document);
        Assert.Equal("object", engine.Evaluate("typeof __folio_service_worker_isolated").AsString());
        Assert.Equal("object", engine.Evaluate("let probe = function() { return typeof __folio_service_worker_isolated; }; probe()").AsString());
        Assert.Equal("object", engine.Evaluate("let captured = __folio_service_worker_isolated; let probe2 = function() { return typeof captured; }; probe2()").AsString());
        Assert.Equal("object", engine.Evaluate("let outer = function(x) { return { f: function() { return typeof x; } }; }; let nested = outer(__folio_service_worker_isolated); nested.f()").AsString());
        Assert.Equal("function", engine.Evaluate("typeof __folio_service_worker_isolated.dispatchMessage").AsString());

        Assert.Equal("function", engine.Evaluate("typeof Response").AsString());
        Assert.Equal("object", engine.Evaluate("return typeof new Response('old');").AsString());
        Assert.Equal("object", engine.Evaluate("return typeof new Response('old').bytes();").AsString());
        Assert.True(engine.Evaluate("navigator.serviceWorker.register('old.js'); true").AsBoolean());
        engine.PerformMicrotaskCheckpoint();
        var interceptor = document.Options.ResourceInterceptor;
        Assert.NotNull(interceptor);
        var response = interceptor!.Intercept(new ResourceRequest("https://worker.example/offline.txt", ResourceKind.Document));
        Assert.NotNull(response);
        Assert.Equal("old", Encoding.UTF8.GetString(response!.Data!));
    }

    [Fact]
    public void NativeServiceWorkerScriptCannotReadPageOnlyBindings()
    {
        using var document = Folio.Document.Parse("<p>service worker</p>", new FolioOptions
        {
            BaseUri = new Uri("https://worker.example/"),
            ResourceLoader = new TestServiceWorkerLoader("self.addEventListener('fetch', e => e.respondWith(new Response(typeof pageSecret + ':' + typeof document)));") ,
            Capabilities = new FeatureCapabilities { ServiceWorkers = true }
        });
        using var engine = new NativeJavaScriptEngine();
        DomBindings.Install(engine, document);
        engine.SetGlobalProperty("pageSecret", ScriptValue.FromString("page-only"));
        Assert.True(engine.Evaluate("navigator.serviceWorker.register('isolated.js'); true").AsBoolean());
        engine.PerformMicrotaskCheckpoint();

        var response = document.Options.ResourceInterceptor!.Intercept(new ResourceRequest("https://worker.example/offline.txt", ResourceKind.Document));
        Assert.NotNull(response);
        Assert.Equal("undefined:undefined", Encoding.UTF8.GetString(response!.Data!));
    }

    [Fact]
    public void NativeServiceWorkerDeliversMessagesThroughItsIsolatedRuntime()
    {
        using var document = Folio.Document.Parse("<p>service worker</p>", new FolioOptions
        {
            BaseUri = new Uri("https://worker.example/"),
            ResourceLoader = new TestServiceWorkerLoader("self.onmessage = function(e) { self.postMessage(e.data + 2); };"),
            Capabilities = new FeatureCapabilities { ServiceWorkers = true }
        });
        using var engine = new NativeJavaScriptEngine();
        DomBindings.Install(engine, document);
        Assert.True(engine.Evaluate("let result = 0; let registrationError = ''; navigator.serviceWorker.register('message-bridge.js', { scope: '/bridge/' }).then(r => { r.active.onmessage = e => result = e.data; r.active.postMessage(40); }).catch(e => registrationError = e.message); true").AsBoolean());
        for (var i = 0; i < 8; i++) engine.PerformMicrotaskCheckpoint();
        var registrationError = engine.Evaluate("registrationError").AsString();
        Assert.True(string.IsNullOrEmpty(registrationError), registrationError);
        Assert.Equal(42d, engine.Evaluate("result").AsNumber());
    }

    [Fact]
    public void NativeServiceWorkerClientsClaimUpdatesThePageController()
    {
        using var document = Folio.Document.Parse("<p>service worker</p>", new FolioOptions
        {
            BaseUri = new Uri("https://worker.example/"),
            ResourceLoader = new TestServiceWorkerLoader("self.addEventListener('activate', e => e.waitUntil(self.clients.claim()));"),
            Capabilities = new FeatureCapabilities { ServiceWorkers = true }
        });
        using var engine = new NativeJavaScriptEngine();
        DomBindings.Install(engine, document);
        engine.Evaluate("let registrationError = ''; navigator.serviceWorker.register('claim.js').catch(e => registrationError = e.name);");
        for (var i = 0; i < 8; i++) engine.PerformMicrotaskCheckpoint();
        Assert.Equal("", engine.Evaluate("registrationError").AsString());
        Assert.True(engine.Evaluate("navigator.serviceWorker.controller !== null").AsBoolean());
    }

    [Fact]
    public void JintServiceWorkerDeliversMessagesThroughItsIsolatedRuntime()
    {
        using var document = Folio.Document.Parse("<p>service worker</p>", new FolioOptions
        {
            BaseUri = new Uri("https://worker.example/"),
            ResourceLoader = new TestServiceWorkerLoader("self.onmessage = function(e) { self.postMessage(e.data + 3); };"),
            Capabilities = new FeatureCapabilities { ServiceWorkers = true }
        });
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        Assert.True(engine.Evaluate("let result = 0; let registrationError = ''; navigator.serviceWorker.register('message-bridge-jint.js', { scope: '/jint/' }).then(r => { r.active.onmessage = e => result = e.data; r.active.postMessage(40); }).catch(e => registrationError = e.message); true").AsBoolean());
        for (var i = 0; i < 8; i++) engine.PerformMicrotaskCheckpoint();
        Assert.True(string.IsNullOrEmpty(engine.Evaluate("registrationError").AsString()), engine.Evaluate("registrationError").AsString());
        Assert.Equal(43d, engine.Evaluate("result").AsNumber());
    }

    [Fact]
    public void JintServiceWorkerClientsClaimUpdatesThePageController()
    {
        using var document = Folio.Document.Parse("<p>service worker</p>", new FolioOptions
        {
            BaseUri = new Uri("https://worker.example/"),
            ResourceLoader = new TestServiceWorkerLoader("self.addEventListener('activate', e => e.waitUntil(self.clients.claim()));"),
            Capabilities = new FeatureCapabilities { ServiceWorkers = true }
        });
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        engine.Evaluate("let registrationError = ''; navigator.serviceWorker.register('claim-jint.js').catch(e => registrationError = e.name);");
        for (var i = 0; i < 8; i++) engine.PerformMicrotaskCheckpoint();
        Assert.Equal("", engine.Evaluate("registrationError").AsString());
        Assert.True(engine.Evaluate("navigator.serviceWorker.controller !== null").AsBoolean());
    }

    [Fact]
    public void JintServiceWorkerScriptCannotReadPageOnlyBindings()
    {
        using var document = Folio.Document.Parse("<p>service worker</p>", new FolioOptions
        {
            BaseUri = new Uri("https://worker.example/"),
            ResourceLoader = new TestServiceWorkerLoader("self.addEventListener('fetch', e => e.respondWith(new Response(typeof pageSecret + ':' + typeof document)));"),
            Capabilities = new FeatureCapabilities { ServiceWorkers = true }
        });
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        engine.SetGlobalProperty("pageSecret", ScriptValue.FromString("page-only"));
        Assert.True(engine.Evaluate("navigator.serviceWorker.register('isolated-jint.js'); true").AsBoolean());
        engine.PerformMicrotaskCheckpoint();

        var response = document.Options.ResourceInterceptor!.Intercept(new ResourceRequest("https://worker.example/offline.txt", ResourceKind.Document));
        Assert.NotNull(response);
        Assert.Equal("undefined:undefined", Encoding.UTF8.GetString(response!.Data!));
    }

    [Fact]
    public void RegistrationRejectsCrossOriginScriptBeforeHostLoad()
    {
        var loader = new ServiceWorkerDocumentLoader();
        using var document = Folio.Document.Parse("<p>service worker</p>", new FolioOptions
        {
            BaseUri = new Uri("https://worker.example/index.html"),
            Capabilities = new FeatureCapabilities { ServiceWorkers = true },
            ResourceLoader = loader
        });
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        try
        {
            Assert.True(engine.Evaluate("let errorName = ''; navigator.serviceWorker.register('https://evil.example/sw.js').catch(e => errorName = e.name); true").AsBoolean());
            engine.PerformMicrotaskCheckpoint();
            Assert.Equal("SecurityError", engine.Evaluate("errorName").AsString());
            Assert.DoesNotContain(loader.DocumentRequests, url => url.Contains("evil.example", StringComparison.Ordinal));
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void RegistrationRejectsCrossOriginScope()
    {
        using var document = Folio.Document.Parse("<p>service worker</p>", new FolioOptions
        {
            BaseUri = new Uri("https://worker.example/index.html"),
            Capabilities = new FeatureCapabilities { ServiceWorkers = true },
            ResourceLoader = new ServiceWorkerDocumentLoader()
        });
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        try
        {
            Assert.True(engine.Evaluate("let validRegistration; navigator.serviceWorker.register('sw.js', { scope: 'https://worker.example/app/' }).then(r => validRegistration = r); true").AsBoolean());
            engine.PerformMicrotaskCheckpoint();
            Assert.Equal("/app/", engine.Evaluate("validRegistration.scope").AsString());
            Assert.True(engine.Evaluate("let errorName = ''; navigator.serviceWorker.register('sw.js', { scope: 'https://evil.example/' }).catch(e => errorName = e.name); true").AsBoolean());
            engine.PerformMicrotaskCheckpoint();
            Assert.Equal("SecurityError", engine.Evaluate("errorName").AsString());
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void ControllerInterceptsIframeDocumentNavigation()
    {
        var loader = new ServiceWorkerDocumentLoader();
        using var document = Folio.Document.Parse("<iframe id='f' src='https://worker.example/offline.html'></iframe>", new FolioOptions
        {
            BaseUri = new Uri("https://worker.example/index.html"),
            Capabilities = new FeatureCapabilities { Iframes = true, ServiceWorkers = true },
            ResourceLoader = loader
        });
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        try
        {
            Assert.True(engine.Evaluate("navigator.serviceWorker.register('sw.js'); true").AsBoolean());
            engine.PerformMicrotaskCheckpoint();
            document.Paint(300, 200);
            var frame = Assert.IsType<IframeState>(document.GetElementById("f")!.Node.FramePresentation);
            var child = frame.ChildDocument;
            Assert.NotNull(child);
            Assert.Equal("offline", child.Descendants.OfType<ElementNode>().Single(node => node.GetAttribute("id") == "offline").GetAttribute("id"));
            Assert.DoesNotContain(loader.DocumentRequests, url => url.EndsWith("offline.html", StringComparison.Ordinal));
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void ControllerPreservesFontResourceKindAndContentType()
    {
        var loader = new ServiceWorkerDocumentLoader();
        using var document = Folio.Document.Parse("<p>font</p>", new FolioOptions
        {
            BaseUri = new Uri("https://worker.example/index.html"),
            Capabilities = new FeatureCapabilities { ServiceWorkers = true },
            ResourceLoader = loader
        });
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        try
        {
            Assert.True(engine.Evaluate("navigator.serviceWorker.register('sw.js'); true").AsBoolean());
            engine.PerformMicrotaskCheckpoint();
            var response = document.Options.ResourceInterceptor!.Intercept(new ResourceRequest("https://worker.example/offline.ttf", ResourceKind.Font));
            Assert.NotNull(response);
            Assert.Equal(new byte[] { 1, 2, 3 }, response!.Data);
            Assert.Equal("font/ttf", response.ContentType);
            Assert.DoesNotContain(loader.FontRequests, url => url.EndsWith("offline.ttf", StringComparison.Ordinal));
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void ControllerPreservesLargeBinaryResponsesWithoutStringExpansion()
    {
        var loader = new LargeBinaryServiceWorkerLoader();
        using var document = Folio.Document.Parse("<p>binary</p>", new FolioOptions
        {
            BaseUri = new Uri("https://worker.example/index.html"),
            Capabilities = new FeatureCapabilities { ServiceWorkers = true },
            ResourceLoader = loader
        });
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        try
        {
            Assert.True(engine.Evaluate("navigator.serviceWorker.register('sw.js'); true").AsBoolean());
            engine.PerformMicrotaskCheckpoint();
            var response = document.Options.ResourceInterceptor!.Intercept(new ResourceRequest("https://worker.example/large.bin", ResourceKind.Media));
            Assert.NotNull(response);
            Assert.Equal(4097, response!.Data!.Length);
            Assert.Equal(17, response.Data[0]);
            Assert.Equal(231, response.Data[^1]);
            Assert.Equal("application/octet-stream", response.ContentType);
            Assert.Empty(loader.MediaRequests);
        }
        finally { DomBindings.Dispose(engine); }
    }

    [Fact]
    public void ControllerDeliversARealFontThatFolioCanDecode()
    {
        var loader = new FontServiceWorkerLoader();
        using var document = Folio.Document.Parse("<p>font</p>", new FolioOptions
        {
            BaseUri = new Uri("https://worker.example/index.html"),
            Capabilities = new FeatureCapabilities { ServiceWorkers = true },
            ResourceLoader = loader
        });
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        try
        {
            Assert.True(engine.Evaluate("navigator.serviceWorker.register('sw.js'); true").AsBoolean());
            engine.PerformMicrotaskCheckpoint();
            var response = document.Options.ResourceInterceptor!.Intercept(new ResourceRequest("https://worker.example/folio.ttf", ResourceKind.Font));
            Assert.NotNull(response);
            Assert.Equal("font/ttf", response!.ContentType);
            var sfnt = WebFontDecoder.Decode(response.Data!);
            Assert.NotNull(sfnt);
            Assert.Equal("Folio Box", FontFace.Parse(sfnt!)!.Family);
            Assert.Empty(loader.FontRequests);
        }
        finally { DomBindings.Dispose(engine); }
    }

    private sealed class TestServiceWorkerLoader : IResourceLoader
    {
        private readonly string? _script;
        public TestServiceWorkerLoader(string? script = null) => _script = script;
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<ResourceResponse?>(request.Kind == ResourceKind.Script
                ? ResourceResponse.Loaded(Encoding.UTF8.GetBytes(_script ?? (request.Url.EndsWith("new.js", StringComparison.Ordinal) ? "self.addEventListener('fetch', e => e.respondWith(new Response('new')));" : "self.addEventListener('fetch', e => e.respondWith(new Response('old')));")), "application/javascript")
                : ResourceResponse.Refused("unsupported"));
    }

    private sealed class ServiceWorkerDocumentLoader : IResourceLoader
    {
        public List<string> DocumentRequests { get; } = [];
        public List<string> FontRequests { get; } = [];
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            if (request.Kind == ResourceKind.Script)
            {
                var script = "self.addEventListener('fetch', e => e.respondWith(e.request.url.endsWith('.ttf') ? new Response(new Uint8Array([1,2,3]), { headers: { 'content-type': 'font/ttf' } }) : new Response('<p id=\\\"offline\\\">offline</p>', { headers: { 'content-type': 'text/html' } })));";
                return Task.FromResult<ResourceResponse?>(ResourceResponse.Loaded(Encoding.UTF8.GetBytes(script), "application/javascript"));
            }
            if (request.Kind == ResourceKind.Document) DocumentRequests.Add(request.Url);
            if (request.Kind == ResourceKind.Font) FontRequests.Add(request.Url);
            return Task.FromResult<ResourceResponse?>(ResourceResponse.Refused("host should not receive intercepted document"));
        }
    }

    private sealed class LargeBinaryServiceWorkerLoader : IResourceLoader
    {
        public List<string> MediaRequests { get; } = [];
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            if (request.Kind == ResourceKind.Script)
            {
                const string script = "self.addEventListener('fetch', e => { const data = new Uint8Array(4097); data[0] = 17; data[4096] = 231; e.respondWith(new Response(data, { headers: { 'content-type': 'application/octet-stream' } })); });";
                return Task.FromResult<ResourceResponse?>(ResourceResponse.Loaded(Encoding.UTF8.GetBytes(script), "application/javascript"));
            }
            if (request.Kind == ResourceKind.Media) MediaRequests.Add(request.Url);
            return Task.FromResult<ResourceResponse?>(ResourceResponse.Refused("host should not receive intercepted media"));
        }
    }

    private sealed class FontServiceWorkerLoader : IResourceLoader
    {
        private readonly string _script;
        public List<string> FontRequests { get; } = [];

        public FontServiceWorkerLoader()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "tests", "fonts", "FolioBox.ttf")))
                directory = directory.Parent;
            var fontPath = directory is null ? throw new FileNotFoundException("FolioBox.ttf") : Path.Combine(directory.FullName, "tests", "fonts", "FolioBox.ttf");
            var font = Convert.ToBase64String(File.ReadAllBytes(fontPath));
            _script = "self.addEventListener('fetch', e => { if (e.request.url.endsWith('.ttf')) { const raw = atob('" + font + "'); const bytes = new Uint8Array(raw.length); for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i); e.respondWith(new Response(bytes, { headers: { 'content-type': 'font/ttf' } })); } });";
        }

        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            if (request.Kind == ResourceKind.Script)
                return Task.FromResult<ResourceResponse?>(ResourceResponse.Loaded(Encoding.UTF8.GetBytes(_script), "application/javascript"));
            if (request.Kind == ResourceKind.Font) FontRequests.Add(request.Url);
            return Task.FromResult<ResourceResponse?>(ResourceResponse.Refused("host should not receive intercepted font"));
        }
    }

    [Fact]
    public void DisabledAuthorityDoesNotAcquireLeases()
    {
        using var authority = new ServiceWorkerCapabilityAuthority(false);

        Assert.False(authority.TryAcquire(out var lease));
        Assert.Null(lease);
    }

    [Fact]
    public void RevocationRetiresExistingLeasesAndRegrantRequiresFreshLease()
    {
        using var authority = new ServiceWorkerCapabilityAuthority(true);
        Assert.True(authority.TryAcquire(out var oldLease));
        Assert.NotNull(oldLease);
        Assert.True(oldLease!.IsActive);

        authority.UpdateCapabilities(false);
        Assert.False(oldLease.IsActive);
        Assert.False(authority.TryAcquire(out _));

        authority.UpdateCapabilities(true);
        Assert.True(authority.TryAcquire(out var newLease));
        Assert.NotNull(newLease);
        Assert.True(newLease!.IsActive);
        Assert.False(oldLease.IsActive);
    }

    [Fact]
    public void DisposedAuthorityRetiresAllLeases()
    {
        var authority = new ServiceWorkerCapabilityAuthority(true);
        Assert.True(authority.TryAcquire(out var lease));
        authority.Dispose();

        Assert.False(authority.Enabled);
        Assert.False(lease!.IsActive);
        Assert.False(authority.TryAcquire(out _));
        authority.Dispose();
    }

    [Fact]
    public void RepeatedGrantDoesNotRetireAnUnchangedLease()
    {
        using var authority = new ServiceWorkerCapabilityAuthority(true);
        Assert.True(authority.TryAcquire(out var lease));

        authority.UpdateCapabilities(true);

        Assert.True(lease!.IsActive);
    }
}
