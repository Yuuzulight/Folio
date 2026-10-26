using Folio.Scripting.Gpu;
using Folio.Scripting.Bindings;

namespace Folio.Scripting.Dom;

/// <summary>Identity and lifetime state for one native WebGL context.</summary>
/// <remarks>
/// WebGL objects are opaque host wrappers.  Their native numeric names never
/// cross this boundary; a wrapper is valid only for this context generation.
/// </remarks>
internal sealed class WebGlObjectRegistry : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<WebGlBufferHandle, WebGlBufferObject> _buffers = [];
    private readonly Dictionary<WebGlTextureHandle, WebGlTextureObject> _textures = [];
    private readonly Dictionary<WebGlFramebufferHandle, WebGlFramebufferObject> _framebuffers = [];
    private readonly Dictionary<WebGlRenderbufferHandle, WebGlRenderbufferObject> _renderbuffers = [];
    private readonly Dictionary<WebGlShaderHandle, WebGlShaderObject> _shaders = [];
    private readonly Dictionary<WebGlProgramHandle, WebGlProgramObject> _programs = [];
    private readonly Dictionary<WebGlVertexArrayHandle, WebGlVertexArrayObject> _vertexArrays = [];
    private readonly Dictionary<(WebGlProgramHandle Program, long Generation, int Location), WebGlUniformLocationObject> _uniformLocations = [];
    private bool _disposed;
    private long _generation = 1;

    internal WebGlObjectRegistry(INativeWebGlContext native) => Native = native ?? throw new ArgumentNullException(nameof(native));

    internal INativeWebGlContext Native { get; }
    internal long Generation { get { lock (_gate) return _generation; } }
    internal bool IsLive
    {
        get { lock (_gate) return !_disposed && !Native.IsLost; }
    }

    internal void BeginContextLoss()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _generation++;
            foreach (var value in _uniformLocations.Values) value.Invalidate();
            foreach (var value in _buffers.Values) value.Invalidate();
            foreach (var value in _textures.Values) value.Invalidate();
            foreach (var value in _framebuffers.Values) value.Invalidate();
            foreach (var value in _renderbuffers.Values) value.Invalidate();
            foreach (var value in _shaders.Values) value.Invalidate();
            foreach (var value in _programs.Values) value.Invalidate();
            foreach (var value in _vertexArrays.Values) value.Invalidate();
        }
    }

    internal WebGlBufferObject? CreateBuffer(out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            EnsureContextLive();
            if (!Native.TryCreateBuffer(out var handle, out failure)) return null;
            var result = new WebGlBufferObject(this, handle, _generation);
            _buffers[handle] = result;
            return result;
        }
    }

    internal WebGlShaderObject? CreateShader(uint type, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            EnsureContextLive();
            if (!Native.TryCreateShader(type, out var handle, out failure)) return null;
            var result = new WebGlShaderObject(this, handle, _generation, type);
            _shaders[handle] = result;
            return result;
        }
    }

    internal WebGlProgramObject? CreateProgram(out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            EnsureContextLive();
            if (!Native.TryCreateProgram(out var handle, out failure)) return null;
            var result = new WebGlProgramObject(this, handle, _generation);
            _programs[handle] = result;
            return result;
        }
    }

    internal WebGlVertexArrayObject? CreateVertexArray(out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            EnsureContextLive();
            if (!Native.TryCreateVertexArray(out var handle, out failure)) return null;
            var result = new WebGlVertexArrayObject(this, handle, _generation);
            _vertexArrays[handle] = result;
            return result;
        }
    }

    internal WebGlTextureObject? CreateTexture(out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null; EnsureContextLive();
            if (!Native.TryCreateTexture(out var handle, out failure)) return null;
            var result = new WebGlTextureObject(this, handle, _generation); _textures[handle] = result; return result;
        }
    }

    internal WebGlFramebufferObject? CreateFramebuffer(out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null; EnsureContextLive();
            if (!Native.TryCreateFramebuffer(out var handle, out failure)) return null;
            var result = new WebGlFramebufferObject(this, handle, _generation); _framebuffers[handle] = result; return result;
        }
    }

    internal WebGlRenderbufferObject? CreateRenderbuffer(out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null; EnsureContextLive();
            if (!Native.TryCreateRenderbuffer(out var handle, out failure)) return null;
            var result = new WebGlRenderbufferObject(this, handle, _generation); _renderbuffers[handle] = result; return result;
        }
    }

    internal WebGlUniformLocationObject? CreateUniformLocation(WebGlProgramObject program, int location)
    {
        lock (_gate)
        {
            Validate(program);
            var result = new WebGlUniformLocationObject(this, program, new WebGlUniformLocation(location), program.LinkGeneration, _generation);
            _uniformLocations[(program.Handle, program.LinkGeneration, location)] = result;
            return result;
        }
    }

    internal bool TryGetBuffer(WebGlBufferHandle handle, out WebGlBufferObject value) => _buffers.TryGetValue(handle, out value!);
    internal bool TryGetShader(WebGlShaderHandle handle, out WebGlShaderObject value) => _shaders.TryGetValue(handle, out value!);
    internal bool TryGetProgram(WebGlProgramHandle handle, out WebGlProgramObject value) => _programs.TryGetValue(handle, out value!);
    internal bool TryGetVertexArray(WebGlVertexArrayHandle handle, out WebGlVertexArrayObject value) => _vertexArrays.TryGetValue(handle, out value!);

    internal void Validate(WebGlObject value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            if (!ReferenceEquals(value.Owner, this)) throw new WebGlValidationException(WebGlValidationFailure.InvalidOperation, "The WebGL object belongs to another context.");
            if (_disposed || Native.IsLost || value.Generation != _generation)
                throw new WebGlValidationException(WebGlValidationFailure.ContextLost, "The WebGL object belongs to a lost or retired context.");
            if (value.Deleted) throw new WebGlValidationException(WebGlValidationFailure.InvalidOperation, "The WebGL object has been deleted.");
        }
    }

    internal void ValidateAllowDeleted(WebGlObject value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            if (!ReferenceEquals(value.Owner, this)) throw new WebGlValidationException(WebGlValidationFailure.InvalidOperation, "The WebGL object belongs to another context.");
            if (_disposed || Native.IsLost || value.Generation != _generation)
                throw new WebGlValidationException(WebGlValidationFailure.ContextLost, "The WebGL object belongs to a lost or retired context.");
        }
    }

    internal void ValidateForDetach(WebGlShaderObject shader)
    {
        ArgumentNullException.ThrowIfNull(shader);
        lock (_gate)
        {
            if (!ReferenceEquals(shader.Owner, this)) throw new WebGlValidationException(WebGlValidationFailure.InvalidOperation, "The WebGL object belongs to another context.");
            if (_disposed || Native.IsLost || shader.Generation != _generation)
                throw new WebGlValidationException(WebGlValidationFailure.ContextLost, "The WebGL object belongs to a lost or retired context.");
        }
    }

    internal void Retire(WebGlObject value)
    {
        lock (_gate)
        {
            switch (value)
            {
                case WebGlBufferObject buffer: _buffers.Remove(buffer.Handle); break;
                case WebGlTextureObject texture: _textures.Remove(texture.Handle); break;
                case WebGlFramebufferObject framebuffer: _framebuffers.Remove(framebuffer.Handle); break;
                case WebGlRenderbufferObject renderbuffer: _renderbuffers.Remove(renderbuffer.Handle); break;
                case WebGlShaderObject shader: _shaders.Remove(shader.Handle); break;
                case WebGlProgramObject program: _programs.Remove(program.Handle); break;
                case WebGlVertexArrayObject array: _vertexArrays.Remove(array.Handle); break;
                case WebGlUniformLocationObject uniform: _uniformLocations.Remove((uniform.Program.Handle, uniform.LinkGeneration, uniform.Handle.Value)); break;
            }
        }
    }

    private void EnsureContextLive()
    {
        if (_disposed || Native.IsLost) throw new WebGlValidationException(WebGlValidationFailure.ContextLost, "The WebGL context is lost.");
    }

    internal void DisposeObject(WebGlObject value)
    {
        lock (_gate)
        {
            if (value.Deleted) return;
            value.Deleted = true;
            switch (value)
            {
                case WebGlBufferObject buffer:
                    Native.TryDeleteBuffer(buffer.Handle, out _); _buffers.Remove(buffer.Handle); break;
                case WebGlTextureObject texture:
                    Native.TryDeleteTexture(texture.Handle, out _); _textures.Remove(texture.Handle); break;
                case WebGlFramebufferObject framebuffer:
                    Native.TryDeleteFramebuffer(framebuffer.Handle, out _); _framebuffers.Remove(framebuffer.Handle); break;
                case WebGlRenderbufferObject renderbuffer:
                    Native.TryDeleteRenderbuffer(renderbuffer.Handle, out _); _renderbuffers.Remove(renderbuffer.Handle); break;
                case WebGlVertexArrayObject array:
                    Native.TryDeleteVertexArray(array.Handle, out _); _vertexArrays.Remove(array.Handle); break;
                case WebGlProgramObject program:
                    program.DeleteNative(); _programs.Remove(program.Handle); break;
                case WebGlShaderObject shader:
                    shader.DeleteOrDeferNative();
                    if (shader.Attachments.Count == 0) _shaders.Remove(shader.Handle);
                    break;
                case WebGlUniformLocationObject uniform:
                    _uniformLocations.Remove((uniform.Program.Handle, uniform.LinkGeneration, uniform.Handle.Value)); break;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            foreach (var shader in _shaders.Values) shader.Invalidate();
            foreach (var program in _programs.Values) program.Invalidate();
            foreach (var buffer in _buffers.Values) buffer.Invalidate();
            foreach (var renderbuffer in _renderbuffers.Values) renderbuffer.Invalidate();
            foreach (var array in _vertexArrays.Values) array.Invalidate();
            foreach (var uniform in _uniformLocations.Values) uniform.Invalidate();
            _uniformLocations.Clear(); _buffers.Clear(); _textures.Clear(); _framebuffers.Clear(); _renderbuffers.Clear(); _shaders.Clear(); _programs.Clear(); _vertexArrays.Clear();
            Native.Dispose();
        }
    }
}

