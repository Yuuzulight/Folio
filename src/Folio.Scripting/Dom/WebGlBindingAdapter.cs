using Folio.Scripting.Bindings;
using Folio.Scripting.Gpu;
using Folio.Scripting;
using System.Text;

namespace Folio.Scripting.Dom;

/// <summary>
/// Managed stateful portion of the generated WebGL target. Remaining binding
/// surface methods are added as their native state operations are wired.
/// </summary>
internal class WebGlBindingAdapter : IWebGLRenderingContextTarget
{
    private readonly Queue<uint> _errors = new();
    private WebGlProgramObject? _currentProgram;
    private float _clearRed, _clearGreen, _clearBlue, _clearAlpha;
    protected IScriptEngine? Engine { get; }
    protected Action<string>? DiagnosticSink { get; }
    private readonly Action? _present;
    private readonly Action? _loseContext;
    private readonly Func<bool>? _restoreContext;
    private readonly HostObjectTemplate? _loseContextTemplate;
    // Extension objects are host objects with stable identity for the lifetime
    // of a context.  Returning a freshly boxed string on every call breaks the
    // WebGL requirement that repeated getExtension calls compare by identity.
    private readonly Dictionary<string, ScriptValue> _extensions = new(StringComparer.OrdinalIgnoreCase);
    public ScriptValue Canvas { get; }
    internal WebGlBindingAdapter(WebGlObjectRegistry registry, IScriptEngine? engine = null, Action<string>? diagnosticSink = null, ScriptValue canvas = default, int maxShaderSourceBytes = 1_000_000, Action? present = null, Action? loseContext = null, Func<bool>? restoreContext = null)
    {
        Registry = registry;
        Engine = engine;
        DiagnosticSink = diagnosticSink;
        _present = present;
        _loseContext = loseContext;
        _restoreContext = restoreContext;
        if (engine is not null)
            _loseContextTemplate = new HostObjectTemplate("WEBGLLoseContext")
                .DefineMethod("loseContext", (target, _, _) => { ((WebGlLoseContextBridge)target).Lose(); return ScriptValue.Undefined; })
                .DefineMethod("restoreContext", (target, _, _) => { ((WebGlLoseContextBridge)target).Restore(); return ScriptValue.Undefined; });
        Canvas = canvas;
        MaxShaderSourceBytes = maxShaderSourceBytes > 0 ? maxShaderSourceBytes : throw new ArgumentOutOfRangeException(nameof(maxShaderSourceBytes));
    }
    protected WebGlObjectRegistry Registry { get; private set; }
    protected INativeWebGlContext Native => Registry.Native;
    protected int MaxShaderSourceBytes { get; }
    internal void ReplaceRegistry(WebGlObjectRegistry registry)
    {
        Registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _currentProgram = null;
        lock (_errors) _errors.Clear();
    }

    public int DrawingBufferWidth => Native.DrawingBufferWidth;
    public int DrawingBufferHeight => Native.DrawingBufferHeight;
    public WebGLContextAttributes? GetContextAttributes() => Native.IsLost ? null : new()
    {
        Alpha = Native.Attributes.Alpha,
        Depth = Native.Attributes.Depth,
        Stencil = Native.Attributes.Stencil,
        Antialias = Native.Attributes.Antialias,
        PremultipliedAlpha = Native.Attributes.PremultipliedAlpha,
        PreserveDrawingBuffer = Native.Attributes.PreserveDrawingBuffer,
    };
    internal void Resize(int width, int height)
    {
        if (!Native.TryResize(width, height, out var failure)) RecordNativeFailure(failure);
    }
    public ScriptValue? Oncontextlost { get; set; }
    public ScriptValue? Oncontextrestored { get; set; }

