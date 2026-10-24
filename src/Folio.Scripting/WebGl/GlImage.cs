using System.Numerics;

namespace Folio.Scripting.WebGl;

/// <summary>How the components of a colour or depth image are stored.</summary>
internal enum GlStorage : byte { U8, F16, F32 }

/// <summary>Which components of a texel are meaningful, and what the others read as.</summary>
internal enum GlLayout : byte { R, RG, RGB, RGBA, L, LA, A, Depth, DepthStencil, Stencil }

/// <summary>
/// The format of an image: how it is stored and which components it has. Every colour texel is stored as four components of the storage
/// type; the layout decides what the unused ones read as (alpha 1, green and blue 0, and so on). Depth is a float32 per texel.
/// </summary>
internal readonly record struct GlFormat(uint InternalFormat, GlStorage Storage, GlLayout Layout)
{
    public bool IsDepth => Layout is GlLayout.Depth or GlLayout.DepthStencil;
    public bool HasStencil => Layout is GlLayout.DepthStencil or GlLayout.Stencil;
    public bool IsColor => !IsDepth && Layout != GlLayout.Stencil;
    public bool IsFloat => IsColor && Storage != GlStorage.U8;
    public bool HasAlpha => Layout is GlLayout.RGBA or GlLayout.LA or GlLayout.A;
    public int BytesPerTexel => Layout == GlLayout.Stencil ? 0 : IsDepth ? 4 : Storage switch { GlStorage.U8 => 4, GlStorage.F16 => 8, _ => 16 };

    public static readonly GlFormat Rgba8 = new(0x8058, GlStorage.U8, GlLayout.RGBA);
}

/// <summary>Resolves the format arguments of the texture and renderbuffer calls to a <see cref="GlFormat"/>.</summary>
internal static class GlFormats
{
    public const uint HalfFloat = 0x140B;
    public const uint HalfFloatOes = 0x8D61;

    private static readonly Dictionary<uint, (GlStorage Storage, GlLayout Layout)> Sized = new()
    {
        [0x8229] = (GlStorage.U8, GlLayout.R), [0x822D] = (GlStorage.F16, GlLayout.R), [0x822E] = (GlStorage.F32, GlLayout.R),
        [0x822B] = (GlStorage.U8, GlLayout.RG), [0x822F] = (GlStorage.F16, GlLayout.RG), [0x8230] = (GlStorage.F32, GlLayout.RG),
        [0x8051] = (GlStorage.U8, GlLayout.RGB), [0x881B] = (GlStorage.F16, GlLayout.RGB), [0x8815] = (GlStorage.F32, GlLayout.RGB),
        [0x8058] = (GlStorage.U8, GlLayout.RGBA), [0x881A] = (GlStorage.F16, GlLayout.RGBA), [0x8814] = (GlStorage.F32, GlLayout.RGBA),
        [0x8C41] = (GlStorage.U8, GlLayout.RGB), [0x8C43] = (GlStorage.U8, GlLayout.RGBA),
        [0x8D62] = (GlStorage.U8, GlLayout.RGB), [0x8056] = (GlStorage.U8, GlLayout.RGBA), [0x8057] = (GlStorage.U8, GlLayout.RGBA), [0x8059] = (GlStorage.U8, GlLayout.RGBA),
        [0x8C3A] = (GlStorage.F16, GlLayout.RGB), [0x8C3D] = (GlStorage.F16, GlLayout.RGB),
        [0x81A5] = (GlStorage.F32, GlLayout.Depth), [0x81A6] = (GlStorage.F32, GlLayout.Depth), [0x8CAC] = (GlStorage.F32, GlLayout.Depth),
        [0x88F0] = (GlStorage.F32, GlLayout.DepthStencil), [0x8CAD] = (GlStorage.F32, GlLayout.DepthStencil),
        [0x8D48] = (GlStorage.U8, GlLayout.Stencil),
    };

