
using System.Runtime.InteropServices;

namespace Folio.Scripting.Gpu;

internal sealed partial class AngleNativeWebGlContext
{
    internal bool TryGetParameterVector(uint parameter, Span<int> values, out GpuFailure? failure)
    {
        lock (_gate)
        {
            var required = parameter switch { 0x0BA2 or 0x0C10 or 0x0C23 => 4, 0x0D3A or 0x846D or 0x846E => 2, _ => 0 };
            failure = null; if (required == 0 || values.Length != required) { failure = NativeFailure("The integer vector query is invalid."); return false; }
            if (!Begin(out failure)) return false; var memory = Marshal.AllocHGlobal(sizeof(int) * values.Length);
            try { _gl.GetIntegervArray(parameter, memory); if (!CheckError(out failure)) return false; for (var i = 0; i < values.Length; i++) values[i] = Marshal.ReadInt32(memory, i * sizeof(int)); return true; }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; } finally { Marshal.FreeHGlobal(memory); End(); }
        }
    }

    internal bool TryGetVertexAttribCurrent(uint index, Span<float> values, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null; if (index > 32 || values.Length != 4) { failure = NativeFailure("The current vertex attribute query is invalid."); return false; }
            _vertexAttribConstants.AsSpan(checked((int)index * 4), 4).CopyTo(values); return true;
        }
    }
}
