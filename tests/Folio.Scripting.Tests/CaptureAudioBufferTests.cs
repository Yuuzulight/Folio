using Folio.Scripting.Dom;

namespace Folio.Scripting.Tests;

public sealed class CaptureAudioBufferTests
{
    [Fact]
    public void ReadersHaveIndependentPartialCursorsAndTimestamps()
    {
        using var buffer = new CaptureAudioBuffer(new(4096, 0), () => 0);
        var first = buffer.CreateCursor();
        var second = buffer.CreateCursor();
        buffer.Push(new(1000, 2, Enumerable.Range(0, 40).Select(i => (float)i).ToArray()));
        Assert.True(buffer.Read(first, .01, out var left));
        Assert.Equal(Enumerable.Range(0, 20).Select(i => (float)i), left.Samples.ToArray());
        Assert.Equal(0, first.Timestamp);
        Assert.True(buffer.Read(first, .01, out var right));
        Assert.Equal(Enumerable.Range(20, 20).Select(i => (float)i), right.Samples.ToArray());
        Assert.Equal(.01, first.Timestamp, 6);
        Assert.False(buffer.Read(first, .01, out _));
        Assert.True(buffer.Read(second, .02, out var independent));
        Assert.Equal(40, independent.Samples.Length);
        Assert.Equal(0, second.Timestamp);
    }

    [Fact]
    public void SkippingOneConsumerDoesNotDropAnotherConsumersHistory()
    {
        using var buffer = new CaptureAudioBuffer(new(4096, 0), () => 0);
        var muted = buffer.CreateCursor();
        var audible = buffer.CreateCursor();
        buffer.Push(new(1000, 1, new float[] { 1, 2, 3 }));
        buffer.Skip(muted);
        Assert.False(buffer.Read(muted, .02, out _));
        Assert.True(buffer.Read(audible, .02, out var retained));
        Assert.Equal(new float[] { 1, 2, 3 }, retained.Samples.ToArray());
    }

    [Fact]
    public void AuthoritativeSequenceRejectsRehydratedDuplicatesAndOutOfOrderBlocksAfterClear()
    {
        using var buffer = new CaptureAudioBuffer(new(4096, 0), () => 0);
        var cursor = buffer.CreateCursor();
        buffer.Push(new(1000, 1, new float[] { 1 }) { CaptureSequence = 10 });
        buffer.Push(new(1000, 1, new float[] { 1 }) { CaptureSequence = 10 });
        buffer.Push(new(1000, 1, new float[] { 9 }) { CaptureSequence = 9 });
        Assert.True(buffer.Read(cursor, .02, out _));
        Assert.False(buffer.Read(cursor, .02, out _));
        buffer.Clear();
        buffer.Push(new(1000, 1, new float[] { 1 }) { CaptureSequence = 10 });
        Assert.False(buffer.Read(cursor, .02, out _));
        buffer.Push(new(1000, 1, new float[] { 2 }) { CaptureSequence = 11 });
        Assert.True(buffer.Read(cursor, .02, out var fresh));
        Assert.Equal(2f, fresh.Samples.Span[0]);
    }

    [Fact]
    public void SequenceWatermarkSurvivesBudgetDropAndNewRequestsStartFresh()
    {
        var budget = new MediaResourceBudget(128, 0);
        using var occupied = budget.AcquireBytes()!;
        Assert.True(occupied.Resize(128));
        using var first = new CaptureAudioBuffer(budget, () => 0);
        var cursor = first.CreateCursor();
        first.Push(new(1000, 1, new float[] { 1 }) { CaptureSequence = 100 });
        occupied.Dispose();
        first.Push(new(1000, 1, new float[] { 1 }) { CaptureSequence = 100 });
        Assert.False(first.Read(cursor, .02, out _));
        using var nextRequest = new CaptureAudioBuffer(budget, () => 0);
        var next = nextRequest.CreateCursor();
        nextRequest.Push(new(1000, 1, new float[] { 2 }) { CaptureSequence = 1 });
        Assert.True(nextRequest.Read(next, .02, out _));
    }

    [Fact]
    public void SourceHistoryBoundSkipsEvictedPartialBlockWithoutRepeatingItsTail()
    {
        using var buffer = new CaptureAudioBuffer(new(4096, 0), () => 0);
        var slow = buffer.CreateCursor();
        buffer.Push(new(1000, 1, Enumerable.Repeat(1f, 100).ToArray()));
        Assert.True(buffer.Read(slow, .01, out _));
        buffer.Push(new(1000, 1, Enumerable.Repeat(2f, 100).ToArray()));
        buffer.Push(new(1000, 1, Enumerable.Repeat(3f, 100).ToArray()));
        Assert.True(buffer.Read(slow, .1, out var surviving));
        Assert.Equal(100, surviving.Samples.Length);
        Assert.All(surviving.Samples.ToArray(), sample => Assert.Equal(2f, sample));
        Assert.Equal(.1, slow.Timestamp, 6);
    }

