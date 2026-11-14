// Intra and inter prediction, the inverse transforms and the loop filter. Ported from the RFC 6386 reference decoder
// (see Vp8Decoder.cs); the arithmetic is unchanged so the output is bit exact.
namespace Folio.Scripting.Rtc.Vp8;

internal sealed partial class Vp8Decoder
{
    private static readonly short[][] SixTap =
    [
        [0, 0, 128, 0, 0, 0], [0, -6, 123, 12, -1, 0], [2, -11, 108, 36, -8, 1], [0, -9, 93, 50, -6, 0],
        [3, -16, 77, 77, -16, 3], [0, -6, 50, 93, -9, 0], [1, -8, 36, 108, -11, 2], [0, -1, 12, 123, -6, 0],
    ];

    private static readonly short[][] Bilinear =
    [
        [0, 0, 128, 0, 0, 0], [0, 0, 112, 16, 0, 0], [0, 0, 96, 32, 0, 0], [0, 0, 80, 48, 0, 0],
        [0, 0, 64, 64, 0, 0], [0, 0, 48, 80, 0, 0], [0, 0, 32, 96, 0, 0], [0, 0, 16, 112, 0, 0],
    ];

    private static byte Clamp255(int x) => (byte)(x < 0 ? 0 : x > 255 ? 255 : x);

    private void PredictRow(int row)
    {
        var cur = _cur!;
        var yBase = _yOrigin + row * 16 * _yStride;
        var uvBase = _uvOrigin + row * 8 * _uvStride;
        var first = Mi(row, 0);
        FixupLeft(cur.Y, yBase, 16, _yStride, row, _yMode[first]);
        FixupLeft(cur.U, uvBase, 8, _uvStride, row, _uvMode[first]);
        FixupLeft(cur.V, uvBase, 8, _uvStride, row, _uvMode[first]);
        if (row == 0) cur.Y[yBase - _yStride - 1] = 127;
        for (var col = 0; col < _mbCols; col++)
        {
            var mb = Mi(row, col);
            var y = yBase + col * 16;
            var uv = uvBase + col * 8;
            if (row == 0)
            {
                FixupAbove(cur.Y, y, 16, _yStride, col, _yMode[mb]);
                FixupAbove(cur.U, uv, 8, _uvStride, col, _uvMode[mb]);
                FixupAbove(cur.V, uv, 8, _uvStride, col, _uvMode[mb]);
            }
            var coeffs = col * 400;
            if (_yMode[mb] <= BPred)
            {
                PredictIntraLuma(cur.Y, y, _yStride, mb, coeffs);
                PredictIntraChroma(cur.U, cur.V, uv, _uvStride, _uvMode[mb], coeffs);
            }
            else
            {
                if (_yMode[mb] != SplitMv) FixupDcCoeffs(coeffs);
                PredictInter(cur, mb, row, col, coeffs);
            }
        }
        // Extend the last row by four pixels for the intra "above right" of the next row (copied down by the sub-block predictor).
        var extend = yBase + _mbCols * 16 + 15 * _yStride;
        var value = cur.Y[extend - 1];
        for (var i = 0; i < 4; i++) cur.Y[extend + i] = value;
    }

    private static void FixupLeft(byte[] d, int predict, int width, int stride, int row, int mode)
    {
        // The out-of-frame left column is 129, except for DC prediction below the first row, which mirrors the above row.
        if (mode == DcPred && row != 0)
        {
            for (var i = 0; i < width; i++) d[predict - 1 + i * stride] = d[predict - stride + i];
        }
        else
        {
            for (var i = -1; i < width; i++) d[predict - 1 + i * stride] = 129;
        }
    }

    private static void FixupAbove(byte[] d, int predict, int width, int stride, int col, int mode)
    {
        var above = predict - stride;
        if (mode == DcPred && col != 0)
        {
            for (var i = 0; i < width; i++) d[above + i] = d[predict - 1 + i * stride];
        }
        else
        {
            for (var i = -1; i < width; i++) d[above + i] = 127;
        }
        for (var i = 0; i < 4; i++) d[above + width + i] = 127;
    }

    // ---- intra ----------------------------------------------------------------------------------------------------

    private void FixupDcCoeffs(int coeffs)
    {
        Span<short> y2 = stackalloc short[16];
        Walsh(_coeffs, coeffs + 24 * 16, y2);
        for (var i = 0; i < 16; i++) _coeffs[coeffs + i * 16] = y2[i];
    }