    public IWebGLBufferTarget? CreateBuffer()
    {
        try
        {
            var value = Registry.CreateBuffer(out var failure);
            if (value is null) { EnqueueFailure(failure); return null; }
            return value;
        }
        catch (WebGlValidationException error) { EnqueueFailure(error); return null; }
    }
    public void DeleteBuffer(IWebGLBufferTarget? buffer)
    {
        if (buffer is null) return;
        try { Require<WebGlBufferObject>(buffer).Delete(); } catch (WebGlValidationException error) { EnqueueFailure(error); }
    }
    public bool IsBuffer(IWebGLBufferTarget? buffer) => buffer is WebGlBufferObject value && value.Owner == Registry && !value.Deleted;
    public IWebGLShaderTarget? CreateShader(uint type)
    {
        try
        {
            var value = Registry.CreateShader(type, out var failure);
            if (value is null) { EnqueueFailure(failure); return null; }
            return value;
        }
        catch (WebGlValidationException error) { EnqueueFailure(error); return null; }
    }
    public void DeleteShader(IWebGLShaderTarget? shader)
    {
        if (shader is null) return;
        try { Require<WebGlShaderObject>(shader).Delete(); } catch (WebGlValidationException error) { EnqueueFailure(error); }
    }
    public bool IsShader(IWebGLShaderTarget? shader) => shader is WebGlShaderObject value && value.Owner == Registry && !value.Deleted;
    public IWebGLProgramTarget? CreateProgram()
    {
        try
        {
            var value = Registry.CreateProgram(out var failure);
            if (value is null) { EnqueueFailure(failure); return null; }
            return value;
        }
        catch (WebGlValidationException error) { EnqueueFailure(error); return null; }
    }
    public void DeleteProgram(IWebGLProgramTarget? program)
    {
        if (program is null) return;
        try { Require<WebGlProgramObject>(program).Delete(); } catch (WebGlValidationException error) { EnqueueFailure(error); }
    }
    public bool IsProgram(IWebGLProgramTarget? program) => program is WebGlProgramObject value && value.Owner == Registry && !value.Deleted;
    public void AttachShader(IWebGLProgramTarget program, IWebGLShaderTarget shader) { try { Require<WebGlProgramObject>(program).Attach(Require<WebGlShaderObject>(shader)); } catch (WebGlValidationException error) { EnqueueFailure(error); } }
    public void DetachShader(IWebGLProgramTarget program, IWebGLShaderTarget shader) { try { Require<WebGlProgramObject>(program).Detach(Require<WebGlShaderObject>(shader)); } catch (WebGlValidationException error) { EnqueueFailure(error); } }
    public void LinkProgram(IWebGLProgramTarget program)
    {
        try
        {
            var value = Require<WebGlProgramObject>(program);
            if (!value.Link(out var infoLog)) DiagnosticSink?.Invoke($"WebGL link failed: {infoLog}");
        }
        catch (WebGlValidationException error) { EnqueueFailure(error); }
    }
    public void ShaderSource(IWebGLShaderTarget shader, string source)
    {
        try
        {
            var value = Require<WebGlShaderObject>(shader);
            var byteCount = Encoding.UTF8.GetByteCount(source);
            if (byteCount > MaxShaderSourceBytes) { EnqueueFailure(new WebGlValidationException(WebGlValidationFailure.InvalidValue, "Shader source exceeds the configured limit.")); return; }
            if (!Native.TrySetShaderSource(value.Handle, Encoding.UTF8.GetBytes(source), out var failure)) EnqueueFailure(failure);
        }
        catch (WebGlValidationException error) { EnqueueFailure(error); }
    }
    public void CompileShader(IWebGLShaderTarget shader)
    {
        try
        {
            var value = Require<WebGlShaderObject>(shader);
            if (!Native.TryCompileShader(value.Handle, out var compiled, out var infoLog, out var failure)) { EnqueueFailure(failure); return; }
            if (!compiled) DiagnosticSink?.Invoke($"WebGL shader compilation failed: {infoLog}");
        }
        catch (WebGlValidationException error) { EnqueueFailure(error); }
    }
    public ScriptValue GetShaderParameter(IWebGLShaderTarget shader, uint parameter)
    {
        try
        {
            var value = Require<WebGlShaderObject>(shader);
            if (!Native.TryGetShaderParameter(value.Handle, parameter, out var result, out var failure)) { EnqueueFailure(failure); return ScriptValue.Undefined; }
            return ToScriptValue(result);
        }
        catch (WebGlValidationException error) { EnqueueFailure(error); return ScriptValue.Undefined; }
    }
    public string GetShaderInfoLog(IWebGLShaderTarget shader)
    {
        try { var value = Require<WebGlShaderObject>(shader); if (!Native.TryGetShaderInfoLog(value.Handle, out var result, out var failure)) { EnqueueFailure(failure); return string.Empty; } return result; }
        catch (WebGlValidationException error) { EnqueueFailure(error); return string.Empty; }
    }
    public string? GetShaderSource(IWebGLShaderTarget shader)
    {
        try { var value = Require<WebGlShaderObject>(shader); if (!Native.TryGetShaderSource(value.Handle, out var result, out var failure)) { EnqueueFailure(failure); return null; } return result; }
        catch (WebGlValidationException error) { EnqueueFailure(error); return null; }
    }
    public IWebGLShaderPrecisionFormatTarget GetShaderPrecisionFormat(uint shaderType, uint precisionType)
    {
        if (!Native.TryGetShaderPrecisionFormat(shaderType, precisionType, out var min, out var max, out var precision, out var failure))
        { EnqueueFailure(failure); return new WebGlShaderPrecisionFormatObject(0, 0, 0); }
        return new WebGlShaderPrecisionFormatObject(min, max, precision);
    }
    public ScriptValue GetProgramParameter(IWebGLProgramTarget program, uint parameter)
    {
        try
        {
            var value = Require<WebGlProgramObject>(program);
            if (!Native.TryGetProgramParameter(value.Handle, parameter, out var result, out var failure)) { EnqueueFailure(failure); return ScriptValue.Undefined; }
            return ToScriptValue(result);
        }
        catch (WebGlValidationException error) { EnqueueFailure(error); return ScriptValue.Undefined; }
    }
    public string GetProgramInfoLog(IWebGLProgramTarget program)
    {
        try { var value = Require<WebGlProgramObject>(program); if (!Native.TryGetProgramInfoLog(value.Handle, out var result, out var failure)) { EnqueueFailure(failure); return string.Empty; } return result; }
        catch (WebGlValidationException error) { EnqueueFailure(error); return string.Empty; }
    }
    public IReadOnlyList<IWebGLShaderTarget> GetAttachedShaders(IWebGLProgramTarget program)
    {
        try
        {
            var value = Require<WebGlProgramObject>(program);
            Span<WebGlShaderHandle> handles = stackalloc WebGlShaderHandle[64];
            if (!Native.TryGetAttachedShaders(value.Handle, handles, out var count, out var failure)) { EnqueueFailure(failure); return []; }
            var result = new List<IWebGLShaderTarget>(count);
            for (var i = 0; i < count && i < handles.Length; i++) if (Registry.TryGetShader(handles[i], out var shader)) result.Add(shader);
            return result;
        }
        catch (WebGlValidationException error) { EnqueueFailure(error); return []; }
    }
    public IWebGLActiveInfoTarget? GetActiveAttrib(IWebGLProgramTarget program, uint index)
    {
        try { var value = Require<WebGlProgramObject>(program); if (!Native.TryGetActiveAttrib(value.Handle, index, out var info, out var failure)) { EnqueueFailure(failure); return null; } return new WebGlActiveInfoObject(info); }
        catch (WebGlValidationException error) { EnqueueFailure(error); return null; }
    }
    public IWebGLActiveInfoTarget? GetActiveUniform(IWebGLProgramTarget program, uint index)
    {
        try { var value = Require<WebGlProgramObject>(program); if (!Native.TryGetActiveUniform(value.Handle, index, out var info, out var failure)) { EnqueueFailure(failure); return null; } return new WebGlActiveInfoObject(info); }
        catch (WebGlValidationException error) { EnqueueFailure(error); return null; }
    }
    public int GetAttribLocation(IWebGLProgramTarget program, string name)
    {
        try { var value = Require<WebGlProgramObject>(program); if (!Native.TryGetAttribLocation(value.Handle, name, out var location, out var failure)) { EnqueueFailure(failure); return -1; } return location.Value; }
        catch (WebGlValidationException error) { EnqueueFailure(error); return -1; }
    }
    public void BindAttribLocation(IWebGLProgramTarget program, uint index, string name)
    {
        try { var value = Require<WebGlProgramObject>(program); NativeCall(Native.TryBindAttribLocation(value.Handle, index, Encoding.UTF8.GetBytes(name), out var failure), failure); }
        catch (WebGlValidationException error) { EnqueueFailure(error); }
    }
    public void EnableVertexAttribArray(int index) => NativeCall(Native.TryEnableVertexAttrib(new WebGlAttribLocation(index), out var failure), failure);
    public void DisableVertexAttribArray(int index) => NativeCall(Native.TryDisableVertexAttrib(new WebGlAttribLocation(index), out var failure), failure);
    public void VertexAttribPointer(int index, int size, uint type, bool normalized, int stride, int offset) => NativeCall(Native.TryVertexAttribPointer(new WebGlAttribLocation(index), size, type, normalized, stride, offset, out var failure), failure);
    public void VertexAttribIPointer(int index, int size, uint type, int stride, int offset) => NativeCall(Native.TryVertexAttribIPointer(new WebGlAttribLocation(index), size, type, stride, offset, out var failure), failure);
    public void VertexAttribDivisor(int index, uint divisor) => NativeCall(Native.TryVertexAttribDivisor(new WebGlAttribLocation(index), divisor, out var failure), failure);
    public void VertexAttrib1f(int index, float x) => SetVertexAttribConstant(index, [x], 1);
    public void VertexAttrib2f(int index, float x, float y) => SetVertexAttribConstant(index, [x, y], 2);
    public void VertexAttrib3f(int index, float x, float y, float z) => SetVertexAttribConstant(index, [x, y, z], 3);
    public void VertexAttrib4f(int index, float x, float y, float z, float w) => SetVertexAttribConstant(index, [x, y, z, w], 4);
    public void VertexAttrib1fv(int index, IReadOnlyList<float> values) => SetVertexAttribConstant(index, values, 1);
    public void VertexAttrib2fv(int index, IReadOnlyList<float> values) => SetVertexAttribConstant(index, values, 2);
    public void VertexAttrib3fv(int index, IReadOnlyList<float> values) => SetVertexAttribConstant(index, values, 3);
    public void VertexAttrib4fv(int index, IReadOnlyList<float> values) => SetVertexAttribConstant(index, values, 4);
    public void VertexAttribI4i(int index, int x, int y, int z, int w) => NativeCall(Native.TrySetVertexAttribIConstant(new WebGlAttribLocation(index), new[] { x, y, z, w }, out var failure), failure);
    public void VertexAttribI4ui(int index, uint x, uint y, uint z, uint w) => NativeCall(Native.TrySetVertexAttribUIConstant(new WebGlAttribLocation(index), new[] { x, y, z, w }, out var failure), failure);
    public int GetVertexAttribOffset(int index, uint parameter)
    {
        if (!Native.TryGetVertexAttribOffset(new WebGlAttribLocation(index), parameter, out var offset, out var failure)) { EnqueueFailure(failure); return 0; }
        return checked((int)offset);
    }
    public ScriptValue GetVertexAttrib(int index, uint parameter)
    {
        if (!Native.TryGetVertexAttrib(new WebGlAttribLocation(index), parameter, out var value, out var failure)) { EnqueueFailure(failure); return ScriptValue.Undefined; }
        return ToScriptValue(value);
    }
    public void ValidateProgram(IWebGLProgramTarget program)
    {
        try { var value = Require<WebGlProgramObject>(program); if (!Native.TryValidateProgram(value.Handle, out _, out _, out var failure)) EnqueueFailure(failure); }
        catch (WebGlValidationException error) { EnqueueFailure(error); }
    }
    public void UseProgram(IWebGLProgramTarget? program)
    {
        try
        {
            WebGlProgramHandle? handle = null;
            if (program is not null) handle = Require<WebGlProgramObject>(program).Handle;
            if (!Native.TryUseProgram(handle, out var failure)) { EnqueueFailure(failure); return; }
            _currentProgram = program as WebGlProgramObject;
        }
        catch (WebGlValidationException error) { EnqueueFailure(error); }
    }
    // A deleted program remains the current executable until explicitly
    // unbound, matching native GL deferred deletion semantics.
    public IWebGLProgramTarget? GetCurrentProgram() => _currentProgram;
    public ScriptValue GetUniform(IWebGLProgramTarget program, IWebGLUniformLocationTarget? location)
    {
        if (location is null) return ScriptValue.Null;
        try
        {
            var owner = Require<WebGlProgramObject>(program);
            var uniform = Require<WebGlUniformLocationObject>(location);
            if (!ReferenceEquals(uniform.Program, owner)) throw new WebGlValidationException(WebGlValidationFailure.InvalidOperation, "The uniform location belongs to another program.");
            uniform.EnsureCurrent();
            if (!Native.TryGetUniform(owner.Handle, uniform.Handle, out var value, out var failure)) return FailureValue(failure);
            return value.Kind switch
            {
                WebGlParameterKind.Int32 => ScriptValue.FromNumber(value.Int32?.FirstOrDefault() ?? 0),
                WebGlParameterKind.UInt32 => ScriptValue.FromNumber(value.UInt32?.FirstOrDefault() ?? 0),
                WebGlParameterKind.Float32 => ScriptValue.FromNumber(value.Float32?.FirstOrDefault() ?? 0),
                WebGlParameterKind.Int32Array => CreateTypedArray(ScriptBufferSourceKind.Int32, value.Int32),
                WebGlParameterKind.UInt32Array => CreateTypedArray(ScriptBufferSourceKind.Uint32, value.UInt32),
                WebGlParameterKind.Float32Array => CreateTypedArray(ScriptBufferSourceKind.Float32, value.Float32),
                _ => ScriptValue.Undefined,
            };
        }
        catch (WebGlValidationException error) { EnqueueFailure(error); return ScriptValue.Undefined; }
    }
    public IWebGLUniformLocationTarget? GetUniformLocation(IWebGLProgramTarget program, string name)
    {
        try
        {
            var value = Require<WebGlProgramObject>(program);
            if (!Native.TryGetUniformLocation(value.Handle, name, out var location, out var failure)) { EnqueueFailure(failure); return null; }
            return location.Value < 0 ? null : Registry.CreateUniformLocation(value, location.Value);
        }
        catch (WebGlValidationException error) { EnqueueFailure(error); return null; }
    }

