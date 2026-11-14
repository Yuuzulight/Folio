// Band energy shaping, folding, stereo coding and the band-by-band shape quantiser of the CELT layer
// (RFC 6716 sections 4.3.2 - 4.3.5), after the Opus reference implementation (celt/bands.c; Copyright (c) 2007-2009
// Xiph.Org Foundation, CSIRO, Gregory Maxwell; BSD 3-clause, see docs/webrtc-opus.md). The same code path serves the
// decoder and the encoder, as in the reference, selected by <see cref="BandCtx.Encode"/>.
namespace Folio.Scripting.Rtc.Opus;

internal sealed class BandCtx
{
    public bool Encode, Resynth;
    public int I, Intensity, Spread, TfChange;
    public RangeDecoder? Dec;
    public RangeEncoder? Enc;
    public int RemainingBits;
    public float[]? BandE;
    public uint Seed;
    public int ThetaRound;
    public bool DisableInv, AvoidSplitNoise;

    public uint TellFrac => Encode ? Enc!.TellFrac : Dec!.TellFrac;

    public BandCtx Clone() => (BandCtx)MemberwiseClone();
}

internal struct SplitCtx
{
    public bool Inv;
    public int Imid, Iside, Delta, Itheta, Qalloc;
}

internal static class CeltBands
{
    private static readonly int[] OrderyTable =
    [
        1, 0,
        3, 0, 2, 1,
        7, 0, 4, 3, 6, 1, 5, 2,
        15, 0, 8, 7, 12, 3, 11, 4, 14, 1, 9, 6, 13, 2, 10, 5,
    ];

    internal static void ComputeBandEnergies(ReadOnlySpan<float> x, float[] bandE, int end, int c, int lm)
    {
        var n = Celt.ShortMdctSize << lm;
        for (var ch = 0; ch < c; ch++)
            for (var i = 0; i < end; i++)
            {
                var lo = (Celt.EBands[i] << lm) + ch * n;
                var len = (Celt.EBands[i + 1] - Celt.EBands[i]) << lm;
                var sum = 1e-27f;
                for (var j = 0; j < len; j++) sum += x[lo + j] * x[lo + j];
                bandE[i + ch * Celt.NbEBands] = MathF.Sqrt(sum);
            }
    }

    internal static void NormaliseBands(ReadOnlySpan<float> freq, Span<float> x, float[] bandE, int end, int c, int m)
    {
        var n = m * Celt.ShortMdctSize;
        for (var ch = 0; ch < c; ch++)
            for (var i = 0; i < end; i++)
            {
                var g = 1f / (1e-27f + bandE[i + ch * Celt.NbEBands]);
                for (var j = m * Celt.EBands[i]; j < m * Celt.EBands[i + 1]; j++) x[j + ch * n] = freq[j + ch * n] * g;
            }
    }

    internal static void DenormaliseBands(ReadOnlySpan<float> x, Span<float> freq, ReadOnlySpan<float> bandLogE, int start, int end, int m, bool silence)
    {
        var n = m * Celt.ShortMdctSize;
        var bound = m * Celt.EBands[end];
        if (silence) { bound = 0; start = end = 0; }
        var f = 0;
        var xi = m * Celt.EBands[start];
        for (var i = 0; i < m * Celt.EBands[start]; i++) freq[f++] = 0;
        for (var i = start; i < end; i++)
        {
            var bandEnd = m * Celt.EBands[i + 1];
            var lg = bandLogE[i] + Celt.EMeans[i];
            var g = MathF.Pow(2f, MathF.Min(32f, lg));
            for (var j = m * Celt.EBands[i]; j < bandEnd; j++) freq[f++] = x[xi++] * g;
        }
        freq[bound..n].Clear();
    }

