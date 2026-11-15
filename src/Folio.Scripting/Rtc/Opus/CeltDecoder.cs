// CELT decoder (RFC 6716 section 4.3), after the Opus reference implementation (celt/celt_decoder.c, celt/quant_bands.c,
// celt/celt.c; Copyright (c) 2007-2009 Xiph.Org Foundation, CSIRO, Jean-Marc Valin; BSD 3-clause, see docs/webrtc-opus.md).
// Output is 48 kHz; samples are interleaved when the decoder has two channels. Packet loss concealment follows the
// reference structure (noise substitution, and pitch repetition through an LPC excitation) except that the pitch search
// is a plain normalised cross-correlation instead of the reference's multi-stage search; see docs/webrtc-opus.md.
namespace Folio.Scripting.Rtc.Opus;

internal sealed class CeltDecoder
{
    private const int Dbs = Celt.DecodeBufferSize;
    private const int Ov = Celt.Overlap;
    private const int Ne = Celt.NbEBands;

    private readonly int _cc;
    private int _streamChannels;
    internal int Start, End = Ne;
    private readonly bool _disableInv;
    private uint _rng;
    private int _lastPitchIndex, _lossDuration;
    private bool _skipPlc, _prefilterAndFold;
    private int _postfilterPeriod, _postfilterPeriodOld, _postfilterTapset, _postfilterTapsetOld;
    private float _postfilterGain, _postfilterGainOld;
    private readonly float[] _preemphMemD = new float[2];
    private readonly float[][] _mem;
    private readonly float[] _lpc;
    private readonly float[] _oldBandE = new float[2 * Ne], _oldLogE = new float[2 * Ne], _oldLogE2 = new float[2 * Ne], _backgroundLogE = new float[2 * Ne];
    private readonly Mdct _mdct = new(2 * Celt.ShortMdctSize * (1 << Celt.MaxLm), Celt.MaxLm);

    // scratch
    private readonly float[] _x = new float[2 * 960];
    private readonly float[] _freq = new float[960];
    private readonly float[] _freq2 = new float[960];
    private readonly float[] _etmp = new float[Ov];
    private readonly byte[] _collapse = new byte[2 * Ne];

    internal uint FinalRange => _rng;

    internal CeltDecoder(int channels)
    {
        _cc = channels;
        _streamChannels = channels;
        _disableInv = channels == 1;
        _mem = new float[channels][];
        for (var c = 0; c < channels; c++) _mem[c] = new float[Dbs + Ov];
        _lpc = new float[channels * Celt.LpcOrder];
        Reset();
    }

    internal int StreamChannels { get => _streamChannels; set => _streamChannels = Math.Clamp(value, 1, 2); }

    internal void Reset()
    {
        _rng = 0; _lastPitchIndex = 0; _lossDuration = 0;
        _postfilterPeriod = _postfilterPeriodOld = _postfilterTapset = _postfilterTapsetOld = 0;
        _postfilterGain = _postfilterGainOld = 0;
        _prefilterAndFold = false;
        Array.Clear(_preemphMemD);
        foreach (var m in _mem) Array.Clear(m);
        Array.Clear(_lpc); Array.Clear(_oldBandE); Array.Clear(_backgroundLogE);
        Array.Fill(_oldLogE, -28f); Array.Fill(_oldLogE2, -28f);
        _skipPlc = true;
    }

    // ---- comb filter (RFC 6716 section 4.3.7.1) ----

    private static readonly float[] CombGains =
    [
        0.3066406250f, 0.2170410156f, 0.1296386719f,
        0.4638671875f, 0.2680664062f, 0f,
        0.7998046875f, 0.1000976562f, 0f,
    ];

    internal static void CombFilter(float[] y, int yOff, float[] x, int xOff, int t0, int t1, int n, float g0, float g1, int tapset0, int tapset1, float[]? window, int overlap)
    {
        if (g0 == 0 && g1 == 0)
        {
            if (!ReferenceEquals(x, y) || xOff != yOff) Array.Copy(x, xOff, y, yOff, n);
            return;
        }
        t0 = Math.Max(t0, Celt.CombFilterMinPeriod);
        t1 = Math.Max(t1, Celt.CombFilterMinPeriod);
        var g00 = g0 * CombGains[tapset0 * 3];
        var g01 = g0 * CombGains[tapset0 * 3 + 1];
        var g02 = g0 * CombGains[tapset0 * 3 + 2];
        var g10 = g1 * CombGains[tapset1 * 3];
        var g11 = g1 * CombGains[tapset1 * 3 + 1];
        var g12 = g1 * CombGains[tapset1 * 3 + 2];
        var x1 = x[xOff - t1 + 1];
        var x2 = x[xOff - t1];
        var x3 = x[xOff - t1 - 1];
        var x4 = x[xOff - t1 - 2];
        if (g0 == g1 && t0 == t1 && tapset0 == tapset1) overlap = 0;
        int i;
        for (i = 0; i < overlap; i++)
        {
            var x0 = x[xOff + i - t1 + 2];
            var f = window![i] * window[i];
            y[yOff + i] = x[xOff + i]
                + (1 - f) * g00 * x[xOff + i - t0]
                + (1 - f) * g01 * (x[xOff + i - t0 + 1] + x[xOff + i - t0 - 1])
                + (1 - f) * g02 * (x[xOff + i - t0 + 2] + x[xOff + i - t0 - 2])
                + f * g10 * x2
                + f * g11 * (x1 + x3)
                + f * g12 * (x0 + x4);
            x4 = x3; x3 = x2; x2 = x1; x1 = x0;
        }
        if (g1 == 0)
        {
            if (!ReferenceEquals(x, y) || xOff != yOff) Array.Copy(x, xOff + overlap, y, yOff + overlap, n - overlap);
            return;
        }
        // constant filter for the rest of the block
        x4 = x[xOff + i - t1 - 2];
        x3 = x[xOff + i - t1 - 1];
        x2 = x[xOff + i - t1];
        x1 = x[xOff + i - t1 + 1];
        for (; i < n; i++)
        {
            var x0 = x[xOff + i - t1 + 2];
            y[yOff + i] = x[xOff + i] + g10 * x2 + g11 * (x1 + x3) + g12 * (x0 + x4);
            x4 = x3; x3 = x2; x2 = x1; x1 = x0;
        }
    }

