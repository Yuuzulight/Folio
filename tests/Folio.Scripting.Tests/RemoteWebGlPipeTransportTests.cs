using System.Buffers.Binary;
using Folio.Scripting.Gpu.Remote;

namespace Folio.Scripting.Tests;

public sealed class RemoteWebGlPipeTransportTests
{
    [Fact]
    public void OversizedReplyIsTerminalAndKeepsTheOriginalProtocolException()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, GpuWebGlWire.MaxReplyBytes + 1);
        using var transport = new GpuWebGlPipeTransport(new MemoryStream(), new MemoryStream(header), TimeSpan.FromSeconds(2));
        Assert.Throws<InvalidDataException>(() => transport.Exchange([1]));
        Assert.Throws<IOException>(() => transport.Exchange([2]));
        Assert.True(SpinWait.SpinUntil(() => !transport.IsPumpAlive, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void BrokenPipeCleanupDoesNotEscapeTheOwnedThread()
    {
        using var frame = new MemoryStream();
        GpuChannelFraming.Write(frame, [1, 2, 3]);
        using var transport = new GpuWebGlPipeTransport(new BrokenDisposeStream([]), new BrokenDisposeStream(frame.ToArray()), TimeSpan.FromSeconds(2));
        Assert.Equal(new byte[] { 1, 2, 3 }, transport.Exchange([4]));
        transport.Dispose(); transport.Dispose();
        Assert.True(SpinWait.SpinUntil(() => !transport.IsPumpAlive, TimeSpan.FromSeconds(2)));
    }

    private sealed class BrokenDisposeStream(byte[] initial) : MemoryStream(initial.Length == 0 ? new byte[64] : initial)
    {
        protected override void Dispose(bool disposing)
        { base.Dispose(disposing); throw new IOException("Injected broken-pipe cleanup."); }
    }
}
