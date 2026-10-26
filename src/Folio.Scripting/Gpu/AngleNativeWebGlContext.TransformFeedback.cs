using System.Runtime.InteropServices;
using System.Text;

namespace Folio.Scripting.Gpu;

internal sealed partial class AngleNativeWebGlContext
{
    private const uint GlTransformFeedback = 0x8E22;
    private const uint GlPoints = 0x0000;
    private const uint GlLines = 0x0001;
    private const uint GlTriangles = 0x0004;

    public bool TryCreateTransformFeedback(out WebGlTransformFeedbackHandle handle, out GpuFailure? failure)
    {
        handle = default;
        lock (_gate)
        {
            failure = null;
            if (!IsWebGl2(out failure) || !Begin(out failure)) return false;
            try
            {
                _gl.GenTransformFeedbacks(1, out var raw);
                if (raw == 0 || !CheckError(out failure)) return false;
                _transformFeedbacks.Add(raw);
                handle = new(raw);
                return true;
            }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { End(); }
        }
    }

    public bool TryDeleteTransformFeedback(WebGlTransformFeedbackHandle handle, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (handle.Value == 0) return true;
            if (!_transformFeedbacks.Contains(handle.Value)) { failure = NativeFailure("The transform-feedback handle is invalid for this context."); return false; }
            if (!IsWebGl2(out failure) || !Begin(out failure)) return false;
            try { var raw = handle.Value; _gl.DeleteTransformFeedbacks(1, ref raw); if (!CheckError(out failure)) return false; _transformFeedbacks.Remove(handle.Value); return true; }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { End(); }
        }
    }

    public bool TryBindTransformFeedback(uint target, WebGlTransformFeedbackHandle? handle, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (!IsWebGl2(out failure) || target != GlTransformFeedback) { failure ??= NativeFailure("The transform-feedback target is invalid."); return false; }
            if (handle is { } value && !_transformFeedbacks.Contains(value.Value)) { failure = NativeFailure("The transform-feedback handle is invalid for this context."); return false; }
            if (!Begin(out failure)) return false;
            try { _gl.BindTransformFeedback(target, handle?.Value ?? 0); return CheckError(out failure); }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { End(); }
        }
    }

    public bool TryBeginTransformFeedback(uint primitiveMode, out GpuFailure? failure) => TransformFeedbackStateCall(() => _gl.BeginTransformFeedback(primitiveMode), primitiveMode is GlPoints or GlLines or GlTriangles, out failure);
    public bool TryEndTransformFeedback(out GpuFailure? failure) => TransformFeedbackStateCall(() => _gl.EndTransformFeedback(), true, out failure);
    public bool TryPauseTransformFeedback(out GpuFailure? failure) => TransformFeedbackStateCall(() => _gl.PauseTransformFeedback(), true, out failure);
    public bool TryResumeTransformFeedback(out GpuFailure? failure) => TransformFeedbackStateCall(() => _gl.ResumeTransformFeedback(), true, out failure);

    public bool TryTransformFeedbackVaryings(WebGlProgramHandle program, IReadOnlyList<string> varyings, uint bufferMode, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (!IsWebGl2(out failure) || !_programs.Contains(program.Value) || varyings.Count == 0 || varyings.Count > 64 || bufferMode is not (0x8C8C or 0x8C8D))
            { failure ??= NativeFailure("The transform-feedback varying list is invalid."); return false; }
            if (!Begin(out failure)) return false;
            var strings = new IntPtr[varyings.Count];
            var array = IntPtr.Zero;
            try
            {
                for (var i = 0; i < strings.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(varyings[i])) { failure = NativeFailure("A transform-feedback varying name is empty."); return false; }
                    strings[i] = Marshal.StringToCoTaskMemUTF8(varyings[i]);
                }
                array = Marshal.AllocHGlobal(IntPtr.Size * strings.Length);
                for (var i = 0; i < strings.Length; i++) Marshal.WriteIntPtr(array, i * IntPtr.Size, strings[i]);
                _gl.TransformFeedbackVaryings(program.Value, strings.Length, array, bufferMode);
                return CheckError(out failure);
            }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally
            {
                if (array != IntPtr.Zero) Marshal.FreeHGlobal(array);
                foreach (var item in strings) if (item != IntPtr.Zero) Marshal.FreeCoTaskMem(item);
                End();
            }
        }
    }

    public bool TryGetTransformFeedbackVarying(WebGlProgramHandle program, uint index, out WebGlActiveInfo info, out GpuFailure? failure)
    {
        info = default!;
        lock (_gate)
        {
            failure = null;
            if (!IsWebGl2(out failure) || !_programs.Contains(program.Value) || !Begin(out failure)) return false;
            var buffer = Marshal.AllocHGlobal(512);
            try
            {
                _gl.GetTransformFeedbackVarying(program.Value, index, 512, out var length, out var size, out var type, buffer);
                if (!CheckError(out failure)) return false;
                length = Math.Clamp(length, 0, 511);
                var bytes = new byte[length];
                Marshal.Copy(buffer, bytes, 0, length);
                info = new(Encoding.UTF8.GetString(bytes), size, type);
                return true;
            }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { Marshal.FreeHGlobal(buffer); End(); }
        }
    }

    private bool TransformFeedbackStateCall(Action action, bool valid, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (!IsWebGl2(out failure) || !valid) { failure ??= NativeFailure("The transform-feedback operation is invalid."); return false; }
            return StateCall(action, out failure);
        }
    }

    private bool IsWebGl2(out GpuFailure? failure)
    {
        failure = Options.Version == WebGlContextVersion.WebGl2 ? null : NativeFailure("Transform feedback requires a WebGL 2 context.");
        return failure is null;
    }
}
