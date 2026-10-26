
using System.Runtime.InteropServices;

namespace Folio.Scripting.Gpu;

internal sealed partial class AngleNativeWebGlContext
{
    public bool TryGetUniformIndices(uint program, IReadOnlyList<string> names, Span<uint> indices, out GpuFailure? failure)
    {
        failure = null;
        if (names.Count == 0 || names.Count > indices.Length || !_programs.Contains(program)) { failure = NativeFailure("The uniform index query is invalid."); return false; }
        return TryGetUniformIndicesCore(program, names, indices, out failure);
    }

    private bool TryGetUniformIndicesCore(uint program, IReadOnlyList<string> names, Span<uint> indices, out GpuFailure? failure)
    {
        failure = null; lock (_gate) { if (!Begin(out failure)) return false; var pointers = Marshal.AllocHGlobal(IntPtr.Size * names.Count); var allocations = new IntPtr[names.Count]; var indexPtr = Marshal.AllocHGlobal(sizeof(uint) * names.Count); try { for (var i = 0; i < names.Count; i++) { allocations[i] = Marshal.StringToHGlobalAnsi(names[i]); Marshal.WriteIntPtr(pointers, i * IntPtr.Size, allocations[i]); } _gl.GetUniformIndices(program, names.Count, pointers, indexPtr); if (!CheckError(out failure)) return false; for (var i = 0; i < names.Count; i++) indices[i] = unchecked((uint)Marshal.ReadInt32(indexPtr, i * sizeof(uint))); return true; } catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; } finally { foreach (var allocation in allocations) if (allocation != IntPtr.Zero) Marshal.FreeHGlobal(allocation); Marshal.FreeHGlobal(pointers); Marshal.FreeHGlobal(indexPtr); End(); } }
    }

    public bool TryGetActiveUniforms(uint program, ReadOnlySpan<uint> indices, uint parameter, Span<int> values, out GpuFailure? failure)
    {
        failure = null; if (indices.Length == 0 || values.Length < indices.Length || !_programs.Contains(program)) { failure = NativeFailure("The active uniform query is invalid."); return false; }
        lock (_gate) { if (!Begin(out failure)) return false; var ip = Marshal.AllocHGlobal(sizeof(uint) * indices.Length); var vp = Marshal.AllocHGlobal(sizeof(int) * indices.Length); try { for (var i = 0; i < indices.Length; i++) Marshal.WriteInt32(ip, i * sizeof(uint), unchecked((int)indices[i])); _gl.GetActiveUniformsiv(program, indices.Length, ip, parameter, vp); if (!CheckError(out failure)) return false; for (var i = 0; i < indices.Length; i++) values[i] = Marshal.ReadInt32(vp, i * sizeof(int)); return true; } catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; } finally { Marshal.FreeHGlobal(ip); Marshal.FreeHGlobal(vp); End(); } } 
    }
}
