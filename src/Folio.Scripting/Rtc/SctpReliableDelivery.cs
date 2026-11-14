using System.Buffers.Binary;

namespace Folio.Scripting.Rtc;

/// <summary>Bounded DATA fragmentation, reassembly and reliability. Accessed under the association lock.</summary>
internal sealed class SctpReliableDelivery(uint initialLocalTsn = 1, RtcMediaBudget? budget = null)
{
    internal const int MaxSendBytes = 1024 * 1024;
    internal const int MaxReceiveBytes = 1024 * 1024;
    internal const int MaxChunks = 1024;
    private readonly Dictionary<uint, Pending> _outgoing = [];
    private readonly Queue<uint> _sendOrder = [];
    private readonly Queue<uint> _unsent = [];
    private readonly HashSet<uint> _received = [];
    private readonly Dictionary<(ushort Stream, ushort Sequence), Message> _ordered = [];
    private readonly Dictionary<ushort, ushort> _sendSequences = [];
    private readonly Dictionary<ushort, ushort> _receiveSequences = [];
    private readonly Dictionary<ushort, long> _bufferedByStream = [];
    private readonly Dictionary<uint, Fragment> _fragments = [];
    private uint _highestAssigned = unchecked(initialLocalTsn - 1);
    private uint _highestEmitted = unchecked(initialLocalTsn - 1);
    private uint _lastAck = unchecked(initialLocalTsn - 1);
    private uint _cumulativeReceived;
    private uint? _peerInitialTsn;
    private int _sendBytes;
    private int _receiveBytes;
    private long? _gapSince;
    internal const int MaximumDataChunkBytes = 16 + SctpDataChannel.MaxFragmentPayloadBytes;
    private int _congestionWindow = Math.Min(4 * MaximumDataChunkBytes, Math.Max(2 * MaximumDataChunkBytes, 4404));
    private int _slowStartThreshold = MaxSendBytes;
    private int _partialBytesAcked;
    private uint _peerWindow = MaxReceiveBytes;
    private uint? _fastRecoveryUntil;
    private long? _timerStartedAt;
    private long? _zeroWindowSince;
    private long? _lastProbeAt;
    private double _probeInterval = 1;
    private double _rtoSeconds = 1;
    private double? _smoothedRtt;
    private double _rttVariation;
    private uint? _timeoutRecoveryTsn;
    private long? _lastEmissionAt;
    private long? _lastPeerActivityAt;
    internal int CongestionWindow => _congestionWindow;
    internal int FlightBytes => _outgoing.Values.Where(p => p.Sent && !p.GapAcknowledged && !p.TimeoutQueued).Sum(p => p.WireBytes);
    internal uint HighestEmittedTsn => _highestEmitted;
    internal string Diagnostics => $"assigned={_highestAssigned},emitted={_highestEmitted},ack={_lastAck},queued={_unsent.Count},retransmitQueued={_outgoing.Values.Count(p => p.TimeoutQueued && !p.GapAcknowledged)},pending={_outgoing.Count},flight={FlightBytes},cwnd={_congestionWindow},rwnd={_peerWindow},rto={_rtoSeconds:F3},rx={_cumulativeReceived},gaps={_received.Count},fragments={_fragments.Count},rxBytes={_receiveBytes}";
    internal uint NextTsn => unchecked(_highestAssigned + 1);
    internal uint CumulativeReceived => _cumulativeReceived;
    // RFC 6525: a reset stream's next message starts again at stream sequence number 0.
    internal void ResetOutgoingStream(ushort stream) => _sendSequences.Remove(stream);
    internal void ResetIncomingStream(ushort stream) => _receiveSequences.Remove(stream);