    private static void Walsh(short[] input, int offset, Span<short> output)
    {
        for (var i = 0; i < 4; i++)
        {
            int a1 = input[offset + i] + input[offset + 12 + i];
            int b1 = input[offset + 4 + i] + input[offset + 8 + i];
            int c1 = input[offset + 4 + i] - input[offset + 8 + i];
            int d1 = input[offset + i] - input[offset + 12 + i];
            output[i] = (short)(a1 + b1);
            output[4 + i] = (short)(c1 + d1);
            output[8 + i] = (short)(a1 - b1);
            output[12 + i] = (short)(d1 - c1);
        }
        for (var i = 0; i < 4; i++)
        {
            var o = i * 4;
            int a1 = output[o] + output[o + 3];
            int b1 = output[o + 1] + output[o + 2];
            int c1 = output[o + 1] - output[o + 2];
            int d1 = output[o] - output[o + 3];
            int a2 = a1 + b1, b2 = c1 + d1, c2 = a1 - b1, d2 = d1 - c1;
            output[o] = (short)((a2 + 3) >> 3);
            output[o + 1] = (short)((b2 + 3) >> 3);
            output[o + 2] = (short)((c2 + 3) >> 3);
            output[o + 3] = (short)((d2 + 3) >> 3);
        }
    }

    private void IdctAdd(byte[] d, int pos, int stride, int coeffs) => IdctAdd(d, pos, d, pos, stride, coeffs);

    private void IdctAdd(byte[] recon, int reconPos, byte[] predict, int predictPos, int stride, int coeffs)
    {
        var c = _coeffs;
        var any = false;
        for (var i = 0; i < 16; i++) if (c[coeffs + i] != 0) { any = true; break; }
        if (!any)
        {
            if (!ReferenceEquals(recon, predict) || reconPos != predictPos)
                for (var r = 0; r < 4; r++) Buffer.BlockCopy(predict, predictPos + r * stride, recon, reconPos + r * stride, 4);
            return;
        }
        Span<short> tmp = stackalloc short[16];
        for (var i = 0; i < 4; i++)
        {
            int a1 = c[coeffs + i] + c[coeffs + 8 + i];
            int b1 = c[coeffs + i] - c[coeffs + 8 + i];
            int temp1 = (c[coeffs + 4 + i] * 35468) >> 16;
            int temp2 = c[coeffs + 12 + i] + ((c[coeffs + 12 + i] * 20091) >> 16);
            int c1 = temp1 - temp2;
            temp1 = c[coeffs + 4 + i] + ((c[coeffs + 4 + i] * 20091) >> 16);
            temp2 = (c[coeffs + 12 + i] * 35468) >> 16;
            int d1 = temp1 + temp2;
            tmp[i] = (short)(a1 + d1);
            tmp[12 + i] = (short)(a1 - d1);
            tmp[4 + i] = (short)(b1 + c1);
            tmp[8 + i] = (short)(b1 - c1);
        }
        for (var i = 0; i < 4; i++)
        {
            var o = i * 4;
            int a1 = tmp[o] + tmp[o + 2];
            int b1 = tmp[o] - tmp[o + 2];
            int temp1 = (tmp[o + 1] * 35468) >> 16;
            int temp2 = tmp[o + 3] + ((tmp[o + 3] * 20091) >> 16);
            int c1 = temp1 - temp2;
            temp1 = tmp[o + 1] + ((tmp[o + 1] * 20091) >> 16);
            temp2 = (tmp[o + 3] * 35468) >> 16;
            int d1 = temp1 + temp2;
            var p = predictPos + i * stride; var r = reconPos + i * stride;
            recon[r] = Clamp255(predict[p] + ((a1 + d1 + 4) >> 3));
            recon[r + 3] = Clamp255(predict[p + 3] + ((a1 - d1 + 4) >> 3));
            recon[r + 1] = Clamp255(predict[p + 1] + ((b1 + c1 + 4) >> 3));
            recon[r + 2] = Clamp255(predict[p + 2] + ((b1 - c1 + 4) >> 3));
        }
    }

