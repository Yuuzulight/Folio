// Pyramid vector quantiser of the CELT layer (RFC 6716 section 4.3.4): combinatorial codeword (de)coding and the spreading
// rotation. After the Opus reference implementation (celt/cwrs.c, celt/vq.c; Copyright (c) 2007-2009 Xiph.Org Foundation,
// CSIRO, Timothy B. Terriberry, Jean-Marc Valin; BSD 3-clause, see docs/webrtc-opus.md). The row-recurrence form of
// cwrs.c is used, so no large table is needed. Hostile streams cannot reach unchecked sizes: (N, K) pairs only come from the
// bit allocation, which clamps K through the pulse cache.
namespace Folio.Scripting.Rtc.Opus;

internal static class CeltPvq
{
    private static void Unext(Span<uint> ui, int len, uint ui0)
    {
        var j = 1;
        do
        {
            var ui1 = ui[j] + ui[j - 1] + ui0;
            ui[j - 1] = ui0;
            ui0 = ui1;
        } while (++j < len);
        ui[j - 1] = ui0;
    }

    private static void Uprev(Span<uint> ui, int n, uint ui0)
    {
        var j = 1;
        do
        {
            var ui1 = ui[j] - ui[j - 1] - ui0;
            ui[j - 1] = ui0;
            ui0 = ui1;
        } while (++j < n);
        ui[j - 1] = ui0;
    }

    /// <summary>Fills the U row for (n, k) and returns V(n, k), the number of codewords.</summary>
    private static uint NcwrsUrow(int n, int k, Span<uint> u)
    {
        var len = k + 2;
        u[0] = 0;
        u[1] = 1;
        for (var i = 2; i < len; i++) u[i] = (uint)((i << 1) - 1);
        for (var i = 2; i < n; i++) Unext(u[1..], k + 1, 1);
        return u[k] + u[k + 1];
    }

    private static float Cwrsi(int n, int k, uint i, Span<int> y, Span<uint> u)
    {
        var yy = 0f;
        var j = 0;
        do
        {
            var p = u[k + 1];
            var s = i >= p ? -1 : 0;
            i -= p & (uint)s;
            var yj = k;
            p = u[k];
            while (p > i) p = u[--k];
            i -= p;
            yj -= k;
            var val = (yj + s) ^ s;
            y[j] = val;
            yy += (float)val * val;
            Uprev(u, k + 2, 0);
        } while (++j < n);
        return yy;
    }

    /// <summary>Decodes a PVQ codeword of <paramref name="n"/> dimensions and <paramref name="k"/> pulses; returns its energy.</summary>
    internal static float DecodePulses(Span<int> y, int n, int k, RangeDecoder dec)
    {
        Span<uint> u = stackalloc uint[132];
        if (k <= 0 || k > 128 || n < 2 || n > 256) { y[..Math.Max(0, Math.Min(n, y.Length))].Clear(); return 0; }
        var v = NcwrsUrow(n, k, u);
        return Cwrsi(n, k, dec.Uint(v), y, u);
    }

    internal static void EncodePulses(ReadOnlySpan<int> y, int n, int k, RangeEncoder enc)
    {
        Span<uint> u = stackalloc uint[132];
        u[0] = 0;
        for (var i = 1; i <= k + 1; i++) u[i] = (uint)((i << 1) - 1);
        // icwrs1 on the last coefficient
        var kk = Math.Abs(y[n - 1]);
        uint code = y[n - 1] < 0 ? 1u : 0u;
        var j = n - 2;
        code += u[kk];
        kk += Math.Abs(y[j]);
        if (y[j] < 0) code += u[kk + 1];
        while (j-- > 0)
        {
            Unext(u, k + 2, 0);
            code += u[kk];
            kk += Math.Abs(y[j]);
            if (y[j] < 0) code += u[kk + 1];
        }
        var nc = u[kk] + u[kk + 1];
        enc.Uint(code, nc);
    }

    private static void ExpRotation1(Span<float> x, int len, int stride, float c, float s)
    {
        var ms = -s;
        for (var i = 0; i < len - stride; i++)
        {
            var x1 = x[i];
            var x2 = x[i + stride];
            x[i + stride] = c * x2 + s * x1;
            x[i] = c * x1 + ms * x2;
        }
        for (var i = len - 2 * stride - 1; i >= 0; i--)
        {
            var x1 = x[i];
            var x2 = x[i + stride];
            x[i + stride] = c * x2 + s * x1;
            x[i] = c * x1 + ms * x2;
        }
    }