    public uint GetError()
    {
        lock (_errors) return _errors.Count == 0 ? 0u : _errors.Dequeue();
    }

    public void BindBuffer(uint target, IWebGLBufferTarget? buffer)
    {
        try
        {
            WebGlBufferHandle? handle = null;
            if (buffer is not null) handle = Require<WebGlBufferObject>(buffer).Handle;
            NativeCall(Native.TryBindBuffer(target, handle, out var failure), failure);
        }
        catch (WebGlValidationException error) { EnqueueFailure(error); }
    }
    public void BufferData(uint target, int size, uint usage) => NativeCall(Native.TryAllocateBufferStorage(target, size, usage, out var failure), failure);
    public void BufferData(uint target, ScriptBufferSource data, uint usage)
    {
        // Let the native context validate/budget the allocation before copying
        // an untrusted script buffer into managed memory.
        if (!Native.TryAllocateBufferStorage(target, data.Info.ByteLength, usage, out var allocationFailure)) { EnqueueFailure(allocationFailure); return; }
        var bytes = CopySource(data);
        NativeCall(Native.TryUpdateBuffer(target, 0, bytes, out var failure), failure);
    }
    public void BufferSubData(uint target, int offset, ScriptBufferSource data)
    {
        if (!Native.TryGetBufferParameter(target, 0x8764, out var sizeValue, out var sizeFailure)) { EnqueueFailure(sizeFailure); return; }
        var size = sizeValue.Int32;
        if (offset < 0 || data.Info.ByteLength > size - (long)offset) { EnqueueError(0x0501); return; }
        var bytes = CopySource(data);
        NativeCall(Native.TryUpdateBuffer(target, offset, bytes, out var failure), failure);
    }
    public void GetBufferSubData(uint target, int offset, ScriptBufferSource destination)
    {
        if (!Native.TryGetBufferParameter(target, 0x8764, out var sizeValue, out var sizeFailure)) { EnqueueFailure(sizeFailure); return; }
        var size = sizeValue.Int32;
        if (offset < 0 || destination.Info.ByteLength > size - (long)offset) { EnqueueError(0x0501); return; }
        var bytes = new byte[destination.Info.ByteLength];
        if (!Native.TryReadBuffer(target, offset, bytes, out var failure)) { EnqueueFailure(failure); return; }
        if (Engine is null || !Engine.TryWriteBufferSource(destination.Value, 0, bytes)) EnqueueError(0x0502);
    }
    public void DrawBuffers(IReadOnlyList<uint> buffers) => NativeCall(Native.TryDrawBuffers(buffers.ToArray(), out var failure), failure);
    public void ReadBuffer(uint source) => NativeCall(Native.TryReadBuffer(source, out var failure), failure);
    public ScriptValue GetBufferParameter(uint target, uint parameter)
    {
        if (!Native.TryGetBufferParameter(target, parameter, out var value, out var failure)) { EnqueueFailure(failure); return ScriptValue.Undefined; }
        return ToScriptValue(value);
    }

