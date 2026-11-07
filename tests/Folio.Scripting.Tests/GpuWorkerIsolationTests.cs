using Folio.Scripting.Gpu;

namespace Folio.Scripting.Tests;

// These tests never open a device, compile a shader, or launch a process.
public sealed class GpuWorkerIsolationTests
{
    [Fact]
    public void OversizedReplyLosesOnlyTheOwnedWorker()
    {
        var response = new byte[GpuWorkerProtocol.HeaderBytes];
        GpuWorkerProtocol.Put(response, 0, GpuWorkerProtocol.Magic);
        GpuWorkerProtocol.Put(response, 1, (int)GpuWorkerCommand.Open | 0x40000000);
        GpuWorkerProtocol.Put(response, 2, 1);
        GpuWorkerProtocol.Put(response, 4, GpuWorkerProtocol.MaxPayloadBytes + 1);
        int killedWorker = 0;
        using var broker = new GpuWorkerBroker(new MemoryStream(), new MemoryStream(response), () => killedWorker++, TimeSpan.FromSeconds(1));
        Assert.Throws<InvalidDataException>(() => broker.Exchange(GpuWorkerProtocol.Request(GpuWorkerCommand.Open), TestContext.Current.CancellationToken));
        Assert.True(broker.IsLost);
        Assert.Equal(1, killedWorker);
        Assert.Throws<IOException>(() => broker.Exchange(GpuWorkerProtocol.Request(GpuWorkerCommand.Open), TestContext.Current.CancellationToken));
        Assert.Equal(1, killedWorker);
    }

    [Fact]
    public async Task CancellationOfInjectedStallKillsWorkerAndDoesNotWaitForItsReply()
    {
        var testCancellation = TestContext.Current.CancellationToken;
        using var stalled = new StalledReadStream(testCancellation);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(testCancellation);
        int killed = 0;
        using var broker = new GpuWorkerBroker(new MemoryStream(), stalled,
            () => { Interlocked.Increment(ref killed); stalled.Release.Set(); }, TimeSpan.FromSeconds(5));
        var call = Task.Run(() => broker.Exchange(GpuWorkerProtocol.Request(GpuWorkerCommand.Open), cancellation.Token), testCancellation);
        Assert.True(stalled.Entered.Wait(TimeSpan.FromSeconds(2), testCancellation));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await call.WaitAsync(TimeSpan.FromSeconds(2), testCancellation); });
        Assert.True(broker.IsLost);
        Assert.Equal(1, killed);
    }

    [Fact]
    public async Task DeadlineOfInjectedStallIsTerminal()
    {
        var testCancellation = TestContext.Current.CancellationToken;
        using var stalled = new StalledReadStream(testCancellation);
        int killed = 0;
        using var broker = new GpuWorkerBroker(new MemoryStream(), stalled,
            () => { Interlocked.Increment(ref killed); stalled.Release.Set(); }, TimeSpan.FromMilliseconds(50));
        var call = Task.Run(() => broker.Exchange(GpuWorkerProtocol.Request(GpuWorkerCommand.Open), testCancellation), testCancellation);
        await Assert.ThrowsAsync<TimeoutException>(async () => { await call.WaitAsync(TimeSpan.FromSeconds(5), testCancellation); });
        Assert.True(broker.IsLost);
        Assert.Equal(1, killed);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(64 * 1024 * 1024 + 1)]
    [InlineData(int.MaxValue)]
    public void WorkerRejectsInvalidBudgetBeforeInvokingDeviceFactory(int budget)
    {
        var request = GpuWorkerProtocol.Request(GpuWorkerCommand.Open);
        GpuWorkerProtocol.Put(request, 2, 1);
        GpuWorkerProtocol.Put(request, 4, budget);
        int invoked = 0;
        using var output = new MemoryStream();
        GpuWorkerServer.Run(new MemoryStream(request), output, _ => { invoked++; throw new InvalidOperationException(); });
        Assert.Equal(0, invoked);
        var reply = output.ToArray();
        Assert.Equal(GpuWorkerProtocol.HeaderBytes, reply.Length);
        Assert.Equal(GpuWorkerProtocol.Magic, GpuWorkerProtocol.Get(reply, 0));
        Assert.Equal((int)GpuWorkerCommand.Open | 0x40000000, GpuWorkerProtocol.Get(reply, 1));
        Assert.Equal(1, GpuWorkerProtocol.Get(reply, 2));
        Assert.Equal(1, GpuWorkerProtocol.Get(reply, 3));
        Assert.Equal(0, GpuWorkerProtocol.Get(reply, 4));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2049, 1)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void SurfaceLimitsRejectBeforeMultiplication(int width, int height)
        => Assert.Throws<InvalidDataException>(() => GpuWorkerProtocol.SurfaceBytes(width, height));

    private sealed class StalledReadStream(CancellationToken cancellationToken) : Stream
    {
        internal readonly ManualResetEventSlim Entered = new(), Release = new();
        public override int Read(byte[] buffer, int offset, int count)
        { Entered.Set(); Release.Wait(cancellationToken); throw new EndOfStreamException("Injected worker exit."); }
        public override int Read(Span<byte> buffer)
        { Entered.Set(); Release.Wait(cancellationToken); throw new EndOfStreamException("Injected worker exit."); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        // Events are retained until the injected blocked IO has unwound, even if the test fails.
        protected override void Dispose(bool disposing) { Release.Set(); base.Dispose(disposing); }
    }
}