    private void PredictIntraLuma(byte[] d, int pos, int stride, int mb, int coeffs)
    {
        if (_yMode[mb] == BPred)
        {
            BPredict(d, pos, stride, mb, coeffs);
            return;
        }
        switch (_yMode[mb])
        {
            case DcPred: PredictDc(d, pos, stride, 16); break;
            case VPred: PredictV(d, pos, stride, 16); break;
            case HPred: PredictH(d, pos, stride, 16); break;
            default: PredictTm(d, pos, stride, 16); break;
        }
        FixupDcCoeffs(coeffs);
        for (var i = 0; i < 16; i++)
            IdctAdd(d, pos + (i >> 2) * 4 * stride + (i & 3) * 4, stride, coeffs + i * 16);
    }

    private void PredictIntraChroma(byte[] u, byte[] v, int pos, int stride, int mode, int coeffs)
    {
        switch (mode)
        {
            case DcPred: PredictDc(u, pos, stride, 8); PredictDc(v, pos, stride, 8); break;
            case VPred: PredictV(u, pos, stride, 8); PredictV(v, pos, stride, 8); break;
            case HPred: PredictH(u, pos, stride, 8); PredictH(v, pos, stride, 8); break;
            default: PredictTm(u, pos, stride, 8); PredictTm(v, pos, stride, 8); break;
        }
        for (var i = 0; i < 4; i++)
        {
            var o = pos + (i >> 1) * 4 * stride + (i & 1) * 4;
            IdctAdd(u, o, stride, coeffs + (16 + i) * 16);
            IdctAdd(v, o, stride, coeffs + (20 + i) * 16);
        }
    }

    private static void PredictH(byte[] d, int pos, int stride, int n)
    {
        for (var i = 0; i < n; i++) { var left = d[pos - 1 + i * stride]; for (var j = 0; j < n; j++) d[pos + i * stride + j] = left; }
    }

    private static void PredictV(byte[] d, int pos, int stride, int n)
    {
        for (var i = 0; i < n; i++) for (var j = 0; j < n; j++) d[pos + i * stride + j] = d[pos - stride + j];
    }

    private static void PredictTm(byte[] d, int pos, int stride, int n)
    {
        int p = d[pos - stride - 1];
        for (var j = 0; j < n; j++)
        {
            int left = d[pos - 1 + j * stride];
            for (var i = 0; i < n; i++) d[pos + j * stride + i] = Clamp255(left + d[pos - stride + i] - p);
        }
    }

    private static void PredictDc(byte[] d, int pos, int stride, int n)
    {
        var dc = 0;
        for (var i = 0; i < n; i++) dc += d[pos - 1 + i * stride] + d[pos - stride + i];
        dc = n switch { 16 => (dc + 16) >> 5, 8 => (dc + 8) >> 4, _ => (dc + 4) >> 3 };
        for (var i = 0; i < n; i++) for (var j = 0; j < n; j++) d[pos + i * stride + j] = (byte)dc;
    }

