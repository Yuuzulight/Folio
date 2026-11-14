namespace Folio.Scripting.Rtc;

/// <summary>
/// Per-document limits on WebRTC media, enforced by the host side of the transport: live tracks, and token buckets for
/// packets and bytes in each direction. One instance is shared by every peer connection of a document. Exceeding a
/// limit drops the packet (or refuses the track); it never throws and never queues.
/// </summary>
internal sealed class RtcMediaBudget
{
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly int _maxTracks, _packetsPerSecond, _bytesPerSecond;
    private readonly long _maxVideoBytes, _maxSctpReassemblyBytes, _maxDtlsReassemblyBytes;
    private long _videoBytes, _sctpReassemblyBytes, _dtlsReassemblyBytes;
    private readonly Bucket _rx = new(), _tx = new();
    private int _tracks;

    private sealed class Bucket { public double Packets, Bytes; public long Stamp; public bool Started; }

    internal long DroppedPackets { get; private set; }

    /// <summary>The largest picture (width x height) a remote video decoder may allocate for.</summary>
    internal int MaxVideoPixels { get; }

    /// <summary>Video decoder memory (reference frames, mode info, output frames) currently reserved by this document.</summary>
    internal long VideoBytesInUse { get { lock (_gate) return _videoBytes; } }

    /// <summary>SCTP data channel reassembly memory currently reserved by this document in aggregate.</summary>
    internal long SctpReassemblyBytesInUse { get { lock (_gate) return _sctpReassemblyBytes; } }

    /// <summary>DTLS handshake fragment reassembly memory currently reserved by this document in aggregate.</summary>
    internal long DtlsReassemblyBytesInUse { get { lock (_gate) return _dtlsReassemblyBytes; } }

    public RtcMediaBudget(TimeProvider? time = null, int maxTracks = 16, int packetsPerSecond = 4000, int bytesPerSecond = 4 * 1024 * 1024,
        long maxVideoBytes = 64L * 1024 * 1024, int maxVideoPixels = 1920 * 1088,
        long maxSctpReassemblyBytes = 4L * 1024 * 1024, long maxDtlsReassemblyBytes = 1024 * 1024)
    {
        _maxVideoBytes = Math.Max(0, maxVideoBytes);
        MaxVideoPixels = Math.Max(256, maxVideoPixels);
        _maxSctpReassemblyBytes = Math.Max(0, maxSctpReassemblyBytes);
        _maxDtlsReassemblyBytes = Math.Max(0, maxDtlsReassemblyBytes);
        _time = time ?? TimeProvider.System;
        _maxTracks = Math.Max(0, maxTracks);
        _packetsPerSecond = Math.Max(1, packetsPerSecond);
        _bytesPerSecond = Math.Max(1, bytesPerSecond);
    }

    public bool TryAcquireTrack()
    {
        lock (_gate)
        {
            if (_tracks >= _maxTracks) return false;
            _tracks++;
            return true;
        }
    }

    public void ReleaseTrack() { lock (_gate) if (_tracks > 0) _tracks--; }

    /// <summary>Reserves decoder memory against the document's video budget. False (and nothing reserved) when it would not fit.</summary>
    public bool TryReserveVideoBytes(long bytes)
    {
        if (bytes < 0) return false;
        lock (_gate)
        {
            if (_videoBytes + bytes > _maxVideoBytes) return false;
            _videoBytes += bytes;
            return true;
        }
    }

    // Repair requests (NACK, PLI, FIR) share the send path with audio. They get their own small allowance, document-wide, so a lossy
    // or hostile video stream cannot use up the send budget, and key frame requests are at least KeyFrameRequestSpacing apart.
    internal const int FeedbackPerSecond = 50;
    internal static readonly TimeSpan KeyFrameRequestSpacing = TimeSpan.FromMilliseconds(100);
    private double _feedbackTokens = FeedbackPerSecond;
    private long _feedbackStamp;
    private bool _feedbackStarted;
    private long _lastKeyFrameRequest;
    private bool _keyFrameRequested;

