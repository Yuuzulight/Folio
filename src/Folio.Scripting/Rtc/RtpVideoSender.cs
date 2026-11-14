using System.Buffers.Binary;
using Folio.Scripting.Rtc.Vp8;

namespace Folio.Scripting.Rtc;

/// <summary>One VP8 frame ready to packetize, with the 90 kHz clock advance since the previous sent frame.</summary>
internal readonly record struct EncodedVideoFrame(byte[] Data, uint TimestampAdvance);

/// <summary>
/// Packetizes and sends VP8 video frames as RTP over SRTP (RFC 7741, RFC 3550, RFC 3711).
/// Every frame is an intra frame, so PLI and FIR only matter for forcing a resend of an unchanged picture.
/// Frames are limited to <see cref="MaxFps"/>, scaled to at most 640x360, and skipped when the camera has not produced a new one.
/// Keeps a bounded send history for NACK retransmission, with per-NACK, per-packet and per-second limits.
/// Threading: <see cref="Encode"/> belongs to one producer thread and takes no lock; <see cref="Packetize"/>, <see cref="HandleNack"/>
/// and <see cref="CompleteFrame"/> belong to the media engine and run under its lock; <see cref="RequestKeyFrame"/> is safe from any thread.
/// </summary>
internal sealed class RtpVideoSender
{
    public const int MaxPacketPayloadBytes = 1150;
    public const int ClockRate = 90000;
    public const int MaxHistoryPackets = 128;
    public const int MaxWidth = 640;
    public const int MaxHeight = 360;
    public const int MaxFps = 15;
    /// <summary>Distinct sequence numbers honoured from one NACK.</summary>
    public const int MaxNackSequencesPerRequest = 32;
    /// <summary>Times one packet may be retransmitted.</summary>
    public const int MaxRetransmitsPerPacket = 3;
    /// <summary>Retransmissions allowed per second across the whole stream.</summary>
    public const int MaxRetransmitsPerSecond = 100;
    /// <summary>A packet retransmitted this recently is not retransmitted again (about one round trip).</summary>
    public static readonly TimeSpan RetransmitSpacing = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MinFrameInterval = TimeSpan.FromSeconds(1.0 / MaxFps);

    private sealed class Sent { public byte[] Packet = []; public int Retransmits; public long LastRetransmit; public bool Retransmitted; }

    private readonly RtcLocalTrack _track;
    private readonly TimeProvider _time;
    private readonly RtcMediaBudget _budget;
    private Vp8Encoder? _encoder;
    private volatile bool _forceKeyFrame = true;
    private bool _forcedAtEncode;
    private long _lastCaptureSequence;
    private long? _lastFrameStamp;
    private readonly Dictionary<ushort, Sent> _history = [];
    private readonly Queue<ushort> _historyOrder = new();
    private long _retransmitWindowStart;
    private int _retransmitWindowCount;
    private bool _retransmitWindowStarted;

    public RtpVideoSender(RtcLocalTrack track, TimeProvider time, RtcMediaBudget budget)
    {
        _track = track;
        _time = time;
        _budget = budget;
    }

    internal int HistoryCount => _history.Count;
    internal long KeyFrameRequestsHonoured;

    /// <summary>Asks for the next frame to be sent even if the camera has not changed (RFC 4585 PLI / RFC 5104 FIR). Charged against the document's key frame request budget.</summary>
    public void RequestKeyFrame()
    {
        if (!_budget.TryConsumeKeyFrameRequest()) return;
        KeyFrameRequestsHonoured++;
        _forceKeyFrame = true;
    }

    /// <summary>Encodes an RGBA frame and creates SRTP-protected RTP packets for transmission.</summary>
    public List<byte[]> EncodeAndPacketize(ReadOnlySpan<byte> rgba, int width, int height, SrtpTransport srtp, long captureSequence = 0)
    {
        ArgumentNullException.ThrowIfNull(srtp);
        if (Encode(rgba, width, height, captureSequence) is not { } frame) return [];
        var packets = Packetize(frame, srtp);
        CompleteFrame(true);
        return packets;
    }

