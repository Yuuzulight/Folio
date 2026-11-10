using System.Buffers.Binary;
using Folio.Scripting.Rtc;

namespace Folio.Scripting.Tests;

public sealed class SctpSendWindowTests
{
    private sealed class Clock : TimeProvider
    {
        internal long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
        internal void Advance(double seconds) => Ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
    private static byte[] Sack(uint cumulative, uint window = SctpReliableDelivery.MaxReceiveBytes, ushort gapStart = 0, ushort gapEnd = 0)
    {
        var bytes = new byte[gapStart == 0 ? 12 : 16];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, cumulative); BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), window);
        if (gapStart != 0)
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(8), 1);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12), gapStart);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(14), gapEnd);
        }
        return bytes;
    }

    [Fact]
    public void LargeMessageStartsWithOneBoundedBurstAndCannotAckUnsentData()
    {
        var clock = new Clock(); var sender = new SctpReliableDelivery();
        var chunks = sender.Enqueue(1, 0, 53, new byte[SctpDataChannel.MaxMessageBytes], 0);
        var first = sender.TakeSendable(clock);
        Assert.Equal(4, first.Count); Assert.True(first.Count < chunks.Count);
        Assert.InRange(sender.FlightBytes, sender.CongestionWindow, sender.CongestionWindow + SctpReliableDelivery.MaximumDataChunkBytes - 1);
        Assert.Empty(sender.TakeSendable(clock));
        Assert.False(sender.Acknowledge(Sack(sender.NextTsn - 1), clock));
        Assert.Equal(SctpDataChannel.MaxMessageBytes, sender.BufferedAmount(0));
        var previousWindow = sender.CongestionWindow;
        Assert.True(sender.Acknowledge(Sack(4), clock));
        Assert.True(sender.CongestionWindow > previousWindow);
        Assert.InRange(sender.TakeSendable(clock).Count, 1, 4);
    }

    [Fact]
    public void ReceiverCreditLimitsNewDataAndWindowReopeningResumesIt()
    {
        var clock = new Clock(); var sender = new SctpReliableDelivery();
        sender.SetPeerWindow(SctpDataChannel.MaxFragmentPayloadBytes, clock);
        sender.Enqueue(1, 0, 53, new byte[5000], 0);
        Assert.Single(sender.TakeSendable(clock)); Assert.Empty(sender.TakeSendable(clock));
        Assert.True(sender.Acknowledge(Sack(1, 0), clock));
        Assert.Empty(sender.TakeSendable(clock));
        clock.Advance(0.99); Assert.Empty(sender.TakeSendable(clock));
        clock.Advance(0.01);
        var window = sender.CongestionWindow;
        Assert.Single(sender.TakeSendable(clock)); // One zero-window probe.
        Assert.Equal(window, sender.CongestionWindow);
        Assert.Empty(sender.TakeSendable(clock));
        Assert.True(sender.Acknowledge(Sack(2, 0), clock));
        clock.Advance(1); Assert.Empty(sender.TakeSendable(clock));
        clock.Advance(1); Assert.Single(sender.TakeSendable(clock));
        Assert.True(sender.Acknowledge(Sack(3), clock));
        Assert.NotEmpty(sender.TakeSendable(clock));
    }

    [Fact]
    public void ZeroWindowProbeResponsesDoNotConsumeCongestionRetryBudget()
    {
        var clock = new Clock(); var sender = new SctpReliableDelivery();
        sender.SetPeerWindow(0, clock); sender.Enqueue(1, 0, 51, [1], 0);
        clock.Advance(1); Assert.Single(sender.TakeSendable(clock));
        var window = sender.CongestionWindow;
        for (var i = 0; i < 4; i++)
        {
            Assert.True(sender.Acknowledge(Sack(0, 0), clock));
            clock.Advance(1 << i);
            Assert.Single(sender.DueRetransmissions(clock, out var failed)); Assert.False(failed);
            Assert.Equal(window, sender.CongestionWindow);
        }
    }

    [Fact]
    public void ResponsiveZeroWindowCanHoldBoundedDataPastTheOrdinaryStallDeadline()
    {
        var clock = new Clock(); var sender = new SctpReliableDelivery();
        sender.SetPeerWindow(0, clock); sender.Enqueue(1, 0, 51, [1], 0);
        clock.Advance(1); sender.TakeSendable(clock);
        for (var turn = 0; turn < 9; turn++)
        {
            clock.Advance(10); Assert.True(sender.Acknowledge(Sack(0, 0), clock));
            sender.DueRetransmissions(clock, out var failed); Assert.False(failed);
        }
        Assert.Equal(1, sender.BufferedAmount(0));
        Assert.True(sender.Acknowledge(Sack(1), clock));
        sender.Enqueue(sender.NextTsn, 0, 51, [2], clock.GetTimestamp());
        Assert.Single(sender.TakeSendable(clock));
        Assert.Empty(sender.DueRetransmissions(clock, out var failure)); Assert.False(failure);
    }

    [Fact]
    public void TimeoutRetransmitsOnePacketAndStopsNewDataUntilRecovery()
    {
        var clock = new Clock(); var sender = new SctpReliableDelivery();
        sender.Enqueue(1, 0, 53, new byte[20000], 0); Assert.Equal(4, sender.TakeSendable(clock).Count);
        clock.Advance(0.99); Assert.Empty(sender.DueRetransmissions(clock, out _));
        clock.Advance(0.01);
        Assert.Single(sender.DueRetransmissions(clock, out var failed)); Assert.False(failed);
        Assert.Equal(SctpReliableDelivery.MaximumDataChunkBytes, sender.CongestionWindow);
        Assert.Empty(sender.TakeSendable(clock));
        clock.Advance(1); Assert.Empty(sender.DueRetransmissions(clock, out _));
        clock.Advance(1); Assert.Single(sender.DueRetransmissions(clock, out _));
        Assert.True(sender.Acknowledge(Sack(4), clock));
        Assert.NotEmpty(sender.TakeSendable(clock));
    }

    [Fact]
    public void ThreeNewGapReportsTriggerFastRecoveryWithoutWaitingForTimer()
    {
        var clock = new Clock(); var sender = new SctpReliableDelivery();
        sender.Enqueue(1, 0, 53, new byte[20000], 0); sender.TakeSendable(clock);
        for (ushort end = 2; end <= 4; end++) Assert.True(sender.Acknowledge(Sack(0, gapStart: 2, gapEnd: end), clock));
        var packets = sender.TakeSendable(clock);
        Assert.NotEmpty(packets); Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(packets[0].AsSpan(4)));
        var window = sender.CongestionWindow;
        Assert.True(sender.Acknowledge(Sack(0, gapStart: 2, gapEnd: 4), clock));
        Assert.DoesNotContain(sender.TakeSendable(clock), packet => BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4)) == 1);
        Assert.Equal(window, sender.CongestionWindow);
    }

    [Fact]
    public void DuplicateAndStaleAcksDoNotInflateWindowOrRetireQueuedBytes()
    {
        var clock = new Clock(); var sender = new SctpReliableDelivery();
        sender.Enqueue(1, 0, 53, new byte[20000], 0); sender.TakeSendable(clock);
        Assert.True(sender.Acknowledge(Sack(4), clock));
        var window = sender.CongestionWindow; var bytes = sender.BufferedAmount(0);
        for (var i = 0; i < 20; i++) Assert.True(sender.Acknowledge(Sack(4), clock));
        Assert.False(sender.Acknowledge(Sack(3), clock));
        Assert.Equal(window, sender.CongestionWindow); Assert.Equal(bytes, sender.BufferedAmount(0));
    }

    [Fact]
    public void IdleWindowDecaysBeforeAnotherTransfer()
    {
        var clock = new Clock(); var sender = new SctpReliableDelivery();
        sender.Enqueue(1, 0, 53, new byte[20000], 0); SctpTestWire.DrainAndAcknowledge(sender, clock);
        var previous = sender.CongestionWindow;
        clock.Advance(5); sender.Enqueue(sender.NextTsn, 0, 53, new byte[20000], clock.GetTimestamp());
        sender.TakeSendable(clock);
        Assert.True(sender.CongestionWindow < previous);
        Assert.Equal(4 * SctpReliableDelivery.MaximumDataChunkBytes, sender.CongestionWindow);
    }

    [Fact]
    public void RttEstimationBackoffAndKarnRuleControlTheNextDeadline()
    {
        var clock = new Clock(); var sender = new SctpReliableDelivery();
        sender.Enqueue(1, 0, 51, [1], clock.GetTimestamp()); sender.TakeSendable(clock);
        clock.Advance(0.2); Assert.True(sender.Acknowledge(Sack(1), clock));
        sender.Enqueue(2, 0, 51, [2], clock.GetTimestamp()); sender.TakeSendable(clock);
        clock.Advance(2); Assert.True(sender.Acknowledge(Sack(2), clock));
        sender.Enqueue(3, 0, 51, [3], clock.GetTimestamp()); sender.TakeSendable(clock);
        clock.Advance(2.5); Assert.Empty(sender.DueRetransmissions(clock, out _));
        clock.Advance(0.1); Assert.Single(sender.DueRetransmissions(clock, out _));
        Assert.True(sender.Acknowledge(Sack(3), clock)); // Retransmitted data cannot supply an RTT sample.
        sender.Enqueue(4, 0, 51, [4], clock.GetTimestamp()); sender.TakeSendable(clock);
        clock.Advance(5); Assert.Empty(sender.DueRetransmissions(clock, out _));
        clock.Advance(0.1); Assert.Single(sender.DueRetransmissions(clock, out _));
    }

    [Fact]
    public void CongestionAvoidanceAddsOnlyOneChunkForAFullWindowOfAcknowledgements()
    {
        var clock = new Clock(); var sender = new SctpReliableDelivery();
        sender.Enqueue(1, 0, 53, new byte[20000], 0); sender.TakeSendable(clock);
        for (ushort end = 2; end <= 4; end++) Assert.True(sender.Acknowledge(Sack(0, gapStart: 2, gapEnd: end), clock));
        sender.TakeSendable(clock);
        Assert.True(sender.Acknowledge(Sack(sender.HighestEmittedTsn), clock));
        var before = sender.CongestionWindow;
        while (sender.TakeSendable(clock).Count > 0) { }
        Assert.True(sender.FlightBytes >= before);
        Assert.True(sender.Acknowledge(Sack(sender.HighestEmittedTsn), clock));
        Assert.Equal(before + SctpReliableDelivery.MaximumDataChunkBytes, sender.CongestionWindow);
    }
}
