using System.Net;
using System.Security.Cryptography;
using System.Text;
using Folio.Scripting.Rtc;

namespace Folio.Scripting.Tests;

public sealed class RemoteWebRtcTests
{
    [Fact]
    public void DetectsSecureRenegotiationScsvWithoutReadingOutsideClientHello()
    {
        var hello = new byte[44];
        hello[0] = 0xfe; hello[1] = 0xfd;
        hello[37] = 4; hello[38] = 0xc0; hello[39] = 0x2b;
        hello[41] = 0xff; hello[42] = 1;
        Assert.True(DtlsTransport.OffersSecureRenegotiation(hello));
        for (var length = 0; length < hello.Length; length++)
            Assert.False(DtlsTransport.OffersSecureRenegotiation(hello.AsSpan(0, length)));
        hello[41] = 0;
        Assert.False(DtlsTransport.OffersSecureRenegotiation(hello));
    }

    [Fact]
    public void CertificateRequestNegotiatesSupportedAlgorithmAndBoundsVariableLists()
    {
        byte[] request = [2, 1, 64, 0, 4, 4, 1, 4, 3, 0, 5, 0, 3, 1, 2, 3];
        Assert.True(DtlsTransport.SupportsCertificateRequest(request));
        for (var length = 0; length < request.Length; length++)
            Assert.False(DtlsTransport.SupportsCertificateRequest(request.AsSpan(0, length)));
        var unsupported = (byte[])request.Clone(); unsupported[8] = 1;
        Assert.False(DtlsTransport.SupportsCertificateRequest(unsupported));
        var malformed = (byte[])request.Clone(); malformed[12] = 4;
        Assert.False(DtlsTransport.SupportsCertificateRequest(malformed));
    }

