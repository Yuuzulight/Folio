// Mode and motion vector parsing and DCT token decoding. Ported from the RFC 6386 reference decoder (see Vp8Decoder.cs).
namespace Folio.Scripting.Rtc.Vp8;

internal sealed partial class Vp8Decoder
{
    private const int DcPred = 0, VPred = 1, HPred = 2, TmPred = 3, BPred = 4, NearestMv = 5, NearMv = 6, ZeroMv = 7, NewMv = 8, SplitMv = 9;
    private const int Left4x4 = 10, Above4x4 = 11, Zero4x4 = 12, New4x4 = 13;
    private const int BDc = 0, BTm = 1, BVe = 2, BHe = 3, BLd = 4, BRd = 5, BVr = 6, BVl = 7, BHd = 8, BHu = 9;

    private static readonly int[] KfYModeTree = [-BPred, 2, 4, 6, -DcPred, -VPred, -HPred, -TmPred];
    private static readonly int[] YModeTree = [-DcPred, 2, 4, 6, -VPred, -HPred, -TmPred, -BPred];
    private static readonly int[] UvModeTree = [-DcPred, 2, -VPred, 4, -HPred, -TmPred];
    private static readonly int[] BModeTree = [-BDc, 2, -BTm, 4, -BVe, 6, 8, 12, -BHe, 10, -BRd, -BVr, -BLd, 14, -BVl, 16, -BHd, -BHu];
    private static readonly int[] SmallMvTree = [2, 8, 4, 6, -0, -1, -2, -3, 10, 12, -4, -5, -6, -7];
    private static readonly int[] MvRefTree = [-ZeroMv, 2, -NearestMv, 4, -NearMv, 6, -NewMv, -SplitMv];
    private static readonly int[] SubMvRefTree = [-Left4x4, 2, -Above4x4, 4, -Zero4x4, -New4x4];
    private static readonly int[] SplitMvTree = [-3, 2, -2, 4, -0, -1];

