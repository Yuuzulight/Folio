using System.Buffers.Binary;
using Folio.Scripting.Rtc;

namespace Folio.Scripting.Tests;

public sealed class SctpReliabilityTransportTests
{
    private sealed class Clock : TimeProvider
    {
        internal long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
    }

    private sealed class Pair : IDisposable
    {
        internal readonly Clock Clock = new();
        internal readonly SctpDataChannel Client;
        internal readonly SctpDataChannel Server;
        internal readonly Queue<(SctpDataChannel Target, byte[] Packet)> Queue = new();
        internal readonly List<string> Messages = [];
        internal readonly List<SctpDataChannel.ChannelState> RemoteChannels = [];
        internal readonly SctpDataChannel.ChannelState Local;
        internal readonly SctpDataChannel.ChannelState Reverse;
        internal Func<byte[], bool>? Drop;
        internal Pair()
        {
            Client = new(Clock, false); Server = new(Clock, false);
            Client.OnPacketToSend += packet => Send(Server, packet);
            Server.OnPacketToSend += packet => Send(Client, packet);
            Client.OnDataChannelCreated += Remote; Server.OnDataChannelCreated += Remote;
            Local = Client.CreateDataChannel("client"); Reverse = Server.CreateDataChannel("server");
        }
        private void Remote(SctpDataChannel.ChannelState channel)
        { RemoteChannels.Add(channel); channel.OnMessage += message => Messages.Add((string)message); }
        private void Send(SctpDataChannel target, byte[] packet)
        { if (Drop?.Invoke(packet) != true) Queue.Enqueue((target, packet)); }
        internal void Start() { Server.StartAssociation(false); Client.StartAssociation(true); Drain(); }
        internal void Drain()
        {
            var count = 0;
            while (Queue.TryDequeue(out var message))
            { Assert.True(++count < 500); message.Target.ProcessIncomingPacket(message.Packet); }
        }
        internal void Tick()
        {
            Clock.Ticks += TimeSpan.FromSeconds(1).Ticks;
            Client.RetryDueHandshake(); Server.RetryDueHandshake();
            Client.RetryDueData(); Server.RetryDueData(); Drain();
        }
        public void Dispose() { Client.Dispose(); Server.Dispose(); }
    }

    [Fact]
    public void LostCookieAckRecoversPendingChannelsInBothDirections()
    {
        using var pair = new Pair(); var dropped = false;
        pair.Drop = packet => !dropped && packet[12] == 11 && (dropped = true);
        pair.Start();
        for (var tick = 0; tick < 10 && (pair.Local.ReadyState != "open" || pair.Reverse.ReadyState != "open"); tick++) pair.Tick();
        Assert.True(dropped);
        Assert.Equal("open", pair.Local.ReadyState); Assert.Equal("open", pair.Reverse.ReadyState);
        Assert.Equal(2, pair.RemoteChannels.Count);
        pair.Local.Send("forward"); pair.Reverse.Send("reverse"); pair.Drain();
        Assert.Equal(new[] { "forward", "reverse" }, pair.Messages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LostDataOrSackRecoversWithoutDuplicateMessageDelivery(bool loseSack)
    {
        using var pair = new Pair(); pair.Start(); var dropped = false;
        pair.Drop = packet => !dropped && (loseSack ? packet[12] == 3 :
            packet[12] == 0 && BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(24)) == 51) && (dropped = true);
        pair.Local.Send("first"); pair.Local.Send("second"); pair.Drain();
        Assert.True(dropped);
        if (!loseSack) Assert.Empty(pair.Messages);
        for (var tick = 0; tick < 4; tick++) pair.Tick();
        Assert.Equal(new[] { "first", "second" }, pair.Messages);
        Assert.Equal(0, pair.Local.BufferedAmount);
    }

    [Fact]
    public void ReorderedAndDuplicatePacketsDeliverMessagesOnceInStreamOrder()
    {
        using var pair = new Pair(); pair.Start();
        pair.Local.Send("first"); pair.Local.Send("second");
        var first = pair.Queue.Dequeue(); var second = pair.Queue.Dequeue();
        pair.Queue.Enqueue(second); pair.Queue.Enqueue(second); pair.Queue.Enqueue(first); pair.Queue.Enqueue(first);
        pair.Drain();
        Assert.Equal(new[] { "first", "second" }, pair.Messages);
        Assert.Equal(0, pair.Local.BufferedAmount);
    }

    [Fact]
    public void PermanentDataLossClosesChannelsAndReleasesBufferedPayloads()
    {
        using var pair = new Pair(); pair.Start();
        pair.Drop = packet => packet[12] == 0;
        pair.Local.Send("lost"); pair.Drain();
        Assert.True(pair.Local.BufferedAmount > 0);
        for (var tick = 0; tick < 45; tick++) pair.Tick();
        Assert.Equal("failed", pair.Client.State);
        Assert.Equal("closed", pair.Local.ReadyState);
        Assert.Equal(0, pair.Local.BufferedAmount);
    }

    [Fact]
    public void EmptyStringReachesTheApplicationAsEmptyRatherThanItsWirePadding()
    {
        using var pair = new Pair(); pair.Start();
        pair.Local.Send(""); pair.Drain();
        Assert.Equal(new[] { "" }, pair.Messages);
        Assert.Equal(0, pair.Local.BufferedAmount);
    }
}
