namespace Folio.Scripting.Rtc;

/// <summary>
/// RFC 6184 RTP depacketizer for H.264, packetization-mode 0 and 1: single NAL unit packets, STAP-A aggregation and FU-A
/// fragmentation. Packets of one RTP timestamp are assembled into an Annex-B access unit (4-byte start codes), emitted
/// when the marker bit arrives or when the next timestamp starts. An access unit that lost a packet (a sequence number
/// gap, or a fragment sequence that does not complete) is dropped whole, never passed on half-built. STAP-B, MTAP and
/// FU-B (mode 2 interleaving, never negotiated here) are ignored. Malformed packets are dropped; nothing throws.
/// </summary>
internal sealed class H264Depacketizer
{
    private static ReadOnlySpan<byte> StartCode => [0, 0, 0, 1];

    private readonly int _maxAccessUnitBytes;
    private readonly List<byte> _unit = [];
    private readonly List<byte> _fragment = [];
    private bool _inFragment;
    private bool _damaged;
    private bool _haveTimestamp;
    private uint _timestamp;
    private bool _haveSequence;
    private ushort _nextSequence;

    internal long DroppedAccessUnits { get; private set; }
    internal long DroppedPackets { get; private set; }

    internal H264Depacketizer(int maxAccessUnitBytes = 2 << 20) => _maxAccessUnitBytes = Math.Max(64, maxAccessUnitBytes);

    /// <summary>Feeds one RTP payload (the bytes after the RTP header). Completed access units are appended to <paramref name="output"/>.</summary>
    internal void Push(ReadOnlySpan<byte> payload, ushort sequenceNumber, uint timestamp, bool marker, List<byte[]> output)
    {
        // A gap while a picture is being assembled damages it. If the gap also straddles a timestamp change the lost packets
        // may be the head of the new picture, so that one is damaged too. A gap between complete pictures is left to the
        // decoder (it sees the frame_num gap).
        var gap = _haveSequence && sequenceNumber != _nextSequence && _haveTimestamp;
        if (gap) MarkLoss();
        _haveSequence = true;
        _nextSequence = (ushort)(sequenceNumber + 1);

        if (_haveTimestamp && timestamp != _timestamp)
        {
            Flush(output);   // the marker packet of the previous picture was lost
            if (gap) MarkLoss();
        }
        _haveTimestamp = true;
        _timestamp = timestamp;

        if (!Accept(payload)) { DroppedPackets++; _damaged = true; }
        if (marker) Flush(output);
    }

    /// <summary>Discards any partial state, for example after a transport reset.</summary>
    internal void Reset()
    {
        _unit.Clear();
        _fragment.Clear();
        _inFragment = false;
        _damaged = false;
        _haveTimestamp = false;
        _haveSequence = false;
    }

    private void MarkLoss()
    {
        _damaged = true;
        _inFragment = false;
        _fragment.Clear();
    }

    private void Flush(List<byte[]> output)
    {
        if (_inFragment) { _damaged = true; _inFragment = false; _fragment.Clear(); }
        if (_unit.Count > 0)
        {
            if (_damaged) DroppedAccessUnits++;
            else output.Add([.. _unit]);
        }
        _unit.Clear();
        _damaged = false;
        _haveTimestamp = false;
    }

    private bool Accept(ReadOnlySpan<byte> p)
    {
        if (p.Length < 1 || (p[0] & 0x80) != 0) return false;
        var type = p[0] & 0x1F;
        switch (type)
        {
            case >= 1 and <= 23:
                if (_inFragment) { _damaged = true; _inFragment = false; _fragment.Clear(); }
                return AppendNal(p);
            case 24:
                {
                    if (_inFragment) { _damaged = true; _inFragment = false; _fragment.Clear(); }
                    var pos = 1;
                    // Validate the whole aggregate first so a truncated packet contributes nothing.
                    var count = 0;
                    while (pos < p.Length)
                    {
                        if (pos + 2 > p.Length) return false;
                        var size = p[pos] << 8 | p[pos + 1];
                        pos += 2;
                        if (size == 0 || pos + size > p.Length || (p[pos] & 0x80) != 0 || (p[pos] & 0x1F) is 0 or >= 24) return false;
                        pos += size;
                        count++;
                    }
                    if (count == 0) return false;
                    pos = 1;
                    while (pos < p.Length)
                    {
                        var size = p[pos] << 8 | p[pos + 1];
                        pos += 2;
                        if (!AppendNal(p.Slice(pos, size))) return false;
                        pos += size;
                    }
                    return true;
                }
            case 28:
                {
                    if (p.Length < 3) return false;
                    var start = (p[1] & 0x80) != 0;
                    var end = (p[1] & 0x40) != 0;
                    var nalType = p[1] & 0x1F;
                    if (nalType is 0 or >= 24 || (p[1] & 0x20) != 0 || (start && end)) return false;
                    if (start)
                    {
                        if (_inFragment) _damaged = true;      // the previous fragmented NAL never ended
                        _fragment.Clear();
                        _fragment.Add((byte)((p[0] & 0xE0) | nalType));
                        _inFragment = true;
                    }
                    else if (!_inFragment) return false;       // continuation without a start: its head was lost
                    if (_fragment.Count + p.Length - 2 > _maxAccessUnitBytes) { _inFragment = false; _fragment.Clear(); return false; }
                    foreach (var b in p[2..]) _fragment.Add(b);
                    if (end)
                    {
                        _inFragment = false;
                        var ok = AppendNal(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_fragment));
                        _fragment.Clear();
                        return ok;
                    }
                    return true;
                }
            default:
                return false;   // STAP-B, MTAP16/24, FU-B and reserved types
        }
    }

    private bool AppendNal(ReadOnlySpan<byte> nal)
    {
        if (nal.Length == 0 || _unit.Count + 4 + nal.Length > _maxAccessUnitBytes) return false;
        _unit.AddRange(StartCode.ToArray());
        foreach (var b in nal) _unit.Add(b);
        return true;
    }
}
