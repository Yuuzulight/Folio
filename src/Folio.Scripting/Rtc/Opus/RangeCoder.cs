// Range coder of RFC 6716 section 4.1, translated to managed C#.
// Derived from the Opus reference implementation:
//   Copyright (c) 2001-2011 Timothy B. Terriberry, Copyright (c) 2008-2009 Xiph.Org Foundation.
//   Redistribution and use are permitted under the BSD 3-clause licence reproduced in docs/webrtc-opus.md.
namespace Folio.Scripting.Rtc.Opus;

internal static class RangeConstants
{
    internal const int SymBits = 8;
    internal const uint SymMax = 255;
    internal const int CodeBits = 32;
    internal const int CodeShift = CodeBits - SymBits - 1;
    internal const uint CodeTop = 1u << (CodeBits - 1);
    internal const uint CodeBot = CodeTop >> SymBits;
    internal const int CodeExtra = (CodeBits - 2) % SymBits + 1;
    internal const int WindowSize = 32;
    internal const int UintBits = 8;
    internal const int BitRes = 3;

    internal static int Ilog(uint v) => v == 0 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount(v);
}

/// <summary>The decoding half. Reads past the end of the buffer return zero bytes, so hostile input cannot overrun it.</summary>
internal sealed class RangeDecoder
{
    private readonly byte[] _buf;
    private readonly int _start;
    private uint _storage;
    private uint _endOffs;
    private uint _endWindow;
    private int _nendBits;
    internal int NbitsTotal;
    private uint _offs;
    internal uint Rng;
    private uint _val;
    private uint _ext;
    private int _rem;
    internal bool Error;

    internal RangeDecoder(byte[] buf, int start, int length)
    {
        _buf = buf; _start = start; _storage = (uint)length;
        NbitsTotal = RangeConstants.CodeBits + 1 - (RangeConstants.CodeBits - RangeConstants.CodeExtra) / RangeConstants.SymBits * RangeConstants.SymBits;
        Rng = 1u << RangeConstants.CodeExtra;
        _rem = ReadByte();
        _val = Rng - 1 - (uint)(_rem >> (RangeConstants.SymBits - RangeConstants.CodeExtra));
        Normalize();
    }

    internal uint Storage => _storage;
    internal void ReduceStorage(int bytes) => _storage = bytes >= 0 && (uint)bytes <= _storage ? _storage - (uint)bytes : 0;
    internal int Tell => NbitsTotal - RangeConstants.Ilog(Rng);
    internal uint TellFrac
    {
        get
        {
            ReadOnlySpan<uint> correction = [35733, 38967, 42495, 46340, 50535, 55109, 60097, 65535];
            var nbits = (uint)NbitsTotal << RangeConstants.BitRes;
            var l = RangeConstants.Ilog(Rng);
            var r = Rng >> (l - 16);
            var b = (r >> 12) - 8;
            if (r > correction[(int)b]) b++;
            l = (l << 3) + (int)b;
            return nbits - (uint)l;
        }
    }

    internal uint Range => Rng;

    private int ReadByte() => _offs < _storage ? _buf[_start + (int)_offs++] : 0;
    private int ReadByteFromEnd() => _endOffs < _storage ? _buf[_start + (int)(_storage - ++_endOffs)] : 0;

    private void Normalize()
    {
        while (Rng <= RangeConstants.CodeBot)
        {
            NbitsTotal += RangeConstants.SymBits;
            Rng <<= RangeConstants.SymBits;
            var sym = _rem;
            _rem = ReadByte();
            sym = (sym << RangeConstants.SymBits | _rem) >> (RangeConstants.SymBits - RangeConstants.CodeExtra);
            _val = ((_val << RangeConstants.SymBits) + (RangeConstants.SymMax & ~(uint)sym)) & (RangeConstants.CodeTop - 1);
        }
    }

