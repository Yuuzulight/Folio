using System.Buffers.Binary;
using Folio.Scripting.Gpu.Remote;

namespace Folio.Scripting.Tests;

public sealed class GpuCommandBatchTests
{
    private const long Capacity = 1024;

    [Fact]
    public void SealedBatchRoundTripsCommandsInOrder()
    {
        var writer = new GpuCommandBatchWriter();
        Assert.True(writer.TryAppend(7, [1, 2, 3]));
        Assert.True(writer.TryAppend(9, []));
        Assert.True(writer.TryAppend(11, new byte[GpuCommandBatch.MaxInlineBytes]));
        var batch = writer.Seal(5, 32, 96, sync: true);

        Assert.True(GpuCommandBatch.TryValidate(batch, Capacity, out var header));
        Assert.Equal(new GpuCommandBatch.Header(5, 3, batch.Length, 32, 96, true), header);
        var reader = new GpuCommandBatch.Reader(batch);
        Assert.True(reader.MoveNext()); Assert.Equal(7, reader.Current.Opcode); Assert.Equal([1, 2, 3], reader.Current.Payload.ToArray());
        Assert.True(reader.MoveNext()); Assert.Equal(9, reader.Current.Opcode); Assert.Equal(0, reader.Current.Payload.Length);
        Assert.True(reader.MoveNext()); Assert.Equal(GpuCommandBatch.MaxInlineBytes, reader.Current.Payload.Length);
        Assert.False(reader.MoveNext());
        Assert.True(writer.IsEmpty); // sealing resets the writer
    }

    [Fact]
    public void WriterReportsOverflowInsteadOfTruncating()
    {
        var writer = new GpuCommandBatchWriter();
        for (var i = 0; i < GpuCommandBatch.MaxCommands; i++) Assert.True(writer.TryAppend(1, []));
        Assert.False(writer.TryAppend(1, []));
        Assert.Throws<ArgumentException>(() => new GpuCommandBatchWriter().TryAppend(1, new byte[GpuCommandBatch.MaxInlineBytes + 1]));

        var big = new GpuCommandBatchWriter();
        var appended = 0;
        while (big.TryAppend(2, new byte[GpuCommandBatch.MaxInlineBytes])) appended++;
        Assert.True(GpuCommandBatch.TryValidate(big.Seal(1, 0, 0, false), Capacity, out var header));
        Assert.Equal(appended, header.CommandCount);
    }

    [Theory]
    [InlineData(0, 0x31425048)]   // magic
    [InlineData(4, 0)]            // sequence must be positive
    [InlineData(8, 2)]            // command count disagrees with the body
    [InlineData(8, GpuCommandBatch.MaxCommands + 1)]
    [InlineData(12, 4)]           // declared length disagrees with the buffer
    [InlineData(32, 2)]           // unknown flag
    [InlineData(36, 1)]           // reserved word
    public void HeaderFieldsAreValidated(int offset, int value)
    {
        var batch = OneCommandBatch();
        BinaryPrimitives.WriteInt32LittleEndian(batch.AsSpan(offset), value);
        Assert.False(GpuCommandBatch.TryValidate(batch, Capacity, out _));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(64, 32)]                 // end before start
    [InlineData(0, Capacity + 16)]       // window wider than the arena
    public void UploadWindowIsValidated(long start, long end)
    {
        var writer = new GpuCommandBatchWriter();
        writer.TryAppend(1, []);
        Assert.False(GpuCommandBatch.TryValidate(writer.Seal(1, start, end, false), Capacity, out _));
    }

    [Fact]
    public void CommandHeadersAreValidated()
    {
        var zeroOpcode = OneCommandBatch();
        BinaryPrimitives.WriteUInt16LittleEndian(zeroOpcode.AsSpan(GpuCommandBatch.HeaderBytes), 0);
        Assert.False(GpuCommandBatch.TryValidate(zeroOpcode, Capacity, out _));

        var reserved = OneCommandBatch();
        BinaryPrimitives.WriteUInt16LittleEndian(reserved.AsSpan(GpuCommandBatch.HeaderBytes + 2), 1);
        Assert.False(GpuCommandBatch.TryValidate(reserved, Capacity, out _));

        var overlong = OneCommandBatch();
        BinaryPrimitives.WriteUInt32LittleEndian(overlong.AsSpan(GpuCommandBatch.HeaderBytes + 4), 64);
        Assert.False(GpuCommandBatch.TryValidate(overlong, Capacity, out _));

        var oversized = OneCommandBatch();
        BinaryPrimitives.WriteUInt32LittleEndian(oversized.AsSpan(GpuCommandBatch.HeaderBytes + 4), GpuCommandBatch.MaxInlineBytes + 1);
        Assert.False(GpuCommandBatch.TryValidate(oversized, Capacity, out _));

        Assert.False(GpuCommandBatch.TryValidate(OneCommandBatch()[..^4], Capacity, out _));
        Assert.False(GpuCommandBatch.TryValidate(new byte[GpuCommandBatch.HeaderBytes - 1], Capacity, out _));
        Assert.False(GpuCommandBatch.TryValidate(new byte[GpuCommandBatch.MaxBatchBytes + 1], Capacity, out _));
    }

