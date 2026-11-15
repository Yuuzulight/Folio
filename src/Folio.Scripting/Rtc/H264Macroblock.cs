namespace Folio.Scripting.Rtc;

/// <summary>Slice data: macroblock parsing (CAVLC), residual reconstruction and motion vector prediction.</summary>
internal sealed partial class H264Decoder
{
    private const byte KindI4 = 1, KindI16 = 2, KindPcm = 3, KindInter = 4;

    private int _mbW, _mbH, _mbCount;
    private int[] _sliceOf = [];
    private byte[] _kind = [], _qp = [], _ipm = [], _tc = [], _tcc = [];
    private short[] _mv = [];
    private sbyte[] _ref = [];
    private int[] _refId = [];
    private readonly List<(int Disable, int Alpha, int Beta)> _slicePar = [];

    private SliceHeader _sh = null!;
    private Picture[] _refList = [];
    private int _curSlice, _qpCur, _mbX, _mbY, _doneMask, _nzLuma, _nzChroma;
    private readonly int[] _lev = new int[16];
    private readonly int[] _coef = new int[256];
    private readonly int[] _coefC = new int[128];
    private readonly int[] _dcY = new int[16];
    private readonly int[] _levelTmp = new int[16];
    private readonly int[] _runTmp = new int[16];
    private readonly int[] _top = new int[9];
    private readonly int[] _left = new int[4];

    private static readonly byte[] PosClass = BuildPosClass();

    private static byte[] BuildPosClass()
    {
        var c = new byte[16];
        for (var p = 0; p < 16; p++)
        {
            var x = p & 3; var y = p >> 2;
            c[p] = (byte)(((x & 1) == 0 && (y & 1) == 0) ? 0 : ((x & 1) == 1 && (y & 1) == 1) ? 1 : 2);
        }
        return c;
    }

    private void AllocatePictureState()
    {
        _mbCount = _mbW * _mbH;
        _sliceOf = new int[_mbCount];
        _kind = new byte[_mbCount];
        _qp = new byte[_mbCount];
        _ipm = new byte[_mbCount * 16];
        _tc = new byte[_mbCount * 16];
        _tcc = new byte[_mbCount * 8];
        _mv = new short[_mbCount * 32];
        _ref = new sbyte[_mbCount * 4];
        _refId = new int[_mbCount * 4];
    }

    private void BeginPictureState()
    {
        Array.Fill(_sliceOf, -1);
        Array.Clear(_kind);
        Array.Clear(_tc);
        Array.Clear(_tcc);
        Array.Clear(_mv);
        Array.Fill(_ref, (sbyte)-1);
        Array.Fill(_refId, -1);
        _slicePar.Clear();
    }

    private void DecodeSliceData(SliceHeader h, H264BitReader br)
    {
        _sh = h;
        if (h.FirstMb >= _mbCount) throw new H264Exception("first_mb_in_slice");
        _refList = h.IsI ? [] : BuildRefList(h);
        _curSlice = _slicePar.Count;
        _slicePar.Add((h.DisableDeblock, h.AlphaOffset, h.BetaOffset));
        _qpCur = h.SliceQp;
        var mb = h.FirstMb;
        var more = true;
        while (true)
        {
            if (!h.IsI)
            {
                var run = br.Ue();
                if (run > _mbCount - mb) throw new H264Exception("mb_skip_run");
                for (var i = 0; i < run; i++) DecodeSkip(mb++);
                if (run > 0) more = br.MoreData;
            }
            if (more)
            {
                if (mb >= _mbCount) throw new H264Exception("slice runs past the picture");
                DecodeMacroblock(mb++, br);
            }
            more = br.MoreData;
            if (!more) break;
        }
    }

    private void BeginMb(int mb)
    {
        Charge(1);
        _sliceOf[mb] = _curSlice;
        _mbX = mb % _mbW;
        _mbY = mb / _mbW;
        _doneMask = 0;
        _nzLuma = _nzChroma = 0;
        Array.Clear(_tc, mb * 16, 16);
        Array.Clear(_tcc, mb * 8, 8);
        Array.Fill(_ipm, (byte)2, mb * 16, 16);
        _qp[mb] = (byte)_qpCur;
    }

