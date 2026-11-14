using System.Buffers.Binary;
using System.Security.Cryptography;
using Folio.Scripting.Rtc;

namespace Folio.Scripting.Tests;

/// <summary>
/// Remote H.264 video through the media engine: SDP negotiation next to VP8, RTP depacketization (RFC 6184), loss repair with
/// NACK/PLI (RFC 4585), budgets and hostile input. The pictures are the checked-in x264 Constrained Baseline streams, so every
/// delivered frame is compared with a direct decode of the same access unit.
/// </summary>
public sealed class RtcH264Tests
{
    private const uint Ssrc = 0x6262;
    private const int PayloadType = 97;
    private const string Fmtp = "a=fmtp:97 level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f\r\n";
    private const string Feedback97 = "a=rtcp-fb:97 nack\r\na=rtcp-fb:97 nack pli\r\na=rtcp-fb:97 ccm fir\r\n";

    private static string Local(string feedback = Feedback97) =>
        "v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 97\r\na=mid:v\r\na=recvonly\r\na=rtcp-mux\r\na=rtpmap:97 H264/90000\r\n" + Fmtp + feedback;
    private static string Remote(string feedback = Feedback97) =>
        "v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 97\r\na=mid:v\r\na=sendonly\r\na=rtcp-mux\r\na=rtpmap:97 H264/90000\r\n" + Fmtp + feedback;

    private static List<List<byte[]>> Units(byte[] stream) => H264DecoderTests.AccessUnits(stream);

    /// <summary>The RGBA of every picture, decoded directly without any RTP in between.</summary>
    private static List<byte[]> Reference(IReadOnlyList<List<byte[]>> units)
    {
        var decoder = new H264Decoder { RethrowUnexpected = true };
        var result = new List<byte[]>();
        foreach (var unit in units)
            foreach (var frame in decoder.Decode(H264DecoderTests.AnnexB(unit))) result.Add(H264Receiver.ToRgba(frame));
        return result;
    }

    private sealed class H264Link : IDisposable
    {
        public readonly ManualTime Time = new();
        public readonly RtcMediaEngine Engine;
        public readonly SrtpTransport Sender, Receiver;
        public readonly List<RtcRemoteTrack> Tracks = [];
        public readonly List<MediaCaptureVideoFrame> Delivered = [];
        public readonly Queue<byte[]> Outgoing = new();
        public readonly RtcMediaBudget Budget;
        public ushort Sequence = 100;
        public uint Timestamp = 90_000;

        public H264Link(string? local = null, string? remote = null, RtcMediaBudget? budget = null)
        {
            Budget = budget ?? new RtcMediaBudget(Time);
            Engine = new RtcMediaEngine(Time, Budget);
            var ka = RandomNumberGenerator.GetBytes(16); var sa = RandomNumberGenerator.GetBytes(12);
            var kb = RandomNumberGenerator.GetBytes(16); var sb = RandomNumberGenerator.GetBytes(12);
            Sender = new SrtpTransport(ka, sa, kb, sb); Receiver = new SrtpTransport(kb, sb, ka, sa);
            Engine.SetSrtp(Receiver);
            Engine.OnPacketToSend += Outgoing.Enqueue;
            Engine.OnRemoteTrack += track => { Tracks.Add(track); track.VideoFrame += Delivered.Add; };
            Engine.Apply(null, RtcSdp.Parse(remote ?? Remote()));
            Engine.Apply(RtcSdp.Parse(local ?? Local()), null);
        }

        /// <summary>One access unit as SRTP packets (mode 1: single NAL, STAP-A when <paramref name="stap"/>, FU-A above the MTU). The timestamp advances per unit.</summary>
        public List<(ushort Sequence, byte[] Datagram)> Packetize(List<byte[]> unit, int mtu = 1000, bool stap = false)
        {
            var payloads = new List<byte[]>();
            var small = new List<byte[]>();
            void FlushSmall()
            {
                if (small.Count == 0) return;
                if (small.Count == 1 || !stap) payloads.AddRange(small);
                else
                {
                    var p = new List<byte> { (byte)(24 | (small.Max(n => n[0]) & 0x60)) };
                    foreach (var n in small) { p.Add((byte)(n.Length >> 8)); p.Add((byte)n.Length); p.AddRange(n); }
                    payloads.Add([.. p]);
                }
                small.Clear();
            }
            foreach (var nal in unit)
            {
                if (nal.Length <= mtu) { small.Add(nal); continue; }
                FlushSmall();
                var body = nal.AsSpan(1);
                for (var offset = 0; offset < body.Length; offset += mtu - 2)
                {
                    var length = Math.Min(mtu - 2, body.Length - offset);
                    var p = new byte[2 + length];
                    p[0] = (byte)((nal[0] & 0xE0) | 28);
                    p[1] = (byte)((offset == 0 ? 0x80 : 0) | (offset + length == body.Length ? 0x40 : 0) | (nal[0] & 0x1F));
                    body.Slice(offset, length).CopyTo(p.AsSpan(2));
                    payloads.Add(p);
                }
            }
            FlushSmall();
            var packets = new List<(ushort, byte[])>();
            for (var i = 0; i < payloads.Count; i++) packets.Add(Rtp(payloads[i], i == payloads.Count - 1, Timestamp));
            Timestamp += 3000;
            return packets;
        }

