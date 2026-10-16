using Folio.Dom;
using Folio.Html;
using Folio.Resources;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

/// <summary>
/// Restricted fetch (issue #497): <c>fetch</c>, <c>Headers</c>, <c>Request</c> and <c>Response</c> over <c>data:</c> URLs
/// and the host's loader, GET and HEAD only, with only the content type and length as response headers.
/// The scripts chain promises and cover <c>await</c> resumption across host tasks.
/// </summary>
public class FetchTests
{
    private sealed class FakeLoader : IResourceLoader
    {
        public List<ResourceRequest> Requests { get; } = [];
        public Dictionary<string, (byte[] Data, string? Type)> Responses { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult<ResourceResponse?>(Responses.TryGetValue(request.Url, out var hit)
                ? ResourceResponse.Loaded(hit.Data, hit.Type)
                : ResourceResponse.Refused("Not allowed by the fake loader"));
        }
    }

    // Helpers the scripts share: seq runs a promise-returning step per item, in order; get reads a URL to {r, t}.
    private const string Helpers = @"
        const seq = (items, step) => items.reduce((p, x) => p.then(() => step(x)), Promise.resolve());
        const get = (url, init) => fetch(url, init).then(r => r.text().then(t => ({ r, t })));
        const name = (e) => e && e.name;";

    // Runs the script (a promise chain that fills `log`), lets promises and timers settle, and returns the log.
    private static string Run(string script, FakeLoader? loader = null, List<Diagnostic>? diagnostics = null)
    {
        var document = TreeBuilder.Parse("<html><body></body></html>", new ParserLimits(ScriptingEnabled: true));
        using var engine = new JintScriptEngine();
        var loop = new HtmlEventLoop(engine);
        ScriptGlobalsInstaller.Install(loop, engine);
        var baseUri = new Uri("https://example.com/app/index.html");
        DomBindings.Install(engine, document, eventLoop: loop, resourceLoader: loader, baseUri: baseUri);
        var options = new FolioOptions { Scripting = ScriptingPolicy.ContentProcess, BaseUri = baseUri, ResourceLoader = loader };
        using var runner = new ScriptRunner(document, engine, loop, options, diagnostics ?? []);
        engine.Evaluate("var log = [];" + Helpers + "(" + script + ").catch(e => log.push('threw ' + e.name + ': ' + e.message));");
        runner.Run();
        loop.SpinUntilIdle();
        return engine.Evaluate("log.join('|')").AsString()!;
    }

    [Fact]
    public void AwaitResumesAfterAHostTimerTask()
    {
        var log = Run("(async () => { await new Promise(resolve => setTimeout(resolve, 0)); log.push('timer'); })()");

        Assert.Equal("timer", log);
    }

    [Fact]
    public void AwaitResumesAfterAHostFetchTask()
    {
        var log = Run("(async () => { const response = await fetch('data:,ready'); log.push(await response.text()); })()");

        Assert.Equal("ready", log);
    }

