using System.Runtime.InteropServices;

namespace Folio.Scripting.Gpu;

internal sealed partial class AngleNativeWebGlContext
{
    private delegate void LogReader(int maxLength, out int written, IntPtr pointer);
    private const uint GlCompileStatus = 0x8B81;
    private const uint GlInfoLogLength = 0x8B84;
    private const uint GlShaderSourceLength = 0x8B88;
    private const uint GlLinkStatus = 0x8B82;
    private const uint GlValidateStatus = 0x8B83;
    private const uint GlAttachedShaders = 0x8B85;
    private const uint GlActiveAttributes = 0x8B89;
    private const uint GlActiveUniforms = 0x8B86;
    private const uint GlTrue = 1;

    public bool TryCreateShader(uint shaderType, out uint shader, out GpuFailure? failure)
    {
        lock (_gate)
        {
            shader = 0; failure = null;
            if (!Begin(out failure)) return false;
            try
            {
                shader = _gl.CreateShader(shaderType);
                if (shader == 0) { failure = NativeFailure("glCreateShader returned zero."); return false; }
                _shaders.Add(shader);
                return true;
            }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { End(); }
        }
    }

    public bool TrySetShaderSource(uint shader, ReadOnlySpan<byte> utf8Source, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (!_shaders.Contains(shader)) { failure = new(GpuFailureKind.NativeError, "The shader handle is invalid for this context."); return false; }
            if (utf8Source.Length > Options.MaxShaderSourceBytes) { failure = new(GpuFailureKind.BudgetExceeded, "Shader source exceeds the configured source limit."); return false; }
            if (!Begin(out failure)) return false;
            IntPtr source = IntPtr.Zero, pointers = IntPtr.Zero, lengths = IntPtr.Zero;
            try
            {
                source = Marshal.AllocHGlobal(checked(utf8Source.Length + 1));
                var bytes = utf8Source.ToArray();
                Marshal.Copy(bytes, 0, source, bytes.Length);
                Marshal.WriteByte(source, bytes.Length, 0);
                pointers = Marshal.AllocHGlobal(IntPtr.Size);
                lengths = Marshal.AllocHGlobal(sizeof(int));
                Marshal.WriteIntPtr(pointers, source);
                Marshal.WriteInt32(lengths, bytes.Length);
                _gl.ShaderSource(shader, 1, pointers, lengths);
                return CheckError(out failure);
            }
            catch (OverflowException) { failure = new(GpuFailureKind.BudgetExceeded, "Shader source length overflowed."); return false; }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { if (lengths != IntPtr.Zero) Marshal.FreeHGlobal(lengths); if (pointers != IntPtr.Zero) Marshal.FreeHGlobal(pointers); if (source != IntPtr.Zero) Marshal.FreeHGlobal(source); End(); }
        }
    }

    public bool TryCompileShader(uint shader, out bool compiled, out string infoLog, out GpuFailure? failure)
    {
        lock (_gate)
        {
            compiled = false; infoLog = ""; failure = null;
            if (!_shaders.Contains(shader)) { failure = new(GpuFailureKind.NativeError, "The shader handle is invalid for this context."); return false; }
            if (!Begin(out failure)) return false;
            try
            {
                _gl.CompileShader(shader);
                _gl.GetShaderiv(shader, GlCompileStatus, out var status);
                compiled = status == GlTrue;
                infoLog = ReadShaderLog(shader);
                return CheckError(out failure);
            }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { End(); }
        }
    }

    public bool TryGetShaderParameter(uint shader, uint parameter, out int value, out GpuFailure? failure)
    {
        lock (_gate)
        {
            value = 0; failure = null;
            if (!_shaders.Contains(shader)) { failure = new(GpuFailureKind.NativeError, "The shader handle is invalid for this context."); return false; }
            if (!Begin(out failure)) return false;
            try { _gl.GetShaderiv(shader, parameter, out value); return CheckError(out failure); }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { End(); }
        }
    }

    public bool TryGetShaderSource(uint shader, out string sourceText, out GpuFailure? failure)
    {
        lock (_gate)
        {
            sourceText = ""; failure = null;
            if (!_shaders.Contains(shader)) { failure = new(GpuFailureKind.NativeError, "The shader handle is invalid for this context."); return false; }
            if (!Begin(out failure)) return false;
            try
            {
                _gl.GetShaderiv(shader, GlShaderSourceLength, out var length);
                if (length <= 0) return true;
                var memory = Marshal.AllocHGlobal(length);
                try { _gl.GetShaderSource(shader, length, out var written, memory); sourceText = Marshal.PtrToStringAnsi(memory, Math.Max(0, written)) ?? ""; return CheckError(out failure); }
                finally { Marshal.FreeHGlobal(memory); }
            }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { End(); }
        }
    }

    public bool TryGetShaderInfoLog(uint shader, out string infoLog, out GpuFailure? failure)
    {
        lock (_gate)
        {
            infoLog = ""; failure = null;
            if (!_shaders.Contains(shader)) { failure = new(GpuFailureKind.NativeError, "The shader handle is invalid for this context."); return false; }
            if (!Begin(out failure)) return false;
            try { infoLog = ReadShaderLog(shader); return CheckError(out failure); }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { End(); }
        }
    }

    public bool TryDeleteShader(uint shader, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (!_shaders.Remove(shader)) return true;
            if (!Begin(out failure)) { _shaders.Add(shader); return false; }
            try { _gl.DeleteShader(shader); return CheckError(out failure); }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { End(); }
        }
    }

    public bool TryCreateProgram(out uint program, out GpuFailure? failure)
    {
        lock (_gate)
        {
            program = 0; failure = null;
            if (!Begin(out failure)) return false;
            try { program = _gl.CreateProgram(); if (program == 0) { failure = NativeFailure("glCreateProgram returned zero."); return false; } _programs.Add(program); return true; }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { End(); }
        }
    }

    public bool TryAttachShader(uint program, uint shader, out GpuFailure? failure) => ProgramShaderCall(program, shader, true, out failure);
    public bool TryDetachShader(uint program, uint shader, out GpuFailure? failure) => ProgramShaderCall(program, shader, false, out failure);

    private bool ProgramShaderCall(uint program, uint shader, bool attach, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (!_programs.Contains(program) || !_shaders.Contains(shader)) { failure = new(GpuFailureKind.NativeError, "The program or shader handle is invalid for this context."); return false; }
            if (!Begin(out failure)) return false;
            try { if (attach) _gl.AttachShader(program, shader); else _gl.DetachShader(program, shader); return CheckError(out failure); }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { End(); }
        }
    }

    public bool TryLinkProgram(uint program, out bool linked, out string infoLog, out GpuFailure? failure) => LinkOrValidate(program, false, out linked, out infoLog, out failure);
    public bool TryValidateProgram(uint program, out bool valid, out string infoLog, out GpuFailure? failure) => LinkOrValidate(program, true, out valid, out infoLog, out failure);

    public bool TryGetProgramParameter(uint program, uint parameter, out int value, out GpuFailure? failure)
    {
        lock (_gate)
        {
            value = 0; failure = null;
            if (!_programs.Contains(program)) { failure = NativeFailure("The program handle is invalid for this context."); return false; }
            if (!Begin(out failure)) return false;
            try { _gl.GetProgramiv(program, parameter, out value); return CheckError(out failure); }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { End(); }
        }
    }

    public bool TryGetProgramInfoLog(uint program, out string infoLog, out GpuFailure? failure)
    {
        lock (_gate)
        {
            infoLog = string.Empty; failure = null;
            if (!_programs.Contains(program)) { failure = NativeFailure("The program handle is invalid for this context."); return false; }
            if (!Begin(out failure)) return false;
            try { infoLog = ReadProgramLog(program); return CheckError(out failure); }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { End(); }
        }
    }

    public bool TryGetUniformBlockIndex(uint program, ReadOnlySpan<byte> nameBytes, out uint index, out GpuFailure? failure)
    {
        index = uint.MaxValue; failure = null; lock (_gate) { if (!_programs.Contains(program)) { failure = NativeFailure("The program handle is invalid for this context."); return false; } if (!Begin(out failure)) return false; var name = IntPtr.Zero; try { name = AllocUtf8(nameBytes); index = _gl.GetUniformBlockIndex(program, name); return CheckError(out failure); } catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; } finally { if (name != IntPtr.Zero) Marshal.FreeHGlobal(name); End(); } }
    }

    public bool TryUniformBlockBinding(uint program, uint index, uint binding, out GpuFailure? failure)
    {
        lock (_gate) { failure = null; if (!_programs.Contains(program)) { failure = NativeFailure("The program handle is invalid for this context."); return false; } return StateCall(() => _gl.UniformBlockBinding(program, index, binding), out failure); }
    }

    private bool LinkOrValidate(uint program, bool validate, out bool succeeded, out string infoLog, out GpuFailure? failure)
    {
        lock (_gate)
        {
            succeeded = false; infoLog = ""; failure = null;
            if (!_programs.Contains(program)) { failure = new(GpuFailureKind.NativeError, "The program handle is invalid for this context."); return false; }
            if (!Begin(out failure)) return false;
            try
            {
                if (validate) _gl.ValidateProgram(program); else _gl.LinkProgram(program);
                _gl.GetProgramiv(program, validate ? GlValidateStatus : GlLinkStatus, out var status);
                succeeded = status == GlTrue;
                infoLog = ReadProgramLog(program);
                return CheckError(out failure);
            }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { End(); }
        }
    }

    public bool TryUseProgram(uint? program, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (program is { } value && !_programs.Contains(value)) { failure = new(GpuFailureKind.NativeError, "The program handle is invalid for this context."); return false; }
            if (!Begin(out failure)) return false;
            try { _gl.UseProgram(program ?? 0); return CheckError(out failure); }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { End(); }
        }
    }

    public bool TryDeleteProgram(uint program, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (!_programs.Remove(program)) return true;
            if (!Begin(out failure)) { _programs.Add(program); return false; }
            try { _gl.DeleteProgram(program); return CheckError(out failure); }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { End(); }
        }
    }

    public bool TryGetAttribLocation(uint program, ReadOnlySpan<byte> utf8Name, out int location, out GpuFailure? failure) => GetLocation(program, utf8Name, true, out location, out failure);
    public bool TryGetUniformLocation(uint program, ReadOnlySpan<byte> utf8Name, out int location, out GpuFailure? failure) => GetLocation(program, utf8Name, false, out location, out failure);

    public bool TryBindAttribLocation(uint program, uint index, ReadOnlySpan<byte> utf8Name, out GpuFailure? failure)
    {
        lock (_gate) { failure = null; if (!_programs.Contains(program)) { failure = NativeFailure("The program handle is invalid for this context."); return false; } if (!Begin(out failure)) return false; var name = IntPtr.Zero; try { name = AllocUtf8(utf8Name); _gl.BindAttribLocation(program, index, name); return CheckError(out failure); } catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; } finally { if (name != IntPtr.Zero) Marshal.FreeHGlobal(name); End(); } }
    }

    public bool TryGetAttachedShaders(uint program, Span<uint> shaders, out int count, out GpuFailure? failure)
    {
        lock (_gate) { count = 0; failure = null; if (!_programs.Contains(program) || shaders.Length == 0) { failure = NativeFailure("The program or shader destination is invalid."); return false; } if (!Begin(out failure)) return false; var memory = Marshal.AllocHGlobal(sizeof(uint) * shaders.Length); try { _gl.GetAttachedShaders(program, shaders.Length, out count, memory); if (!CheckError(out failure)) return false; count = Math.Clamp(count, 0, shaders.Length); for (var i = 0; i < count; i++) shaders[i] = unchecked((uint)Marshal.ReadInt32(memory, i * sizeof(uint))); return true; } catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; } finally { Marshal.FreeHGlobal(memory); End(); } }
    }

    private bool GetLocation(uint program, ReadOnlySpan<byte> utf8Name, bool attrib, out int location, out GpuFailure? failure)
    {
        lock (_gate)
        {
            location = -1; failure = null;
            if (!_programs.Contains(program)) { failure = new(GpuFailureKind.NativeError, "The program handle is invalid for this context."); return false; }
            if (!Begin(out failure)) return false;
            IntPtr name = IntPtr.Zero;
            try { name = AllocUtf8(utf8Name); location = attrib ? _gl.GetAttribLocation(program, name) : _gl.GetUniformLocation(program, name); return CheckError(out failure); }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { if (name != IntPtr.Zero) Marshal.FreeHGlobal(name); End(); }
        }
    }

    private string ReadShaderLog(uint shader) { _gl.GetShaderiv(shader, GlInfoLogLength, out var length); return ReadLog(length, (int max, out int written, IntPtr ptr) => _gl.GetShaderInfoLog(shader, max, out written, ptr)); }
    private string ReadProgramLog(uint program) { _gl.GetProgramiv(program, GlInfoLogLength, out var length); return ReadLog(length, (int max, out int written, IntPtr ptr) => _gl.GetProgramInfoLog(program, max, out written, ptr)); }
    private static string ReadLog(int length, LogReader read)
    {
        if (length <= 1) return "";
        var memory = Marshal.AllocHGlobal(length);
        try { read(length, out var written, memory); return Marshal.PtrToStringAnsi(memory, Math.Max(0, written)) ?? ""; }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private static IntPtr AllocUtf8(ReadOnlySpan<byte> value)
    {
        var pointer = Marshal.AllocHGlobal(checked(value.Length + 1));
        var bytes = value.ToArray(); Marshal.Copy(bytes, 0, pointer, bytes.Length); Marshal.WriteByte(pointer, bytes.Length, 0); return pointer;
    }

    private bool Begin(out GpuFailure? failure)
    {
        failure = null;
        if (Volatile.Read(ref _disposed) != 0) { failure = new(GpuFailureKind.Unavailable, "The WebGL context is disposed."); return false; }
        try { MakeCurrentLocked(); if (_multisampleFramebuffer != 0) _gl.BindFramebuffer(0x8D40, _multisampleFramebuffer); return true; }
        catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
    }

    private void End() => ReleaseCurrentLocked();
    private GpuFailure NativeFailure(string message, uint? apiError = null) => new(GpuFailureKind.NativeError, message, apiError);
    private bool CheckError(out GpuFailure? failure)
    {
        var error = _gl.GetError();
        failure = error == 0 ? null : new(GpuFailureKind.NativeError, $"ANGLE OpenGL ES error 0x{error:X4}.", error);
        return error == 0;
    }
}

