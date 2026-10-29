using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Folio;
using Folio.Hosting;
using Folio.Resources;
using Folio.Scripting.Ipc;

namespace Folio.Scripting.Tests;

public sealed class ContentResourceBrokerTests
{
    [Fact]
    public async Task ContentHostRejectsResourceRequestWithoutKind()
    {
        using var payload = JsonDocument.Parse("{\"url\":\"https://example.test/a.js\"}");
        var input = new MemoryStream(ContentProtocol.Encode(new ContentMessage(
            ContentMessageKind.ResourceRequest, 41, payload.RootElement.Clone())));
        var output = new MemoryStream();
        await using var session = new ContentHostSession(input, output);

        await session.RunAsync(TestContext.Current.CancellationToken);
        output.Position = 0;
        var response = await ContentTransport.ReadAsync(output, TestContext.Current.CancellationToken);

        Assert.Equal(ContentMessageKind.ResourceResponse, response!.Kind);
        Assert.Equal(0, response.Payload.GetProperty("status").GetInt32());
        Assert.Equal(41, response.Payload.GetProperty("requestId").GetInt64());
    }

    [Fact]
    public async Task ContentHostAcknowledgesHeartbeatExactlyOnce()
    {
        using var payload = JsonDocument.Parse("{}");
        var input = new MemoryStream(ContentProtocol.Encode(new ContentMessage(ContentMessageKind.Heartbeat, 8, payload.RootElement.Clone())));
        var output = new MemoryStream();
        await using var session = new ContentHostSession(input, output);

        await session.RunAsync(TestContext.Current.CancellationToken);
        output.Position = 0;
        var response = await ContentTransport.ReadAsync(output, TestContext.Current.CancellationToken);
        var second = await ContentTransport.ReadAsync(output, TestContext.Current.CancellationToken);

        Assert.Equal(ContentMessageKind.Heartbeat, response!.Kind);
        Assert.Equal(8, response.Payload.GetProperty("acknowledged").GetInt64());
        Assert.Null(second);
    }

    [Fact]
    public async Task ContentProcessLoadsRelativeExternalScriptThroughHostLoaderOnce()
    {
        const string source = "globalThis.externalValue = 42;";
        var integrity = $"sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(source)))}";
        var loader = new RecordingLoader((request, _) =>
        {
            if (request.Kind != ResourceKind.Script || request.Url != "https://example.test/scripts/app.js")
                return ResourceResponse.Refused("unexpected request");
            Assert.Equal(integrity, request.Integrity);
            return ResourceResponse.Loaded(Encoding.UTF8.GetBytes(source), "application/javascript");
        });
        await using var client = StartClient(loader, new ContentResourcePolicy
        {
            AllowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "https://example.test" }
        });

        var frame = await Load(client, new
        {
            html = $"<script src='scripts/app.js' integrity='{integrity}'></script>",
            script = "externalValue",
            url = "https://example.test/index.html"
        });