    internal static void AntiCollapse(Span<float> xAll, byte[] collapseMasks, int lm, int c, int size, int start, int end,
        ReadOnlySpan<float> logE, ReadOnlySpan<float> prev1logE, ReadOnlySpan<float> prev2logE, int[] pulses, uint seed)
    {
        for (var i = start; i < end; i++)
        {
            var n0 = Celt.EBands[i + 1] - Celt.EBands[i];
            var depth = (1 + pulses[i]) / n0 >> lm;
            var thresh = .5f * MathF.Pow(2f, -.125f * depth);
            var sqrt1 = 1f / MathF.Sqrt(n0 << lm);
            for (var ch = 0; ch < c; ch++)
            {
                var prev1 = prev1logE[ch * Celt.NbEBands + i];
                var prev2 = prev2logE[ch * Celt.NbEBands + i];
                if (c == 1)
                {
                    prev1 = MathF.Max(prev1, prev1logE[Celt.NbEBands + i]);
                    prev2 = MathF.Max(prev2, prev2logE[Celt.NbEBands + i]);
                }
                var ediff = logE[ch * Celt.NbEBands + i] - MathF.Min(prev1, prev2);
                ediff = MathF.Max(0, ediff);
                var r = 2f * MathF.Pow(2f, -ediff);
                if (lm == 3) r *= 1.41421356f;
                r = MathF.Min(thresh, r);
                r *= sqrt1;
                var x = xAll.Slice(ch * size + (Celt.EBands[i] << lm));
                var renormalize = false;
                for (var k = 0; k < 1 << lm; k++)
                {
                    if ((collapseMasks[i * c + ch] & (1 << k)) == 0)
                    {
                        for (var j = 0; j < n0; j++)
                        {
                            seed = Celt.LcgRand(seed);
                            x[(j << lm) + k] = (seed & 0x8000) != 0 ? r : -r;
                        }
                        renormalize = true;
                    }
                }
                if (renormalize) CeltPvq.RenormaliseVector(x, n0 << lm, 1f);
            }
        }
    }

    private static void IntensityStereo(Span<float> x, Span<float> y, float[] bandE, int bandId, int n)
    {
        var left = bandE[bandId];
        var right = bandE[bandId + Celt.NbEBands];
        var norm = Celt.Epsilon + MathF.Sqrt(Celt.Epsilon + left * left + right * right);
        var a1 = left / norm;
        var a2 = right / norm;
        for (var j = 0; j < n; j++) x[j] = a1 * x[j] + a2 * y[j];
    }

    private static void StereoSplit(Span<float> x, Span<float> y, int n)
    {
        for (var j = 0; j < n; j++)
        {
            var l = .70710678f * x[j];
            var r = .70710678f * y[j];
            x[j] = l + r;
            y[j] = r - l;
        }
    }

    private static void StereoMerge(Span<float> x, Span<float> y, float mid, int n)
    {
        float xp = 0, side = 0;
        for (var j = 0; j < n; j++) { xp += y[j] * x[j]; side += y[j] * y[j]; }
        xp = mid * xp;
        var el = mid * mid + side - 2 * xp;
        var er = mid * mid + side + 2 * xp;
        if (er < 6e-4f || el < 6e-4f)
        {
            x[..n].CopyTo(y);
            return;
        }
        var lgain = 1f / MathF.Sqrt(el);
        var rgain = 1f / MathF.Sqrt(er);
        for (var j = 0; j < n; j++)
        {
            var l = mid * x[j];
            var r = y[j];
            x[j] = lgain * (l - r);
            y[j] = rgain * (l + r);
        }
    }

    internal static int SpreadingDecision(ReadOnlySpan<float> x, ref int average, int lastDecision, ref int hfAverage, ref int tapsetDecision,
        bool updateHf, int end, int c, int m, ReadOnlySpan<int> spreadWeight)
    {
        var sum = 0; var nbBands = 0; var hfSum = 0;
        var n0 = m * Celt.ShortMdctSize;
        if (m * (Celt.EBands[end] - Celt.EBands[end - 1]) <= 8) return Celt.SpreadNone;
        for (var ch = 0; ch < c; ch++)
            for (var i = 0; i < end; i++)
            {
                var n = m * (Celt.EBands[i + 1] - Celt.EBands[i]);
                if (n <= 8) continue;
                var xs = x.Slice(m * Celt.EBands[i] + ch * n0, n);
                int t0 = 0, t1 = 0, t2 = 0;
                for (var j = 0; j < n; j++)
                {
                    var x2n = xs[j] * xs[j] * n;
                    if (x2n < 0.25f) t0++;
                    if (x2n < 0.0625f) t1++;
                    if (x2n < 0.015625f) t2++;
                }
                if (i > Celt.NbEBands - 4) hfSum += 32 * (t1 + t0) / n;
                var tmp = (2 * t2 >= n ? 1 : 0) + (2 * t1 >= n ? 1 : 0) + (2 * t0 >= n ? 1 : 0);
                sum += tmp * spreadWeight[i];
                nbBands += spreadWeight[i];
            }
        if (updateHf)
        {
            if (hfSum != 0) hfSum /= c * (4 - Celt.NbEBands + end);
            hfAverage = (hfAverage + hfSum) >> 1;
            hfSum = hfAverage;
            if (tapsetDecision == 2) hfSum += 4; else if (tapsetDecision == 0) hfSum -= 4;
            tapsetDecision = hfSum > 22 ? 2 : hfSum > 18 ? 1 : 0;
        }
        sum = (sum << 8) / nbBands;
        sum = (sum + average) >> 1;
        average = sum;
        sum = (3 * sum + (((3 - lastDecision) << 7) + 64) + 2) >> 2;
        return sum < 80 ? Celt.SpreadAggressive : sum < 256 ? Celt.SpreadNormal : sum < 384 ? Celt.SpreadLight : Celt.SpreadNone;
    }

