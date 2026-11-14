// SILK packet loss concealment and comfort noise (RFC 6716 section 4.2.8), translated from the Opus reference implementation
// (Copyright (c) 2006-2011 Skype Limited, BSD 3-clause; see docs/webrtc-opus.md).
using static Folio.Scripting.Rtc.Opus.SilkMath;

namespace Folio.Scripting.Rtc.Opus;

internal static class SilkConceal
{
    private const int RandBufSize = 128, RandBufMask = 127;
    private static readonly short[] HarmAttQ15 = [32440, 31130];
    private static readonly short[] RandAttenuateVQ15 = [31130, 26214];
    private static readonly short[] RandAttenuateUvQ15 = [32440, 29491];

    internal static void PlcReset(SilkChannelState d)
    {
        d.Plc.PitchLQ8 = d.FrameLength << (8 - 1);
        d.Plc.PrevGainQ16[0] = 1 << 16;
        d.Plc.PrevGainQ16[1] = 1 << 16;
        d.Plc.SubfrLength = 20;
        d.Plc.NbSubfr = 2;
    }

    internal static void CngReset(SilkChannelState d)
    {
        var step = short.MaxValue / (d.LpcOrder + 1);
        var acc = 0;
        for (var i = 0; i < d.LpcOrder; i++) { acc += step; d.Cng.SmthNlsfQ15[i] = (short)acc; }
        d.Cng.SmthGainQ16 = 0;
        d.Cng.RandSeed = 3176576;
    }

    internal static void Plc(SilkChannelState d, SilkDecoderControl ctrl, Span<short> frame, bool lost)
    {
        if (d.FsKHz != d.Plc.FsKHz)
        {
            PlcReset(d);
            d.Plc.FsKHz = d.FsKHz;
        }
        if (lost) { Conceal(d, ctrl, frame); d.LossCnt++; }
        else Update(d, ctrl);
    }

    private static void Update(SilkChannelState d, SilkDecoderControl ctrl)
    {
        var plc = d.Plc;
        d.PrevSignalType = d.SignalType;
        var ltpGainQ14 = 0;
        if (d.SignalType == SilkDecoder.TypeVoiced)
        {
            for (var j = 0; j * d.SubfrLength < ctrl.PitchL[d.NbSubfr - 1]; j++)
            {
                if (j == d.NbSubfr) break;
                var temp = 0;
                for (var i = 0; i < 5; i++) temp += ctrl.LtpCoefQ14[(d.NbSubfr - 1 - j) * 5 + i];
                if (temp > ltpGainQ14)
                {
                    ltpGainQ14 = temp;
                    Array.Copy(ctrl.LtpCoefQ14, (d.NbSubfr - 1 - j) * 5, plc.LtpCoefQ14, 0, 5);
                    plc.PitchLQ8 = ctrl.PitchL[d.NbSubfr - 1 - j] << 8;
                }
            }
            Array.Clear(plc.LtpCoefQ14);
            plc.LtpCoefQ14[2] = (short)ltpGainQ14;
            if (ltpGainQ14 < 11469)
            {
                var scale = (11469 << 10) / Math.Max(ltpGainQ14, 1);
                for (var i = 0; i < 5; i++) plc.LtpCoefQ14[i] = (short)(Smulbb(plc.LtpCoefQ14[i], scale) >> 10);
            }
            else if (ltpGainQ14 > 15565)
            {
                var scale = (15565 << 14) / Math.Max(ltpGainQ14, 1);
                for (var i = 0; i < 5; i++) plc.LtpCoefQ14[i] = (short)(Smulbb(plc.LtpCoefQ14[i], scale) >> 14);
            }
        }
        else
        {
            plc.PitchLQ8 = Smulbb(d.FsKHz, 18) << 8;
            Array.Clear(plc.LtpCoefQ14);
        }
        Array.Copy(ctrl.PredCoefQ12[1], plc.PrevLpcQ12, d.LpcOrder);
        plc.PrevLtpScaleQ14 = (short)ctrl.LtpScaleQ14;
        Array.Copy(ctrl.GainsQ16, d.NbSubfr - 2, plc.PrevGainQ16, 0, 2);
        plc.SubfrLength = d.SubfrLength;
        plc.NbSubfr = d.NbSubfr;
    }

