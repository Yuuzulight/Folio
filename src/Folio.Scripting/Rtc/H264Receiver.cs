using Folio;

namespace Folio.Scripting.Rtc;

/// <summary>
/// Receives H.264 over RTP (RFC 6184): packets are put back in order by the shared <see cref="RtpVideoReceiver"/> reorder
/// buffer (with its NACK and keyframe-request repair), fed in sequence to the <see cref="H264Depacketizer"/>, and every
/// complete access unit is decoded. A picture that lost a packet is dropped whole by the depacketizer; the receiver then
/// waits for an IDR picture (asking for one with PLI or FIR) instead of showing a picture predicted from a missing
/// reference. Only the newest decoded picture of a call is converted to RGBA and delivered.
/// </summary>
internal sealed class H264Receiver : RtpVideoReceiver
{
    private readonly H264Decoder _decoder;
    private readonly H264Depacketizer _depacketizer = new(MaxFrameBytes);
    private readonly List<byte[]> _units = [];
    private readonly long _maxPixels;
    private long _lastPixels;
    private long _droppedUnitsSeen, _droppedPacketsSeen;
    private DecodedVideoFrame? _pending;
    private long _frameSequence;
    private uint _lastTimestamp;
    private bool _haveFed;

    /// <param name="maxPixels">The largest picture the decoder accepts; a picture whose size is not known yet is charged at this size.</param>
    /// <param name="maxPixelsPerSecond">Pixels the stream may reconstruct per second (default: 40 pictures of <paramref name="maxPixels"/>); more is dropped.</param>
    internal H264Receiver(H264Decoder decoder, TimeProvider time, int maxPixels, double? maxPixelsPerSecond = null)
        : base(time, maxPixelsPerSecond ?? (double)maxPixels * DecodeFramesPerSecond)
    {
        _decoder = decoder;
        _maxPixels = maxPixels;
    }

    /// <summary>Decoder calls cut short by a per-call budget (debug statistics only).</summary>
    internal int BudgetStops => _decoder.BudgetStops;

    /// <summary>Times the decoder's catch-all fired: a decoder bug, never bad input (debug statistics only).</summary>
    internal int UnexpectedErrors => _decoder.UnexpectedErrors;

    public override void Dispose() => _decoder.Dispose();

    /// <summary>Takes one RTP packet's payload (the bytes after the RTP header). <paramref name="extendedSequence"/> is the extended sequence number the engine tracks.</summary>
    internal void Push(long extendedSequence, uint timestamp, bool marker, ReadOnlySpan<byte> payload, long now, VideoReceiverOutput output)
    {
        // A first packet is never "a frame start" for resume purposes: H264Receiver finds those by timestamp.
        if (!Admit(extendedSequence, timestamp, marker, false, payload, 0, now)) return;
        Assemble(output);
        Maintain(now, output);
    }

    protected override long FindResumePoint(long firstMissing)
    {
        // The first buffered packet that belongs to a later picture than the last one fed; the rest of the damaged picture is skipped.
        foreach (var (sequence, packet) in Buffer)
            if (sequence > firstMissing && (!_haveFed || packet.Timestamp != _lastTimestamp)) return sequence;
        return -1;
    }

    protected override void Assemble(VideoReceiverOutput output)
    {
        var budget = MaxFramesPerCall;
        while (budget > 0 && Buffer.TryGetValue(Next, out var packet))
        {
            var sequence = Next;
            Remove(sequence);
            Consumed(sequence);
            Next = sequence + 1;
            HoleClosed();
            _lastTimestamp = packet.Timestamp; _haveFed = true;
            _units.Clear();
            _depacketizer.Push(packet.Data, (ushort)sequence, packet.Timestamp, packet.Marker, _units);
            var droppedUnits = _depacketizer.DroppedAccessUnits - _droppedUnitsSeen;
            if (droppedUnits > 0)
            {
                // The depacketizer dropped a picture that lost data: nothing after it can be trusted until an IDR picture.
                _droppedUnitsSeen = _depacketizer.DroppedAccessUnits;
                FramesDiscarded += droppedUnits;
                NeedKey = true; KeyRequestAfter = 0;
            }
            PacketsDiscarded += _depacketizer.DroppedPackets - _droppedPacketsSeen;
            _droppedPacketsSeen = _depacketizer.DroppedPackets;
            foreach (var unit in _units) { budget--; DeliverUnit(unit); }
        }
        FlushPending(output);
    }

