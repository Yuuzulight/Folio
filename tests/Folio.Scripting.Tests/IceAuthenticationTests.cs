using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Folio.Scripting.Rtc;

namespace Folio.Scripting.Tests;

public sealed class IceAuthenticationTests
{
    [Fact]
    public async Task RtcTransportDoesNotNeedAnAvailableClrThreadPoolThread()
    {
        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(Program).Assembly.Location); start.ArgumentList.Add("rtc-thread-pool-probe");
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            Assert.True(process.ExitCode == 0, $"RTC pool probe {process.ExitCode}: {await output} {await errors}");
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }

    [Fact]
    public async Task DroppedConnectivityCheckRetriesTheSameAuthenticatedTransaction()
    {
        using var agent = new IceAgent { RemoteUfrag = "remote", RemotePassword = "remote-password" };
        await agent.StartGatheringAsync();
        using var peer = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        peer.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.OnConnectionStateChanged += state => { if (state == "connected") connected.TrySetResult(); };
        agent.AddRemoteCandidate(new IceCandidate
        {
            Foundation = "retry", Component = 1, Protocol = "udp", Priority = 100,
            Address = "127.0.0.1", Port = ((IPEndPoint)peer.LocalEndPoint!).Port, Type = "host"
        });
        var bytes = new byte[2048];
        var first = await peer.ReceiveFromAsync(bytes, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), TestContext.Current.CancellationToken);
        Assert.True(StunMessage.TryParse(bytes.AsSpan(0, first.ReceivedBytes), out var firstMessage));
        var initial = bytes.AsSpan(0, first.ReceivedBytes).ToArray(); // Drop this request.
        var second = await peer.ReceiveFromAsync(bytes, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), TestContext.Current.CancellationToken);
        Assert.Equal(initial, bytes.AsSpan(0, second.ReceivedBytes).ToArray());
        var reply = StunMessage.Encode(StunMessage.BindingResponse, firstMessage.TransactionId.Span, [], Encoding.UTF8.GetBytes("remote-password"));
        await peer.SendToAsync(reply, SocketFlags.None, second.RemoteEndPoint);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal("connected", agent.ConnectionState);
    }

    [Fact]
    public async Task AuthenticatedPeerReflexiveNominationWorksWithoutAnIpCandidateInSdp()
    {
        using var agent = new IceAgent { IsControlling = false, RemoteUfrag = "remote", RemotePassword = "remote-password" };
        await agent.StartGatheringAsync();
        using var peer = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        peer.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var target = new IPEndPoint(IPAddress.Loopback, agent.LocalCandidates[0].Port);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.OnConnectionStateChanged += state => { if (state == "connected") connected.TrySetResult(); };
        byte[] priority = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(priority, 1234);
        List<(ushort, byte[])> Attributes(string username) =>
        [
            (StunMessage.Username, Encoding.UTF8.GetBytes(username)), (StunMessage.Priority, priority),
            (StunMessage.IceControlling, new byte[8]), (StunMessage.UseCandidate, [])
        ];
        var key = Encoding.UTF8.GetBytes(agent.LocalPassword);
        // A correct HMAC alone cannot authorize another ICE username/session.
        await peer.SendToAsync(StunMessage.Encode(StunMessage.BindingRequest, RandomNumberGenerator.GetBytes(12),
            Attributes("wrong:remote"), key), SocketFlags.None, target);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(connected.Task.IsCompleted);
        await peer.SendToAsync(StunMessage.Encode(StunMessage.BindingRequest, RandomNumberGenerator.GetBytes(12),
            Attributes(agent.LocalUfrag + ":remote"), Encoding.UTF8.GetBytes("wrong-key")), SocketFlags.None, target);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(connected.Task.IsCompleted);
        await peer.SendToAsync(StunMessage.Encode(StunMessage.BindingRequest, RandomNumberGenerator.GetBytes(12),
            Attributes(agent.LocalUfrag + ":remote"), key), SocketFlags.None, target);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal("connected", agent.ConnectionState);

        var datagram = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.OnDatagramReceived += bytes => datagram.TrySetResult(bytes);
        using var stranger = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        stranger.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        await stranger.SendToAsync(new byte[] { 99 }, SocketFlags.None, target);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(datagram.Task.IsCompleted);
        await peer.SendToAsync(new byte[] { 23, 254, 253 }, SocketFlags.None, target);
        Assert.Equal(new byte[] { 23, 254, 253 }, await datagram.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConnectivityResponseRequiresTheExpectedEndpointAndPeerIntegrity()
    {
        using var agent = new IceAgent { RemoteUfrag = "remote", RemotePassword = "remote-password" };
        await agent.StartGatheringAsync();
        using var peer = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        peer.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        agent.AddRemoteCandidate(new IceCandidate
        {
            Foundation = "test", Component = 1, Protocol = "udp", Priority = 100,
            Address = "127.0.0.1", Port = ((IPEndPoint)peer.LocalEndPoint!).Port, Type = "host"
        });
        var bytes = new byte[2048];
        var request = await peer.ReceiveFromAsync(bytes, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), TestContext.Current.CancellationToken);
        Assert.True(StunMessage.TryParse(bytes.AsSpan(0, request.ReceivedBytes), out var stun));
        var transaction = stun.TransactionId.ToArray();
        var good = StunMessage.Encode(StunMessage.BindingResponse, transaction, [], Encoding.UTF8.GetBytes("remote-password"));
        using var stranger = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        stranger.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        await stranger.SendToAsync(good, SocketFlags.None, request.RemoteEndPoint);
        await peer.SendToAsync(StunMessage.Encode(StunMessage.BindingResponse, transaction, [], Encoding.UTF8.GetBytes("wrong-key")), SocketFlags.None, request.RemoteEndPoint);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.NotEqual("connected", agent.ConnectionState);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.OnConnectionStateChanged += state => { if (state == "connected") connected.TrySetResult(); };
        var immediateData = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.OnDatagramReceived += data => immediateData.TrySetResult(data);
        await peer.SendToAsync(good, SocketFlags.None, request.RemoteEndPoint);
        // Real peers may send their first DTLS flight directly after the ICE
        // response. Endpoint selection must precede that socket delivery.
        await peer.SendToAsync(new byte[] { 22, 254, 253 }, SocketFlags.None, request.RemoteEndPoint);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 22, 254, 253 }, await immediateData.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        Assert.Equal("connected", agent.ConnectionState);
    }
}