    private void SetIntraMotion(int mb)
    {
        Array.Clear(_mv, mb * 32, 32);
        for (var q = 0; q < 4; q++) { _ref[mb * 4 + q] = -1; _refId[mb * 4 + q] = -1; }
    }

    private void DecodeMacroblock(int mb, H264BitReader br)
    {
        BeginMb(mb);
        var type = br.Ue();
        if (!_sh.IsI)
        {
            if (type < 5) { InterMb(mb, type, br); return; }
            type -= 5;
        }
        IntraMb(mb, type, br);
    }

    // ---- intra macroblocks ----

    private bool AvailMb(int nmb) => nmb >= 0 && _sliceOf[nmb] == _curSlice && (!_pps!.ConstrainedIntra || _kind[nmb] != KindInter);

    private int LeftMb(int mb) => _mbX > 0 ? mb - 1 : -1;
    private int TopMb(int mb) => _mbY > 0 ? mb - _mbW : -1;
    private int TopLeftMb(int mb) => _mbX > 0 && _mbY > 0 ? mb - _mbW - 1 : -1;
    private int TopRightMb(int mb) => _mbY > 0 && _mbX < _mbW - 1 ? mb - _mbW + 1 : -1;

    private static int BlkX(int blk) => ((blk >> 2) & 1) * 2 + (blk & 1);
    private static int BlkY(int blk) => ((blk >> 3) & 1) * 2 + ((blk >> 1) & 1);
    private static int BlkIdx(int bx, int by) => (by >> 1) * 8 + (bx >> 1) * 4 + (by & 1) * 2 + (bx & 1);

    private void IntraMb(int mb, int type, H264BitReader br)
    {
        if (type == 25) { Pcm(mb, br); return; }
        if (type > 25) throw new H264Exception("mb_type");
        SetIntraMotion(mb);
        var i16 = type != 0;
        var cbp = 0;
        var i16Mode = 0;
        if (!i16)
        {
            _kind[mb] = KindI4;
            for (var blk = 0; blk < 16; blk++)
            {
                var bx = BlkX(blk); var by = BlkY(blk);
                var pred = PredictIntra4Mode(mb, bx, by);
                int mode;
                if (br.U1() == 1) mode = pred;
                else { var rem = br.U(3); mode = rem < pred ? rem : rem + 1; }
                _ipm[mb * 16 + by * 4 + bx] = (byte)mode;
            }
        }
        else
        {
            _kind[mb] = KindI16;
            i16Mode = (type - 1) & 3;
            cbp = ((type - 1) >> 2) % 3 << 4 | (type >= 13 ? 15 : 0);
        }
        var chromaMode = br.Ue();
        if (chromaMode > 3) throw new H264Exception("intra_chroma_pred_mode");
        if (!i16)
        {
            var code = br.Ue();
            if (code > 47) throw new H264Exception("coded_block_pattern");
            cbp = H264Tables.CbpIntra[code];
        }
        ReadResidual(mb, br, cbp, i16);

        var stride = _mbW * 16;
        var cur = _cur!;
        var x0 = _mbX * 16; var y0 = _mbY * 16;
        if (i16)
        {
            Pred16x16(mb, i16Mode, cur.Y, stride, x0, y0);
            AddLuma(cur.Y, stride, x0, y0);
        }
        else
        {
            for (var blk = 0; blk < 16; blk++)
            {
                var bx = BlkX(blk); var by = BlkY(blk);
                Pred4x4(mb, _ipm[mb * 16 + by * 4 + bx], bx, by, cur.Y, stride, x0 + bx * 4, y0 + by * 4);
                if ((_nzLuma >> blk & 1) != 0) IdctAdd(_coef, blk * 16, cur.Y, (y0 + by * 4) * stride + x0 + bx * 4, stride);
            }
        }
        PredChroma(mb, chromaMode);
        AddChroma();
    }

