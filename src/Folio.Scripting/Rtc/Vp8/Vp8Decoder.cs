// A managed VP8 decoder (RFC 6386). The structure and the bit-exact arithmetic follow the RFC's reference decoder
// ("dixie", RFC 6386 section 20), Copyright (c) 2010, 2011 Google Inc., BSD-licensed (RFC 6386 section 20.26).
// Differences from that code: frames are reconstructed into bounds-checked managed planes, motion compensation reads
// through clamped coordinates instead of relying on a border, the loop filter runs once over the finished frame, and
// every size comes from a caller-supplied budget. Nothing here uses unsafe code or third-party codec libraries.
using System.Buffers.Binary;
using System.Numerics;

namespace Folio.Scripting.Rtc.Vp8;

internal enum Vp8Status { Ok, Corrupt, Unsupported, TooLarge, NoReference }

/// <summary>
/// Decodes VP8 frames (key and inter, with golden/altref references, segmentation and the normal and simple loop
/// filters). Hostile input never throws and never reads or writes out of bounds: a bad frame returns a status, a frame
/// larger than the pixel cap or the per-document byte budget returns <see cref="Vp8Status.TooLarge"/>, and the decoder
/// then needs a new key frame.
/// </summary>
internal sealed partial class Vp8Decoder : IDisposable
{
    internal const int MaxDimension = 16383;
    internal const int DefaultMaxPixels = 1920 * 1088;
    private const int Border = 16;

    private readonly int _maxPixels;
    private readonly RtcMediaBudget? _budget;
    private long _reserved;
    private bool _disposed;

    private sealed class Frame
    {
        public byte[] Y = [], U = [], V = [];
    }

    private readonly Frame[] _store = [new(), new(), new(), new()];
    private Frame? _cur, _last, _golden, _altref;
    private int _mbCols, _mbRows, _yStride, _uvStride, _yOrigin, _uvOrigin;
    private int _width, _height;
    private bool _haveSize;

    // Header state that persists across frames.
    private bool _isKey;
    private int _version;
    private bool _showFrame;
    private int _part0Size;

    private bool _segEnabled, _segUpdateMap, _segUpdateData, _segAbs;
    private readonly int[] _segTreeProbs = new int[3];
    private readonly int[] _segLf = new int[4], _segQuant = new int[4];

    private bool _lfSimple;
    private int _lfLevel, _lfSharpness;
    private bool _lfDeltaEnabled;
    private readonly int[] _lfRefDelta = new int[4], _lfModeDelta = new int[4];

    private int _partitions;
    private readonly int[] _partitionSize = new int[8];
    private readonly BoolDecoder[] _tokenBool = new BoolDecoder[8];

    private int _qIndex, _y1dcDelta, _y2dcDelta, _y2acDelta, _uvdcDelta, _uvacDelta;

    private bool _refreshLast, _refreshGolden, _refreshAltref, _refreshEntropy;
    private int _copyGolden, _copyAltref;
    private readonly bool[] _signBias = new bool[4];

    // Entropy state, saved and restored when refresh_entropy_probs is 0.
    private sealed class Entropy
    {
        public readonly byte[] Coeff = new byte[1056];
        public readonly byte[] Mv = new byte[38];
        public readonly byte[] YMode = new byte[4];
        public readonly byte[] UvMode = new byte[3];
        public void CopyFrom(Entropy other)
        {
            other.Coeff.CopyTo(Coeff, 0); other.Mv.CopyTo(Mv, 0); other.YMode.CopyTo(YMode, 0); other.UvMode.CopyTo(UvMode, 0);
        }
    }

    private readonly Entropy _entropy = new(), _saved = new();
    private bool _skipEnabled;
    private int _skipProb, _probInter, _probLast, _probGolden;

    // Per-macroblock mode info, with a one-macroblock border on the left and above (all zero: intra, DC_PRED).
    private byte[] _yMode = [], _uvMode = [], _segmentId = [], _refFrame = [], _skip = [], _partitioning = [];
    private short[] _mvX = [], _mvY = [];
    private uint[] _eobMask = [];
    private byte[] _bModes = [];
    private short[] _subMvX = [], _subMvY = [];
    private short[] _coeffs = [];
    private int[] _aboveCtx = [];

