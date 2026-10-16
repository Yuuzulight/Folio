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
        _capacity = checked(HeaderBytes + 2 * stride * height);
        _map = MemoryMappedFile.CreateFromFile(path, FileMode.OpenOrCreate, null, _capacity, MemoryMappedFileAccess.ReadWrite);
        _view = _map.CreateViewAccessor(0, _capacity, MemoryMappedFileAccess.ReadWrite);
        if (_view.ReadInt32(0) != 0x4F494C46 || _view.ReadInt32(4) != width
            || _view.ReadInt32(8) != height || _view.ReadInt32(12) != stride)
        {
            _view.Write(0, 0x4F494C46); // FOLI
            _view.Write(4, width);
            _view.Write(8, height);
            _view.Write(12, stride);
            _view.Write(16, 0L);
            _view.Write(24, 0);
            _view.Flush();
        }
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public long Sequence { get; private set; }

    public void Publish(ReadOnlySpan<byte> pixels)
    {
        if (pixels.Length != Stride * Height) throw new ArgumentException("Pixel buffer size does not match the frame.", nameof(pixels));
        var active = _view.ReadInt32(24);
        var inactive = active == 0 ? 1 : 0;
        _view.WriteArray(HeaderBytes + inactive * Stride * Height, pixels.ToArray(), 0, pixels.Length);
        Sequence++;
        _view.Write(16, Sequence);
        _view.Write(24, inactive);
        _view.Flush();
    }

    public byte[] ReadLatest(out long sequence)
    {
        var pixels = new byte[Stride * Height];
        for (var attempt = 0; ; attempt++)
        {
            var before = _view.ReadInt64(16);
            var active = _view.ReadInt32(24);
            _view.ReadArray(HeaderBytes + active * Stride * Height, pixels, 0, pixels.Length);
            var after = _view.ReadInt64(16);
            if (before == after || attempt == 3)
            {
                sequence = after;
                return pixels;
            }
        }
    }

    public void Dispose()
    {
        _view.Dispose();
        _map.Dispose();
    }
}