    private int PredictIntra4Mode(int mb, int bx, int by)
    {
        int modeA, modeB;
        if (bx > 0) modeA = _ipm[mb * 16 + by * 4 + bx - 1];
        else
        {
            var n = LeftMb(mb);
            if (n < 0 || _sliceOf[n] != _curSlice || (_pps!.ConstrainedIntra && _kind[n] == KindInter)) return 2;
            modeA = _ipm[n * 16 + by * 4 + 3];
        }
        if (by > 0) modeB = _ipm[mb * 16 + (by - 1) * 4 + bx];
        else
        {
            var n = TopMb(mb);
            if (n < 0 || _sliceOf[n] != _curSlice || (_pps!.ConstrainedIntra && _kind[n] == KindInter)) return 2;
            modeB = _ipm[n * 16 + 12 + bx];
        }
        return Math.Min(modeA, modeB);
    }

    private void Pcm(int mb, H264BitReader br)
    {
        _kind[mb] = KindPcm;
        SetIntraMotion(mb);
        br.AlignToByte();
        var cur = _cur!;
        var stride = _mbW * 16;
        var cs = stride / 2;
        var tmp = new byte[16];
        for (var r = 0; r < 16; r++)
        {
            br.ReadBytes(tmp, 0, 16);
            Buffer.BlockCopy(tmp, 0, cur.Y, (_mbY * 16 + r) * stride + _mbX * 16, 16);
        }
        for (var c = 0; c < 2; c++)
        {
            var pl = c == 0 ? cur.U : cur.V;
            for (var r = 0; r < 8; r++)
            {
                br.ReadBytes(tmp, 0, 8);
                Buffer.BlockCopy(tmp, 0, pl, (_mbY * 8 + r) * cs + _mbX * 8, 8);
            }
        }
        Array.Fill(_tc, (byte)16, mb * 16, 16);
        Array.Fill(_tcc, (byte)16, mb * 8, 8);
        _qp[mb] = 0;   // deblocking treats I_PCM as QP 0; the running QP predictor is unchanged
    }

    // ---- residual ----

    private int ChromaQp(int qpY)
    {
        var qpi = Math.Clamp(qpY + _pps!.ChromaQpOffset, 0, 51);
        return qpi < 30 ? qpi : H264Tables.ChromaQp[qpi - 30];
    }

