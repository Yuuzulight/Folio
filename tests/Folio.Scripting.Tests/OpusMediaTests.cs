using System.Security.Cryptography;
using Folio.Scripting.Rtc;
using Folio.Scripting.Rtc.Opus;

namespace Folio.Scripting.Tests;

public sealed class OpusMediaTests
{
    private const string ChromeLikeOffer =
        "v=0\r\na=group:BUNDLE 0\r\nm=audio 9 UDP/TLS/RTP/SAVPF 111 63 9 0 8\r\na=mid:0\r\na=sendrecv\r\na=rtcp-mux\r\n" +
        "a=rtpmap:111 opus/48000/2\r\na=fmtp:111 minptime=10;useinbandfec=1\r\n" +
        "a=rtpmap:63 red/48000/2\r\na=rtpmap:9 G722/8000\r\na=rtpmap:0 PCMU/8000\r\na=rtpmap:8 PCMA/8000\r\n";

    private static string OpusAnswer(string fmtp) =>
        $"v=0\r\na=group:BUNDLE 0\r\nm=audio 9 UDP/TLS/RTP/SAVPF 111\r\na=mid:0\r\na=sendrecv\r\na=rtcp-mux\r\na=rtpmap:111 opus/48000/2\r\na=fmtp:111 {fmtp}\r\n";

    [Fact]
    public void SdpCarriesOpusWithItsFormatParametersBothWays()
    {
        var parsed = RtcSdp.Parse(ChromeLikeOffer);
        var media = parsed.Media[0];
        var opus = media.CodecFor(111)!;
        Assert.True(opus.IsOpus);
        Assert.Equal("minptime=10;useinbandfec=1", opus.Fmtp);
        Assert.Equal("1", opus.FmtpParameter("useinbandfec"));
        Assert.Null(opus.FmtpParameter("sprop-stereo"));
        Assert.False(media.CodecFor(63)!.IsOpus); // red/48000/2 is not Opus
        Assert.False(media.CodecFor(9)!.IsOpus);

        var written = new RtcSdp { Type = "offer" };
        var section = new RtcMediaDescription { Kind = "audio", Port = 9, Protocol = "UDP/TLS/RTP/SAVPF", Mid = "0", Direction = "sendrecv", RtcpMux = true };
        section.Codecs.Add(new RtcCodec(111, "opus", 48000, 2) { Fmtp = RtcPeerConnectionController.OpusFmtp });
        section.Codecs.Add(new RtcCodec(0, "PCMU", 8000, 1));
        written.Media.Add(section);
        var text = written.ToSdp();
        Assert.True(text.Contains("m=audio 9 UDP/TLS/RTP/SAVPF 111 0"), text);
        Assert.Contains("a=rtpmap:111 opus/48000/2", text);
        Assert.Contains("a=fmtp:111 minptime=10;useinbandfec=1;stereo=0;sprop-stereo=0", text);
        Assert.True(text.IndexOf("opus/48000/2", StringComparison.Ordinal) < text.IndexOf("PCMU/8000", StringComparison.Ordinal)); // Opus is preferred
        var roundTrip = RtcSdp.Parse(text).Media[0].CodecFor(111)!;
        Assert.Equal("1", roundTrip.FmtpParameter("useinbandfec"));
        Assert.Equal("0", roundTrip.FmtpParameter("sprop-stereo"));
    }

    [Fact]
    public void HostileFormatParametersAreBounded()
    {
        var lines = string.Concat(Enumerable.Range(0, 200).Select(i => $"a=fmtp:{i % 128} {new string('x', 40)}\r\n"));
        var parsed = RtcSdp.Parse("v=0\r\nm=audio 9 UDP/TLS/RTP/SAVPF 111\r\na=mid:0\r\na=rtpmap:111 opus/48000/2\r\n" + lines + "a=fmtp:111 " + new string('y', 5000) + "\r\n");
        Assert.True(parsed.Media[0].Fmtps.Count <= RtcMediaDescription.MaxCodecs);
        Assert.All(parsed.Media[0].Fmtps.Values, v => Assert.True(v.Length <= 256));
    }

    private sealed class OpusLink
    {
        public readonly ManualTime Time = new();
        public readonly RtcMediaEngine A, B;
        public readonly List<MediaAudioFrame> FramesOnB = [];
        public readonly List<RtcRemoteTrack> TracksOnB = [];
        public readonly Queue<byte[]> ToB = new(), ToA = new();
        public readonly RtcLocalTrack Sender;

