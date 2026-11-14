namespace Folio.Scripting.Rtc;

/// <summary>Intra prediction (8.3) and inter prediction sample interpolation (8.4.2.2).</summary>
internal sealed partial class H264Decoder
{
    private readonly int[] _mcTmp = new int[21 * 21];
    private readonly int[] _mcCols = new int[21];

    // ---- intra 4x4 ----

    private void Pred4x4(int mb, int mode, int bx, int by, byte[] pl, int stride, int x, int y)
    {
        var availL = bx > 0 || AvailMb(LeftMb(mb));
        var availT = by > 0 || AvailMb(TopMb(mb));
        var availTL = bx > 0 ? (by > 0 || AvailMb(TopMb(mb))) : (by > 0 ? AvailMb(LeftMb(mb)) : AvailMb(TopLeftMb(mb)));
        bool availTR;
        if (by == 0) availTR = bx < 3 ? AvailMb(TopMb(mb)) : AvailMb(TopRightMb(mb));
        else availTR = bx < 3 && BlkIdx(bx + 1, by - 1) < BlkIdx(bx, by);

        var o = y * stride + x;
        var t = _top; var l = _left;
        var tl = 128;
        if (availT)
        {
            for (var i = 0; i < 4; i++) t[i] = pl[o - stride + i];
            for (var i = 4; i < 8; i++) t[i] = availTR ? pl[o - stride + i] : t[3];
        }
        if (availL) for (var i = 0; i < 4; i++) l[i] = pl[o + i * stride - 1];
        if (availTL) tl = pl[o - stride - 1];

        switch (mode)
        {
            case 0:
                if (!availT) throw new H264Exception("intra 4x4 vertical without top samples");
                break;
            case 1 or 8:
                if (!availL) throw new H264Exception("intra 4x4 horizontal without left samples");
                break;
            case 3 or 7:
                if (!availT) throw new H264Exception("intra 4x4 diagonal without top samples");
                break;
            case 4 or 5 or 6:
                if (!availT || !availL || !availTL) throw new H264Exception("intra 4x4 diagonal without neighbours");
                break;
            case > 8:
                throw new H264Exception("intra 4x4 mode");
        }

        int P(int xx, int yy) => yy < 0 ? (xx < 0 ? tl : t[xx]) : l[yy];

        for (var yy = 0; yy < 4; yy++)
        {
            for (var xx = 0; xx < 4; xx++)
            {
                int v;
                switch (mode)
                {
                    case 0: v = t[xx]; break;
                    case 1: v = l[yy]; break;
                    case 2:
                        {
                            var st = t[0] + t[1] + t[2] + t[3];
                            var sl = l[0] + l[1] + l[2] + l[3];
                            v = availT && availL ? (st + sl + 4) >> 3 : availL ? (sl + 2) >> 2 : availT ? (st + 2) >> 2 : 128;
                            break;
                        }
                    case 3:
                        v = xx == 3 && yy == 3 ? (t[6] + 3 * t[7] + 2) >> 2 : (t[xx + yy] + 2 * t[xx + yy + 1] + t[xx + yy + 2] + 2) >> 2;
                        break;
                    case 4:
                        if (xx > yy) v = (P(xx - yy - 2, -1) + 2 * P(xx - yy - 1, -1) + P(xx - yy, -1) + 2) >> 2;
                        else if (xx < yy) v = (P(-1, yy - xx - 2) + 2 * P(-1, yy - xx - 1) + P(-1, yy - xx) + 2) >> 2;
                        else v = (P(0, -1) + 2 * tl + P(-1, 0) + 2) >> 2;
                        break;
                    case 5:
                        {
                            var z = 2 * xx - yy;
                            var k = xx - (yy >> 1);
                            if (z >= 0 && (z & 1) == 0) v = (P(k - 1, -1) + P(k, -1) + 1) >> 1;
                            else if (z >= 0) v = (P(k - 2, -1) + 2 * P(k - 1, -1) + P(k, -1) + 2) >> 2;
                            else if (z == -1) v = (P(-1, 0) + 2 * tl + P(0, -1) + 2) >> 2;
                            else v = (P(-1, yy - 1) + 2 * P(-1, yy - 2) + P(-1, yy - 3) + 2) >> 2;
                            break;
                        }
                    case 6:
                        {
                            var z = 2 * yy - xx;
                            var k = yy - (xx >> 1);
                            if (z >= 0 && (z & 1) == 0) v = (P(-1, k - 1) + P(-1, k) + 1) >> 1;
                            else if (z >= 0) v = (P(-1, k - 2) + 2 * P(-1, k - 1) + P(-1, k) + 2) >> 2;
                            else if (z == -1) v = (P(-1, 0) + 2 * tl + P(0, -1) + 2) >> 2;
                            else v = (P(xx - 1, -1) + 2 * P(xx - 2, -1) + P(xx - 3, -1) + 2) >> 2;
                            break;
                        }
                    case 7:
                        {
                            var k = xx + (yy >> 1);
                            v = (yy & 1) == 0 ? (t[k] + t[k + 1] + 1) >> 1 : (t[k] + 2 * t[k + 1] + t[k + 2] + 2) >> 2;
                            break;
                        }
                    default:
                        {
                            var z = xx + 2 * yy;
                            var k = yy + (xx >> 1);
                            if (z > 5) v = l[3];
                            else if (z == 5) v = (l[2] + 3 * l[3] + 2) >> 2;
                            else if ((z & 1) == 0) v = (l[k] + l[k + 1] + 1) >> 1;
                            else v = (l[k] + 2 * l[k + 1] + l[k + 2] + 2) >> 2;
                            break;
                        }
                }
                pl[o + yy * stride + xx] = (byte)v;
            }
        }
    }

