// Fixed-point primitives of the SILK layer (RFC 6716 section 4.2), translated from the Opus reference implementation
// (Copyright (c) 2006-2011 Skype Limited, BSD 3-clause; see docs/webrtc-opus.md). Arithmetic wraps like the C code does.
using System.Numerics;

namespace Folio.Scripting.Rtc.Opus;

internal static class SilkMath
{
    internal static int Smulwb(int a, int b) => (int)(((long)a * (short)b) >> 16);
    internal static int Smlawb(int acc, int a, int b) => unchecked(acc + Smulwb(a, b));
    internal static int Smulwt(int a, int b) => (int)(((long)a * (b >> 16)) >> 16);
    internal static int Smulww(int a, int b) => unchecked(Smulwb(a, b) + a * RshiftRound(b, 16));
    internal static int Smlaww(int acc, int a, int b) => unchecked(acc + Smulww(a, b));
    internal static int Smulbb(int a, int b) => (short)a * (short)b;
    internal static int Smlabb(int acc, int a, int b) => unchecked(acc + (short)a * (short)b);
    internal static int Smultt(int a, int b) => (a >> 16) * (b >> 16);
    internal static int Smmul(int a, int b) => (int)(((long)a * b) >> 32);
    internal static long Smull(int a, int b) => (long)a * b;

    internal static int RshiftRound(int a, int shift) => shift == 1 ? (a >> 1) + (a & 1) : ((a >> (shift - 1)) + 1) >> 1;
    internal static long RshiftRound64(long a, int shift) => shift == 1 ? (a >> 1) + (a & 1) : ((a >> (shift - 1)) + 1) >> 1;

    internal static short Sat16(int a) => (short)(a > short.MaxValue ? short.MaxValue : a < short.MinValue ? short.MinValue : a);
    internal static int AddSat32(int a, int b) => (int)Math.Clamp((long)a + b, int.MinValue, int.MaxValue);
    internal static int SubSat32(int a, int b) => (int)Math.Clamp((long)a - b, int.MinValue, int.MaxValue);
    internal static short AddSat16(int a, int b) => Sat16(a + b);
    internal static int LshiftSat32(int a, int shift) => unchecked(Math.Clamp(a, int.MinValue >> shift, int.MaxValue >> shift) << shift);
    internal static int Limit(int a, int lo, int hi) => lo > hi ? (a > lo ? lo : a < hi ? hi : a) : (a > hi ? hi : a < lo ? lo : a);
    internal static int Clz32(int a) => BitOperations.LeadingZeroCount((uint)a);
    internal static int Rand(int seed) => unchecked(907633515 + seed * 196314165);
    internal static int Abs(int a) => a > 0 ? a : unchecked(-a);

    internal static void ClzFrac(int input, out int lz, out int fracQ7)
    {
        lz = Clz32(input);
        fracQ7 = (int)BitOperations.RotateRight((uint)input, 24 - lz) & 0x7F;
    }

    internal static int SqrtApprox(int x)
    {
        if (x <= 0) return 0;
        ClzFrac(x, out var lz, out var frac);
        var y = (lz & 1) != 0 ? 32768 : 46214;
        y >>= lz >> 1;
        return Smlawb(y, y, Smulbb(213, frac));
    }

    internal static int Div32Varq(int a32, int b32, int qres)
    {
        if (b32 == 0) return 0; // hostile input cannot reach this; the reference asserts
        var aHeadrm = Clz32(Abs(a32)) - 1;
        var a32Nrm = a32 << aHeadrm;
        var bHeadrm = Clz32(Abs(b32)) - 1;
        var b32Nrm = b32 << bHeadrm;
        var b32Inv = (int.MaxValue >> 2) / (b32Nrm >> 16);
        var result = Smulwb(a32Nrm, b32Inv);
        a32Nrm = unchecked(a32Nrm - (Smmul(b32Nrm, result) << 3));
        result = Smlawb(result, a32Nrm, b32Inv);
        var lshift = 29 + aHeadrm - bHeadrm - qres;
        if (lshift < 0) return LshiftSat32(result, -lshift);
        return lshift < 32 ? result >> lshift : 0;
    }

