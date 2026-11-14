using Folio.Scripting.Rtc;

namespace Folio.Scripting.Tests;

public sealed class H264DepacketizerTests
{
    private sealed record Pkt(byte[] Payload, ushort Seq, uint Ts, bool Marker);

    /// <summary>Packetization-mode 1 packetizer for tests: STAP-A for small NAL runs, FU-A when a NAL exceeds the MTU.</summary>
    private static List<Pkt> Packetize(List<List<byte[]>> units, int mtu, bool stap)
    {
        var packets = new List<Pkt>();
        ushort seq = 100;
        uint ts = 90000;
        foreach (var unit in units)
        {
            var batch = new List<Pkt>();
            var pendingSmall = new List<byte[]>();
            void FlushSmall()
            {
                if (pendingSmall.Count == 0) return;
                if (pendingSmall.Count == 1 || !stap) foreach (var n in pendingSmall) batch.Add(new Pkt(n, 0, ts, false));
                else
                {
                    var p = new List<byte> { (byte)(24 | (pendingSmall.Max(n => n[0]) & 0x60)) };
                    foreach (var n in pendingSmall) { p.Add((byte)(n.Length >> 8)); p.Add((byte)n.Length); p.AddRange(n); }
                    batch.Add(new Pkt([.. p], 0, ts, false));
                }
                pendingSmall.Clear();
            }
            foreach (var nal in unit)
            {
                if (nal.Length <= mtu) { if (stap && nal.Length > 200) { FlushSmall(); batch.Add(new Pkt(nal, 0, ts, false)); } else pendingSmall.Add(nal); continue; }
                FlushSmall();
                var body = nal.AsSpan(1);
                for (var off = 0; off < body.Length; off += mtu - 2)
                {
                    var len = Math.Min(mtu - 2, body.Length - off);
                    var p = new byte[2 + len];
                    p[0] = (byte)((nal[0] & 0xE0) | 28);
                    p[1] = (byte)((off == 0 ? 0x80 : 0) | (off + len == body.Length ? 0x40 : 0) | (nal[0] & 0x1F));
                    body.Slice(off, len).CopyTo(p.AsSpan(2));
                    batch.Add(new Pkt(p, 0, ts, false));
                }
            }
            FlushSmall();
            for (var i = 0; i < batch.Count; i++) packets.Add(batch[i] with { Seq = seq++, Marker = i == batch.Count - 1 });
            ts += 6000;
        }
        return packets;
    }

    private static List<byte[]> Run(IEnumerable<Pkt> packets, H264Depacketizer? d = null)
    {
        d ??= new H264Depacketizer();
        var o = new List<byte[]>();
        foreach (var p in packets) d.Push(p.Payload, p.Seq, p.Ts, p.Marker, o);
        return o;
    }

    [Theory]
    [InlineData("life", 1200, true)]
    [InlineData("life", 100, false)]
    [InlineData("refs", 60, true)]
    [InlineData("slices", 300, true)]
    public void PacketizedStreamsRoundTripThroughTheDecoder(string name, int mtu, bool stap)
    {
        var stream = name == "life" ? H264Fixtures.QcifLife() : name == "refs" ? H264Fixtures.SixteenRefs() : H264Fixtures.CroppedSlices();
        var expected = name == "life" ? H264Fixtures.QcifLifeMd5 : name == "refs" ? H264Fixtures.SixteenRefsMd5 : H264Fixtures.CroppedSlicesMd5;
        var units = H264DecoderTests.AccessUnits(stream);
        var access = Run(Packetize(units, mtu, stap));
        Assert.Equal(units.Count, access.Count);
        var decoder = new H264Decoder { RethrowUnexpected = true };
        var frames = new List<DecodedVideoFrame>();
        foreach (var au in access) frames.AddRange(decoder.Decode(au));
        Assert.Equal(expected, H264DecoderTests.Md5(frames));
    }

    [Fact]
    public void LostFragmentDropsThatAccessUnitOnly()
    {
        var units = H264DecoderTests.AccessUnits(H264Fixtures.QcifLife());
        var packets = Packetize(units, 100, false);
        var victim = packets.First(p => (p.Payload[0] & 0x1F) == 28 && (p.Payload[1] & 0xC0) == 0);
        var d = new H264Depacketizer();
        var access = Run(packets.Where(p => p != victim), d);
        Assert.Equal(units.Count - 1, access.Count);
        Assert.Equal(1, d.DroppedAccessUnits);
    }

    [Fact]
    public void LostFirstPacketOfTheNextAccessUnitDropsIt()
    {
        var units = H264DecoderTests.AccessUnits(H264Fixtures.CroppedSlices());
        var packets = Packetize(units, 1200, false);
        var firstOfThird = packets.First(p => p.Ts == 90000 + 6000 * 2);
        var d = new H264Depacketizer();
        var access = Run(packets.Where(p => p != firstOfThird), d);
        Assert.Equal(units.Count - 1, access.Count);
        Assert.Equal(1, d.DroppedAccessUnits);
    }

    [Fact]
    public void MissingMarkerIsRecoveredByTheNextTimestamp()
    {
        var packets = Packetize(H264DecoderTests.AccessUnits(H264Fixtures.QcifLife())[..3], 1200, true);
        var noMarker = packets.Select(p => p with { Marker = false });
        Assert.Equal(2, Run(noMarker).Count);   // the last picture is still waiting for its marker or a later timestamp
    }

    [Fact]
    public void MalformedPacketsAreDroppedWithoutThrowing()
    {
        byte[][] bad =
        [
            [], [0x81, 1, 2],                                   // forbidden bit
            [0x78, 0x00, 0x05, 0x65, 1],                        // STAP-A truncated
            [0x18, 0x00, 0x00],                                 // STAP-A zero-size
            [0x18],                                             // STAP-A empty
            [0x7C, 0xC5, 1, 2],                                 // FU-A with S and E
            [0x7C, 0x45, 1, 2],                                 // FU-A continuation without start
            [0x7C],                                             // FU-A too short
            [0x19, 1, 2, 3], [0x1A, 1], [0x1B, 1], [0x1D, 1], [0x1E, 1], [0x1F, 1], [0x00, 1],   // STAP-B, MTAP, FU-B, reserved
        ];
        var d = new H264Depacketizer();
        var o = new List<byte[]>();
        ushort seq = 0;
        foreach (var p in bad) d.Push(p, seq++, 1000, true, o);
        Assert.Empty(o);
        Assert.Equal(bad.Length, d.DroppedPackets);
    }

    [Fact]
    public void AccessUnitSizeIsCapped()
    {
        var d = new H264Depacketizer(maxAccessUnitBytes: 100);
        var o = new List<byte[]>();
        d.Push(new byte[200].Select((_, i) => (byte)(i == 0 ? 0x65 : 1)).ToArray(), 1, 1, true, o);
        Assert.Empty(o);
        d.Push([0x41, 9, 9], 2, 2, true, o);
        Assert.Equal([[0, 0, 0, 1, 0x41, 9, 9]], o);
    }

    [Fact]
    public void SequenceNumberWrapAroundIsNotALoss()
    {
        var d = new H264Depacketizer();
        var o = new List<byte[]>();
        d.Push([0x41, 1], 65535, 1, false, o);
        d.Push([0x41, 2], 0, 1, true, o);
        Assert.Single(o);
    }
}
