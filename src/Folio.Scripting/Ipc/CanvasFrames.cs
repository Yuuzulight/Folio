using System.IO.MemoryMappedFiles;

namespace Folio.Scripting.Ipc;

/// <summary>File-backed shared canvas frame for passing pixels between a content process and its host.</summary>
public sealed class CanvasFrameStore : IDisposable
{
    private const int HeaderBytes = 32;
    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private readonly int _capacity;

    public CanvasFrameStore(string path, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0 || stride < width * 4) throw new ArgumentOutOfRangeException();
        Width = width;
        Height = height;
        Stride = stride;
        _capacity = checked(HeaderBytes + stride * height);
        _map = MemoryMappedFile.CreateFromFile(path, FileMode.OpenOrCreate, null, _capacity, MemoryMappedFileAccess.ReadWrite);
        _view = _map.CreateViewAccessor(0, _capacity, MemoryMappedFileAccess.ReadWrite);
        _view.Write(0, 0x4F494C46); // FOLI
        _view.Write(4, width);
        _view.Write(8, height);
        _view.Write(12, stride);
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public long Sequence { get; private set; }

    public void Publish(ReadOnlySpan<byte> pixels)
    {
        if (pixels.Length != Stride * Height) throw new ArgumentException("Pixel buffer size does not match the frame.", nameof(pixels));
        _view.WriteArray(HeaderBytes, pixels.ToArray(), 0, pixels.Length);
        Sequence++;
        _view.Write(16, Sequence);
        _view.Flush();
    }

    public byte[] ReadLatest(out long sequence)
    {
        sequence = _view.ReadInt64(16);
        var pixels = new byte[Stride * Height];
        _view.ReadArray(HeaderBytes, pixels, 0, pixels.Length);
        return pixels;
    }

    public void Dispose()
    {
        _view.Dispose();
        _map.Dispose();
    }
}
