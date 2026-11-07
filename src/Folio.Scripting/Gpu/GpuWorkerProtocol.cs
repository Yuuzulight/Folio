using System.Buffers.Binary;

namespace Folio.Scripting.Gpu;

// Independent of the content protocol. No JSON, reflection, paths or native handles on the wire.
internal enum GpuWorkerCommand { Open = 1, CreateSurface, Clear, ClearRect, ReadPixels, Release }

internal static class GpuWorkerProtocol
{
    internal const int Magic = 0x31575047; // GPW1, little endian
    internal const int RequestBytes = 64, HeaderBytes = 32, MaxPayloadBytes = 64 * 1024;
    internal const int MaxDimension = 2048, MaxSurfaces = 64;
    internal const long MaxDeviceBytes = 64L * 1024 * 1024;

    internal static int Get(ReadOnlySpan<byte> bytes, int index) => BinaryPrimitives.ReadInt32LittleEndian(bytes[(index * 4)..]);
    internal static void Put(Span<byte> bytes, int index, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes[(index * 4)..], value);
    internal static byte[] Request(GpuWorkerCommand command, int surface = 0)
    {
        var bytes = new byte[RequestBytes];
        Put(bytes, 0, Magic); Put(bytes, 1, (int)command); Put(bytes, 3, surface);
        return bytes;
    }
    internal static int SurfaceBytes(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension)
            throw new InvalidDataException("GPU surface dimensions exceed the worker limit.");
        return checked(width * height * 4);
    }
}
