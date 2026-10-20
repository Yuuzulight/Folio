using Folio.Scripting.Ipc;

namespace Folio.Scripting.Tests;

[Collection("Content process round trips")]
public sealed class WebAssemblyContentProcessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealContentProcessRunsNestedBlobWorkersAndTheirPlatformApis(bool wasm)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        var host = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/Folio.ContentHost/bin", configuration, "net10.0/Folio.ContentHost.dll"));
        await using var client = ContentProcessClient.Start(host,
            limits: new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromSeconds(15)),
            documentLimits: ResourceLimits.Default with { MaxWorkers = 2 });
        const string nested = "onmessage=()=>{var bytes=new Uint8Array(8),identity=crypto.getRandomValues(bytes)===bytes;var target=new EventTarget(),abort=new AbortController(),count=0;target.addEventListener('x',()=>count++,{signal:abort.signal});target.dispatchEvent(new Event('x'));abort.abort();target.dispatchEvent(new Event('x'));console.warn('nested console');fetch('data:text/plain,%E2%98%83').then(r=>r.text()).then(text=>postMessage(new TextDecoder().decode(new TextEncoder().encode(text))+':'+identity+':'+count+':'+typeof document+':'+typeof window));};";
        var source = "var nested=new Worker(URL.createObjectURL(new Blob([" + System.Text.Json.JsonSerializer.Serialize(nested) + "])));nested.onmessage=e=>postMessage(e.data);onmessage=()=>nested.postMessage(0);";
        var create = "var responses=[];var worker=new Worker('data:text/javascript,'+encodeURIComponent(" + System.Text.Json.JsonSerializer.Serialize(source) + "));worker.onmessage=e=>responses.push(String(e.data));";
        var grants = new FeatureCapabilities { Workers = true, WebAssembly = wasm };
        await Exchange(client, ContentMessageKind.Load, new { html = "<script>" + create + "</script>", script = "'ready'", capabilities = grants });
        async Task Probe()
        {
            await Exchange(client, ContentMessageKind.Load, new { script = "worker.postMessage(0);'sent';" });
            var result = await Exchange(client, ContentMessageKind.Load, new { script = "responses[responses.length-1]" });
            Assert.Equal("☃:true:1:undefined:undefined", Value(result));
            Assert.Contains(result.Payload.GetProperty("diagnostics").EnumerateArray(), d => d.GetString()!.StartsWith("Worker (blob:folio-", StringComparison.Ordinal) && d.GetString()!.EndsWith(": nested console", StringComparison.Ordinal));
        }
        await Probe();
        await Exchange(client, ContentMessageKind.Capabilities, new { capabilities = FeatureCapabilities.Default });
        await Exchange(client, ContentMessageKind.Capabilities, new { capabilities = grants });
        await Exchange(client, ContentMessageKind.Load, new { script = create });
        await Probe();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealContentProcessWorkerCanFetchWithoutWindowBindings(bool wasm)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        var host = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/Folio.ContentHost/bin", configuration, "net10.0/Folio.ContentHost.dll"));
        await using var client = ContentProcessClient.Start(host,
            limits: new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromSeconds(15)));
        const string source = "onmessage=()=>fetch('data:text/plain,%E2%98%83').then(r=>r.text()).then(text=>postMessage(text+':'+typeof document+':'+typeof window));";
        var create = "var responses=[];var worker=new Worker('data:text/javascript,'+encodeURIComponent(" + System.Text.Json.JsonSerializer.Serialize(source) + "));worker.onmessage=e=>responses.push(String(e.data));";
        await Exchange(client, ContentMessageKind.Load, new { html = "<script>" + create + "</script>", script = "'ready'", capabilities = new FeatureCapabilities { Workers = true, WebAssembly = wasm } });
        await Exchange(client, ContentMessageKind.Load, new { script = "worker.postMessage(0);'sent';" });
        Assert.Equal("☃:undefined:undefined", Value(await Exchange(client, ContentMessageKind.Load, new { script = "responses[responses.length-1]" })));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealContentProcessPropagatesGrantsToExistingWorkersAndReleasesRevokedWorkers(bool initiallyGranted)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        var host = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/Folio.ContentHost/bin", configuration, "net10.0/Folio.ContentHost.dll"));
        await using var client = ContentProcessClient.Start(host,
            limits: new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromSeconds(15)),
            documentLimits: ResourceLimits.Default with { MaxWebAssemblyBytes = 65536, MaxWorkers = 1 });
        static FeatureCapabilities Grants(bool wasm) => new() { Workers = true, WebAssembly = wasm };
        const string workerSource = "var retained=typeof WebAssembly==='undefined'?null:WebAssembly; onmessage=()=>{try{postMessage(new retained.Memory({initial:1}).buffer.byteLength);}catch(error){postMessage(error.name);}};";
        var create = "var responses=[];var worker=new Worker('data:text/javascript,'+encodeURIComponent(" + System.Text.Json.JsonSerializer.Serialize(workerSource) + "));worker.onmessage=e=>responses.push(String(e.data));";
        await Exchange(client, ContentMessageKind.Load, new { html = "<script>" + create + "</script>", script = "'ready'", capabilities = Grants(initiallyGranted) });
        async Task<string?> Probe()
        {
            await Exchange(client, ContentMessageKind.Load, new { script = "worker.postMessage(0);'sent';" });
            return Value(await Exchange(client, ContentMessageKind.Load, new { script = "responses[responses.length-1]" }));
        }
        Assert.Equal(initiallyGranted ? "65536" : "NotSupportedError", await Probe());
        await Exchange(client, ContentMessageKind.Capabilities, new { capabilities = Grants(true) });
        Assert.Equal("65536", await Probe());
        await Exchange(client, ContentMessageKind.Capabilities, new { capabilities = Grants(false) });
        Assert.Equal("NotSupportedError", await Probe());
        var report = await Exchange(client, ContentMessageKind.Load, new { script = "'diagnostics'" });
        Assert.Single(report.Payload.GetProperty("diagnostics").EnumerateArray(),
            item => item.GetString()!.Contains("WebAssembly is disabled by the host.", StringComparison.Ordinal));
        await Exchange(client, ContentMessageKind.Capabilities, new { capabilities = Grants(true) });
        Assert.Equal("65536", await Probe());
        await Exchange(client, ContentMessageKind.Capabilities, new { capabilities = FeatureCapabilities.Default });
        await Exchange(client, ContentMessageKind.Capabilities, new { capabilities = Grants(true) });
        var fresh = "worker=new Worker('data:text/javascript,'+encodeURIComponent(\"onmessage=()=>postMessage('fresh');\"));worker.onmessage=e=>responses.push(e.data);'created';";
        Assert.Equal("created", Value(await Exchange(client, ContentMessageKind.Load, new { script = fresh })));
        Assert.Equal("fresh", await Probe());
    }

    [Fact]
    public async Task RealContentProcessAppliesGrantsRevocationAndRegrantToRetainedApi()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        var host = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/Folio.ContentHost/bin", configuration, "net10.0/Folio.ContentHost.dll"));
        await using var client = ContentProcessClient.Start(host,
            limits: new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromSeconds(15)),
            documentLimits: ResourceLimits.Default with { MaxWebAssemblyBytes = 65536 });
        var granted = new FeatureCapabilities { WebAssembly = true };
        var initial = await Exchange(client, ContentMessageKind.Load, new
        {
            html = "<script>var retained=WebAssembly; var bytes=new Uint8Array([0,97,115,109,1,0,0,0]);</script>",
            script = "retained.validate(bytes)", capabilities = granted
        });
        Assert.Equal("true", Value(initial));

        var revoked = await Exchange(client, ContentMessageKind.Capabilities, new { capabilities = FeatureCapabilities.Default });
        Assert.True(revoked.TryGetCapabilities(out var revokedCapabilities));
        Assert.False(revokedCapabilities.WebAssembly);
        const string attempt = "__folio_wasm_enabled=true; try {new retained.Memory({initial:1}); 'allowed';} catch(error) {error.name;}";
        var denied = await Exchange(client, ContentMessageKind.Load, new { script = attempt });
        Assert.Equal("NotSupportedError", Value(denied));
        var repeated = await Exchange(client, ContentMessageKind.Load, new { script = attempt });
        Assert.Equal("NotSupportedError", Value(repeated));
        Assert.Single(repeated.Payload.GetProperty("diagnostics").EnumerateArray(),
            item => item.GetString()!.Contains("WebAssembly is disabled by the host.", StringComparison.Ordinal));

        var restored = await Exchange(client, ContentMessageKind.Capabilities, new { capabilities = granted });
        Assert.True(restored.TryGetCapabilities(out var restoredCapabilities));
        Assert.True(restoredCapabilities.WebAssembly);
        Assert.Equal("65536", Value(await Exchange(client, ContentMessageKind.Load,
            new { script = "new retained.Memory({initial:1}).buffer.byteLength" })));
        Assert.Equal("RangeError", Value(await Exchange(client, ContentMessageKind.Load, new
        {
            script = "__folio_wasm_max_pages=100; try {new retained.Memory({initial:2}); 'allowed';} catch(error) {error.name;}"
        })));

        // A fresh document's denied load must not inherit the previous grant.
        Assert.Equal("false", Value(await Exchange(client, ContentMessageKind.Load, new
        {
            html = "<p>fresh denied document</p>", capabilities = FeatureCapabilities.Default,
            script = "__folio_wasm_enabled=true; WebAssembly.validate(new Uint8Array([0,97,115,109,1,0,0,0]))"
        })));
    }

    private static string? Value(ContentMessage message)
    {
        Assert.Equal(ContentMessageKind.Frame, message.Kind);
        return message.Payload.GetProperty("value").GetString();
    }

    private static async Task<ContentMessage> Exchange(ContentProcessClient client, ContentMessageKind kind, object payload)
    {
        var received = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask Handler(ContentMessage message, CancellationToken _)
        {
            if (message.Kind is ContentMessageKind.Frame or ContentMessageKind.Capabilities or ContentMessageKind.Diagnostic)
                received.TrySetResult(message);
            return ValueTask.CompletedTask;
        }
        client.MessageReceived += Handler;
        try
        {
            await client.SendAsync(kind, payload, TestContext.Current.CancellationToken);
            var result = await received.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.NotEqual(ContentMessageKind.Diagnostic, result.Kind);
            Assert.Equal(kind == ContentMessageKind.Capabilities ? ContentMessageKind.Capabilities : ContentMessageKind.Frame, result.Kind);
            return result;
        }
        finally { client.MessageReceived -= Handler; }
    }
}
