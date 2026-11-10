using Folio.Scripting.Rtc;

namespace Folio.Scripting.Tests;

public sealed class SctpRetransmissionTests
{
    private sealed class Clock : TimeProvider
    {
        internal long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
        internal CapturedTimer? Timer;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => Timer = new CapturedTimer(callback, state);
    }

    private sealed class CapturedTimer(TimerCallback callback, object? state) : ITimer
    {
        internal bool Disposed;
        internal void Fire() { if (!Disposed) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    [Fact]
    public void OwnedTimerRetriesAndDisposalSurvivesThrowingCloseCallbacks()
    {
        var clock = new Clock();
        using var client = new SctpDataChannel(clock);
        var channel = client.CreateDataChannel("timer");
        channel.OnClose += () => throw new InvalidOperationException("consumer callback");
        var sends = 0; client.OnPacketToSend += _ => sends++;
        client.StartAssociation(true);
        Assert.NotNull(clock.Timer);
        clock.Ticks = TimeSpan.FromSeconds(1).Ticks; clock.Timer.Fire();
        Assert.Equal(2, sends);
        client.Dispose();
        Assert.True(clock.Timer.Disposed);
        clock.Ticks += TimeSpan.FromSeconds(10).Ticks; clock.Timer.Fire();
        Assert.Equal(2, sends);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(10)]
    [InlineData(11)]
    public void RecoversFromEachLostAssociationFlight(int lostChunk)
    {
        var clock = new Clock();
        using var client = new SctpDataChannel(clock, false);
        using var server = new SctpDataChannel(clock, false);
        var queue = new Queue<(SctpDataChannel Target, byte[] Packet)>();
        var dropped = false;
        void Send(SctpDataChannel target, byte[] packet)
        {
            if (!dropped && packet[12] == lostChunk) { dropped = true; return; }
            queue.Enqueue((target, packet));
        }
        client.OnPacketToSend += packet => Send(server, packet);
        server.OnPacketToSend += packet => Send(client, packet);
        var channel = client.CreateDataChannel("loss");
        server.StartAssociation(false); client.StartAssociation(true);
        for (var turn = 0; turn < 10; turn++)
        {
            var count = 0;
            while (queue.TryDequeue(out var message))
            {
                Assert.True(++count < 100);
                message.Target.ProcessIncomingPacket(message.Packet);
            }
            if (channel.ReadyState == "open") break;
            clock.Ticks += TimeSpan.FromSeconds(1).Ticks;
            client.RetryDueHandshake(); server.RetryDueHandshake();
        }
        Assert.True(dropped);
        Assert.Equal("connected", client.State);
        Assert.Equal("connected", server.State);
        Assert.Equal("open", channel.ReadyState);
    }

    [Fact]
    public void BoundsRetriesClosesPendingChannelsAndCannotReviveAfterTimeout()
    {
        var clock = new Clock();
        using var client = new SctpDataChannel(clock, false);
        var channel = client.CreateDataChannel("timeout");
        var sends = 0; client.OnPacketToSend += _ => sends++;
        client.StartAssociation(true);
        client.RetryDueHandshake(); Assert.Equal(1, sends);
        for (var retry = 0; retry < 7; retry++)
        {
            clock.Ticks += TimeSpan.FromSeconds(8).Ticks;
            client.RetryDueHandshake();
        }
        Assert.Equal(7, sends);
        Assert.Equal("failed", client.State);
        Assert.Equal("closed", channel.ReadyState);
        Assert.Throws<InvalidOperationException>(() => client.CreateDataChannel("after-failure"));
        client.Dispose();
        clock.Ticks += TimeSpan.FromMinutes(1).Ticks; client.RetryDueHandshake();
        Assert.Equal(7, sends);
    }

    [Fact]
    public void PassiveAssociationHasAnAbsoluteHandshakeDeadline()
    {
        var clock = new Clock();
        using var server = new SctpDataChannel(clock, false);
        var channel = server.CreateDataChannel("passive");
        server.StartAssociation(false);
        clock.Ticks = TimeSpan.FromMinutes(1).Ticks; server.RetryDueHandshake();
        Assert.Equal("failed", server.State);
        Assert.Equal("closed", channel.ReadyState);
    }
}
