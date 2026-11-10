using System.Buffers.Binary;

namespace Folio.Scripting.Rtc;

/// <summary>Bounded complete-DATA reliability state. Accessed under the association lock.</summary>
internal sealed class SctpReliableDelivery(uint initialLocalTsn = 1)
{
    internal const int MaxSendBytes = 1024 * 1024;
    internal const int MaxReceiveBytes = 131072;
    internal const int MaxChunks = 1024;
    private readonly Dictionary<uint, Pending> _outgoing = [];
    private readonly HashSet<uint> _received = [];
    private readonly Dictionary<(ushort Stream, ushort Sequence), Message> _ordered = [];
    private readonly Dictionary<ushort, ushort> _sendSequences = [];
    private readonly Dictionary<ushort, ushort> _receiveSequences = [];
    private readonly Dictionary<ushort, long> _bufferedByStream = [];
    private uint _highestSent = unchecked(initialLocalTsn - 1);
    private uint _lastAck = unchecked(initialLocalTsn - 1);
    private uint _cumulativeReceived;
    private uint? _peerInitialTsn;
    private int _sendBytes;
    private int _receiveBytes;

    internal readonly record struct Message(ushort Stream, uint Ppid, byte[] Data);
    private sealed class Pending(byte[] chunk, ushort stream, uint ppid, int dataBytes, long sentAt)
    {
        internal readonly byte[] Chunk = chunk;
        internal readonly ushort Stream = stream;
        internal readonly uint Ppid = ppid;
        internal readonly int DataBytes = dataBytes;
        internal long SentAt = sentAt;
        internal readonly long FirstSentAt = sentAt;
        internal int Retries;
        internal bool GapAcknowledged;
    }

    internal bool SetPeerInitialTsn(uint tsn)
    {
        if (_peerInitialTsn is { } existing) return existing == tsn;
        _peerInitialTsn = tsn;
        _cumulativeReceived = unchecked(tsn - 1);
        return true;
    }

    internal long BufferedAmount(ushort stream) => _bufferedByStream.GetValueOrDefault(stream);

    internal byte[] Enqueue(uint tsn, ushort stream, uint ppid, byte[] data, long now)
    {
        if (data.Length == 0 && ppid is not (56 or 57))
            throw new InvalidOperationException("Empty messages require an empty-message PPID.");
        var size = 16 + Math.Max(1, data.Length);
        if (data.Length > SctpDataChannel.MaxUnfragmentedMessageBytes ||
            _outgoing.Count >= MaxChunks || size > MaxSendBytes - _sendBytes)
            throw new InvalidOperationException("SCTP send buffer is full or message requires fragmentation.");
        if (tsn != unchecked(_highestSent + 1)) throw new InvalidOperationException("Nonconsecutive local SCTP TSN.");
        var sequence = _sendSequences.GetValueOrDefault(stream);
        var chunk = new byte[size]; chunk[0] = 0; chunk[1] = 3; // Ordered, complete DATA
        BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(2), (ushort)size);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(4), tsn);
        BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(8), stream);
        BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(10), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(12), ppid);
        data.CopyTo(chunk, 16);
        _outgoing.Add(tsn, new Pending(chunk, stream, ppid, data.Length, now));
        if (ppid != 50) _bufferedByStream[stream] = BufferedAmount(stream) + data.Length;
        _sendSequences[stream] = unchecked((ushort)(sequence + 1));
        _highestSent = tsn; _sendBytes += size;
        return chunk;
    }

    internal bool Acknowledge(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 12) return false;
        var cumulative = BinaryPrimitives.ReadUInt32BigEndian(payload);
        var gaps = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(8));
        var duplicates = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(10));
        if (gaps > MaxChunks || payload.Length != 12 + 4 * (gaps + duplicates) ||
            unchecked((int)(cumulative - _highestSent)) > 0) return false;
        var ranges = new List<(ushort Start, ushort End)>();
        ushort previous = 0;
        for (var i = 0; i < gaps; i++)
        {
            var start = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(12 + i * 4));
            var end = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(14 + i * 4));
            if (start <= previous || end < start || unchecked((int)(cumulative + end - _highestSent)) > 0) return false;
            ranges.Add((start, end)); previous = end;
        }
        if (unchecked((int)(cumulative - _lastAck)) < 0) return false;
        _lastAck = cumulative;
        foreach (var (tsn, pending) in _outgoing.ToArray())
        {
            if (unchecked((int)(tsn - cumulative)) <= 0)
            {
                _outgoing.Remove(tsn); _sendBytes -= pending.Chunk.Length;
                if (pending.Ppid != 50) _bufferedByStream[pending.Stream] = BufferedAmount(pending.Stream) - pending.DataBytes;
            }
            else
            {
                var distance = unchecked(tsn - cumulative);
                pending.GapAcknowledged = ranges.Any(range => distance >= range.Start && distance <= range.End);
            }
        }
        return true;
    }

    internal List<byte[]> DueRetransmissions(TimeProvider clock, out bool failed)
    {
        var result = new List<byte[]>(); failed = false;
        foreach (var pending in _outgoing.Values)
        {
            if (clock.GetElapsedTime(pending.FirstSentAt) >= TimeSpan.FromSeconds(60))
            { failed = true; return []; }
            if (pending.GapAcknowledged || clock.GetElapsedTime(pending.SentAt) <
                TimeSpan.FromSeconds(Math.Min(8, 1 << pending.Retries))) continue;
            if (pending.Retries >= 6) { failed = true; return []; }
            pending.Retries++; pending.SentAt = clock.GetTimestamp();
            result.Add((byte[])pending.Chunk.Clone());
        }
        return result;
    }

    internal List<Message> Receive(byte flags, ReadOnlySpan<byte> payload)
    {
        var ready = new List<Message>();
        if (_peerInitialTsn is null || payload.Length < 12 || (flags & 3) != 3) return ready;
        var tsn = BinaryPrimitives.ReadUInt32BigEndian(payload);
        var distance = unchecked((int)(tsn - _cumulativeReceived));
        if (distance <= 0 || distance > ushort.MaxValue || _received.Contains(tsn)) return ready;
        var stream = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(4));
        var sequence = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(6));
        var ppid = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(8));
        var expected = _receiveSequences.GetValueOrDefault(stream);
        var unordered = (flags & 4) != 0;
        if (_received.Count >= MaxChunks || _ordered.Count >= MaxChunks ||
            (!_receiveSequences.ContainsKey(stream) && _receiveSequences.Count >= MaxChunks) ||
            payload.Length > MaxReceiveBytes - _receiveBytes ||
            (!unordered && (unchecked((short)(sequence - expected)) < 0 || _ordered.ContainsKey((stream, sequence)))))
            return ready;
        var message = new Message(stream, ppid, payload.Slice(12).ToArray());
        _received.Add(tsn);
        while (_received.Remove(unchecked(_cumulativeReceived + 1))) _cumulativeReceived++;
        if (unordered) ready.Add(message);
        else
        {
            _ordered.Add((stream, sequence), message); _receiveBytes += message.Data.Length + 12;
            while (_ordered.Remove((stream, expected), out var next))
            {
                _receiveBytes -= next.Data.Length + 12;
                ready.Add(next); expected++;
            }
            _receiveSequences[stream] = expected;
        }
        return ready;
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
            else ranges.Add((value, value));
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
        _outgoing.Clear(); _received.Clear(); _ordered.Clear();
        _sendSequences.Clear(); _receiveSequences.Clear(); _bufferedByStream.Clear(); _sendBytes = _receiveBytes = 0;
    }
}