    private void BPredict(byte[] d, int pos, int stride, int mb, int coeffs)
    {
        // The four pixels above and to the right of the macroblock serve sub-blocks 3, 7, 11 and 15.
        Span<int> a = stackalloc int[8];
        Span<int> ar = stackalloc int[4];
        for (var i = 0; i < 4; i++) ar[i] = d[pos - stride + 16 + i];
        for (var i = 0; i < 16; i++)
        {
            var o = pos + (i >> 2) * 4 * stride + (i & 3) * 4;
            var column = i & 3;
            for (var k = 0; k < 4; k++) a[k] = d[o - stride + k];
            for (var k = 0; k < 4; k++) a[4 + k] = column == 3 ? ar[k] : d[o - stride + 4 + k];
            int p = d[o - stride - 1];
            int l0 = d[o - 1], l1 = d[o - 1 + stride], l2 = d[o - 1 + 2 * stride], l3 = d[o - 1 + 3 * stride];
            switch (_bModes[mb * 16 + i])
            {
                case BDc:
                {
                    var dc = (a[0] + a[1] + a[2] + a[3] + l0 + l1 + l2 + l3 + 4) >> 3;
                    for (var r = 0; r < 4; r++) for (var c = 0; c < 4; c++) d[o + r * stride + c] = (byte)dc;
                    break;
                }
                case BTm:
                {
                    Span<int> left = [l0, l1, l2, l3];
                    for (var r = 0; r < 4; r++) for (var c = 0; c < 4; c++) d[o + r * stride + c] = Clamp255(left[r] + a[c] - p);
                    break;
                }
                case BVe:
                    for (var c = 0; c < 4; c++)
                    {
                        var prev = c == 0 ? p : a[c - 1];
                        var v = (byte)((prev + 2 * a[c] + a[c + 1] + 2) >> 2);
                        for (var r = 0; r < 4; r++) d[o + r * stride + c] = v;
                    }
                    break;
                case BHe:
                    Fill4(d, o, 0, stride, (p + 2 * l0 + l1 + 2) >> 2);
                    Fill4(d, o, 1, stride, (l0 + 2 * l1 + l2 + 2) >> 2);
                    Fill4(d, o, 2, stride, (l1 + 2 * l2 + l3 + 2) >> 2);
                    Fill4(d, o, 3, stride, (l2 + 2 * l3 + l3 + 2) >> 2);
                    break;
                case BLd:
                {
                    Span<int> pr = stackalloc int[7];
                    for (var k = 0; k < 6; k++) pr[k] = (a[k] + 2 * a[k + 1] + a[k + 2] + 2) >> 2;
                    pr[6] = (a[6] + 2 * a[7] + a[7] + 2) >> 2;
                    for (var r = 0; r < 4; r++) for (var c = 0; c < 4; c++) d[o + r * stride + c] = (byte)pr[r + c];
                    break;
                }
                case BRd:
                {
                    var p0 = (l0 + 2 * p + a[0] + 2) >> 2; var p1 = (p + 2 * a[0] + a[1] + 2) >> 2;
                    var p2 = (a[0] + 2 * a[1] + a[2] + 2) >> 2; var p3 = (a[1] + 2 * a[2] + a[3] + 2) >> 2;
                    var p4 = (l1 + 2 * l0 + p + 2) >> 2; var p5 = (l2 + 2 * l1 + l0 + 2) >> 2; var p6 = (l3 + 2 * l2 + l1 + 2) >> 2;
                    Put4(d, o, 0, stride, p0, p1, p2, p3);
                    Put4(d, o, 1, stride, p4, p0, p1, p2);
                    Put4(d, o, 2, stride, p5, p4, p0, p1);
                    Put4(d, o, 3, stride, p6, p5, p4, p0);
                    break;
                }
                case BVr:
                {
                    var p0 = (p + a[0] + 1) >> 1; var p1 = (a[0] + a[1] + 1) >> 1; var p2 = (a[1] + a[2] + 1) >> 1; var p3 = (a[2] + a[3] + 1) >> 1;
                    var p4 = (l0 + 2 * p + a[0] + 2) >> 2; var p5 = (p + 2 * a[0] + a[1] + 2) >> 2;
                    var p6 = (a[0] + 2 * a[1] + a[2] + 2) >> 2; var p7 = (a[1] + 2 * a[2] + a[3] + 2) >> 2;
                    var p8 = (l1 + 2 * l0 + p + 2) >> 2; var p9 = (l2 + 2 * l1 + l0 + 2) >> 2;
                    Put4(d, o, 0, stride, p0, p1, p2, p3);
                    Put4(d, o, 1, stride, p4, p5, p6, p7);
                    Put4(d, o, 2, stride, p8, p0, p1, p2);
                    Put4(d, o, 3, stride, p9, p4, p5, p6);
                    break;
                }
                case BVl:
                {
                    var p0 = (a[0] + a[1] + 1) >> 1; var p1 = (a[1] + a[2] + 1) >> 1; var p2 = (a[2] + a[3] + 1) >> 1; var p3 = (a[3] + a[4] + 1) >> 1;
                    var p4 = (a[0] + 2 * a[1] + a[2] + 2) >> 2; var p5 = (a[1] + 2 * a[2] + a[3] + 2) >> 2;
                    var p6 = (a[2] + 2 * a[3] + a[4] + 2) >> 2; var p7 = (a[3] + 2 * a[4] + a[5] + 2) >> 2;
                    var p8 = (a[4] + 2 * a[5] + a[6] + 2) >> 2; var p9 = (a[5] + 2 * a[6] + a[7] + 2) >> 2;
                    Put4(d, o, 0, stride, p0, p1, p2, p3);
                    Put4(d, o, 1, stride, p4, p5, p6, p7);
                    Put4(d, o, 2, stride, p1, p2, p3, p8);
                    Put4(d, o, 3, stride, p5, p6, p7, p9);
                    break;
                }
                case BHd:
                {
                    var p0 = (l0 + p + 1) >> 1; var p1 = (l0 + 2 * p + a[0] + 2) >> 2; var p2 = (p + 2 * a[0] + a[1] + 2) >> 2; var p3 = (a[0] + 2 * a[1] + a[2] + 2) >> 2;
                    var p4 = (l1 + l0 + 1) >> 1; var p5 = (l1 + 2 * l0 + p + 2) >> 2;
                    var p6 = (l2 + l1 + 1) >> 1; var p7 = (l2 + 2 * l1 + l0 + 2) >> 2;
                    var p8 = (l3 + l2 + 1) >> 1; var p9 = (l3 + 2 * l2 + l1 + 2) >> 2;
                    Put4(d, o, 0, stride, p0, p1, p2, p3);
                    Put4(d, o, 1, stride, p4, p5, p0, p1);
                    Put4(d, o, 2, stride, p6, p7, p4, p5);
                    Put4(d, o, 3, stride, p8, p9, p6, p7);
                    break;
                }
                default: // BHu
                {
                    var p0 = (l0 + l1 + 1) >> 1; var p1 = (l0 + 2 * l1 + l2 + 2) >> 2; var p2 = (l1 + l2 + 1) >> 1; var p3 = (l1 + 2 * l2 + l3 + 2) >> 2;
                    var p4 = (l2 + l3 + 1) >> 1; var p5 = (l2 + 2 * l3 + l3 + 2) >> 2; var p6 = l3;
                    Put4(d, o, 0, stride, p0, p1, p2, p3);
                    Put4(d, o, 1, stride, p2, p3, p4, p5);
                    Put4(d, o, 2, stride, p4, p5, p6, p6);
                    Put4(d, o, 3, stride, p6, p6, p6, p6);
                    break;
                }
            }
            IdctAdd(d, o, stride, coeffs + i * 16);
        }
    }