    /// <summary>The format for <c>texImage2D</c>-style arguments, or null when the combination is not one the API allows.</summary>
    public static GlFormat? Resolve(uint internalFormat, uint format, uint type)
    {
        if (Sized.TryGetValue(internalFormat, out var sized)) return new GlFormat(internalFormat, sized.Storage, sized.Layout);
        var storage = type switch
        {
            0x1401 or 0x8363 or 0x8033 or 0x8034 or 0x8368 => GlStorage.U8,
            0x1406 => GlStorage.F32,
            HalfFloat or HalfFloatOes => GlStorage.F16,
            _ => (GlStorage?)null,
        };
        switch (internalFormat)
        {
            case 0x1908: return storage is { } a ? new GlFormat(internalFormat, a, GlLayout.RGBA) : null;
            case 0x1907: return storage is { } b ? new GlFormat(internalFormat, b, GlLayout.RGB) : null;
            case 0x190A: return storage is { } c ? new GlFormat(internalFormat, c, GlLayout.LA) : null;
            case 0x1909: return storage is { } d ? new GlFormat(internalFormat, d, GlLayout.L) : null;
            case 0x1906: return storage is { } e ? new GlFormat(internalFormat, e, GlLayout.A) : null;
            case 0x1902: return type is 0x1403 or 0x1405 or 0x1406 ? new GlFormat(internalFormat, GlStorage.F32, GlLayout.Depth) : null;
            case 0x84F9: return type is 0x84FA or 0x8DAD ? new GlFormat(internalFormat, GlStorage.F32, GlLayout.DepthStencil) : null;
        }
        return null;
    }

    /// <summary>The format of a renderbuffer or an immutable texture from its sized internal format alone.</summary>
    public static GlFormat? FromSized(uint internalFormat) =>
        Sized.TryGetValue(internalFormat, out var sized) ? new GlFormat(internalFormat, sized.Storage, sized.Layout) : null;

    /// <summary>The components per texel and bytes per component for the <paramref name="format"/>/<paramref name="type"/> pair of an upload, or null.</summary>
    public static (int Components, int Bytes, bool Packed)? Unpack(uint format, uint type)
    {
        var components = format switch { 0x1908 => 4, 0x1907 => 3, 0x190A => 2, 0x1909 or 0x1906 or 0x1903 or 0x1902 => 1, 0x8227 => 2, 0x84F9 => 1, _ => 0 };
        if (components == 0) return null;
        return type switch
        {
            0x1401 => (components, 1, false),
            0x1406 => (components, 4, false),
            HalfFloat or HalfFloatOes => (components, 2, false),
            0x8363 when format == 0x1907 => (1, 2, true),
            0x8033 or 0x8034 when format == 0x1908 => (1, 2, true),
            0x1403 when format == 0x1902 => (1, 2, false),
            0x1405 when format == 0x1902 => (1, 4, false),
            0x84FA when format == 0x84F9 => (1, 4, true),
            _ => null,
        };
    }
}

/// <summary>A rectangle of texels (or samples) in one format: a texture level, a renderbuffer or a framebuffer's colour or depth.</summary>
internal sealed class GlImage
{
    public GlImage(int width, int height, GlFormat format, int samples = 1)
    {
        Width = width;
        Height = height;
        Format = format;
        Samples = samples;
        var count = checked(width * height * samples);
        Data = new byte[checked(count * format.BytesPerTexel)];
        if (format.HasStencil) Stencil = new byte[count];
    }

    public int Width { get; }
    public int Height { get; }
    public GlFormat Format { get; }
    public int Samples { get; }
    public byte[] Data { get; }
    /// <summary>The stencil bits, one byte per sample, when the format has them.</summary>
    public byte[]? Stencil { get; }
    public int ByteLength => Data.Length + (Stencil?.Length ?? 0);

    /// <summary>The stored components of a texel (or of one sample, in a multisampled image) without the layout's defaults.</summary>
    public Vector4 Load(int index)
    {
        if (Format.IsDepth) return new Vector4(BitConverter.ToSingle(Data, index * 4), 0, 0, 1);
        switch (Format.Storage)
        {
            case GlStorage.U8:
            {
                var o = index * 4;
                return new Vector4(Data[o], Data[o + 1], Data[o + 2], Data[o + 3]) * (1f / 255f);
            }
            case GlStorage.F16:
            {
                var o = index * 8;
                return new Vector4((float)BitConverter.ToHalf(Data, o), (float)BitConverter.ToHalf(Data, o + 2), (float)BitConverter.ToHalf(Data, o + 4), (float)BitConverter.ToHalf(Data, o + 6));
            }
            default:
            {
                var o = index * 16;
                return new Vector4(BitConverter.ToSingle(Data, o), BitConverter.ToSingle(Data, o + 4), BitConverter.ToSingle(Data, o + 8), BitConverter.ToSingle(Data, o + 12));
            }
        }
    }

