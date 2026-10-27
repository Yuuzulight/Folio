namespace Folio.Scripting.Gpu;

internal sealed partial class AngleNativeWebGlContext
{
    public bool TryCreateShader(uint type, out WebGlShaderHandle handle, out GpuFailure? failure)
    {
        handle = default; uint raw; if (!TryCreateShader(type, out raw, out failure)) return false; handle = new(raw); return true;
    }
    public bool TrySetShaderSource(WebGlShaderHandle shader, ReadOnlySpan<byte> source, out GpuFailure? failure) => TrySetShaderSource(shader.Value, source, out failure);
    public bool TryCompileShader(WebGlShaderHandle shader, out bool compiled, out string log, out GpuFailure? failure) => TryCompileShader(shader.Value, out compiled, out log, out failure);
    public bool TryGetShaderParameter(WebGlShaderHandle shader, uint parameter, out WebGlParameterValue value, out GpuFailure? failure)
    {
        value = default; if (!TryGetShaderParameter(shader.Value, parameter, out var raw, out failure)) return false; value = IsBooleanParameter(parameter) ? new(WebGlParameterKind.Boolean, Boolean: raw != 0) : new(WebGlParameterKind.Int32, Int32: raw); return true;
    }
    public bool TryGetShaderSource(WebGlShaderHandle shader, out string source, out GpuFailure? failure) => TryGetShaderSource(shader.Value, out source, out failure);
    public bool TryGetShaderInfoLog(WebGlShaderHandle shader, out string log, out GpuFailure? failure) => TryGetShaderInfoLog(shader.Value, out log, out failure);
    public bool TryDeleteShader(WebGlShaderHandle shader, out GpuFailure? failure) => TryDeleteShader(shader.Value, out failure);

    public bool TryCreateProgram(out WebGlProgramHandle handle, out GpuFailure? failure)
    {
        handle = default; uint raw; if (!TryCreateProgram(out raw, out failure)) return false; handle = new(raw); return true;
    }
    public bool TryDeleteProgram(WebGlProgramHandle program, out GpuFailure? failure) => TryDeleteProgram(program.Value, out failure);
    public bool TryAttachShader(WebGlProgramHandle program, WebGlShaderHandle shader, out GpuFailure? failure) => TryAttachShader(program.Value, shader.Value, out failure);
    public bool TryDetachShader(WebGlProgramHandle program, WebGlShaderHandle shader, out GpuFailure? failure) => TryDetachShader(program.Value, shader.Value, out failure);
    public bool TryLinkProgram(WebGlProgramHandle program, out bool linked, out string log, out GpuFailure? failure) => TryLinkProgram(program.Value, out linked, out log, out failure);
    public bool TryValidateProgram(WebGlProgramHandle program, out bool valid, out string log, out GpuFailure? failure) => TryValidateProgram(program.Value, out valid, out log, out failure);
    public bool TryGetProgramParameter(WebGlProgramHandle program, uint parameter, out WebGlParameterValue value, out GpuFailure? failure)
    {
        value = default; if (!TryGetProgramParameter(program.Value, parameter, out var raw, out failure)) return false; value = IsBooleanParameter(parameter) ? new(WebGlParameterKind.Boolean, Boolean: raw != 0) : new(WebGlParameterKind.Int32, Int32: raw); return true;
    }
    public bool TryGetProgramInfoLog(WebGlProgramHandle program, out string log, out GpuFailure? failure) => TryGetProgramInfoLog(program.Value, out log, out failure);
    public bool TryGetActiveAttrib(WebGlProgramHandle program, uint index, out WebGlActiveInfo info, out GpuFailure? failure)
    {
        info = default!; if (!TryGetActiveAttrib(program.Value, index, out var name, out var size, out var type, out failure)) return false; info = new(name, size, type); return true;
    }
    public bool TryGetActiveUniform(WebGlProgramHandle program, uint index, out WebGlActiveInfo info, out GpuFailure? failure)
    {
        info = default!; if (!TryGetActiveUniform(program.Value, index, out var name, out var size, out var type, out failure)) return false; info = new(name, size, type); return true;
    }
    public bool TryBindAttribLocation(WebGlProgramHandle program, uint index, ReadOnlySpan<byte> name, out GpuFailure? failure) => TryBindAttribLocation(program.Value, index, name, out failure);
    public bool TryGetAttachedShaders(WebGlProgramHandle program, Span<WebGlShaderHandle> destination, out int count, out GpuFailure? failure)
    {
        count = 0; var raw = new uint[destination.Length]; if (!TryGetAttachedShaders(program.Value, raw, out count, out failure)) return false; for (var i = 0; i < count; i++) destination[i] = new(raw[i]); return true;
    }
    public bool TryUseProgram(WebGlProgramHandle? program, out GpuFailure? failure) => TryUseProgram(program?.Value, out failure);

    public bool TryCreateBuffer(out WebGlBufferHandle handle, out GpuFailure? failure)
    {
        handle = default; uint raw; if (!TryCreateBuffer(out raw, out failure)) return false; handle = new(raw); return true;
    }
    public bool TryDeleteBuffer(WebGlBufferHandle buffer, out GpuFailure? failure) => TryDeleteBuffer(buffer.Value, out failure);
    public bool TryBindBuffer(uint target, WebGlBufferHandle? buffer, out GpuFailure? failure) => TryBindBuffer(target, buffer?.Value, out failure);
    public bool TryUpdateBuffer(uint target, int byteOffset, ReadOnlySpan<byte> data, out GpuFailure? failure) => TryUpdateBuffer(target, (long)byteOffset, data, out failure);
    public bool TryBindBufferBase(uint target, uint index, WebGlBufferHandle? buffer, out GpuFailure? failure) => TryBindBufferBase(target, index, buffer?.Value, out failure);
    public bool TryBindBufferRange(uint target, uint index, WebGlBufferHandle? buffer, long offset, long size, out GpuFailure? failure) => TryBindBufferRange(target, index, buffer?.Value, offset, size, out failure);
    public bool TryGetUniformIndices(WebGlProgramHandle program, IReadOnlyList<string> names, Span<uint> indices, out GpuFailure? failure) => TryGetUniformIndices(program.Value, names, indices, out failure);
    public bool TryGetActiveUniforms(WebGlProgramHandle program, ReadOnlySpan<uint> indices, uint parameter, Span<int> values, out GpuFailure? failure) => TryGetActiveUniforms(program.Value, indices, parameter, values, out failure);
    public bool TryGetUniformBlockIndex(WebGlProgramHandle program, ReadOnlySpan<byte> name, out uint index, out GpuFailure? failure) => TryGetUniformBlockIndex(program.Value, name, out index, out failure);
    public bool TryUniformBlockBinding(WebGlProgramHandle program, uint index, uint binding, out GpuFailure? failure) => TryUniformBlockBinding(program.Value, index, binding, out failure);
    public bool TryCreateVertexArray(out WebGlVertexArrayHandle handle, out GpuFailure? failure)
    {
        handle = default; uint raw; if (!TryCreateVertexArray(out raw, out failure)) return false; handle = new(raw); return true;
    }
    public bool TryDeleteVertexArray(WebGlVertexArrayHandle handle, out GpuFailure? failure) => TryDeleteVertexArray(handle.Value, out failure);
    public bool TryBindVertexArray(WebGlVertexArrayHandle? handle, out GpuFailure? failure) => TryBindVertexArray(handle?.Value, out failure);

    private static bool IsBooleanParameter(uint parameter) => parameter is 0x8B81 or 0x8B82 or 0x8B83 or 0x8B86 or 0x8B87 or 0x8B88;
}