    [Fact]
    public void MutatedBatchesNeverThrowOrReadOutOfBounds()
    {
        var random = new Random(1701);
        var writer = new GpuCommandBatchWriter();
        for (var i = 0; i < 40; i++) writer.TryAppend((ushort)(i + 1), new byte[random.Next(0, 300)]);
        var valid = writer.Seal(3, 0, 512, false);
        var accepted = 0;
        for (var round = 0; round < 20_000; round++)
        {
            var mutated = (byte[])valid.Clone();
            for (var flips = random.Next(1, 5); flips > 0; flips--) mutated[random.Next(mutated.Length)] = (byte)random.Next(256);
            if (random.Next(8) == 0) mutated = mutated[..random.Next(mutated.Length)];
            if (!GpuCommandBatch.TryValidate(mutated, Capacity, out var header)) continue;
            accepted++;
            var reader = new GpuCommandBatch.Reader(mutated);
            var seen = 0;
            while (reader.MoveNext())
            {
                seen++;
                Assert.InRange(reader.Current.Payload.Length, 0, GpuCommandBatch.MaxInlineBytes);
            }
            Assert.Equal(header.CommandCount, seen);
        }
        Assert.True(accepted > 0, "Payload-only mutations should still validate.");
    }

    [Fact]
    public void UploadReferencesMustStayInsideTheWindowAndNotWrap()
    {
        var header = new GpuCommandBatch.Header(1, 0, 0, 1000, 1200, false);
        Assert.True(GpuCommandBatch.TryResolveUpload(header, Capacity, 1000, 24, out var physical));
        Assert.Equal(1000, physical);
        Assert.True(GpuCommandBatch.TryResolveUpload(header, Capacity, 1024, 176, out physical));
        Assert.Equal(0, physical);
        Assert.False(GpuCommandBatch.TryResolveUpload(header, Capacity, 999, 8, out _));   // before the window
        Assert.False(GpuCommandBatch.TryResolveUpload(header, Capacity, 1190, 16, out _)); // past its end
        Assert.False(GpuCommandBatch.TryResolveUpload(header, Capacity, 1016, 16, out _)); // wraps the arena
        Assert.False(GpuCommandBatch.TryResolveUpload(header, Capacity, 1000, -1, out _));
        Assert.False(GpuCommandBatch.TryResolveUpload(header, Capacity, long.MaxValue, 16, out _));
    }

    [Fact]
    public void RingSkipsToTheNextLapAndAppliesBackPressureUntilAcknowledged()
    {
        var storage = new ArrayGpuArena(64);
        var ring = new GpuUploadRing(storage);
        Assert.True(ring.TryWrite([1, 2, 3], out var first));
        Assert.Equal(0, first);
        Assert.True(ring.TryWrite(new byte[32], out var second));
        Assert.Equal(16, second);
        Assert.Equal((0L, 48L), ring.PublishBatch(1));

        // 32 bytes do not fit in the 16 left on this lap, and the lap start is still in flight.
        Assert.False(ring.TryWrite(new byte[32], out _));
        Assert.Equal(1, First(storage)); // a refused write leaves in-flight data untouched
        ring.Acknowledge(1);
        Assert.Equal(0, ring.InFlightBytes);
        Assert.True(ring.TryWrite(new byte[32], out var third));
        Assert.Equal(64, third); // skipped the 16-byte tail, physical offset 0
        var window = ring.PublishBatch(2);
        Assert.Equal((48L, 96L), window);
        var header = new GpuCommandBatch.Header(2, 0, 0, window.Start, window.End, false);
        Assert.True(GpuCommandBatch.TryResolveUpload(header, ring.Capacity, third, 32, out var physical));
        Assert.Equal(0, physical);
        Assert.Equal(0, First(storage)); // reused only after the acknowledgement
    }

    [Fact]
    public void AcknowledgingOutOfOrderOnlyReleasesThroughThatBatch()
    {
        var ring = new GpuUploadRing(new ArrayGpuArena(64));
        ring.TryWrite(new byte[16], out _); ring.PublishBatch(1);
        ring.TryWrite(new byte[16], out _); ring.PublishBatch(2);
        ring.TryWrite(new byte[16], out _); ring.PublishBatch(3);
        ring.Acknowledge(2);
        Assert.Equal(16, ring.InFlightBytes);
        ring.Acknowledge(1); // stale acknowledgement is harmless
        Assert.Equal(16, ring.InFlightBytes);
        ring.Acknowledge(3);
        Assert.Equal(0, ring.InFlightBytes);
        Assert.Throws<ArgumentException>(() => ring.TryWrite(new byte[65], out _));
        Assert.Throws<ArgumentException>(() => new GpuUploadRing(new ArrayGpuArena(24)));
    }

    private static byte First(GpuArena arena) { Span<byte> one = stackalloc byte[1]; arena.Read(0, one); return one[0]; }

    private static byte[] OneCommandBatch()
    {
        var writer = new GpuCommandBatchWriter();
        writer.TryAppend(1, [1, 2, 3, 4]);
        return writer.Seal(1, 0, 0, false);
    }
}
