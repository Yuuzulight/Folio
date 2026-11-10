using System.Buffers.Binary;
using Folio.Scripting.Rtc;

namespace Folio.Scripting.Tests;

public sealed class SctpReliabilityTransportTests
{
    [Fact]
    public void SmallerNegotiatedLimitClosesUnestablishablePendingChannel()
    {
        using var pair = new Pair();
        pair.Client.ConfigureRemote(1); pair.Start();
        Assert.Equal("connected", pair.Client.State);
        Assert.Equal("closed", pair.Local.ReadyState);
        Assert.Equal("open", pair.Reverse.ReadyState);
        pair.Reverse.Send("other direction works"); pair.Drain();
        Assert.Equal(new[] { "other direction works" }, pair.Messages);
    }

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
        for (var tick = 0; tick < 59; tick++) pair.Tick();
        Assert.Equal("connected", pair.Client.State);
        pair.Tick(); // The association's absolute stalled-send budget is 60 seconds.
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

    [Fact]
    public void LostMiddleFragmentRecoversBeforeTheFollowingMessageIsDelivered()
    {
        using var pair = new Pair(); pair.Start(); var dropped = false;
        uint? missingTsn = null; var recovered = false;
        pair.Drop = packet =>
        {
            if (packet[12] != 0) return false;
            var tsn = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(16));
            if (!dropped && (packet[13] & 3) == 0)
            { dropped = true; missingTsn = tsn; return true; }
            if (missingTsn == tsn) recovered = true;
            return false;
        };
        foreach (var channel in pair.RemoteChannels) channel.OnMessage += _ => Assert.True(recovered);
        var expected = new string('\u20ac', 16000);
        pair.Local.Send(expected); pair.Local.Send("after"); pair.Drain();
        Assert.True(dropped);
        pair.Tick();
        Assert.True(recovered);
        Assert.Equal(new[] { expected, "after" }, pair.Messages);
        Assert.Equal(0, pair.Local.BufferedAmount);
    }

    [Fact]
    public void ConflictingValidChecksumFragmentClosesAssociationWithoutPartialDelivery()
    {
        using var pair = new Pair(); pair.Start();
        pair.Local.Send(new string('x', 5000));
        var first = pair.Queue.Dequeue(); var second = pair.Queue.Dequeue();
        second.Packet[24] ^= 1; Rechecksum(second.Packet); // Change fragment PPID, preserving packet integrity.
        first.Target.ProcessIncomingPacket(first.Packet);
        second.Target.ProcessIncomingPacket(second.Packet);
        Assert.Equal("failed", pair.Server.State);
        Assert.Equal("closed", pair.Reverse.ReadyState);
        Assert.Empty(pair.Messages);
    }

    [Fact]
    public void MissingFinalFragmentExpiresWithoutAffectingAnUnrelatedAssociation()
    {
        using var pair = new Pair(); using var sibling = new Pair(); pair.Start(); sibling.Start();
        pair.Drop = packet => packet[12] == 0 && (packet[13] & 1) != 0;
        pair.Local.Send(new string('x', 5000)); pair.Drain();
        Assert.Empty(pair.Messages);
        pair.Clock.Ticks = TimeSpan.FromSeconds(60).Ticks; pair.Server.RetryDueData();
        Assert.Equal("failed", pair.Server.State);
        Assert.Equal("closed", pair.Reverse.ReadyState);
        sibling.Local.Send("still alive"); sibling.Drain();
        Assert.Equal(new[] { "still alive" }, sibling.Messages);
    }

    private static void Rechecksum(byte[] packet)
    {
        packet.AsSpan(8, 4).Clear(); uint crc = uint.MaxValue;
        foreach (var value in packet)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0x82f63b78u : 0);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), ~crc);
    }
}
