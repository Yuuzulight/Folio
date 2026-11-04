using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Folio.Scripting.Gpu;

internal sealed partial class FolioNativeWebGlContext
{
    internal sealed class TextureImageSlice
    {
        public int Width;
        public int Height;
        public int Depth = 1;
        public uint InternalFormat;
        public uint Format;
        public uint Type;
        public float[] FloatTexels = []; // length = Width * Height * Depth * 4
        public int[] IntTexels = [];
        public uint[] UIntTexels = [];
        public bool Defined;
    }

    internal sealed class TextureEntry : IDisposable
    {
        public uint Handle { get; }
        public uint Target; // GlTexture2D, GlTextureCubeMap, GlTexture3D, GlTexture2DArray
        public bool Immutable;
        public int ImmutableLevels;
        public uint InternalFormat;
        public bool IsInteger;
        public bool IsUnsigned;
        public bool IsDepth;
        public long ReservedBytes;

        // Parameters
        public int MinFilter = (int)GlNearestMipmapLinear;
        public int MagFilter = (int)GlLinear;
        public int WrapS = (int)GlRepeat;
        public int WrapT = (int)GlRepeat;
        public int WrapR = (int)GlRepeat;
        public int BaseLevel;
        public int MaxLevel = 1000;
        public int MinLod = -1000;
        public int MaxLod = 1000;
        public int CompareMode;
        public int CompareFunc = (int)GlLequal;

        // Key: (faceIndex, level) where faceIndex is 0 for 2D/3D/2DArray or 0..5 for CubeMap
        public readonly Dictionary<(int Face, int Level), TextureImageSlice> Slices = [];

        public IntPtr D3DResource;
        public IntPtr D3DSrv;
        public IntPtr D3DSampler;
        public bool D3DDirty = true;
        public bool SamplerDirty = true;

        public TextureEntry(uint handle) => Handle = handle;

        public void ReleaseD3D()
        {
            if (D3DSrv != IntPtr.Zero) { Marshal.Release(D3DSrv); D3DSrv = IntPtr.Zero; }
            if (D3DResource != IntPtr.Zero) { Marshal.Release(D3DResource); D3DResource = IntPtr.Zero; }
            D3DDirty = true;
        }

        public void ReleaseSampler()
        {
            if (D3DSampler != IntPtr.Zero) { Marshal.Release(D3DSampler); D3DSampler = IntPtr.Zero; }
            SamplerDirty = true;
        }

        public void Dispose()
        {
            ReleaseD3D();
            ReleaseSampler();
        }
    }

    internal sealed class RenderbufferEntry : IDisposable
    {
        public uint Handle { get; }
        public int Width;
        public int Height;
        public int Samples;
        public uint InternalFormat;
        public bool IsInteger;
        public bool IsUnsigned;
        public bool IsDepth;
        public bool IsStencil;
        public long ReservedBytes;
        public float[] FloatPixels = [];
        public int[] IntPixels = [];
        public uint[] UIntPixels = [];
        public float[] DepthValues = [];
        public byte[] StencilValues = [];
        public bool Defined;

        public RenderbufferEntry(uint handle) => Handle = handle;
        public void Dispose() { }
    }

    internal readonly record struct FboAttachment(
        uint AttachmentType, // 0 = none, GlTexture2D/CubeFace/3D/2DArray, or GlRenderbuffer
        uint ResourceHandle,
        uint TextureTarget,
        int Level,
        int Layer);

    internal sealed class FramebufferEntry : IDisposable
    {
        public uint Handle { get; }
        public readonly FboAttachment[] ColorAttachments = new FboAttachment[MaxColorAttachments];
        public FboAttachment DepthAttachment;
        public FboAttachment StencilAttachment;
        public readonly uint[] DrawBuffers = [GlColorAttachment0];

        public FramebufferEntry(uint handle) => Handle = handle;
        public void Dispose() { }
    }

    private ref uint? GetBoundTextureSlot(uint target, out bool valid)
    {
        valid = true;
        var unit = _activeTextureUnit;
        switch (target)
        {
            case GlTexture2D: return ref _boundTextures2D[unit];
            case GlTextureCubeMap:
            case GlTextureCubeMapPositiveX:
            case GlTextureCubeMapNegativeX:
            case GlTextureCubeMapPositiveY:
            case GlTextureCubeMapNegativeY:
            case GlTextureCubeMapPositiveZ:
            case GlTextureCubeMapNegativeZ:
                return ref _boundTexturesCube[unit];
            case GlTexture3D: return ref _boundTextures3D[unit];
            case GlTexture2DArray: return ref _boundTextures2DArray[unit];
            default:
                valid = false;
                return ref _boundTextures2D[0];
        }
    }

    private static uint NormalizeTextureTarget(uint target) => target switch
    {
        GlTextureCubeMapPositiveX or GlTextureCubeMapNegativeX or
        GlTextureCubeMapPositiveY or GlTextureCubeMapNegativeY or
        GlTextureCubeMapPositiveZ or GlTextureCubeMapNegativeZ => GlTextureCubeMap,
        _ => target,
    };

    private static int GetCubeFaceIndex(uint target) => target switch
    {
        GlTextureCubeMapPositiveX => 0,
        GlTextureCubeMapNegativeX => 1,
        GlTextureCubeMapPositiveY => 2,
        GlTextureCubeMapNegativeY => 3,
        GlTextureCubeMapPositiveZ => 4,
        GlTextureCubeMapNegativeZ => 5,
        _ => 0,
    };