    // ---- synthesis ----

    private void Synthesis(float[] x, int c, int cc, bool isTransient, int lm, bool silence, int start, int effEnd)
    {
        var m = 1 << lm;
        var n = Celt.ShortMdctSize << lm;
        int b, nb, shift;
        if (isTransient) { b = m; nb = Celt.ShortMdctSize; shift = Celt.MaxLm; }
        else { b = 1; nb = Celt.ShortMdctSize << lm; shift = Celt.MaxLm - lm; }
        var freq = _freq;
        var window = Celt.Window;
        var outOff = Dbs - n;
        if (cc == 2 && c == 1)
        {
            CeltBands.DenormaliseBands(x, freq, _oldBandE, start, effEnd, m, silence);
            var freq2Off = Ov / 2;
            Array.Copy(freq, 0, _mem[1], outOff + freq2Off, n);
            for (var bi = 0; bi < b; bi++) _mdct.Backward(_mem[1], outOff + freq2Off + bi, _mem[0], outOff + nb * bi, window, Ov, shift, b);
            for (var bi = 0; bi < b; bi++) _mdct.Backward(freq, bi, _mem[1], outOff + nb * bi, window, Ov, shift, b);
        }
        else if (cc == 1 && c == 2)
        {
            var tmp = _freq2;
            CeltBands.DenormaliseBands(x.AsSpan(0, n), freq, _oldBandE, start, effEnd, m, silence);
            CeltBands.DenormaliseBands(x.AsSpan(n, n), tmp, _oldBandE.AsSpan(Ne), start, effEnd, m, silence);
            for (var i = 0; i < n; i++) freq[i] = .5f * freq[i] + .5f * tmp[i];
            for (var bi = 0; bi < b; bi++) _mdct.Backward(freq, bi, _mem[0], outOff + nb * bi, window, Ov, shift, b);
        }
        else
        {
            for (var ch = 0; ch < cc; ch++)
            {
                CeltBands.DenormaliseBands(x.AsSpan(ch * n, n), freq, _oldBandE.AsSpan(ch * Ne), start, effEnd, m, silence);
                for (var bi = 0; bi < b; bi++) _mdct.Backward(freq, bi, _mem[ch], outOff + nb * bi, window, Ov, shift, b);
            }
        }
        for (var ch = 0; ch < cc; ch++)
            for (var i = 0; i < n; i++)
            {
                ref var v = ref _mem[ch][outOff + i];
                v = Math.Clamp(v, -300000000f, 300000000f);
            }
    }

    private void Deemphasis(float[] pcm, int pcmOff, int n, int cc)
    {
        for (var ch = 0; ch < cc; ch++)
        {
            var m = _preemphMemD[ch];
            var src = _mem[ch];
            var s = Dbs - n;
            for (var j = 0; j < n; j++)
            {
                var tmp = src[s + j] + Celt.VerySmall + m;
                m = Celt.Preemphasis * tmp;
                pcm[pcmOff + j * cc + ch] = tmp * (1f / 32768f);
            }
            _preemphMemD[ch] = m;
        }
    }

    private void PrefilterAndFold(int n)
    {
        for (var c = 0; c < _cc; c++)
        {
            CombFilter(_etmp, 0, _mem[c], Dbs - n, _postfilterPeriodOld, _postfilterPeriod, Ov, -_postfilterGainOld, -_postfilterGain, _postfilterTapsetOld, _postfilterTapset, null, 0);
            var w = Celt.Window;
            for (var i = 0; i < Ov / 2; i++) _mem[c][Dbs - n + i] = w[i] * _etmp[Ov - 1 - i] + w[Ov - i - 1] * _etmp[i];
        }
    }

    // ---- energy ----

    private void UnquantCoarseEnergy(int start, int end, bool intra, RangeDecoder dec, int c, int lm)
    {
        var pi0 = (lm * 2 + (intra ? 1 : 0)) * 42;
        float coef, beta;
        if (intra) { coef = 0; beta = Celt.BetaIntra; }
        else { beta = Celt.BetaCoef[lm]; coef = Celt.PredCoef[lm]; }
        Span<float> prev = stackalloc float[2];
        var budget = (int)dec.Storage * 8;
        for (var i = start; i < end; i++)
            for (var ch = 0; ch < c; ch++)
            {
                int qi;
                var tell = dec.Tell;
                if (budget - tell >= 15)
                {
                    var pi = 2 * Math.Min(i, 20);
                    qi = dec.Laplace((uint)Celt.EProbModel[pi0 + pi] << 7, Celt.EProbModel[pi0 + pi + 1] << 6);
                }
                else if (budget - tell >= 2)
                {
                    qi = dec.Icdf(Celt.SmallEnergyIcdf, 2);
                    qi = (qi >> 1) ^ -(qi & 1);
                }
                else if (budget - tell >= 1) qi = -dec.BitLogp(1);
                else qi = -1;
                float q = qi;
                _oldBandE[i + ch * Ne] = MathF.Max(-9f, _oldBandE[i + ch * Ne]);
                _oldBandE[i + ch * Ne] = coef * _oldBandE[i + ch * Ne] + prev[ch] + q;
                prev[ch] = prev[ch] + q - beta * q;
            }
    }