    private static void Fill4(byte[] d, int o, int row, int stride, int value)
    {
        var b = (byte)value;
        for (var c = 0; c < 4; c++) d[o + row * stride + c] = b;
    }

    private static void Put4(byte[] d, int o, int row, int stride, int a, int b, int c, int e)
    {
        var p = o + row * stride;
        d[p] = (byte)a; d[p + 1] = (byte)b; d[p + 2] = (byte)c; d[p + 3] = (byte)e;
    }

    // ---- inter ----------------------------------------------------------------------------------------------------

    private void PredictInter(Frame cur, int mb, int row, int col, int coeffs)
    {
        var reference = _refFrame[mb] switch { 1 => _last!, 2 => _golden!, _ => _altref! };
        var fullPixel = _version == 3;
        var split = _yMode[mb] == SplitMv;
        Span<int> chromaX = stackalloc int[4];
        Span<int> chromaY = stackalloc int[4];
        if (!split)
        {
            int ux = _mvX[mb], uy = _mvY[mb];
            ux = (ux + 1 + (ux >> 31) * 2) / 2;
            uy = (uy + 1 + (uy >> 31) * 2) / 2;
            if (fullPixel) { ux &= ~7; uy &= ~7; }
            for (var i = 0; i < 4; i++) { chromaX[i] = ux; chromaY[i] = uy; }
        }
        else
        {
            var baseIndex = mb * 16;
            for (var q = 0; q < 4; q++)
            {
                var b = (q & 1) * 2 + (q >> 1) * 8;
                var tx = _subMvX[baseIndex + b] + _subMvX[baseIndex + b + 1] + _subMvX[baseIndex + b + 4] + _subMvX[baseIndex + b + 5];
                var ty = _subMvY[baseIndex + b] + _subMvY[baseIndex + b + 1] + _subMvY[baseIndex + b + 4] + _subMvY[baseIndex + b + 5];
                tx = (tx < 0 ? tx - 4 : tx + 4) / 8;
                ty = (ty < 0 ? ty - 4 : ty + 4) / 8;
                if (fullPixel) { tx &= ~7; ty &= ~7; }
                chromaX[q] = (short)tx; chromaY[q] = (short)ty;
            }
        }
        var x = col * 16; var y = row * 16;
        var pw = _mbCols * 16; var ph = _mbRows * 16;
        if (!split)
        {
            PredictBlock(reference.Y, cur.Y, _yOrigin, _yStride, pw, ph, x, y, 16, 16, _mvX[mb], _mvY[mb]);
            for (var i = 0; i < 16; i++)
                IdctAdd(cur.Y, _yOrigin + (y + (i >> 2) * 4) * _yStride + x + (i & 3) * 4, _yStride, coeffs + i * 16);
            PredictBlock(reference.U, cur.U, _uvOrigin, _uvStride, pw >> 1, ph >> 1, x >> 1, y >> 1, 8, 8, chromaX[0], chromaY[0]);
            PredictBlock(reference.V, cur.V, _uvOrigin, _uvStride, pw >> 1, ph >> 1, x >> 1, y >> 1, 8, 8, chromaX[0], chromaY[0]);
        }
        else
        {
            for (var b = 0; b < 16; b++)
            {
                var bx = x + (b & 3) * 4; var by = y + (b >> 2) * 4;
                PredictBlock(reference.Y, cur.Y, _yOrigin, _yStride, pw, ph, bx, by, 4, 4, _subMvX[mb * 16 + b], _subMvY[mb * 16 + b]);
                IdctAdd(cur.Y, _yOrigin + by * _yStride + bx, _yStride, coeffs + b * 16);
            }
            for (var q = 0; q < 4; q++)
            {
                var bx = (x >> 1) + (q & 1) * 4; var by = (y >> 1) + (q >> 1) * 4;
                PredictBlock(reference.U, cur.U, _uvOrigin, _uvStride, pw >> 1, ph >> 1, bx, by, 4, 4, chromaX[q], chromaY[q]);
                PredictBlock(reference.V, cur.V, _uvOrigin, _uvStride, pw >> 1, ph >> 1, bx, by, 4, 4, chromaX[q], chromaY[q]);
            }
        }
        for (var q = 0; q < 4; q++)
        {
            var bx = (x >> 1) + (q & 1) * 4; var by = (y >> 1) + (q >> 1) * 4;
            var o = _uvOrigin + by * _uvStride + bx;
            IdctAdd(cur.U, o, _uvStride, coeffs + (16 + q) * 16);
            IdctAdd(cur.V, o, _uvStride, coeffs + (20 + q) * 16);
        }
    }

