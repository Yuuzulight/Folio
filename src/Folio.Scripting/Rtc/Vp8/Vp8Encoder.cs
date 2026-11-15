using System.Buffers.Binary;
using System.Numerics;

namespace Folio.Scripting.Rtc.Vp8;

/// <summary>
/// A boolean arithmetic encoder for VP8 (RFC 6386 section 7 / libvpx).
/// Matches <see cref="BoolDecoder"/> in Vp8Decoder.cs bit-for-bit.
/// </summary>
internal sealed class BoolEncoder
{
    private byte[] _buffer;
    private int _pos;
    private uint _low;
    private int _range;
    private int _count;

    public BoolEncoder(int initialCapacity = 4096)
    {
        _buffer = new byte[Math.Max(64, initialCapacity)];
        _pos = 0;
        _low = 0;
        _range = 255;
        _count = -24; // 24 bits of buffer before first byte is output
    }

    private void Ensure(int count)
    {
        if (_pos + count <= _buffer.Length) return;
        var next = new byte[Math.Max(_buffer.Length * 2, _pos + count + 256)];
        Buffer.BlockCopy(_buffer, 0, next, 0, _pos);
        _buffer = next;
    }

    public void Put(int bit, int probability)
    {
        var split = 1 + (((_range - 1) * probability) >> 8);
        if (bit != 0)
        {
            _low += (uint)split;
            _range -= split;
        }
        else
        {
            _range = split;
        }

        var shift = BitOperations.LeadingZeroCount((uint)_range) - 24;
        _range <<= shift;
        _count += shift;

        if (_count >= 0)
        {
            var offset = shift - _count;
            if (offset > 0 && ((_low << (offset - 1)) & 0x80000000u) != 0)
            {
                var p = _pos - 1;
                while (p >= 0 && _buffer[p] == 255)
                {
                    _buffer[p] = 0;
                    p--;
                }
                if (p >= 0) _buffer[p]++;
            }

            Ensure(1);
            _buffer[_pos++] = (byte)((_low >> (24 - offset)) & 0xFF);
            _low = (uint)(((ulong)_low << offset) & 0x00FFFFFFu);
            shift = _count;
            _count -= 8;
        }

        _low <<= shift;
    }

    public void PutBit(int bit) => Put(bit, 128);

    public void PutUInt(int value, int bits)
    {
        for (var bit = bits - 1; bit >= 0; bit--)
            PutBit((value >> bit) & 1);
    }

    public void Finish()
    {
        for (var i = 0; i < 32; i++)
            PutBit(0);
    }

    public byte[] ToArray()
    {
        var result = new byte[_pos];
        Buffer.BlockCopy(_buffer, 0, result, 0, _pos);
        return result;
    }

    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _pos);
    public int Position => _pos;
}

/// <summary>
/// A minimal VP8 video encoder (RFC 6386).
/// Intra-only: every frame is a key frame carrying the picture it was given, so a frame is never stale and
/// a lost packet only costs one frame. Decodable by <see cref="Vp8Decoder"/>; interoperability with browsers is not yet verified.
/// Frame rate is the caller's to limit (see <see cref="RtpVideoSender"/>); the encoder only caps resolution at 640x360.
/// </summary>
internal sealed class Vp8Encoder
{
    public const int MaxWidth = 640;
    public const int MaxHeight = 360;

    private static readonly byte[] KfYModeProbs = [145, 156, 163, 128];
    private static readonly byte[] KfUvModeProbs = [142, 114, 183];
    private static readonly int[] LeftCtxIndex = [0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8];
    private static readonly int[] AboveCtxIndex = [0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3, 4, 5, 4, 5, 6, 7, 6, 7, 8];
    private static readonly int[] BandsX = [0, 33, 66, 99, 198, 132, 165, 198, 198, 198, 198, 198, 198, 198, 198, 231];
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