    private static void Conceal(SilkChannelState d, SilkDecoderControl ctrl, Span<short> frame)
    {
        var plc = d.Plc;
        var sLtpQ14 = d.SLtpQ15; // reused scratch: ltp_mem_length + frame_length entries
        var sLtp = d.SLtp;
        Span<int> prevGainQ10 = stackalloc int[2];
        Span<short> aQ12 = stackalloc short[16];
        prevGainQ10[0] = plc.PrevGainQ16[0] >> 6;
        prevGainQ10[1] = plc.PrevGainQ16[1] >> 6;
        if (d.FirstFrameAfterReset) Array.Clear(plc.PrevLpcQ12);

        // energy of the last two excitation subframes decides which half of the history feeds the noise
        Span<short> excBuf = stackalloc short[2 * 80];
        var sub = d.SubfrLength;
        for (var k = 0; k < 2; k++)
            for (var i = 0; i < sub; i++)
                excBuf[k * sub + i] = Sat16(Smulww(d.ExcQ14[i + (k + d.NbSubfr - 2) * sub], prevGainQ10[k]) >> 8);
        SumSqrShift(out var energy1, out var shift1, excBuf[..sub], sub);
        SumSqrShift(out var energy2, out var shift2, excBuf.Slice(sub, sub), sub);
        int randPtr;
        if ((energy1 >> shift2) < (energy2 >> shift1)) randPtr = Math.Max(0, (plc.NbSubfr - 1) * plc.SubfrLength - RandBufSize);
        else randPtr = Math.Max(0, plc.NbSubfr * plc.SubfrLength - RandBufSize);

        var bQ14 = plc.LtpCoefQ14;
        var randScaleQ14 = plc.RandScaleQ14;
        var harmGainQ15 = HarmAttQ15[Math.Min(1, d.LossCnt)];
        var randGainQ15 = d.PrevSignalType == SilkDecoder.TypeVoiced ? RandAttenuateVQ15[Math.Min(1, d.LossCnt)] : RandAttenuateUvQ15[Math.Min(1, d.LossCnt)];
        Bwexpander(plc.PrevLpcQ12, d.LpcOrder, 64881); // 0.99 in Q16
        plc.PrevLpcQ12.AsSpan(0, d.LpcOrder).CopyTo(aQ12);

        if (d.LossCnt == 0)
        {
            randScaleQ14 = 1 << 14;
            if (d.PrevSignalType == SilkDecoder.TypeVoiced)
            {
                for (var i = 0; i < 5; i++) randScaleQ14 -= bQ14[i];
                randScaleQ14 = Math.Max((short)3277, randScaleQ14);
                randScaleQ14 = (short)(Smulbb(randScaleQ14, plc.PrevLtpScaleQ14) >> 14);
            }
            else
            {
                var invGainQ30 = LpcInversePredGain(plc.PrevLpcQ12, d.LpcOrder);
                var down = Math.Min((1 << 30) >> 3, invGainQ30);
                down = Math.Max((1 << 30) >> 8, down);
                down <<= 3;
                randGainQ15 = (short)(Smulwb(down, randGainQ15) >> 14);
            }
        }
        var randSeed = plc.RandSeed;
        var lag = RshiftRound(plc.PitchLQ8, 8);
        var sLtpBufIdx = d.LtpMemLength;
        var idx = d.LtpMemLength - lag - d.LpcOrder - 2;
        if (idx <= 0) idx = 1;
        LpcAnalysisFilter(sLtp.AsSpan(idx), d.OutBuf.AsSpan(idx), aQ12, d.LtpMemLength - idx, d.LpcOrder);
        var invGainQ30b = Inverse32Varq(plc.PrevGainQ16[1], 46);
        invGainQ30b = Math.Min(invGainQ30b, int.MaxValue >> 1);
        for (var i = idx + d.LpcOrder; i < d.LtpMemLength; i++) sLtpQ14[i] = Smulwb(invGainQ30b, sLtp[i]);

        for (var k = 0; k < d.NbSubfr; k++)
        {
            var predLag = sLtpBufIdx - lag + 2;
            for (var i = 0; i < d.SubfrLength; i++)
            {
                var ltpPred = 2;
                ltpPred = Smlawb(ltpPred, sLtpQ14[predLag], bQ14[0]);
                ltpPred = Smlawb(ltpPred, sLtpQ14[predLag - 1], bQ14[1]);
                ltpPred = Smlawb(ltpPred, sLtpQ14[predLag - 2], bQ14[2]);
                ltpPred = Smlawb(ltpPred, sLtpQ14[predLag - 3], bQ14[3]);
                ltpPred = Smlawb(ltpPred, sLtpQ14[predLag - 4], bQ14[4]);
                predLag++;
                randSeed = Rand(randSeed);
                idx = (randSeed >> 25) & RandBufMask;
                sLtpQ14[sLtpBufIdx] = Smlawb(ltpPred, d.ExcQ14[randPtr + idx], randScaleQ14) << 2;
                sLtpBufIdx++;
            }
            for (var j = 0; j < 5; j++) bQ14[j] = (short)(Smulbb(harmGainQ15, bQ14[j]) >> 15);
            randScaleQ14 = (short)(Smulbb(randScaleQ14, randGainQ15) >> 15);
            plc.PitchLQ8 = Smlawb(plc.PitchLQ8, plc.PitchLQ8, 655);
            plc.PitchLQ8 = Math.Min(plc.PitchLQ8, Smulbb(18, d.FsKHz) << 8);
            lag = RshiftRound(plc.PitchLQ8, 8);
        }

        // sLPC occupies sLtpQ14[ltp_mem_length - 16 ..]; the first 16 entries are the previous LPC state
        var baseIdx = d.LtpMemLength - 16;
        for (var i = 0; i < 16; i++) sLtpQ14[baseIdx + i] = d.SlpcQ14Buf[i];
        for (var i = 0; i < d.FrameLength; i++)
        {
            var pred = d.LpcOrder >> 1;
            for (var j = 0; j < d.LpcOrder; j++) pred = Smlawb(pred, sLtpQ14[baseIdx + 16 + i - 1 - j], aQ12[j]);
            sLtpQ14[baseIdx + 16 + i] = AddSat32(sLtpQ14[baseIdx + 16 + i], LshiftSat32(pred, 4));
            frame[i] = Sat16(Sat16(RshiftRound(Smulww(sLtpQ14[baseIdx + 16 + i], prevGainQ10[1]), 8)));
        }
        for (var i = 0; i < 16; i++) d.SlpcQ14Buf[i] = sLtpQ14[baseIdx + d.FrameLength + i];
        plc.RandSeed = randSeed;
        plc.RandScaleQ14 = randScaleQ14;
        for (var i = 0; i < 4; i++) ctrl.PitchL[i] = lag;
    }