    [Fact]
    public void DataUrlsDecodeFromPercentEncodingAndBase64AndKeepTheirMimeParameters()
    {
        var log = Run(@"
            get('data:text/plain;charset=utf-8,hello%20w%C3%B6rld')
                .then(({ r, t }) => { log.push(r.status + ' ' + r.ok + ' ' + r.statusText + ' ' + r.headers.get('content-type') + ' ' + t); return get('data:text/plain;base64,aGVsbG8gd29ybGQ='); })
                .then(({ t }) => { log.push(t); return get('data:,plain'); })
                .then(({ r, t }) => { log.push(r.headers.get('content-type') + ' ' + t); })");
        Assert.Equal("200 true OK text/plain;charset=utf-8 hello wörld|hello world|text/plain;charset=us-ascii plain", log);
    }

    [Fact]
    public void TextJsonAndArrayBufferBodiesCanEachBeReadOnce()
    {
        var log = Run(@"
            fetch('data:application/json,%7B%22n%22%3A%5B1%2C2%5D%7D')
                .then(j => j.json().then(parsed => { log.push(parsed.n.join(',') + ' used=' + j.bodyUsed); return j.text(); }).catch(e => log.push(name(e))))
                .then(() => fetch('data:application/octet-stream;base64,AAH//g=='))
                .then(b => b.arrayBuffer())
                .then(buffer => log.push(buffer.byteLength + ':' + Array.from(new Uint8Array(buffer)).join(',')))
                .then(() => fetch('data:text/plain,copy'))
                .then(c => { const clone = c.clone(); return Promise.all([c.text(), clone.text()]); })
                .then(([a, b]) => log.push(a + '+' + b))");
        Assert.Equal("1,2 used=true|TypeError|4:0,1,255,254|copy+copy", log);
    }

    [Fact]
    public void ResponseHeadersAreOnlyTheContentTypeAndLength()
    {
        var loader = new FakeLoader();
        loader.Responses["https://cdn.example/data.json"] = (System.Text.Encoding.UTF8.GetBytes("{\"a\":1}"), "application/json");
        var log = Run(@"
            fetch('https://cdn.example/data.json').then(r => {
                log.push(Array.from(r.headers.keys()).sort().join(','));
                log.push(r.headers.get('Content-Length') + ' ' + r.headers.get('CONTENT-TYPE') + ' ' + r.headers.has('set-cookie') + ' ' + r.url + ' ' + r.type + ' ' + r.redirected);
            })", loader);
        Assert.Equal("content-length,content-type|7 application/json false https://cdn.example/data.json basic false", log);
    }

    [Fact]
    public void AnHttpsFetchGoesToTheHostLoaderAsAFetchRequestWithNoCredentialsAndRelativeUrlsResolveAgainstTheDocument()
    {
        var loader = new FakeLoader();
        loader.Responses["https://example.com/app/api/items.json"] = (System.Text.Encoding.UTF8.GetBytes("[1]"), "application/json");
        var log = Run("get('api/items.json', { credentials: 'include', headers: { Cookie: 'x=1', Referer: 'y' } }).then(({ r, t }) => { log.push(t + ' ' + r.url); })", loader);
        Assert.Equal("[1] https://example.com/app/api/items.json", log);
        var request = Assert.Single(loader.Requests);
        Assert.Equal(ResourceKind.Fetch, request.Kind);
        Assert.Equal("https://example.com/app/api/items.json", request.Url);
    }

    [Fact]
    public void RefusedUrlsAndUnsupportedSchemesRejectWithATypeErrorAndReportADiagnostic()
    {
        var diagnostics = new List<Diagnostic>();
        var log = Run(@"
            seq(['https://blocked.example/x', 'file:///C:/secret.txt', 'blob:https://example.com/1', 'javascript:alert(1)', 'ftp://example.com/x'],
                url => fetch(url).then(() => log.push('resolved'), e => log.push(name(e))))", new FakeLoader(), diagnostics);
        Assert.Equal("TypeError|TypeError|TypeError|TypeError|TypeError", log);
        Assert.True(diagnostics.Count(d => d.Code == DiagnosticCode.ResourceNotLoaded) >= 5);
    }

    [Fact]
    public void OnlyGetAndHeadAreAllowedAndHeadHasNoBody()
    {
        var log = Run(@"
            seq(['POST', 'PUT', 'DELETE', 'PATCH', 'OPTIONS'], method => fetch('data:,x', { method }).then(() => log.push(method + ' resolved'), e => log.push(method + ' ' + name(e))))
                .then(() => get('data:text/plain,body', { method: 'head' }))
                .then(({ r, t }) => log.push(r.status + ' [' + t + '] ' + r.headers.get('content-length')))
                .then(() => fetch('data:,x', { method: 'GET', body: 'x' }).then(() => log.push('body resolved'), e => log.push('body ' + name(e))))");
        Assert.Equal("POST TypeError|PUT TypeError|DELETE TypeError|PATCH TypeError|OPTIONS TypeError|200 [] 4|body TypeError", log);
    }

    [Fact]
    public void AnAbortBeforeTheCallBetweenTheCallAndTheLoadAndBeforeTheBodyIsReadRejects()
    {
        var log = Run(@"
            (() => {
            const before = new AbortController(); before.abort();
            return fetch('data:,x', { signal: before.signal }).then(() => log.push('before resolved'), e => log.push('before ' + name(e)))
                .then(() => {
                    const during = new AbortController();
                    const pending = fetch('data:,x', { signal: during.signal });
                    during.abort(new DOMException('stop', 'AbortError'));
                    return pending.then(() => log.push('during resolved'), e => log.push('during ' + name(e) + ' ' + e.message));
                })
                .then(() => {
                    const reading = new AbortController();
                    return fetch('data:,body', { signal: reading.signal }).then(response => {
                        reading.abort();
                        return response.text().then(() => log.push('reading resolved'), e => log.push('reading ' + name(e)));
                    });
                });
            })()");
        Assert.Equal("before AbortError|during AbortError stop|reading AbortError", log);
    }

    [Fact]
    public void AResponseOverTheByteCapRejectsAndFetchedBytesCountTowardsTheDocumentTotal()
    {
        var loader = new FakeLoader();
        loader.Responses["https://cdn.example/big"] = (new byte[ResourceLoader.MaxResourceBytes + 1], "application/octet-stream");
        loader.Responses["https://cdn.example/chunk"] = (new byte[16 * 1024 * 1024], "application/octet-stream");
        var log = Run(@"
            (() => {
            let ok = 0, stopped = false;
            return fetch('https://cdn.example/big').then(() => log.push('big resolved'), e => log.push('big ' + name(e)))
                .then(() => seq([1, 2, 3, 4, 5, 6], i => stopped ? null : fetch('https://cdn.example/chunk').then(() => { ok++; }, e => { stopped = true; log.push('chunk ' + i + ' ' + name(e)); })))
                .then(() => log.push('ok=' + ok));
            })()", loader);
        // 4 x 16 MB is 64 MB: the fifth chunk goes over the document's total.
        Assert.Equal("big TypeError|chunk 5 TypeError|ok=4", log);
    }

    [Fact]
    public void HeadersRequestAndResponseBehaveLikeTheirStandardCounterparts()
    {
        var log = Run(@"
            Promise.resolve().then(() => {
                const h = new Headers({ 'X-One': '1', 'x-two': '2' });
                h.append('x-one', '11'); h.set('X-Three', '3'); h.delete('x-two');
                log.push([...h].map(([k, v]) => k + '=' + v).join(','));
                log.push(h.get('x-one') + ' ' + h.has('X-TWO') + ' ' + h.get('missing'));
                const r = new Request('data:,x', { method: 'head', headers: [['A', 'b']] });
                log.push(r.method + ' ' + r.url + ' ' + r.headers.get('a'));
                const resp = new Response('héllo', { status: 201, statusText: 'Made', headers: { 'Content-Type': 'text/plain' } });
                log.push(resp.status + ' ' + resp.ok + ' ' + resp.statusText);
                return resp.text();
            }).then(text => { log.push(text); log.push(Response.error().type + ' ' + Response.error().status); return Response.json({ a: 1 }).text(); })
              .then(json => log.push(json))");
        Assert.Equal("x-one=1, 11,x-three=3|1, 11 false null|HEAD data:,x b|201 true Made|héllo|error 0|{\"a\":1}", log);
    }
}
