
using System.Runtime.InteropServices;

namespace Folio.Scripting.Gpu;

internal sealed partial class AngleNativeWebGlContext
{
    private const uint GlInvalidEnum = 0x0500;
    private const uint GlInvalidValue = 0x0501;
    public bool TryGetBooleanParameter(uint parameter, out bool value, out GpuFailure? failure)
    {
        lock (_gate) { value = false; failure = null; if (parameter is not (0x0BE2 or 0x0B71 or 0x0B44 or 0x0C11 or 0x0B90 or 0x0BD0 or 0x8037 or 0x80A0 or 0x809E or 0x80AB or 0x8C89 or 0x0B72)) { failure = NativeFailure("The capability query is invalid.", GlInvalidEnum); return false; } if (!Begin(out failure)) return false; try { _gl.GetBooleanv(parameter, out var raw); value = raw != 0; return CheckError(out failure); } catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; } finally { End(); } }
    }
    public bool TryGetFloatVector(uint parameter, Span<float> values, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            var required = parameter switch { 0x0C22u => 4, 0x0B73u or 0x0B21u => 1, 0x846Du or 0x846Eu => 2, _ => 0 };
            if (required == 0) { failure = NativeFailure("The float vector query parameter is invalid.", GlInvalidEnum); return false; }
            if (values.Length != required) { failure = NativeFailure("The float vector query destination width is invalid.", GlInvalidValue); return false; }
            if (!Begin(out failure)) return false;
            var memory = Marshal.AllocHGlobal(sizeof(float) * required);
            try { _gl.GetFloatvArray(parameter, memory); if (!CheckError(out failure)) return false; for (var i = 0; i < required; i++) values[i] = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(memory, i * sizeof(float))); return true; }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; } finally { Marshal.FreeHGlobal(memory); End(); }
        }
    }
    public bool TryGetStringParameter(uint parameter, out string value, out GpuFailure? failure)
    {
        lock (_gate) { value = string.Empty; failure = null; if (parameter is not (0x1F00 or 0x1F01 or 0x1F02 or 0x8B8C)) { failure = NativeFailure("The string query is invalid.", GlInvalidEnum); return false; } if (!Begin(out failure)) return false; try { value = Marshal.PtrToStringAnsi(_gl.GetString(parameter)) ?? string.Empty; return CheckError(out failure); } catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; } finally { End(); } }
    }
}