internal abstract class WebGlObject(WebGlObjectRegistry owner, long generation)
{
    internal WebGlObjectRegistry Owner { get; } = owner;
    internal long Generation { get; } = generation;
    internal bool Deleted { get; set; }
    internal void Invalidate() => Deleted = true;
    internal void EnsureLive() => Owner.Validate(this);
    internal void Delete() => Owner.DisposeObject(this);
}

internal enum WebGlValidationFailure
{
    InvalidOperation,
    InvalidValue,
    ContextLost,
}

/// <summary>Internal control flow only; the generated adapter converts this to getError/no-op semantics.</summary>
internal sealed class WebGlValidationException(WebGlValidationFailure failure, string message) : Exception(message)
{
    internal WebGlValidationFailure Failure { get; } = failure;
}

internal sealed class WebGlBufferObject(WebGlObjectRegistry owner, WebGlBufferHandle handle, long generation) : WebGlObject(owner, generation), IWebGLBufferTarget
{
    internal WebGlBufferHandle Handle { get; } = handle;
}

internal sealed class WebGlTextureObject(WebGlObjectRegistry owner, WebGlTextureHandle handle, long generation) : WebGlObject(owner, generation), IWebGLTextureTarget
{
    internal WebGlTextureHandle Handle { get; } = handle;
}