    // ---- intra 16x16 and chroma ----

    private void Pred16x16(int mb, int mode, byte[] pl, int stride, int x, int y)
    {
        var availL = AvailMb(LeftMb(mb));
        var availT = AvailMb(TopMb(mb));
        var availTL = AvailMb(TopLeftMb(mb));
        var o = y * stride + x;
        switch (mode)
        {
            case 0:
                if (!availT) throw new H264Exception("intra 16x16 vertical without top samples");
                for (var r = 0; r < 16; r++) Buffer.BlockCopy(pl, o - stride, pl, o + r * stride, 16);
                break;
            case 1:
                if (!availL) throw new H264Exception("intra 16x16 horizontal without left samples");
                for (var r = 0; r < 16; r++) Array.Fill(pl, pl[o + r * stride - 1], o + r * stride, 16);
                break;
            case 2:
                {
                    var st = 0; var sl = 0;
                    if (availT) for (var i = 0; i < 16; i++) st += pl[o - stride + i];
                    if (availL) for (var i = 0; i < 16; i++) sl += pl[o + i * stride - 1];
                    var v = availT && availL ? (st + sl + 16) >> 5 : availL ? (sl + 8) >> 4 : availT ? (st + 8) >> 4 : 128;
                    for (var r = 0; r < 16; r++) Array.Fill(pl, (byte)v, o + r * stride, 16);
                    break;
                }
            case 3:
                {
                    if (!availT || !availL || !availTL) throw new H264Exception("intra 16x16 plane without neighbours");
                    int hh = 0, vv = 0;
                    for (var i = 0; i < 8; i++)
                    {
                        hh += (i + 1) * (pl[o - stride + 8 + i] - pl[o - stride + 6 - i]);
                        vv += (i + 1) * (pl[o + (8 + i) * stride - 1] - pl[o + (6 - i) * stride - 1]);
                    }
                    var a = 16 * (pl[o + 15 * stride - 1] + pl[o - stride + 15]);
                    var b = (5 * hh + 32) >> 6;
                    var c = (5 * vv + 32) >> 6;
                    for (var yy = 0; yy < 16; yy++)
                        for (var xx = 0; xx < 16; xx++)
                            pl[o + yy * stride + xx] = (byte)Math.Clamp((a + b * (xx - 7) + c * (yy - 7) + 16) >> 5, 0, 255);
                    break;
                }
        }
    }

