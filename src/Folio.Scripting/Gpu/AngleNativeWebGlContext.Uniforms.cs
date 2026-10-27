using System.Runtime.InteropServices;

namespace Folio.Scripting.Gpu;

internal sealed partial class AngleNativeWebGlContext
{
    private bool TryUniform(int location, int components, ReadOnlySpan<float> values, out GpuFailure? failure)
    {
        failure = null;
        if (location < 0 || components is < 1 or > 4 || values.Length % components != 0) { failure = NativeFailure("The float uniform payload is invalid."); return false; }
        var copy = values.ToArray();
        Action<IntPtr, int> call = components switch { 1 => (p, n) => _gl.Uniform1fv(location, n, p), 2 => (p, n) => _gl.Uniform2fv(location, n, p), 3 => (p, n) => _gl.Uniform3fv(location, n, p), _ => (p, n) => _gl.Uniform4fv(location, n, p) };
        return UniformCall(location, copy.Length / components, copy, call, out failure);
    }

    private bool TryUniform(int location, int components, ReadOnlySpan<int> values, out GpuFailure? failure)
    {
        failure = null;
        if (location < 0 || components is < 1 or > 4 || values.Length % components != 0) { failure = NativeFailure("The integer uniform payload is invalid."); return false; }
        var copy = values.ToArray();
        Action<IntPtr, int> call = components switch { 1 => (p, n) => _gl.Uniform1iv(location, n, p), 2 => (p, n) => _gl.Uniform2iv(location, n, p), 3 => (p, n) => _gl.Uniform3iv(location, n, p), _ => (p, n) => _gl.Uniform4iv(location, n, p) };
        return UniformCall(location, copy.Length / components, copy, call, out failure);
    }

    private bool TryUniform(int location, int components, ReadOnlySpan<uint> values, out GpuFailure? failure)
    {
        failure = null;
        if (location < 0 || components is < 1 or > 4 || values.Length % components != 0) { failure = NativeFailure("The unsigned uniform payload is invalid."); return false; }
        var copy = values.ToArray();
        Action<IntPtr, int> call = components switch { 1 => (p, n) => _gl.Uniform1uiv(location, n, p), 2 => (p, n) => _gl.Uniform2uiv(location, n, p), 3 => (p, n) => _gl.Uniform3uiv(location, n, p), _ => (p, n) => _gl.Uniform4uiv(location, n, p) };
        return UniformCall(location, copy.Length / components, copy, call, out failure);
    }

    internal bool TryUniformFloats(int location, int components, ReadOnlySpan<float> values, out GpuFailure? failure) => TryUniform(location, components, values, out failure);
    internal bool TryUniformIntegers(int location, int components, ReadOnlySpan<int> values, out GpuFailure? failure) => TryUniform(location, components, values, out failure);
    internal bool TryUniformUnsigned(int location, int components, ReadOnlySpan<uint> values, out GpuFailure? failure) => TryUniform(location, components, values, out failure);

    internal bool TryUniformMatrix(int location, int columns, int rows, ReadOnlySpan<float> values, bool transpose, out GpuFailure? failure)
    {
        failure = null;
        if (location < 0 || columns is < 2 or > 4 || rows is < 2 or > 4 || values.Length % (columns * rows) != 0) { failure = NativeFailure("The matrix uniform payload is invalid."); return false; }
        var copy = values.ToArray(); var count = copy.Length / (columns * rows); var t = transpose ? (byte)1 : (byte)0;
        Action<IntPtr, int> call = (columns, rows) switch
        {
            (2, 2) => (p, n) => _gl.UniformMatrix2fv(location, n, t, p), (3, 3) => (p, n) => _gl.UniformMatrix3fv(location, n, t, p), (4, 4) => (p, n) => _gl.UniformMatrix4fv(location, n, t, p),
            (2, 3) => (p, n) => _gl.UniformMatrix2x3fv(location, n, t, p), (3, 2) => (p, n) => _gl.UniformMatrix3x2fv(location, n, t, p), (2, 4) => (p, n) => _gl.UniformMatrix2x4fv(location, n, t, p),
            (4, 2) => (p, n) => _gl.UniformMatrix4x2fv(location, n, t, p), (3, 4) => (p, n) => _gl.UniformMatrix3x4fv(location, n, t, p), _ => (p, n) => _gl.UniformMatrix4x3fv(location, n, t, p)
        };
        return UniformCall(location, count, copy, call, out failure);
    }