    /// <summary>Predicts a block from a reference plane. Coordinates outside the plane clamp to its edge, which is what the
    /// reference decoder's border emulation produces; mv components are in eighth pixels.</summary>
    private void PredictBlock(byte[] src, byte[] dst, int origin, int stride, int planeW, int planeH, int bx, int by, int bw, int bh, int mvx, int mvy)
    {
        var sx = bx + (mvx >> 3);
        var sy = by + (mvy >> 3);
        var fx = mvx & 7; var fy = mvy & 7;
        var dpos = origin + by * stride + bx;
        var ww = bw + 5; var wh = bh + 5;
        Span<byte> window = stackalloc byte[21 * 21];
        // Window covers columns sx-2 .. sx+bw+2 and rows sy-2 .. sy+bh+2; columns and rows are clamped into the plane.
        for (var r = 0; r < wh; r++)
        {
            var yy = sy - 2 + r; yy = yy < 0 ? 0 : yy >= planeH ? planeH - 1 : yy;
            var rowPos = origin + yy * stride;
            var x0 = sx - 2;
            if (x0 >= 0 && x0 + ww <= planeW) src.AsSpan(rowPos + x0, ww).CopyTo(window.Slice(r * ww, ww));
            else
                for (var c = 0; c < ww; c++)
                {
                    var xx = x0 + c; xx = xx < 0 ? 0 : xx >= planeW ? planeW - 1 : xx;
                    window[r * ww + c] = src[rowPos + xx];
                }
        }
        if ((fx | fy) == 0)
        {
            for (var r = 0; r < bh; r++) window.Slice((r + 2) * ww + 2, bw).CopyTo(dst.AsSpan(dpos + r * stride, bw));
            return;
        }
        var filters = _version == 0 ? SixTap : Bilinear;
        Span<byte> mid = stackalloc byte[21 * 16];
        var hf = filters[fx];
        for (var r = 0; r < wh; r++)
            for (var c = 0; c < bw; c++)
            {
                var w = r * ww + c;
                var t = window[w] * hf[0] + window[w + 1] * hf[1] + window[w + 2] * hf[2] + window[w + 3] * hf[3] + window[w + 4] * hf[4] + window[w + 5] * hf[5] + 64;
                mid[r * bw + c] = Clamp255(t >> 7);
            }
        var vf = filters[fy];
        for (var r = 0; r < bh; r++)
            for (var c = 0; c < bw; c++)
            {
                var m = r * bw + c;
                var t = mid[m] * vf[0] + mid[m + bw] * vf[1] + mid[m + 2 * bw] * vf[2] + mid[m + 3 * bw] * vf[3] + mid[m + 4 * bw] * vf[4] + mid[m + 5 * bw] * vf[5] + 64;
                dst[dpos + r * stride + c] = Clamp255(t >> 7);
            }
    }