internal sealed class WebGlFramebufferObject(WebGlObjectRegistry owner, WebGlFramebufferHandle handle, long generation) : WebGlObject(owner, generation), IWebGLFramebufferTarget
{
    internal WebGlFramebufferHandle Handle { get; } = handle;
}

internal sealed class WebGlRenderbufferObject(WebGlObjectRegistry owner, WebGlRenderbufferHandle handle, long generation) : WebGlObject(owner, generation), IWebGLRenderbufferTarget
{
    internal WebGlRenderbufferHandle Handle { get; } = handle;
}

internal sealed class WebGlVertexArrayObject(WebGlObjectRegistry owner, WebGlVertexArrayHandle handle, long generation) : WebGlObject(owner, generation), IWebGLVertexArrayObjectTarget
{
    internal WebGlVertexArrayHandle Handle { get; } = handle;
}

internal sealed class WebGlShaderObject(WebGlObjectRegistry owner, WebGlShaderHandle handle, long generation, uint type) : WebGlObject(owner, generation), IWebGLShaderTarget
{
    private bool _nativeDeletePending;
    internal WebGlShaderHandle Handle { get; } = handle;
    internal uint Type { get; } = type;
    internal HashSet<WebGlProgramObject> Attachments { get; } = [];
    internal void DeleteOrDeferNative()
    {
        if (Attachments.Count != 0) { _nativeDeletePending = true; return; }
        Owner.Native.TryDeleteShader(Handle, out _);
    }
    internal void Detached(WebGlProgramObject program)
    {
        Attachments.Remove(program);
        if (_nativeDeletePending && Attachments.Count == 0)
        {
            _nativeDeletePending = false;
            Owner.Native.TryDeleteShader(Handle, out _);
        }
    }
}

