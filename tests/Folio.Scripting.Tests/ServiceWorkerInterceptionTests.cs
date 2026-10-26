using System.Text;
using Folio.Dom;
using Folio.Resources;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

public sealed class ServiceWorkerInterceptionTests
{
    private static readonly byte[] TinyPng = [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // PNG magic
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, // IHDR chunk
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, // 1x1
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, // 8-bit RGBA
        0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41, // IDAT chunk
        0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00,
        0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, // IEND chunk
        0x42, 0x60, 0x82
    ];

    [Fact]
    public void ServiceWorkerInterceptsImageRequestWithSyntheticBytes()
    {
        var testLoader = new MockResourceLoader();
        using var engine = new JintScriptEngine();
        var document = Document.Parse("<img id='test-img' src='https://worker.example/images/hero.png'>", new FolioOptions
        {
            BaseUri = new Uri("https://worker.example/"),
            ResourceLoader = testLoader,
            Capabilities = new FeatureCapabilities { ServiceWorkers = true }
        });
        DomBindings.Install(engine, document);

        try
        {
            // Register service worker with fetch handler returning TinyPng
            var pngBase64 = Convert.ToBase64String(TinyPng);
            testLoader.SetScript("https://worker.example/sw.js", $$"""
                self.addEventListener('fetch', (event) => {
                    if (event.request.url.includes('hero.png')) {
                        const bin = atob('{{pngBase64}}');
                        const bytes = new Uint8Array(bin.length);
                        for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
                        event.respondWith(new Response(bytes.buffer, {
                            headers: { 'content-type': 'image/png' }
                        }));
                    }
                });
            """);

            engine.Evaluate("navigator.serviceWorker.register('sw.js');");
            engine.PerformMicrotaskCheckpoint();

            // Intercept image load through context loader
            var imgNode = document.Node.Descendants.OfType<ElementNode>().First(e => e.GetAttribute("id") == "test-img");
            var imageHandle = DomBindings.GetImageOf(engine, imgNode);

            Assert.NotNull(imageHandle);
            Assert.Equal(1, imageHandle!.Width);
            Assert.Equal(1, imageHandle!.Height);

            // Also verify via JS naturalWidth/naturalHeight property
            var naturalW = engine.Evaluate("document.getElementById('test-img').naturalWidth").AsNumber();
            var naturalH = engine.Evaluate("document.getElementById('test-img').naturalHeight").AsNumber();
            Assert.Equal(1, (int)naturalW);
            Assert.Equal(1, (int)naturalH);
        }
        finally
        {
            DomBindings.Dispose(engine);
        }
    }

    [Fact]
    public void ServiceWorkerInterceptsFetchWithSyntheticJsonResponse()
    {
        var testLoader = new MockResourceLoader();
        using var engine = new JintScriptEngine();
        var document = Document.Parse("<p>fetch</p>", new FolioOptions
        {
            BaseUri = new Uri("https://worker.example/"),
            ResourceLoader = testLoader,
            Capabilities = new FeatureCapabilities { ServiceWorkers = true }
        });
        DomBindings.Install(engine, document);

        try
        {
            testLoader.SetScript("https://worker.example/sw.js", """
                self.addEventListener('fetch', (event) => {
                    if (event.request.url.includes('/api/greeting')) {
                        event.respondWith(new Response(JSON.stringify({ greeting: 'hello-from-sw' }), {
                            headers: { 'content-type': 'application/json' }
                        }));
                    }
                });
            """);

            engine.Evaluate("navigator.serviceWorker.register('sw.js');");
            engine.PerformMicrotaskCheckpoint();

            engine.Evaluate("""
                window.fetchResult = null;
                fetch('https://worker.example/api/greeting')
                    .then(res => res.json())
                    .then(data => { window.fetchResult = data.greeting; });
            """);

            var passed = SpinWait.SpinUntil(() => engine.Evaluate("window.fetchResult !== null").AsBoolean(), TimeSpan.FromSeconds(3));
            Assert.True(passed);
            Assert.Equal("hello-from-sw", engine.Evaluate("window.fetchResult").AsString());
        }
        finally
        {
            DomBindings.Dispose(engine);
        }
    }