    private void ReadResidual(int mb, H264BitReader br, int cbp, bool i16)
    {
        Array.Clear(_coef);
        Array.Clear(_coefC);
        if (cbp != 0 || i16)
        {
            var d = br.Se();
            if (d is < -26 or > 25) throw new H264Exception("mb_qp_delta");
            _qpCur = (_qpCur + d + 52) % 52;
            _qp[mb] = (byte)_qpCur;
        }
        var qp = _qpCur;
        var qpDiv = qp / 6; var qpMod = qp % 6;
        var hasDc = false;
        if (i16)
        {
            var nC = CalcNc(_tc, 16, 0, 4, mb, 0, 0);
            var tc = ReadBlock(br, nC, 16);
            if (tc > 0)
            {
                hasDc = true;
                var m = new int[16];
                for (var k = 0; k < 16; k++) m[H264Tables.Zigzag4[k]] = _lev[k];
                HadamardLumaDc(m, qp);
            }
        }
        var maxNum = i16 ? 15 : 16;
        for (var i8 = 0; i8 < 4; i8++)
        {
            for (var i4 = 0; i4 < 4; i4++)
            {
                var blk = i8 * 4 + i4;
                var bx = BlkX(blk); var by = BlkY(blk);
                if ((cbp & (1 << i8)) != 0)
                {
                    var nC = CalcNc(_tc, 16, 0, 4, mb, bx, by);
                    var tc = ReadBlock(br, nC, maxNum);
                    _tc[mb * 16 + by * 4 + bx] = (byte)tc;
                    if (tc > 0)
                    {
                        _nzLuma |= 1 << blk;
                        var off = i16 ? 1 : 0;
                        for (var k = 0; k < maxNum; k++)
                        {
                            var l = _lev[k];
                            if (l == 0) continue;
                            var pos = H264Tables.Zigzag4[k + off];
                            _coef[blk * 16 + pos] = (l * H264Tables.NormAdjust[qpMod * 3 + PosClass[pos]]) << qpDiv;
                        }
                    }
                }
                if (i16 && hasDc)
                {
                    var dc = _dcY[by * 4 + bx];
                    if (dc != 0) { _coef[blk * 16] = dc; _nzLuma |= 1 << blk; }
                }
            }
        }

        var cbpC = cbp >> 4;
        if (cbpC == 0) return;
        var qpc = ChromaQp(qp);
        var dcC = new int[8];
        for (var c = 0; c < 2; c++)
        {
            var tc = ReadBlock(br, -1, 4);
            if (tc == 0) continue;
            var f0 = _lev[0] + _lev[1] + _lev[2] + _lev[3];
            var f1 = _lev[0] - _lev[1] + _lev[2] - _lev[3];
            var f2 = _lev[0] + _lev[1] - _lev[2] - _lev[3];
            var f3 = _lev[0] - _lev[1] - _lev[2] + _lev[3];
            var scale = 16 * H264Tables.NormAdjust[qpc % 6 * 3];
            dcC[c * 4] = ((f0 * scale) << (qpc / 6)) >> 5;
            dcC[c * 4 + 1] = ((f1 * scale) << (qpc / 6)) >> 5;
            dcC[c * 4 + 2] = ((f2 * scale) << (qpc / 6)) >> 5;
            dcC[c * 4 + 3] = ((f3 * scale) << (qpc / 6)) >> 5;
        }
        var cDiv = qpc / 6; var cMod = qpc % 6;
        for (var c = 0; c < 2; c++)
        {
            for (var blk = 0; blk < 4; blk++)
            {
                var bx = blk & 1; var by = blk >> 1;
                var o = c * 64 + blk * 16;
                if (cbpC == 2)
                {
                    var nC = CalcNc(_tcc, 8, c * 4, 2, mb, bx, by);
                    var tc = ReadBlock(br, nC, 15);
                    _tcc[mb * 8 + c * 4 + blk] = (byte)tc;
                    for (var k = 0; k < 15; k++)
                    {
                        var l = _lev[k];
                        if (l == 0) continue;
                        var pos = H264Tables.Zigzag4[k + 1];
                        _coefC[o + pos] = (l * H264Tables.NormAdjust[cMod * 3 + PosClass[pos]]) << cDiv;
                        _nzChroma |= 1 << (c * 4 + blk);
                    }
                }
                if (dcC[c * 4 + blk] != 0) { _coefC[o] = dcC[c * 4 + blk]; _nzChroma |= 1 << (c * 4 + blk); }
            }
        }
    }

    private void HadamardLumaDc(int[] c, int qp)
    {
        // f = A * c * A with A the 4x4 Hadamard matrix, then scaling (8.5.10).
        var t = new int[16];
        for (var i = 0; i < 4; i++)
        {
            var a = c[i * 4]; var b = c[i * 4 + 1]; var cc = c[i * 4 + 2]; var d = c[i * 4 + 3];
            t[i * 4] = a + b + cc + d;
            t[i * 4 + 1] = a + b - cc - d;
            t[i * 4 + 2] = a - b - cc + d;
            t[i * 4 + 3] = a - b + cc - d;
        }
        var scale = 16 * H264Tables.NormAdjust[qp % 6 * 3];
        var div = qp / 6;
        for (var j = 0; j < 4; j++)
        {
            var a = t[j]; var b = t[4 + j]; var cc = t[8 + j]; var d = t[12 + j];
            Span<int> f = [a + b + cc + d, a + b - cc - d, a - b - cc + d, a - b + cc - d];
            for (var i = 0; i < 4; i++)
                _dcY[i * 4 + j] = qp >= 36 ? (f[i] * scale) << (div - 6) : (f[i] * scale + (1 << (5 - div))) >> (6 - div);
        }
    }

