using System.Numerics;

namespace Folio.Imaging;

/// <summary>
/// A VP8 key frame, as WebP stores it (RFC 6386, https://www.rfc-editor.org/rfc/rfc6386): the boolean entropy decoder,
/// segment and quantiser headers, intra prediction (sixteen-by-sixteen, four-by-four and chroma), the token partitions,
/// the inverse DCT and Walsh-Hadamard transforms, and the loop filter. The result is Y, U and V planes cropped to the
/// frame size; <see cref="Vp8Convert"/> makes pixels of them. Anything truncated or inconsistent gives null.
/// </summary>
internal static class Vp8Decoder
{
    internal sealed record Frame(int Width, int Height, byte[] Y, byte[] U, byte[] V, int YStride, int UvStride);

    // The block modes, in the order the specification numbers them (section 11.2).
    private const int BDc = 0, BTm = 1, BVe = 2, BHe = 3, BLd = 4, BRd = 5, BVr = 6, BVl = 7, BHd = 8, BHu = 9;
    private const int DcPred = 0, VPred = 1, HPred = 2, TmPred = 3;

    private static ReadOnlySpan<byte> Zigzag => [0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15];
    private static ReadOnlySpan<byte> Bands => [0, 1, 2, 3, 6, 4, 5, 6, 6, 6, 6, 6, 6, 6, 6, 7, 0];
    private static ReadOnlySpan<byte> Cat3 => [173, 148, 140];
    private static ReadOnlySpan<byte> Cat4 => [176, 155, 140, 135];
    private static ReadOnlySpan<byte> Cat5 => [180, 157, 141, 134, 130];
    private static ReadOnlySpan<byte> Cat6 => [254, 254, 243, 230, 196, 177, 153, 140, 133, 130, 129];

    // The subblock mode tree (section 11.2): a value above zero is the index of the next pair, anything else is minus a mode.
    private static ReadOnlySpan<sbyte> BModeTree => [-BDc, 2, -BTm, 4, -BVe, 6, 8, 12, -BHe, 10, -BRd, -BVr, -BLd, 14, -BVl, 16, -BHd, -BHu];

