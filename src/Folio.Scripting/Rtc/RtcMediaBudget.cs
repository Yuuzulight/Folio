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
    private readonly long _maxVideoBytes;
    private long _videoBytes;
    private readonly Bucket _rx = new(), _tx = new();
    private int _tracks;

    private sealed class Bucket { public double Packets, Bytes; public long Stamp; public bool Started; }

    internal long DroppedPackets { get; private set; }

    /// <summary>The largest picture (width x height) a remote video decoder may allocate for.</summary>
    internal int MaxVideoPixels { get; }

    /// <summary>Video decoder memory (reference frames, mode info, output frames) currently reserved by this document.</summary>
    internal long VideoBytesInUse { get { lock (_gate) return _videoBytes; } }

    public RtcMediaBudget(TimeProvider? time = null, int maxTracks = 16, int packetsPerSecond = 4000, int bytesPerSecond = 4 * 1024 * 1024,
        long maxVideoBytes = 64L * 1024 * 1024, int maxVideoPixels = 1920 * 1088)
    {
        _maxVideoBytes = Math.Max(0, maxVideoBytes);
        MaxVideoPixels = Math.Max(256, maxVideoPixels);
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

    public void ReleaseVideoBytes(long bytes) { lock (_gate) _videoBytes = Math.Max(0, _videoBytes - Math.Max(0, bytes)); }

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