    internal static void PlcGlueFrames(SilkChannelState d, Span<short> frame, int length)
    {
        var plc = d.Plc;
        if (d.LossCnt != 0)
        {
            SumSqrShift(out plc.ConcEnergy, out plc.ConcEnergyShift, frame, length);
            plc.LastFrameLost = 1;
        }
        else
        {
            if (plc.LastFrameLost != 0)
            {
                SumSqrShift(out var energy, out var energyShift, frame, length);
                if (energyShift > plc.ConcEnergyShift) plc.ConcEnergy >>= energyShift - plc.ConcEnergyShift;
                else if (energyShift < plc.ConcEnergyShift) energy >>= plc.ConcEnergyShift - energyShift;
                if (energy > plc.ConcEnergy)
                {
                    var lz = Clz32(plc.ConcEnergy) - 1;
                    plc.ConcEnergy <<= lz;
                    energy >>= Math.Max(24 - lz, 0);
                    var fracQ24 = plc.ConcEnergy / Math.Max(energy, 1);
                    var gainQ16 = SqrtApprox(fracQ24) << 4;
                    var slopeQ16 = ((1 << 16) - gainQ16) / length;
                    slopeQ16 <<= 2;
                    for (var i = 0; i < length; i++)
                    {
                        frame[i] = (short)Smulwb(gainQ16, frame[i]);
                        gainQ16 += slopeQ16;
                        if (gainQ16 > 1 << 16) break;
                    }
                }
            }
            plc.LastFrameLost = 0;
        }
    }