    private readonly short[][] _dq = [new short[8], new short[8], new short[8], new short[8]]; // [segment][Y1dc,Y1ac,UVdc,UVac,Y2dc,Y2ac]

    private byte[] _data = [];

    public Vp8Decoder(int maxPixels = DefaultMaxPixels, RtcMediaBudget? budget = null)
    {
        _maxPixels = Math.Clamp(maxPixels, 256, MaxDimension * MaxDimension);
        _budget = budget;
    }

    /// <summary>The visible size of the last decoded key frame, or 0 before one arrives.</summary>
    public int Width => _haveSize ? _width : 0;
    public int Height => _haveSize ? _height : 0;
    /// <summary>True when the last successfully decoded frame had show_frame set.</summary>
    public bool FrameShown => _showFrame;
    public bool HasFrame => _haveSize && _cur is not null;

    /// <summary>True when the frame tag says key frame (first bit clear). The frame must have at least one byte.</summary>
    public static bool IsKeyFrame(ReadOnlySpan<byte> frame) => frame.Length >= 1 && (frame[0] & 1) == 0;

    /// <summary>Reads the dimensions from a key frame header without decoding it. False when it is not a valid key frame header.</summary>
    public static bool TryReadKeyFrameSize(ReadOnlySpan<byte> frame, out int width, out int height)
    {
        width = height = 0;
        if (frame.Length < 10 || (frame[0] & 1) != 0 || frame[3] != 0x9d || frame[4] != 0x01 || frame[5] != 0x2a) return false;
        width = BinaryPrimitives.ReadUInt16LittleEndian(frame[6..]) & 0x3FFF;
        height = BinaryPrimitives.ReadUInt16LittleEndian(frame[8..]) & 0x3FFF;
        return width > 0 && height > 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cur = _last = _golden = _altref = null;
        foreach (var frame in _store) { frame.Y = frame.U = frame.V = []; }
        _haveSize = false;
        if (_reserved > 0) _budget?.ReleaseVideoBytes(_reserved);
        _reserved = 0;
    }

    /// <summary>Drops all reference state; the next frame must be a key frame.</summary>
    public void Reset()
    {
        _cur = _last = _golden = _altref = null;
        _haveSize = false;
    }

    /// <summary>
    /// Decodes one frame. Never throws for malformed input. On any failure the decoder forgets its references so an
    /// inter frame cannot be decoded against a half-updated state; only a key frame recovers it.
    /// </summary>
    public Vp8Status Decode(ReadOnlySpan<byte> data)
    {
        if (_disposed) return Vp8Status.Corrupt;
        try
        {
            var status = DecodeFrame(data);
            if (status != Vp8Status.Ok) Reset();
            return status;
        }
        catch (Exception exception) when (exception is IndexOutOfRangeException or ArgumentException or InvalidOperationException or OverflowException)
        {
            // Defence in depth: every index is bounds-checked by the runtime; a hostile stream that still trips one is a corrupt frame.
            Reset();
            return Vp8Status.Corrupt;
        }
    }

