using System.Runtime.InteropServices;

namespace Folio.Scripting.Gpu;

internal sealed partial class AngleNativeWebGlContext
{
    public bool TryCreateTexture(out WebGlTextureHandle texture, out GpuFailure? failure)
    {
        lock (_gate)
        {
            texture = default; failure = null;
            if (!Begin(out failure)) return false;
            try
            {
                _gl.GenTextures(1, out var value);
                if (value == 0) { failure = NativeFailure("glGenTextures returned zero."); return false; }
                _textures.Add(value); _textureParameters[value] = new() { [0x2801] = 0x2601, [0x2800] = 0x2601, [0x2802] = 0x812F, [0x2803] = 0x812F }; texture = new(value); return true;
            }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { End(); }
        }
    }

    public bool TryDeleteTexture(WebGlTextureHandle texture, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (!_textures.Remove(texture.Value)) { failure = NativeFailure("The texture handle is invalid for this context."); return false; }
            if (!Begin(out failure)) return false;
            try { var value = texture.Value; _gl.DeleteTextures(1, ref value); if (_textureReservations.Remove(texture.Value, out var reservation)) reservation.Dispose(); _textureSizes.Remove(texture.Value); _textureParameters.Remove(texture.Value); _textureTargets.Remove(texture.Value); foreach (var key in _textureLevels.Keys.Where(key => key.Texture == texture.Value).ToArray()) { _textureLevels.Remove(key); _textureLevelSizes.Remove(key); } return CheckError(out failure); }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { End(); }
        }
    }

    public bool TryBindTexture(uint target, WebGlTextureHandle? texture, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (target is not (0x0DE1 or 0x806F or 0x8C1A or 0x8513)) { failure = NativeFailure("The texture target is invalid.", 0x0500); return false; }
            if (texture is { } value && !_textures.Contains(value.Value)) { failure = NativeFailure("The texture handle is invalid for this context.", 0x0502); return false; }
            if (texture is { } existing && _textureTargets.TryGetValue(existing.Value, out var existingTarget) && existingTarget != target)
            { failure = NativeFailure("A texture cannot be rebound to a different target.", 0x0502); return false; }
            var result = StateCall(() => _gl.BindTexture(target, texture?.Value ?? 0), out failure);
            if (result) { if (texture is { } assigned) _textureTargets[assigned.Value] = target; if (target == 0x0DE1) _boundTexture2D = texture?.Value; else if (target == 0x806F) _boundTexture3D = texture?.Value; else if (target == 0x8C1A) _boundTexture2DArray = texture?.Value; else _boundTextureCube = texture?.Value; }
            return result;
        }
    }

    public bool TryTextureParameter(uint target, uint parameter, int value, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (target is not (0x0DE1 or 0x806F or 0x8C1A or 0x8513)) { failure = NativeFailure("The texture target is invalid.", 0x0500); return false; }
            var result = StateCall(() => _gl.TexParameteri(target, parameter, value), out failure);
            var bound = target == 0x0DE1 ? _boundTexture2D : target == 0x806F ? _boundTexture3D : target == 0x8C1A ? _boundTexture2DArray : _boundTextureCube;
            if (result && bound is { } handle && _textureParameters.TryGetValue(handle, out var parameters)) parameters[parameter] = value;
            return result;
        }
    }

    public bool TryGetTextureParameter(uint target, uint parameter, out WebGlParameterValue value, out GpuFailure? failure)
    {
        lock (_gate)
        {
            value = default; failure = null;
            if (target is not (0x0DE1 or 0x806F or 0x8C1A or 0x8513)) { failure = NativeFailure("The texture target is invalid.", 0x0500); return false; }
            var bound = target == 0x0DE1 ? _boundTexture2D : target == 0x806F ? _boundTexture3D : target == 0x8C1A ? _boundTexture2DArray : _boundTextureCube;
            if (bound is not { } handle || !_textures.Contains(handle)) { failure = NativeFailure("No texture is bound to the requested target.", 0x0502); return false; }
            if (!_textureParameters.TryGetValue(handle, out var parameters) || !parameters.TryGetValue(parameter, out var result)) { failure = NativeFailure("The texture parameter is invalid.", 0x0500); return false; }
            value = new(WebGlParameterKind.Int32, Int32: result); return true;
        }
    }

    public bool TryGetTextureLevelParameter(uint target, int level, uint parameter, out WebGlParameterValue value, out GpuFailure? failure)
    {
        lock (_gate)
        {
            value = default; failure = null;
            if (level < 0 || (target is not (0x0DE1 or 0x806F or 0x8C1A or 0x8513) && target is not (>= 0x8515 and <= 0x851A))) { failure = NativeFailure("The texture target or level is invalid.", 0x0500); return false; }
            var bound = target == 0x0DE1 ? _boundTexture2D : target == 0x806F ? _boundTexture3D : target == 0x8C1A ? _boundTexture2DArray : _boundTextureCube;
            if (bound is not { } handle || !_textures.Contains(handle)) { failure = NativeFailure("No texture is bound to the requested target.", 0x0502); return false; }
            if (!_textureLevels.TryGetValue((handle, target, level), out var image)) { value = new(WebGlParameterKind.Int32, Int32: 0); return true; }
            var result = parameter switch { 0x1000u => image.Width, 0x1001u => image.Height, 0x8071u => image.Depth, 0x1003u => image.InternalFormat, _ => -1 };
            if (result < 0) { failure = NativeFailure("The texture level parameter is invalid.", 0x0500); return false; }
            value = new(WebGlParameterKind.Int32, Int32: result); return true;
        }
    }

    public bool TryTextureImage2D(uint target, int level, int internalFormat, int width, int height, uint format, uint type,
                                  ReadOnlySpan<byte> pixels, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if ((target != 0x0DE1 && target is not (>= 0x8515 and <= 0x851A)) || level < 0 || width < 0 || height < 0 || width > 16384 || height > 16384)
            { failure = NativeFailure("The texture image dimensions or target are invalid.", 0x0501); return false; }
            var depth = format == 0x1902 && type == 0x1403;
            var integer = format == 0x8D99 && type is 0x1400 or 0x1401 or 0x1402 or 0x1403 or 0x1404 or 0x1405;
            var packed = format == 0x1907 && type == 0x8363 || format == 0x1908 && type is 0x8033 or 0x8034;
            if ((!depth && !integer && !packed && format is not (0x1907 or 0x1908)) || (!depth && !integer && !packed && type is not (0x1401 or 0x1406 or 0x140B)))
            { failure = NativeFailure("The texture upload format or type is unsupported by this native path.", 0x0500); return false; }
            byte[]? expanded = null;
            if (packed)
            {
                expanded = ExpandPackedPixels(pixels, type, format == 0x1907 ? 3 : 4, out failure);
                if (expanded is null) return false;
                internalFormat = 0x1908;
                format = 0x1908;
                type = 0x1401;
            }
            var upload = expanded is null ? pixels : expanded.AsSpan();
            var channels = depth ? 1 : 4;
            if (!depth && !integer) channels = format == 0x1908 ? 4 : 3;
            var componentBytes = depth ? 2 : type is 0x1404 or 0x1405 or 0x1406 ? 4 : type is 0x1402 or 0x1403 or 0x140B ? 2 : 1;
            var required = checked((long)width * height * channels * componentBytes);
            if (upload.Length != required) { failure = NativeFailure("The texture upload length is invalid.", 0x0501); return false; }
            var boundCandidate = target == 0x0DE1 ? _boundTexture2D : _boundTextureCube;
            if (boundCandidate is not { } bound || !_textures.Contains(bound)) { failure = NativeFailure("No texture is bound to the requested texture target.", 0x0502); return false; }
            var levelKey = (bound, target, level);
            var oldLevelSize = _textureLevelSizes.TryGetValue(levelKey, out var existingLevelSize) ? existingLevelSize : 0;
            var previousTotal = _textureSizes.TryGetValue(bound, out var currentTotal) ? currentTotal : 0;
            var nextTotal = checked(previousTotal - oldLevelSize + required);
            IDisposable? replacement = null;
            var oldReservation = _textureReservations.GetValueOrDefault(bound);
            if (previousTotal != nextTotal && !_device.TryReserve(nextTotal, out replacement))
            { failure = new(GpuFailureKind.BudgetExceeded, "The GPU budget was exceeded."); return false; }
            if (!Begin(out failure)) { replacement?.Dispose(); return false; }
            GCHandle pin = default;
            try
            {
                var bytes = upload.ToArray();
                pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                _gl.TexImage2D(target, level, internalFormat, width, height, 0, format, type, pin.AddrOfPinnedObject());
                if (!CheckError(out failure)) { replacement?.Dispose(); return false; }
                if (replacement is not null)
                {
                    _textureReservations[bound] = replacement;
                    _textureSizes[bound] = nextTotal;
                    oldReservation?.Dispose();
                }
                _textureLevelSizes[levelKey] = required;
                _textureLevels[levelKey] = (width, height, 1, internalFormat);
                return true;
            }
            catch (Exception error) { replacement?.Dispose(); failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { if (pin.IsAllocated) pin.Free(); End(); }
        }
    }

    private static byte[]? ExpandPackedPixels(ReadOnlySpan<byte> source, uint type, int channels, out GpuFailure? failure)
    {
        failure = null;
        if (source.Length % 2 != 0) { failure = new(GpuFailureKind.NativeError, "The packed texture upload length is invalid."); return null; }
        var result = new byte[checked(source.Length / 2 * 4)];
        for (var i = 0; i < source.Length / 2; i++)
        {
            var value = (ushort)(source[i * 2] | source[i * 2 + 1] << 8);
            var offset = i * 4;
            if (channels == 3)
            {
                result[offset] = (byte)(((value >> 11) & 0x1F) * 255 / 31);
                result[offset + 1] = (byte)(((value >> 5) & 0x3F) * 255 / 63);
                result[offset + 2] = (byte)((value & 0x1F) * 255 / 31);
            }
            else if (type == 0x8033)
            {
                result[offset] = (byte)(((value >> 12) & 0xF) * 17);
                result[offset + 1] = (byte)(((value >> 8) & 0xF) * 17);
                result[offset + 2] = (byte)(((value >> 4) & 0xF) * 17);
                result[offset + 3] = (byte)((value & 0xF) * 17);
            }
            else
            {
                result[offset] = (byte)(((value >> 11) & 0x1F) * 255 / 31);
                result[offset + 1] = (byte)(((value >> 6) & 0x1F) * 255 / 31);
                result[offset + 2] = (byte)(((value >> 1) & 0x1F) * 255 / 31);
                result[offset + 3] = (byte)((value & 1) * 255);
            }
        }
        return result;
    }

    public bool TryTextureStorage2D(uint target, int levels, uint internalFormat, int width, int height, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (Options.Version != WebGlContextVersion.WebGl2 || target is not (0x0DE1 or 0x8513) || levels <= 0 || width <= 0 || height <= 0 || width > 16384 || height > 16384 || (target == 0x8513 && width != height))
            { failure = NativeFailure("The immutable texture storage target, dimensions or levels are invalid.", 0x0501); return false; }
            var bytesPerPixel = internalFormat switch { 0x8058 or 0x8814 => internalFormat == 0x8814 ? 16 : 4, 0x8051 => 3, 0x881A => 8, 0x81A5 => 2, _ => 0 };
            if (bytesPerPixel == 0) { failure = NativeFailure("The immutable texture internal format is unsupported by this native path.", 0x0500); return false; }
            var boundCandidate = target == 0x0DE1 ? _boundTexture2D : _boundTextureCube;
            if (boundCandidate is not { } bound || !_textures.Contains(bound)) { failure = NativeFailure("No texture is bound to the requested target.", 0x0502); return false; }
            if (_textureLevels.Keys.Any(key => key.Texture == bound && key.Target == target)) { failure = NativeFailure("Immutable texture storage has already been allocated.", 0x0502); return false; }
            var faceCount = target == 0x8513 ? 6 : 1;
            long total = 0;
            var w = width; var h = height;
            for (var level = 0; level < levels; level++) { total = checked(total + (long)w * h * bytesPerPixel * faceCount); w = Math.Max(1, w / 2); h = Math.Max(1, h / 2); }
            if (!_device.TryReserve(total, out var reservation)) { failure = new(GpuFailureKind.BudgetExceeded, "The GPU budget was exceeded."); return false; }
            var oldReservation = _textureReservations.GetValueOrDefault(bound);
            var levelMetadata = new List<((uint Texture, uint Target, int Level) Key, (int Width, int Height, int Depth, int InternalFormat) Image, long Size)>();
            w = width; h = height;
            for (var level = 0; level < levels; level++)
            {
                var size = checked((long)w * h * bytesPerPixel);
                if (target == 0x8513)
                    for (uint face = 0x8515; face <= 0x851A; face++) levelMetadata.Add(((bound, face, level), (w, h, 1, (int)internalFormat), size));
                else levelMetadata.Add(((bound, target, level), (w, h, 1, (int)internalFormat), size));
                w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
            }
            if (!StateCall(() => _gl.TexStorage2D(target, levels, internalFormat, width, height), out failure))
            { reservation.Dispose(); return false; }
            _textureReservations[bound] = reservation;
            _textureSizes[bound] = total;
            oldReservation?.Dispose();
            foreach (var entry in levelMetadata)
            {
                _textureLevels[entry.Key] = entry.Image;
                _textureLevelSizes[entry.Key] = entry.Size;
            }
            return true;
        }
    }

    public bool TryGenerateMipmap(uint target, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (target is not (0x0DE1 or 0x806F or 0x8C1A or 0x8513)) { failure = NativeFailure("The texture target is invalid.", 0x0500); return false; }
            var bound = target == 0x0DE1 ? _boundTexture2D : target == 0x806F ? _boundTexture3D : target == 0x8C1A ? _boundTexture2DArray : _boundTextureCube;
            if (bound is not { } value || !_textures.Contains(value)) { failure = NativeFailure("No texture is bound to the requested target.", 0x0502); return false; }

            // Keep Folio's level metadata and GPU budget in step with the native
            // allocation.  Previously GenerateMipmap updated ANGLE only, so
            // getTexLevelParameter() reported zero for generated levels and the
            // document budget under-counted their storage.
            var bases = target == 0x8513
                ? Enumerable.Range(0x8515, 6).Select(face => ((uint)face, _textureLevels.TryGetValue((value, (uint)face, 0), out var baseLevelValue) ? baseLevelValue : default)).ToArray()
                : new[] { (target, _textureLevels.TryGetValue((value, target, 0), out var baseLevelValue) ? baseLevelValue : default) };
            var metadata = new List<((uint Target, int Level) Key, (int Width, int Height, int Depth, int InternalFormat) Value, long Bytes)>();
            foreach (var item in bases)
            {
                var baseLevel = item.Item2;
                if (baseLevel.Width <= 0 || baseLevel.Height <= 0 || baseLevel.Depth <= 0) continue;
                var baseSize = _textureLevelSizes.TryGetValue((value, item.Item1, 0), out var recorded) ? recorded : 0;
                var bytesPerVoxel = baseSize > 0 ? Math.Max(1, baseSize / ((long)baseLevel.Width * baseLevel.Height * baseLevel.Depth)) : 0;
                var width = baseLevel.Width; var height = baseLevel.Height; var depth = baseLevel.Depth;
                for (var level = 1; width > 1 || height > 1 || target == 0x806F && depth > 1; level++)
                {
                    width = Math.Max(1, width / 2);
                    height = Math.Max(1, height / 2);
                    if (target == 0x806F) depth = Math.Max(1, depth / 2);
                    var key = (item.Item1, level);
                    if (!_textureLevelSizes.ContainsKey((value, key.Item1, key.Item2)))
                        metadata.Add((key, (width, height, target == 0x8C1A ? baseLevel.Depth : depth, baseLevel.InternalFormat), checked((long)width * height * (target == 0x8C1A ? baseLevel.Depth : depth) * bytesPerVoxel)));
                }
            }
            var additional = metadata.Sum(item => item.Bytes);
            IDisposable? replacement = null;
            var previousTotal = _textureSizes.TryGetValue(value, out var total) ? total : 0;
            if (additional > 0 && !_device.TryReserve(additional, out replacement!))
            {
                failure = new(GpuFailureKind.BudgetExceeded, "The GPU budget was exceeded by generated mip levels.");
                return false;
            }
            if (!StateCall(() => _gl.GenerateMipmap(target), out failure))
            {
                replacement?.Dispose();
                return false;
            }
            if (replacement is not null)
            {
                _textureReservations.Remove(value, out var oldReservation);
                _textureReservations[value] = oldReservation is null ? replacement : new CombinedReservation(oldReservation, replacement);
                _textureSizes[value] = checked(previousTotal + additional);
                foreach (var item in metadata)
                {
                    _textureLevels[(value, item.Key.Target, item.Key.Level)] = item.Value;
                    _textureLevelSizes[(value, item.Key.Target, item.Key.Level)] = item.Bytes;
                }
            }
            return true;
        }
    }

    private sealed class CombinedReservation(IDisposable first, IDisposable second) : IDisposable
    {
        public void Dispose()
        {
            first.Dispose();
            second.Dispose();
        }
    }

    public bool TryTextureImage3D(uint target, int level, int internalFormat, int width, int height, int depth, uint format, uint type,
                                  ReadOnlySpan<byte> pixels, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (Options.Version != WebGlContextVersion.WebGl2 || target is not (0x806F or 0x8C1A) || level < 0 || width < 0 || height < 0 || depth < 0 || width > 2048 || height > 2048 || depth > 2048)
            { failure = NativeFailure("The 3D texture target, context version or dimensions are invalid.", 0x0501); return false; }
            var integer = format is 0x8D98 or 0x8D99;
            if ((!integer && format is not (0x1907 or 0x1908)) || (integer ? type is not (0x1400 or 0x1401 or 0x1402 or 0x1403 or 0x1404 or 0x1405) : type is not (0x1401 or 0x1406 or 0x140B))) { failure = NativeFailure("The 3D texture upload format or type is unsupported.", 0x0500); return false; }
            var channels = format is 0x1908 or 0x8D99 ? 4 : 3;
            var componentBytes = type is 0x1404 or 0x1405 or 0x1406 ? 4 : type is 0x1402 or 0x1403 or 0x140B ? 2 : 1;
            var required = checked((long)width * height * depth * channels * componentBytes);
            if (pixels.Length != required) { failure = NativeFailure("The 3D texture upload length is invalid.", 0x0501); return false; }
            var bound = target == 0x806F ? _boundTexture3D : _boundTexture2DArray;
            if (bound is not { } value || !_textures.Contains(value)) { failure = NativeFailure("No 3D texture is bound to the requested target.", 0x0502); return false; }
            var levelKey = (value, target, level);
            var oldLevelSize = _textureLevelSizes.TryGetValue(levelKey, out var existingLevelSize) ? existingLevelSize : 0;
            var previousTotal = _textureSizes.TryGetValue(value, out var currentTotal) ? currentTotal : 0;
            var nextTotal = checked(previousTotal - oldLevelSize + required);
            IDisposable? replacement = null;
            var oldReservation = _textureReservations.GetValueOrDefault(value);
            if (previousTotal != nextTotal && !_device.TryReserve(nextTotal, out replacement))
            { failure = new(GpuFailureKind.BudgetExceeded, "The GPU budget was exceeded."); return false; }
            if (!Begin(out failure)) { replacement?.Dispose(); return false; }
            GCHandle pin = default;
            try
            {
                var bytes = pixels.ToArray(); pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                _gl.TexImage3D(target, level, internalFormat, width, height, depth, 0, format, type, pin.AddrOfPinnedObject());
                if (!CheckError(out failure)) { replacement?.Dispose(); return false; }
                if (replacement is not null)
                {
                    _textureReservations[value] = replacement;
                    _textureSizes[value] = nextTotal;
                    oldReservation?.Dispose();
                }
                _textureLevelSizes[levelKey] = required;
                _textureLevels[levelKey] = (width, height, depth, internalFormat);
                return true;
            }
            catch (Exception error) { replacement?.Dispose(); failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { if (pin.IsAllocated) pin.Free(); End(); }
        }
    }

    public bool TryTextureStorage3D(uint target, int levels, uint internalFormat, int width, int height, int depth, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (Options.Version != WebGlContextVersion.WebGl2 || target is not (0x806F or 0x8C1A) || levels <= 0 || width <= 0 || height <= 0 || depth <= 0 || width > 2048 || height > 2048 || depth > 2048)
            { failure = NativeFailure("The immutable 3D texture storage target, dimensions or levels are invalid.", 0x0501); return false; }
            var bytesPerPixel = internalFormat switch
            {
                0x8058 or 0x8814 or 0x8D7C or 0x8D70 => internalFormat is 0x8814 or 0x8D70 ? 16 : 4,
                0x8051 or 0x8D7D => 3,
                0x881A or 0x8D77 => 8,
                0x81A5 or 0x8D76 => 2,
                _ => 0
            };
            if (bytesPerPixel == 0) { failure = NativeFailure("The immutable 3D texture internal format is unsupported by this native path.", 0x0500); return false; }
            var bound = target == 0x806F ? _boundTexture3D : _boundTexture2DArray;
            if (bound is not { } value || !_textures.Contains(value)) { failure = NativeFailure("No 3D texture is bound to the requested target.", 0x0502); return false; }
            if (_textureLevels.Keys.Any(key => key.Texture == value && key.Target == target)) { failure = NativeFailure("Immutable texture storage has already been allocated.", 0x0502); return false; }
            long total = 0; var w = width; var h = height; var d = depth;
            for (var level = 0; level < levels; level++) { total = checked(total + (long)w * h * d * bytesPerPixel); w = Math.Max(1, w / 2); h = Math.Max(1, h / 2); if (target == 0x806F) d = Math.Max(1, d / 2); }
            if (!_device.TryReserve(total, out var replacement)) { failure = new(GpuFailureKind.BudgetExceeded, "The GPU budget was exceeded."); return false; }
            var oldReservation = _textureReservations.GetValueOrDefault(value);
            var levelMetadata = new List<((uint Texture, uint Target, int Level) Key, (int Width, int Height, int Depth, int InternalFormat) Image, long Size)>();
            w = width; h = height; d = depth;
            for (var level = 0; level < levels; level++)
            {
                var size = checked((long)w * h * d * bytesPerPixel);
                levelMetadata.Add(((value, target, level), (w, h, d, (int)internalFormat), size));
                w = Math.Max(1, w / 2); h = Math.Max(1, h / 2); if (target == 0x806F) d = Math.Max(1, d / 2);
            }
            if (!StateCall(() => _gl.TexStorage3D(target, levels, internalFormat, width, height, depth), out failure))
            { replacement.Dispose(); return false; }
            _textureReservations[value] = replacement;
            _textureSizes[value] = total;
            oldReservation?.Dispose();
            foreach (var entry in levelMetadata)
            {
                _textureLevels[entry.Key] = entry.Image;
                _textureLevelSizes[entry.Key] = entry.Size;
            }
            return true;
        }
    }

    public bool TryTextureSubImage2D(uint target, int level, int xOffset, int yOffset, int width, int height, uint format, uint type,
                                     ReadOnlySpan<byte> pixels, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if ((target != 0x0DE1 && target is not (>= 0x8515 and <= 0x851A)) || level < 0 || xOffset < 0 || yOffset < 0 || width < 0 || height < 0)
            { failure = NativeFailure("The texture subimage dimensions or target are invalid.", 0x0501); return false; }
            var boundCandidate = target == 0x0DE1 ? _boundTexture2D : _boundTextureCube;
            if (boundCandidate is not { } bound || !_textures.Contains(bound))
            { failure = NativeFailure("No texture is bound to the requested texture target.", 0x0502); return false; }
            if (!_textureLevels.TryGetValue((bound, target, level), out var image) || xOffset > image.Width - width || yOffset > image.Height - height)
            { failure = NativeFailure("The texture subimage is outside the allocated level.", 0x0501); return false; }
            var depth = format == 0x1902 && type == 0x1403;
            var integer = format == 0x8D99 && type == 0x1401;
            var packed = format == 0x1907 && type == 0x8363 || format == 0x1908 && type is 0x8033 or 0x8034;
            if ((!depth && !integer && !packed && format is not (0x1907 or 0x1908)) || (!depth && !integer && !packed && type is not (0x1401 or 0x1406 or 0x140B)))
            { failure = NativeFailure("The texture subimage format or type is unsupported by this native path.", 0x0500); return false; }
            byte[]? expanded = null;
            if (packed)
            {
                expanded = ExpandPackedPixels(pixels, type, format == 0x1907 ? 3 : 4, out failure);
                if (expanded is null) return false;
                format = 0x1908;
                type = 0x1401;
            }
            var upload = expanded is null ? pixels : expanded.AsSpan();
            var channels = depth ? 1 : 4;
            if (!depth && !integer) channels = format == 0x1908 ? 4 : 3;
            var componentBytes = depth ? 2 : type == 0x1406 ? 4 : type == 0x140B ? 2 : 1;
            var required = checked((long)width * height * channels * componentBytes);
            if (upload.Length != required) { failure = NativeFailure("The texture subimage length is invalid.", 0x0501); return false; }
            if (!Begin(out failure)) return false;
            GCHandle pin = default;
            try
            {
                var bytes = upload.ToArray(); pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                _gl.TexSubImage2D(target, level, xOffset, yOffset, width, height, format, type, pin.AddrOfPinnedObject());
                return CheckError(out failure);
            }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { if (pin.IsAllocated) pin.Free(); End(); }
        }
    }

    public bool TryCopyTextureImage2D(uint target, int level, int internalFormat, int x, int y, int width, int height, int border, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if ((target != 0x0DE1 && target is not (>= 0x8515 and <= 0x851A)) || level < 0 || border != 0 || width <= 0 || height <= 0)
            { failure = NativeFailure("The copied texture image dimensions or target are invalid.", 0x0501); return false; }
            var bytesPerPixel = internalFormat is 0x1907 or 0x8051 ? 3 : internalFormat is 0x1908 or 0x8058 ? 4 : 0;
            if (bytesPerPixel == 0) { failure = NativeFailure("The copied texture internal format is unsupported.", 0x0500); return false; }
            var boundCandidate = target == 0x0DE1 ? _boundTexture2D : _boundTextureCube;
            if (boundCandidate is not { } bound || !_textures.Contains(bound)) { failure = NativeFailure("No texture is bound to the requested target.", 0x0502); return false; }
            var required = checked((long)width * height * bytesPerPixel); var key = (bound, target, level);
            var oldLevelSize = _textureLevelSizes.TryGetValue(key, out var existingLevelSize) ? existingLevelSize : 0;
            var previousTotal = _textureSizes.TryGetValue(bound, out var currentTotal) ? currentTotal : 0;
            var nextTotal = checked(previousTotal - oldLevelSize + required);
            IDisposable? replacement = null;
            var oldReservation = _textureReservations.GetValueOrDefault(bound);
            if (previousTotal != nextTotal && !_device.TryReserve(nextTotal, out replacement))
            { failure = new(GpuFailureKind.BudgetExceeded, "The GPU budget was exceeded."); return false; }
            if (!Begin(out failure)) { replacement?.Dispose(); return false; }
            try
            {
                _gl.CopyTexImage2D(target, level, internalFormat, x, y, width, height, 0);
                if (!CheckError(out failure)) { replacement?.Dispose(); return false; }
                if (replacement is not null)
                {
                    _textureReservations[bound] = replacement;
                    _textureSizes[bound] = nextTotal;
                    oldReservation?.Dispose();
                }
                _textureLevelSizes[key] = required;
                _textureLevels[key] = (width, height, 1, internalFormat);
                return true;
            }
            catch (Exception error) { replacement?.Dispose(); failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { End(); }
        }
    }

    public bool TryCopyTextureSubImage2D(uint target, int level, int xOffset, int yOffset, int x, int y, int width, int height, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if ((target != 0x0DE1 && target is not (>= 0x8515 and <= 0x851A)) || level < 0 || xOffset < 0 || yOffset < 0 || width < 0 || height < 0)
            { failure = NativeFailure("The copied texture subimage dimensions or target are invalid.", 0x0501); return false; }
            var boundCandidate = target == 0x0DE1 ? _boundTexture2D : _boundTextureCube;
            if (boundCandidate is not { } bound || !_textures.Contains(bound)) { failure = NativeFailure("No texture is bound to the requested target.", 0x0502); return false; }
            if (!_textureLevels.TryGetValue((bound, target, level), out var image) || xOffset > image.Width - width || yOffset > image.Height - height)
            { failure = NativeFailure("The copied texture subimage is outside the allocated level.", 0x0501); return false; }
            if (!Begin(out failure)) return false;
            try { _gl.CopyTexSubImage2D(target, level, xOffset, yOffset, x, y, width, height); return CheckError(out failure); }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { End(); }
        }
    }

    public bool TryTextureSubImage3D(uint target, int level, int xOffset, int yOffset, int zOffset, int width, int height, int depth, uint format, uint type,
                                     ReadOnlySpan<byte> pixels, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (Options.Version != WebGlContextVersion.WebGl2 || target is not (0x806F or 0x8C1A) || level < 0 || xOffset < 0 || yOffset < 0 || zOffset < 0 || width < 0 || height < 0 || depth < 0)
            { failure = NativeFailure("The 3D texture subimage target, context version or dimensions are invalid.", 0x0501); return false; }
            var bound = target == 0x806F ? _boundTexture3D : _boundTexture2DArray;
            if (bound is not { } value || !_textures.Contains(value)) { failure = NativeFailure("No 3D texture is bound to the requested target.", 0x0502); return false; }
            if (!_textureLevels.TryGetValue((value, target, level), out var image) || xOffset > image.Width - width || yOffset > image.Height - height || zOffset > image.Depth - depth)
            { failure = NativeFailure("The 3D texture subimage is outside the allocated level.", 0x0501); return false; }
            var integer = format is 0x8D98 or 0x8D99;
            if ((!integer && format is not (0x1907 or 0x1908)) || (integer ? type is not (0x1401 or 0x1403 or 0x1405) : type is not (0x1401 or 0x1406 or 0x140B)))
            { failure = NativeFailure("The 3D texture subimage format or type is unsupported.", 0x0500); return false; }
            var channels = format is 0x1908 or 0x8D99 ? 4 : 3;
            var componentBytes = type is 0x1406 or 0x1405 ? 4 : type is 0x140B or 0x1403 ? 2 : 1;
            var required = checked((long)width * height * depth * channels * componentBytes);
            if (pixels.Length != required) { failure = NativeFailure("The 3D texture subimage length is invalid.", 0x0501); return false; }
            if (!Begin(out failure)) return false;
            GCHandle pin = default;
            try
            {
                var bytes = pixels.ToArray(); pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                _gl.TexSubImage3D(target, level, xOffset, yOffset, zOffset, width, height, depth, format, type, pin.AddrOfPinnedObject());
                return CheckError(out failure);
            }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { if (pin.IsAllocated) pin.Free(); End(); }
        }
    }

    public bool TryCreateFramebuffer(out WebGlFramebufferHandle framebuffer, out GpuFailure? failure)
    {
        lock (_gate)
        {
            framebuffer = default; failure = null;
            if (!Begin(out failure)) return false;
            try
            {
                _gl.GenFramebuffers(1, out var value);
                if (value == 0) { failure = NativeFailure("glGenFramebuffers returned zero."); return false; }
                _framebuffers.Add(value); framebuffer = new(value); return true;
            }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { End(); }
        }
    }

    public bool TryDeleteFramebuffer(WebGlFramebufferHandle framebuffer, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (!_framebuffers.Remove(framebuffer.Value)) { failure = NativeFailure("The framebuffer handle is invalid for this context."); return false; }
            if (!Begin(out failure)) return false;
            try { var value = framebuffer.Value; _gl.DeleteFramebuffers(1, ref value); return CheckError(out failure); }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { End(); }
        }
    }

    public bool TryBindFramebuffer(uint target, WebGlFramebufferHandle? framebuffer, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (target is not (0x8D40 or 0x8CA8 or 0x8CA9)) { failure = NativeFailure("The framebuffer target is invalid.", 0x0500); return false; }
            if (framebuffer is { } value && !_framebuffers.Contains(value.Value)) { failure = NativeFailure("The framebuffer handle is invalid for this context.", 0x0502); return false; }
            return StateCall(() => _gl.BindFramebuffer(target, framebuffer?.Value ?? 0), out failure);
        }
    }

    public bool TryFramebufferTexture2D(uint target, uint attachment, uint textureTarget, WebGlTextureHandle? texture, int level, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (target is not (0x8D40 or 0x8CA8 or 0x8CA9) || (textureTarget != 0x0DE1 && textureTarget is not (>= 0x8515 and <= 0x851A)) || level < 0)
            { failure = NativeFailure("The framebuffer attachment target is invalid.", 0x0500); return false; }
            if (texture is { } value && !_textures.Contains(value.Value)) { failure = NativeFailure("The texture handle is invalid for this context.", 0x0502); return false; }
            return StateCall(() => _gl.FramebufferTexture2D(target, attachment, textureTarget, texture?.Value ?? 0, level), out failure);
        }
    }

    public bool TryFramebufferTextureLayer(uint target, uint attachment, WebGlTextureHandle? texture, int level, int layer, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (Options.Version != WebGlContextVersion.WebGl2 || target is not (0x8D40 or 0x8CA8 or 0x8CA9) || level < 0 || layer < 0)
            { failure = NativeFailure("The framebuffer layer attachment is invalid for this context.", 0x0500); return false; }
            if (texture is { } value && !_textures.Contains(value.Value)) { failure = NativeFailure("The texture handle is invalid for this context.", 0x0502); return false; }
            if (texture is { } attached && !_textureLevels.Any(entry => entry.Key.Texture == attached.Value && entry.Key.Level == level && entry.Key.Target is 0x806F or 0x8C1A && layer < entry.Value.Depth))
            { failure = NativeFailure("The framebuffer layer is outside the texture level.", 0x0501); return false; }
            return StateCall(() => _gl.FramebufferTextureLayer(target, attachment, texture?.Value ?? 0, level, layer), out failure);
        }
    }

    public bool TryCheckFramebufferStatus(uint target, out uint status, out GpuFailure? failure)
    {
        lock (_gate)
        {
            status = 0; failure = null;
            if (target is not (0x8D40 or 0x8CA8 or 0x8CA9)) { failure = NativeFailure("The framebuffer target is invalid.", 0x0500); return false; }
            if (!Begin(out failure)) return false;
            try { status = _gl.CheckFramebufferStatus(target); return CheckError(out failure); }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { End(); }
        }
    }

    public bool TryBlitFramebuffer(int srcX0, int srcY0, int srcX1, int srcY1, int dstX0, int dstY0, int dstX1, int dstY1, uint mask, uint filter, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (Options.Version != WebGlContextVersion.WebGl2 || (mask & ~(0x4000u | 0x0100u | 0x0400u)) != 0 || mask == 0 || filter is not (0x2600 or 0x2601))
            { failure = NativeFailure("The framebuffer blit parameters are invalid for this context.", 0x0500); return false; }
            return StateCall(() => _gl.BlitFramebuffer(srcX0, srcY0, srcX1, srcY1, dstX0, dstY0, dstX1, dstY1, mask, filter), out failure);
        }
    }
}
