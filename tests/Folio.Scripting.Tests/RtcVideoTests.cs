using System.Buffers.Binary;
using System.Security.Cryptography;
using Folio.Scripting.Rtc;
using Folio.Scripting.Rtc.Vp8;

namespace Folio.Scripting.Tests;

/// <summary>
/// Remote video through the media engine: SDP, RTP depacketization (RFC 7741), loss repair with NACK/PLI/FIR (RFC 4585,
/// RFC 5104), budgets and hostile input. The picture data is the libvpx conformance vector vp80-00-comprehensive-001
/// (key frame, then inter frames), so every delivered frame can be compared with a direct decode.
/// </summary>
public sealed class RtcVideoTests
{
    private const uint Ssrc = 0x5151;
    private const string Vector = "vp80-00-comprehensive-001";

    private static string Local(string feedback = "a=rtcp-fb:96 nack\r\na=rtcp-fb:96 nack pli\r\na=rtcp-fb:96 ccm fir\r\n") =>
        "v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=mid:v\r\na=recvonly\r\na=rtcp-mux\r\na=rtpmap:96 VP8/90000\r\n" + feedback;
    private static string Remote(string feedback = "a=rtcp-fb:96 nack\r\na=rtcp-fb:96 nack pli\r\na=rtcp-fb:96 ccm fir\r\n") =>
        "v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=mid:v\r\na=sendonly\r\na=rtcp-mux\r\na=rtpmap:96 VP8/90000\r\n" + feedback;