        public (ushort Sequence, byte[] Datagram) Rtp(byte[] payload, bool marker, uint timestamp, int payloadType = PayloadType)
        {
            var rtp = new byte[12 + payload.Length];
            rtp[0] = 0x80; rtp[1] = (byte)(payloadType | (marker ? 0x80 : 0));
            BinaryPrimitives.WriteUInt16BigEndian(rtp.AsSpan(2), Sequence);
            BinaryPrimitives.WriteUInt32BigEndian(rtp.AsSpan(4), timestamp);
            BinaryPrimitives.WriteUInt32BigEndian(rtp.AsSpan(8), Ssrc);
            payload.CopyTo(rtp, 12);
            return (Sequence++, Sender.ProtectRtp(rtp));
        }

        public void Send(IEnumerable<(ushort Sequence, byte[] Datagram)> packets, Func<ushort, bool>? drop = null)
        {
            foreach (var (sequence, datagram) in packets)
                if (drop?.Invoke(sequence) != true) Engine.ProcessDatagram(datagram);
        }

        public void Advance(int milliseconds, int step = 10)
        {
            for (var elapsed = 0; elapsed < milliseconds; elapsed += step) { Time.Advance(step); Engine.Tick(); }
        }

        /// <summary>Everything the engine sent back so far, parsed (it is SRTCP, opened with the sender's context).</summary>
        public List<RtcpPacket> Feedback()
        {
            var all = new List<RtcpPacket>();
            while (Outgoing.Count > 0)
            {
                var datagram = Outgoing.Dequeue();
                if (Sender.TryUnprotectRtcp(datagram, out var plain) && plain is not null) all.AddRange(RtcpCodec.Parse(plain));
            }
            return all;
        }

        public void Dispose() => Engine.Dispose();
    }

    private static void AssertFrames(IReadOnlyList<byte[]> expected, IReadOnlyList<MediaCaptureVideoFrame> delivered, params int[] indexes)
    {
        Assert.Equal(indexes.Length, delivered.Count);
        for (var i = 0; i < indexes.Length; i++)
            Assert.True(expected[indexes[i]].AsSpan().SequenceEqual(delivered[i].Rgba.Span), $"delivered frame {i} differs from the direct decode of picture {indexes[i]}");
    }

    [Theory]
    [InlineData(1000, false)]
    [InlineData(200, false)]
    [InlineData(300, true)]
    public void H264FramesFlowThroughSrtpAndDecodeToTheReferenceOutput(int mtu, bool stap)
    {
        var units = Units(H264Fixtures.QcifLife());
        var expected = Reference(units);
        using var link = new H264Link();
        foreach (var unit in units) link.Send(link.Packetize(unit, mtu, stap));
        var track = Assert.Single(link.Tracks);
        Assert.Equal("video", track.Kind);
        Assert.True(track.Decodable);
        AssertFrames(expected, link.Delivered, Enumerable.Range(0, expected.Count).ToArray());
        Assert.All(link.Delivered, f => { Assert.Equal(128, f.Width); Assert.Equal(96, f.Height); Assert.True(f.IsValid); });
        Assert.Equal(1, track.KeyFramesDecoded);
        Assert.Equal(128, track.VideoWidth);
        Assert.Equal(0, track.VideoBudgetStops);
        Assert.Equal(0, track.VideoUnexpectedErrors);
        Assert.DoesNotContain(link.Feedback(), p => p.Kind is RtcpKind.Nack or RtcpKind.PictureLossIndication or RtcpKind.FullIntraRequest);
        Assert.True(link.Budget.VideoBytesInUse > 0);
    }

    [Fact]
    public void ACroppedMultiSliceStreamDecodesToo()
    {
        var units = Units(H264Fixtures.CroppedSlices());
        var expected = Reference(units);
        using var link = new H264Link();
        foreach (var unit in units) link.Send(link.Packetize(unit, 500, stap: true));
        AssertFrames(expected, link.Delivered, Enumerable.Range(0, expected.Count).ToArray());
        Assert.Equal(100, link.Delivered[0].Width); Assert.Equal(70, link.Delivered[0].Height);
        Assert.Equal(2, link.Tracks[0].KeyFramesDecoded);
    }

    [Fact]
    public void ReorderedAndDuplicatedPacketsStillDecodeInOrderWithoutAnyRepairRequest()
    {
        var units = Units(H264Fixtures.QcifLife());
        var expected = Reference(units);
        using var link = new H264Link();
        var all = units.SelectMany(unit => link.Packetize(unit, 200)).ToList();
        for (var i = 2; i + 1 < all.Count; i += 2) (all[i], all[i + 1]) = (all[i + 1], all[i]); // the very first packet fixes where the stream starts, so it stays first
        foreach (var packet in all) { link.Send([packet]); link.Send([packet]); } // every packet twice
        link.Advance(100);
        // A swapped pair can complete two pictures in one call, and only the newest of a call is delivered: so the delivered
        // frames are an in-order subsequence of the pictures, ending with the last one, and every one of them is decoded right.
        var next = 0;
        foreach (var frame in link.Delivered)
        {
            while (next < expected.Count && !expected[next].AsSpan().SequenceEqual(frame.Rgba.Span)) next++;
            Assert.True(next < expected.Count, "a delivered frame matches no later picture");
            next++;
        }
        Assert.True(expected[^1].AsSpan().SequenceEqual(link.Delivered[^1].Rgba.Span));
        Assert.True(link.Delivered.Count >= expected.Count / 2);
        Assert.DoesNotContain(link.Feedback(), p => p.Kind is RtcpKind.Nack or RtcpKind.PictureLossIndication or RtcpKind.FullIntraRequest);
    }