    /// <summary>What a shader or a read sees: the stored components with the layout's defaults (alpha 1, missing colour 0, luminance in all three).</summary>
    public Vector4 LoadColor(int index) => Expand(Load(index), Format.Layout);

    public static Vector4 Expand(Vector4 v, GlLayout layout) => layout switch
    {
        GlLayout.R => new Vector4(v.X, 0, 0, 1),
        GlLayout.RG => new Vector4(v.X, v.Y, 0, 1),
        GlLayout.RGB => new Vector4(v.X, v.Y, v.Z, 1),
        GlLayout.L => new Vector4(v.X, v.X, v.X, 1),
        GlLayout.LA => new Vector4(v.X, v.X, v.X, v.W),
        GlLayout.A => new Vector4(0, 0, 0, v.W),
        GlLayout.Depth or GlLayout.DepthStencil => new Vector4(v.X, v.X, v.X, 1),
        _ => v,
    };

    /// <summary>Stores components, rounding and clamping them for an 8-bit image and keeping them as they are for a floating-point one.</summary>
    public void Store(int index, Vector4 v, bool maskR = true, bool maskG = true, bool maskB = true, bool maskA = true)
    {
        if (Format.IsDepth) { BitConverter.TryWriteBytes(Data.AsSpan(index * 4, 4), v.X); return; }
        switch (Format.Storage)
        {
            case GlStorage.U8:
            {
                var o = index * 4;
                if (maskR) Data[o] = ToByte(v.X);
                if (maskG) Data[o + 1] = ToByte(v.Y);
                if (maskB) Data[o + 2] = ToByte(v.Z);
                if (maskA) Data[o + 3] = ToByte(v.W);
                break;
            }
            case GlStorage.F16:
            {
                var o = index * 8;
                if (maskR) BitConverter.TryWriteBytes(Data.AsSpan(o, 2), (Half)v.X);
                if (maskG) BitConverter.TryWriteBytes(Data.AsSpan(o + 2, 2), (Half)v.Y);
                if (maskB) BitConverter.TryWriteBytes(Data.AsSpan(o + 4, 2), (Half)v.Z);
                if (maskA) BitConverter.TryWriteBytes(Data.AsSpan(o + 6, 2), (Half)v.W);
                break;
            }
            default:
            {
                var o = index * 16;
                if (maskR) BitConverter.TryWriteBytes(Data.AsSpan(o, 4), v.X);
                if (maskG) BitConverter.TryWriteBytes(Data.AsSpan(o + 4, 4), v.Y);
                if (maskB) BitConverter.TryWriteBytes(Data.AsSpan(o + 8, 4), v.Z);
                if (maskA) BitConverter.TryWriteBytes(Data.AsSpan(o + 12, 4), v.W);
                break;
            }
        }
    }

    public void Fill(Vector4 v, bool maskR = true, bool maskG = true, bool maskB = true, bool maskA = true)
    {
        var count = Width * Height * Samples;
        for (var i = 0; i < count; i++) Store(i, v, maskR, maskG, maskB, maskA);
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
}

/// <summary>A texture: the images of its mip levels (level 0 first; a level not given yet is null) and what it was specified as.</summary>
internal sealed class GlTexture
{
    public List<GlImage?> Levels { get; } = [];
    public bool Immutable { get; set; }
    /// <summary>Whether <c>generateMipmap</c> built the levels (as opposed to the page giving them one by one).</summary>
    public bool Generated { get; set; }

    public GlImage? Level(int level) => level >= 0 && level < Levels.Count ? Levels[level] : null;
    public GlImage? Base => Level(0);
    public GlFormat Format => Base?.Format ?? GlFormat.Rgba8;
}

internal sealed class GlRenderbuffer
{
    public GlImage? Image { get; set; }
    public uint Format { get; set; }
}

/// <summary>What is attached at one attachment point of a framebuffer: a level of a texture or a renderbuffer.</summary>
internal readonly record struct GlAttachment(uint? Texture, uint? Renderbuffer, int Level);

internal sealed class GlFramebufferObject
{
    public Dictionary<uint, GlAttachment> Attachments { get; } = [];
    public uint[] DrawBuffers { get; set; } = [0x8CE0];
    public uint ReadBuffer { get; set; } = 0x8CE0;
}