    // ---- loop filter ----------------------------------------------------------------------------------------------

    private void LoopFilterFrame()
    {
        var cur = _cur!;
        for (var row = 0; row < _mbRows; row++)
            for (var col = 0; col < _mbCols; col++)
            {
                var mb = Mi(row, col);
                var level = _lfLevel;
                if (_segEnabled)
                {
                    var seg = _segmentId[mb] & 3;
                    level = !_segAbs ? level + _segLf[seg] : _segLf[seg];
                }
                level = level > 63 ? 63 : level < 0 ? 0 : level;
                if (_lfDeltaEnabled)
                {
                    level += _lfRefDelta[_refFrame[mb]];
                    if (_refFrame[mb] == 0) { if (_yMode[mb] == BPred) level += _lfModeDelta[0]; }
                    else if (_yMode[mb] == ZeroMv) level += _lfModeDelta[1];
                    else if (_yMode[mb] == SplitMv) level += _lfModeDelta[3];
                    else level += _lfModeDelta[2];
                    level = level > 63 ? 63 : level < 0 ? 0 : level;
                }
                var interior = level;
                if (_lfSharpness != 0)
                {
                    interior >>= _lfSharpness > 4 ? 2 : 1;
                    if (interior > 9 - _lfSharpness) interior = 9 - _lfSharpness;
                }
                if (interior < 1) interior = 1;
                var hev = level >= 15 ? 1 : 0;
                if (level >= 40) hev++;
                if (level >= 20 && !_isKey) hev++;
                if (level == 0) continue;
                var filterInner = _eobMask[mb] != 0 || _yMode[mb] == SplitMv || _yMode[mb] == BPred;
                var y = _yOrigin + row * 16 * _yStride + col * 16;
                var uv = _uvOrigin + row * 8 * _uvStride + col * 8;
                if (_lfSimple)
                {
                    var mbLimit = (level + 2) * 2 + interior;
                    var bLimit = level * 2 + interior;
                    if (col != 0) SimpleEdge(cur.Y, y, 1, _yStride, mbLimit);
                    if (filterInner) { SimpleEdge(cur.Y, y + 4, 1, _yStride, bLimit); SimpleEdge(cur.Y, y + 8, 1, _yStride, bLimit); SimpleEdge(cur.Y, y + 12, 1, _yStride, bLimit); }
                    if (row != 0) SimpleEdge(cur.Y, y, _yStride, 1, mbLimit);
                    if (filterInner) { SimpleEdge(cur.Y, y + 4 * _yStride, _yStride, 1, bLimit); SimpleEdge(cur.Y, y + 8 * _yStride, _yStride, 1, bLimit); SimpleEdge(cur.Y, y + 12 * _yStride, _yStride, 1, bLimit); }
                    continue;
                }
                if (col != 0)
                {
                    MbEdge(cur.Y, y, 1, _yStride, level + 2, interior, hev, 16);
                    MbEdge(cur.U, uv, 1, _uvStride, level + 2, interior, hev, 8);
                    MbEdge(cur.V, uv, 1, _uvStride, level + 2, interior, hev, 8);
                }
                if (filterInner)
                {
                    SubEdge(cur.Y, y + 4, 1, _yStride, level, interior, hev, 16);
                    SubEdge(cur.Y, y + 8, 1, _yStride, level, interior, hev, 16);
                    SubEdge(cur.Y, y + 12, 1, _yStride, level, interior, hev, 16);
                    SubEdge(cur.U, uv + 4, 1, _uvStride, level, interior, hev, 8);
                    SubEdge(cur.V, uv + 4, 1, _uvStride, level, interior, hev, 8);
                }
                if (row != 0)
                {
                    MbEdge(cur.Y, y, _yStride, 1, level + 2, interior, hev, 16);
                    MbEdge(cur.U, uv, _uvStride, 1, level + 2, interior, hev, 8);
                    MbEdge(cur.V, uv, _uvStride, 1, level + 2, interior, hev, 8);
                }
                if (filterInner)
                {
                    SubEdge(cur.Y, y + 4 * _yStride, _yStride, 1, level, interior, hev, 16);
                    SubEdge(cur.Y, y + 8 * _yStride, _yStride, 1, level, interior, hev, 16);
                    SubEdge(cur.Y, y + 12 * _yStride, _yStride, 1, level, interior, hev, 16);
                    SubEdge(cur.U, uv + 4 * _uvStride, _uvStride, 1, level, interior, hev, 8);
                    SubEdge(cur.V, uv + 4 * _uvStride, _uvStride, 1, level, interior, hev, 8);
                }
            }
    }