    private bool UniformCall<T>(int location, int count, T[] values, Action<IntPtr, int> call, out GpuFailure? failure) where T : struct
    {
        lock (_gate)
        {
            failure = null; if (!Begin(out failure)) return false;
            GCHandle pinned = default;
            try { pinned = GCHandle.Alloc(values, GCHandleType.Pinned); call(pinned.AddrOfPinnedObject(), count); return CheckError(out failure); }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { if (pinned.IsAllocated) pinned.Free(); End(); }
        }
    }

    internal bool TryGetUniformFloats(uint program, int location, Span<float> destination, out GpuFailure? failure) => GetUniform(program, location, destination, (p, l, v) => _gl.GetUniformfv(p, l, v), out failure);
    internal bool TryGetUniformIntegers(uint program, int location, Span<int> destination, out GpuFailure? failure) => GetUniform(program, location, destination, (p, l, v) => _gl.GetUniformiv(p, l, v), out failure);
    internal bool TryGetUniformUnsigned(uint program, int location, Span<uint> destination, out GpuFailure? failure) => GetUniform(program, location, destination, (p, l, v) => _gl.GetUniformuiv(p, l, v), out failure);

    private bool GetUniform<T>(uint program, int location, Span<T> destination, Action<uint, int, IntPtr> call, out GpuFailure? failure) where T : struct
    {
        lock (_gate)
        {
            failure = null; if (location < 0 || destination.Length == 0) { failure = NativeFailure("The uniform query is invalid."); return false; }
            if (!_programs.Contains(program)) { failure = NativeFailure("The program handle is invalid for this context."); return false; }
            if (!TryGetUniformShape(program, location, out _, out var components, out failure)) return false;
            if (destination.Length < components) { failure = NativeFailure("The uniform destination is too small."); return false; }
            if (!Begin(out failure)) return false;
            var values = new T[components]; GCHandle pinned = default;
            try { pinned = GCHandle.Alloc(values, GCHandleType.Pinned); call(program, location, pinned.AddrOfPinnedObject()); if (!CheckError(out failure)) return false; values.AsSpan().CopyTo(destination); return true; }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { if (pinned.IsAllocated) pinned.Free(); End(); }
        }
    }

    internal bool TryGetUniformShape(uint program, int location, out uint type, out int components, out GpuFailure? failure)
    {
        lock (_gate)
        {
            type = 0; components = 0; failure = null;
            if (!_programs.Contains(program) || location < 0) { failure = NativeFailure("The uniform location is invalid."); return false; }
            if (!Begin(out failure)) return false;
            int count;
            try { _gl.GetProgramiv(program, GlActiveUniforms, out count); if (!CheckError(out failure)) return false; }
            finally { End(); }
            for (uint index = 0; index < count; index++)
            {
                if (!TryGetActiveUniform(program, index, out var name, out var size, out var candidateType, out failure)) return false;
                for (var element = 0; element < size; element++)
                {
                    var marker = name.IndexOf("[0]", StringComparison.Ordinal);
                    var elementName = element == 0 ? name : marker >= 0
                        ? name[..marker] + $"[{element}]" + name[(marker + 3)..]
                        : name + $"[{element}]";
                    if (!TryGetUniformLocation(program, System.Text.Encoding.UTF8.GetBytes(elementName), out var candidate, out failure)) return false;
                    if (candidate != location) continue;
                    type = candidateType;
                    components = type switch
                    {
                        0x8B50 or 0x8B53 or 0x8B57 or 0x8DC6 => 2,
                        0x8B51 or 0x8B54 or 0x8B58 or 0x8DC7 => 3,
                        0x8B52 or 0x8B55 or 0x8B59 or 0x8DC8 or 0x8B5A => 4,
                        0x8B65 or 0x8B67 => 6,
                        0x8B66 or 0x8B69 => 8,
                        0x8B5B => 9,
                        0x8B68 or 0x8B6A => 12,
                        0x8B5C => 16,
                        _ => 1,
                    };
                    return true;
                }
            }
            failure = NativeFailure("The location is not an active uniform of this program.");
            return false;
        }
    }
}