    private Vp8Status DecodeFrame(ReadOnlySpan<byte> data)
    {
        if (data.Length < 3 || data.Length > (1 << 24)) return Vp8Status.Corrupt;
        var raw = data[0] | (data[1] << 8) | (data[2] << 16);
        var key = (raw & 1) == 0;
        var version = (raw >> 1) & 7;
        var show = ((raw >> 4) & 1) != 0;
        var part0 = (raw >> 5) & 0x7FFFF;
        if (version > 3) return Vp8Status.Unsupported;
        var headerBytes = key ? 10 : 3;
        if (data.Length < headerBytes || data.Length < headerBytes + part0) return Vp8Status.Corrupt;
        if (key)
        {
            if (data[3] != 0x9d || data[4] != 0x01 || data[5] != 0x2a) return Vp8Status.Unsupported;
            var width = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]) & 0x3FFF; // the top two bits are a scaling hint for the display
            var height = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]) & 0x3FFF;
            if (width == 0 || height == 0) return Vp8Status.Unsupported;
            if ((long)width * height > _maxPixels) return Vp8Status.TooLarge;
            if (!Allocate(width, height)) return Vp8Status.TooLarge;
        }
        else if (!_haveSize || _last is null || _golden is null || _altref is null) return Vp8Status.NoReference;

        _isKey = key; _version = version; _showFrame = show; _part0Size = part0;
        // Own a copy: the bool decoders keep indexes into it for the duration of the frame.
        if (_data.Length < data.Length) _data = new byte[Math.Max(data.Length, _data.Length * 2)];
        data.CopyTo(_data);
        var length = data.Length;
        var pos = headerBytes;

        var bd = new BoolDecoder(_data, pos, part0);
        if (key)
        {
            bd.GetUInt(2); // color space and clamping type: clamping is always applied, which is harmless when the encoder says it is not needed
            // Key frames reset persistent header state (RFC 6386 sections 9.3, 9.6, 9.11).
            _segEnabled = false; _segUpdateMap = false; _segUpdateData = false; _segAbs = false;
            Array.Clear(_segTreeProbs); Array.Clear(_segLf); Array.Clear(_segQuant);
            _lfSimple = false; _lfLevel = 0; _lfSharpness = 0; _lfDeltaEnabled = false;
            Array.Clear(_lfRefDelta); Array.Clear(_lfModeDelta);
        }
        ParseSegmentation(ref bd);
        ParseLoopFilter(ref bd);
        var partitionStatus = InitTokenPartitions(ref bd, pos + part0, length - pos - part0);
        if (partitionStatus != Vp8Status.Ok) return partitionStatus;
        ParseQuantizer(ref bd);
        ParseReferences(ref bd);

        if (key)
        {
            Vp8Tables.DefaultCoeffProbs.CopyTo(_entropy.Coeff, 0);
            Vp8Tables.DefaultMvProbs.CopyTo(_entropy.Mv, 0);
            _entropy.YMode[0] = 112; _entropy.YMode[1] = 86; _entropy.YMode[2] = 140; _entropy.YMode[3] = 37;
            _entropy.UvMode[0] = 162; _entropy.UvMode[1] = 101; _entropy.UvMode[2] = 204;
        }
        var restoreEntropy = !_refreshEntropy;
        if (restoreEntropy) _saved.CopyFrom(_entropy);
        ParseEntropy(ref bd);

        // Segment ids are only meaningful with segmentation on; key frames without a map start from segment 0.
        if (key && !_segUpdateMap) Array.Clear(_segmentId);
        InitDequant();
        PrepareCurrent();

        Array.Clear(_aboveCtx);
        for (var row = 0; row < _mbRows; row++)
        {
            ModeRow(ref bd, row);
            var partition = row & (_partitions - 1);
            TokenRow(partition, row);
            PredictRow(row);
        }
        if (_lfLevel != 0) LoopFilterFrame();

        if (restoreEntropy) _entropy.CopyFrom(_saved);
        UpdateReferences();
        return Vp8Status.Ok;
    }

    // ---- allocation -----------------------------------------------------------------------------------------------

    private bool Allocate(int width, int height)
    {
        var cols = (width + 15) / 16; var rows = (height + 15) / 16;
        if (_haveSize && cols == _mbCols && rows == _mbRows && width == _width && height == _height) return true;
        var yStride = cols * 16 + 2 * Border;
        var uvStride = cols * 8 + Border;
        var yRows = rows * 16 + 2 * Border;
        var uvRows = rows * 8 + Border;
        var frameBytes = (long)yStride * yRows + 2L * uvStride * uvRows;
        var mbs = (long)(cols + 1) * (rows + 1);
        // Four reference buffers, the mode info, one row of coefficients and two RGBA frames the caller may hold.
        var total = 4 * frameBytes + mbs * (24 + 16 * 5) + (long)cols * 25 * 16 * 2 + 2L * width * height * 4;
        if (_budget is not null)
        {
            if (_reserved > 0) { _budget.ReleaseVideoBytes(_reserved); _reserved = 0; }
            if (!_budget.TryReserveVideoBytes(total)) { _haveSize = false; return false; }
            _reserved = total;
        }
        _mbCols = cols; _mbRows = rows; _width = width; _height = height;
        _yStride = yStride; _uvStride = uvStride;
        _yOrigin = Border * yStride + Border;
        _uvOrigin = (Border / 2) * uvStride + Border / 2;
        foreach (var frame in _store)
        {
            frame.Y = new byte[yStride * yRows];
            frame.U = new byte[uvStride * uvRows];
            frame.V = new byte[uvStride * uvRows];
        }
        _cur = _last = _golden = _altref = null;
        var n = (int)mbs;
        _yMode = new byte[n]; _uvMode = new byte[n]; _segmentId = new byte[n]; _refFrame = new byte[n]; _skip = new byte[n]; _partitioning = new byte[n];
        _mvX = new short[n]; _mvY = new short[n]; _eobMask = new uint[n];
        _bModes = new byte[n * 16]; _subMvX = new short[n * 16]; _subMvY = new short[n * 16];
        _coeffs = new short[cols * 25 * 16];
        _aboveCtx = new int[cols * 9];
        _haveSize = true;
        return true;
    }

    private int Mi(int row, int col) => (row + 1) * (_mbCols + 1) + col + 1;

    private void PrepareCurrent()
    {
        foreach (var frame in _store)
            if (!ReferenceEquals(frame, _last) && !ReferenceEquals(frame, _golden) && !ReferenceEquals(frame, _altref)) { _cur = frame; return; }
        throw new InvalidOperationException("No free frame buffer.");
    }

    private void UpdateReferences()
    {
        var current = _cur!;
        if (_copyAltref == 1) _altref = _last; else if (_copyAltref == 2) _altref = _golden;
        if (_copyGolden == 1) _golden = _last; else if (_copyGolden == 2) _golden = _altref;
        if (_refreshGolden) _golden = current;
        if (_refreshAltref) _altref = current;
        if (_refreshLast) _last = current;
        // A frame that refreshes nothing is shown from _cur and then forgotten; keep a stable view for ToRgba.
        _lastDecoded = current;
    }

    private Frame? _lastDecoded;

    // ---- headers --------------------------------------------------------------------------------------------------

    private void ParseSegmentation(ref BoolDecoder bd)
    {
        _segEnabled = bd.GetBit() != 0;
        if (!_segEnabled) { _segUpdateMap = false; _segUpdateData = false; return; }
        _segUpdateMap = bd.GetBit() != 0;
        _segUpdateData = bd.GetBit() != 0;
        if (_segUpdateData)
        {
            _segAbs = bd.GetBit() != 0;
            for (var i = 0; i < 4; i++) _segQuant[i] = bd.MaybeGetInt(7);
            for (var i = 0; i < 4; i++) _segLf[i] = bd.MaybeGetInt(6);
        }
        if (_segUpdateMap)
            for (var i = 0; i < 3; i++) _segTreeProbs[i] = bd.GetBit() != 0 ? bd.GetUInt(8) : 255;
    }

    private void ParseLoopFilter(ref BoolDecoder bd)
    {
        _lfSimple = bd.GetBit() != 0;
        _lfLevel = bd.GetUInt(6);
        _lfSharpness = bd.GetUInt(3);
        _lfDeltaEnabled = bd.GetBit() != 0;
        if (_lfDeltaEnabled && bd.GetBit() != 0)
        {
            for (var i = 0; i < 4; i++) _lfRefDelta[i] = bd.MaybeGetInt(6);
            for (var i = 0; i < 4; i++) _lfModeDelta[i] = bd.MaybeGetInt(6);
        }
    }

    private Vp8Status InitTokenPartitions(ref BoolDecoder bd, int start, int size)
    {
        _partitions = 1 << bd.GetUInt(2);
        if (size < 3 * (_partitions - 1)) return Vp8Status.Corrupt;
        var table = start;
        var pos = start + 3 * (_partitions - 1);
        var remaining = size - 3 * (_partitions - 1);
        for (var i = 0; i < _partitions; i++)
        {
            int partitionSize;
            if (i < _partitions - 1)
            {
                partitionSize = _data[table] | (_data[table + 1] << 8) | (_data[table + 2] << 16);
                table += 3;
            }
            else partitionSize = remaining;
            if (partitionSize > remaining) return Vp8Status.Corrupt;
            remaining -= partitionSize;
            _partitionSize[i] = partitionSize;
            _tokenBool[i] = new BoolDecoder(_data, pos, partitionSize);
            pos += partitionSize;
        }
        return Vp8Status.Ok;
    }

    private void ParseQuantizer(ref BoolDecoder bd)
    {
        _qIndex = bd.GetUInt(7);
        _y1dcDelta = bd.MaybeGetInt(4);
        _y2dcDelta = bd.MaybeGetInt(4);
        _y2acDelta = bd.MaybeGetInt(4);
        _uvdcDelta = bd.MaybeGetInt(4);
        _uvacDelta = bd.MaybeGetInt(4);
    }

    private void ParseReferences(ref BoolDecoder bd)
    {
        var key = _isKey;
        _refreshGolden = key || bd.GetBit() != 0;
        _refreshAltref = key || bd.GetBit() != 0;
        _copyGolden = key ? 0 : !_refreshGolden ? bd.GetUInt(2) : 0;
        _copyAltref = key ? 0 : !_refreshAltref ? bd.GetUInt(2) : 0;
        _signBias[2] = !key && bd.GetBit() != 0;
        _signBias[3] = !key && bd.GetBit() != 0;
        _refreshEntropy = bd.GetBit() != 0;
        _refreshLast = key || bd.GetBit() != 0;
    }

    private void ParseEntropy(ref BoolDecoder bd)
    {
        var coeff = _entropy.Coeff;
        for (var i = 0; i < 1056; i++)
            if (bd.Get(Vp8Tables.CoeffUpdateProbs[i]) != 0) coeff[i] = (byte)bd.GetUInt(8);
        _skipEnabled = bd.GetBit() != 0;
        if (_skipEnabled) _skipProb = bd.GetUInt(8);
        if (_isKey) return;
        _probInter = bd.GetUInt(8);
        _probLast = bd.GetUInt(8);
        _probGolden = bd.GetUInt(8);
        if (bd.GetBit() != 0) for (var i = 0; i < 4; i++) _entropy.YMode[i] = (byte)bd.GetUInt(8);
        if (bd.GetBit() != 0) for (var i = 0; i < 3; i++) _entropy.UvMode[i] = (byte)bd.GetUInt(8);
        for (var i = 0; i < 2; i++)
            for (var j = 0; j < 19; j++)
                if (bd.Get(Vp8Tables.MvUpdateProbs[i * 19 + j]) != 0)
                {
                    var x = bd.GetUInt(7);
                    _entropy.Mv[i * 19 + j] = (byte)(x != 0 ? x << 1 : 1);
                }
    }

    private static int ClampQ(int q) => q < 0 ? 0 : q > 127 ? 127 : q;

    private void InitDequant()
    {
        for (var i = 0; i < 4; i++)
        {
            var q = _qIndex;
            if (_segEnabled) q = !_segAbs ? q + _segQuant[i] : _segQuant[i];
            var dq = _dq[i];
            dq[0] = Vp8Tables.DcQ[ClampQ(q + _y1dcDelta)];
            dq[1] = Vp8Tables.AcQ[ClampQ(q)];
            var uvdc = Vp8Tables.DcQ[ClampQ(q + _uvdcDelta)];
            dq[2] = (short)(uvdc > 132 ? 132 : uvdc);
            dq[3] = Vp8Tables.AcQ[ClampQ(q + _uvacDelta)];
            dq[4] = (short)(Vp8Tables.DcQ[ClampQ(q + _y2dcDelta)] * 2);
            var y2ac = Vp8Tables.AcQ[ClampQ(q + _y2acDelta)] * 155 / 100;
            dq[5] = (short)(y2ac < 8 ? 8 : y2ac);
        }
    }

    // ---- output ---------------------------------------------------------------------------------------------------

    /// <summary>Converts the last decoded frame to RGBA (BT.601, limited range) into a new array of Width x Height x 4 bytes.</summary>
    public byte[]? ToRgba()
    {
        if (!_haveSize || _lastDecoded is not { } frame) return null;
        var output = GC.AllocateUninitializedArray<byte>(checked(_width * _height * 4));
        ToRgba(frame, output);
        return output;
    }

    /// <summary>Copies the last decoded frame as planar I420 (Y, then U, then V) without padding, for tests and tools.</summary>
    public byte[]? ToI420()
    {
        if (!_haveSize || _lastDecoded is not { } frame) return null;
        var cw = (_width + 1) / 2; var ch = (_height + 1) / 2;
        var output = new byte[_width * _height + 2 * cw * ch];
        var o = 0;
        for (var y = 0; y < _height; y++) { Buffer.BlockCopy(frame.Y, _yOrigin + y * _yStride, output, o, _width); o += _width; }
        for (var y = 0; y < ch; y++) { Buffer.BlockCopy(frame.U, _uvOrigin + y * _uvStride, output, o, cw); o += cw; }
        for (var y = 0; y < ch; y++) { Buffer.BlockCopy(frame.V, _uvOrigin + y * _uvStride, output, o, cw); o += cw; }
        return output;
    }

    private void ToRgba(Frame frame, byte[] output)
    {
        var w = _width;
        for (var y = 0; y < _height; y++)
        {
            var yRow = _yOrigin + y * _yStride;
            var uvRow = _uvOrigin + (y >> 1) * _uvStride;
            var o = y * w * 4;
            for (var x = 0; x < w; x++)
            {
                var c = 298 * (frame.Y[yRow + x] - 16);
                var d = frame.U[uvRow + (x >> 1)] - 128;
                var e = frame.V[uvRow + (x >> 1)] - 128;
                var r = (c + 409 * e + 128) >> 8;
                var g = (c - 100 * d - 208 * e + 128) >> 8;
                var b = (c + 516 * d + 128) >> 8;
                output[o] = (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
                output[o + 1] = (byte)(g < 0 ? 0 : g > 255 ? 255 : g);
                output[o + 2] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
                output[o + 3] = 255;
                o += 4;
            }
        }
    }
}