    private static int Sat8(int x) => x < -128 ? -128 : x > 127 ? 127 : x;

    // step: distance between the pixels across the edge (1 for a vertical edge, stride for a horizontal one);
    // along: distance between successive positions along the edge.
    private static bool Simple(byte[] b, int o, int step, int limit) =>
        Math.Abs(b[o - step] - b[o]) * 2 + (Math.Abs(b[o - 2 * step] - b[o + step]) >> 1) <= limit;

    private static bool Normal(byte[] b, int o, int step, int edge, int interior) =>
        Simple(b, o, step, 2 * edge + interior)
        && Math.Abs(b[o - 4 * step] - b[o - 3 * step]) <= interior && Math.Abs(b[o - 3 * step] - b[o - 2 * step]) <= interior
        && Math.Abs(b[o - 2 * step] - b[o - step]) <= interior && Math.Abs(b[o + 3 * step] - b[o + 2 * step]) <= interior
        && Math.Abs(b[o + 2 * step] - b[o + step]) <= interior && Math.Abs(b[o + step] - b[o]) <= interior;

    private static bool HighEdgeVariance(byte[] b, int o, int step, int threshold) =>
        Math.Abs(b[o - 2 * step] - b[o - step]) > threshold || Math.Abs(b[o + step] - b[o]) > threshold;

    private static void FilterCommon(byte[] b, int o, int step, bool outerTaps)
    {
        int p1 = b[o - 2 * step], p0 = b[o - step], q0 = b[o], q1 = b[o + step];
        var a = 3 * (q0 - p0);
        if (outerTaps) a += Sat8(p1 - q1);
        a = Sat8(a);
        var f1 = (a + 4 > 127 ? 127 : a + 4) >> 3;
        var f2 = (a + 3 > 127 ? 127 : a + 3) >> 3;
        b[o - step] = Clamp255(p0 + f2);
        b[o] = Clamp255(q0 - f1);
        if (!outerTaps)
        {
            a = (f1 + 1) >> 1;
            b[o - 2 * step] = Clamp255(p1 + a);
            b[o + step] = Clamp255(q1 - a);
        }
    }

    private static void FilterMbEdge(byte[] b, int o, int step)
    {
        int p2 = b[o - 3 * step], p1 = b[o - 2 * step], p0 = b[o - step], q0 = b[o], q1 = b[o + step], q2 = b[o + 2 * step];
        var w = Sat8(Sat8(p1 - q1) + 3 * (q0 - p0));
        var a = (27 * w + 63) >> 7;
        b[o - step] = Clamp255(p0 + a); b[o] = Clamp255(q0 - a);
        a = (18 * w + 63) >> 7;
        b[o - 2 * step] = Clamp255(p1 + a); b[o + step] = Clamp255(q1 - a);
        a = (9 * w + 63) >> 7;
        b[o - 3 * step] = Clamp255(p2 + a); b[o + 2 * step] = Clamp255(q2 - a);
    }

    private static void MbEdge(byte[] b, int o, int step, int along, int edge, int interior, int hev, int count)
    {
        for (var i = 0; i < count; i++, o += along)
        {
            if (!Normal(b, o, step, edge, interior)) continue;
            if (HighEdgeVariance(b, o, step, hev)) FilterCommon(b, o, step, true);
            else FilterMbEdge(b, o, step);
        }
    }

    private static void SubEdge(byte[] b, int o, int step, int along, int edge, int interior, int hev, int count)
    {
        for (var i = 0; i < count; i++, o += along)
            if (Normal(b, o, step, edge, interior)) FilterCommon(b, o, step, HighEdgeVariance(b, o, step, hev));
    }

    private static void SimpleEdge(byte[] b, int o, int step, int along, int limit)
    {
        for (var i = 0; i < 16; i++, o += along)
            if (Simple(b, o, step, limit)) FilterCommon(b, o, step, true);
    }
}
