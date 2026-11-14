using System.Buffers.Binary;
using System.Security.Cryptography;
using Folio.Scripting.Rtc;

namespace Folio.Scripting.Tests;

/// <summary>A clock the test advances by hand, so jitter-buffer waits and RTCP intervals need no sleeping.</summary>
internal sealed class ManualTime : TimeProvider
{
    private long _ms;
    public override long GetTimestamp() => _ms;
    public override long TimestampFrequency => 1000;
    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(_ms);
    public void Advance(int milliseconds) => _ms += milliseconds;
}

public sealed class RtcMediaTests
{
    private static (SrtpTransport A, SrtpTransport B) SrtpPair()
    {
        var ka = RandomNumberGenerator.GetBytes(16); var sa = RandomNumberGenerator.GetBytes(12);
        var kb = RandomNumberGenerator.GetBytes(16); var sb = RandomNumberGenerator.GetBytes(12);
        return (new SrtpTransport(ka, sa, kb, sb), new SrtpTransport(kb, sb, ka, sa));
    }

    private static byte[] Rtp(uint ssrc, ushort seq, int payload = 20, int pt = 0, bool extension = false)
    {
        var header = extension ? 12 + 8 : 12;
        var packet = new byte[header + payload];
        packet[0] = (byte)(0x80 | (extension ? 0x10 : 0)); packet[1] = (byte)pt;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), seq);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), ssrc);
        if (extension) { packet[12] = 0xBE; packet[13] = 0xDE; packet[15] = 1; packet[16] = 0x10; packet[17] = 7; }
        for (var i = 0; i < payload; i++) packet[header + i] = (byte)(i + seq);
        return packet;
    }

    [Fact]
    public void SrtpRoundTripsWithHeaderExtensionAndRejectsTamperingAndReplay()
    {
        var (a, b) = SrtpPair(); using var _a = a; using var _b = b;
        var rtp = Rtp(0x11223344, 7, extension: true);
        var wire = a.ProtectRtp(rtp);
        Assert.Equal(rtp.Length + 16, wire.Length);
        Assert.False(wire.AsSpan(20, 20).SequenceEqual(rtp.AsSpan(20, 20)));             // payload is encrypted
        Assert.True(wire.AsSpan(0, 20).SequenceEqual(rtp.AsSpan(0, 20)));              // header (and extension) stay clear
        var tampered = (byte[])wire.Clone(); tampered[16] ^= 1; // extension bytes are authenticated
        Assert.False(b.TryUnprotectRtp(tampered, out _));
        var flipped = (byte[])wire.Clone(); flipped[25] ^= 1;
        Assert.False(b.TryUnprotectRtp(flipped, out _));
        Assert.True(b.TryUnprotectRtp(wire, out var plain));
        Assert.Equal(rtp, plain);
        Assert.False(b.TryUnprotectRtp(wire, out _));
    }

    [Fact]
    public void SrtpTracksRolloverAndKeepsSsrcsIndependent()
    {
        var (a, b) = SrtpPair(); using var _a = a; using var _b = b;
        ushort seq = 65530;
        for (var i = 0; i < 20; i++, seq++) // crosses 65535 -> 0
        {
            Assert.True(b.TryUnprotectRtp(a.ProtectRtp(Rtp(1, seq)), out var plain));
            Assert.Equal(seq, BinaryPrimitives.ReadUInt16BigEndian(plain.AsSpan(2)));
        }
        // A second SSRC with an unrelated sequence number has its own window and rollover counter.
        Assert.True(b.TryUnprotectRtp(a.ProtectRtp(Rtp(2, 40000)), out _));
        Assert.True(b.TryUnprotectRtp(a.ProtectRtp(Rtp(2, 40001)), out _));
        // Reordering inside the window is accepted exactly once.
        var late = a.ProtectRtp(Rtp(1, seq));
        var next = a.ProtectRtp(Rtp(1, (ushort)(seq + 1)));
        Assert.True(b.TryUnprotectRtp(next, out _));
        Assert.True(b.TryUnprotectRtp(late, out _));
        Assert.False(b.TryUnprotectRtp(late, out _));
    }

    [Fact]
    public void SrtpRejectsOldPacketsMalformedPacketsAndCapsStreamState()
    {
        var (a, b) = SrtpPair(); using var _a = a; using var _b = b;
        var old = a.ProtectRtp(Rtp(1, 10));
        for (ushort s = 11; s < 120; s++) Assert.True(b.TryUnprotectRtp(a.ProtectRtp(Rtp(1, s)), out _));
        Assert.False(b.TryUnprotectRtp(old, out _)); // more than 64 behind
        Assert.False(b.TryUnprotectRtp(new byte[5], out _));
        Assert.False(b.TryUnprotectRtp(new byte[3000], out _));
        var bad = Rtp(9, 1); bad[0] = 0x40; // version 1
        Assert.False(b.TryUnprotectRtp(bad, out _));
        var truncatedExtension = Rtp(9, 1, extension: true); truncatedExtension[15] = 200; // extension longer than packet
        Assert.False(b.TryUnprotectRtp(truncatedExtension, out _));
        Assert.Throws<InvalidOperationException>(() => a.ProtectRtp(new byte[4]));
        // Streams are only remembered once they authenticate, and only up to the cap.
        var (ka, sa, kb, sb) = (RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(12), RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(12));
        using var capped = new SrtpTransport(kb, sb, ka, sa);
        var accepted = 0;
        for (var sender = 0; sender < 3; sender++)
        {
            using var source = new SrtpTransport(ka, sa, kb, sb);
            for (uint ssrc = (uint)(1000 + sender * 20); ssrc < 1000 + sender * 20 + 20; ssrc++)
                if (capped.TryUnprotectRtp(source.ProtectRtp(Rtp(ssrc, 1)), out _)) accepted++;
        }
        Assert.Equal(SrtpTransport.MaxRemoteStreams, accepted);
    }

    [Fact]
    public void SrtcpRoundTripsAndRejectsTamperingReplayAndClearText()
    {
        var (a, b) = SrtpPair(); using var _a = a; using var _b = b;
        var rtcp = RtcpCodec.BuildReport(0xAABBCCDD, sender: false, 0, 0, 0, 0,
            [new RtcpReportBlock(5, 3, 2, 1000, 4, 0, 0)], "peer");
        var wire = a.ProtectRtcp(rtcp);
        Assert.Equal(rtcp.Length + 16 + 4, wire.Length);
        Assert.True(wire.AsSpan(0, 8).SequenceEqual(rtcp.AsSpan(0, 8)));
        Assert.True(b.TryUnprotectRtcp(wire, out var plain));
        Assert.Equal(rtcp, plain);
        Assert.False(b.TryUnprotectRtcp(wire, out _)); // replay
        var tampered = (byte[])wire.Clone(); tampered[12] ^= 1;
        Assert.False(b.TryUnprotectRtcp(tampered, out _));
        var clear = (byte[])wire.Clone(); clear[^4] &= 0x7F; // E bit cleared: GCM never sends clear
        Assert.False(b.TryUnprotectRtcp(clear, out _));
        Assert.False(b.TryUnprotectRtcp(new byte[10], out _));
    }

    // RFC 7714 section 16.1 (RTP) and 17.1 (RTCP), AEAD_AES_128_GCM. The vectors give session keys, so they bypass the KDF.
    private static readonly byte[] VectorKey = Convert.FromHexString("000102030405060708090a0b0c0d0e0f");
    private static readonly byte[] VectorSalt = Convert.FromHexString("51756964" + "2070726f" + "2071756f");
    private const string RtpPlain = "8040f17b8041f8d35501a0b247616c6c69612065737420" + "6f6d6e697320646976697361" + "20696e20706172746573207472" + "6573";
    private const string RtpSecret = "8040f17b8041f8d35501a0b2f24de3a3fb34de6cacba861c9d7e4bcabe633bd50d294e6f42a5f47a51c7d19b36de3adf8833899d7f27beb16a9152cf765ee4390cce";
    private const string RtcpPlain = "81c8000d4d6172734e5450314e54503252545020" + "0000042a0000e9304c756e61" + "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef";
    private const string RtcpSecret = "81c8000d4d61727363e94885dcdab67ca727d7662f6b7e997ff5c0f76c06f32dc676a5f1730d6fda4ce09b4686303ded0bb9275bc84aa45896cf4d2fc5abf87245d9eade800005d4";

    [Fact]
    public void SrtpMatchesRfc7714KnownAnswerVectorsForRtp()
    {
        var plain = Convert.FromHexString(RtpPlain);
        using var sender = new SrtpTransport(VectorKey, VectorSalt, true);
        Assert.Equal(RtpSecret, Convert.ToHexStringLower(sender.ProtectRtp(plain)));
        using var receiver = new SrtpTransport(VectorKey, VectorSalt, true);
        Assert.True(receiver.TryUnprotectRtp(Convert.FromHexString(RtpSecret), out var decrypted));
        Assert.Equal(plain, decrypted);
        var broken = Convert.FromHexString(RtpSecret); broken[^1] ^= 1;
        using var strict = new SrtpTransport(VectorKey, VectorSalt, true);
        Assert.False(strict.TryUnprotectRtp(broken, out _));
    }

    [Fact]
    public void SrtpMatchesRfc7714KnownAnswerVectorsForRtcp()
    {
        var plain = Convert.FromHexString(RtcpPlain);
        using var sender = new SrtpTransport(VectorKey, VectorSalt, true) { RtcpSendIndex = 0x5d4 };
        Assert.Equal(RtcpSecret, Convert.ToHexStringLower(sender.ProtectRtcp(plain)));
        using var receiver = new SrtpTransport(VectorKey, VectorSalt, true);
        Assert.True(receiver.TryUnprotectRtcp(Convert.FromHexString(RtcpSecret), out var decrypted));
        Assert.Equal(plain, decrypted);
    }

    [Fact]
    public void ByeEvictsTheSsrcReceiveState()
    {
        var (a, b) = SrtpPair(); using var _a = a; using var _b = b;
        var first = a.ProtectRtp(Rtp(5, 10));
        Assert.True(b.TryUnprotectRtp(first, out _));
        Assert.False(b.TryUnprotectRtp(first, out _));
        b.RemoveRemoteStream(5);
        Assert.True(b.TryUnprotectRtp(first, out _)); // state is gone, so the slot is free again (and a replay would re-enter)
    }

    [Fact]
    public void SdpOnlyTreatsEightKilohertzMonoAsG711AndBoundsSizeAndCandidates()
    {
        var sdp = RtcSdp.Parse("m=audio 9 X 0 8 96\r\na=rtpmap:0 PCMU/16000\r\na=rtpmap:8 PCMA/8000\r\na=rtpmap:96 PCMU/8000/2\r\n");
        var media = sdp.Media[0];
        Assert.False(media.CodecFor(0)!.IsG711);
        Assert.True(media.CodecFor(8)!.IsG711);
        Assert.False(media.CodecFor(96)!.IsG711);
        Assert.Throws<InvalidDataException>(() => RtcSdp.Parse(new string('a', RtcSdp.MaxSdpChars + 1)));
        var candidates = string.Concat(Enumerable.Range(0, RtcSdp.MaxCandidates + 1).Select(i => $"a=candidate:{i} 1 udp 2130706431 10.0.0.1 {40000 + i} typ host\r\n"));
        Assert.Throws<InvalidDataException>(() => RtcSdp.Parse("v=0\r\nm=audio 9 X 0\r\n" + candidates));
    }

    [Fact]
    public void KeyDerivationUsesTheDirectionOfTheKeys()
    {
        var (a, b) = SrtpPair(); using var _a = a; using var _b = b;
        // A packet protected by A cannot be read by A's own receive context.
        Assert.False(a.TryUnprotectRtp(a.ProtectRtp(Rtp(1, 1)), out _));
    }

    [Fact]
    public void DtlsClientRejectsUnsupportedSrtpProfile()
    {
        var clock = new ManualTime();
        using var client = new DtlsTransport(clock, false);
        using var server = new DtlsTransport(clock, false) { IsClient = false };
        client.RemoteFingerprint = server.Fingerprint;
        server.RemoteFingerprint = client.Fingerprint;
        byte[]? serverHello = null;
        client.OnDatagramToSend += bytes => server.ProcessIncomingDatagram(bytes);
        server.OnDatagramToSend += bytes =>
        {
            if (serverHello is null && bytes.Length > 13 && bytes[0] == 22 && bytes[13] == 2)
                serverHello = (byte[])bytes.Clone();
        };
        server.StartHandshake();
        client.StartHandshake();
        Assert.NotNull(serverHello);

        // Mutate the use_srtp extension in ServerHello to an unsupported profile (0x0001 instead of 0x0007)
        var pattern = new byte[] { 0x00, 0x0E, 0x00, 0x05, 0x00, 0x02, 0x00, 0x07, 0x00 };
        var idx = serverHello.AsSpan().IndexOf(pattern);
        Assert.True(idx >= 0);

        var badProfile = (byte[])serverHello.Clone();
        badProfile[idx + 7] = 0x01; // unsupported profile
        var ex = Assert.Throws<CryptographicException>(() => client.ProcessIncomingDatagram(badProfile));
        Assert.Contains("unsupported SRTP profile", ex.Message);

        // Malformed length on a fresh client is also rejected
        using var client2 = new DtlsTransport(clock, false);
        client2.RemoteFingerprint = server.Fingerprint;
        client2.OnDatagramToSend += bytes => server.ProcessIncomingDatagram(bytes);
        client2.StartHandshake();
        var badLength = (byte[])serverHello.Clone();
        badLength[idx + 3] = 0x04;
        var ex2 = Assert.Throws<CryptographicException>(() => client2.ProcessIncomingDatagram(badLength));
        Assert.Contains("unsupported SRTP profile", ex2.Message);
    }

    [Fact]
    public void G711RoundTripsWithinQuantisationError()
    {
        for (var s = -32768; s <= 32767; s += 97)
        {
            var mu = G711.DecodeMuLaw(G711.EncodeMuLaw((short)s));
            var a = G711.DecodeALaw(G711.EncodeALaw((short)s));
            var tolerance = Math.Max(64, Math.Abs(s) / 16);
            Assert.InRange(Math.Abs(mu - s), 0, tolerance + 32);
            Assert.InRange(Math.Abs(a - s), 0, tolerance + 32);
        }
        Assert.Equal(0xFF, G711.EncodeMuLaw(0)); // known G.711 code for silence
        Assert.Equal(0xD5, G711.EncodeALaw(0));
    }

    [Fact]
    public void RtcpBuildAndParseRoundTripAndHostileInputIsBounded()
    {
        var block = new RtcpReportBlock(77, 12, -3, 70000, 9, 111, 222);
        var packet = RtcpCodec.BuildReport(5, sender: true, 0x1122334455667788, 4000, 10, 1600, [block], "cname");
        var parsed = RtcpCodec.Parse(packet);
        Assert.Equal(2, parsed.Count); // SR + SDES
        Assert.Equal(RtcpKind.SenderReport, parsed[0].Kind);
        Assert.Equal(4000u, parsed[0].RtpTimestamp);
        Assert.Equal(1600u, parsed[0].OctetCount);
        Assert.Equal(0x33445566u, parsed[0].NtpMiddle); // middle 32 bits of the NTP stamp
        Assert.Equal(block, parsed[0].Blocks.Single());
        Assert.Equal(RtcpKind.Bye, RtcpCodec.Parse(RtcpCodec.BuildBye(9))[0].Kind);
        var rng = new Random(5);
        for (var i = 0; i < 500; i++)
        {
            var junk = new byte[rng.Next(0, 200)]; rng.NextBytes(junk);
            if (junk.Length > 0) junk[0] = (byte)(0x80 | (junk[0] & 0x1F));
            Assert.True(RtcpCodec.Parse(junk).Count <= RtcpCodec.MaxPacketsPerCompound);
        }
        var lying = (byte[])packet.Clone(); lying[3] = 0xFF; // length beyond the datagram
        Assert.Empty(RtcpCodec.Parse(lying));
    }

    [Fact]
    public void SdpParsesAudioSectionsAndBoundsAttackerControlledCounts()
    {
        var sdp = RtcSdp.Parse("v=0\r\na=group:BUNDLE a v\r\nm=audio 9 UDP/TLS/RTP/SAVPF 111 0 8\r\na=mid:a\r\na=recvonly\r\na=rtcp-mux\r\na=rtpmap:111 opus/48000/2\r\na=ssrc:1234 cname:x\r\na=ssrc:1234 msid:y\r\nm=video 0 UDP/TLS/RTP/SAVPF 96\r\na=mid:v\r\n");
        var audio = sdp.Media[0];
        Assert.Equal("recvonly", audio.Direction);
        Assert.True(audio.RtcpMux);
        Assert.Equal(new[] { 111, 0, 8 }, audio.PayloadTypes);
        Assert.Equal("OPUS", audio.CodecFor(111)!.Name);
        Assert.Equal(2, audio.CodecFor(111)!.Channels);
        Assert.Equal("PCMU", audio.CodecFor(0)!.Name);
        Assert.Equal(new[] { 1234u }, audio.Ssrcs);
        Assert.Equal(0, sdp.Media[1].Port);

        var many = "v=0\r\n" + string.Concat(Enumerable.Range(0, 40).Select(i => $"m=audio 9 UDP/TLS/RTP/SAVPF 0\r\na=mid:{i}\r\n"));
        Assert.Throws<InvalidDataException>(() => RtcSdp.Parse(many));
        var ssrcs = RtcSdp.Parse("m=audio 9 X " + string.Join(' ', Enumerable.Range(0, 200)) + "\r\n" + string.Concat(Enumerable.Range(1, 100).Select(i => $"a=ssrc:{i} cname:x\r\n")));
        Assert.True(ssrcs.Media[0].PayloadTypes.Count <= RtcMediaDescription.MaxCodecs);
        Assert.True(ssrcs.Media[0].Ssrcs.Count <= RtcMediaDescription.MaxSsrcs);
    }

    [Fact]
    public void SdpRoundTripsCodecsDirectionAndSsrcAndDropsRejectedSectionsFromBundle()
    {
        var sdp = new RtcSdp { IceUfrag = "u", IcePassword = "p", FingerprintValue = "AA" };
        var audio = new RtcMediaDescription { Kind = "audio", Protocol = "UDP/TLS/RTP/SAVPF", Mid = "0", Direction = "sendonly" };
        audio.Codecs.Add(new RtcCodec(0, "PCMU", 8000, 1)); audio.Ssrcs.Add(99);
        var rejected = new RtcMediaDescription { Kind = "video", Port = 0, Protocol = "UDP/TLS/RTP/SAVPF", Mid = "1" };
        rejected.Codecs.Add(new RtcCodec(96, "VP8", 90000, 1));
        sdp.Media.Add(audio); sdp.Media.Add(rejected);
        var text = sdp.ToSdp();
        Assert.Contains("a=group:BUNDLE 0\r\n", text);
        Assert.Contains("m=audio 9 UDP/TLS/RTP/SAVPF 0\r\n", text);
        Assert.Contains("m=video 0 UDP/TLS/RTP/SAVPF 96\r\n", text);
        var parsed = RtcSdp.Parse(text);
        Assert.Equal("sendonly", parsed.Media[0].Direction);
        Assert.True(parsed.Media[0].RtcpMux);
        Assert.Equal(new[] { 99u }, parsed.Media[0].Ssrcs);
        Assert.Equal(new[] { "0" }, parsed.BundleMids);
    }

    private const string Offer = "v=0\r\na=group:BUNDLE 0\r\nm=audio 9 UDP/TLS/RTP/SAVPF 0 8\r\na=mid:0\r\na=sendrecv\r\na=rtcp-mux\r\na=rtpmap:0 PCMU/8000\r\na=rtpmap:8 PCMA/8000\r\n";
    private static string Answer(int pt, string name) => $"v=0\r\na=group:BUNDLE 0\r\nm=audio 9 UDP/TLS/RTP/SAVPF {pt}\r\na=mid:0\r\na=sendrecv\r\na=rtcp-mux\r\na=rtpmap:{pt} {name}/8000\r\n";

    private sealed class Link
    {
        public readonly ManualTime Time = new();
        public readonly RtcMediaEngine A, B;
        public readonly List<RtcRemoteTrack> TracksOnA = [], TracksOnB = [];
        public readonly List<MediaAudioFrame> FramesOnB = [];
        public readonly Queue<byte[]> ToB = new(), ToA = new();
        public readonly SrtpTransport SrtpA, SrtpB;
        public RtcLocalTrack? Sender;

        public Link(int pt = 0, string name = "PCMU", RtcMediaBudget? budgetA = null, RtcMediaBudget? budgetB = null)
        {
            A = new RtcMediaEngine(Time, budgetA); B = new RtcMediaEngine(Time, budgetB);
            (SrtpA, SrtpB) = SrtpPair();
            A.SetSrtp(SrtpA); B.SetSrtp(SrtpB);
            A.OnPacketToSend += p => ToB.Enqueue(p); B.OnPacketToSend += p => ToA.Enqueue(p);
            A.OnRemoteTrack += TracksOnA.Add;
            B.OnRemoteTrack += t => { TracksOnB.Add(t); t.AudioFrame += FramesOnB.Add; };
            Sender = A.AddLocalAudio();
            var offer = RtcSdp.Parse(Offer); var answer = RtcSdp.Parse(Answer(pt, name));
            A.Apply(offer, null);
            B.Apply(null, offer);
            B.Apply(answer, null);
            A.Apply(null, answer);
        }

        public void Pump(Func<byte[], int, bool>? filter = null)
        {
            var index = 0;
            while (ToB.Count > 0) { var packet = ToB.Dequeue(); if (filter?.Invoke(packet, index++) ?? true) B.ProcessDatagram(packet); }
            while (ToA.Count > 0) A.ProcessDatagram(ToA.Dequeue());
        }
    }

    private static float[] Sine(int rate, double seconds, double hz = 440, double amplitude = 0.5) =>
        Enumerable.Range(0, (int)(rate * seconds)).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / rate))).ToArray();

    [Theory]
    [InlineData(0, "PCMU")]
    [InlineData(8, "PCMA")]
    public void AudioFlowsFromSenderThroughSrtpToTheRemoteTrack(int pt, string name)
    {
        var link = new Link(pt, name);
        var sent = link.A.SendAudio(link.Sender!, Sine(48000, 1.0), 48000, 1);
        Assert.Equal(50, sent); // 1 s = 50 packets of 20 ms
        Assert.Equal(50, link.ToB.Count);
        // On the wire every packet is SRTP: header in clear, payload+tag opaque and 16 bytes longer.
        Assert.All(link.ToB, p => Assert.Equal(12 + 160 + 16, p.Length));
        link.Pump();
        Assert.Single(link.TracksOnB);
        var track = link.TracksOnB[0];
        Assert.Equal("audio", track.Kind);
        Assert.True(track.Decodable);
        Assert.Equal(50, track.PacketsReceived);
        Assert.Equal(50, link.FramesOnB.Count);
        Assert.All(link.FramesOnB, f => { Assert.Equal(8000, f.SampleRate); Assert.Equal(1, f.Channels); Assert.Equal(160, f.Samples.Length); });
        var samples = link.FramesOnB.SelectMany(f => f.Samples.ToArray()).ToArray();
        Assert.Equal(8000, samples.Length);
        var rms = Math.Sqrt(samples.Select(s => (double)s * s).Average());
        Assert.InRange(rms, 0.3, 0.4); // a 0.5 amplitude sine has RMS 0.354
        Assert.Equal("0", link.Sender!.Mid);
        Assert.Equal(50, link.Sender.PacketsSent);
    }

    [Fact]
    public void JitterBufferReordersAndSkipsALostPacketAfterTheWaitAndReceiverReportsCountIt()
    {
        var link = new Link();
        link.A.SendAudio(link.Sender!, Sine(8000, 0.2), 8000, 1); // the resampler holds back the final sample: 9 whole packets
        Assert.Equal(9, link.ToB.Count);
        var wire = link.ToB.ToArray(); link.ToB.Clear();
        // Order: 0, 2, 1 (reordered), 3, [4 lost], 5, 6.
        foreach (var index in new[] { 0, 2, 1, 3, 5, 6 }) link.B.ProcessDatagram(wire[index]);
        Assert.Equal(4, link.FramesOnB.Count); // 0..3 in order; 5 and 6 wait behind the hole
        link.Time.Advance(30); link.B.Tick();
        Assert.Equal(4, link.FramesOnB.Count);  // still inside the wait
        link.Time.Advance(40); link.B.Tick();
        Assert.Equal(6, link.FramesOnB.Count);  // gap skipped after RtcMediaEngine.JitterDelay
        Assert.Equal(1, link.TracksOnB[0].PacketsLost);
        // A duplicate and a packet from before the playout point are dropped, not replayed.
        link.B.ProcessDatagram(wire[3]);
        Assert.Equal(6, link.FramesOnB.Count);

        link.Time.Advance(1000); link.B.Tick();
        Assert.Single(link.ToA);
        link.Pump();
        var report = link.A.LastReports.First(r => r.Kind == RtcpKind.ReceiverReport);
        var block = report.Blocks.Single();
        Assert.Equal(link.Sender!.Ssrc, block.Ssrc);
        Assert.Equal(1, block.CumulativeLost);
        Assert.True(block.FractionLost > 0);
    }

    [Fact]
    public void SenderReportsCarryCountsAndDelayInTheReceiverBlock()
    {
        var link = new Link();
        link.A.SendAudio(link.Sender!, Sine(8000, 0.1), 8000, 1);
        link.Pump();
        link.Time.Advance(1000); link.A.Tick();           // A has sent: SR
        Assert.Single(link.ToB);
        link.Pump();
        var sr = link.B.LastReports.First(r => r.Kind == RtcpKind.SenderReport);
        Assert.Equal(link.Sender!.PacketsSent, (long)sr.PacketCount);
        Assert.Equal(link.Sender.BytesSent, (long)sr.OctetCount);
        link.B.Tick(); link.Pump();                       // B's first report
        link.Time.Advance(500); link.B.Tick();            // 500 ms later it is not due again (interval is 1 s)
        Assert.Empty(link.ToA);
        link.Time.Advance(500); link.B.Tick();
        link.Pump();
        var block = link.A.LastReports.First(r => r.Kind == RtcpKind.ReceiverReport).Blocks.Single();
        Assert.NotEqual(0u, block.LastSr);
        Assert.InRange(block.DelaySinceLastSr, 60000u, 70000u); // ~1 s in 1/65536 s units
    }

    [Fact]
    public void ByeEndsTheRemoteTrack()
    {
        var link = new Link();
        link.A.SendAudio(link.Sender!, Sine(8000, 0.1), 8000, 1);
        link.Pump();
        var ended = 0; link.TracksOnB[0].Ended += () => ended++;
        link.ToB.Enqueue(link.SrtpA.ProtectRtcp(RtcpCodec.BuildBye(link.Sender!.Ssrc)));
        link.Pump();
        Assert.Equal(1, ended);
    }

    [Fact]
    public void BudgetsBoundTracksPacketsAndBytes()
    {
        // Track budget: no track, no ontrack, no sender.
        var none = new Link(budgetA: new RtcMediaBudget(maxTracks: 0), budgetB: new RtcMediaBudget(maxTracks: 0));
        Assert.Null(none.Sender);
        Assert.Empty(none.TracksOnB);

        // Send budget: 10 packets per second.
        var timeA = new ManualTime();
        var limited = new Link(budgetA: new RtcMediaBudget(timeA, packetsPerSecond: 10));
        var sent = limited.A.SendAudio(limited.Sender!, Sine(8000, 1.0), 8000, 1);
        Assert.Equal(10, sent);
        // Receive budget: 5 packets per second on B; the rest are dropped host-side before SRTP.
        var receiving = new Link(budgetB: new RtcMediaBudget(new ManualTime(), packetsPerSecond: 5));
        receiving.A.SendAudio(receiving.Sender!, Sine(8000, 1.0), 8000, 1);
        receiving.Pump();
        Assert.Equal(5, receiving.TracksOnB[0].PacketsReceived);
        // Byte budget.
        var bytes = new RtcMediaBudget(new ManualTime(), bytesPerSecond: 1000);
        Assert.True(bytes.TryConsume(true, 900));
        Assert.False(bytes.TryConsume(true, 200));
        Assert.True(bytes.TryConsume(false, 900)); // the send side has its own bucket
        // Tracks are returned to the budget on close.
        var shared = new RtcMediaBudget(maxTracks: 1);
        var engine = new RtcMediaEngine(budget: shared);
        Assert.NotNull(engine.AddLocalAudio());
        Assert.Null(new RtcMediaEngine(budget: shared).AddLocalAudio());
        engine.Dispose();
        Assert.NotNull(new RtcMediaEngine(budget: shared).AddLocalAudio());
    }

    [Fact]
    public void HostileDatagramsAreDroppedWithoutFramesOrFaults()
    {
        var link = new Link();
        var rng = new Random(11);
        for (var i = 0; i < 2000; i++)
        {
            var junk = new byte[rng.Next(0, 400)]; rng.NextBytes(junk);
            if (junk.Length > 0) junk[0] = (byte)(0x80 | (junk[0] & 0x3F));
            link.B.ProcessDatagram(junk);
        }
        link.B.ProcessDatagram(new byte[4000]);
        link.B.ProcessDatagram([0x80]);
        Assert.Empty(link.FramesOnB);
        Assert.True(link.B.RtpPacketsDropped > 0);

        // Authenticated but unusable: unknown payload type, oversized payload, bad padding, header extension past the end.
        uint ssrc = 4242;
        foreach (var packet in new[]
        {
            Rtp(ssrc, 1, pt: 111),
            Rtp(ssrc, 2, payload: RtcMediaEngine.MaxPayloadBytes + 1),
        })
            link.B.ProcessDatagram(link.SrtpA.ProtectRtp(packet));
        var padded = Rtp(ssrc, 3); padded[0] |= 0x20; padded[^1] = 200;
        link.B.ProcessDatagram(link.SrtpA.ProtectRtp(padded));
        Assert.Empty(link.FramesOnB);
        Assert.Equal(0, link.TracksOnB[0].PacketsReceived);
        // A valid packet on the same SSRC still works afterwards.
        link.B.ProcessDatagram(link.SrtpA.ProtectRtp(Rtp(ssrc, 4, payload: 160)));
        Assert.Equal(1, link.TracksOnB[0].PacketsReceived);
    }

    [Fact]
    public void JitterBufferHoldsAtMostSixtyFourPacketsPerStream()
    {
        var link = new Link();
        // Packet 100 plays; then packets 102.. arrive with the hole at 101, so everything buffers behind it (time is frozen).
        link.B.ProcessDatagram(link.SrtpA.ProtectRtp(Rtp(7, 100, payload: 160)));
        for (ushort s = 102; s < 102 + RtcMediaEngine.MaxJitterPackets; s++) link.B.ProcessDatagram(link.SrtpA.ProtectRtp(Rtp(7, s, payload: 160)));
        Assert.Single(link.FramesOnB);
        Assert.Equal(0, link.TracksOnB[0].PacketsDropped);
        // One more would exceed the bound: the oldest is dropped, the hole counts as lost, and playout resumes.
        link.B.ProcessDatagram(link.SrtpA.ProtectRtp(Rtp(7, 102 + RtcMediaEngine.MaxJitterPackets, payload: 160)));
        Assert.Equal(1, link.TracksOnB[0].PacketsDropped);
        Assert.Equal(1, link.TracksOnB[0].PacketsLost);
        Assert.Equal(1 + RtcMediaEngine.MaxJitterPackets, link.FramesOnB.Count);
    }

    [Fact]
    public void UnsupportedVideoCodecsAreNegotiatedButNotDecoded()
    {
        var time = new ManualTime();
        var engine = new RtcMediaEngine(time);
        var tracks = new List<RtcRemoteTrack>();
        engine.OnRemoteTrack += tracks.Add;
        var (a, b) = SrtpPair(); using var _a = a; using var _b = b;
        engine.SetSrtp(b);
        var offer = RtcSdp.Parse("v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=mid:v\r\na=sendonly\r\na=rtcp-mux\r\na=rtpmap:96 VP9/90000\r\n");
        var local = RtcSdp.Parse("v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=mid:v\r\na=recvonly\r\na=rtcp-mux\r\na=rtpmap:96 VP9/90000\r\n");
        engine.Apply(null, offer); engine.Apply(local, null);
        var track = Assert.Single(tracks);
        Assert.Equal("video", track.Kind);
        Assert.False(track.Decodable);
        engine.ProcessDatagram(a.ProtectRtp(Rtp(5, 1, payload: 100, pt: 96)));
        Assert.Equal(1, track.PacketsReceived); // counted for stats and RTCP, payload not retained
    }
}
