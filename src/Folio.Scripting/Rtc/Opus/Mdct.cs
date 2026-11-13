// MDCT of the CELT layer (RFC 6716 section 4.3.7), after the Opus reference implementation
// (Copyright (c) 2007-2008 CSIRO, 2007-2008 Xiph.Org Foundation, Written by Jean-Marc Valin; BSD 3-clause, see docs/webrtc-opus.md).
// The complex transform is a small mixed-radix FFT written for this port; the pre- and post-rotations follow the reference.
namespace Folio.Scripting.Rtc.Opus;

/// <summary>Complex DFT on interleaved (re, im) floats for sizes made of the factors 2, 3, 4 and 5. Not thread safe (it owns scratch).</summary>
internal sealed class ComplexFft
{
    private readonly int _n;
    private readonly int[] _factors;
    private readonly float[] _twRe, _twIm;
    private readonly float[] _scratchIn;
    private readonly float[] _bfly = new float[10];

    internal ComplexFft(int n)
    {
        _n = n;
        var list = new List<int>();
        var rest = n;
        foreach (var p in new[] { 4, 2, 3, 5 })
            while (rest % p == 0 && rest > 1) { list.Add(p); rest /= p; }
        if (rest != 1) throw new ArgumentOutOfRangeException(nameof(n));
        // factors as (p, m) pairs where m is the remaining size after this stage
        _factors = new int[list.Count * 2];
        var m = n;
        for (var i = 0; i < list.Count; i++) { m /= list[i]; _factors[2 * i] = list[i]; _factors[2 * i + 1] = m; }
        _twRe = new float[n]; _twIm = new float[n];
        for (var i = 0; i < n; i++)
        {
            var phase = -2 * Math.PI / n * i;
            _twRe[i] = (float)Math.Cos(phase); _twIm[i] = (float)Math.Sin(phase);
        }
        _scratchIn = new float[2 * n];
    }

    /// <summary>In-place forward DFT of <c>n</c> complex values starting at <paramref name="offset"/> (unscaled, e^-i).</summary>
    internal void Transform(float[] data, int offset)
    {
        Array.Copy(data, offset, _scratchIn, 0, 2 * _n);
        Work(data, offset, 0, 1, 0);
    }

    private void Work(float[] output, int outOff, int inIdx, int fstride, int level)
    {
        var p = _factors[2 * level];
        var m = _factors[2 * level + 1];
        if (m == 1)
        {
            for (var j = 0; j < p; j++)
            {
                output[outOff + 2 * j] = _scratchIn[2 * (inIdx + j * fstride)];
                output[outOff + 2 * j + 1] = _scratchIn[2 * (inIdx + j * fstride) + 1];
            }
        }
        else
        {
            for (var j = 0; j < p; j++) Work(output, outOff + 2 * j * m, inIdx + j * fstride, fstride * p, level + 1);
        }
        var s = _bfly;
        for (var u = 0; u < m; u++)
        {
            var k = u;
            for (var q1 = 0; q1 < p; q1++)
            {
                s[2 * q1] = output[outOff + 2 * k];
                s[2 * q1 + 1] = output[outOff + 2 * k + 1];
                k += m;
            }
            k = u;
            for (var q1 = 0; q1 < p; q1++)
            {
                var twidx = 0;
                var re = s[0]; var im = s[1];
                for (var q = 1; q < p; q++)
                {
                    twidx += fstride * k;
                    if (twidx >= _n) twidx -= _n;
                    var tr = _twRe[twidx]; var ti = _twIm[twidx];
                    re += s[2 * q] * tr - s[2 * q + 1] * ti;
                    im += s[2 * q] * ti + s[2 * q + 1] * tr;
                }
                output[outOff + 2 * k] = re;
                output[outOff + 2 * k + 1] = im;
                k += m;
            }
        }
    }
}

internal sealed class Mdct
{
    private readonly int _n;
    private readonly int _maxShift;
    private readonly float[][] _trig;
    private readonly ComplexFft[] _fft;
    private readonly float[] _fwdTmp;

    internal Mdct(int n, int maxShift)
    {
        _n = n; _maxShift = maxShift;
        _trig = new float[maxShift + 1][];
        _fft = new ComplexFft[maxShift + 1];
        var nn = n;
        for (var s = 0; s <= maxShift; s++)
        {
            _fft[s] = new ComplexFft(n >> 2 >> s);
            var n2 = nn >> 1;
            _trig[s] = new float[n2];
            for (var i = 0; i < n2; i++) _trig[s][i] = (float)Math.Cos(2 * Math.PI * (i + .125) / nn);
            nn >>= 1;
        }
        _fwdTmp = new float[n];
    }

