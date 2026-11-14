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
    [InlineData("e3b0c442-98fc-1c14-9afb-f4c8996fb924.local\n", false)] // trailing newline
    [InlineData("------------------------------------.local", false)] // 36 dashes
    [InlineData("e3b0c442-98fc-1c14-9afb-f4c8996fb924-.local", false)]
    [InlineData("e3b0c4-4298-fc1c-149a-fbf4c8996fb924.local", false)] // misplaced dashes
    [InlineData("e3b0c44298fc1c149afbf4c8996fb9240000.local", false)] // no dashes
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

    private const string Name = "e3b0c442-98fc-1c14-9afb-f4c8996fb924.local";

    private static byte[] EncodeName(string name)
    {
        var bytes = new List<byte>();
        foreach (var label in name.Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }
        bytes.Add(0);
        return [.. bytes];
    }

    private static byte[] Ans(byte[] owner, ushort type, byte[] rdata, int rdLength = -1)
    {
        var b = new List<byte>(owner) { (byte)(type >> 8), (byte)type, 0x80, 0x01, 0, 0, 0, 120 };
        var len = rdLength < 0 ? rdata.Length : rdLength;
        b.Add((byte)(len >> 8)); b.Add((byte)len);
        b.AddRange(rdata);
        return [.. b];
    }

    private static readonly byte[] Ptr = [0xC0, 0x0C];

    /// <summary>A well-formed legacy-unicast reply echoing the question, with the given ANCOUNT.</summary>
    private static byte[] Reply(string question, int ancount, params byte[][] answers)
    {
        var b = new List<byte> { 0, 0, 0x84, 0, 0, 1, (byte)(ancount >> 8), (byte)ancount, 0, 0, 0, 0 };
        b.AddRange(EncodeName(question));
        b.AddRange([0, 1, 0x80, 1]);
        foreach (var a in answers) b.AddRange(a);
        return [.. b];
    }

    private static byte[] A(byte[] owner, params byte[] ip) => Ans(owner, 1, ip);

    [Fact]
    public void TryParseResponseRejectsHostileInputs()
    {
        // Sanity: the builders produce an accepted reply.
        var good = Reply(Name, 1, A(Ptr, 192, 168, 1, 5));
        Assert.True(MdnsClient.TryParseResponse(good, Name, out var ok));
        Assert.Equal(IPAddress.Parse("192.168.1.5"), ok);

        byte[] With(int index, byte value) { var c = (byte[])good.Clone(); c[index] = value; return c; }

        // Header
        Assert.False(MdnsClient.TryParseResponse(new byte[11], Name, out _));
        Assert.False(MdnsClient.TryParseResponse(With(2, 0x04), Name, out _)); // QR clear
        Assert.False(MdnsClient.TryParseResponse(With(2, 0x8C), Name, out _)); // opcode 1
        Assert.False(MdnsClient.TryParseResponse(With(3, 0x03), Name, out _)); // rcode NXDOMAIN
        Assert.False(MdnsClient.TryParseResponse(With(1, 0x01), Name, out _)); // ID != 0
        Assert.False(MdnsClient.TryParseResponse(With(5, 0), Name, out _)); // QDCOUNT 0 (no question echo)
        Assert.False(MdnsClient.TryParseResponse(Reply(Name, 0), Name, out _)); // ANCOUNT 0

        // Question echo mismatch
        Assert.False(MdnsClient.TryParseResponse(Reply("00000000-0000-0000-0000-000000000000.local", 1, A(Ptr, 192, 168, 1, 5)), Name, out _));

        // Answer owned by a different name
        var other = EncodeName("00000000-0000-0000-0000-000000000000.local");
        Assert.False(MdnsClient.TryParseResponse(Reply(Name, 1, A(other, 192, 168, 1, 5)), Name, out _));

        // A after a CNAME: the A is owned by the CNAME target, which is not the queried name
        var cname = Ans(Ptr, 5, other);
        Assert.False(MdnsClient.TryParseResponse(Reply(Name, 2, cname, A(other, 192, 168, 1, 5)), Name, out _));
        // ...but an A for the queried name after an unrelated record is still found
        Assert.True(MdnsClient.TryParseResponse(Reply(Name, 2, cname, A(Ptr, 192, 168, 1, 6)), Name, out var afterCname));
        Assert.Equal(IPAddress.Parse("192.168.1.6"), afterCname);

        // Unusable addresses
        Assert.False(MdnsClient.TryParseResponse(Reply(Name, 1, A(Ptr, 224, 0, 0, 251)), Name, out _)); // multicast
        Assert.False(MdnsClient.TryParseResponse(Reply(Name, 1, A(Ptr, 127, 0, 0, 1)), Name, out _)); // loopback
        Assert.False(MdnsClient.TryParseResponse(Reply(Name, 1, A(Ptr, 0, 0, 0, 0)), Name, out _)); // unspecified
        // First usable matching A wins over an unusable one before it
        Assert.True(MdnsClient.TryParseResponse(Reply(Name, 2, A(Ptr, 127, 0, 0, 1), A(Ptr, 10, 0, 0, 7)), Name, out var second));
        Assert.Equal(IPAddress.Parse("10.0.0.7"), second);

        // Truncated rdata (RDLENGTH 4 but only 2 bytes present)
        Assert.False(MdnsClient.TryParseResponse(Reply(Name, 1, Ans(Ptr, 1, [192, 168], 4)), Name, out _));
        // Wrong rdata length for an A record
        Assert.False(MdnsClient.TryParseResponse(Reply(Name, 1, Ans(Ptr, 1, [192, 168, 1])), Name, out _));

        // Truncated question section
        Assert.False(MdnsClient.TryParseResponse(good.AsSpan(0, 20), Name, out _));

        // Bad label length (reserved 0x80 prefix, and a length that runs past the buffer)
        Assert.False(MdnsClient.TryParseResponse(Reply(Name, 1, A([0x80, 0x01, 0], 192, 168, 1, 5)), Name, out _));
        Assert.False(MdnsClient.TryParseResponse(Reply(Name, 1, A([60, 1, 2, 3], 192, 168, 1, 5)), Name, out _));

        // Pointer loops: an owner pointing at itself, and a two-pointer cycle
        var ownerAt = Reply(Name, 0).Length; // first answer's owner starts here
        var selfLoop = Reply(Name, 1, A([0xC0, (byte)ownerAt], 192, 168, 1, 5));
        Assert.False(MdnsClient.TryParseResponse(selfLoop, Name, out _));
        var cycle = Reply(Name, 1, A([0xC0, (byte)(ownerAt + 2), 0xC0, (byte)ownerAt], 192, 168, 1, 5));
        Assert.False(MdnsClient.TryParseResponse(cycle, Name, out _));

        // Case-insensitive owner comparison
        Assert.True(MdnsClient.TryParseResponse(Reply(Name, 1, A(EncodeName(Name.ToUpperInvariant()), 192, 168, 1, 5)), Name, out _));
    }

    [Theory]
    [InlineData("192.168.1.9", 5353, true)]
    [InlineData("10.1.2.3", 5353, true)]
    [InlineData("172.16.0.1", 5353, true)]
    [InlineData("169.254.1.1", 5353, true)]
    [InlineData("192.168.1.9", 5354, false)] // wrong source port
    [InlineData("192.168.1.9", 40000, false)]
    [InlineData("8.8.8.8", 5353, false)] // off-link
    [InlineData("127.0.0.1", 5353, false)]
    public void ReplySourceMustBeLanPort5353(string ip, int port, bool expected) =>
        Assert.Equal(expected, MdnsClient.IsAcceptableSource(new IPEndPoint(IPAddress.Parse(ip), port)));

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
    public async Task RepeatedNameDoesNotCountAgainstQuotaAndBeginDocumentCancelsLookup()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new UdpBrokerOptions
        {
            AllowMdns = true,
            MaxMdnsNames = 1,
            MdnsTimeout = TimeSpan.FromSeconds(30),
            MdnsResolver = async (_, token) =>
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
                return null;
            }
        };
        using var h = new Harness(options);
        h.Run("""{"op":"resolveMdns","token":40,"name":"00000000-0000-0000-0000-000000000001.local"}""");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        // The same name again is not refused by the name quota (only the rate limiter and in-flight cap apply).
        h.Run("""{"op":"resolveMdns","token":41,"name":"00000000-0000-0000-0000-000000000001.local"}""");
        h.Broker.BeginDocument(h.Broker.Generation + 1);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.DoesNotContain(h.Snapshot(), e => e.TryGetProperty("reason", out var r) && r.GetString() == "mDNS resolve quota exceeded");
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
        public int Receive(byte[] buffer, int timeoutMicroseconds, ref EndPoint remote)
        {
            // Behave like a quiet socket: wait out the timeout so the receive loop never spins.
            Thread.Sleep(Math.Min(timeoutMicroseconds / 1000, 25));
            return -1;
        }

        public Task<IPAddress?> ResolveMdnsAsync(string host, CancellationToken cancellation)
        {
            ResolvedRequested = true;
            return Task.FromResult<IPAddress?>(resolvedIp);
        }

        public void Dispose() { }
    }
}
