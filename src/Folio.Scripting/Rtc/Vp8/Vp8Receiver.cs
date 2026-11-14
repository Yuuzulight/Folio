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

/// <summary>What a receiver wants the engine to do after it handled packets or time passed.</summary>
internal sealed class Vp8ReceiverOutput
{
    public readonly List<MediaCaptureVideoFrame> Frames = [];
    public readonly List<ushort> Nack = [];
    public bool KeyFrameRequest;
}

/// <summary>
/// Reassembles VP8 frames from RTP packets (RFC 7741), decodes them and asks for repair: a generic NACK for a hole
/// (RFC 4585), then a keyframe request (PLI or FIR) when the hole is not filled in time. Everything is bounded: buffered
/// packets and bytes, the assembled frame size, the NACK window and the number of sequence numbers tracked.
/// It never blocks and never reads the clock itself; every call passes the current timestamp.
/// </summary>
internal sealed class Vp8Receiver : IDisposable
{
    internal const int MaxBufferedPackets = 512;
    internal const int MaxBufferedBytes = 4 * 1024 * 1024;
    internal const int MaxFrameBytes = 2 * 1024 * 1024;
    /// <summary>The furthest ahead of the next expected number a packet may be before the stream is treated as restarted.</summary>
    internal const int MaxSequenceJump = 1024;
    internal const int MaxNackWindow = 256;
    internal const int MaxNackTracked = 128;
    internal const int MaxNackRetries = 4;
    /// <summary>A hole must be this old before it is asked for, so plain reordering does not cost a retransmission.</summary>
    internal static readonly TimeSpan NackDelay = TimeSpan.FromMilliseconds(20);
    internal static readonly TimeSpan NackRetry = TimeSpan.FromMilliseconds(100);
    /// <summary>A hole older than this is given up on: the frame is dropped and a key frame requested.</summary>
    internal static readonly TimeSpan LossTimeout = TimeSpan.FromMilliseconds(400);
    internal static readonly TimeSpan KeyFrameRequestInterval = TimeSpan.FromMilliseconds(500);
    /// <summary>How long a stream may take to produce its first key frame before one is requested.</summary>
    internal static readonly TimeSpan InitialKeyFrameWait = TimeSpan.FromMilliseconds(100);

    private sealed class Packet
    {
        public uint Timestamp;
        public bool Marker, Start;
        public int PartitionId;
        public byte[] Data = [];
    }

    private sealed class NackState { public long LastSent; public int Count; }

    private readonly Vp8Decoder _decoder;
    private readonly TimeProvider _time;
    private readonly SortedDictionary<long, Packet> _buffer = [];
    private readonly Dictionary<long, NackState> _nacks = [];
    private int _bufferedBytes;
    private long _next = -1;
    private long _holeSince;
    private bool _hole;
    private bool _needKey = true;
    private long _keyRequestAfter;
    private long _lastKeyRequest;
    private bool _keyRequestEver;
    private long _frameSequence;
    private long _highest = -1;
    private long _scanHint;
    private long _lastNackScan;
    private bool _pendingShown;
    private double _pixelTokens;
    private long _pixelStamp;
    private bool _pixelStarted;
    private readonly double _pixelsPerSecond;
    /// <summary>The most frames one call decodes; the rest wait for the next packet or tick, so the engine's lock is never held for long.</summary>
    internal const int MaxFramesPerCall = 8;
    /// <summary>The decode work budget in pictures per second at the decoder's largest size.</summary>
    internal const int DecodeFramesPerSecond = 40;
    private bool _started;

    internal bool SupportsNack { get; set; } = true;
    internal bool SupportsKeyFrameRequest { get; set; } = true;

    internal long PacketsLost { get; private set; }
    internal long PacketsDiscarded { get; private set; }
    internal long FramesDecoded { get; private set; }
    internal long KeyFramesDecoded { get; private set; }
    internal long FramesDiscarded { get; private set; }
    internal long DecodeErrors { get; private set; }
    internal long NacksRequested { get; private set; }
    internal long KeyFrameRequests { get; private set; }
    internal bool WaitingForKeyFrame => _needKey;
    internal int BufferedPackets => _buffer.Count;

