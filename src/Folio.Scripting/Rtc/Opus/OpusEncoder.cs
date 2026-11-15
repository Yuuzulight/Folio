// A deliberately small Opus encoder: mono, 48 kHz, CELT only, fullband, 20 ms frames, constant bitrate.
// It produces valid RFC 6716 packets (TOC config 31) that any conforming decoder accepts. It reuses the CELT band coder
// and allocation of the decoder (after the Opus reference implementation, BSD 3-clause, see docs/webrtc-opus.md), and
// leaves out everything that needs analysis: no transient detection (always one long MDCT), no pitch pre-filter, no
// time/frequency resolution switching, no spreading decision (normal), no dynamic allocation boosts, no stereo, no VBR,
// no SILK or hybrid modes and no in-band FEC. Quality is therefore below libopus at the same bitrate; see the document.
namespace Folio.Scripting.Rtc.Opus;

internal sealed class OpusEncoder
{
    internal const int FrameSamples = 960;
    internal const int MinBytes = 8, MaxBytes = 1275;
    private const int N = FrameSamples, Ov = Celt.Overlap, Ne = Celt.NbEBands;

    private readonly Mdct _mdct = new(2 * Celt.ShortMdctSize * (1 << Celt.MaxLm), Celt.MaxLm);
    private readonly float[] _inBuf = new float[N + Ov];
    private readonly float[] _freq = new float[N];
    private readonly float[] _x = new float[N];
    private readonly float[] _bandE = new float[Ne], _bandLogE = new float[Ne], _oldE = new float[Ne], _error = new float[Ne];
    private float _preemphMem;
    private int _frameIndex;

    /// <summary>Target bitrate in bits per second (8 kbit/s to 128 kbit/s); the packet size is fixed from it.</summary>
    internal int Bitrate { get; set; } = 40000;

    internal OpusEncoder()
    {
        Array.Fill(_oldE, -28f);
    }

    internal void Reset()
    {
        Array.Clear(_inBuf);
        _preemphMem = 0;
        _frameIndex = 0;
        Array.Fill(_oldE, -28f);
    }

    internal int PacketBytes => Math.Clamp(Bitrate * 20 / 8000, MinBytes, MaxBytes) + 1;

    /// <summary>Encodes 960 mono samples (nominal range [-1, 1]) into <paramref name="packet"/>; returns the packet length.</summary>
    internal int Encode(ReadOnlySpan<float> pcm, Span<byte> packet, out uint finalRange)
    {
        finalRange = 0;
        if (pcm.Length < N) throw new ArgumentException("one 20 ms frame (960 samples) is required", nameof(pcm));
        var nbBytes = PacketBytes - 1;
        if (packet.Length < nbBytes + 1) throw new ArgumentException("packet buffer too small", nameof(packet));

        // pre-emphasis and the MDCT input: the last 120 samples of the previous frame, then this frame
        Array.Copy(_inBuf, N, _inBuf, 0, Ov);
        for (var i = 0; i < N; i++)
        {
            var v = float.IsFinite(pcm[i]) ? Math.Clamp(pcm[i], -4f, 4f) * 32768f : 0f;
            _inBuf[Ov + i] = v - _preemphMem;
            _preemphMem = Celt.Preemphasis * v;
        }
        _mdct.Forward(_inBuf, 0, _freq, 0, Celt.Window, Ov, Celt.MaxLm - 3, 1);
        CeltBands.ComputeBandEnergies(_freq, _bandE, Ne, 1, 3);
        for (var i = 0; i < Ne; i++) _bandLogE[i] = MathF.Log2(_bandE[i]) - Celt.EMeans[i];
        CeltBands.NormaliseBands(_freq, _x, _bandE, Ne, 1, 8);

        var buf = new byte[nbBytes];
        var enc = new RangeEncoder(buf, nbBytes);
        var totalBits = nbBytes * 8;
        const int lm = 3;
        const int start = 0, end = Ne;

        var tell = enc.Tell;
        if (tell == 1) enc.BitLogp(0, 15); // not silent
        tell = enc.Tell;
        if (start == 0 && tell + 16 <= totalBits) enc.BitLogp(0, 1); // no post-filter
        tell = enc.Tell;
        if (lm > 0 && tell + 3 <= totalBits) enc.BitLogp(0, 3); // one long block

        var intra = _frameIndex % 20 == 0;
        QuantCoarseEnergy(start, end, totalBits, enc, lm, intra, nbBytes);

        // time/frequency resolution: all zero. Mirrors the decoder's tf_decode.
        var tfBudget = totalBits;
        tell = enc.Tell;
        var logp = 4;
        var tfSelectRsv = tell + logp + 1 <= tfBudget ? 1 : 0;
        tfBudget -= tfSelectRsv;
        for (var i = start; i < end; i++)
        {
            if (tell + logp <= tfBudget)
            {
                enc.BitLogp(0, logp);
                tell = enc.Tell;
            }
            logp = 5;
        }
        tell = enc.Tell;
        if (tell + 4 <= totalBits) enc.Icdf(Celt.SpreadNormal, Celt.SpreadIcdf, 5);

        var cap = new int[Ne];
        Celt.InitCaps(cap, lm, 1);
        var offsets = new int[Ne];
        var dynallocLogp = 6;
        var totalFrac = totalBits << Celt.BitRes;
        var tellFrac = (int)enc.TellFrac;
        for (var i = start; i < end; i++)
        {
            if (tellFrac + (dynallocLogp << Celt.BitRes) < totalFrac && 0 < cap[i])
            {
                enc.BitLogp(0, dynallocLogp);
                tellFrac = (int)enc.TellFrac;
            }
        }
        const int allocTrim = 5;
        if (tellFrac + (6 << Celt.BitRes) <= totalFrac) enc.Icdf(allocTrim, Celt.TrimIcdf, 7);

        var bits = ((nbBytes * 8) << Celt.BitRes) - (int)enc.TellFrac - 1;
        var pulses = new int[Ne];
        var fineQuant = new int[Ne];
        var finePriority = new int[Ne];
        var intensity = 0;
        var dualStereo = false;
        var codedBands = CeltDecoder.ComputeAllocation(start, end, offsets, cap, allocTrim, ref intensity, ref dualStereo, bits, out var balance,
            pulses, fineQuant, finePriority, 1, lm, null, enc);

        QuantFineEnergy(start, end, fineQuant, enc);
        var collapse = new byte[Ne];
        var seed = 0u;
        var tfRes = new int[Ne];
        CeltBands.QuantAllBands(true, start, end, _x, default, collapse, _bandE, pulses, false, Celt.SpreadNormal, false, intensity, tfRes,
            nbBytes * (8 << Celt.BitRes), balance, null, enc, lm, codedBands, ref seed, true);
        QuantEnergyFinalise(start, end, fineQuant, finePriority, nbBytes * 8 - enc.Tell, enc);
        enc.Done();

        packet[0] = 0xF8; // config 31 (CELT fullband 20 ms), mono, one frame
        buf.CopyTo(packet[1..]);
        finalRange = enc.Rng;
        _frameIndex++;
        return nbBytes + 1;
    }

