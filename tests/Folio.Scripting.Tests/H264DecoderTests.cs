using System.Security.Cryptography;
using Folio.Scripting.Rtc;

namespace Folio.Scripting.Tests;

/// <summary>Builds H.264 syntax by hand for known-answer tests.</summary>
internal sealed class H264BitWriter
{
    private readonly List<byte> _bytes = [];
    private int _bit;

    internal H264BitWriter U(int bits, int value)
    {
        for (var i = bits - 1; i >= 0; i--) Bit((value >> i) & 1);
        return this;
    }

    internal H264BitWriter Ue(int value)
    {
        var v = (long)value + 1;
        var len = 64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)v);
        U(len - 1, 0);
        for (var i = len - 1; i >= 0; i--) Bit((int)((v >> i) & 1));
        return this;
    }

    internal H264BitWriter Se(int value) => Ue(value > 0 ? 2 * value - 1 : -2 * value);

    internal H264BitWriter Align() { while (_bit != 0) Bit(0); return this; }

    internal H264BitWriter Bytes(byte[] data) { Align(); _bytes.AddRange(data); return this; }

    /// <summary>The rbsp_trailing_bits.</summary>
    internal byte[] Finish() { Bit(1); Align(); return [.. _bytes]; }

    private void Bit(int b)
    {
        if (_bit == 0) _bytes.Add(0);
        _bytes[^1] |= (byte)(b << (7 - _bit));
        _bit = (_bit + 1) & 7;
    }

    /// <summary>Wraps an RBSP into an Annex-B NAL unit with emulation prevention.</summary>
    internal static byte[] Nal(int refIdc, int type, byte[] rbsp)
    {
        var o = new List<byte> { 0, 0, 0, 1, (byte)(refIdc << 5 | type) };
        var zeros = 0;
        foreach (var b in rbsp)
        {
            if (zeros >= 2 && b <= 3) { o.Add(3); zeros = 0; }
            o.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }
        return [.. o];
    }

    internal static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];
}

