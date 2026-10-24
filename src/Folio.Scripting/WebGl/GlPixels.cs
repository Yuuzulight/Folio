using System.Buffers.Binary;
using System.Numerics;

namespace Folio.Scripting.WebGl;

/// <summary>Moves pixels between what a page uploads and the images a context stores.</summary>
internal static class GlPixels
{
    /// <summary>
    /// Decodes an upload (<c>texImage2D</c> with a typed array) into texels, first row first. The components sit where the upload's format puts them
    /// (luminance in X, alpha of luminance-alpha in W, and so on), which is where <see cref="GlImage.Store"/> and the layouts expect them.
    /// Returns null when the data is too short for the size or the format and type do not go together.
    /// </summary>
    public static Vector4[]? Decode(ReadOnlySpan<byte> source, int width, int height, uint format, uint type, int alignment, bool flipY, bool premultiplyAlpha, out byte[]? stencil)
    {
        stencil = null;
        if (GlFormats.Unpack(format, type) is not { } layout) return null;
        var pixelBytes = layout.Packed ? layout.Bytes : layout.Components * layout.Bytes;
        var rowBytes = (long)width * pixelBytes;
        var stride = (rowBytes + alignment - 1) / alignment * alignment;
        var needed = height == 0 ? 0 : stride * (height - 1) + rowBytes;
        if (needed > source.Length) return null;
        var texels = new Vector4[width * height];
        if (format == 0x84F9) stencil = new byte[width * height];
        Span<float> c = stackalloc float[4];
        for (var row = 0; row < height; row++)
        {
            var from = (flipY ? height - 1 - row : row) * stride;
            for (var column = 0; column < width; column++)
            {
                var at = (int)(from + (long)column * pixelBytes);
                c.Clear();
                if (layout.Packed)
                {
                    var packed = layout.Bytes == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(at, 2)) : BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(at, 4));
                    switch (type)
                    {
                        case 0x8363: c[0] = ((packed >> 11) & 31) / 31f; c[1] = ((packed >> 5) & 63) / 63f; c[2] = (packed & 31) / 31f; break;
                        case 0x8033: c[0] = ((packed >> 12) & 15) / 15f; c[1] = ((packed >> 8) & 15) / 15f; c[2] = ((packed >> 4) & 15) / 15f; c[3] = (packed & 15) / 15f; break;
                        case 0x8034: c[0] = ((packed >> 11) & 31) / 31f; c[1] = ((packed >> 6) & 31) / 31f; c[2] = ((packed >> 1) & 31) / 31f; c[3] = packed & 1; break;
                        default: c[0] = (packed >> 8) / 16777215f; stencil![row * width + column] = (byte)(packed & 255); break;
                    }
                }
                else
                {
                    for (var i = 0; i < layout.Components; i++)
                    {
                        var o = at + i * layout.Bytes;
                        c[i] = type switch
                        {
                            0x1401 => source[o] / 255f,
                            0x1406 => BinaryPrimitives.ReadSingleLittleEndian(source.Slice(o, 4)),
                            0x1403 => BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(o, 2)) / 65535f,
                            0x1405 => BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(o, 4)) / 4294967295f,
                            _ => (float)BinaryPrimitives.ReadHalfLittleEndian(source.Slice(o, 2)),
                        };
                    }
                }
                var texel = format switch
                {
                    0x1908 => new Vector4(c[0], c[1], c[2], c[3]),
                    0x1907 => new Vector4(c[0], c[1], c[2], 1),
                    0x190A => new Vector4(c[0], 0, 0, c[1]),
                    0x1909 or 0x1903 => new Vector4(c[0], 0, 0, 1),
                    0x1906 => new Vector4(0, 0, 0, c[0]),
                    0x8227 => new Vector4(c[0], c[1], 0, 1),
                    _ => new Vector4(c[0], 0, 0, 1),
                };
                if (premultiplyAlpha && format is 0x1908 or 0x190A or 0x1906)
                    texel = format == 0x190A ? new Vector4(texel.X * texel.W, 0, 0, texel.W) : format == 0x1906 ? texel : new Vector4(texel.X * texel.W, texel.Y * texel.W, texel.Z * texel.W, texel.W);
                texels[row * width + column] = texel;
            }
        }
        return texels;
    }

    /// <summary>The texels of RGBA8 bytes (a decoded image, a canvas, an ImageData), optionally flipped and premultiplied as the unpack state asks.</summary>
    public static Vector4[] FromRgba8(ReadOnlySpan<byte> source, int width, int height, bool flipY, bool premultiplyAlpha)
    {
        var texels = new Vector4[width * height];
        for (var row = 0; row < height; row++)
        {
            var from = (flipY ? height - 1 - row : row) * width * 4;
            for (var column = 0; column < width; column++)
            {
                var o = from + column * 4;
                if (o + 3 >= source.Length) break;
                int r = source[o], g = source[o + 1], b = source[o + 2], a = source[o + 3];
                if (premultiplyAlpha)
                {
                    r = r * a / 255;
                    g = g * a / 255;
                    b = b * a / 255;
                }
                texels[row * width + column] = new Vector4(r, g, b, a) * (1f / 255f);
            }
        }
        return texels;
    }

    /// <summary>Writes a block of texels into an image at an offset, clipping what falls outside it.</summary>
    public static void Write(GlImage image, int x, int y, int width, int height, Vector4[] texels, byte[]? stencil = null, int slice = 0)
    {
        for (var row = 0; row < height; row++)
        {
            if (y + row >= image.Height) break;
            for (var column = 0; column < width; column++)
            {
                if (x + column >= image.Width) break;
                var index = (slice * image.Height + y + row) * image.Width + x + column;
                image.Store(index, texels[row * width + column]);
                if (stencil is not null && image.Stencil is not null) image.Stencil[index] = stencil[row * width + column];
            }
        }
    }
}