        Assert.Equal("42", frame.Payload.GetProperty("value").GetString());
        var request = Assert.Single(loader.Requests);
        Assert.Equal(ResourceKind.Script, request.Kind);
        Assert.Equal("https://example.test/scripts/app.js", request.Url);
    }

    [Fact]
    public async Task ContentProcessFetchUsesLoaderWithoutReaderDeadlock()
    {
        var loader = new RecordingLoader((request, _) => request.Kind == ResourceKind.Fetch
            ? ResourceResponse.Loaded(Encoding.UTF8.GetBytes("broker-body"), "text/plain")
            : ResourceResponse.Refused("unsupported"));
        await using var client = StartClient(loader, AllowExampleOrigin());

        var frame = await Load(client, new
        {
            html = "<script>fetch('/data.txt').then(r => r.text()).then(v => globalThis.fetchValue1 = v); fetch('/data.txt').then(r => r.text()).then(v => globalThis.fetchValue2 = v); globalThis.inlineCount = (globalThis.inlineCount || 0) + 1;</script>",
            script = "inlineCount + ':' + fetchValue1 + ':' + fetchValue2",
            url = "https://example.test/index.html"
        });

        Assert.Equal("1:broker-body:broker-body", frame.Payload.GetProperty("value").GetString());
        Assert.Equal(2, loader.Requests.Count);
        Assert.All(loader.Requests, request =>
        {
            Assert.Equal(ResourceKind.Fetch, request.Kind);
            Assert.Equal("https://example.test/data.txt", request.Url);
        });
    }

    [Fact]
    public async Task ContentProcessMediaDecoderBridgeUsesHostDecoder()
    {
        var loader = new RecordingLoader((request, _) => ResourceResponse.Loaded(Encoding.ASCII.GetBytes("RIFFxxxxWAVE"), "audio/test"));
        var decoder = new DebugDecoder();
        var output = new DebugAudioOutput();
        await using var client = ContentProcessClient.Start("dotnet", $"\"{ContentHostPath()}\"", new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromSeconds(15)), resourceLoader: loader, resourcePolicy: AllowExampleOrigin(), mediaDecoder: decoder, mediaAudioOutput: output);
        var frame = await Load(client, new { html = "<audio id='a' src='clip'></audio>", script = "const a=document.getElementById('a'); a.play(); a.readyState + ':' + a.error + ':' + a.paused", url = "https://example.test/index.html", capabilities = new FeatureCapabilities { Media = true, Sound = true } });
        Assert.Equal("4:null:false", frame.Payload.GetProperty("value").GetString());
        Assert.Equal(1, decoder.Calls);
        Assert.NotEmpty(output.Frames);
        Assert.Contains("start", output.Lifecycle);
    }

    [Fact]
    public async Task ContentProcessMediaCapabilityRevocationAndRegrantSwitchesTheLiveElement()
    {
        var loader = new RecordingLoader((request, _) =>
            request.Kind == ResourceKind.Media
                ? ResourceResponse.Loaded(Encoding.ASCII.GetBytes("RIFFxxxxWAVE"), "audio/test")
                : ResourceResponse.Refused("unsupported"));
        var decoder = new DebugDecoder();
        await using var client = ContentProcessClient.Start("dotnet", $"\"{ContentHostPath()}\"",
            new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromSeconds(15)),
            resourceLoader: loader, resourcePolicy: AllowExampleOrigin(), mediaDecoder: decoder);

        var first = await Load(client, new
        {
            html = "<audio id='a' src='clip'></audio>",
            script = "const a=document.getElementById('a'); a.readyState + ':' + a.error",
            url = "https://example.test/index.html",
            capabilities = new FeatureCapabilities { Media = true }
        });
        Assert.Equal("4:null", first.Payload.GetProperty("value").GetString());
        var initialCalls = decoder.Calls;

        await UpdateCapabilities(client, new FeatureCapabilities());
        var revoked = await Load(client, new { script = "a.error + ':' + a.readyState + ':' + a.paused" });
        Assert.Equal("Media is disabled by the host.:0:true", revoked.Payload.GetProperty("value").GetString());

        await UpdateCapabilities(client, new FeatureCapabilities { Media = true });
        var restored = await Load(client, new { script = "a.error + ':' + a.readyState" });
        Assert.Equal("null:4", restored.Payload.GetProperty("value").GetString());
        Assert.True(decoder.Calls > initialCalls);
    }

    [Fact]
    public async Task ContentProcessDeniedOriginDoesNotCallLoader()
    {
        var loader = new RecordingLoader((_, _) => ResourceResponse.Loaded(Encoding.UTF8.GetBytes("globalThis.loaded = true;"), "application/javascript"));
        await using var client = StartClient(loader, new ContentResourcePolicy
        {
            AllowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "https://other.example" }
        });

        var frame = await Load(client, new
        {
            html = "<script src='https://example.test/app.js'></script>",
            script = "typeof loaded",
            url = "https://example.test/index.html"
        });

        Assert.Equal("undefined", frame.Payload.GetProperty("value").GetString());
        Assert.Empty(loader.Requests);
    }

    [Fact]
    public async Task ContentProcessLoaderRefusalDoesNotExecuteExternalScript()
    {
        var loader = new RecordingLoader((_, _) => ResourceResponse.Refused("host refused"));
        await using var client = StartClient(loader, AllowExampleOrigin());

        var frame = await Load(client, new
        {
            html = "<script src='app.js'></script>",
            script = "typeof loaded",
            url = "https://example.test/index.html"
        });

        Assert.Equal("undefined", frame.Payload.GetProperty("value").GetString());
        Assert.Single(loader.Requests);
    }

    [Fact]
    public async Task ContentProcessRejectsOversizedResourceResponse()
    {
        var loader = new RecordingLoader((_, _) =>
            ResourceResponse.Loaded(new byte[ContentHostSession.MaxResourceResponseBytes + 1], "application/javascript"));
        await using var client = StartClient(loader, AllowExampleOrigin());

        var result = await LoadFrameOrDiagnostic(client, new
        {
            html = "<script src='large.js'></script>",
            script = "typeof loaded",
            url = "https://example.test/index.html"
        });

        Assert.Single(loader.Requests);
        Assert.Equal(ContentMessageKind.Frame, result.Kind);
        Assert.Equal("undefined", result.Payload.GetProperty("value").GetString());
    }

    [Fact]
    public async Task ContentProcessReloadStartsWithFreshGlobalsAndPreservesServiceWorkerNavigator()
    {
        await using var client = StartClient(new RecordingLoader((_, _) => ResourceResponse.Refused("unused")), AllowExampleOrigin());
        var capabilities = new FeatureCapabilities { ServiceWorkers = true };

        var first = await Load(client, new
        {
            html = "<script>globalThis.leaked = 7;</script>",
            script = "leaked",
            url = "https://example.test/first.html",
            capabilities
        });
        Assert.Equal("7", first.Payload.GetProperty("value").GetString());

        var second = await Load(client, new
        {
            html = "<p>second</p>",
            script = "typeof leaked + ':' + typeof navigator.serviceWorker",
            url = "https://example.test/second.html",
            capabilities
        });
        Assert.Equal("undefined:object", second.Payload.GetProperty("value").GetString());
    }

    private static ContentResourcePolicy AllowExampleOrigin() => new()
    {
        AllowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "https://example.test" }
    };

    private static ContentProcessClient StartClient(RecordingLoader loader, ContentResourcePolicy policy) =>
        ContentProcessClient.Start("dotnet", $"\"{ContentHostPath()}\"",
            new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromSeconds(15)),
            resourceLoader: loader, resourcePolicy: policy);

    private static async Task<ContentMessage> Load(ContentProcessClient client, object payload)
    {
        var result = await LoadFrameOrDiagnostic(client, payload);
        Assert.True(result.Kind == ContentMessageKind.Frame,
            result.Kind == ContentMessageKind.Diagnostic ? result.Payload.GetRawText() : $"Unexpected {result.Kind}");
        return result;
    }

    private static async Task<ContentMessage> LoadFrameOrDiagnostic(ContentProcessClient client, object payload)
    {
        var completion = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask Handler(ContentMessage message, CancellationToken _) {
            if (message.Kind is ContentMessageKind.Frame or ContentMessageKind.Diagnostic)
                completion.TrySetResult(message);
            return ValueTask.CompletedTask;
        }
        client.MessageReceived += Handler;
        try
        {
            await client.SendAsync(ContentMessageKind.Load, payload, TestContext.Current.CancellationToken);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        }
        finally { client.MessageReceived -= Handler; }
    }

    private static async Task UpdateCapabilities(ContentProcessClient client, FeatureCapabilities capabilities)
    {
        var completion = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask Handler(ContentMessage message, CancellationToken _) {
            if (message.Kind == ContentMessageKind.Capabilities) completion.TrySetResult(message);
            return ValueTask.CompletedTask;
        }
        client.MessageReceived += Handler;
        try
        {
            await client.UpdateCapabilitiesAsync(capabilities, TestContext.Current.CancellationToken);
            var response = await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.True(response.Payload.GetProperty("accepted").GetBoolean());
        }
        finally { client.MessageReceived -= Handler; }
    }

    private static string ContentHostPath()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Parent!.Name;
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/Folio.ContentHost/bin", configuration, "net10.0/Folio.ContentHost.dll"));
    }

    private sealed class RecordingLoader(Func<ResourceRequest, CancellationToken, ResourceResponse> load) : IResourceLoader
    {
        public ConcurrentQueue<ResourceRequest> Requests { get; } = new();

        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            return Task.FromResult<ResourceResponse?>(load(request, cancellationToken));
        }
    }

    private sealed class DebugDecoder : IMediaDecoder
    {
        public int Calls;
        public MediaDecodeResult? Decode(ReadOnlyMemory<byte> data, string? contentType, bool video, int maxOutputBytes)
        {
            Interlocked.Increment(ref Calls);
            return new MediaDecodeResult(1, null, new MediaAudioFrame(8000, 1, new float[] { .25f }));
        }
    }

    private sealed class DebugAudioOutput : IMediaAudioOutput, IMediaAudioOutputLifecycle
    {
        public ConcurrentQueue<MediaAudioFrame> Frames { get; } = new();
        public ConcurrentQueue<string> Lifecycle { get; } = new();
        public void Submit(MediaAudioFrame frame) => Frames.Enqueue(frame);
        public void Start() => Lifecycle.Enqueue("start");
        public void Pause() => Lifecycle.Enqueue("pause");
        public void Flush() => Lifecycle.Enqueue("flush");
        public void Stop() => Lifecycle.Enqueue("stop");
    }

}
