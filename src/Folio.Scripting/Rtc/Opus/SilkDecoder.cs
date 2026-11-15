// SILK decoder (RFC 6716 section 4.2), translated from the Opus reference implementation
// (Copyright (c) 2006-2011 Skype Limited, BSD 3-clause; see docs/webrtc-opus.md).
// Every table index is derived from entropy-decoded symbols with a bounded alphabet or is clamped, and every buffer is
// allocated once per decoder, so a hostile bitstream can neither read out of range nor make the decoder allocate.
using static Folio.Scripting.Rtc.Opus.SilkMath;

namespace Folio.Scripting.Rtc.Opus;

internal sealed class SilkNlsfCodebook
{
    public int NVectors, Order, QuantStepSizeQ16;
    public byte[] Cb1NlsfQ8 = [], Cb1Icdf = [], PredQ8 = [], EcSel = [], EcIcdf = [];
    public short[] Cb1WghtQ9 = [], DeltaMinQ15 = [];

    internal static readonly SilkNlsfCodebook NbMb = new()
    {
        NVectors = 32, Order = 10, QuantStepSizeQ16 = 11796,
        Cb1NlsfQ8 = SilkTables.NLSFCB1NBMBQ8, Cb1WghtQ9 = SilkTables.NLSFCB1WghtQ9, Cb1Icdf = SilkTables.NLSFCB1ICDFNBMB,
        PredQ8 = SilkTables.NLSFPREDNBMBQ8, EcSel = SilkTables.NLSFCB2SELECTNBMB, EcIcdf = SilkTables.NLSFCB2ICDFNBMB, DeltaMinQ15 = SilkTables.NLSFDELTAMINNBMBQ15,
    };

    internal static readonly SilkNlsfCodebook Wb = new()
    {
        NVectors = 32, Order = 16, QuantStepSizeQ16 = 9830,
        Cb1NlsfQ8 = SilkTables.NLSFCB1WBQ8, Cb1WghtQ9 = SilkTables.NLSFCB1WBWghtQ9, Cb1Icdf = SilkTables.NLSFCB1ICDFWB,
        PredQ8 = SilkTables.NLSFPREDWBQ8, EcSel = SilkTables.NLSFCB2SELECTWB, EcIcdf = SilkTables.NLSFCB2ICDFWB, DeltaMinQ15 = SilkTables.NLSFDELTAMINWBQ15,
    };
}

internal sealed class SilkControl
{
    public int NChannelsApi, NChannelsInternal, ApiSampleRate = 48000, InternalSampleRate, PayloadSizeMs, PrevPitchLag;
}

internal sealed class SilkDecoderControl
{
    public readonly int[] PitchL = new int[4];
    public readonly int[] GainsQ16 = new int[4];
    public readonly short[][] PredCoefQ12 = [new short[16], new short[16]];
    public readonly short[] LtpCoefQ14 = new short[20];
    public int LtpScaleQ14;
}

internal sealed class SilkPlcState
{
    public int PitchLQ8;
    public readonly short[] LtpCoefQ14 = new short[5];
    public readonly short[] PrevLpcQ12 = new short[16];
    public int LastFrameLost, RandSeed;
    public short RandScaleQ14;
    public int ConcEnergy, ConcEnergyShift;
    public short PrevLtpScaleQ14;
    public readonly int[] PrevGainQ16 = new int[2];
    public int FsKHz, NbSubfr, SubfrLength;
}

internal sealed class SilkCngState
{
    public readonly int[] ExcBufQ14 = new int[320];
    public readonly short[] SmthNlsfQ15 = new short[16];
    public readonly int[] SynthState = new int[16];
    public int SmthGainQ16, RandSeed, FsKHz;
}

internal sealed class SilkChannelState
{
    public int PrevGainQ16;
    public readonly int[] ExcQ14 = new int[320];
    public readonly int[] SlpcQ14Buf = new int[16];
    public readonly short[] OutBuf = new short[320 + 2 * 80];
    public int LagPrev;
    public sbyte LastGainIndex;
    public int FsKHz, FsApiHz, NbSubfr, FrameLength, SubfrLength, LtpMemLength, LpcOrder;
    public readonly short[] PrevNlsfQ15 = new short[16];
    public bool FirstFrameAfterReset;
    public byte[] PitchLagLowBitsIcdf = SilkTables.Uniform8ICDF;
    public byte[] PitchContourIcdf = SilkTables.PitchContourICDF;
    public int NFramesDecoded, NFramesPerPacket, EcPrevSignalType;
    public short EcPrevLagIndex;
    public readonly int[] VadFlags = new int[3];
    public int LbrrFlag;
    public readonly int[] LbrrFlags = new int[3];
    public SilkResampler Resampler = new();
    public SilkNlsfCodebook NlsfCb = SilkNlsfCodebook.NbMb;
    public readonly SilkCngState Cng = new();
    public int LossCnt, PrevSignalType;
    public readonly SilkPlcState Plc = new();

    // side information (SideInfoIndices)
    public readonly sbyte[] GainsIndices = new sbyte[4];
    public readonly sbyte[] LtpIndex = new sbyte[4];
    public readonly sbyte[] NlsfIndices = new sbyte[17];
    public short LagIndex;
    public sbyte ContourIndex, SignalType, QuantOffsetType, NlsfInterpCoefQ2, PerIndex, LtpScaleIndex, Seed;

    // scratch (never reallocated)
    internal readonly short[] Pulses = new short[336];
    internal readonly SilkDecoderControl Ctrl = new();
    internal readonly short[] SLtp = new short[320];
    internal readonly int[] SLtpQ15 = new int[640];
    internal readonly int[] ResQ14 = new int[80];
    internal readonly int[] SlpcQ14 = new int[96];

    internal SilkChannelState() { Reset(); }

