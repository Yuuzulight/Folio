using System.Text;

namespace Folio.Scripting.Gpu;

internal sealed partial class AngleNativeWebGlContext
{
    public bool TryGetAttribLocation(WebGlProgramHandle program, string name, out WebGlAttribLocation location, out GpuFailure? failure)
    {
        location = default; var bytes = Encoding.UTF8.GetBytes(name); if (!TryGetAttribLocation(program.Value, bytes, out var raw, out failure)) return false; location = new(raw); return true;
    }
    public bool TryGetUniformLocation(WebGlProgramHandle program, string name, out WebGlUniformLocation location, out GpuFailure? failure)
    {
        location = default; var bytes = Encoding.UTF8.GetBytes(name); if (!TryGetUniformLocation(program.Value, bytes, out var raw, out failure)) return false; location = new(raw); return true;
    }
    public bool TryUniformFloat(WebGlUniformLocation location, int components, ReadOnlySpan<float> values, out GpuFailure? failure) => TryUniformFloats(location.Value, components, values, out failure);
    public bool TryUniformInt(WebGlUniformLocation location, int components, ReadOnlySpan<int> values, out GpuFailure? failure) => TryUniformIntegers(location.Value, components, values, out failure);
    public bool TryUniformUInt(WebGlUniformLocation location, int components, ReadOnlySpan<uint> values, out GpuFailure? failure) => TryUniformUnsigned(location.Value, components, values, out failure);
    public bool TryUniformMatrix(WebGlUniformLocation location, int columns, int rows, ReadOnlySpan<float> values, bool transpose, out GpuFailure? failure) => TryUniformMatrix(location.Value, columns, rows, values, transpose, out failure);

    public bool TryGetUniform(WebGlProgramHandle program, WebGlUniformLocation location, out WebGlUniformValue value, out GpuFailure? failure)
    {
        value = default;
        if (!TryGetUniformShape(program.Value, location.Value, out var type, out var components, out failure)) return false;
        var count = Math.Max(1, components);
        if (type is 0x1404 or 0x8B53 or 0x8B56 or 0x8B57)
        {
            var data = new int[count]; if (!TryGetUniformIntegers(program.Value, location.Value, data, out failure)) return false; value = new(WebGlParameterKind.Int32, Int32: data); return true;
        }
        if (type is 0x1405 or 0x8DC6 or 0x8DC7 or 0x8DC8)
        {
            var data = new uint[count]; if (!TryGetUniformUnsigned(program.Value, location.Value, data, out failure)) return false; value = new(WebGlParameterKind.UInt32, UInt32: data); return true;
        }
        var floats = new float[count]; if (!TryGetUniformFloats(program.Value, location.Value, floats, out failure)) return false; value = new(WebGlParameterKind.Float32, Float32: floats); return true;
    }

    public bool TryEnableVertexAttrib(WebGlAttribLocation location, out GpuFailure? failure) => TryEnableVertexAttrib((uint)location.Value, true, out failure);
    public bool TryDisableVertexAttrib(WebGlAttribLocation location, out GpuFailure? failure) => TryEnableVertexAttrib((uint)location.Value, false, out failure);
    public bool TryVertexAttribPointer(WebGlAttribLocation location, int size, uint type, bool normalized, int stride, int offset, out GpuFailure? failure) => TryVertexAttribPointer((uint)location.Value, size, type, normalized, stride, offset, out failure);
    public bool TryVertexAttribIPointer(WebGlAttribLocation location, int size, uint type, int stride, int offset, out GpuFailure? failure) => TryVertexAttribPointer((uint)location.Value, size, type, false, stride, offset, out failure, true);
    public bool TryVertexAttribDivisor(WebGlAttribLocation location, uint divisor, out GpuFailure? failure) => StateCall(() => _gl.VertexAttribDivisor((uint)location.Value, divisor), out failure);
    public bool TrySetVertexAttribConstant(WebGlAttribLocation location, ReadOnlySpan<float> values, out GpuFailure? failure) => TrySetVertexAttribConstant((uint)location.Value, values, out failure);
    public bool TrySetVertexAttribIConstant(WebGlAttribLocation location, ReadOnlySpan<int> values, out GpuFailure? failure) => TrySetVertexAttribIConstant((uint)location.Value, values, out failure);
    public bool TrySetVertexAttribUIConstant(WebGlAttribLocation location, ReadOnlySpan<uint> values, out GpuFailure? failure) => TrySetVertexAttribUIConstant((uint)location.Value, values, out failure);
    public bool TryGetVertexAttribOffset(WebGlAttribLocation location, uint parameter, out long offset, out GpuFailure? failure)
    {
        offset = 0; if (!TryGetVertexAttribOffset((uint)location.Value, parameter, out var raw, out failure)) return false; offset = raw.ToInt64(); return true;
    }
}