    public IWebGLTextureTarget? CreateTexture()
    {
        try
        {
            var value = Registry.CreateTexture(out var failure);
            if (value is null) { EnqueueFailure(failure); return null; }
            return value;
        }
        catch (WebGlValidationException error) { EnqueueFailure(error); return null; }
    }
    public void DeleteTexture(IWebGLTextureTarget? texture)
    {
        if (texture is null) return;
        try { Require<WebGlTextureObject>(texture).Delete(); } catch (WebGlValidationException error) { EnqueueFailure(error); }
    }
    public bool IsTexture(IWebGLTextureTarget? texture) => texture is WebGlTextureObject value && value.Owner == Registry && !value.Deleted;
    public void BindTexture(uint target, IWebGLTextureTarget? texture)
    {
        try
        {
            WebGlTextureHandle? handle = null;
            if (texture is not null) handle = Require<WebGlTextureObject>(texture).Handle;
            NativeCall(Native.TryBindTexture(target, handle, out var failure), failure);
        }
        catch (WebGlValidationException error) { EnqueueFailure(error); }
    }
    public void TexParameteri(uint target, uint parameter, int value) => NativeCall(Native.TryTextureParameter(target, parameter, value, out var failure), failure);
    public ScriptValue GetTexParameter(uint target, uint parameter)
    {
        if (!Native.TryGetTextureParameter(target, parameter, out var value, out var failure)) { EnqueueFailure(failure); return ScriptValue.Undefined; }
        return ToScriptValue(value);
    }
    public ScriptValue GetTexLevelParameter(uint target, int level, uint parameter)
    {
        if (!Native.TryGetTextureLevelParameter(target, level, parameter, out var value, out var failure)) { EnqueueFailure(failure); return ScriptValue.Undefined; }
        return ToScriptValue(value);
    }
    public void TexImage2D(uint target, int level, int internalFormat, int width, int height, int border, uint format, uint type, ScriptBufferSource pixels)
    {
        if (border != 0) { EnqueueError(0x0501); return; }
        var bytes = CopySource(pixels);
        NativeCall(Native.TryTextureImage2D(target, level, internalFormat, width, height, format, type, bytes, out var failure), failure);
    }
    public void TexStorage2D(uint target, int levels, uint internalFormat, int width, int height) => NativeCall(Native.TryTextureStorage2D(target, levels, internalFormat, width, height, out var failure), failure);
    public void TexSubImage2D(uint target, int level, int xOffset, int yOffset, int width, int height, int border, uint format, uint type, ScriptBufferSource pixels)
    {
        if (border != 0) { EnqueueError(0x0501); return; }
        var bytes = CopySource(pixels);
        NativeCall(Native.TryTextureSubImage2D(target, level, xOffset, yOffset, width, height, format, type, bytes, out var failure), failure);
    }
    public void CopyTexImage2D(uint target, int level, uint internalFormat, int x, int y, int width, int height, int border) => NativeCall(Native.TryCopyTextureImage2D(target, level, (int)internalFormat, x, y, width, height, border, out var failure), failure);
    public void CopyTexSubImage2D(uint target, int level, int xOffset, int yOffset, int x, int y, int width, int height) => NativeCall(Native.TryCopyTextureSubImage2D(target, level, xOffset, yOffset, x, y, width, height, out var failure), failure);
    public void TexImage3D(uint target, int level, int internalFormat, int width, int height, int depth, int border, uint format, uint type, ScriptBufferSource pixels)
    {
        if (border != 0) { EnqueueError(0x0501); return; }
        var bytes = CopySource(pixels);
        NativeCall(Native.TryTextureImage3D(target, level, internalFormat, width, height, depth, format, type, bytes, out var failure), failure);
    }
    public void TexStorage3D(uint target, int levels, uint internalFormat, int width, int height, int depth) => NativeCall(Native.TryTextureStorage3D(target, levels, internalFormat, width, height, depth, out var failure), failure);
    public void TexSubImage3D(uint target, int level, int xOffset, int yOffset, int zOffset, int width, int height, int depth, int border, uint format, uint type, ScriptBufferSource pixels)
    {
        if (border != 0) { EnqueueError(0x0501); return; }
        var bytes = CopySource(pixels);
        NativeCall(Native.TryTextureSubImage3D(target, level, xOffset, yOffset, zOffset, width, height, depth, format, type, bytes, out var failure), failure);
    }
    public void GenerateMipmap(uint target) => NativeCall(Native.TryGenerateMipmap(target, out var failure), failure);