    internal void Reset()
    {
        PrevGainQ16 = 65536;
        FsKHz = 0; FsApiHz = 0; NbSubfr = 0; FrameLength = 0; SubfrLength = 0; LtpMemLength = 0; LpcOrder = 0;
        Array.Clear(ExcQ14); Array.Clear(SlpcQ14Buf); Array.Clear(OutBuf);
        LagPrev = 0; LastGainIndex = 0;
        Array.Clear(PrevNlsfQ15);
        FirstFrameAfterReset = true;
        NFramesDecoded = 0; NFramesPerPacket = 0; EcPrevSignalType = 0; EcPrevLagIndex = 0;
        Array.Clear(VadFlags); LbrrFlag = 0; Array.Clear(LbrrFlags);
        LossCnt = 0; PrevSignalType = 0;
        SilkConceal.CngReset(this);
        SilkConceal.PlcReset(this);
        Cng.FsKHz = 0; Plc.FsKHz = 0;
        Plc.LastFrameLost = 0; Plc.RandSeed = 0; Plc.RandScaleQ14 = 0; Plc.ConcEnergy = 0; Plc.ConcEnergyShift = 0; Plc.PrevLtpScaleQ14 = 0;
        Array.Clear(Plc.LtpCoefQ14); Array.Clear(Plc.PrevLpcQ12);
        Array.Clear(Cng.ExcBufQ14); Array.Clear(Cng.SynthState);
    }
}

internal sealed class SilkDecoder
{
    internal const int MaxFrameLength = 320;
    internal const int TypeNoVoiceActivity = 0, TypeUnvoiced = 1, TypeVoiced = 2;
    internal const int CodeIndependently = 0, CodeIndependentlyNoLtpScaling = 1, CodeConditionally = 2;
    internal const int FlagDecodeNormal = 0, FlagPacketLost = 1, FlagDecodeLbrr = 2;

    internal readonly SilkChannelState[] Channel = [new(), new()];
    private readonly short[] _pred = new short[2]; // pred_prev_Q13
    private readonly short[] _sMid = new short[2];
    private readonly short[] _sSide = new short[2];
    private readonly int[] _msPred = new int[2];
    private readonly short[] _tmp0 = new short[MaxFrameLength + 2];
    private readonly short[] _tmp1 = new short[MaxFrameLength + 2];
    private readonly short[] _resampled = new short[960];
    private readonly short[] _lbrrPulses = new short[MaxFrameLength + 16];
    private int _nChannelsApi, _nChannelsInternal, _prevDecodeOnlyMiddle;

    internal void Reset()
    {
        foreach (var c in Channel) c.Reset();
        Array.Clear(_pred); Array.Clear(_sMid); Array.Clear(_sSide);
        _prevDecodeOnlyMiddle = 0;
    }

    private static void SetFs(SilkChannelState d, int fsKHz, int fsApiHz)
    {
        d.SubfrLength = 5 * fsKHz;
        var frameLength = d.NbSubfr * d.SubfrLength;
        if (d.FsKHz != fsKHz || d.FsApiHz != fsApiHz)
        {
            d.Resampler.Init(fsKHz * 1000, fsApiHz);
            d.FsApiHz = fsApiHz;
        }
        if (d.FsKHz != fsKHz || frameLength != d.FrameLength)
        {
            if (fsKHz == 8) d.PitchContourIcdf = d.NbSubfr == 4 ? SilkTables.PitchContourNBICDF : SilkTables.PitchContour10MsNBICDF;
            else d.PitchContourIcdf = d.NbSubfr == 4 ? SilkTables.PitchContourICDF : SilkTables.PitchContour10MsICDF;
            if (d.FsKHz != fsKHz)
            {
                d.LtpMemLength = 20 * fsKHz;
                if (fsKHz is 8 or 12) { d.LpcOrder = 10; d.NlsfCb = SilkNlsfCodebook.NbMb; }
                else { d.LpcOrder = 16; d.NlsfCb = SilkNlsfCodebook.Wb; }
                d.PitchLagLowBitsIcdf = fsKHz == 16 ? SilkTables.Uniform8ICDF : fsKHz == 12 ? SilkTables.Uniform6ICDF : SilkTables.Uniform4ICDF;
                d.FirstFrameAfterReset = true;
                d.LagPrev = 100;
                d.LastGainIndex = 10;
                d.PrevSignalType = TypeNoVoiceActivity;
                Array.Clear(d.OutBuf);
                Array.Clear(d.SlpcQ14Buf);
            }
            d.FsKHz = fsKHz;
            d.FrameLength = frameLength;
        }
    }