    [Fact]
    public void AnswersLegacyVersionCookieChallengeWithRetriedClientHello()
    {
        using var transport = new DtlsTransport();
        var sent = new List<byte[]>(); transport.OnDatagramToSend += sent.Add;
        transport.StartHandshake();
        var challenge = new byte[31];
        challenge[0] = 22; challenge[1] = 0xfe; challenge[2] = 0xff;
        challenge[12] = 18; challenge[13] = 3; challenge[16] = 6; challenge[24] = 6;
        challenge[25] = 0xfe; challenge[26] = 0xfd; challenge[27] = 3;
        challenge[28] = 10; challenge[29] = 20; challenge[30] = 30;
        transport.ProcessIncomingDatagram(challenge);
        Assert.Equal(2, sent.Count);
        var retry = sent[1];
        Assert.Equal(1, retry[18]); // handshake message sequence
        Assert.Equal(3, retry[60]); // ClientHello cookie length
        Assert.Equal(new byte[] { 10, 20, 30 }, retry[61..64]);
        transport.ProcessIncomingDatagram(challenge);
        Assert.Equal(2, sent.Count); // old challenge cannot restart the transcript
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void PinnedServerHandshakeExchangesEncryptedApplicationData(bool corruptClientFinished, bool corruptClientProof, bool fragmentCertificate)
    {
        using var client = new DtlsTransport();
        using var server = new DtlsTransport { IsClient = false };
        client.RemoteFingerprint = server.Fingerprint;
        server.RemoteFingerprint = client.Fingerprint;
        var pending = new Queue<(DtlsTransport Target, byte[] Record)>();
        client.OnDatagramToSend += bytes =>
        {
            if (corruptClientFinished && bytes[0] == 22 && bytes[4] == 1)
                bytes[^1] ^= 1;
            if (corruptClientProof && bytes[0] == 22 && bytes[4] == 0 && bytes[13] == 15)
                bytes[^1] ^= 1;
            pending.Enqueue((server, bytes));
        };
        server.OnDatagramToSend += bytes =>
        {
            if (!fragmentCertificate || bytes[0] != 22 || bytes[4] != 0 || bytes[13] != 11)
            { pending.Enqueue((client, bytes)); return; }
            var length = bytes.Length - 25;
            var split = length / 2;
            // Deliver the second certificate fragment first.
            foreach (var (offset, count) in new[] { (split, length - split), (0, split) })
            {
                var fragment = new byte[25 + count];
                Array.Copy(bytes, fragment, 25);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(fragment.AsSpan(11), (ushort)(12 + count));
                fragment[19] = (byte)(offset >> 16); fragment[20] = (byte)(offset >> 8); fragment[21] = (byte)offset;
                fragment[22] = (byte)(count >> 16); fragment[23] = (byte)(count >> 8); fragment[24] = (byte)count;
                Array.Copy(bytes, 25 + offset, fragment, 25, count);
                pending.Enqueue((client, fragment));
            }
        };
        void Drain()
        {
            var count = 0;
            while (pending.TryDequeue(out var message))
            {
                Assert.True(++count < 100);
                message.Target.ProcessIncomingDatagram(message.Record);
            }
        }
        server.StartHandshake(); client.StartHandshake();
        if (corruptClientProof)
        {
            Assert.Throws<CryptographicException>(Drain);
            Assert.NotEqual("connected", client.State);
            Assert.NotEqual("connected", server.State);
            return;
        }
        Drain();
        if (corruptClientFinished)
        {
            Assert.Equal("connecting", client.State);
            Assert.Equal("connecting", server.State);
            Assert.Throws<InvalidOperationException>(() => client.SendApplicationData([1]));
            Assert.Throws<InvalidOperationException>(() => server.SendApplicationData([1]));
            return;
        }
        Assert.Equal("connected", client.State);
        Assert.Equal("connected", server.State);
        Assert.Equal(client.SrtpLocalMasterKey, server.SrtpRemoteMasterKey);
        Assert.Equal(client.SrtpRemoteMasterKey, server.SrtpLocalMasterKey);
        var received = new List<byte[]>();
        server.OnApplicationDataReceived += received.Add;
        client.SendApplicationData([1, 2, 3]);
        var applicationRecord = pending.Peek().Record;
        Drain(); server.ProcessIncomingDatagram(applicationRecord);
        Assert.Single(received);
        Assert.Equal(new byte[] { 1, 2, 3 }, received[0]);
    }

    [Fact]
    public void ServerKeyExchangeAuthenticatesParametersAndBothRandoms()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var exchange = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var clientRandom = RandomNumberGenerator.GetBytes(32);
        var serverRandom = RandomNumberGenerator.GetBytes(32);
        var body = SignedExchange(signer, exchange, clientRandom, serverRandom);
        using var peer = DtlsTransport.VerifyServerKeyExchange(body, signer, clientRandom, serverRandom);
        Assert.Equal(exchange.ExportParameters(false).Q.X, peer.ExportParameters(false).Q.X);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<CryptographicException>(() => DtlsTransport.VerifyServerKeyExchange(body, otherKey, clientRandom, serverRandom));
        clientRandom[0] ^= 1;
        Assert.Throws<CryptographicException>(() => DtlsTransport.VerifyServerKeyExchange(body, signer, clientRandom, serverRandom));
        clientRandom[0] ^= 1;
        serverRandom[0] ^= 1;
        Assert.Throws<CryptographicException>(() => DtlsTransport.VerifyServerKeyExchange(body, signer, clientRandom, serverRandom));
        serverRandom[0] ^= 1;
        body[20] ^= 1;
        Assert.Throws<CryptographicException>(() => DtlsTransport.VerifyServerKeyExchange(body, signer, clientRandom, serverRandom));
    }