    /// <summary>nC for coeff_token (9.2.1) from the left and upper neighbouring blocks.</summary>
    private int CalcNc(byte[] counts, int mbStride, int planeOffset, int dim, int mb, int bx, int by)
    {
        var availA = true; var availB = true;
        int nA = 0, nB = 0;
        if (bx > 0) nA = counts[mb * mbStride + planeOffset + by * dim + bx - 1];
        else if (_mbX > 0 && _sliceOf[mb - 1] == _curSlice) nA = counts[(mb - 1) * mbStride + planeOffset + by * dim + dim - 1];
        else availA = false;
        if (by > 0) nB = counts[mb * mbStride + planeOffset + (by - 1) * dim + bx];
        else if (_mbY > 0 && _sliceOf[mb - _mbW] == _curSlice) nB = counts[(mb - _mbW) * mbStride + planeOffset + (dim - 1) * dim + bx];
        else availB = false;
        if (availA && availB) return (nA + nB + 1) >> 1;
        if (availA) return nA;
        return availB ? nB : 0;
    }

    /// <summary>Reads one residual_block_cavlc into <see cref="_lev"/> (scan order within the block); returns TotalCoeff.</summary>
    private int ReadBlock(H264BitReader br, int nC, int maxNum)
    {
        var vlc = nC < 0 ? H264Tables.ChromaDcToken : H264Tables.CoeffToken[nC < 2 ? 0 : nC < 4 ? 1 : nC < 8 ? 2 : 3];
        var entry = vlc.Table[br.Peek(vlc.MaxLen)];
        if (entry == 0) throw new H264Exception("coeff_token");
        br.Skip(entry >> 8);
        var tc = (entry & 0xFF) >> 2;
        var t1 = entry & 3;
        Array.Clear(_lev);
        if (tc == 0) return 0;
        if (tc > maxNum) throw new H264Exception("too many coefficients");
        var level = _levelTmp;
        for (var i = 0; i < t1; i++) level[i] = br.U1() == 1 ? -1 : 1;
        var suffixLength = tc > 10 && t1 < 3 ? 1 : 0;
        for (var i = t1; i < tc; i++)
        {
            var prefix = 0;
            while (br.U1() == 0)
            {
                if (++prefix > 15) throw new H264Exception("level_prefix");
            }
            var levelCode = Math.Min(15, prefix) << suffixLength;
            if (suffixLength > 0 || prefix >= 14)
            {
                var size = prefix == 14 && suffixLength == 0 ? 4 : prefix >= 15 ? prefix - 3 : suffixLength;
                if (size > 0) levelCode += br.U(size);
            }
            if (prefix >= 15 && suffixLength == 0) levelCode += 15;
            if (i == t1 && t1 < 3) levelCode += 2;
            var v = (levelCode & 1) == 0 ? (levelCode + 2) >> 1 : (-levelCode - 1) >> 1;
            level[i] = v;
            if (suffixLength == 0) suffixLength = 1;
            if (Math.Abs(v) > (3 << (suffixLength - 1)) && suffixLength < 6) suffixLength++;
        }
        var zerosLeft = 0;
        if (tc < maxNum)
        {
            var tz = nC < 0 ? H264Tables.ChromaDcTotalZeros[tc - 1] : H264Tables.TotalZeros[tc - 1];
            var e = tz.Table[br.Peek(tz.MaxLen)];
            if (e == 0) throw new H264Exception("total_zeros");
            br.Skip(e >> 8);
            zerosLeft = e & 0xFF;
            if (zerosLeft + tc > maxNum) throw new H264Exception("total_zeros out of range");
        }
        var runs = _runTmp;
        for (var i = 0; i < tc - 1; i++)
        {
            var run = 0;
            if (zerosLeft > 0)
            {
                var rv = H264Tables.RunBefore[Math.Min(zerosLeft, 7) - 1];
                var e = rv.Table[br.Peek(rv.MaxLen)];
                if (e == 0) throw new H264Exception("run_before");
                br.Skip(e >> 8);
                run = e & 0xFF;
                if (run > zerosLeft) throw new H264Exception("run_before out of range");
                zerosLeft -= run;
            }
            runs[i] = run;
        }
        runs[tc - 1] = zerosLeft;
        var coeffNum = -1;
        for (var i = tc - 1; i >= 0; i--)
        {
            coeffNum += runs[i] + 1;
            _lev[coeffNum] = level[i];
        }
        return tc;
    }