    private static void DeinterleaveHadamard(Span<float> x, int n0, int stride, bool hadamard)
    {
        var n = n0 * stride;
        Span<float> tmp = stackalloc float[n];
        if (hadamard)
        {
            var ordery = OrderyTable.AsSpan(stride - 2);
            for (var i = 0; i < stride; i++)
                for (var j = 0; j < n0; j++) tmp[ordery[i] * n0 + j] = x[j * stride + i];
        }
        else
            for (var i = 0; i < stride; i++)
                for (var j = 0; j < n0; j++) tmp[i * n0 + j] = x[j * stride + i];
        tmp.CopyTo(x);
    }

    private static void InterleaveHadamard(Span<float> x, int n0, int stride, bool hadamard)
    {
        var n = n0 * stride;
        Span<float> tmp = stackalloc float[n];
        if (hadamard)
        {
            var ordery = OrderyTable.AsSpan(stride - 2);
            for (var i = 0; i < stride; i++)
                for (var j = 0; j < n0; j++) tmp[j * stride + i] = x[ordery[i] * n0 + j];
        }
        else
            for (var i = 0; i < stride; i++)
                for (var j = 0; j < n0; j++) tmp[j * stride + i] = x[i * n0 + j];
        tmp.CopyTo(x);
    }

    internal static void Haar1(Span<float> x, int n0, int stride)
    {
        n0 >>= 1;
        for (var i = 0; i < stride; i++)
            for (var j = 0; j < n0; j++)
            {
                var tmp1 = .70710678f * x[stride * 2 * j + i];
                var tmp2 = .70710678f * x[stride * (2 * j + 1) + i];
                x[stride * 2 * j + i] = tmp1 + tmp2;
                x[stride * (2 * j + 1) + i] = tmp1 - tmp2;
            }
    }

    private static int ComputeQn(int n, int b, int offset, int pulseCap, bool stereo)
    {
        ReadOnlySpan<short> exp2Table8 = [16384, 17866, 19483, 21247, 23170, 25267, 27554, 30048];
        var n2 = 2 * n - 1;
        if (stereo && n == 2) n2--;
        var qb = (b + n2 * offset) / n2;
        qb = Math.Min(b - pulseCap - (4 << Celt.BitRes), qb);
        qb = Math.Min(8 << Celt.BitRes, qb);
        int qn;
        if (qb < (1 << Celt.BitRes >> 1)) qn = 1;
        else
        {
            qn = exp2Table8[qb & 0x7] >> (14 - (qb >> Celt.BitRes));
            qn = (qn + 1) >> 1 << 1;
        }
        return qn;
    }

    private static int StereoItheta(ReadOnlySpan<float> x, ReadOnlySpan<float> y, bool stereo, int n)
    {
        float emid = Celt.Epsilon, eside = Celt.Epsilon;
        if (stereo)
            for (var i = 0; i < n; i++)
            {
                var m = x[i] + y[i];
                var s = x[i] - y[i];
                emid += m * m;
                eside += s * s;
            }
        else
            for (var i = 0; i < n; i++) { emid += x[i] * x[i]; eside += y[i] * y[i]; }
        var mid = MathF.Sqrt(emid);
        var side = MathF.Sqrt(eside);
        return (int)MathF.Floor(.5f + 16384 * 0.63662f * MathF.Atan2(side, mid));
    }

