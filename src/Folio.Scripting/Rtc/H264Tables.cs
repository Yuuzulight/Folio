namespace Folio.Scripting.Rtc;

/// <summary>
/// Constant tables for the H.264 decoder. The CAVLC code tables (coeff_token, total_zeros, run_before; ITU-T H.264
/// tables 9-5, 9-7, 9-8, 9-9, 9-10) are stored as (length, code) pairs and cross-checked in the tests for prefix-freeness.
/// </summary>
internal static class H264Tables
{
    internal static readonly byte[] ChromaDcTokenLen = [2, 0, 0, 0, 6, 1, 0, 0, 6, 6, 3, 0, 6, 7, 7, 6, 6, 8, 8, 7];
    internal static readonly byte[] ChromaDcTokenBits = [1, 0, 0, 0, 7, 1, 0, 0, 4, 6, 1, 0, 3, 3, 2, 5, 2, 3, 2, 0];
    internal static readonly byte[] TokenLen0 = [1, 0, 0, 0, 6, 2, 0, 0, 8, 6, 3, 0, 9, 8, 7, 5, 10, 9, 8, 6, 11, 10, 9, 7, 13, 11, 10, 8, 13, 13, 11, 9, 13, 13, 13, 10, 14, 14, 13, 11, 14, 14, 14, 13, 15, 15, 14, 14, 15, 15, 15, 14, 16, 15, 15, 15, 16, 16, 16, 15, 16, 16, 16, 16, 16, 16, 16, 16];
    internal static readonly byte[] TokenLen1 = [2, 0, 0, 0, 6, 2, 0, 0, 6, 5, 3, 0, 7, 6, 6, 4, 8, 6, 6, 4, 8, 7, 7, 5, 9, 8, 8, 6, 11, 9, 9, 6, 11, 11, 11, 7, 12, 11, 11, 9, 12, 12, 12, 11, 12, 12, 12, 11, 13, 13, 13, 12, 13, 13, 13, 13, 13, 14, 13, 13, 14, 14, 14, 13, 14, 14, 14, 14];
    internal static readonly byte[] TokenLen2 = [4, 0, 0, 0, 6, 4, 0, 0, 6, 5, 4, 0, 6, 5, 5, 4, 7, 5, 5, 4, 7, 5, 5, 4, 7, 6, 6, 4, 7, 6, 6, 4, 8, 7, 7, 5, 8, 8, 7, 6, 9, 8, 8, 7, 9, 9, 8, 8, 9, 9, 9, 8, 10, 9, 9, 9, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10];
    internal static readonly byte[] TokenLen3 = [6, 0, 0, 0, 6, 6, 0, 0, 6, 6, 6, 0, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6];
    internal static readonly byte[] TokenBits0 = [1, 0, 0, 0, 5, 1, 0, 0, 7, 4, 1, 0, 7, 6, 5, 3, 7, 6, 5, 3, 7, 6, 5, 4, 15, 6, 5, 4, 11, 14, 5, 4, 8, 10, 13, 4, 15, 14, 9, 4, 11, 10, 13, 12, 15, 14, 9, 12, 11, 10, 13, 8, 15, 1, 9, 12, 11, 14, 13, 8, 7, 10, 9, 12, 4, 6, 5, 8];
    internal static readonly byte[] TokenBits1 = [3, 0, 0, 0, 11, 2, 0, 0, 7, 7, 3, 0, 7, 10, 9, 5, 7, 6, 5, 4, 4, 6, 5, 6, 7, 6, 5, 8, 15, 6, 5, 4, 11, 14, 13, 4, 15, 10, 9, 4, 11, 14, 13, 12, 8, 10, 9, 8, 15, 14, 13, 12, 11, 10, 9, 12, 7, 11, 6, 8, 9, 8, 10, 1, 7, 6, 5, 4];
    internal static readonly byte[] TokenBits2 = [15, 0, 0, 0, 15, 14, 0, 0, 11, 15, 13, 0, 8, 12, 14, 12, 15, 10, 11, 11, 11, 8, 9, 10, 9, 14, 13, 9, 8, 10, 9, 8, 15, 14, 13, 13, 11, 14, 10, 12, 15, 10, 13, 12, 11, 14, 9, 12, 8, 10, 13, 8, 13, 7, 9, 12, 9, 12, 11, 10, 5, 8, 7, 6, 1, 4, 3, 2];
    internal static readonly byte[] TokenBits3 = [3, 0, 0, 0, 0, 1, 0, 0, 4, 5, 6, 0, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 63];
    internal static readonly byte[][] TotalZerosLen = [[1, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 9], [3, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 6, 6, 6, 6], [4, 3, 3, 3, 4, 4, 3, 3, 4, 5, 5, 6, 5, 6], [5, 3, 4, 4, 3, 3, 3, 4, 3, 4, 5, 5, 5], [4, 4, 4, 3, 3, 3, 3, 3, 4, 5, 4, 5], [6, 5, 3, 3, 3, 3, 3, 3, 4, 3, 6], [6, 5, 3, 3, 3, 2, 3, 4, 3, 6], [6, 4, 5, 3, 2, 2, 3, 3, 6], [6, 6, 4, 2, 2, 3, 2, 5], [5, 5, 3, 2, 2, 2, 4], [4, 4, 3, 3, 1, 3], [4, 4, 2, 1, 3], [3, 3, 1, 2], [2, 2, 1], [1, 1]];
    internal static readonly byte[][] TotalZerosBits = [[1, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 1], [7, 6, 5, 4, 3, 5, 4, 3, 2, 3, 2, 3, 2, 1, 0], [5, 7, 6, 5, 4, 3, 4, 3, 2, 3, 2, 1, 1, 0], [3, 7, 5, 4, 6, 5, 4, 3, 3, 2, 2, 1, 0], [5, 4, 3, 7, 6, 5, 4, 3, 2, 1, 1, 0], [1, 1, 7, 6, 5, 4, 3, 2, 1, 1, 0], [1, 1, 5, 4, 3, 3, 2, 1, 1, 0], [1, 1, 1, 3, 3, 2, 2, 1, 0], [1, 0, 1, 3, 2, 1, 1, 1], [1, 0, 1, 3, 2, 1, 1], [0, 1, 1, 2, 1, 3], [0, 1, 1, 1, 1], [0, 1, 1, 1], [0, 1, 1], [0, 1]];
    internal static readonly byte[][] ChromaDcTotalZerosLen = [[1, 2, 3, 3], [1, 2, 2, 0], [1, 1, 0, 0]];
    internal static readonly byte[][] ChromaDcTotalZerosBits = [[1, 1, 1, 0], [1, 1, 0, 0], [1, 0, 0, 0]];
    internal static readonly byte[][] RunLen = [[1, 1], [1, 2, 2], [2, 2, 2, 2], [2, 2, 2, 3, 3], [2, 2, 3, 3, 3, 3], [2, 3, 3, 3, 3, 3, 3], [3, 3, 3, 3, 3, 3, 3, 4, 5, 6, 7, 8, 9, 10, 11]];
    internal static readonly byte[][] RunBits = [[1, 0], [1, 1, 0], [3, 2, 1, 0], [3, 2, 1, 1, 0], [3, 2, 3, 2, 1, 0], [3, 0, 1, 3, 2, 5, 4], [7, 6, 5, 4, 3, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1]];
    internal static readonly byte[] Tc0 = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 1, 1, 2, 1, 1, 2, 1, 1, 2, 1, 2, 3, 1, 2, 3, 2, 2, 3, 2, 2, 4, 2, 3, 4, 2, 3, 4, 3, 3, 5, 3, 4, 6, 3, 4, 6, 4, 5, 7, 4, 5, 8, 4, 6, 9, 5, 7, 10, 6, 8, 11, 6, 8, 13, 7, 10, 14, 8, 11, 16, 9, 12, 18, 10, 13, 20, 11, 15, 23, 13, 17, 25];
    internal static readonly byte[] Alpha = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4, 4, 5, 6, 7, 8, 9, 10, 12, 13, 15, 17, 20, 22, 25, 28, 32, 36, 40, 45, 50, 56, 63, 71, 80, 90, 101, 113, 127, 144, 162, 182, 203, 226, 255, 255];
    internal static readonly byte[] Beta = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14, 15, 15, 16, 16, 17, 17, 18, 18];

    internal static readonly byte[] CbpIntra = [47, 31, 15, 0, 23, 27, 29, 30, 7, 11, 13, 14, 39, 43, 45, 46, 16, 3, 5, 10, 12, 19, 21, 26, 28, 35, 37, 42, 44, 1, 2, 4, 8, 17, 18, 20, 24, 6, 9, 22, 25, 32, 33, 34, 36, 40, 38, 41];
    internal static readonly byte[] CbpInter = [0, 16, 1, 2, 4, 8, 32, 3, 5, 10, 12, 15, 47, 7, 11, 13, 14, 6, 9, 31, 35, 37, 42, 44, 33, 34, 36, 40, 39, 43, 45, 46, 17, 18, 20, 24, 19, 21, 26, 28, 23, 27, 29, 30, 22, 25, 38, 41];

    internal static readonly byte[] ChromaQp = [29, 30, 31, 32, 32, 33, 34, 34, 35, 35, 36, 36, 37, 37, 37, 38, 38, 38, 39, 39, 39, 39];

    /// <summary>4x4 zig-zag scan: scan index to raster position.</summary>
    internal static readonly byte[] Zigzag4 = [0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15];

    /// <summary>Normalisation (flat scaling) per qP % 6 for the three coefficient position classes.</summary>
    internal static readonly byte[] NormAdjust = [10, 16, 13, 11, 18, 14, 13, 20, 16, 14, 23, 18, 16, 25, 20, 18, 29, 23];

    /// <summary>A prefix-code lookup built from (length, code) tables; entries are (length &lt;&lt; 8) | value, zero for an invalid prefix.</summary>
    internal sealed class Vlc
    {
        internal readonly int MaxLen;
        internal readonly ushort[] Table;

        internal Vlc(byte[] len, byte[] bits)
        {
            for (var i = 0; i < len.Length; i++) if (len[i] > MaxLen) MaxLen = len[i];
            Table = new ushort[1 << MaxLen];
            for (var v = 0; v < len.Length; v++)
            {
                var l = len[v];
                if (l == 0) continue;
                var first = bits[v] << (MaxLen - l);
                var count = 1 << (MaxLen - l);
                for (var k = 0; k < count; k++) Table[first + k] = (ushort)(l << 8 | v);
            }
        }
    }

    internal static readonly Vlc[] CoeffToken = [new(TokenLen0, TokenBits0), new(TokenLen1, TokenBits1), new(TokenLen2, TokenBits2), new(TokenLen3, TokenBits3)];
    internal static readonly Vlc ChromaDcToken = new(ChromaDcTokenLen, ChromaDcTokenBits);
    internal static readonly Vlc[] TotalZeros = Build(TotalZerosLen, TotalZerosBits);
    internal static readonly Vlc[] ChromaDcTotalZeros = Build(ChromaDcTotalZerosLen, ChromaDcTotalZerosBits);
    internal static readonly Vlc[] RunBefore = Build(RunLen, RunBits);

    private static Vlc[] Build(byte[][] len, byte[][] bits)
    {
        var r = new Vlc[len.Length];
        for (var i = 0; i < r.Length; i++) r[i] = new Vlc(len[i], bits[i]);
        return r;
    }
}