/// <summary>The VP8 boolean entropy decoder (RFC 6386 section 7). Reading past the end of its partition yields zero bits.</summary>
internal struct BoolDecoder
{
    private readonly byte[] _buffer;
    private int _pos;
    private readonly int _end;
    private uint _value;
    private int _range;
    private int _bitCount;

    public BoolDecoder(byte[] buffer, int start, int length)
    {
        _buffer = buffer;
        _end = Math.Min(buffer.Length, start + Math.Max(0, length));
        _pos = Math.Min(start, _end);
        _range = 255;
        _bitCount = 0;
        _value = 0;
        if (length >= 2) { _value = (uint)((_buffer[_pos] << 8) | _buffer[_pos + 1]); _pos += 2; }
        else _pos = _end;
    }

    public int Get(int probability)
    {
        var split = 1 + (((_range - 1) * probability) >> 8);
        var bigSplit = (uint)split << 8;
        int result;
        if (_value >= bigSplit) { result = 1; _range -= split; _value -= bigSplit; }
        else { result = 0; _range = split; }
        if (_range < 128)
        {
            var shift = BitOperations.LeadingZeroCount((uint)_range) - 24;
            _range <<= shift;
            _value <<= shift;
            _bitCount += shift;
            if (_bitCount >= 8)
            {
                _bitCount -= 8;
                if (_pos < _end) _value |= (uint)_buffer[_pos++] << _bitCount;
            }
        }
        return result;
    }

    public int GetBit() => Get(128);

    public int GetUInt(int bits)
    {
        var z = 0;
        for (var bit = bits - 1; bit >= 0; bit--) z |= GetBit() << bit;
        return z;
    }

    private int GetInt(int bits)
    {
        var z = GetUInt(bits);
        return GetBit() != 0 ? -z : z;
    }

    public int MaybeGetInt(int bits) => GetBit() != 0 ? GetInt(bits) : 0;

    public int ReadTree(int[] tree, byte[] probs, int probOffset = 0)
    {
        var i = 0;
        while ((i = tree[i + Get(probs[probOffset + (i >> 1)])]) > 0) { }
        return -i;
    }
}