    private static readonly byte[] KfYModeProbs = [145, 156, 163, 128];
    private static readonly byte[] KfUvModeProbs = [142, 114, 183];
    private static readonly byte[] DefaultBModeProbs = [120, 90, 79, 133, 87, 85, 80, 111, 151];
    private static readonly byte[][] MvCountsToProbs =
    [
        [7, 1, 1, 143], [14, 18, 14, 107], [135, 64, 57, 68], [60, 56, 128, 65], [159, 134, 128, 34], [234, 188, 128, 28],
    ];
    private static readonly byte[] SplitMvProbs = [110, 111, 150];
    private static readonly byte[][] SubMvRefProbs =
    [
        [147, 136, 18], [106, 145, 1], [179, 121, 1], [223, 1, 34], [208, 1, 1],
    ];
    private static readonly int[][] MvPartitions =
    [
        [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1],
        [0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1],
        [0, 0, 1, 1, 0, 0, 1, 1, 2, 2, 3, 3, 2, 2, 3, 3],
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15],
    ];

    private void ModeRow(ref BoolDecoder bd, int row)
    {
        // Eighth-pel bounds with a one macroblock border (RFC 6386 section 18.3).
        var toLeft = -((0 + 1) << 7);
        var toRight = _mbCols << 7;
        var toTop = -((row + 1) << 7);
        var toBottom = (_mbRows - row) << 7;
        for (var col = 0; col < _mbCols; col++)
        {
            var mb = Mi(row, col);
            if (_segUpdateMap) _segmentId[mb] = (byte)ReadSegmentId(ref bd);
            _skip[mb] = (byte)(_skipEnabled ? bd.Get(_skipProb) : 0);
            if (_isKey)
            {
                DecodeKeyFrameModes(ref bd, mb);
            }
            else
            {
                if (bd.Get(_probInter) != 0) DecodeInterModes(ref bd, row, col, mb, toLeft, toRight, toTop, toBottom);
                else DecodeIntraModes(ref bd, mb);
                toLeft -= 16 << 3;
                toRight -= 16 << 3;
            }
        }
    }

    private int ReadSegmentId(ref BoolDecoder bd) =>
        bd.Get(_segTreeProbs[0]) != 0 ? 2 + bd.Get(_segTreeProbs[2]) : bd.Get(_segTreeProbs[1]);

    private int AboveBlockMode(int mb, int above, int b)
    {
        if (b < 4)
        {
            switch (_yMode[above])
            {
                case DcPred: return BDc;
                case VPred: return BVe;
                case HPred: return BHe;
                case TmPred: return BTm;
                default: return _bModes[above * 16 + b + 12];
            }
        }
        return _bModes[mb * 16 + b - 4];
    }

    private int LeftBlockMode(int mb, int left, int b)
    {
        if ((b & 3) == 0)
        {
            switch (_yMode[left])
            {
                case DcPred: return BDc;
                case VPred: return BVe;
                case HPred: return BHe;
                case TmPred: return BTm;
                default: return _bModes[left * 16 + b + 3];
            }
        }
        return _bModes[mb * 16 + b - 1];
    }

    private void DecodeKeyFrameModes(ref BoolDecoder bd, int mb)
    {
        var above = mb - (_mbCols + 1);
        var left = mb - 1;
        var yMode = bd.ReadTree(KfYModeTree, KfYModeProbs);
        if (yMode == BPred)
        {
            for (var i = 0; i < 16; i++)
            {
                var a = AboveBlockMode(mb, above, i);
                var l = LeftBlockMode(mb, left, i);
                _bModes[mb * 16 + i] = (byte)bd.ReadTree(BModeTree, Vp8Tables.KfBModeProbs, (a * 10 + l) * 9);
            }
        }
        _yMode[mb] = (byte)yMode;
        _uvMode[mb] = (byte)bd.ReadTree(UvModeTree, KfUvModeProbs);
        _mvX[mb] = _mvY[mb] = 0;
        _refFrame[mb] = 0;
    }

    private void DecodeIntraModes(ref BoolDecoder bd, int mb)
    {
        var yMode = bd.ReadTree(YModeTree, _entropy.YMode);
        if (yMode == BPred)
            for (var i = 0; i < 16; i++) _bModes[mb * 16 + i] = (byte)bd.ReadTree(BModeTree, DefaultBModeProbs);
        _yMode[mb] = (byte)yMode;
        _uvMode[mb] = (byte)bd.ReadTree(UvModeTree, _entropy.UvMode);
        _mvX[mb] = _mvY[mb] = 0;
        _refFrame[mb] = 0;
    }

    private static int ReadMvComponent(ref BoolDecoder bd, byte[] mvc, int offset)
    {
        const int IsShort = 0, Sign = 1, Short = 2, Bits = Short + 8 - 1, LongWidth = 10;
        var x = 0;
        if (bd.Get(mvc[offset + IsShort]) != 0)
        {
            for (var i = 0; i < 3; i++) x += bd.Get(mvc[offset + Bits + i]) << i;
            for (var i = LongWidth - 1; i > 3; i--) x += bd.Get(mvc[offset + Bits + i]) << i;
            if ((x & 0xFFF0) == 0 || bd.Get(mvc[offset + Bits + 3]) != 0) x += 8;
        }
        else x = bd.ReadTree(SmallMvTree, mvc, offset + Short);
        if (x != 0 && bd.Get(mvc[offset + Sign]) != 0) x = -x;
        return x << 1;
    }

    private void ReadMv(ref BoolDecoder bd, out short x, out short y)
    {
        y = (short)ReadMvComponent(ref bd, _entropy.Mv, 0);
        x = (short)ReadMvComponent(ref bd, _entropy.Mv, 19);
    }

    private static int Clamp(int value, int low, int high) => value < low ? low : value > high ? high : value;

    private void DecodeInterModes(ref BoolDecoder bd, int row, int col, int mb, int toLeft, int toRight, int toTop, int toBottom)
    {
        var above = mb - (_mbCols + 1);
        var left = mb - 1;
        var aboveLeft = above - 1;
        var refFrame = bd.Get(_probLast) != 0 ? 2 + bd.Get(_probGolden) : 1;
        _refFrame[mb] = (byte)refFrame;

        // find_near_mvs (RFC 6386 section 18.3). near[0] doubles as "best".
        Span<short> nearX = stackalloc short[4];
        Span<short> nearY = stackalloc short[4];
        Span<int> cnt = stackalloc int[4];
        var mv = 0;
        var cntx = 0;
        if (_refFrame[above] != 0)
        {
            if (_mvX[above] != 0 || _mvY[above] != 0)
            {
                mv++;
                nearX[mv] = _mvX[above]; nearY[mv] = _mvY[above];
                Bias(above, refFrame, ref nearX[mv], ref nearY[mv]);
                cntx++;
            }
            cnt[cntx] += 2;
        }
        if (_refFrame[left] != 0)
        {
            if (_mvX[left] != 0 || _mvY[left] != 0)
            {
                var tx = _mvX[left]; var ty = _mvY[left];
                Bias(left, refFrame, ref tx, ref ty);
                if (tx != nearX[mv] || ty != nearY[mv]) { mv++; nearX[mv] = tx; nearY[mv] = ty; cntx++; }
                cnt[cntx] += 2;
            }
            else cnt[0] += 2;
        }
        if (_refFrame[aboveLeft] != 0)
        {
            if (_mvX[aboveLeft] != 0 || _mvY[aboveLeft] != 0)
            {
                var tx = _mvX[aboveLeft]; var ty = _mvY[aboveLeft];
                Bias(aboveLeft, refFrame, ref tx, ref ty);
                if (tx != nearX[mv] || ty != nearY[mv]) { mv++; nearX[mv] = tx; nearY[mv] = ty; cntx++; }
                cnt[cntx] += 1;
            }
            else cnt[0] += 1;
        }
        if (cnt[3] != 0 && nearX[mv] == nearX[1] && nearY[mv] == nearY[1]) cnt[1] += 1;
        cnt[3] = ((_yMode[above] == SplitMv ? 1 : 0) + (_yMode[left] == SplitMv ? 1 : 0)) * 2 + (_yMode[aboveLeft] == SplitMv ? 1 : 0);
        if (cnt[2] > cnt[1])
        {
            (cnt[1], cnt[2]) = (cnt[2], cnt[1]);
            (nearX[1], nearX[2]) = (nearX[2], nearX[1]);
            (nearY[1], nearY[2]) = (nearY[2], nearY[1]);
        }
        if (cnt[1] >= cnt[0]) { nearX[0] = nearX[1]; nearY[0] = nearY[1]; }

        Span<byte> probs = stackalloc byte[4];
        for (var i = 0; i < 4; i++) probs[i] = MvCountsToProbs[Math.Min(cnt[i], 5)][i];
        var mode = ReadMvRef(ref bd, probs);
        _yMode[mb] = (byte)mode;
        _uvMode[mb] = (byte)mode;

        switch (mode)
        {
            case NearestMv:
                _mvX[mb] = (short)Clamp(nearX[1], toLeft, toRight); _mvY[mb] = (short)Clamp(nearY[1], toTop, toBottom);
                break;
            case NearMv:
                _mvX[mb] = (short)Clamp(nearX[2], toLeft, toRight); _mvY[mb] = (short)Clamp(nearY[2], toTop, toBottom);
                break;
            case ZeroMv:
                _mvX[mb] = _mvY[mb] = 0;
                break;
            case NewMv:
            {
                var bestX = Clamp(nearX[0], toLeft, toRight); var bestY = Clamp(nearY[0], toTop, toBottom);
                ReadMv(ref bd, out var dx, out var dy);
                _mvX[mb] = (short)(dx + bestX); _mvY[mb] = (short)(dy + bestY);
                break;
            }
            default: // SplitMv
            {
                var bestX = (short)Clamp(nearX[0], toLeft, toRight); var bestY = (short)Clamp(nearY[0], toTop, toBottom);
                DecodeSplitMv(ref bd, mb, left, above, bestX, bestY);
                _mvX[mb] = _subMvX[mb * 16 + 15]; _mvY[mb] = _subMvY[mb * 16 + 15];
                break;
            }
        }
    }

    private int ReadMvRef(ref BoolDecoder bd, ReadOnlySpan<byte> probs)
    {
        var i = 0;
        while ((i = MvRefTree[i + bd.Get(probs[i >> 1])]) > 0) { }
        return -i;
    }

    private void Bias(int neighbour, int refFrame, ref short x, ref short y)
    {
        if (_signBias[_refFrame[neighbour]] ^ _signBias[refFrame]) { x = (short)-x; y = (short)-y; }
    }

    private void DecodeSplitMv(ref BoolDecoder bd, int mb, int left, int above, short bestX, short bestY)
    {
        var partitionId = bd.ReadTree(SplitMvTree, SplitMvProbs);
        var partition = MvPartitions[partitionId];
        _partitioning[mb] = (byte)partitionId;
        var baseIndex = mb * 16;
        var mask = 0;
        for (var j = 0; mask < 65535; j++)
        {
            var k = 0;
            while (partition[k] != j) k++;
            var (lx, ly) = k % 4 == 0
                ? (_yMode[left] == SplitMv ? (_subMvX[left * 16 + k + 3], _subMvY[left * 16 + k + 3]) : (_mvX[left], _mvY[left]))
                : (_subMvX[baseIndex + k - 1], _subMvY[baseIndex + k - 1]);
            var (ax, ay) = k < 4
                ? (_yMode[above] == SplitMv ? (_subMvX[above * 16 + k + 12], _subMvY[above * 16 + k + 12]) : (_mvX[above], _mvY[above]))
                : (_subMvX[baseIndex + k - 4], _subMvY[baseIndex + k - 4]);
            var lez = lx == 0 && ly == 0;
            var aez = ax == 0 && ay == 0;
            var lea = lx == ax && ly == ay;
            var context = lea && lez ? 4 : lea ? 3 : aez ? 2 : lez ? 1 : 0;
            var sub = bd.ReadTree(SubMvRefTree, SubMvRefProbs[context]);
            short x, y;
            switch (sub)
            {
                case Left4x4: x = lx; y = ly; break;
                case Above4x4: x = ax; y = ay; break;
                case Zero4x4: x = 0; y = 0; break;
                default:
                    ReadMv(ref bd, out x, out y);
                    x = (short)(x + bestX); y = (short)(y + bestY);
                    break;
            }
            for (; k < 16; k++)
                if (partition[k] == j)
                {
                    _subMvX[baseIndex + k] = x; _subMvY[baseIndex + k] = y;
                    mask |= 1 << k;
                }
        }
    }

    // ---- DCT tokens -----------------------------------------------------------------------------------------------

    private static readonly int[] LeftCtxIndex = [0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8];
    private static readonly int[] AboveCtxIndex = [0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3, 4, 5, 4, 5, 6, 7, 6, 7, 8];
    private static readonly int[] BandsX = [0, 33, 66, 99, 198, 132, 165, 198, 198, 198, 198, 198, 198, 198, 198, 231];
    private static readonly int[] Zigzag = [0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15];
    private static readonly int[] CatMin = [5, 7, 11, 19, 35, 67];
    private static readonly byte[][] CatProbs =
    [
        [159],
        [145, 165],
        [140, 148, 173],
        [135, 140, 155, 176],
        [130, 134, 141, 157, 180],
        [129, 130, 133, 140, 153, 177, 196, 230, 243, 254, 254],
    ];

    private readonly int[] _leftCtx = new int[9];

    private void TokenRow(int partition, int row)
    {
        Array.Clear(_leftCtx);
        ref var tokens = ref _tokenBool[partition];
        for (var col = 0; col < _mbCols; col++)
        {
            var mb = Mi(row, col);
            var coeffOffset = col * 400;
            Array.Clear(_coeffs, coeffOffset, 400);
            var mode = _yMode[mb];
            if (_skip[mb] != 0)
            {
                // Reset the contexts, keeping Y2 when this mode has no Y2 block.
                for (var i = 0; i < 8; i++) { _leftCtx[i] = 0; _aboveCtx[col * 9 + i] = 0; }
                if (mode != BPred && mode != SplitMv) { _leftCtx[8] = 0; _aboveCtx[col * 9 + 8] = 0; }
                _eobMask[mb] = 0;
            }
            else
            {
                _eobMask[mb] = DecodeMbTokens(ref tokens, col * 9, coeffOffset, mode, _dq[_segEnabled ? _segmentId[mb] & 3 : 0]);
            }
        }
    }

    private uint DecodeMbTokens(ref BoolDecoder bd, int aboveBase, int coeffBase, int mode, short[] dq)
    {
        var probs = _entropy.Coeff;
        uint eobMask = 0;
        int i, stop, type;
        int dcFactor, acFactor;
        if (mode != BPred && mode != SplitMv)
        {
            i = 24; stop = 24; type = 1; dcFactor = dq[4]; acFactor = dq[5];
        }
        else
        {
            i = 0; stop = 16; type = 3; dcFactor = dq[0]; acFactor = dq[1];
        }
        while (true)
        {
            var typeBase = type * 264;
            var blockBase = coeffBase + i * 16;
            var t = _leftCtx[LeftCtxIndex[i]] + _aboveCtx[aboveBase + AboveCtxIndex[i]];
            var c = type == 0 ? 1 : 0;
            var p = typeBase + t * 11 + BandsX[c];
            while (true)
            {
                if (bd.Get(probs[p]) == 0) break; // end of block
                var skippedToEnd = false;
                while (bd.Get(probs[p + 1]) == 0) // zero token; no end-of-block check follows a zero
                {
                    if (c >= 15) { skippedToEnd = true; break; }
                    c++;
                    p = typeBase + BandsX[c];
                }
                if (skippedToEnd) break;
                int value, next;
                if (bd.Get(probs[p + 2]) == 0) { value = 1; next = 1; }
                else
                {
                    next = 2;
                    if (bd.Get(probs[p + 3]) == 0)
                    {
                        if (bd.Get(probs[p + 4]) == 0) value = 2;
                        else value = bd.Get(probs[p + 5]) == 0 ? 3 : 4;
                    }
                    else
                    {
                        int category;
                        if (bd.Get(probs[p + 6]) == 0) category = bd.Get(probs[p + 7]) == 0 ? 0 : 1;
                        else if (bd.Get(probs[p + 8]) == 0) category = bd.Get(probs[p + 9]) == 0 ? 2 : 3;
                        else category = bd.Get(probs[p + 10]) == 0 ? 4 : 5;
                        var catProbs = CatProbs[category];
                        value = CatMin[category];
                        for (var bit = catProbs.Length - 1; bit >= 0; bit--) value += bd.Get(catProbs[bit]) << bit;
                    }
                }
                var signed = bd.GetBit() != 0 ? -value : value;
                _coeffs[blockBase + Zigzag[c]] = (short)(signed * (c != 0 ? acFactor : dcFactor));
                if (c < 15)
                {
                    c++;
                    p = typeBase + next * 11 + BandsX[c];
                    continue;
                }
                break; // the last position was filled; c stays 15
            }
            eobMask |= (c > 1 ? 1u : 0u) << i;
            var nonZero = c != (type == 0 ? 1 : 0) ? 1 : 0;
            eobMask |= (uint)nonZero << 31;
            _leftCtx[LeftCtxIndex[i]] = _aboveCtx[aboveBase + AboveCtxIndex[i]] = nonZero;
            i++;
            if (i < stop) continue;
            if (i == 25)
            {
                type = 0; i = 0; stop = 16; dcFactor = dq[0]; acFactor = dq[1];
                continue;
            }
            if (i == 16)
            {
                type = 2; stop = 24; dcFactor = dq[2]; acFactor = dq[3];
                continue;
            }
            return eobMask;
        }
    }
}
