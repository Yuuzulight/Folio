using System.Buffers.Binary;

namespace Folio.Imaging;

/// <summary>
/// Folio's JPEG decoder (ITU-T T.81, https://www.w3.org/Graphics/JPEG/itu-t81.pdf, with JFIF colour): 8-bit
/// baseline, extended sequential and progressive Huffman-coded images with one (greyscale) or three (YCbCr)
/// components, any integral sampling factors and restart intervals. Chroma is upsampled by linear interpolation
/// between sample centres. Arithmetic coding, lossless, hierarchical, 12-bit and CMYK images give null, as does
/// malformed or over-limit input. Blocks that a truncated scan does not reach keep what earlier scans gave them (flat
/// grey in a sequential image).
/// </summary>
// ponytail: EXIF orientation, Adobe RGB/CMYK and DCT-domain downscaling are not read yet.
internal ref struct JpegDecoder
{
    // T.81 figure A.6: zigzag index -> natural (row-major) index.
    private static readonly byte[] ZigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21,
        28, 35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61,
        54, 47, 55, 62, 63,
    ];

    // IdctBasis[x * 8 + u] = C(u) / 2 * cos((2x + 1) u pi / 16), T.81 A.3.3.
    private static readonly float[] IdctBasis = BuildIdctBasis();

    private sealed class Component(int id, int h, int v, int quantTable)
    {
        public readonly int Id = id, H = h, V = v, QuantTable = quantTable;
        public int BlocksPerLine, BlocksPerColumn;
        public short[] Coefficients = [];
        public Huffman? Dc, Ac;
        public int Predictor;
    }

    private sealed class Huffman
    {
        public readonly int[] MaxCode = new int[17], ValueOffset = new int[17];
        public byte[] Values = [];
    }

    private readonly ReadOnlySpan<byte> _data;
    private int _pos;
    private readonly int[]?[] _quant = new int[4][];
    private readonly Huffman?[] _tables = new Huffman?[8];
    private Component[] _components = [];
    private int _width, _height, _maxH, _maxV, _mcusPerLine, _mcusPerColumn, _restartInterval;
    private bool _progressive;
    private int _scans;

    // Progressive images use about a dozen scans; each extra scan can make the decoder walk every block again.
    private const int MaxScans = 100;

    // Entropy-coded segment state.
    private int _bits, _bitCount, _eobRun, _ss, _se, _ah, _al;
    private bool _exhausted;

    private JpegDecoder(ReadOnlySpan<byte> data) => _data = data;

    public static bool CanDecode(ReadOnlySpan<byte> header) => header is [0xFF, 0xD8, 0xFF, ..];

    public static DecodedImage? Decode(ReadOnlySpan<byte> data, long maxPixels = ImageLimits.MaxPixels)
    {
        if (!CanDecode(data))
            return null;
        try
        {
            return new JpegDecoder(data).Run(maxPixels);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private DecodedImage? Run(long maxPixels)
    {
        _pos = 2;
        while (NextMarker() is int marker && marker != 0xD9)
        {
            if (marker is >= 0xD0 and <= 0xD8 or 0x01)
                continue; // RSTn, SOI and TEM carry no segment.

            var length = U16(_pos);
            if (length < 2 || _pos + length > _data.Length)
                throw new InvalidDataException("Truncated JPEG segment.");
            var segment = _data.Slice(_pos + 2, length - 2);
            _pos += length;

            switch (marker)
            {
                case 0xC0 or 0xC1 or 0xC2:
                    if (!ReadFrame(segment, marker == 0xC2, maxPixels))
                        return null;
                    break;
                case 0xC3 or (>= 0xC5 and <= 0xC7) or (>= 0xC9 and <= 0xCB) or (>= 0xCD and <= 0xCF):
                    return null; // Lossless, hierarchical and arithmetic-coded frames.
                case 0xC4:
                    ReadHuffmanTables(segment);
                    break;
                case 0xDB:
                    ReadQuantTables(segment);
                    break;
                case 0xDD:
                    _restartInterval = segment.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(segment) : 0;
                    break;
                case 0xDA:
                    if (++_scans <= MaxScans)
                        DecodeScan(segment);
                    break;
            }
        }
        return _scans > 0 ? Output() : null;
    }

    /// <summary>The next marker code at or after the current position, skipping stray bytes and fill bytes; null at the end.</summary>
    private int? NextMarker()
    {
        for (; _pos + 1 < _data.Length; _pos++)
        {
            if (_data[_pos] == 0xFF && _data[_pos + 1] is not (0x00 or 0xFF))
            {
                _pos += 2;
                return _data[_pos - 1];
            }
        }
        return null;
    }

    private int U16(int at) =>
        at + 2 <= _data.Length ? BinaryPrimitives.ReadUInt16BigEndian(_data[at..]) : throw new InvalidDataException("Truncated JPEG.");

    private bool ReadFrame(ReadOnlySpan<byte> s, bool progressive, long maxPixels)
    {
        if (_components.Length > 0 || s.Length < 6)
            throw new InvalidDataException("Bad frame header.");
        var count = s[5];
        if (s[0] != 8 || count is not (1 or 3))
            return false; // 12-bit precision, CMYK and other component counts are not supported.
        _height = BinaryPrimitives.ReadUInt16BigEndian(s[1..]);
        _width = BinaryPrimitives.ReadUInt16BigEndian(s[3..]);
        if (!ImageLimits.Allows(_width, _height, maxPixels) || s.Length < 6 + 3 * count)
            return false;

        _progressive = progressive;
        _components = new Component[count];
        for (var i = 0; i < count; i++)
        {
            var c = s.Slice(6 + 3 * i, 3);
            _components[i] = new Component(c[0], c[1] >> 4, c[1] & 15, c[2]);
            if (_components[i].H is < 1 or > 4 || _components[i].V is < 1 or > 4 || c[2] > 3)
                throw new InvalidDataException("Bad component.");
        }
        _maxH = _components.Max(c => c.H);
        _maxV = _components.Max(c => c.V);
        _mcusPerLine = (_width + 8 * _maxH - 1) / (8 * _maxH);
        _mcusPerColumn = (_height + 8 * _maxV - 1) / (8 * _maxV);
        foreach (var c in _components)
        {
            if (_maxH % c.H != 0 || _maxV % c.V != 0)
                return false;
            c.BlocksPerLine = _mcusPerLine * c.H;
            c.BlocksPerColumn = _mcusPerColumn * c.V;
            c.Coefficients = new short[c.BlocksPerLine * c.BlocksPerColumn * 64];
        }
        return true;
    }

    // T.81 B.2.4.2 and annex C.
    private void ReadHuffmanTables(ReadOnlySpan<byte> s)
    {
        while (s.Length > 0)
        {
            if (s.Length < 17 || (s[0] >> 4) > 1 || (s[0] & 15) > 3)
                throw new InvalidDataException("Bad Huffman table.");
            var table = new Huffman();
            var counts = s.Slice(1, 16);
            var total = 0;
            var code = 0;
            for (var length = 1; length <= 16; length++)
            {
                var n = counts[length - 1];
                table.ValueOffset[length] = total - code;
                code += n;
                total += n;
                table.MaxCode[length] = n > 0 ? code - 1 : -1;
                code <<= 1;
            }
            if (total > 256 || s.Length < 17 + total)
                throw new InvalidDataException("Bad Huffman table.");
            table.Values = s.Slice(17, total).ToArray();
            _tables[(s[0] >> 4) * 4 + (s[0] & 15)] = table;
            s = s[(17 + total)..];
        }
    }

    // T.81 B.2.4.1: tables are stored in natural order.
    private void ReadQuantTables(ReadOnlySpan<byte> s)
    {
        while (s.Length > 0)
        {
            var wide = s[0] >> 4 == 1;
            var size = 1 + 64 * (wide ? 2 : 1);
            if (s[0] >> 4 > 1 || (s[0] & 15) > 3 || s.Length < size)
                throw new InvalidDataException("Bad quantization table.");
            var table = new int[64];
            for (var k = 0; k < 64; k++)
                table[ZigZag[k]] = wide ? BinaryPrimitives.ReadUInt16BigEndian(s[(1 + 2 * k)..]) : s[1 + k];
            _quant[s[0] & 15] = table;
            s = s[size..];
        }
    }

    // T.81 B.2.3, then the entropy-coded data up to the next marker.
    private void DecodeScan(ReadOnlySpan<byte> s)
    {
        if (_components.Length == 0 || s.Length < 1 || s.Length < 4 + 2 * s[0] || s[0] is < 1 or > 4)
            throw new InvalidDataException("Bad scan header.");
        var scan = new Component[s[0]];
        for (var i = 0; i < scan.Length; i++)
        {
            var id = s[1 + 2 * i];
            var tables = s[2 + 2 * i];
            var c = Array.Find(_components, c => c.Id == id) ?? throw new InvalidDataException("Unknown scan component.");
            c.Dc = _tables[(tables >> 4) & 3];
            c.Ac = _tables[4 + (tables & 3)];
            scan[i] = c;
        }
        var p = 1 + 2 * scan.Length;
        (_ss, _se, _ah, _al) = (s[p], s[p + 1], s[p + 2] >> 4, s[p + 2] & 15);
        if (!_progressive)
            (_ss, _se, _ah, _al) = (0, 63, 0, 0);
        else if (_se > 63 || _ss > _se || (_ss == 0) != (_se == 0) || (_ss > 0 && scan.Length != 1) || _al > 13)
            throw new InvalidDataException("Bad progressive scan.");
        foreach (var c in scan)
        {
            if ((_ss == 0 && _ah == 0 && c.Dc is null) || (_se > 0 && c.Ac is null))
                throw new InvalidDataException("Missing Huffman table.");
            c.Predictor = 0;
        }
        _bits = _bitCount = _eobRun = 0;
        _exhausted = false;

        // Non-interleaved scans cover only the component's own blocks; interleaved ones go MCU by MCU.
        var single = scan.Length == 1 ? scan[0] : null;
        var perLine = single is null ? _mcusPerLine : (((_width * single.H + _maxH - 1) / _maxH) + 7) / 8;
        var units = single is null ? _mcusPerLine * _mcusPerColumn : perLine * ((((_height * single.V + _maxV - 1) / _maxV) + 7) / 8);
        for (var n = 0; n < units; n++)
        {
            if (_restartInterval > 0 && n > 0 && n % _restartInterval == 0)
                Restart(scan);
            if (_exhausted)
            {
                // Out of data: the rest of the interval keeps its coefficients, as does the scan without a later restart marker.
                if (_restartInterval == 0 || !RestartAhead())
                    break;
                n += _restartInterval - 1 - n % _restartInterval;
                continue;
            }
            int row = n / perLine, column = n % perLine;
            if (single is not null)
            {
                DecodeBlock(single, (row * single.BlocksPerLine + column) * 64);
                continue;
            }
            foreach (var c in scan)
            {
                for (var v = 0; v < c.V; v++)
                    for (var h = 0; h < c.H; h++)
                        DecodeBlock(c, ((row * c.V + v) * c.BlocksPerLine + column * c.H + h) * 64);
            }
        }
    }

    private void Restart(Component[] scan)
    {
        _bits = _bitCount = _eobRun = 0;
        foreach (var c in scan)
            c.Predictor = 0;
        if (RestartAhead())
        {
            NextMarker();
            _exhausted = false;
        }
    }

    // Other markers are left for the segment loop.
    private bool RestartAhead()
    {
        var at = _pos;
        var marker = NextMarker();
        _pos = at;
        return marker is >= 0xD0 and <= 0xD7;
    }

    // T.81 F.2.2 (sequential) and G.1.2 (progressive): sequential blocks decode as a DC-first then an AC-first scan.
    private void DecodeBlock(Component c, int block)
    {
        var coefficients = c.Coefficients.AsSpan(block, 64);
        if (_ss == 0)
        {
            if (_ah == 0)
            {
                var size = DecodeHuffman(c.Dc!);
                if (size > 11)
                    throw new InvalidDataException("Bad DC difference.");
                c.Predictor += Extend(Receive(size), size);
                coefficients[0] = (short)(c.Predictor << _al);
            }
            else if (Receive(1) != 0)
            {
                coefficients[0] |= (short)(1 << _al);
            }
            if (_se == 0)
                return;
        }

        var start = Math.Max(_ss, 1);
        if (_ah == 0)
            DecodeAcFirst(c.Ac!, coefficients, start);
        else
            DecodeAcRefine(c.Ac!, coefficients, start);
    }

    private void DecodeAcFirst(Huffman table, Span<short> coefficients, int k)
    {
        if (_eobRun > 0)
        {
            _eobRun--;
            return;
        }
        for (; k <= _se; k++)
        {
            var rs = DecodeHuffman(table);
            int run = rs >> 4, size = rs & 15;
            if (size == 0)
            {
                if (run < 15)
                {
                    _eobRun = (1 << run) + Receive(run) - 1;
                    return;
                }
                k += 15;
                continue;
            }
            k += run;
            if (k > _se)
                return;
            coefficients[ZigZag[k]] = (short)(Extend(Receive(size), size) << _al);
        }
    }

    // T.81 G.1.2.3: correction bits for known non-zero coefficients, new coefficients of magnitude 1.
    private void DecodeAcRefine(Huffman table, Span<short> coefficients, int k)
    {
        int plus = 1 << _al, minus = -1 << _al;
        if (_eobRun == 0)
        {
            for (; k <= _se; k++)
            {
                var rs = DecodeHuffman(table);
                int run = rs >> 4, value = 0;
                if ((rs & 15) != 0)
                {
                    value = Receive(1) != 0 ? plus : minus;
                }
                else if (run < 15)
                {
                    _eobRun = (1 << run) + Receive(run);
                    break;
                }

                for (; k <= _se; k++)
                {
                    ref var coefficient = ref coefficients[ZigZag[k]];
                    if (coefficient != 0)
                        Refine(ref coefficient, plus, minus);
                    else if (--run < 0)
                        break;
                }
                if (value != 0 && k <= _se)
                    coefficients[ZigZag[k]] = (short)value;
            }
        }

        if (_eobRun > 0)
        {
            for (; k <= _se; k++)
            {
                ref var coefficient = ref coefficients[ZigZag[k]];
                if (coefficient != 0)
                    Refine(ref coefficient, plus, minus);
            }
            _eobRun--;
        }
    }

    private void Refine(ref short coefficient, int plus, int minus)
    {
        if (Receive(1) != 0 && (coefficient & plus) == 0)
            coefficient += (short)(coefficient >= 0 ? plus : minus);
    }

    // T.81 F.2.2.3 (DECODE) with the MAXCODE/VALPTR tables of F.2.2.3 and C.
    private int DecodeHuffman(Huffman table)
    {
        var code = 0;
        for (var length = 1; length <= 16; length++)
        {
            code = (code << 1) | Receive(1);
            if (code <= table.MaxCode[length])
            {
                var index = table.ValueOffset[length] + code;
                return (uint)index < (uint)table.Values.Length ? table.Values[index] : throw new InvalidDataException("Bad Huffman code.");
            }
        }
        throw new InvalidDataException("Bad Huffman code.");
    }

    /// <summary>
    /// The next <paramref name="count"/> bits. Past a marker or the end of the data, bits read as zero and the scan
    /// is exhausted.
    /// </summary>
    private int Receive(int count)
    {
        var result = 0;
        for (var i = 0; i < count; i++)
        {
            if (_bitCount == 0)
            {
                _bits = 0;
                if (_pos < _data.Length && _data[_pos] != 0xFF)
                {
                    _bits = _data[_pos++];
                }
                else if (_pos + 1 < _data.Length && _data[_pos + 1] == 0x00)
                {
                    _bits = 0xFF;
                    _pos += 2;
                }
                else
                {
                    _exhausted = true;
                }
                _bitCount = 8;
            }
            _bitCount--;
            result = (result << 1) | ((_bits >> _bitCount) & 1);
        }
        return result;
    }

    // T.81 F.2.2.1 (EXTEND).
    private static int Extend(int value, int size) => size == 0 ? 0 : value < 1 << (size - 1) ? value - (1 << size) + 1 : value;

    private DecodedImage Output()
    {
        var planes = new byte[_components.Length][];
        for (var i = 0; i < planes.Length; i++)
            planes[i] = Upsample(_components[i]);

        var pixels = new byte[_width * _height * 4];
        for (var i = 0; i < _width * _height; i++)
        {
            var target = pixels.AsSpan(i * 4, 4);
            if (planes.Length == 1)
            {
                target[0] = target[1] = target[2] = planes[0][i];
            }
            else
            {
                // JFIF YCbCr -> RGB.
                float y = planes[0][i], cb = planes[1][i] - 128f, cr = planes[2][i] - 128f;
                target[0] = Clamp(y + 1.402f * cr);
                target[1] = Clamp(y - 0.344136f * cb - 0.714136f * cr);
                target[2] = Clamp(y + 1.772f * cb);
            }
            target[3] = 255;
        }
        return new DecodedImage(_width, _height, pixels);
    }

    /// <summary>The component's samples after the inverse DCT, interpolated to the full image size.</summary>
    private byte[] Upsample(Component c)
    {
        var stride = c.BlocksPerLine * 8;
        var samples = new byte[stride * c.BlocksPerColumn * 8];
        var quant = _quant[c.QuantTable] ?? throw new InvalidDataException("Missing quantization table.");
        Span<float> block = stackalloc float[64];
        for (var by = 0; by < c.BlocksPerColumn; by++)
        {
            for (var bx = 0; bx < c.BlocksPerLine; bx++)
            {
                var coefficients = c.Coefficients.AsSpan((by * c.BlocksPerLine + bx) * 64, 64);
                for (var i = 0; i < 64; i++)
                    block[i] = coefficients[i] * quant[i];
                InverseDct(block);
                for (var y = 0; y < 8; y++)
                    for (var x = 0; x < 8; x++)
                        samples[(by * 8 + y) * stride + bx * 8 + x] = Clamp(block[y * 8 + x] + 128);
            }
        }

        int scaleX = _maxH / c.H, scaleY = _maxV / c.V;
        int width = (_width + scaleX - 1) / scaleX, height = (_height + scaleY - 1) / scaleY;
        var result = new byte[_width * _height];
        for (var y = 0; y < _height; y++)
        {
            var sy = Math.Clamp((y + 0.5f) / scaleY - 0.5f, 0, height - 1);
            int y0 = (int)sy, y1 = Math.Min(y0 + 1, height - 1);
            var fy = sy - y0;
            for (var x = 0; x < _width; x++)
            {
                var sx = Math.Clamp((x + 0.5f) / scaleX - 0.5f, 0, width - 1);
                int x0 = (int)sx, x1 = Math.Min(x0 + 1, width - 1);
                var fx = sx - x0;
                var top = samples[y0 * stride + x0] * (1 - fx) + samples[y0 * stride + x1] * fx;
                var bottom = samples[y1 * stride + x0] * (1 - fx) + samples[y1 * stride + x1] * fx;
                result[y * _width + x] = Clamp(top * (1 - fy) + bottom * fy);
            }
        }
        return result;
    }

    // Separable 8x8 inverse DCT in place, T.81 A.3.3.
    // ponytail: direct 8-point sums; a factored fast IDCT when decode time shows in the benchmarks.
    private static void InverseDct(Span<float> block)
    {
        Span<float> temp = stackalloc float[64];
        for (var row = 0; row < 8; row++)
        {
            for (var x = 0; x < 8; x++)
            {
                var sum = 0f;
                for (var u = 0; u < 8; u++)
                    sum += IdctBasis[x * 8 + u] * block[row * 8 + u];
                temp[row * 8 + x] = sum;
            }
        }
        for (var column = 0; column < 8; column++)
        {
            for (var y = 0; y < 8; y++)
            {
                var sum = 0f;
                for (var v = 0; v < 8; v++)
                    sum += IdctBasis[y * 8 + v] * temp[v * 8 + column];
                block[y * 8 + column] = sum;
            }
        }
    }

    private static float[] BuildIdctBasis()
    {
        var basis = new float[64];
        for (var x = 0; x < 8; x++)
            for (var u = 0; u < 8; u++)
                basis[x * 8 + u] = (float)((u == 0 ? Math.Sqrt(0.5) : 1) / 2 * Math.Cos((2 * x + 1) * u * Math.PI / 16));
        return basis;
    }

    private static byte Clamp(float value) => (byte)Math.Clamp((int)MathF.Round(value), 0, 255);
}
