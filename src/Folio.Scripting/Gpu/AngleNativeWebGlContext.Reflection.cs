using System.Runtime.InteropServices;
using System.Text;

namespace Folio.Scripting.Gpu;

internal sealed partial class AngleNativeWebGlContext
{
    internal bool TryGetActiveUniform(uint program, uint index, out string name, out int size, out uint type, out GpuFailure? failure)
    {
        return TryGetActive(program, index, false, out name, out size, out type, out failure);
    }

    internal bool TryGetActiveAttrib(uint program, uint index, out string name, out int size, out uint type, out GpuFailure? failure)
    {
        return TryGetActive(program, index, true, out name, out size, out type, out failure);
    }

    private bool TryGetActive(uint program, uint index, bool attrib, out string name, out int size, out uint type, out GpuFailure? failure)
    {
        name = string.Empty; size = 0; type = 0; failure = null;
        lock (_gate)
        {
            if (!_programs.Contains(program)) { failure = NativeFailure("The program handle is invalid for this context."); return false; }
            if (!Begin(out failure)) return false;
            IntPtr memory = IntPtr.Zero;
            try
            {
                _gl.GetProgramiv(program, attrib ? 0x8B8Au : 0x8B87u, out var capacity);
                if (!CheckError(out failure)) return false;
                if (capacity < 1 || capacity > (long)Options.MaxShaderSourceBytes + 1)
                {
                    failure = new(GpuFailureKind.BudgetExceeded, "The active-variable name exceeds the configured source limit.");
                    return false;
                }
                memory = Marshal.AllocHGlobal(capacity);
                int length;
                if (attrib) _gl.GetActiveAttrib(program, index, capacity, out length, out size, out type, memory);
                else _gl.GetActiveUniform(program, index, capacity, out length, out size, out type, memory);
                if (!CheckError(out failure)) return false;
                length = Math.Clamp(length, 0, capacity - 1); name = Marshal.PtrToStringUTF8(memory, length) ?? string.Empty; return true;
            }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { if (memory != IntPtr.Zero) Marshal.FreeHGlobal(memory); End(); }
        }
    }

    internal bool TryGetShaderPrecision(uint shaderType, uint precisionType, out int rangeMin, out int rangeMax, out int precision, out GpuFailure? failure)
    {
        rangeMin = rangeMax = precision = 0; failure = null;
        lock (_gate)
        {
            if (!Begin(out failure)) return false;
            var range = Marshal.AllocHGlobal(sizeof(int) * 2); var bits = Marshal.AllocHGlobal(sizeof(int));
            try { _gl.GetShaderPrecisionFormat(shaderType, precisionType, range, bits); if (!CheckError(out failure)) return false; rangeMin = Marshal.ReadInt32(range); rangeMax = Marshal.ReadInt32(range, sizeof(int)); precision = Marshal.ReadInt32(bits); return true; }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { Marshal.FreeHGlobal(range); Marshal.FreeHGlobal(bits); End(); }
        }
    }

    internal bool TryGetError(out uint error)
    {
        lock (_gate)
        {
            error = 0; if (!Begin(out _)) return false;
            try { error = _gl.GetError(); return true; }
            catch { return false; }
            finally { End(); }
        }
    }
}
