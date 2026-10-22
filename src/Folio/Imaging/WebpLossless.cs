namespace Folio.Imaging;

/// <summary>
/// The WebP lossless bitstream (VP8L, https://developers.google.com/speed/webp/docs/webp_lossless_bitstream_specification):
/// transforms (predictor, cross-colour, subtract-green, colour indexing), canonical prefix codes with meta prefix groups,
/// the colour cache and LZ77 backward references. Any input that is truncated, inconsistent or over the limits
/// gives null.
/// </summary>
internal static class WebpLossless
{
    private const int MaxCodeLength = 15;
    private const int NumLiteralCodes = 256;
    private const int NumLengthCodes = 24;
    private const int NumDistanceCodes = 40;
    private const int CodeLengthCodes = 19;

    private static ReadOnlySpan<byte> CodeLengthCodeOrder => [17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    // The (x, y) offsets the first 120 distance codes stand for (the lossless specification, "distance mapping").
    private static ReadOnlySpan<sbyte> DistanceMap =>
    [
        0, 1, 1, 0, 1, 1, -1, 1, 0, 2, 2, 0, 1, 2, -1, 2, 2, 1, -2, 1, 2, 2, -2, 2, 0, 3, 3, 0, 1, 3, -1, 3,
        3, 1, -3, 1, 2, 3, -2, 3, 3, 2, -3, 2, 0, 4, 4, 0, 1, 4, -1, 4, 4, 1, -4, 1, 3, 3, -3, 3, 2, 4, -2, 4,
        4, 2, -4, 2, 0, 5, 3, 4, -3, 4, 4, 3, -4, 3, 5, 0, 1, 5, -1, 5, 5, 1, -5, 1, 2, 5, -2, 5, 5, 2, -5, 2,
        4, 4, -4, 4, 3, 5, -3, 5, 5, 3, -5, 3, 0, 6, 6, 0, 1, 6, -1, 6, 6, 1, -6, 1, 2, 6, -2, 6, 6, 2, -6, 2,
        4, 5, -4, 5, 5, 4, -5, 4, 3, 6, -3, 6, 6, 3, -6, 3, 0, 7, 7, 0, 1, 7, -1, 7, 5, 5, -5, 5, 7, 1, -7, 1,
        4, 6, -4, 6, 6, 4, -6, 4, 2, 7, -2, 7, 7, 2, -7, 2, 3, 7, -3, 7, 7, 3, -7, 3, 5, 6, -5, 6, 6, 5, -6, 5,
        8, 0, 4, 7, -4, 7, 7, 4, -7, 4, 8, 1, 8, 2, 6, 6, -6, 6, 8, 3, 5, 7, -5, 7, 7, 5, -7, 5, 8, 4, 6, 7,
        -6, 7, 7, 6, -7, 6, 8, 5, 7, 7, -7, 7, 8, 6, 8, 7,
    ];

    /// <summary>Decodes a VP8L chunk payload (starting at its 0x2F signature) to ARGB pixels, or null.</summary>
    public static (int Width, int Height, uint[] Argb)? Decode(ReadOnlySpan<byte> data, long maxPixels)
    {
        try
        {
            var bits = new BitReader(data);
            if (bits.ReadBits(8) != 0x2F)
                return null;
            var width = (int)bits.ReadBits(14) + 1;
            var height = (int)bits.ReadBits(14) + 1;
            bits.ReadBits(1); // alpha_is_used is only a hint: the alpha values themselves are decoded
            if (bits.ReadBits(3) != 0 || !ImageLimits.Allows(width, height, maxPixels))
                return null;
            var decoder = new Decoder(bits);
            var argb = decoder.DecodeMain(width, height);
            return argb is null || decoder.Bits.Overrun ? null : (width, height, argb);
        }
        catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>Decodes a VP8L stream that has no header, its size known from elsewhere (the alpha plane of a lossy file).</summary>
    public static uint[]? DecodeHeaderless(ReadOnlySpan<byte> data, int width, int height, long maxPixels)
    {
        try
        {
            if (!ImageLimits.Allows(width, height, maxPixels))
                return null;
            var decoder = new Decoder(new BitReader(data));
            var argb = decoder.DecodeMain(width, height);
            return argb is null || decoder.Bits.Overrun ? null : argb;
        }
        catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    private sealed class BitReader(ReadOnlySpan<byte> source)
    {
        private readonly byte[] _data = source.ToArray();
        private long _position; // in bits

        public bool Overrun => _position > (long)_data.Length * 8;

        public uint ReadBits(int count)
        {
            // Reading past the end gives zeros and sets Overrun, which the caller checks once the stream is decoded.
            uint value = 0;
            for (var i = 0; i < count;)
            {
                var byteIndex = _position >> 3;
                var shift = (int)(_position & 7);
                var take = Math.Min(8 - shift, count - i);
                var b = byteIndex < _data.Length ? _data[byteIndex] : 0;
                value |= (uint)((b >> shift) & ((1 << take) - 1)) << i;
                _position += take;
                i += take;
            }
            return value;
        }

        // The next up-to-16 bits without consuming them (LSB first, like ReadBits).
        public int Peek(int count)
        {
            var saved = _position;
            var value = (int)ReadBits(count);
            _position = saved;
            return value;
        }

        public void Skip(int count) => _position += count;
    }

    /// <summary>A canonical prefix code: symbols by code length, decoded through a table for short codes.</summary>
    private sealed class PrefixCode
    {
        private const int TableBits = 8;
        private readonly int _single = -1;
        private readonly short[] _tableSymbol = new short[1 << TableBits];
        private readonly byte[] _tableLength = new byte[1 << TableBits];
        private readonly int[] _count = new int[MaxCodeLength + 1];
        private readonly int[] _sorted;
        private readonly int _maxLength;

        public PrefixCode(ReadOnlySpan<int> lengths)
        {
            var nonZero = 0;
            var last = 0;
            for (var s = 0; s < lengths.Length; s++)
            {
                if (lengths[s] > 0)
                {
                    nonZero++;
                    last = s;
                    _count[lengths[s]]++;
                    _maxLength = Math.Max(_maxLength, lengths[s]);
                }
            }
            if (nonZero == 0)
                throw new InvalidDataException("A prefix code with no symbols.");
            _sorted = new int[nonZero];
            if (nonZero == 1)
            {
                _single = last; // a one-symbol code uses no bits
                return;
            }

            // The code must be complete: it neither oversubscribes nor leaves unused code space.
            var left = 1;
            for (var len = 1; len <= MaxCodeLength; len++)
            {
                left = (left << 1) - _count[len];
                if (left < 0)
                    throw new InvalidDataException("An oversubscribed prefix code.");
            }
            if (left != 0)
                throw new InvalidDataException("An incomplete prefix code.");

            var offsets = new int[MaxCodeLength + 2];
            for (var len = 1; len <= MaxCodeLength; len++)
                offsets[len + 1] = offsets[len] + _count[len];
            for (var s = 0; s < lengths.Length; s++)
            {
                if (lengths[s] > 0)
                    _sorted[offsets[lengths[s]]++] = s;
            }

            // Canonical codes, and the lookup table: the stream is read least significant bit first, so a code's bits are
            // reversed to index the table.
            var code = 0;
            var index = 0;
            for (var len = 1; len <= MaxCodeLength; len++)
            {
                for (var n = 0; n < _count[len]; n++, index++, code++)
                {
                    if (len > TableBits)
                        continue;
                    var reversed = 0;
                    for (var b = 0; b < len; b++)
                        reversed |= ((code >> b) & 1) << (len - 1 - b);
                    for (var fill = reversed; fill < 1 << TableBits; fill += 1 << len)
                    {
                        _tableSymbol[fill] = (short)_sorted[index];
                        _tableLength[fill] = (byte)len;
                    }
                }
                code <<= 1;
            }
        }

        public int Read(BitReader bits)
        {
            if (_single >= 0)
                return _single;
            var peek = bits.Peek(TableBits);
            if (_tableLength[peek] is var length and > 0)
            {
                bits.Skip(length);
                return _tableSymbol[peek];
            }

            // A code longer than the table: walk it bit by bit.
            var value = 0;
            var first = 0;
            var position = 0;
            for (var len = 1; len <= _maxLength; len++)
            {
                value |= (int)bits.ReadBits(1);
                var count = _count[len];
                if (value - count < first)
                    return _sorted[position + (value - first)];
                position += count;
                first = (first + count) << 1;
                value <<= 1;
            }
            throw new InvalidDataException("An invalid prefix code.");
        }
    }

    private sealed class PrefixGroup
    {
        // Green + length + colour cache, red, blue, alpha, distance.
        public PrefixCode[] Codes = new PrefixCode[5];
    }

    private enum TransformKind { Predictor, CrossColor, SubtractGreen, ColorIndexing }

    private sealed record Transform(TransformKind Kind, int Width, int Bits, uint[]? Data);

    private sealed class Decoder(BitReader bits)
    {
        public readonly BitReader Bits = bits;
        private readonly List<Transform> _transforms = [];

        public uint[]? DecodeMain(int width, int height)
        {
            var transformsSeen = 0;
            var xsize = width;
            while (Bits.ReadBits(1) == 1)
            {
                var kind = (TransformKind)Bits.ReadBits(2);
                if ((transformsSeen & (1 << (int)kind)) != 0)
                    return null; // each transform may be used once
                transformsSeen |= 1 << (int)kind;
                switch (kind)
                {
                    case TransformKind.Predictor:
                    case TransformKind.CrossColor:
                    {
                        var bitsPerBlock = (int)Bits.ReadBits(3) + 2;
                        var data = DecodeImage(DivRoundUp(xsize, 1 << bitsPerBlock), DivRoundUp(height, 1 << bitsPerBlock), false);
                        _transforms.Add(new Transform(kind, xsize, bitsPerBlock, data));
                        break;
                    }
                    case TransformKind.SubtractGreen:
                        _transforms.Add(new Transform(kind, xsize, 0, null));
                        break;
                    default:
                    {
                        var tableSize = (int)Bits.ReadBits(8) + 1;
                        var table = DecodeImage(tableSize, 1, false);
                        for (var i = 1; i < table.Length; i++)
                            table[i] = AddPixels(table[i], table[i - 1]);
                        var packing = tableSize switch { <= 2 => 3, <= 4 => 2, <= 16 => 1, _ => 0 };
                        _transforms.Add(new Transform(kind, xsize, packing, table));
                        xsize = DivRoundUp(xsize, 1 << packing);
                        break;
                    }
                }
            }

            var pixels = DecodeImage(xsize, height, true);
            for (var i = _transforms.Count - 1; i >= 0; i--)
                pixels = Inverse(_transforms[i], pixels, height);
            return pixels.Length == width * height ? pixels : null;
        }

        private uint[] DecodeImage(int xsize, int ysize, bool mainImage)
        {
            var cacheBits = 0;
            if (Bits.ReadBits(1) == 1)
            {
                cacheBits = (int)Bits.ReadBits(4);
                if (cacheBits is < 1 or > 11)
                    throw new InvalidDataException("A colour cache of an invalid size.");
            }

            var metaBits = 0;
            uint[]? meta = null;
            var groupCount = 1;
            if (mainImage && Bits.ReadBits(1) == 1)
            {
                metaBits = (int)Bits.ReadBits(3) + 2;
                meta = DecodeImage(DivRoundUp(xsize, 1 << metaBits), DivRoundUp(ysize, 1 << metaBits), false);
                foreach (var pixel in meta)
                    groupCount = Math.Max(groupCount, (int)((pixel >> 8) & 0xFFFF) + 1);
            }

            var cacheSize = cacheBits > 0 ? 1 << cacheBits : 0;
            var groups = new PrefixGroup[groupCount];
            for (var g = 0; g < groupCount; g++)
            {
                if (Bits.Overrun)
                    throw new InvalidDataException("The prefix codes end early.");
                var group = new PrefixGroup();
                for (var c = 0; c < 5; c++)
                    group.Codes[c] = ReadPrefixCode(c switch { 0 => NumLiteralCodes + NumLengthCodes + cacheSize, 4 => NumDistanceCodes, _ => NumLiteralCodes });
                groups[g] = group;
            }

            return DecodePixels(xsize, ysize, groups, meta, metaBits, cacheBits);
        }

        private PrefixCode ReadPrefixCode(int alphabetSize)
        {
            var lengths = new int[alphabetSize];
            if (Bits.ReadBits(1) == 1)
            {
                // A simple code: one or two symbols.
                var symbolCount = (int)Bits.ReadBits(1) + 1;
                var first = (int)Bits.ReadBits(Bits.ReadBits(1) == 0 ? 1 : 8);
                if (first >= alphabetSize)
                    throw new InvalidDataException("A symbol outside the alphabet.");
                lengths[first] = 1;
                if (symbolCount == 2)
                {
                    var second = (int)Bits.ReadBits(8);
                    if (second >= alphabetSize)
                        throw new InvalidDataException("A symbol outside the alphabet.");
                    lengths[second] = 1; // the same symbol twice is a one-symbol code
                }
                return new PrefixCode(lengths);
            }

            var codeLengthLengths = new int[CodeLengthCodes];
            var count = (int)Bits.ReadBits(4) + 4;
            for (var i = 0; i < count; i++)
                codeLengthLengths[CodeLengthCodeOrder[i]] = (int)Bits.ReadBits(3);
            var codeLengthCode = new PrefixCode(codeLengthLengths);

            var maxSymbol = alphabetSize;
            if (Bits.ReadBits(1) == 1)
            {
                var lengthBits = 2 + 2 * (int)Bits.ReadBits(3);
                maxSymbol = 2 + (int)Bits.ReadBits(lengthBits);
                if (maxSymbol > alphabetSize)
                    throw new InvalidDataException("More code lengths than symbols.");
            }

            var symbol = 0;
            var previous = 8;
            while (symbol < alphabetSize && maxSymbol-- > 0)
            {
                var code = codeLengthCode.Read(Bits);
                if (code < 16)
                {
                    lengths[symbol++] = code;
                    if (code != 0)
                        previous = code;
                    continue;
                }

                var (value, repeat) = code switch
                {
                    16 => (previous, 3 + (int)Bits.ReadBits(2)),
                    17 => (0, 3 + (int)Bits.ReadBits(3)),
                    _ => (0, 11 + (int)Bits.ReadBits(7)),
                };
                if (symbol + repeat > alphabetSize)
                    throw new InvalidDataException("A run of code lengths past the alphabet.");
                lengths.AsSpan(symbol, repeat).Fill(value);
                symbol += repeat;
            }
            return new PrefixCode(lengths);
        }

        private uint[] DecodePixels(int xsize, int ysize, PrefixGroup[] groups, uint[]? meta, int metaBits, int cacheBits)
        {
            var total = checked(xsize * ysize);
            var pixels = new uint[total];
            var cache = cacheBits > 0 ? new uint[1 << cacheBits] : null;
            var metaWidth = meta is null ? 0 : DivRoundUp(xsize, 1 << metaBits);

            var position = 0;
            while (position < total)
            {
                if (Bits.Overrun)
                    throw new InvalidDataException("The image data ends early.");
                var x = position % xsize;
                var y = position / xsize;
                var group = groups[meta is null ? 0 : (int)((meta[(y >> metaBits) * metaWidth + (x >> metaBits)] >> 8) & 0xFFFF)];

                var symbol = group.Codes[0].Read(Bits);
                if (symbol < NumLiteralCodes)
                {
                    var red = (uint)group.Codes[1].Read(Bits);
                    var blue = (uint)group.Codes[2].Read(Bits);
                    var alpha = (uint)group.Codes[3].Read(Bits);
                    var pixel = alpha << 24 | red << 16 | (uint)symbol << 8 | blue;
                    pixels[position++] = pixel;
                    Insert(cache, cacheBits, pixel);
                }
                else if (symbol < NumLiteralCodes + NumLengthCodes)
                {
                    var length = PrefixValue(symbol - NumLiteralCodes);
                    var distanceSymbol = group.Codes[4].Read(Bits);
                    var distanceCode = PrefixValue(distanceSymbol);
                    var distance = MapDistance(distanceCode, xsize);
                    if (distance > position || position + length > total)
                        throw new InvalidDataException("A backward reference outside the image.");
                    for (var i = 0; i < length; i++, position++)
                    {
                        var pixel = pixels[position - distance];
                        pixels[position] = pixel;
                        Insert(cache, cacheBits, pixel);
                    }
                }
                else
                {
                    var index = symbol - NumLiteralCodes - NumLengthCodes;
                    if (cache is null || index >= cache.Length)
                        throw new InvalidDataException("A colour cache index without a cache.");
                    var pixel = cache[index];
                    pixels[position++] = pixel;
                    Insert(cache, cacheBits, pixel);
                }
            }
            return pixels;
        }

        // A length or distance prefix symbol and its extra bits (the lossless specification, "LZ77 prefix coding").
        private int PrefixValue(int prefix)
        {
            if (prefix < 4)
                return prefix + 1;
            var extraBits = (prefix - 2) >> 1;
            var offset = (2 + (prefix & 1)) << extraBits;
            return offset + (int)Bits.ReadBits(extraBits) + 1;
        }

        private static int MapDistance(int code, int xsize)
        {
            if (code > 120)
                return code - 120;
            var dx = DistanceMap[(code - 1) * 2];
            var dy = DistanceMap[(code - 1) * 2 + 1];
            return Math.Max(1, dx + dy * xsize);
        }

        private static void Insert(uint[]? cache, int cacheBits, uint pixel)
        {
            if (cache is not null)
                cache[(int)(0x1E35A7BDu * pixel >> (32 - cacheBits))] = pixel;
        }
    }

    private static int DivRoundUp(int value, int divisor) => (value + divisor - 1) / divisor;

    private static uint[] Inverse(Transform transform, uint[] pixels, int height)
    {
        var width = transform.Width;
        switch (transform.Kind)
        {
            case TransformKind.SubtractGreen:
                for (var i = 0; i < pixels.Length; i++)
                {
                    var green = (pixels[i] >> 8) & 0xFF;
                    var redBlue = ((pixels[i] & 0x00FF00FF) + (green << 16 | green)) & 0x00FF00FF;
                    pixels[i] = (pixels[i] & 0xFF00FF00) | redBlue;
                }
                return pixels;
            case TransformKind.ColorIndexing:
                return InverseColorIndexing(transform, pixels, height);
            case TransformKind.CrossColor:
                InverseCrossColor(transform, pixels, height);
                return pixels;
            default:
                InversePredictor(transform, pixels, height);
                return pixels;
        }
    }

    private static uint[] InverseColorIndexing(Transform transform, uint[] packed, int height)
    {
        var width = transform.Width;
        var table = transform.Data!;
        var packing = transform.Bits;
        var packedWidth = DivRoundUp(width, 1 << packing);
        var output = new uint[checked(width * height)];
        var bitsPerPixel = 8 >> packing;
        var mask = (1 << bitsPerPixel) - 1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var green = (int)((packed[y * packedWidth + (x >> packing)] >> 8) & 0xFF);
                var index = packing == 0 ? green : (green >> ((x & ((1 << packing) - 1)) * bitsPerPixel)) & mask;
                output[y * width + x] = index < table.Length ? table[index] : 0;
            }
        }
        return output;
    }

    private static void InverseCrossColor(Transform transform, uint[] pixels, int height)
    {
        var width = transform.Width;
        var blocksPerRow = DivRoundUp(width, 1 << transform.Bits);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var element = transform.Data![(y >> transform.Bits) * blocksPerRow + (x >> transform.Bits)];
                var greenToRed = (sbyte)(element & 0xFF);
                var greenToBlue = (sbyte)((element >> 8) & 0xFF);
                var redToBlue = (sbyte)((element >> 16) & 0xFF);

                var pixel = pixels[y * width + x];
                var green = (sbyte)((pixel >> 8) & 0xFF);
                var red = (int)((pixel >> 16) & 0xFF);
                var blue = (int)(pixel & 0xFF);
                red = (red + ((greenToRed * green) >> 5)) & 0xFF;
                blue = (blue + ((greenToBlue * green) >> 5)) & 0xFF;
                blue = (blue + ((redToBlue * (sbyte)red) >> 5)) & 0xFF;
                pixels[y * width + x] = (pixel & 0xFF00FF00) | (uint)red << 16 | (uint)blue;
            }
        }
    }

    private static void InversePredictor(Transform transform, uint[] pixels, int height)
    {
        var width = transform.Width;
        var blocksPerRow = DivRoundUp(width, 1 << transform.Bits);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                uint prediction;
                if (y == 0)
                    prediction = x == 0 ? 0xFF000000 : pixels[i - 1];
                else if (x == 0)
                    prediction = pixels[i - width];
                else
                {
                    var mode = (int)((transform.Data![(y >> transform.Bits) * blocksPerRow + (x >> transform.Bits)] >> 8) & 0xF);
                    prediction = Predict(mode, pixels[i - 1], pixels[i - width], pixels[i - width - 1], pixels[i - width + 1]);
                }
                pixels[i] = AddPixels(pixels[i], prediction);
            }
        }
    }

    private static uint Predict(int mode, uint left, uint top, uint topLeft, uint topRight) => mode switch
    {
        1 => left,
        2 => top,
        3 => topRight,
        4 => topLeft,
        5 => Average2(Average2(left, topRight), top),
        6 => Average2(left, topLeft),
        7 => Average2(left, top),
        8 => Average2(topLeft, top),
        9 => Average2(top, topRight),
        10 => Average2(Average2(left, topLeft), Average2(top, topRight)),
        11 => Select(left, top, topLeft),
        12 => ClampAddSubtractFull(left, top, topLeft),
        13 => ClampAddSubtractHalf(Average2(left, top), topLeft),
        _ => 0xFF000000,
    };

    private static uint AddPixels(uint a, uint b) =>
        ((a & 0xFF00FF00) + (b & 0xFF00FF00) & 0xFF00FF00) | ((a & 0x00FF00FF) + (b & 0x00FF00FF) & 0x00FF00FF);

    private static uint Average2(uint a, uint b) => (((a ^ b) & 0xFEFEFEFE) >> 1) + (a & b);

    private static uint Select(uint left, uint top, uint topLeft)
    {
        var distanceLeft = 0;
        var distanceTop = 0;
        for (var shift = 0; shift < 32; shift += 8)
        {
            var l = (int)((left >> shift) & 0xFF);
            var t = (int)((top >> shift) & 0xFF);
            var tl = (int)((topLeft >> shift) & 0xFF);
            var estimate = l + t - tl;
            distanceLeft += Math.Abs(estimate - l);
            distanceTop += Math.Abs(estimate - t);
        }
        return distanceLeft < distanceTop ? left : top;
    }

    private static uint ClampAddSubtractFull(uint a, uint b, uint c)
    {
        uint result = 0;
        for (var shift = 0; shift < 32; shift += 8)
            result |= (uint)Math.Clamp((int)((a >> shift) & 0xFF) + (int)((b >> shift) & 0xFF) - (int)((c >> shift) & 0xFF), 0, 255) << shift;
        return result;
    }

    private static uint ClampAddSubtractHalf(uint a, uint b)
    {
        uint result = 0;
        for (var shift = 0; shift < 32; shift += 8)
        {
            var x = (int)((a >> shift) & 0xFF);
            var y = (int)((b >> shift) & 0xFF);
            result |= (uint)Math.Clamp(x + (x - y) / 2, 0, 255) << shift;
        }
        return result;
    }
}