        public OpusLink(string answerFmtp = "minptime=10;useinbandfec=1;stereo=0;sprop-stereo=0")
        {
            A = new RtcMediaEngine(Time); B = new RtcMediaEngine(Time);
            var ka = RandomNumberGenerator.GetBytes(16); var sa = RandomNumberGenerator.GetBytes(12);
            var kb = RandomNumberGenerator.GetBytes(16); var sb = RandomNumberGenerator.GetBytes(12);
            A.SetSrtp(new SrtpTransport(ka, sa, kb, sb)); B.SetSrtp(new SrtpTransport(kb, sb, ka, sa));
            A.OnPacketToSend += p => ToB.Enqueue(p); B.OnPacketToSend += p => ToA.Enqueue(p);
            B.OnRemoteTrack += t => { TracksOnB.Add(t); t.AudioFrame += FramesOnB.Add; };
            Sender = A.AddLocalAudio()!;
            var offer = RtcSdp.Parse(ChromeLikeOffer);
            var answer = RtcSdp.Parse(OpusAnswer(answerFmtp));
            A.Apply(offer, null); B.Apply(null, offer); B.Apply(answer, null); A.Apply(null, answer);
        }

        public void Pump(Func<int, bool>? deliver = null)
        {
            var i = 0;
            while (ToB.Count > 0) { var p = ToB.Dequeue(); if (deliver?.Invoke(i++) ?? true) B.ProcessDatagram(p); }
            while (ToA.Count > 0) A.ProcessDatagram(ToA.Dequeue());
        }
    }