internal sealed class WebGlProgramObject(WebGlObjectRegistry owner, WebGlProgramHandle handle, long generation) : WebGlObject(owner, generation), IWebGLProgramTarget
{
    internal WebGlProgramHandle Handle { get; } = handle;
    internal long LinkGeneration { get; private set; } = 1;
    internal HashSet<WebGlShaderObject> AttachedShaders { get; } = [];
    internal void Attach(WebGlShaderObject shader)
    {
        Owner.Validate(this); Owner.Validate(shader);
        if (AttachedShaders.Contains(shader))
            throw new WebGlValidationException(WebGlValidationFailure.InvalidOperation, "The shader is already attached to this program.");
        if (AttachedShaders.Add(shader))
        {
            if (!Owner.Native.TryAttachShader(Handle, shader.Handle, out var failure))
            {
                AttachedShaders.Remove(shader);
                throw WebGlException(failure);
            }
            shader.Attachments.Add(this);
        }
    }
    internal void Detach(WebGlShaderObject shader)
    {
        Owner.Validate(this); Owner.ValidateForDetach(shader);
        if (!AttachedShaders.Contains(shader)) return;
        if (!Owner.Native.TryDetachShader(Handle, shader.Handle, out var failure))
            throw WebGlException(failure);
        AttachedShaders.Remove(shader);
        shader.Detached(this);
    }
    internal bool Link(out string infoLog)
    {
        Owner.Validate(this);
        LinkGeneration++;
        return Owner.Native.TryLinkProgram(Handle, out var linked, out infoLog, out _) && linked;
    }
    internal void DeleteNative()
    {
        foreach (var shader in AttachedShaders.ToArray())
        {
            Owner.Native.TryDetachShader(Handle, shader.Handle, out _);
            shader.Detached(this);
        }
        AttachedShaders.Clear();
        Owner.Native.TryDeleteProgram(Handle, out _);
    }
    private static Exception WebGlException(GpuFailure? failure) =>
        new WebGlValidationException(WebGlValidationFailure.InvalidOperation, failure?.Message ?? "The native WebGL operation failed.");
}

internal sealed class WebGlUniformLocationObject(WebGlObjectRegistry owner, WebGlProgramObject program, WebGlUniformLocation handle, long linkGeneration, long generation) : WebGlObject(owner, generation), IWebGLUniformLocationTarget
{
    internal WebGlProgramObject Program { get; } = program;
    internal WebGlUniformLocation Handle { get; } = handle;
    internal long LinkGeneration { get; } = linkGeneration;
    internal void EnsureCurrent()
    {
        EnsureLive();
        if (Program.Deleted || Program.LinkGeneration != LinkGeneration)
            throw new WebGlValidationException(WebGlValidationFailure.InvalidOperation, "The uniform location is no longer valid.");
    }
}

internal sealed class WebGlActiveInfoObject(WebGlActiveInfo info) : IWebGLActiveInfoTarget
{
    public string Name => info.Name;
    public int Size => info.Size;
    public uint Type => info.Type;
}

internal sealed class WebGlShaderPrecisionFormatObject(int rangeMin, int rangeMax, int precision) : IWebGLShaderPrecisionFormatTarget
{
    public int RangeMin => rangeMin;
    public int RangeMax => rangeMax;
    public int Precision => precision;
}
