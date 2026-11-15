// Shared constants, tables and helpers of the CELT layer (RFC 6716 section 4.3), after the Opus reference implementation
// (Copyright (c) 2007-2009 Xiph.Org Foundation, CSIRO; Written by Jean-Marc Valin and Timothy B. Terriberry;
// BSD 3-clause, see docs/webrtc-opus.md). Only the 48 kHz, 960 sample frame mode is implemented; other output rates are
// produced by the caller. Arithmetic is the reference "float" build.
namespace Folio.Scripting.Rtc.Opus;

internal static class Celt
{
    internal const int NbEBands = 21;
    internal const int ShortMdctSize = 120;
    internal const int Overlap = 120;
    internal const int MaxLm = 3;
    internal const int MaxPeriod = 1024;
    internal const int CombFilterMinPeriod = 15;
    internal const int LpcOrder = 24;
    internal const int DecodeBufferSize = 2048;
    internal const int BitRes = 3;
    internal const int MaxFineBits = 8;
    internal const int FineOffset = 21;
    internal const int QthetaOffset = 4;
    internal const int QthetaOffsetTwophase = 16;
    internal const int SpreadNone = 0, SpreadLight = 1, SpreadNormal = 2, SpreadAggressive = 3;
    internal const float Preemphasis = 0.85000610f;
    internal const float Epsilon = 1e-15f;
    internal const float VerySmall = 1e-30f;

    internal static readonly short[] EBands = [0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 20, 24, 28, 34, 40, 48, 60, 78, 100];