    private static List<byte[]> Frames()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Vp8Vectors", Vector + ".ivf");
        return Vp8DecoderTests.IvfFrames(File.ReadAllBytes(path), out _, out _);
    }

    /// <summary>The RGBA of every frame, decoded directly without any RTP in between.</summary>
    private static List<byte[]> Reference(IReadOnlyList<byte[]> frames)
    {
        using var decoder = new Vp8Decoder(maxPixels: 1 << 22);
        var result = new List<byte[]>();
        foreach (var frame in frames)
        {
            Assert.Equal(Vp8Status.Ok, decoder.Decode(frame));
            if (decoder.FrameShown) result.Add(decoder.ToRgba()!);
        }
        return result;
    }

    private sealed class VideoLink : IDisposable
    {
        public readonly ManualTime Time = new();
        public readonly RtcMediaEngine Engine;
        public readonly SrtpTransport Sender, Receiver;
        public readonly List<RtcRemoteTrack> Tracks = [];
        public readonly List<MediaCaptureVideoFrame> Delivered = [];
        public readonly Queue<byte[]> Outgoing = new();
        public readonly RtcMediaBudget Budget;
        private ushort _sequence = 100;
        private uint _timestamp = 90_000;

        public VideoLink(string? local = null, string? remote = null, RtcMediaBudget? budget = null)
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

        /// <summary>Splits one encoded frame into SRTP packets of at most <paramref name="chunk"/> payload bytes. The sequence number advances; the timestamp is per frame.</summary>
        public List<(ushort Sequence, byte[] Datagram)> Packetize(byte[] frame, int chunk = 1000, bool descriptorExtension = false)
        {
            var packets = new List<(ushort, byte[])>();
            for (var offset = 0; offset < frame.Length; offset += chunk)
            {
                var length = Math.Min(chunk, frame.Length - offset);
                var first = offset == 0; var last = offset + length == frame.Length;
                var descriptor = descriptorExtension ? new byte[] { (byte)(0x80 | (first ? 0x10 : 0)), 0xA0, 0x80 | 0x12, 0x34, 0x05 } : [(byte)(first ? 0x10 : 0)];
                var rtp = new byte[12 + descriptor.Length + length];
                rtp[0] = 0x80; rtp[1] = (byte)(96 | (last ? 0x80 : 0));
                BinaryPrimitives.WriteUInt16BigEndian(rtp.AsSpan(2), _sequence);
                BinaryPrimitives.WriteUInt32BigEndian(rtp.AsSpan(4), _timestamp);
                BinaryPrimitives.WriteUInt32BigEndian(rtp.AsSpan(8), Ssrc);
                descriptor.CopyTo(rtp, 12);
                frame.AsSpan(offset, length).CopyTo(rtp.AsSpan(12 + descriptor.Length));
                packets.Add((_sequence, Sender.ProtectRtp(rtp)));
                _sequence++;
            }
            _timestamp += 3000;
            return packets;
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

    [Fact]
    public void VideoFramesFlowThroughSrtpAndDecodeToTheReferenceOutput()
    {
        var frames = Frames();
        var expected = Reference(frames);
        using var link = new VideoLink();
        foreach (var frame in frames) link.Send(link.Packetize(frame));
        var track = Assert.Single(link.Tracks);
        Assert.Equal("video", track.Kind);
        Assert.True(track.Decodable);
        Assert.Equal(expected.Count, link.Delivered.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(176, link.Delivered[i].Width); Assert.Equal(144, link.Delivered[i].Height);
            Assert.True(link.Delivered[i].IsValid);
            Assert.True(expected[i].AsSpan().SequenceEqual(link.Delivered[i].Rgba.Span), $"frame {i} differs from the direct decode");
        }
        Assert.Equal(1, track.KeyFramesDecoded);
        Assert.Equal(176, track.VideoWidth);
        Assert.Empty(link.Feedback().Where(p => p.Kind is RtcpKind.Nack or RtcpKind.PictureLossIndication or RtcpKind.FullIntraRequest));
        Assert.True(link.Budget.VideoBytesInUse > 0);
    }

    [Fact]
    public void PayloadDescriptorExtensionsAreSkipped()
    {
        var frames = Frames();
        using var link = new VideoLink();
        foreach (var frame in frames.Take(4)) link.Send(link.Packetize(frame, 400, descriptorExtension: true));
        Assert.Equal(4, link.Delivered.Count);
    }

    [Fact]
    public void ALostPacketIsNackedAndTheRetransmissionCompletesTheFrame()
    {
        var frames = Frames();
        var expected = Reference(frames);
        using var link = new VideoLink();
        link.Send(link.Packetize(frames[0], 100)); link.Send(link.Packetize(frames[1], 100));
        var third = link.Packetize(frames[2], 100);
        Assert.True(third.Count >= 3);
        var lost = third[1];
        link.Send(third, drop: s => s == lost.Sequence);
        link.Send(link.Packetize(frames[3], 100));
        Assert.Equal(2, link.Delivered.Count); // the hole blocks frame 2 and everything behind it
        link.Advance(30);
        var nack = Assert.Single(link.Feedback(), p => p.Kind == RtcpKind.Nack);
        Assert.Equal(Ssrc, nack.MediaSsrc);
        Assert.Equal(new[] { lost.Sequence }, nack.NackSequences);
        Assert.Equal(1, link.Tracks[0].NacksSent);

        link.Engine.ProcessDatagram(lost.Datagram); // the retransmission
        // Frames 2 and 3 complete in the same call; only the newest is converted.
        Assert.Equal(3, link.Delivered.Count);
        for (var i = 0; i < 2; i++) Assert.True(expected[i].AsSpan().SequenceEqual(link.Delivered[i].Rgba.Span), $"frame {i}");
        Assert.True(expected[3].AsSpan().SequenceEqual(link.Delivered[2].Rgba.Span), "frame 3");
        link.Advance(700);
        Assert.DoesNotContain(link.Feedback(), p => p.Kind is RtcpKind.PictureLossIndication or RtcpKind.FullIntraRequest);
    }

    [Fact]
    public void ANackIsRetriedOnlyAFewTimesAndBoundedState()
    {
        var frames = Frames();
        using var link = new VideoLink();
        link.Send(link.Packetize(frames[0], 300));
        var second = link.Packetize(frames[1], 300);
        link.Send(second, drop: s => s == second[0].Sequence);
        link.Send(link.Packetize(frames[2], 300));
        link.Advance(380); // under the loss timeout
        var nacks = link.Feedback().Where(p => p.Kind == RtcpKind.Nack).ToArray();
        Assert.InRange(nacks.Length, 2, Vp8Receiver.MaxNackRetries);
    }

    [Fact]
    public void AnUnrecoveredLossRequestsAKeyFrameAndTheNextKeyFrameRecovers()
    {
        var frames = Frames();
        var expected = Reference(frames);
        using var link = new VideoLink();
        link.Send(link.Packetize(frames[0])); link.Send(link.Packetize(frames[1]));
        var third = link.Packetize(frames[2], 300);
        link.Send(third, drop: s => s == third[1].Sequence);
        for (var i = 3; i < 8; i++) link.Send(link.Packetize(frames[i], 300));
        Assert.Equal(2, link.Delivered.Count);
        link.Advance(450);
        var feedback = link.Feedback();
        Assert.Contains(feedback, p => p.Kind == RtcpKind.PictureLossIndication && p.MediaSsrc == Ssrc);
        Assert.True(link.Tracks[0].KeyFrameRequestsSent >= 1);
        var before = link.Delivered.Count;

        // Inter frames are not decoded against a broken reference.
        link.Send(link.Packetize(frames[8]));
        Assert.Equal(before, link.Delivered.Count);

        // The sender answers with a key frame; the stream continues from there bit-exactly.
        foreach (var frame in frames) link.Send(link.Packetize(frame));
        Assert.Equal(before + expected.Count, link.Delivered.Count);
        for (var i = 0; i < expected.Count; i++)
            Assert.True(expected[i].AsSpan().SequenceEqual(link.Delivered[before + i].Rgba.Span), $"recovered frame {i}");
        Assert.Equal(2, link.Tracks[0].KeyFramesDecoded);
        link.Advance(700);
        link.Feedback(); // drain
        link.Advance(700);
        Assert.DoesNotContain(link.Feedback(), p => p.Kind == RtcpKind.PictureLossIndication);
    }

    [Fact]
    public void AStreamThatStartsWithoutAKeyFrameAsksForOneAfterAShortWait()
    {
        var frames = Frames();
        using var link = new VideoLink();
        link.Send(link.Packetize(frames[5]));
        Assert.Empty(link.Delivered);
        link.Advance(50);
        Assert.DoesNotContain(link.Feedback(), p => p.Kind == RtcpKind.PictureLossIndication); // a key frame may still be on its way
        link.Advance(120);
        Assert.Contains(link.Feedback(), p => p.Kind == RtcpKind.PictureLossIndication);
        link.Advance(450);
        Assert.Contains(link.Feedback(), p => p.Kind == RtcpKind.PictureLossIndication); // repeated, at most every half second
    }

    [Fact]
    public void FirIsUsedWhenOnlyFirIsNegotiatedAndNothingWhenNoFeedbackIs()
    {
        var frames = Frames();
        using (var fir = new VideoLink("v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=mid:v\r\na=recvonly\r\na=rtcp-mux\r\na=rtpmap:96 VP8/90000\r\na=rtcp-fb:96 ccm fir\r\n",
            Remote("a=rtcp-fb:96 ccm fir\r\n")))
        {
            fir.Send(fir.Packetize(frames[5]));
            fir.Advance(200);
            var request = Assert.Single(fir.Feedback(), p => p.Kind == RtcpKind.FullIntraRequest);
            Assert.Equal(Ssrc, request.MediaSsrc);
        }
        using var none = new VideoLink(Local(""), Remote(""));
        none.Send(none.Packetize(frames[0])); none.Send(none.Packetize(frames[1]));
        var third = none.Packetize(frames[2], 300);
        none.Send(third, drop: s => s == third[1].Sequence);
        none.Advance(1000);
        Assert.DoesNotContain(none.Feedback(), p => p.Kind is RtcpKind.Nack or RtcpKind.PictureLossIndication or RtcpKind.FullIntraRequest);
    }

    [Fact]
    public void FeedbackNeedsBothSidesToListItBeforeItIsSent()
    {
        var frames = Frames();
        using var link = new VideoLink(Local(), Remote("")); // we offered it, the peer's description does not list it
        link.Send(link.Packetize(frames[5]));
        link.Advance(300);
        Assert.DoesNotContain(link.Feedback(), p => p.Kind is RtcpKind.PictureLossIndication or RtcpKind.FullIntraRequest);
    }

    [Fact]
    public void ThePictureSizeCapAndTheDocumentMemoryBudgetStopTheDecoder()
    {
        var frames = Frames();
        using (var small = new VideoLink(budget: new RtcMediaBudget(maxVideoPixels: 10_000)))
        {
            small.Send(small.Packetize(frames[0]));
            Assert.Empty(small.Delivered);
            Assert.Equal(0, small.Budget.VideoBytesInUse);
            Assert.Equal(1, small.Tracks[0].PacketsReceived > 0 ? 1 : 0);
        }
        var tight = new RtcMediaBudget(maxVideoBytes: 100_000);
        using (var link = new VideoLink(budget: tight))
        {
            link.Send(link.Packetize(frames[0]));
            Assert.Empty(link.Delivered);
            Assert.Equal(0, tight.VideoBytesInUse);
        }
        // One decoder's charge, measured; the shared budget then fits one and a half of them, so exactly one stream gets a decoder.
        var probe = new RtcMediaBudget();
        using (var measure = new VideoLink(budget: probe)) { measure.Send(measure.Packetize(frames[0])); }
        Assert.Equal(0, probe.VideoBytesInUse);
        long one;
        var measuring = new RtcMediaBudget();
        using (var measure = new VideoLink(budget: measuring)) { measure.Send(measure.Packetize(frames[0])); one = measuring.VideoBytesInUse; }
        Assert.True(one > 0);
        var shared = new RtcMediaBudget(maxVideoBytes: one + one / 2);
        var first = new VideoLink(budget: shared);
        first.Send(first.Packetize(frames[0]));
        Assert.Single(first.Delivered);
        Assert.Equal(one, shared.VideoBytesInUse);
        var second = new VideoLink(budget: shared);
        second.Send(second.Packetize(frames[0]));
        Assert.Empty(second.Delivered);               // refused: it would not fit
        Assert.Equal(one, shared.VideoBytesInUse);    // and nothing was charged for it
        first.Dispose();
        Assert.Equal(0, shared.VideoBytesInUse);      // closing a connection returns its bytes
        second.Send(second.Packetize(frames[0]));     // the next key frame now fits
        Assert.Single(second.Delivered);
        Assert.Equal(one, shared.VideoBytesInUse);
        second.Dispose();
        Assert.Equal(0, shared.VideoBytesInUse);
    }

    [Fact]
    public void AFailedReservationOnASizeChangeRetainsNothing()
    {
        var frames = Frames();
        var budget = new RtcMediaBudget(maxVideoBytes: 600_000);
        using var decoder = new Vp8Decoder(maxPixels: 1 << 22, budget);
        Assert.Equal(Vp8Status.Ok, decoder.Decode(frames[0]));
        var held = budget.VideoBytesInUse;
        Assert.InRange(held, 1, 600_000);
        Assert.True(decoder.RetainedBytes > 0);
        // The same key frame claiming 640x480: the new size does not fit, and the old arrays must not outlive their reservation.
        var bigger = (byte[])frames[0].Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(bigger.AsSpan(6), 640); BinaryPrimitives.WriteUInt16LittleEndian(bigger.AsSpan(8), 480);
        Assert.Equal(Vp8Status.TooLarge, decoder.Decode(bigger));
        Assert.Equal(0, budget.VideoBytesInUse);
        Assert.Equal(0, decoder.RetainedBytes);
        Assert.Equal(0, decoder.Width);
        Assert.Equal(Vp8Status.NoReference, decoder.Decode(frames[1]));
        Assert.Equal(Vp8Status.Ok, decoder.Decode(frames[0])); // and the original size works again
        Assert.Equal(held, budget.VideoBytesInUse);
    }

    [Fact]
    public void AFailedDecodeKeepsTheBuffersWhenTheSizeIsUnchanged()
    {
        var frames = Frames();
        var budget = new RtcMediaBudget();
        using var decoder = new Vp8Decoder(maxPixels: 1 << 22, budget);
        Assert.Equal(Vp8Status.Ok, decoder.Decode(frames[0]));
        var retained = decoder.RetainedBytes; var charged = budget.VideoBytesInUse;
        Assert.Equal(Vp8Status.Corrupt, decoder.Decode(new byte[] { 1, 2 }));
        Assert.Equal(Vp8Status.NoReference, decoder.Decode(frames[1]));
        Assert.Equal(retained, decoder.RetainedBytes);   // not freed, so recovery does not reallocate
        Assert.Equal(charged, budget.VideoBytesInUse);
        Assert.Equal(Vp8Status.Ok, decoder.Decode(frames[0]));
        Assert.Equal(retained, decoder.RetainedBytes);
    }

    [Fact]
    public void DecodeWorkIsBoundedByAPixelRateNoMatterHowSmallTheFramesAre()
    {
        // A ~10 byte key frame header claiming 1920x1080 would otherwise cost a full reconstruction on every packet.
        var tiny = new byte[10];
        tiny[0] = 0x10; tiny[3] = 0x9d; tiny[4] = 0x01; tiny[5] = 0x2a;
        BinaryPrimitives.WriteUInt16LittleEndian(tiny.AsSpan(6), 1920); BinaryPrimitives.WriteUInt16LittleEndian(tiny.AsSpan(8), 1080);
        var time = new ManualTime();
        using var receiver = new Vp8Receiver(new Vp8Decoder(maxPixels: 1920 * 1088), time);
        var output = new VideoReceiverOutput();
        var payload = new byte[tiny.Length + 1];
        payload[0] = 0x10; tiny.CopyTo(payload, 1);
        for (var i = 0; i < 1000; i++) receiver.Push(i, (uint)i, true, payload, time.GetTimestamp(), output); // no time passes
        Assert.InRange(receiver.FramesDecoded + receiver.DecodeErrors, 0, Vp8Receiver.DecodeFramesPerSecond);
        Assert.True(receiver.FramesRateLimited >= 900);
        Assert.True(output.Frames.Count <= Vp8Receiver.DecodeFramesPerSecond);
    }

    [Fact]
    public void OnlyTheNewestFrameOfABurstIsConvertedToRgba()
    {
        var frames = Frames();
        // Hold packets back by one missing packet, then release them all at once: several frames complete in one call.
        var link = new VideoLink();
        var held = new List<(ushort Sequence, byte[] Datagram)>();
        for (var i = 0; i < 6; i++) held.AddRange(link.Packetize(frames[i], 300));
        var delivered = new List<MediaCaptureVideoFrame>();
        link.Send(held.Where((_, index) => index != 1)); // all but the second packet of the key frame
        Assert.Empty(link.Delivered);
        link.Engine.ProcessDatagram(held[1].Datagram);
        Assert.Single(link.Delivered);                 // six frames decoded, one converted: the newest
        var expected = Reference(frames);
        Assert.True(expected[5].AsSpan().SequenceEqual(link.Delivered[0].Rgba.Span));
        link.Dispose();
    }

    [Fact]
    public void KeyFrameRequestsKeepTheirSpacingEvenWhenAKeyFrameDecodesBetweenThem()
    {
        var frames = Frames();
        using var link = new VideoLink();
        var requests = 0;
        for (var cycle = 0; cycle < 6; cycle++)
        {
            link.Send(link.Packetize(Enumerable.Repeat((byte)0xFF, 30).Prepend((byte)1).ToArray())); // a corrupt inter frame: a key frame is needed
            link.Advance(200);
            requests += link.Feedback().Count(p => p.Kind == RtcpKind.PictureLossIndication);
            link.Send(link.Packetize(frames[0]));   // a key frame decodes (this used to reset the throttle)
            Assert.False(link.Tracks[0].VideoWidth == 0);
        }
        Assert.True(requests <= 3, $"{requests} requests in 1.2 s");
    }

    [Fact]
    public void RepairRequestsShareADocumentAllowanceAndKeyFrameRequestsAreSpacedAcrossStreams()
    {
        var time = new ManualTime();
        var budget = new RtcMediaBudget(time);
        Assert.True(budget.TryConsumeKeyFrameRequest());
        Assert.False(budget.TryConsumeKeyFrameRequest());   // another stream, same instant
        time.Advance(150);
        Assert.True(budget.TryConsumeKeyFrameRequest());
        var granted = 0;
        for (var i = 0; i < 500; i++) if (budget.TryConsumeFeedback()) granted++;
        Assert.InRange(granted, 1, RtcMediaBudget.FeedbackPerSecond);
        time.Advance(1000);
        Assert.True(budget.TryConsumeFeedback());
    }

    [Fact]
    public void ANackNeverAsksForMoreThanItCanCarry()
    {
        var frames = Frames();
        var time = new ManualTime();
        using var receiver = new Vp8Receiver(new Vp8Decoder(maxPixels: 1 << 22), time);
        var output = new VideoReceiverOutput();
        // Every second packet missing: 100 holes that cannot share bitmask items beyond 17 numbers each.
        for (var i = 0; i < 200; i += 2)
        {
            var payload = new byte[40]; payload[0] = (byte)(i == 0 ? 0x10 : 0);
            receiver.Push(10 + i, 5, false, payload, time.GetTimestamp(), output);
        }
        time.Advance(30);
        receiver.Tick(time.GetTimestamp(), output);
        Assert.InRange(output.Nack.Count, 1, RtcpCodec.MaxNackItems);
        var parsed = RtcpCodec.Parse(RtcpCodec.BuildNack(1, 2, output.Nack)).Single();
        Assert.Equal(output.Nack.Count, parsed.NackSequences.Count); // every number counted as asked for was on the wire
    }

    [Fact]
    public void RenegotiatingASectionToPortZeroOrInactiveReleasesItsDecoder()
    {
        var frames = Frames();
        var budget = new RtcMediaBudget();
        using var link = new VideoLink(budget: budget);
        link.Send(link.Packetize(frames[0]));
        Assert.True(budget.VideoBytesInUse > 0);
        // The peer turns the section inactive.
        link.Engine.Apply(null, RtcSdp.Parse(Remote().Replace("a=sendonly", "a=inactive")));
        Assert.Equal(0, budget.VideoBytesInUse);
        link.Send(link.Packetize(frames[0]));
        Assert.Single(link.Delivered);                    // nothing more is decoded while it is inactive
        // Back to sendonly: a new key frame is decoded again.
        link.Engine.Apply(null, RtcSdp.Parse(Remote()));
        link.Send(link.Packetize(frames[0]));
        Assert.Equal(2, link.Delivered.Count);
        Assert.True(budget.VideoBytesInUse > 0);
        // And a rejected (port 0) section frees it too.
        link.Engine.Apply(null, RtcSdp.Parse("v=0\r\nm=video 0 UDP/TLS/RTP/SAVPF 96\r\na=mid:v\r\n"));
        Assert.Equal(0, budget.VideoBytesInUse);
    }

    [Fact]
    public void ByeEndsTheTrackAndFreesTheDecoder()
    {
        var frames = Frames();
        var budget = new RtcMediaBudget();
        using var link = new VideoLink(budget: budget);
        link.Send(link.Packetize(frames[0]));
        Assert.True(budget.VideoBytesInUse > 0);
        var ended = false;
        link.Tracks[0].Ended += () => ended = true;
        var bye = RtcpCodec.BuildBye(Ssrc);
        link.Engine.ProcessDatagram(link.Sender.ProtectRtcp(bye));
        Assert.True(ended);
        Assert.Equal(0, budget.VideoBytesInUse);
    }

    [Fact]
    public void HostileVideoPacketsAreDroppedAndTheBuffersStayBounded()
    {
        var frames = Frames();
        var random = new Random(7);
        using var link = new VideoLink();
        link.Send(link.Packetize(frames[0]));
        var delivered = link.Delivered.Count;
        // Garbage payloads of every size behind a valid RTP header, with a plausible sequence number each.
        for (var i = 0; i < 400; i++)
        {
            var payload = new byte[random.Next(0, 1100)];
            random.NextBytes(payload);
            var rtp = new byte[12 + payload.Length];
            rtp[0] = 0x80; rtp[1] = (byte)(96 | (random.Next(2) << 7));
            BinaryPrimitives.WriteUInt16BigEndian(rtp.AsSpan(2), (ushort)(5000 + random.Next(0, 700)));
            BinaryPrimitives.WriteUInt32BigEndian(rtp.AsSpan(4), (uint)random.Next());
            BinaryPrimitives.WriteUInt32BigEndian(rtp.AsSpan(8), Ssrc);
            payload.CopyTo(rtp, 12);
            link.Engine.ProcessDatagram(link.Sender.ProtectRtp(rtp));
            if (i % 50 == 0) link.Advance(100);
        }
        link.Advance(2000);
        Assert.True(link.Delivered.Count >= delivered);
        link.Engine.Dispose(); // no exception, nothing left over
    }

    [Fact]
    public void OversizedFramesAndSequenceJumpsAreBounded()
    {
        var decoder = new Vp8Decoder(maxPixels: 1 << 22);
        var time = new ManualTime();
        using var receiver = new Vp8Receiver(decoder, time);
        var output = new VideoReceiverOutput();
        // A frame that never ends: 1000 byte packets without a marker, far beyond the 2 MiB frame limit and the buffer limit.
        var filler = new byte[1001]; filler[0] = 0x10;
        for (var i = 0; i < 3000; i++)
        {
            filler[0] = (byte)(i == 0 ? 0x10 : 0);
            receiver.Push(10 + i, 77, false, filler, time.GetTimestamp(), output);
            Assert.True(receiver.BufferedPackets <= Vp8Receiver.MaxBufferedPackets);
        }
        // A jump far beyond the window restarts the stream instead of buffering for ever.
        receiver.Push(10 + 1_000_000, 88, true, filler, time.GetTimestamp(), output);
        Assert.True(receiver.BufferedPackets <= 2);
        Assert.True(receiver.WaitingForKeyFrame);
        Assert.Empty(output.Frames);
    }

    [Fact]
    public void PayloadDescriptorParsingIsStrict()
    {
        Assert.False(Vp8PayloadDescriptor.TryParse([], out _));
        Assert.False(Vp8PayloadDescriptor.TryParse([0x10], out _));                  // descriptor but no payload
        Assert.False(Vp8PayloadDescriptor.TryParse([0x90, 0x80], out _));            // X set, I set, picture id missing
        Assert.False(Vp8PayloadDescriptor.TryParse([0x90, 0x80, 0x80], out _));      // 15 bit picture id cut short
        Assert.True(Vp8PayloadDescriptor.TryParse([0x10, 0xFF], out var simple));
        Assert.True(simple.Start); Assert.Equal(0, simple.PartitionId); Assert.Equal(1, simple.PayloadOffset);
        Assert.True(Vp8PayloadDescriptor.TryParse([0xB5, 0xF0, 0x81, 0x23, 0x07, 0x09, 0xAA], out var full)); // X N S PID=5, I L T K, 15 bit PictureID
        Assert.True(full.NonReference); Assert.True(full.Start); Assert.Equal(5, full.PartitionId); Assert.Equal(6, full.PayloadOffset);
    }

    [Fact]
    public void RtcpFeedbackRoundTripsAndHostileInputIsIgnored()
    {
        var nack = RtcpCodec.BuildNack(1, 2, [10, 11, 13, 40, 65535, 0, 1]);
        var parsed = Assert.Single(RtcpCodec.Parse(nack));
        Assert.Equal(RtcpKind.Nack, parsed.Kind);
        Assert.Equal(1u, parsed.Ssrc); Assert.Equal(2u, parsed.MediaSsrc);
        Assert.Equal(new ushort[] { 0, 1, 10, 11, 13, 40, 65535 }, parsed.NackSequences.OrderBy(s => s));
        var pli = Assert.Single(RtcpCodec.Parse(RtcpCodec.BuildPli(3, 4)));
        Assert.Equal(RtcpKind.PictureLossIndication, pli.Kind); Assert.Equal(4u, pli.MediaSsrc);
        var fir = Assert.Single(RtcpCodec.Parse(RtcpCodec.BuildFir(5, 6, 9)));
        Assert.Equal(RtcpKind.FullIntraRequest, fir.Kind); Assert.Equal(6u, fir.MediaSsrc); Assert.Equal(9, fir.FirSequence);
        // The wire layout (RFC 4585 section 6.1): V=2, FMT in the count field, PT 205/206, length in words minus one.
        Assert.Equal(new byte[] { 0x81, 205 }, nack[..2]);
        Assert.Equal(new byte[] { 0x81, 206, 0, 2 }, RtcpCodec.BuildPli(3, 4)[..4]);
        Assert.Equal(new byte[] { 0x84, 206, 0, 4 }, RtcpCodec.BuildFir(5, 6, 9)[..4]);
        // Truncated and oversized feedback never throws and never over-reads.
        for (var length = 0; length < nack.Length; length++) RtcpCodec.Parse(nack.AsSpan(0, length));
        var huge = RtcpCodec.BuildNack(1, 2, Enumerable.Range(0, 5000).Select(i => (ushort)(i * 40)));
        Assert.True(RtcpCodec.Parse(huge).Single().NackSequences.Count <= RtcpCodec.MaxNackItems * 17);
        var random = new Random(3);
        for (var i = 0; i < 2000; i++) { var junk = new byte[random.Next(0, 80)]; random.NextBytes(junk); if (junk.Length > 0) junk[0] = (byte)(0x80 | (junk[0] & 0x1F)); RtcpCodec.Parse(junk); }
    }

    [Fact]
    public void SdpCarriesVideoFeedbackThroughAParseAndGenerateRoundTrip()
    {
        var sdp = new RtcSdp { IceUfrag = "u", IcePassword = "p", FingerprintValue = "AA" };
        var video = new RtcMediaDescription { Kind = "video", Protocol = "UDP/TLS/RTP/SAVPF", Mid = "0", Direction = "recvonly" };
        video.Codecs.Add(new RtcCodec(96, "VP8", 90000, 1));
        video.AddFeedback(96, "nack"); video.AddFeedback(96, "nack pli"); video.AddFeedback(96, "ccm fir");
        sdp.Media.Add(video);
        var text = sdp.ToSdp();
        Assert.Contains("m=video 9 UDP/TLS/RTP/SAVPF 96\r\n", text);
        Assert.Contains("a=recvonly\r\n", text);
        Assert.Contains("a=rtcp-mux\r\n", text);
        Assert.Contains("a=rtpmap:96 VP8/90000\r\n", text);
        Assert.Contains("a=rtcp-fb:96 nack\r\n", text);
        Assert.Contains("a=rtcp-fb:96 nack pli\r\n", text);
        Assert.Contains("a=rtcp-fb:96 ccm fir\r\n", text);
        Assert.Contains("a=group:BUNDLE 0\r\n", text);
        var parsed = RtcSdp.Parse(text).Media[0];
        Assert.True(parsed.HasFeedback(96, "nack") && parsed.HasFeedback(96, "pli") && parsed.HasFeedback(96, "fir"));
        Assert.False(parsed.HasFeedback(97, "nack"));
        var wildcard = RtcSdp.Parse("v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=rtcp-fb:* nack\r\na=rtcp-fb:96 goog-remb\r\na=rtcp-fb:96 transport-cc\r\n").Media[0];
        Assert.True(wildcard.HasFeedback(96, "nack")); Assert.False(wildcard.HasFeedback(96, "pli"));
        // Hostile feedback lines are bounded.
        var flood = string.Concat(Enumerable.Range(0, 500).Select(i => $"a=rtcp-fb:{i} nack\r\n"));
        Assert.True(RtcSdp.Parse("v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\n" + flood).Media[0].Feedback.Values.Sum(v => v.Count) <= RtcMediaDescription.MaxFeedbackEntries);
    }

    [Fact]
    public async Task AnOfferIsRecvonlyVideoAndAnAnswerPicksTheOfferedVp8PayloadType()
    {
        using var offerer = new RtcPeerConnectionController();
        var offer = await offerer.CreateOfferAsync(false, 0, 0, 0, 1);
        Assert.Contains("m=video 9 UDP/TLS/RTP/SAVPF 96 97 98\r\n", offer); // VP8 first, then H.264 (RtcH264Tests covers those)
        Assert.Contains("a=recvonly\r\n", offer);
        Assert.Contains("a=rtpmap:96 VP8/90000\r\n", offer);
        Assert.Contains("a=rtcp-fb:96 nack\r\n", offer);
        Assert.Contains("a=rtcp-fb:96 nack pli\r\n", offer);
        Assert.Contains("a=rtcp-fb:96 ccm fir\r\n", offer);
        Assert.Contains("a=rtcp-mux\r\n", offer);
        Assert.DoesNotContain("a=inactive", offer);

        // A browser-style offer: VP8 on 120 with a retransmission payload type, H.264 and a feedback list; it sends and receives.
        const string browser = "v=0\r\no=- 1 2 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\na=group:BUNDLE 0\r\na=ice-ufrag:abcd\r\na=ice-pwd:0123456789012345678901\r\na=fingerprint:sha-256 AA:BB\r\na=setup:actpass\r\n" +
            "m=video 9 UDP/TLS/RTP/SAVPF 102 120 121\r\nc=IN IP4 0.0.0.0\r\na=mid:0\r\na=sendrecv\r\na=rtcp-mux\r\n" +
            "a=rtpmap:102 H264/90000\r\na=rtpmap:120 VP8/90000\r\na=rtpmap:121 rtx/90000\r\na=fmtp:121 apt=120\r\n" +
            "a=rtcp-fb:120 goog-remb\r\na=rtcp-fb:120 transport-cc\r\na=rtcp-fb:120 ccm fir\r\na=rtcp-fb:120 nack\r\na=rtcp-fb:120 nack pli\r\n";
        using var answerer = new RtcPeerConnectionController();
        answerer.SetRemoteDescription("offer", browser);
        var answer = await answerer.CreateAnswerAsync(false, 0, 0);
        Assert.Contains("m=video 9 UDP/TLS/RTP/SAVPF 120\r\n", answer);
        Assert.Contains("a=recvonly\r\n", answer); // it can receive, not send
        Assert.Contains("a=rtpmap:120 VP8/90000\r\n", answer);
        Assert.DoesNotContain("H264", answer);
        Assert.DoesNotContain("rtx", answer);
        Assert.Contains("a=rtcp-fb:120 nack\r\n", answer);
        Assert.Contains("a=rtcp-fb:120 nack pli\r\n", answer);
        Assert.Contains("a=rtcp-fb:120 ccm fir\r\n", answer);
        Assert.DoesNotContain("goog-remb", answer);
        Assert.DoesNotContain("transport-cc", answer);
        Assert.Contains("a=group:BUNDLE 0\r\n", answer);

        // A video section without a decodable codec is rejected and leaves the bundle group.
        using var other = new RtcPeerConnectionController();
        other.SetRemoteDescription("offer", browser.Replace("102 120 121", "120").Replace("a=rtpmap:102 H264/90000\r\n", "").Replace("a=rtpmap:120 VP8/90000", "a=rtpmap:120 VP9/90000"));
        var rejected = await other.CreateAnswerAsync(false, 0, 0);
        Assert.Contains("m=video 0 ", rejected);
        Assert.DoesNotContain("a=group:BUNDLE 0", rejected);
    }

    [Fact]
    public void TheMediaDecoderAdapterDecodesAnIvfFileAndRefusesWhatItCannot()
    {
        var file = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Vp8Vectors", Vector + ".ivf"));
        var decoder = new Vp8MediaDecoder();
        Assert.True(decoder.CanDecode("video/x-ivf", true));
        Assert.True(decoder.CanDecode("video/vp8; codecs=vp8", true));
        Assert.False(decoder.CanDecode("video/mp4", true));
        Assert.False(decoder.CanDecode("video/x-ivf", false));
        var result = decoder.Decode(file, "video/x-ivf", true, maxOutputBytes: 64 * 1024 * 1024);
        Assert.NotNull(result);
        Assert.Equal(29, result.VideoFrames!.Count);
        Assert.Equal(176, result.VideoFrame!.Width);
        Assert.Equal(0, result.VideoFrames[0].Time);
        Assert.True(result.Duration > 0.9 && result.Duration < 1.1); // 29 frames at 30 fps
        var reference = Reference(Frames());
        Assert.True(reference[28].AsSpan().SequenceEqual(result.VideoFrames[28].Frame.Pixels.Span));
        // The output budget is a hard limit, and bad or truncated input is refused, never thrown.
        Assert.Null(decoder.Decode(file, "video/x-ivf", true, maxOutputBytes: 100_000));
        Assert.Null(decoder.Decode(file.AsMemory(0, 20), "video/x-ivf", true, 64 * 1024 * 1024));
        Assert.Null(decoder.Decode(new byte[100], "video/x-ivf", true, 64 * 1024 * 1024));
        var truncated = decoder.Decode(file.AsMemory(0, file.Length / 2), "video/x-ivf", true, 64 * 1024 * 1024);
        Assert.NotNull(truncated);
        Assert.InRange(truncated.VideoFrames!.Count, 1, 28);
    }

    [Fact]
    public void TheDecoderSurvivesMutatedTruncatedAndOversizedFramesWithoutThrowing()
    {
        var frames = Frames();
        var random = new Random(11);
        using var decoder = new Vp8Decoder(maxPixels: 1 << 20);
        Assert.Equal(Vp8Status.NoReference, decoder.Decode(frames[1]));              // an inter frame needs a key frame first
        Assert.Equal(Vp8Status.Corrupt, decoder.Decode(ReadOnlySpan<byte>.Empty));
        Assert.Equal(Vp8Status.Ok, decoder.Decode(frames[0]));
        // Dimensions beyond the cap, then back to a valid key frame.
        var huge = (byte[])frames[0].Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(huge.AsSpan(6), 16383); BinaryPrimitives.WriteUInt16LittleEndian(huge.AsSpan(8), 16383);
        Assert.Equal(Vp8Status.TooLarge, decoder.Decode(huge));
        Assert.Equal(Vp8Status.NoReference, decoder.Decode(frames[1]));
        Assert.Equal(Vp8Status.Ok, decoder.Decode(frames[0]));
        for (var i = 0; i < 1500; i++)
        {
            var frame = (byte[])frames[random.Next(frames.Count)].Clone();
            switch (random.Next(3))
            {
                case 0: for (var k = 0; k < 1 + random.Next(8); k++) frame[random.Next(frame.Length)] ^= (byte)(1 << random.Next(8)); break;
                case 1: frame = frame[..random.Next(0, frame.Length)]; break;
                default: random.NextBytes(frame.AsSpan(Math.Min(10, frame.Length))); break;
            }
            var status = decoder.Decode(frame);
            Assert.True(Enum.IsDefined(status));
            if (status != Vp8Status.Ok) Assert.Equal(Vp8Status.Ok, decoder.Decode(frames[0])); // a key frame always recovers
        }
    }

    [Fact]
    public void AddTrackOfVideoFailsCleanlyOnANetworkPeerConnection()
    {
        // The script side of this lives in the content process; here the host-side facts it relies on.
        var offer = RtcSdp.Parse("v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=mid:0\r\na=recvonly\r\na=rtpmap:96 VP8/90000\r\n");
        Assert.False(offer.Media[0].Sends);
        Assert.True(offer.Media[0].Receives);
    }
}