    private void FlushPending(VideoReceiverOutput output)
    {
        if (_pending is not { } frame) return;
        _pending = null;
        output.Frames.Add(new MediaCaptureVideoFrame(frame.Width, frame.Height, ToRgba(frame)) { CaptureSequence = ++_frameSequence });
    }

    private void DeliverUnit(byte[] unit)
    {
        Classify(unit, out var key, out var slices, out var parameterSets);
        // A picture predicted from a missing reference is worse than none. Parameter sets sent in an access unit of their own
        // (an SPS and PPS before the IDR picture) still go to the decoder: they cost no picture work.
        if (NeedKey && !key && !(parameterSets && !slices)) { FramesDiscarded++; return; }
        // The cost is the picture the decoder reconstructs, whatever the unit's byte size; the size is known once a picture decoded.
        var estimate = slices ? (_lastPixels > 0 ? _lastPixels : _maxPixels) : 0;
        if (slices && !TryChargePixels(estimate, Time.GetTimestamp()))
        {
            FramesRateLimited++; FramesDiscarded++;
            NeedKey = true; KeyRequestAfter = 0; // the dropped picture breaks the reference chain
            return;
        }
        var dropped = _decoder.NalUnitsDropped; var stops = _decoder.BudgetStops; var unexpected = _decoder.UnexpectedErrors; var concealed = _decoder.PicturesConcealed;
        var frames = _decoder.Decode(unit);
        if (_decoder.NalUnitsDropped != dropped || _decoder.BudgetStops != stops || _decoder.UnexpectedErrors != unexpected || _decoder.PicturesConcealed != concealed)
        {
            // A concealed picture is one where slices were missing without a sequence gap to show it, for example a stream joined in
            // the middle of an IDR picture: it is never shown, and the next complete IDR picture starts the stream.
            // Unsupported tools, a corrupt NAL unit or a budget stop: the references may be damaged, so start over from an IDR picture.
            DecodeErrors++;
            NeedKey = true; KeyRequestAfter = 0;
            return;
        }
        if (frames.Count == 0) return;
        var last = frames[^1];
        _lastPixels = (long)last.Width * last.Height;
        ChargePixelDebt((long)frames.Count * _lastPixels - estimate); // a larger picture than the estimate is paid for afterwards
        FramesDecoded += frames.Count;
        if (key) { NeedKey = false; KeyFramesDecoded++; }
        _pending = last; // earlier pictures of this call were superseded and are never converted
    }

    /// <summary>What the access unit holds: an IDR slice (NAL type 5), any slice (1 or 5), an SPS or PPS (7 or 8). Units from the depacketizer use 4-byte start codes.</summary>
    internal static void Classify(ReadOnlySpan<byte> unit, out bool idr, out bool slices, out bool parameterSets)
    {
        idr = slices = parameterSets = false;
        for (var i = 0; i + 3 < unit.Length; i++)
        {
            if (unit[i + 2] > 1) { i += 2; continue; }
            if (unit[i] != 0 || unit[i + 1] != 0 || unit[i + 2] != 1) continue;
            switch (unit[i + 3] & 0x1F)
            {
                case 5: idr = slices = true; break;
                case 1: slices = true; break;
                case 7 or 8: parameterSets = true; break;
            }
        }
    }

    internal static bool ContainsIdr(ReadOnlySpan<byte> unit) { Classify(unit, out var idr, out _, out _); return idr; }

    /// <summary>BT.601 limited range, the same conversion the VP8 path uses.</summary>
    internal static byte[] ToRgba(DecodedVideoFrame frame)
    {
        var w = frame.Width; var h = frame.Height;
        var output = GC.AllocateUninitializedArray<byte>(checked(w * h * 4));
        var cw = frame.ChromaWidth;
        for (var y = 0; y < h; y++)
        {
            var o = y * w * 4;
            var uv = (y >> 1) * cw;
            for (var x = 0; x < w; x++)
            {
                var c = 298 * (frame.Y[y * w + x] - 16);
                var d = frame.U[uv + (x >> 1)] - 128;
                var e = frame.V[uv + (x >> 1)] - 128;
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
        return output;
    }
}