    internal static readonly byte[] BandAllocation =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        90, 80, 75, 69, 63, 56, 49, 40, 34, 29, 20, 18, 10, 0, 0, 0, 0, 0, 0, 0, 0,
        110, 100, 90, 84, 78, 71, 65, 58, 51, 45, 39, 32, 26, 20, 12, 0, 0, 0, 0, 0, 0,
        118, 110, 103, 93, 86, 80, 75, 70, 65, 59, 53, 47, 40, 31, 23, 15, 4, 0, 0, 0, 0,
        126, 119, 112, 104, 95, 89, 83, 78, 72, 66, 60, 54, 47, 39, 32, 25, 17, 12, 1, 0, 0,
        134, 127, 120, 114, 103, 97, 91, 85, 78, 72, 66, 60, 54, 47, 41, 35, 29, 23, 16, 10, 1,
        144, 137, 130, 124, 113, 107, 101, 95, 88, 82, 76, 70, 64, 57, 51, 45, 39, 33, 26, 15, 1,
        152, 145, 138, 132, 123, 117, 111, 105, 98, 92, 86, 80, 74, 67, 61, 55, 49, 43, 36, 20, 1,
        162, 155, 148, 142, 133, 127, 121, 115, 108, 102, 96, 90, 84, 77, 71, 65, 59, 53, 46, 30, 1,
        172, 165, 158, 152, 143, 137, 131, 125, 118, 112, 106, 100, 94, 87, 81, 75, 69, 63, 56, 45, 20,
        200, 200, 200, 200, 200, 200, 200, 200, 198, 193, 188, 183, 178, 173, 168, 163, 158, 153, 148, 129, 104,
    ];

    internal static readonly float[] EMeans =
    [
        6.437500f, 6.250000f, 5.750000f, 5.312500f, 5.062500f, 4.812500f, 4.500000f, 4.375000f, 4.875000f, 4.687500f,
        4.562500f, 4.437500f, 4.875000f, 4.625000f, 4.312500f, 4.500000f, 4.375000f, 4.625000f, 4.750000f, 4.437500f,
        3.750000f,
    ];

    internal static readonly float[] PredCoef = [29440 / 32768f, 26112 / 32768f, 21248 / 32768f, 16384 / 32768f];
    internal static readonly float[] BetaCoef = [30147 / 32768f, 22282 / 32768f, 12124 / 32768f, 6554 / 32768f];
    internal const float BetaIntra = 4915 / 32768f;

    internal static readonly byte[] SmallEnergyIcdf = [2, 1, 0];
    internal static readonly byte[] TrimIcdf = [126, 124, 119, 109, 87, 41, 19, 9, 4, 2, 0];
    internal static readonly byte[] SpreadIcdf = [25, 23, 2, 0];
    internal static readonly byte[] TapsetIcdf = [2, 1, 0];

    internal static readonly byte[] EProbModel =
    [
        // [LM 0] inter
        72, 127, 65, 129, 66, 128, 65, 128, 64, 128, 62, 128, 64, 128, 64, 128, 92, 78, 92, 79, 92, 78, 90, 79, 116, 41, 115, 40, 114, 40, 132, 26, 132, 26, 145, 17, 161, 12, 176, 10, 177, 11,
        // [LM 0] intra
        24, 179, 48, 138, 54, 135, 54, 132, 53, 134, 56, 133, 55, 132, 55, 132, 61, 114, 70, 96, 74, 88, 75, 88, 87, 74, 89, 66, 91, 67, 100, 59, 108, 50, 120, 40, 122, 37, 97, 43, 78, 50,
        // [LM 1] inter
        83, 78, 84, 81, 88, 75, 86, 74, 87, 71, 90, 73, 93, 74, 93, 74, 109, 40, 114, 36, 117, 34, 117, 34, 143, 17, 145, 18, 146, 19, 162, 12, 165, 10, 178, 7, 189, 6, 190, 8, 177, 9,
        // [LM 1] intra
        23, 178, 54, 115, 63, 102, 66, 98, 69, 99, 74, 89, 71, 91, 73, 91, 78, 89, 86, 80, 92, 66, 93, 64, 102, 59, 103, 60, 104, 60, 117, 52, 123, 44, 138, 35, 133, 31, 97, 38, 77, 45,
        // [LM 2] inter
        61, 90, 93, 60, 105, 42, 107, 41, 110, 45, 116, 38, 113, 38, 112, 38, 124, 26, 132, 27, 136, 19, 140, 20, 155, 14, 159, 16, 158, 18, 170, 13, 177, 10, 187, 8, 192, 6, 175, 9, 159, 10,
        // [LM 2] intra
        21, 178, 59, 110, 71, 86, 75, 85, 84, 83, 91, 66, 88, 73, 87, 72, 92, 75, 98, 72, 105, 58, 107, 54, 115, 52, 114, 55, 112, 56, 129, 51, 132, 40, 150, 33, 140, 29, 98, 35, 77, 42,
        // [LM 3] inter
        42, 121, 96, 66, 108, 43, 111, 40, 117, 44, 123, 32, 120, 36, 119, 33, 127, 33, 134, 34, 139, 21, 147, 23, 152, 20, 158, 25, 154, 26, 166, 21, 173, 16, 184, 13, 184, 10, 150, 13, 139, 15,
        // [LM 3] intra
        22, 178, 63, 114, 74, 82, 84, 83, 92, 82, 103, 62, 96, 72, 96, 67, 101, 73, 107, 72, 113, 55, 118, 52, 125, 52, 118, 52, 117, 55, 135, 49, 137, 39, 157, 32, 145, 29, 97, 33, 77, 40,
    ];

    internal static readonly sbyte[] TfSelectTable =
    [
        0, -1, 0, -1, 0, -1, 0, -1,
        0, -1, 0, -2, 1, 0, 1, -1,
        0, -2, 0, -3, 2, 0, 1, -1,
        0, -2, 0, -3, 3, 0, 1, -1,
    ];

    internal static readonly byte[] Log2FracTable = [0, 8, 13, 16, 19, 21, 23, 24, 26, 27, 28, 29, 30, 31, 32, 32, 33, 34, 34, 35, 36, 36, 37, 37];

    internal static int LogN(int band) => CeltModeTables.LogN400[band];

    internal static float[] Window => CeltModeTables.Window120;

    internal static int Ilog2(uint x) => x == 0 ? -1 : 31 - System.Numerics.BitOperations.LeadingZeroCount(x);

    internal static uint LcgRand(uint seed) => unchecked(1664525 * seed + 1013904223);

    internal static int FracMul16(int a, int b) => (16384 + a * b) >> 15;

    internal static short BitexactCos(short x)
    {
        var tmp = (4096 + x * x) >> 13;
        var x2 = (short)tmp;
        x2 = (short)((32767 - x2) + FracMul16(x2, -7651 + FracMul16(x2, 8277 + FracMul16(-626, x2))));
        return (short)(1 + x2);
    }

    internal static int BitexactLog2Tan(int isin, int icos)
    {
        var lc = (int)RangeConstants.Ilog((uint)icos);
        var ls = (int)RangeConstants.Ilog((uint)isin);
        icos <<= 15 - lc;
        isin <<= 15 - ls;
        return (ls - lc) * (1 << 11) + FracMul16(isin, FracMul16(isin, -2597) + 7932) - FracMul16(icos, FracMul16(icos, -2597) + 7932);
    }

    internal static uint Isqrt32(uint val)
    {
        uint g = 0;
        var bshift = (RangeConstants.Ilog(val) - 1) >> 1;
        var b = 1u << bshift;
        do
        {
            var t = ((g << 1) + b) << bshift;
            if (t <= val) { g += b; val -= t; }
            b >>= 1;
            bshift--;
        } while (bshift >= 0);
        return g;
    }

    internal static int GetPulses(int i) => i < 8 ? i : (8 + (i & 7)) << ((i >> 3) - 1);

    internal static int Bits2Pulses(int band, int lm, int bits)
    {
        lm++;
        var cache = CeltModeTables.CacheIndex50[lm * NbEBands + band];
        var bt = CeltModeTables.CacheBits50;
        var lo = 0;
        int hi = bt[cache];
        bits--;
        for (var i = 0; i < 6; i++)
        {
            var mid = (lo + hi + 1) >> 1;
            if (bt[cache + mid] >= bits) hi = mid; else lo = mid;
        }
        return bits - (lo == 0 ? -1 : bt[cache + lo]) <= bt[cache + hi] - bits ? lo : hi;
    }

    internal static int Pulses2Bits(int band, int lm, int pulses)
    {
        lm++;
        var cache = CeltModeTables.CacheIndex50[lm * NbEBands + band];
        return pulses == 0 ? 0 : CeltModeTables.CacheBits50[cache + pulses] + 1;
    }

    internal static void InitCaps(Span<int> cap, int lm, int c)
    {
        for (var i = 0; i < NbEBands; i++)
        {
            var n = (EBands[i + 1] - EBands[i]) << lm;
            cap[i] = (CeltModeTables.CacheCaps50[NbEBands * (2 * lm + c - 1) + i] + 64) * c * n >> 2;
        }
    }
}
