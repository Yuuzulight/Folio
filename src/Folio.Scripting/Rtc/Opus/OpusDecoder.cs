// Opus decoder (RFC 6716): packet framing, mode switching, hybrid SILK + CELT, redundancy and loss concealment.
// After the Opus reference implementation (src/opus_decoder.c, src/opus.c; Copyright (c) 2010 Xiph.Org Foundation,
// Skype Limited; BSD 3-clause, see docs/webrtc-opus.md). The output rate is fixed at 48 kHz; the float output is in [-1, 1].
namespace Folio.Scripting.Rtc.Opus;

/// <summary>Result codes of the managed Opus codec; negative values are errors, as in the C API.</summary>
internal static class OpusError
{
    internal const int Ok = 0, BadArg = -1, BufferTooSmall = -2, InternalError = -3, InvalidPacket = -4;
}

internal static class OpusPacket
{
    internal const int ModeSilkOnly = 1000, ModeHybrid = 1001, ModeCeltOnly = 1002;
    internal const int BandwidthNarrow = 1101, BandwidthMedium = 1102, BandwidthWide = 1103, BandwidthSuperWide = 1104, BandwidthFull = 1105;
    internal const int MaxFrames = 48;
    internal const int MaxPacketBytes = 1276 * 48;

    internal static int GetMode(byte toc) => (toc & 0x80) != 0 ? ModeCeltOnly : (toc & 0x60) == 0x60 ? ModeHybrid : ModeSilkOnly;

    internal static int GetBandwidth(byte toc)
    {
        if ((toc & 0x80) != 0)
        {
            var bw = BandwidthMedium + ((toc >> 5) & 0x3);
            return bw == BandwidthMedium ? BandwidthNarrow : bw;
        }
        if ((toc & 0x60) == 0x60) return (toc & 0x10) != 0 ? BandwidthFull : BandwidthSuperWide;
        return BandwidthNarrow + ((toc >> 5) & 0x3);
    }

    /// <summary>Samples per frame at 48 kHz.</summary>
    internal static int GetSamplesPerFrame(byte toc)
    {
        const int fs = 48000;
        if ((toc & 0x80) != 0) return (fs << ((toc >> 3) & 0x3)) / 400;
        if ((toc & 0x60) == 0x60) return (toc & 0x08) != 0 ? fs / 50 : fs / 100;
        var size = (toc >> 3) & 0x3;
        return size == 3 ? fs * 60 / 1000 : (fs << size) / 100;
    }

    internal static int GetChannels(byte toc) => (toc & 0x4) != 0 ? 2 : 1;

    private static int ParseSize(ReadOnlySpan<byte> data, int len, out int size)
    {
        if (len < 1) { size = -1; return -1; }
        if (data[0] < 252) { size = data[0]; return 1; }
        if (len < 2) { size = -1; return -1; }
        size = 4 * data[1] + data[0];
        return 2;
    }