    private static void ComputeTheta(BandCtx ctx, out SplitCtx sctx, Span<float> x, Span<float> y, int n, ref int b, int bb, int b0, int lm, bool stereo, ref int fill)
    {
        var encode = ctx.Encode;
        var i = ctx.I;
        var intensity = ctx.Intensity;
        var pulseCap = Celt.LogN(i) + lm * (1 << Celt.BitRes);
        var offset = (pulseCap >> 1) - (stereo && n == 2 ? Celt.QthetaOffsetTwophase : Celt.QthetaOffset);
        var qn = ComputeQn(n, b, offset, pulseCap, stereo);
        if (stereo && i >= intensity) qn = 1;
        var itheta = 0;
        if (encode) itheta = StereoItheta(x, y, stereo, n);
        var tell = (int)ctx.TellFrac;
        var inv = false;
        int imid, iside, delta;
        if (qn != 1)
        {
            if (encode)
            {
                if (!stereo || ctx.ThetaRound == 0)
                {
                    itheta = (itheta * qn + 8192) >> 14;
                    if (!stereo && ctx.AvoidSplitNoise && itheta > 0 && itheta < qn)
                    {
                        var unquantized = itheta * 16384 / qn;
                        imid = Celt.BitexactCos((short)unquantized);
                        iside = Celt.BitexactCos((short)(16384 - unquantized));
                        delta = Celt.FracMul16((n - 1) << 7, Celt.BitexactLog2Tan(iside, imid));
                        if (delta > b) itheta = qn; else if (delta < -b) itheta = 0;
                    }
                }
                else
                {
                    var bias = itheta > 8192 ? 32767 / qn : -32767 / qn;
                    var down = Math.Min(qn - 1, Math.Max(0, (itheta * qn + bias) >> 14));
                    itheta = ctx.ThetaRound < 0 ? down : down + 1;
                }
            }
            if (stereo && n > 2)
            {
                const int p0 = 3;
                var xv = itheta;
                var x0 = qn / 2;
                var ft = p0 * (x0 + 1) + x0;
                if (encode)
                {
                    ctx.Enc!.Encode((uint)(xv <= x0 ? p0 * xv : (xv - 1 - x0) + (x0 + 1) * p0), (uint)(xv <= x0 ? p0 * (xv + 1) : (xv - x0) + (x0 + 1) * p0), (uint)ft);
                }
                else
                {
                    var fs = (int)ctx.Dec!.Decode((uint)ft);
                    xv = fs < (x0 + 1) * p0 ? fs / p0 : x0 + 1 + (fs - (x0 + 1) * p0);
                    ctx.Dec.Update((uint)(xv <= x0 ? p0 * xv : (xv - 1 - x0) + (x0 + 1) * p0), (uint)(xv <= x0 ? p0 * (xv + 1) : (xv - x0) + (x0 + 1) * p0), (uint)ft);
                    itheta = xv;
                }
            }
            else if (b0 > 1 || stereo)
            {
                if (encode) ctx.Enc!.Uint((uint)itheta, (uint)(qn + 1));
                else itheta = (int)ctx.Dec!.Uint((uint)(qn + 1));
            }
            else
            {
                var ft = ((qn >> 1) + 1) * ((qn >> 1) + 1);
                if (encode)
                {
                    var fs = itheta <= (qn >> 1) ? itheta + 1 : qn + 1 - itheta;
                    var fl = itheta <= (qn >> 1) ? itheta * (itheta + 1) >> 1 : ft - ((qn + 1 - itheta) * (qn + 2 - itheta) >> 1);
                    ctx.Enc!.Encode((uint)fl, (uint)(fl + fs), (uint)ft);
                }
                else
                {
                    int fs, fl;
                    var fm = (int)ctx.Dec!.Decode((uint)ft);
                    if (fm < ((qn >> 1) * ((qn >> 1) + 1) >> 1))
                    {
                        itheta = (int)(Celt.Isqrt32(8 * (uint)fm + 1) - 1) >> 1;
                        fs = itheta + 1;
                        fl = itheta * (itheta + 1) >> 1;
                    }
                    else
                    {
                        itheta = (2 * (qn + 1) - (int)Celt.Isqrt32(8 * (uint)(ft - fm - 1) + 1)) >> 1;
                        fs = qn + 1 - itheta;
                        fl = ft - ((qn + 1 - itheta) * (qn + 2 - itheta) >> 1);
                    }
                    ctx.Dec.Update((uint)fl, (uint)(fl + fs), (uint)ft);
                }
            }
            itheta = itheta * 16384 / qn;
            if (encode && stereo)
            {
                if (itheta == 0) IntensityStereo(x, y, ctx.BandE!, i, n);
                else StereoSplit(x, y, n);
            }
        }
        else if (stereo)
        {
            if (encode)
            {
                inv = itheta > 8192 && !ctx.DisableInv;
                if (inv) for (var j = 0; j < n; j++) y[j] = -y[j];
                IntensityStereo(x, y, ctx.BandE!, i, n);
            }
            if (b > 2 << Celt.BitRes && ctx.RemainingBits > 2 << Celt.BitRes)
            {
                if (encode) ctx.Enc!.BitLogp(inv ? 1 : 0, 2);
                else inv = ctx.Dec!.BitLogp(2) != 0;
            }
            else inv = false;
            if (ctx.DisableInv) inv = false;
            itheta = 0;
        }
        var qalloc = (int)ctx.TellFrac - tell;
        b -= qalloc;
        if (itheta == 0)
        {
            imid = 32767; iside = 0;
            fill &= (1 << bb) - 1;
            delta = -16384;
        }
        else if (itheta == 16384)
        {
            imid = 0; iside = 32767;
            fill &= ((1 << bb) - 1) << bb;
            delta = 16384;
        }
        else
        {
            imid = Celt.BitexactCos((short)itheta);
            iside = Celt.BitexactCos((short)(16384 - itheta));
            delta = Celt.FracMul16((n - 1) << 7, Celt.BitexactLog2Tan(iside, imid));
        }
        sctx = new SplitCtx { Inv = inv, Imid = imid, Iside = iside, Delta = delta, Itheta = itheta, Qalloc = qalloc };
    }

