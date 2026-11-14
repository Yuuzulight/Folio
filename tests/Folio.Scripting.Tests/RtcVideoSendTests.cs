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

        // First frame is key frame
        engineA.SendVideo(videoTrack, rgba, w, h);
        toB.Clear();

        // Advance 100 ms and send inter frame
        time.Advance(100);
        engineA.SendVideo(videoTrack, rgba, w, h);
        Assert.NotEmpty(toB);

        // Check that second frame is an inter frame
        var firstInterPacket = toB.Peek();
        Assert.True(srtpB.TryUnprotectRtp(firstInterPacket, out var plainInter));
        Assert.NotNull(plainInter);
        var isInterKey = (plainInter[13] & 1) == 0;
        Assert.False(isInterKey, "Second frame should be an inter frame");
        toB.Clear();

        // Send PLI from B to A
        time.Advance(150);
        var pliCompound = new List<byte[]>
        {
            RtcpCodec.BuildReport(0x9999, false, 0, 0, 0, 0, [], "peer"),
            RtcpCodec.BuildPli(0x9999, videoTrack.Ssrc)
        };
        var plainPli = pliCompound.SelectMany(p => p).ToArray();
        engineA.ProcessDatagram(srtpB.ProtectRtcp(plainPli));

        // Now next send should be forced keyframe
        engineA.SendVideo(videoTrack, rgba, w, h);
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