    /// <summary>
    /// Splits a packet into frames (opus_packet_parse_impl, not self-delimited). Returns the number of frames or a negative error;
    /// <paramref name="offset"/> is where the first frame starts and <paramref name="sizes"/> holds the frame lengths.
    /// Every length is checked against the packet, so a frame never extends past the input.
    /// </summary>
    internal static int Parse(ReadOnlySpan<byte> data, Span<int> sizes, out int offset, out int payloadOffset)
    {
        offset = 0; payloadOffset = 0;
        var len = data.Length;
        if (len < 1) return OpusError.InvalidPacket;
        var toc = data[0];
        var framesize = GetSamplesPerFrame(toc);
        var pos = 1;
        len--;
        var lastSize = len;
        int count;
        var pad = 0;
        switch (toc & 0x3)
        {
            case 0:
                count = 1;
                break;
            case 1:
                count = 2;
                if ((len & 1) != 0) return OpusError.InvalidPacket;
                lastSize = len / 2;
                sizes[0] = lastSize;
                break;
            case 2:
                {
                    count = 2;
                    var bytes = ParseSize(data[pos..], len, out var s0);
                    len -= bytes;
                    if (s0 < 0 || s0 > len) return OpusError.InvalidPacket;
                    sizes[0] = s0;
                    pos += bytes;
                    lastSize = len - s0;
                    break;
                }
            default:
                {
                    if (len < 1) return OpusError.InvalidPacket;
                    var ch = data[pos++];
                    len--;
                    count = ch & 0x3F;
                    if (count <= 0 || framesize * count > 5760) return OpusError.InvalidPacket;
                    if ((ch & 0x40) != 0)
                    {
                        int p;
                        do
                        {
                            if (len <= 0) return OpusError.InvalidPacket;
                            p = data[pos++];
                            len--;
                            var tmp = p == 255 ? 254 : p;
                            len -= tmp;
                            pad += tmp;
                        } while (p == 255);
                    }
                    if (len < 0) return OpusError.InvalidPacket;
                    if ((ch & 0x80) != 0)
                    {
                        // VBR: the last frame takes what is left
                        lastSize = len;
                        for (var i = 0; i < count - 1; i++)
                        {
                            var bytes = ParseSize(data[pos..], len, out var s);
                            len -= bytes;
                            if (s < 0 || s > len) return OpusError.InvalidPacket;
                            sizes[i] = s;
                            pos += bytes;
                            lastSize -= bytes + s;
                        }
                        if (lastSize < 0) return OpusError.InvalidPacket;
                    }
                    else
                    {
                        // CBR
                        if (len % count != 0) return OpusError.InvalidPacket;
                        lastSize = len / count;
                        for (var i = 0; i < count - 1; i++) sizes[i] = lastSize;
                    }
                    break;
                }
        }
        if (lastSize > 1275) return OpusError.InvalidPacket;
        sizes[count - 1] = lastSize;
        offset = pos;
        payloadOffset = pos;
        // the frames must fit in what is left of the packet
        var total = 0;
        for (var i = 0; i < count; i++) total += sizes[i];
        if (total > len + 0 && (toc & 0x3) != 3) return OpusError.InvalidPacket;
        if (pos + total + pad > data.Length) return OpusError.InvalidPacket;
        return count;
    }

    /// <summary>Total samples per channel at 48 kHz, or a negative error for an invalid packet.</summary>
    internal static int GetNbSamples(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1) return OpusError.BadArg;
        Span<int> sizes = stackalloc int[MaxFrames];
        var count = Parse(data, sizes, out _, out _);
        if (count < 0) return count;
        var samples = count * GetSamplesPerFrame(data[0]);
        return samples * 25 > 48000 * 3 ? OpusError.InvalidPacket : samples; // at most 120 ms
    }
}

internal sealed class OpusDecoder
{
    private const int Fs = 48000;
    private const int F20 = Fs / 50, F10 = F20 / 2, F5 = F10 / 2, F25 = F5 / 2;

    private readonly int _channels;
    private readonly CeltDecoder _celt;
    private readonly SilkDecoder _silk = new();
    private readonly SilkControl _silkCtl = new() { ApiSampleRate = Fs };
    private int _streamChannels, _bandwidth, _mode, _prevMode, _frameSize;
    private bool _prevRedundancy;
    private int _lastPacketDuration;
    private readonly short[] _pcmSilk;
    private readonly float[] _transition, _redundant;
    private readonly RangeDecoder _emptyDecoder = new(Array.Empty<byte>(), 0, 0);

    /// <summary>The final range coder state of the last decoded packet, used by the conformance vectors.</summary>
    internal uint FinalRange { get; private set; }

    internal int Channels => _channels;
    internal int LastPacketDuration => _lastPacketDuration;
    internal int LastBandwidth => _bandwidth;
    internal int LastMode => _mode;

    internal OpusDecoder(int channels)
    {
        if (channels is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(channels));
        _channels = channels;
        _streamChannels = channels;
        _celt = new CeltDecoder(channels);
        _pcmSilk = new short[F20 * 3 * channels];
        _transition = new float[F5 * channels];
        _redundant = new float[F5 * channels];
        _frameSize = F20;
        _prevMode = 0;
    }

    internal void Reset()
    {
        _celt.Reset();
        _silk.Reset();
        _streamChannels = _channels;
        _prevMode = 0;
        _prevRedundancy = false;
        _lastPacketDuration = 0;
        FinalRange = 0;
    }