    internal uint Decode(uint ft)
    {
        _ext = Rng / ft;
        var s = _val / _ext;
        return ft - Math.Min(s + 1, ft);
    }

    internal uint DecodeBin(int bits)
    {
        _ext = Rng >> bits;
        var s = _val / _ext;
        return (1u << bits) - Math.Min(s + 1u, 1u << bits);
    }

    internal void Update(uint fl, uint fh, uint ft)
    {
        var s = _ext * (ft - fh);
        _val -= s;
        Rng = fl > 0 ? _ext * (fh - fl) : Rng - s;
        Normalize();
    }

    internal int BitLogp(int logp)
    {
        var r = Rng; var d = _val;
        var s = r >> logp;
        var ret = d < s;
        if (!ret) _val = d - s;
        Rng = ret ? s : r - s;
        Normalize();
        return ret ? 1 : 0;
    }

    internal int Icdf(ReadOnlySpan<byte> icdf, int ftb)
    {
        var s = Rng; var d = _val;
        var r = s >> ftb;
        var ret = -1; uint t;
        do { t = s; s = r * icdf[++ret]; } while (d < s);
        _val = d - s; Rng = t - s;
        Normalize();
        return ret;
    }

    internal int Icdf16(ReadOnlySpan<ushort> icdf, int ftb)
    {
        var s = Rng; var d = _val;
        var r = s >> ftb;
        var ret = -1; uint t;
        do { t = s; s = r * icdf[++ret]; } while (d < s);
        _val = d - s; Rng = t - s;
        Normalize();
        return ret;
    }

    internal uint Uint(uint ft)
    {
        if (ft <= 1) return 0; // hostile or degenerate sizes decode to zero without consuming bits
        ft--;
        var ftb = RangeConstants.Ilog(ft);
        if (ftb > RangeConstants.UintBits)
        {
            ftb -= RangeConstants.UintBits;
            var f = (ft >> ftb) + 1;
            var s = Decode(f);
            Update(s, s + 1, f);
            var t = s << ftb | Bits(ftb);
            if (t <= ft) return t;
            Error = true;
            return ft;
        }
        ft++;
        var sym = Decode(ft);
        Update(sym, sym + 1, ft);
        return sym;
    }

    internal uint Bits(int bits)
    {
        var window = _endWindow;
        var available = _nendBits;
        if ((uint)available < (uint)bits)
        {
            do
            {
                window |= (uint)ReadByteFromEnd() << available;
                available += RangeConstants.SymBits;
            } while (available <= RangeConstants.WindowSize - RangeConstants.SymBits);
        }
        var ret = window & ((1u << bits) - 1u);
        window >>= bits;
        available -= bits;
        _endWindow = window;
        _nendBits = available;
        NbitsTotal += bits;
        return ret;
    }

    /// <summary>The Laplace decoder of RFC 6716 section 4.3.2.1 (energy deltas).</summary>
    internal int Laplace(uint fs, int decay)
    {
        const uint minp = 1, nmin = 16;
        var val = 0;
        var fm = DecodeBin(15);
        uint fl = 0;
        if (fm >= fs)
        {
            val++;
            fl = fs;
            fs = (uint)(((32768 - minp * (2 * nmin) - fs) * (uint)(16384 - decay)) >> 15) + minp;
            while (fs > minp && fm >= fl + 2 * fs)
            {
                fs *= 2;
                fl += fs;
                fs = (uint)(((fs - 2 * minp) * (uint)decay) >> 15);
                fs += minp;
                val++;
            }
            if (fs <= minp)
            {
                var di = (int)((fm - fl) >> 1);
                val += di;
                fl += (uint)(2 * di) * minp;
            }
            if (fm < fl + fs) val = -val; else fl += fs;
        }
        Update(fl, Math.Min(fl + fs, 32768), 32768);
        return val;
    }
}

