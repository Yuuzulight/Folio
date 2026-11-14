namespace Folio.Scripting.Rtc;

/// <summary>In-loop deblocking filter (8.7) and concealment of macroblocks a damaged picture never delivered.</summary>
internal sealed partial class H264Decoder
{
    private readonly int[] _bs = new int[4];

    private void ConcealMissing(Picture cur)
    {
        var source = _dpb.Count > 0 ? _dpb[^1] : null;
        var stride = _mbW * 16; var cs = _mbW * 8;
        for (var mb = 0; mb < _mbCount; mb++)
        {
            if (_sliceOf[mb] >= 0) continue;
            _kind[mb] = KindInter;
            var mx = mb % _mbW; var my = mb / _mbW;
            for (var r = 0; r < 16; r++)
            {
                var o = (my * 16 + r) * stride + mx * 16;
                if (source != null) Buffer.BlockCopy(source.Y, o, cur.Y, o, 16); else Array.Fill(cur.Y, (byte)128, o, 16);
            }
            for (var r = 0; r < 8; r++)
            {
                var o = (my * 8 + r) * cs + mx * 8;
                if (source != null) { Buffer.BlockCopy(source.U, o, cur.U, o, 8); Buffer.BlockCopy(source.V, o, cur.V, o, 8); }
                else { Array.Fill(cur.U, (byte)128, o, 8); Array.Fill(cur.V, (byte)128, o, 8); }
            }
        }
    }

    private void Deblock(Picture cur)
    {
        var stride = _mbW * 16; var cs = _mbW * 8;
        for (var mb = 0; mb < _mbCount; mb++)
        {
            var slice = _sliceOf[mb];
            if (slice < 0) continue;
            var (disable, offA, offB) = _slicePar[slice];
            if (disable == 1) continue;
            var mx = mb % _mbW; var my = mb / _mbW;
            var left = mx > 0 && _sliceOf[mb - 1] >= 0 && (disable != 2 || _sliceOf[mb - 1] == slice);
            var top = my > 0 && _sliceOf[mb - _mbW] >= 0 && (disable != 2 || _sliceOf[mb - _mbW] == slice);

            for (var dir = 0; dir < 2; dir++)
            {
                var vertical = dir == 0;
                for (var e = 0; e < 4; e++)
                {
                    if (e == 0 && !(vertical ? left : top)) continue;
                    var pmb = e > 0 ? mb : vertical ? mb - 1 : mb - _mbW;
                    var any = false;
                    for (var s = 0; s < 4; s++)
                    {
                        int bq, bp;
                        if (vertical) { bq = s * 4 + e; bp = e > 0 ? bq - 1 : s * 4 + 3; }
                        else { bq = e * 4 + s; bp = e > 0 ? bq - 4 : 12 + s; }
                        _bs[s] = Strength(mb, bq, pmb, bp, e == 0);
                        if (_bs[s] != 0) any = true;
                    }
                    if (!any) continue;

                    // Luma
                    var qpAv = (_qp[mb] + _qp[pmb] + 1) >> 1;
                    FilterEdge(cur.Y, stride, mx * 16, my * 16, e * 4, vertical, 16, qpAv, offA, offB, false);

                    // Chroma: edges 0 and 2 of the luma grid are the chroma transform edges; bS is shared.
                    if ((e & 1) == 0)
                    {
                        var qpc = (ChromaQp(_qp[mb]) + ChromaQp(_qp[pmb]) + 1) >> 1;
                        FilterEdge(cur.U, cs, mx * 8, my * 8, e * 2, vertical, 8, qpc, offA, offB, true);
                        FilterEdge(cur.V, cs, mx * 8, my * 8, e * 2, vertical, 8, qpc, offA, offB, true);
                    }
                }
            }
        }
    }

    /// <summary>Boundary strength (8.7.2.1) for frame macroblocks in I/P pictures.</summary>
    private int Strength(int mbQ, int bq, int mbP, int bp, bool mbEdge)
    {
        if (_kind[mbQ] != KindInter || _kind[mbP] != KindInter) return mbEdge ? 4 : 3;
        if (_tc[mbQ * 16 + bq] != 0 || _tc[mbP * 16 + bp] != 0) return 2;
        var rq = _refId[mbQ * 4 + ((bq >> 3) << 1 | (bq & 3) >> 1)];
        var rp = _refId[mbP * 4 + ((bp >> 3) << 1 | (bp & 3) >> 1)];
        if (rq != rp) return 1;
        var q = (mbQ * 16 + bq) * 2; var p = (mbP * 16 + bp) * 2;
        return Math.Abs(_mv[q] - _mv[p]) >= 4 || Math.Abs(_mv[q + 1] - _mv[p + 1]) >= 4 ? 1 : 0;
    }