    private static void SmoothFade(ReadOnlySpan<float> in1, ReadOnlySpan<float> in2, Span<float> output, int overlap, int channels)
    {
        var window = Celt.Window;
        for (var c = 0; c < channels; c++)
            for (var i = 0; i < overlap; i++)
            {
                var w = window[i] * window[i];
                output[i * channels + c] = w * in2[i * channels + c] + (1f - w) * in1[i * channels + c];
            }
    }

    /// <summary>
    /// Decodes a packet into <paramref name="pcm"/> (interleaved, 48 kHz). An empty packet conceals a lost frame of
    /// <paramref name="frameSize"/> samples. With <paramref name="fec"/> the in-band redundancy of this packet recovers the frame before it.
    /// Returns the number of samples per channel or a negative error.
    /// </summary>
    internal int Decode(ReadOnlySpan<byte> data, Span<float> pcm, int frameSize, bool fec = false)
    {
        if (frameSize <= 0) return OpusError.BadArg;
        if (pcm.Length < frameSize * _channels) return OpusError.BufferTooSmall;
        if ((fec || data.IsEmpty) && frameSize % (Fs / 400) != 0) return OpusError.BadArg;
        if (data.IsEmpty)
        {
            var pcmCount = 0;
            do
            {
                var ret = DecodeFrame(default, false, pcm[(pcmCount * _channels)..], frameSize - pcmCount, false);
                if (ret < 0) return ret;
                pcmCount += ret;
            } while (pcmCount < frameSize);
            _lastPacketDuration = pcmCount;
            return pcmCount;
        }
        var toc = data[0];
        var packetMode = OpusPacket.GetMode(toc);
        var packetBandwidth = OpusPacket.GetBandwidth(toc);
        var packetFrameSize = OpusPacket.GetSamplesPerFrame(toc);
        var packetChannels = OpusPacket.GetChannels(toc);
        Span<int> size = stackalloc int[OpusPacket.MaxFrames];
        var count = OpusPacket.Parse(data, size, out var offset, out _);
        if (count < 0) return count;
        if (!fec && count * packetFrameSize > frameSize) return OpusError.BufferTooSmall;
        var body = data[offset..];
        if (fec)
        {
            if (frameSize < packetFrameSize || packetMode == OpusPacket.ModeCeltOnly || _mode == OpusPacket.ModeCeltOnly)
                return Decode(default, pcm, frameSize);
            var durationCopy = _lastPacketDuration;
            if (frameSize - packetFrameSize != 0)
            {
                var ret = Decode(default, pcm, frameSize - packetFrameSize);
                if (ret < 0) { _lastPacketDuration = durationCopy; return ret; }
            }
            _mode = packetMode; _bandwidth = packetBandwidth; _frameSize = packetFrameSize; _streamChannels = packetChannels;
            var r = DecodeFrame(body[..size[0]], true, pcm[((frameSize - packetFrameSize) * _channels)..], packetFrameSize, true);
            if (r < 0) return r;
            _lastPacketDuration = frameSize;
            return frameSize;
        }
        _mode = packetMode; _bandwidth = packetBandwidth; _frameSize = packetFrameSize; _streamChannels = packetChannels;
        var nbSamples = 0;
        for (var i = 0; i < count; i++)
        {
            var ret = DecodeFrame(body[..size[i]], true, pcm[(nbSamples * _channels)..], frameSize - nbSamples, false);
            if (ret < 0) return ret;
            body = body[size[i]..];
            nbSamples += ret;
        }
        _lastPacketDuration = nbSamples;
        return nbSamples;
    }