    public IWebGLFramebufferTarget? CreateFramebuffer()
    {
        try
        {
            var value = Registry.CreateFramebuffer(out var failure);
            if (value is null) { EnqueueFailure(failure); return null; }
            return value;
        }
        catch (WebGlValidationException error) { EnqueueFailure(error); return null; }
    }
    public void DeleteFramebuffer(IWebGLFramebufferTarget? framebuffer)
    {
        if (framebuffer is null) return;
        try { Require<WebGlFramebufferObject>(framebuffer).Delete(); } catch (WebGlValidationException error) { EnqueueFailure(error); }
    }
    public bool IsFramebuffer(IWebGLFramebufferTarget? framebuffer) => framebuffer is WebGlFramebufferObject value && value.Owner == Registry && !value.Deleted;
    public void BindFramebuffer(uint target, IWebGLFramebufferTarget? framebuffer)
    {
        try
        {
            WebGlFramebufferHandle? handle = null;
            if (framebuffer is not null) handle = Require<WebGlFramebufferObject>(framebuffer).Handle;
            NativeCall(Native.TryBindFramebuffer(target, handle, out var failure), failure);
        }
        catch (WebGlValidationException error) { EnqueueFailure(error); }
    }
    public void FramebufferTexture2D(uint target, uint attachment, uint textarget, IWebGLTextureTarget? texture, int level)
    {
        try
        {
            WebGlTextureHandle? handle = null;
            if (texture is not null) handle = Require<WebGlTextureObject>(texture).Handle;
            NativeCall(Native.TryFramebufferTexture2D(target, attachment, textarget, handle, level, out var failure), failure);
        }
        catch (WebGlValidationException error) { EnqueueFailure(error); }
    }
    public void FramebufferTextureLayer(uint target, uint attachment, IWebGLTextureTarget? texture, int level, int layer)
    {
        try
        {
            WebGlTextureHandle? handle = null;
            if (texture is not null) handle = Require<WebGlTextureObject>(texture).Handle;
            NativeCall(Native.TryFramebufferTextureLayer(target, attachment, handle, level, layer, out var failure), failure);
        }
        catch (WebGlValidationException error) { EnqueueFailure(error); }
    }
    public uint CheckFramebufferStatus(uint target)
    {
        if (Native.TryCheckFramebufferStatus(target, out var status, out var failure)) return status;
        EnqueueFailure(failure); return 0;
    }
    public void BlitFramebuffer(int srcX0, int srcY0, int srcX1, int srcY1, int dstX0, int dstY0, int dstX1, int dstY1, uint mask, uint filter) => NativeCall(Native.TryBlitFramebuffer(srcX0, srcY0, srcX1, srcY1, dstX0, dstY0, dstX1, dstY1, mask, filter, out var failure), failure);
    public IWebGLRenderbufferTarget? CreateRenderbuffer()
    {
        try
        {
            var value = Registry.CreateRenderbuffer(out var failure);
            if (value is null) { EnqueueFailure(failure); return null; }
            return value;
        }
        catch (WebGlValidationException error) { EnqueueFailure(error); return null; }
    }
    public void DeleteRenderbuffer(IWebGLRenderbufferTarget? renderbuffer)
    {
        if (renderbuffer is null) return;
        try { Require<WebGlRenderbufferObject>(renderbuffer).Delete(); } catch (WebGlValidationException error) { EnqueueFailure(error); }
    }
    public bool IsRenderbuffer(IWebGLRenderbufferTarget? renderbuffer) => renderbuffer is WebGlRenderbufferObject value && value.Owner == Registry && !value.Deleted;
    public void BindRenderbuffer(uint target, IWebGLRenderbufferTarget? renderbuffer)
    {
        try
        {
            WebGlRenderbufferHandle? handle = null;
            if (renderbuffer is not null) handle = Require<WebGlRenderbufferObject>(renderbuffer).Handle;
            NativeCall(Native.TryBindRenderbuffer(target, handle, out var failure), failure);
        }
        catch (WebGlValidationException error) { EnqueueFailure(error); }
    }
    public void RenderbufferStorage(uint target, uint internalFormat, int width, int height) => NativeCall(Native.TryRenderbufferStorage(target, internalFormat, width, height, out var failure), failure);
    public void RenderbufferStorageMultisample(uint target, int samples, uint internalFormat, int width, int height) => NativeCall(Native.TryRenderbufferStorageMultisample(target, samples, internalFormat, width, height, out var failure), failure);
    public void FramebufferRenderbuffer(uint target, uint attachment, uint renderbufferTarget, IWebGLRenderbufferTarget? renderbuffer)
    {
        try
        {
            WebGlRenderbufferHandle? handle = null;
            if (renderbuffer is not null) handle = Require<WebGlRenderbufferObject>(renderbuffer).Handle;
            NativeCall(Native.TryFramebufferRenderbuffer(target, attachment, renderbufferTarget, handle, out var failure), failure);
        }
        catch (WebGlValidationException error) { EnqueueFailure(error); }
    }
    public IWebGLVertexArrayObjectTarget? CreateVertexArray()
    {
        try
        {
            var value = Registry.CreateVertexArray(out var failure);
            if (value is null) { EnqueueFailure(failure); return null; }
            return value;
        }
        catch (WebGlValidationException error) { EnqueueFailure(error); return null; }
    }
    public void DeleteVertexArray(IWebGLVertexArrayObjectTarget? array) { try { if (array is not null) Require<WebGlVertexArrayObject>(array).Delete(); } catch (WebGlValidationException error) { EnqueueFailure(error); } }
    public bool IsVertexArray(IWebGLVertexArrayObjectTarget? array) => array is WebGlVertexArrayObject value && value.Owner == Registry && !value.Deleted;
    public void BindVertexArray(IWebGLVertexArrayObjectTarget? array)
    {
        try
        {
            WebGlVertexArrayHandle? handle = null;
            if (array is not null) handle = Require<WebGlVertexArrayObject>(array).Handle;
            NativeCall(Native.TryBindVertexArray(handle, out var failure), failure);
        }
        catch (WebGlValidationException error) { EnqueueFailure(error); }
    }
    public void Uniform1f(IWebGLUniformLocationTarget? location, float x) => UniformFloat(location, 1, new[] { x });
    public void Uniform1i(IWebGLUniformLocationTarget? location, int x) => UniformInt(location, 1, new[] { x });
    public void Uniform2i(IWebGLUniformLocationTarget? location, int x, int y) => UniformInt(location, 2, new[] { x, y });
    public void Uniform3i(IWebGLUniformLocationTarget? location, int x, int y, int z) => UniformInt(location, 3, new[] { x, y, z });
    public void Uniform4i(IWebGLUniformLocationTarget? location, int x, int y, int z, int w) => UniformInt(location, 4, new[] { x, y, z, w });
    public void Uniform2f(IWebGLUniformLocationTarget? location, float x, float y) => UniformFloat(location, 2, new[] { x, y });
    public void Uniform3f(IWebGLUniformLocationTarget? location, float x, float y, float z) => UniformFloat(location, 3, new[] { x, y, z });
    public void Uniform4f(IWebGLUniformLocationTarget? location, float x, float y, float z, float w) => UniformFloat(location, 4, new[] { x, y, z, w });
    public void Uniform1fv(IWebGLUniformLocationTarget? location, IReadOnlyList<float> value) => UniformFloat(location, 1, value.ToArray());
    public void Uniform2fv(IWebGLUniformLocationTarget? location, IReadOnlyList<float> value) => UniformFloat(location, 2, value.ToArray());
    public void Uniform3fv(IWebGLUniformLocationTarget? location, IReadOnlyList<float> value) => UniformFloat(location, 3, value.ToArray());
    public void Uniform4fv(IWebGLUniformLocationTarget? location, IReadOnlyList<float> value) => UniformFloat(location, 4, value.ToArray());
    public void Uniform1iv(IWebGLUniformLocationTarget? location, IReadOnlyList<int> value) => UniformInt(location, 1, value.ToArray());
    public void Uniform2iv(IWebGLUniformLocationTarget? location, IReadOnlyList<int> value) => UniformInt(location, 2, value.ToArray());
    public void Uniform3iv(IWebGLUniformLocationTarget? location, IReadOnlyList<int> value) => UniformInt(location, 3, value.ToArray());
    public void Uniform4iv(IWebGLUniformLocationTarget? location, IReadOnlyList<int> value) => UniformInt(location, 4, value.ToArray());
    public void UniformMatrix2fv(IWebGLUniformLocationTarget? location, bool transpose, IReadOnlyList<float> value) => UniformMatrix(location, 2, 2, transpose, value.ToArray());
    public void UniformMatrix3fv(IWebGLUniformLocationTarget? location, bool transpose, IReadOnlyList<float> value) => UniformMatrix(location, 3, 3, transpose, value.ToArray());
    public void UniformMatrix4fv(IWebGLUniformLocationTarget? location, bool transpose, IReadOnlyList<float> value) => UniformMatrix(location, 4, 4, transpose, value.ToArray());