    /// <summary>
    /// silk_Decode: decodes one SILK frame of a packet (or conceals it). Output is interleaved 16-bit at 48 kHz.
    /// Returns false for an invalid configuration.
    /// </summary>
    internal bool Decode(SilkControl ctl, int lostFlag, bool newPacket, RangeDecoder rc, Span<short> samplesOut, out int nSamplesOut)
    {
        nSamplesOut = 0;
        if (ctl.NChannelsInternal is not (1 or 2) || ctl.NChannelsApi is not (1 or 2) || ctl.ApiSampleRate != 48000) return false;
        var decodeOnlyMiddle = 0;
        if (newPacket) for (var n = 0; n < ctl.NChannelsInternal; n++) Channel[n].NFramesDecoded = 0;
        if (Channel[0].NFramesDecoded >= 3) return false;
        if (ctl.NChannelsInternal > _nChannelsInternal) Channel[1].Reset();
        var stereoToMono = ctl.NChannelsInternal == 1 && _nChannelsInternal == 2 && ctl.InternalSampleRate == 1000 * Channel[0].FsKHz;
        if (Channel[0].NFramesDecoded == 0)
        {
            for (var n = 0; n < ctl.NChannelsInternal; n++)
            {
                var ch = Channel[n];
                switch (ctl.PayloadSizeMs)
                {
                    case 0 or 10: ch.NFramesPerPacket = 1; ch.NbSubfr = 2; break;
                    case 20: ch.NFramesPerPacket = 1; ch.NbSubfr = 4; break;
                    case 40: ch.NFramesPerPacket = 2; ch.NbSubfr = 4; break;
                    case 60: ch.NFramesPerPacket = 3; ch.NbSubfr = 4; break;
                    default: return false;
                }
                var fsKHz = (ctl.InternalSampleRate >> 10) + 1;
                if (fsKHz is not (8 or 12 or 16)) return false;
                SetFs(ch, fsKHz, ctl.ApiSampleRate);
            }
        }
        if (ctl.NChannelsApi == 2 && ctl.NChannelsInternal == 2 && (_nChannelsApi == 1 || _nChannelsInternal == 1))
        {
            Array.Clear(_pred); Array.Clear(_sSide);
            Channel[1].Resampler.CopyFrom(Channel[0].Resampler);
        }
        _nChannelsApi = ctl.NChannelsApi;
        _nChannelsInternal = ctl.NChannelsInternal;

        if (lostFlag != FlagPacketLost && Channel[0].NFramesDecoded == 0)
        {
            for (var n = 0; n < ctl.NChannelsInternal; n++)
            {
                var ch = Channel[n];
                for (var i = 0; i < ch.NFramesPerPacket; i++) ch.VadFlags[i] = rc.BitLogp(1);
                ch.LbrrFlag = rc.BitLogp(1);
            }
            for (var n = 0; n < ctl.NChannelsInternal; n++)
            {
                var ch = Channel[n];
                Array.Clear(ch.LbrrFlags);
                if (ch.LbrrFlag != 0)
                {
                    if (ch.NFramesPerPacket == 1) ch.LbrrFlags[0] = 1;
                    else
                    {
                        var symbol = rc.Icdf(ch.NFramesPerPacket == 2 ? SilkTables.LBRRFlags2ICDF : SilkTables.LBRRFlags3ICDF, 8) + 1;
                        for (var i = 0; i < ch.NFramesPerPacket; i++) ch.LbrrFlags[i] = (symbol >> i) & 1;
                    }
                }
            }
            if (lostFlag == FlagDecodeNormal)
            {
                // LBRR frames of a normally decoded packet are parsed and discarded.
                for (var i = 0; i < Channel[0].NFramesPerPacket; i++)
                    for (var n = 0; n < ctl.NChannelsInternal; n++)
                    {
                        var ch = Channel[n];
                        if (ch.LbrrFlags[i] == 0) continue;
                        if (ctl.NChannelsInternal == 2 && n == 0)
                        {
                            DecodeStereoPred(rc, _msPred);
                            if (Channel[1].LbrrFlags[i] == 0) decodeOnlyMiddle = rc.Icdf(SilkTables.StereoOnlyCodeMidICDF, 8);
                        }
                        var cond = i > 0 && ch.LbrrFlags[i - 1] != 0 ? CodeConditionally : CodeIndependently;
                        DecodeIndices(ch, rc, i, true, cond);
                        DecodePulses(rc, _lbrrPulses, ch.SignalType, ch.QuantOffsetType, ch.FrameLength);
                    }
            }
        }

        if (ctl.NChannelsInternal == 2)
        {
            if (lostFlag == FlagDecodeNormal || (lostFlag == FlagDecodeLbrr && Channel[0].LbrrFlags[Channel[0].NFramesDecoded] == 1))
            {
                DecodeStereoPred(rc, _msPred);
                if ((lostFlag == FlagDecodeNormal && Channel[1].VadFlags[Channel[0].NFramesDecoded] == 0)
                    || (lostFlag == FlagDecodeLbrr && Channel[1].LbrrFlags[Channel[0].NFramesDecoded] == 0))
                    decodeOnlyMiddle = rc.Icdf(SilkTables.StereoOnlyCodeMidICDF, 8);
                else decodeOnlyMiddle = 0;
            }
            else for (var n = 0; n < 2; n++) _msPred[n] = _pred[n];
        }
        if (ctl.NChannelsInternal == 2 && decodeOnlyMiddle == 0 && _prevDecodeOnlyMiddle == 1)
        {
            var c1 = Channel[1];
            Array.Clear(c1.OutBuf); Array.Clear(c1.SlpcQ14Buf);
            c1.LagPrev = 100; c1.LastGainIndex = 10; c1.PrevSignalType = TypeNoVoiceActivity; c1.FirstFrameAfterReset = true;
        }

        var frameLength = Channel[0].FrameLength;
        var tmp = new[] { _tmp0, _tmp1 };
        bool hasSide;
        if (lostFlag == FlagDecodeNormal) hasSide = decodeOnlyMiddle == 0;
        else hasSide = _prevDecodeOnlyMiddle == 0
            || (ctl.NChannelsInternal == 2 && lostFlag == FlagDecodeLbrr && Channel[1].LbrrFlags[Channel[1].NFramesDecoded] == 1);
        var nSamplesOutDec = 0;
        for (var n = 0; n < ctl.NChannelsInternal; n++)
        {
            if (n == 0 || hasSide)
            {
                var frameIndex = Channel[0].NFramesDecoded - n;
                int cond;
                if (frameIndex <= 0) cond = CodeIndependently;
                else if (lostFlag == FlagDecodeLbrr) cond = Channel[n].LbrrFlags[frameIndex - 1] != 0 ? CodeConditionally : CodeIndependently;
                else if (n > 0 && _prevDecodeOnlyMiddle == 1) cond = CodeIndependentlyNoLtpScaling;
                else cond = CodeConditionally;
                nSamplesOutDec = DecodeFrame(Channel[n], rc, tmp[n].AsSpan(2), lostFlag, cond);
            }
            else tmp[n].AsSpan(2, nSamplesOutDec).Clear();
            Channel[n].NFramesDecoded++;
        }
        if (nSamplesOutDec <= 0) nSamplesOutDec = frameLength;

        if (ctl.NChannelsApi == 2 && ctl.NChannelsInternal == 2)
            StereoMsToLr(_tmp0, _tmp1, _msPred, Channel[0].FsKHz, nSamplesOutDec);
        else
        {
            _sMid.CopyTo(_tmp0, 0);
            _sMid[0] = _tmp0[nSamplesOutDec]; _sMid[1] = _tmp0[nSamplesOutDec + 1];
        }
        nSamplesOut = nSamplesOutDec * ctl.ApiSampleRate / (Channel[0].FsKHz * 1000);
        if (nSamplesOut > _resampled.Length) return false;
        var nResample = Math.Min(ctl.NChannelsApi, ctl.NChannelsInternal);
        for (var n = 0; n < nResample; n++)
        {
            Channel[n].Resampler.Process(_resampled, tmp[n].AsSpan(1), nSamplesOutDec);
            if (ctl.NChannelsApi == 2) for (var i = 0; i < nSamplesOut; i++) samplesOut[n + 2 * i] = _resampled[i];
            else _resampled.AsSpan(0, nSamplesOut).CopyTo(samplesOut);
        }
        if (ctl.NChannelsApi == 2 && ctl.NChannelsInternal == 1)
        {
            if (stereoToMono)
            {
                Channel[1].Resampler.Process(_resampled, _tmp0.AsSpan(1), nSamplesOutDec);
                for (var i = 0; i < nSamplesOut; i++) samplesOut[1 + 2 * i] = _resampled[i];
            }
            else for (var i = 0; i < nSamplesOut; i++) samplesOut[1 + 2 * i] = samplesOut[2 * i];
        }
        ReadOnlySpan<int> multTab = [6, 4, 3];
        ctl.PrevPitchLag = Channel[0].PrevSignalType == TypeVoiced ? Channel[0].LagPrev * multTab[(Channel[0].FsKHz - 8) >> 2] : 0;
        if (lostFlag == FlagPacketLost) for (var i = 0; i < _nChannelsInternal; i++) Channel[i].LastGainIndex = 10;
        else _prevDecodeOnlyMiddle = decodeOnlyMiddle;
        return true;
    }

