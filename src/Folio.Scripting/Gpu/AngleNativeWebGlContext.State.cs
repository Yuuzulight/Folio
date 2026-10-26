using System.Runtime.InteropServices;

namespace Folio.Scripting.Gpu;

internal sealed partial class AngleNativeWebGlContext
{
    public bool TrySetCapability(uint capability, bool enabled, out GpuFailure? failure) => StateCall(() => { if (enabled) _gl.Enable(capability); else _gl.Disable(capability); }, out failure);
    public bool TrySetClearColor(float red, float green, float blue, float alpha, out GpuFailure? failure) => StateCall(() => _gl.ClearColor(red, green, blue, alpha), out failure);
    public bool TryClearMask(uint mask, out GpuFailure? failure) => StateCall(() => _gl.Clear(mask), out failure);
    public bool TryPixelStore(uint parameter, int value, out GpuFailure? failure)
    {
        if (parameter == 0x9240)
        {
            if (value is not (0 or 1)) { failure = NativeFailure("The unpack flip-Y value is invalid."); return false; }
            _unpackFlipY = value != 0; failure = null; return true;
        }
        if (value < 0 || (parameter == 0x0D05 && value is not (1 or 2 or 4 or 8))) { failure = NativeFailure("The pixel-store value is invalid."); return false; }
        var ok = StateCall(() => _gl.PixelStorei(parameter, value), out failure);
        if (ok) { if (parameter == 0x0D05) _packAlignment = value; else if (parameter == 0x0D02) _packRowLength = value; else if (parameter == 0x0D04) _packSkipPixels = value; else if (parameter == 0x0D03) _packSkipRows = value; }
        return ok;
    }
    public bool TryDrawBuffers(ReadOnlySpan<uint> buffers, out GpuFailure? failure) { if (buffers.Length == 0) { failure = NativeFailure("At least one draw buffer is required."); return false; } var copy = buffers.ToArray(); var ints = Array.ConvertAll(copy, static value => unchecked((int)value)); var memory = Marshal.AllocHGlobal(sizeof(uint) * copy.Length); try { Marshal.Copy(ints, 0, memory, ints.Length); return StateCall(() => _gl.DrawBuffers(copy.Length, memory), out failure); } finally { Marshal.FreeHGlobal(memory); } }
    public bool TryClearBufferfv(uint buffer, int drawBuffer, ReadOnlySpan<float> values, out GpuFailure? failure)
    {
        if (Options.Version != WebGlContextVersion.WebGl2 || buffer is not (0x1800 or 0x1801) || drawBuffer < 0 || values.Length < (buffer == 0x1800 ? 4 : 1))
        { failure = NativeFailure("The clear-buffer target, index or values are invalid.", 0x0500); return false; }
        var copy = values.ToArray(); var pin = GCHandle.Alloc(copy, GCHandleType.Pinned);
        try { return StateCall(() => _gl.ClearBufferfv(buffer, drawBuffer, pin.AddrOfPinnedObject()), out failure); }
        finally { pin.Free(); }
    }
    public bool TryClearBufferiv(uint buffer, int drawBuffer, ReadOnlySpan<int> values, out GpuFailure? failure)
    {
        if (Options.Version != WebGlContextVersion.WebGl2 || buffer is not (0x1800 or 0x1802) || drawBuffer < 0 || values.Length < 4)
        { failure = NativeFailure("The integer clear-buffer target, index or values are invalid.", 0x0500); return false; }
        var copy = values.ToArray(); var pin = GCHandle.Alloc(copy, GCHandleType.Pinned);
        try { return StateCall(() => _gl.ClearBufferiv(buffer, drawBuffer, pin.AddrOfPinnedObject()), out failure); }
        finally { pin.Free(); }
    }
    public bool TryClearBufferuiv(uint buffer, int drawBuffer, ReadOnlySpan<uint> values, out GpuFailure? failure)
    {
        if (Options.Version != WebGlContextVersion.WebGl2 || buffer != 0x1800 || drawBuffer < 0 || values.Length < 4)
        { failure = NativeFailure("The unsigned integer clear-buffer target, index or values are invalid.", 0x0500); return false; }
        var copy = values.ToArray(); var pin = GCHandle.Alloc(copy, GCHandleType.Pinned);
        try { return StateCall(() => _gl.ClearBufferuiv(buffer, drawBuffer, pin.AddrOfPinnedObject()), out failure); }
        finally { pin.Free(); }
    }
    public bool TryClearBufferfi(uint buffer, int drawBuffer, float depth, int stencil, out GpuFailure? failure)
    {
        if (Options.Version != WebGlContextVersion.WebGl2 || buffer != 0x84F9 || drawBuffer < 0 || depth is < 0 or > 1)
        { failure = NativeFailure("The depth-stencil clear-buffer target, index or values are invalid.", 0x0500); return false; }
        return StateCall(() => _gl.ClearBufferfi(buffer, drawBuffer, depth, stencil), out failure);
    }
    public bool TryReadBuffer(uint buffer, out GpuFailure? failure) => StateCall(() => _gl.ReadBuffer(buffer), out failure);
    public bool TrySetActiveTexture(uint texture, out GpuFailure? failure) => StateCall(() => _gl.ActiveTexture(texture), out failure);
    public bool TrySetDepthFunction(uint function, out GpuFailure? failure) => StateCall(() => _gl.DepthFunc(function), out failure);
    public bool TrySetDepthMask(bool enabled, out GpuFailure? failure) => StateCall(() => _gl.DepthMask(enabled ? (byte)1 : (byte)0), out failure);
    public bool TrySetClearDepth(float value, out GpuFailure? failure) => StateCall(() => _gl.ClearDepthf(value), out failure);
    public bool TrySetStencilFunction(uint face, uint function, int reference, uint mask, out GpuFailure? failure) => StateCall(() => _gl.StencilFuncSeparate(face, function, reference, mask), out failure);
    public bool TrySetStencilMask(uint face, uint mask, out GpuFailure? failure) => StateCall(() => _gl.StencilMaskSeparate(face, mask), out failure);
    public bool TrySetStencilOperation(uint face, uint fail, uint depthFail, uint depthPass, out GpuFailure? failure) => StateCall(() => _gl.StencilOpSeparate(face, fail, depthFail, depthPass), out failure);
    public bool TrySetCullFace(uint face, out GpuFailure? failure) => StateCall(() => _gl.CullFace(face), out failure);
    public bool TrySetFrontFace(uint mode, out GpuFailure? failure) => StateCall(() => _gl.FrontFace(mode), out failure);
    public bool TrySetBlendFunction(uint sourceRgb, uint destinationRgb, uint sourceAlpha, uint destinationAlpha, out GpuFailure? failure) => StateCall(() => _gl.BlendFuncSeparate(sourceRgb, destinationRgb, sourceAlpha, destinationAlpha), out failure);
    public bool TrySetBlendEquation(uint rgb, uint alpha, out GpuFailure? failure) => StateCall(() => _gl.BlendEquationSeparate(rgb, alpha), out failure);
    public bool TrySetBlendColor(float red, float green, float blue, float alpha, out GpuFailure? failure) => StateCall(() => _gl.BlendColor(red, green, blue, alpha), out failure);
    public bool TrySetColorMask(bool red, bool green, bool blue, bool alpha, out GpuFailure? failure) => StateCall(() => _gl.ColorMask(red ? (byte)1 : (byte)0, green ? (byte)1 : (byte)0, blue ? (byte)1 : (byte)0, alpha ? (byte)1 : (byte)0), out failure);
    public bool TrySetDepthState(uint function, bool writeMask, float clearValue, out GpuFailure? failure) => StateCall(() => { _gl.DepthFunc(function); _gl.DepthMask(writeMask ? (byte)1 : (byte)0); _gl.ClearDepthf(clearValue); }, out failure);
    public bool TrySetStencilState(uint face, uint function, int reference, uint compareMask, uint writeMask, uint fail, uint depthFail, uint depthPass, out GpuFailure? failure) => StateCall(() => { _gl.StencilFuncSeparate(face, function, reference, compareMask); _gl.StencilMaskSeparate(face, writeMask); _gl.StencilOpSeparate(face, fail, depthFail, depthPass); }, out failure);
    public bool TrySetStencilClear(int value, out GpuFailure? failure) => StateCall(() => _gl.ClearStencil(value), out failure);
    public bool TrySetCullState(uint face, uint frontFace, out GpuFailure? failure) => StateCall(() => { _gl.CullFace(face); _gl.FrontFace(frontFace); }, out failure);
    public bool TrySetDepthRange(float nearValue, float farValue, out GpuFailure? failure) => StateCall(() => _gl.DepthRangef(nearValue, farValue), out failure);
    public bool TrySetLineWidth(float width, out GpuFailure? failure) => StateCall(() => _gl.LineWidth(width), out failure);
    public bool TrySetPolygonOffset(float factor, float units, out GpuFailure? failure) => StateCall(() => _gl.PolygonOffset(factor, units), out failure);
    public bool TrySetSampleCoverage(float value, bool invert, out GpuFailure? failure) => StateCall(() => _gl.SampleCoverage(value, invert ? (byte)1 : (byte)0), out failure);
    public bool TrySetHint(uint target, uint mode, out GpuFailure? failure) => StateCall(() => _gl.Hint(target, mode), out failure);
    public bool TryFlush(out GpuFailure? failure) => StateCall(() => _gl.Flush(), out failure);
    public bool TryFinish(out GpuFailure? failure) => StateCall(() => _gl.Finish(), out failure);