    private void UnquantFineEnergy(int start, int end, int[] fineQuant, RangeDecoder dec, int c)
    {
        for (var i = start; i < end; i++)
        {
            if (fineQuant[i] <= 0) continue;
            for (var ch = 0; ch < c; ch++)
            {
                var q2 = (int)dec.Bits(fineQuant[i]);
                var offset = (q2 + .5f) * (1 << (14 - fineQuant[i])) * (1f / 16384) - .5f;
                _oldBandE[i + ch * Ne] += offset;
            }
        }
    }

    private void UnquantEnergyFinalise(int start, int end, int[] fineQuant, int[] finePriority, int bitsLeft, RangeDecoder dec, int c)
    {
        for (var prio = 0; prio < 2; prio++)
            for (var i = start; i < end && bitsLeft >= c; i++)
            {
                if (fineQuant[i] >= Celt.MaxFineBits || finePriority[i] != prio) continue;
                for (var ch = 0; ch < c; ch++)
                {
                    var q2 = (int)dec.Bits(1);
                    var offset = (q2 - .5f) * (1 << (14 - fineQuant[i] - 1)) * (1f / 16384);
                    _oldBandE[i + ch * Ne] += offset;
                    bitsLeft--;
                }
            }
    }

    private static void TfDecode(int start, int end, bool isTransient, int[] tfRes, int lm, RangeDecoder dec)
    {
        var budget = (int)dec.Storage * 8;
        var tell = dec.Tell;
        var logp = isTransient ? 2 : 4;
        var tfSelectRsv = lm > 0 && tell + logp + 1 <= budget ? 1 : 0;
        budget -= tfSelectRsv;
        var tfChanged = 0; var curr = 0;
        for (var i = start; i < end; i++)
        {
            if (tell + logp <= budget)
            {
                curr ^= dec.BitLogp(logp);
                tell = dec.Tell;
                tfChanged |= curr;
            }
            tfRes[i] = curr;
            logp = isTransient ? 4 : 5;
        }
        var tfSelect = 0;
        var t = isTransient ? 4 : 0;
        if (tfSelectRsv != 0 && Celt.TfSelectTable[lm * 8 + t + tfChanged] != Celt.TfSelectTable[lm * 8 + t + 2 + tfChanged])
            tfSelect = dec.BitLogp(1);
        for (var i = start; i < end; i++) tfRes[i] = Celt.TfSelectTable[lm * 8 + t + 2 * tfSelect + tfRes[i]];
    }

    // ---- allocation (celt/rate.c, decoder subset) ----

    internal static int ComputeAllocation(int start, int end, int[] offsets, int[] cap, int allocTrim, ref int intensity, ref bool dualStereo,
        int total, out int balance, int[] pulses, int[] ebits, int[] finePriority, int c, int lm, RangeDecoder? dec, RangeEncoder? enc = null)
    {
        total = Math.Max(total, 0);
        var len = Ne;
        var skipStart = start;
        var skipRsv = total >= 1 << Celt.BitRes ? 1 << Celt.BitRes : 0;
        total -= skipRsv;
        int intensityRsv = 0, dualStereoRsv = 0;
        if (c == 2)
        {
            intensityRsv = Celt.Log2FracTable[end - start];
            if (intensityRsv > total) intensityRsv = 0;
            else
            {
                total -= intensityRsv;
                dualStereoRsv = total >= 1 << Celt.BitRes ? 1 << Celt.BitRes : 0;
                total -= dualStereoRsv;
            }
        }
        Span<int> bits1 = stackalloc int[len], bits2 = stackalloc int[len], thresh = stackalloc int[len], trimOffset = stackalloc int[len];
        for (var j = start; j < end; j++)
        {
            thresh[j] = Math.Max(c << Celt.BitRes, (3 * (Celt.EBands[j + 1] - Celt.EBands[j]) << lm << Celt.BitRes) >> 4);
            trimOffset[j] = c * (Celt.EBands[j + 1] - Celt.EBands[j]) * (allocTrim - 5 - lm) * (end - j - 1) * (1 << (lm + Celt.BitRes)) >> 6;
            if ((Celt.EBands[j + 1] - Celt.EBands[j]) << lm == 1) trimOffset[j] -= c << Celt.BitRes;
        }
        var lo = 1;
        var hi = 10;
        do
        {
            var done = false;
            var psum = 0;
            var mid = (lo + hi) >> 1;
            for (var j = end; j-- > start;)
            {
                var n = Celt.EBands[j + 1] - Celt.EBands[j];
                var bitsj = c * n * Celt.BandAllocation[mid * len + j] << lm >> 2;
                if (bitsj > 0) bitsj = Math.Max(0, bitsj + trimOffset[j]);
                bitsj += offsets[j];
                if (bitsj >= thresh[j] || done)
                {
                    done = true;
                    psum += Math.Min(bitsj, cap[j]);
                }
                else if (bitsj >= c << Celt.BitRes) psum += c << Celt.BitRes;
            }
            if (psum > total) hi = mid - 1; else lo = mid + 1;
        } while (lo <= hi);
        hi = lo--;
        for (var j = start; j < end; j++)
        {
            var n = Celt.EBands[j + 1] - Celt.EBands[j];
            var bits1j = c * n * Celt.BandAllocation[lo * len + j] << lm >> 2;
            var bits2j = hi >= 11 ? cap[j] : c * n * Celt.BandAllocation[hi * len + j] << lm >> 2;
            if (bits1j > 0) bits1j = Math.Max(0, bits1j + trimOffset[j]);
            if (bits2j > 0) bits2j = Math.Max(0, bits2j + trimOffset[j]);
            if (lo > 0) bits1j += offsets[j];
            bits2j += offsets[j];
            if (offsets[j] > 0) skipStart = j;
            bits2j = Math.Max(0, bits2j - bits1j);
            bits1[j] = bits1j;
            bits2[j] = bits2j;
        }
        return Interp(start, end, skipStart, bits1, bits2, thresh, cap, total, out balance, skipRsv, ref intensity, intensityRsv, ref dualStereo, dualStereoRsv, pulses, ebits, finePriority, c, lm, dec, enc);
    }