    [Fact]
    public void ALostPacketIsNackedAndTheRetransmissionCompletesThePicture()
    {
        var units = Units(H264Fixtures.CroppedSlices());
        var expected = Reference(units);
        using var link = new H264Link();
        link.Send(link.Packetize(units[0]));
        var lost = link.Packetize(units[1]);
        var victim = lost[0];
        link.Send(lost, drop: s => s == victim.Sequence);
        link.Send(link.Packetize(units[2]));
        Assert.Single(link.Delivered);                     // pictures 1 and 2 wait behind the hole
        link.Advance(40);
        var nack = Assert.Single(link.Feedback(), p => p.Kind == RtcpKind.Nack);
        Assert.Contains(victim.Sequence, nack.NackSequences);
        link.Engine.ProcessDatagram(victim.Datagram);      // the retransmission
        AssertFrames(expected, link.Delivered, 0, 2);       // pictures 1 and 2 completed in one call: only the newest is delivered
        link.Advance(1000);
        Assert.DoesNotContain(link.Feedback(), p => p.Kind is RtcpKind.PictureLossIndication or RtcpKind.FullIntraRequest);
        Assert.Equal(1, link.Tracks[0].KeyFramesDecoded);
    }

    [Fact]
    public void AnUnrecoveredLossDropsThePictureAsksForAKeyFrameAndTheNextIdrRecovers()
    {
        var units = Units(H264Fixtures.CroppedSlices()); // IDR, P, P, P, IDR, P, P, P
        var expected = Reference(units);
        using var link = new H264Link();
        link.Send(link.Packetize(units[0]));
        var lost = link.Packetize(units[1]);
        link.Send(lost, drop: s => s == lost[0].Sequence);
        link.Send(link.Packetize(units[2]));
        link.Send(link.Packetize(units[3]));
        link.Advance(600);                                 // never retransmitted: the hole times out
        var feedback = link.Feedback();
        Assert.Contains(feedback, p => p.Kind == RtcpKind.Nack);
        Assert.Contains(feedback, p => p.Kind == RtcpKind.PictureLossIndication);
        Assert.Single(link.Delivered);                     // nothing predicted from the missing reference was shown
        // The sender answers the PLI with the next IDR picture; the pictures after it decode again.
        foreach (var unit in units.Skip(4)) link.Send(link.Packetize(unit));
        AssertFrames(expected, link.Delivered, 0, 4, 5, 6, 7);
        Assert.Equal(2, link.Tracks[0].KeyFramesDecoded);
        Assert.True(link.Tracks[0].VideoFramesDiscarded > 0);
        Assert.Equal(0, link.Tracks[0].VideoUnexpectedErrors);
    }

    [Fact]
    public void AStreamThatNeverSendsAnIdrKeepsAskingAtABoundedRate()
    {
        var units = Units(H264Fixtures.QcifLife());
        using var link = new H264Link();
        link.Send(link.Packetize(units[1]));               // a P picture first: nothing to predict from
        Assert.Empty(link.Delivered);
        link.Advance(150);
        Assert.Single(link.Feedback(), p => p.Kind == RtcpKind.PictureLossIndication);
        link.Advance(2000);
        var requests = link.Feedback().Count(p => p.Kind == RtcpKind.PictureLossIndication);
        Assert.InRange(requests, 3, 5);                    // one per KeyFrameRequestInterval, whatever the tick rate
    }

    [Fact]
    public void FirIsUsedWhenOnlyFirIsNegotiatedAndNothingWhenNoFeedbackIs()
    {
        var units = Units(H264Fixtures.QcifLife());
        using (var link = new H264Link(Local("a=rtcp-fb:97 ccm fir\r\n"), Remote("a=rtcp-fb:97 ccm fir\r\n")))
        {
            link.Send(link.Packetize(units[1]));
            link.Advance(150);
            var feedback = link.Feedback();
            Assert.Single(feedback, p => p.Kind == RtcpKind.FullIntraRequest);
            Assert.DoesNotContain(feedback, p => p.Kind is RtcpKind.PictureLossIndication or RtcpKind.Nack);
        }
        using var silent = new H264Link(Local(""), Remote(""));
        silent.Send(silent.Packetize(units[1]));
        silent.Advance(1500);
        Assert.DoesNotContain(silent.Feedback(), p => p.Kind is RtcpKind.PictureLossIndication or RtcpKind.FullIntraRequest or RtcpKind.Nack);
    }