    /// <summary>Inverse MDCT with the windowed overlap-add of the reference. <paramref name="input"/> is read with the given stride.</summary>
    internal void Backward(float[] input, int inOff, float[] output, int outOff, float[] window, int overlap, int shift, int stride)
    {
        var n = _n >> shift;
        var n2 = n >> 1;
        var n4 = n >> 2;
        var t = _trig[shift];
        var xp1 = inOff;
        var xp2 = inOff + stride * (n2 - 1);
        var yp = outOff + (overlap >> 1);
        for (var i = 0; i < n4; i++)
        {
            var yr = input[xp2] * t[i] + input[xp1] * t[n4 + i];
            var yi = input[xp1] * t[i] - input[xp2] * t[n4 + i];
            output[yp + 2 * i + 1] = yr;
            output[yp + 2 * i] = yi;
            xp1 += 2 * stride;
            xp2 -= 2 * stride;
        }
        _fft[shift].Transform(output, yp);
        var p0 = yp;
        var p1 = yp + n2 - 2;
        for (var i = 0; i < (n4 + 1) >> 1; i++)
        {
            var re = output[p0 + 1];
            var im = output[p0];
            var t0 = t[i];
            var t1 = t[n4 + i];
            var yr = re * t0 + im * t1;
            var yi = re * t1 - im * t0;
            re = output[p1 + 1];
            im = output[p1];
            output[p0] = yr;
            output[p1 + 1] = yi;
            t0 = t[n4 - i - 1];
            t1 = t[n2 - i - 1];
            yr = re * t0 + im * t1;
            yi = re * t1 - im * t0;
            output[p1] = yr;
            output[p0 + 1] = yi;
            p0 += 2;
            p1 -= 2;
        }
        var xo = outOff + overlap - 1;
        var yo = outOff;
        var w1 = 0;
        var w2 = overlap - 1;
        for (var i = 0; i < overlap / 2; i++)
        {
            var x1 = output[xo];
            var x2 = output[yo];
            output[yo++] = window[w2] * x2 - window[w1] * x1;
            output[xo--] = window[w1] * x2 + window[w2] * x1;
            w1++;
            w2--;
        }
    }

    /// <summary>Forward MDCT (encoder side).</summary>
    internal void Forward(float[] input, int inOff, float[] output, int outOff, float[] window, int overlap, int shift, int stride)
    {
        var n = _n >> shift;
        var n2 = n >> 1;
        var n4 = n >> 2;
        var t = _trig[shift];
        var scale = 1f / n4;
        var f = _fwdTmp; // first n2 floats: folded input; the next 2 * n4 floats: complex work area
        var f2Off = n2;
        var xp1 = inOff + (overlap >> 1);
        var xp2 = inOff + n2 - 1 + (overlap >> 1);
        var yp = 0;
        var wp1 = overlap >> 1;
        var wp2 = (overlap >> 1) - 1;
        int i;
        for (i = 0; i < ((overlap + 3) >> 2); i++)
        {
            f[yp++] = window[wp2] * input[xp1 + n2] + window[wp1] * input[xp2];
            f[yp++] = window[wp1] * input[xp1] - window[wp2] * input[xp2 - n2];
            xp1 += 2; xp2 -= 2; wp1 += 2; wp2 -= 2;
        }
        wp1 = 0;
        wp2 = overlap - 1;
        for (; i < n4 - ((overlap + 3) >> 2); i++)
        {
            f[yp++] = input[xp2];
            f[yp++] = input[xp1];
            xp1 += 2; xp2 -= 2;
        }
        for (; i < n4; i++)
        {
            f[yp++] = -window[wp1] * input[xp1 - n2] + window[wp2] * input[xp2];
            f[yp++] = window[wp2] * input[xp1] + window[wp1] * input[xp2 + n2];
            xp1 += 2; xp2 -= 2; wp1 += 2; wp2 -= 2;
        }
        for (i = 0; i < n4; i++)
        {
            var t0 = t[i];
            var t1 = t[n4 + i];
            var re = f[2 * i];
            var im = f[2 * i + 1];
            f[f2Off + 2 * i] = (re * t0 - im * t1) * scale;
            f[f2Off + 2 * i + 1] = (im * t0 + re * t1) * scale;
        }
        _fft[shift].Transform(f, f2Off);
        var yp1 = outOff;
        var yp2 = outOff + stride * (n2 - 1);
        for (i = 0; i < n4; i++)
        {
            var fr = f[f2Off + 2 * i];
            var fi = f[f2Off + 2 * i + 1];
            output[yp1] = fi * t[n4 + i] - fr * t[i];
            output[yp2] = fr * t[n4 + i] + fi * t[i];
            yp1 += 2 * stride;
            yp2 -= 2 * stride;
        }
    }
}
