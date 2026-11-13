// SILK decoder-side resampler from the internal 8/12/16 kHz rates to 48 kHz (silk_resampler with the up2_HQ and IIR/FIR stages),
// translated from the Opus reference implementation (Copyright (c) 2006-2011 Skype Limited, BSD 3-clause; see docs/webrtc-opus.md).
using static Folio.Scripting.Rtc.Opus.SilkMath;

namespace Folio.Scripting.Rtc.Opus;

internal sealed class SilkResampler
{
    private const int OrderFir12 = 8;
    private static readonly short[] Up2Hq0 = [1746, 14986, 39083 - 65536];
    private static readonly short[] Up2Hq1 = [6854, 25769, 55542 - 65536];

    private readonly int[] _sIir = new int[6];
    private readonly short[] _sFir = new short[OrderFir12];
    private readonly short[] _delayBuf = new short[96];
    private int _invRatioQ16, _batchSize, _inputDelay, _fsInKHz, _fsOutKHz;
    private short[] _buf = new short[2 * 160 + OrderFir12];

    internal void CopyFrom(SilkResampler o)
    {
        Array.Copy(o._sIir, _sIir, _sIir.Length);
        Array.Copy(o._sFir, _sFir, _sFir.Length);
        Array.Copy(o._delayBuf, _delayBuf, _delayBuf.Length);
        _invRatioQ16 = o._invRatioQ16; _batchSize = o._batchSize; _inputDelay = o._inputDelay; _fsInKHz = o._fsInKHz; _fsOutKHz = o._fsOutKHz;
    }

    internal void Init(int fsIn, int fsOut)
    {
        Array.Clear(_sIir); Array.Clear(_sFir); Array.Clear(_delayBuf);
        // decoder delay matrix for 48 kHz output: 8 kHz -> 0, 12 kHz -> 4, 16 kHz -> 7 samples
        _inputDelay = fsIn switch { 8000 => 0, 12000 => 4, _ => 7 };
        _fsInKHz = fsIn / 1000;
        _fsOutKHz = fsOut / 1000;
        _batchSize = _fsInKHz * 10;
        _invRatioQ16 = (int)((((long)fsIn << 15) / fsOut) << 2);
        // fsIn << 14 + up2x(1): the reference computes DIV32(LSHIFT32(Fs_in, 15), Fs_out) << 2
        while (Smulww(_invRatioQ16, fsOut) < fsIn << 1) _invRatioQ16++;
    }

    /// <summary>Resamples <paramref name="inLen"/> samples (at least one millisecond) into <paramref name="output"/>.</summary>
    internal void Process(Span<short> output, ReadOnlySpan<short> input, int inLen)
    {
        var nSamples = _fsInKHz - _inputDelay;
        input[..nSamples].CopyTo(_delayBuf.AsSpan(_inputDelay));
        IirFir(output, _delayBuf, _fsInKHz);
        IirFir(output[_fsOutKHz..], input[nSamples..], inLen - _fsInKHz);
        input.Slice(inLen - _inputDelay, _inputDelay).CopyTo(_delayBuf);
    }

    private static void Up2Hq(int[] s, Span<short> output, ReadOnlySpan<short> input, int len)
    {
        for (var k = 0; k < len; k++)
        {
            var in32 = input[k] << 10;
            var y = in32 - s[0];
            var x = Smulwb(y, Up2Hq0[0]);
            var out1 = s[0] + x;
            s[0] = in32 + x;
            y = out1 - s[1];
            x = Smulwb(y, Up2Hq0[1]);
            var out2 = s[1] + x;
            s[1] = out1 + x;
            y = out2 - s[2];
            x = Smlawb(y, y, Up2Hq0[2]);
            out1 = s[2] + x;
            s[2] = out2 + x;
            output[2 * k] = Sat16(RshiftRound(out1, 10));
            y = in32 - s[3];
            x = Smulwb(y, Up2Hq1[0]);
            out1 = s[3] + x;
            s[3] = in32 + x;
            y = out1 - s[4];
            x = Smulwb(y, Up2Hq1[1]);
            out2 = s[4] + x;
            s[4] = out1 + x;
            y = out2 - s[5];
            x = Smlawb(y, y, Up2Hq1[2]);
            out1 = s[5] + x;
            s[5] = out2 + x;
            output[2 * k + 1] = Sat16(RshiftRound(out1, 10));
        }
    }

    private int IirFir(Span<short> output, ReadOnlySpan<short> input, int inLen)
    {
        var buf = _buf;
        _sFir.CopyTo(buf, 0);
        var written = 0;
        while (true)
        {
            var nIn = Math.Min(inLen, _batchSize);
            Up2Hq(_sIir, buf.AsSpan(OrderFir12), input, nIn);
            var maxIndex = nIn << (16 + 1);
            for (var index = 0; index < maxIndex; index += _invRatioQ16)
            {
                var tableIndex = Smulwb(index & 0xFFFF, 12);
                var p = index >> 16;
                var f0 = tableIndex * 4;
                var f1 = (11 - tableIndex) * 4;
                var res = Smulbb(buf[p], SilkTables.ResamplerFracFIR12[f0]);
                res = Smlabb(res, buf[p + 1], SilkTables.ResamplerFracFIR12[f0 + 1]);
                res = Smlabb(res, buf[p + 2], SilkTables.ResamplerFracFIR12[f0 + 2]);
                res = Smlabb(res, buf[p + 3], SilkTables.ResamplerFracFIR12[f0 + 3]);
                res = Smlabb(res, buf[p + 4], SilkTables.ResamplerFracFIR12[f1 + 3]);
                res = Smlabb(res, buf[p + 5], SilkTables.ResamplerFracFIR12[f1 + 2]);
                res = Smlabb(res, buf[p + 6], SilkTables.ResamplerFracFIR12[f1 + 1]);
                res = Smlabb(res, buf[p + 7], SilkTables.ResamplerFracFIR12[f1]);
                output[written++] = Sat16(RshiftRound(res, 15));
            }
            input = input[nIn..];
            inLen -= nIn;
            if (inLen > 0) Array.Copy(buf, nIn << 1, buf, 0, OrderFir12);
            else { Array.Copy(buf, nIn << 1, _sFir, 0, OrderFir12); break; }
        }
        return written;
    }
}