    public bool TryCreateTexture(out WebGlTextureHandle texture, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            texture = default;
            if (!CheckAvailable(out failure)) return false;
            var id = _nextHandle++;
            _textures[id] = new TextureEntry(id);
            texture = new WebGlTextureHandle(id);
            return true;
        }
    }

    public bool TryDeleteTexture(WebGlTextureHandle texture, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (texture.Value == 0) return true;
            if (!_textures.TryGetValue(texture.Value, out var entry))
                return true;

            if (entry.ReservedBytes > 0)
                TryReserveResourceBytes(-entry.ReservedBytes, out _);

            entry.Dispose();
            _textures.Remove(texture.Value);
            for (var i = 0; i < MaxTextureUnits; i++)
            {
                if (_boundTextures2D[i] == texture.Value) _boundTextures2D[i] = null;
                if (_boundTexturesCube[i] == texture.Value) _boundTexturesCube[i] = null;
                if (_boundTextures3D[i] == texture.Value) _boundTextures3D[i] = null;
                if (_boundTextures2DArray[i] == texture.Value) _boundTextures2DArray[i] = null;
            }
            return true;
        }
    }

    public bool TryBindTexture(uint target, WebGlTextureHandle? texture, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (target is not (GlTexture2D or GlTextureCubeMap or GlTexture3D or GlTexture2DArray))
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            if ((target is GlTexture3D or GlTexture2DArray) && _options.Version == WebGlContextVersion.WebGl1)
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            ref var slot = ref GetBoundTextureSlot(target, out _);
            if (texture is null || texture.Value.Value == 0)
            {
                slot = null;
                return true;
            }
            if (!_textures.TryGetValue(texture.Value.Value, out var entry))
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            if (entry.Target != 0 && entry.Target != target)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            entry.Target = target;
            slot = entry.Handle;
            return true;
        }
    }

    public bool TryTextureParameter(uint target, uint parameter, int value, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            ref var slot = ref GetBoundTextureSlot(target, out var valid);
            if (!valid)
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            if (slot is not uint id || !_textures.TryGetValue(id, out var entry))
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            switch (parameter)
            {
                case GlTextureMinFilter:
                    if ((uint)value is not (GlNearest or GlLinear or GlNearestMipmapNearest or GlLinearMipmapNearest or GlNearestMipmapLinear or GlLinearMipmapLinear))
                    {
                        RecordGlError(GlInvalidEnum);
                        return true;
                    }
                    entry.MinFilter = value;
                    entry.SamplerDirty = true;
                    return true;
                case GlTextureMagFilter:
                    if ((uint)value is not (GlNearest or GlLinear))
                    {
                        RecordGlError(GlInvalidEnum);
                        return true;
                    }
                    entry.MagFilter = value;
                    entry.SamplerDirty = true;
                    return true;
                case GlTextureWrapS:
                    if ((uint)value is not (GlRepeat or GlClampToEdge or GlMirroredRepeat))
                    {
                        RecordGlError(GlInvalidEnum);
                        return true;
                    }
                    entry.WrapS = value;
                    entry.SamplerDirty = true;
                    return true;
                case GlTextureWrapT:
                    if ((uint)value is not (GlRepeat or GlClampToEdge or GlMirroredRepeat))
                    {
                        RecordGlError(GlInvalidEnum);
                        return true;
                    }
                    entry.WrapT = value;
                    entry.SamplerDirty = true;
                    return true;
                case GlTextureWrapR:
                    if ((uint)value is not (GlRepeat or GlClampToEdge or GlMirroredRepeat))
                    {
                        RecordGlError(GlInvalidEnum);
                        return true;
                    }
                    entry.WrapR = value;
                    entry.SamplerDirty = true;
                    return true;
                case GlTextureBaseLevel:
                    if (value < 0) { RecordGlError(GlInvalidValue); return true; }
                    entry.BaseLevel = value;
                    entry.D3DDirty = true;
                    return true;
                case GlTextureMaxLevel:
                    if (value < 0) { RecordGlError(GlInvalidValue); return true; }
                    entry.MaxLevel = value;
                    entry.D3DDirty = true;
                    return true;
                case GlTextureMinLod:
                    entry.MinLod = value;
                    entry.SamplerDirty = true;
                    return true;
                case GlTextureMaxLod:
                    entry.MaxLod = value;
                    entry.SamplerDirty = true;
                    return true;
                case GlTextureCompareMode:
                    entry.CompareMode = value;
                    entry.SamplerDirty = true;
                    return true;
                case GlTextureCompareFunc:
                    entry.CompareFunc = value;
                    entry.SamplerDirty = true;
                    return true;
                default:
                    RecordGlError(GlInvalidEnum);
                    return true;
            }
        }
    }

    public bool TryGetTextureParameter(uint target, uint parameter, out WebGlParameterValue value, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            value = default;
            if (!CheckAvailable(out failure)) return false;
            ref var slot = ref GetBoundTextureSlot(target, out var valid);
            if (!valid)
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            if (slot is not uint id || !_textures.TryGetValue(id, out var entry))
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            switch (parameter)
            {
                case GlTextureMinFilter: value = new(WebGlParameterKind.Int32, Int32: entry.MinFilter, UInt32: (uint)entry.MinFilter); return true;
                case GlTextureMagFilter: value = new(WebGlParameterKind.Int32, Int32: entry.MagFilter, UInt32: (uint)entry.MagFilter); return true;
                case GlTextureWrapS: value = new(WebGlParameterKind.Int32, Int32: entry.WrapS, UInt32: (uint)entry.WrapS); return true;
                case GlTextureWrapT: value = new(WebGlParameterKind.Int32, Int32: entry.WrapT, UInt32: (uint)entry.WrapT); return true;
                case GlTextureWrapR: value = new(WebGlParameterKind.Int32, Int32: entry.WrapR, UInt32: (uint)entry.WrapR); return true;
                case GlTextureBaseLevel: value = new(WebGlParameterKind.Int32, Int32: entry.BaseLevel); return true;
                case GlTextureMaxLevel: value = new(WebGlParameterKind.Int32, Int32: entry.MaxLevel); return true;
                case GlTextureMinLod: value = new(WebGlParameterKind.Float32, Float32: entry.MinLod, Int32: entry.MinLod); return true;
                case GlTextureMaxLod: value = new(WebGlParameterKind.Float32, Float32: entry.MaxLod, Int32: entry.MaxLod); return true;
                case GlTextureCompareMode: value = new(WebGlParameterKind.Int32, Int32: entry.CompareMode, UInt32: (uint)entry.CompareMode); return true;
                case GlTextureCompareFunc: value = new(WebGlParameterKind.Int32, Int32: entry.CompareFunc, UInt32: (uint)entry.CompareFunc); return true;
                case 0x912F: // TEXTURE_IMMUTABLE_FORMAT
                    value = new(WebGlParameterKind.Boolean, Boolean: entry.Immutable, Int32: entry.Immutable ? 1 : 0);
                    return true;
                case 0x82DF: // TEXTURE_IMMUTABLE_LEVELS
                    value = new(WebGlParameterKind.Int32, Int32: entry.ImmutableLevels);
                    return true;
                default:
                    RecordGlError(GlInvalidEnum);
                    return true;
            }
        }
    }

    public bool TryGetTextureLevelParameter(uint target, int level, uint parameter, out WebGlParameterValue value, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            value = default;
            if (!CheckAvailable(out failure)) return false;
            ref var slot = ref GetBoundTextureSlot(target, out var valid);
            if (!valid || level < 0)
            {
                RecordGlError(level < 0 ? GlInvalidValue : GlInvalidEnum);
                return true;
            }
            if (slot is not uint id || !_textures.TryGetValue(id, out var entry))
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            var face = GetCubeFaceIndex(target);
            entry.Slices.TryGetValue((face, level), out var slice);
            switch (parameter)
            {
                case 0x1000: // TEXTURE_WIDTH
                    value = new(WebGlParameterKind.Int32, Int32: slice?.Width ?? 0);
                    return true;
                case 0x1001: // TEXTURE_HEIGHT
                    value = new(WebGlParameterKind.Int32, Int32: slice?.Height ?? 0);
                    return true;
                case 0x8071: // TEXTURE_DEPTH
                    value = new(WebGlParameterKind.Int32, Int32: slice?.Depth ?? 0);
                    return true;
                case 0x1003: // TEXTURE_INTERNAL_FORMAT
                    value = new(WebGlParameterKind.UInt32, UInt32: slice?.InternalFormat ?? GlRgba, Int32: (int)(slice?.InternalFormat ?? GlRgba));
                    return true;
                default:
                    RecordGlError(GlInvalidEnum);
                    return true;
            }
        }
    }

    internal static bool ClassifyInternalFormat(
        uint internalFormat,
        uint format,
        uint type,
        out int bytesPerTexel,
        out bool isInteger,
        out bool isUnsigned,
        out bool isDepth)
    {
        isInteger = false;
        isUnsigned = false;
        isDepth = false;
        bytesPerTexel = 4;

        var validFormat = format is GlAlpha or GlLuminance or GlLuminanceAlpha or GlRed or GlRg or GlRgb or GlRgba
            or GlRedInteger or GlRgInteger or GlRgbInteger or GlRgbaInteger
            or GlDepthComponent or GlDepthStencil or GlStencilIndex8;
        var validType = type is GlByte or GlUnsignedByte or GlShort or GlUnsignedShort or GlInt or GlUnsignedInt
            or GlFloat or GlHalfFloat or GlHalfFloatOes
            or GlUnsignedShort565 or GlUnsignedShort4444 or GlUnsignedShort5551
            or 0x84FA /* UNSIGNED_INT_24_8 */ or 0x8DAD /* FLOAT_32_UNSIGNED_INT_24_8_REV */;
        if (!validFormat || !validType)
            return false;

        switch (internalFormat)
        {
            case GlAlpha:
            case GlLuminance:
            case GlR8:
                bytesPerTexel = 1;
                return true;
            case GlLuminanceAlpha:
            case GlRg8:
            case GlRgba4:
            case GlRgb5A1:
            case GlRgb565:
            case GlR16f:
                bytesPerTexel = 2;
                return true;
            case GlRgb:
            case GlRgb8:
                bytesPerTexel = type == GlUnsignedShort565 ? 2 : type == GlFloat ? 12 : type is GlHalfFloat or GlHalfFloatOes ? 6 : 3;
                return true;
            case GlRgba:
            case GlRgba8:
                bytesPerTexel = type is GlUnsignedShort4444 or GlUnsignedShort5551 ? 2 : type == GlFloat ? 16 : type is GlHalfFloat or GlHalfFloatOes ? 8 : 4;
                return true;
            case GlR32f:
            case GlRg16f:
                bytesPerTexel = 4;
                return true;
            case GlRg32f:
            case GlRgb16f:
            case GlRgba16f:
                bytesPerTexel = 8;
                return true;
            case GlRgb32f:
                bytesPerTexel = 12;
                return true;
            case GlRgba32f:
                bytesPerTexel = 16;
                return true;
            case GlR8ui:
            case GlRg8ui:
            case GlRgb8ui:
            case GlRgba8ui:
                bytesPerTexel = 4;
                isInteger = true;
                isUnsigned = true;
                return true;
            case GlR8i:
            case GlRg8i:
            case GlRgb8i:
            case GlRgba8i:
                bytesPerTexel = 4;
                isInteger = true;
                return true;
            case GlR16ui:
            case GlRg16ui:
            case GlRgba16ui:
                bytesPerTexel = 8;
                isInteger = true;
                isUnsigned = true;
                return true;
            case GlR16i:
            case GlRg16i:
            case GlRgba16i:
                bytesPerTexel = 8;
                isInteger = true;
                return true;
            case GlR32ui:
            case GlRg32ui:
            case GlRgba32ui:
                bytesPerTexel = 16;
                isInteger = true;
                isUnsigned = true;
                return true;
            case GlR32i:
            case GlRg32i:
            case GlRgba32i:
                bytesPerTexel = 16;
                isInteger = true;
                return true;
            case GlDepthComponent:
            case GlDepthComponent16:
                bytesPerTexel = 2;
                isDepth = true;
                return true;
            case GlDepthComponent24:
            case GlDepthComponent32f:
            case GlDepth24Stencil8:
            case GlDepthStencil:
                bytesPerTexel = 4;
                isDepth = true;
                return true;
            case GlDepth32fStencil8:
                bytesPerTexel = 8;
                isDepth = true;
                return true;
            case GlStencilIndex8:
                bytesPerTexel = 1;
                return true;
            default:
                return false;
        }
    }

    public bool TryTextureImage2D(
        uint target, int level, int internalFormat, int width, int height, uint format, uint type,
        ReadOnlySpan<byte> pixels, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (level < 0 || width < 0 || height < 0 || width > 4096 || height > 4096)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            var baseTarget = NormalizeTextureTarget(target);
            if (baseTarget is not (GlTexture2D or GlTextureCubeMap))
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            if (baseTarget == GlTextureCubeMap && width != height)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            ref var slot = ref GetBoundTextureSlot(target, out var valid);
            if (!valid || slot is not uint id || !_textures.TryGetValue(id, out var entry))
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            if (entry.Immutable)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            if (!ClassifyInternalFormat((uint)internalFormat, format, type, out var bpp, out var isInt, out var isUInt, out var isDepth))
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            if (!pixels.IsEmpty && width > 0 && height > 0)
            {
                if (_unpackRowLength > 0 && _unpackRowLength < width)
                {
                    RecordGlError(GlInvalidOperation);
                    return true;
                }
                var pixelBytes = GetSourcePixelByteSize(format, type);
                var rowPixels = _unpackRowLength > 0 ? _unpackRowLength : width;
                var rawRowBytes = rowPixels * pixelBytes;
                var align = Math.Max(1, _unpackAlignment);
                var alignedRowStride = ((rawRowBytes + align - 1) / align) * align;
                var baseByteOffset = _unpackSkipRows * alignedRowStride + _unpackSkipPixels * pixelBytes;
                var requiredBytes = baseByteOffset + (height - 1) * alignedRowStride + width * pixelBytes;
                if (pixels.Length < requiredBytes)
                {
                    RecordGlError(GlInvalidValue);
                    return true;
                }
            }

            long newSliceBytes = (long)width * height * bpp;
            var face = GetCubeFaceIndex(target);
            long oldSliceBytes = 0;
            if (entry.Slices.TryGetValue((face, level), out var existing))
            {
                ClassifyInternalFormat(existing.InternalFormat, existing.Format, existing.Type, out var oldBpp, out _, out _, out _);
                oldSliceBytes = (long)existing.Width * existing.Height * oldBpp;
            }

            if (!TryReserveResourceBytes(newSliceBytes - oldSliceBytes, out failure))
                return false;
            entry.ReservedBytes += newSliceBytes - oldSliceBytes;

            var slice = new TextureImageSlice
            {
                Width = width,
                Height = height,
                Depth = 1,
                InternalFormat = (uint)internalFormat,
                Format = format,
                Type = type,
                Defined = true,
                FloatTexels = new float[width * height * 4],
                IntTexels = isInt && !isUInt ? new int[width * height * 4] : [],
                UIntTexels = isUInt ? new uint[width * height * 4] : [],
            };

            if (!pixels.IsEmpty && width > 0 && height > 0)
                UnpackTexels2D(pixels, width, height, (uint)internalFormat, format, type, isInt, isUInt, slice);

            entry.InternalFormat = (uint)internalFormat;
            entry.IsInteger = isInt;
            entry.IsUnsigned = isUInt;
            entry.IsDepth = isDepth;
            entry.Slices[(face, level)] = slice;
            entry.D3DDirty = true;
            return true;
        }
    }

    public bool TryTextureStorage2D(uint target, int levels, uint internalFormat, int width, int height, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (levels <= 0 || width <= 0 || height <= 0 || width > 4096 || height > 4096)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            var maxPossibleLevels = (int)Math.Floor(Math.Log2(Math.Max(width, height))) + 1;
            if (levels > maxPossibleLevels)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            ref var slot = ref GetBoundTextureSlot(target, out var valid);
            if (!valid || slot is not uint id || !_textures.TryGetValue(id, out var entry) || entry.Immutable)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            if (!ClassifyInternalFormat(internalFormat, GlRgba, GlUnsignedByte, out var bpp, out var isInt, out var isUInt, out var isDepth))
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }

            var faces = target == GlTextureCubeMap ? 6 : 1;
            long totalBytes = 0;
            for (var lv = 0; lv < levels; lv++)
            {
                var w = Math.Max(1, width >> lv);
                var h = Math.Max(1, height >> lv);
                totalBytes += (long)w * h * bpp * faces;
            }

            if (!TryReserveResourceBytes(totalBytes - entry.ReservedBytes, out failure))
                return false;
            entry.ReservedBytes = totalBytes;
            entry.Immutable = true;
            entry.ImmutableLevels = levels;
            entry.InternalFormat = internalFormat;
            entry.IsInteger = isInt;
            entry.IsUnsigned = isUInt;
            entry.IsDepth = isDepth;
            entry.Slices.Clear();

            for (var f = 0; f < faces; f++)
            {
                for (var lv = 0; lv < levels; lv++)
                {
                    var w = Math.Max(1, width >> lv);
                    var h = Math.Max(1, height >> lv);
                    entry.Slices[(f, lv)] = new TextureImageSlice
                    {
                        Width = w,
                        Height = h,
                        Depth = 1,
                        InternalFormat = internalFormat,
                        Format = isInt ? GlRgbaInteger : GlRgba,
                        Type = isUInt ? GlUnsignedByte : isInt ? GlByte : GlUnsignedByte,
                        Defined = true,
                        FloatTexels = new float[w * h * 4],
                        IntTexels = isInt && !isUInt ? new int[w * h * 4] : [],
                        UIntTexels = isUInt ? new uint[w * h * 4] : [],
                    };
                }
            }
            entry.D3DDirty = true;
            return true;
        }
    }

    public bool TryTextureSubImage2D(
        uint target, int level, int xOffset, int yOffset, int width, int height, uint format, uint type,
        ReadOnlySpan<byte> pixels, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            ref var slot = ref GetBoundTextureSlot(target, out var valid);
            if (!valid || slot is not uint id || !_textures.TryGetValue(id, out var entry))
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            var face = GetCubeFaceIndex(target);
            if (!entry.Slices.TryGetValue((face, level), out var slice) || !slice.Defined)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            if (xOffset < 0 || yOffset < 0 || width < 0 || height < 0 || xOffset + width > slice.Width || yOffset + height > slice.Height)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            if (width == 0 || height == 0 || pixels.IsEmpty) return true;

            var temp = new TextureImageSlice
            {
                Width = width,
                Height = height,
                Depth = 1,
                InternalFormat = slice.InternalFormat,
                Format = format,
                Type = type,
                Defined = true,
                FloatTexels = new float[width * height * 4],
                IntTexels = entry.IsInteger && !entry.IsUnsigned ? new int[width * height * 4] : [],
                UIntTexels = entry.IsUnsigned ? new uint[width * height * 4] : [],
            };
            UnpackTexels2D(pixels, width, height, slice.InternalFormat, format, type, entry.IsInteger, entry.IsUnsigned, temp);

            for (var r = 0; r < height; r++)
            {
                for (var c = 0; c < width; c++)
                {
                    var srcIdx = (r * width + c) * 4;
                    var dstIdx = ((yOffset + r) * slice.Width + (xOffset + c)) * 4;
                    for (var k = 0; k < 4; k++)
                    {
                        slice.FloatTexels[dstIdx + k] = temp.FloatTexels[srcIdx + k];
                        if (slice.IntTexels.Length > 0) slice.IntTexels[dstIdx + k] = temp.IntTexels[srcIdx + k];
                        if (slice.UIntTexels.Length > 0) slice.UIntTexels[dstIdx + k] = temp.UIntTexels[srcIdx + k];
                    }
                }
            }
            entry.D3DDirty = true;
            return true;
        }
    }

    public bool TryCopyTextureImage2D(uint target, int level, int internalFormat, int x, int y, int width, int height, int border, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (border != 0 || width < 0 || height < 0)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            var rgbaBytes = new byte[width * height * 4];
            if (!ReadCurrentFramebufferPixelsRgba8(x, y, width, height, rgbaBytes, out failure))
                return false;
            return TryTextureImage2D(target, level, internalFormat, width, height, GlRgba, GlUnsignedByte, rgbaBytes, out failure);
        }
    }

    public bool TryCopyTextureSubImage2D(uint target, int level, int xOffset, int yOffset, int x, int y, int width, int height, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (width < 0 || height < 0)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            var rgbaBytes = new byte[width * height * 4];
            if (!ReadCurrentFramebufferPixelsRgba8(x, y, width, height, rgbaBytes, out failure))
                return false;
            return TryTextureSubImage2D(target, level, xOffset, yOffset, width, height, GlRgba, GlUnsignedByte, rgbaBytes, out failure);
        }
    }

    public bool TryTextureImage3D(
        uint target, int level, int internalFormat, int width, int height, int depth, uint format, uint type,
        ReadOnlySpan<byte> pixels, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (target is not (GlTexture3D or GlTexture2DArray))
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            if (level < 0 || width < 0 || height < 0 || depth < 0 || width > 2048 || height > 2048 || depth > 2048)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            ref var slot = ref GetBoundTextureSlot(target, out var valid);
            if (!valid || slot is not uint id || !_textures.TryGetValue(id, out var entry) || entry.Immutable)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            if (!ClassifyInternalFormat((uint)internalFormat, format, type, out var bpp, out var isInt, out var isUInt, out var isDepth))
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }

            long newBytes = (long)width * height * depth * bpp;
            long oldBytes = 0;
            if (entry.Slices.TryGetValue((0, level), out var existing))
            {
                ClassifyInternalFormat(existing.InternalFormat, existing.Format, existing.Type, out var oldBpp, out _, out _, out _);
                oldBytes = (long)existing.Width * existing.Height * existing.Depth * oldBpp;
            }
            if (!TryReserveResourceBytes(newBytes - oldBytes, out failure))
                return false;
            entry.ReservedBytes += newBytes - oldBytes;

            var slice = new TextureImageSlice
            {
                Width = width,
                Height = height,
                Depth = depth,
                InternalFormat = (uint)internalFormat,
                Format = format,
                Type = type,
                Defined = true,
                FloatTexels = new float[width * height * depth * 4],
                IntTexels = isInt && !isUInt ? new int[width * height * depth * 4] : [],
                UIntTexels = isUInt ? new uint[width * height * depth * 4] : [],
            };

            if (!pixels.IsEmpty && width > 0 && height > 0 && depth > 0)
                UnpackTexels3D(pixels, width, height, depth, (uint)internalFormat, format, type, isInt, isUInt, slice);

            entry.InternalFormat = (uint)internalFormat;
            entry.IsInteger = isInt;
            entry.IsUnsigned = isUInt;
            entry.IsDepth = isDepth;
            entry.Slices[(0, level)] = slice;
            entry.D3DDirty = true;
            return true;
        }
    }

    public bool TryTextureStorage3D(uint target, int levels, uint internalFormat, int width, int height, int depth, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (target is not (GlTexture3D or GlTexture2DArray))
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            if (levels <= 0 || width <= 0 || height <= 0 || depth <= 0 || width > 2048 || height > 2048 || depth > 2048)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            var maxDim = target == GlTexture3D ? Math.Max(width, Math.Max(height, depth)) : Math.Max(width, height);
            var maxPossibleLevels = (int)Math.Floor(Math.Log2(maxDim)) + 1;
            if (levels > maxPossibleLevels)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            ref var slot = ref GetBoundTextureSlot(target, out var valid);
            if (!valid || slot is not uint id || !_textures.TryGetValue(id, out var entry) || entry.Immutable)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            if (!ClassifyInternalFormat(internalFormat, GlRgba, GlUnsignedByte, out var bpp, out var isInt, out var isUInt, out var isDepth))
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }

            long totalBytes = 0;
            for (var lv = 0; lv < levels; lv++)
            {
                var w = Math.Max(1, width >> lv);
                var h = Math.Max(1, height >> lv);
                var d = target == GlTexture3D ? Math.Max(1, depth >> lv) : depth;
                totalBytes += (long)w * h * d * bpp;
            }

            if (!TryReserveResourceBytes(totalBytes - entry.ReservedBytes, out failure))
                return false;
            entry.ReservedBytes = totalBytes;
            entry.Immutable = true;
            entry.ImmutableLevels = levels;
            entry.InternalFormat = internalFormat;
            entry.IsInteger = isInt;
            entry.IsUnsigned = isUInt;
            entry.IsDepth = isDepth;
            entry.Slices.Clear();

            for (var lv = 0; lv < levels; lv++)
            {
                var w = Math.Max(1, width >> lv);
                var h = Math.Max(1, height >> lv);
                var d = target == GlTexture3D ? Math.Max(1, depth >> lv) : depth;
                entry.Slices[(0, lv)] = new TextureImageSlice
                {
                    Width = w,
                    Height = h,
                    Depth = d,
                    InternalFormat = internalFormat,
                    Format = isInt ? GlRgbaInteger : GlRgba,
                    Type = isUInt ? GlUnsignedByte : isInt ? GlByte : GlUnsignedByte,
                    Defined = true,
                    FloatTexels = new float[w * h * d * 4],
                    IntTexels = isInt && !isUInt ? new int[w * h * d * 4] : [],
                    UIntTexels = isUInt ? new uint[w * h * d * 4] : [],
                };
            }
            entry.D3DDirty = true;
            return true;
        }
    }

    public bool TryTextureSubImage3D(
        uint target, int level, int xOffset, int yOffset, int zOffset, int width, int height, int depth, uint format, uint type,
        ReadOnlySpan<byte> pixels, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            ref var slot = ref GetBoundTextureSlot(target, out var valid);
            if (!valid || slot is not uint id || !_textures.TryGetValue(id, out var entry))
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            if (!entry.Slices.TryGetValue((0, level), out var slice) || !slice.Defined)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            if (xOffset < 0 || yOffset < 0 || zOffset < 0 || width < 0 || height < 0 || depth < 0 ||
                xOffset + width > slice.Width || yOffset + height > slice.Height || zOffset + depth > slice.Depth)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            if (width == 0 || height == 0 || depth == 0 || pixels.IsEmpty) return true;

            var temp = new TextureImageSlice
            {
                Width = width,
                Height = height,
                Depth = depth,
                InternalFormat = slice.InternalFormat,
                Format = format,
                Type = type,
                Defined = true,
                FloatTexels = new float[width * height * depth * 4],
                IntTexels = entry.IsInteger && !entry.IsUnsigned ? new int[width * height * depth * 4] : [],
                UIntTexels = entry.IsUnsigned ? new uint[width * height * depth * 4] : [],
            };
            UnpackTexels3D(pixels, width, height, depth, slice.InternalFormat, format, type, entry.IsInteger, entry.IsUnsigned, temp);

            for (var z = 0; z < depth; z++)
            {
                for (var r = 0; r < height; r++)
                {
                    for (var c = 0; c < width; c++)
                    {
                        var srcIdx = ((z * height + r) * width + c) * 4;
                        var dstIdx = (((zOffset + z) * slice.Height + (yOffset + r)) * slice.Width + (xOffset + c)) * 4;
                        for (var k = 0; k < 4; k++)
                        {
                            slice.FloatTexels[dstIdx + k] = temp.FloatTexels[srcIdx + k];
                            if (slice.IntTexels.Length > 0) slice.IntTexels[dstIdx + k] = temp.IntTexels[srcIdx + k];
                            if (slice.UIntTexels.Length > 0) slice.UIntTexels[dstIdx + k] = temp.UIntTexels[srcIdx + k];
                        }
                    }
                }
            }
            entry.D3DDirty = true;
            return true;
        }
    }

    public bool TryGenerateMipmap(uint target, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            ref var slot = ref GetBoundTextureSlot(target, out var valid);
            if (!valid || slot is not uint id || !_textures.TryGetValue(id, out var entry))
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            if (entry.IsInteger || entry.IsDepth)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            var faces = target == GlTextureCubeMap ? 6 : 1;
            for (var f = 0; f < faces; f++)
            {
                if (!entry.Slices.TryGetValue((f, 0), out var baseSlice) || !baseSlice.Defined || baseSlice.Width <= 0 || baseSlice.Height <= 0)
                {
                    RecordGlError(GlInvalidOperation);
                    return true;
                }
                if (_options.Version == WebGlContextVersion.WebGl1 &&
                    (!IsPowerOfTwo(baseSlice.Width) || !IsPowerOfTwo(baseSlice.Height)))
                {
                    RecordGlError(GlInvalidOperation);
                    return true;
                }
            }

            if (!entry.Immutable)
            {
                long additionalBytes = 0;
                for (var f = 0; f < faces; f++)
                {
                    var baseSlice = entry.Slices[(f, 0)];
                    ClassifyInternalFormat(baseSlice.InternalFormat, baseSlice.Format, baseSlice.Type, out var bpp, out _, out _, out _);
                    var maxDim = entry.Target == GlTexture3D
                        ? Math.Max(baseSlice.Width, Math.Max(baseSlice.Height, baseSlice.Depth))
                        : Math.Max(baseSlice.Width, baseSlice.Height);
                    var levels = (int)Math.Floor(Math.Log2(maxDim)) + 1;
                    for (var lv = 1; lv < levels; lv++)
                    {
                        var w = Math.Max(1, baseSlice.Width >> lv);
                        var h = Math.Max(1, baseSlice.Height >> lv);
                        var d = entry.Target == GlTexture3D ? Math.Max(1, baseSlice.Depth >> lv) : baseSlice.Depth;
                        long oldSliceBytes = 0;
                        if (entry.Slices.TryGetValue((f, lv), out var oldSlice))
                        {
                            ClassifyInternalFormat(oldSlice.InternalFormat, oldSlice.Format, oldSlice.Type, out var oldBpp, out _, out _, out _);
                            oldSliceBytes = (long)oldSlice.Width * oldSlice.Height * oldSlice.Depth * oldBpp;
                        }
                        additionalBytes += (long)w * h * d * bpp - oldSliceBytes;
                    }
                }
                if (!TryReserveResourceBytes(additionalBytes, out failure))
                    return false;
                entry.ReservedBytes += additionalBytes;
            }

            for (var f = 0; f < faces; f++)
            {
                var prev = entry.Slices[(f, 0)];
                var maxDim = entry.Target == GlTexture3D
                    ? Math.Max(prev.Width, Math.Max(prev.Height, prev.Depth))
                    : Math.Max(prev.Width, prev.Height);
                var levels = (int)Math.Floor(Math.Log2(maxDim)) + 1;
                for (var lv = 1; lv < levels; lv++)
                {
                    var w = Math.Max(1, prev.Width >> 1);
                    var h = Math.Max(1, prev.Height >> 1);
                    var d = entry.Target == GlTexture3D ? Math.Max(1, prev.Depth >> 1) : prev.Depth;
                    var next = new TextureImageSlice
                    {
                        Width = w,
                        Height = h,
                        Depth = d,
                        InternalFormat = prev.InternalFormat,
                        Format = prev.Format,
                        Type = prev.Type,
                        Defined = true,
                        FloatTexels = new float[w * h * d * 4],
                    };
                    DownsampleBoxFilter(prev, next, entry.Target == GlTexture3D);
                    entry.Slices[(f, lv)] = next;
                    prev = next;
                }
            }
            entry.D3DDirty = true;
            return true;
        }
    }

    private static bool IsPowerOfTwo(int v) => v > 0 && (v & (v - 1)) == 0;

    private static void DownsampleBoxFilter(TextureImageSlice src, TextureImageSlice dst, bool is3D)
    {
        for (var z = 0; z < dst.Depth; z++)
        {
            for (var y = 0; y < dst.Height; y++)
            {
                for (var x = 0; x < dst.Width; x++)
                {
                    var sx0 = Math.Min(src.Width - 1, x * 2);
                    var sx1 = Math.Min(src.Width - 1, x * 2 + 1);
                    var sy0 = Math.Min(src.Height - 1, y * 2);
                    var sy1 = Math.Min(src.Height - 1, y * 2 + 1);
                    var sz0 = is3D ? Math.Min(src.Depth - 1, z * 2) : z;
                    var sz1 = is3D ? Math.Min(src.Depth - 1, z * 2 + 1) : z;

                    var dstBase = ((z * dst.Height + y) * dst.Width + x) * 4;
                    for (var c = 0; c < 4; c++)
                    {
                        var sum =
                            src.FloatTexels[((sz0 * src.Height + sy0) * src.Width + sx0) * 4 + c] +
                            src.FloatTexels[((sz0 * src.Height + sy0) * src.Width + sx1) * 4 + c] +
                            src.FloatTexels[((sz0 * src.Height + sy1) * src.Width + sx0) * 4 + c] +
                            src.FloatTexels[((sz0 * src.Height + sy1) * src.Width + sx1) * 4 + c] +
                            src.FloatTexels[((sz1 * src.Height + sy0) * src.Width + sx0) * 4 + c] +
                            src.FloatTexels[((sz1 * src.Height + sy0) * src.Width + sx1) * 4 + c] +
                            src.FloatTexels[((sz1 * src.Height + sy1) * src.Width + sx0) * 4 + c] +
                            src.FloatTexels[((sz1 * src.Height + sy1) * src.Width + sx1) * 4 + c];
                        dst.FloatTexels[dstBase + c] = sum * 0.125f;
                    }
                }
            }
        }
    }

    private void UnpackTexels2D(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        uint internalFormat,
        uint format,
        uint type,
        bool isInt,
        bool isUInt,
        TextureImageSlice slice)
    {
        var pixelBytes = GetSourcePixelByteSize(format, type);
        var rowPixels = _unpackRowLength > 0 ? _unpackRowLength : width;
        var rawRowBytes = rowPixels * pixelBytes;
        var align = Math.Max(1, _unpackAlignment);
        var alignedRowStride = ((rawRowBytes + align - 1) / align) * align;
        var baseByteOffset = _unpackSkipRows * alignedRowStride + _unpackSkipPixels * pixelBytes;

        for (var r = 0; r < height; r++)
        {
            var dstRow = _unpackFlipY ? (height - 1 - r) : r;
            var rowStart = baseByteOffset + r * alignedRowStride;
            for (var c = 0; c < width; c++)
            {
                var pxStart = rowStart + c * pixelBytes;
                if (pxStart + pixelBytes > pixels.Length) break;
                var pxSpan = pixels.Slice(pxStart, pixelBytes);
                var dstIdx = (dstRow * width + c) * 4;
                DecodeOnePixel(pxSpan, internalFormat, format, type, isInt, isUInt, _unpackPremultiplyAlpha, slice, dstIdx);
            }
        }
    }

    private void UnpackTexels3D(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int depth,
        uint internalFormat,
        uint format,
        uint type,
        bool isInt,
        bool isUInt,
        TextureImageSlice slice)
    {
        var pixelBytes = GetSourcePixelByteSize(format, type);
        var rowPixels = _unpackRowLength > 0 ? _unpackRowLength : width;
        var imgRows = _unpackImageHeight > 0 ? _unpackImageHeight : height;
        var rawRowBytes = rowPixels * pixelBytes;
        var align = Math.Max(1, _unpackAlignment);
        var alignedRowStride = ((rawRowBytes + align - 1) / align) * align;
        var imageStride = alignedRowStride * imgRows;
        var baseOffset = _unpackSkipImages * imageStride + _unpackSkipRows * alignedRowStride + _unpackSkipPixels * pixelBytes;

        for (var z = 0; z < depth; z++)
        {
            var zStart = baseOffset + z * imageStride;
            for (var r = 0; r < height; r++)
            {
                var dstRow = _unpackFlipY ? (height - 1 - r) : r;
                var rowStart = zStart + r * alignedRowStride;
                for (var c = 0; c < width; c++)
                {
                    var pxStart = rowStart + c * pixelBytes;
                    if (pxStart + pixelBytes > pixels.Length) break;
                    var pxSpan = pixels.Slice(pxStart, pixelBytes);
                    var dstIdx = ((z * height + dstRow) * width + c) * 4;
                    DecodeOnePixel(pxSpan, internalFormat, format, type, isInt, isUInt, _unpackPremultiplyAlpha, slice, dstIdx);
                }
            }
        }
    }

    private static int GetSourcePixelByteSize(uint format, uint type)
    {
        if (type is GlUnsignedShort565 or GlUnsignedShort4444 or GlUnsignedShort5551)
            return 2;
        var comps = format switch
        {
            GlAlpha or GlLuminance or GlRed or GlRedInteger or GlDepthComponent or GlStencilIndex8 => 1,
            GlLuminanceAlpha or GlRg or GlRgInteger or GlDepthStencil => 2,
            GlRgb or GlRgbInteger => 3,
            _ => 4,
        };
        var compSize = type switch
        {
            GlByte or GlUnsignedByte => 1,
            GlShort or GlUnsignedShort or GlHalfFloat or GlHalfFloatOes => 2,
            GlInt or GlUnsignedInt or GlFloat => 4,
            _ => 1,
        };
        return comps * compSize;
    }

    private static void DecodeOnePixel(
        ReadOnlySpan<byte> src,
        uint internalFormat,
        uint format,
        uint type,
        bool isInt,
        bool isUInt,
        bool premultiplyAlpha,
        TextureImageSlice slice,
        int dstIdx)
    {
        if (isUInt)
        {
            uint r = 0, g = 0, b = 0, a = 1;
            var comps = format switch { GlRedInteger => 1, GlRgInteger => 2, GlRgbInteger => 3, _ => 4 };
            for (var i = 0; i < comps; i++)
            {
                var v = type switch
                {
                    GlUnsignedByte => (uint)src[i],
                    GlUnsignedShort => BinaryPrimitives.ReadUInt16LittleEndian(src[(i * 2)..]),
                    GlUnsignedInt => BinaryPrimitives.ReadUInt32LittleEndian(src[(i * 4)..]),
                    _ => src[i],
                };
                if (i == 0) r = v; else if (i == 1) g = v; else if (i == 2) b = v; else a = v;
            }
            slice.UIntTexels[dstIdx] = r;
            slice.UIntTexels[dstIdx + 1] = g;
            slice.UIntTexels[dstIdx + 2] = b;
            slice.UIntTexels[dstIdx + 3] = a;
            slice.FloatTexels[dstIdx] = r;
            slice.FloatTexels[dstIdx + 1] = g;
            slice.FloatTexels[dstIdx + 2] = b;
            slice.FloatTexels[dstIdx + 3] = a;
            return;
        }

        if (isInt)
        {
            int r = 0, g = 0, b = 0, a = 1;
            var comps = format switch { GlRedInteger => 1, GlRgInteger => 2, GlRgbInteger => 3, _ => 4 };
            for (var i = 0; i < comps; i++)
            {
                var v = type switch
                {
                    GlByte => (sbyte)src[i],
                    GlShort => BinaryPrimitives.ReadInt16LittleEndian(src[(i * 2)..]),
                    GlInt => BinaryPrimitives.ReadInt32LittleEndian(src[(i * 4)..]),
                    _ => (sbyte)src[i],
                };
                if (i == 0) r = v; else if (i == 1) g = v; else if (i == 2) b = v; else a = v;
            }
            slice.IntTexels[dstIdx] = r;
            slice.IntTexels[dstIdx + 1] = g;
            slice.IntTexels[dstIdx + 2] = b;
            slice.IntTexels[dstIdx + 3] = a;
            slice.FloatTexels[dstIdx] = r;
            slice.FloatTexels[dstIdx + 1] = g;
            slice.FloatTexels[dstIdx + 2] = b;
            slice.FloatTexels[dstIdx + 3] = a;
            return;
        }

        float rf = 0f, gf = 0f, bf = 0f, af = 1f;
        if (type == GlUnsignedShort565)
        {
            var packed = BinaryPrimitives.ReadUInt16LittleEndian(src);
            rf = ((packed >> 11) & 0x1F) / 31f;
            gf = ((packed >> 5) & 0x3F) / 63f;
            bf = (packed & 0x1F) / 31f;
            af = 1f;
        }
        else if (type == GlUnsignedShort4444)
        {
            var packed = BinaryPrimitives.ReadUInt16LittleEndian(src);
            rf = ((packed >> 12) & 0xF) / 15f;
            gf = ((packed >> 8) & 0xF) / 15f;
            bf = ((packed >> 4) & 0xF) / 15f;
            af = (packed & 0xF) / 15f;
        }
        else if (type == GlUnsignedShort5551)
        {
            var packed = BinaryPrimitives.ReadUInt16LittleEndian(src);
            rf = ((packed >> 11) & 0x1F) / 31f;
            gf = ((packed >> 6) & 0x1F) / 31f;
            bf = ((packed >> 1) & 0x1F) / 31f;
            af = (packed & 0x1) != 0 ? 1f : 0f;
        }
        else
        {
            var comps = format switch
            {
                GlAlpha or GlLuminance or GlRed or GlDepthComponent => 1,
                GlLuminanceAlpha or GlRg or GlDepthStencil => 2,
                GlRgb => 3,
                _ => 4,
            };
            Span<float> c = stackalloc float[4];
            c[0] = 0f; c[1] = 0f; c[2] = 0f; c[3] = 1f;
            for (var i = 0; i < comps; i++)
            {
                c[i] = type switch
                {
                    GlUnsignedByte => src[i] / 255f,
                    GlByte => Math.Max(-1f, (sbyte)src[i] / 127f),
                    GlUnsignedShort => BinaryPrimitives.ReadUInt16LittleEndian(src[(i * 2)..]) / 65535f,
                    GlShort => Math.Max(-1f, BinaryPrimitives.ReadInt16LittleEndian(src[(i * 2)..]) / 32767f),
                    GlHalfFloat or GlHalfFloatOes => (float)BinaryPrimitives.ReadHalfLittleEndian(src[(i * 2)..]),
                    GlFloat => BinaryPrimitives.ReadSingleLittleEndian(src[(i * 4)..]),
                    GlUnsignedInt => BinaryPrimitives.ReadUInt32LittleEndian(src[(i * 4)..]) / (float)uint.MaxValue,
                    _ => src[i] / 255f,
                };
            }
            switch (format)
            {
                case GlAlpha:
                    rf = 0f; gf = 0f; bf = 0f; af = c[0];
                    break;
                case GlLuminance:
                    rf = c[0]; gf = c[0]; bf = c[0]; af = 1f;
                    break;
                case GlLuminanceAlpha:
                    rf = c[0]; gf = c[0]; bf = c[0]; af = c[1];
                    break;
                case GlRed or GlDepthComponent:
                    rf = c[0]; gf = 0f; bf = 0f; af = 1f;
                    break;
                case GlRg:
                    rf = c[0]; gf = c[1]; bf = 0f; af = 1f;
                    break;
                case GlRgb:
                    rf = c[0]; gf = c[1]; bf = c[2]; af = 1f;
                    break;
                default:
                    rf = c[0]; gf = c[1]; bf = c[2]; af = c[3];
                    break;
            }
        }

        if (premultiplyAlpha && format is GlRgba or GlRgb or GlLuminanceAlpha)
        {
            rf *= af;
            gf *= af;
            bf *= af;
        }

        slice.FloatTexels[dstIdx] = rf;
        slice.FloatTexels[dstIdx + 1] = gf;
        slice.FloatTexels[dstIdx + 2] = bf;
        slice.FloatTexels[dstIdx + 3] = af;
    }

    internal IntPtr EnsureTextureSrv(TextureEntry tex)
    {
        if (!tex.D3DDirty && tex.D3DSrv != IntPtr.Zero)
            return tex.D3DSrv;

        tex.ReleaseD3D();
        if (!tex.Slices.TryGetValue((0, tex.BaseLevel), out var baseSlice) && !tex.Slices.TryGetValue((0, 0), out baseSlice))
            return IntPtr.Zero;
        if (!baseSlice.Defined || baseSlice.Width <= 0 || baseSlice.Height <= 0)
            return IntPtr.Zero;

        var dxgiFormat = tex.IsUnsigned
            ? FolioD3D11Interop.DxgiFormatR32G32B32A32UInt
            : tex.IsInteger
                ? FolioD3D11Interop.DxgiFormatR32G32B32A32SInt
                : FolioD3D11Interop.DxgiFormatR32G32B32A32Float;

        var usesMips = (uint)tex.MinFilter is GlNearestMipmapNearest or GlLinearMipmapNearest or GlNearestMipmapLinear or GlLinearMipmapLinear;
        var maxPossible = (int)Math.Floor(Math.Log2(Math.Max(baseSlice.Width, Math.Max(baseSlice.Height, baseSlice.Depth)))) + 1;
        var mipCount = 1;
        if (usesMips)
        {
            while (mipCount < maxPossible && mipCount <= tex.MaxLevel && tex.Slices.ContainsKey((0, mipCount)))
                mipCount++;
        }

        if (tex.Target == GlTexture3D)
        {
            var desc3D = new FolioD3D11Interop.D3D11Texture3DDesc
            {
                Width = (uint)baseSlice.Width,
                Height = (uint)baseSlice.Height,
                Depth = (uint)baseSlice.Depth,
                MipLevels = (uint)mipCount,
                Format = dxgiFormat,
                Usage = FolioD3D11Interop.D3D11UsageDefault,
                BindFlags = FolioD3D11Interop.D3D11BindShaderResource,
                CPUAccessFlags = 0,
                MiscFlags = 0,
            };
            var hr = _createTexture3D(_device.DevicePtr, ref desc3D, IntPtr.Zero, out tex.D3DResource);
            if (hr < 0 || tex.D3DResource == IntPtr.Zero) return IntPtr.Zero;

            for (var lv = 0; lv < mipCount; lv++)
            {
                if (!tex.Slices.TryGetValue((0, lv), out var s)) continue;
                UploadSliceSubresource(tex.D3DResource, (uint)lv, s, tex.IsInteger, tex.IsUnsigned);
            }

            hr = _createSrv(_device.DevicePtr, tex.D3DResource, IntPtr.Zero, out tex.D3DSrv);
            if (hr < 0) return IntPtr.Zero;
            tex.D3DDirty = false;
            return tex.D3DSrv;
        }

        var isCube = tex.Target == GlTextureCubeMap;
        var isArray = tex.Target == GlTexture2DArray;
        var arraySize = isCube ? 6 : isArray ? Math.Max(1, baseSlice.Depth) : 1;

        var desc2D = new FolioD3D11Interop.D3D11Texture2DDesc
        {
            Width = (uint)baseSlice.Width,
            Height = (uint)baseSlice.Height,
            MipLevels = (uint)mipCount,
            ArraySize = (uint)arraySize,
            Format = dxgiFormat,
            SampleDesc = new FolioD3D11Interop.DxgiSampleDesc { Count = 1, Quality = 0 },
            Usage = FolioD3D11Interop.D3D11UsageDefault,
            BindFlags = FolioD3D11Interop.D3D11BindShaderResource,
            CPUAccessFlags = 0,
            MiscFlags = isCube ? FolioD3D11Interop.D3D11ResourceMiscTextureCube : 0,
        };

        var hr2D = _createTexture2D(_device.DevicePtr, ref desc2D, IntPtr.Zero, out tex.D3DResource);
        if (hr2D < 0 || tex.D3DResource == IntPtr.Zero) return IntPtr.Zero;

        if (isArray)
        {
            for (var lv = 0; lv < mipCount; lv++)
            {
                if (!tex.Slices.TryGetValue((0, lv), out var s)) continue;
                var layerFloats = s.Width * s.Height * 4;
                for (var layer = 0; layer < s.Depth; layer++)
                {
                    var subIdx = (uint)(layer * mipCount + lv);
                    UploadArrayLayerSubresource(tex.D3DResource, subIdx, s, layer, layerFloats, tex.IsInteger, tex.IsUnsigned);
                }
            }

            var srvDesc = new FolioD3D11Interop.D3D11ShaderResourceViewDesc
            {
                Format = dxgiFormat,
                ViewDimension = FolioD3D11Interop.D3D11SrvDimensionTexture2DArray,
                Param0 = 0, // MostDetailedMip
                Param1 = (uint)mipCount, // MipLevels
                Param2 = 0, // FirstArraySlice
                Param3 = (uint)arraySize, // ArraySize
            };
            hr2D = _createSrvDesc(_device.DevicePtr, tex.D3DResource, ref srvDesc, out tex.D3DSrv);
        }
        else if (isCube)
        {
            for (var f = 0; f < 6; f++)
            {
                for (var lv = 0; lv < mipCount; lv++)
                {
                    var s = tex.Slices.TryGetValue((f, lv), out var faceSlice) ? faceSlice : baseSlice;
                    var subIdx = (uint)(f * mipCount + lv);
                    UploadSliceSubresource(tex.D3DResource, subIdx, s, tex.IsInteger, tex.IsUnsigned);
                }
            }
            var srvDesc = new FolioD3D11Interop.D3D11ShaderResourceViewDesc
            {
                Format = dxgiFormat,
                ViewDimension = FolioD3D11Interop.D3D11SrvDimensionTextureCube,
                Param0 = 0,
                Param1 = (uint)mipCount,
            };
            hr2D = _createSrvDesc(_device.DevicePtr, tex.D3DResource, ref srvDesc, out tex.D3DSrv);
        }
        else
        {
            for (var lv = 0; lv < mipCount; lv++)
            {
                if (!tex.Slices.TryGetValue((0, lv), out var s)) continue;
                UploadSliceSubresource(tex.D3DResource, (uint)lv, s, tex.IsInteger, tex.IsUnsigned);
            }
            hr2D = _createSrv(_device.DevicePtr, tex.D3DResource, IntPtr.Zero, out tex.D3DSrv);
        }

        if (hr2D < 0) return IntPtr.Zero;
        tex.D3DDirty = false;
        return tex.D3DSrv;
    }

    private void UploadSliceSubresource(IntPtr resource, uint subresource, TextureImageSlice s, bool isInt, bool isUInt)
    {
        var rowPitch = (uint)(s.Width * 16);
        var depthPitch = (uint)(s.Width * s.Height * 16);
        if (isUInt && s.UIntTexels.Length > 0)
        {
            var h = GCHandle.Alloc(s.UIntTexels, GCHandleType.Pinned);
            try { _updateSubresourceFull(_device.ContextPtr, resource, subresource, IntPtr.Zero, h.AddrOfPinnedObject(), rowPitch, depthPitch); }
            finally { h.Free(); }
        }
        else if (isInt && s.IntTexels.Length > 0)
        {
            var h = GCHandle.Alloc(s.IntTexels, GCHandleType.Pinned);
            try { _updateSubresourceFull(_device.ContextPtr, resource, subresource, IntPtr.Zero, h.AddrOfPinnedObject(), rowPitch, depthPitch); }
            finally { h.Free(); }
        }
        else
        {
            var h = GCHandle.Alloc(s.FloatTexels, GCHandleType.Pinned);
            try { _updateSubresourceFull(_device.ContextPtr, resource, subresource, IntPtr.Zero, h.AddrOfPinnedObject(), rowPitch, depthPitch); }
            finally { h.Free(); }
        }
    }

    private void UploadArrayLayerSubresource(IntPtr resource, uint subresource, TextureImageSlice s, int layer, int layerFloats, bool isInt, bool isUInt)
    {
        var rowPitch = (uint)(s.Width * 16);
        var byteOffset = layer * layerFloats * 4;
        if (isUInt && s.UIntTexels.Length > 0)
        {
            var h = GCHandle.Alloc(s.UIntTexels, GCHandleType.Pinned);
            try { _updateSubresourceFull(_device.ContextPtr, resource, subresource, IntPtr.Zero, IntPtr.Add(h.AddrOfPinnedObject(), byteOffset), rowPitch, 0); }
            finally { h.Free(); }
        }
        else if (isInt && s.IntTexels.Length > 0)
        {
            var h = GCHandle.Alloc(s.IntTexels, GCHandleType.Pinned);
            try { _updateSubresourceFull(_device.ContextPtr, resource, subresource, IntPtr.Zero, IntPtr.Add(h.AddrOfPinnedObject(), byteOffset), rowPitch, 0); }
            finally { h.Free(); }
        }
        else
        {
            var h = GCHandle.Alloc(s.FloatTexels, GCHandleType.Pinned);
            try { _updateSubresourceFull(_device.ContextPtr, resource, subresource, IntPtr.Zero, IntPtr.Add(h.AddrOfPinnedObject(), byteOffset), rowPitch, 0); }
            finally { h.Free(); }
        }
    }

    internal IntPtr EnsureTextureSampler(TextureEntry tex)
    {
        if (!tex.SamplerDirty && tex.D3DSampler != IntPtr.Zero)
            return tex.D3DSampler;

        tex.ReleaseSampler();
        var magLinear = (uint)tex.MagFilter == GlLinear && !tex.IsInteger;
        var minLinear = (uint)tex.MinFilter is GlLinear or GlLinearMipmapNearest or GlLinearMipmapLinear && !tex.IsInteger;
        var mipLinear = (uint)tex.MinFilter is GlNearestMipmapLinear or GlLinearMipmapLinear && !tex.IsInteger;

        var filter = (minLinear ? 0x10 : 0) | (magLinear ? 0x04 : 0) | (mipLinear ? 0x01 : 0);
        static int MapWrap(int w) => (uint)w switch
        {
            GlRepeat => 1, // D3D11_TEXTURE_ADDRESS_WRAP
            GlMirroredRepeat => 2, // D3D11_TEXTURE_ADDRESS_MIRROR
            _ => 3, // D3D11_TEXTURE_ADDRESS_CLAMP
        };

        var desc = new FolioD3D11Interop.D3D11SamplerDesc
        {
            Filter = filter,
            AddressU = MapWrap(tex.WrapS),
            AddressV = MapWrap(tex.WrapT),
            AddressW = MapWrap(tex.WrapR),
            MipLODBias = 0f,
            MaxAnisotropy = 1,
            ComparisonFunc = 8, // ALWAYS
            MinLOD = tex.MinLod,
            MaxLOD = tex.MaxLod,
        };
        var hr = _createSamplerState(_device.DevicePtr, ref desc, out tex.D3DSampler);
        if (hr < 0) return IntPtr.Zero;
        tex.SamplerDirty = false;
        return tex.D3DSampler;
    }
}