    private static uint QuantBandN1(BandCtx ctx, Span<float> x, Span<float> y, Span<float> lowbandOut)
    {
        var stereo = !y.IsEmpty;
        var cur = x;
        for (var c = 0; c < (stereo ? 2 : 1); c++)
        {
            var sign = 0;
            if (ctx.RemainingBits >= 1 << Celt.BitRes)
            {
                if (ctx.Encode) { sign = cur[0] < 0 ? 1 : 0; ctx.Enc!.Bits((uint)sign, 1); }
                else sign = (int)ctx.Dec!.Bits(1);
                ctx.RemainingBits -= 1 << Celt.BitRes;
            }
            if (ctx.Resynth) cur[0] = sign != 0 ? -1f : 1f;
            cur = y;
        }
        if (!lowbandOut.IsEmpty) lowbandOut[0] = x[0];
        return 1;
    }

    private static uint QuantPartition(BandCtx ctx, Span<float> x, int n, int b, int bb, Span<float> lowband, int lm, float gain, int fill)
    {
        var i = ctx.I;
        var b0 = bb;
        uint cm = 0;
        var cache = CeltModeTables.CacheIndex50[(lm + 1) * Celt.NbEBands + i];
        var cacheBits = CeltModeTables.CacheBits50;
        if (lm != -1 && b > cacheBits[cache + cacheBits[cache]] + 12 && n > 2)
        {
            n >>= 1;
            var y = x.Slice(n);
            lm -= 1;
            if (bb == 1) fill = (fill & 1) | (fill << 1);
            bb = (bb + 1) >> 1;
            ComputeTheta(ctx, out var sctx, x, y, n, ref b, bb, b0, lm, false, ref fill);
            var imid = sctx.Imid;
            var iside = sctx.Iside;
            var delta = sctx.Delta;
            var itheta = sctx.Itheta;
            var qalloc = sctx.Qalloc;
            var mid = (1f / 32768) * imid;
            var side = (1f / 32768) * iside;
            if (b0 > 1 && (itheta & 0x3fff) != 0)
            {
                if (itheta > 8192) delta -= delta >> (4 - lm);
                else delta = Math.Min(0, delta + (n << Celt.BitRes >> (5 - lm)));
            }
            var mbits = Math.Max(0, Math.Min(b, (b - delta) / 2));
            var sbits = b - mbits;
            ctx.RemainingBits -= qalloc;
            var nextLowband2 = lowband.IsEmpty ? lowband : lowband.Slice(n);
            var rebalance = ctx.RemainingBits;
            if (mbits >= sbits)
            {
                cm = QuantPartition(ctx, x, n, mbits, bb, lowband, lm, gain * mid, fill);
                rebalance = mbits - (rebalance - ctx.RemainingBits);
                if (rebalance > 3 << Celt.BitRes && itheta != 0) sbits += rebalance - (3 << Celt.BitRes);
                cm |= QuantPartition(ctx, y, n, sbits, bb, nextLowband2, lm, gain * side, fill >> bb) << (b0 >> 1);
            }
            else
            {
                cm = QuantPartition(ctx, y, n, sbits, bb, nextLowband2, lm, gain * side, fill >> bb) << (b0 >> 1);
                rebalance = sbits - (rebalance - ctx.RemainingBits);
                if (rebalance > 3 << Celt.BitRes && itheta != 16384) mbits += rebalance - (3 << Celt.BitRes);
                cm |= QuantPartition(ctx, x, n, mbits, bb, lowband, lm, gain * mid, fill);
            }
        }
        else
        {
            var q = Celt.Bits2Pulses(i, lm, b);
            var currBits = Celt.Pulses2Bits(i, lm, q);
            ctx.RemainingBits -= currBits;
            while (ctx.RemainingBits < 0 && q > 0)
            {
                ctx.RemainingBits += currBits;
                q--;
                currBits = Celt.Pulses2Bits(i, lm, q);
                ctx.RemainingBits -= currBits;
            }
            if (q != 0)
            {
                var k = Celt.GetPulses(q);
                cm = ctx.Encode ? CeltPvq.AlgQuant(x, n, k, ctx.Spread, bb, ctx.Enc!) : CeltPvq.AlgUnquant(x, n, k, ctx.Spread, bb, ctx.Dec!, gain);
            }
            else if (ctx.Resynth)
            {
                var cmMask = (uint)((1UL << bb) - 1);
                fill &= (int)cmMask;
                if (fill == 0) x[..n].Clear();
                else
                {
                    if (lowband.IsEmpty)
                    {
                        for (var j = 0; j < n; j++)
                        {
                            ctx.Seed = Celt.LcgRand(ctx.Seed);
                            x[j] = (int)ctx.Seed >> 20;
                        }
                        cm = cmMask;
                    }
                    else
                    {
                        for (var j = 0; j < n; j++)
                        {
                            ctx.Seed = Celt.LcgRand(ctx.Seed);
                            var tmp = 1.0f / 256;
                            tmp = (ctx.Seed & 0x8000) != 0 ? tmp : -tmp;
                            x[j] = lowband[j] + tmp;
                        }
                        cm = (uint)fill;
                    }
                    CeltPvq.RenormaliseVector(x, n, gain);
                }
            }
        }
        return cm;
    }