    public bool TryGetParameter(uint parameter, out int value, out GpuFailure? failure)
    {
        lock (_gate) { value = 0; failure = null; if (parameter is not (0x8B8D or 0x8894 or 0x8895 or 0x84E0 or 0x0CF5 or 0x0D05 or 0x0D33 or 0x8B4D or 0x8B4C or 0x8869 or 0x8CA6 or 0x8CA7 or 0x85B5 or 0x0BA2 or 0x0C10)) { failure = NativeFailure("The parameter is not a scalar integer query."); return false; } if (!Begin(out failure)) return false; try { _gl.GetIntegerv(parameter, out value); return CheckError(out failure); } catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; } finally { End(); } }
    }

    public bool TryGetParameterFloat(uint parameter, out float value, out GpuFailure? failure)
    {
        lock (_gate) { value = 0; failure = null; if (parameter is not (0x0B21 or 0x0B73 or 0x0B74)) { failure = NativeFailure("The parameter is not a scalar float query."); return false; } if (!Begin(out failure)) return false; try { _gl.GetFloatv(parameter, out value); return CheckError(out failure); } catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; } finally { End(); } }
    }

    internal bool TryGetParameterFloatVector(uint parameter, Span<float> values, out GpuFailure? failure) => TryGetFloatVector(parameter, values, out failure);
    internal bool TryGetParameterBooleanVector(uint parameter, Span<bool> values, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (parameter != 0x0C23 || values.Length != 4) { failure = NativeFailure("The boolean vector query is invalid.", 0x0501); return false; }
            if (!Begin(out failure)) return false;
            var memory = Marshal.AllocHGlobal(sizeof(int) * 4);
            try { _gl.GetIntegervArray(parameter, memory); if (!CheckError(out failure)) return false; for (var i = 0; i < 4; i++) values[i] = Marshal.ReadInt32(memory, i * sizeof(int)) != 0; return true; }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; } finally { Marshal.FreeHGlobal(memory); End(); }
        }
    }

    public bool TryGetExtensions(out IReadOnlyList<string> extensions, out GpuFailure? failure)
    {
        lock (_gate)
        {
            extensions = Array.Empty<string>(); failure = null; if (!Begin(out failure)) return false;
            try { _gl.GetIntegerv(0x821D, out var count); if (!CheckError(out failure) || count < 0 || count > 4096) return false; var result = new List<string>(count); for (uint i = 0; i < count; i++) { var ptr = _gl.GetStringi(0x1F03, i); if (ptr != IntPtr.Zero) result.Add(Marshal.PtrToStringAnsi(ptr) ?? string.Empty); } extensions = result; return true; }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; } finally { End(); }
        }
    }
}