    internal static void Cng(SilkChannelState d, SilkDecoderControl ctrl, Span<short> frame, int length)
    {
        var cng = d.Cng;
        if (d.FsKHz != cng.FsKHz)
        {
            CngReset(d);
            cng.FsKHz = d.FsKHz;
        }
        if (d.LossCnt == 0 && d.PrevSignalType == SilkDecoder.TypeNoVoiceActivity)
        {
            for (var i = 0; i < d.LpcOrder; i++)
                cng.SmthNlsfQ15[i] += (short)Smulwb(d.PrevNlsfQ15[i] - cng.SmthNlsfQ15[i], 16348);
            var maxGain = 0;
            var subfr = 0;
            for (var i = 0; i < d.NbSubfr; i++)
                if (ctrl.GainsQ16[i] > maxGain) { maxGain = ctrl.GainsQ16[i]; subfr = i; }
            Array.Copy(cng.ExcBufQ14, 0, cng.ExcBufQ14, d.SubfrLength, (d.NbSubfr - 1) * d.SubfrLength);
            Array.Copy(d.ExcQ14, subfr * d.SubfrLength, cng.ExcBufQ14, 0, d.SubfrLength);
            for (var i = 0; i < d.NbSubfr; i++)
            {
                cng.SmthGainQ16 += Smulwb(ctrl.GainsQ16[i] - cng.SmthGainQ16, 4634);
                if (Smulww(cng.SmthGainQ16, 46396) > ctrl.GainsQ16[i]) cng.SmthGainQ16 = ctrl.GainsQ16[i];
            }
        }
        if (d.LossCnt != 0)
        {
            Span<int> sig = stackalloc int[320 + 16];
            Span<short> aQ12 = stackalloc short[16];
            var gainQ16 = Smulww(d.Plc.RandScaleQ14, d.Plc.PrevGainQ16[1]);
            if (gainQ16 >= 1 << 21 || cng.SmthGainQ16 > 1 << 23)
            {
                gainQ16 = Smultt(gainQ16, gainQ16);
                gainQ16 = Smultt(cng.SmthGainQ16, cng.SmthGainQ16) - (gainQ16 << 5);
                gainQ16 = SqrtApprox(gainQ16) << 16;
            }
            else
            {
                gainQ16 = Smulww(gainQ16, gainQ16);
                gainQ16 = Smulww(cng.SmthGainQ16, cng.SmthGainQ16) - (gainQ16 << 5);
                gainQ16 = SqrtApprox(gainQ16) << 8;
            }
            var gainQ10 = gainQ16 >> 6;
            // CNG excitation
            var mask = 255;
            while (mask > length) mask >>= 1;
            var seed = cng.RandSeed;
            for (var i = 0; i < length; i++)
            {
                seed = Rand(seed);
                sig[16 + i] = cng.ExcBufQ14[(seed >> 24) & mask];
            }
            cng.RandSeed = seed;
            SilkDecoder.Nlsf2A(aQ12, cng.SmthNlsfQ15, d.LpcOrder);
            for (var i = 0; i < 16; i++) sig[i] = cng.SynthState[i];
            for (var i = 0; i < length; i++)
            {
                var pred = d.LpcOrder >> 1;
                for (var j = 0; j < d.LpcOrder; j++) pred = Smlawb(pred, sig[16 + i - 1 - j], aQ12[j]);
                sig[16 + i] = AddSat32(sig[16 + i], LshiftSat32(pred, 4));
                frame[i] = AddSat16(frame[i], Sat16(RshiftRound(Smulww(sig[16 + i], gainQ10), 8)));
            }
            for (var i = 0; i < 16; i++) cng.SynthState[i] = sig[length + i];
        }
        else Array.Clear(cng.SynthState, 0, d.LpcOrder);
    }
}