    private void PredChroma(int mb, int mode)
    {
        var availL = AvailMb(LeftMb(mb));
        var availT = AvailMb(TopMb(mb));
        var availTL = AvailMb(TopLeftMb(mb));
        var stride = _mbW * 8;
        var x = _mbX * 8; var y = _mbY * 8;
        for (var c = 0; c < 2; c++)
        {
            var pl = c == 0 ? _cur!.U : _cur!.V;
            var o = y * stride + x;
            switch (mode)
            {
                case 0:
                    for (var blk = 0; blk < 4; blk++)
                    {
                        var xo = (blk & 1) * 4; var yo = (blk >> 1) * 4;
                        int st = 0, sl = 0;
                        if (availT) for (var i = 0; i < 4; i++) st += pl[o - stride + xo + i];
                        if (availL) for (var i = 0; i < 4; i++) sl += pl[o + (yo + i) * stride - 1];
                        int v;
                        if (xo == yo) v = availT && availL ? (st + sl + 4) >> 3 : availL ? (sl + 2) >> 2 : availT ? (st + 2) >> 2 : 128;
                        else if (xo > 0) v = availT ? (st + 2) >> 2 : availL ? (sl + 2) >> 2 : 128;
                        else v = availL ? (sl + 2) >> 2 : availT ? (st + 2) >> 2 : 128;
                        for (var r = 0; r < 4; r++) Array.Fill(pl, (byte)v, o + (yo + r) * stride + xo, 4);
                    }
                    break;
                case 1:
                    if (!availL) throw new H264Exception("chroma horizontal without left samples");
                    for (var r = 0; r < 8; r++) Array.Fill(pl, pl[o + r * stride - 1], o + r * stride, 8);
                    break;
                case 2:
                    if (!availT) throw new H264Exception("chroma vertical without top samples");
                    for (var r = 0; r < 8; r++) Buffer.BlockCopy(pl, o - stride, pl, o + r * stride, 8);
                    break;
                default:
                    {
                        if (!availT || !availL || !availTL) throw new H264Exception("chroma plane without neighbours");
                        int hh = 0, vv = 0;
                        for (var i = 0; i < 4; i++)
                        {
                            hh += (i + 1) * (pl[o - stride + 4 + i] - pl[o - stride + 2 - i]);
                            vv += (i + 1) * (pl[o + (4 + i) * stride - 1] - pl[o + (2 - i) * stride - 1]);
                        }
                        var a = 16 * (pl[o + 7 * stride - 1] + pl[o - stride + 7]);
                        var b = (34 * hh + 32) >> 6;
                        var cc = (34 * vv + 32) >> 6;
                        for (var yy = 0; yy < 8; yy++)
                            for (var xx = 0; xx < 8; xx++)
                                pl[o + yy * stride + xx] = (byte)Math.Clamp((a + b * (xx - 3) + cc * (yy - 3) + 16) >> 5, 0, 255);
                        break;
                    }
            }
        }
    }

    // ---- inter ----

    private void MotionCompensate(int mb, int bx, int by, int w4, int h4, int refIdx, int mvx, int mvy)
    {
        var refPic = _refList[refIdx];
        var cur = _cur!;
        var w = w4 * 4; var h = h4 * 4;
        var px = _mbX * 16 + bx * 4; var py = _mbY * 16 + by * 4;
        McLuma(refPic.Y, px, py, w, h, mvx, mvy, cur.Y);
        McChroma(refPic.U, px / 2, py / 2, w / 2, h / 2, mvx, mvy, cur.U);
        McChroma(refPic.V, px / 2, py / 2, w / 2, h / 2, mvx, mvy, cur.V);
    }