    private static float[] Sine(double seconds, double hz = 440, double amplitude = 0.5) =>
        Enumerable.Range(0, (int)(48000 * seconds)).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / 48000))).ToArray();

    [Fact]
    public void OpusAudioFlowsAt48kHzFromCaptureThroughSrtpToTheRemoteTrack()
    {
        var link = new OpusLink();
        var sent = link.A.SendAudio(link.Sender, Sine(1.0), 48000, 1);
        Assert.InRange(sent, 49, 50);
        Assert.Equal(111, link.Sender.PayloadType);
        Assert.True(link.Sender.Opus);
        // every packet is SRTP: 12 byte header, the Opus packet without its TOC-less framing change, and a 16 byte tag
        Assert.All(link.ToB, p => Assert.InRange(p.Length, 12 + 20 + 16, 12 + 200 + 16));
        link.Pump();
        var track = Assert.Single(link.TracksOnB);
        Assert.True(track.Decodable);
        Assert.Equal(sent, link.FramesOnB.Count);
        Assert.All(link.FramesOnB, f => { Assert.Equal(48000, f.SampleRate); Assert.Equal(1, f.Channels); Assert.Equal(960, f.Samples.Length); });
        var samples = link.FramesOnB.SelectMany(f => f.Samples.ToArray()).ToArray();
        var rms = Math.Sqrt(samples.Skip(4800).Select(s => (double)s * s).Average());
        Assert.InRange(rms, 0.25, 0.45); // a 0.5 amplitude sine has RMS 0.354
        // the same tone is more than 8 kHz bandwidth away from G.711: a 6 kHz component survives the trip
        var link2 = new OpusLink();
        link2.A.SendAudio(link2.Sender, Enumerable.Range(0, 48000).Select(i => (float)(0.4 * Math.Sin(2 * Math.PI * 6000 * i / 48000))).ToArray(), 48000, 1);
        link2.Pump();
        var hf = link2.FramesOnB.SelectMany(f => f.Samples.ToArray()).Skip(9600).ToArray();
        Assert.True(Math.Sqrt(hf.Select(s => (double)s * s).Average()) > 0.2, "wideband content was lost");
    }

    [Fact]
    public void StereoIsDecodedOnlyWhenThePeerAnnouncesIt()
    {
        var mono = new OpusLink("minptime=10;useinbandfec=1");
        mono.A.SendAudio(mono.Sender, Sine(0.2), 48000, 1);
        mono.Pump();
        Assert.All(mono.FramesOnB, f => Assert.Equal(1, f.Channels));
        // with sprop-stereo=1 in the offer side description the decoder outputs two channels
        var stereoOffer = ChromeLikeOffer.Replace("minptime=10;useinbandfec=1", "minptime=10;useinbandfec=1;sprop-stereo=1");
        var engine = new RtcMediaEngine(new ManualTime());
        var tracks = new List<RtcRemoteTrack>();
        engine.OnRemoteTrack += tracks.Add;
        engine.Apply(null, RtcSdp.Parse(stereoOffer));
        engine.Apply(RtcSdp.Parse(OpusAnswer("minptime=10;useinbandfec=1")), null);
        Assert.Single(tracks);
    }

    [Fact]
    public void LostOpusPacketsAreConcealedInsteadOfLeavingAGap()
    {
        var link = new OpusLink();
        link.A.SendAudio(link.Sender, Sine(0.6), 48000, 1);
        var count = link.ToB.Count;
        Assert.True(count >= 28);
        // drop packets 10 and 11 and 20
        link.Pump(i => i is not (10 or 11 or 20));
        link.Time.Advance(200); link.B.Tick();
        // 3 lost packets are concealed: frame count equals the packets sent, not the packets received
        Assert.Equal(count, link.FramesOnB.Count);
        Assert.All(link.FramesOnB, f => Assert.All(f.Samples.ToArray(), s => Assert.True(float.IsFinite(s))));
        Assert.Equal(3, link.TracksOnB[0].PacketsLost);
    }

    [Fact]
    public void GarbageOpusPayloadsAreDroppedWithoutBreakingTheStream()
    {
        var link = new OpusLink();
        link.A.SendAudio(link.Sender, Sine(0.1), 48000, 1);
        var good = link.ToB.Count;
        link.Pump();
        var delivered = link.FramesOnB.Count;
        Assert.Equal(good, delivered);
        // a sender that authenticates but sends junk (valid SRTP, invalid Opus) must only raise drop counters
        var rnd = new Random(3);
        var ka = RandomNumberGenerator.GetBytes(16); var sa = RandomNumberGenerator.GetBytes(12);
        var kb = RandomNumberGenerator.GetBytes(16); var sb = RandomNumberGenerator.GetBytes(12);
        var junkTx = new SrtpTransport(ka, sa, kb, sb);
        var engine = new RtcMediaEngine(new ManualTime());
        engine.SetSrtp(new SrtpTransport(kb, sb, ka, sa));
        var offer = RtcSdp.Parse(ChromeLikeOffer);
        var tracks = new List<RtcRemoteTrack>(); var frames = 0;
        engine.OnRemoteTrack += t => { tracks.Add(t); t.AudioFrame += _ => frames++; };
        engine.Apply(null, offer);
        engine.Apply(RtcSdp.Parse(OpusAnswer("minptime=10")), null);
        for (ushort seq = 0; seq < 200; seq++)
        {
            var payload = new byte[rnd.Next(1, 300)];
            rnd.NextBytes(payload);
            var rtp = new byte[12 + payload.Length];
            rtp[0] = 0x80; rtp[1] = 111;
            rtp[2] = (byte)(seq >> 8); rtp[3] = (byte)seq; rtp[11] = 9;
            payload.CopyTo(rtp, 12);
            engine.ProcessDatagram(junkTx.ProtectRtp(rtp));
        }
        Assert.Single(tracks);
        Assert.True(frames <= 200);
    }

    // ---- CPU ceiling and durations ----

    private sealed class RawStream
    {
        public readonly ManualTime Time = new();
        public readonly RtcMediaEngine Engine;
        public readonly List<MediaAudioFrame> Frames = [];
        public RtcRemoteTrack Track = null!;
        private readonly SrtpTransport _tx;

        public RawStream()
        {
            var ka = RandomNumberGenerator.GetBytes(16); var sa = RandomNumberGenerator.GetBytes(12);
            var kb = RandomNumberGenerator.GetBytes(16); var sb = RandomNumberGenerator.GetBytes(12);
            _tx = new SrtpTransport(ka, sa, kb, sb);
            Engine = new RtcMediaEngine(Time);
            Engine.SetSrtp(new SrtpTransport(kb, sb, ka, sa));
            Engine.OnRemoteTrack += t => { Track = t; t.AudioFrame += Frames.Add; };
            Engine.Apply(null, RtcSdp.Parse(ChromeLikeOffer));
            Engine.Apply(RtcSdp.Parse(OpusAnswer("minptime=10")), null);
        }

        public void Send(ushort seq, byte[] opusPacket)
        {
            var rtp = new byte[12 + opusPacket.Length];
            rtp[0] = 0x80; rtp[1] = 111; rtp[2] = (byte)(seq >> 8); rtp[3] = (byte)seq; rtp[11] = 5;
            opusPacket.CopyTo(rtp, 12);
            Engine.ProcessDatagram(_tx.ProtectRtp(rtp));
        }
    }

    private static List<byte[]> LongestRunWithSameDuration(int samples)
    {
        var best = new List<byte[]>();
        foreach (var (name, bytes) in OpusCodecTests.LoadVectors().Where(e => e.Key.EndsWith(".bit")))
        {
            var run = new List<byte[]>();
            foreach (var (packet, _) in OpusCodecTests.ReadBitFile(bytes))
            {
                if (packet.Length > 1 && OpusPacket.GetNbSamples(packet) == samples && (run.Count == 0 || OpusPacket.GetMode(packet[0]) == OpusPacket.GetMode(run[0][0]))) run.Add(packet);
                else { if (run.Count > best.Count) best = run; run = []; }
            }
            if (run.Count > best.Count) best = run;
        }
        return best;
    }

    [Theory]
    [InlineData(480)]
    [InlineData(960)]
    [InlineData(2880)]
    public void ConcealmentFollowsThePacketDurationAndKeepsTheTimeline(int samples)
    {
        var run = LongestRunWithSameDuration(samples);
        Assert.True(run.Count >= 8, $"fixtures hold no run of {samples} sample packets");
        var stream = new RawStream();
        ushort seq = 0;
        for (var i = 0; i < 5; i++) stream.Send(seq++, run[i]);
        seq++; // packet 5 is lost
        for (var i = 6; i < 8; i++) stream.Send(seq++, run[i]);
        stream.Time.Advance(100); stream.Engine.Tick();
        Assert.Equal(1, stream.Track.PacketsLost);
        var total = stream.Frames.Sum(f => f.Samples.Length);
        Assert.Equal(8 * samples, total); // seven packets plus one concealed packet of the same length
        Assert.Contains(stream.Frames, f => f.Samples.Length == samples);
        Assert.All(stream.Frames, f => Assert.All(f.Samples.ToArray(), s => Assert.True(float.IsFinite(s))));
    }

    [Fact]
    public void DecodedAudioPerStreamIsCappedAtAFewTimesRealTime()
    {
        var run = LongestRunWithSameDuration(960);
        var stream = new RawStream();
        // 400 packets (8 s of audio) arriving in the same instant: only the burst allowance may be decoded
        for (var i = 0; i < 400; i++) stream.Send((ushort)i, run[i % run.Count]);
        var decodedMs = stream.Frames.Sum(f => f.Samples.Length) / 48.0;
        Assert.InRange(decodedMs, 1000, RtcMediaEngine.OpusBurstMs + 40);
        Assert.True(stream.Track.PacketsDropped >= 400 - 80);
        // as time passes the allowance refills at the stated rate
        var before = stream.Frames.Count;
        stream.Time.Advance(1000);
        stream.Send(400, run[0]);
        Assert.True(stream.Frames.Count > before);
    }

    [Fact]
    public void ABurstBehindAHoleIsDecodedInBoundedStepsAndConcealmentIsBounded()
    {
        var run = LongestRunWithSameDuration(960);
        var stream = new RawStream();
        stream.Send(0, run[0]);
        stream.Send(1, run[1]);
        // packet 2 is lost together with a long stretch; 40 later packets pile up behind the hole
        for (var i = 0; i < 40; i++) stream.Send((ushort)(500 + i), run[2 + i % (run.Count - 2)]);
        var delivered = stream.Frames.Count;
        stream.Time.Advance(100);
        stream.Engine.Tick();
        // one pass decodes at most MaxOpusPacketsPerDrain packets and MaxConcealedFrames concealed frames for the gap
        Assert.InRange(stream.Frames.Count - delivered, 1, RtcMediaEngine.MaxOpusPacketsPerDrain + RtcMediaEngine.MaxConcealedFrames);
        for (var i = 0; i < 4; i++) { stream.Time.Advance(100); stream.Engine.Tick(); }
        Assert.True(stream.Frames.Count - delivered > RtcMediaEngine.MaxOpusPacketsPerDrain, "the rest drains on later passes");
    }
}