    private static readonly byte[] BitInterleaveTable = [0, 1, 1, 1, 2, 3, 3, 3, 2, 3, 3, 3, 2, 3, 3, 3];
    private static readonly byte[] BitDeinterleaveTable = [0x00, 0x03, 0x0C, 0x0F, 0x30, 0x33, 0x3C, 0x3F, 0xC0, 0xC3, 0xCC, 0xCF, 0xF0, 0xF3, 0xFC, 0xFF];

    private static uint QuantBand(BandCtx ctx, Span<float> x, int n, int b, int bb, Span<float> lowband, int lm, Span<float> lowbandOut, float gain, Span<float> lowbandScratch, int fill)
    {
        var n0 = n;
        var nB = n;
        var b0 = bb;
        var timeDivide = 0;
        var recombine = 0;
        var longBlocks = b0 == 1;
        var tfChange = ctx.TfChange;
        nB /= bb;
        if (n == 1) return QuantBandN1(ctx, x, default, lowbandOut);
        if (tfChange > 0) recombine = tfChange;
        if (!lowbandScratch.IsEmpty && !lowband.IsEmpty && (recombine != 0 || ((nB & 1) == 0 && tfChange < 0) || b0 > 1))
        {
            lowband[..n].CopyTo(lowbandScratch);
            lowband = lowbandScratch;
        }
        for (var k = 0; k < recombine; k++)
        {
            if (ctx.Encode) Haar1(x, n >> k, 1 << k);
            if (!lowband.IsEmpty) Haar1(lowband, n >> k, 1 << k);
            fill = BitInterleaveTable[fill & 0xF] | BitInterleaveTable[fill >> 4] << 2;
        }
        bb >>= recombine;
        nB <<= recombine;
        while ((nB & 1) == 0 && tfChange < 0)
        {
            if (ctx.Encode) Haar1(x, nB, bb);
            if (!lowband.IsEmpty) Haar1(lowband, nB, bb);
            fill |= fill << bb;
            bb <<= 1;
            nB >>= 1;
            timeDivide++;
            tfChange++;
        }
        b0 = bb;
        var nB0 = nB;
        if (b0 > 1)
        {
            if (ctx.Encode) DeinterleaveHadamard(x, nB >> recombine, b0 << recombine, longBlocks);
            if (!lowband.IsEmpty) DeinterleaveHadamard(lowband, nB >> recombine, b0 << recombine, longBlocks);
        }
        var cm = QuantPartition(ctx, x, n, b, bb, lowband, lm, gain, fill);
        if (ctx.Resynth)
        {
            if (b0 > 1) InterleaveHadamard(x, nB >> recombine, b0 << recombine, longBlocks);
            nB = nB0;
            bb = b0;
            for (var k = 0; k < timeDivide; k++)
            {
                bb >>= 1;
                nB <<= 1;
                cm |= cm >> bb;
                Haar1(x, nB, bb);
            }
            for (var k = 0; k < recombine; k++)
            {
                cm = BitDeinterleaveTable[cm];
                Haar1(x, n0 >> k, 1 << k);
            }
            bb <<= recombine;
            if (!lowbandOut.IsEmpty)
            {
                var nn = MathF.Sqrt(n0);
                for (var j = 0; j < n0; j++) lowbandOut[j] = nn * x[j];
            }
            cm &= (uint)((1 << bb) - 1);
        }
        return cm;
    }

