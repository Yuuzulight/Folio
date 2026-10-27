using System.Runtime.InteropServices;

namespace Folio.Scripting.Gpu;

internal sealed partial class AngleNativeWebGlContext
{
    private const uint GlArrayBuffer = 0x8892;
    private const uint GlElementArrayBuffer = 0x8893;
    private const uint GlCopyReadBuffer = 0x8F36;
    private const uint GlCopyWriteBuffer = 0x8F37;
    private const uint GlUniformBuffer = 0x8A11;
    private const uint GlTransformFeedbackBuffer = 0x8C8E;
    private const uint GlStaticDraw = 0x88E4;
    private const uint GlColorBufferBit = 0x4000;

    private uint? _boundArrayBuffer;
    private uint? _boundElementArrayBuffer;
    private uint? _boundTransformFeedbackBuffer;
    private uint? _boundVertexArray;

    public bool TryCreateBuffer(out uint buffer, out GpuFailure? failure)
    {
        lock (_gate)
        {
            buffer = 0; failure = null;
            if (!Begin(out failure)) return false;
            try { _gl.GenBuffers(1, out buffer); if (buffer == 0) { failure = NativeFailure("glGenBuffers returned zero."); return false; } _buffers.Add(buffer); return true; }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { End(); }
        }
    }

