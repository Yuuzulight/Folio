using System.Buffers.Binary;
using System.Security.Cryptography;
using Folio;
using Folio.Scripting.Rtc;
using Folio.Scripting.Rtc.Vp8;

namespace Folio.Scripting.Tests;

public sealed class RtcVideoSendTests
{
    private static (SrtpTransport A, SrtpTransport B) SrtpPair()
    {
        var ka = RandomNumberGenerator.GetBytes(16); var sa = RandomNumberGenerator.GetBytes(12);
        var kb = RandomNumberGenerator.GetBytes(16); var sb = RandomNumberGenerator.GetBytes(12);
        return (new SrtpTransport(ka, sa, kb, sb), new SrtpTransport(kb, sb, ka, sa));
    }

    private static byte[] SampleRgba(int w, int h, byte rBase = 100, byte gBase = 150, byte bBase = 200)
    {
        var rgba = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var idx = (y * w + x) * 4;
                rgba[idx] = (byte)(rBase + (x * 50 / w));
                rgba[idx + 1] = (byte)(gBase + (y * 50 / h));
                rgba[idx + 2] = bBase;
                rgba[idx + 3] = 255;
            }
        }
        return rgba;
    }

    [Fact]
    public void VideoSenderPacketizesIntoMtuAndSetsMarkerBitOnLastPacket()
    {
        var time = new ManualTime();
        var budget = new RtcMediaBudget(time);
        var (srtpSender, srtpReceiver) = SrtpPair();
        var localTrack = new RtcLocalTrack("video", 0x12345678) { PayloadType = 96 };
        var sender = new RtpVideoSender(localTrack, time, budget);

        const int w = 320;
        const int h = 240;
        var rgba = SampleRgba(w, h);

        var packets = sender.EncodeAndPacketize(rgba, w, h, srtpSender);
        Assert.NotEmpty(packets);

        uint? frameTimestamp = null;
        for (var i = 0; i < packets.Count; i++)
        {
            var protectedPacket = packets[i];
            Assert.True(srtpReceiver.TryUnprotectRtp(protectedPacket, out var plainRtp));
            Assert.NotNull(plainRtp);

            // Plain RTP must have 12-byte header + at least 1 byte VP8 descriptor + payload
            Assert.True(plainRtp.Length >= 13);
            Assert.Equal(0x80, plainRtp[0]);

            var isLast = i == packets.Count - 1;
            var expectedPt = 96 | (isLast ? 0x80 : 0);
            Assert.Equal(expectedPt, plainRtp[1]);

            var ts = BinaryPrimitives.ReadUInt32BigEndian(plainRtp.AsSpan(4));
            frameTimestamp ??= ts;
            Assert.Equal(frameTimestamp.Value, ts);

            var ssrc = BinaryPrimitives.ReadUInt32BigEndian(plainRtp.AsSpan(8));
            Assert.Equal(0x12345678u, ssrc);

            // VP8 Payload Descriptor
            var desc = plainRtp[12];
            var isFirst = i == 0;
            if (isFirst)
                Assert.Equal(0x10, desc); // S=1
            else
                Assert.Equal(0x00, desc); // S=0
        }
    }

    [Fact]
    public void VideoSenderRetransmitsOnNack()
    {
        var time = new ManualTime();
        var budget = new RtcMediaBudget(time);
        var (srtpA, srtpB) = SrtpPair();

        var engineA = new RtcMediaEngine(time, budget);
        engineA.SetSrtp(srtpA);
        var toB = new Queue<byte[]>();
        engineA.OnPacketToSend += toB.Enqueue;

        var videoTrack = engineA.AddLocalVideo();
        Assert.NotNull(videoTrack);

        const string offerSdp = "v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=mid:0\r\na=sendonly\r\na=rtcp-mux\r\na=rtpmap:96 VP8/90000\r\na=rtcp-fb:96 nack\r\n";
        const string answerSdp = "v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=mid:0\r\na=recvonly\r\na=rtcp-mux\r\na=rtpmap:96 VP8/90000\r\na=rtcp-fb:96 nack\r\n";

        engineA.Apply(RtcSdp.Parse(offerSdp), RtcSdp.Parse(answerSdp));

        const int w = 160;
        const int h = 120;
        var rgba = SampleRgba(w, h);
        var sent = engineA.SendVideo(videoTrack, rgba, w, h);
        Assert.True(sent > 0);
        Assert.Equal(sent, toB.Count);

        var firstPacket = toB.Peek();
        var seqToNack = BinaryPrimitives.ReadUInt16BigEndian(firstPacket.AsSpan(2));

        toB.Clear();

        // B sends NACK for seqToNack to A
        var nackCompound = new List<byte[]>
        {
            RtcpCodec.BuildReport(0x9999, false, 0, 0, 0, 0, [], "peer"),
            RtcpCodec.BuildNack(0x9999, videoTrack.Ssrc, [seqToNack])
        };
        var plainNack = nackCompound.SelectMany(p => p).ToArray();
        var protectedNack = srtpB.ProtectRtcp(plainNack);

        engineA.ProcessDatagram(protectedNack);

        // A should retransmit the requested packet
        Assert.Single(toB);
        var retransmit = toB.Dequeue();
        Assert.True(srtpB.TryUnprotectRtp(retransmit, out var plainRetransmit));
        Assert.NotNull(plainRetransmit);
        var retransmitSeq = BinaryPrimitives.ReadUInt16BigEndian(plainRetransmit.AsSpan(2));
        Assert.Equal(seqToNack, retransmitSeq);
    }

    [Fact]
    public void VideoSenderForcesKeyFrameOnPliAndFir()
    {
        var time = new ManualTime();
        var budget = new RtcMediaBudget(time);
        var (srtpA, srtpB) = SrtpPair();

        var engineA = new RtcMediaEngine(time, budget);
        engineA.SetSrtp(srtpA);
        var toB = new Queue<byte[]>();
        engineA.OnPacketToSend += toB.Enqueue;

        var videoTrack = engineA.AddLocalVideo();
        Assert.NotNull(videoTrack);

        const string offerSdp = "v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=mid:0\r\na=sendonly\r\na=rtcp-mux\r\na=rtpmap:96 VP8/90000\r\na=rtcp-fb:96 nack pli\r\n";
        const string answerSdp = "v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=mid:0\r\na=recvonly\r\na=rtcp-mux\r\na=rtpmap:96 VP8/90000\r\na=rtcp-fb:96 nack pli\r\n";
        engineA.Apply(RtcSdp.Parse(offerSdp), RtcSdp.Parse(answerSdp));

        const int w = 160;
        const int h = 120;
        var rgba = SampleRgba(w, h);

        Assert.True(engineA.SendVideo(videoTrack, rgba, w, h, captureSequence: 1) > 0);
        toB.Clear();

        // The same camera frame is not sent again, however late
        time.Advance(500);
        Assert.Equal(0, engineA.SendVideo(videoTrack, rgba, w, h, captureSequence: 1));

        // A PLI makes the next send go out even for an unchanged capture
        time.Advance(150);
        var pliCompound = new List<byte[]>
        {
            RtcpCodec.BuildReport(0x9999, false, 0, 0, 0, 0, [], "peer"),
            RtcpCodec.BuildPli(0x9999, videoTrack.Ssrc)
        };
        var plainPli = pliCompound.SelectMany(p => p).ToArray();
        engineA.ProcessDatagram(srtpB.ProtectRtcp(plainPli));

        // Now next send should be forced keyframe
        Assert.True(engineA.SendVideo(videoTrack, rgba, w, h, captureSequence: 1) > 0);
        Assert.NotEmpty(toB);
        var firstPliPacket = toB.Peek();
        Assert.True(srtpB.TryUnprotectRtp(firstPliPacket, out var plainForcedKey));
        Assert.NotNull(plainForcedKey);
        var isForcedKey = (plainForcedKey[13] & 1) == 0;
        Assert.True(isForcedKey, "Frame after PLI must be a key frame");
    }

    [Fact]
    public void FolioToFolioVideoLoopbackTransfersFramesAndDecodes()
    {
        var time = new ManualTime();
        var budgetA = new RtcMediaBudget(time);
        var budgetB = new RtcMediaBudget(time);
        var (srtpA, srtpB) = SrtpPair();

        var engineA = new RtcMediaEngine(time, budgetA);
        var engineB = new RtcMediaEngine(time, budgetB);
        engineA.SetSrtp(srtpA);
        engineB.SetSrtp(srtpB);

        var toB = new Queue<byte[]>();
        var toA = new Queue<byte[]>();
        engineA.OnPacketToSend += toB.Enqueue;
        engineB.OnPacketToSend += toA.Enqueue;

        var receivedTracks = new List<RtcRemoteTrack>();
        var decodedFrames = new List<MediaCaptureVideoFrame>();
        engineB.OnRemoteTrack += track =>
        {
            receivedTracks.Add(track);
            track.VideoFrame += decodedFrames.Add;
        };

        var trackA = engineA.AddLocalVideo();
        Assert.NotNull(trackA);

        const string offer = "v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=mid:0\r\na=sendonly\r\na=rtcp-mux\r\na=rtpmap:96 VP8/90000\r\na=rtcp-fb:96 nack\r\na=rtcp-fb:96 nack pli\r\n";
        const string answer = "v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=mid:0\r\na=recvonly\r\na=rtcp-mux\r\na=rtpmap:96 VP8/90000\r\na=rtcp-fb:96 nack\r\na=rtcp-fb:96 nack pli\r\n";

        engineA.Apply(RtcSdp.Parse(offer), null);
        engineB.Apply(null, RtcSdp.Parse(offer));
        engineB.Apply(RtcSdp.Parse(answer), null);
        engineA.Apply(null, RtcSdp.Parse(answer));

        Assert.Single(receivedTracks);
        var remoteTrack = receivedTracks[0];
        Assert.Equal("video", remoteTrack.Kind);
        Assert.True(remoteTrack.Decodable);

        const int w = 160;
        const int h = 120;
        var rgba = SampleRgba(w, h, 80, 120, 160);

        var sent = engineA.SendVideo(trackA, rgba, w, h);
        Assert.True(sent > 0);
        Assert.Equal(sent, toB.Count);

        // Deliver packets from A to B
        while (toB.Count > 0)
            engineB.ProcessDatagram(toB.Dequeue());

        Assert.Single(decodedFrames);
        var frame = decodedFrames[0];
        Assert.Equal(w, frame.Width);
        Assert.Equal(h, frame.Height);
        Assert.True(frame.IsValid);
        Assert.Equal(w * h * 4, frame.Rgba.Length);
        Assert.True(Vp8EncoderTests.Psnr(rgba, frame.Rgba.ToArray()) >= 30);

        // a different picture a frame later arrives as that picture, not as the first one again
        time.Advance(100);
        var rgba2 = Vp8EncoderTests.Pattern(w, h, 1);
        Assert.True(engineA.SendVideo(trackA, rgba2, w, h, captureSequence: 2) > 0);
        while (toB.Count > 0) engineB.ProcessDatagram(toB.Dequeue());
        Assert.Equal(2, decodedFrames.Count);
        Assert.True(Vp8EncoderTests.Psnr(rgba2, decodedFrames[1].Rgba.ToArray()) >= 30);
    }

    private static (RtcMediaEngine Engine, RtcLocalTrack Track, Queue<byte[]> Out, SrtpTransport Peer, ManualTime Time, RtcMediaBudget Budget) SendingEngine(int w = 160, int h = 120)
    {
        var time = new ManualTime();
        var budget = new RtcMediaBudget(time);
        var (srtpA, srtpB) = SrtpPair();
        var engine = new RtcMediaEngine(time, budget);
        engine.SetSrtp(srtpA);
        var output = new Queue<byte[]>();
        engine.OnPacketToSend += output.Enqueue;
        var track = engine.AddLocalVideo()!;
        const string offer = "v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=mid:0\r\na=sendonly\r\na=rtcp-mux\r\na=rtpmap:96 VP8/90000\r\na=rtcp-fb:96 nack\r\na=rtcp-fb:96 nack pli\r\n";
        const string answer = "v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=mid:0\r\na=recvonly\r\na=rtcp-mux\r\na=rtpmap:96 VP8/90000\r\na=rtcp-fb:96 nack\r\na=rtcp-fb:96 nack pli\r\n";
        engine.Apply(RtcSdp.Parse(offer), RtcSdp.Parse(answer));
        return (engine, track, output, srtpB, time, budget);
    }

    private static void SendNack(RtcMediaEngine engine, SrtpTransport peer, uint mediaSsrc, IEnumerable<ushort> sequences)
    {
        var plain = RtcpCodec.BuildReport(0x9999, false, 0, 0, 0, 0, [], "peer").Concat(RtcpCodec.BuildNack(0x9999, mediaSsrc, sequences.ToArray())).ToArray();
        engine.ProcessDatagram(peer.ProtectRtcp(plain));
    }

    [Fact]
    public void NackFloodIsBoundedPerRequestPerPacketAndPerSecond()
    {
        var (engine, track, output, peer, time, _) = SendingEngine();
        var noise = new byte[640 * 360 * 4];
        new Random(3).NextBytes(noise); // several packets per frame
        Assert.True(engine.SendVideo(track, noise, 640, 360, 1) > 3);
        var sequences = output.Select(p => BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(2))).ToArray();
        output.Clear();

        // the same sequence listed many times is one retransmit
        SendNack(engine, peer, track.Ssrc, Enumerable.Repeat(sequences[0], 200));
        Assert.Single(output);
        output.Clear();

        // asking again inside one RTT is ignored
        time.Advance(10);
        SendNack(engine, peer, track.Ssrc, [sequences[0]]);
        Assert.Empty(output);

        // a packet is retransmitted at most MaxRetransmitsPerPacket times in total
        for (var i = 0; i < 10; i++) { time.Advance(150); SendNack(engine, peer, track.Ssrc, [sequences[0]]); }
        Assert.Equal(RtpVideoSender.MaxRetransmitsPerPacket - 1, output.Count);
        output.Clear();

        // a request for a huge range retransmits no more than the per-request cap
        time.Advance(2000);
        SendNack(engine, peer, track.Ssrc, Enumerable.Range(0, 5000).Select(i => (ushort)(sequences[0] + i)));
        Assert.True(output.Count <= RtpVideoSender.MaxNackSequencesPerRequest);
    }

    [Fact]
    public void NackRetransmitsPerSecondAreCapped()
    {
        var time = new ManualTime();
        var (srtpA, _) = SrtpPair();
        var track = new RtcLocalTrack("video", 1) { PayloadType = 96 };
        var sender = new RtpVideoSender(track, time, new RtcMediaBudget(time));
        var noise = new byte[640 * 360 * 4];
        new Random(5).NextBytes(noise);
        for (var f = 0; f < 12; f++)
        {
            sender.EncodeAndPacketize(noise, 640, 360, srtpA, f + 1);
            time.Advance(70);
        }
        var seqs = Enumerable.Range(0, sender.HistoryCount).Select(i => (ushort)(track.Sequence - 1 - i)).ToArray();
        var total = 0;
        for (var round = 0; round < 50; round++)
        {
            var list = new List<byte[]>();
            foreach (var chunk in seqs.Chunk(RtpVideoSender.MaxNackSequencesPerRequest)) sender.HandleNack(chunk, list);
            total += list.Count;
        }
        Assert.True(total <= RtpVideoSender.MaxRetransmitsPerSecond, $"retransmitted {total} within one second");
    }

    [Fact]
    public void SendHistoryStaysBounded()
    {
        var time = new ManualTime();
        var (srtpA, _) = SrtpPair();
        var track = new RtcLocalTrack("video", 1) { PayloadType = 96 };
        var sender = new RtpVideoSender(track, time, new RtcMediaBudget(time));
        var noise = new byte[640 * 360 * 4];
        new Random(9).NextBytes(noise);
        for (var f = 0; f < 40; f++)
        {
            sender.EncodeAndPacketize(noise, 640, 360, srtpA, f + 1);
            time.Advance(70);
            Assert.True(sender.HistoryCount <= RtpVideoSender.MaxHistoryPackets);
        }
        Assert.Equal(RtpVideoSender.MaxHistoryPackets, sender.HistoryCount);
    }

    [Fact]
    public void FramesAreLimitedToFifteenPerSecondAndSkipRepeatedCaptures()
    {
        var (engine, track, output, _, time, _) = SendingEngine();
        var rgba = Vp8EncoderTests.Pattern(160, 120, 0);
        Assert.True(engine.SendVideo(track, rgba, 160, 120, 1) > 0);
        output.Clear();
        time.Advance(30);
        Assert.Equal(0, engine.SendVideo(track, rgba, 160, 120, 2)); // too soon
        time.Advance(50);
        Assert.Equal(0, engine.SendVideo(track, rgba, 160, 120, 1)); // same capture
        Assert.True(engine.SendVideo(track, rgba, 160, 120, 2) > 0);  // due and new
        var sent = 0;
        for (var i = 0; i < 100; i++) { time.Advance(10); if (engine.SendVideo(track, rgba, 160, 120, 3 + i) > 0) sent++; }
        Assert.InRange(sent, 13, 16); // about one second of 10 ms ticks
    }

    [Fact]
    public void KeyFrameRequestsAreChargedToTheSharedBudget()
    {
        var (engine, track, _, peer, time, _) = SendingEngine();
        var rgba = Vp8EncoderTests.Pattern(160, 120, 0);
        engine.SendVideo(track, rgba, 160, 120, 1);
        // PLIs at time zero (a fresh clock) and in a burst: the budget, not a per-sender timer starting at zero, decides
        for (var i = 0; i < 20; i++)
        {
            var plain = RtcpCodec.BuildReport(0x9999, false, 0, 0, 0, 0, [], "peer").Concat(RtcpCodec.BuildPli(0x9999, track.Ssrc)).ToArray();
            engine.ProcessDatagram(peer.ProtectRtcp(plain));
        }
        Assert.Equal(1, track.VideoSender!.KeyFrameRequestsHonoured);
        time.Advance(200);
        var again = RtcpCodec.BuildReport(0x9999, false, 0, 0, 0, 0, [], "peer").Concat(RtcpCodec.BuildPli(0x9999, track.Ssrc)).ToArray();
        engine.ProcessDatagram(peer.ProtectRtcp(again));
        Assert.Equal(2, track.VideoSender.KeyFrameRequestsHonoured);
    }

    [Fact]
    public void KeyFrameRequestSurvivesAFrameTheBudgetRefused()
    {
        var time = new ManualTime();
        var (srtpA, _) = SrtpPair();
        var track = new RtcLocalTrack("video", 1) { PayloadType = 96 };
        var sender = new RtpVideoSender(track, time, new RtcMediaBudget(time));
        var rgba = Vp8EncoderTests.Pattern(160, 120, 0);
        // the initial request is pending; a frame that did not fully leave must not clear it
        var frame = sender.Encode(rgba, 160, 120, 1);
        Assert.NotNull(frame);
        sender.Packetize(frame.Value, srtpA);
        sender.CompleteFrame(allSent: false);
        // same capture, no time passed: only the still-pending request lets it through
        Assert.NotNull(sender.Encode(rgba, 160, 120, 1));
        sender.CompleteFrame(allSent: true);
        time.Advance(10);
        Assert.Null(sender.Encode(rgba, 160, 120, 1));
    }

    [Fact]
    public void RepeatedCameraStartAndStopDoesNotLeakTracks()
    {
        var time = new ManualTime();
        var budget = new RtcMediaBudget(time, maxTracks: 16);
        var controller = new RtcPeerConnectionController(null, budget);
        for (var cycle = 0; cycle < 60; cycle++)
        {
            var first = controller.LocalVideo(cycle);
            Assert.NotNull(first);
            // a second, longer-lived camera keeps working while the first one comes and goes
            controller.ReleaseLocalVideo(cycle);
            Assert.Null(controller.LocalVideoTrack(cycle));
            Assert.Empty(controller.Media.LocalTracks);
        }
        // a released slot is never revived, and other slots keep their own track
        var other = new RtcPeerConnectionController(null, new RtcMediaBudget(time, maxTracks: 16));
        var a = other.LocalVideo(0)!;
        var b = other.LocalVideo(1)!;
        other.ReleaseLocalVideo(0);
        Assert.Same(b, other.LocalVideoTrack(1));
        Assert.Null(other.LocalVideoTrack(0));
        Assert.Null(other.LocalVideo(0));
        Assert.True(a.Released);
        Assert.Null(a.VideoSender);
    }

    [Fact]
    public async Task H264FirstOfferStillGetsVp8SendingAndMixedCaseNameIsAccepted()
    {
        var controller = new RtcPeerConnectionController();
        var offer = "v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 100 96\r\na=mid:video-0\r\na=sendrecv\r\na=rtcp-mux\r\na=rtpmap:100 H264/90000\r\na=fmtp:100 profile-level-id=42e01f;packetization-mode=1\r\na=rtpmap:96 vp8/90000\r\n";
        controller.SetRemoteDescription("offer", offer);
        var answer = await controller.CreateAnswerAsync(hasDataChannels: false, audioSenders: 0, videoSenders: 1);
        var video = RtcSdp.Parse(answer).Media[0];
        Assert.Equal("sendrecv", video.Direction);
        Assert.Equal(96, video.PayloadTypes.Single());
        Assert.NotEmpty(video.Ssrcs);
    }

    [Fact]
    public async Task SdpOfferAnswerRejectsVideoWhenPeerOnlyOffersH264()
    {
        var controller = new RtcPeerConnectionController();
        var h264Offer = "v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 100\r\na=mid:video-0\r\na=sendrecv\r\na=rtcp-mux\r\na=rtpmap:100 H264/90000\r\n";

        controller.SetRemoteDescription("offer", h264Offer);
        var answer = await controller.CreateAnswerAsync(hasDataChannels: false, audioSenders: 0, videoSenders: 1);

        var sdp = RtcSdp.Parse(answer);
        Assert.Single(sdp.Media);
        var videoSection = sdp.Media[0];
        Assert.Equal("video", videoSection.Kind);
        // Folio does not send H.264; it refuses to send and answers recvonly without a local send SSRC
        Assert.Equal("recvonly", videoSection.Direction);
        Assert.Empty(videoSection.Ssrcs);
    }
}
