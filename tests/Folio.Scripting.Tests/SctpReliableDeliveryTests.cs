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
        var first = Assert.Single(sender.Enqueue(1, 0, 51, [1], 0));
        var second = Assert.Single(sender.Enqueue(2, 0, 51, [2], 0));
        var independent = Assert.Single(sender.Enqueue(3, 2, 51, [3], 0));
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
        var data = new byte[SctpDataChannel.MaxFragmentPayloadBytes];
        var count = (uint)(SctpReliableDelivery.MaxSendBytes / (16 + data.Length));
        for (uint tsn = 1; tsn <= count; tsn++) sender.Enqueue(tsn, 0, 53, data, 0);
        var sack = new byte[12]; BinaryPrimitives.WriteUInt32BigEndian(sack, count + 1);
        Assert.False(sender.Acknowledge(sack));
        Assert.Throws<InvalidOperationException>(() => sender.Enqueue(count + 1, 0, 53, data, 0));
        BinaryPrimitives.WriteUInt32BigEndian(sack, count);
        Assert.True(sender.Acknowledge(sack));
        var admitted = Assert.Single(sender.Enqueue(count + 1, 0, 53, [9], 0));
        Assert.Equal(count, BinaryPrimitives.ReadUInt16BigEndian(admitted.AsSpan(10)));
        Assert.Equal(1, sender.BufferedAmount(0));
    }

    [Fact]
    public void LostAckRetransmitsButDoesNotRedeliverAndRetainedPayloadIsOwned()
    {
        var sender = new SctpReliableDelivery(); var receiver = new SctpReliableDelivery();
        receiver.SetPeerInitialTsn(1);
        byte[] source = [1, 2, 3];
        var data = Assert.Single(sender.Enqueue(1, 0, 53, source, 0)); source[0] = 99;
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
        var previous = Assert.Single(sender.Enqueue(uint.MaxValue, 0, 51, [1], 0));
        var wrapped = Assert.Single(sender.Enqueue(0, 0, 51, [2], 0));
        Assert.Empty(receiver.Receive(3, wrapped.AsSpan(4)));
        Assert.Equal(2, receiver.Receive(3, previous.AsSpan(4)).Count);
        Assert.True(sender.Acknowledge(receiver.CreateSack().AsSpan(4)));
        for (uint tsn = 1; tsn <= ushort.MaxValue; tsn++)
        {
            var chunk = Assert.Single(sender.Enqueue(tsn, 0, 56, [], 0));
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
        var chunk = Assert.Single(sender.Enqueue(1, 0, ppid, [], 0));
        Assert.Equal(17, chunk.Length); Assert.Equal(0, chunk[16]);
        Assert.Equal(0, sender.BufferedAmount(0));
    }

    [Fact]
    public void GapAckRenegingResumesRetransmissionAndGapStateHasAnAbsoluteDeadline()
    {
        var sender = new SctpReliableDelivery();
        sender.Enqueue(1, 0, 51, [1], 0);
        var later = Assert.Single(sender.Enqueue(2, 0, 51, [2], 0));
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
    public void IncompleteFragmentsAreRetainedWithoutDeliveryAndExpire()
    {
        var sender = new SctpReliableDelivery(); var receiver = new SctpReliableDelivery();
        receiver.SetPeerInitialTsn(1);
        var chunk = Assert.Single(sender.Enqueue(1, 0, 51, [1], 0));
        Assert.Empty(receiver.Receive(2, chunk.AsSpan(4)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(receiver.CreateSack().AsSpan(4)));
        var clock = new Clock { Ticks = TimeSpan.FromSeconds(60).Ticks };
        Assert.Empty(receiver.DueRetransmissions(clock, out var failed)); Assert.True(failed);
    }

    [Fact]
    public void FullReceiveStorageRefusesMoreFragmentsWithoutAcknowledgingThem()
    {
        var sender = new SctpReliableDelivery(); var receiver = new SctpReliableDelivery();
        receiver.SetPeerInitialTsn(1);
        sender.Enqueue(1, 0, 51, [1], 0); // Keep SSN zero and TSN one missing.
        for (var message = 0; message < 5; message++)
        {
            foreach (var chunk in sender.Enqueue(sender.NextTsn, 0, 53, new byte[SctpDataChannel.MaxMessageBytes], 0))
                Assert.Empty(receiver.Receive(chunk[1], chunk.AsSpan(4)));
            // A faulty peer can continue sending despite receiver credit. Its
            // local queue is released here; the receiving limit must still hold.
            var ack = new byte[12]; BinaryPrimitives.WriteUInt32BigEndian(ack, sender.NextTsn - 1);
            Assert.True(sender.Acknowledge(ack));
        }
        var sack = receiver.CreateSack();
        Assert.InRange(BinaryPrimitives.ReadUInt32BigEndian(sack.AsSpan(8)), 0u, (uint)(SctpDataChannel.MaxFragmentPayloadBytes + 11));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(sack.AsSpan(4)));
        Assert.True(BinaryPrimitives.ReadUInt16BigEndian(sack.AsSpan(18)) < sender.NextTsn - 1);
    }
}