    public bool TryBindBuffer(uint target, uint? buffer, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (buffer is { } value && !_buffers.Contains(value)) { failure = NativeFailure("The buffer handle is invalid for this context."); return false; }
            if (target != GlArrayBuffer && target != GlElementArrayBuffer && target != GlCopyReadBuffer && target != GlCopyWriteBuffer &&
                (target != GlUniformBuffer && target != GlTransformFeedbackBuffer || Options.Version != WebGlContextVersion.WebGl2 && target is GlUniformBuffer or GlTransformFeedbackBuffer)) { failure = NativeFailure("The buffer target is invalid for this context."); return false; }
            if (!Begin(out failure)) return false;
            try
            {
                _gl.BindBuffer(target, buffer ?? 0);
                if (!CheckError(out failure)) return false;
                if (target == GlArrayBuffer) _boundArrayBuffer = buffer;
                if (target == GlElementArrayBuffer)
                {
                    _boundElementArrayBuffer = buffer;
                    _vaoElementBuffers[_boundVertexArray ?? 0] = buffer;
                }
                if (target == GlCopyReadBuffer) _boundCopyReadBuffer = buffer;
                if (target == GlCopyWriteBuffer) _boundCopyWriteBuffer = buffer;
                if (target == GlUniformBuffer) _boundUniformBuffer = buffer;
                if (target == GlTransformFeedbackBuffer) _boundTransformFeedbackBuffer = buffer;
                return true;
            }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { End(); }
        }
    }

    public bool TryAllocateBufferStorage(uint target, long size, uint usage, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (size < 0 || size > int.MaxValue) { failure = new(GpuFailureKind.BudgetExceeded, "The WebGL buffer size is invalid or too large."); return false; }
            var buffer = CurrentBuffer(target);
            if (buffer is not { } handle) { failure = NativeFailure("No buffer is bound to the target."); return false; }
            if (!_device.TryReserve(size, out var reservation)) { failure = new(GpuFailureKind.BudgetExceeded, "The document GPU budget was exceeded."); return false; }
            if (!Begin(out failure)) { reservation.Dispose(); return false; }
            try
            {
                _gl.BufferData(target, (IntPtr)size, IntPtr.Zero, usage);
                if (!CheckError(out failure)) { reservation.Dispose(); return false; }
                if (_bufferReservations.Remove(handle, out var old)) old.Dispose();
                _bufferReservations[handle] = reservation;
                _bufferSizes[handle] = size;
                return true;
            }
            catch (Exception error) { reservation.Dispose(); failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { End(); }
        }
    }

    public bool TryUploadBuffer(uint target, ReadOnlySpan<byte> data, uint usage, out GpuFailure? failure)
    {
        if (!TryAllocateBufferStorage(target, data.Length, usage, out failure)) return false;
        return TryUpdateBuffer(target, 0, data, out failure);
    }

    public bool TryUpdateBuffer(uint target, long offset, ReadOnlySpan<byte> data, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (offset < 0 || offset > int.MaxValue || data.Length > int.MaxValue - offset) { failure = NativeFailure("The buffer update range is invalid."); return false; }
            var bound = CurrentBuffer(target);
            if (bound is null) { failure = NativeFailure("No buffer is bound to the target."); return false; }
            if (!_bufferSizes.TryGetValue(bound.Value, out var bufferSize) || offset > bufferSize || data.Length > bufferSize - offset) { failure = NativeFailure("The buffer update range exceeds the buffer size."); return false; }
            if (!Begin(out failure)) return false;
            IntPtr memory = IntPtr.Zero;
            try
            {
                memory = Marshal.AllocHGlobal(data.Length == 0 ? 1 : data.Length);
                if (data.Length > 0) Marshal.Copy(data.ToArray(), 0, memory, data.Length);
                _gl.BufferSubData(target, (IntPtr)offset, (IntPtr)data.Length, memory);
                return CheckError(out failure);
            }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { if (memory != IntPtr.Zero) Marshal.FreeHGlobal(memory); End(); }
        }
    }

    public bool TryCopyBuffer(uint readTarget, uint writeTarget, long readOffset, long writeOffset, long size, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (Options.Version != WebGlContextVersion.WebGl2 || readTarget != GlCopyReadBuffer || writeTarget != GlCopyWriteBuffer || readOffset < 0 || writeOffset < 0 || size < 0 || size > int.MaxValue || readOffset > int.MaxValue || writeOffset > int.MaxValue) { failure = NativeFailure("The buffer copy range is invalid."); return false; }
            if (CurrentBuffer(readTarget) is not { } readBuffer || CurrentBuffer(writeTarget) is not { } writeBuffer ||
                !_bufferSizes.TryGetValue(readBuffer, out var readSize) || !_bufferSizes.TryGetValue(writeBuffer, out var writeSize) ||
                readOffset > readSize - size || writeOffset > writeSize - size) { failure = NativeFailure("The buffer copy range exceeds a buffer size."); return false; }
            if (!Begin(out failure)) return false;
            try { _gl.CopyBufferSubData(readTarget, writeTarget, (IntPtr)readOffset, (IntPtr)writeOffset, (IntPtr)size); return CheckError(out failure); }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { End(); }
        }
    }

    public bool TryDeleteBuffer(uint buffer, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (!_buffers.Contains(buffer)) return true;
            if (!Begin(out failure)) return false;
            try { var value = buffer; _gl.DeleteBuffers(1, ref value); CheckError(out failure); return failure is null; }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally
            {
                End();
                if (failure is null)
                {
                    _boundArrayBuffer = _boundArrayBuffer == buffer ? null : _boundArrayBuffer;
                    _boundElementArrayBuffer = _boundElementArrayBuffer == buffer ? null : _boundElementArrayBuffer;
                    _boundCopyReadBuffer = _boundCopyReadBuffer == buffer ? null : _boundCopyReadBuffer;
                    _boundCopyWriteBuffer = _boundCopyWriteBuffer == buffer ? null : _boundCopyWriteBuffer;
                    _boundUniformBuffer = _boundUniformBuffer == buffer ? null : _boundUniformBuffer;
                    foreach (var vao in _vaoElementBuffers.Keys.ToArray()) if (_vaoElementBuffers[vao] == buffer) _vaoElementBuffers[vao] = null;
                    _buffers.Remove(buffer);
                    _bufferSizes.Remove(buffer);
                    if (_bufferReservations.Remove(buffer, out var reservation)) reservation.Dispose();
                }
            }
        }
    }

    public bool TryCreateVertexArray(out uint vertexArray, out GpuFailure? failure)
    {
        lock (_gate)
        {
            vertexArray = 0; failure = null;
            if (!Begin(out failure)) return false;
            try { _gl.GenVertexArrays(1, out vertexArray); if (vertexArray == 0) { failure = NativeFailure("glGenVertexArrays returned zero."); return false; } _vertexArrays.Add(vertexArray); return true; }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { End(); }
        }
    }

    public bool TryBindVertexArray(uint? vertexArray, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (vertexArray is { } value && !_vertexArrays.Contains(value)) { failure = NativeFailure("The vertex array handle is invalid for this context."); return false; }
            if (!Begin(out failure)) return false;
            try
            {
                _gl.BindVertexArray(vertexArray ?? 0); if (!CheckError(out failure)) return false;
                _boundVertexArray = vertexArray;
                _boundElementArrayBuffer = vertexArray is { } vao && _vaoElementBuffers.TryGetValue(vao, out var element) ? element : null;
                return true;
            }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { End(); }
        }
    }

    public bool TryDeleteVertexArray(uint vertexArray, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (!_vertexArrays.Contains(vertexArray)) return true;
            if (!Begin(out failure)) return false;
            try { var value = vertexArray; _gl.DeleteVertexArrays(1, ref value); return CheckError(out failure); }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { End(); if (failure is null) { _vertexArrays.Remove(vertexArray); if (_boundVertexArray == vertexArray) { _boundVertexArray = null; _boundElementArrayBuffer = null; } _vaoElementBuffers.Remove(vertexArray); } }
        }
    }

    public bool TryVertexAttribPointer(uint index, int size, uint type, bool normalized, int stride, int offset, out GpuFailure? failure, bool integer = false)
    {
        lock (_gate)
        {
            failure = null;
            if (index > 32 || size < 1 || size > 4 || stride < 0 || offset < 0) { failure = NativeFailure("The vertex attribute description is invalid."); return false; }
            if (!Begin(out failure)) return false;
            try { if (integer) _gl.VertexAttribIPointer(index, size, type, stride, (IntPtr)offset); else _gl.VertexAttribPointer(index, size, type, normalized ? (byte)1 : (byte)0, stride, (IntPtr)offset); return CheckError(out failure); }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { End(); }
        }
    }

    public bool TryEnableVertexAttrib(uint index, bool enabled, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null; if (!Begin(out failure)) return false;
            try { if (enabled) _gl.EnableVertexAttribArray(index); else _gl.DisableVertexAttribArray(index); return CheckError(out failure); }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { End(); }
        }
    }

    public bool TryDrawArrays(uint mode, int first, int count, out GpuFailure? failure) => Draw(() => _gl.DrawArrays(mode, first, count), out failure);
    public bool TryDrawElements(uint mode, int count, uint type, int offset, out GpuFailure? failure) => Draw(() => _gl.DrawElements(mode, count, type, (IntPtr)offset), out failure);
    public bool TryDrawArraysInstanced(uint mode, int first, int count, int instances, out GpuFailure? failure) => Draw(() => _gl.DrawArraysInstanced(mode, first, count, instances), out failure);
    public bool TryDrawElementsInstanced(uint mode, int count, uint type, int offset, int instances, out GpuFailure? failure) => Draw(() => _gl.DrawElementsInstanced(mode, count, type, (IntPtr)offset, instances), out failure);
    private bool Draw(Action action, out GpuFailure? failure)
    {
        lock (_gate) { failure = null; if (!Begin(out failure)) return false; try { action(); return CheckError(out failure); } catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; } finally { End(); } }
    }

    public bool TrySetViewport(int x, int y, int width, int height, out GpuFailure? failure) => StateCall(() => _gl.Viewport(x, y, width, height), out failure);
    public bool TrySetScissor(int x, int y, int width, int height, out GpuFailure? failure) => StateCall(() => _gl.Scissor(x, y, width, height), out failure);
    public bool TrySetScissor(int x, int y, int width, int height, bool enabled, out GpuFailure? failure) => TrySetScissor(x, y, width, height, out failure);
    public bool TryClear(float red, float green, float blue, float alpha, uint mask, out GpuFailure? failure) => StateCall(() => { _gl.ClearColor(red, green, blue, alpha); _gl.Clear(mask); }, out failure);
    public bool TryReadPixels(int x, int y, int width, int height, Span<byte> destination, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            return TryReadPixelsCoreLocked(x, y, width, height, destination, 0x1908, 0x1401, out failure);
        }
    }

    private bool TryReadPixelsCoreLocked(int x, int y, int width, int height, Span<byte> destination, uint format, uint type, out GpuFailure? failure)
    {
        failure = null;
        var packed = type is 0x8363 or 0x8033 or 0x8034;
        var bytesPerPixel = format == 0x1908 && type == 0x1401 ? 4 : format == 0x1907 && type == 0x1401 ? 3 : format == 0x1908 && type == 0x1406 ? 16 : packed && ((format == 0x1907 && type == 0x8363) || (format == 0x1908 && type is 0x8033 or 0x8034)) ? 2 : 0;
        if (width < 0 || height < 0 || bytesPerPixel == 0) { failure = NativeFailure("The readback dimensions or format are unsupported.", 0x0500); return false; }
        var rowPixels = _packRowLength == 0 ? width : _packRowLength;
        if (_packAlignment is not (1 or 2 or 4 or 8) || rowPixels < width || _packSkipPixels < 0 || _packSkipRows < 0)
        { failure = NativeFailure("The readback pack state is invalid.", 0x0501); return false; }
        var rowBytes = checked(rowPixels * bytesPerPixel);
        var stride = (rowBytes + _packAlignment - 1) / _packAlignment * _packAlignment;
        var required = height == 0 ? 0L : checked((long)(_packSkipRows + height - 1) * stride + (long)_packSkipPixels * bytesPerPixel + (long)width * bytesPerPixel);
        if (required > int.MaxValue || destination.Length < required) { failure = NativeFailure("The readback destination is too small.", 0x0502); return false; }
        if (!Begin(out failure)) return false;
        if (!ResolveMultisampleLocked(out failure)) { End(); return false; }
        var bytes = new byte[destination.Length];
        var source = format == 0x1907 || type == 0x1406 || packed ? new byte[checked(width * height * 4)] : bytes;
        var handle = GCHandle.Alloc(source, GCHandleType.Pinned);
            var changedPacking = format == 0x1907 || type == 0x1406 || packed;
        try
        {
            if (changedPacking)
            {
                _gl.PixelStorei(0x0D05, 1); _gl.PixelStorei(0x0D02, 0); _gl.PixelStorei(0x0D04, 0); _gl.PixelStorei(0x0D03, 0);
            }
            _gl.ReadPixels(x, y, width, height, changedPacking ? 0x1908u : format, type == 0x1406 || packed ? 0x1401u : type, handle.AddrOfPinnedObject());
            if (!CheckError(out failure)) return false;
            if (changedPacking)
            {
                var targetRowBytes = width * 3;
                var targetPackedRowBytes = rowPixels * 3;
                var targetStride = (targetPackedRowBytes + _packAlignment - 1) / _packAlignment * _packAlignment;
                var sourceRowBytes = width * 4;
                if (type == 0x1406)
                {
                    targetRowBytes = width * 16; targetPackedRowBytes = rowPixels * 16; targetStride = (targetPackedRowBytes + _packAlignment - 1) / _packAlignment * _packAlignment;
                    for (var row = 0; row < height; row++)
                    {
                        var src = source.AsSpan(row * sourceRowBytes, sourceRowBytes);
                        var dst = bytes.AsSpan((_packSkipRows + row) * targetStride + _packSkipPixels * 16, targetRowBytes);
                        for (var pixel = 0; pixel < width; pixel++) for (var channel = 0; channel < 4; channel++)
                            BitConverter.TryWriteBytes(dst.Slice(pixel * 16 + channel * 4, 4), src[pixel * 4 + channel] / 255f);
                    }
                }
                else if (packed)
                {
                    var packedStride = (rowPixels * 2 + _packAlignment - 1) / _packAlignment * _packAlignment;
                    for (var row = 0; row < height; row++)
                    {
                        var src = source.AsSpan(row * sourceRowBytes, sourceRowBytes);
                        var dst = bytes.AsSpan((_packSkipRows + row) * packedStride + _packSkipPixels * 2, width * 2);
                        for (var pixel = 0; pixel < width; pixel++)
                        {
                            var r = src[pixel * 4];
                            var g = src[pixel * 4 + 1];
                            var b = src[pixel * 4 + 2];
                            var a = src[pixel * 4 + 3];
                            var value = type == 0x8363
                                ? (ushort)((r * 31 + 127) / 255 << 11 | (g * 63 + 127) / 255 << 5 | (b * 31 + 127) / 255)
                                : type == 0x8033
                                    ? (ushort)((r * 15 + 127) / 255 << 12 | (g * 15 + 127) / 255 << 8 | (b * 15 + 127) / 255 << 4 | (a * 15 + 127) / 255)
                                    : (ushort)((r * 31 + 127) / 255 << 11 | (g * 31 + 127) / 255 << 6 | (b * 31 + 127) / 255 << 1 | (a + 127) / 255);
                            BitConverter.TryWriteBytes(dst.Slice(pixel * 2, 2), value);
                        }
                    }
                }
                else for (var row = 0; row < height; row++)
                {
                    var src = source.AsSpan(row * sourceRowBytes, sourceRowBytes);
                    var dst = bytes.AsSpan((_packSkipRows + row) * targetStride + _packSkipPixels * 3, targetRowBytes);
                    for (var pixel = 0; pixel < width; pixel++) src.Slice(pixel * 4, 3).CopyTo(dst.Slice(pixel * 3, 3));
                }
            }
            bytes.AsSpan().CopyTo(destination); return true;
        }
        catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
        finally
        {
            handle.Free();
            if (changedPacking)
            {
                try { _gl.PixelStorei(0x0D05, _packAlignment); _gl.PixelStorei(0x0D02, _packRowLength); _gl.PixelStorei(0x0D04, _packSkipPixels); _gl.PixelStorei(0x0D03, _packSkipRows); } catch { }
            }
            End();
        }
    }
    public bool TryReadPixelsPresentation(int x, int y, int width, int height, Span<byte> destination, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            var expected = width < 0 || height < 0 ? -1L : (long)width * height * 4;
            if (expected < 0 || expected > int.MaxValue || destination.Length != expected) { failure = NativeFailure("The presentation readback dimensions or destination are invalid."); return false; }
            var alignment = _packAlignment; var rowLength = _packRowLength; var skipPixels = _packSkipPixels; var skipRows = _packSkipRows;
            _packAlignment = 4; _packRowLength = _packSkipPixels = _packSkipRows = 0;
            var current = false;
            try
            {
                if (!Begin(out failure)) return false;
                current = true;
                _gl.PixelStorei(0x0D05, 4);
                if (Options.Version == WebGlContextVersion.WebGl2) { _gl.PixelStorei(0x0D02, 0); _gl.PixelStorei(0x0D04, 0); _gl.PixelStorei(0x0D03, 0); }
                if (!CheckError(out failure)) return false;
                End(); current = false;
                if (!TryReadPixels(x, y, width, height, destination, out failure)) return false;
                var rowBytes = checked(width * 4); var temporary = new byte[rowBytes];
                for (var row = 0; row < height / 2; row++) { var top = destination.Slice(row * rowBytes, rowBytes); var bottom = destination.Slice((height - row - 1) * rowBytes, rowBytes); bottom.CopyTo(temporary); top.CopyTo(bottom); temporary.AsSpan().CopyTo(top); }
                return true;
            }
            finally
            {
                if (current) End();
                _packAlignment = alignment; _packRowLength = rowLength; _packSkipPixels = skipPixels; _packSkipRows = skipRows;
                var restoreCurrent = false;
                try
                {
                    if (Begin(out _))
                    {
                        restoreCurrent = true;
                        _gl.PixelStorei(0x0D05, alignment);
                        if (Options.Version == WebGlContextVersion.WebGl2) { _gl.PixelStorei(0x0D02, rowLength); _gl.PixelStorei(0x0D04, skipPixels); _gl.PixelStorei(0x0D03, skipRows); }
                        CheckError(out _);
                    }
                }
                catch { }
                finally { if (restoreCurrent) End(); }
            }
        }
    }
    public bool TryReadPixels(int x, int y, int width, int height, Span<byte> destination, uint format, uint type, out GpuFailure? failure)
    {
        lock (_gate) return TryReadPixelsCoreLocked(x, y, width, height, destination, format, type, out failure);
    }

    private bool StateCall(Action action, out GpuFailure? failure)
    {
        lock (_gate) { failure = null; if (!Begin(out failure)) return false; try { action(); return CheckError(out failure); } catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; } finally { End(); } }
    }

    public bool TryBindBufferBase(uint target, uint index, uint? buffer, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (Options.Version != WebGlContextVersion.WebGl2 || target is not (GlUniformBuffer or GlTransformFeedbackBuffer)) { failure = NativeFailure("The indexed buffer target is invalid for this context."); return false; }
            if (buffer is { } value && !_buffers.Contains(value)) { failure = NativeFailure("The buffer handle is invalid for this context."); return false; }
            if (!Begin(out failure)) return false;
            try { _gl.BindBufferBase(target, index, buffer ?? 0); if (!CheckError(out failure)) return false; if (index == 0) { if (target == GlUniformBuffer) _boundUniformBuffer = buffer; else _boundTransformFeedbackBuffer = buffer; } return true; }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; } finally { End(); }
        }
    }

    public bool TryBindBufferRange(uint target, uint index, uint? buffer, long offset, long size, out GpuFailure? failure)
    {
        lock (_gate) { failure = null; if (Options.Version != WebGlContextVersion.WebGl2 || target is not (GlUniformBuffer or GlTransformFeedbackBuffer) || offset < 0 || size < 0 || buffer is null && size != 0) { failure = NativeFailure("The buffer range is invalid for this context."); return false; } if (buffer is { } value && (!_buffers.Contains(value) || !_bufferSizes.TryGetValue(value, out var bufferSize) || offset > bufferSize || size > bufferSize - offset)) { failure = NativeFailure("The buffer range exceeds the buffer size."); return false; } if (!Begin(out failure)) return false; try { _gl.BindBufferRange(target, index, buffer ?? 0, (IntPtr)offset, (IntPtr)size); if (!CheckError(out failure)) return false; if (index == 0) { if (target == GlUniformBuffer) _boundUniformBuffer = buffer; else _boundTransformFeedbackBuffer = buffer; } return true; } catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; } finally { End(); } }
    }

    public bool TryGetBufferParameter(uint target, uint parameter, out int value, out GpuFailure? failure)
    {
        lock (_gate) { value = 0; failure = null; if (CurrentBuffer(target) is null) { failure = NativeFailure("No buffer is bound to the target."); return false; } if (!Begin(out failure)) return false; try { _gl.GetBufferParameteriv(target, parameter, out value); return CheckError(out failure); } catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; } finally { End(); } }
    }

    public bool TryReadBuffer(uint target, long offset, Span<byte> destination, out GpuFailure? failure)
    {
        lock (_gate) { failure = null; if (offset < 0 || destination.Length == 0 || CurrentBuffer(target) is null) { failure = NativeFailure("The buffer read range is invalid."); return false; } if (!Begin(out failure)) return false; IntPtr mapped = IntPtr.Zero; try { mapped = _gl.MapBufferRange(target, (IntPtr)offset, (IntPtr)destination.Length, 0x0001); if (mapped == IntPtr.Zero) { failure = NativeFailure("ANGLE could not map the buffer range for readback."); return false; } var bytes = new byte[destination.Length]; Marshal.Copy(mapped, bytes, 0, bytes.Length); bytes.AsSpan().CopyTo(destination); return true; } catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; } finally { if (mapped != IntPtr.Zero) { try { if (_gl.UnmapBuffer(target) == 0 && failure is null) failure = NativeFailure("ANGLE reported a buffer mapping failure."); } catch (Exception error) { if (failure is null) failure = new(GpuFailureKind.NativeError, error.Message); } } End(); } }
    }

    public bool TrySetVertexAttribConstant(uint index, ReadOnlySpan<float> values, out GpuFailure? failure)
    {
        lock (_gate) { failure = null; if (index > 32 || values.Length is < 1 or > 4) { failure = NativeFailure("The vertex attribute constant is invalid."); return false; } if (!Begin(out failure)) return false; var copy = new float[] { 0, 0, 0, 1 }; values.CopyTo(copy); var pin = GCHandle.Alloc(copy, GCHandleType.Pinned); try { _gl.VertexAttrib4fv(index, pin.AddrOfPinnedObject()); if (!CheckError(out failure)) return false; copy.AsSpan().CopyTo(_vertexAttribConstants.AsSpan(checked((int)index * 4), 4)); return true; } catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; } finally { pin.Free(); End(); } }
    }

    public bool TrySetVertexAttribIConstant(uint index, ReadOnlySpan<int> values, out GpuFailure? failure)
    {
        lock (_gate) { failure = null; if (index > 32 || values.Length is < 1 or > 4) { failure = NativeFailure("The integer vertex attribute constant is invalid."); return false; } if (!Begin(out failure)) return false; var copy = new int[] { 0, 0, 0, 1 }; values.CopyTo(copy); var pin = GCHandle.Alloc(copy, GCHandleType.Pinned); try { _gl.VertexAttribI4iv(index, pin.AddrOfPinnedObject()); return CheckError(out failure); } catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; } finally { pin.Free(); End(); } }
    }
    public bool TrySetVertexAttribUIConstant(uint index, ReadOnlySpan<uint> values, out GpuFailure? failure)
    {
        lock (_gate) { failure = null; if (index > 32 || values.Length is < 1 or > 4) { failure = NativeFailure("The unsigned integer vertex attribute constant is invalid."); return false; } if (!Begin(out failure)) return false; var copy = new uint[] { 0, 0, 0, 1 }; values.CopyTo(copy); var pin = GCHandle.Alloc(copy, GCHandleType.Pinned); try { _gl.VertexAttribI4uiv(index, pin.AddrOfPinnedObject()); return CheckError(out failure); } catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; } finally { pin.Free(); End(); } }
    }

    public bool TryGetVertexAttrib(uint index, uint parameter, out int value, out GpuFailure? failure)
    {
        lock (_gate) { value = 0; failure = null; if (index > 32 || parameter is not (0x8622 or 0x8623 or 0x8625 or 0x88FD or 0x8645 or 0x88FE)) { failure = NativeFailure("The vertex attribute query is not a scalar integer."); return false; } if (!Begin(out failure)) return false; try { _gl.GetVertexAttribiv(index, parameter, out value); return CheckError(out failure); } catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; } finally { End(); } }
    }

    public bool TryGetVertexAttribOffset(uint index, uint parameter, out IntPtr offset, out GpuFailure? failure)
    {
        lock (_gate) { offset = IntPtr.Zero; failure = null; if (index > 32) { failure = NativeFailure("The vertex attribute index is invalid."); return false; } if (!Begin(out failure)) return false; try { _gl.GetVertexAttribPointerv(index, parameter, out offset); return CheckError(out failure); } catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; } finally { End(); } }
    }
    private uint? CurrentBuffer(uint target) => target == GlArrayBuffer ? _boundArrayBuffer : target == GlElementArrayBuffer ? _boundElementArrayBuffer : target == GlCopyReadBuffer ? _boundCopyReadBuffer : target == GlCopyWriteBuffer ? _boundCopyWriteBuffer : target == GlUniformBuffer ? _boundUniformBuffer : target == GlTransformFeedbackBuffer ? _boundTransformFeedbackBuffer : null;
}