    [Fact]
    public void ServiceWorkerFetchFallsThroughWhenHandlerDoesNotCallRespondWith()
    {
        var testLoader = new MockResourceLoader();
        testLoader.SetText("https://worker.example/fallback.txt", "network-fallback-content");

        using var engine = new JintScriptEngine();
        var document = Document.Parse("<p>fallback</p>", new FolioOptions
        {
            BaseUri = new Uri("https://worker.example/"),
            ResourceLoader = testLoader,
            Capabilities = new FeatureCapabilities { ServiceWorkers = true }
        });
        DomBindings.Install(engine, document);

        try
        {
            testLoader.SetScript("https://worker.example/sw.js", """
                self.addEventListener('fetch', (event) => {
                    // Do nothing -> fall through to network
                });
            """);

            engine.Evaluate("navigator.serviceWorker.register('sw.js');");
            engine.PerformMicrotaskCheckpoint();

            engine.Evaluate("""
                window.fallbackResult = null;
                fetch('https://worker.example/fallback.txt')
                    .then(res => res.text())
                    .then(text => { window.fallbackResult = text; });
            """);

            var passed = SpinWait.SpinUntil(() => engine.Evaluate("window.fallbackResult !== null").AsBoolean(), TimeSpan.FromSeconds(3));
            Assert.True(passed);
            Assert.Equal("network-fallback-content", engine.Evaluate("window.fallbackResult").AsString());
        }
        finally
        {
            DomBindings.Dispose(engine);
        }
    }

    [Fact]
    public void ServiceWorkerRefusedOriginRecordsDiagnosticOrRejects()
    {
        var testLoader = new MockResourceLoader();
        using var engine = new JintScriptEngine();
        var document = Document.Parse("<p>refused</p>", new FolioOptions
        {
            BaseUri = new Uri("https://worker.example/"),
            ResourceLoader = testLoader,
            Capabilities = new FeatureCapabilities { ServiceWorkers = true }
        });
        DomBindings.Install(engine, document);

        try
        {
            testLoader.SetScript("https://worker.example/sw.js", """
                self.addEventListener('fetch', (event) => {
                    // Fetch to a disallowed origin
                    if (event.request.url.includes('/intercepted')) {
                        event.respondWith(fetch('https://refused.example/secret'));
                    }
                });
            """);

            engine.Evaluate("navigator.serviceWorker.register('sw.js');");
            engine.PerformMicrotaskCheckpoint();

            engine.Evaluate("""
                window.refusedCaught = false;
                fetch('https://worker.example/intercepted')
                    .catch(err => { window.refusedCaught = true; });
            """);

            var passed = SpinWait.SpinUntil(() => engine.Evaluate("window.refusedCaught").AsBoolean(), TimeSpan.FromSeconds(3));
            Assert.True(passed);
        }
        finally
        {
            DomBindings.Dispose(engine);
        }
    }

    private sealed class MockResourceLoader : IResourceLoader
    {
        private readonly Dictionary<string, byte[]> _resources = new();
        private readonly Dictionary<string, string> _contentTypes = new();

        public void SetScript(string url, string script)
        {
            _resources[url] = Encoding.UTF8.GetBytes(script);
            _contentTypes[url] = "application/javascript";
        }

        public void SetText(string url, string text)
        {
            _resources[url] = Encoding.UTF8.GetBytes(text);
            _contentTypes[url] = "text/plain";
        }

        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            if (request.Url.StartsWith("https://refused.example", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<ResourceResponse?>(ResourceResponse.Refused("Origin not permitted"));
            }

            if (_resources.TryGetValue(request.Url, out var data))
            {
                var type = _contentTypes.GetValueOrDefault(request.Url, "application/octet-stream");
                return Task.FromResult<ResourceResponse?>(ResourceResponse.Loaded(data, type));
            }

            return Task.FromResult<ResourceResponse?>(null);
        }
    }
}
