using System.Text;
using System.Text.Json;
using Folio.Resources;
using Folio.Hosting;
using Folio.Imaging;
using Folio.Scripting.Ipc;

namespace Folio.Scripting.Tests;

public sealed class ContentResourceTransportTests
{
    [Fact]
    public async Task MediaDecodeRequestUsesHostDecoderAndReturnsBoundedAudio()
    {
        using var payload = JsonSerializer.SerializeToDocument(new
        {
            data = Convert.ToBase64String(new byte[] { 1, 2, 3 }),
            contentType = "audio/test",
            video = false,
            maxOutputBytes = 64
        });
        var input = new MemoryStream(ContentProtocol.Encode(new ContentMessage(ContentMessageKind.MediaDecodeRequest, 41, payload.RootElement.Clone())));
        var output = new MemoryStream();
        await using var session = new ContentHostSession(input, output, mediaDecoder: new Decoder());
        await session.RunAsync(TestContext.Current.CancellationToken);
        output.Position = 0;
        var response = (await ContentTransport.ReadAsync(output, TestContext.Current.CancellationToken))!;
        Assert.Equal(ContentMessageKind.MediaDecodeResponse, response.Kind);
        var result = response.Payload.GetProperty("result");
        Assert.Equal(1.25, result.GetProperty("duration").GetDouble());
        Assert.Equal(2, result.GetProperty("audio").GetProperty("samples").GetArrayLength());
    }