    private void QuantCoarseEnergy(int start, int end, int budget, RangeEncoder enc, int lm, bool intra, int nbBytes)
    {
        if (enc.Tell + 3 > budget) intra = false;
        if (enc.Tell + 3 <= budget) enc.BitLogp(intra ? 1 : 0, 3);
        float coef, beta;
        var maxDecay = 16f;
        if (end - start > 10) maxDecay = MathF.Min(maxDecay, .125f * nbBytes);
        if (intra) { coef = 0; beta = Celt.BetaIntra; }
        else { coef = Celt.PredCoef[lm]; beta = Celt.BetaCoef[lm]; }
        var pi0 = (lm * 2 + (intra ? 1 : 0)) * 42;
        float prev = 0;
        for (var i = start; i < end; i++)
        {
            var x = _bandLogE[i];
            var oldE = MathF.Max(-9f, _oldE[i]);
            var f = x - coef * oldE - prev;
            var qi = (int)MathF.Floor(.5f + f);
            var decayBound = MathF.Max(-28f, _oldE[i]) - maxDecay;
            if (qi < 0 && x < decayBound)
            {
                qi += (int)(decayBound - x);
                if (qi > 0) qi = 0;
            }
            var tell = enc.Tell;
            var bitsLeft = budget - tell - 3 * (end - i);
            if (i != start && bitsLeft < 30)
            {
                if (bitsLeft < 24) qi = Math.Min(1, qi);
                if (bitsLeft < 16) qi = Math.Max(-1, qi);
            }
            if (budget - tell >= 15)
            {
                var pi = 2 * Math.Min(i, 20);
                enc.Laplace(ref qi, (uint)Celt.EProbModel[pi0 + pi] << 7, Celt.EProbModel[pi0 + pi + 1] << 6);
            }
            else if (budget - tell >= 2)
            {
                qi = Math.Max(-1, Math.Min(qi, 1));
                enc.Icdf(2 * qi ^ (qi < 0 ? -1 : 0), Celt.SmallEnergyIcdf, 2);
            }
            else if (budget - tell >= 1)
            {
                qi = Math.Min(0, qi);
                enc.BitLogp(-qi, 1);
            }
            else qi = -1;
            _error[i] = f - qi;
            float q = qi;
            _oldE[i] = coef * oldE + prev + q;
            prev = prev + q - beta * q;
        }
    }

    private void QuantFineEnergy(int start, int end, int[] fineQuant, RangeEncoder enc)
    {
        for (var i = start; i < end; i++)
        {
            if (fineQuant[i] <= 0) continue;
            var frac = 1 << fineQuant[i];
            var q2 = (int)MathF.Floor((_error[i] + .5f) * frac);
            if (q2 > frac - 1) q2 = frac - 1;
            if (q2 < 0) q2 = 0;
            enc.Bits((uint)q2, fineQuant[i]);
            var offset = (q2 + .5f) * (1 << (14 - fineQuant[i])) * (1f / 16384) - .5f;
            _oldE[i] += offset;
            _error[i] -= offset;
        }
    }

    private void QuantEnergyFinalise(int start, int end, int[] fineQuant, int[] finePriority, int bitsLeft, RangeEncoder enc)
    {
        for (var prio = 0; prio < 2; prio++)
            for (var i = start; i < end && bitsLeft >= 1; i++)
            {
                if (fineQuant[i] >= Celt.MaxFineBits || finePriority[i] != prio) continue;
                var q2 = _error[i] < 0 ? 0 : 1;
                enc.Bits((uint)q2, 1);
                var offset = (q2 - .5f) * (1 << (14 - fineQuant[i] - 1)) * (1f / 16384);
                _oldE[i] += offset;
                _error[i] -= offset;
                bitsLeft--;
            }
    }
}