    [Fact]
    public void BothCodecsAreDecodedOnTheirOwnPayloadTypesOfOneSection()
    {
        const string both = "v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 96 97\r\na=mid:v\r\na=recvonly\r\na=rtcp-mux\r\na=rtpmap:96 VP8/90000\r\na=rtpmap:97 H264/90000\r\n" +
            "a=fmtp:97 level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f\r\n" +
            "a=rtcp-fb:96 nack\r\na=rtcp-fb:97 nack\r\na=rtcp-fb:97 nack pli\r\n";
        var units = Units(H264Fixtures.QcifLife());
        var expected = Reference(units);
        using var link = new H264Link(both, both.Replace("a=recvonly", "a=sendonly"));
        foreach (var unit in units) link.Send(link.Packetize(unit));
        AssertFrames(expected, link.Delivered, Enumerable.Range(0, expected.Count).ToArray());
        Assert.True(link.Tracks[0].Decodable);
    }

    // ---- SDP ----

    private const string BrowserHead = "v=0\r\no=- 1 2 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\na=group:BUNDLE 0\r\na=ice-ufrag:abcd\r\na=ice-pwd:0123456789012345678901\r\na=fingerprint:sha-256 AA:BB\r\na=setup:actpass\r\n" +
        "m=video 9 UDP/TLS/RTP/SAVPF ";
    private const string BrowserTail = "\r\nc=IN IP4 0.0.0.0\r\na=mid:0\r\na=sendrecv\r\na=rtcp-mux\r\n";

    private static string Fb(int pt) => $"a=rtcp-fb:{pt} goog-remb\r\na=rtcp-fb:{pt} transport-cc\r\na=rtcp-fb:{pt} ccm fir\r\na=rtcp-fb:{pt} nack\r\na=rtcp-fb:{pt} nack pli\r\n";

    private static async Task<string> Answer(string offer)
    {
        using var answerer = new RtcPeerConnectionController();
        answerer.SetRemoteDescription("offer", offer);
        return await answerer.CreateAnswerAsync(false, 0, 0);
    }

    [Fact]
    public async Task AnOfferListsVp8ThenBothH264VariantsWithFeedbackAndRtcpMux()
    {
        using var offerer = new RtcPeerConnectionController();
        var offer = await offerer.CreateOfferAsync(false, 0, 0, 0, 1);
        Assert.Contains("m=video 9 UDP/TLS/RTP/SAVPF 96 97 98\r\n", offer);
        Assert.Contains("a=rtpmap:96 VP8/90000\r\n", offer);
        Assert.Contains("a=rtpmap:97 H264/90000\r\n", offer);
        Assert.Contains("a=rtpmap:98 H264/90000\r\n", offer);
        Assert.Contains("a=fmtp:97 level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f\r\n", offer);
        Assert.Contains("a=fmtp:98 level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42001f\r\n", offer);
        foreach (var pt in new[] { 96, 97, 98 })
        {
            Assert.Contains($"a=rtcp-fb:{pt} nack\r\n", offer);
            Assert.Contains($"a=rtcp-fb:{pt} nack pli\r\n", offer);
            Assert.Contains($"a=rtcp-fb:{pt} ccm fir\r\n", offer);
        }
        Assert.Contains("a=rtcp-mux\r\n", offer);
        Assert.Contains("a=group:BUNDLE 0\r\n", offer);
        var parsed = RtcSdp.Parse(offer).Media[0];
        Assert.True(parsed.CodecFor(97)!.IsDecodableH264);
        Assert.True(parsed.CodecFor(98)!.IsDecodableH264);
        Assert.True(parsed.CodecFor(96)!.IsVideoDecodable);
    }

