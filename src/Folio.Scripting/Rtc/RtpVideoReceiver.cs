using Folio;

namespace Folio.Scripting.Rtc;

/// <summary>What a receiver wants the engine to do after it handled packets or time passed.</summary>
internal class VideoReceiverOutput
{
    public readonly List<MediaCaptureVideoFrame> Frames = [];
    public readonly List<ushort> Nack = [];
    public bool KeyFrameRequest;
}

/// <summary>
/// The codec independent half of a remote video receiver: a bounded reorder buffer keyed by extended sequence number, hole
/// detection, a generic NACK for a hole (RFC 4585) and then a keyframe request (PLI or FIR) when the hole is not filled in
/// time, a decode work budget in pixels per second, and the counters the engine publishes. A codec subclass turns the
/// in-order packets into pictures in <see cref="Assemble"/>. Everything is bounded: buffered packets and bytes, the NACK
/// window and the number of sequence numbers tracked. It never blocks and never reads the clock itself; every call passes
/// the current timestamp.
/// </summary>
internal abstract class RtpVideoReceiver : IDisposable
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
    internal static readonly TimeSpan NackScanInterval = TimeSpan.FromMilliseconds(5);
    /// <summary>The most frames one call decodes; the rest wait for the next packet or tick, so the engine's lock is never held for long.</summary>
    internal const int MaxFramesPerCall = 8;
    /// <summary>The decode work budget in pictures per second at the decoder's largest size.</summary>
    internal const int DecodeFramesPerSecond = 40;

    /// <summary>One buffered RTP payload. <see cref="FrameStart"/> marks a packet that begins a picture.</summary>
    protected sealed class Packet
    {
        public uint Timestamp;
        public bool Marker, FrameStart;
        public byte[] Data = [];
    }

    private sealed class NackState { public long LastSent; public int Count; }

    protected readonly TimeProvider Time;
    protected readonly SortedDictionary<long, Packet> Buffer = [];
    private readonly Dictionary<long, NackState> _nacks = [];
    private int _bufferedBytes;
    protected long Next = -1;
    private long _holeSince;
    private bool _hole;
    protected bool NeedKey = true;
    protected long KeyRequestAfter;
    private long _lastKeyRequest;
    private bool _keyRequestEver;
    private long _highest = -1;
    private long _scanHint;
    private long _lastNackScan;
    private double _pixelTokens;
    private long _pixelStamp;
    private bool _pixelStarted;
    private readonly double _pixelsPerSecond;
    private bool _started;

    internal bool SupportsNack { get; set; } = true;
    internal bool SupportsKeyFrameRequest { get; set; } = true;

    internal long PacketsLost { get; private protected set; }
    internal long PacketsDiscarded { get; private protected set; }
    internal long FramesDecoded { get; private protected set; }
    internal long KeyFramesDecoded { get; private protected set; }
    internal long FramesDiscarded { get; private protected set; }
    internal long DecodeErrors { get; private protected set; }
    internal long NacksRequested { get; private set; }
    internal long KeyFrameRequests { get; private set; }
    internal long FramesRateLimited { get; private protected set; }
    internal bool WaitingForKeyFrame => NeedKey;
    internal int BufferedPackets => Buffer.Count;

    /// <param name="pixelsPerSecond">Pixels the stream may reconstruct per second; more is dropped.</param>
    protected RtpVideoReceiver(TimeProvider time, double pixelsPerSecond)
    {
        Time = time;
        _pixelsPerSecond = pixelsPerSecond;
        _pixelTokens = pixelsPerSecond;
    }

    public abstract void Dispose();

    /// <summary>Takes in as many whole pictures as the buffer allows, at most <see cref="MaxFramesPerCall"/>, and appends decoded frames to <paramref name="output"/>.</summary>
    protected abstract void Assemble(VideoReceiverOutput output);

    /// <summary>Drives the loss timers; called from the engine's maintenance tick.</summary>
    internal void Tick(long now, VideoReceiverOutput output)
    {
        if (!_started) return;
        Assemble(output);
        Maintain(now, output);
    }

    /// <summary>
    /// Admits one packet to the reorder buffer: late and duplicate packets are discarded, a stream restart or an overflow
    /// clears what is waiting. The bytes from <paramref name="offset"/> are copied; the whole payload length counts against the byte limit.
    /// </summary>
    protected bool Admit(long extendedSequence, uint timestamp, bool marker, bool frameStart, ReadOnlySpan<byte> payload, int offset, long now)
    {
        if (!_started)
        {
            _started = true;
            Next = extendedSequence;
            KeyRequestAfter = now + (long)(InitialKeyFrameWait.TotalSeconds * Time.TimestampFrequency);
        }
        if (extendedSequence < Next || Buffer.ContainsKey(extendedSequence)) { PacketsDiscarded++; return false; } // late or duplicate
        if (extendedSequence - Next > MaxSequenceJump)
        {
            // The sender restarted or we missed far too much: nothing buffered can be completed any more.
            ClearBuffer();
            Next = extendedSequence;
            NeedKeyFrame(now, immediately: true);
        }
        if (Buffer.Count >= MaxBufferedPackets || _bufferedBytes + payload.Length > MaxBufferedBytes)
        {
            // Bounded: give up on what is waiting, resume from this packet, and ask for a fresh key frame.
            PacketsLost += Math.Max(0, extendedSequence - Next);
            ClearBuffer();
            Next = extendedSequence;
            NeedKeyFrame(now, immediately: true);
        }
        var data = payload[offset..].ToArray();
        _nacks.Remove(extendedSequence); // it arrived after all
        if (extendedSequence > _highest) _highest = extendedSequence;
        Buffer[extendedSequence] = new Packet { Timestamp = timestamp, Marker = marker, FrameStart = frameStart, Data = data };
        _bufferedBytes += data.Length;
        return true;
    }

    protected void ClearBuffer()
    {
        Buffer.Clear();
        _bufferedBytes = 0;
        _highest = -1;
        _scanHint = 0;
        _nacks.Clear();
        _hole = false;
    }

    protected void NeedKeyFrame(long now, bool immediately)
    {
        if (!NeedKey) { NeedKey = true; KeyRequestAfter = immediately ? now : now + (long)(InitialKeyFrameWait.TotalSeconds * Time.TimestampFrequency); }
        else if (immediately && KeyRequestAfter > now) KeyRequestAfter = now;
    }

    protected void Remove(long sequence)
    {
        if (Buffer.Remove(sequence, out var packet)) _bufferedBytes -= packet.Data.Length;
        if (Buffer.Count == 0) _highest = -1;
    }

    /// <summary>Forgets repair state for a sequence number that was consumed.</summary>
    protected void Consumed(long sequence) => _nacks.Remove(sequence);

    /// <summary>Marks that the packets before the next expected number are accounted for, so no hole is pending.</summary>
    protected void HoleClosed() => _hole = false;

    protected bool TryChargePixels(long pixels, long now)
    {
        var frequency = Time.TimestampFrequency;
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

    /// <summary>Charges work that was only known after the fact; the bucket may go negative, which delays the next picture.</summary>
    protected void ChargePixelDebt(long pixels) => _pixelTokens -= Math.Max(0, pixels);

    protected void DropRange(long from, long to)
    {
        for (var s = from; s <= to; s++) { Remove(s); _nacks.Remove(s); }
        PacketsDiscarded += to - from + 1;
        Next = to + 1;
        _hole = false;
        FramesDiscarded++;
    }

    protected void Maintain(long now, VideoReceiverOutput output)
    {
        var frequency = Time.TimestampFrequency;
        // A hole: some sequence number at or after the next expected one is absent while a later one is buffered.
        // With no gap the buffer holds exactly (highest - next + 1) packets, which is checked without walking it.
        if (Buffer.Count > 0 && Buffer.Count != _highest - Next + 1)
        {
            // The scan resumes where the last one stopped: packets below the hint cannot disappear except by Next moving past them.
            var firstMissing = Math.Max(Next, _scanHint);
            while (Buffer.ContainsKey(firstMissing)) firstMissing++;
            _scanHint = firstMissing;
            var highest = _highest;
            if (!_hole) { _hole = true; _holeSince = now; }
            var age = Time.GetElapsedTime(_holeSince, now);
            if (highest - Next > MaxNackWindow || age >= LossTimeout)
            {
                GiveUpOnHole(firstMissing, now);
            }
            else if (SupportsNack && age >= NackDelay && (_lastNackScan == 0 || Time.GetElapsedTime(_lastNackScan, now) >= NackScanInterval))
            {
                _lastNackScan = now;
                if (_nacks.Count >= MaxNackTracked) foreach (var stale in _nacks.Keys.Where(k => k < Next).ToArray()) _nacks.Remove(stale);
                // At most MaxNackItems numbers per request: every one of them fits in the packet, so retry counts stay honest.
                for (var s = firstMissing; s < highest && output.Nack.Count < RtcpCodec.MaxNackItems; s++)
                {
                    if (Buffer.ContainsKey(s)) continue;
                    if (!_nacks.TryGetValue(s, out var state))
                    {
                        if (_nacks.Count >= MaxNackTracked) continue;
                        _nacks[s] = state = new NackState { LastSent = now - 10 * frequency };
                    }
                    if (state.Count >= MaxNackRetries || Time.GetElapsedTime(state.LastSent, now) < NackRetry) continue;
                    state.Count++; state.LastSent = now;
                    output.Nack.Add((ushort)s);
                    NacksRequested++;
                }
            }
        }
        else { _hole = false; _scanHint = 0; }

        // The interval is measured from the last request whether or not a key frame decoded in between.
        if (NeedKey && SupportsKeyFrameRequest && now >= KeyRequestAfter &&
            (!_keyRequestEver || Time.GetElapsedTime(_lastKeyRequest, now) >= KeyFrameRequestInterval))
        {
            _keyRequestEver = true; _lastKeyRequest = now;
            output.KeyFrameRequest = true;
            KeyFrameRequests++;
        }
    }

    /// <summary>The first buffered sequence number after <paramref name="firstMissing"/> from which assembly can resume, or -1 when there is none.</summary>
    protected virtual long FindResumePoint(long firstMissing)
    {
        foreach (var (sequence, packet) in Buffer)
            if (sequence > firstMissing && packet.FrameStart) return sequence;
        return -1;
    }

    private void GiveUpOnHole(long firstMissing, long now)
    {
        // Count the missing packets, drop everything up to the next frame start that follows the hole, and wait for a key frame.
        var resume = FindResumePoint(firstMissing);
        var limit = resume >= 0 ? resume : _highest + 1;
        for (var s = Next; s < limit; s++)
        {
            if (!Buffer.ContainsKey(s)) PacketsLost++;
            Remove(s);
            _nacks.Remove(s);
        }
        Next = limit;
        _hole = false;
        _scanHint = 0;
        FramesDiscarded++;
        NeedKey = true;
        KeyRequestAfter = now;
    }
}
