using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using Folio.Scripting.Ipc;
using Folio.Scripting.Rtc;

namespace Folio.Scripting.Tests;

public sealed class UdpBrokerMdnsTests
{
    private static JsonElement Cmd(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class Harness : IDisposable
    {
        public readonly List<JsonElement> Events = [];
        public readonly UdpBroker Broker;
        public volatile bool Granted = true;

        public Harness(UdpBrokerOptions options)
        {
            Broker = new UdpBroker(options, () => Granted, evt =>
            {
                lock (Events) Events.Add(evt);
            });
        }

        public void Run(string json) =>
            Broker.Handle(Cmd(json.StartsWith('{') ? json.Insert(1, $"\"gen\":{Broker.Generation},") : json));

        public void RunWithGen(long gen, string json) =>
            Broker.Handle(Cmd(json.StartsWith('{') ? json.Insert(1, $"\"gen\":{gen},") : json));

        public JsonElement[] Snapshot() { lock (Events) return [.. Events]; }

        public async Task<JsonElement> WaitFor(Func<JsonElement, bool> match)
        {
            for (var i = 0; i < 500; i++)
            {
                foreach (var evt in Snapshot()) if (match(evt)) return evt;
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
            throw new TimeoutException("Expected broker event did not arrive.");
        }

        public void Dispose() => Broker.Dispose();
    }

    [Theory]
    [InlineData("e3b0c442-98fc-1c14-9afb-f4c8996fb924.local", true)]
    [InlineData("00000000-0000-0000-0000-000000000000.local", true)]
    [InlineData("12345678-abcd-ef01-2345-6789abcdef01.local", true)]
    [InlineData("E3B0C442-98FC-1C14-9AFB-F4C8996FB924.local", false)] // upper case rejected
    [InlineData("e3b0c442-98fc-1c14-9afb-f4c8996fb92.local", false)] // 35 chars
    [InlineData("e3b0c442-98fc-1c14-9afb-f4c8996fb9244.local", false)] // 37 chars
    [InlineData("e3b0c442-98fc-1c14-9afb-f4c8996fb924.com", false)] // non-.local
    [InlineData("foo.local", false)]
    [InlineData("..local", false)]
    [InlineData("e3b0c442-98fc-1c14-9afb-f4c8996fb924.sub.local", false)]
    public void MdnsNameRegexValidatesStrictFormat(string name, bool valid)
    {
        Assert.Equal(valid, MdnsClient.ValidMdnsNameRegex().IsMatch(name));
    }

    [Fact]
    public void BuildQueryProducesCompliantDnsPacket()
    {
        var name = "e3b0c442-98fc-1c14-9afb-f4c8996fb924.local";
        var query = MdnsClient.BuildQuery(name);

        Assert.True(query.Length > 12);
        // ID == 0
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(0, 2)));
        // Flags == 0 (standard query)
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(2, 2)));
        // QDCOUNT == 1
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(4, 2)));
        // ANCOUNT == 0
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(6, 2)));

        // Verify trailing QTYPE = 1 (A) and QCLASS = 0x8001 (unicast response)
        var qtype = BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(query.Length - 4, 2));
        var qclass = BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(query.Length - 2, 2));
        Assert.Equal(1, qtype);
        Assert.Equal(0x8001, qclass);
    }

    [Fact]
    public void TryParseResponseExtractsIpv4Address()
    {
        var name = "e3b0c442-98fc-1c14-9afb-f4c8996fb924.local";
        var query = MdnsClient.BuildQuery(name);

        // Build a mock DNS response from the query
        var packet = new List<byte>(query);
        // Set QR bit in flags (0x8400: response + authoritative)
        packet[2] = 0x84;
        packet[3] = 0x00;
        // ANCOUNT = 1
        packet[6] = 0x00;
        packet[7] = 0x01;

        // Answer: pointer to QNAME (0xC00C), Type A (1), Class IN (1), TTL 120, RDLENGTH 4, RDATA 192.168.1.100
        packet.AddRange([(byte)0xC0, 0x0C]); // Name pointer
        packet.AddRange([0x00, 0x01]); // Type A
        packet.AddRange([0x80, 0x01]); // Class IN with cache-flush
        packet.AddRange([0x00, 0x00, 0x00, 0x78]); // TTL
        packet.AddRange([0x00, 0x04]); // RDLENGTH 4
        packet.AddRange([192, 168, 1, 100]); // IP: 192.168.1.100

        var success = MdnsClient.TryParseResponse(packet.ToArray(), name, out var address);
        Assert.True(success);
        Assert.NotNull(address);
        Assert.Equal(IPAddress.Parse("192.168.1.100"), address);
    }

    [Fact]
    public void TryParseResponseRejectsHostileInputs()
    {
        var name = "e3b0c442-98fc-1c14-9afb-f4c8996fb924.local";

        // Too short (< 12 bytes)
        Assert.False(MdnsClient.TryParseResponse(new byte[11], name, out _));

        // Not a response (QR = 0)
        Assert.False(MdnsClient.TryParseResponse(new byte[12], name, out _));

        // Response but anCount = 0
        var empty = new byte[12];
        empty[2] = 0x80;
        Assert.False(MdnsClient.TryParseResponse(empty, name, out _));

        // Truncated question section
        var truncatedQ = new byte[16];
        truncatedQ[2] = 0x80;
        truncatedQ[5] = 1; // QDCOUNT = 1
        Assert.False(MdnsClient.TryParseResponse(truncatedQ, name, out _));

        // Malformed label length exceeding buffer
        var badLen = new byte[20];
        badLen[2] = 0x80;
        badLen[5] = 1;
        badLen[12] = 100; // label length 100 on 20-byte buffer
        Assert.False(MdnsClient.TryParseResponse(badLen, name, out _));

        // Cyclic pointer loop in name
        var loop = new byte[24];
        loop[2] = 0x80;
        loop[5] = 1;
        loop[12] = 0xC0; loop[13] = 0x0C; // pointer to self
        Assert.False(MdnsClient.TryParseResponse(loop, name, out _));
    }

    [Fact]
    public async Task PolicyDefaultOffRefusesMdnsResolution()
    {
        var options = new UdpBrokerOptions { AllowMdns = false };
        using var h = new Harness(options);

        h.Run("""{"op":"resolveMdns","token":1,"name":"e3b0c442-98fc-1c14-9afb-f4c8996fb924.local"}""");
        var evt = await h.WaitFor(e => e.TryGetProperty("token", out var t) && t.GetInt64() == 1);

        Assert.Equal("resolvedMdns", evt.GetProperty("op").GetString());
        Assert.Equal("mDNS resolution is disabled by host policy", evt.GetProperty("reason").GetString());
        Assert.Null(evt.GetProperty("host").GetString());
    }

    [Fact]
    public async Task InvalidMdnsNameRefusedImmediately()
    {
        var options = new UdpBrokerOptions { AllowMdns = true };
        using var h = new Harness(options);

        h.Run("""{"op":"resolveMdns","token":2,"name":"not-a-uuid.local"}""");
        var evt = await h.WaitFor(e => e.TryGetProperty("token", out var t) && t.GetInt64() == 2);

        Assert.Equal("resolvedMdns", evt.GetProperty("op").GetString());
        Assert.Equal("invalid mDNS name", evt.GetProperty("reason").GetString());
        Assert.Null(evt.GetProperty("host").GetString());
    }

    [Fact]
    public async Task FakeResolverPrivateAddressRequiresAllowPrivate()
    {
        // 1. Without AllowPrivate, private IP is disallowed
        var optsDenied = new UdpBrokerOptions
        {
            AllowMdns = true,
            AllowPrivate = false,
            MdnsResolver = (_, _) => Task.FromResult<IPAddress?>(IPAddress.Parse("192.168.1.55"))
        };
        using (var h = new Harness(optsDenied))
        {
            h.Run("""{"op":"resolveMdns","token":10,"name":"e3b0c442-98fc-1c14-9afb-f4c8996fb924.local"}""");
            var evt = await h.WaitFor(e => e.TryGetProperty("token", out var t) && t.GetInt64() == 10);
            Assert.Equal("the name resolves to a disallowed address", evt.GetProperty("reason").GetString());
            Assert.Null(evt.GetProperty("host").GetString());
        }

        // 2. With AllowPrivate = true, private IP succeeds
        var optsAllowed = new UdpBrokerOptions
        {
            AllowMdns = true,
            AllowPrivate = true,
            MdnsResolver = (_, _) => Task.FromResult<IPAddress?>(IPAddress.Parse("192.168.1.55"))
        };
        using (var h = new Harness(optsAllowed))
        {
            h.Run("""{"op":"resolveMdns","token":11,"name":"e3b0c442-98fc-1c14-9afb-f4c8996fb924.local"}""");
            var evt = await h.WaitFor(e => e.TryGetProperty("token", out var t) && t.GetInt64() == 11);
            Assert.Null(evt.GetProperty("reason").GetString());
            Assert.Equal("192.168.1.55", evt.GetProperty("host").GetString());
        }
    }

    [Fact]
    public async Task QuotaEnforcementCapsDistinctNames()
    {
        var options = new UdpBrokerOptions
        {
            AllowMdns = true,
            AllowPrivate = true,
            MaxMdnsNames = 2,
            MdnsResolver = (_, _) => Task.FromResult<IPAddress?>(IPAddress.Parse("192.168.1.1"))
        };
        using var h = new Harness(options);

        h.Run("""{"op":"resolveMdns","token":21,"name":"00000000-0000-0000-0000-000000000001.local"}""");
        var evt1 = await h.WaitFor(e => e.TryGetProperty("token", out var t) && t.GetInt64() == 21);
        Assert.Equal("192.168.1.1", evt1.GetProperty("host").GetString());

        h.Run("""{"op":"resolveMdns","token":22,"name":"00000000-0000-0000-0000-000000000002.local"}""");
        var evt2 = await h.WaitFor(e => e.TryGetProperty("token", out var t) && t.GetInt64() == 22);
        Assert.Equal("192.168.1.1", evt2.GetProperty("host").GetString());

        // 3rd distinct name exceeds quota of 2
        h.Run("""{"op":"resolveMdns","token":23,"name":"00000000-0000-0000-0000-000000000003.local"}""");
        var evt3 = await h.WaitFor(e => e.TryGetProperty("token", out var t) && t.GetInt64() == 23);
        Assert.Equal("mDNS resolve quota exceeded", evt3.GetProperty("reason").GetString());
        Assert.Null(evt3.GetProperty("host").GetString());
    }

    [Fact]
    public async Task StaleGenerationReturnsDocumentReplaced()
    {
        var options = new UdpBrokerOptions { AllowMdns = true };
        using var h = new Harness(options);

        // Command with wrong generation (e.g. Generation + 99)
        h.RunWithGen(h.Broker.Generation + 99, """{"op":"resolveMdns","token":30,"name":"e3b0c442-98fc-1c14-9afb-f4c8996fb924.local"}""");
        var evt = await h.WaitFor(e => e.TryGetProperty("token", out var t) && t.GetInt64() == 30);

        Assert.Equal("resolvedMdns", evt.GetProperty("op").GetString());
        Assert.Equal("the document was replaced", evt.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task IceAgentResolvesMdnsCandidateAndFormsPairs()
    {
        var fakeTransport = new FakeMdnsTransport(IPAddress.Parse("192.168.1.88"));
        using var agent = new IceAgent(transport: fakeTransport);

        // Start gathering to populate local candidates
        await agent.StartGatheringAsync();
        Assert.NotEmpty(agent.LocalCandidates);

        // Add remote .local candidate
        var remoteMdns = new IceCandidate
        {
            Foundation = "2",
            Component = 1,
            Protocol = "udp",
            Priority = IceCandidate.ComputePriority("host", 1),
            Address = "e3b0c442-98fc-1c14-9afb-f4c8996fb924.local",
            Port = 50001,
            Type = "host"
        };
        agent.AddRemoteCandidate(remoteMdns);

        // Poll for resolved candidate to be added
        for (var i = 0; i < 300; i++)
        {
            if (fakeTransport.ResolvedRequested) break;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.True(fakeTransport.ResolvedRequested);

        // Allow resolution callback to process
        var expectedEp = new IPEndPoint(IPAddress.Parse("192.168.1.88"), 50001);
        for (var i = 0; i < 300; i++)
        {
            if (agent.RemoteCandidates.Any(c => c.Address == "192.168.1.88")) break;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Contains(agent.RemoteCandidates, c => c.Address == "192.168.1.88" && c.Port == 50001);
        Assert.True(agent.HasCandidatePair(expectedEp));
    }

    [Fact]
    public void RtcSdpParsesHostnameCandidateInBundledSdp()
    {
        var sdpText = """
            v=0
            o=- 1234567890 2 IN IP4 127.0.0.1
            s=-
            t=0 0
            a=group:BUNDLE 0
            m=audio 9 UDP/TLS/RTP/SAVPF 111
            c=IN IP4 0.0.0.0
            a=mid:0
            a=candidate:1 1 udp 2122260223 e3b0c442-98fc-1c14-9afb-f4c8996fb924.local 54321 typ host
            a=rtcp-mux
            """;

        var sdp = RtcSdp.Parse(sdpText);
        Assert.Single(sdp.TransportCandidates);
        var cand = sdp.TransportCandidates[0];
        Assert.Equal("e3b0c442-98fc-1c14-9afb-f4c8996fb924.local", cand.Address);
        Assert.Equal(54321, cand.Port);
        Assert.Equal("host", cand.Type);
    }

    private sealed class FakeMdnsTransport(IPAddress resolvedIp) : IIceTransport
    {
        public bool ResolvedRequested { get; private set; }

        public IPEndPoint Bind() => new(IPAddress.Any, 50000);
        public void SendTo(ReadOnlySpan<byte> datagram, IPEndPoint destination) { }
        public int Receive(byte[] buffer, int timeoutMicroseconds, ref EndPoint remote) => -1;

        public Task<IPAddress?> ResolveMdnsAsync(string host, CancellationToken cancellation)
        {
            ResolvedRequested = true;
            return Task.FromResult<IPAddress?>(resolvedIp);
        }

        public void Dispose() { }
    }
}
