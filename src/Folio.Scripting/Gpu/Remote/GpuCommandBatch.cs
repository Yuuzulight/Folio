using System.Buffers.Binary;

namespace Folio.Scripting.Gpu.Remote;

/// <summary>
/// Framing for one command-buffer flush from the document process to the GPU worker.
/// Layout (little endian): a 40-byte header, then commands, each a 8-byte command header
/// (opcode u16, reserved u16, payload length u32) followed by its payload padded to 4 bytes.
/// Upload data never travels inline beyond <see cref="MaxInlineBytes"/>; larger data is a
/// reference into the upload ring window this batch publishes.
/// </summary>
internal static class GpuCommandBatch
{
    internal const int Magic = 0x31425047; // GPB1
    internal const int HeaderBytes = 40, CommandHeaderBytes = 8;
    internal const int MaxBatchBytes = 256 * 1024, MaxCommands = 4096, MaxInlineBytes = 4 * 1024;

    internal readonly record struct Header(int Sequence, int CommandCount, int ByteLength, long UploadStart, long UploadEnd, bool Sync);

    internal readonly ref struct Command(ushort opcode, ReadOnlySpan<byte> payload)
    {
        public ushort Opcode { get; } = opcode;
        public ReadOnlySpan<byte> Payload { get; } = payload;
    }

    /// <summary>Validates the whole batch before anything is executed; returns false on any malformed field.</summary>
    internal static bool TryValidate(ReadOnlySpan<byte> batch, long uploadCapacity, out Header header)
    {
        header = default;
        if (batch.Length < HeaderBytes || batch.Length > MaxBatchBytes) return false;
        if (BinaryPrimitives.ReadInt32LittleEndian(batch) != Magic) return false;
        var sequence = BinaryPrimitives.ReadInt32LittleEndian(batch[4..]);
        var count = BinaryPrimitives.ReadInt32LittleEndian(batch[8..]);
        var length = BinaryPrimitives.ReadInt32LittleEndian(batch[12..]);
        var start = BinaryPrimitives.ReadInt64LittleEndian(batch[16..]);
        var end = BinaryPrimitives.ReadInt64LittleEndian(batch[24..]);
        var flags = BinaryPrimitives.ReadInt32LittleEndian(batch[32..]);
        if (sequence <= 0 || count < 0 || count > MaxCommands || length != batch.Length) return false;
        if (start < 0 || end < start || end - start > uploadCapacity || (flags & ~1) != 0) return false;
        if (BinaryPrimitives.ReadInt32LittleEndian(batch[36..]) != 0) return false;
        var offset = HeaderBytes;
        for (var i = 0; i < count; i++)
        {
            if (!TryReadCommandHeader(batch, offset, out _, out var payload)) return false;
            offset += CommandHeaderBytes + Align(payload);
        }
        if (offset != batch.Length) return false;
        header = new(sequence, count, length, start, end, (flags & 1) != 0);
        return true;
    }

    /// <summary>Enumerates commands of a batch that already passed <see cref="TryValidate"/>.</summary>
    internal ref struct Reader(ReadOnlySpan<byte> batch)
    {
        private readonly ReadOnlySpan<byte> _batch = batch;
        private int _offset = HeaderBytes;
        public Command Current { get; private set; }
        public bool MoveNext()
        {
            if (_offset >= _batch.Length || !TryReadCommandHeader(_batch, _offset, out var opcode, out var length)) return false;
            Current = new(opcode, _batch.Slice(_offset + CommandHeaderBytes, length));
            _offset += CommandHeaderBytes + Align(length);
            return true;
        }
    }

    /// <summary>
    /// Checks that an upload reference lies inside the window this batch published and does not
    /// wrap the ring; returns the physical offset to copy from.
    /// </summary>
    internal static bool TryResolveUpload(in Header header, long uploadCapacity, long logicalOffset, int length, out int physicalOffset)
    {
        physicalOffset = 0;
        if (length < 0 || logicalOffset < header.UploadStart || logicalOffset > header.UploadEnd - length) return false;
        var physical = logicalOffset % uploadCapacity;
        if (physical + length > uploadCapacity) return false;
        physicalOffset = (int)physical;
        return true;
    }

    internal static int Align(int length) => (length + 3) & ~3;

    private static bool TryReadCommandHeader(ReadOnlySpan<byte> batch, int offset, out ushort opcode, out int length)
    {
        opcode = 0; length = 0;
        if (offset > batch.Length - CommandHeaderBytes) return false;
        opcode = BinaryPrimitives.ReadUInt16LittleEndian(batch[offset..]);
        var reserved = BinaryPrimitives.ReadUInt16LittleEndian(batch[(offset + 2)..]);
        var payload = BinaryPrimitives.ReadUInt32LittleEndian(batch[(offset + 4)..]);
        if (opcode == 0 || reserved != 0 || payload > MaxInlineBytes) return false;
        length = (int)payload;
        return offset + CommandHeaderBytes + Align(length) <= batch.Length;
    }
}

/// <summary>Builds one batch. Overflow is reported, never truncated: callers flush and retry.</summary>
internal sealed class GpuCommandBatchWriter
{
    private readonly byte[] _buffer = new byte[GpuCommandBatch.MaxBatchBytes];
    private int _length = GpuCommandBatch.HeaderBytes, _count;

    internal int CommandCount => _count;
    internal bool IsEmpty => _count == 0;

    internal bool TryAppend(ushort opcode, ReadOnlySpan<byte> payload)
    {
        if (opcode == 0) throw new ArgumentOutOfRangeException(nameof(opcode));
        if (payload.Length > GpuCommandBatch.MaxInlineBytes) throw new ArgumentException("Inline payload exceeds the command limit; use the upload ring.", nameof(payload));
        var size = GpuCommandBatch.CommandHeaderBytes + GpuCommandBatch.Align(payload.Length);
        if (_count == GpuCommandBatch.MaxCommands || _length + size > _buffer.Length) return false;
        var span = _buffer.AsSpan(_length, size);
        span.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(span, opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)payload.Length);
        payload.CopyTo(span[GpuCommandBatch.CommandHeaderBytes..]);
        _length += size;
        _count++;
        return true;
    }

    /// <summary>Seals the batch and returns its bytes; the writer is reset for the next batch.</summary>
    internal byte[] Seal(int sequence, long uploadStart, long uploadEnd, bool sync)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
        var header = _buffer.AsSpan(0, GpuCommandBatch.HeaderBytes);
        header.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(header, GpuCommandBatch.Magic);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], sequence);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], _count);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], _length);
        BinaryPrimitives.WriteInt64LittleEndian(header[16..], uploadStart);
        BinaryPrimitives.WriteInt64LittleEndian(header[24..], uploadEnd);
        BinaryPrimitives.WriteInt32LittleEndian(header[32..], sync ? 1 : 0);
        var sealedBatch = _buffer.AsSpan(0, _length).ToArray();
        _length = GpuCommandBatch.HeaderBytes;
        _count = 0;
        return sealedBatch;
    }
}
