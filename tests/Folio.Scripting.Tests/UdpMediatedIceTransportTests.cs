using System.Net;
using System.Net.Sockets;
using System.Text;
using Folio.Scripting.Ipc;
using Folio.Scripting.Rtc;

namespace Folio.Scripting.Tests;

/// <summary>IceAgent over the host-mediated transport, joined to a real broker by an in-memory channel.</summary>
public sealed class UdpMediatedIceTransportTests
{
    private sealed class Channel : IDisposable
    {
        public readonly MediatedUdpClient Client;
        public readonly UdpBroker Broker;
        public volatile bool Granted = true;

        public Channel(UdpBrokerOptions? options = null)
        {
            Client = new MediatedUdpClient(command => Broker!.Handle(command));
            Broker = new UdpBroker(options ?? UdpBrokerTests.Loopback, () => Granted, Client.Deliver);
        }

        public void Dispose() { Broker.Dispose(); Client.Close(); }
    }

    private static async Task<(IceAgent Agent, TaskCompletionSource Connected)> ConnectedAgentAsync(Channel channel, Socket peer)
    {
        var agent = new IceAgent(null, channel.Client.CreateTransport()) { RemoteUfrag = "remote", RemotePassword = "remote-password" };
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.OnConnectionStateChanged += state => { if (state == "connected") connected.TrySetResult(); };
        await agent.StartGatheringAsync();
        agent.AddRemoteCandidate(new IceCandidate
        {
            Foundation = "peer", Component = 1, Protocol = "udp", Priority = 100,
            Address = "127.0.0.1", Port = ((IPEndPoint)peer.LocalEndPoint!).Port, Type = "host"
        });
        var bytes = new byte[2048];
        var request = await peer.ReceiveFromAsync(bytes, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), TestContext.Current.CancellationToken);
        Assert.True(StunMessage.TryParse(bytes.AsSpan(0, request.ReceivedBytes), out var message));
        var reply = StunMessage.Encode(StunMessage.BindingResponse, message.TransactionId.Span, [], Encoding.UTF8.GetBytes("remote-password"));
        await peer.SendToAsync(reply, SocketFlags.None, request.RemoteEndPoint);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        return (agent, connected);
    }

    [Fact]
    public async Task AgentGathersChecksConnectivityAndExchangesDatagramsThroughTheBroker()
    {
        using var channel = new Channel();
        using var peer = UdpBrokerTests.Peer();
        var (agent, _) = await ConnectedAgentAsync(channel, peer);
        using var _ = agent;
        Assert.Equal("connected", agent.ConnectionState);
        Assert.Equal(1, channel.Broker.OpenSockets);
        Assert.All(agent.LocalCandidates, candidate => Assert.True(candidate.Port > 0));

        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.OnDatagramReceived += datagram => received.TrySetResult(datagram);
        agent.SendDatagram([0x16, 0xFE, 0xFD, 1, 2, 3]); // non-STUN, DTLS-shaped
        var bytes = new byte[2048];
        var outbound = await peer.ReceiveFromAsync(bytes, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), TestContext.Current.CancellationToken);
        Assert.Equal([0x16, 0xFE, 0xFD, 1, 2, 3], bytes.AsSpan(0, outbound.ReceivedBytes).ToArray());

        await peer.SendToAsync(new byte[] { 0x17, 0xFE, 0xFD, 9 }, SocketFlags.None, outbound.RemoteEndPoint);
        Assert.Equal([0x17, 0xFE, 0xFD, 9], await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RefusedSocketLeavesGatheringCompleteWithoutCandidatesAndTheConnectionFailed()
    {
        using var channel = new Channel { Granted = false };
        using var agent = new IceAgent(null, channel.Client.CreateTransport());
        var gathered = new List<IceCandidate>();
        agent.OnCandidateGathered += gathered.Add;
        await agent.StartGatheringAsync();
        Assert.Equal("complete", agent.GatheringState);
        Assert.Equal("failed", agent.ConnectionState);
        Assert.Empty(gathered);
        Assert.Equal(0, channel.Broker.OpenSockets);
        agent.SendDatagram([1]); // no transport: silently dropped
    }

    [Fact]
    public async Task SocketQuotaRefusalFailsTheSecondAgentOnly()
    {
        using var channel = new Channel(new UdpBrokerOptions { AllowLoopback = true, MaxSockets = 1 });
        using var first = new IceAgent(null, channel.Client.CreateTransport());
        using var second = new IceAgent(null, channel.Client.CreateTransport());
        await first.StartGatheringAsync();
        await second.StartGatheringAsync();
        Assert.NotEmpty(first.LocalCandidates);
        Assert.Empty(second.LocalCandidates);
        Assert.Equal("failed", second.ConnectionState);
    }

    [Fact]
    public async Task RevocationEndsTheReceivePumpAndStopsSending()
    {
        using var channel = new Channel();
        using var peer = UdpBrokerTests.Peer();
        var (agent, _) = await ConnectedAgentAsync(channel, peer);
        using var _ = agent;
        var states = new List<string>();
        agent.OnConnectionStateChanged += state => { lock (states) states.Add(state); };
        channel.Broker.Revoke();
        for (var i = 0; i < 100 && agent.IsReceivePumpAlive; i++) await Task.Delay(50);
        Assert.False(agent.IsReceivePumpAlive);
        // The host closing the socket fails the connection, exactly once.
        Assert.Equal("failed", agent.ConnectionState);
        lock (states) Assert.Equal(["failed"], states);
        Assert.Equal(0, channel.Broker.OpenSockets);
        // Nothing reaches the peer after revocation.
        agent.SendDatagram([1, 2, 3]);
        using var timeout = new CancellationTokenSource(300);
        var bytes = new byte[64];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            while (true) await peer.ReceiveFromAsync(bytes, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token);
        });
    }

    [Fact]
    public async Task DisposingTheAgentClosesTheHostSocket()
    {
        using var channel = new Channel();
        using var peer = UdpBrokerTests.Peer();
        var (agent, _) = await ConnectedAgentAsync(channel, peer);
        agent.Dispose();
        for (var i = 0; i < 100 && channel.Broker.OpenSockets != 0; i++) await Task.Delay(10);
        Assert.Equal(0, channel.Broker.OpenSockets);
    }
}