    /// <summary>
    /// Filters one edge of a macroblock. <paramref name="offset"/> is the edge position inside the macroblock in samples,
    /// <paramref name="length"/> the number of lines along it (the four bS entries cover them evenly: 4 lines each for luma, 2 for chroma).
    /// </summary>
    private void FilterEdge(byte[] pl, int stride, int mbx, int mby, int offset, bool vertical, int length, int qpAv, int offA, int offB, bool chroma)
    {
        var indexA = Math.Clamp(qpAv + offA, 0, 51);
        var indexB = Math.Clamp(qpAv + offB, 0, 51);
        var alpha = H264Tables.Alpha[indexA];
        var beta = H264Tables.Beta[indexB];
        if (alpha == 0 || beta == 0) return;
        var across = vertical ? 1 : stride;
        var along = vertical ? stride : 1;
        var linesPerEntry = length / 4;
        var origin = vertical ? mby * stride + mbx + offset : (mby + offset) * stride + mbx;
        for (var line = 0; line < length; line++)
        {
            var bS = _bs[line / linesPerEntry];
            if (bS == 0) continue;
            var q0i = origin + line * along;
            int p0 = pl[q0i - across], q0 = pl[q0i];
            if (Math.Abs(p0 - q0) >= alpha) continue;
            int p1 = pl[q0i - 2 * across], q1 = pl[q0i + across];
            if (Math.Abs(p1 - p0) >= beta || Math.Abs(q1 - q0) >= beta) continue;
            if (chroma)
            {
                if (bS < 4)
                {
                    var tc = H264Tables.Tc0[indexA * 3 + bS - 1] + 1;
                    var delta = Math.Clamp((((q0 - p0) << 2) + (p1 - q1) + 4) >> 3, -tc, tc);
                    pl[q0i - across] = Clip(p0 + delta);
                    pl[q0i] = Clip(q0 - delta);
                }
                else
                {
                    pl[q0i - across] = (byte)((2 * p1 + p0 + q1 + 2) >> 2);
                    pl[q0i] = (byte)((2 * q1 + q0 + p1 + 2) >> 2);
                }
                continue;
            }
            int p2 = pl[q0i - 3 * across], q2 = pl[q0i + 2 * across];
            var ap = Math.Abs(p2 - p0); var aq = Math.Abs(q2 - q0);
            if (bS < 4)
            {
                var tc0 = H264Tables.Tc0[indexA * 3 + bS - 1];
                var tc = tc0 + (ap < beta ? 1 : 0) + (aq < beta ? 1 : 0);
                var delta = Math.Clamp((((q0 - p0) << 2) + (p1 - q1) + 4) >> 3, -tc, tc);
                pl[q0i - across] = Clip(p0 + delta);
                pl[q0i] = Clip(q0 - delta);
                if (ap < beta) pl[q0i - 2 * across] = (byte)(p1 + Math.Clamp((p2 + ((p0 + q0 + 1) >> 1) - (p1 << 1)) >> 1, -tc0, tc0));
                if (aq < beta) pl[q0i + across] = (byte)(q1 + Math.Clamp((q2 + ((p0 + q0 + 1) >> 1) - (q1 << 1)) >> 1, -tc0, tc0));
            }
            else
            {
                var strong = Math.Abs(p0 - q0) < (alpha >> 2) + 2;
                if (ap < beta && strong)
                {
                    int p3 = pl[q0i - 4 * across];
                    pl[q0i - across] = (byte)((p2 + 2 * p1 + 2 * p0 + 2 * q0 + q1 + 4) >> 3);
                    pl[q0i - 2 * across] = (byte)((p2 + p1 + p0 + q0 + 2) >> 2);
                    pl[q0i - 3 * across] = (byte)((2 * p3 + 3 * p2 + p1 + p0 + q0 + 4) >> 3);
                }
                else pl[q0i - across] = (byte)((2 * p1 + p0 + q1 + 2) >> 2);
                if (aq < beta && strong)
                {
                    int q3 = pl[q0i + 3 * across];
                    pl[q0i] = (byte)((p1 + 2 * p0 + 2 * q0 + 2 * q1 + q2 + 4) >> 3);
                    pl[q0i + across] = (byte)((p0 + q0 + q1 + q2 + 2) >> 2);
                    pl[q0i + 2 * across] = (byte)((2 * q3 + 3 * q2 + q1 + q0 + p0 + 4) >> 3);
                }
                else pl[q0i] = (byte)((2 * q1 + q0 + p1 + 2) >> 2);
            }
        }

        static byte Clip(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
    }
}