    private void StereoMsToLr(short[] x1, short[] x2, int[] predQ13, int fsKHz, int frameLength)
    {
        x1[0] = _sMid[0]; x1[1] = _sMid[1];
        x2[0] = _sSide[0]; x2[1] = _sSide[1];
        _sMid[0] = x1[frameLength]; _sMid[1] = x1[frameLength + 1];
        _sSide[0] = x2[frameLength]; _sSide[1] = x2[frameLength + 1];
        int pred0 = _pred[0], pred1 = _pred[1];
        var denom = 65536 / (8 * fsKHz);
        var delta0 = RshiftRound(Smulbb(predQ13[0] - _pred[0], denom), 16);
        var delta1 = RshiftRound(Smulbb(predQ13[1] - _pred[1], denom), 16);
        int n;
        for (n = 0; n < 8 * fsKHz; n++)
        {
            pred0 += delta0; pred1 += delta1;
            var sum = (x1[n] + x1[n + 2] + (x1[n + 1] << 1)) << 9;
            sum = Smlawb(x2[n + 1] << 8, sum, pred0);
            sum = Smlawb(sum, x1[n + 1] << 11, pred1);
            x2[n + 1] = Sat16(RshiftRound(sum, 8));
        }
        pred0 = predQ13[0]; pred1 = predQ13[1];
        for (; n < frameLength; n++)
        {
            var sum = (x1[n] + x1[n + 2] + (x1[n + 1] << 1)) << 9;
            sum = Smlawb(x2[n + 1] << 8, sum, pred0);
            sum = Smlawb(sum, x1[n + 1] << 11, pred1);
            x2[n + 1] = Sat16(RshiftRound(sum, 8));
        }
        _pred[0] = (short)predQ13[0]; _pred[1] = (short)predQ13[1];
        for (n = 0; n < frameLength; n++)
        {
            var sum = x1[n + 1] + x2[n + 1];
            var diff = x1[n + 1] - x2[n + 1];
            x1[n + 1] = Sat16(sum);
            x2[n + 1] = Sat16(diff);
        }
    }

    private static void DecodeStereoPred(RangeDecoder rc, int[] predQ13)
    {
        Span<int> ix = stackalloc int[6]; // [n][0..2]
        var n = rc.Icdf(SilkTables.StereoPredJointICDF, 8);
        ix[2] = n / 5;
        ix[5] = n - 5 * ix[2];
        for (n = 0; n < 2; n++)
        {
            ix[n * 3] = rc.Icdf(SilkTables.Uniform3ICDF, 8);
            ix[n * 3 + 1] = rc.Icdf(SilkTables.Uniform5ICDF, 8);
        }
        for (n = 0; n < 2; n++)
        {
            ix[n * 3] += 3 * ix[n * 3 + 2];
            int low = SilkTables.StereoPredQuantQ13[ix[n * 3]];
            var step = Smulwb(SilkTables.StereoPredQuantQ13[ix[n * 3] + 1] - low, 6554); // 0.5/5 in Q16
            predQ13[n] = Smlabb(low, step, 2 * ix[n * 3 + 1] + 1);
        }
        predQ13[0] -= predQ13[1];
    }

    private int DecodeFrame(SilkChannelState d, RangeDecoder rc, Span<short> pOut, int lostFlag, int condCoding)
    {
        var ctrl = d.Ctrl;
        var l = d.FrameLength;
        ctrl.LtpScaleQ14 = 0;
        if (lostFlag == FlagDecodeNormal || (lostFlag == FlagDecodeLbrr && d.LbrrFlags[d.NFramesDecoded] == 1))
        {
            DecodeIndices(d, rc, d.NFramesDecoded, lostFlag != 0, condCoding);
            DecodePulses(rc, d.Pulses, d.SignalType, d.QuantOffsetType, d.FrameLength);
            DecodeParameters(d, ctrl, condCoding);
            DecodeCore(d, ctrl, pOut, d.Pulses);
            var mvLen = d.LtpMemLength - d.FrameLength;
            Array.Copy(d.OutBuf, d.FrameLength, d.OutBuf, 0, mvLen);
            pOut[..d.FrameLength].CopyTo(d.OutBuf.AsSpan(mvLen));
            SilkConceal.Plc(d, ctrl, pOut, false);
            d.LossCnt = 0;
            d.PrevSignalType = d.SignalType;
            d.FirstFrameAfterReset = false;
        }
        else
        {
            SilkConceal.Plc(d, ctrl, pOut, true);
            var mvLen = d.LtpMemLength - d.FrameLength;
            Array.Copy(d.OutBuf, d.FrameLength, d.OutBuf, 0, mvLen);
            pOut[..d.FrameLength].CopyTo(d.OutBuf.AsSpan(mvLen));
        }
        SilkConceal.Cng(d, ctrl, pOut, l);
        SilkConceal.PlcGlueFrames(d, pOut, l);
        d.LagPrev = ctrl.PitchL[d.NbSubfr - 1];
        return l;
    }

    // ---- side information ----

