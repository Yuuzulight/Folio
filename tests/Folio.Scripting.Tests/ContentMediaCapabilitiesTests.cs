using Folio.Scripting.Ipc;

namespace Folio.Scripting.Tests;

[Collection("Content process round trips")]
public sealed class ContentMediaCapabilitiesTests
{
    [Fact]
    public async Task ChildCanPlayTypeUsesTheHostDecoderDecisionInsteadOfClaimingEveryCodec()
    {
        var decoder = new Decoder();
        await using var client = ContentProcessClient.StartWithOptions(ContentHostTestPaths.Resolve(), new FolioOptions { MediaDecoder = decoder });
        var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask Receive(ContentMessage message, CancellationToken cancellation)
        {
            if (message.Kind == ContentMessageKind.Frame && message.Payload.GetProperty("sequence").GetInt64() > 0) answer.TrySetResult(message.Payload.GetProperty("value").GetString()!);
            return ValueTask.CompletedTask;
        }
        client.MessageReceived += Receive;
        try
        {
            await client.SendAsync(ContentMessageKind.Load, new
            {
                html = "<video id='v'></video>", capabilities = new FeatureCapabilities { Media = true },
                script = "var v=document.getElementById('v');v.canPlayType('video/mp4')+'|'+v.canPlayType('video/ogg')"
            }, TestContext.Current.CancellationToken);
            Assert.Equal("maybe|", await answer.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
            Assert.Equal(0, decoder.Decodes);
            Assert.Equal(2, decoder.Queries);
        }
        finally { client.MessageReceived -= Receive; }
    }

    [Theory]
    [InlineData("codec")]
    [InlineData("decode")]
    [InlineData("capture")]
    public async Task ClosingTheConnectionCompletesPendingMediaWaitersWithoutWaitingForTheirTimeout(string operation)
    {
        var output = new NotifyingStream();
        var session = new ContentHostSession(new MemoryStream(), output);
        var request = Task.Run(async () =>
        {
            if (operation == "codec") return !session.RequestMediaCanDecode("video/mp4", true, TestContext.Current.CancellationToken);
            if (operation == "decode") return session.RequestMediaDecode(new byte[] { 1 }, "audio/test", false, 1024, TestContext.Current.CancellationToken) is null;
            try
            {
                await session.RequestMediaCaptureAsync(new { action = "open", audio = true, video = false }, TestContext.Current.CancellationToken);
                return false;
            }
            catch (IOException) { return true; }
        }, TestContext.Current.CancellationToken);
        try { await output.Written.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken); }
        finally { await session.DisposeAsync(); }
        Assert.True(await request.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("codec")]
    [InlineData("decode")]
    [InlineData("capture")]
    public async Task DisposingWhileAWriteIsInFlightIsAClosedConnectionNotAFault(string operation)
    {
        var output = new NotifyingStream { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var session = new ContentHostSession(new MemoryStream(), output);
        var request = Task.Run(async () =>
        {
            if (operation == "codec") return !session.RequestMediaCanDecode("video/mp4", true, TestContext.Current.CancellationToken);
            if (operation == "decode") return session.RequestMediaDecode(new byte[] { 1 }, "audio/test", false, 1024, TestContext.Current.CancellationToken) is null;
            try { await session.RequestMediaCaptureAsync(new { action = "open", audio = true, video = false }, TestContext.Current.CancellationToken); return false; }
            catch (IOException) { return true; }
        }, TestContext.Current.CancellationToken);
        await output.Written.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await session.DisposeAsync(); // the write gate is gone while the write still holds it
        output.Hold.SetResult();
        Assert.True(await request.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    private sealed class NotifyingStream : MemoryStream
    {
        internal TaskCompletionSource Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource? Hold { get; init; }
        public override async Task FlushAsync(CancellationToken cancellationToken) { Written.TrySetResult(); if (Hold is not null) await Hold.Task.ConfigureAwait(false); }
    }

    private sealed class Decoder : IMediaDecoder
    {
        internal int Queries, Decodes;
        public bool CanDecode(string? contentType, bool video) { Interlocked.Increment(ref Queries); return video && contentType == "video/mp4"; }
        public MediaDecodeResult? Decode(ReadOnlyMemory<byte> data, string? contentType, bool video, int maxOutputBytes)
        { Interlocked.Increment(ref Decodes); return null; }
    }
}