    private readonly int _targetWidth;
    private readonly int _targetHeight;
    private readonly int _qIndex;
    private readonly int _dcFactorY;
    private readonly int _dcFactorUv;

    public Vp8Encoder(int targetWidth = 320, int targetHeight = 240, int qIndex = 4)
    {
        _targetWidth = Math.Clamp(targetWidth, 16, MaxWidth);
        _targetHeight = Math.Clamp(targetHeight, 16, MaxHeight);
        _qIndex = Math.Clamp(qIndex, 0, 127);
        _dcFactorY = Vp8Tables.DcQ[_qIndex];
        var uvdc = Vp8Tables.DcQ[_qIndex];
        _dcFactorUv = uvdc > 132 ? 132 : uvdc;
    }

    public int Width => _targetWidth;
    public int Height => _targetHeight;

    /// <summary>
    /// Encodes an RGBA image into a VP8 key frame, resampling to the encoder dimensions. Returns null for malformed input.
    /// </summary>
    public byte[]? Encode(ReadOnlySpan<byte> rgba, int inputWidth, int inputHeight)
    {
        if (inputWidth <= 0 || inputHeight <= 0 || (long)inputWidth * inputHeight > rgba.Length / 4)
            return null;

        var w = _targetWidth;
        var h = _targetHeight;
        var cols = (w + 15) / 16;
        var rows = (h + 15) / 16;

        var yPlane = new byte[cols * 16 * rows * 16];
        var uPlane = new byte[cols * 8 * rows * 8];
        var vPlane = new byte[cols * 8 * rows * 8];
        RgbaToPlanar(rgba, inputWidth, inputHeight, yPlane, uPlane, vPlane, w, h, cols * 16, cols * 8);

        return EncodeKey(yPlane, uPlane, vPlane, w, h, cols, rows);
    }

    private static void RgbaToPlanar(ReadOnlySpan<byte> rgba, int inW, int inH,
        byte[] yPlane, byte[] uPlane, byte[] vPlane, int outW, int outH, int yStride, int uvStride)
    {
        for (var y = 0; y < outH; y++)
        {
            var srcY = (y * inH) / outH;
            var srcRow = srcY * inW * 4;
            var yRow = y * yStride;
            var uvRow = (y >> 1) * uvStride;
            var isEvenY = (y & 1) == 0;

            for (var x = 0; x < outW; x++)
            {
                var srcX = (x * inW) / outW;
                var si = srcRow + srcX * 4;
                int r = rgba[si];
                int g = rgba[si + 1];
                int b = rgba[si + 2];

                yPlane[yRow + x] = (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);

                if (isEvenY && (x & 1) == 0)
                {
                    var uvX = x >> 1;
                    uPlane[uvRow + uvX] = (byte)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                    vPlane[uvRow + uvX] = (byte)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
                }
            }
        }
    }