    private void McLuma(byte[] src, int px, int py, int w, int h, int mvx, int mvy, byte[] dst)
    {
        var width = _mbW * 16; var height = _mbH * 16;
        var ix = px + (mvx >> 2); var iy = py + (mvy >> 2);
        var fx = mvx & 3; var fy = mvy & 3;
        var tw = w + 5; var th = h + 5;
        var t = _mcTmp;
        var cols = _mcCols;
        for (var c = 0; c < tw; c++) cols[c] = Math.Clamp(ix - 2 + c, 0, width - 1);
        for (var r = 0; r < th; r++)
        {
            var row = Math.Clamp(iy - 2 + r, 0, height - 1) * width;
            for (var c = 0; c < tw; c++) t[r * tw + c] = src[row + cols[c]];
        }

        int B1(int g) => t[g - 2] - 5 * t[g - 1] + 20 * t[g] + 20 * t[g + 1] - 5 * t[g + 2] + t[g + 3];
        int H1(int g) => t[g - 2 * tw] - 5 * t[g - tw] + 20 * t[g] + 20 * t[g + tw] - 5 * t[g + 2 * tw] + t[g + 3 * tw];
        int J1(int g) => B1(g - 2 * tw) - 5 * B1(g - tw) + 20 * B1(g) + 20 * B1(g + tw) - 5 * B1(g + 2 * tw) + B1(g + 3 * tw);
        static int Clip(int v) => v < 0 ? 0 : v > 255 ? 255 : v;
        int B(int g) => Clip((B1(g) + 16) >> 5);
        int Hh(int g) => Clip((H1(g) + 16) >> 5);
        int J(int g) => Clip((J1(g) + 512) >> 10);

        var width2 = _mbW * 16;
        var sel = fy << 2 | fx;
        for (var y = 0; y < h; y++)
        {
            var d = (py + y) * width2 + px;
            for (var x = 0; x < w; x++)
            {
                var g = (y + 2) * tw + x + 2;
                int v;
                switch (sel)
                {
                    case 0: v = t[g]; break;
                    case 1: v = (t[g] + B(g) + 1) >> 1; break;
                    case 2: v = B(g); break;
                    case 3: v = (t[g + 1] + B(g) + 1) >> 1; break;
                    case 4: v = (t[g] + Hh(g) + 1) >> 1; break;
                    case 5: v = (B(g) + Hh(g) + 1) >> 1; break;
                    case 6: v = (B(g) + J(g) + 1) >> 1; break;
                    case 7: v = (B(g) + Hh(g + 1) + 1) >> 1; break;
                    case 8: v = Hh(g); break;
                    case 9: v = (Hh(g) + J(g) + 1) >> 1; break;
                    case 10: v = J(g); break;
                    case 11: v = (J(g) + Hh(g + 1) + 1) >> 1; break;
                    case 12: v = (t[g + tw] + Hh(g) + 1) >> 1; break;
                    case 13: v = (Hh(g) + B(g + tw) + 1) >> 1; break;
                    case 14: v = (J(g) + B(g + tw) + 1) >> 1; break;
                    default: v = (Hh(g + 1) + B(g + tw) + 1) >> 1; break;
                }
                dst[d + x] = (byte)v;
            }
        }
    }

    private void McChroma(byte[] src, int px, int py, int w, int h, int mvx, int mvy, byte[] dst)
    {
        var width = _mbW * 8; var height = _mbH * 8;
        var ix = px + (mvx >> 3); var iy = py + (mvy >> 3);
        var fx = mvx & 7; var fy = mvy & 7;
        var tw = w + 1;
        var t = _mcTmp;
        var cols = _mcCols;
        for (var c = 0; c < tw; c++) cols[c] = Math.Clamp(ix + c, 0, width - 1);
        for (var r = 0; r <= h; r++)
        {
            var row = Math.Clamp(iy + r, 0, height - 1) * width;
            for (var c = 0; c < tw; c++) t[r * tw + c] = src[row + cols[c]];
        }
        for (var y = 0; y < h; y++)
        {
            var d = (py + y) * width + px;
            for (var x = 0; x < w; x++)
            {
                var g = y * tw + x;
                dst[d + x] = (byte)(((8 - fx) * (8 - fy) * t[g] + fx * (8 - fy) * t[g + 1] + (8 - fx) * fy * t[g + tw] + fx * fy * t[g + tw + 1] + 32) >> 6);
            }
        }
    }
}
