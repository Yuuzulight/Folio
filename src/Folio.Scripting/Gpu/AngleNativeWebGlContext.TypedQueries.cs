namespace Folio.Scripting.Gpu;

internal sealed partial class AngleNativeWebGlContext
{
    public bool TryGetParameter(uint parameter, out WebGlParameterValue value, out GpuFailure? failure)
    {
        value = default;
        if (parameter is 0x0BE2 or 0x0B71 or 0x0B44 or 0x0C11 or 0x0B90 or 0x0BD0 or 0x8037 or 0x80A0 or 0x809E or 0x80AB or 0x8C89 or 0x0B72) { if (!TryGetBooleanParameter(parameter, out var enabled, out failure)) return false; value = new(WebGlParameterKind.Boolean, Boolean: enabled); return true; }
        if (parameter == 0x0C23) { Span<bool> mask = stackalloc bool[4]; if (!TryGetParameterBooleanVector(parameter, mask, out failure)) return false; value = new(WebGlParameterKind.BooleanArray, Int32Array: mask.ToArray().Select(static x => x ? 1 : 0).ToArray()); return true; }
        if (parameter is 0x1F00 or 0x1F01 or 0x1F02 or 0x8B8C) { if (!TryGetStringParameter(parameter, out var text, out failure)) return false; if (parameter == 0x1F02) text = $"WebGL {(Options.Version == WebGlContextVersion.WebGl2 ? "2.0" : "1.0")} (OpenGL ES {text})"; else if (parameter == 0x8B8C) text = $"WebGL GLSL ES {(Options.Version == WebGlContextVersion.WebGl2 ? "3.00" : "1.00")} ({text})"; value = new(WebGlParameterKind.String, String: text); return true; }
        if (parameter is 0x0BA2 or 0x0C10 or 0x0D3A)
        {
            var width = parameter == 0x0D3A ? 2 : 4; Span<int> vector = stackalloc int[4]; if (!TryGetParameterVector(parameter, vector[..width], out failure)) return false; value = new(WebGlParameterKind.Int32Array, Int32Array: vector[..width].ToArray()); return true;
        }
        if (parameter is 0x0C22 or 0x0B70 or 0x846D or 0x846E) { Span<float> vector = stackalloc float[4]; var width = parameter == 0x0C22 ? 4 : 2; if (!TryGetFloatVector(parameter, vector[..width], out failure)) return false; value = new(WebGlParameterKind.Float32Array, Float32Array: vector[..width].ToArray()); return true; }
        if (parameter is 0x0B21 or 0x0B73) { Span<float> scalar = stackalloc float[1]; if (!TryGetFloatVector(parameter, scalar, out failure)) return false; value = new(WebGlParameterKind.Float32, Float32: scalar[0]); return true; }
        int scalarValue; if (!TryGetParameter(parameter, out scalarValue, out failure)) return false; value = new(WebGlParameterKind.Int32, Int32: scalarValue); return true;
    }

    public bool TryGetBufferParameter(uint target, uint parameter, out WebGlParameterValue value, out GpuFailure? failure)
    {
        value = default; int scalar; if (!TryGetBufferParameter(target, parameter, out scalar, out failure)) return false; value = new(WebGlParameterKind.Int32, Int32: scalar); return true;
    }

    public bool TryGetVertexAttrib(WebGlAttribLocation location, uint parameter, out WebGlParameterValue value, out GpuFailure? failure)
    {
        value = default;
        if (parameter == 0x8626)
        {
            Span<float> current = stackalloc float[4]; if (!TryGetVertexAttribCurrent((uint)location.Value, current, out failure)) return false; value = new(WebGlParameterKind.Float32Array, Float32Array: current.ToArray()); return true;
        }
        if (parameter is 0x8622 or 0x886A or 0x88FD) { if (!TryGetVertexAttrib((uint)location.Value, parameter, out var scalar, out failure)) return false; value = new(WebGlParameterKind.Boolean, Boolean: scalar != 0); return true; }
        if (!TryGetVertexAttrib((uint)location.Value, parameter, out var scalarValue, out failure)) return false; value = new(WebGlParameterKind.Int32, Int32: scalarValue); return true;
    }
}
