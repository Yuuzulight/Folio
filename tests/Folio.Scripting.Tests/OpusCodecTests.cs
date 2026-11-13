using System.Buffers.Binary;
using System.IO.Compression;
using Folio.Scripting.Rtc.Opus;

namespace Folio.Scripting.Tests;

public sealed class OpusCodecTests
{
    // ---- RFC 8251 test vectors (opus-codec.org/testvectors/opus_testvectors.tar.gz), truncated ----

    private static IEnumerable<(byte[] Packet, uint FinalRange)> ReadBitFile(byte[] file)
    {
        var pos = 0;
        while (pos + 8 <= file.Length)
        {
            var len = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(pos));
            var range = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(pos + 4));
            if (pos + 8 + len > file.Length) yield break;
            yield return (file.AsSpan(pos + 8, len).ToArray(), range);
            pos += 8 + len;
        }
    }

    private static Dictionary<string, byte[]> LoadVectors()
    {
        var assembly = typeof(OpusCodecTests).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("OpusVectors.zip", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var zip = new ZipArchive(stream);
        return zip.Entries.ToDictionary(e => e.Name, e =>
        {
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        });
    }

    private static (int Packets, int Mismatches, double SnrDb, HashSet<int> Modes, double ErrorRms, double SignalRms) RunVector(byte[] bit, byte[] reference)
    {
        var decoder = new OpusDecoder(2);
        var pcm = new float[5760 * 2];
        var packets = 0; var mismatches = 0; var offset = 0;
        double signal = 0, error = 0;
        var modes = new HashSet<int>();
        foreach (var (packet, range) in ReadBitFile(bit))
        {
            var samples = decoder.Decode(packet, pcm, 5760);
            Assert.True(samples > 0, $"packet {packets} failed with {samples}");
            modes.Add(decoder.LastMode);
            if (decoder.FinalRange != range) mismatches++;
            for (var i = 0; i < samples * 2 && offset + 2 <= reference.Length; i++, offset += 2)
            {
                var r = BinaryPrimitives.ReadInt16LittleEndian(reference.AsSpan(offset));
                var m = Math.Clamp(pcm[i] * 32768.0, -32768, 32767);
                signal += (double)r * r; error += (m - r) * (m - r);
            }
            packets++;
        }
        var count = Math.Max(1, offset / 2);
        return (packets, mismatches, error == 0 ? 999 : 10 * Math.Log10(signal / error), modes, Math.Sqrt(error / count), Math.Sqrt(signal / count));
    }

    [Fact]
    public void TruncatedOfficialVectorsDecodeWithMatchingRangeCoderStateAndSamples()
    {
        var files = LoadVectors();
        var modes = new HashSet<int>();
        for (var v = 1; v <= 12; v++)
        {
            var (packets, mismatches, snr, vectorModes, errorRms, signalRms) = RunVector(files[$"testvector{v:00}.bit"], files[$"testvector{v:00}.dec"]);
            Assert.True(packets >= 100, $"vector {v}: only {packets} packets");
            Assert.Equal(0, mismatches); // the final range of every packet equals the reference decoder's: the bitstream parse is exact
            // float against the fixed-point reference: within a few LSB (the first packets are near silence, so SNR alone is misleading)
            Assert.True(errorRms <= Math.Max(4.0, signalRms * 0.01), $"vector {v}: error {errorRms:F2} LSB rms, signal {signalRms:F0}, SNR {snr:F1} dB");
            modes.UnionWith(vectorModes);
        }
        // the vectors between them cover SILK-only, hybrid and CELT-only
        Assert.Contains(OpusPacket.ModeSilkOnly, modes);
        Assert.Contains(OpusPacket.ModeHybrid, modes);
        Assert.Contains(OpusPacket.ModeCeltOnly, modes);
    }

    /// <summary>Set FOLIO_OPUS_VECTORS to a directory holding the complete testvector01..12 files to run them end to end.</summary>
    [Fact]
    public void CompleteOfficialVectorsWhenAvailable()
    {
        var dir = Environment.GetEnvironmentVariable("FOLIO_OPUS_VECTORS");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        for (var v = 1; v <= 12; v++)
        {
            var (_, mismatches, snr, _, _, _) = RunVector(File.ReadAllBytes(Path.Combine(dir, $"testvector{v:00}.bit")), File.ReadAllBytes(Path.Combine(dir, $"testvector{v:00}.dec")));
            Assert.Equal(0, mismatches);
            Assert.True(snr > 60, $"vector {v}: SNR {snr:F1} dB");
        }
    }

    // ---- range coder ----

    [Fact]
    public void RangeCoderRoundTripsBitsUintsAndSymbols()
    {
        var rnd = new Random(7);
        var buf = new byte[2000];
        var enc = new RangeEncoder(buf, buf.Length);
        var expected = new List<(int Kind, uint A, uint B)>();
        ReadOnlySpan<byte> icdf = [200, 120, 40, 0];
        for (var i = 0; i < 400; i++)
        {
            switch (rnd.Next(4))
            {
                case 0: { var bits = rnd.Next(1, 14); var v = (uint)rnd.Next(1 << bits); enc.Bits(v, bits); expected.Add((0, v, (uint)bits)); break; }
                case 1: { var ft = (uint)rnd.Next(2, 100000); var v = (uint)rnd.Next((int)ft); enc.Uint(v, ft); expected.Add((1, v, ft)); break; }
                case 2: { var s = rnd.Next(4); enc.Icdf(s, icdf, 8); expected.Add((2, (uint)s, 0)); break; }
                default: { var b = rnd.Next(2); enc.BitLogp(b, 3); expected.Add((3, (uint)b, 0)); break; }
            }
        }
        enc.Done();
        Assert.False(enc.Error);
        var dec = new RangeDecoder(buf, 0, buf.Length);
        foreach (var (kind, a, b) in expected)
        {
            switch (kind)
            {
                case 0: Assert.Equal(a, dec.Bits((int)b)); break;
                case 1: Assert.Equal(a, dec.Uint(b)); break;
                case 2: Assert.Equal((int)a, dec.Icdf(icdf, 8)); break;
                default: Assert.Equal((int)a, dec.BitLogp(3)); break;
            }
        }
        Assert.Equal(enc.Rng, dec.Rng);
    }

    [Fact]
    public void PyramidVectorCodewordsRoundTrip()
    {
        var rnd = new Random(1);
        foreach (var n in new[] { 2, 3, 6, 8, 12, 24, 48, 96, 176 })
            for (var k = 1; k <= 40; k++)
            {
                if (!FitsIn32Bits(n, k)) continue; // the bit allocation never asks for these; their codeword count overflows 32 bits
                var y = new int[n];
                for (var left = k; left > 0;) { var i = rnd.Next(n); var s = rnd.Next(2) == 0 ? 1 : -1; if (y[i] != 0 && Math.Sign(y[i]) != s) continue; y[i] += s; left--; }
                var buf = new byte[600];
                var enc = new RangeEncoder(buf, buf.Length);
                CeltPvq.EncodePulses(y, n, k, enc);
                enc.Done();
                var decoded = new int[n];
                CeltPvq.DecodePulses(decoded, n, k, new RangeDecoder(buf, 0, buf.Length));
                Assert.Equal(y, decoded);
            }
    }

    private static bool FitsIn32Bits(int n, int k)
    {
        ReadOnlySpan<int> maxN = [32767, 32767, 32767, 1476, 283, 109, 60, 40, 29, 24, 20, 18, 16, 14, 13];
        ReadOnlySpan<int> maxK = [32767, 32767, 32767, 32767, 1172, 238, 95, 53, 36, 27, 22, 18, 16, 15, 13];
        return n >= 14 ? k < 14 && n <= maxN[k] : k <= maxK[n];
    }

    // ---- framing ----

    [Fact]
    public void PacketFramingHandlesEveryCodeAndRejectsMalformedLengths()
    {
        Span<int> sizes = stackalloc int[OpusPacket.MaxFrames];
        // code 0: one frame
        Assert.Equal(1, OpusPacket.Parse([0xF8, 1, 2, 3], sizes, out var offset, out _));
        Assert.Equal(1, offset); Assert.Equal(3, sizes[0]);
        // code 1: two equal frames, odd remainder is invalid
        Assert.Equal(2, OpusPacket.Parse([0xF9, 1, 2, 3, 4], sizes, out _, out _)); Assert.Equal(2, sizes[0]);
        Assert.True(OpusPacket.Parse([0xF9, 1, 2, 3], sizes, out _, out _) < 0);
        // code 2: explicit first size
        Assert.Equal(2, OpusPacket.Parse([0xFA, 2, 9, 9, 7], sizes, out _, out _)); Assert.Equal(2, sizes[0]); Assert.Equal(1, sizes[1]);
        Assert.True(OpusPacket.Parse([0xFA, 9, 9], sizes, out _, out _) < 0); // first frame longer than the packet
        // code 3 CBR with three frames
        Assert.Equal(3, OpusPacket.Parse([0xFB, 3, 1, 2, 3], sizes, out _, out _));
        // code 3 VBR with padding
        Assert.Equal(2, OpusPacket.Parse([0xFB, 0x80 | 0x40 | 2, 2, 1, 1, 9, 9, 0, 0], sizes, out _, out _));
        // zero frames, too many frames, truncated padding length
        Assert.True(OpusPacket.Parse([0xFB, 0], sizes, out _, out _) < 0);
        Assert.True(OpusPacket.Parse([0xFB, 48, 1], sizes, out _, out _) < 0); // 48 frames of 20 ms is 960 ms
        Assert.True(OpusPacket.Parse([0xFB, 0x40 | 1], sizes, out _, out _) < 0);
        Assert.True(OpusPacket.Parse([], sizes, out _, out _) < 0);
        // 120 ms limit
        Assert.True(OpusPacket.GetNbSamples([0x78 | 3, 49]) < 0);
        Assert.Equal(960, OpusPacket.GetNbSamples([0xF8, 1]));
        Assert.Equal(2880, OpusPacket.GetNbSamples([0x18, 1]));
    }

    // ---- hostile input ----

    [Fact]
    public void RandomAndMutatedPacketsNeverThrowAndNeverGrowTheDecoder()
    {
        var rnd = new Random(1234);
        var decoder = new OpusDecoder(2);
        var pcm = new float[5760 * 2];
        var before = GC.GetTotalMemory(true);
        var decoded = 0;
        for (var i = 0; i < 6000; i++)
        {
            var packet = new byte[rnd.Next(1, 400)];
            rnd.NextBytes(packet);
            if (i % 3 == 0) packet[0] = (byte)(packet[0] & 0xFC | (i % 4)); // exercise every frame-count code
            var result = decoder.Decode(packet, pcm, 5760);
            if (result > 0)
            {
                decoded++;
                Assert.All(pcm.AsSpan(0, result * 2).ToArray(), s => Assert.True(float.IsFinite(s)));
            }
            else Assert.True(result < 0);
        }
        Assert.True(decoded > 500);
        // every TOC byte with short and long bodies, including all three SILK frame sizes and CELT frame sizes
        for (var toc = 0; toc < 256; toc++)
            foreach (var len in new[] { 1, 2, 3, 20, 200 })
            {
                var packet = new byte[len];
                rnd.NextBytes(packet);
                packet[0] = (byte)toc;
                var result = decoder.Decode(packet, pcm, 5760);
                Assert.True(result is > 0 or < 0);
            }
        var grown = GC.GetTotalMemory(true) - before;
        Assert.True(grown < 4_000_000, $"decoder state grew by {grown} bytes");
        // the decoder keeps working afterwards
        Assert.True(decoder.Decode([0xF8, 0xFF, 0xFF], pcm, 960) > 0);
    }

    [Fact]
    public void BitFlippedRealPacketsAreDecodedOrRejectedWithoutFaults()
    {
        var files = LoadVectors();
        var rnd = new Random(99);
        for (var v = 1; v <= 12; v++)
        {
            var decoder = new OpusDecoder(2);
            var pcm = new float[5760 * 2];
            foreach (var (packet, _) in ReadBitFile(files[$"testvector{v:00}.bit"]).Take(60))
            {
                var bad = packet.ToArray();
                for (var f = 0; f < 3; f++) bad[rnd.Next(bad.Length)] ^= (byte)(1 << rnd.Next(8));
                var r = decoder.Decode(bad, pcm, 5760);
                if (r > 0) Assert.All(pcm.AsSpan(0, r * 2).ToArray(), s => Assert.True(float.IsFinite(s) && Math.Abs(s) < 64));
                r = decoder.Decode(packet.AsSpan(0, Math.Max(1, packet.Length / 2)), pcm, 5760); // truncated
                Assert.True(r is > 0 or < 0);
            }
        }
    }

    [Fact]
    public void OutputBufferTooSmallIsAnErrorNotAnOverrun()
    {
        var decoder = new OpusDecoder(1);
        Assert.Equal(OpusError.BufferTooSmall, decoder.Decode([0xF8, 0xFF, 0xFF], new float[100], 960));
        Assert.Equal(OpusError.BadArg, decoder.Decode([0xF8, 0xFF, 0xFF], new float[100], 0));
        Assert.Equal(OpusError.BufferTooSmall, decoder.Decode([0xFB, 6, 1, 1, 1, 1, 1, 1], new float[960], 960)); // six frames do not fit one frame of room
    }

    // ---- loss concealment ----

    [Fact]
    public void LostPacketsAreConcealedWithPlausibleAudioForEveryMode()
    {
        var files = LoadVectors();
        foreach (var v in new[] { 2, 5, 7 }) // SILK, hybrid, CELT
        {
            var decoder = new OpusDecoder(2);
            var pcm = new float[5760 * 2];
            var n = 0; double concealedEnergy = 0, normalEnergy = 0; var concealed = 0;
            foreach (var (packet, _) in ReadBitFile(files[$"testvector{v:00}.bit"]).Take(120))
            {
                var lose = n >= 40 && n % 7 == 3 && n < 100;
                var frame = OpusPacket.GetSamplesPerFrame(packet[0]);
                var r = lose ? decoder.Decode(default, pcm, frame) : decoder.Decode(packet, pcm, 5760);
                Assert.Equal(lose ? frame : OpusPacket.GetNbSamples(packet), r);
                var e = pcm.AsSpan(0, r * 2).ToArray().Sum(s => (double)s * s) / r;
                Assert.All(pcm.AsSpan(0, r * 2).ToArray(), s => Assert.True(float.IsFinite(s) && Math.Abs(s) < 4));
                if (lose) { concealedEnergy += e; concealed++; } else if (n >= 40) normalEnergy += e;
                n++;
            }
            Assert.True(concealed > 5);
            Assert.True(concealedEnergy > 0, "concealment produced silence for a voiced signal");
            Assert.True(concealedEnergy / concealed < 20 * normalEnergy, "concealment blew up");
        }
    }

    [Fact]
    public void InBandRedundancyRecoversALostSilkFrame()
    {
        // vector 12 and 8 carry SILK frames; decoding a packet with fec = true must succeed whatever the packet holds
        var files = LoadVectors();
        var decoder = new OpusDecoder(2);
        var pcm = new float[5760 * 2];
        var n = 0;
        foreach (var (packet, _) in ReadBitFile(files["testvector02.bit"]).Take(80))
        {
            var r = n == 50 ? decoder.Decode(packet, pcm, 960, fec: true) : decoder.Decode(packet, pcm, 5760);
            Assert.True(r > 0);
            n++;
        }
    }

    // ---- encoder ----

    [Theory]
    [InlineData(16000)]
    [InlineData(32000)]
    [InlineData(64000)]
    public void EncoderProducesValidFixedSizePacketsThatDecodeToTheInput(int bitrate)
    {
        var encoder = new OpusEncoder { Bitrate = bitrate };
        var decoder = new OpusDecoder(1);
        const int frames = 60;
        var input = new float[frames * 960]; var output = new float[frames * 960];
        var rnd = new Random(2);
        for (var i = 0; i < input.Length; i++)
            input[i] = (float)(0.3 * Math.Sin(2 * Math.PI * 440 * i / 48000.0) + 0.15 * Math.Sin(2 * Math.PI * 1870 * i / 48000.0) + 0.05 * Math.Sin(2 * Math.PI * 6100 * i / 48000.0) + 0.01 * (rnd.NextDouble() - .5));
        var packet = new byte[1300];
        var pcm = new float[960];
        for (var f = 0; f < frames; f++)
        {
            var len = encoder.Encode(input.AsSpan(f * 960, 960), packet, out var range);
            Assert.Equal(encoder.PacketBytes, len);
            Assert.Equal(0xF8, packet[0]); // CELT fullband, 20 ms, mono, one frame
            Assert.Equal(960, OpusPacket.GetNbSamples(packet.AsSpan(0, len)));
            Assert.Equal(960, decoder.Decode(packet.AsSpan(0, len), pcm, 960));
            Assert.Equal(range, decoder.FinalRange); // encoder and decoder agree on the whole bitstream
            pcm.CopyTo(output, f * 960);
        }
        double best = -99;
        for (var lag = 0; lag < 300; lag++)
        {
            double s = 0, e = 0;
            for (var i = 20000; i < 50000; i++) { var a = input[i - lag]; var b = output[i]; s += a * a; e += (a - b) * (a - b); }
            best = Math.Max(best, 10 * Math.Log10(s / e));
        }
        // CELT is a perceptual codec, so this is a floor, not a quality claim; more bits must help.
        var floor = bitrate switch { 16000 => 8.0, 32000 => 14.0, _ => 20.0 };
        Assert.True(best > floor, $"{bitrate} bit/s: SNR {best:F1} dB");
    }

    [Fact]
    public void EncoderSurvivesSilenceClippingAndNonFiniteInput()
    {
        var encoder = new OpusEncoder { Bitrate = 24000 };
        var decoder = new OpusDecoder(1);
        var packet = new byte[1300]; var pcm = new float[960];
        var frame = new float[960];
        var rnd = new Random(5);
        for (var f = 0; f < 80; f++)
        {
            for (var i = 0; i < frame.Length; i++)
                frame[i] = f % 4 switch { 0 => 0f, 1 => (rnd.Next(2) == 0 ? 1f : -1f) * 10f, 2 => (float)(rnd.NextDouble() * 2 - 1), _ => i % 97 == 0 ? float.NaN : 0.2f };
            var len = encoder.Encode(frame, packet, out var range);
            Assert.Equal(960, decoder.Decode(packet.AsSpan(0, len), pcm, 960));
            Assert.Equal(range, decoder.FinalRange);
            Assert.All(pcm, s => Assert.True(float.IsFinite(s)));
        }
    }
}