    [Fact]
    public async Task MediaDecodeResponseRefusesOutputBeyondRequestedLimit()
    {
        using var payload = JsonSerializer.SerializeToDocument(new { data = Convert.ToBase64String(new byte[] { 1 }), video = false, maxOutputBytes = 4 });
        var input = new MemoryStream(ContentProtocol.Encode(new ContentMessage(ContentMessageKind.MediaDecodeRequest, 41, payload.RootElement.Clone())));
        var output = new MemoryStream();
        await using var session = new ContentHostSession(input, output, mediaDecoder: new OversizedDecoder());
        await session.RunAsync(TestContext.Current.CancellationToken);
        output.Position = 0;
        var response = (await ContentTransport.ReadAsync(output, TestContext.Current.CancellationToken))!;
        Assert.Equal(JsonValueKind.Null, response.Payload.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task MediaDecodeResponseChargesVideoAndAudioAgainstOneAggregateLimit()
    {
        using var payload = JsonSerializer.SerializeToDocument(new { data = Convert.ToBase64String(new byte[] { 1 }), video = true, maxOutputBytes = 12 });
        var input = new MemoryStream(ContentProtocol.Encode(new ContentMessage(ContentMessageKind.MediaDecodeRequest, 41, payload.RootElement.Clone())));
        var output = new MemoryStream();
        await using var session = new ContentHostSession(input, output, mediaDecoder: new CombinedOutputDecoder());
        await session.RunAsync(TestContext.Current.CancellationToken);
        output.Position = 0;
        var response = (await ContentTransport.ReadAsync(output, TestContext.Current.CancellationToken))!;
        Assert.Equal(JsonValueKind.Null, response.Payload.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task MediaAudioMessagesReachTheHostOutputAndLifecycle()
    {
        using var submit = JsonSerializer.SerializeToDocument(new { sampleRate = 8000, channels = 1, samples = new[] { .5f, -.5f } });
        using var start = JsonSerializer.SerializeToDocument(new { action = "start" });
        using var flush = JsonSerializer.SerializeToDocument(new { action = "flush" });
        var bytes = ContentProtocol.Encode(new ContentMessage(ContentMessageKind.MediaAudioSubmit, 1, submit.RootElement.Clone()))
            .Concat(ContentProtocol.Encode(new ContentMessage(ContentMessageKind.MediaAudioLifecycle, 2, start.RootElement.Clone())))
            .Concat(ContentProtocol.Encode(new ContentMessage(ContentMessageKind.MediaAudioLifecycle, 3, flush.RootElement.Clone())))
            .ToArray();
        var input = new MemoryStream(bytes);
        var output = new MemoryStream();
        var audio = new RecordingAudioOutput();
        await using var session = new ContentHostSession(input, output, mediaAudioOutput: audio);
        await session.RunAsync(TestContext.Current.CancellationToken);
        var frame = Assert.Single(audio.Frames);
        Assert.Equal(new[] { .5f, -.5f }, frame.Samples.ToArray());
        Assert.Equal(new[] { "start", "flush" }, audio.Lifecycle);
    }

    [Theory]
    [InlineData("{\"sampleRate\":8000,\"channels\":2,\"samples\":[0.1]}")]
    [InlineData("{\"sampleRate\":8000,\"channels\":1,\"samples\":[null]}")]
    [InlineData("{\"sampleRate\":0,\"channels\":1,\"samples\":[0.1]}")]
    public async Task InvalidMediaAudioMessagesAreDroppedBeforeHostOutput(string json)
    {
        using var payload = JsonDocument.Parse(json);
        var input = new MemoryStream(ContentProtocol.Encode(new ContentMessage(ContentMessageKind.MediaAudioSubmit, 1, payload.RootElement.Clone())));
        var output = new MemoryStream();
        var audio = new RecordingAudioOutput();
        await using var session = new ContentHostSession(input, output, mediaAudioOutput: audio);
        await session.RunAsync(TestContext.Current.CancellationToken);
        Assert.Empty(audio.Frames);
    }

    [Theory]
    [InlineData("{\"duration\":-1}")]
    [InlineData("{\"audio\":{\"sampleRate\":8000,\"channels\":2,\"samples\":[0.1]}}")]
    [InlineData("{\"audio\":{\"sampleRate\":8000,\"channels\":1,\"samples\":[null]}}")]
    [InlineData("{\"audio\":null}")]
    [InlineData("{\"frame\":{\"width\":1}}")]
    public void MediaDecodeWireRejectsMalformedDecodedPayloads(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<JsonException>(() => ContentHostSession.MediaDecodeWire.Read(document.RootElement));
    }
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"resource\"")]
    public async Task ResourceRequestWithNonObjectPayloadIsRefused(string json)
    {
        var response = await RunRequest(json);

        Assert.Equal(ContentMessageKind.ResourceResponse, response.Kind);
        Assert.Equal(0, response.Payload.GetProperty("status").GetInt32());
    }

    [Theory]
    [InlineData("\"Script\"")]
    [InlineData("999")]
    public async Task ResourceRequestWithInvalidKindIsRefusedWithoutCallingLoader(string kind)
    {
        var loader = new CountingLoader();
        var response = await RunRequest($"{{\"url\":\"https://example.test/app.js\",\"kind\":{kind}}}", loader);

        Assert.Equal(0, response.Payload.GetProperty("status").GetInt32());
        Assert.Equal(0, loader.Calls);
    }

    [Fact]
    public async Task HeartbeatWaitsBehindSerializedLoadCallbackUntilItFinishes()
    {
        using var loadPayload = JsonDocument.Parse("{\"html\":\"<p>blocked</p>\"}");
        using var heartbeatPayload = JsonDocument.Parse("{}");
        var bytes = ContentProtocol.Encode(new ContentMessage(ContentMessageKind.Load, 1, loadPayload.RootElement.Clone()))
            .Concat(ContentProtocol.Encode(new ContentMessage(ContentMessageKind.Heartbeat, 2, heartbeatPayload.RootElement.Clone())))
            .ToArray();
        var input = new MemoryStream(bytes);
        var output = new MemoryStream();
        await using var session = new ContentHostSession(input, output);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.MessageReceived += async (message, cancellationToken) =>
        {
            if (message.Kind == ContentMessageKind.Load)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
        };

        var run = session.RunAsync(TestContext.Current.CancellationToken).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(0, output.Length);
        release.TrySetResult();
        await run;

        output.Position = 0;
        var response = await ContentTransport.ReadAsync(output, TestContext.Current.CancellationToken);
        Assert.Equal(ContentMessageKind.Heartbeat, response!.Kind);
        Assert.Equal(2, response.Payload.GetProperty("acknowledged").GetInt64());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"requestId\":\"1\"}")]
    public async Task MalformedResourceResponseCompletesPendingRequestAndPreservesReaderError(string responseJson)
    {
        using var payload = JsonDocument.Parse(responseJson);
        var input = new MemoryStream(ContentProtocol.Encode(new ContentMessage(ContentMessageKind.ResourceResponse, 17, payload.RootElement.Clone())));
        var output = new MemoryStream();
        await using var session = new ContentHostSession(input, output);
        var request = session.RequestResourceAsync(new ResourceRequest("https://example.test/app.js", ResourceKind.Script), TestContext.Current.CancellationToken);
        await WaitForOutput(output);

        var error = await Assert.ThrowsAsync<InvalidDataException>(async () => await session.RunAsync(TestContext.Current.CancellationToken));
        var response = await request;

        Assert.Contains("correlation", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(response.Succeeded);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"response\"")]
    [InlineData("{\"requestId\":1}")]
    [InlineData("{\"requestId\":1,\"status\":\"ok\"}")]
    public async Task InvalidResourceResponseRootOrStatusCompletesPendingRequestAndFailsReader(string responseJson)
    {
        using var payload = JsonDocument.Parse(responseJson);
        var input = new MemoryStream(ContentProtocol.Encode(new ContentMessage(ContentMessageKind.ResourceResponse, 17, payload.RootElement.Clone())));
        var output = new MemoryStream();
        await using var session = new ContentHostSession(input, output);
        var request = session.RequestResourceAsync(new ResourceRequest("https://example.test/app.js", ResourceKind.Script), TestContext.Current.CancellationToken);
        await WaitForOutput(output);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await session.RunAsync(TestContext.Current.CancellationToken));
        var response = await request;
        Assert.False(response.Succeeded);
    }

