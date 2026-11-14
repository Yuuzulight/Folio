using Folio;

namespace Folio.Scripting.Rtc.Vp8;

/// <summary>The VP8 RTP payload descriptor (RFC 7741 section 4.2).</summary>
internal readonly record struct Vp8PayloadDescriptor(bool NonReference, bool Start, int PartitionId, int PayloadOffset)
{
    /// <summary>Parses the descriptor. False for a truncated one or one with no VP8 payload behind it.</summary>
    internal static bool TryParse(ReadOnlySpan<byte> payload, out Vp8PayloadDescriptor descriptor)
    {
        descriptor = default;
        if (payload.Length < 2) return false;
        var first = payload[0];
        var offset = 1;
        if ((first & 0x80) != 0) // X: extension byte follows
        {
            var extension = payload[offset++];
            if ((extension & 0x80) != 0) // I: PictureID, one byte or (M bit) two
            {
                if (offset >= payload.Length) return false;
                offset += (payload[offset] & 0x80) != 0 ? 2 : 1;
            }
            if ((extension & 0x40) != 0) offset++; // L: TL0PICIDX
            if ((extension & 0x30) != 0) offset++; // T or K: TID/Y/KEYIDX
        }
        if (offset >= payload.Length) return false;
        descriptor = new Vp8PayloadDescriptor((first & 0x20) != 0, (first & 0x10) != 0, first & 0x07, offset);
        return true;
    }
}

/// <summary>
/// Reassembles VP8 frames from RTP packets (RFC 7741) and decodes them; reordering, NACK, keyframe requests and the decode
/// work budget are the shared <see cref="RtpVideoReceiver"/>.
/// </summary>
internal sealed class Vp8Receiver : RtpVideoReceiver
{
    private readonly Vp8Decoder _decoder;
    private long _frameSequence;
    private bool _pendingShown;

    /// <param name="maxPixelsPerSecond">Pixels the stream may reconstruct per second (default: 40 pictures of the decoder's cap); more is dropped.</param>
    internal Vp8Receiver(Vp8Decoder decoder, TimeProvider time, double? maxPixelsPerSecond = null)
        : base(time, maxPixelsPerSecond ?? (double)Vp8Decoder.DefaultMaxPixels * DecodeFramesPerSecond)
    {
        _decoder = decoder;
    }

    public override void Dispose() => _decoder.Dispose();

    /// <summary>Takes one RTP packet's payload. <paramref name="extendedSequence"/> is the 32 bit extended sequence number the engine tracks.</summary>
    internal void Push(long extendedSequence, uint timestamp, bool marker, ReadOnlySpan<byte> payload, long now, VideoReceiverOutput output)
    {
        if (!Vp8PayloadDescriptor.TryParse(payload, out var descriptor)) { PacketsDiscarded++; return; }
        if (!Admit(extendedSequence, timestamp, marker, descriptor.Start && descriptor.PartitionId == 0, payload, descriptor.PayloadOffset, now)) return;
        Assemble(output);
        Maintain(now, output);
    }

    protected override void Assemble(VideoReceiverOutput output)
    {
        var budget = MaxFramesPerCall;
        while (budget > 0 && Buffer.TryGetValue(Next, out var first))
        {
            var sequence = Next;
            var bytes = 0;
            var end = -1L;
            var oversized = false;
            Packet? packet;
            while (Buffer.TryGetValue(sequence, out packet) && packet.Timestamp == first.Timestamp)
            {
                bytes += packet.Data.Length;
                if (bytes > MaxFrameBytes) { oversized = true; break; }
                if (packet.Marker) { end = sequence; break; }
                sequence++;
            }
            if (oversized || (end < 0 && Buffer.TryGetValue(sequence, out var other) && other.Timestamp != first.Timestamp))
            {
                // Too big, or the marker packet never arrived and the next frame started: this frame cannot be completed.
                DropRange(Next, oversized ? FindFrameEnd(Next) : sequence - 1);
                NeedKey = true; KeyRequestAfter = 0;
                continue;
            }
            if (end < 0) return; // the rest of this frame is still in flight
            if (!first.FrameStart)
            {
                // A frame must begin with the start of partition 0 (RFC 7741 section 4.4).
                DropRange(Next, end);
                NeedKey = true; KeyRequestAfter = 0;
                continue;
            }
            var frame = new byte[bytes];
            var offset = 0;
            for (var s = Next; s <= end; s++)
            {
                var part = Buffer[s].Data;
                part.CopyTo(frame, offset);
                offset += part.Length;
            }
            for (var s = Next; s <= end; s++) Remove(s);
            Consumed(Next);
            Next = end + 1;
            HoleClosed();
            budget--;
            DeliverFrame(frame, output);
        }
        FlushPending(output);
    }

    /// <summary>Converts the newest decoded shown frame; earlier ones in the same call were superseded and never converted.</summary>
    private void FlushPending(VideoReceiverOutput output)
    {
        if (!_pendingShown) return;
        _pendingShown = false;
        if (_decoder.ToRgba() is not { } rgba) return;
        output.Frames.Add(new MediaCaptureVideoFrame(_decoder.Width, _decoder.Height, rgba) { CaptureSequence = ++_frameSequence });
    }

    private long FindFrameEnd(long from)
    {
        // Used only for an oversized frame: drop through its last buffered packet.
        var sequence = from;
        var timestamp = Buffer[from].Timestamp;
        while (Buffer.TryGetValue(sequence + 1, out var next) && next.Timestamp == timestamp) sequence++;
        return sequence;
    }

    private void DeliverFrame(byte[] frame, VideoReceiverOutput output)
    {
        var key = Vp8Decoder.IsKeyFrame(frame);
        if (NeedKey && !key) { FramesDiscarded++; return; } // an inter frame is useless until a key frame has arrived
        // Cost is the picture the decoder would reconstruct, whatever the frame's byte size: a 10 byte key frame at 1080p is not cheap.
        long pixels;
        if (key) pixels = Vp8Decoder.TryReadKeyFrameSize(frame, out var kw, out var kh) ? (long)kw * kh : 0;
        else pixels = (long)_decoder.Width * _decoder.Height;
        if (!TryChargePixels(pixels, Time.GetTimestamp()))
        {
            FramesRateLimited++; FramesDiscarded++;
            if (!key) { NeedKey = true; KeyRequestAfter = 0; } // the dropped inter frame breaks the reference chain
            return;
        }
        var shown = (frame[0] & 0x10) != 0;
        if (_pendingShown && !shown) FlushPending(output); // the pending picture would be replaced by one that is not shown
        _pendingShown = false;
        if (_decoder.Decode(frame) != Vp8Status.Ok)
        {
            DecodeErrors++;
            NeedKey = true; KeyRequestAfter = 0;
            return;
        }
        if (key) { NeedKey = false; KeyFramesDecoded++; }
        FramesDecoded++;
        if (_decoder.FrameShown) _pendingShown = true;
    }
}