    private static uint QuantBandStereo(BandCtx ctx, Span<float> x, Span<float> y, int n, int b, int bb, Span<float> lowband, int lm, Span<float> lowbandOut, Span<float> lowbandScratch, int fill)
    {
        if (n == 1) return QuantBandN1(ctx, x, y, lowbandOut);
        var origFill = fill;
        ComputeTheta(ctx, out var sctx, x, y, n, ref b, bb, bb, lm, true, ref fill);
        var inv = sctx.Inv;
        var delta = sctx.Delta;
        var itheta = sctx.Itheta;
        var qalloc = sctx.Qalloc;
        var mid = (1f / 32768) * sctx.Imid;
        var side = (1f / 32768) * sctx.Iside;
        uint cm;
        int mbits, sbits;
        if (n == 2)
        {
            var sign = 0;
            mbits = b;
            sbits = 0;
            if (itheta != 0 && itheta != 16384) sbits = 1 << Celt.BitRes;
            mbits -= sbits;
            var c = itheta > 8192;
            ctx.RemainingBits -= qalloc + sbits;
            var x2 = c ? y : x;
            var y2 = c ? x : y;
            if (sbits != 0)
            {
                if (ctx.Encode) { sign = x2[0] * y2[1] - x2[1] * y2[0] < 0 ? 1 : 0; ctx.Enc!.Bits((uint)sign, 1); }
                else sign = (int)ctx.Dec!.Bits(1);
            }
            sign = 1 - 2 * sign;
            cm = QuantBand(ctx, x2, n, mbits, bb, lowband, lm, lowbandOut, 1f, lowbandScratch, origFill);
            y2[0] = -sign * x2[1];
            y2[1] = sign * x2[0];
            if (ctx.Resynth)
            {
                x[0] = mid * x[0]; x[1] = mid * x[1];
                y[0] = side * y[0]; y[1] = side * y[1];
                var tmp = x[0];
                x[0] = tmp - y[0]; y[0] = tmp + y[0];
                tmp = x[1];
                x[1] = tmp - y[1]; y[1] = tmp + y[1];
            }
        }
        else
        {
            mbits = Math.Max(0, Math.Min(b, (b - delta) / 2));
            sbits = b - mbits;
            ctx.RemainingBits -= qalloc;
            var rebalance = ctx.RemainingBits;
            if (mbits >= sbits)
            {
                cm = QuantBand(ctx, x, n, mbits, bb, lowband, lm, lowbandOut, 1f, lowbandScratch, fill);
                rebalance = mbits - (rebalance - ctx.RemainingBits);
                if (rebalance > 3 << Celt.BitRes && itheta != 0) sbits += rebalance - (3 << Celt.BitRes);
                cm |= QuantBand(ctx, y, n, sbits, bb, default, lm, default, side, default, fill >> bb);
            }
            else
            {
                cm = QuantBand(ctx, y, n, sbits, bb, default, lm, default, side, default, fill >> bb);
                rebalance = sbits - (rebalance - ctx.RemainingBits);
                if (rebalance > 3 << Celt.BitRes && itheta != 16384) mbits += rebalance - (3 << Celt.BitRes);
                cm |= QuantBand(ctx, x, n, mbits, bb, lowband, lm, lowbandOut, 1f, lowbandScratch, fill);
            }
        }
        if (ctx.Resynth)
        {
            if (n != 2) StereoMerge(x, y, mid, n);
            if (inv) for (var j = 0; j < n; j++) y[j] = -y[j];
        }
        return cm;
    }