    private void AddLuma(byte[] pl, int stride, int x0, int y0)
    {
        for (var blk = 0; blk < 16; blk++)
            if ((_nzLuma >> blk & 1) != 0)
                IdctAdd(_coef, blk * 16, pl, (y0 + BlkY(blk) * 4) * stride + x0 + BlkX(blk) * 4, stride);
    }

    private void AddChroma()
    {
        if (_nzChroma == 0) return;
        var cs = _mbW * 8;
        var cur = _cur!;
        for (var c = 0; c < 2; c++)
        {
            var pl = c == 0 ? cur.U : cur.V;
            for (var blk = 0; blk < 4; blk++)
                if ((_nzChroma >> (c * 4 + blk) & 1) != 0)
                    IdctAdd(_coefC, c * 64 + blk * 16, pl, (_mbY * 8 + (blk >> 1) * 4) * cs + _mbX * 8 + (blk & 1) * 4, cs);
        }
    }

    /// <summary>4x4 inverse transform (8.5.12) of already scaled coefficients, added to the prediction in place.</summary>
    private static void IdctAdd(int[] c, int o, byte[] pl, int dst, int stride)
    {
        Span<int> t = stackalloc int[16];
        for (var i = 0; i < 4; i++)
        {
            var d0 = c[o + i * 4]; var d1 = c[o + i * 4 + 1]; var d2 = c[o + i * 4 + 2]; var d3 = c[o + i * 4 + 3];
            var e = d0 + d2; var f = d0 - d2; var g = (d1 >> 1) - d3; var h = d1 + (d3 >> 1);
            t[i * 4] = e + h; t[i * 4 + 1] = f + g; t[i * 4 + 2] = f - g; t[i * 4 + 3] = e - h;
        }
        for (var j = 0; j < 4; j++)
        {
            var d0 = t[j]; var d1 = t[4 + j]; var d2 = t[8 + j]; var d3 = t[12 + j];
            var e = d0 + d2; var f = d0 - d2; var g = (d1 >> 1) - d3; var h = d1 + (d3 >> 1);
            Add(pl, dst + j, (e + h + 32) >> 6);
            Add(pl, dst + stride + j, (f + g + 32) >> 6);
            Add(pl, dst + 2 * stride + j, (f - g + 32) >> 6);
            Add(pl, dst + 3 * stride + j, (e - h + 32) >> 6);
        }
        Array.Clear(c, o, 16);

        static void Add(byte[] p, int i, int r) => p[i] = (byte)Math.Clamp(p[i] + r, 0, 255);
    }

    // ---- inter macroblocks ----

    private int ReadTe(H264BitReader br, int max)
    {
        var v = max == 1 ? 1 - br.U1() : br.Ue();
        if (v > max) throw new H264Exception("ref_idx");
        return v;
    }

    private void SetRef(int mb, int quadrant, int refIdx)
    {
        _ref[mb * 4 + quadrant] = (sbyte)refIdx;
        _refId[mb * 4 + quadrant] = _refList[refIdx].Id;
    }