    [Fact]
    public void ServerKeyExchangeRejectsMalformedSignatureAndPoint()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var exchange = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var random = new byte[32];
        var body = SignedExchange(signer, exchange, random, random);
        body[^1] ^= 1;
        Assert.Throws<CryptographicException>(() => DtlsTransport.VerifyServerKeyExchange(body, signer, random, random));
        Assert.Throws<CryptographicException>(() => DtlsTransport.VerifyServerKeyExchange(body[..^1], signer, random, random));
        Assert.Throws<CryptographicException>(() => DtlsTransport.ImportKeyExchangePoint(exchange.ExportSubjectPublicKeyInfo()));
        Assert.Throws<CryptographicException>(() => DtlsTransport.ImportKeyExchangePoint(new byte[65]));
    }

    private static byte[] SignedExchange(ECDsa signer, ECDiffieHellman exchange, byte[] clientRandom, byte[] serverRandom)
    {
        var parameters = new byte[69];
        parameters[0] = 3; parameters[2] = 23; parameters[3] = 65;
        DtlsTransport.ExportKeyExchangePoint(exchange).CopyTo(parameters, 4);
        var signed = clientRandom.Concat(serverRandom).Concat(parameters).ToArray();
        var signature = signer.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return parameters.Concat(new byte[] { 4, 3, (byte)(signature.Length >> 8), (byte)signature.Length }).Concat(signature).ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnauthenticatedFinishedCannotConnect(bool started)
    {
        using var transport = new DtlsTransport();
        if (started) transport.StartHandshake();
        var record = new byte[25];
        record[0] = 22; // handshake record
        record[1] = 0xfe; record[2] = 0xfd;
        record[12] = 12; // record payload length
        record[13] = 20; // empty Finished handshake
        transport.ProcessIncomingDatagram(record);
        Assert.NotEqual("connected", transport.State);
    }

    [Fact]
    public void ApplicationDataRequiresAnAuthenticatedConnection()
    {
        using var transport = new DtlsTransport();
        var delivered = false;
        var sent = false;
        transport.OnApplicationDataReceived += _ => delivered = true;
        transport.OnDatagramToSend += _ => sent = true;
        var record = new byte[14];
        record[0] = 23; record[1] = 0xfe; record[2] = 0xfd;
        record[12] = 1; record[13] = 42;
        transport.ProcessIncomingDatagram(record);
        Assert.False(delivered);
        Assert.Throws<InvalidOperationException>(() => transport.SendApplicationData([42]));
        Assert.False(sent);
    }

    [Fact]
    public void IceCandidateParsesAndFormatsSdpCorrectly()
    {
        var raw = "candidate:1 1 udp 2130706431 127.0.0.1 54321 typ host ufrag testUfrag";
        var parsed = IceCandidate.TryParse(raw, out var candidate);
        Assert.True(parsed);
        Assert.NotNull(candidate);
        Assert.Equal("1", candidate.Foundation);
        Assert.Equal(1, candidate.Component);
        Assert.Equal("udp", candidate.Protocol);
        Assert.Equal(2130706431, candidate.Priority);
        Assert.Equal("127.0.0.1", candidate.Address);
        Assert.Equal(54321, candidate.Port);
        Assert.Equal("host", candidate.Type);
        Assert.Equal("testUfrag", candidate.UsernameFragment);

        var sdp = candidate.ToSdp();
        Assert.Contains("candidate:1 1 udp 2130706431 127.0.0.1 54321 typ host", sdp);
    }

    [Fact]
    public void IceCandidatePriorityFollowsRfc5245()
    {
        var hostPriority = IceCandidate.ComputePriority("host", 1);
        var srflxPriority = IceCandidate.ComputePriority("srflx", 1);
        var relayPriority = IceCandidate.ComputePriority("relay", 1);

        Assert.True(hostPriority > srflxPriority);
        Assert.True(srflxPriority > relayPriority);
    }

    [Fact]
    public void StunMessageEncodesAndParsesUnauthenticatedBindingRequest()
    {
        var txId = new byte[12];
        RandomNumberGenerator.Fill(txId);

        var wire = StunMessage.EncodeRequest(StunMessage.BindingRequest, txId);
        var parsed = StunMessage.TryParse(wire, out var msg);

        Assert.True(parsed);
        Assert.NotNull(msg);
        Assert.Equal(StunMessage.BindingRequest, msg.Type);
        Assert.True(msg.TransactionId.Span.SequenceEqual(txId));
        Assert.True(msg.VerifyFingerprint());
    }

    [Fact]
    public void DtlsTransportGeneratesValidSha256Fingerprint()
    {
        using var dtls = new DtlsTransport();
        Assert.NotNull(dtls.Fingerprint);
        Assert.Matches(@"^([0-9A-F]{2}:){31}[0-9A-F]{2}$", dtls.Fingerprint);
    }

    [Fact]
    public void SrtpProtectsAndUnprotectsPacketsWithAntiReplayWindow()
    {
        var localKey = new byte[16];
        var localSalt = new byte[14];
        var remoteKey = new byte[16];
        var remoteSalt = new byte[14];
        RandomNumberGenerator.Fill(localKey);
        RandomNumberGenerator.Fill(localSalt);
        RandomNumberGenerator.Fill(remoteKey);
        RandomNumberGenerator.Fill(remoteSalt);

        using var sender = new SrtpTransport(localKey, localSalt, remoteKey, remoteSalt);
        using var receiver = new SrtpTransport(remoteKey, remoteSalt, localKey, localSalt);

        // Build a mock RTP packet: V=2, PT=96, Seq=100, Timestamp=1000, SSRC=0x12345678, Payload="hello audio"
        var rtp = new byte[12 + 11];
        rtp[0] = 0x80; // V=2, P=0, X=0, CC=0
        rtp[1] = 96;   // M=0, PT=96
        rtp[2] = 0; rtp[3] = 100; // Seq = 100
        rtp[4] = 0; rtp[5] = 0; rtp[6] = 3; rtp[7] = 232; // Timestamp = 1000
        rtp[8] = 0x12; rtp[9] = 0x34; rtp[10] = 0x56; rtp[11] = 0x78; // SSRC
        Encoding.UTF8.GetBytes("hello audio").CopyTo(rtp.AsSpan(12));

        var protectedPacket = sender.ProtectRtp(rtp);
        Assert.Equal(rtp.Length + 16, protectedPacket.Length); // 16-byte auth tag added

        // Receiver unprotects
        var unprotected = receiver.TryUnprotectRtp(protectedPacket, out var decrypted);
        Assert.True(unprotected);
        Assert.NotNull(decrypted);
        Assert.Equal(rtp, decrypted);

        // Replay of the exact same packet must be rejected by 64-packet sliding window
        var replay = receiver.TryUnprotectRtp(protectedPacket, out _);
        Assert.False(replay);
    }

    [Fact]
    public void SctpDataChannelsNegotiateAndExchangeMessages()
    {
        using var client = new SctpDataChannel();
        using var server = new SctpDataChannel();

        // Connect client to server packet streams
        client.OnPacketToSend += pkt => server.ProcessIncomingPacket(pkt);
        server.OnPacketToSend += pkt => client.ProcessIncomingPacket(pkt);

        server.StartAssociation(isClient: false);
        client.StartAssociation(isClient: true);

        Assert.Equal("connected", client.State);
        Assert.Equal("connected", server.State);

        SctpDataChannel.ChannelState? remoteChannel = null;
        server.OnDataChannelCreated += ch => remoteChannel = ch;

        var localChannel = client.CreateDataChannel("chat");
        Assert.NotNull(remoteChannel);
        Assert.Equal("chat", remoteChannel.Label);
        Assert.Equal("open", remoteChannel.ReadyState);

        string? receivedOnServer = null;
        remoteChannel.OnMessage += msg => receivedOnServer = msg as string;

        localChannel.Send("ping from client");
        Assert.Equal("ping from client", receivedOnServer);

        string? receivedOnClient = null;
        localChannel.OnMessage += msg => receivedOnClient = msg as string;

        remoteChannel.Send("pong from server");
        Assert.Equal("pong from server", receivedOnClient);
    }

    [Fact]
    public void RtcSdpRoundtripsOffersAndAnswersWithCandidates()
    {
        var sdp = new RtcSdp
        {
            Type = "offer",
            IceUfrag = "abc12345",
            IcePassword = "password1234567890123456",
            FingerprintValue = "AA:BB:CC:DD:EE:FF:11:22:33:44:55:66:77:88:99:00:AA:BB:CC:DD:EE:FF:11:22:33:44:55:66:77:88:99:00",
            Setup = "actpass"
        };
        sdp.Media.Add(new RtcMediaDescription
        {
            Kind = "application",
            Port = 9,
            Protocol = "UDP/DTLS/SCTP",
            Mid = "0",
            SctpPort = 5000
        });
        sdp.Candidates.Add(new IceCandidate
        {
            Foundation = "1",
            Component = 1,
            Protocol = "udp",
            Priority = 2130706431,
            Address = "127.0.0.1",
            Port = 49152,
            Type = "host"
        });

        var text = sdp.ToSdp();
        Assert.Contains("v=0", text);
        Assert.Contains("a=ice-ufrag:abc12345", text);
        Assert.Contains("a=fingerprint:sha-256 AA:BB:CC", text);
        Assert.Contains("m=application 9 UDP/DTLS/SCTP", text);
        Assert.Contains("a=candidate:1 1 udp 2130706431 127.0.0.1 49152 typ host", text);

        var parsed = RtcSdp.Parse(text);
        Assert.Equal("abc12345", parsed.IceUfrag);
        Assert.Equal("password1234567890123456", parsed.IcePassword);
        Assert.Equal("AA:BB:CC:DD:EE:FF:11:22:33:44:55:66:77:88:99:00:AA:BB:CC:DD:EE:FF:11:22:33:44:55:66:77:88:99:00", parsed.FingerprintValue);
        Assert.Single(parsed.Media);
        Assert.Equal("application", parsed.Media[0].Kind);
        Assert.Single(parsed.Candidates);
        Assert.Equal("127.0.0.1", parsed.Candidates[0].Address);
        Assert.Equal(49152, parsed.Candidates[0].Port);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoControllersPerformCandidateExchangeAndConnect(bool passiveAnswer)
    {
        using var a = new RtcPeerConnectionController();
        using var b = new RtcPeerConnectionController();

        var offerSdp = await a.CreateOfferAsync(hasDataChannels: true, audioSenders: 0, videoSenders: 0);
        a.SetLocalDescription("offer", offerSdp);
        b.SetRemoteDescription("offer", offerSdp);

        var answerSdp = await b.CreateAnswerAsync(hasDataChannels: true, audioSenders: 0, videoSenders: 0);
        if (passiveAnswer) answerSdp = answerSdp.Replace("a=setup:active", "a=setup:passive", StringComparison.Ordinal);
        b.SetLocalDescription("answer", answerSdp);
        a.SetRemoteDescription("answer", answerSdp);

        // Exchange gathered candidates
        foreach (var c in a.Ice.LocalCandidates) b.AddIceCandidate(c.ToSdp());
        foreach (var c in b.Ice.LocalCandidates) a.AddIceCandidate(c.ToSdp());

        Assert.Equal(passiveAnswer, a.Dtls.IsClient);
        Assert.Equal(!passiveAnswer, b.Dtls.IsClient);
        Assert.True(a.Ice.IsControlling);
        Assert.False(b.Ice.IsControlling);
        // Require real DTLS and SCTP progress, rather than treating ICE as proof
        // that the authenticated transport and data association are ready.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline && (a.Sctp.State != "connected" || b.Sctp.State != "connected"))
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.Equal("connected", a.IceConnectionState);
        Assert.Equal("connected", b.IceConnectionState);
        Assert.Equal("connected", a.Dtls.State);
        Assert.Equal("connected", b.Dtls.State);
        Assert.Equal("connected", a.Sctp.State);
        Assert.Equal("connected", b.Sctp.State);

        var remote = new TaskCompletionSource<SctpDataChannel.ChannelState>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.OnDataChannel += channel => remote.TrySetResult(channel);
        var local = a.CreateDataChannel("udp-chat");
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        local.OnOpen += () => opened.TrySetResult();
        if (local.ReadyState != "open")
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var remoteChannel = await remote.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var delivered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        remoteChannel.OnMessage += value => delivered.TrySetResult(value as string ?? "wrong type");
        local.Send("authenticated UDP data");
        Assert.Equal("authenticated UDP data", await delivered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
    }
}