    internal static int Inverse32Varq(int b32, int qres)
    {
        if (b32 == 0) return 0;
        var bHeadrm = Clz32(Abs(b32)) - 1;
        var b32Nrm = b32 << bHeadrm;
        var b32Inv = (int.MaxValue >> 2) / (b32Nrm >> 16);
        var result = b32Inv << 16;
        var errQ32 = unchecked(((1 << 29) - Smulwb(b32Nrm, b32Inv)) << 3);
        result = Smlaww(result, errQ32, b32Inv);
        var lshift = 61 - bHeadrm - qres;
        if (lshift <= 0) return LshiftSat32(result, -lshift);
        return lshift < 32 ? result >> lshift : 0;
    }

    internal static int Lin2Log(int inLin)
    {
        ClzFrac(inLin, out var lz, out var fracQ7);
        return unchecked(Smlawb(fracQ7, fracQ7 * (128 - fracQ7), 179) + ((31 - lz) << 7));
    }

    internal static int Log2Lin(int inLogQ7)
    {
        if (inLogQ7 < 0) return 0;
        if (inLogQ7 >= 3967) return int.MaxValue;
        var output = 1 << (inLogQ7 >> 7);
        var fracQ7 = inLogQ7 & 0x7F;
        if (inLogQ7 < 2048)
            output = unchecked(output + ((output * Smlawb(fracQ7, Smulbb(fracQ7, 128 - fracQ7), -174)) >> 7));
        else
            output = unchecked(output + (output >> 7) * Smlawb(fracQ7, Smulbb(fracQ7, 128 - fracQ7), -174));
        return output;
    }

    internal static void SumSqrShift(out int energy, out int shift, ReadOnlySpan<short> x, int len)
    {
        var shft = 31 - Clz32(len);
        var nrg = len;
        int i;
        for (i = 0; i < len - 1; i += 2)
        {
            var tmp = unchecked((uint)(Smulbb(x[i], x[i]) + Smulbb(x[i + 1], x[i + 1])));
            nrg = unchecked(nrg + (int)(tmp >> shft));
        }
        if (i < len) nrg = unchecked(nrg + (int)((uint)Smulbb(x[i], x[i]) >> shft));
        shft = Math.Max(0, shft + 3 - Clz32(nrg));
        nrg = 0;
        for (i = 0; i < len - 1; i += 2)
        {
            var tmp = unchecked((uint)(Smulbb(x[i], x[i]) + Smulbb(x[i + 1], x[i + 1])));
            nrg = unchecked(nrg + (int)(tmp >> shft));
        }
        if (i < len) nrg = unchecked(nrg + (int)((uint)Smulbb(x[i], x[i]) >> shft));
        shift = shft;
        energy = nrg;
    }

    internal static void Bwexpander(Span<short> ar, int d, int chirpQ16)
    {
        var chirpMinusOne = chirpQ16 - 65536;
        for (var i = 0; i < d - 1; i++)
        {
            ar[i] = (short)RshiftRound(unchecked(chirpQ16 * ar[i]), 16);
            chirpQ16 += RshiftRound(unchecked(chirpQ16 * chirpMinusOne), 16);
        }
        ar[d - 1] = (short)RshiftRound(unchecked(chirpQ16 * ar[d - 1]), 16);
    }

    internal static void Bwexpander32(Span<int> ar, int d, int chirpQ16)
    {
        var chirpMinusOne = chirpQ16 - 65536;
        for (var i = 0; i < d - 1; i++)
        {
            ar[i] = Smulww(chirpQ16, ar[i]);
            chirpQ16 += RshiftRound(unchecked(chirpQ16 * chirpMinusOne), 16);
        }
        ar[d - 1] = Smulww(chirpQ16, ar[d - 1]);
    }