/// <summary>The encoding half of the range coder, writing into a fixed-size buffer.</summary>
internal sealed class RangeEncoder
{
    private readonly byte[] _buf;
    private uint _storage;
    private uint _endOffs;
    private uint _endWindow;
    private int _nendBits;
    internal int NbitsTotal;
    private uint _offs;
    internal uint Rng;
    private uint _val;
    private uint _ext;
    private int _rem;
    internal bool Error;

    internal RangeEncoder(byte[] buf, int size)
    {
        _buf = buf; _storage = (uint)size;
        NbitsTotal = RangeConstants.CodeBits + 1;
        Rng = RangeConstants.CodeTop;
        _rem = -1;
    }

    internal int Tell => NbitsTotal - RangeConstants.Ilog(Rng);
    internal uint TellFrac
    {
        get
        {
            ReadOnlySpan<uint> correction = [35733, 38967, 42495, 46340, 50535, 55109, 60097, 65535];
            var nbits = (uint)NbitsTotal << RangeConstants.BitRes;
            var l = RangeConstants.Ilog(Rng);
            var r = Rng >> (l - 16);
            var b = (r >> 12) - 8;
            if (r > correction[(int)b]) b++;
            l = (l << 3) + (int)b;
            return nbits - (uint)l;
        }
    }
    internal uint Storage => _storage;

    private bool WriteByte(uint value)
    {
        if (_offs + _endOffs >= _storage) { Error = true; return false; }
        _buf[_offs++] = (byte)value;
        return true;
    }

    private bool WriteByteAtEnd(uint value)
    {
        if (_offs + _endOffs >= _storage) { Error = true; return false; }
        _buf[_storage - ++_endOffs] = (byte)value;
        return true;
    }

    private void CarryOut(int c)
    {
        if (c != RangeConstants.SymMax)
        {
            var carry = c >> RangeConstants.SymBits;
            if (_rem >= 0) WriteByte((uint)(_rem + carry));
            if (_ext > 0)
            {
                var sym = (RangeConstants.SymMax + (uint)carry) & RangeConstants.SymMax;
                do WriteByte(sym); while (--_ext > 0);
            }
            _rem = c & (int)RangeConstants.SymMax;
        }
        else _ext++;
    }

    private void Normalize()
    {
        while (Rng <= RangeConstants.CodeBot)
        {
            CarryOut((int)(_val >> RangeConstants.CodeShift));
            _val = (_val << RangeConstants.SymBits) & (RangeConstants.CodeTop - 1);
            Rng <<= RangeConstants.SymBits;
            NbitsTotal += RangeConstants.SymBits;
        }
    }

    internal void Encode(uint fl, uint fh, uint ft)
    {
        var r = Rng / ft;
        if (fl > 0) { _val += Rng - r * (ft - fl); Rng = r * (fh - fl); }
        else Rng -= r * (ft - fh);
        Normalize();
    }

    internal void EncodeBin(uint fl, uint fh, int bits)
    {
        var r = Rng >> bits;
        if (fl > 0) { _val += Rng - r * ((1u << bits) - fl); Rng = r * (fh - fl); }
        else Rng -= r * ((1u << bits) - fh);
        Normalize();
    }

    internal void BitLogp(int val, int logp)
    {
        var r = Rng; var l = _val;
        var s = r >> logp;
        r -= s;
        if (val != 0) _val = l + r;
        Rng = val != 0 ? s : r;
        Normalize();
    }

    internal void Icdf(int s, ReadOnlySpan<byte> icdf, int ftb)
    {
        var r = Rng >> ftb;
        if (s > 0) { _val += Rng - r * icdf[s - 1]; Rng = r * (uint)(icdf[s - 1] - icdf[s]); }
        else Rng -= r * icdf[s];
        Normalize();
    }