    internal readonly record struct Message(ushort Stream, uint Ppid, byte[] Data, long ReceivedAt = 0);
    private sealed record Fragment(uint Tsn, ushort Stream, ushort Sequence, uint Ppid, byte Flags, byte[] Data, long ReceivedAt)
    {
        internal bool Begin => (Flags & 2) != 0;
        internal bool End => (Flags & 1) != 0;
        internal bool Unordered => (Flags & 4) != 0;
        internal bool Matches(Fragment other) => Stream == other.Stream && Ppid == other.Ppid &&
            Unordered == other.Unordered && (Unordered || Sequence == other.Sequence);
    }
    private sealed class Pending(byte[] chunk, ushort stream, uint ppid, int dataBytes, long sentAt)
    {
        internal readonly byte[] Chunk = chunk;
        internal readonly ushort Stream = stream;
        internal readonly uint Ppid = ppid;
        internal readonly int DataBytes = dataBytes;
        internal long SentAt = sentAt;
        internal long StallBudgetStartedAt = sentAt;
        internal int Transmissions;
        internal int Retries;
        internal bool GapAcknowledged;
        internal bool Sent;
        internal bool RttSampled;
        internal int MissingReports;
        internal bool FastQueued;
        internal bool TimeoutQueued;
        internal bool FastRetransmitted;
        internal bool ZeroWindowProbe;
        internal int WireBytes => (Chunk.Length + 3) & ~3;
    }

    internal bool SetPeerInitialTsn(uint tsn)
    {
        if (_peerInitialTsn is { } existing) return existing == tsn;
        _peerInitialTsn = tsn;
        _cumulativeReceived = unchecked(tsn - 1);
        return true;
    }

    internal long BufferedAmount(ushort stream) => _bufferedByStream.GetValueOrDefault(stream);