    /// <param name="maxPixelsPerSecond">Pixels the stream may reconstruct per second (default: 40 pictures of the decoder's cap); more is dropped.</param>
    internal Vp8Receiver(Vp8Decoder decoder, TimeProvider time, double? maxPixelsPerSecond = null)
    {
        _decoder = decoder;
        _time = time;
        _pixelsPerSecond = maxPixelsPerSecond ?? (double)Vp8Decoder.DefaultMaxPixels * DecodeFramesPerSecond;
        _pixelTokens = _pixelsPerSecond;
    }

    internal long FramesRateLimited { get; private set; }

    public void Dispose() => _decoder.Dispose();

    /// <summary>Takes one RTP packet's payload. <paramref name="extendedSequence"/> is the 32 bit extended sequence number the engine tracks.</summary>
    internal void Push(long extendedSequence, uint timestamp, bool marker, ReadOnlySpan<byte> payload, long now, Vp8ReceiverOutput output)
    {
        if (!Vp8PayloadDescriptor.TryParse(payload, out var descriptor)) { PacketsDiscarded++; return; }
        if (!_started)
        {
            _started = true;
            _next = extendedSequence;
            _keyRequestAfter = now + (long)(InitialKeyFrameWait.TotalSeconds * _time.TimestampFrequency);
        }
        if (extendedSequence < _next || _buffer.ContainsKey(extendedSequence)) { PacketsDiscarded++; return; } // late or duplicate
        if (extendedSequence - _next > MaxSequenceJump)
        {
            // The sender restarted or we missed far too much: nothing buffered can be completed any more.
            ClearBuffer();
            _next = extendedSequence;
            NeedKeyFrame(now, immediately: true);
        }
        if (_buffer.Count >= MaxBufferedPackets || _bufferedBytes + payload.Length > MaxBufferedBytes)
        {
            // Bounded: give up on what is waiting, resume from this packet, and ask for a fresh key frame.
            PacketsLost += Math.Max(0, extendedSequence - _next);
            ClearBuffer();
            _next = extendedSequence;
            NeedKeyFrame(now, immediately: true);
        }
        var data = payload[descriptor.PayloadOffset..].ToArray();
        _nacks.Remove(extendedSequence); // it arrived after all
        if (extendedSequence > _highest) _highest = extendedSequence;
        _buffer[extendedSequence] = new Packet { Timestamp = timestamp, Marker = marker, Start = descriptor.Start, PartitionId = descriptor.PartitionId, Data = data };
        _bufferedBytes += data.Length;
        Assemble(output);
        Maintain(now, output);
    }

    /// <summary>Drives the loss timers; called from the engine's maintenance tick.</summary>
    internal void Tick(long now, Vp8ReceiverOutput output)
    {
        if (!_started) return;
        Assemble(output);
        Maintain(now, output);
    }

    private void ClearBuffer()
    {
        _buffer.Clear();
        _bufferedBytes = 0;
        _highest = -1;
        _scanHint = 0;
        _nacks.Clear();
        _hole = false;
    }

    private void NeedKeyFrame(long now, bool immediately)
    {
        if (!_needKey) { _needKey = true; _keyRequestAfter = immediately ? now : now + (long)(InitialKeyFrameWait.TotalSeconds * _time.TimestampFrequency); }
        else if (immediately && _keyRequestAfter > now) _keyRequestAfter = now;
    }

    private void Remove(long sequence)
    {
        if (_buffer.Remove(sequence, out var packet)) _bufferedBytes -= packet.Data.Length;
        if (_buffer.Count == 0) _highest = -1;
    }