    private static void DecodeIndices(SilkChannelState d, RangeDecoder rc, int frameIndex, bool decodeLbrr, int condCoding)
    {
        int ix;
        if (decodeLbrr || d.VadFlags[frameIndex] != 0) ix = rc.Icdf(SilkTables.TypeOffsetVADICDF, 8) + 2;
        else ix = rc.Icdf(SilkTables.TypeOffsetNoVADICDF, 8);
        d.SignalType = (sbyte)(ix >> 1);
        d.QuantOffsetType = (sbyte)(ix & 1);
        if (condCoding == CodeConditionally) d.GainsIndices[0] = (sbyte)rc.Icdf(SilkTables.DeltaGainICDF, 8);
        else
        {
            d.GainsIndices[0] = (sbyte)(rc.Icdf(SilkTables.GainICDF.AsSpan(d.SignalType * 8, 8), 8) << 3);
            d.GainsIndices[0] += (sbyte)rc.Icdf(SilkTables.Uniform8ICDF, 8);
        }
        for (var i = 1; i < d.NbSubfr; i++) d.GainsIndices[i] = (sbyte)rc.Icdf(SilkTables.DeltaGainICDF, 8);

        var cb = d.NlsfCb;
        d.NlsfIndices[0] = (sbyte)rc.Icdf(cb.Cb1Icdf.AsSpan((d.SignalType >> 1) * cb.NVectors, cb.NVectors), 8);
        Span<short> ecIx = stackalloc short[16];
        Span<byte> predQ8 = stackalloc byte[16];
        NlsfUnpack(ecIx, predQ8, cb, d.NlsfIndices[0]);
        for (var i = 0; i < cb.Order; i++)
        {
            ix = rc.Icdf(cb.EcIcdf.AsSpan(ecIx[i], 9), 8);
            if (ix == 0) ix -= rc.Icdf(SilkTables.NLSFEXTICDF, 8);
            else if (ix == 8) ix += rc.Icdf(SilkTables.NLSFEXTICDF, 8);
            d.NlsfIndices[i + 1] = (sbyte)(ix - 4);
        }
        d.NlsfInterpCoefQ2 = d.NbSubfr == 4 ? (sbyte)rc.Icdf(SilkTables.NLSFInterpolationFactorICDF, 8) : (sbyte)4;
        if (d.SignalType == TypeVoiced)
        {
            var decodeAbsolute = true;
            if (condCoding == CodeConditionally && d.EcPrevSignalType == TypeVoiced)
            {
                var delta = (short)rc.Icdf(SilkTables.PitchDeltaICDF, 8);
                if (delta > 0)
                {
                    delta -= 9;
                    d.LagIndex = (short)(d.EcPrevLagIndex + delta);
                    decodeAbsolute = false;
                }
            }
            if (decodeAbsolute)
            {
                d.LagIndex = (short)(rc.Icdf(SilkTables.PitchLagICDF, 8) * (d.FsKHz >> 1));
                d.LagIndex += (short)rc.Icdf(d.PitchLagLowBitsIcdf, 8);
            }
            d.EcPrevLagIndex = d.LagIndex;
            d.ContourIndex = (sbyte)rc.Icdf(d.PitchContourIcdf, 8);
            d.PerIndex = (sbyte)rc.Icdf(SilkTables.LTPPerIndexICDF, 8);
            for (var k = 0; k < d.NbSubfr; k++)
                d.LtpIndex[k] = (sbyte)rc.Icdf(d.PerIndex switch { 0 => SilkTables.LTPGainICDF0, 1 => SilkTables.LTPGainICDF1, _ => SilkTables.LTPGainICDF2 }, 8);
            d.LtpScaleIndex = condCoding == CodeIndependently ? (sbyte)rc.Icdf(SilkTables.LTPscaleICDF, 8) : (sbyte)0;
        }
        d.EcPrevSignalType = d.SignalType;
        d.Seed = (sbyte)rc.Icdf(SilkTables.Uniform4ICDF, 8);
    }

    private static void NlsfUnpack(Span<short> ecIx, Span<byte> predQ8, SilkNlsfCodebook cb, int cb1Index)
    {
        var sel = cb1Index * cb.Order / 2;
        for (var i = 0; i < cb.Order; i += 2)
        {
            int entry = cb.EcSel[sel++];
            ecIx[i] = (short)(((entry >> 1) & 7) * 9);
            predQ8[i] = cb.PredQ8[i + (entry & 1) * (cb.Order - 1)];
            ecIx[i + 1] = (short)(((entry >> 5) & 7) * 9);
            predQ8[i + 1] = cb.PredQ8[i + ((entry >> 4) & 1) * (cb.Order - 1) + 1];
        }
    }

    private static void DecodePulses(RangeDecoder rc, short[] pulses, int signalType, int quantOffsetType, int frameLength)
    {
        Span<int> sumPulses = stackalloc int[20];
        Span<int> nLshifts = stackalloc int[20];
        var rateLevel = rc.Icdf(SilkTables.RateLevelsICDF.AsSpan((signalType >> 1) * 9, 9), 8);
        var iter = frameLength >> 4;
        if (iter * 16 < frameLength) iter++;
        var cdf = SilkTables.PulsesPerBlockICDF.AsSpan(rateLevel * 18, 18);
        for (var i = 0; i < iter; i++)
        {
            nLshifts[i] = 0;
            sumPulses[i] = rc.Icdf(cdf, 8);
            while (sumPulses[i] == 17)
            {
                nLshifts[i]++;
                sumPulses[i] = rc.Icdf(SilkTables.PulsesPerBlockICDF.AsSpan(9 * 18 + (nLshifts[i] == 10 ? 1 : 0), nLshifts[i] == 10 ? 17 : 18), 8);
            }
        }
        for (var i = 0; i < iter; i++)
        {
            var block = pulses.AsSpan(i * 16, 16);
            if (sumPulses[i] > 0) ShellDecode(block, rc, sumPulses[i]);
            else block.Clear();
        }
        for (var i = 0; i < iter; i++)
        {
            if (nLshifts[i] <= 0) continue;
            var nls = nLshifts[i];
            for (var k = 0; k < 16; k++)
            {
                int absq = pulses[i * 16 + k];
                for (var j = 0; j < nls; j++) { absq <<= 1; absq += rc.Icdf(SilkTables.LsbICDF, 8); }
                pulses[i * 16 + k] = (short)absq;
            }
            sumPulses[i] |= nls << 5;
        }
        // signs
        Span<byte> icdf = stackalloc byte[2];
        icdf[1] = 0;
        var iPtr = 7 * (quantOffsetType + (signalType << 1));
        var blocks = (frameLength + 8) >> 4;
        for (var i = 0; i < blocks; i++)
        {
            var p = sumPulses[i];
            if (p > 0)
            {
                icdf[0] = SilkTables.SignICDF[iPtr + Math.Min(p & 0x1F, 6)];
                for (var j = 0; j < 16; j++)
                    if (pulses[i * 16 + j] > 0) pulses[i * 16 + j] *= (short)((rc.Icdf(icdf, 8) << 1) - 1);
            }
        }
    }