public sealed class H264DecoderTests
{
    internal static string Md5(IEnumerable<DecodedVideoFrame> frames)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        foreach (var f in frames) { md5.AppendData(f.Y); md5.AppendData(f.U); md5.AppendData(f.V); }
        return Convert.ToHexStringLower(md5.GetHashAndReset());
    }

    /// <summary>Splits an Annex-B stream into NAL units (without start codes).</summary>
    internal static List<byte[]> SplitNals(byte[] data)
    {
        var nals = new List<byte[]>();
        int start = -1;
        for (var i = 0; i + 2 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                if (start >= 0) nals.Add(Trim(data, start, i));
                start = i + 3;
                i += 2;
            }
        }
        if (start >= 0) nals.Add(Trim(data, start, data.Length));
        return nals;

        static byte[] Trim(byte[] d, int s, int e)
        {
            while (e > s && d[e - 1] == 0) e--;
            return d[s..e];
        }
    }

    /// <summary>Groups NAL units into access units: parameter sets and delimiters start a new unit, so does a slice with first_mb_in_slice == 0.</summary>
    internal static List<List<byte[]>> AccessUnits(byte[] stream)
    {
        var units = new List<List<byte[]>>();
        List<byte[]>? cur = null;
        var hasSlice = false;
        foreach (var nal in SplitNals(stream))
        {
            var type = nal[0] & 0x1F;
            var slice = type is 1 or 5;
            if (cur == null || (hasSlice && (type is 7 or 8 or 9 || (slice && (nal[1] & 0x80) != 0))))
            {
                cur = [];
                units.Add(cur);
                hasSlice = false;
            }
            cur.Add(nal);
            hasSlice |= slice;
        }
        return units;
    }

    internal static byte[] AnnexB(IEnumerable<byte[]> nals) => [.. nals.SelectMany(n => new byte[] { 0, 0, 0, 1 }.Concat(n))];

    private static byte[] Fixture(string name) => name switch
    {
        "life" => H264Fixtures.QcifLife(),
        "refs" => H264Fixtures.SixteenRefs(),
        _ => H264Fixtures.CroppedSlices(),
    };

    private static string Expected(string name) => name switch
    {
        "life" => H264Fixtures.QcifLifeMd5,
        "refs" => H264Fixtures.SixteenRefsMd5,
        _ => H264Fixtures.CroppedSlicesMd5,
    };

    [Theory]
    [InlineData("life", 15, 128, 96)]
    [InlineData("refs", 20, 96, 80)]
    [InlineData("slices", 8, 100, 70)]
    public void FfmpegReferenceStreamsDecodeToTheSameFrames(string name, int frames, int width, int height)
    {
        var decoder = new H264Decoder { RethrowUnexpected = true };
        var decoded = decoder.Decode(Fixture(name));
        Assert.Equal(frames, decoded.Count);
        Assert.All(decoded, f => { Assert.Equal(width, f.Width); Assert.Equal(height, f.Height); });
        Assert.Equal(Expected(name), Md5(decoded));
        Assert.Equal(0, decoder.NalUnitsDropped);
    }

    [Theory]
    [InlineData("life")]
    [InlineData("refs")]
    [InlineData("slices")]
    public void DecodingOneAccessUnitPerCallMatchesDecodingTheWholeStream(string name)
    {
        var decoder = new H264Decoder { RethrowUnexpected = true };
        var all = new List<DecodedVideoFrame>();
        foreach (var unit in AccessUnits(Fixture(name))) all.AddRange(decoder.Decode(AnnexB(unit)));
        Assert.Equal(Expected(name), Md5(all));
    }

    // ---- hand-built known answers ----

    private static byte[] Sps(int mbW, int mbH, int profile = 66, int cropRight = 0)
    {
        var w = new H264BitWriter().U(8, profile).U(8, 0xC0).U(8, 10).Ue(0).Ue(0).Ue(2).Ue(1).U(1, 0)
            .Ue(mbW - 1).Ue(mbH - 1).U(1, 1).U(1, 1);
        if (cropRight > 0) w.U(1, 1).Ue(0).Ue(cropRight).Ue(0).Ue(0); else w.U(1, 0);
        return H264BitWriter.Nal(3, 7, w.U(1, 0).Finish());
    }

    private static byte[] Pps(bool cabac = false) =>
        H264BitWriter.Nal(3, 8, new H264BitWriter().Ue(0).Ue(0).U(1, cabac ? 1 : 0).U(1, 0).Ue(0).Ue(0).Ue(0).U(1, 0).U(2, 0)
            .Se(0).Se(0).Se(0).U(1, 1).U(1, 0).U(1, 0).Finish());

    private static byte[] PcmIdr(byte[] samples)
    {
        var w = new H264BitWriter().Ue(0).Ue(7).Ue(0).U(4, 0).Ue(0).U(1, 0).U(1, 0).Se(0).Ue(1);
        w.Ue(25).Bytes(samples);
        return H264BitWriter.Nal(3, 5, w.Finish());
    }

    private static byte[] OneMbSamples(out byte[] y, out byte[] u, out byte[] v)
    {
        y = new byte[256]; u = new byte[64]; v = new byte[64];
        for (var i = 0; i < 256; i++) y[i] = (byte)((i % 16) * 13 + (i / 16) * 7);
        for (var i = 0; i < 64; i++) { u[i] = (byte)((i % 8) * 29 + (i / 8) * 3 + 40); v[i] = (byte)(255 - (i % 8) * 17 - (i / 8) * 5); }
        return [.. y, .. u, .. v];
    }

    [Fact]
    public void IntraPcmPictureIsReproducedExactly()
    {
        var samples = OneMbSamples(out var y, out var u, out var v);
        var frames = new H264Decoder { RethrowUnexpected = true }.Decode(H264BitWriter.Concat(Sps(1, 1), Pps(), PcmIdr(samples)));
        var f = Assert.Single(frames);
        Assert.Equal((16, 16), (f.Width, f.Height));
        Assert.Equal(y, f.Y);
        Assert.Equal(u, f.U);
        Assert.Equal(v, f.V);
    }

    [Fact]
    public void SkippedAndTranslatedInterMacroblocksFollowTheSpecInterpolation()
    {
        var samples = OneMbSamples(out var y, out var u, out var v);
        // P frame 1: P_L0_16x16, no residual, mvd = (4, 0) quarter samples, i.e. one luma sample right (half a chroma sample).
        var shifted = H264BitWriter.Nal(2, 1, BuildTranslatedP(1));
        // P frame 2: one skipped macroblock (mb_skip_run = 1); its motion vector predicts to zero, so the picture is copied.
        var skipped = H264BitWriter.Nal(2, 1, BuildSkippedP(2));
        var decoder = new H264Decoder { RethrowUnexpected = true };
        var frames = decoder.Decode(H264BitWriter.Concat(Sps(1, 1), Pps(), PcmIdr(samples), shifted, skipped));
        Assert.Equal(3, frames.Count);

        var p1 = frames[1];
        for (var r = 0; r < 16; r++)
            for (var c = 0; c < 16; c++)
                Assert.Equal(y[r * 16 + Math.Min(c + 1, 15)], p1.Y[r * 16 + c]);
        for (var r = 0; r < 8; r++)
            for (var c = 0; c < 8; c++)
            {
                Assert.Equal((u[r * 8 + c] + u[r * 8 + Math.Min(c + 1, 7)] + 1) >> 1, p1.U[r * 8 + c]);
                Assert.Equal((v[r * 8 + c] + v[r * 8 + Math.Min(c + 1, 7)] + 1) >> 1, p1.V[r * 8 + c]);
            }
        Assert.Equal(p1.Y, frames[2].Y);
        Assert.Equal(p1.U, frames[2].U);
        Assert.Equal(p1.V, frames[2].V);
    }

    private static byte[] BuildTranslatedP(int frameNum)
    {
        // Header, then slice data: mb_skip_run 0, mb_type 0 (P_L0_16x16), mvd (4,0), coded_block_pattern code 0 (inter cbp 0).
        var w = new H264BitWriter().Ue(0).Ue(5).Ue(0).U(4, frameNum).U(1, 0).U(1, 0).U(1, 0).Se(0).Ue(1);
        w.Ue(0).Ue(0).Se(4).Se(0).Ue(0);
        return w.Finish();
    }

    private static byte[] BuildSkippedP(int frameNum)
    {
        var w = new H264BitWriter().Ue(0).Ue(5).Ue(0).U(4, frameNum).U(1, 0).U(1, 0).U(1, 0).Se(0).Ue(1);
        w.Ue(1);
        return w.Finish();
    }

    [Fact]
    public void FrameCroppingIsApplied()
    {
        // 2x1 macroblocks cropped by 2 crop units (4 luma columns) on the right: 28x16.
        var mb = new byte[384];
        Array.Fill(mb, (byte)90);
        var slice = new H264BitWriter().Ue(0).Ue(7).Ue(0).U(4, 0).Ue(0).U(1, 0).U(1, 0).Se(0).Ue(1);
        slice.Ue(25).Bytes(mb).Ue(25).Bytes(mb);
        var frames = new H264Decoder { RethrowUnexpected = true }.Decode(H264BitWriter.Concat(Sps(2, 1, cropRight: 2), Pps(), H264BitWriter.Nal(3, 5, slice.Finish())));
        var f = Assert.Single(frames);
        Assert.Equal((28, 16), (f.Width, f.Height));
        Assert.Equal(28 * 16, f.Y.Length);
        Assert.Equal(14 * 8, f.U.Length);
    }

    // ---- rejection and limits ----

    [Fact]
    public void UnsupportedStreamsAreDroppedWithoutThrowing()
    {
        var samples = OneMbSamples(out _, out _, out _);
        var decoder = new H264Decoder { RethrowUnexpected = true };
        Assert.Empty(decoder.Decode(H264BitWriter.Concat(Sps(1, 1, profile: 100), Pps(), PcmIdr(samples))));   // High profile
        Assert.Empty(decoder.Decode(H264BitWriter.Concat(Sps(1, 1), Pps(cabac: true), PcmIdr(samples))));        // CABAC
        Assert.Empty(decoder.Decode(PcmIdr(samples)));                                                           // no parameter sets
        Assert.Empty(decoder.Decode([1, 2, 3, 4, 5]));
        Assert.Empty(decoder.Decode([]));
        Assert.True(decoder.NalUnitsDropped >= 3);
    }

    [Fact]
    public void InterPictureBeforeAnyKeyframeIsIgnored()
    {
        var decoder = new H264Decoder { RethrowUnexpected = true };
        Assert.Empty(decoder.Decode(H264BitWriter.Concat(Sps(1, 1), Pps(), H264BitWriter.Nal(2, 1, BuildSkippedP(1)))));
        var samples = OneMbSamples(out _, out _, out _);
        Assert.Single(decoder.Decode(PcmIdr(samples)));
    }

    [Fact]
    public void PictureSizeMemoryAndInputLimitsAreEnforced()
    {
        var stream = Fixture("life");   // 8x6 macroblocks, 3 reference frames
        Assert.Empty(new H264Decoder(new H264DecoderLimits { MaxMacroblocks = 47 }).Decode(stream));
        Assert.Equal(15, new H264Decoder(new H264DecoderLimits { MaxMacroblocks = 48 }).Decode(stream).Count);
        Assert.Empty(new H264Decoder(new H264DecoderLimits { MaxMemoryBytes = 48 * 384 * 5 - 1 }).Decode(stream));
        Assert.Equal(15, new H264Decoder(new H264DecoderLimits { MaxMemoryBytes = 48 * 384 * 5 }).Decode(stream).Count);
        Assert.Empty(new H264Decoder(new H264DecoderLimits { MaxInputBytes = stream.Length - 1 }).Decode(stream));
        // An SPS can claim far more than the limits; it is refused before anything is allocated.
        var huge = H264BitWriter.Nal(3, 7, new H264BitWriter().U(8, 66).U(8, 0).U(8, 10).Ue(0).Ue(0).Ue(2).Ue(16).U(1, 0)
            .Ue(1023).Ue(1023).U(1, 1).U(1, 1).U(1, 0).U(1, 0).Finish());
        Assert.Empty(new H264Decoder { RethrowUnexpected = true }.Decode(H264BitWriter.Concat(huge, Pps(), PcmIdr([]))));
    }

    [Fact]
    public void ALostPictureIsConcealedAndDecodingContinues()
    {
        var units = AccessUnits(Fixture("life"));
        Assert.Equal(15, units.Count);
        units.RemoveAt(6);
        var decoder = new H264Decoder { RethrowUnexpected = true };
        var frames = new List<DecodedVideoFrame>();
        foreach (var u in units) frames.AddRange(decoder.Decode(AnnexB(u)));
        Assert.Equal(14, frames.Count);
    }

    [Fact]
    public void TruncatedAndCorruptedStreamsNeverEscapeTheDecoder()
    {
        var random = new Random(719);
        foreach (var name in new[] { "life", "refs", "slices" })
        {
            var stream = Fixture(name);
            for (var len = 0; len < stream.Length; len += 61)
                new H264Decoder { RethrowUnexpected = true }.Decode(stream.AsSpan(0, len));
            for (var round = 0; round < 150; round++)
            {
                var copy = (byte[])stream.Clone();
                var flips = 1 + random.Next(8);
                for (var i = 0; i < flips; i++) copy[random.Next(copy.Length)] ^= (byte)(1 << random.Next(8));
                var decoder = new H264Decoder { RethrowUnexpected = true };
                decoder.Decode(copy);
                decoder.Decode(copy);   // a second pass exercises references and state left by the first
            }
        }
    }

    [Fact]
    public void RandomBytesNeverEscapeTheDecoder()
    {
        var random = new Random(704);
        var decoder = new H264Decoder { RethrowUnexpected = true };
        var head = H264BitWriter.Concat(Sps(2, 2), Pps());
        for (var round = 0; round < 300; round++)
        {
            var body = new byte[random.Next(1, 300)];
            random.NextBytes(body);
            var nal = H264BitWriter.Nal(random.Next(4), random.Next(2) == 0 ? 5 : 1, body);
            decoder.Decode(H264BitWriter.Concat(head, nal));
        }
    }

    [Fact]
    public void VlcTablesAreCompleteAndPrefixFree()
    {
        var all = new List<H264Tables.Vlc>(H264Tables.CoeffToken) { H264Tables.ChromaDcToken };
        all.AddRange(H264Tables.TotalZeros);
        all.AddRange(H264Tables.ChromaDcTotalZeros);
        all.AddRange(H264Tables.RunBefore);
        foreach (var vlc in all)
        {
            // Every table entry belongs to exactly one code, and the codes cover at most the whole code space.
            var covered = vlc.Table.Count(e => e != 0);
            double kraft = 0;
            foreach (var group in vlc.Table.Where(e => e != 0).GroupBy(e => e))
            {
                var len = group.Key >> 8;
                Assert.Equal(1 << (vlc.MaxLen - len), group.Count());
                kraft += Math.Pow(2, -len);
            }
            Assert.True(kraft <= 1.0000001);
            Assert.True(covered > 0);
        }
    }

    /// <summary>
    /// Runs every JVT conformance bitstream found under the directory named by FOLIO_H264_CONFORMANCE_DIR (one sub-folder
    /// per bitstream, each holding the .264/.jsv/.h264 stream and its .yuv reference, as unzipped from the ITU-T H.264.1
    /// draft_conformance/AVCv1 archives). Skipped when the variable is unset; the vectors are not redistributed here.
    /// </summary>
    [Fact]
    public void JvtConformanceBitstreamsMatchTheirReferenceYuv()
    {
        var root = Environment.GetEnvironmentVariable("FOLIO_H264_CONFORMANCE_DIR");
        Assert.SkipWhen(string.IsNullOrEmpty(root) || !Directory.Exists(root), "FOLIO_H264_CONFORMANCE_DIR is not set");
        string[] streamExt = [".264", ".jsv", ".h264", ".avc", ".26l", ".bit", ".jvt"];
        string[] yuvExt = [".yuv", ".qcif", ".cif"];
        // Out of scope: CVSE3/HCMP1 use B slices; HCBP2/MR4/MR5 decode every picture correctly but output in POC order,
        // while this decoder emits pictures in decoding order (equal for the RTP use case).
        string[] outOfScope = ["CVSE3_Sony_H", "HCBP2_HHI_A", "HCMP1_HHI_A", "MR4_TANDBERG_C", "MR5_TANDBERG_C"];
        var ran = 0;
        foreach (var dir in Directory.GetDirectories(root!))
        {
            if (outOfScope.Contains(Path.GetFileName(dir))) continue;
            var stream = Directory.GetFiles(dir).FirstOrDefault(f => streamExt.Contains(Path.GetExtension(f).ToLowerInvariant()));
            var yuv = Directory.GetFiles(dir).FirstOrDefault(f => yuvExt.Contains(Path.GetExtension(f).ToLowerInvariant()));
            if (stream == null || yuv == null) continue;
            var frames = new H264Decoder { RethrowUnexpected = true }.Decode(File.ReadAllBytes(stream));
            var expected = File.ReadAllBytes(yuv);
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            foreach (var f in frames) { md5.AppendData(f.Y); md5.AppendData(f.U); md5.AppendData(f.V); }
            Assert.True(Convert.ToHexStringLower(md5.GetHashAndReset()) == Convert.ToHexStringLower(MD5.HashData(expected)), Path.GetFileName(dir));
            ran++;
        }
        Assert.True(ran > 0);
    }
}
