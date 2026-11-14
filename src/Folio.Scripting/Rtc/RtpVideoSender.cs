using System.Buffers.Binary;
using Folio.Scripting.Rtc.Vp8;

namespace Folio.Scripting.Rtc;

/// <summary>
/// Packetizes and sends VP8 video frames as RTP over SRTP (RFC 7741, RFC 3550, RFC 3711).
/// Maintains a bounded send history for NACK retransmission, handles keyframe forcing on PLI/FIR,
/// and downscales/rate-limits video frames to fit send budgets.
/// </summary>
internal sealed class RtpVideoSender
{
    public const int MaxPacketPayloadBytes = 1150;
    public const int ClockRate = 90000;
    public const int MaxHistoryPackets = 128;
    public const int MaxWidth = 640;
    public const int MaxHeight = 360;

    private readonly RtcLocalTrack _track;
    private readonly TimeProvider _time;
    private readonly RtcMediaBudget _budget;
    private Vp8Encoder? _encoder;
    private int _encoderWidth;
    private int _encoderHeight;
    private bool _forceKeyFrame = true;
    private long _lastKeyFrameRequestTime;
    private long _lastFrameTimestamp;
    private readonly Dictionary<ushort, byte[]> _history = [];
    private readonly Queue<ushort> _historyOrder = new();

    public RtpVideoSender(RtcLocalTrack track, TimeProvider time, RtcMediaBudget budget)
    {
        _track = track;
        _time = time;
        _budget = budget;
    }

    /// <summary>Forces the next encoded video frame to be an intra keyframe (RFC 4585 PLI / RFC 5104 FIR).</summary>
    public void RequestKeyFrame()
    {
        var now = _time.GetTimestamp();
        if (_time.GetElapsedTime(_lastKeyFrameRequestTime, now) < RtcMediaBudget.KeyFrameRequestSpacing) return;
        _lastKeyFrameRequestTime = now;
        _forceKeyFrame = true;
    }

    /// <summary>Encodes an RGBA frame and creates SRTP-protected RTP packets for transmission.</summary>
    public List<byte[]> EncodeAndPacketize(ReadOnlySpan<byte> rgba, int width, int height, SrtpTransport srtp)
    {
        ArgumentNullException.ThrowIfNull(srtp);
        if (rgba.Length == 0 || width <= 0 || height <= 0 || (long)width * height * 4 != rgba.Length)
            return [];

        // Determine target dimensions capped at 640x360, maintaining aspect ratio and even dimensions
        var targetW = width;
        var targetH = height;
        if (targetW > MaxWidth || targetH > MaxHeight)
        {
            var scale = Math.Min((double)MaxWidth / targetW, (double)MaxHeight / targetH);
            targetW = Math.Max(16, ((int)(targetW * scale) >> 1) << 1);
            targetH = Math.Max(16, ((int)(targetH * scale) >> 1) << 1);
        }

        byte[] inputRgba;
        if (targetW != width || targetH != height)
        {
            inputRgba = new byte[targetW * targetH * 4];
            DownscaleRgba(rgba, width, height, inputRgba, targetW, targetH);
        }
        else
        {
            inputRgba = rgba.ToArray();
        }

        if (_encoder is null || _encoderWidth != targetW || _encoderHeight != targetH)
        {
            _encoder = new Vp8Encoder(targetW, targetH);
            _encoderWidth = targetW;
            _encoderHeight = targetH;
            _forceKeyFrame = true;
        }

        var isKeyFrame = _forceKeyFrame;
        var frameData = _encoder.Encode(inputRgba, targetW, targetH, forceKeyFrame: isKeyFrame);
        if (frameData is null || frameData.Length == 0) return [];
        if (isKeyFrame) _forceKeyFrame = false;

        // Advance 90 kHz timestamp for this frame
        var now = _time.GetTimestamp();
        var elapsedSeconds = _lastFrameTimestamp == 0 ? 1.0 / 15.0 : Math.Max(0.01, _time.GetElapsedTime(_lastFrameTimestamp, now).TotalSeconds);
        _lastFrameTimestamp = now;
        _track.Timestamp += (uint)(elapsedSeconds * ClockRate);

        // Split frameData into RFC 7741 RTP packets
        var totalBytes = frameData.Length;
        var chunkCount = (totalBytes + MaxPacketPayloadBytes - 1) / MaxPacketPayloadBytes;
        var packets = new List<byte[]>(chunkCount);

        for (var i = 0; i < chunkCount; i++)
        {
            var offset = i * MaxPacketPayloadBytes;
            var chunkSize = Math.Min(MaxPacketPayloadBytes, totalBytes - offset);
            var isFirst = i == 0;
            var isLast = i == chunkCount - 1;

            var rtp = new byte[12 + 1 + chunkSize];
            // RTP Header
            rtp[0] = 0x80;
            rtp[1] = (byte)(_track.PayloadType | (isLast ? 0x80 : 0)); // Marker bit on last packet of frame
            var seq = _track.Sequence++;
            BinaryPrimitives.WriteUInt16BigEndian(rtp.AsSpan(2), seq);
            BinaryPrimitives.WriteUInt32BigEndian(rtp.AsSpan(4), _track.Timestamp);
            BinaryPrimitives.WriteUInt32BigEndian(rtp.AsSpan(8), _track.Ssrc);

            // VP8 Payload Descriptor (RFC 7741 section 4.2): S=1 for first packet of partition, 0 for continuation
            rtp[12] = isFirst ? (byte)0x10 : (byte)0x00;
            frameData.AsSpan(offset, chunkSize).CopyTo(rtp.AsSpan(13));

            _track.PacketsSent++;
            _track.BytesSent += chunkSize;

            var protectedPacket = srtp.ProtectRtp(rtp);
            packets.Add(protectedPacket);

            // Bounded history for NACK retransmission
            _history[seq] = protectedPacket;
            _historyOrder.Enqueue(seq);
            while (_historyOrder.Count > MaxHistoryPackets)
            {
                var oldest = _historyOrder.Dequeue();
                _history.Remove(oldest);
            }
        }

        return packets;
    }

    /// <summary>Finds buffered packets matching the requested sequence numbers for NACK retransmission.</summary>
    public void HandleNack(IEnumerable<ushort> sequences, List<byte[]> retransmits)
    {
        ArgumentNullException.ThrowIfNull(sequences);
        ArgumentNullException.ThrowIfNull(retransmits);

        foreach (var seq in sequences)
        {
            if (_history.TryGetValue(seq, out var packet))
                retransmits.Add(packet);
        }
    }

    private static void DownscaleRgba(ReadOnlySpan<byte> src, int srcW, int srcH, Span<byte> dst, int dstW, int dstH)
    {
        for (var dy = 0; dy < dstH; dy++)
        {
            var sy = (dy * srcH) / dstH;
            var srcRow = sy * srcW * 4;
            var dstRow = dy * dstW * 4;
            for (var dx = 0; dx < dstW; dx++)
            {
                var sx = (dx * srcW) / dstW;
                var si = srcRow + sx * 4;
                var di = dstRow + dx * 4;
                dst[di] = src[si];
                dst[di + 1] = src[si + 1];
                dst[di + 2] = src[si + 2];
                dst[di + 3] = src[si + 3];
            }
        }
    }
}
