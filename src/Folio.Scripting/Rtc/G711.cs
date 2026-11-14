namespace Folio.Scripting.Rtc;

/// <summary>ITU-T G.711 PCMU (mu-law, payload type 0) and PCMA (A-law, payload type 8) for 8 kHz mono audio.</summary>
internal static class G711
{
    internal static byte EncodeMuLaw(short sample)
    {
        const int bias = 0x84, clip = 32635;
        int s = sample, sign = (s >> 8) & 0x80;
        if (sign != 0) s = -s;
        if (s > clip) s = clip;
        s += bias;
        var exponent = 7;
        for (var mask = 0x4000; (s & mask) == 0 && exponent > 0; exponent--, mask >>= 1) { }
        var mantissa = (s >> (exponent + 3)) & 0x0F;
        return (byte)~(sign | (exponent << 4) | mantissa);
    }

    internal static short DecodeMuLaw(byte value)
    {
        value = (byte)~value;
        var t = (((value & 0x0F) << 3) + 0x84) << ((value & 0x70) >> 4);
        return (short)((value & 0x80) != 0 ? 0x84 - t : t - 0x84);
    }

    private static readonly int[] SegmentEnd = [0x1F, 0x3F, 0x7F, 0xFF, 0x1FF, 0x3FF, 0x7FF, 0xFFF];

    internal static byte EncodeALaw(short sample)
    {
        int pcm = sample >> 3, mask;
        if (pcm >= 0) mask = 0xD5; else { mask = 0x55; pcm = -pcm - 1; }
        var segment = 0;
        while (segment < 8 && pcm > SegmentEnd[segment]) segment++;
        if (segment >= 8) return (byte)(0x7F ^ mask);
        var value = segment << 4 | (segment < 2 ? (pcm >> 1) & 0x0F : (pcm >> segment) & 0x0F);
        return (byte)(value ^ mask);
    }

    internal static short DecodeALaw(byte value)
    {
        value ^= 0x55;
        var t = (value & 0x0F) << 4;
        var segment = (value & 0x70) >> 4;
        t = segment switch { 0 => t + 8, 1 => t + 0x108, _ => (t + 0x108) << (segment - 1) };
        return (short)((value & 0x80) != 0 ? t : -t);
    }
}
