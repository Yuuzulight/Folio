using System.Buffers.Binary;
using Folio.Scripting.Rtc;

namespace Folio.Scripting.Tests;

public sealed class SctpReliableDeliveryTests
{
    private sealed class Clock : TimeProvider
    {
        internal long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
    }

    [Fact]
    public void ReorderedMessagesWaitPerStreamButDoNotBlockOtherStreams()
    {
        var sender = new SctpReliableDelivery(); var receiver = new SctpReliableDelivery();
        receiver.SetPeerInitialTsn(1);
        var first = sender.Enqueue(1, 0, 51, [1], 0);
        var second = sender.Enqueue(2, 0, 51, [2], 0);
        var independent = sender.Enqueue(3, 2, 51, [3], 0);
        Assert.Empty(receiver.Receive(3, second.AsSpan(4)));
        Assert.Equal(new byte[] { 3 }, Assert.Single(receiver.Receive(3, independent.AsSpan(4))).Data);
        var sack = receiver.CreateSack();
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(sack.AsSpan(4)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(sack.AsSpan(12)));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16BigEndian(sack.AsSpan(16)));
        Assert.Equal(3, BinaryPrimitives.ReadUInt16BigEndian(sack.AsSpan(18)));
        Assert.True(sender.Acknowledge(sack.AsSpan(4)));
        var clock = new Clock { Ticks = TimeSpan.FromSeconds(1).Ticks };
        var retransmit = sender.DueRetransmissions(clock, out var failed);
        Assert.False(failed); Assert.Equal(first, Assert.Single(retransmit));
        var ready = receiver.Receive(3, first.AsSpan(4));
        Assert.Equal(2, ready.Count);
        Assert.Equal(new byte[] { 1 }, ready[0].Data); Assert.Equal(new byte[] { 2 }, ready[1].Data);
        Assert.Empty(receiver.Receive(3, first.AsSpan(4)));
        Assert.Empty(receiver.Receive(3, second.AsSpan(4)));
        Assert.True(sender.Acknowledge(receiver.CreateSack().AsSpan(4)));
        Assert.Equal(0, sender.BufferedAmount(0)); Assert.Equal(0, sender.BufferedAmount(2));
    }

    [Fact]
    public void RejectsFutureOrMalformedAcksWithoutReleasingSendAdmission()
    {
        var sender = new SctpReliableDelivery();
        var data = new byte[SctpDataChannel.MaxUnfragmentedMessageBytes];
        for (uint tsn = 1; tsn <= 64; tsn++) sender.Enqueue(tsn, 0, 53, data, 0);
        var sack = new byte[12]; BinaryPrimitives.WriteUInt32BigEndian(sack, 65);
        Assert.False(sender.Acknowledge(sack));
        Assert.Throws<InvalidOperationException>(() => sender.Enqueue(65, 0, 53, data, 0));
        BinaryPrimitives.WriteUInt32BigEndian(sack, 64);
        Assert.True(sender.Acknowledge(sack));
        var admitted = sender.Enqueue(65, 0, 53, [9], 0);
        Assert.Equal(64, BinaryPrimitives.ReadUInt16BigEndian(admitted.AsSpan(10)));
        Assert.Equal(1, sender.BufferedAmount(0));
    }

    [Fact]
    public void LostAckRetransmitsButDoesNotRedeliverAndRetainedPayloadIsOwned()
    {
        var sender = new SctpReliableDelivery(); var receiver = new SctpReliableDelivery();
        receiver.SetPeerInitialTsn(1);
        byte[] source = [1, 2, 3];
        var data = sender.Enqueue(1, 0, 53, source, 0); source[0] = 99;
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(receiver.Receive(3, data.AsSpan(4))).Data);
        var clock = new Clock { Ticks = TimeSpan.FromSeconds(1).Ticks };
        var retry = Assert.Single(sender.DueRetransmissions(clock, out var failed));
        Assert.False(failed); Assert.Empty(receiver.Receive(3, retry.AsSpan(4)));
        Assert.True(sender.Acknowledge(receiver.CreateSack().AsSpan(4)));
        clock.Ticks += TimeSpan.FromSeconds(2).Ticks;
        Assert.Empty(sender.DueRetransmissions(clock, out failed)); Assert.False(failed);
    }

    [Fact]
    public void TsnWrapAndStreamSequenceWrapRemainOrdered()
    {
        var sender = new SctpReliableDelivery(uint.MaxValue); var receiver = new SctpReliableDelivery();
        receiver.SetPeerInitialTsn(uint.MaxValue);
        var previous = sender.Enqueue(uint.MaxValue, 0, 51, [1], 0);
        var wrapped = sender.Enqueue(0, 0, 51, [2], 0);
        Assert.Empty(receiver.Receive(3, wrapped.AsSpan(4)));
        Assert.Equal(2, receiver.Receive(3, previous.AsSpan(4)).Count);
        Assert.True(sender.Acknowledge(receiver.CreateSack().AsSpan(4)));
        for (uint tsn = 1; tsn <= ushort.MaxValue; tsn++)
        {
            var chunk = sender.Enqueue(tsn, 0, 56, [], 0);
            Assert.Single(receiver.Receive(3, chunk.AsSpan(4)));
            Assert.True(sender.Acknowledge(receiver.CreateSack().AsSpan(4)));
        }
    }

    [Theory]
    [InlineData(56u)]
    [InlineData(57u)]
    public void EmptyMessagesCarryTheRequiredPaddingByteWithoutApplicationBufferCharge(uint ppid)
    {
        var sender = new SctpReliableDelivery();
        var chunk = sender.Enqueue(1, 0, ppid, [], 0);
        Assert.Equal(17, chunk.Length); Assert.Equal(0, chunk[16]);
        Assert.Equal(0, sender.BufferedAmount(0));
    }

    [Fact]
    public void GapAckRenegingResumesRetransmissionAndGapStateHasAnAbsoluteDeadline()
    {
        var sender = new SctpReliableDelivery();
        sender.Enqueue(1, 0, 51, [1], 0);
        var later = sender.Enqueue(2, 0, 51, [2], 0);
        var sack = new byte[16];
        BinaryPrimitives.WriteUInt16BigEndian(sack.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(sack.AsSpan(12), 2);
        BinaryPrimitives.WriteUInt16BigEndian(sack.AsSpan(14), 2);
        Assert.True(sender.Acknowledge(sack));
        var clock = new Clock { Ticks = TimeSpan.FromSeconds(1).Ticks };
        Assert.Single(sender.DueRetransmissions(clock, out _));
        Assert.True(sender.Acknowledge(new byte[12]));
        Assert.Equal(later, Assert.Single(sender.DueRetransmissions(clock, out _)));
        clock.Ticks = TimeSpan.FromSeconds(60).Ticks;
        Assert.Empty(sender.DueRetransmissions(clock, out var failed)); Assert.True(failed);
    }

    [Fact]
    public void IncompleteFragmentsAndFullReceiveBuffersAreNotAcknowledged()
    {
        var sender = new SctpReliableDelivery(); var receiver = new SctpReliableDelivery();
        receiver.SetPeerInitialTsn(1);
        var chunk = sender.Enqueue(1, 0, 51, [1], 0);
        Assert.Empty(receiver.Receive(2, chunk.AsSpan(4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(receiver.CreateSack().AsSpan(4)));
        // Keep SSN zero missing so subsequent complete messages consume receive storage.
        for (uint tsn = 2; tsn <= 10; tsn++)
        {
            chunk = sender.Enqueue(tsn, 0, 53, new byte[SctpDataChannel.MaxUnfragmentedMessageBytes], 0);
            Assert.Empty(receiver.Receive(3, chunk.AsSpan(4)));
        }
        var sack = receiver.CreateSack();
        Assert.True(BinaryPrimitives.ReadUInt32BigEndian(sack.AsSpan(8)) < 16368);
        Assert.Equal(9, BinaryPrimitives.ReadUInt16BigEndian(sack.AsSpan(18)));
        Assert.Single(receiver.Receive(3, sender.Enqueue(11, 2, 51, [7], 0).AsSpan(4)));
    }
}