    internal IReadOnlyList<byte[]> Enqueue(uint tsn, ushort stream, uint ppid, byte[] data, long now)
    {
        if (data.Length == 0 && ppid is not (56 or 57))
            throw new InvalidOperationException("Empty messages require an empty-message PPID.");
        if (data.Length > SctpDataChannel.MaxMessageBytes) throw new InvalidOperationException("SCTP message limit exceeded.");
        var wireBytes = Math.Max(1, data.Length);
        var count = (wireBytes + SctpDataChannel.MaxFragmentPayloadBytes - 1) / SctpDataChannel.MaxFragmentPayloadBytes;
        var size = wireBytes + 16 * count;
        if (count > MaxChunks - _outgoing.Count || size > MaxSendBytes - _sendBytes)
            throw new InvalidOperationException("SCTP send buffer or message limit exceeded.");
        if (tsn != unchecked(_highestAssigned + 1)) throw new InvalidOperationException("Nonconsecutive local SCTP TSN.");
        var sequence = _sendSequences.GetValueOrDefault(stream);
        var chunks = new List<byte[]>(count);
        for (var index = 0; index < count; index++)
        {
            var offset = index * SctpDataChannel.MaxFragmentPayloadBytes;
            var length = Math.Min(SctpDataChannel.MaxFragmentPayloadBytes, wireBytes - offset);
            var payloadBytes = Math.Min(length, Math.Max(0, data.Length - offset));
            var chunk = new byte[16 + length]; chunk[0] = 0;
            chunk[1] = (byte)((index == 0 ? 2 : 0) | (index == count - 1 ? 1 : 0));
            BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(2), (ushort)chunk.Length);
            BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(4), tsn);
            BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(8), stream);
            BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(10), sequence);
            BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(12), ppid);
            if (payloadBytes > 0) data.AsSpan(offset, payloadBytes).CopyTo(chunk.AsSpan(16));
            _outgoing.Add(tsn, new Pending(chunk, stream, ppid, payloadBytes, now));
            _sendOrder.Enqueue(tsn); _unsent.Enqueue(tsn);
            chunks.Add(chunk); tsn = unchecked(tsn + 1);
        }
        if (ppid != 50) _bufferedByStream[stream] = BufferedAmount(stream) + data.Length;
        _sendSequences[stream] = unchecked((ushort)(sequence + 1));
        _highestAssigned = unchecked(tsn - 1); _sendBytes += size;
        return chunks;
    }

    internal bool Acknowledge(ReadOnlySpan<byte> payload, TimeProvider clock)
    {
        if (payload.Length < 12) return false;
        var cumulative = BinaryPrimitives.ReadUInt32BigEndian(payload);
        var gaps = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(8));
        var duplicates = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(10));
        if (gaps > MaxChunks || payload.Length != 12 + 4 * (gaps + duplicates) ||
            unchecked((int)(cumulative - _highestEmitted)) > 0) return false;
        var ranges = new List<(ushort Start, ushort End)>();
        ushort previous = 0;
        for (var i = 0; i < gaps; i++)
        {
            var start = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(12 + i * 4));
            var end = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(14 + i * 4));
            if (start <= previous || end < start || unchecked((int)(cumulative + end - _highestEmitted)) > 0) return false;
            ranges.Add((start, end)); previous = end;
        }
        if (unchecked((int)(cumulative - _lastAck)) < 0) return false;
        var flightBefore = FlightBytes;
        var oldestBefore = OldestOutstanding();
        var newlyAcked = 0;
        uint? highestNewAck = null;
        var sampledRtt = false;
        var cumulativeAdvanced = cumulative != _lastAck;
        _lastAck = cumulative;
        foreach (var (tsn, pending) in _outgoing.ToArray())
        {
            var cumulativeAck = unchecked((int)(tsn - cumulative)) <= 0;
            var distance = unchecked(tsn - cumulative);
            var gapAck = !cumulativeAck && ranges.Any(range => distance >= range.Start && distance <= range.End);
            if ((cumulativeAck || gapAck) && pending.Sent && !pending.GapAcknowledged)
            {
                if (_timeoutRecoveryTsn == tsn) _timeoutRecoveryTsn = null;
                newlyAcked += pending.WireBytes;
                if (highestNewAck is null || unchecked((int)(tsn - highestNewAck.Value)) > 0) highestNewAck = tsn;
                if (!sampledRtt && pending.Transmissions == 1 && !pending.RttSampled)
                {
                    var sample = clock.GetElapsedTime(pending.SentAt).TotalSeconds;
                    if (sample > 0) { UpdateRto(sample); sampledRtt = true; }
                    pending.RttSampled = true;
                }
            }
            if (cumulativeAck)
            {
                _outgoing.Remove(tsn); _sendBytes -= pending.Chunk.Length;
                if (pending.Ppid != 50) _bufferedByStream[pending.Stream] = BufferedAmount(pending.Stream) - pending.DataBytes;
            }
            else
            {
                if (pending.GapAcknowledged && !gapAck)
                { pending.MissingReports++; pending.FastRetransmitted = false; }
                pending.GapAcknowledged = gapAck;
                if (gapAck) { pending.FastQueued = false; pending.MissingReports = 0; }
            }
        }
        while (_sendOrder.TryPeek(out var first) && !_outgoing.ContainsKey(first)) _sendOrder.Dequeue();
        if (_fastRecoveryUntil is { } recovery && unchecked((int)(cumulative - recovery)) >= 0) _fastRecoveryUntil = null;
        if (newlyAcked > 0 && _fastRecoveryUntil is null)
        {
            if (_congestionWindow <= _slowStartThreshold)
            {
                if (cumulativeAdvanced && flightBefore >= _congestionWindow)
                    _congestionWindow = Math.Min(MaxSendBytes, _congestionWindow + Math.Min(newlyAcked, MaximumDataChunkBytes));
            }
            else
            {
                _partialBytesAcked += newlyAcked;
                if (flightBefore < _congestionWindow) _partialBytesAcked = Math.Min(_partialBytesAcked, _congestionWindow);
                else if (_partialBytesAcked >= _congestionWindow)
                { _partialBytesAcked -= _congestionWindow; _congestionWindow = Math.Min(MaxSendBytes, _congestionWindow + MaximumDataChunkBytes); }
            }
        }
        foreach (var (tsn, pending) in _outgoing)
        {
            if (!pending.Sent || pending.GapAcknowledged || pending.TimeoutQueued) continue;
            if (highestNewAck is { } highest && unchecked((int)(highest - tsn)) > 0) pending.MissingReports++;
            if (pending.MissingReports >= 3 && !pending.FastRetransmitted)
            {
                pending.FastQueued = true;
                if (_fastRecoveryUntil is null)
                {
                    _slowStartThreshold = Math.Max(_congestionWindow / 2, 4 * MaximumDataChunkBytes);
                    _congestionWindow = _slowStartThreshold; _partialBytesAcked = 0;
                    _fastRecoveryUntil = _highestEmitted;
                }
            }
        }
        SetPeerWindow(BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(4)), clock);
        var oldestAfter = OldestOutstanding();
        if (oldestAfter is null) { _timerStartedAt = null; _partialBytesAcked = 0; }
        else if (oldestBefore != oldestAfter) _timerStartedAt = clock.GetTimestamp();
        return true;
    }

    internal void SetPeerWindow(uint bytes, TimeProvider clock)
    {
        if (_peerWindow == 0 && bytes > 0)
        {
            // A responsive peer may hold its receive window shut indefinitely.
            // Resuming it starts a fresh progress budget, not an immediate
            // timeout based on how long the application was flow-controlled.
            foreach (var pending in _outgoing.Values) pending.StallBudgetStartedAt = clock.GetTimestamp();
            _rtoSeconds = _smoothedRtt is { } rtt ? Math.Clamp(rtt + 4 * _rttVariation, 1, 60) : 1;
        }
        _peerWindow = bytes;
        _lastPeerActivityAt = clock.GetTimestamp();
        if (bytes == 0) _zeroWindowSince ??= clock.GetTimestamp();
        else { _zeroWindowSince = null; _lastProbeAt = null; _probeInterval = 1; }
    }

    private uint? OldestOutstanding()
    {
        foreach (var tsn in _sendOrder)
            if (_outgoing.TryGetValue(tsn, out var pending) && pending.Sent && !pending.GapAcknowledged) return tsn;
        return null;
    }

    internal List<byte[]> TakeSendable(TimeProvider clock)
    {
        var result = new List<byte[]>();
        if (_timeoutRecoveryTsn is not null) return result;
        if (FlightBytes == 0 && _unsent.Count > 0 && _lastEmissionAt is { } last)
        {
            var idlePeriods = (int)Math.Min(32, clock.GetElapsedTime(last).TotalSeconds / _rtoSeconds);
            for (var period = 0; period < idlePeriods; period++)
                _congestionWindow = Math.Min(_congestionWindow, Math.Max(_congestionWindow / 2, 4 * MaximumDataChunkBytes));
            if (idlePeriods > 0) _partialBytesAcked = 0;
        }
        foreach (var tsn in _sendOrder)
        {
            var pending = _outgoing[tsn];
            if (!pending.FastQueued || pending.GapAcknowledged || pending.TimeoutQueued) continue;
            pending.FastQueued = false; pending.FastRetransmitted = true; pending.Retries++;
            pending.Transmissions++;
            pending.SentAt = clock.GetTimestamp(); result.Add((byte[])pending.Chunk.Clone());
            _lastEmissionAt = clock.GetTimestamp();
            if (OldestOutstanding() == tsn) _timerStartedAt = clock.GetTimestamp();
            if (result.Count >= 4) break;
        }
        if (_outgoing.Values.Any(pending => pending.FastQueued && !pending.GapAcknowledged && !pending.TimeoutQueued)) return result;
        var flight = FlightBytes;
        var payloadFlight = _outgoing.Values.Where(p => p.Sent && !p.GapAcknowledged && !p.TimeoutQueued).Sum(p => p.Chunk.Length - 16);
        // RFC 9260 section 6.3.3: after the first timeout retransmission,
        // remaining lost DATA becomes eligible as SACKs reopen cwnd. It is
        // no longer counted as in flight until actually retransmitted.
        foreach (var tsn in _sendOrder)
        {
            var pending = _outgoing[tsn];
            if (!pending.TimeoutQueued || pending.GapAcknowledged) continue;
            if (result.Count >= 4 || flight >= _congestionWindow) break;
            if (pending.DataBytes > Math.Max(0L, (long)_peerWindow - payloadFlight)) break;
            pending.TimeoutQueued = false; pending.FastQueued = false;
            pending.Retries++; pending.Transmissions++; pending.SentAt = clock.GetTimestamp();
            _lastEmissionAt = clock.GetTimestamp();
            if (OldestOutstanding() == tsn) _timerStartedAt = clock.GetTimestamp();
            result.Add((byte[])pending.Chunk.Clone());
            flight += pending.WireBytes; payloadFlight += pending.DataBytes;
        }
        if (_outgoing.Values.Any(pending => pending.TimeoutQueued && !pending.GapAcknowledged)) return result;
        while (result.Count < 4 && _unsent.TryPeek(out var tsn))
        {
            var pending = _outgoing[tsn];
            if (flight >= _congestionWindow) break;
            var credit = Math.Max(0L, (long)_peerWindow - payloadFlight);
            var probe = _peerWindow == 0 && flight == 0 && _zeroWindowSince is { } since &&
                clock.GetElapsedTime(_lastProbeAt ?? since).TotalSeconds >= _probeInterval;
            if (!probe && pending.Chunk.Length - 16 > credit) break;
            _unsent.Dequeue(); pending.Sent = true; pending.SentAt = clock.GetTimestamp(); pending.ZeroWindowProbe = probe;
            pending.Transmissions = 1;
            _highestEmitted = tsn; _timerStartedAt ??= clock.GetTimestamp();
            _lastEmissionAt = clock.GetTimestamp();
            result.Add((byte[])pending.Chunk.Clone());
            flight += pending.WireBytes; payloadFlight += pending.Chunk.Length - 16;
            if (probe) { _lastProbeAt = clock.GetTimestamp(); _probeInterval = Math.Min(60, _probeInterval * 2); break; }
        }
        return result;
    }

    private void UpdateRto(double sample)
    {
        if (_smoothedRtt is not { } smoothed) { _smoothedRtt = sample; _rttVariation = sample / 2; }
        else
        { _rttVariation = 0.75 * _rttVariation + 0.25 * Math.Abs(smoothed - sample); _smoothedRtt = 0.875 * smoothed + 0.125 * sample; }
        _rtoSeconds = Math.Clamp(_smoothedRtt.Value + 4 * _rttVariation, 1, 60);
    }

    internal List<byte[]> DueRetransmissions(TimeProvider clock, out bool failed)
    {
        var result = new List<byte[]>(); failed = false;
        if ((_gapSince is { } gap && clock.GetElapsedTime(gap) >= TimeSpan.FromSeconds(60)) ||
            _fragments.Values.Any(fragment => clock.GetElapsedTime(fragment.ReceivedAt) >= TimeSpan.FromSeconds(60)) ||
            _ordered.Values.Any(message => clock.GetElapsedTime(message.ReceivedAt) >= TimeSpan.FromSeconds(60)))
        { failed = true; return result; }
        foreach (var pending in _outgoing.Values)
        {
            if (clock.GetElapsedTime(pending.StallBudgetStartedAt) >= TimeSpan.FromSeconds(60) &&
                !(_peerWindow == 0 && _lastPeerActivityAt is { } activity && clock.GetElapsedTime(activity) < TimeSpan.FromSeconds(60)))
            { failed = true; return []; }
        }
        if (OldestOutstanding() is not { } oldest || _timerStartedAt is not { } timer || clock.GetElapsedTime(timer).TotalSeconds < _rtoSeconds) return result;
        var retransmit = _outgoing[oldest];
        if (retransmit.Retries >= 6) { failed = true; return []; }
        if (!retransmit.ZeroWindowProbe || _peerWindow != 0)
        {
            retransmit.Retries++;
            _slowStartThreshold = Math.Max(_congestionWindow / 2, 4 * MaximumDataChunkBytes);
            _congestionWindow = MaximumDataChunkBytes; _partialBytesAcked = 0; _fastRecoveryUntil = null;
            _timeoutRecoveryTsn = oldest;
            foreach (var pending in _outgoing.Values)
            {
                pending.FastQueued = false;
                if (pending.Sent && !pending.GapAcknowledged) pending.TimeoutQueued = true;
            }
            retransmit.TimeoutQueued = false;
        }
        retransmit.SentAt = clock.GetTimestamp(); _timerStartedAt = clock.GetTimestamp();
        retransmit.Transmissions++;
        _lastEmissionAt = clock.GetTimestamp();
        _rtoSeconds = Math.Min(60, _rtoSeconds * 2);
        result.Add((byte[])retransmit.Chunk.Clone());
        return result;
    }

    internal List<Message> Receive(byte flags, ReadOnlySpan<byte> payload, long now = 0)
    {
        var ready = new List<Message>();
        if (_peerInitialTsn is null || payload.Length <= 12) return ready;
        var tsn = BinaryPrimitives.ReadUInt32BigEndian(payload);
        var distance = unchecked((int)(tsn - _cumulativeReceived));
        if (distance <= 0 || distance > ushort.MaxValue || _received.Contains(tsn)) return ready;
        var stream = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(4));
        var sequence = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(6));
        var ppid = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(8));
        var expected = _receiveSequences.GetValueOrDefault(stream);
        var unordered = (flags & 4) != 0;
        if (_received.Count >= MaxChunks || _ordered.Count + _fragments.Count >= MaxChunks ||
            (!_receiveSequences.ContainsKey(stream) && _receiveSequences.Count >= MaxChunks) ||
            payload.Length > MaxReceiveBytes - _receiveBytes ||
            (budget is not null && payload.Length > budget.SctpPeerReassemblyLimit - _receiveBytes) ||
            (!unordered && (unchecked((short)(sequence - expected)) < 0 || _ordered.ContainsKey((stream, sequence)))))
            return ready;
        var fragment = new Fragment(tsn, stream, sequence, ppid, flags, payload.Slice(12).ToArray(), now);
        if (_fragments.TryGetValue(unchecked(tsn - 1), out var previous)) ValidateAdjacent(previous, fragment);
        if (_fragments.TryGetValue(unchecked(tsn + 1), out var nextFragment)) ValidateAdjacent(fragment, nextFragment);
        // A refused fragment is neither stored nor acknowledged (not in _received), so the sender retransmits it later: the aggregate
        // budget exerts backpressure and a silent per-document drop is only visible as a stalled transfer. Nothing is held for it.
        if (budget is not null && !budget.TryReserveSctpReassemblyBytes(payload.Length))
            return ready;
        _fragments.Add(tsn, fragment); _receiveBytes += payload.Length;
        _received.Add(tsn);
        while (_received.Remove(unchecked(_cumulativeReceived + 1))) _cumulativeReceived++;
        if (_received.Count == 0) _gapSince = null;
        else _gapSince ??= now;
        var start = fragment;
        for (var count = 0; !start.Begin; count++)
        {
            if (count >= MaxChunks || !_fragments.TryGetValue(unchecked(start.Tsn - 1), out var preceding)) return ready;
            ValidateAdjacent(preceding, start); start = preceding;
        }
        var parts = new List<Fragment>(); var current = start; var total = 0;
        while (true)
        {
            parts.Add(current); total += current.Data.Length;
            if (total > SctpDataChannel.MaxMessageBytes) throw new InvalidDataException("SCTP message exceeds the advertised receive limit.");
            if (current.End) break;
            if (parts.Count >= MaxChunks || !_fragments.TryGetValue(unchecked(current.Tsn + 1), out var following)) return ready;
            ValidateAdjacent(current, following); current = following;
        }
        var data = new byte[total]; var destination = 0; var fragmentBytes = 0L;
        foreach (var part in parts)
        {
            part.Data.CopyTo(data, destination); destination += part.Data.Length;
            _fragments.Remove(part.Tsn); _receiveBytes -= part.Data.Length + 12;
            fragmentBytes += part.Data.Length + 12;
        }
        var message = new Message(start.Stream, start.Ppid, data, start.ReceivedAt);
        if (start.Unordered) { budget?.ReleaseSctpReassemblyBytes(fragmentBytes); ready.Add(message); }
        else
        {
            expected = _receiveSequences.GetValueOrDefault(start.Stream);
            if (unchecked((short)(start.Sequence - expected)) < 0 || _ordered.ContainsKey((start.Stream, start.Sequence)))
            {
                budget?.ReleaseSctpReassemblyBytes(fragmentBytes);
                throw new InvalidDataException("Conflicting SCTP message sequence.");
            }
            // The message's bytes are exactly what the fragments held, minus their per-chunk headers: swap atomically (never grows).
            _ordered.Add((start.Stream, start.Sequence), message); _receiveBytes += message.Data.Length + 12;
            budget?.ReplaceSctpReassemblyBytes(fragmentBytes, message.Data.Length + 12);
            stream = start.Stream;
            while (_ordered.Remove((stream, expected), out var next))
            {
                _receiveBytes -= next.Data.Length + 12;
                budget?.ReleaseSctpReassemblyBytes(next.Data.Length + 12);
                ready.Add(next); expected++;
            }
            _receiveSequences[stream] = expected;
        }
        return ready;
    }

    private static void ValidateAdjacent(Fragment first, Fragment second)
    {
        if (first.End && second.Begin) return;
        if (first.End || second.Begin || !first.Matches(second))
            throw new InvalidDataException("Conflicting SCTP fragment chain.");
    }

    internal byte[] CreateSack()
    {
        var distances = _received.Select(tsn => unchecked(tsn - _cumulativeReceived)).Order().ToArray();
        var ranges = new List<(ushort Start, ushort End)>();
        foreach (var distance in distances)
        {
            var value = (ushort)distance;
            if (ranges.Count > 0 && ranges[^1].End + 1 == value)
                ranges[^1] = (ranges[^1].Start, value);
            else
            {
                if (ranges.Count >= 256) break; // Keep SACK below the conservative path MTU.
                ranges.Add((value, value));
            }
        }
        var chunk = new byte[16 + ranges.Count * 4]; chunk[0] = 3;
        BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(2), (ushort)chunk.Length);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(4), _cumulativeReceived);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8), (uint)(MaxReceiveBytes - _receiveBytes));
        BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(12), (ushort)ranges.Count);
        for (var i = 0; i < ranges.Count; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(16 + i * 4), ranges[i].Start);
            BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(18 + i * 4), ranges[i].End);
        }
        return chunk;
    }

    internal void Clear()
    {
        _outgoing.Clear(); _sendOrder.Clear(); _unsent.Clear(); _received.Clear(); _ordered.Clear(); _fragments.Clear(); _gapSince = null;
        _timerStartedAt = null; _zeroWindowSince = null; _lastProbeAt = null;
        _lastPeerActivityAt = null;
        if (budget is not null && _receiveBytes > 0) budget.ReleaseSctpReassemblyBytes(_receiveBytes);
        _sendSequences.Clear(); _receiveSequences.Clear(); _bufferedByStream.Clear(); _sendBytes = _receiveBytes = 0;
    }
}