    private static int Interp(int start, int end, int skipStart, Span<int> bits1, Span<int> bits2, Span<int> thresh, int[] cap, int total, out int balanceOut,
        int skipRsv, ref int intensity, int intensityRsv, ref bool dualStereo, int dualStereoRsv, int[] bits, int[] ebits, int[] finePriority, int c, int lm, RangeDecoder? dec, RangeEncoder? enc)
    {
        var allocFloor = c << Celt.BitRes;
        var stereo = c > 1 ? 1 : 0;
        var logM = lm << Celt.BitRes;
        var lo = 0;
        var hi = 1 << 6;
        for (var i = 0; i < 6; i++)
        {
            var mid = (lo + hi) >> 1;
            var psum = 0;
            var done = false;
            for (var j = end; j-- > start;)
            {
                var tmp = bits1[j] + (mid * bits2[j] >> 6);
                if (tmp >= thresh[j] || done)
                {
                    done = true;
                    psum += Math.Min(tmp, cap[j]);
                }
                else if (tmp >= allocFloor) psum += allocFloor;
            }
            if (psum > total) hi = mid; else lo = mid;
        }
        var psum2 = 0;
        var done2 = false;
        for (var j = end; j-- > start;)
        {
            var tmp = bits1[j] + (lo * bits2[j] >> 6);
            if (tmp < thresh[j] && !done2)
            {
                tmp = tmp >= allocFloor ? allocFloor : 0;
            }
            else done2 = true;
            tmp = Math.Min(tmp, cap[j]);
            bits[j] = tmp;
            psum2 += tmp;
        }
        int codedBands;
        for (codedBands = end; ; codedBands--)
        {
            var j = codedBands - 1;
            if (j <= skipStart)
            {
                total += skipRsv;
                break;
            }
            var left = total - psum2;
            var percoeff = left / (Celt.EBands[codedBands] - Celt.EBands[start]);
            left -= (Celt.EBands[codedBands] - Celt.EBands[start]) * percoeff;
            var rem = Math.Max(left - (Celt.EBands[j] - Celt.EBands[start]), 0);
            var bandWidth = Celt.EBands[codedBands] - Celt.EBands[j];
            var bandBits = bits[j] + percoeff * bandWidth + rem;
            if (bandBits >= Math.Max(thresh[j], allocFloor + (1 << Celt.BitRes)))
            {
                if (enc is not null)
                {
                    // the encoder never drops a band it has bits for
                    enc.BitLogp(1, 1);
                    break;
                }
                if (dec!.BitLogp(1) != 0) break;
                psum2 += 1 << Celt.BitRes;
                bandBits -= 1 << Celt.BitRes;
            }
            psum2 -= bits[j] + intensityRsv;
            if (intensityRsv > 0) intensityRsv = Celt.Log2FracTable[j - start];
            psum2 += intensityRsv;
            if (bandBits >= allocFloor)
            {
                psum2 += allocFloor;
                bits[j] = allocFloor;
            }
            else bits[j] = 0;
        }
        if (intensityRsv > 0)
        {
            if (enc is not null) { intensity = Math.Min(intensity, codedBands); enc.Uint((uint)(intensity - start), (uint)(codedBands + 1 - start)); }
            else intensity = start + (int)dec!.Uint((uint)(codedBands + 1 - start));
        }
        else intensity = 0;
        if (intensity <= start)
        {
            total += dualStereoRsv;
            dualStereoRsv = 0;
        }
        if (dualStereoRsv > 0)
        {
            if (enc is not null) enc.BitLogp(dualStereo ? 1 : 0, 1);
            else dualStereo = dec!.BitLogp(1) != 0;
        }
        else dualStereo = false;
        var left2 = total - psum2;
        var pc = left2 / (Celt.EBands[codedBands] - Celt.EBands[start]);
        left2 -= (Celt.EBands[codedBands] - Celt.EBands[start]) * pc;
        for (var j = start; j < codedBands; j++) bits[j] += pc * (Celt.EBands[j + 1] - Celt.EBands[j]);
        for (var j = start; j < codedBands; j++)
        {
            var tmp = Math.Min(left2, Celt.EBands[j + 1] - Celt.EBands[j]);
            bits[j] += tmp;
            left2 -= tmp;
        }
        var balance = 0;
        int jj;
        for (jj = start; jj < codedBands; jj++)
        {
            var n0 = Celt.EBands[jj + 1] - Celt.EBands[jj];
            var n = n0 << lm;
            var bit = bits[jj] + balance;
            int excess;
            if (n > 1)
            {
                excess = Math.Max(bit - cap[jj], 0);
                bits[jj] = bit - excess;
                var den = c * n + (c == 2 && n > 2 && !dualStereo && jj < intensity ? 1 : 0);
                var nClogN = den * (Celt.LogN(jj) + logM);
                var offset = (nClogN >> 1) - den * Celt.FineOffset;
                if (n == 2) offset += den << Celt.BitRes >> 2;
                if (bits[jj] + offset < den * 2 << Celt.BitRes) offset += nClogN >> 2;
                else if (bits[jj] + offset < den * 3 << Celt.BitRes) offset += nClogN >> 3;
                ebits[jj] = Math.Max(0, bits[jj] + offset + (den << (Celt.BitRes - 1)));
                ebits[jj] = ebits[jj] / den >> Celt.BitRes;
                if (c * ebits[jj] > bits[jj] >> Celt.BitRes) ebits[jj] = bits[jj] >> stereo >> Celt.BitRes;
                ebits[jj] = Math.Min(ebits[jj], Celt.MaxFineBits);
                finePriority[jj] = ebits[jj] * (den << Celt.BitRes) >= bits[jj] + offset ? 1 : 0;
                bits[jj] -= c * ebits[jj] << Celt.BitRes;
            }
            else
            {
                excess = Math.Max(0, bit - (c << Celt.BitRes));
                bits[jj] = bit - excess;
                ebits[jj] = 0;
                finePriority[jj] = 1;
            }
            if (excess > 0)
            {
                var extraFine = Math.Min(excess >> (stereo + Celt.BitRes), Celt.MaxFineBits - ebits[jj]);
                ebits[jj] += extraFine;
                var extraBits = extraFine * c << Celt.BitRes;
                finePriority[jj] = extraBits >= excess - balance ? 1 : 0;
                excess -= extraBits;
            }
            balance = excess;
        }
        balanceOut = balance;
        for (; jj < end; jj++)
        {
            ebits[jj] = bits[jj] >> stereo >> Celt.BitRes;
            bits[jj] = 0;
            finePriority[jj] = ebits[jj] < 1 ? 1 : 0;
        }
        return codedBands;
    }