    internal static void ExpRotation(Span<float> x, int len, int dir, int stride, int k, int spread)
    {
        if (2 * k >= len || spread == Celt.SpreadNone) return;
        ReadOnlySpan<int> spreadFactor = [15, 10, 5];
        var factor = spreadFactor[spread - 1];
        var gain = (float)len / (len + factor * k);
        var theta = .5f * (gain * gain);
        var c = MathF.Cos(0.5f * MathF.PI * theta);
        var s = MathF.Cos(0.5f * MathF.PI * (1f - theta));
        var stride2 = 0;
        if (len >= 8 * stride)
        {
            stride2 = 1;
            while ((stride2 * stride2 + stride2) * stride + (stride >> 2) < len) stride2++;
        }
        len /= stride;
        for (var i = 0; i < stride; i++)
        {
            var block = x.Slice(i * len, len);
            if (dir < 0)
            {
                if (stride2 != 0) ExpRotation1(block, len, stride2, s, c);
                ExpRotation1(block, len, 1, c, s);
            }
            else
            {
                ExpRotation1(block, len, 1, c, -s);
                if (stride2 != 0) ExpRotation1(block, len, stride2, s, -c);
            }
        }
    }

    private static void NormaliseResidual(ReadOnlySpan<int> iy, Span<float> x, int n, float ryy, float gain)
    {
        var g = 1f / MathF.Sqrt(ryy) * gain;
        for (var i = 0; i < n; i++) x[i] = g * iy[i];
    }

    private static uint ExtractCollapseMask(ReadOnlySpan<int> iy, int n, int b)
    {
        if (b <= 1) return 1;
        var n0 = n / b;
        uint mask = 0;
        for (var i = 0; i < b; i++)
        {
            var tmp = 0;
            for (var j = 0; j < n0; j++) tmp |= iy[i * n0 + j];
            if (tmp != 0) mask |= 1u << i;
        }
        return mask;
    }

    internal static uint AlgUnquant(Span<float> x, int n, int k, int spread, int b, RangeDecoder dec, float gain)
    {
        Span<int> iy = stackalloc int[Math.Min(Math.Max(n, 2), 256)];
        var ryy = DecodePulses(iy, n, k, dec);
        if (ryy <= 0) { x[..n].Clear(); return 0; }
        NormaliseResidual(iy, x, n, ryy, gain);
        ExpRotation(x, n, -1, b, k, spread);
        return ExtractCollapseMask(iy, n, b);
    }

    internal static void RenormaliseVector(Span<float> x, int n, float gain)
    {
        var e = Celt.Epsilon;
        for (var i = 0; i < n; i++) e += x[i] * x[i];
        var g = 1f / MathF.Sqrt(e) * gain;
        for (var i = 0; i < n; i++) x[i] *= g;
    }

    // ---- encoder side ----

    private static float PvqSearch(Span<float> x, Span<int> iy, int k, int n)
    {
        Span<float> y = stackalloc float[n];
        Span<int> signx = stackalloc int[n];
        var sum = 0f;
        for (var j = 0; j < n; j++)
        {
            signx[j] = x[j] < 0 ? 1 : 0;
            x[j] = MathF.Abs(x[j]);
            iy[j] = 0;
            y[j] = 0;
        }
        float xy = 0, yy = 0;
        var pulsesLeft = k;
        if (k > (n >> 1))
        {
            for (var j = 0; j < n; j++) sum += x[j];
            if (!(sum > Celt.Epsilon && sum < 64))
            {
                x[0] = 1f;
                for (var j = 1; j < n; j++) x[j] = 0;
                sum = 1f;
            }
            var rcp = (k + 0.8f) * (1f / sum);
            for (var j = 0; j < n; j++)
            {
                iy[j] = (int)MathF.Floor(rcp * x[j]);
                y[j] = iy[j];
                yy += y[j] * y[j];
                xy += x[j] * y[j];
                y[j] *= 2;
                pulsesLeft -= iy[j];
            }
        }
        if (pulsesLeft > n + 3)
        {
            var tmp = (float)pulsesLeft;
            yy += tmp * tmp;
            yy += tmp * y[0];
            iy[0] += pulsesLeft;
            pulsesLeft = 0;
        }
        for (var i = 0; i < pulsesLeft; i++)
        {
            var bestId = 0;
            yy += 1;
            var rxy = xy + x[0];
            var ryy = yy + y[0];
            rxy *= rxy;
            var bestDen = ryy;
            var bestNum = rxy;
            for (var j = 1; j < n; j++)
            {
                rxy = xy + x[j];
                ryy = yy + y[j];
                rxy *= rxy;
                if (bestDen * rxy > ryy * bestNum) { bestDen = ryy; bestNum = rxy; bestId = j; }
            }
            xy += x[bestId];
            yy += y[bestId];
            y[bestId] += 2;
            iy[bestId]++;
        }
        for (var j = 0; j < n; j++) iy[j] = (iy[j] ^ -signx[j]) + signx[j];
        return yy;
    }

    internal static uint AlgQuant(Span<float> x, int n, int k, int spread, int b, RangeEncoder enc)
    {
        Span<int> iy = stackalloc int[n + 3];
        ExpRotation(x, n, 1, b, k, spread);
        PvqSearch(x, iy, k, n);
        EncodePulses(iy, n, k, enc);
        return ExtractCollapseMask(iy, n, b);
    }
}
