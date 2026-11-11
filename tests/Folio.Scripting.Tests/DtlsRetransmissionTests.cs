using Folio.Scripting.Rtc;

namespace Folio.Scripting.Tests;

public sealed class DtlsRetransmissionTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RecoversFromDroppedHandshakeRecords(int lostRecord)
    {
        var clock = new Clock();
        using var client = new DtlsTransport(clock, false);
        using var server = new DtlsTransport(clock, false) { IsClient = false };
        client.RemoteFingerprint = server.Fingerprint; server.RemoteFingerprint = client.Fingerprint;
        var queue = new Queue<(DtlsTransport Target, byte[] Record)>();
        var dropped = false;
        void Send(bool fromClient, byte[] bytes)
        {
            var match = bytes[0] == 22 && (lostRecord switch
            {
                0 => fromClient && bytes[4] == 0 && bytes[13] == 1,
                1 => !fromClient && bytes[4] == 0 && bytes[13] == 11,
                2 => fromClient && bytes[4] == 1,
                _ => !fromClient && bytes[4] == 1
            });
            if (!dropped && match) { dropped = true; return; }
            queue.Enqueue((fromClient ? server : client, bytes));
        }
        client.OnDatagramToSend += bytes => Send(true, bytes);
        server.OnDatagramToSend += bytes => Send(false, bytes);
        server.StartHandshake(); client.StartHandshake();
        for (var tick = 0; tick < 20 && (client.State != "connected" || server.State != "connected"); tick++)
        {
            var processed = 0;
            while (queue.TryDequeue(out var message))
            {
                Assert.True(++processed < 200);
                message.Target.ProcessIncomingDatagram(message.Record);
            }
            clock.Now += TimeSpan.FromSeconds(1);
            client.RetryDueFlight(); server.RetryDueFlight();
        }
        Assert.True(dropped);
        Assert.Equal("connected", client.State); Assert.Equal("connected", server.State);
    }

    [Fact]
    public void StopsAfterBoundedRetriesAndDisposal()
    {
        var clock = new Clock();
        using var transport = new DtlsTransport(clock, false);
        var sends = 0; transport.OnDatagramToSend += _ => sends++;
        transport.StartHandshake();
        for (var retry = 0; retry < 8; retry++)
        { clock.Now += TimeSpan.FromMinutes(1); transport.RetryDueFlight(); }
        Assert.Equal("failed", transport.State); Assert.Equal(7, sends);
        transport.Dispose();
        clock.Now += TimeSpan.FromMinutes(1); transport.RetryDueFlight();
        Assert.Equal(7, sends);
    }
}