    // ---- decoding ----

    /// <summary>
    /// Decodes one CELT frame. <paramref name="dec"/> is the range decoder positioned at the start of the CELT payload (it already
    /// consumed any SILK data in hybrid mode); <paramref name="len"/> is the byte budget of the frame. A null decoder or a length of at most one byte
    /// conceals a lost frame. Returns the number of samples per channel, or a negative value on error.
    /// </summary>
    internal int Decode(RangeDecoder? dec, int len, float[] pcm, int pcmOff, int frameSize)
    {
        var cc = _cc;
        var c = _streamChannels;
        int lm;
        for (lm = 0; lm <= Celt.MaxLm; lm++) if (Celt.ShortMdctSize << lm == frameSize) break;
        if (lm > Celt.MaxLm) return -1;
        var m = 1 << lm;
        if (len < 0 || len > 1275) return -1;
        var n = m * Celt.ShortMdctSize;
        if (pcm.Length - pcmOff < n * cc) return -1;
        var effEnd = Math.Min(End, Ne);
        if (dec is null || len <= 1)
        {
            DecodeLost(n, lm);
            Deemphasis(pcm, pcmOff, n, cc);
            return frameSize;
        }
        if (_lossDuration == 0) _skipPlc = false;
        var start = Start;
        var end = End;
        if (c == 1) for (var i = 0; i < Ne; i++) _oldBandE[i] = MathF.Max(_oldBandE[i], _oldBandE[Ne + i]);
        var totalBits = len * 8;
        int tell = dec.Tell;
        bool silence;
        if (tell >= totalBits) silence = true;
        else if (tell == 1) silence = dec.BitLogp(15) != 0;
        else silence = false;
        if (silence)
        {
            tell = len * 8;
            dec.NbitsTotal += tell - dec.Tell;
        }
        var postfilterGain = 0f;
        var postfilterPitch = 0;
        var postfilterTapset = 0;
        if (start == 0 && tell + 16 <= totalBits)
        {
            if (dec.BitLogp(1) != 0)
            {
                var octave = (int)dec.Uint(6);
                postfilterPitch = (16 << octave) + (int)dec.Bits(4 + octave) - 1;
                var qg = (int)dec.Bits(3);
                if (dec.Tell + 2 <= totalBits) postfilterTapset = dec.Icdf(Celt.TapsetIcdf, 2);
                postfilterGain = .09375f * (qg + 1);
            }
            tell = dec.Tell;
        }
        bool isTransient;
        if (lm > 0 && tell + 3 <= totalBits)
        {
            isTransient = dec.BitLogp(3) != 0;
            tell = dec.Tell;
        }
        else isTransient = false;
        var intraEner = tell + 3 <= totalBits && dec.BitLogp(3) != 0;
        if (!intraEner && _lossDuration != 0)
        {
            for (var ch = 0; ch < 2; ch++)
            {
                var safety = 0f;
                var missing = Math.Min(10, _lossDuration >> lm);
                if (lm == 0) safety = 1.5f; else if (lm == 1) safety = .5f;
                for (var i = start; i < end; i++)
                {
                    var o = ch * Ne + i;
                    if (_oldBandE[o] < MathF.Max(_oldLogE[o], _oldLogE2[o]))
                    {
                        float e0 = _oldBandE[o], e1 = _oldLogE[o], e2 = _oldLogE2[o];
                        var slope = MathF.Max(e1 - e0, .5f * (e2 - e0));
                        e0 -= MathF.Max(0, (1 + missing) * slope);
                        _oldBandE[o] = MathF.Max(-20f, e0);
                    }
                    else _oldBandE[o] = MathF.Min(MathF.Min(_oldBandE[o], _oldLogE[o]), _oldLogE2[o]);
                    _oldBandE[o] -= safety;
                }
            }
        }
        UnquantCoarseEnergy(start, end, intraEner, dec, c, lm);
        var tfRes = new int[Ne];
        TfDecode(start, end, isTransient, tfRes, lm, dec);
        tell = dec.Tell;
        var spreadDecision = Celt.SpreadNormal;
        if (tell + 4 <= totalBits) spreadDecision = dec.Icdf(Celt.SpreadIcdf, 5);
        var cap = new int[Ne];
        Celt.InitCaps(cap, lm, c);
        var offsets = new int[Ne];
        var dynallocLogp = 6;
        totalBits <<= Celt.BitRes;
        var tellFrac = (int)dec.TellFrac;
        for (var i = start; i < end; i++)
        {
            var width = c * (Celt.EBands[i + 1] - Celt.EBands[i]) << lm;
            var quanta = Math.Min(width << Celt.BitRes, Math.Max(6 << Celt.BitRes, width));
            var loopLogp = dynallocLogp;
            var boost = 0;
            while (tellFrac + (loopLogp << Celt.BitRes) < totalBits && boost < cap[i])
            {
                var flag = dec.BitLogp(loopLogp);
                tellFrac = (int)dec.TellFrac;
                if (flag == 0) break;
                boost += quanta;
                totalBits -= quanta;
                loopLogp = 1;
            }
            offsets[i] = boost;
            if (boost > 0) dynallocLogp = Math.Max(2, dynallocLogp - 1);
        }
        var fineQuant = new int[Ne];
        var allocTrim = tellFrac + (6 << Celt.BitRes) <= totalBits ? dec.Icdf(Celt.TrimIcdf, 7) : 5;
        var bits = ((len * 8) << Celt.BitRes) - (int)dec.TellFrac - 1;
        var antiCollapseRsv = isTransient && lm >= 2 && bits >= (lm + 2) << Celt.BitRes ? 1 << Celt.BitRes : 0;
        bits -= antiCollapseRsv;
        var pulses = new int[Ne];
        var finePriority = new int[Ne];
        var intensity = 0;
        var dualStereo = false;
        var codedBands = ComputeAllocation(start, end, offsets, cap, allocTrim, ref intensity, ref dualStereo, bits, out var balance, pulses, fineQuant, finePriority, c, lm, dec);
        UnquantFineEnergy(start, end, fineQuant, dec, c);
        for (var ch = 0; ch < cc; ch++) Array.Copy(_mem[ch], n, _mem[ch], 0, Dbs - n + Ov);
        var x = _x;
        Array.Clear(x, 0, c * n);
        var collapse = _collapse;
        Array.Clear(collapse);
        CeltBands.QuantAllBands(false, start, end, x.AsSpan(0, n), c == 2 ? x.AsSpan(n, n) : default, collapse, null, pulses, isTransient, spreadDecision, dualStereo,
            intensity, tfRes, len * (8 << Celt.BitRes) - antiCollapseRsv, balance, dec, null, lm, codedBands, ref _rng, _disableInv);
        var antiCollapseOn = false;
        if (antiCollapseRsv > 0) antiCollapseOn = dec.Bits(1) != 0;
        UnquantEnergyFinalise(start, end, fineQuant, finePriority, len * 8 - dec.Tell, dec, c);
        if (antiCollapseOn) CeltBands.AntiCollapse(x, collapse, lm, c, n, start, end, _oldBandE, _oldLogE, _oldLogE2, pulses, _rng);
        if (silence) for (var i = 0; i < c * Ne; i++) _oldBandE[i] = -28f;
        if (_prefilterAndFold) PrefilterAndFold(n);
        Synthesis(x, c, cc, isTransient, lm, silence, start, effEnd);
        for (var ch = 0; ch < cc; ch++)
        {
            _postfilterPeriod = Math.Max(_postfilterPeriod, Celt.CombFilterMinPeriod);
            _postfilterPeriodOld = Math.Max(_postfilterPeriodOld, Celt.CombFilterMinPeriod);
            var off = Dbs - n;
            CombFilter(_mem[ch], off, _mem[ch], off, _postfilterPeriodOld, _postfilterPeriod, Celt.ShortMdctSize, _postfilterGainOld, _postfilterGain, _postfilterTapsetOld, _postfilterTapset, Celt.Window, Ov);
            if (lm != 0)
                CombFilter(_mem[ch], off + Celt.ShortMdctSize, _mem[ch], off + Celt.ShortMdctSize, _postfilterPeriod, postfilterPitch, n - Celt.ShortMdctSize, _postfilterGain, postfilterGain, _postfilterTapset, postfilterTapset, Celt.Window, Ov);
        }
        _postfilterPeriodOld = _postfilterPeriod;
        _postfilterGainOld = _postfilterGain;
        _postfilterTapsetOld = _postfilterTapset;
        _postfilterPeriod = postfilterPitch;
        _postfilterGain = postfilterGain;
        _postfilterTapset = postfilterTapset;
        if (lm != 0)
        {
            _postfilterPeriodOld = _postfilterPeriod;
            _postfilterGainOld = _postfilterGain;
            _postfilterTapsetOld = _postfilterTapset;
        }
        if (c == 1) Array.Copy(_oldBandE, 0, _oldBandE, Ne, Ne);
        if (!isTransient)
        {
            Array.Copy(_oldLogE, _oldLogE2, 2 * Ne);
            Array.Copy(_oldBandE, _oldLogE, 2 * Ne);
        }
        else for (var i = 0; i < 2 * Ne; i++) _oldLogE[i] = MathF.Min(_oldLogE[i], _oldBandE[i]);
        var maxBackgroundIncrease = Math.Min(160, _lossDuration + m) * 0.001f;
        for (var i = 0; i < 2 * Ne; i++) _backgroundLogE[i] = MathF.Min(_backgroundLogE[i] + maxBackgroundIncrease, _oldBandE[i]);
        for (var ch = 0; ch < 2; ch++)
        {
            for (var i = 0; i < start; i++) { _oldBandE[ch * Ne + i] = 0; _oldLogE[ch * Ne + i] = _oldLogE2[ch * Ne + i] = -28f; }
            for (var i = end; i < Ne; i++) { _oldBandE[ch * Ne + i] = 0; _oldLogE[ch * Ne + i] = _oldLogE2[ch * Ne + i] = -28f; }
        }
        _rng = dec.Rng;
        Deemphasis(pcm, pcmOff, n, cc);
        _lossDuration = 0;
        _prefilterAndFold = false;
        if (dec.Tell > 8 * len) return -3;
        return frameSize;
    }