    public void Uniform1ui(IWebGLUniformLocationTarget? location, uint x) => UniformUInt(location, 1, new[] { x });
    public void Uniform2ui(IWebGLUniformLocationTarget? location, uint x, uint y) => UniformUInt(location, 2, new[] { x, y });
    public void Uniform3ui(IWebGLUniformLocationTarget? location, uint x, uint y, uint z) => UniformUInt(location, 3, new[] { x, y, z });
    public void Uniform4ui(IWebGLUniformLocationTarget? location, uint x, uint y, uint z, uint w) => UniformUInt(location, 4, new[] { x, y, z, w });
    public void Uniform1uiv(IWebGLUniformLocationTarget? location, IReadOnlyList<uint> value) => UniformUInt(location, 1, value.ToArray());
    public void Uniform2uiv(IWebGLUniformLocationTarget? location, IReadOnlyList<uint> value) => UniformUInt(location, 2, value.ToArray());
    public void Uniform3uiv(IWebGLUniformLocationTarget? location, IReadOnlyList<uint> value) => UniformUInt(location, 3, value.ToArray());
    public void Uniform4uiv(IWebGLUniformLocationTarget? location, IReadOnlyList<uint> value) => UniformUInt(location, 4, value.ToArray());
    public void Viewport(int x, int y, int width, int height) => NativeCall(Native.TrySetViewport(x, y, width, height, out var failure), failure);
    public void Scissor(int x, int y, int width, int height) => NativeCall(Native.TrySetScissor(x, y, width, height, false, out var failure), failure);
    public void Enable(uint capability) => NativeCall(Native.TrySetCapability(capability, true, out var failure), failure);
    public void Disable(uint capability) => NativeCall(Native.TrySetCapability(capability, false, out var failure), failure);
    public void BlendFunc(uint source, uint destination) => NativeCall(Native.TrySetBlendFunction(source, destination, source, destination, out var failure), failure);
    public void BlendFuncSeparate(uint srgb, uint drgb, uint salpha, uint dalpha) => NativeCall(Native.TrySetBlendFunction(srgb, drgb, salpha, dalpha, out var failure), failure);
    public void BlendEquation(uint mode) => NativeCall(Native.TrySetBlendEquation(mode, mode, out var failure), failure);
    public void BlendEquationSeparate(uint rgb, uint alpha) => NativeCall(Native.TrySetBlendEquation(rgb, alpha, out var failure), failure);
    public void ColorMask(bool red, bool green, bool blue, bool alpha) => NativeCall(Native.TrySetColorMask(red, green, blue, alpha, out var failure), failure);
    public void DepthFunc(uint function) => NativeCall(Native.TrySetDepthFunction(function, out var failure), failure);
    public void DepthMask(bool flag) => NativeCall(Native.TrySetDepthMask(flag, out var failure), failure);
    public void CullFace(uint mode) => NativeCall(Native.TrySetCullFace(mode, out var failure), failure);
    public void FrontFace(uint mode) => NativeCall(Native.TrySetFrontFace(mode, out var failure), failure);
    public void ActiveTexture(uint texture) => NativeCall(Native.TrySetActiveTexture(texture, out var failure), failure);
    public void BlendColor(float red, float green, float blue, float alpha) => NativeCall(Native.TrySetBlendColor(red, green, blue, alpha, out var failure), failure);
    public void LineWidth(float width) => NativeCall(Native.TrySetLineWidth(width, out var failure), failure);
    public void PolygonOffset(float factor, float units) => NativeCall(Native.TrySetPolygonOffset(factor, units, out var failure), failure);
    public void SampleCoverage(float value, bool invert) => NativeCall(Native.TrySetSampleCoverage(value, invert, out var failure), failure);
    public void ClearColor(float red, float green, float blue, float alpha)
    {
        _clearRed = red; _clearGreen = green; _clearBlue = blue; _clearAlpha = alpha;
        NativeCall(Native.TrySetClearColor(red, green, blue, alpha, out var failure), failure);
    }
    public void DrawArraysInstanced(uint mode, int first, int count, int instances) { NativeCall(Native.TryDrawArraysInstanced(mode, first, count, instances, out var failure), failure); _present?.Invoke(); }
    public void DrawElementsInstanced(uint mode, int count, uint type, int offset, int instances) { NativeCall(Native.TryDrawElementsInstanced(mode, count, type, offset, instances, out var failure), failure); _present?.Invoke(); }
    public bool IsEnabled(uint capability)
    {
        if (Native.TryGetParameter(capability, out var value, out var failure)) return value.Boolean;
        EnqueueFailure(failure); return false;
    }
    public ScriptValue GetParameter(uint parameter)
    {
        if (parameter == 0x8B8D)
            return _currentProgram is { } current
                ? Engine is null ? ScriptValue.FromObject(current) : WebGLProgramBinding.CreateObject(Engine, current)
                : ScriptValue.Null;
        if (!Native.TryGetParameter(parameter, out var value, out var failure)) return FailureValue(failure);
        if (value.Kind == WebGlParameterKind.Int32)
        {
            var handle = unchecked((uint)value.Int32);
            if (parameter == 0x8894 && Registry.TryGetBuffer(new WebGlBufferHandle(handle), out var buffer))
                return Engine is null ? ScriptValue.FromObject(buffer) : WebGLBufferBinding.CreateObject(Engine, buffer);
            if (parameter == 0x8895 && Registry.TryGetBuffer(new WebGlBufferHandle(handle), out var elementBuffer))
                return Engine is null ? ScriptValue.FromObject(elementBuffer) : WebGLBufferBinding.CreateObject(Engine, elementBuffer);
            if (parameter == 0x85B5 && Registry.TryGetVertexArray(new WebGlVertexArrayHandle(handle), out var array))
                return Engine is null ? ScriptValue.FromObject(array) : WebGLVertexArrayObjectBinding.CreateObject(Engine, array);
            if (parameter is 0x8894 or 0x8895 or 0x85B5) return ScriptValue.Null;
        }
        return ToScriptValue(value);
    }
    public bool IsContextLost() => Native.IsLost;
    public IReadOnlyList<string> GetSupportedExtensions()
    {
        var result = new List<string> { "WEBGL_lose_context" };
        if (SupportsDriverExtension("EXT_color_buffer_float")) result.Add("EXT_color_buffer_float");
        if (SupportsDriverExtension("EXT_float_blend")) result.Add("EXT_float_blend");
        return result;
    }
    public void Flush() => NativeCall(Native.TryFlush(out var failure), failure);
    public void Finish() => NativeCall(Native.TryFinish(out var failure), failure);
    public void PixelStorei(uint pname, int param) => NativeCall(Native.TryPixelStore(pname, param, out var failure), failure);
    public void StencilFunc(uint function, int reference, uint mask) => NativeCall(Native.TrySetStencilFunction(0x0408, function, reference, mask, out var failure), failure);
    public void StencilFuncSeparate(uint face, uint function, int reference, uint mask) => NativeCall(Native.TrySetStencilFunction(face, function, reference, mask, out var failure), failure);
    public void StencilMask(uint mask) => NativeCall(Native.TrySetStencilMask(0x0408, mask, out var failure), failure);
    public void StencilMaskSeparate(uint face, uint mask) => NativeCall(Native.TrySetStencilMask(face, mask, out var failure), failure);
    public void StencilOp(uint fail, uint depthFail, uint depthPass) => NativeCall(Native.TrySetStencilOperation(0x0408, fail, depthFail, depthPass, out var failure), failure);
    public void StencilOpSeparate(uint face, uint fail, uint depthFail, uint depthPass) => NativeCall(Native.TrySetStencilOperation(face, fail, depthFail, depthPass, out var failure), failure);
    public ScriptValue GetExtension(string name)
    {
        // WEBGL_lose_context is implemented by the host lifecycle. Native
        // GLES extension names are not WebGL extension objects and must not be
        // exposed merely because the driver reports them.
        var isLoss = string.Equals(name, "WEBGL_lose_context", StringComparison.OrdinalIgnoreCase);
        var isFloatColor = string.Equals(name, "EXT_color_buffer_float", StringComparison.OrdinalIgnoreCase);
        var isFloatBlend = string.Equals(name, "EXT_float_blend", StringComparison.OrdinalIgnoreCase);
        if (!isLoss && (!isFloatColor && !isFloatBlend || !SupportsDriverExtension(name))) return ScriptValue.Null;
        if (Native.IsLost) return ScriptValue.Null;
        lock (_extensions)
        {
            if (_extensions.TryGetValue(name, out var extension)) return extension;
            extension = _loseContextTemplate is not null && Engine is not null
                ? Engine.CreateHostObject(_loseContextTemplate, new WebGlLoseContextBridge(_loseContext, _restoreContext))
                : ScriptValue.FromObject(name);
            _extensions[name] = extension;
            return extension;
        }
    }

