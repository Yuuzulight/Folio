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
    private readonly Bucket _rx = new(), _tx = new();
    private int _tracks;

    private sealed class Bucket { public double Packets, Bytes; public long Stamp; public bool Started; }

    internal long DroppedPackets { get; private set; }

    public RtcMediaBudget(TimeProvider? time = null, int maxTracks = 16, int packetsPerSecond = 4000, int bytesPerSecond = 4 * 1024 * 1024)
    {
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