    internal static void LpcFit(Span<short> aQout, Span<int> aQin, int qout, int qin, int d)
    {
        int i, idx = 0;
        for (i = 0; i < 10; i++)
        {
            var maxabs = 0;
            for (var k = 0; k < d; k++)
            {
                var absval = Abs(aQin[k]);
                if (absval > maxabs) { maxabs = absval; idx = k; }
            }
            maxabs = RshiftRound(maxabs, qin - qout);
            if (maxabs > short.MaxValue)
            {
                maxabs = Math.Min(maxabs, 163838);
                var chirp = 65470 /* 0.999 in Q16 */ - ((maxabs - short.MaxValue) << 14) / (unchecked(maxabs * (idx + 1)) >> 2);
                Bwexpander32(aQin, d, chirp);
            }
            else break;
        }
        if (i == 10)
        {
            for (var k = 0; k < d; k++)
            {
                aQout[k] = Sat16(RshiftRound(aQin[k], qin - qout));
                aQin[k] = aQout[k] << (qin - qout);
            }
        }
        else for (var k = 0; k < d; k++) aQout[k] = (short)RshiftRound(aQin[k], qin - qout);
    }

    /// <summary>Inverse prediction gain in Q30, or zero when the filter is unstable (silk_LPC_inverse_pred_gain).</summary>
    internal static int LpcInversePredGain(ReadOnlySpan<short> aQ12, int order)
    {
        Span<int> atmp = stackalloc int[16];
        var dc = 0;
        for (var k = 0; k < order; k++) { dc += aQ12[k]; atmp[k] = aQ12[k] << (24 - 12); }
        if (dc >= 4096) return 0;
        const int qa = 24;
        const int aLimit = 16773022; // 0.99975 in Q24
        var invGainQ30 = 1 << 30;
        int k2;
        for (k2 = order - 1; k2 > 0; k2--)
        {
            if (atmp[k2] > aLimit || atmp[k2] < -aLimit) return 0;
            var rcQ31 = -(atmp[k2] << (31 - qa));
            var rcMult1Q30 = (1 << 30) - Smmul(rcQ31, rcQ31);
            invGainQ30 = Smmul(invGainQ30, rcMult1Q30) << 2;
            if (invGainQ30 < 107374) return 0; // 1 / MAX_PREDICTION_POWER_GAIN (1e4) in Q30
            var mult2Q = 32 - Clz32(Abs(rcMult1Q30));
            var rcMult2 = Inverse32Varq(rcMult1Q30, mult2Q + 30);
            for (var n = 0; n < (k2 + 1) >> 1; n++)
            {
                var tmp1 = atmp[n];
                var tmp2 = atmp[k2 - n - 1];
                var tmp64 = RshiftRound64(Smull(SubSat32(tmp1, (int)RshiftRound64(Smull(tmp2, rcQ31), 31)), rcMult2), mult2Q);
                if (tmp64 > int.MaxValue || tmp64 < int.MinValue) return 0;
                atmp[n] = (int)tmp64;
                tmp64 = RshiftRound64(Smull(SubSat32(tmp2, (int)RshiftRound64(Smull(tmp1, rcQ31), 31)), rcMult2), mult2Q);
                if (tmp64 > int.MaxValue || tmp64 < int.MinValue) return 0;
                atmp[k2 - n - 1] = (int)tmp64;
            }
        }
        if (atmp[0] > aLimit || atmp[0] < -aLimit) return 0;
        var rc = -(atmp[0] << (31 - qa));
        var rm1 = (1 << 30) - Smmul(rc, rc);
        invGainQ30 = Smmul(invGainQ30, rm1) << 2;
        return invGainQ30 < 107374 ? 0 : invGainQ30;
    }

    /// <summary>silk_LPC_analysis_filter: out[0..d) are zeroed; d is even and at least 6.</summary>
    internal static void LpcAnalysisFilter(Span<short> output, ReadOnlySpan<short> input, ReadOnlySpan<short> b, int len, int d)
    {
        for (var ix = d; ix < len; ix++)
        {
            var p = ix - 1;
            var acc = Smulbb(input[p], b[0]);
            for (var j = 1; j < d; j++) acc = Smlabb(acc, input[p - j], b[j]);
            acc = unchecked((input[p + 1] << 12) - acc);
            output[ix] = Sat16(RshiftRound(acc, 12));
        }
        output[..d].Clear();
    }
}
