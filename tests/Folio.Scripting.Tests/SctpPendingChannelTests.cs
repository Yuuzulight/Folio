using Folio.Scripting.Rtc;
using System.Buffers.Binary;

namespace Folio.Scripting.Tests;

public sealed class SctpPendingChannelTests
{
    [Fact]
    public void AcceptsInitAndCookieParametersWithOuterChunkPadding()
    {
        using var client = new SctpDataChannel(); using var server = new SctpDataChannel();
        var requests = new Queue<byte[]>(); var responses = new Queue<byte[]>();
        client.OnPacketToSend += requests.Enqueue; server.OnPacketToSend += responses.Enqueue;
        server.StartAssociation(false); client.StartAssociation(true);
        var initial = requests.Dequeue();
        var padded = new byte[40]; initial.CopyTo(padded, 0);
        BinaryPrimitives.WriteUInt16BigEndian(padded.AsSpan(14), 26);
        // Supported Extensions, length six with two outer chunk-padding bytes.
        BinaryPrimitives.WriteUInt16BigEndian(padded.AsSpan(32), 0x8008);
        BinaryPrimitives.WriteUInt16BigEndian(padded.AsSpan(34), 6);
        padded[36] = 130; padded[37] = 192; Rechecksum(padded);
        server.ProcessIncomingPacket(padded);
        var ack = responses.Dequeue();
        // Opaque cookie with an odd length; clients must echo it verbatim.
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(14), (ushort)(ack.Length - 13));
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(34), 83);
        Rechecksum(ack);
        client.ProcessIncomingPacket(ack);
        var echo = requests.Dequeue();
        Assert.Equal(10, echo[12]);
        Assert.Equal(83, BinaryPrimitives.ReadUInt16BigEndian(echo.AsSpan(14)));
        Assert.Equal(ack.AsSpan(36, 79).ToArray(), echo.AsSpan(16, 79).ToArray());
    }

    private sealed class Clock : TimeProvider
    {
        internal long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
    }

    [Fact]
    public void ExpiredCookieCannotEstablishAssociationAndInitCanRefreshIt()
    {
        var clock = new Clock();
        using var client = new SctpDataChannel();
        using var server = new SctpDataChannel(clock, false);
        var requests = new Queue<byte[]>(); var responses = new Queue<byte[]>();
        client.OnPacketToSend += requests.Enqueue;
        server.OnPacketToSend += responses.Enqueue;
        server.StartAssociation(false); client.StartAssociation(true);
        var init = requests.Dequeue();
        server.ProcessIncomingPacket(init);
        client.ProcessIncomingPacket(responses.Dequeue());
        var oldEcho = requests.Dequeue();
        clock.Ticks = TimeSpan.FromSeconds(60).Ticks;
        server.ProcessIncomingPacket(oldEcho);
        Assert.Equal("connecting", server.State);
        Assert.Empty(responses);
        server.ProcessIncomingPacket(init);
        client.ProcessIncomingPacket(responses.Dequeue());
        server.ProcessIncomingPacket(oldEcho);
        Assert.Empty(responses);
        server.ProcessIncomingPacket(requests.Dequeue());
        client.ProcessIncomingPacket(responses.Dequeue());
        Assert.Equal("connected", client.State);
        Assert.Equal("connected", server.State);
    }

    [Fact]
    public void SynchronousCookieAckDeliveryAllowsPendingChannelsOnBothPeers()
    {
        using var client = new SctpDataChannel();
        using var server = new SctpDataChannel();
        client.OnPacketToSend += packet => server.ProcessIncomingPacket(packet);
        server.OnPacketToSend += packet => client.ProcessIncomingPacket(packet);
        var first = client.CreateDataChannel("client");
        var second = server.CreateDataChannel("server");
        var received = new List<string>();
        client.OnDataChannelCreated += channel => channel.OnMessage += value => received.Add((string)value);
        server.OnDataChannelCreated += channel => channel.OnMessage += value => received.Add((string)value);
        server.StartAssociation(false); client.StartAssociation(true);
        Assert.Equal("open", first.ReadyState);
        Assert.Equal("open", second.ReadyState);
        first.Send("from-client"); second.Send("from-server");
        Assert.Equal(new[] { "from-client", "from-server" }, received);
    }

    [Fact]
    public void InvalidTagsPortsAndCookiesCannotAdvanceHandshake()
    {
        using var client = new SctpDataChannel();
        using var server = new SctpDataChannel();
        var outboundClient = new Queue<byte[]>();
        var outboundServer = new Queue<byte[]>();
        client.OnPacketToSend += outboundClient.Enqueue;
        server.OnPacketToSend += outboundServer.Enqueue;
        server.StartAssociation(false); client.StartAssociation(true);
        var init = outboundClient.Dequeue();
        var invalid = (byte[])init.Clone(); invalid[7] = 1; Rechecksum(invalid);
        server.ProcessIncomingPacket(invalid);
        Assert.Empty(outboundServer);
        invalid = (byte[])init.Clone(); invalid[0] ^= 1; Rechecksum(invalid);
        server.ProcessIncomingPacket(invalid);
        Assert.Empty(outboundServer);
        server.ProcessIncomingPacket(init);
        var initAck = outboundServer.Dequeue();
        invalid = (byte[])initAck.Clone(); invalid[7] ^= 1; Rechecksum(invalid);
        client.ProcessIncomingPacket(invalid);
        Assert.Empty(outboundClient);
        // An INIT_ACK without its mandatory cookie is not a usable offer.
        invalid = initAck[..32];
        BinaryPrimitives.WriteUInt16BigEndian(invalid.AsSpan(14), 20); Rechecksum(invalid);
        client.ProcessIncomingPacket(invalid);
        Assert.Empty(outboundClient);
        // Even a correctly tagged COOKIE_ACK must follow our COOKIE_ECHO.
        invalid = new byte[16]; initAck.AsSpan(0, 8).CopyTo(invalid);
        invalid[12] = 11; invalid[15] = 4; Rechecksum(invalid);
        client.ProcessIncomingPacket(invalid);
        Assert.Equal("connecting", client.State);
        client.ProcessIncomingPacket(initAck);
        var echo = outboundClient.Dequeue();
        invalid = (byte[])echo.Clone(); invalid[^1] ^= 1; Rechecksum(invalid);
        server.ProcessIncomingPacket(invalid);
        Assert.Empty(outboundServer);
        Assert.Equal("connecting", server.State);
        server.ProcessIncomingPacket(echo);
        client.ProcessIncomingPacket(outboundServer.Dequeue());
        Assert.Equal("connected", client.State);
        Assert.Equal("connected", server.State);
    }

    private static void Rechecksum(byte[] packet)
    {
        packet.AsSpan(8, 4).Clear();
        uint crc = uint.MaxValue;
        foreach (var value in packet)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0x82f63b78u : 0);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), ~crc);
    }

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
        Assert.Throws<InvalidOperationException>(() => association.CreateDataChannel(new string('a', 9000), new string('b', 9000)));
        association.Dispose();
        Assert.Throws<ObjectDisposedException>(() => association.CreateDataChannel("retired"));
    }
}