    private static void ShellDecode(Span<short> p0, RangeDecoder rc, int pulses4)
    {
        Span<short> p3 = stackalloc short[2], p2 = stackalloc short[4], p1 = stackalloc short[8];
        Split(rc, pulses4, SilkTables.ShellCodeTable3, out p3[0], out p3[1]);
        Split(rc, p3[0], SilkTables.ShellCodeTable2, out p2[0], out p2[1]);
        Split(rc, p2[0], SilkTables.ShellCodeTable1, out p1[0], out p1[1]);
        Split(rc, p1[0], SilkTables.ShellCodeTable0, out p0[0], out p0[1]);
        Split(rc, p1[1], SilkTables.ShellCodeTable0, out p0[2], out p0[3]);
        Split(rc, p2[1], SilkTables.ShellCodeTable1, out p1[2], out p1[3]);
        Split(rc, p1[2], SilkTables.ShellCodeTable0, out p0[4], out p0[5]);
        Split(rc, p1[3], SilkTables.ShellCodeTable0, out p0[6], out p0[7]);
        Split(rc, p3[1], SilkTables.ShellCodeTable2, out p2[2], out p2[3]);
        Split(rc, p2[2], SilkTables.ShellCodeTable1, out p1[4], out p1[5]);
        Split(rc, p1[4], SilkTables.ShellCodeTable0, out p0[8], out p0[9]);
        Split(rc, p1[5], SilkTables.ShellCodeTable0, out p0[10], out p0[11]);
        Split(rc, p2[3], SilkTables.ShellCodeTable1, out p1[6], out p1[7]);
        Split(rc, p1[6], SilkTables.ShellCodeTable0, out p0[12], out p0[13]);
        Split(rc, p1[7], SilkTables.ShellCodeTable0, out p0[14], out p0[15]);
    }

    private static void Split(RangeDecoder rc, int p, byte[] table, out short child1, out short child2)
    {
        if (p > 0)
        {
            child1 = (short)rc.Icdf(table.AsSpan(SilkTables.ShellCodeTableOffsets[p]), 8);
            child2 = (short)(p - child1);
        }
        else { child1 = 0; child2 = 0; }
    }

    private static void DecodeParameters(SilkChannelState d, SilkDecoderControl ctrl, int condCoding)
    {
        GainsDequant(ctrl.GainsQ16, d.GainsIndices, ref d.LastGainIndex, condCoding == CodeConditionally, d.NbSubfr);
        Span<short> nlsf = stackalloc short[16], nlsf0 = stackalloc short[16];
        NlsfDecode(nlsf, d.NlsfIndices, d.NlsfCb);
        Nlsf2A(ctrl.PredCoefQ12[1], nlsf, d.LpcOrder);
        if (d.FirstFrameAfterReset) d.NlsfInterpCoefQ2 = 4;
        if (d.NlsfInterpCoefQ2 < 4)
        {
            for (var i = 0; i < d.LpcOrder; i++)
                nlsf0[i] = (short)(d.PrevNlsfQ15[i] + ((d.NlsfInterpCoefQ2 * (nlsf[i] - d.PrevNlsfQ15[i])) >> 2));
            Nlsf2A(ctrl.PredCoefQ12[0], nlsf0, d.LpcOrder);
        }
        else Array.Copy(ctrl.PredCoefQ12[1], ctrl.PredCoefQ12[0], d.LpcOrder);
        nlsf[..d.LpcOrder].CopyTo(d.PrevNlsfQ15);
        if (d.LossCnt != 0)
        {
            Bwexpander(ctrl.PredCoefQ12[0], d.LpcOrder, 63570);
            Bwexpander(ctrl.PredCoefQ12[1], d.LpcOrder, 63570);
        }
        if (d.SignalType == TypeVoiced)
        {
            DecodePitch(d.LagIndex, d.ContourIndex, ctrl.PitchL, d.FsKHz, d.NbSubfr);
            var cbk = d.PerIndex switch { 0 => SilkTables.LTPGainVq0, 1 => SilkTables.LTPGainVq1, _ => SilkTables.LTPGainVq2 };
            for (var k = 0; k < d.NbSubfr; k++)
            {
                int ix = d.LtpIndex[k];
                for (var i = 0; i < 5; i++) ctrl.LtpCoefQ14[k * 5 + i] = (short)(cbk[ix * 5 + i] << 7);
            }
            ctrl.LtpScaleQ14 = SilkTables.LTPScalesTableQ14[d.LtpScaleIndex];
        }
        else
        {
            Array.Clear(ctrl.PitchL, 0, d.NbSubfr);
            Array.Clear(ctrl.LtpCoefQ14, 0, 5 * d.NbSubfr);
            d.PerIndex = 0;
            ctrl.LtpScaleQ14 = 0;
        }
    }

    private static void GainsDequant(int[] gainQ16, sbyte[] ind, ref sbyte prevInd, bool conditional, int nbSubfr)
    {
        const int offset = (2 * 128) / 6 + 16 * 128, invScaleQ16 = (65536 * (((88 - 2) * 128) / 6)) / 63;
        int prev = prevInd;
        for (var k = 0; k < nbSubfr; k++)
        {
            if (k == 0 && !conditional) prev = Math.Max(ind[k], prev - 16);
            else
            {
                var indTmp = ind[k] - 4;
                var threshold = 2 * 36 - 64 + prev;
                prev += indTmp > threshold ? (indTmp << 1) - threshold : indTmp;
            }
            prev = Limit(prev, 0, 63);
            gainQ16[k] = Log2Lin(Math.Min(Smulwb(invScaleQ16, prev) + offset, 3967));
        }
        prevInd = (sbyte)prev;
    }

    private static void NlsfDecode(Span<short> nlsfQ15, sbyte[] indices, SilkNlsfCodebook cb)
    {
        Span<byte> predQ8 = stackalloc byte[16];
        Span<short> ecIx = stackalloc short[16], resQ10 = stackalloc short[16];
        NlsfUnpack(ecIx, predQ8, cb, indices[0]);
        var outQ10 = 0;
        for (var i = cb.Order - 1; i >= 0; i--)
        {
            var predQ10 = Smulbb(outQ10, (short)predQ8[i]) >> 8;
            outQ10 = indices[i + 1] << 10;
            if (outQ10 > 0) outQ10 = (short)(outQ10 - 102); // 0.1 in Q10
            else if (outQ10 < 0) outQ10 = (short)(outQ10 + 102);
            outQ10 = Smlawb(predQ10, outQ10, cb.QuantStepSizeQ16);
            resQ10[i] = (short)outQ10;
        }
        var cbOff = indices[0] * cb.Order;
        for (var i = 0; i < cb.Order; i++)
        {
            var tmp = (((int)resQ10[i] << 14) / cb.Cb1WghtQ9[cbOff + i]) + (cb.Cb1NlsfQ8[cbOff + i] << 7);
            nlsfQ15[i] = (short)Limit(tmp, 0, 32767);
        }
        NlsfStabilize(nlsfQ15, cb.DeltaMinQ15, cb.Order);
    }