    // ---- concealment ----

    private void DecodeLost(int n, int lm)
    {
        var c = _cc;
        var start = Start;
        var lossDuration = _lossDuration;
        var noiseBased = lossDuration >= 40 || start != 0 || _skipPlc;
        if (noiseBased)
        {
            var end = End;
            var effEnd = Math.Max(start, Math.Min(end, Ne));
            for (var ch = 0; ch < c; ch++) Array.Copy(_mem[ch], n, _mem[ch], 0, Dbs - n + Ov);
            if (_prefilterAndFold) PrefilterAndFold(n);
            var decay = lossDuration == 0 ? 1.5f : .5f;
            for (var ch = 0; ch < c; ch++)
                for (var i = start; i < end; i++)
                    _oldBandE[ch * Ne + i] = MathF.Max(_backgroundLogE[ch * Ne + i], _oldBandE[ch * Ne + i] - decay);
            var seed = _rng;
            var x = _x;
            for (var ch = 0; ch < c; ch++)
                for (var i = start; i < effEnd; i++)
                {
                    var boffs = n * ch + (Celt.EBands[i] << lm);
                    var blen = (Celt.EBands[i + 1] - Celt.EBands[i]) << lm;
                    for (var j = 0; j < blen; j++)
                    {
                        seed = Celt.LcgRand(seed);
                        x[boffs + j] = (int)seed >> 20;
                    }
                    CeltPvq.RenormaliseVector(x.AsSpan(boffs, blen), blen, 1f);
                }
            _rng = seed;
            Synthesis(x, c, c, false, lm, false, start, effEnd);
            _prefilterAndFold = false;
            _skipPlc = true;
        }
        else
        {
            var fade = 1f;
            int pitchIndex;
            if (lossDuration == 0) _lastPitchIndex = pitchIndex = PlcPitchSearch();
            else { pitchIndex = _lastPitchIndex; fade = .8f; }
            var excLength = Math.Min(2 * pitchIndex, Celt.MaxPeriod);
            var exc = new float[Celt.MaxPeriod + Celt.LpcOrder];
            var firTmp = new float[excLength];
            var window = Celt.Window;
            Span<float> ac = stackalloc float[Celt.LpcOrder + 1];
            Span<float> lpcMem = stackalloc float[Celt.LpcOrder];
            for (var ch = 0; ch < c; ch++)
            {
                var buf = _mem[ch];
                float s1 = 0;
                for (var i = 0; i < Celt.MaxPeriod + Celt.LpcOrder; i++) exc[i] = buf[Dbs - Celt.MaxPeriod - Celt.LpcOrder + i];
                var lpcOff = ch * Celt.LpcOrder;
                if (lossDuration == 0)
                {
                    CeltLpc.Autocorr(exc.AsSpan(Celt.LpcOrder), ac, window, Ov, Celt.LpcOrder, Celt.MaxPeriod);
                    ac[0] *= 1.0001f;
                    for (var i = 1; i <= Celt.LpcOrder; i++) ac[i] -= ac[i] * (0.008f * 0.008f) * i * i;
                    CeltLpc.Lpc(_lpc.AsSpan(lpcOff, Celt.LpcOrder), ac, Celt.LpcOrder);
                }
                CeltLpc.Fir(exc, Celt.LpcOrder + Celt.MaxPeriod - excLength, _lpc, lpcOff, firTmp, excLength, Celt.LpcOrder);
                Array.Copy(firTmp, 0, exc, Celt.LpcOrder + Celt.MaxPeriod - excLength, excLength);
                float e1 = 1, e2 = 1;
                var decayLength = excLength >> 1;
                var mp = Celt.LpcOrder + Celt.MaxPeriod;
                for (var i = 0; i < decayLength; i++)
                {
                    var e = exc[mp - decayLength + i];
                    e1 += e * e;
                    e = exc[mp - 2 * decayLength + i];
                    e2 += e * e;
                }
                e1 = MathF.Min(e1, e2);
                var decay = MathF.Sqrt(e1 / e2);
                Array.Copy(buf, n, buf, 0, Dbs - n);
                var extrapolationOffset = Celt.MaxPeriod - pitchIndex;
                var extrapolationLen = n + Ov;
                var attenuation = fade * decay;
                int j;
                var ii = 0;
                for (ii = j = 0; ii < extrapolationLen; ii++, j++)
                {
                    if (j >= pitchIndex)
                    {
                        j -= pitchIndex;
                        attenuation *= decay;
                    }
                    buf[Dbs - n + ii] = attenuation * exc[Celt.LpcOrder + extrapolationOffset + j];
                    var tmp = buf[Dbs - Celt.MaxPeriod - n + extrapolationOffset + j];
                    s1 += tmp * tmp;
                }
                for (var i = 0; i < Celt.LpcOrder; i++) lpcMem[i] = buf[Dbs - n - 1 - i];
                CeltLpc.Iir(buf, Dbs - n, _lpc, lpcOff, extrapolationLen, Celt.LpcOrder, lpcMem);
                float s2 = 0;
                for (var i = 0; i < extrapolationLen; i++)
                {
                    var tmp = buf[Dbs - n + i];
                    s2 += tmp * tmp;
                }
                if (!(s1 > 0.2f * s2))
                {
                    for (var i = 0; i < extrapolationLen; i++) buf[Dbs - n + i] = 0;
                }
                else if (s1 < s2)
                {
                    var ratio = MathF.Sqrt((s1 + 1) / (s2 + 1));
                    for (var i = 0; i < Ov; i++)
                    {
                        var tmpG = 1f - window[i] * (1f - ratio);
                        buf[Dbs - n + i] *= tmpG;
                    }
                    for (var i = Ov; i < extrapolationLen; i++) buf[Dbs - n + i] *= ratio;
                }
            }
            _prefilterAndFold = true;
        }
        _lossDuration = Math.Min(10000, lossDuration + (1 << lm));
    }

