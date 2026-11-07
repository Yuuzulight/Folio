using System.Buffers.Binary;

namespace Folio.Scripting.Gpu.Remote;

/// <summary>Length-prefixed frames on the direct pipe between the document process and the GPU worker.</summary>
internal static class GpuChannelFraming
{
    internal static void Write(Stream stream, ReadOnlySpan<byte> frame)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, frame.Length);
        stream.Write(length);
        stream.Write(frame);
        stream.Flush();
    }

    /// <summary>Reads one frame; null on a clean end of stream. A length outside the bound is malformed.</summary>
    internal static byte[]? Read(Stream stream, int maxBytes)
    {
        Span<byte> length = stackalloc byte[4];
        var read = stream.ReadAtLeast(length, 4, throwOnEndOfStream: false);
        if (read == 0) return null;
        if (read != 4) throw new EndOfStreamException("Truncated GPU frame header.");
        var size = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (size < 0 || size > maxBytes) throw new InvalidDataException("GPU frame length is out of range.");
        var frame = new byte[size];
        stream.ReadExactly(frame);
        return frame;
    }
}
