using System.Buffers.Binary;
using Folio.Scripting.Rtc;

namespace Folio.Scripting.Tests;

public sealed class SctpMetadataBudgetTests
{
    [Fact]
    public void LocalAdmissionAccountsForUtf8AndRejectsWithoutConsumingBudget()
    {
        using var association = new SctpDataChannel();
        for (var i = 0; i < 4; i++) association.CreateDataChannel(new string('\u6c49', 10922));
        Assert.Equal(SctpDataChannel.MaxChannelMetadataBytes - 16, association.RetainedChannelMetadataBytes);
        Assert.Throws<InvalidOperationException>(() => association.CreateDataChannel("", "123456789"));
        var last = association.CreateDataChannel("", "12345678");
        Assert.Equal(8, last.Id);
        Assert.Equal(SctpDataChannel.MaxChannelMetadataBytes, association.RetainedChannelMetadataBytes);
        Assert.Throws<InvalidOperationException>(() => association.CreateDataChannel("x"));
        association.Dispose();
        Assert.Equal(0, association.RetainedChannelMetadataBytes);
    }

    [Fact]
    public void ClosedRegisteredStreamsKeepTheirMetadataChargeUntilRegistryTeardown()
    {
        using var association = new SctpDataChannel();
        for (var i = 0; i < 4; i++) association.CreateDataChannel(new string('a', 32768)).Close();
        // Stream reset is separate work. Closed registered states still retain
        // strings, so close must not make their storage free in the accounting.
        Assert.Equal(SctpDataChannel.MaxChannelMetadataBytes, association.RetainedChannelMetadataBytes);
        Assert.Throws<InvalidOperationException>(() => association.CreateDataChannel("x"));
    }

    [Fact]
    public void FailedOpenAdmissionRollsBackMetadataAndStreamReservation()
    {
        using var client = new SctpDataChannel(TimeProvider.System, false);
        using var server = new SctpDataChannel(TimeProvider.System, false);
        var delayed = new Queue<byte[]>(); var blocked = false;
        client.OnPacketToSend += packet => { if (blocked) delayed.Enqueue(packet); else server.ProcessIncomingPacket(packet); };
        server.OnPacketToSend += packet => client.ProcessIncomingPacket(packet);
        var initial = client.CreateDataChannel("initial");
        server.StartAssociation(false); client.StartAssociation(true);
        var metadataBefore = client.RetainedChannelMetadataBytes;
        blocked = true;
        for (var i = 0; i < SctpReliableDelivery.MaxChunks; i++) initial.Send(new byte[] { 1 });
        Assert.Throws<InvalidOperationException>(() => client.CreateDataChannel("cannot enqueue OPEN"));
        Assert.Equal(metadataBefore, client.RetainedChannelMetadataBytes);
        blocked = false;
        while (delayed.TryDequeue(out var packet)) server.ProcessIncomingPacket(packet);
        Assert.Equal(0, initial.BufferedAmount);
        var admitted = client.CreateDataChannel("now");
        Assert.Equal(2, admitted.Id);
        Assert.Equal("open", admitted.ReadyState);
        Assert.Equal(metadataBefore + 6, client.RetainedChannelMetadataBytes);
    }

    [Fact]
    public void RemoteMetadataFloodFailsOnlyTheOffendingAssociationAndReleasesItsRegistry()
    {
        using var client = new SctpDataChannel(); using var server = new SctpDataChannel();
        client.OnPacketToSend += packet => server.ProcessIncomingPacket(packet);
        server.OnPacketToSend += packet => client.ProcessIncomingPacket(packet);
        server.StartAssociation(false); client.StartAssociation(true);
        var received = new List<SctpDataChannel.ChannelState>();
        server.OnDataChannelCreated += received.Add;
        uint tsn = 1;
        for (ushort i = 0; i < 4; i++)
            InjectOpen(server, ref tsn, (ushort)(2 * i), 32768);
        Assert.Equal(4, received.Count);
        Assert.Equal("connected", server.State);
        Assert.Equal(SctpDataChannel.MaxChannelMetadataBytes, server.RetainedChannelMetadataBytes);
        InjectOpen(server, ref tsn, 8, 1);
        Assert.Equal("failed", server.State);
        Assert.Equal(0, server.RetainedChannelMetadataBytes);
        Assert.All(received, channel => Assert.Equal("closed", channel.ReadyState));
        Assert.Equal(4, received.Count);

        using var siblingA = new SctpDataChannel(); using var siblingB = new SctpDataChannel();
        siblingA.OnPacketToSend += packet => siblingB.ProcessIncomingPacket(packet);
        siblingB.OnPacketToSend += packet => siblingA.ProcessIncomingPacket(packet);
        string? delivered = null;
        siblingB.OnDataChannelCreated += channel => channel.OnMessage += message => delivered = (string)message;
        var healthy = siblingA.CreateDataChannel("healthy");
        siblingB.StartAssociation(false); siblingA.StartAssociation(true);
        healthy.Send("still works");
        Assert.Equal("still works", delivered);
    }

    private static void InjectOpen(SctpDataChannel receiver, ref uint tsn, ushort stream, ushort labelBytes)
    {
        var open = new byte[12 + labelBytes]; open[0] = 3;
        BinaryPrimitives.WriteUInt16BigEndian(open.AsSpan(8), labelBytes);
        open.AsSpan(12).Fill((byte)'a');
        // A malicious sender can bypass its own admission/window control. Feed
        // authentic framed fragments to exercise the receiver's independent cap.
        var sender = new SctpReliableDelivery(tsn);
        foreach (var chunk in sender.Enqueue(tsn, stream, 50, open, 0))
        {
            var packet = new byte[12 + ((chunk.Length + 3) & ~3)];
            BinaryPrimitives.WriteUInt16BigEndian(packet, 5000);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 5000);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), receiver.LocalVerificationTag);
            chunk.CopyTo(packet, 12);
            uint crc = uint.MaxValue;
            foreach (var value in packet)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0x82F63B78u);
            }
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), ~crc);
            receiver.ProcessIncomingPacket(packet);
        }
        tsn = sender.NextTsn;
    }
}