    [Fact]
    public async Task FailedResourceResponseCannotBecomeSuccessfulWithData()
    {
        using var payload = JsonDocument.Parse("{\"requestId\":1,\"status\":0,\"data\":\"aGVsbG8=\",\"contentType\":\"text/plain\"}");
        var input = new MemoryStream(ContentProtocol.Encode(new ContentMessage(ContentMessageKind.ResourceResponse, 17, payload.RootElement.Clone())));
        var output = new MemoryStream();
        await using var session = new ContentHostSession(input, output);
        var request = session.RequestResourceAsync(new ResourceRequest("https://example.test/app.js", ResourceKind.Script), TestContext.Current.CancellationToken);
        await WaitForOutput(output);
        await session.RunAsync(TestContext.Current.CancellationToken);

        var response = await request;
        Assert.False(response.Succeeded);
        Assert.Null(response.Data);
    }

    [Fact]
    public async Task OutOfOrderResponsesOnlyCompleteTheirCorrelatedRequests()
    {
        var input = new MemoryStream();
        foreach (var (id, body) in new[] { (99L, "unrelated"), (2L, "second"), (1L, "first") })
        {
            using var payload = JsonSerializer.SerializeToDocument(new
            {
                requestId = id, status = 200, data = Encoding.UTF8.GetBytes(body), contentType = "text/plain"
            });
            await ContentTransport.WriteAsync(input, new ContentMessage(ContentMessageKind.ResourceResponse, id + 100, payload.RootElement.Clone()), TestContext.Current.CancellationToken);
        }
        input.Position = 0;
        await using var session = new ContentHostSession(input, new MemoryStream());
        var first = session.RequestResourceAsync(new ResourceRequest("https://example.test/first", ResourceKind.Fetch), TestContext.Current.CancellationToken);
        var second = session.RequestResourceAsync(new ResourceRequest("https://example.test/second", ResourceKind.Fetch), TestContext.Current.CancellationToken);
        await session.RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal("first", Encoding.UTF8.GetString((await first).Data!));
        Assert.Equal("second", Encoding.UTF8.GetString((await second).Data!));
    }