    private bool SupportsDriverExtension(string name)
    {
        if (!Native.TryGetExtensions(out var extensions, out _)) return false;
        return extensions.Any(extension => string.Equals(extension, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, "GL_" + name, StringComparison.OrdinalIgnoreCase));
    }
    public ScriptValue ReadPixels(int x, int y, int width, int height, uint format, uint type, ScriptBufferSource pixels)
    {
        if (width < 0 || height < 0) { EnqueueError(0x0501); return ScriptValue.Undefined; }
        var packed = type is 0x8363 or 0x8033 or 0x8034;
        var bytesPerPixel = (format == 0x1908 || format == 0x1907) && (type == 0x1401 || (type == 0x1406 && format == 0x1908) || (packed && ((format == 0x1907 && type == 0x8363) || format == 0x1908))) ? (type == 0x1406 ? 16 : packed ? 2 : format == 0x1908 ? 4 : 3) : 0;
        if (bytesPerPixel == 0) { EnqueueError(0x0500); return ScriptValue.Undefined; }
        if ((type == 0x1401 && pixels.Info.Kind is not (ScriptBufferSourceKind.Uint8 or ScriptBufferSourceKind.Uint8Clamped)) || (type == 0x1406 && pixels.Info.Kind != ScriptBufferSourceKind.Float32) || (packed && pixels.Info.Kind != ScriptBufferSourceKind.Uint16)) { EnqueueError(0x0502); return ScriptValue.Undefined; }
        var bytes = new byte[pixels.Info.ByteLength];
        if (!Native.TryReadPixels(x, y, width, height, bytes, format, type, out var failure)) return FailureValue(failure);
        if (Engine is null || !Engine.TryWriteBufferSource(pixels.Value, 0, bytes)) { EnqueueError(0x0502); return ScriptValue.Undefined; }
        return ScriptValue.Undefined;
    }
    public void Clear(uint mask) { NativeCall(Native.TryClearMask(mask, out var failure), failure); _present?.Invoke(); }
    public void DrawArrays(uint mode, int first, int count) { NativeCall(Native.TryDrawArrays(mode, first, count, out var failure), failure); _present?.Invoke(); }
    public void DrawElements(uint mode, int count, uint type, int offset) { NativeCall(Native.TryDrawElements(mode, count, type, offset, out var failure), failure); _present?.Invoke(); }