    private static void NlsfStabilize(Span<short> nlsf, short[] deltaMin, int l)
    {
        int loops;
        for (loops = 0; loops < 20; loops++)
        {
            var minDiff = nlsf[0] - deltaMin[0];
            var idx = 0;
            for (var i = 1; i <= l - 1; i++)
            {
                var diff = nlsf[i] - (nlsf[i - 1] + deltaMin[i]);
                if (diff < minDiff) { minDiff = diff; idx = i; }
            }
            var last = (1 << 15) - (nlsf[l - 1] + deltaMin[l]);
            if (last < minDiff) { minDiff = last; idx = l; }
            if (minDiff >= 0) return;
            if (idx == 0) nlsf[0] = deltaMin[0];
            else if (idx == l) nlsf[l - 1] = (short)((1 << 15) - deltaMin[l]);
            else
            {
                var minCenter = 0;
                for (var k = 0; k < idx; k++) minCenter += deltaMin[k];
                minCenter += deltaMin[idx] >> 1;
                var maxCenter = 1 << 15;
                for (var k = l; k > idx; k--) maxCenter -= deltaMin[k];
                maxCenter -= deltaMin[idx] >> 1;
                var center = (short)Limit(RshiftRound(nlsf[idx - 1] + nlsf[idx], 1), minCenter, maxCenter);
                nlsf[idx - 1] = (short)(center - (deltaMin[idx] >> 1));
                nlsf[idx] = (short)(nlsf[idx - 1] + deltaMin[idx]);
            }
        }
        // did not converge: sort, then force the minimum spacing
        for (var i = 1; i < l; i++)
        {
            int value = nlsf[i]; var j = i - 1;
            for (; j >= 0 && value < nlsf[j]; j--) nlsf[j + 1] = nlsf[j];
            nlsf[j + 1] = (short)value;
        }
        nlsf[0] = (short)Math.Max(nlsf[0], deltaMin[0]);
        for (var i = 1; i < l; i++) nlsf[i] = (short)Math.Max(nlsf[i], AddSat16(nlsf[i - 1], deltaMin[i]));
        nlsf[l - 1] = (short)Math.Min(nlsf[l - 1], (1 << 15) - deltaMin[l]);
        for (var i = l - 2; i >= 0; i--) nlsf[i] = (short)Math.Min(nlsf[i], nlsf[i + 1] - deltaMin[i + 1]);
    }

    internal static void Nlsf2A(Span<short> aQ12, ReadOnlySpan<short> nlsf, int d)
    {
        ReadOnlySpan<byte> ordering16 = [0, 15, 8, 7, 4, 11, 12, 3, 2, 13, 10, 5, 6, 9, 14, 1];
        ReadOnlySpan<byte> ordering10 = [0, 9, 6, 3, 4, 5, 8, 1, 2, 7];
        var ordering = d == 16 ? ordering16 : ordering10;
        Span<int> cosLsf = stackalloc int[16], cosOdd = stackalloc int[8], cosEven = stackalloc int[8];
        Span<int> p = stackalloc int[9], q = stackalloc int[9], a32 = stackalloc int[16];
        for (var k = 0; k < d; k++)
        {
            var fInt = nlsf[k] >> (15 - 7);
            var fFrac = nlsf[k] - (fInt << (15 - 7));
            int cosVal = SilkTables.LSFCosTabFIXQ12[fInt];
            var delta = SilkTables.LSFCosTabFIXQ12[fInt + 1] - cosVal;
            cosLsf[ordering[k]] = RshiftRound((cosVal << 8) + delta * fFrac, 20 - 16);
        }
        var dd = d >> 1;
        for (var k = 0; k < dd; k++) { cosEven[k] = cosLsf[2 * k]; cosOdd[k] = cosLsf[2 * k + 1]; }
        FindPoly(p, cosEven, dd);
        FindPoly(q, cosOdd, dd);
        for (var k = 0; k < dd; k++)
        {
            var ptmp = p[k + 1] + p[k];
            var qtmp = q[k + 1] - q[k];
            a32[k] = -qtmp - ptmp;
            a32[d - k - 1] = qtmp - ptmp;
        }
        LpcFit(aQ12, a32, 12, 16 + 1, d);
        for (var i = 0; LpcInversePredGain(aQ12, d) == 0 && i < 16; i++)
        {
            Bwexpander32(a32, d, 65536 - (2 << i));
            for (var k = 0; k < d; k++) aQ12[k] = (short)RshiftRound(a32[k], 16 + 1 - 12);
        }
    }

    private static void FindPoly(Span<int> output, ReadOnlySpan<int> cLsf, int dd)
    {
        output[0] = 1 << 16;
        output[1] = -cLsf[0];
        for (var k = 1; k < dd; k++)
        {
            var ftmp = cLsf[k];
            output[k + 1] = (output[k - 1] << 1) - (int)RshiftRound64(Smull(ftmp, output[k]), 16);
            for (var n = k; n > 1; n--) output[n] += output[n - 2] - (int)RshiftRound64(Smull(ftmp, output[n - 1]), 16);
            output[1] -= ftmp;
        }
    }

    private static void DecodePitch(short lagIndex, sbyte contourIndex, int[] pitchLags, int fsKHz, int nbSubfr)
    {
        sbyte[] cb;
        int cbkSize;
        if (fsKHz == 8)
        {
            if (nbSubfr == 4) { cb = SilkTables.CBLagsStage2; cbkSize = 11; }
            else { cb = SilkTables.CBLagsStage210Ms; cbkSize = 3; }
        }
        else if (nbSubfr == 4) { cb = SilkTables.CBLagsStage3; cbkSize = 34; }
        else { cb = SilkTables.CBLagsStage310Ms; cbkSize = 12; }
        var minLag = 2 * fsKHz;
        var maxLag = 18 * fsKHz;
        var lag = minLag + lagIndex;
        for (var k = 0; k < nbSubfr; k++)
        {
            var c = contourIndex < cbkSize ? contourIndex : cbkSize - 1; // the alphabets bound it; this is belt and braces
            pitchLags[k] = Limit(lag + cb[k * cbkSize + c], minLag, maxLag);
        }
    }