    [Fact]
    public void WallTimeExpirationAndCloseReleaseSharedBudgetAndPreventLateCallbacks()
    {
        double now = 0;
        var budget = new MediaResourceBudget(104, 0);
        using var first = new CaptureAudioBuffer(budget, () => now);
        using var second = new CaptureAudioBuffer(budget, () => now);
        var a = first.CreateCursor();
        var b = second.CreateCursor();
        first.Push(new(1000, 1, new float[10])); // 40 PCM bytes + 64 metadata bytes
        second.Push(new(1000, 1, new float[10]));
        Assert.False(second.Read(b, .02, out _));
        now = .3;
        Assert.False(first.Read(a, .02, out _));
        second.Push(new(1000, 1, new float[10]));
        Assert.True(second.Read(b, .02, out _));
        second.Dispose();
        second.Push(new(1000, 1, new float[10]));
        Assert.False(second.Read(b, .02, out _));
        using var available = budget.AcquireBytes()!;
        Assert.True(available.Resize(104));
    }

    [Fact]
    public void CallbackFloodCoalescesToThirtyTwoBlocksAndLegacyIdentityDoesNotReplay()
    {
        using var buffer = new CaptureAudioBuffer(new(4096, 0), () => 0);
        var reader = buffer.CreateCursor();
        var snapshot = new MediaAudioFrame(48000, 1, new float[] { 7 });
        buffer.Seed(snapshot);
        buffer.Push(snapshot);
        Assert.True(buffer.Read(reader, .02, out _));
        Assert.False(buffer.Read(reader, .02, out _));
        for (var i = 0; i < 100; i++) buffer.Push(new(48000, 1, new float[] { i }));
        var read = new List<float>();
        while (buffer.Read(reader, .02, out var frame)) read.Add(frame.Samples.Span[0]);
        Assert.Equal(Enumerable.Range(68, 32).Select(i => (float)i), read);
    }

    [Fact]
    public void LegacyIdentitySurvivesClearWithoutKeepingAnUnbudgetedStrongReference()
    {
        using var buffer = new CaptureAudioBuffer(new(4096, 0), () => 0);
        var cursor = buffer.CreateCursor();
        var snapshot = new MediaAudioFrame(1000, 1, new float[] { 1 });
        buffer.Push(snapshot);
        Assert.True(buffer.Read(cursor, .02, out _));
        buffer.Clear();
        buffer.Push(snapshot);
        Assert.False(buffer.Read(cursor, .02, out _));
    }

    [Fact]
    public void SuppressedBlocksAdvanceSequenceWatermarkWithoutRetainingAudio()
    {
        using var buffer = new CaptureAudioBuffer(new(4096, 0), () => 0);
        var cursor = buffer.CreateCursor();
        buffer.Push(new(1000, 1, new float[] { 2 }) { CaptureSequence = 2 }, retain: false);
        buffer.Push(new(1000, 1, new float[] { 2 }) { CaptureSequence = 2 });
        Assert.False(buffer.Read(cursor, .02, out _));
        buffer.Push(new(1000, 1, new float[] { 3 }) { CaptureSequence = 3 });
        Assert.True(buffer.Read(cursor, .02, out var fresh));
        Assert.Equal(3f, fresh.Samples.Span[0]);
        Assert.Equal(3, fresh.CaptureSequence);
    }

    [Theory]
    [InlineData(0, 1, 10)]
    [InlineData(48000, 0, 10)]
    [InlineData(48000, 2, 3)]
    [InlineData(int.MaxValue, int.MaxValue, 2)]
    public void MalformedFormatsAreRejected(int rate, int channels, int samples)
    {
        using var buffer = new CaptureAudioBuffer(new(4096, 0), () => 0);
        var cursor = buffer.CreateCursor();
        buffer.Push(new(rate, channels, new float[samples]));
        Assert.False(buffer.Read(cursor, .02, out _));
    }

    [Fact]
    public void NonFiniteAndOversizeBlocksAreRejected()
    {
        using var buffer = new CaptureAudioBuffer(new(1024 * 1024, 0), () => 0);
        var cursor = buffer.CreateCursor();
        buffer.Push(new(48000, 1, new float[] { float.NaN }));
        buffer.Push(new(48000, 1, new float[] { float.PositiveInfinity }));
        buffer.Push(new(48000, 1, new float[100000]));
        buffer.Push(new(1000, 1, new float[300]));
        Assert.False(buffer.Read(cursor, .02, out _));
    }
}