    internal void Uint(uint fl, uint ft)
    {
        ft--;
        var ftb = RangeConstants.Ilog(ft);
        if (ftb > RangeConstants.UintBits)
        {
            ftb -= RangeConstants.UintBits;
            var f = (ft >> ftb) + 1;
            var low = fl >> ftb;
            Encode(low, low + 1, f);
            Bits(fl & ((1u << ftb) - 1u), ftb);
        }
        else Encode(fl, fl + 1, ft + 1);
    }

    internal void Bits(uint fl, int bits)
    {
        var window = _endWindow;
        var used = _nendBits;
        if (used + bits > RangeConstants.WindowSize)
        {
            do
            {
                WriteByteAtEnd(window & RangeConstants.SymMax);
                window >>= RangeConstants.SymBits;
                used -= RangeConstants.SymBits;
            } while (used >= RangeConstants.SymBits);
        }
        window |= fl << used;
        used += bits;
        _endWindow = window;
        _nendBits = used;
        NbitsTotal += bits;
    }

    internal void Laplace(ref int value, uint fs, int decay)
    {
        const uint minp = 1, nmin = 16;
        uint fl = 0;
        var val = value;
        if (val != 0)
        {
            var s = val < 0 ? -1 : 0;
            val = (val + s) ^ s;
            fl = fs;
            fs = (uint)(((32768 - minp * (2 * nmin) - fs) * (uint)(16384 - decay)) >> 15);
            int i;
            for (i = 1; fs > 0 && i < val; i++)
            {
                fs *= 2;
                fl += fs + 2 * minp;
                fs = (fs * (uint)decay) >> 15;
            }
            if (fs == 0)
            {
                var ndiMax = (int)((32768 - fl + minp - 1) >> 0);
                ndiMax = (ndiMax - s) >> 1;
                var di = Math.Min(val - i, ndiMax - 1);
                fl += (uint)(2 * di + 1 + s) * minp;
                fs = Math.Min(minp, 32768 - fl);
                value = (i + di + s) ^ s;
            }
            else
            {
                fs += minp;
                fl += fs & ~(uint)s;
            }
        }
        EncodeBin(fl, fl + fs, 15);
    }

    /// <summary>Finishes the stream and returns the number of bytes to transmit (all of the buffer up to the end bits, which are merged).</summary>
    internal void Done()
    {
        var l = RangeConstants.CodeBits - RangeConstants.Ilog(Rng);
        var msk = (RangeConstants.CodeTop - 1) >> l;
        var end = (_val + msk) & ~msk;
        if ((end | msk) >= _val + Rng)
        {
            l++;
            msk >>= 1;
            end = (_val + msk) & ~msk;
        }
        while (l > 0)
        {
            CarryOut((int)(end >> RangeConstants.CodeShift));
            end = (end << RangeConstants.SymBits) & (RangeConstants.CodeTop - 1);
            l -= RangeConstants.SymBits;
        }
        if (_rem >= 0 || _ext > 0) CarryOut(0);
        var window = _endWindow;
        var used = _nendBits;
        while (used >= RangeConstants.SymBits)
        {
            WriteByteAtEnd(window & RangeConstants.SymMax);
            window >>= RangeConstants.SymBits;
            used -= RangeConstants.SymBits;
        }
        if (!Error)
        {
            Array.Clear(_buf, (int)_offs, (int)(_storage - _offs - _endOffs));
            if (used > 0)
            {
                if (_endOffs >= _storage) Error = true;
                else
                {
                    l = -l;
                    if (_offs + _endOffs >= _storage && l < used) { window &= (1u << l) - 1; Error = true; }
                    _buf[_storage - _endOffs - 1] |= (byte)window;
                }
            }
        }
    }

    internal void Shrink(uint size)
    {
        Array.Copy(_buf, (int)(_storage - _endOffs), _buf, (int)(size - _endOffs), (int)_endOffs);
        _storage = size;
    }

    /// <summary>Bytes of the buffer that carry data (front bytes plus the bytes written at the end).</summary>
    internal uint RangeBytes => _offs;
}