    private int DecodeFrame(ReadOnlySpan<byte> data, bool hasData, Span<float> pcm, int frameSize, bool decodeFec)
    {
        var len = data.Length;
        if (frameSize < F25) return OpusError.BufferTooSmall;
        frameSize = Math.Min(frameSize, Fs / 25 * 3);
        if (len <= 1)
        {
            hasData = false;
            frameSize = Math.Min(frameSize, _frameSize);
        }
        int audiosize, mode, bandwidth;
        RangeDecoder dec;
        var frameBytes = Array.Empty<byte>();
        if (hasData)
        {
            audiosize = _frameSize;
            mode = _mode;
            bandwidth = _bandwidth;
            frameBytes = data.ToArray();
            dec = new RangeDecoder(frameBytes, 0, len);
        }
        else
        {
            audiosize = frameSize;
            mode = _prevRedundancy ? OpusPacket.ModeCeltOnly : _prevMode;
            bandwidth = 0;
            dec = _emptyDecoder;
            if (mode == 0)
            {
                pcm[..(audiosize * _channels)].Clear();
                return audiosize;
            }
            if (audiosize > F20)
            {
                do
                {
                    var ret = DecodeFrame(default, false, pcm, Math.Min(audiosize, F20), false);
                    if (ret < 0) return ret;
                    pcm = pcm[(ret * _channels)..];
                    audiosize -= ret;
                } while (audiosize > 0);
                return frameSize;
            }
            if (audiosize < F20)
            {
                if (audiosize > F10) audiosize = F10;
                else if (mode != OpusPacket.ModeSilkOnly && audiosize > F5 && audiosize < F10) audiosize = F5;
            }
        }
        var transition = false;
        if (hasData && _prevMode > 0 && ((mode == OpusPacket.ModeCeltOnly && _prevMode != OpusPacket.ModeCeltOnly && !_prevRedundancy)
            || (mode != OpusPacket.ModeCeltOnly && _prevMode == OpusPacket.ModeCeltOnly)))
        {
            transition = true;
            if (mode == OpusPacket.ModeCeltOnly)
            {
                // conceal 5 ms of the previous mode to cross-fade from it
                var r = DecodeFrame(default, false, _transition, Math.Min(F5, audiosize), false);
                if (r < 0) return r;
            }
        }
        if (audiosize > frameSize) return OpusError.BadArg;
        frameSize = audiosize;

        var silkSamples = Math.Max(F10, frameSize) * _channels;
        var pcmSilk = _pcmSilk;
        if (mode != OpusPacket.ModeCeltOnly)
        {
            if (_prevMode == OpusPacket.ModeCeltOnly) _silk.Reset();
            var ctl = _silkCtl;
            ctl.NChannelsApi = _channels;
            ctl.PayloadSizeMs = Math.Max(10, 1000 * audiosize / Fs);
            if (hasData)
            {
                ctl.NChannelsInternal = _streamChannels;
                if (mode == OpusPacket.ModeSilkOnly)
                    ctl.InternalSampleRate = bandwidth switch { OpusPacket.BandwidthNarrow => 8000, OpusPacket.BandwidthMedium => 12000, _ => 16000 };
                else ctl.InternalSampleRate = 16000;
            }
            var lostFlag = !hasData ? SilkDecoder.FlagPacketLost : decodeFec ? SilkDecoder.FlagDecodeLbrr : SilkDecoder.FlagDecodeNormal;
            var decoded = 0;
            do
            {
                var first = decoded == 0;
                var ok = _silk.Decode(ctl, lostFlag, first, dec, pcmSilk.AsSpan(decoded * _channels), out var silkFrameSize);
                if (!ok)
                {
                    if (lostFlag != 0)
                    {
                        silkFrameSize = frameSize;
                        pcmSilk.AsSpan(0, frameSize * _channels).Clear();
                    }
                    else return OpusError.InternalError;
                }
                decoded += silkFrameSize;
            } while (decoded < frameSize);
        }
        _ = silkSamples;

        var startBand = 0;
        var redundancy = false;
        var celtToSilk = false;
        var redundancyBytes = 0;
        if (!decodeFec && mode != OpusPacket.ModeCeltOnly && hasData && dec.Tell + 17 + (mode == OpusPacket.ModeHybrid ? 20 : 0) <= 8 * len)
        {
            redundancy = mode == OpusPacket.ModeHybrid ? dec.BitLogp(12) != 0 : true;
            if (redundancy)
            {
                celtToSilk = dec.BitLogp(1) != 0;
                redundancyBytes = mode == OpusPacket.ModeHybrid ? (int)dec.Uint(256) + 2 : len - ((dec.Tell + 7) >> 3);
                len -= redundancyBytes;
                if (len * 8 < dec.Tell)
                {
                    len = 0; redundancyBytes = 0; redundancy = false;
                }
                // the SILK/CELT payload ends before the redundancy frame
                dec = TrimStorage(data, dec, redundancyBytes, len);
            }
        }
        if (mode != OpusPacket.ModeCeltOnly) startBand = 17;
        if (redundancy) transition = false;
        if (transition && mode != OpusPacket.ModeCeltOnly)
        {
            var r = DecodeFrame(default, false, _transition, Math.Min(F5, audiosize), false);
            if (r < 0) return r;
        }
        if (bandwidth != 0)
        {
            _celt.End = bandwidth switch
            {
                OpusPacket.BandwidthNarrow => 13,
                OpusPacket.BandwidthMedium or OpusPacket.BandwidthWide => 17,
                OpusPacket.BandwidthSuperWide => 19,
                _ => 21,
            };
        }
        _celt.StreamChannels = _streamChannels;
        var redundantRng = 0u;
        if (redundancy && celtToSilk)
        {
            _celt.Start = 0;
            var rd = new RangeDecoder(frameBytes, len, redundancyBytes);
            _celt.Decode(rd, redundancyBytes, _redundant, 0, F5);
            redundantRng = _celt.FinalRange;
        }
        _celt.Start = startBand;
        var celtRet = 0;
        if (mode != OpusPacket.ModeSilkOnly)
        {
            var celtFrameSize = Math.Min(F20, frameSize);
            if (mode != _prevMode && _prevMode > 0 && !_prevRedundancy) _celt.Reset();
            celtRet = _celt.Decode(hasData && !decodeFec ? dec : null, len, _celtOut, 0, celtFrameSize);
            if (celtRet >= 0) _celtOut.AsSpan(0, celtFrameSize * _channels).CopyTo(pcm);
        }
        else
        {
            pcm[..(frameSize * _channels)].Clear();
            if (_prevMode == OpusPacket.ModeHybrid && !(redundancy && celtToSilk && _prevRedundancy))
            {
                // flush the CELT overlap with 2.5 ms of silence
                _celt.Start = 0;
                var silence = new RangeDecoder([0xFF, 0xFF], 0, 2);
                _celt.Decode(silence, 2, _celtOut, 0, F25);
                for (var i = 0; i < F25 * _channels; i++) pcm[i] += _celtOut[i];
            }
        }
        if (mode != OpusPacket.ModeCeltOnly)
            for (var i = 0; i < frameSize * _channels; i++) pcm[i] += (1f / 32768f) * pcmSilk[i];

        if (redundancy && !celtToSilk)
        {
            _celt.Reset();
            _celt.Start = 0;
            var rd = new RangeDecoder(frameBytes, len, redundancyBytes);
            _celt.Decode(rd, redundancyBytes, _redundant, 0, F5);
            redundantRng = _celt.FinalRange;
            SmoothFade(pcm[(_channels * (frameSize - F25))..], _redundant.AsSpan(_channels * F25), pcm[(_channels * (frameSize - F25))..], F25, _channels);
        }
        if (redundancy && celtToSilk && (_prevMode != OpusPacket.ModeSilkOnly || _prevRedundancy))
        {
            for (var c = 0; c < _channels; c++)
                for (var i = 0; i < F25; i++) pcm[_channels * i + c] = _redundant[_channels * i + c];
            SmoothFade(_redundant.AsSpan(_channels * F25), pcm[(_channels * F25)..], pcm[(_channels * F25)..], F25, _channels);
        }
        if (transition)
        {
            if (audiosize >= F5)
            {
                for (var i = 0; i < _channels * F25; i++) pcm[i] = _transition[i];
                SmoothFade(_transition.AsSpan(_channels * F25), pcm[(_channels * F25)..], pcm[(_channels * F25)..], F25, _channels);
            }
            else SmoothFade(_transition, pcm, pcm, F25, _channels);
        }
        FinalRange = len <= 1 ? 0 : dec.Rng ^ redundantRng;
        _prevMode = mode;
        _prevRedundancy = redundancy && !celtToSilk;
        return celtRet < 0 ? celtRet : audiosize;
    }

    // CELT decodes into this scratch first so that the SILK contribution can be added without aliasing the output span.
    private readonly float[] _celtOut = new float[F20 * 2];

    /// <summary>Returns a range decoder over the first bytes of the frame, excluding the redundancy frame, continuing from the state of <paramref name="dec"/>.</summary>
    private static RangeDecoder TrimStorage(ReadOnlySpan<byte> data, RangeDecoder dec, int redundancyBytes, int len)
    {
        dec.ReduceStorage(redundancyBytes);
        _ = data; _ = len;
        return dec;
    }
}