    private void InterMb(int mb, int type, H264BitReader br)
    {
        _kind[mb] = KindInter;
        var nRef = _sh.NumRefIdx;
        Span<int> subTypes = stackalloc int[4];
        Span<int> refs = stackalloc int[4];
        switch (type)
        {
            case 0:
                refs[0] = nRef > 1 ? ReadTe(br, nRef - 1) : 0;
                for (var q = 0; q < 4; q++) SetRef(mb, q, refs[0]);
                ReadMv(mb, br, 0, 0, 4, 4, refs[0], 0);
                break;
            case 1:
                refs[0] = nRef > 1 ? ReadTe(br, nRef - 1) : 0;
                refs[1] = nRef > 1 ? ReadTe(br, nRef - 1) : 0;
                SetRef(mb, 0, refs[0]); SetRef(mb, 1, refs[0]); SetRef(mb, 2, refs[1]); SetRef(mb, 3, refs[1]);
                ReadMv(mb, br, 0, 0, 4, 2, refs[0], 1);
                ReadMv(mb, br, 0, 2, 4, 2, refs[1], 2);
                break;
            case 2:
                refs[0] = nRef > 1 ? ReadTe(br, nRef - 1) : 0;
                refs[1] = nRef > 1 ? ReadTe(br, nRef - 1) : 0;
                SetRef(mb, 0, refs[0]); SetRef(mb, 2, refs[0]); SetRef(mb, 1, refs[1]); SetRef(mb, 3, refs[1]);
                ReadMv(mb, br, 0, 0, 2, 4, refs[0], 3);
                ReadMv(mb, br, 2, 0, 2, 4, refs[1], 4);
                break;
            case 3 or 4:
                for (var q = 0; q < 4; q++)
                {
                    subTypes[q] = br.Ue();
                    if (subTypes[q] > 3) throw new H264Exception("sub_mb_type");
                }
                for (var q = 0; q < 4; q++)
                {
                    refs[q] = type == 3 && nRef > 1 ? ReadTe(br, nRef - 1) : 0;
                    SetRef(mb, q, refs[q]);
                }
                for (var q = 0; q < 4; q++)
                {
                    var bx = (q & 1) * 2; var by = (q >> 1) * 2;
                    switch (subTypes[q])
                    {
                        case 0: ReadMv(mb, br, bx, by, 2, 2, refs[q], 0); break;
                        case 1: ReadMv(mb, br, bx, by, 2, 1, refs[q], 0); ReadMv(mb, br, bx, by + 1, 2, 1, refs[q], 0); break;
                        case 2: ReadMv(mb, br, bx, by, 1, 2, refs[q], 0); ReadMv(mb, br, bx + 1, by, 1, 2, refs[q], 0); break;
                        default:
                            ReadMv(mb, br, bx, by, 1, 1, refs[q], 0); ReadMv(mb, br, bx + 1, by, 1, 1, refs[q], 0);
                            ReadMv(mb, br, bx, by + 1, 1, 1, refs[q], 0); ReadMv(mb, br, bx + 1, by + 1, 1, 1, refs[q], 0);
                            break;
                    }
                }
                break;
            default:
                throw new H264Exception("mb_type");
        }
        var code = br.Ue();
        if (code > 47) throw new H264Exception("coded_block_pattern");
        var cbp = H264Tables.CbpInter[code];
        ReadResidual(mb, br, cbp, false);
        var cur = _cur!;
        AddLuma(cur.Y, _mbW * 16, _mbX * 16, _mbY * 16);
        AddChroma();
    }

    private void DecodeSkip(int mb)
    {
        BeginMb(mb);
        _kind[mb] = KindInter;
        if (_refList.Length == 0) throw new H264Exception("skipped macroblock without a reference picture");
        for (var q = 0; q < 4; q++) SetRef(mb, q, 0);
        GetNeighbor(mb, _mbX * 4 - 1, _mbY * 4, out var availA, out var refA, out var ax, out var ay);
        GetNeighbor(mb, _mbX * 4, _mbY * 4 - 1, out var availB, out var refB, out var bx, out var by);
        int mx = 0, my = 0;
        if (availA && availB && !(refA == 0 && ax == 0 && ay == 0) && !(refB == 0 && bx == 0 && by == 0))
            PredictMv(mb, 0, 0, 4, 4, 0, 0, out mx, out my);
        StoreMv(mb, 0, 0, 4, 4, mx, my);
        MotionCompensate(mb, 0, 0, 4, 4, 0, mx, my);
    }

