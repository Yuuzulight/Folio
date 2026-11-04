namespace Folio.Scripting.Gpu;

internal sealed partial class FolioNativeWebGlContext
{
    private ref uint? GetBoundBufferSlot(uint target, out bool validTarget)
    {
        validTarget = true;
        switch (target)
        {
            case GlArrayBuffer: return ref _boundArrayBuffer;
            case GlElementArrayBuffer: return ref CurrentVao.ElementArrayBuffer;
            case GlCopyReadBuffer: return ref _boundCopyReadBuffer;
            case GlCopyWriteBuffer: return ref _boundCopyWriteBuffer;
            case GlPixelPackBuffer: return ref _boundPixelPackBuffer;
            case GlPixelUnpackBuffer: return ref _boundPixelUnpackBuffer;
            case GlUniformBuffer: return ref _boundUniformBuffer;
            case GlTransformFeedbackBuffer: return ref _boundTransformFeedbackBuffer;
            default:
                validTarget = false;
                return ref _boundArrayBuffer;
        }
    }

    private static bool IsElementTarget(uint target) => target == GlElementArrayBuffer;

    public bool TryCreateBuffer(out WebGlBufferHandle buffer, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            buffer = default;
            if (!CheckAvailable(out failure)) return false;
            var id = _nextHandle++;
            _buffers[id] = new BufferEntry(id);
            buffer = new WebGlBufferHandle(id);
            return true;
        }
    }

    public bool TryDeleteBuffer(WebGlBufferHandle buffer, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (buffer.Value == 0) return true;
            if (!_buffers.TryGetValue(buffer.Value, out var entry))
                return true;

            if (entry.ReservedBytes > 0)
                TryReserveResourceBytes(-entry.ReservedBytes, out _);

            _buffers.Remove(buffer.Value);
            if (_boundArrayBuffer == buffer.Value) _boundArrayBuffer = null;
            if (_boundCopyReadBuffer == buffer.Value) _boundCopyReadBuffer = null;
            if (_boundCopyWriteBuffer == buffer.Value) _boundCopyWriteBuffer = null;
            if (_boundPixelPackBuffer == buffer.Value) _boundPixelPackBuffer = null;
            if (_boundPixelUnpackBuffer == buffer.Value) _boundPixelUnpackBuffer = null;
            if (_boundUniformBuffer == buffer.Value) _boundUniformBuffer = null;
            if (_boundTransformFeedbackBuffer == buffer.Value) _boundTransformFeedbackBuffer = null;

            if (_defaultVao.ElementArrayBuffer == buffer.Value) _defaultVao.ElementArrayBuffer = null;
            for (var i = 0; i < MaxVertexAttribs; i++)
                if (_defaultVao.Attribs[i].Buffer == buffer.Value) _defaultVao.Attribs[i].Buffer = null;

            foreach (var vao in _vertexArrays.Values)
            {
                if (vao.ElementArrayBuffer == buffer.Value) vao.ElementArrayBuffer = null;
                for (var i = 0; i < MaxVertexAttribs; i++)
                    if (vao.Attribs[i].Buffer == buffer.Value) vao.Attribs[i].Buffer = null;
            }

            for (var i = 0; i < _uboBindings.Length; i++)
                if (_uboBindings[i].Buffer == buffer.Value) _uboBindings[i] = default;
            for (var i = 0; i < _tfBindings.Length; i++)
                if (_tfBindings[i].Buffer == buffer.Value) _tfBindings[i] = default;

            return true;
        }
    }

    public bool TryBindBuffer(uint target, WebGlBufferHandle? buffer, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            ref var slot = ref GetBoundBufferSlot(target, out var validTarget);
            if (!validTarget)
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            if (buffer is null || buffer.Value.Value == 0)
            {
                slot = null;
                return true;
            }
            if (!_buffers.TryGetValue(buffer.Value.Value, out var entry))
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            if (entry.FirstTarget != 0 && IsElementTarget(entry.FirstTarget) != IsElementTarget(target))
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            if (entry.FirstTarget == 0)
                entry.FirstTarget = target;
            slot = entry.Handle;
            return true;
        }
    }

    public bool TryAllocateBufferStorage(uint target, long size, uint usage, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (size < 0 || size > int.MaxValue)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            ref var slot = ref GetBoundBufferSlot(target, out var validTarget);
            if (!validTarget)
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            if (slot is not uint id || !_buffers.TryGetValue(id, out var entry))
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }

            var delta = size - entry.ReservedBytes;
            if (!TryReserveResourceBytes(delta, out failure))
                return false;

            entry.Data = new byte[(int)size];
            entry.Usage = usage;
            entry.ReservedBytes = size;
            return true;
        }
    }

    public bool TryUploadBuffer(uint target, ReadOnlySpan<byte> data, uint usage, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            ref var slot = ref GetBoundBufferSlot(target, out var validTarget);
            if (!validTarget)
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            if (slot is not uint id || !_buffers.TryGetValue(id, out var entry))
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }

            var delta = (long)data.Length - entry.ReservedBytes;
            if (!TryReserveResourceBytes(delta, out failure))
                return false;

            entry.Data = data.ToArray();
            entry.Usage = usage;
            entry.ReservedBytes = data.Length;
            return true;
        }
    }

    public bool TryUpdateBuffer(uint target, int byteOffset, ReadOnlySpan<byte> data, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            ref var slot = ref GetBoundBufferSlot(target, out var validTarget);
            if (!validTarget)
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            if (slot is not uint id || !_buffers.TryGetValue(id, out var entry))
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            if (byteOffset < 0 || (long)byteOffset + data.Length > entry.Data.Length)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            data.CopyTo(entry.Data.AsSpan(byteOffset, data.Length));
            return true;
        }
    }

    public bool TryCopyBuffer(uint readTarget, uint writeTarget, long readOffset, long writeOffset, long size, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            var readId = GetBoundBufferSlot(readTarget, out var validR);
            var writeId = GetBoundBufferSlot(writeTarget, out var validW);
            if (!validR || !validW)
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            if (readId is not uint rId || writeId is not uint wId ||
                !_buffers.TryGetValue(rId, out var rBuf) || !_buffers.TryGetValue(wId, out var wBuf))
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            if (readOffset < 0 || writeOffset < 0 || size < 0 ||
                readOffset + size > rBuf.Data.Length || writeOffset + size > wBuf.Data.Length)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            if (rId == wId && readOffset < writeOffset + size && writeOffset < readOffset + size)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            rBuf.Data.AsSpan((int)readOffset, (int)size).CopyTo(wBuf.Data.AsSpan((int)writeOffset, (int)size));
            return true;
        }
    }

    public bool TryBindBufferBase(uint target, uint index, WebGlBufferHandle? buffer, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (!TryBindBuffer(target, buffer, out failure)) return false;
            var id = buffer is { Value: > 0 } h ? (uint?)h.Value : null;
            var len = id is uint bid && _buffers.TryGetValue(bid, out var b) ? b.Data.Length : 0L;
            if (target == GlUniformBuffer && index < MaxUniformBufferBindings)
                _uboBindings[index] = new(id, 0, len);
            else if (target == GlTransformFeedbackBuffer && index < MaxTransformFeedbackBuffers)
                _tfBindings[index] = new(id, 0, len);
            else
                RecordGlError(GlInvalidValue);
            return true;
        }
    }

    public bool TryBindBufferRange(uint target, uint index, WebGlBufferHandle? buffer, long offset, long size, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (offset < 0 || size <= 0)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            if (!TryBindBuffer(target, buffer, out failure)) return false;
            var id = buffer is { Value: > 0 } h ? (uint?)h.Value : null;
            if (target == GlUniformBuffer && index < MaxUniformBufferBindings)
                _uboBindings[index] = new(id, offset, size);
            else if (target == GlTransformFeedbackBuffer && index < MaxTransformFeedbackBuffers)
                _tfBindings[index] = new(id, offset, size);
            else
                RecordGlError(GlInvalidValue);
            return true;
        }
    }

    public bool TryGetBufferParameter(uint target, uint parameter, out WebGlParameterValue value, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            value = default;
            if (!CheckAvailable(out failure)) return false;
            var slot = GetBoundBufferSlot(target, out var validTarget);
            if (!validTarget)
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            if (slot is not uint id || !_buffers.TryGetValue(id, out var entry))
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            switch (parameter)
            {
                case GlBufferSize:
                    value = new(WebGlParameterKind.Int32, Int32: entry.Data.Length, Int64: entry.Data.Length);
                    return true;
                case GlBufferUsage:
                    value = new(WebGlParameterKind.UInt32, UInt32: entry.Usage, Int32: (int)entry.Usage);
                    return true;
                default:
                    RecordGlError(GlInvalidEnum);
                    return true;
            }
        }
    }

    public bool TryReadBuffer(uint target, long offset, Span<byte> destination, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            var slot = GetBoundBufferSlot(target, out var validTarget);
            if (!validTarget)
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            if (slot is not uint id || !_buffers.TryGetValue(id, out var entry))
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            if (offset < 0 || offset + destination.Length > entry.Data.Length)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            entry.Data.AsSpan((int)offset, destination.Length).CopyTo(destination);
            return true;
        }
    }

    public bool TryCreateVertexArray(out WebGlVertexArrayHandle vertexArray, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            vertexArray = default;
            if (!CheckAvailable(out failure)) return false;
            var id = _nextHandle++;
            _vertexArrays[id] = new VertexArrayEntry(id);
            vertexArray = new WebGlVertexArrayHandle(id);
            return true;
        }
    }

    public bool TryDeleteVertexArray(WebGlVertexArrayHandle vertexArray, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (vertexArray.Value == 0) return true;
            if (_boundVertexArray == vertexArray.Value)
                _boundVertexArray = null;
            _vertexArrays.Remove(vertexArray.Value);
            return true;
        }
    }

    public bool TryBindVertexArray(WebGlVertexArrayHandle? vertexArray, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (vertexArray is null || vertexArray.Value.Value == 0)
            {
                _boundVertexArray = null;
                return true;
            }
            if (!_vertexArrays.ContainsKey(vertexArray.Value.Value))
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            _boundVertexArray = vertexArray.Value.Value;
            return true;
        }
    }

    public bool TryCreateTransformFeedback(out WebGlTransformFeedbackHandle feedback, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            feedback = default;
            if (!CheckAvailable(out failure)) return false;
            var id = _nextHandle++;
            _transformFeedbacks[id] = new TransformFeedbackEntry(id);
            feedback = new WebGlTransformFeedbackHandle(id);
            return true;
        }
    }

    public bool TryDeleteTransformFeedback(WebGlTransformFeedbackHandle feedback, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (feedback.Value == 0) return true;
            if (_transformFeedbackActive && _boundTransformFeedback == feedback.Value)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            if (_boundTransformFeedback == feedback.Value)
                _boundTransformFeedback = null;
            _transformFeedbacks.Remove(feedback.Value);
            return true;
        }
    }

    public bool TryBindTransformFeedback(uint target, WebGlTransformFeedbackHandle? feedback, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (target != GlTransformFeedback)
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            if (_transformFeedbackActive && !_transformFeedbackPaused)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            if (feedback is null || feedback.Value.Value == 0)
            {
                _boundTransformFeedback = null;
                return true;
            }
            if (!_transformFeedbacks.ContainsKey(feedback.Value.Value))
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            _boundTransformFeedback = feedback.Value.Value;
            return true;
        }
    }

    public bool TryBeginTransformFeedback(uint primitiveMode, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (primitiveMode is not (GlPoints or GlLines or GlTriangles))
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            if (_transformFeedbackActive || _currentProgram is not uint pid || !_programs.TryGetValue(pid, out var prog) || !prog.Linked || prog.LinkedTfVaryings.Count == 0)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            _transformFeedbackActive = true;
            _transformFeedbackPaused = false;
            _transformFeedbackMode = primitiveMode;
            return true;
        }
    }

    public bool TryEndTransformFeedback(out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (!_transformFeedbackActive)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            _transformFeedbackActive = false;
            _transformFeedbackPaused = false;
            return true;
        }
    }

    public bool TryPauseTransformFeedback(out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (!_transformFeedbackActive || _transformFeedbackPaused)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            _transformFeedbackPaused = true;
            return true;
        }
    }

    public bool TryResumeTransformFeedback(out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (!_transformFeedbackActive || !_transformFeedbackPaused)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            _transformFeedbackPaused = false;
            return true;
        }
    }

    public bool TryEnableVertexAttrib(WebGlAttribLocation location, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (location.Value < 0 || location.Value >= MaxVertexAttribs)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            CurrentVao.Attribs[location.Value].Enabled = true;
            return true;
        }
    }

    public bool TryDisableVertexAttrib(WebGlAttribLocation location, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (location.Value < 0 || location.Value >= MaxVertexAttribs)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            CurrentVao.Attribs[location.Value].Enabled = false;
            return true;
        }
    }

    public bool TryVertexAttribPointer(WebGlAttribLocation location, int size, uint type, bool normalized, int stride, int offset, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (location.Value < 0 || location.Value >= MaxVertexAttribs || size is < 1 or > 4 || stride < 0 || stride > 255 || offset < 0)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            if (_boundArrayBuffer is null && offset != 0)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            var compBytes = GetVertexComponentByteSize(type);
            if (compBytes == 0)
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            if (offset % compBytes != 0 || stride % compBytes != 0)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            ref var attr = ref CurrentVao.Attribs[location.Value];
            attr.Size = size;
            attr.Type = type;
            attr.Normalized = normalized;
            attr.IsInteger = false;
            attr.Stride = stride;
            attr.Offset = offset;
            attr.Buffer = _boundArrayBuffer;
            return true;
        }
    }

    public bool TryVertexAttribIPointer(WebGlAttribLocation location, int size, uint type, int stride, int offset, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (location.Value < 0 || location.Value >= MaxVertexAttribs || size is < 1 or > 4 || stride < 0 || stride > 255 || offset < 0)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            if (_boundArrayBuffer is null && offset != 0)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            var compBytes = GetVertexComponentByteSize(type);
            if (compBytes == 0 || type is GlFloat or GlHalfFloat)
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            if (offset % compBytes != 0 || stride % compBytes != 0)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            ref var attr = ref CurrentVao.Attribs[location.Value];
            attr.Size = size;
            attr.Type = type;
            attr.Normalized = false;
            attr.IsInteger = true;
            attr.Stride = stride;
            attr.Offset = offset;
            attr.Buffer = _boundArrayBuffer;
            return true;
        }
    }

    public bool TryVertexAttribDivisor(WebGlAttribLocation location, uint divisor, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (location.Value < 0 || location.Value >= MaxVertexAttribs)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            CurrentVao.Attribs[location.Value].Divisor = divisor;
            return true;
        }
    }

    public bool TrySetVertexAttribConstant(WebGlAttribLocation location, ReadOnlySpan<float> values, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (location.Value < 0 || location.Value >= MaxVertexAttribs)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            ref var c = ref _attribConstants[location.Value];
            c.Kind = WebGlParameterKind.Float32Array;
            c.F0 = values.Length > 0 ? values[0] : 0f;
            c.F1 = values.Length > 1 ? values[1] : 0f;
            c.F2 = values.Length > 2 ? values[2] : 0f;
            c.F3 = values.Length > 3 ? values[3] : 1f;
            return true;
        }
    }

    public bool TrySetVertexAttribIConstant(WebGlAttribLocation location, ReadOnlySpan<int> values, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (location.Value < 0 || location.Value >= MaxVertexAttribs)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            ref var c = ref _attribConstants[location.Value];
            c.Kind = WebGlParameterKind.Int32Array;
            c.I0 = values.Length > 0 ? values[0] : 0;
            c.I1 = values.Length > 1 ? values[1] : 0;
            c.I2 = values.Length > 2 ? values[2] : 0;
            c.I3 = values.Length > 3 ? values[3] : 1;
            return true;
        }
    }

    public bool TrySetVertexAttribUIConstant(WebGlAttribLocation location, ReadOnlySpan<uint> values, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (location.Value < 0 || location.Value >= MaxVertexAttribs)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            ref var c = ref _attribConstants[location.Value];
            c.Kind = WebGlParameterKind.UInt32Array;
            c.U0 = values.Length > 0 ? values[0] : 0u;
            c.U1 = values.Length > 1 ? values[1] : 0u;
            c.U2 = values.Length > 2 ? values[2] : 0u;
            c.U3 = values.Length > 3 ? values[3] : 1u;
            return true;
        }
    }

    public bool TryGetVertexAttrib(WebGlAttribLocation location, uint parameter, out WebGlParameterValue value, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            value = default;
            if (!CheckAvailable(out failure)) return false;
            if (location.Value < 0 || location.Value >= MaxVertexAttribs)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            ref var attr = ref CurrentVao.Attribs[location.Value];
            ref var c = ref _attribConstants[location.Value];
            switch (parameter)
            {
                case 0x8622: // VERTEX_ATTRIB_ARRAY_ENABLED
                    value = new(WebGlParameterKind.Boolean, Boolean: attr.Enabled, Int32: attr.Enabled ? 1 : 0);
                    return true;
                case 0x8623: // VERTEX_ATTRIB_ARRAY_SIZE
                    value = new(WebGlParameterKind.Int32, Int32: attr.Size);
                    return true;
                case 0x8624: // VERTEX_ATTRIB_ARRAY_STRIDE
                    value = new(WebGlParameterKind.Int32, Int32: attr.Stride);
                    return true;
                case 0x8625: // VERTEX_ATTRIB_ARRAY_TYPE
                    value = new(WebGlParameterKind.UInt32, UInt32: attr.Type, Int32: (int)attr.Type);
                    return true;
                case 0x886A: // VERTEX_ATTRIB_ARRAY_NORMALIZED
                    value = new(WebGlParameterKind.Boolean, Boolean: attr.Normalized, Int32: attr.Normalized ? 1 : 0);
                    return true;
                case 0x889F: // VERTEX_ATTRIB_ARRAY_BUFFER_BINDING
                    value = new(WebGlParameterKind.UInt32, UInt32: attr.Buffer ?? 0u, Int32: (int)(attr.Buffer ?? 0u));
                    return true;
                case 0x88FD: // VERTEX_ATTRIB_ARRAY_INTEGER
                    value = new(WebGlParameterKind.Boolean, Boolean: attr.IsInteger, Int32: attr.IsInteger ? 1 : 0);
                    return true;
                case 0x88FE: // VERTEX_ATTRIB_ARRAY_DIVISOR
                    value = new(WebGlParameterKind.UInt32, UInt32: attr.Divisor, Int32: (int)attr.Divisor);
                    return true;
                case 0x8626: // CURRENT_VERTEX_ATTRIB
                    if (c.Kind == WebGlParameterKind.Int32Array)
                        value = new(WebGlParameterKind.Int32Array, Int32Array: [c.I0, c.I1, c.I2, c.I3]);
                    else if (c.Kind == WebGlParameterKind.UInt32Array)
                        value = new(WebGlParameterKind.UInt32Array, UInt32Array: [c.U0, c.U1, c.U2, c.U3]);
                    else
                        value = new(WebGlParameterKind.Float32Array, Float32Array: [c.F0, c.F1, c.F2, c.F3]);
                    return true;
                default:
                    RecordGlError(GlInvalidEnum);
                    return true;
            }
        }
    }

    public bool TryGetVertexAttribOffset(WebGlAttribLocation location, uint parameter, out long offset, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            offset = 0;
            if (!CheckAvailable(out failure)) return false;
            if (location.Value < 0 || location.Value >= MaxVertexAttribs)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            if (parameter != 0x8645) // VERTEX_ATTRIB_ARRAY_POINTER
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            offset = CurrentVao.Attribs[location.Value].Offset;
            return true;
        }
    }

    internal static int GetVertexComponentByteSize(uint type) => type switch
    {
        GlByte or GlUnsignedByte => 1,
        GlShort or GlUnsignedShort or GlHalfFloat or GlHalfFloatOes => 2,
        GlInt or GlUnsignedInt or GlFloat => 4,
        _ => 0,
    };
}