    /// <summary>Quantises (encoder) or reconstructs (decoder) all bands. <paramref name="yAll"/> is empty for a mono stream.</summary>
    internal static void QuantAllBands(bool encode, int start, int end, Span<float> xAll, Span<float> yAll, byte[] collapseMasks,
        float[]? bandE, int[] pulses, bool shortBlocks, int spread, bool dualStereo, int intensity, int[] tfRes, int totalBits,
        int balance, RangeDecoder? dec, RangeEncoder? enc, int lm, int codedBands, ref uint seed, bool disableInv)
    {
        var m = 1 << lm;
        var bBlocks = shortBlocks ? m : 1;
        var normOffset = m * Celt.EBands[start];
        var c = yAll.IsEmpty ? 1 : 2;
        var normLen = m * Celt.EBands[Celt.NbEBands - 1] - normOffset;
        Span<float> normAll = stackalloc float[c * normLen];
        normAll.Clear();
        var norm = normAll[..normLen];
        var norm2 = c == 2 ? normAll.Slice(normLen, normLen) : default;
        var resynth = !encode;
        var lowbandOffset = 0;
        var updateLowband = true;
        var ctx = new BandCtx
        {
            Encode = encode, Resynth = resynth, Dec = dec, Enc = enc, BandE = bandE, Intensity = intensity, Spread = spread,
            Seed = seed, DisableInv = disableInv, ThetaRound = 0, AvoidSplitNoise = bBlocks > 1,
        };
        for (var i = start; i < end; i++)
        {
            ctx.I = i;
            var last = i == end - 1;
            var x = xAll.Slice(m * Celt.EBands[i]);
            var y = c == 2 ? yAll.Slice(m * Celt.EBands[i]) : default;
            var n = m * Celt.EBands[i + 1] - m * Celt.EBands[i];
            var tell = (int)ctx.TellFrac;
            if (i != start) balance -= tell;
            var remainingBits = totalBits - tell - 1;
            ctx.RemainingBits = remainingBits;
            int b;
            if (i <= codedBands - 1)
            {
                var currBalance = balance / Math.Min(3, codedBands - i);
                b = Math.Max(0, Math.Min(16383, Math.Min(remainingBits + 1, pulses[i] + currBalance)));
            }
            else b = 0;
            if (resynth && (m * Celt.EBands[i] - n >= m * Celt.EBands[start] || i == start + 1) && (updateLowband || lowbandOffset == 0)) lowbandOffset = i;
            if (i == start + 1)
            {
                // special hybrid folding (the 2017 decoder update, RFC 8251)
                var n1 = m * (Celt.EBands[start + 1] - Celt.EBands[start]);
                var n2 = m * (Celt.EBands[start + 2] - Celt.EBands[start + 1]);
                norm.Slice(2 * n1 - n2, n2 - n1).CopyTo(norm.Slice(n1));
                if (dualStereo) norm2.Slice(2 * n1 - n2, n2 - n1).CopyTo(norm2.Slice(n1));
            }
            var tfChange = tfRes[i];
            ctx.TfChange = tfChange;
            // like the reference, the not yet coded tail of X doubles as scratch for folding (never used by the last band)
            var scratch = last ? default : xAll.Slice(m * Celt.EBands[Celt.NbEBands - 1]);
            int effectiveLowband = -1;
            uint xCm, yCm;
            if (lowbandOffset != 0 && (spread != Celt.SpreadAggressive || bBlocks > 1 || tfChange < 0))
            {
                effectiveLowband = Math.Max(0, m * Celt.EBands[lowbandOffset] - normOffset - n);
                var foldStart = lowbandOffset;
                while (m * Celt.EBands[--foldStart] > effectiveLowband + normOffset) { }
                var foldEnd = lowbandOffset - 1;
                while (++foldEnd < i && m * Celt.EBands[foldEnd] < effectiveLowband + normOffset + n) { }
                xCm = yCm = 0;
                var foldI = foldStart;
                do
                {
                    xCm |= collapseMasks[foldI * c + 0];
                    yCm |= collapseMasks[foldI * c + c - 1];
                } while (++foldI < foldEnd);
            }
            else xCm = yCm = (uint)((1 << bBlocks) - 1);
            if (dualStereo && i == intensity)
            {
                dualStereo = false;
                if (resynth) for (var j = 0; j < m * Celt.EBands[i] - normOffset; j++) norm[j] = .5f * (norm[j] + norm2[j]);
            }
            var lowbandX = effectiveLowband != -1 ? norm.Slice(effectiveLowband) : default;
            var lowbandOutX = last ? default : norm.Slice(m * Celt.EBands[i] - normOffset);
            if (dualStereo)
            {
                var lowbandY = effectiveLowband != -1 ? norm2.Slice(effectiveLowband) : default;
                var lowbandOutY = last ? default : norm2.Slice(m * Celt.EBands[i] - normOffset);
                xCm = QuantBand(ctx, x, n, b / 2, bBlocks, lowbandX, lm, lowbandOutX, 1f, scratch, (int)xCm);
                yCm = QuantBand(ctx, y, n, b / 2, bBlocks, lowbandY, lm, lowbandOutY, 1f, scratch, (int)yCm);
            }
            else
            {
                if (!y.IsEmpty)
                {
                    ctx.ThetaRound = 0;
                    xCm = QuantBandStereo(ctx, x, y, n, b, bBlocks, lowbandX, lm, lowbandOutX, scratch, (int)(xCm | yCm));
                }
                else xCm = QuantBand(ctx, x, n, b, bBlocks, lowbandX, lm, lowbandOutX, 1f, scratch, (int)(xCm | yCm));
                yCm = xCm;
            }
            collapseMasks[i * c + 0] = (byte)xCm;
            collapseMasks[i * c + c - 1] = (byte)yCm;
            balance += pulses[i] + tell;
            updateLowband = b > n << Celt.BitRes;
            ctx.AvoidSplitNoise = false;
        }
        seed = ctx.Seed;
    }
}
