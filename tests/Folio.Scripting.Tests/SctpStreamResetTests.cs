using Folio.Scripting.Rtc;

namespace Folio.Scripting.Tests;

public sealed class SctpStreamResetTests
{
    private static (SctpDataChannel A, SctpDataChannel B, List<SctpDataChannel.ChannelState> Remote) Connect()
    {
        var a = new SctpDataChannel(); var b = new SctpDataChannel();
        a.OnPacketToSend += packet => b.ProcessIncomingPacket(packet);
        b.OnPacketToSend += packet => a.ProcessIncomingPacket(packet);
        var remote = new List<SctpDataChannel.ChannelState>();
        b.OnDataChannelCreated += remote.Add;
        b.StartAssociation(false); a.StartAssociation(true);
        return (a, b, remote);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ClosingEitherSideResetsTheStreamAndClosesBothChannels(bool closeLocal)
    {
        var (a, b, remote) = Connect();
        using var _a = a; using var _b = b;
        var local = a.CreateDataChannel("chat", explicitId: 4);
        var messages = new List<object>();
        local.Send("before");
        var peer = Assert.Single(remote);
        peer.OnMessage += messages.Add;
        local.Send("open");
        var closes = (Local: 0, Peer: 0);
        local.OnClose += () => closes.Local++;
        peer.OnClose += () => closes.Peer++;

        (closeLocal ? local : peer).Close();

        Assert.Equal("closed", local.ReadyState);
        Assert.Equal("closed", peer.ReadyState);
        Assert.Equal((1, 1), closes);
        Assert.Equal(["open"], messages);
        Assert.Equal("connected", a.State);

        // A reset stream starts again at sequence number 0, so a new channel on the same id still delivers.
        var reopened = a.CreateDataChannel("again", explicitId: 4);
        var again = remote[^1];
        Assert.NotSame(peer, again);
        object? delivered = null;
        again.OnMessage += message => delivered = message;
        reopened.Send("after reset");
        Assert.Equal("after reset", delivered);
    }

    [Fact]
    public void ClosingTwiceSendsOneResetAndKeepsOtherChannelsOpen()
    {
        var (a, b, remote) = Connect();
        using var _a = a; using var _b = b;
        var first = a.CreateDataChannel("first", explicitId: 0);
        var second = a.CreateDataChannel("second", explicitId: 2);
        var otherPeer = remote.Single(channel => channel.Id == 2);
        object? delivered = null;
        otherPeer.OnMessage += message => delivered = message;

        first.Close();
        first.Close();

        Assert.Equal("closed", first.ReadyState);
        Assert.Equal("open", second.ReadyState);
        second.Send("still open");
        Assert.Equal("still open", delivered);
    }
}