    // ---- synthesis ----

    private static void DecodeCore(SilkChannelState d, SilkDecoderControl ctrl, Span<short> xq, short[] pulses)
    {
        var sLtp = d.SLtp;
        var sLtpQ15 = d.SLtpQ15;
        var resQ14 = d.ResQ14;
        var sLpcQ14 = d.SlpcQ14;
        var offsetQ10 = SilkTables.QuantizationOffsetsQ10[(d.SignalType >> 1) * 2 + d.QuantOffsetType];
        var interpFlag = d.NlsfInterpCoefQ2 < 4;
        var randSeed = (int)d.Seed;
        for (var i = 0; i < d.FrameLength; i++)
        {
            randSeed = Rand(randSeed);
            var e = pulses[i] << 14;
            if (e > 0) e -= 80 << 4; else if (e < 0) e += 80 << 4;
            e += offsetQ10 << 4;
            if (randSeed < 0) e = -e;
            d.ExcQ14[i] = e;
            randSeed = unchecked(randSeed + pulses[i]);
        }
        Array.Copy(d.SlpcQ14Buf, sLpcQ14, 16);
        var pexc = 0;
        var pxq = 0;
        var sLtpBufIdx = d.LtpMemLength;
        Span<short> a = stackalloc short[16];
        var lag = 0;
        for (var k = 0; k < d.NbSubfr; k++)
        {
            var aQ12 = ctrl.PredCoefQ12[k >> 1];
            aQ12.AsSpan(0, d.LpcOrder).CopyTo(a);
            var bOff = k * 5;
            var signalType = (int)d.SignalType;
            var gainQ10 = ctrl.GainsQ16[k] >> 6;
            var invGainQ31 = Inverse32Varq(ctrl.GainsQ16[k], 47);
            int gainAdjQ16;
            if (ctrl.GainsQ16[k] != d.PrevGainQ16)
            {
                gainAdjQ16 = Div32Varq(d.PrevGainQ16, ctrl.GainsQ16[k], 16);
                for (var i = 0; i < 16; i++) sLpcQ14[i] = Smulww(gainAdjQ16, sLpcQ14[i]);
            }
            else gainAdjQ16 = 1 << 16;
            d.PrevGainQ16 = ctrl.GainsQ16[k];
            if (d.LossCnt != 0 && d.PrevSignalType == TypeVoiced && d.SignalType != TypeVoiced && k < 2)
            {
                for (var i = 0; i < 5; i++) ctrl.LtpCoefQ14[bOff + i] = 0;
                ctrl.LtpCoefQ14[bOff + 2] = 4096; // 0.25 in Q14
                signalType = TypeVoiced;
                ctrl.PitchL[k] = d.LagPrev;
            }
            int resBase;
            Span<int> res;
            if (signalType == TypeVoiced)
            {
                lag = ctrl.PitchL[k];
                if (k == 0 || (k == 2 && interpFlag))
                {
                    var startIdx = d.LtpMemLength - lag - d.LpcOrder - 2;
                    if (startIdx <= 0) startIdx = 1; // unreachable for valid lags; keeps the buffer index in range
                    if (k == 2) xq[..(2 * d.SubfrLength)].CopyTo(d.OutBuf.AsSpan(d.LtpMemLength));
                    LpcAnalysisFilter(sLtp.AsSpan(startIdx), d.OutBuf.AsSpan(startIdx + k * d.SubfrLength), aQ12, d.LtpMemLength - startIdx, d.LpcOrder);
                    if (k == 0) invGainQ31 = Smulwb(invGainQ31, ctrl.LtpScaleQ14) << 2;
                    for (var i = 0; i < lag + 2; i++) sLtpQ15[sLtpBufIdx - i - 1] = Smulwb(invGainQ31, sLtp[d.LtpMemLength - i - 1]);
                }
                else if (gainAdjQ16 != 1 << 16)
                {
                    for (var i = 0; i < lag + 2; i++) sLtpQ15[sLtpBufIdx - i - 1] = Smulww(gainAdjQ16, sLtpQ15[sLtpBufIdx - i - 1]);
                }
                var predLag = sLtpBufIdx - lag + 2;
                for (var i = 0; i < d.SubfrLength; i++)
                {
                    var ltpPred = 2;
                    ltpPred = Smlawb(ltpPred, sLtpQ15[predLag], ctrl.LtpCoefQ14[bOff]);
                    ltpPred = Smlawb(ltpPred, sLtpQ15[predLag - 1], ctrl.LtpCoefQ14[bOff + 1]);
                    ltpPred = Smlawb(ltpPred, sLtpQ15[predLag - 2], ctrl.LtpCoefQ14[bOff + 2]);
                    ltpPred = Smlawb(ltpPred, sLtpQ15[predLag - 3], ctrl.LtpCoefQ14[bOff + 3]);
                    ltpPred = Smlawb(ltpPred, sLtpQ15[predLag - 4], ctrl.LtpCoefQ14[bOff + 4]);
                    predLag++;
                    resQ14[i] = unchecked(d.ExcQ14[pexc + i] + (ltpPred << 1));
                    sLtpQ15[sLtpBufIdx] = resQ14[i] << 1;
                    sLtpBufIdx++;
                }
                res = resQ14;
                resBase = 0;
            }
            else { res = d.ExcQ14; resBase = pexc; }
            for (var i = 0; i < d.SubfrLength; i++)
            {
                var pred = d.LpcOrder >> 1;
                for (var j = 0; j < d.LpcOrder; j++) pred = Smlawb(pred, sLpcQ14[16 + i - 1 - j], a[j]);
                sLpcQ14[16 + i] = AddSat32(res[resBase + i], LshiftSat32(pred, 4));
                xq[pxq + i] = Sat16(RshiftRound(Smulww(sLpcQ14[16 + i], gainQ10), 8));
            }
            Array.Copy(sLpcQ14, d.SubfrLength, sLpcQ14, 0, 16);
            pexc += d.SubfrLength;
            pxq += d.SubfrLength;
        }
        Array.Copy(sLpcQ14, d.SlpcQ14Buf, 16);
    }
}