    private void Assemble(Vp8ReceiverOutput output)
    {
        var budget = MaxFramesPerCall;
        while (budget > 0 && _buffer.TryGetValue(_next, out var first))
        {
            var sequence = _next;
            var bytes = 0;
            var end = -1L;
            var oversized = false;
            Packet? packet;
            while (_buffer.TryGetValue(sequence, out packet) && packet.Timestamp == first.Timestamp)
            {
                bytes += packet.Data.Length;
                if (bytes > MaxFrameBytes) { oversized = true; break; }
                if (packet.Marker) { end = sequence; break; }
                sequence++;
            }
            if (oversized || (end < 0 && _buffer.TryGetValue(sequence, out var other) && other.Timestamp != first.Timestamp))
            {
                // Too big, or the marker packet never arrived and the next frame started: this frame cannot be completed.
                DropRange(_next, oversized ? FindFrameEnd(_next) : sequence - 1);
                _needKey = true; _keyRequestAfter = 0;
                continue;
            }
            if (end < 0) return; // the rest of this frame is still in flight
            if (!first.Start || first.PartitionId != 0)
            {
                // A frame must begin with the start of partition 0 (RFC 7741 section 4.4).
                DropRange(_next, end);
                _needKey = true; _keyRequestAfter = 0;
                continue;
            }
            var frame = new byte[bytes];
            var offset = 0;
            for (var s = _next; s <= end; s++)
            {
                var part = _buffer[s].Data;
                part.CopyTo(frame, offset);
                offset += part.Length;
            }
            for (var s = _next; s <= end; s++) Remove(s);
            _nacks.Remove(_next);
            _next = end + 1;
            _hole = false;
            budget--;
            DeliverFrame(frame, output);
        }
        FlushPending(output);
    }

    /// <summary>Converts the newest decoded shown frame; earlier ones in the same call were superseded and never converted.</summary>
    private void FlushPending(Vp8ReceiverOutput output)
    {
        if (!_pendingShown) return;
        _pendingShown = false;
        if (_decoder.ToRgba() is not { } rgba) return;
        output.Frames.Add(new MediaCaptureVideoFrame(_decoder.Width, _decoder.Height, rgba) { CaptureSequence = ++_frameSequence });
    }

    private bool TryChargePixels(long pixels, long now)
    {
        var frequency = _time.TimestampFrequency;
        if (!_pixelStarted) { _pixelStarted = true; _pixelStamp = now; }
        else
        {
            _pixelTokens = Math.Min(_pixelsPerSecond, _pixelTokens + Math.Max(0, now - _pixelStamp) * _pixelsPerSecond / frequency);
            _pixelStamp = now;
        }
        if (_pixelTokens < pixels) return false;
        _pixelTokens -= pixels;
        return true;
    }

    private long FindFrameEnd(long from)
    {
        // Used only for an oversized frame: drop through its last buffered packet.
        var sequence = from;
        var timestamp = _buffer[from].Timestamp;
        while (_buffer.TryGetValue(sequence + 1, out var next) && next.Timestamp == timestamp) sequence++;
        return sequence;
    }

    private void DropRange(long from, long to)
    {
        for (var s = from; s <= to; s++) { Remove(s); _nacks.Remove(s); }
        PacketsDiscarded += to - from + 1;
        _next = to + 1;
        _hole = false;
        FramesDiscarded++;
    }

    private void DeliverFrame(byte[] frame, Vp8ReceiverOutput output)
    {
        var key = Vp8Decoder.IsKeyFrame(frame);
        if (_needKey && !key) { FramesDiscarded++; return; } // an inter frame is useless until a key frame has arrived
        // Cost is the picture the decoder would reconstruct, whatever the frame's byte size: a 10 byte key frame at 1080p is not cheap.
        long pixels;
        if (key) pixels = Vp8Decoder.TryReadKeyFrameSize(frame, out var kw, out var kh) ? (long)kw * kh : 0;
        else pixels = (long)_decoder.Width * _decoder.Height;
        if (!TryChargePixels(pixels, _time.GetTimestamp()))
        {
            FramesRateLimited++; FramesDiscarded++;
            if (!key) { _needKey = true; _keyRequestAfter = 0; } // the dropped inter frame breaks the reference chain
            return;
        }
        var shown = (frame[0] & 0x10) != 0;
        if (_pendingShown && !shown) FlushPending(output); // the pending picture would be replaced by one that is not shown
        _pendingShown = false;
        if (_decoder.Decode(frame) != Vp8Status.Ok)
        {
            DecodeErrors++;
            _needKey = true; _keyRequestAfter = 0;
            return;
        }
        if (key) { _needKey = false; KeyFramesDecoded++; }
        FramesDecoded++;
        if (_decoder.FrameShown) _pendingShown = true;
    }

