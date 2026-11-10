using System.Buffers.Binary;
using Folio.Scripting.Rtc;

namespace Folio.Scripting.Tests;

public sealed class SctpFragmentationTests
{
    private sealed class Clock : TimeProvider { public override long GetTimestamp() => 0; }
    private readonly Clock _clock = new();
    private static byte[] Pattern(int length) => Enumerable.Range(0, length).Select(index => (byte)(index % 251)).ToArray();

    [Fact]
    public void FragmentsUseConsecutiveTsnsOneSequenceAndBoundedPayloads()
    {
        var sender = new SctpReliableDelivery();
        var chunks = sender.Enqueue(1, 4, 53, Pattern(5000), 0);
        Assert.Equal(5, chunks.Count);
        for (var index = 0; index < chunks.Count; index++)
        {
            Assert.InRange(chunks[index].Length, 17, 16 + SctpDataChannel.MaxFragmentPayloadBytes);
            Assert.Equal((uint)(index + 1), BinaryPrimitives.ReadUInt32BigEndian(chunks[index].AsSpan(4)));
            Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(chunks[index].AsSpan(10)));
            Assert.Equal(index == 0 ? 2 : index == chunks.Count - 1 ? 1 : 0, chunks[index][1]);
        }
        Assert.Equal(5000, sender.BufferedAmount(4));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(Assert.Single(sender.Enqueue(sender.NextTsn, 4, 51, [9], 0)).AsSpan(10)));
    }

    [Fact]
    public void EveryThreeFragmentPermutationAndDuplicateReassemblesExactlyOnce()
    {
        int[][] permutations = [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];
        foreach (var order in permutations)
        {
            var sender = new SctpReliableDelivery(); var receiver = new SctpReliableDelivery(); receiver.SetPeerInitialTsn(1);
            var expected = Pattern(2 * SctpDataChannel.MaxFragmentPayloadBytes + 17);
            var source = (byte[])expected.Clone(); var chunks = sender.Enqueue(1, 0, 53, source, 0); source.AsSpan().Fill(99);
            Assert.Equal(chunks.Count, sender.TakeSendable(_clock).Count);
            var messages = new List<SctpReliableDelivery.Message>();
            foreach (var index in order)
            {
                messages.AddRange(receiver.Receive(chunks[index][1], chunks[index].AsSpan(4)));
                Assert.Empty(receiver.Receive(chunks[index][1], chunks[index].AsSpan(4)));
            }
            Assert.Equal(expected, Assert.Single(messages).Data);
            Assert.True(sender.Acknowledge(receiver.CreateSack().AsSpan(4), _clock)); Assert.Equal(0, sender.BufferedAmount(0));
        }
    }

    [Fact]
    public void FragmentChainReassemblesAcrossTsnWrap()
    {
        var sender = new SctpReliableDelivery(uint.MaxValue - 1); var receiver = new SctpReliableDelivery();
        receiver.SetPeerInitialTsn(uint.MaxValue - 1);
        var expected = Pattern(3000); var chunks = sender.Enqueue(uint.MaxValue - 1, 0, 53, expected, 0);
        Assert.Equal(chunks.Count, sender.TakeSendable(_clock).Count);
        var messages = new List<SctpReliableDelivery.Message>();
        foreach (var chunk in chunks.Reverse()) messages.AddRange(receiver.Receive(chunk[1], chunk.AsSpan(4)));
        Assert.Equal(expected, Assert.Single(messages).Data);
        Assert.True(sender.Acknowledge(receiver.CreateSack().AsSpan(4), _clock));
    }

    [Fact]
    public void UnorderedReassemblyIgnoresFragmentSequenceNumbersAndDoesNotAdvanceOrderedSequence()
    {
        var sender = new SctpReliableDelivery(); var receiver = new SctpReliableDelivery(); receiver.SetPeerInitialTsn(1);
        var expected = Pattern(3000); var chunks = sender.Enqueue(1, 0, 53, expected, 0);
        var messages = new List<SctpReliableDelivery.Message>();
        for (var index = chunks.Count - 1; index >= 0; index--)
        {
            BinaryPrimitives.WriteUInt16BigEndian(chunks[index].AsSpan(10), (ushort)(123 + index));
            messages.AddRange(receiver.Receive((byte)(chunks[index][1] | 4), chunks[index].AsSpan(4)));
        }
        Assert.Equal(expected, Assert.Single(messages).Data);
        var next = Assert.Single(sender.Enqueue(sender.NextTsn, 0, 51, [9], 0));
        BinaryPrimitives.WriteUInt16BigEndian(next.AsSpan(10), 0);
        Assert.Equal(new byte[] { 9 }, Assert.Single(receiver.Receive(next[1], next.AsSpan(4))).Data);
    }

    [Theory]
    [InlineData(8)] // Stream identifier
    [InlineData(10)] // Ordered stream sequence
    [InlineData(12)] // Payload protocol identifier
    public void ConflictingFragmentMetadataCannotProduceAnApplicationMessage(int headerOffset)
    {
        var sender = new SctpReliableDelivery(); var receiver = new SctpReliableDelivery(); receiver.SetPeerInitialTsn(1);
        var chunks = sender.Enqueue(1, 0, 53, Pattern(3000), 0);
        Assert.Empty(receiver.Receive(chunks[0][1], chunks[0].AsSpan(4)));
        chunks[1][headerOffset] ^= 1;
        Assert.Throws<InvalidDataException>(() => receiver.Receive(chunks[1][1], chunks[1].AsSpan(4)));
    }

    [Fact]
    public void OversizedFragmentedSendIsRefusedAtomically()
    {
        var sender = new SctpReliableDelivery();
        var data = Pattern(SctpDataChannel.MaxMessageBytes);
        for (var message = 0; message < 3; message++) sender.Enqueue(sender.NextTsn, 0, 53, data, 0);
        var nextTsn = sender.NextTsn; var amount = sender.BufferedAmount(0);
        Assert.Throws<InvalidOperationException>(() => sender.Enqueue(nextTsn, 0, 53, data, 0));
        Assert.Equal(nextTsn, sender.NextTsn); Assert.Equal(amount, sender.BufferedAmount(0));
        SctpTestWire.DrainAndAcknowledge(sender, _clock);
        var next = Assert.Single(sender.Enqueue(nextTsn, 0, 51, [9], 0));
        Assert.Equal(3, BinaryPrimitives.ReadUInt16BigEndian(next.AsSpan(10)));
    }

    [Fact]
    public void GapReportsStayWithinTheConservativePathMtu()
    {
        var receiver = new SctpReliableDelivery(); receiver.SetPeerInitialTsn(1);
        for (uint tsn = 2; tsn <= 1024; tsn += 2)
        {
            var payload = new byte[13]; BinaryPrimitives.WriteUInt32BigEndian(payload, tsn);
            Assert.Single(receiver.Receive(7, payload));
        }
        var sack = receiver.CreateSack();
        Assert.Equal(256, BinaryPrimitives.ReadUInt16BigEndian(sack.AsSpan(12)));
        Assert.True(sack.Length + 12 + 37 < 1200);
    }
}