    /// <summary>Plain normalised cross-correlation pitch search over the decoded history (lags 100..720 samples at 48 kHz).</summary>
    private int PlcPitchSearch()
    {
        const int lagMin = 100, lagMax = 720, decim = 4;
        var len = Dbs / decim;
        var lp = new float[len];
        for (var i = 0; i < len; i++)
        {
            float s = 0;
            for (var j = 0; j < decim; j++)
                for (var ch = 0; ch < _cc; ch++) s += _mem[ch][i * decim + j];
            lp[i] = s / (decim * _cc);
        }
        var win = 1024 / decim;
        var best = lagMin / decim;
        var bestScore = float.MinValue;
        for (var lag = lagMin / decim; lag <= lagMax / decim; lag++)
        {
            float xy = 0, yy = 1e-3f;
            for (var i = 0; i < win; i++)
            {
                var a = lp[len - 1 - i];
                var b = lp[len - 1 - i - lag];
                xy += a * b;
                yy += b * b;
            }
            var score = xy > 0 ? xy * xy / yy : 0;
            if (score > bestScore) { bestScore = score; best = lag; }
        }
        // refine at full rate
        var center = best * decim;
        var bestLag = center;
        bestScore = float.MinValue;
        for (var lag = Math.Max(lagMin, center - decim); lag <= Math.Min(lagMax, center + decim); lag++)
        {
            float xy = 0, yy = 1e-3f;
            for (var i = 0; i < 1024; i += 2)
            {
                var a = _mem[0][Dbs - 1 - i];
                var b = _mem[0][Dbs - 1 - i - lag];
                xy += a * b;
                yy += b * b;
            }
            var score = xy > 0 ? xy * xy / yy : 0;
            if (score > bestScore) { bestScore = score; bestLag = lag; }
        }
        return Math.Clamp(bestLag, lagMin, lagMax);
    }
}