    private void Maintain(long now, Vp8ReceiverOutput output)
    {
        var frequency = _time.TimestampFrequency;
        // A hole: some sequence number at or after the next expected one is absent while a later one is buffered.
        // With no gap the buffer holds exactly (highest - next + 1) packets, which is checked without walking it.
        if (_buffer.Count > 0 && _buffer.Count != _highest - _next + 1)
        {
            // The scan resumes where the last one stopped: packets below the hint cannot disappear except by _next moving past them.
            var firstMissing = Math.Max(_next, _scanHint);
            while (_buffer.ContainsKey(firstMissing)) firstMissing++;
            _scanHint = firstMissing;
            var highest = _highest;
            if (!_hole) { _hole = true; _holeSince = now; }
            var age = _time.GetElapsedTime(_holeSince, now);
            if (highest - _next > MaxNackWindow || age >= LossTimeout)
            {
                GiveUpOnHole(firstMissing, now);
            }
            else if (SupportsNack && age >= NackDelay && (_lastNackScan == 0 || _time.GetElapsedTime(_lastNackScan, now) >= NackScanInterval))
            {
                _lastNackScan = now;
                if (_nacks.Count >= MaxNackTracked) foreach (var stale in _nacks.Keys.Where(k => k < _next).ToArray()) _nacks.Remove(stale);
                // At most MaxNackItems numbers per request: every one of them fits in the packet, so retry counts stay honest.
                for (var s = firstMissing; s < highest && output.Nack.Count < RtcpCodec.MaxNackItems; s++)
                {
                    if (_buffer.ContainsKey(s)) continue;
                    if (!_nacks.TryGetValue(s, out var state))
                    {
                        if (_nacks.Count >= MaxNackTracked) continue;
                        _nacks[s] = state = new NackState { LastSent = now - 10 * frequency };
                    }
                    if (state.Count >= MaxNackRetries || _time.GetElapsedTime(state.LastSent, now) < NackRetry) continue;
                    state.Count++; state.LastSent = now;
                    output.Nack.Add((ushort)s);
                    NacksRequested++;
                }
            }
        }
        else { _hole = false; _scanHint = 0; }

        // The interval is measured from the last request whether or not a key frame decoded in between.
        if (_needKey && SupportsKeyFrameRequest && now >= _keyRequestAfter &&
            (!_keyRequestEver || _time.GetElapsedTime(_lastKeyRequest, now) >= KeyFrameRequestInterval))
        {
            _keyRequestEver = true; _lastKeyRequest = now;
            output.KeyFrameRequest = true;
            KeyFrameRequests++;
        }
    }

    internal static readonly TimeSpan NackScanInterval = TimeSpan.FromMilliseconds(5);

    private void GiveUpOnHole(long firstMissing, long now)
    {
        // Count the missing packets, drop everything up to the next frame start that follows the hole, and wait for a key frame.
        var resume = -1L;
        foreach (var (sequence, packet) in _buffer)
            if (sequence > firstMissing && packet.Start && packet.PartitionId == 0) { resume = sequence; break; }
        var limit = resume >= 0 ? resume : _highest + 1;
        for (var s = _next; s < limit; s++)
        {
            if (!_buffer.ContainsKey(s)) PacketsLost++;
            Remove(s);
            _nacks.Remove(s);
        }
        _next = limit;
        _hole = false;
        _scanHint = 0;
        FramesDiscarded++;
        _needKey = true;
        _keyRequestAfter = now;
    }
}
