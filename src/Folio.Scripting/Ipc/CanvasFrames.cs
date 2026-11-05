using System.IO.MemoryMappedFiles;

namespace Folio.Scripting.Ipc;

/// <summary>File-backed shared canvas frame for passing pixels between a content process and its host.</summary>
public sealed class CanvasFrameStore : IDisposable
{
    private const int HeaderBytes = 32;
    private readonly FileStream _file;
    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private readonly int _capacity;

    public CanvasFrameStore(string path, int width, int height, int stride)
    {
        if (!IsValidDescriptor(width, height, stride)) throw new ArgumentOutOfRangeException();
        Width = width;
        Height = height;
        Stride = stride;
        _capacity = checked(HeaderBytes + 2 * stride * height);
        _file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        _map = MemoryMappedFile.CreateFromFile(_file, null, Math.Max(_capacity, _file.Length), MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
        _view = _map.CreateViewAccessor(0, _capacity, MemoryMappedFileAccess.ReadWrite);
        var magic = _view.ReadInt32(0);
        if (magic != 0 && magic != 0x32494C46)
        {
            Dispose();
            throw new InvalidDataException("The canvas frame has an invalid header.");
        }
        if (magic == 0 || _view.ReadInt32(4) != width
            || _view.ReadInt32(8) != height || _view.ReadInt32(12) != stride)
        {
            _view.Write(0, 0x32494C46); // FLI2: atomic sequence/buffer publication.
            _view.Write(4, width);
            _view.Write(8, height);
            _view.Write(12, stride);
            _view.Write(16, 0L);
            _view.Write(24, 0);
        }
        else
        {
            var state = _view.ReadInt64(16);
            if (state < 0)
            {
                Dispose();
                throw new InvalidDataException("The canvas frame has an invalid buffer state.");
            }
            Sequence = state >> 1;
        }
    }

    /// <summary>Checks the dimensions carried in a frame descriptor before opening its shared section.</summary>
    public static bool IsValidDescriptor(int width, int height, int stride)
    {
        if (width <= 0 || height <= 0 || stride < (long)width * 4)
            return false;
        try { _ = checked(HeaderBytes + 2 * checked(stride * height)); return true; }
        catch (OverflowException) { return false; }
    }

    public int Width { get; }
    public string FilePath => _file.Name;
    public int Height { get; }
    public int Stride { get; }
    public long Sequence { get; private set; }

    public void Publish(ReadOnlySpan<byte> pixels) => Publish(pixels.ToArray());

    public void Publish(byte[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length != Stride * Height) throw new ArgumentException("Pixel buffer size does not match the frame.", nameof(pixels));
        var state = _view.ReadInt64(16);
        if (state < 0) throw new InvalidDataException("The canvas frame has an invalid buffer state.");
        var active = (int)(state & 1);
        var inactive = active == 0 ? 1 : 0;
        _view.WriteArray(HeaderBytes + inactive * Stride * Height, pixels, 0, pixels.Length);
        Thread.MemoryBarrier();
        Sequence = checked((state >> 1) + 1);
        _view.Write(16, checked(Sequence * 2) | (long)inactive);
        // Views of the same file share coherent memory. Persistence to disk is
        // unnecessary for IPC and would force synchronous I/O on every frame.
        Thread.MemoryBarrier();
    }

    public byte[] ReadLatest(out long sequence)
    {
        var pixels = new byte[Stride * Height];
        for (var attempt = 0; ; attempt++)
        {
            var before = _view.ReadInt64(16);
            if (before < 0) throw new InvalidDataException("The canvas frame has an invalid buffer state.");
            var active = (int)(before & 1);
            Thread.MemoryBarrier();
            _view.ReadArray(HeaderBytes + active * Stride * Height, pixels, 0, pixels.Length);
            Thread.MemoryBarrier();
            var after = _view.ReadInt64(16);
            if (before == after)
            {
                sequence = after >> 1;
                return pixels;
            }
            if (attempt >= 99) throw new IOException("The content frame changed continuously while reading pixels.");
        }
    }

    public void Dispose()
    {
        _view.Dispose();
        _map.Dispose();
        _file.Dispose();
    }
}