    [Fact]
    public async Task AnAnswerPrefersVp8ButFollowsTheOfferWhenOnlyH264IsOffered()
    {
        // Both offered, H.264 listed first: VP8 is still chosen.
        var both = await Answer(BrowserHead + "102 120" + BrowserTail + "a=rtpmap:102 H264/90000\r\na=fmtp:102 level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f\r\na=rtpmap:120 VP8/90000\r\n" + Fb(120));
        Assert.Contains("m=video 9 UDP/TLS/RTP/SAVPF 120\r\n", both);
        Assert.DoesNotContain("H264", both);

        // Only H.264: one payload type, the offered profile and mode, and only the feedback the offer lists for it.
        var only = await Answer(BrowserHead + "102 103" + BrowserTail +
            "a=rtpmap:102 H264/90000\r\na=fmtp:102 level-asymmetry-allowed=1;packetization-mode=0;profile-level-id=42e01f\r\n" +
            "a=rtpmap:103 H264/90000\r\na=fmtp:103 level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42001f\r\n" + Fb(103));
        Assert.Contains("m=video 9 UDP/TLS/RTP/SAVPF 103\r\n", only);   // mode 1 before mode 0
        Assert.Contains("a=rtpmap:103 H264/90000\r\n", only);
        Assert.Contains("a=fmtp:103 level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42001f\r\n", only);
        Assert.Contains("a=recvonly\r\n", only);
        Assert.Contains("a=rtcp-mux\r\n", only);
        Assert.Contains("a=rtcp-fb:103 nack\r\n", only);
        Assert.Contains("a=rtcp-fb:103 nack pli\r\n", only);
        Assert.Contains("a=rtcp-fb:103 ccm fir\r\n", only);
        Assert.DoesNotContain("goog-remb", only);
        Assert.DoesNotContain("VP8", only);
        Assert.DoesNotContain("a=rtpmap:102", only);
        Assert.Contains("a=group:BUNDLE 0\r\n", only);

        // Mode 0 alone is answered as mode 0; no fmtp at all means Baseline level 1 in mode 0.
        var mode0 = await Answer(BrowserHead + "102" + BrowserTail + "a=rtpmap:102 H264/90000\r\na=fmtp:102 packetization-mode=0;profile-level-id=42001f\r\n");
        Assert.Contains("a=fmtp:102 level-asymmetry-allowed=1;packetization-mode=0;profile-level-id=42001f\r\n", mode0);
        var bare = await Answer(BrowserHead + "102" + BrowserTail + "a=rtpmap:102 H264/90000\r\n");
        Assert.Contains("a=fmtp:102 level-asymmetry-allowed=1;packetization-mode=0;profile-level-id=42000a\r\n", bare);

        // Main and High profile payload types are not decodable and are never picked, even when listed first.
        var mixed = await Answer(BrowserHead + "101 102" + BrowserTail +
            "a=rtpmap:101 H264/90000\r\na=fmtp:101 level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=640c1f\r\n" +
            "a=rtpmap:102 H264/90000\r\na=fmtp:102 level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f\r\n");
        Assert.Contains("m=video 9 UDP/TLS/RTP/SAVPF 102\r\n", mixed);
        // Constrained Baseline signalled as Main (4d80) is.
        var asMain = await Answer(BrowserHead + "101" + BrowserTail + "a=rtpmap:101 H264/90000\r\na=fmtp:101 packetization-mode=1;profile-level-id=4d801f\r\n");
        Assert.Contains("m=video 9 UDP/TLS/RTP/SAVPF 101\r\n", asMain);
    }

    [Fact]
    public async Task AVideoSectionWithoutADecodableCodecIsRejectedAndLeavesTheBundleGroup()
    {
        foreach (var body in new[]
        {
            "120" + BrowserTail + "a=rtpmap:120 VP9/90000\r\n",
            "102" + BrowserTail + "a=rtpmap:102 H264/90000\r\na=fmtp:102 level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=64001f\r\n",   // High
            "102" + BrowserTail + "a=rtpmap:102 H264/90000\r\na=fmtp:102 packetization-mode=2;profile-level-id=42e01f\r\n",                               // interleaved
            "102" + BrowserTail + "a=rtpmap:102 H264/48000\r\na=fmtp:102 packetization-mode=1;profile-level-id=42e01f\r\n",                                // not the video clock
            "102" + BrowserTail + "a=rtpmap:102 H264/90000\r\na=fmtp:102 packetization-mode=1;profile-level-id=zzzzzz\r\n",
        })
        {
            var answer = await Answer(BrowserHead + body);
            Assert.Contains("m=video 0 ", answer);
            Assert.DoesNotContain("a=group:BUNDLE 0", answer);
        }
    }

    [Fact]
    public void ReceivingFollowsTheNegotiatedCodecEvenWhenTheListedOnesDiffer()
    {
        var parsed = RtcSdp.Parse("v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 96 97 98\r\na=rtpmap:96 VP8/90000\r\na=rtpmap:97 H264/90000\r\na=fmtp:97 packetization-mode=1;profile-level-id=640032\r\na=rtpmap:98 rtx/90000\r\n").Media[0];
        Assert.False(parsed.CodecFor(97)!.IsDecodableH264);
        Assert.True(parsed.CodecFor(97)!.IsH264);
        Assert.False(parsed.CodecFor(98)!.IsVideoDecodable);
        // A section listing only an undecodable H.264 payload type is negotiated but yields no decodable track.
        using var link = new H264Link(Local().Replace("profile-level-id=42e01f", "profile-level-id=640032"), Remote().Replace("profile-level-id=42e01f", "profile-level-id=640032"));
        Assert.False(link.Tracks[0].Decodable);
        link.Send(link.Packetize(Units(H264Fixtures.QcifLife())[0]));
        Assert.Empty(link.Delivered);
    }

    // ---- budgets ----

