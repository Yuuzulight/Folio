using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Folio.Scripting.Gpu.Remote;

/// <summary>Little-endian payload encoder for one command or reply. Reused between commands.</summary>
internal sealed class GpuPayloadWriter
{
    /// <summary>Blobs at or below this size travel inline; larger ones go through the upload ring.</summary>
    internal const int InlineBlobBytes = 1024;
    private byte[] _buffer = new byte[256];
    private int _length;

    internal ReadOnlySpan<byte> Written => _buffer.AsSpan(0, _length);
    internal int Length => _length;
    internal GpuPayloadWriter Reset() { _length = 0; return this; }

    internal GpuPayloadWriter U32(uint value) { BinaryPrimitives.WriteUInt32LittleEndian(Grow(4), value); return this; }
    internal GpuPayloadWriter I32(int value) { BinaryPrimitives.WriteInt32LittleEndian(Grow(4), value); return this; }
    internal GpuPayloadWriter I64(long value) { BinaryPrimitives.WriteInt64LittleEndian(Grow(8), value); return this; }
    internal GpuPayloadWriter F32(float value) { BinaryPrimitives.WriteSingleLittleEndian(Grow(4), value); return this; }
    internal GpuPayloadWriter Bool(bool value) => U32(value ? 1u : 0u);

    /// <summary>Inline bytes with a length prefix (replies, and small command data).</summary>
    internal GpuPayloadWriter Bytes(ReadOnlySpan<byte> value) { I32(value.Length); value.CopyTo(Grow(value.Length)); return this; }
    internal GpuPayloadWriter String(string? value) => Bytes(Encoding.UTF8.GetBytes(value ?? string.Empty));
    internal GpuPayloadWriter Strings(IReadOnlyList<string> values) { I32(values.Count); foreach (var value in values) String(value); return this; }

    /// <summary>
    /// Command data: inline when small, otherwise copied into the upload ring and referenced.
    /// Returns false when the ring has no room (the caller flushes and retries).
    /// </summary>
    internal bool TryBlob(ReadOnlySpan<byte> value, GpuUploadRing ring)
    {
        if (value.Length <= InlineBlobBytes) { U32(0); Bytes(value); return true; }
        if (!ring.TryWrite(value, out var offset)) return false;
        U32(1); I64(offset); I32(value.Length);
        return true;
    }

    internal static ReadOnlySpan<byte> AsBytes<T>(ReadOnlySpan<T> values) where T : struct => MemoryMarshal.AsBytes(values);

    private Span<byte> Grow(int count)
    {
        if (_length + count > _buffer.Length) Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + count));
        var span = _buffer.AsSpan(_length, count);
        _length += count;
        return span;
    }
}

/// <summary>Bounds-checked decoder. Any malformed field throws <see cref="InvalidDataException"/>.</summary>
internal ref struct GpuPayloadReader(ReadOnlySpan<byte> payload)
{
    private readonly ReadOnlySpan<byte> _payload = payload;
    private int _offset;

    internal readonly bool AtEnd => _offset == _payload.Length;
    internal void SkipRest() => _offset = _payload.Length;
    internal ReadOnlySpan<byte> Rest() { var rest = _payload[_offset..]; _offset = _payload.Length; return rest; }
    internal uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    internal int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    internal long I64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
    internal float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    internal bool Bool() => U32() switch { 0 => false, 1 => true, _ => throw new InvalidDataException("Invalid boolean.") };

    internal ReadOnlySpan<byte> Bytes(int maxLength = int.MaxValue)
    {
        var length = I32();
        if (length < 0 || length > maxLength) throw new InvalidDataException("Invalid byte length.");
        return Take(length);
    }

    internal string String(int maxBytes = 1 << 20) => Encoding.UTF8.GetString(Bytes(maxBytes));

    internal string[] Strings(int maxCount = 4096)
    {
        var count = I32();
        if (count < 0 || count > maxCount) throw new InvalidDataException("Invalid string count.");
        var values = new string[count];
        for (var i = 0; i < count; i++) values[i] = String();
        return values;
    }

    /// <summary>Copies a blob out of the command or the upload ring; the copy is what gets validated and used.</summary>
    internal byte[] Blob(in GpuCommandBatch.Header header, GpuArena upload, int maxLength)
    {
        switch (U32())
        {
            case 0: return Bytes(GpuPayloadWriter.InlineBlobBytes).ToArray();
            case 1:
                var offset = I64();
                var length = I32();
                if (length <= GpuPayloadWriter.InlineBlobBytes || length > maxLength ||
                    !GpuCommandBatch.TryResolveUpload(header, upload.Length, offset, length, out var physical))
                    throw new InvalidDataException("Invalid upload reference.");
                var copy = new byte[length];
                upload.Read(physical, copy);
                return copy;
            default: throw new InvalidDataException("Invalid blob kind.");
        }
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || _offset > _payload.Length - count) throw new InvalidDataException("Truncated GPU payload.");
        var span = _payload.Slice(_offset, count);
        _offset += count;
        return span;
    }
}
