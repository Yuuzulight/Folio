using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Folio.Scripting.Rtc;

/// <summary>Bounded DTLS handshake reassembly. Overlapping fragments must agree.</summary>
internal sealed class DtlsHandshakeFragments(RtcMediaBudget? budget = null) : IDisposable
{
    internal const int MaxBufferedBytes = 256 * 1024;
    private readonly Dictionary<ushort, Message> _messages = [];
    private int _retained;
    private sealed class Message(byte type, int length)
    {
        public byte Type { get; } = type;
        public byte[] Body { get; } = new byte[length];
        public bool[] Received { get; } = new bool[length];
        public int Count;
    }

    public void Add(ReadOnlySpan<byte> fragment, ushort expectedSequence)
    {
        if (fragment.Length < 12) throw new CryptographicException("Truncated DTLS handshake fragment.");
        var length = Read24(fragment[1..]);
        var sequence = BinaryPrimitives.ReadUInt16BigEndian(fragment[4..]);
        var offset = Read24(fragment[6..]);
        var count = Read24(fragment[9..]);
        if (count != fragment.Length - 12 || offset > length || count > length - offset)
            throw new CryptographicException("Invalid DTLS handshake fragment bounds.");
        if (sequence < expectedSequence) return; // retransmitted accepted message
        if (!_messages.TryGetValue(sequence, out var message))
        {
            // Charge both payload and coverage arrays before allocating them.
            var cost = checked(length * 2 + 12);
            if (_messages.Count >= 16 || cost > MaxBufferedBytes - _retained || (budget is not null && !budget.TryReserveDtlsReassemblyBytes(cost)))
                throw new CryptographicException("DTLS handshake reassembly limit exceeded.");
            message = new Message(fragment[0], length);
            _messages.Add(sequence, message);
            _retained += cost;
        }
        if (message.Type != fragment[0] || message.Body.Length != length)
            throw new CryptographicException("Conflicting DTLS handshake fragment header.");
        // Validate the entire overlap before changing retained state.
        for (var i = 0; i < count; i++)
            if (message.Received[offset + i] && message.Body[offset + i] != fragment[12 + i])
                throw new CryptographicException("Conflicting DTLS handshake fragment overlap.");
        for (var i = 0; i < count; i++)
        {
            if (message.Received[offset + i]) continue;
            message.Received[offset + i] = true;
            message.Body[offset + i] = fragment[12 + i];
            message.Count++;
        }
    }

    public bool TryTake(ushort sequence, out byte[] complete)
    {
        complete = [];
        if (!_messages.TryGetValue(sequence, out var message) || message.Count != message.Body.Length) return false;
        _messages.Remove(sequence);
        var cost = message.Body.Length * 2 + 12;
        _retained -= cost;
        budget?.ReleaseDtlsReassemblyBytes(cost);
        complete = new byte[12 + message.Body.Length];
        complete[0] = message.Type;
        Write24(complete.AsSpan(1), message.Body.Length);
        BinaryPrimitives.WriteUInt16BigEndian(complete.AsSpan(4), sequence);
        Write24(complete.AsSpan(9), message.Body.Length);
        message.Body.CopyTo(complete, 12);
        return true;
    }

    public void Dispose()
    {
        if (_retained > 0)
        {
            budget?.ReleaseDtlsReassemblyBytes(_retained);
            _retained = 0;
            _messages.Clear();
        }
    }

    internal static int Read24(ReadOnlySpan<byte> data) => (data[0] << 16) | (data[1] << 8) | data[2];
    private static void Write24(Span<byte> data, int value)
    { data[0] = (byte)(value >> 16); data[1] = (byte)(value >> 8); data[2] = (byte)value; }
}