    public static Frame? Decode(ReadOnlySpan<byte> data, long maxPixels)
    {
        try
        {
            return new Decoder(data.ToArray()).Run(maxPixels);
        }
        catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>The boolean entropy decoder of section 7.</summary>
    private sealed class BoolDecoder
    {
        private readonly byte[] _data;
        private int _position;
        private readonly int _end;
        private uint _value;
        private int _range = 255;
        private int _bitCount;
        private int _padding;

        public BoolDecoder(byte[] data, int start, int end)
        {
            _data = data;
            _position = start;
            _end = end;
            _value = (uint)(Next() << 8 | Next());
        }

        // Reading a little past the end gives zeros, as an encoder's padding would; far past it is an error.
        public bool Overrun => _padding > 8;

        private int Next()
        {
            if (_position < _end)
                return _data[_position++];
            _padding++;
            return 0;
        }

        public int GetBit(int probability)
        {
            var split = 1 + (((_range - 1) * probability) >> 8);
            var bigSplit = (uint)split << 8;
            int bit;
            if (_value >= bigSplit)
            {
                _range -= split;
                _value -= bigSplit;
                bit = 1;
            }
            else
            {
                _range = split;
                bit = 0;
            }
            while (_range < 128)
            {
                _value <<= 1;
                _range <<= 1;
                if (++_bitCount == 8)
                {
                    _bitCount = 0;
                    _value |= (uint)Next();
                }
            }
            return bit;
        }

        public int Literal(int bits)
        {
            var value = 0;
            while (bits-- > 0)
                value = value << 1 | GetBit(128);
            return value;
        }

        public int Signed(int bits)
        {
            var value = Literal(bits);
            return GetBit(128) == 1 ? -value : value;
        }

        // An optional signed value: a flag, then magnitude and sign.
        public int OptionalSigned(int bits) => GetBit(128) == 1 ? Signed(bits) : 0;

        public int Tree(ReadOnlySpan<sbyte> tree, ReadOnlySpan<byte> probabilities)
        {
            var i = 0;
            while ((i = tree[i + GetBit(probabilities[i >> 1])]) > 0)
            {
            }
            return -i;
        }
    }

    private sealed class Decoder(byte[] data)
    {
        private BoolDecoder _header = null!;
        private int _width, _height, _mbWidth, _mbHeight;

        // Segment header (section 9.3).
        private bool _segmentsEnabled, _updateSegmentMap, _segmentAbsolute;
        private readonly int[] _segmentQuant = new int[4];
        private readonly int[] _segmentFilter = new int[4];
        private readonly byte[] _segmentProbs = [255, 255, 255];

        // Loop filter header (sections 9.4 and 15).
        private bool _simpleFilter, _filterDeltas;
        private int _filterLevel, _sharpness;
        private readonly int[] _refDelta = new int[4];
        private readonly int[] _modeDelta = new int[4];

        private readonly byte[] _coeffProbs = Vp8Tables.DefaultCoeffProbs.ToArray();
        private bool _useSkipProb;
        private int _skipProb;

        private readonly int[][] _quant = [new int[6], new int[6], new int[6], new int[6]]; // y1 dc, y1 ac, y2 dc, y2 ac, uv dc, uv ac

        private struct FilterInfo
        {
            public int Limit, InnerLevel, HevThreshold;
            public bool Inner;
        }

        public Frame? Run(long maxPixels)
        {
            if (data.Length < 10)
                return null;
            var tag = data[0] | data[1] << 8 | data[2] << 16;
            if ((tag & 1) != 0)
                return null; // only key frames
            var firstPartitionSize = tag >> 5;
            if (data[3] != 0x9D || data[4] != 0x01 || data[5] != 0x2A)
                return null;
            _width = (data[6] | data[7] << 8) & 0x3FFF;
            _height = (data[8] | data[9] << 8) & 0x3FFF;
            if (!ImageLimits.Allows(_width, _height, maxPixels))
                return null;
            _mbWidth = (_width + 15) >> 4;
            _mbHeight = (_height + 15) >> 4;

            var partitionStart = 10;
            if (partitionStart + firstPartitionSize > data.Length)
                return null;
            _header = new BoolDecoder(data, partitionStart, partitionStart + firstPartitionSize);

            ReadFrameHeader(out var partitionCount);
            var tokens = SplitPartitions(partitionStart + firstPartitionSize, partitionCount);
            ReadQuantizers(out var quantIndex, out var deltas);
            SetUpQuantizers(quantIndex, deltas);
            return DecodeMacroblocks(tokens);
        }

        private void ReadFrameHeader(out int partitionCount)
        {
            _header.Literal(1); // colour space
            _header.Literal(1); // clamping type: clamping is always done
            _segmentsEnabled = _header.Literal(1) == 1;
            if (_segmentsEnabled)
            {
                _updateSegmentMap = _header.Literal(1) == 1;
                if (_header.Literal(1) == 1) // update_segment_feature_data
                {
                    _segmentAbsolute = _header.Literal(1) == 1;
                    for (var i = 0; i < 4; i++)
                        _segmentQuant[i] = _header.OptionalSigned(7);
                    for (var i = 0; i < 4; i++)
                        _segmentFilter[i] = _header.OptionalSigned(6);
                }
                if (_updateSegmentMap)
                {
                    for (var i = 0; i < 3; i++)
                        _segmentProbs[i] = (byte)(_header.Literal(1) == 1 ? _header.Literal(8) : 255);
                }
            }

            _simpleFilter = _header.Literal(1) == 1;
            _filterLevel = _header.Literal(6);
            _sharpness = _header.Literal(3);
            _filterDeltas = _header.Literal(1) == 1;
            if (_filterDeltas && _header.Literal(1) == 1) // mode_ref_lf_delta_update
            {
                for (var i = 0; i < 4; i++)
                {
                    if (_header.Literal(1) == 1)
                        _refDelta[i] = _header.Signed(6);
                }
                for (var i = 0; i < 4; i++)
                {
                    if (_header.Literal(1) == 1)
                        _modeDelta[i] = _header.Signed(6);
                }
            }

            partitionCount = 1 << _header.Literal(2);
        }

        private int _qIndex;
        private readonly int[] _qDeltas = new int[5];

        private void ReadQuantizers(out int quantIndex, out int[] deltas)
        {
            quantIndex = _header.Literal(7);
            for (var i = 0; i < 5; i++)
                _qDeltas[i] = _header.OptionalSigned(4);
            deltas = _qDeltas;
            _qIndex = quantIndex;
        }

        private List<BoolDecoder> SplitPartitions(int start, int count)
        {
            var sizesEnd = start + 3 * (count - 1);
            if (sizesEnd > data.Length)
                throw new InvalidDataException("Truncated partition sizes.");
            var partitions = new List<BoolDecoder>(count);
            var position = sizesEnd;
            for (var i = 0; i < count; i++)
            {
                var size = i < count - 1 ? data[start + 3 * i] | data[start + 3 * i + 1] << 8 | data[start + 3 * i + 2] << 16 : data.Length - position;
                if (size < 0 || position + size > data.Length)
                    throw new InvalidDataException("A partition runs past the end of the frame.");
                partitions.Add(new BoolDecoder(data, position, position + size));
                position += size;
            }
            return partitions;
        }

        private void SetUpQuantizers(int baseIndex, int[] deltas)
        {
            _header.Literal(1); // refresh_entropy_probs: a single frame, so nothing is kept
            for (var t = 0; t < 4; t++)
            {
                for (var b = 0; b < 8; b++)
                {
                    for (var c = 0; c < 3; c++)
                    {
                        for (var p = 0; p < 11; p++)
                        {
                            var index = ((t * 8 + b) * 3 + c) * 11 + p;
                            if (_header.GetBit(Vp8Tables.CoeffUpdateProbs[index]) == 1)
                                _coeffProbs[index] = (byte)_header.Literal(8);
                        }
                    }
                }
            }
            _useSkipProb = _header.Literal(1) == 1;
            _skipProb = _useSkipProb ? _header.Literal(8) : 0;

            for (var s = 0; s < 4; s++)
            {
                var q = baseIndex;
                if (_segmentsEnabled)
                    q = _segmentAbsolute ? _segmentQuant[s] : baseIndex + _segmentQuant[s];
                var m = _quant[s];
                m[0] = Vp8Tables.DcQuant[Math.Clamp(q + deltas[0], 0, 127)];
                m[1] = Vp8Tables.AcQuant[Math.Clamp(q, 0, 127)];
                m[2] = Vp8Tables.DcQuant[Math.Clamp(q + deltas[1], 0, 127)] * 2;
                m[3] = Math.Max(8, Vp8Tables.AcQuant[Math.Clamp(q + deltas[2], 0, 127)] * 155 / 100);
                m[4] = Vp8Tables.DcQuant[Math.Clamp(q + deltas[3], 0, 117)];
                m[5] = Vp8Tables.AcQuant[Math.Clamp(q + deltas[4], 0, 127)];
            }
        }

        private Frame? DecodeMacroblocks(List<BoolDecoder> tokens)
        {
            var yStride = _mbWidth * 16;
            var uvStride = _mbWidth * 8;
            var yPlane = new byte[yStride * _mbHeight * 16];
            var uPlane = new byte[uvStride * _mbHeight * 8];
            var vPlane = new byte[uvStride * _mbHeight * 8];
            var filterInfo = new FilterInfo[_mbWidth * _mbHeight];

            // The subblock modes above (one row of four per macroblock) and to the left, for the mode contexts.
            var topModes = new byte[_mbWidth * 4];
            var leftModes = new byte[4];
            var topNz = new byte[_mbWidth * 9]; // 4 luma, 2 + 2 chroma, 1 for the second-order block
            var leftNz = new byte[9];

            // The work area: one border row above, and border columns to the left and, for the top right, beyond.
            const int Bps = 32;
            var yWork = new byte[Bps * 17];
            var uWork = new byte[Bps * 9];
            var vWork = new byte[Bps * 9];
            const int YOrigin = Bps + 8, UvOrigin = Bps + 8;
            var coefficients = new short[384];
            var modes = new byte[16];

            for (var mbY = 0; mbY < _mbHeight; mbY++)
            {
                var token = tokens[mbY & (tokens.Count - 1)];
                Array.Clear(leftModes);
                Array.Clear(leftNz);

                // The left border of the first macroblock of the row.
                for (var j = 0; j < 16; j++)
                    yWork[YOrigin + j * Bps - 1] = 129;
                for (var j = 0; j < 8; j++)
                {
                    uWork[UvOrigin + j * Bps - 1] = 129;
                    vWork[UvOrigin + j * Bps - 1] = 129;
                }
                if (mbY > 0)
                    yWork[YOrigin - Bps - 1] = uWork[UvOrigin - Bps - 1] = vWork[UvOrigin - Bps - 1] = 129;
                else
                {
                    Array.Fill(yWork, (byte)127, YOrigin - Bps - 1, 16 + 4 + 1);
                    Array.Fill(uWork, (byte)127, UvOrigin - Bps - 1, 8 + 1);
                    Array.Fill(vWork, (byte)127, UvOrigin - Bps - 1, 8 + 1);
                }

                for (var mbX = 0; mbX < _mbWidth; mbX++)
                {
                    if (_header.Overrun)
                        throw new InvalidDataException("The first partition ends early.");

                    // Macroblock header (section 19.3).
                    var segment = 0;
                    if (_updateSegmentMap)
                        segment = _header.GetBit(_segmentProbs[0]) == 0 ? _header.GetBit(_segmentProbs[1]) : 2 + _header.GetBit(_segmentProbs[2]);
                    var skipFlag = _useSkipProb && _header.GetBit(_skipProb) == 1;
                    var intra4 = _header.GetBit(145) == 0;
                    int yMode = 0;
                    if (!intra4)
                    {
                        yMode = _header.GetBit(156) == 1 ? (_header.GetBit(128) == 1 ? TmPred : HPred) : (_header.GetBit(163) == 1 ? VPred : DcPred);
                        var implied = yMode switch { DcPred => BDc, VPred => BVe, HPred => BHe, _ => BTm };
                        Array.Fill(topModes, (byte)implied, mbX * 4, 4);
                        Array.Fill(leftModes, (byte)implied);
                    }
                    else
                    {
                        for (var y = 0; y < 4; y++)
                        {
                            var left = leftModes[y];
                            for (var x = 0; x < 4; x++)
                            {
                                var above = topModes[mbX * 4 + x];
                                var probabilities = Vp8Tables.KeyFrameBModeProbs.Slice((above * 10 + left) * 9, 9);
                                left = (byte)_header.Tree(BModeTree, probabilities);
                                topModes[mbX * 4 + x] = left;
                                modes[y * 4 + x] = left;
                            }
                            leftModes[y] = left;
                        }
                    }
                    var uvMode = _header.GetBit(142) == 0 ? DcPred : _header.GetBit(114) == 0 ? VPred : _header.GetBit(183) == 1 ? TmPred : HPred;

                    // Residual (section 13).
                    Array.Clear(coefficients);
                    var nzBase = mbX * 9;
                    if (!skipFlag)
                        ReadResiduals(token, segment, intra4, topNz, nzBase, leftNz, coefficients);
                    else
                    {
                        // A skipped macroblock has no coefficients; its context is cleared, except the second-order one when it has none.
                        Array.Clear(topNz, nzBase, 8);
                        Array.Clear(leftNz, 0, 8);
                        if (!intra4)
                            topNz[nzBase + 8] = leftNz[8] = 0;
                    }
                    if (token.Overrun)
                        throw new InvalidDataException("A token partition ends early.");
                    var hasCoefficients = coefficients.AsSpan().ContainsAnyExcept((short)0);

                    // Prediction and reconstruction in the work area.
                    if (mbX > 0)
                    {
                        for (var j = -1; j < 16; j++)
                            for (var k = 0; k < 4; k++)
                                yWork[YOrigin + j * Bps - 4 + k] = yWork[YOrigin + j * Bps + 12 + k];
                        for (var j = -1; j < 8; j++)
                            for (var k = 0; k < 4; k++)
                            {
                                uWork[UvOrigin + j * Bps - 4 + k] = uWork[UvOrigin + j * Bps + 4 + k];
                                vWork[UvOrigin + j * Bps - 4 + k] = vWork[UvOrigin + j * Bps + 4 + k];
                            }
                    }
                    if (mbY > 0)
                    {
                        Array.Copy(yPlane, ((mbY * 16) - 1) * yStride + mbX * 16, yWork, YOrigin - Bps, 16);
                        Array.Copy(uPlane, ((mbY * 8) - 1) * uvStride + mbX * 8, uWork, UvOrigin - Bps, 8);
                        Array.Copy(vPlane, ((mbY * 8) - 1) * uvStride + mbX * 8, vWork, UvOrigin - Bps, 8);
                    }

                    if (intra4)
                    {
                        // The four pixels above and to the right, copied below for the subblocks on the right edge.
                        var topRight = YOrigin - Bps + 16;
                        if (mbY > 0)
                        {
                            for (var k = 0; k < 4; k++)
                                yWork[topRight + k] = mbX >= _mbWidth - 1 ? yPlane[(mbY * 16 - 1) * yStride + mbX * 16 + 15] : yPlane[(mbY * 16 - 1) * yStride + (mbX + 1) * 16 + k];
                        }
                        for (var r = 1; r <= 3; r++)
                            for (var k = 0; k < 4; k++)
                                yWork[topRight + r * 4 * Bps + k] = yWork[topRight + k];
                        for (var n = 0; n < 16; n++)
                        {
                            var dst = YOrigin + (n >> 2) * 4 * Bps + (n & 3) * 4;
                            PredictSubblock(yWork, dst, Bps, modes[n]);
                            AddTransform(coefficients, n * 16, yWork, dst, Bps);
                        }
                    }
                    else
                    {
                        PredictBlock(yWork, YOrigin, Bps, 16, yMode, mbX, mbY);
                        for (var n = 0; n < 16; n++)
                            AddTransform(coefficients, n * 16, yWork, YOrigin + (n >> 2) * 4 * Bps + (n & 3) * 4, Bps);
                    }
                    PredictBlock(uWork, UvOrigin, Bps, 8, uvMode, mbX, mbY);
                    PredictBlock(vWork, UvOrigin, Bps, 8, uvMode, mbX, mbY);
                    for (var n = 0; n < 4; n++)
                    {
                        var offset = UvOrigin + (n >> 1) * 4 * Bps + (n & 1) * 4;
                        AddTransform(coefficients, 256 + n * 16, uWork, offset, Bps);
                        AddTransform(coefficients, 320 + n * 16, vWork, offset, Bps);
                    }

                    for (var j = 0; j < 16; j++)
                        Array.Copy(yWork, YOrigin + j * Bps, yPlane, (mbY * 16 + j) * yStride + mbX * 16, 16);
                    for (var j = 0; j < 8; j++)
                    {
                        Array.Copy(uWork, UvOrigin + j * Bps, uPlane, (mbY * 8 + j) * uvStride + mbX * 8, 8);
                        Array.Copy(vWork, UvOrigin + j * Bps, vPlane, (mbY * 8 + j) * uvStride + mbX * 8, 8);
                    }

                    filterInfo[mbY * _mbWidth + mbX] = StrengthFor(segment, intra4, hasCoefficients);
                }
            }

            if (_filterLevel > 0)
                Filter(yPlane, uPlane, vPlane, yStride, uvStride, filterInfo);
            return new Frame(_width, _height, yPlane, uPlane, vPlane, yStride, uvStride);
        }

        // The loop filter over the whole reconstructed frame, macroblock by macroblock in raster order (section 15).
        private void Filter(byte[] y, byte[] u, byte[] v, int yStride, int uvStride, FilterInfo[] info)
        {
            for (var mbY = 0; mbY < _mbHeight; mbY++)
            {
                for (var mbX = 0; mbX < _mbWidth; mbX++)
                {
                    var f = info[mbY * _mbWidth + mbX];
                    if (f.Limit == 0)
                        continue;
                    var yPos = mbY * 16 * yStride + mbX * 16;
                    var uvPos = mbY * 8 * uvStride + mbX * 8;
                    if (_simpleFilter)
                    {
                        if (mbX > 0)
                            SimpleEdge(y, yPos, 1, yStride, 16, f.Limit + 4);
                        if (f.Inner)
                            for (var k = 4; k <= 12; k += 4)
                                SimpleEdge(y, yPos + k, 1, yStride, 16, f.Limit);
                        if (mbY > 0)
                            SimpleEdge(y, yPos, yStride, 1, 16, f.Limit + 4);
                        if (f.Inner)
                            for (var k = 4; k <= 12; k += 4)
                                SimpleEdge(y, yPos + k * yStride, yStride, 1, 16, f.Limit);
                        continue;
                    }

                    if (mbX > 0)
                    {
                        MacroblockEdge(y, yPos, 1, yStride, 16, f.Limit + 4, f.InnerLevel, f.HevThreshold);
                        MacroblockEdge(u, uvPos, 1, uvStride, 8, f.Limit + 4, f.InnerLevel, f.HevThreshold);
                        MacroblockEdge(v, uvPos, 1, uvStride, 8, f.Limit + 4, f.InnerLevel, f.HevThreshold);
                    }
                    if (f.Inner)
                    {
                        for (var k = 4; k <= 12; k += 4)
                            InnerEdge(y, yPos + k, 1, yStride, 16, f.Limit, f.InnerLevel, f.HevThreshold);
                        InnerEdge(u, uvPos + 4, 1, uvStride, 8, f.Limit, f.InnerLevel, f.HevThreshold);
                        InnerEdge(v, uvPos + 4, 1, uvStride, 8, f.Limit, f.InnerLevel, f.HevThreshold);
                    }
                    if (mbY > 0)
                    {
                        MacroblockEdge(y, yPos, yStride, 1, 16, f.Limit + 4, f.InnerLevel, f.HevThreshold);
                        MacroblockEdge(u, uvPos, uvStride, 1, 8, f.Limit + 4, f.InnerLevel, f.HevThreshold);
                        MacroblockEdge(v, uvPos, uvStride, 1, 8, f.Limit + 4, f.InnerLevel, f.HevThreshold);
                    }
                    if (f.Inner)
                    {
                        for (var k = 4; k <= 12; k += 4)
                            InnerEdge(y, yPos + k * yStride, yStride, 1, 16, f.Limit, f.InnerLevel, f.HevThreshold);
                        InnerEdge(u, uvPos + 4 * uvStride, uvStride, 1, 8, f.Limit, f.InnerLevel, f.HevThreshold);
                        InnerEdge(v, uvPos + 4 * uvStride, uvStride, 1, 8, f.Limit, f.InnerLevel, f.HevThreshold);
                    }
                }
            }
        }

        private FilterInfo StrengthFor(int segment, bool intra4, bool hasCoefficients)
        {
            var level = _filterLevel;
            if (_segmentsEnabled)
                level = _segmentAbsolute ? _segmentFilter[segment] : level + _segmentFilter[segment];
            if (_filterDeltas)
            {
                level += _refDelta[0];
                if (intra4)
                    level += _modeDelta[0];
            }
            level = Math.Clamp(level, 0, 63);
            var info = new FilterInfo { Inner = intra4 || hasCoefficients };
            if (level == 0)
                return info;
            var inner = level;
            if (_sharpness > 0)
            {
                inner >>= _sharpness > 4 ? 2 : 1;
                inner = Math.Min(inner, 9 - _sharpness);
            }
            inner = Math.Max(inner, 1);
            info.InnerLevel = inner;
            info.Limit = 2 * level + inner;
            info.HevThreshold = level >= 40 ? 2 : level >= 15 ? 1 : 0;
            return info;
        }

        // Reads the coefficients of one macroblock into coefficients (dequantised, in raster order within each block).
        private void ReadResiduals(BoolDecoder br, int segment, bool intra4, byte[] topNz, int nzBase, byte[] leftNz, short[] coefficients)
        {
            var q = _quant[segment];
            var first = 0;
            var yType = 3;
            if (!intra4)
            {
                // The second-order block carries the DC of the sixteen luma blocks.
                var dc = new short[16];
                var context = topNz[nzBase + 8] + leftNz[8];
                var n = ReadBlock(br, 1, context, q[2], q[3], 0, dc, 0);
                topNz[nzBase + 8] = leftNz[8] = (byte)(n > 0 ? 1 : 0);
                if (n > 1)
                    InverseWalsh(dc, coefficients);
                else
                {
                    var dc0 = (dc[0] + 3) >> 3;
                    for (var i = 0; i < 16; i++)
                        coefficients[i * 16] = (short)dc0;
                }
                first = 1;
                yType = 0;
            }

            for (var y = 0; y < 4; y++)
            {
                var left = leftNz[y];
                for (var x = 0; x < 4; x++)
                {
                    var n = ReadBlock(br, yType, left + topNz[nzBase + x], q[0], q[1], first, coefficients, (y * 4 + x) * 16);
                    left = (byte)(n > first ? 1 : 0);
                    topNz[nzBase + x] = left;
                }
                leftNz[y] = left;
            }

            for (var plane = 0; plane < 2; plane++)
            {
                var offset = 4 + plane * 2;
                for (var y = 0; y < 2; y++)
                {
                    var left = leftNz[offset + y];
                    for (var x = 0; x < 2; x++)
                    {
                        var n = ReadBlock(br, 2, left + topNz[nzBase + offset + x], q[4], q[5], 0, coefficients, 256 + plane * 64 + (y * 2 + x) * 16);
                        left = (byte)(n > 0 ? 1 : 0);
                        topNz[nzBase + offset + x] = left;
                    }
                    leftNz[offset + y] = left;
                }
            }
        }

        // The tokens of one block (section 13.2); returns the index after the last non-zero coefficient.
        private int ReadBlock(BoolDecoder br, int type, int context, int dcFactor, int acFactor, int n, short[] output, int offset)
        {
            var p = ((type * 8 + Bands[n]) * 3 + context) * 11;
            for (; n < 16; n++)
            {
                if (br.GetBit(_coeffProbs[p]) == 0)
                    return n;
                while (br.GetBit(_coeffProbs[p + 1]) == 0)
                {
                    if (++n == 16)
                        return 16;
                    p = ((type * 8 + Bands[n]) * 3) * 11;
                }

                var next = (type * 8 + Bands[n + 1]) * 3 * 11;
                int value;
                if (br.GetBit(_coeffProbs[p + 2]) == 0)
                {
                    value = 1;
                    p = next + 11;
                }
                else
                {
                    value = ReadLargeValue(br, p);
                    p = next + 22;
                }
                var signed = br.GetBit(128) == 1 ? -value : value;
                output[offset + Zigzag[n]] = (short)(signed * (n > 0 ? acFactor : dcFactor));
            }
            return 16;
        }

        private int ReadLargeValue(BoolDecoder br, int p)
        {
            if (br.GetBit(_coeffProbs[p + 3]) == 0)
                return br.GetBit(_coeffProbs[p + 4]) == 0 ? 2 : 3 + br.GetBit(_coeffProbs[p + 5]);
            if (br.GetBit(_coeffProbs[p + 6]) == 0)
            {
                if (br.GetBit(_coeffProbs[p + 7]) == 0)
                    return 5 + br.GetBit(159);
                var v = 7 + 2 * br.GetBit(165);
                return v + br.GetBit(145);
            }
            var bit1 = br.GetBit(_coeffProbs[p + 8]);
            var bit0 = br.GetBit(_coeffProbs[p + 9 + bit1]);
            var category = 2 * bit1 + bit0;
            var table = category switch { 0 => Cat3, 1 => Cat4, 2 => Cat5, _ => Cat6 };
            var extra = 0;
            foreach (var probability in table)
                extra += extra + br.GetBit(probability);
            return extra + 3 + (8 << category);
        }
    }

    // The inverse Walsh-Hadamard transform of the second-order block (section 14.3): the DC of each of the 16 luma blocks.
    private static void InverseWalsh(short[] input, short[] coefficients)
    {
        Span<int> tmp = stackalloc int[16];
        for (var i = 0; i < 4; i++)
        {
            var a0 = input[i] + input[12 + i];
            var a1 = input[4 + i] + input[8 + i];
            var a2 = input[4 + i] - input[8 + i];
            var a3 = input[i] - input[12 + i];
            tmp[i] = a0 + a1;
            tmp[8 + i] = a0 - a1;
            tmp[4 + i] = a3 + a2;
            tmp[12 + i] = a3 - a2;
        }
        for (var i = 0; i < 4; i++)
        {
            var dc = tmp[i * 4] + 3;
            var a0 = dc + tmp[3 + i * 4];
            var a1 = tmp[1 + i * 4] + tmp[2 + i * 4];
            var a2 = tmp[1 + i * 4] - tmp[2 + i * 4];
            var a3 = dc - tmp[3 + i * 4];
            coefficients[(i * 4 + 0) * 16] = (short)((a0 + a1) >> 3);
            coefficients[(i * 4 + 1) * 16] = (short)((a3 + a2) >> 3);
            coefficients[(i * 4 + 2) * 16] = (short)((a0 - a1) >> 3);
            coefficients[(i * 4 + 3) * 16] = (short)((a3 - a2) >> 3);
        }
    }

    // Adds the inverse DCT of one 4x4 block (section 14.4) to the prediction already in the work area.
    private static void AddTransform(short[] coefficients, int offset, byte[] work, int dst, int stride)
    {
        var any = false;
        for (var i = 0; i < 16; i++)
            any |= coefficients[offset + i] != 0;
        if (!any)
            return;

        Span<int> tmp = stackalloc int[16];
        for (var i = 0; i < 4; i++)
        {
            var in0 = coefficients[offset + i];
            var in4 = coefficients[offset + 4 + i];
            var in8 = coefficients[offset + 8 + i];
            var in12 = coefficients[offset + 12 + i];
            var a = in0 + in8;
            var b = in0 - in8;
            var c = Mul2(in4) - Mul1(in12);
            var d = Mul1(in4) + Mul2(in12);
            tmp[i * 4] = a + d;
            tmp[i * 4 + 1] = b + c;
            tmp[i * 4 + 2] = b - c;
            tmp[i * 4 + 3] = a - d;
        }
        for (var i = 0; i < 4; i++)
        {
            var dc = tmp[i] + 4;
            var a = dc + tmp[8 + i];
            var b = dc - tmp[8 + i];
            var c = Mul2(tmp[4 + i]) - Mul1(tmp[12 + i]);
            var d = Mul1(tmp[4 + i]) + Mul2(tmp[12 + i]);
            var row = dst + i * stride;
            work[row] = Clip8(work[row] + ((a + d) >> 3));
            work[row + 1] = Clip8(work[row + 1] + ((b + c) >> 3));
            work[row + 2] = Clip8(work[row + 2] + ((b - c) >> 3));
            work[row + 3] = Clip8(work[row + 3] + ((a - d) >> 3));
        }
    }

    private static int Mul1(int a) => ((a * 20091) >> 16) + a;

    private static int Mul2(int a) => (a * 35468) >> 16;

    private static byte Clip8(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    // Whole-block prediction of a luma (16) or chroma (8) block (section 12.2 and 12.3), with the edge rules.
    private static void PredictBlock(byte[] w, int origin, int stride, int size, int mode, int mbX, int mbY)
    {
        switch (mode)
        {
            case DcPred:
            {
                var log = size == 16 ? 4 : 3;
                var sum = 0;
                var count = 0;
                if (mbY > 0)
                {
                    for (var i = 0; i < size; i++)
                        sum += w[origin - stride + i];
                    count++;
                }
                if (mbX > 0)
                {
                    for (var j = 0; j < size; j++)
                        sum += w[origin + j * stride - 1];
                    count++;
                }
                // Both edges: 2 * size samples; one edge: size samples; neither: 128.
                var dc = count == 0 ? 128 : (sum + (count == 2 ? size : size >> 1)) >> (log + count - 1);
                for (var j = 0; j < size; j++)
                    Array.Fill(w, (byte)dc, origin + j * stride, size);
                break;
            }
            case VPred:
                for (var j = 0; j < size; j++)
                    Array.Copy(w, origin - stride, w, origin + j * stride, size);
                break;
            case HPred:
                for (var j = 0; j < size; j++)
                    Array.Fill(w, w[origin + j * stride - 1], origin + j * stride, size);
                break;
            default:
            {
                var topLeft = w[origin - stride - 1];
                for (var j = 0; j < size; j++)
                {
                    var left = w[origin + j * stride - 1] - topLeft;
                    for (var i = 0; i < size; i++)
                        w[origin + j * stride + i] = Clip8(w[origin - stride + i] + left);
                }
                break;
            }
        }
    }

    // One 4x4 luma subblock (section 12.3, the reference code's subblock_intra_predict).
    private static void PredictSubblock(byte[] w, int dst, int stride, int mode)
    {
        // A[-1..7]: the row above (and its top-left, and the four to the right); L[0..3]: the column to the left.
        var above = new int[9]; // above[0] is the top-left, above[1 + i] is A[i]
        var left = new int[4];
        for (var i = 0; i < 9; i++)
            above[i] = w[dst - stride - 1 + i];
        for (var j = 0; j < 4; j++)
            left[j] = w[dst + j * stride - 1];
        int A(int i) => above[i + 1];

        // The edge from the bottom left, up to the top-left corner, then along the top: E[0..8].
        var e = new int[9];
        e[0] = left[3]; e[1] = left[2]; e[2] = left[1]; e[3] = left[0]; e[4] = above[0];
        e[5] = A(0); e[6] = A(1); e[7] = A(2); e[8] = A(3);
        int Avg3(int x, int y, int z) => (x + y + y + z + 2) >> 2;
        int Avg2(int x, int y) => (x + y + 1) >> 1;

        void Set(int r, int c, int v) => w[dst + r * stride + c] = (byte)v;

        switch (mode)
        {
            case BDc:
            {
                var v = 4;
                for (var i = 0; i < 4; i++)
                    v += A(i) + left[i];
                v >>= 3;
                for (var r = 0; r < 4; r++)
                    for (var c = 0; c < 4; c++)
                        Set(r, c, v);
                break;
            }
            case BTm:
                for (var r = 0; r < 4; r++)
                    for (var c = 0; c < 4; c++)
                        Set(r, c, Clip8(left[r] + A(c) - above[0]));
                break;
            case BVe:
                for (var c = 0; c < 4; c++)
                {
                    var v = Avg3(A(c - 1), A(c), A(c + 1));
                    for (var r = 0; r < 4; r++)
                        Set(r, c, v);
                }
                break;
            case BHe:
            {
                // L[-1] is the top-left; the bottom row has no L[4].
                for (var r = 0; r < 4; r++)
                {
                    var v = r == 3 ? Avg3(left[2], left[3], left[3]) : Avg3(r == 0 ? above[0] : left[r - 1], left[r], left[r + 1]);
                    for (var c = 0; c < 4; c++)
                        Set(r, c, v);
                }
                break;
            }
            case BLd:
                Set(0, 0, Avg3(A(0), A(1), A(2)));
                Set(0, 1, Avg3(A(1), A(2), A(3))); Set(1, 0, Avg3(A(1), A(2), A(3)));
                Set(0, 2, Avg3(A(2), A(3), A(4))); Set(1, 1, Avg3(A(2), A(3), A(4))); Set(2, 0, Avg3(A(2), A(3), A(4)));
                Set(0, 3, Avg3(A(3), A(4), A(5))); Set(1, 2, Avg3(A(3), A(4), A(5))); Set(2, 1, Avg3(A(3), A(4), A(5))); Set(3, 0, Avg3(A(3), A(4), A(5)));
                Set(1, 3, Avg3(A(4), A(5), A(6))); Set(2, 2, Avg3(A(4), A(5), A(6))); Set(3, 1, Avg3(A(4), A(5), A(6)));
                Set(2, 3, Avg3(A(5), A(6), A(7))); Set(3, 2, Avg3(A(5), A(6), A(7)));
                Set(3, 3, Avg3(A(6), A(7), A(7)));
                break;
            case BRd:
                Set(3, 0, Avg3(e[0], e[1], e[2]));
                Set(3, 1, Avg3(e[1], e[2], e[3])); Set(2, 0, Avg3(e[1], e[2], e[3]));
                Set(3, 2, Avg3(e[2], e[3], e[4])); Set(2, 1, Avg3(e[2], e[3], e[4])); Set(1, 0, Avg3(e[2], e[3], e[4]));
                Set(3, 3, Avg3(e[3], e[4], e[5])); Set(2, 2, Avg3(e[3], e[4], e[5])); Set(1, 1, Avg3(e[3], e[4], e[5])); Set(0, 0, Avg3(e[3], e[4], e[5]));
                Set(2, 3, Avg3(e[4], e[5], e[6])); Set(1, 2, Avg3(e[4], e[5], e[6])); Set(0, 1, Avg3(e[4], e[5], e[6]));
                Set(1, 3, Avg3(e[5], e[6], e[7])); Set(0, 2, Avg3(e[5], e[6], e[7]));
                Set(0, 3, Avg3(e[6], e[7], e[8]));
                break;
            case BVr:
                Set(3, 0, Avg3(e[1], e[2], e[3]));
                Set(2, 0, Avg3(e[2], e[3], e[4]));
                Set(3, 1, Avg3(e[3], e[4], e[5])); Set(1, 0, Avg3(e[3], e[4], e[5]));
                Set(2, 1, Avg2(e[4], e[5])); Set(0, 0, Avg2(e[4], e[5]));
                Set(3, 2, Avg3(e[4], e[5], e[6])); Set(1, 1, Avg3(e[4], e[5], e[6]));
                Set(2, 2, Avg2(e[5], e[6])); Set(0, 1, Avg2(e[5], e[6]));
                Set(3, 3, Avg3(e[5], e[6], e[7])); Set(1, 2, Avg3(e[5], e[6], e[7]));
                Set(2, 3, Avg2(e[6], e[7])); Set(0, 2, Avg2(e[6], e[7]));
                Set(1, 3, Avg3(e[6], e[7], e[8]));
                Set(0, 3, Avg2(e[7], e[8]));
                break;
            case BVl:
                Set(0, 0, Avg2(A(0), A(1)));
                Set(1, 0, Avg3(A(0), A(1), A(2)));
                Set(2, 0, Avg2(A(1), A(2))); Set(0, 1, Avg2(A(1), A(2)));
                Set(1, 1, Avg3(A(1), A(2), A(3))); Set(3, 0, Avg3(A(1), A(2), A(3)));
                Set(2, 1, Avg2(A(2), A(3))); Set(0, 2, Avg2(A(2), A(3)));
                Set(3, 1, Avg3(A(2), A(3), A(4))); Set(1, 2, Avg3(A(2), A(3), A(4)));
                Set(2, 2, Avg2(A(3), A(4))); Set(0, 3, Avg2(A(3), A(4)));
                Set(3, 2, Avg3(A(3), A(4), A(5))); Set(1, 3, Avg3(A(3), A(4), A(5)));
                Set(2, 3, Avg3(A(4), A(5), A(6)));
                Set(3, 3, Avg3(A(5), A(6), A(7)));
                break;
            case BHd:
                Set(3, 0, Avg2(e[0], e[1]));
                Set(3, 1, Avg3(e[0], e[1], e[2]));
                Set(2, 0, Avg2(e[1], e[2])); Set(3, 2, Avg2(e[1], e[2]));
                Set(2, 1, Avg3(e[1], e[2], e[3])); Set(3, 3, Avg3(e[1], e[2], e[3]));
                Set(2, 2, Avg2(e[2], e[3])); Set(1, 0, Avg2(e[2], e[3]));
                Set(2, 3, Avg3(e[2], e[3], e[4])); Set(1, 1, Avg3(e[2], e[3], e[4]));
                Set(1, 2, Avg2(e[3], e[4])); Set(0, 0, Avg2(e[3], e[4]));
                Set(1, 3, Avg3(e[3], e[4], e[5])); Set(0, 1, Avg3(e[3], e[4], e[5]));
                Set(0, 2, Avg3(e[4], e[5], e[6]));
                Set(0, 3, Avg3(e[5], e[6], e[7]));
                break;
            default: // BHu
                Set(0, 0, Avg2(left[0], left[1]));
                Set(0, 1, Avg3(left[0], left[1], left[2]));
                Set(0, 2, Avg2(left[1], left[2])); Set(1, 0, Avg2(left[1], left[2]));
                Set(0, 3, Avg3(left[1], left[2], left[3])); Set(1, 1, Avg3(left[1], left[2], left[3]));
                Set(1, 2, Avg2(left[2], left[3])); Set(2, 0, Avg2(left[2], left[3]));
                Set(1, 3, Avg3(left[2], left[3], left[3])); Set(2, 1, Avg3(left[2], left[3], left[3]));
                Set(2, 2, left[3]); Set(2, 3, left[3]); Set(3, 0, left[3]); Set(3, 1, left[3]); Set(3, 2, left[3]); Set(3, 3, left[3]);
                break;
        }
    }

    // Filter primitives (sections 15.2 and 15.3). hStep is the distance between the pixels across the edge, vStep along it.
    private static int Abs(int v) => v < 0 ? -v : v;

    private static int SClip1(int v) => v < -128 ? -128 : v > 127 ? 127 : v;

    private static int SClip2(int v) => v < -16 ? -16 : v > 15 ? 15 : v;

    private static void SimpleEdge(byte[] p, int pos, int hStep, int vStep, int count, int threshold)
    {
        var limit = 2 * threshold + 1;
        for (var i = 0; i < count; i++, pos += vStep)
        {
            if (4 * Abs(p[pos - hStep] - p[pos]) + Abs(p[pos - 2 * hStep] - p[pos + hStep]) <= limit)
                Filter2(p, pos, hStep);
        }
    }

    private static bool NeedsFilter(byte[] p, int pos, int h, int limit, int inner)
    {
        int p3 = p[pos - 4 * h], p2 = p[pos - 3 * h], p1 = p[pos - 2 * h], p0 = p[pos - h];
        int q0 = p[pos], q1 = p[pos + h], q2 = p[pos + 2 * h], q3 = p[pos + 3 * h];
        if (4 * Abs(p0 - q0) + Abs(p1 - q1) > limit)
            return false;
        return Abs(p3 - p2) <= inner && Abs(p2 - p1) <= inner && Abs(p1 - p0) <= inner && Abs(q3 - q2) <= inner && Abs(q2 - q1) <= inner && Abs(q1 - q0) <= inner;
    }

    private static bool HighEdgeVariance(byte[] p, int pos, int h, int threshold) =>
        Abs(p[pos - 2 * h] - p[pos - h]) > threshold || Abs(p[pos + h] - p[pos]) > threshold;

    private static void Filter2(byte[] p, int pos, int h)
    {
        int p1 = p[pos - 2 * h], p0 = p[pos - h], q0 = p[pos], q1 = p[pos + h];
        var a = 3 * (q0 - p0) + SClip1(p1 - q1);
        var a1 = SClip2((a + 4) >> 3);
        var a2 = SClip2((a + 3) >> 3);
        p[pos - h] = Clip8(p0 + a2);
        p[pos] = Clip8(q0 - a1);
    }

    private static void Filter4(byte[] p, int pos, int h)
    {
        int p1 = p[pos - 2 * h], p0 = p[pos - h], q0 = p[pos], q1 = p[pos + h];
        var a = 3 * (q0 - p0);
        var a1 = SClip2((a + 4) >> 3);
        var a2 = SClip2((a + 3) >> 3);
        var a3 = (a1 + 1) >> 1;
        p[pos - 2 * h] = Clip8(p1 + a3);
        p[pos - h] = Clip8(p0 + a2);
        p[pos] = Clip8(q0 - a1);
        p[pos + h] = Clip8(q1 - a3);
    }

    private static void Filter6(byte[] p, int pos, int h)
    {
        int p2 = p[pos - 3 * h], p1 = p[pos - 2 * h], p0 = p[pos - h], q0 = p[pos], q1 = p[pos + h], q2 = p[pos + 2 * h];
        var a = SClip1(3 * (q0 - p0) + SClip1(p1 - q1));
        var a1 = (27 * a + 63) >> 7;
        var a2 = (18 * a + 63) >> 7;
        var a3 = (9 * a + 63) >> 7;
        p[pos - 3 * h] = Clip8(p2 + a3);
        p[pos - 2 * h] = Clip8(p1 + a2);
        p[pos - h] = Clip8(p0 + a1);
        p[pos] = Clip8(q0 - a1);
        p[pos + h] = Clip8(q1 - a2);
        p[pos + 2 * h] = Clip8(q2 - a3);
    }

    private static void MacroblockEdge(byte[] p, int pos, int hStep, int vStep, int count, int threshold, int inner, int hevThreshold)
    {
        var limit = 2 * threshold + 1;
        for (var i = 0; i < count; i++, pos += vStep)
        {
            if (!NeedsFilter(p, pos, hStep, limit, inner))
                continue;
            if (HighEdgeVariance(p, pos, hStep, hevThreshold))
                Filter2(p, pos, hStep);
            else
                Filter6(p, pos, hStep);
        }
    }

    private static void InnerEdge(byte[] p, int pos, int hStep, int vStep, int count, int threshold, int inner, int hevThreshold)
    {
        var limit = 2 * threshold + 1;
        for (var i = 0; i < count; i++, pos += vStep)
        {
            if (!NeedsFilter(p, pos, hStep, limit, inner))
                continue;
            if (HighEdgeVariance(p, pos, hStep, hevThreshold))
                Filter2(p, pos, hStep);
            else
                Filter4(p, pos, hStep);
        }
    }
}
