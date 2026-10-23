using System.IO.Compression;
using Folio.Imaging;
using Folio.Painting.Software;
using Folio.Typography;

namespace Folio.Painting;

/// <summary>
/// Folio's own raster backend behind <see cref="IRasterBackend"/> (docs/dependencies.md, "Towards all-Folio"): antialiased
/// paths, strokes, clips, gradients, image fills, text from glyph outlines, layers, blend modes and filter effects, drawn on
/// a bitmap in Folio's own code. The product still draws with the Skia backend until this one is checked against it
/// on the conformance corpus.
/// </summary>
public sealed class SoftwareRasterBackend : IRasterBackend
{
    public static readonly SoftwareRasterBackend Instance = new();

    public ITextShaper? TextShaper => ComplexShaper.Instance;

    public IRasterSurface? CreateSurface(int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > ImageLimits.MaxPixels)
            return null;
        return new SoftwareRasterSurface(width, height);
    }
}

public sealed class SoftwareRasterSurface : IRasterSurface
{
    private readonly PixelSurface _pixels;
    private readonly SoftwareCanvas _canvas;

    public SoftwareRasterSurface(int width, int height)
    {
        _pixels = new PixelSurface(width, height);
        _canvas = new SoftwareCanvas(_pixels);
    }

    public int Width => _pixels.Width;

    public int Height => _pixels.Height;

    public ICanvas Canvas => _canvas;

    /// <summary>The bitmap as it is held: premultiplied RGBA, row-major.</summary>
    internal byte[] PremultipliedPixels => _pixels.Data;

    public byte[] ReadPixels(int x, int y, int width, int height)
    {
        var result = new byte[checked(Math.Max(width, 0) * Math.Max(height, 0) * 4)];
        for (var row = 0; row < height; row++)
        {
            var sy = y + row;
            if (sy < 0 || sy >= Height)
                continue;
            for (var column = 0; column < width; column++)
            {
                var sx = x + column;
                if (sx < 0 || sx >= Width)
                    continue;
                var from = (sy * Width + sx) * 4;
                var to = (row * width + column) * 4;
                var a = _pixels.Data[from + 3];
                if (a == 0)
                    continue;
                // Premultiplied to straight alpha.
                result[to] = (byte)Math.Min(255, (_pixels.Data[from] * 255 + a / 2) / a);
                result[to + 1] = (byte)Math.Min(255, (_pixels.Data[from + 1] * 255 + a / 2) / a);
                result[to + 2] = (byte)Math.Min(255, (_pixels.Data[from + 2] * 255 + a / 2) / a);
                result[to + 3] = a;
            }
        }
        return result;
    }

    public void WritePixels(int x, int y, int width, int height, ReadOnlySpan<byte> rgba)
    {
        for (var row = 0; row < height; row++)
        {
            var dy = y + row;
            if (dy < 0 || dy >= Height)
                continue;
            for (var column = 0; column < width; column++)
            {
                var dx = x + column;
                if (dx < 0 || dx >= Width)
                    continue;
                var from = (row * width + column) * 4;
                if (from + 3 >= rgba.Length)
                    return;
                var to = (dy * Width + dx) * 4;
                var a = rgba[from + 3];
                _pixels.Data[to] = (byte)((rgba[from] * a + 127) / 255);
                _pixels.Data[to + 1] = (byte)((rgba[from + 1] * a + 127) / 255);
                _pixels.Data[to + 2] = (byte)((rgba[from + 2] * a + 127) / 255);
                _pixels.Data[to + 3] = a;
            }
        }
    }

    public byte[]? Encode(string mimeType, double quality) => mimeType == "image/png" ? Png(ReadPixels(0, 0, Width, Height), Width, Height) : null;

    public IImageHandle Snapshot() => new DecodedImage(Width, Height, ReadPixels(0, 0, Width, Height));

    public void Dispose()
    {
    }

    // An RGBA8 PNG: unfiltered rows in one zlib stream (https://www.w3.org/TR/png/).
    private static byte[] Png(byte[] rgba, int width, int height)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            var length = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
            output.Write(length);
            var body = new byte[4 + data.Length];
            System.Text.Encoding.ASCII.GetBytes(type).CopyTo(body, 0);
            data.CopyTo(body, 4);
            output.Write(body);
            var crc = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(body));
            output.Write(crc);
        }

        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = 8;
        header[9] = 6; // RGBA
        Chunk("IHDR", header);

        using var raw = new MemoryStream();
        using (var zlib = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (var y = 0; y < height; y++)
            {
                zlib.WriteByte(0);
                zlib.Write(rgba, y * width * 4, width * 4);
            }
        }
        Chunk("IDAT", raw.ToArray());
        Chunk("IEND", []);
        return output.ToArray();
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }
}