    /// <summary>Charges one repair-request packet against the document's feedback allowance.</summary>
    public bool TryConsumeFeedback()
    {
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            if (!_feedbackStarted) { _feedbackStarted = true; _feedbackStamp = now; }
            else
            {
                _feedbackTokens = Math.Min(FeedbackPerSecond, _feedbackTokens + Math.Max(0, (double)(now - _feedbackStamp) / _time.TimestampFrequency) * FeedbackPerSecond);
                _feedbackStamp = now;
            }
            if (_feedbackTokens < 1) return false;
            _feedbackTokens--;
            return true;
        }
    }

    /// <summary>Allows a key frame request (PLI or FIR) if no other went out in the last <see cref="KeyFrameRequestSpacing"/>, anywhere in the document.</summary>
    public bool TryConsumeKeyFrameRequest()
    {
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            if (_keyFrameRequested && _time.GetElapsedTime(_lastKeyFrameRequest, now) < KeyFrameRequestSpacing) return false;
            _keyFrameRequested = true; _lastKeyFrameRequest = now;
        }
        return TryConsumeFeedback();
    }

    public void ReleaseVideoBytes(long bytes) { lock (_gate) _videoBytes = Math.Max(0, _videoBytes - Math.Max(0, bytes)); }

    /// <summary>Reserves SCTP data channel reassembly memory against the document aggregate budget.</summary>
    public bool TryReserveSctpReassemblyBytes(long bytes)
    {
        if (bytes < 0) return false;
        lock (_gate)
        {
            if (_sctpReassemblyBytes + bytes > _maxSctpReassemblyBytes) return false;
            _sctpReassemblyBytes += bytes;
            return true;
        }
    }

    /// <summary>Each peer may hold at most half the aggregate SCTP budget, so one peer cannot starve its siblings. A fragment over the
    /// limit is refused unacknowledged (the sender retransmits), which is backpressure rather than data loss.</summary>
    internal long SctpPeerReassemblyLimit => _maxSctpReassemblyBytes / 2;

    /// <summary>Swaps a held reservation for a smaller or equal one in a single step so the total never drifts.</summary>
    public void ReplaceSctpReassemblyBytes(long release, long reserve)
    {
        lock (_gate) _sctpReassemblyBytes = Math.Max(0, _sctpReassemblyBytes - Math.Max(0, release) + Math.Max(0, reserve));
    }

    public void ReleaseSctpReassemblyBytes(long bytes) { lock (_gate) _sctpReassemblyBytes = Math.Max(0, _sctpReassemblyBytes - Math.Max(0, bytes)); }

    /// <summary>Reserves DTLS handshake reassembly memory against the document aggregate budget.</summary>
    public bool TryReserveDtlsReassemblyBytes(long bytes)
    {
        if (bytes < 0) return false;
        lock (_gate)
        {
            if (_dtlsReassemblyBytes + bytes > _maxDtlsReassemblyBytes) return false;
            _dtlsReassemblyBytes += bytes;
            return true;
        }
    }

    public void ReleaseDtlsReassemblyBytes(long bytes) { lock (_gate) _dtlsReassemblyBytes = Math.Max(0, _dtlsReassemblyBytes - Math.Max(0, bytes)); }

    /// <summary>Charges one datagram of the given size against the receive or send bucket (one second of burst).</summary>
    public bool TryConsume(bool receive, int bytes)
    {
        lock (_gate)
        {
            var bucket = receive ? _rx : _tx;
            var now = _time.GetTimestamp();
            if (!bucket.Started) { bucket.Started = true; bucket.Packets = _packetsPerSecond; bucket.Bytes = _bytesPerSecond; }
            else
            {
                var seconds = Math.Max(0, (double)(now - bucket.Stamp) / _time.TimestampFrequency);
                bucket.Packets = Math.Min(_packetsPerSecond, bucket.Packets + seconds * _packetsPerSecond);
                bucket.Bytes = Math.Min(_bytesPerSecond, bucket.Bytes + seconds * _bytesPerSecond);
            }
            bucket.Stamp = now;
            if (bucket.Packets < 1 || bucket.Bytes < bytes) { DroppedPackets++; return false; }
            bucket.Packets--; bucket.Bytes -= bytes;
            return true;
        }
    }
}