    private byte[] EncodeKey(byte[] yPlane, byte[] uPlane, byte[] vPlane, int width, int height, int cols, int rows)
    {
        var bd0 = new BoolEncoder(8192);
        bd0.PutUInt(0, 2); // Color space 0, clamping 0
        bd0.PutBit(0); // Segmentation disabled

        // Loop filter: disabled (level 0)
        bd0.PutBit(0); // simple = 0
        bd0.PutUInt(0, 6); // level = 0
        bd0.PutUInt(0, 3); // sharpness = 0
        bd0.PutBit(0); // delta = 0

        // 1 token partition (2^0 = 1)
        bd0.PutUInt(0, 2);

        // Quantizer
        bd0.PutUInt(_qIndex, 7);
        bd0.PutBit(0); // y1dc delta
        bd0.PutBit(0); // y2dc delta
        bd0.PutBit(0); // y2ac delta
        bd0.PutBit(0); // uvdc delta
        bd0.PutBit(0); // uvac delta

        // References (key frame: refresh_entropy = 0)
        bd0.PutBit(0);

        // Entropy: no coeff prob updates (1056 zeroes)
        for (var i = 0; i < 1056; i++)
            bd0.Put(0, Vp8Tables.CoeffUpdateProbs[i]);

        // Skip disabled = 0 (we send DC tokens for all blocks)
        bd0.PutBit(0);

        // Macroblock modes in Partition 0
        var bModes = new byte[(cols + 1) * (rows + 1) * 16];
        var yStride = cols * 16;
        var uvStride = cols * 8;

        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                var mb = (r + 1) * (cols + 1) + c + 1;
                var above = mb - (cols + 1);
                var left = mb - 1;

                // yMode = BPred (bit 0 at root of KfYModeTree)
                bd0.Put(0, KfYModeProbs[0]);

                // 16 sub-blocks: each BDc (bit 0 at root of BModeTree)
                for (var i = 0; i < 16; i++)
                {
                    int a = i < 4 ? bModes[above * 16 + i + 12] : bModes[mb * 16 + i - 4];
                    int l = (i & 3) == 0 ? bModes[left * 16 + i + 3] : bModes[mb * 16 + i - 1];
                    bModes[mb * 16 + i] = 0; // BDc
                    bd0.Put(0, Vp8Tables.KfBModeProbs[(a * 10 + l) * 9]);
                }

                // uvMode = DcPred (bit 0 at root of UvModeTree)
                bd0.Put(0, KfUvModeProbs[0]);
            }
        }
        bd0.Finish();
        var part0Data = bd0.ToArray();
        var part0Size = part0Data.Length;

        // Token Partition (Partition 1): DC tokens for 24 blocks per macroblock
        var bdToken = new BoolEncoder(16384);
        var leftCtx = new int[9];
        var aboveCtx = new int[cols * 9];
        var probs = Vp8Tables.DefaultCoeffProbs;

        // Reconstructed pixel buffers for intra-prediction tracking
        var reconY = new byte[yStride * rows * 16];
        var reconU = new byte[uvStride * rows * 8];
        var reconV = new byte[uvStride * rows * 8];

        for (var r = 0; r < rows; r++)
        {
            Array.Clear(leftCtx);
            for (var c = 0; c < cols; c++)
            {
                var mb = (r + 1) * (cols + 1) + c + 1;
                var aboveBase = c * 9;

                // 16 Y blocks (type = 3 in BPred)
                for (var i = 0; i < 16; i++)
                {
                    var bx = c * 16 + (i & 3) * 4;
                    var by = r * 16 + (i >> 2) * 4;

                    var sum = 0;
                    for (var dy = 0; dy < 4; dy++)
                    {
                        var rowOffset = (by + dy) * yStride + bx;
                        for (var dx = 0; dx < 4; dx++) sum += yPlane[rowOffset + dx];
                    }
                    var avg = (sum + 8) >> 4;

                    var predSum = 0;
                    for (var dx = 0; dx < 4; dx++)
                    {
                        var abovePixel = by > 0 ? reconY[(by - 1) * yStride + bx + dx] : (byte)127;
                        predSum += abovePixel;
                    }
                    for (var dy = 0; dy < 4; dy++)
                    {
                        var leftPixel = bx > 0 ? reconY[(by + dy) * yStride + bx - 1] : (byte)129;
                        predSum += leftPixel;
                    }
                    var pred = (predSum + 4) >> 3;

                    var diff = avg - pred;
                    var signed = (diff * 8) / _dcFactorY;
                    var actualRecon = Math.Clamp(pred + ((signed * _dcFactorY + 4) >> 3), 0, 255);

                    for (var dy = 0; dy < 4; dy++)
                    {
                        var rowOffset = (by + dy) * yStride + bx;
                        for (var dx = 0; dx < 4; dx++) reconY[rowOffset + dx] = (byte)actualRecon;
                    }

                    EncodeDcToken(ref bdToken, 3, i, aboveBase, leftCtx, aboveCtx, probs, signed);
                }

                // 4 U blocks (type = 2, i = 16..19)
                var uBx = c * 8;
                var uBy = r * 8;
                int uPred;
                if (r == 0 && c == 0)
                {
                    uPred = 128;
                }
                else if (r == 0)
                {
                    var sum = 0;
                    for (var dy = 0; dy < 8; dy++) sum += reconU[(uBy + dy) * uvStride + uBx - 1];
                    uPred = (sum + 4) >> 3;
                }
                else if (c == 0)
                {
                    var sum = 0;
                    for (var dx = 0; dx < 8; dx++) sum += reconU[(uBy - 1) * uvStride + uBx + dx];
                    uPred = (sum + 4) >> 3;
                }
                else
                {
                    var sum = 0;
                    for (var dx = 0; dx < 8; dx++) sum += reconU[(uBy - 1) * uvStride + uBx + dx];
                    for (var dy = 0; dy < 8; dy++) sum += reconU[(uBy + dy) * uvStride + uBx - 1];
                    uPred = (sum + 8) >> 4;
                }

                for (var i = 0; i < 4; i++)
                {
                    var bx = uBx + (i & 1) * 4;
                    var by = uBy + (i >> 1) * 4;

                    var sum = 0;
                    for (var dy = 0; dy < 4; dy++)
                    {
                        var rowOffset = (by + dy) * uvStride + bx;
                        for (var dx = 0; dx < 4; dx++) sum += uPlane[rowOffset + dx];
                    }
                    var avg = (sum + 8) >> 4;

                    var diff = avg - uPred;
                    var signed = (diff * 8) / _dcFactorUv;
                    var actualRecon = Math.Clamp(uPred + ((signed * _dcFactorUv + 4) >> 3), 0, 255);

                    for (var dy = 0; dy < 4; dy++)
                    {
                        var rowOffset = (by + dy) * uvStride + bx;
                        for (var dx = 0; dx < 4; dx++) reconU[rowOffset + dx] = (byte)actualRecon;
                    }

                    EncodeDcToken(ref bdToken, 2, 16 + i, aboveBase, leftCtx, aboveCtx, probs, signed);
                }

                // 4 V blocks (type = 2, i = 20..23)
                var vBx = c * 8;
                var vBy = r * 8;
                int vPred;
                if (r == 0 && c == 0)
                {
                    vPred = 128;
                }
                else if (r == 0)
                {
                    var sum = 0;
                    for (var dy = 0; dy < 8; dy++) sum += reconV[(vBy + dy) * uvStride + vBx - 1];
                    vPred = (sum + 4) >> 3;
                }
                else if (c == 0)
                {
                    var sum = 0;
                    for (var dx = 0; dx < 8; dx++) sum += reconV[(vBy - 1) * uvStride + vBx + dx];
                    vPred = (sum + 4) >> 3;
                }
                else
                {
                    var sum = 0;
                    for (var dx = 0; dx < 8; dx++) sum += reconV[(vBy - 1) * uvStride + vBx + dx];
                    for (var dy = 0; dy < 8; dy++) sum += reconV[(vBy + dy) * uvStride + vBx - 1];
                    vPred = (sum + 8) >> 4;
                }

                for (var i = 0; i < 4; i++)
                {
                    var bx = vBx + (i & 1) * 4;
                    var by = vBy + (i >> 1) * 4;

                    var sum = 0;
                    for (var dy = 0; dy < 4; dy++)
                    {
                        var rowOffset = (by + dy) * uvStride + bx;
                        for (var dx = 0; dx < 4; dx++) sum += vPlane[rowOffset + dx];
                    }
                    var avg = (sum + 8) >> 4;

                    var diff = avg - vPred;
                    var signed = (diff * 8) / _dcFactorUv;
                    var actualRecon = Math.Clamp(vPred + ((signed * _dcFactorUv + 4) >> 3), 0, 255);

                    for (var dy = 0; dy < 4; dy++)
                    {
                        var rowOffset = (by + dy) * uvStride + bx;
                        for (var dx = 0; dx < 4; dx++) reconV[rowOffset + dx] = (byte)actualRecon;
                    }

                    EncodeDcToken(ref bdToken, 2, 20 + i, aboveBase, leftCtx, aboveCtx, probs, signed);
                }
            }
        }
        bdToken.Finish();
        var tokenData = bdToken.ToArray();

        var frame = new byte[10 + part0Size + tokenData.Length];
        var rawTag = 0 | (0 << 1) | (1 << 4) | (part0Size << 5);
        frame[0] = (byte)rawTag;
        frame[1] = (byte)(rawTag >> 8);
        frame[2] = (byte)(rawTag >> 16);
        frame[3] = 0x9D;
        frame[4] = 0x01;
        frame[5] = 0x2A;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), (ushort)(width & 0x3FFF));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(8), (ushort)(height & 0x3FFF));

        Buffer.BlockCopy(part0Data, 0, frame, 10, part0Size);
        Buffer.BlockCopy(tokenData, 0, frame, 10 + part0Size, tokenData.Length);
        return frame;
    }

    private static void EncodeDcToken(ref BoolEncoder bd, int type, int blockIndex, int aboveBase,
        int[] leftCtx, int[] aboveCtx, byte[] probs, int signed)
    {
        var typeBase = type * 264;
        var t = leftCtx[LeftCtxIndex[blockIndex]] + aboveCtx[aboveBase + AboveCtxIndex[blockIndex]];
        var p = typeBase + t * 11 + BandsX[0];

        if (signed == 0)
        {
            bd.Put(0, probs[p]); // EOB
            leftCtx[LeftCtxIndex[blockIndex]] = aboveCtx[aboveBase + AboveCtxIndex[blockIndex]] = 0;
            return;
        }

        var val = Math.Abs(signed);
        bd.Put(1, probs[p]); // Not EOB
        bd.Put(1, probs[p + 1]); // Non-zero token

        int next;
        if (val == 1)
        {
            bd.Put(0, probs[p + 2]);
            next = 1;
        }
        else
        {
            next = 2;
            if (val <= 4)
            {
                bd.Put(1, probs[p + 2]);
                bd.Put(0, probs[p + 3]);
                if (val == 2)
                {
                    bd.Put(0, probs[p + 4]);
                }
                else
                {
                    bd.Put(1, probs[p + 4]);
                    bd.Put(val == 3 ? 0 : 1, probs[p + 5]);
                }
            }
            else
            {
                bd.Put(1, probs[p + 2]);
                bd.Put(1, probs[p + 3]);

                var cat = 0;
                while (cat < 5 && val >= CatMin[cat + 1]) cat++;

                if (cat < 2)
                {
                    bd.Put(0, probs[p + 6]);
                    bd.Put(cat == 0 ? 0 : 1, probs[p + 7]);
                }
                else if (cat < 4)
                {
                    bd.Put(1, probs[p + 6]);
                    bd.Put(0, probs[p + 8]);
                    bd.Put(cat == 2 ? 0 : 1, probs[p + 9]);
                }
                else
                {
                    bd.Put(1, probs[p + 6]);
                    bd.Put(1, probs[p + 8]);
                    bd.Put(cat == 4 ? 0 : 1, probs[p + 10]);
                }

                var catProbs = CatProbs[cat];
                var offset = val - CatMin[cat];
                for (var bit = catProbs.Length - 1; bit >= 0; bit--)
                    bd.Put((offset >> bit) & 1, catProbs[bit]);
            }
        }

        // Sign bit
        bd.PutBit(signed < 0 ? 1 : 0);

        // Position 1: End of Block
        var pNext = typeBase + next * 11 + BandsX[1];
        bd.Put(0, probs[pNext]);

        leftCtx[LeftCtxIndex[blockIndex]] = aboveCtx[aboveBase + AboveCtxIndex[blockIndex]] = 1;
    }
}