    private void ReadMv(int mb, H264BitReader br, int bx, int by, int w4, int h4, int refIdx, int shape)
    {
        PredictMv(mb, bx, by, w4, h4, refIdx, shape, out var px, out var py);
        var dx = br.Se(); var dy = br.Se();
        if (Math.Abs(dx) > 16384 || Math.Abs(dy) > 16384) throw new H264Exception("mvd out of range");
        var mx = px + dx; var my = py + dy;
        if (Math.Abs(mx) > 16384 || Math.Abs(my) > 16384) throw new H264Exception("mv out of range");
        StoreMv(mb, bx, by, w4, h4, mx, my);
        MotionCompensate(mb, bx, by, w4, h4, refIdx, mx, my);
    }

    private void StoreMv(int mb, int bx, int by, int w4, int h4, int mx, int my)
    {
        for (var y = by; y < by + h4; y++)
            for (var x = bx; x < bx + w4; x++)
            {
                var b = y * 4 + x;
                _mv[(mb * 16 + b) * 2] = (short)mx;
                _mv[(mb * 16 + b) * 2 + 1] = (short)my;
                _doneMask |= 1 << b;
            }
    }

    private void GetNeighbor(int mb, int nx, int ny, out bool avail, out int refIdx, out int mvx, out int mvy)
    {
        avail = false; refIdx = -1; mvx = mvy = 0;
        if (nx < 0 || ny < 0 || nx >= _mbW * 4) return;
        var nmb = (ny >> 2) * _mbW + (nx >> 2);
        if (_sliceOf[nmb] != _curSlice) return;
        var b = (ny & 3) * 4 + (nx & 3);
        if (nmb == mb && (_doneMask >> b & 1) == 0) return;
        avail = true;
        refIdx = _ref[nmb * 4 + ((ny & 3) >> 1) * 2 + ((nx & 3) >> 1)];
        mvx = _mv[(nmb * 16 + b) * 2];
        mvy = _mv[(nmb * 16 + b) * 2 + 1];
    }

    /// <summary>Motion vector prediction (8.4.1.3). shape: 0 median, 1/2 upper/lower 16x8, 3/4 left/right 8x16.</summary>
    private void PredictMv(int mb, int bx, int by, int w4, int h4, int refIdx, int shape, out int px, out int py)
    {
        var x = _mbX * 4 + bx; var y = _mbY * 4 + by;
        GetNeighbor(mb, x - 1, y, out var availA, out var refA, out var ax, out var ay);
        GetNeighbor(mb, x, y - 1, out var availB, out var refB, out var bxv, out var byv);
        GetNeighbor(mb, x + w4, y - 1, out var availC, out var refC, out var cx, out var cy);
        if (!availC) GetNeighbor(mb, x - 1, y - 1, out availC, out refC, out cx, out cy);
        switch (shape)
        {
            case 1 when refB == refIdx: px = bxv; py = byv; return;
            case 2 when refA == refIdx: px = ax; py = ay; return;
            case 3 when refA == refIdx: px = ax; py = ay; return;
            case 4 when refC == refIdx: px = cx; py = cy; return;
        }
        if (!availB && !availC && availA)
        {
            bxv = cx = ax; byv = cy = ay; refB = refC = refA;
        }
        var matches = (refA == refIdx ? 1 : 0) + (refB == refIdx ? 1 : 0) + (refC == refIdx ? 1 : 0);
        if (matches == 1)
        {
            if (refA == refIdx) { px = ax; py = ay; }
            else if (refB == refIdx) { px = bxv; py = byv; }
            else { px = cx; py = cy; }
            return;
        }
        px = Median(ax, bxv, cx);
        py = Median(ay, byv, cy);
    }

    private static int Median(int a, int b, int c) => Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
}