    [Fact]
    public void InactivePortZeroByeAndDisposeReleaseTheDecoderMemory()
    {
        var units = Units(H264Fixtures.QcifLife());
        var budget = new RtcMediaBudget();
        using var link = new H264Link(budget: budget);
        link.Send(link.Packetize(units[0]));
        Assert.True(budget.VideoBytesInUse > 0);
        link.Engine.Apply(null, RtcSdp.Parse(Remote().Replace("a=sendonly", "a=inactive")));
        Assert.Equal(0, budget.VideoBytesInUse);
        link.Send(link.Packetize(units[0]));
        Assert.Single(link.Delivered);                     // nothing is decoded while it is inactive
        link.Engine.Apply(null, RtcSdp.Parse(Remote()));
        link.Send(link.Packetize(units[0]));
        Assert.Equal(2, link.Delivered.Count);
        Assert.True(budget.VideoBytesInUse > 0);
        link.Engine.Apply(null, RtcSdp.Parse("v=0\r\nm=video 0 UDP/TLS/RTP/SAVPF 97\r\na=mid:v\r\n"));
        Assert.Equal(0, budget.VideoBytesInUse);

        var byeBudget = new RtcMediaBudget();
        using var other = new H264Link(budget: byeBudget);
        other.Send(other.Packetize(units[0]));
        Assert.True(byeBudget.VideoBytesInUse > 0);
        var ended = false;
        other.Tracks[0].Ended += () => ended = true;
        other.Engine.ProcessDatagram(other.Sender.ProtectRtcp(RtcpCodec.BuildBye(Ssrc)));
        Assert.True(ended);
        Assert.Equal(0, byeBudget.VideoBytesInUse);

        var closeBudget = new RtcMediaBudget();
        var closing = new H264Link(budget: closeBudget);
        closing.Send(closing.Packetize(units[0]));
        Assert.True(closeBudget.VideoBytesInUse > 0);
        closing.Dispose();
        Assert.Equal(0, closeBudget.VideoBytesInUse);
    }

    [Fact]
    public void TheDocumentMemoryBudgetAndThePictureSizeCapStopTheDecoderWithoutAFailure()
    {
        var units = Units(H264Fixtures.QcifLife()); // 128x96 = 48 macroblocks
        // Too little memory for even this small picture: nothing is decoded, nothing stays reserved, and a key frame is requested.
        var tiny = new RtcMediaBudget(maxVideoBytes: 10_000);
        using (var link = new H264Link(budget: tiny))
        {
            foreach (var unit in units) link.Send(link.Packetize(unit));
            Assert.Empty(link.Delivered);
            Assert.Equal(0, tiny.VideoBytesInUse);
            link.Advance(1000);
            Assert.Contains(link.Feedback(), p => p.Kind == RtcpKind.PictureLossIndication);
            Assert.Equal(0, link.Tracks[0].VideoUnexpectedErrors);
        }
        // A picture above the document's pixel cap is refused the same way.
        var small = new RtcMediaBudget(maxVideoPixels: 64 * 64);
        using (var link = new H264Link(budget: small))
        {
            foreach (var unit in units) link.Send(link.Packetize(unit));
            Assert.Empty(link.Delivered);
            Assert.Equal(0, small.VideoBytesInUse);
            Assert.Equal(0, link.Tracks[0].VideoUnexpectedErrors);
        }
        // Enough for one stream but not for two: the second reservation fails and leaves the first intact.
        var shared = new RtcMediaBudget(maxVideoBytes: 48L * (384 * 10 + 3 * 384 + 1024) + 1000);
        using var first = new H264Link(budget: shared);
        first.Send(first.Packetize(units[0]));
        var inUse = shared.VideoBytesInUse;
        Assert.True(inUse > 0);
        using var second = new H264Link(budget: shared);
        second.Send(second.Packetize(units[0]));
        Assert.Empty(second.Delivered);
        Assert.Equal(inUse, shared.VideoBytesInUse);
    }

    [Fact]
    public void DecodeWorkIsBoundedByAPixelRateAndTheDecoderCallBudgets()
    {
        var units = Units(H264Fixtures.QcifLife());
        var time = new ManualTime();
        // Two pictures' worth of pixels per second: a burst of IDR pictures at one instant is mostly refused.
        using var receiver = new H264Receiver(new H264Decoder(), time, maxPixels: 128 * 96, maxPixelsPerSecond: 128 * 96 * 2);
        var output = new VideoReceiverOutput();
        ushort sequence = 10; uint timestamp = 1000;
        for (var i = 0; i < 40; i++)
        {
            foreach (var nal in units[0]) { receiver.Push(sequence, timestamp, nal == units[0][^1], nal, time.GetTimestamp(), output); sequence++; }
            timestamp += 3000;
        }
        Assert.True(receiver.FramesRateLimited > 0);
        Assert.InRange(receiver.FramesDecoded, 1, 3);

        // A per-call work budget of one macroblock makes the decoder stop early; that is counted, and an IDR picture is needed again.
        var stopped = new H264Decoder(new H264DecoderLimits { MaxWorkMacroblocksPerCall = 1 });
        using var starved = new H264Receiver(stopped, time, 1 << 20);
        var o2 = new VideoReceiverOutput();
        ushort s2 = 0;
        foreach (var nal in units[0]) { starved.Push(s2, 5, nal == units[0][^1], nal, time.GetTimestamp(), o2); s2++; }
        Assert.True(starved.BudgetStops > 0);
        Assert.Equal(0, starved.UnexpectedErrors);
        Assert.Empty(o2.Frames);
        Assert.True(starved.WaitingForKeyFrame);
    }

    private static List<byte[]> Nals(List<byte[]> unit, params int[] types) => unit.Where(n => types.Contains(n[0] & 0x1F)).ToList();

