namespace Folio.Scripting.Rtc;

/// <summary>Raised for any malformed or unsupported H.264 data; the decoder catches it and drops the affected NAL unit.</summary>
internal sealed class H264Exception(string message) : Exception(message);

/// <summary>MSB-first reader over an RBSP (emulation prevention already removed). Reading past the end throws.</summary>
internal sealed class H264BitReader
{
    private readonly byte[] _data;
    private readonly int _bitLength;
    private readonly int _stopBit;
    private int _pos;

    internal H264BitReader(byte[] data, int length)
    {
        _data = data;
        _bitLength = length * 8;
        // Position of the rbsp_stop_one_bit: the last set bit of the last non-zero byte.
        var last = length - 1;
        while (last >= 0 && data[last] == 0) last--;
        _stopBit = last < 0 ? 0 : last * 8 + 7 - System.Numerics.BitOperations.TrailingZeroCount(data[last]);
    }

    internal int Position => _pos;

    /// <summary>True while payload bits remain before the rbsp_stop_one_bit.</summary>
    internal bool MoreData => _pos < _stopBit;

    internal bool ByteAligned => (_pos & 7) == 0;

    internal int Peek(int n)
    {
        // n in [1, 24]; bits past the end read as zero.
        var v = 0;
        var byteIndex = _pos >> 3;
        for (var i = 0; i < 4; i++)
        {
            v <<= 8;
            if (byteIndex + i < _data.Length && (byteIndex + i) * 8 < _bitLength) v |= _data[byteIndex + i];
        }
        return (int)(((uint)v << (_pos & 7)) >> (32 - n));
    }

    internal void Skip(int n)
    {
        if (n < 0 || _pos + n > _bitLength) throw new H264Exception("read past end of NAL unit");
        _pos += n;
    }

    internal int U1()
    {
        if (_pos >= _bitLength) throw new H264Exception("read past end of NAL unit");
        var b = (_data[_pos >> 3] >> (7 - (_pos & 7))) & 1;
        _pos++;
        return b;
    }

    internal int U(int n)
    {
        if (n == 0) return 0;
        if (n > 32 || _pos + n > _bitLength) throw new H264Exception("read past end of NAL unit");
        uint v = 0;
        for (var i = 0; i < n; i++) v = v << 1 | (uint)U1();
        return (int)v;
    }

    /// <summary>Exp-Golomb ue(v). Values of 2^31 or more are rejected; no syntax element the decoder reads needs them.</summary>
    internal int Ue()
    {
        var zeros = 0;
        while (U1() == 0)
        {
            if (++zeros > 31) throw new H264Exception("exp-golomb code too long");
        }
        if (zeros == 0) return 0;
        var v = (uint)U(zeros);
        var r = (1u << zeros) - 1 + v;
        if (r > int.MaxValue) throw new H264Exception("exp-golomb value out of range");
        return (int)r;
    }

    internal int Se()
    {
        var k = Ue();
        return (k & 1) != 0 ? (int)((k + 1L) >> 1) : -(k >> 1);
    }

    internal void AlignToByte() => _pos = (_pos + 7) & ~7;

    /// <summary>Copies whole bytes from a byte-aligned position.</summary>
    internal void ReadBytes(byte[] dst, int dstOffset, int count)
    {
        if (!ByteAligned || _pos + count * 8 > _bitLength) throw new H264Exception("PCM samples truncated");
        Buffer.BlockCopy(_data, _pos >> 3, dst, dstOffset, count);
        _pos += count * 8;
    }
}
