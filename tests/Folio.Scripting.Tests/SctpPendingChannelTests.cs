using Folio.Scripting.Rtc;

namespace Folio.Scripting.Tests;

public sealed class SctpPendingChannelTests
{
    [Fact]
    public void ChannelsCreatedBeforeNegotiationWaitAndUseDtlsRoleParity()
    {
        using var client = new SctpDataChannel();
        using var server = new SctpDataChannel();
        var queue = new Queue<(SctpDataChannel Target, byte[] Packet)>();
        client.OnPacketToSend += packet => queue.Enqueue((server, packet));
        server.OnPacketToSend += packet => queue.Enqueue((client, packet));
        var clientChannel = client.CreateDataChannel("client-first");
        var serverChannel = server.CreateDataChannel("server-first");
        var cancelled = server.CreateDataChannel("cancelled"); cancelled.Close();
        Assert.Empty(queue);
        Assert.Equal("connecting", clientChannel.ReadyState);
        Assert.Throws<InvalidOperationException>(() => clientChannel.Send("too soon"));
        var remoteChannels = new List<SctpDataChannel.ChannelState>();
        client.OnDataChannelCreated += remoteChannels.Add;
        server.OnDataChannelCreated += remoteChannels.Add;
        server.StartAssociation(false); client.StartAssociation(true);
        var count = 0;
        while (queue.TryDequeue(out var message))
        {
            Assert.True(++count < 100);
            message.Target.ProcessIncomingPacket(message.Packet);
        }
        Assert.Equal("open", clientChannel.ReadyState);
        Assert.Equal("open", serverChannel.ReadyState);
        Assert.Equal(0, clientChannel.Id % 2);
        Assert.Equal(1, serverChannel.Id % 2);
        Assert.Equal(2, remoteChannels.Count);
        Assert.DoesNotContain(remoteChannels, channel => channel.Label == "cancelled");
    }

    [Fact]
    public void RejectsDuplicateStreamsAndOversizedUtf8Labels()
    {
        using var association = new SctpDataChannel();
        association.CreateDataChannel("first", explicitId: 10);
        Assert.Throws<InvalidOperationException>(() => association.CreateDataChannel("duplicate", explicitId: 10));
        Assert.Throws<InvalidOperationException>(() => association.CreateDataChannel(new string('\u20ac', 22000)));
        association.Dispose();
        Assert.Throws<ObjectDisposedException>(() => association.CreateDataChannel("retired"));
    }
}