internal static class CeltLpc
{
    /// <summary>Autocorrelation of a windowed block (celt_autocorr): the first and last <paramref name="overlap"/> samples are windowed.</summary>
    internal static void Autocorr(ReadOnlySpan<float> x, Span<float> ac, float[] window, int overlap, int lag, int n)
    {
        var fastN = n - lag;
        var xx = new float[n];
        x[..n].CopyTo(xx);
        for (var i = 0; i < overlap; i++)
        {
            xx[i] = x[i] * window[i];
            xx[n - i - 1] = x[n - i - 1] * window[i];
        }
        for (var k = 0; k <= lag; k++)
        {
            float sum = 0;
            for (var i = 0; i < fastN; i++) sum += xx[i] * xx[i + k];
            for (var i = k + fastN; i < n; i++) sum += xx[i] * xx[i - k];
            ac[k] = sum;
        }
    }

    internal static void Lpc(Span<float> lpc, ReadOnlySpan<float> ac, int p)
    {
        lpc[..p].Clear();
        var error = ac[0];
        if (ac[0] > 1e-10f)
        {
            for (var i = 0; i < p; i++)
            {
                float rr = 0;
                for (var j = 0; j < i; j++) rr += lpc[j] * ac[i - j];
                rr += ac[i + 1];
                var r = -rr / error;
                lpc[i] = r;
                for (var j = 0; j < (i + 1) >> 1; j++)
                {
                    var tmp1 = lpc[j];
                    var tmp2 = lpc[i - 1 - j];
                    lpc[j] = tmp1 + r * tmp2;
                    lpc[i - 1 - j] = tmp2 + r * tmp1;
                }
                error -= r * r * error;
                if (error <= .001f * ac[0]) break;
            }
        }
    }

    /// <summary>y[i] = x[i] + sum_k num[k] x[i-1-k], reading <paramref name="ord"/> samples of history before <paramref name="xOff"/>.</summary>
    internal static void Fir(float[] x, int xOff, float[] num, int numOff, float[] y, int n, int ord)
    {
        for (var i = 0; i < n; i++)
        {
            var sum = x[xOff + i];
            for (var j = 0; j < ord; j++) sum += num[numOff + j] * x[xOff + i - 1 - j];
            y[i] = sum;
        }
    }

    /// <summary>In-place all-pole filter y[i] = x[i] - sum_k den[k] y[i-1-k] with the given history (mem[k] = y[-1-k]).</summary>
    internal static void Iir(float[] buf, int off, float[] den, int denOff, int n, int ord, Span<float> mem)
    {
        for (var i = 0; i < n; i++)
        {
            var sum = buf[off + i];
            for (var j = 0; j < ord; j++) sum -= den[denOff + j] * mem[j];
            for (var j = ord - 1; j >= 1; j--) mem[j] = mem[j - 1];
            mem[0] = sum;
            buf[off + i] = sum;
        }
    }
}
