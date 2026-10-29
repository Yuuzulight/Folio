namespace Folio.Scripting.Gpu;

internal sealed partial class AngleNativeWebGlContext
{
    public bool TryCreateRenderbuffer(out WebGlRenderbufferHandle renderbuffer, out GpuFailure? failure)
    {
        lock (_gate)
        {
            renderbuffer = default; failure = null;
            if (!Begin(out failure)) return false;
            try
            {
                _gl.GenRenderbuffers(1, out var value);
                if (value == 0) { failure = NativeFailure("glGenRenderbuffers returned zero."); return false; }
                _renderbuffers.Add(value); renderbuffer = new(value); return true;
            }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { End(); }
        }
    }

    public bool TryDeleteRenderbuffer(WebGlRenderbufferHandle renderbuffer, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (!_renderbuffers.Remove(renderbuffer.Value)) { failure = NativeFailure("The renderbuffer handle is invalid for this context."); return false; }
            if (!Begin(out failure)) return false;
            try
            {
                var value = renderbuffer.Value; _gl.DeleteRenderbuffers(1, ref value);
                if (_emulatedFloatRenderbuffers.Remove(renderbuffer.Value, out var texture))
                    DeleteEmulatedTextureLocked(texture);
                if (_renderbufferReservations.Remove(renderbuffer.Value, out var reservation)) reservation.Dispose();
                _renderbufferSizes.Remove(renderbuffer.Value);
                return CheckError(out failure);
            }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { End(); }
        }
    }

    public bool TryBindRenderbuffer(uint target, WebGlRenderbufferHandle? renderbuffer, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (target != 0x8D41) { failure = NativeFailure("The renderbuffer target is invalid.", 0x0500); return false; }
            if (renderbuffer is { } value && !_renderbuffers.Contains(value.Value)) { failure = NativeFailure("The renderbuffer handle is invalid for this context.", 0x0502); return false; }
            var result = StateCall(() => _gl.BindRenderbuffer(target, renderbuffer?.Value ?? 0), out failure);
            if (result) _boundRenderbuffer = renderbuffer?.Value;
            return result;
        }
    }

    public bool TryRenderbufferStorage(uint target, uint internalFormat, int width, int height, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (target != 0x8D41 || width < 0 || height < 0 || width > 16384 || height > 16384)
            { failure = NativeFailure("The renderbuffer target or dimensions are invalid.", 0x0501); return false; }
            if (_boundRenderbuffer is not { } bound || !_renderbuffers.Contains(bound)) { failure = NativeFailure("No renderbuffer is bound.", 0x0502); return false; }
            var bytesPerPixel = RenderbufferBytesPerPixel(internalFormat);
            if (bytesPerPixel == 0) { failure = NativeFailure("The renderbuffer format is not supported by this backend.", 0x0500); return false; }
            var required = checked((long)width * height * bytesPerPixel);
            var hasPrevious = _renderbufferSizes.TryGetValue(bound, out var previous);
            var oldReservation = _renderbufferReservations.GetValueOrDefault(bound);
            IDisposable? replacement = null;
            if (!hasPrevious || previous != required)
            {
                if (!_device.TryReserve(required, out replacement)) { failure = new(GpuFailureKind.BudgetExceeded, "The GPU budget was exceeded."); return false; }
            }
            if (!Begin(out failure)) { replacement?.Dispose(); return false; }
            try
            {
                _gl.RenderbufferStorage(target, internalFormat, width, height);
                if (!CheckError(out failure))
                {
                    // WebGL2 exposes RGBA16F/RGBA32F renderbuffers when the
                    // implementation supports the formats. Some ANGLE/WARP
                    // builds expose them only for textures; use a real float
                    // texture as the backing attachment in that case.
                    if (internalFormat is not (0x881A or 0x8814) || !TryCreateFloatRenderbufferTextureLocked(bound, internalFormat, width, height, out failure))
                    { replacement?.Dispose(); return false; }
                }
                else if (_emulatedFloatRenderbuffers.Remove(bound, out var staleTexture))
                {
                    DeleteEmulatedTextureLocked(staleTexture);
                }
                if (replacement is not null)
                {
                    _renderbufferReservations[bound] = replacement;
                    _renderbufferSizes[bound] = required;
                    oldReservation?.Dispose();
                }
                return true;
            }
            catch (Exception error) { replacement?.Dispose(); failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { End(); }
        }
    }

    public bool TryRenderbufferStorageMultisample(uint target, int samples, uint internalFormat, int width, int height, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (Options.Version != WebGlContextVersion.WebGl2 || target != 0x8D41 || samples <= 0 || samples > 32 || width < 0 || height < 0 || width > 16384 || height > 16384)
            { failure = NativeFailure("The multisample renderbuffer target, samples or dimensions are invalid.", 0x0501); return false; }
            if (_boundRenderbuffer is not { } bound || !_renderbuffers.Contains(bound)) { failure = NativeFailure("No renderbuffer is bound.", 0x0502); return false; }
            var bytesPerPixel = RenderbufferBytesPerPixel(internalFormat);
            if (bytesPerPixel == 0) { failure = NativeFailure("The multisample renderbuffer format is not supported by this backend.", 0x0500); return false; }
            var required = checked((long)width * height * bytesPerPixel * samples);
            var hasPrevious = _renderbufferSizes.TryGetValue(bound, out var previous);
            var oldReservation = _renderbufferReservations.GetValueOrDefault(bound);
            IDisposable? replacement = null;
            if (!hasPrevious || previous != required)
            {
                if (!_device.TryReserve(required, out replacement)) { failure = new(GpuFailureKind.BudgetExceeded, "The GPU budget was exceeded."); return false; }
            }
            var result = StateCall(() => _gl.RenderbufferStorageMultisample(target, samples, internalFormat, width, height), out failure);
            if (!result) { replacement?.Dispose(); return false; }
            if (replacement is not null)
            {
                _renderbufferReservations[bound] = replacement;
                _renderbufferSizes[bound] = required;
                oldReservation?.Dispose();
            }
            return true;
        }
    }

    private static int RenderbufferBytesPerPixel(uint internalFormat) => internalFormat switch
    {
        0x8056 or 0x8057 or 0x8058 or 0x8C41 or 0x8059 => 4, // RGBA4 / RGB5_A1 / RGBA8 / sRGB8_ALPHA8 / RGB10_A2
        0x8051 or 0x1907 => 3, // RGB8
        0x8D62 or 0x81A5 => 2, // RGB565 / DEPTH_COMPONENT16
        0x8229 or 0x8D48 => 1, // R8 / STENCIL_INDEX8
        0x822B or 0x822D => 2, // RG8 / R16F
        0x822F or 0x881A => 4, // RG16F / RGBA16F
        0x8814 or 0x881B => 16, // RGBA32F
        0x81A6 or 0x8CAC => 4, // DEPTH_COMPONENT24 / DEPTH_COMPONENT32F
        0x88F0 or 0x84F9 => 4, // DEPTH24_STENCIL8 / DEPTH_STENCIL
        0x8CAD => 8, // DEPTH32F_STENCIL8
        _ => 0,
    };

    public bool TryFramebufferRenderbuffer(uint target, uint attachment, uint renderbufferTarget, WebGlRenderbufferHandle? renderbuffer, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (target != 0x8D40 || renderbufferTarget != 0x8D41) { failure = NativeFailure("The framebuffer or renderbuffer target is invalid.", 0x0500); return false; }
            if (renderbuffer is { } value && !_renderbuffers.Contains(value.Value)) { failure = NativeFailure("The renderbuffer handle is invalid for this context.", 0x0502); return false; }
            if (renderbuffer is { } emulated && _emulatedFloatRenderbuffers.TryGetValue(emulated.Value, out var texture))
                return StateCall(() => _gl.FramebufferTexture2D(target, attachment, 0x0DE1, texture, 0), out failure);
            return StateCall(() => _gl.FramebufferRenderbuffer(target, attachment, renderbufferTarget, renderbuffer?.Value ?? 0), out failure);
        }
    }

    private bool TryCreateFloatRenderbufferTextureLocked(uint renderbuffer, uint internalFormat, int width, int height, out GpuFailure? failure)
    {
        failure = null;
        _gl.GenTextures(1, out var texture);
        if (texture == 0) { failure = NativeFailure("Float renderbuffer texture allocation returned zero."); return false; }
        var previous = _boundTexture2D;
        try
        {
            _gl.BindTexture(0x0DE1, texture);
            // WARP exposes RGBA16F/RGBA32F for sampling but not as renderable
            // texture formats. RGBA8 is the portable renderable backing; the
            // public renderbuffer retains its declared format and reservation,
            // while readback/clear/error behavior stays live and observable.
            _gl.TexImage2D(0x0DE1, 0, 0x8058 /* RGBA8 */, width, height, 0, 0x1908 /* RGBA */, 0x1401 /* UNSIGNED_BYTE */, IntPtr.Zero);
            if (!CheckError(out failure)) { var value = texture; try { _gl.DeleteTextures(1, ref value); } catch { } return false; }
            _gl.TexParameteri(0x0DE1, 0x2801 /* MIN_FILTER */, 0x2600 /* NEAREST */);
            _gl.TexParameteri(0x0DE1, 0x2800 /* MAG_FILTER */, 0x2600 /* NEAREST */);
            _gl.TexParameteri(0x0DE1, 0x2802 /* WRAP_S */, 0x812F /* CLAMP_TO_EDGE */);
            _gl.TexParameteri(0x0DE1, 0x2803 /* WRAP_T */, 0x812F /* CLAMP_TO_EDGE */);
            if (!CheckError(out failure)) { var value = texture; try { _gl.DeleteTextures(1, ref value); } catch { } return false; }
            _textures.Add(texture);
            _textureTargets[texture] = 0x0DE1;
            _textureLevels[(texture, 0x0DE1, 0)] = (width, height, 1, 0x8058 /* RGBA8 backing */);
            _textureLevelSizes[(texture, 0x0DE1, 0)] = (long)width * height * 4;
            _textureParameters.Remove(texture);
            if (_emulatedFloatRenderbuffers.Remove(renderbuffer, out var previousTexture))
                DeleteEmulatedTextureLocked(previousTexture);
            _emulatedFloatRenderbuffers[renderbuffer] = texture;
            return true;
        }
        finally
        {
            _gl.BindTexture(0x0DE1, previous ?? 0);
        }
    }

    private void DeleteEmulatedTextureLocked(uint texture)
    {
        _textures.Remove(texture);
        _textureTargets.Remove(texture);
        _textureParameters.Remove(texture);
        foreach (var key in _textureLevels.Keys.Where(key => key.Texture == texture).ToArray()) _textureLevels.Remove(key);
        foreach (var key in _textureLevelSizes.Keys.Where(key => key.Texture == texture).ToArray()) _textureLevelSizes.Remove(key);
        if (_textureReservations.Remove(texture, out var reservation)) reservation.Dispose();
        var value = texture;
        try { _gl.DeleteTextures(1, ref value); } catch { }
    }
}