    private byte[] CopySource(ScriptBufferSource source)
    {
        var bytes = new byte[source.Info.ByteLength];
        if (Engine is null || !Engine.TryCopyBufferSource(source.Value, 0, bytes))
        {
            EnqueueError(0x0502);
            return [];
        }
        return bytes;
    }
    private void UniformFloat(IWebGLUniformLocationTarget? location, int components, float[] values)
    {
        if (location is null) return;
        try { var value = Require<WebGlUniformLocationObject>(location); value.EnsureCurrent(); NativeCall(Native.TryUniformFloat(value.Handle, components, values, out var failure), failure); }
        catch (WebGlValidationException error) { EnqueueFailure(error); }
    }
    private void UniformInt(IWebGLUniformLocationTarget? location, int components, int[] values)
    {
        if (location is null) return;
        try { var value = Require<WebGlUniformLocationObject>(location); value.EnsureCurrent(); NativeCall(Native.TryUniformInt(value.Handle, components, values, out var failure), failure); }
        catch (WebGlValidationException error) { EnqueueFailure(error); }
    }
    private void UniformMatrix(IWebGLUniformLocationTarget? location, int columns, int rows, bool transpose, float[] values)
    {
        if (location is null) return;
        try { var value = Require<WebGlUniformLocationObject>(location); value.EnsureCurrent(); NativeCall(Native.TryUniformMatrix(value.Handle, columns, rows, values, transpose, out var failure), failure); }
        catch (WebGlValidationException error) { EnqueueFailure(error); }
    }
    private void UniformUInt(IWebGLUniformLocationTarget? location, int components, uint[] values)
    {
        if (location is null) return;
        try { var value = Require<WebGlUniformLocationObject>(location); value.EnsureCurrent(); NativeCall(Native.TryUniformUInt(value.Handle, components, values, out var failure), failure); }
        catch (WebGlValidationException error) { EnqueueFailure(error); }
    }
    private void SetVertexAttribConstant(int index, IReadOnlyList<float> values, int components)
    {
        if (values.Count < components) { EnqueueError(0x0501); return; }
        var next = new float[components];
        for (var i = 0; i < components; i++) next[i] = values[i];
        if (components < 4) next = components switch
        {
            1 => [next[0], 0, 0, 1],
            2 => [next[0], next[1], 0, 1],
            3 => [next[0], next[1], next[2], 1],
            _ => next,
        };
        NativeCall(Native.TrySetVertexAttribConstant(new WebGlAttribLocation(index), next, out var failure), failure);
    }
    private void NativeCall(bool succeeded, GpuFailure? failure) { if (!succeeded) EnqueueFailure(failure); }
    private ScriptValue FailureValue(GpuFailure? failure) { EnqueueFailure(failure); return ScriptValue.Undefined; }

    private ScriptValue ToScriptValue(WebGlParameterValue value) => value.Kind switch
    {
        WebGlParameterKind.Int32 => ScriptValue.FromNumber(value.Int32),
        WebGlParameterKind.UInt32 => ScriptValue.FromNumber(value.UInt32),
        WebGlParameterKind.Int64 => ScriptValue.FromNumber(value.Int64),
        WebGlParameterKind.Float32 => ScriptValue.FromNumber(value.Float32),
        WebGlParameterKind.Boolean => ScriptValue.FromBoolean(value.Boolean),
        WebGlParameterKind.BooleanArray => CreateBooleanArray(value.Int32Array),
        WebGlParameterKind.String => ScriptValue.FromString(value.String),
        WebGlParameterKind.Int32Array => CreateTypedArray(ScriptBufferSourceKind.Int32, value.Int32Array),
        WebGlParameterKind.UInt32Array => CreateTypedArray(ScriptBufferSourceKind.Uint32, value.UInt32Array),
        WebGlParameterKind.Float32Array => CreateTypedArray(ScriptBufferSourceKind.Float32, value.Float32Array),
        _ => ScriptValue.Undefined,
    };

    private ScriptValue CreateTypedArray(ScriptBufferSourceKind kind, IReadOnlyList<int>? values)
    {
        if (values is null) return ScriptValue.Undefined;
        var bytes = new byte[checked(values.Count * sizeof(int))];
        for (var i = 0; i < values.Count; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * sizeof(int), sizeof(int)), values[i]);
        return Engine?.CreateTypedArray(kind, bytes) ?? ScriptValue.FromArray(values.ToArray());
    }

    private ScriptValue CreateTypedArray(ScriptBufferSourceKind kind, IReadOnlyList<uint>? values)
    {
        if (values is null) return ScriptValue.Undefined;
        var bytes = new byte[checked(values.Count * sizeof(uint))];
        for (var i = 0; i < values.Count; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * sizeof(uint), sizeof(uint)), values[i]);
        return Engine?.CreateTypedArray(kind, bytes) ?? ScriptValue.FromArray(values.ToArray());
    }

    private ScriptValue CreateTypedArray(ScriptBufferSourceKind kind, IReadOnlyList<float>? values)
    {
        if (values is null) return ScriptValue.Undefined;
        var bytes = new byte[checked(values.Count * sizeof(float))];
        for (var i = 0; i < values.Count; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * sizeof(float), sizeof(float)), values[i]);
        return Engine?.CreateTypedArray(kind, bytes) ?? ScriptValue.FromArray(values.ToArray());
    }

    private ScriptValue CreateBooleanArray(IReadOnlyList<int>? values)
    {
        if (values is null) return ScriptValue.Undefined;
        var result = values.Select(static value => ScriptValue.FromBoolean(value != 0)).ToArray();
        return Engine?.CreateArray(result) ?? ScriptValue.FromArray(result);
    }

    private void EnqueueFailure(GpuFailure? failure)
    {
        // A document GPU budget exhaustion is a device-level failure, not a
        // recoverable per-call validation error.  Lose the context before
        // reporting OUT_OF_MEMORY so the page receives the WebGL lifecycle
        // signal and all pre-loss objects become invalid.
        if (failure?.Kind == GpuFailureKind.BudgetExceeded && !_losingContext)
        {
            _losingContext = true;
            try { _loseContext?.Invoke(); }
            finally { _losingContext = false; }
        }
        if (failure?.ApiError is { } apiError)
        {
            EnqueueError(apiError);
            return;
        }
        EnqueueError(failure?.Kind switch
        {
            GpuFailureKind.Unavailable => 0x9242u,
            GpuFailureKind.BudgetExceeded => 0x0505u, // OUT_OF_MEMORY
            _ => 0x0502u,
        });
    }
    private bool _losingContext;
    private void EnqueueFailure(WebGlValidationException failure) => EnqueueError(failure.Failure == WebGlValidationFailure.ContextLost ? 0x9242u : failure.Failure == WebGlValidationFailure.InvalidValue ? 0x0501u : 0x0502u);
    private void EnqueueError(uint error) { lock (_errors) if (_errors.Count < 64) _errors.Enqueue(error); }
    protected void RecordNativeFailure(GpuFailure? failure) => EnqueueFailure(failure);

    private T Require<T>(object value) where T : WebGlObject
    {
        if (value is not T typed) throw new WebGlValidationException(WebGlValidationFailure.InvalidOperation, "The WebGL object has the wrong target type.");
        Registry.Validate(typed);
        return typed;
    }

}

internal sealed class WebGlLoseContextBridge(Action? lose, Func<bool>? restore)
{
    internal void Lose() => lose?.Invoke();
    internal void Restore() => _ = restore?.Invoke();
}