    [Fact]
    public async Task EndOfInputPreventsLaterResourceRequestFromWriting()
    {
        var input = new MemoryStream();
        var output = new MemoryStream();
        await using var session = new ContentHostSession(input, output);
        await session.RunAsync(TestContext.Current.CancellationToken);

        var response = await session.RequestResourceAsync(new ResourceRequest("https://example.test/app.js", ResourceKind.Script), TestContext.Current.CancellationToken);

        Assert.False(response.Succeeded);
        Assert.Equal(0, output.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ResourceResponseBodyHonorsEightMiBBound(int extraBytes)
    {
        var data = new byte[ContentHostSession.MaxResourceResponseBytes + extraBytes];
        using var payload = JsonSerializer.SerializeToDocument(new
        {
            requestId = 1L,
            status = 200,
            data,
            contentType = "application/octet-stream"
        });
        var input = new MemoryStream(ContentProtocol.Encode(new ContentMessage(ContentMessageKind.ResourceResponse, 17, payload.RootElement.Clone())));
        var output = new MemoryStream();
        await using var session = new ContentHostSession(input, output);
        var request = session.RequestResourceAsync(new ResourceRequest("https://example.test/blob", ResourceKind.Fetch), TestContext.Current.CancellationToken);
        await WaitForOutput(output);
        await session.RunAsync(TestContext.Current.CancellationToken);

        var response = await request;
        Assert.Equal(extraBytes == 0, response.Succeeded);
        if (extraBytes == 0) Assert.Equal(data.Length, response.Data!.Length);
        else Assert.Null(response.Data);
    }

    private static async Task<ContentMessage> RunRequest(string payloadJson, IResourceLoader? loader = null)
    {
        using var payload = JsonDocument.Parse(payloadJson);
        var input = new MemoryStream(ContentProtocol.Encode(new ContentMessage(ContentMessageKind.ResourceRequest, 41, payload.RootElement.Clone())));
        var output = new MemoryStream();
        await using var session = new ContentHostSession(input, output, resourceLoader: loader);
        await session.RunAsync(TestContext.Current.CancellationToken);
        output.Position = 0;
        return (await ContentTransport.ReadAsync(output, TestContext.Current.CancellationToken))!;
    }

    private static async Task WaitForOutput(MemoryStream output)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (output.Length == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(1, TestContext.Current.CancellationToken);
        Assert.True(output.Length > 0, "The resource request was not written before the test deadline.");
    }

    private sealed class CountingLoader : IResourceLoader
    {
        public int Calls { get; private set; }

        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<ResourceResponse?>(ResourceResponse.Loaded(Encoding.UTF8.GetBytes("loaded"), "text/plain"));
        }
    }

    private sealed class Decoder : IMediaDecoder
    {
        public MediaDecodeResult? Decode(ReadOnlyMemory<byte> data, string? contentType, bool video, int maxOutputBytes) =>
            new(1.25, null, new MediaAudioFrame(8000, 1, new float[] { .25f, -.25f }));
    }

    private sealed class OversizedDecoder : IMediaDecoder
    {
        public MediaDecodeResult? Decode(ReadOnlyMemory<byte> data, string? contentType, bool video, int maxOutputBytes) =>
            new(1, null, new MediaAudioFrame(8000, 1, new float[2]));
    }

    private sealed class CombinedOutputDecoder : IMediaDecoder
    {
        public MediaDecodeResult? Decode(ReadOnlyMemory<byte> data, string? contentType, bool video, int maxOutputBytes) =>
            new(1, new TestImage(2, 1, new byte[8]), new MediaAudioFrame(8000, 1, new float[2]));

        private sealed record TestImage(int Width, int Height, byte[] Data) : IImageHandle
        {
            public ReadOnlyMemory<byte> Pixels => Data;
        }
    }

    private sealed class RecordingAudioOutput : IMediaAudioOutput, IMediaAudioOutputLifecycle
    {
        public List<MediaAudioFrame> Frames { get; } = [];
        public List<string> Lifecycle { get; } = [];
        public void Submit(MediaAudioFrame frame) => Frames.Add(frame);
        public void Start() => Lifecycle.Add("start");
        public void Pause() => Lifecycle.Add("pause");
        public void Flush() => Lifecycle.Add("flush");
        public void Stop() => Lifecycle.Add("stop");
    }
}