    /// <summary>
    /// Encodes one camera frame, or returns null when it is not due: the frame is malformed, <paramref name="captureSequence"/> equals the last sent one,
    /// or the previous frame went out less than 1/<see cref="MaxFps"/> s ago. A pending key frame request overrides the last two checks.
    /// </summary>
    public EncodedVideoFrame? Encode(ReadOnlySpan<byte> rgba, int width, int height, long captureSequence = 0)
    {
        if (rgba.Length == 0 || width <= 0 || height <= 0 || (long)width * height != rgba.Length / 4 || rgba.Length % 4 != 0) return null;

        var forced = _forceKeyFrame;
        var now = _time.GetTimestamp();
        if (!forced)
        {
            if (captureSequence > 0 && captureSequence == _lastCaptureSequence) return null;
            if (_lastFrameStamp is { } last && _time.GetElapsedTime(last, now) < MinFrameInterval) return null;
        }

        // Capped at 640x360, aspect ratio kept, even dimensions; the encoder resamples from the original, so no copy is needed.
        var targetW = width;
        var targetH = height;
        if (targetW > MaxWidth || targetH > MaxHeight)
        {
            var scale = Math.Min((double)MaxWidth / targetW, (double)MaxHeight / targetH);
            targetW = (int)(targetW * scale);
            targetH = (int)(targetH * scale);
        }
        targetW = Math.Max(16, targetW & ~1);
        targetH = Math.Max(16, targetH & ~1);

        if (_encoder is null || _encoder.Width != targetW || _encoder.Height != targetH)
            _encoder = new Vp8Encoder(targetW, targetH);

        var data = _encoder.Encode(rgba, width, height);
        if (data is null || data.Length == 0) return null;

        var elapsed = _lastFrameStamp is { } prev ? Math.Max(0.01, _time.GetElapsedTime(prev, now).TotalSeconds) : 1.0 / MaxFps;
        _lastFrameStamp = now;
        if (captureSequence > 0) _lastCaptureSequence = captureSequence;
        _forcedAtEncode = forced;
        return new EncodedVideoFrame(data, (uint)(elapsed * ClockRate));
    }

    /// <summary>Splits an encoded frame into RFC 7741 packets, protects them and records them for retransmission.</summary>
    public List<byte[]> Packetize(EncodedVideoFrame frame, SrtpTransport srtp)
    {
        ArgumentNullException.ThrowIfNull(srtp);
        _track.Timestamp += frame.TimestampAdvance;
        var frameData = frame.Data;
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
            rtp[0] = 0x80;
            rtp[1] = (byte)(_track.PayloadType | (isLast ? 0x80 : 0)); // marker bit on the last packet of the frame
            var seq = _track.Sequence++;
            BinaryPrimitives.WriteUInt16BigEndian(rtp.AsSpan(2), seq);
            BinaryPrimitives.WriteUInt32BigEndian(rtp.AsSpan(4), _track.Timestamp);
            BinaryPrimitives.WriteUInt32BigEndian(rtp.AsSpan(8), _track.Ssrc);

            // VP8 payload descriptor (RFC 7741 section 4.2): S=1 on the first packet of the frame
            rtp[12] = isFirst ? (byte)0x10 : (byte)0x00;
            frameData.AsSpan(offset, chunkSize).CopyTo(rtp.AsSpan(13));

            _track.PacketsSent++;
            _track.BytesSent += chunkSize;

            var protectedPacket = srtp.ProtectRtp(rtp);
            packets.Add(protectedPacket);

            _history[seq] = new Sent { Packet = protectedPacket };
            _historyOrder.Enqueue(seq);
            while (_historyOrder.Count > MaxHistoryPackets)
            {
                _history.Remove(_historyOrder.Dequeue());
            }
        }

        return packets;
    }

    /// <summary>Reports whether every packet of the last encoded frame left. A key frame request stays pending until a whole frame went out.</summary>
    public void CompleteFrame(bool allSent)
    {
        if (allSent && _forcedAtEncode) _forceKeyFrame = false;
        _forcedAtEncode = false;
    }

    /// <summary>
    /// Finds buffered packets for the requested sequence numbers. Duplicates in one request are ignored, at most
    /// <see cref="MaxNackSequencesPerRequest"/> distinct numbers are considered, a packet is sent at most <see cref="MaxRetransmitsPerPacket"/> times and not
    /// again within <see cref="RetransmitSpacing"/>, and the stream as a whole stays under <see cref="MaxRetransmitsPerSecond"/>.
    /// </summary>
    public void HandleNack(IEnumerable<ushort> sequences, List<byte[]> retransmits)
    {
        ArgumentNullException.ThrowIfNull(sequences);
        ArgumentNullException.ThrowIfNull(retransmits);

        var now = _time.GetTimestamp();
        if (!_retransmitWindowStarted || _time.GetElapsedTime(_retransmitWindowStart, now) >= TimeSpan.FromSeconds(1))
        {
            _retransmitWindowStarted = true; _retransmitWindowStart = now; _retransmitWindowCount = 0;
        }

        var seen = new HashSet<ushort>();
        foreach (var seq in sequences)
        {
            if (!seen.Add(seq)) continue;
            if (seen.Count > MaxNackSequencesPerRequest) break;
            if (_retransmitWindowCount >= MaxRetransmitsPerSecond) break;
            if (!_history.TryGetValue(seq, out var entry)) continue;
            if (entry.Retransmits >= MaxRetransmitsPerPacket) continue;
            if (entry.Retransmitted && _time.GetElapsedTime(entry.LastRetransmit, now) < RetransmitSpacing) continue;
            entry.Retransmits++; entry.Retransmitted = true; entry.LastRetransmit = now;
            _retransmitWindowCount++;
            retransmits.Add(entry.Packet);
        }
    }
}