    [Fact]
    public void ParameterSetsInAnAccessUnitOfTheirOwnReachTheDecoderBeforeTheIdrPicture()
    {
        var units = Units(H264Fixtures.QcifLife());
        var expected = Reference(units);
        using var link = new H264Link();
        link.Send(link.Packetize(Nals(units[0], 7, 8)));                // SPS and PPS, marker-terminated, no slice
        Assert.Empty(link.Delivered);
        link.Send(link.Packetize(Nals(units[0], 1, 5)));                // the IDR slice without parameter sets
        AssertFrames(expected, link.Delivered, 0);
        Assert.Equal(1, link.Tracks[0].KeyFramesDecoded);
        Assert.Equal(0, link.Tracks[0].VideoDecodeErrors);
    }

    [Fact]
    public void AStreamJoinedInTheMiddleOfAnIdrPictureShowsNothingConcealedAndWaitsForACompleteOne()
    {
        var units = Units(H264Fixtures.CroppedSlices());                // four slices per picture, IDR every fourth
        var expected = Reference(units);
        using var link = new H264Link();
        link.Send(link.Packetize(Nals(units[0], 7, 8)));
        var slices = Nals(units[0], 5);
        Assert.True(slices.Count >= 2);
        link.Send(link.Packetize(slices.Skip(1).ToList()));             // the first slice never arrived: no sequence gap shows it
        Assert.Empty(link.Delivered);                                   // the picture would have been concealed: not shown
        Assert.Equal(1, link.Tracks[0].VideoDecodeErrors);
        link.Send(link.Packetize(units[1]));                            // a P picture on top of it is not shown either
        Assert.Empty(link.Delivered);
        link.Advance(150);
        Assert.Contains(link.Feedback(), p => p.Kind == RtcpKind.PictureLossIndication);
        foreach (var unit in units.Skip(4)) link.Send(link.Packetize(unit));
        AssertFrames(expected, link.Delivered, 4, 5, 6, 7);
        Assert.Equal(0, link.Tracks[0].VideoUnexpectedErrors);
    }

    [Fact]
    public void AnUnparsablePacketizationModeOrProfileLevelIdIsRefusedNotDefaulted()
    {
        static RtcCodec Codec(string fmtp) => RtcSdp.Parse("v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 102\r\na=rtpmap:102 H264/90000\r\na=fmtp:102 " + fmtp + "\r\n").Media[0].CodecFor(102)!;
        Assert.True(Codec("packetization-mode=1;profile-level-id=42e01f").IsDecodableH264);
        Assert.True(Codec("profile-level-id=42E01F").IsDecodableH264);                  // case does not matter, mode defaults to 0
        Assert.True(Codec("packetization-mode=0").IsDecodableH264);                     // absent profile means 42000a
        foreach (var bad in new[] { "packetization-mode=one", "packetization-mode=", "packetization-mode=01", "packetization-mode=-1", "packetization-mode=1.0", "packetization-mode=2", "packetization-mode=10" })
            Assert.False(Codec(bad + ";profile-level-id=42e01f").IsDecodableH264, bad);
        foreach (var bad in new[] { "profile-level-id=", "profile-level-id=42e01", "profile-level-id=42e01f0", "profile-level-id=42e0zz", "profile-level-id=+2e01f", "profile-level-id=0x42e01", "profile-level-id=42e 01" })
            Assert.False(Codec("packetization-mode=1;" + bad).IsDecodableH264, bad);
    }

    [Fact]
    public async Task AnAnswerRejectsASectionWhoseOnlyH264PayloadHasAMalformedFmtp()
    {
        foreach (var fmtp in new[] { "packetization-mode=one;profile-level-id=42e01f", "packetization-mode=1;profile-level-id=42e01" })
        {
            var answer = await Answer(BrowserHead + "102" + BrowserTail + "a=rtpmap:102 H264/90000\r\na=fmtp:102 " + fmtp + "\r\n");
            Assert.Contains("m=video 0 ", answer);
        }
    }

    // ---- hostile input ----

    [Fact]
    public void FuaWithoutAStartAndOtherMalformedPayloadsAreDroppedWithoutAFrameOrAnException()
    {
        var units = Units(H264Fixtures.QcifLife());
        var expected = Reference(units);
        using var link = new H264Link();
        var payloads = new List<byte[]>
        {
            new byte[] { 0x7C, 0x05, 1, 2, 3, 4 },                      // FU-A continuation, its start was never seen
            new byte[] { 0x7C, 0x45, 1, 2, 3 },                         // FU-A end without a start
            new byte[] { 0x7C, 0xC5, 1, 2, 3 },                         // start and end both set
            new byte[] { 0x7C },                                        // truncated FU-A
            new byte[] { 0x78, 0x00, 0x09, 1 },                         // STAP-A whose size overruns the packet
            new byte[] { 0x78 },                                        // empty STAP-A
            new byte[] { 0xFC, 0x85, 9 },                               // forbidden_zero_bit
            new byte[] { 0x79, 1, 2 },                                  // FU-B (type 29, interleaved): unsupported
            new byte[] { 0x00, 1, 2 },                                  // NAL type 0
            new byte[] {  },                                            // empty
        };
        foreach (var payload in payloads) link.Send([link.Rtp(payload, true, link.Timestamp)]);
        link.Timestamp += 3000;
        Assert.Empty(link.Delivered);
        // The stream is still usable afterwards: the real pictures decode.
        foreach (var unit in units) link.Send(link.Packetize(unit));
        link.Advance(500);
        Assert.True(link.Delivered.Count > 0);
        Assert.Equal(0, link.Tracks[0].VideoUnexpectedErrors);
        Assert.True(expected[^1].AsSpan().SequenceEqual(link.Delivered[^1].Rgba.Span));
    }

