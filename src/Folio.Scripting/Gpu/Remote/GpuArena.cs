using System.Runtime.InteropServices;

namespace Folio.Scripting.Gpu.Remote;

/// <summary>Bounded byte arena shared between the document process and the GPU worker.</summary>
internal abstract class GpuArena : IDisposable
{
    internal abstract long Length { get; }
    internal abstract void Write(long offset, ReadOnlySpan<byte> data);
    internal abstract void Read(long offset, Span<byte> destination);
    public virtual void Dispose() { }

    protected void CheckRange(long offset, int length)
    {
        if (offset < 0 || length < 0 || offset > Length - length) throw new ArgumentOutOfRangeException(nameof(offset), "Arena access is out of range.");
    }
}

/// <summary>In-process arena for tests and the loopback pair.</summary>
internal sealed class ArrayGpuArena(int length) : GpuArena
{
    private readonly byte[] _bytes = new byte[length];
    internal override long Length => _bytes.Length;
    internal override void Write(long offset, ReadOnlySpan<byte> data) { CheckRange(offset, data.Length); data.CopyTo(_bytes.AsSpan((int)offset)); }
    internal override void Read(long offset, Span<byte> destination) { CheckRange(offset, destination.Length); _bytes.AsSpan((int)offset, destination.Length).CopyTo(destination); }
}

/// <summary>A mapped view of a shared section. The other process may change the bytes at any time:
/// readers copy out first and validate the copy, never the shared bytes in place.</summary>
internal sealed class MappedGpuArena(SafeBuffer view) : GpuArena
{
    internal override long Length => (long)view.ByteLength;
    internal override void Write(long offset, ReadOnlySpan<byte> data) { CheckRange(offset, data.Length); view.WriteSpan((ulong)offset, data); }
    internal override void Read(long offset, Span<byte> destination) { CheckRange(offset, destination.Length); view.ReadSpan((ulong)offset, destination); }
    public override void Dispose() => view.Dispose();
}