    [Fact]
    public void ARandomFloodAndEndlessFragmentsKeepTheBuffersBoundedAndNeverReachTheCatchAll()
    {
        var units = Units(H264Fixtures.QcifLife());
        var random = new Random(11);
        var time = new ManualTime();
        using var receiver = new H264Receiver(new H264Decoder(), time, 1920 * 1088);
        var output = new VideoReceiverOutput();
        // A fragmented NAL that never ends: far beyond the 2 MiB access unit limit.
        var filler = new byte[1001];
        for (var i = 0; i < 3000; i++)
        {
            filler[0] = 0x7C; filler[1] = (byte)(i == 0 ? 0x85 : 0x05);
            receiver.Push(10 + i, 77, false, filler, time.GetTimestamp(), output);
            Assert.True(receiver.BufferedPackets <= RtpVideoReceiver.MaxBufferedPackets);
        }
        // Reordered garbage inside the window, with random markers and timestamps.
        for (var i = 0; i < 3000; i++)
        {
            var payload = new byte[random.Next(0, 300)];
            random.NextBytes(payload);
            receiver.Push(4000 + random.Next(0, 400), (uint)random.Next(0, 5), random.Next(2) == 0, payload, time.GetTimestamp(), output);
            if (i % 100 == 0) { time.Advance(100); receiver.Tick(time.GetTimestamp(), output); }
            Assert.True(receiver.BufferedPackets <= RtpVideoReceiver.MaxBufferedPackets);
        }
        // A jump far beyond the window restarts the stream instead of buffering for ever.
        receiver.Push(4000 + 1_000_000, 88, true, filler, time.GetTimestamp(), output);
        Assert.True(receiver.BufferedPackets <= 2);
        Assert.True(receiver.WaitingForKeyFrame);
        Assert.Equal(0, receiver.UnexpectedErrors);
        // After all that a real stream still decodes. The first picture after the discontinuity is dropped (its head may have been lost
        // with the buffer), the next IDR picture is shown.
        var sequence = 5_000_000L; uint ts = 9000;
        for (var round = 0; round < 2; round++, ts += 3000)
            foreach (var nal in units[0]) receiver.Push(sequence++, ts, nal == units[0][^1], nal, time.GetTimestamp(), output);
        Assert.NotEmpty(output.Frames);
        Assert.Equal(128, output.Frames[^1].Width);
        Assert.Equal(0, receiver.UnexpectedErrors);
    }

    [Fact]
    public void RandomPayloadsBehindValidRtpHeadersNeverBreakTheEngine()
    {
        var units = Units(H264Fixtures.QcifLife());
        var random = new Random(7);
        using var link = new H264Link();
        link.Send(link.Packetize(units[0]));
        var delivered = link.Delivered.Count;
        for (var i = 0; i < 400; i++)
        {
            var payload = new byte[random.Next(0, 1100)];
            random.NextBytes(payload);
            link.Engine.ProcessDatagram(link.Rtp(payload, random.Next(2) == 0, (uint)random.Next()).Datagram);
            if (i % 50 == 0) link.Advance(100);
        }
        link.Advance(2000);
        Assert.True(link.Delivered.Count >= delivered);
        Assert.Equal(0, link.Tracks[0].VideoUnexpectedErrors);
        link.Engine.Dispose(); // no exception, nothing left over
        Assert.Equal(0, link.Budget.VideoBytesInUse);
    }

    [Fact]
    public void AStreamSwitchingToAnotherCodecOnTheSameSsrcGetsAFreshReceiver()
    {
        var units = Units(H264Fixtures.QcifLife());
        var both = "v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 96 97\r\na=mid:v\r\na=recvonly\r\na=rtcp-mux\r\na=rtpmap:96 VP8/90000\r\na=rtpmap:97 H264/90000\r\n" +
            "a=fmtp:97 packetization-mode=1;profile-level-id=42e01f\r\n";
        using var link = new H264Link(both, both.Replace("a=recvonly", "a=sendonly"));
        link.Send(link.Packetize(units[0]));
        Assert.Single(link.Delivered);
        Assert.True(link.Budget.VideoBytesInUse > 0);
        // The same SSRC now carries the other codec (a renegotiation): the H.264 decoder and its budget go, a new receiver takes over.
        link.Send([link.Rtp([0x10, 1, 2, 3, 4], true, link.Timestamp, payloadType: 96)]);
        Assert.Equal(0, link.Budget.VideoBytesInUse);
        Assert.Single(link.Delivered);
        // And back: H.264 decodes again from its next IDR picture.
        link.Timestamp += 3000;
        link.Send(link.Packetize(units[0]));
        Assert.Equal(2, link.Delivered.Count);
        Assert.True(link.Budget.VideoBytesInUse > 0);
    }
}
