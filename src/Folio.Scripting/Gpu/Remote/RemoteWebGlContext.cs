using System.Runtime.InteropServices;
using System.Text;
using Op = Folio.Scripting.Gpu.Remote.GpuWebGlOp;

namespace Folio.Scripting.Gpu.Remote;

/// <summary>
/// Document-side WebGL context that executes in the GPU worker. Calls without outputs are queued
/// and report success when accepted; a native failure for them is reported through
/// <see cref="TakeDeferredFailures"/>. Calls with outputs flush the session and wait.
/// Object handles are allocated here and mapped to native objects by the worker.
/// </summary>
internal sealed class RemoteWebGlContext : INativeWebGlContext, IDeferredGpuFailures
{
    private const int MaxDeferred = 64;
    private readonly GpuWebGlSession _session;
    private readonly uint _id;
    private readonly List<GpuFailure> _deferred = [];
    private bool _lost, _disposed;

    internal RemoteWebGlContext(GpuWebGlSession session, uint id, WebGlContextAttributes attributes, int width, int height)
    {
        _session = session; _id = id; Attributes = attributes;
        DrawingBufferWidth = width; DrawingBufferHeight = height;
    }

    public WebGlContextAttributes Attributes { get; }
    public int DrawingBufferWidth { get; private set; }
    public int DrawingBufferHeight { get; private set; }
    public bool IsLost => _lost || _disposed || _session.IsLost;
    public long ReservedBytes { get; private set; }
    public long PeakReservedBytes { get; private set; }

    internal void AddDeferred(GpuFailure failure) { if (_deferred.Count < MaxDeferred) _deferred.Add(failure); }
    internal void Update(long reserved, long peak, bool lost, int width, int height)
    {
        ReservedBytes = reserved; PeakReservedBytes = peak; _lost |= lost;
        DrawingBufferWidth = width; DrawingBufferHeight = height;
    }
    internal void MarkLost() => _lost = true;

    public IReadOnlyList<GpuFailure> TakeDeferredFailures()
    {
        lock (_session.Gate)
        {
            _session.Flush(out _);
            if (_deferred.Count == 0) return [];
            var failures = _deferred.ToArray();
            _deferred.Clear();
            return failures;
        }
    }

    // ---- encoding helpers -------------------------------------------------------------------
    private GpuPayloadWriter W(Op op) => _session.Begin(_id, op);
    private bool Defer(out GpuFailure? failure) => _session.Defer(out failure);
    private bool Blob(ReadOnlySpan<byte> data, out GpuFailure? failure) => _session.TryBlob(data, out failure);
    private static uint H(uint? value) => value ?? 0;
    private uint NewHandle() => _session.NextId();
    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
    private static byte[] Encode(IReadOnlyList<string> values) => new GpuPayloadWriter().Strings(values).Written.ToArray();

    private bool Sync(out GpuPayloadResult result, out GpuFailure? failure)
    {
        var outputs = _session.Sync(out failure);
        result = new(outputs ?? []);
        return outputs is not null;
    }

    /// <summary>Copies a readback result out of the readback arena into the caller's span.</summary>
    private bool Readback(Span<byte> destination, out GpuFailure? failure)
    {
        if (!Sync(out var result, out failure)) return false;
        var length = result.Reader().I32();
        if (length != destination.Length) { failure = GpuWebGlSession.LostFailure; return false; }
        _session.Readback.Read(0, destination);
        return true;
    }

    private bool CheckReadback(int length, out GpuFailure? failure)
    {
        failure = length <= _session.Readback.Length ? null : new(GpuFailureKind.NativeError, "The readback exceeds the GPU worker arena.", 0x0505);
        return failure is null;
    }

    private bool Simple(Op op, out GpuFailure? failure) { lock (_session.Gate) { W(op); return Defer(out failure); } }

    // ---- shaders and programs ---------------------------------------------------------------
    public bool TryCreateShader(uint shaderType, out WebGlShaderHandle shader, out GpuFailure? failure)
    {
        lock (_session.Gate) { var id = NewHandle(); shader = new(id); W(Op.CreateShader).U32(id).U32(shaderType); return Defer(out failure); }
    }
    public bool TrySetShaderSource(WebGlShaderHandle shader, ReadOnlySpan<byte> utf8Source, out GpuFailure? failure)
    {
        lock (_session.Gate) { W(Op.SetShaderSource).U32(shader.Value); return Blob(utf8Source, out failure) && Defer(out failure); }
    }
    public bool TryCompileShader(WebGlShaderHandle shader, out bool compiled, out string infoLog, out GpuFailure? failure)
    {
        compiled = false; infoLog = string.Empty;
        lock (_session.Gate)
        {
            W(Op.CompileShader).U32(shader.Value);
            if (!Sync(out var result, out failure)) return false;
            var reader = result.Reader(); compiled = reader.Bool(); infoLog = reader.String(); return true;
        }
    }
    public bool TryGetShaderParameter(WebGlShaderHandle shader, uint parameter, out WebGlParameterValue value, out GpuFailure? failure)
    {
        value = default;
        lock (_session.Gate)
        {
            W(Op.GetShaderParameter).U32(shader.Value).U32(parameter);
            if (!Sync(out var result, out failure)) return false;
            var reader = result.Reader(); value = GpuWebGlWire.Parameter(ref reader); return true;
        }
    }
    public bool TryGetShaderSource(WebGlShaderHandle shader, out string source, out GpuFailure? failure) => SyncString(Op.GetShaderSource, shader.Value, out source, out failure);
    public bool TryGetShaderInfoLog(WebGlShaderHandle shader, out string infoLog, out GpuFailure? failure) => SyncString(Op.GetShaderInfoLog, shader.Value, out infoLog, out failure);
    public bool TryGetShaderPrecisionFormat(uint shaderType, uint precisionType, out int rangeMin, out int rangeMax, out int precision, out GpuFailure? failure)
    {
        rangeMin = rangeMax = precision = 0;
        lock (_session.Gate)
        {
            W(Op.GetShaderPrecisionFormat).U32(shaderType).U32(precisionType);
            if (!Sync(out var result, out failure)) return false;
            var reader = result.Reader(); rangeMin = reader.I32(); rangeMax = reader.I32(); precision = reader.I32(); return true;
        }
    }
    public bool TryDeleteShader(WebGlShaderHandle shader, out GpuFailure? failure) { lock (_session.Gate) { W(Op.DeleteShader).U32(shader.Value); return Defer(out failure); } }
    public bool TryCreateProgram(out WebGlProgramHandle program, out GpuFailure? failure)
    {
        lock (_session.Gate) { var id = NewHandle(); program = new(id); W(Op.CreateProgram).U32(id); return Defer(out failure); }
    }
    public bool TryDeleteProgram(WebGlProgramHandle program, out GpuFailure? failure) { lock (_session.Gate) { W(Op.DeleteProgram).U32(program.Value); return Defer(out failure); } }
    public bool TryAttachShader(WebGlProgramHandle program, WebGlShaderHandle shader, out GpuFailure? failure) { lock (_session.Gate) { W(Op.AttachShader).U32(program.Value).U32(shader.Value); return Defer(out failure); } }
    public bool TryDetachShader(WebGlProgramHandle program, WebGlShaderHandle shader, out GpuFailure? failure) { lock (_session.Gate) { W(Op.DetachShader).U32(program.Value).U32(shader.Value); return Defer(out failure); } }
    public bool TryLinkProgram(WebGlProgramHandle program, out bool linked, out string infoLog, out GpuFailure? failure) => SyncFlagAndLog(Op.LinkProgram, program.Value, out linked, out infoLog, out failure);
    public bool TryValidateProgram(WebGlProgramHandle program, out bool valid, out string infoLog, out GpuFailure? failure) => SyncFlagAndLog(Op.ValidateProgram, program.Value, out valid, out infoLog, out failure);
    public bool TryGetProgramParameter(WebGlProgramHandle program, uint parameter, out WebGlParameterValue value, out GpuFailure? failure)
    {
        value = default;
        lock (_session.Gate)
        {
            W(Op.GetProgramParameter).U32(program.Value).U32(parameter);
            if (!Sync(out var result, out failure)) return false;
            var reader = result.Reader(); value = GpuWebGlWire.Parameter(ref reader); return true;
        }
    }
    public bool TryGetProgramInfoLog(WebGlProgramHandle program, out string infoLog, out GpuFailure? failure) => SyncString(Op.GetProgramInfoLog, program.Value, out infoLog, out failure);
    public bool TryGetAttachedShaders(WebGlProgramHandle program, Span<WebGlShaderHandle> shaders, out int count, out GpuFailure? failure)
    {
        count = 0;
        lock (_session.Gate)
        {
            W(Op.GetAttachedShaders).U32(program.Value).I32(shaders.Length);
            if (!Sync(out var result, out failure)) return false;
            var reader = result.Reader(); count = reader.I32();
            var ids = GpuWebGlWire.UInts(ref reader);
            if (ids.Length > shaders.Length) { failure = GpuWebGlSession.LostFailure; return false; }
            for (var i = 0; i < ids.Length; i++) shaders[i] = new(ids[i]);
            return true;
        }
    }
    public bool TryGetActiveAttrib(WebGlProgramHandle program, uint index, out WebGlActiveInfo info, out GpuFailure? failure) => SyncActiveInfo(Op.GetActiveAttrib, program.Value, index, out info, out failure);
    public bool TryGetActiveUniform(WebGlProgramHandle program, uint index, out WebGlActiveInfo info, out GpuFailure? failure) => SyncActiveInfo(Op.GetActiveUniform, program.Value, index, out info, out failure);
    public bool TryBindAttribLocation(WebGlProgramHandle program, uint index, ReadOnlySpan<byte> utf8Name, out GpuFailure? failure)
    {
        lock (_session.Gate) { W(Op.BindAttribLocation).U32(program.Value).U32(index); return Blob(utf8Name, out failure) && Defer(out failure); }
    }
    public bool TryUseProgram(WebGlProgramHandle? program, out GpuFailure? failure) { lock (_session.Gate) { W(Op.UseProgram).U32(H(program?.Value)); return Defer(out failure); } }
    public bool TryGetAttribLocation(WebGlProgramHandle program, string name, out WebGlAttribLocation location, out GpuFailure? failure)
    {
        location = default;
        if (!SyncNamedInt(Op.GetAttribLocation, program.Value, name, out var value, out failure)) return false;
        location = new(value); return true;
    }
    public bool TryGetUniformLocation(WebGlProgramHandle program, string name, out WebGlUniformLocation location, out GpuFailure? failure)
    {
        location = default;
        if (!SyncNamedInt(Op.GetUniformLocation, program.Value, name, out var value, out failure)) return false;
        location = new(value); return true;
    }
    public bool TryGetUniformIndices(WebGlProgramHandle program, IReadOnlyList<string> names, Span<uint> indices, out GpuFailure? failure)
    {
        lock (_session.Gate)
        {
            W(Op.GetUniformIndices).U32(program.Value).I32(indices.Length);
            if (!Blob(Encode(names), out failure)) return false;
            if (!Sync(out var result, out failure)) return false;
            var reader = result.Reader(); var values = GpuWebGlWire.UInts(ref reader);
            if (values.Length != indices.Length) { failure = GpuWebGlSession.LostFailure; return false; }
            values.CopyTo(indices); return true;
        }
    }
    public bool TryGetActiveUniforms(WebGlProgramHandle program, ReadOnlySpan<uint> indices, uint parameter, Span<int> values, out GpuFailure? failure)
    {
        lock (_session.Gate)
        {
            W(Op.GetActiveUniforms).U32(program.Value).U32(parameter).I32(values.Length);
            if (!Blob(MemoryMarshal.AsBytes(indices), out failure)) return false;
            if (!Sync(out var result, out failure)) return false;
            var reader = result.Reader(); var decoded = GpuWebGlWire.Ints(ref reader);
            if (decoded.Length != values.Length) { failure = GpuWebGlSession.LostFailure; return false; }
            decoded.CopyTo(values); return true;
        }
    }
    public bool TryGetUniformBlockIndex(WebGlProgramHandle program, ReadOnlySpan<byte> name, out uint index, out GpuFailure? failure)
    {
        index = 0;
        lock (_session.Gate)
        {
            W(Op.GetUniformBlockIndex).U32(program.Value);
            if (!Blob(name, out failure)) return false;
            if (!Sync(out var result, out failure)) return false;
            index = result.Reader().U32(); return true;
        }
    }
    public bool TryUniformBlockBinding(WebGlProgramHandle program, uint index, uint binding, out GpuFailure? failure) { lock (_session.Gate) { W(Op.UniformBlockBinding).U32(program.Value).U32(index).U32(binding); return Defer(out failure); } }

    // ---- buffers ----------------------------------------------------------------------------
    public bool TryCreateBuffer(out WebGlBufferHandle buffer, out GpuFailure? failure)
    {
        lock (_session.Gate) { var id = NewHandle(); buffer = new(id); W(Op.CreateBuffer).U32(id); return Defer(out failure); }
    }
    public bool TryDeleteBuffer(WebGlBufferHandle buffer, out GpuFailure? failure) { lock (_session.Gate) { W(Op.DeleteBuffer).U32(buffer.Value); return Defer(out failure); } }
    public bool TryBindBuffer(uint target, WebGlBufferHandle? buffer, out GpuFailure? failure) { lock (_session.Gate) { W(Op.BindBuffer).U32(target).U32(H(buffer?.Value)); return Defer(out failure); } }
    public bool TryAllocateBufferStorage(uint target, long size, uint usage, out GpuFailure? failure) { lock (_session.Gate) { W(Op.AllocateBufferStorage).U32(target).I64(size).U32(usage); return Defer(out failure); } }
    public bool TryUploadBuffer(uint target, ReadOnlySpan<byte> data, uint usage, out GpuFailure? failure)
    {
        lock (_session.Gate) { W(Op.UploadBuffer).U32(target).U32(usage); return Blob(data, out failure) && Defer(out failure); }
    }
    public bool TryUpdateBuffer(uint target, int byteOffset, ReadOnlySpan<byte> data, out GpuFailure? failure)
    {
        lock (_session.Gate) { W(Op.UpdateBuffer).U32(target).I32(byteOffset); return Blob(data, out failure) && Defer(out failure); }
    }
    public bool TryCopyBuffer(uint readTarget, uint writeTarget, long readOffset, long writeOffset, long size, out GpuFailure? failure)
    {
        lock (_session.Gate) { W(Op.CopyBuffer).U32(readTarget).U32(writeTarget).I64(readOffset).I64(writeOffset).I64(size); return Defer(out failure); }
    }
    public bool TryBindBufferBase(uint target, uint index, WebGlBufferHandle? buffer, out GpuFailure? failure) { lock (_session.Gate) { W(Op.BindBufferBase).U32(target).U32(index).U32(H(buffer?.Value)); return Defer(out failure); } }
    public bool TryBindBufferRange(uint target, uint index, WebGlBufferHandle? buffer, long offset, long size, out GpuFailure? failure)
    {
        lock (_session.Gate) { W(Op.BindBufferRange).U32(target).U32(index).U32(H(buffer?.Value)).I64(offset).I64(size); return Defer(out failure); }
    }
    public bool TryGetBufferParameter(uint target, uint parameter, out WebGlParameterValue value, out GpuFailure? failure) => SyncParameter(Op.GetBufferParameter, target, parameter, out value, out failure);
    public bool TryReadBuffer(uint target, long offset, Span<byte> destination, out GpuFailure? failure)
    {
        lock (_session.Gate)
        {
            if (!CheckReadback(destination.Length, out failure)) return false;
            W(Op.GetBufferSubData).U32(target).I64(offset).I32(destination.Length);
            return Readback(destination, out failure);
        }
    }

    // ---- textures ---------------------------------------------------------------------------
    public bool TryCreateTexture(out WebGlTextureHandle texture, out GpuFailure? failure)
    {
        lock (_session.Gate) { var id = NewHandle(); texture = new(id); W(Op.CreateTexture).U32(id); return Defer(out failure); }
    }
    public bool TryDeleteTexture(WebGlTextureHandle texture, out GpuFailure? failure) { lock (_session.Gate) { W(Op.DeleteTexture).U32(texture.Value); return Defer(out failure); } }
    public bool TryBindTexture(uint target, WebGlTextureHandle? texture, out GpuFailure? failure) { lock (_session.Gate) { W(Op.BindTexture).U32(target).U32(H(texture?.Value)); return Defer(out failure); } }
    public bool TryTextureParameter(uint target, uint parameter, int value, out GpuFailure? failure) { lock (_session.Gate) { W(Op.TextureParameter).U32(target).U32(parameter).I32(value); return Defer(out failure); } }
    public bool TryGetTextureParameter(uint target, uint parameter, out WebGlParameterValue value, out GpuFailure? failure) => SyncParameter(Op.GetTextureParameter, target, parameter, out value, out failure);
    public bool TryGetTextureLevelParameter(uint target, int level, uint parameter, out WebGlParameterValue value, out GpuFailure? failure)
    {
        value = default;
        lock (_session.Gate)
        {
            W(Op.GetTextureLevelParameter).U32(target).I32(level).U32(parameter);
            if (!Sync(out var result, out failure)) return false;
            var reader = result.Reader(); value = GpuWebGlWire.Parameter(ref reader); return true;
        }
    }
    public bool TryTextureImage2D(uint target, int level, int internalFormat, int width, int height, uint format, uint type, ReadOnlySpan<byte> pixels, out GpuFailure? failure)
    {
        lock (_session.Gate) { W(Op.TextureImage2D).U32(target).I32(level).I32(internalFormat).I32(width).I32(height).U32(format).U32(type); return Blob(pixels, out failure) && Defer(out failure); }
    }
    public bool TryTextureStorage2D(uint target, int levels, uint internalFormat, int width, int height, out GpuFailure? failure)
    {
        lock (_session.Gate) { W(Op.TextureStorage2D).U32(target).I32(levels).U32(internalFormat).I32(width).I32(height); return Defer(out failure); }
    }
    public bool TryTextureSubImage2D(uint target, int level, int xOffset, int yOffset, int width, int height, uint format, uint type, ReadOnlySpan<byte> pixels, out GpuFailure? failure)
    {
        lock (_session.Gate) { W(Op.TextureSubImage2D).U32(target).I32(level).I32(xOffset).I32(yOffset).I32(width).I32(height).U32(format).U32(type); return Blob(pixels, out failure) && Defer(out failure); }
    }
    public bool TryCopyTextureImage2D(uint target, int level, int internalFormat, int x, int y, int width, int height, int border, out GpuFailure? failure)
    {
        lock (_session.Gate) { W(Op.CopyTextureImage2D).U32(target).I32(level).I32(internalFormat).I32(x).I32(y).I32(width).I32(height).I32(border); return Defer(out failure); }
    }
    public bool TryCopyTextureSubImage2D(uint target, int level, int xOffset, int yOffset, int x, int y, int width, int height, out GpuFailure? failure)
    {
        lock (_session.Gate) { W(Op.CopyTextureSubImage2D).U32(target).I32(level).I32(xOffset).I32(yOffset).I32(x).I32(y).I32(width).I32(height); return Defer(out failure); }
    }
    public bool TryTextureImage3D(uint target, int level, int internalFormat, int width, int height, int depth, uint format, uint type, ReadOnlySpan<byte> pixels, out GpuFailure? failure)
    {
        lock (_session.Gate) { W(Op.TextureImage3D).U32(target).I32(level).I32(internalFormat).I32(width).I32(height).I32(depth).U32(format).U32(type); return Blob(pixels, out failure) && Defer(out failure); }
    }
    public bool TryTextureStorage3D(uint target, int levels, uint internalFormat, int width, int height, int depth, out GpuFailure? failure)
    {
        lock (_session.Gate) { W(Op.TextureStorage3D).U32(target).I32(levels).U32(internalFormat).I32(width).I32(height).I32(depth); return Defer(out failure); }
    }
    public bool TryTextureSubImage3D(uint target, int level, int xOffset, int yOffset, int zOffset, int width, int height, int depth, uint format, uint type, ReadOnlySpan<byte> pixels, out GpuFailure? failure)
    {
        lock (_session.Gate) { W(Op.TextureSubImage3D).U32(target).I32(level).I32(xOffset).I32(yOffset).I32(zOffset).I32(width).I32(height).I32(depth).U32(format).U32(type); return Blob(pixels, out failure) && Defer(out failure); }
    }
    public bool TryGenerateMipmap(uint target, out GpuFailure? failure) { lock (_session.Gate) { W(Op.GenerateMipmap).U32(target); return Defer(out failure); } }

    // ---- framebuffers and renderbuffers -----------------------------------------------------
    public bool TryCreateFramebuffer(out WebGlFramebufferHandle framebuffer, out GpuFailure? failure)
    {
        lock (_session.Gate) { var id = NewHandle(); framebuffer = new(id); W(Op.CreateFramebuffer).U32(id); return Defer(out failure); }
    }
    public bool TryDeleteFramebuffer(WebGlFramebufferHandle framebuffer, out GpuFailure? failure) { lock (_session.Gate) { W(Op.DeleteFramebuffer).U32(framebuffer.Value); return Defer(out failure); } }
    public bool TryBindFramebuffer(uint target, WebGlFramebufferHandle? framebuffer, out GpuFailure? failure) { lock (_session.Gate) { W(Op.BindFramebuffer).U32(target).U32(H(framebuffer?.Value)); return Defer(out failure); } }
    public bool TryFramebufferTexture2D(uint target, uint attachment, uint textureTarget, WebGlTextureHandle? texture, int level, out GpuFailure? failure)
    {
        lock (_session.Gate) { W(Op.FramebufferTexture2D).U32(target).U32(attachment).U32(textureTarget).U32(H(texture?.Value)).I32(level); return Defer(out failure); }
    }
    public bool TryFramebufferTextureLayer(uint target, uint attachment, WebGlTextureHandle? texture, int level, int layer, out GpuFailure? failure)
    {
        lock (_session.Gate) { W(Op.FramebufferTextureLayer).U32(target).U32(attachment).U32(H(texture?.Value)).I32(level).I32(layer); return Defer(out failure); }
    }
    public bool TryCheckFramebufferStatus(uint target, out uint status, out GpuFailure? failure)
    {
        status = 0;
        lock (_session.Gate)
        {
            W(Op.CheckFramebufferStatus).U32(target);
            if (!Sync(out var result, out failure)) return false;
            status = result.Reader().U32(); return true;
        }
    }
    public bool TryBlitFramebuffer(int srcX0, int srcY0, int srcX1, int srcY1, int dstX0, int dstY0, int dstX1, int dstY1, uint mask, uint filter, out GpuFailure? failure)
    {
        lock (_session.Gate) { W(Op.BlitFramebuffer).I32(srcX0).I32(srcY0).I32(srcX1).I32(srcY1).I32(dstX0).I32(dstY0).I32(dstX1).I32(dstY1).U32(mask).U32(filter); return Defer(out failure); }
    }
    public bool TryCreateRenderbuffer(out WebGlRenderbufferHandle renderbuffer, out GpuFailure? failure)
    {
        lock (_session.Gate) { var id = NewHandle(); renderbuffer = new(id); W(Op.CreateRenderbuffer).U32(id); return Defer(out failure); }
    }
    public bool TryDeleteRenderbuffer(WebGlRenderbufferHandle renderbuffer, out GpuFailure? failure) { lock (_session.Gate) { W(Op.DeleteRenderbuffer).U32(renderbuffer.Value); return Defer(out failure); } }
    public bool TryBindRenderbuffer(uint target, WebGlRenderbufferHandle? renderbuffer, out GpuFailure? failure) { lock (_session.Gate) { W(Op.BindRenderbuffer).U32(target).U32(H(renderbuffer?.Value)); return Defer(out failure); } }
    public bool TryRenderbufferStorage(uint target, uint internalFormat, int width, int height, out GpuFailure? failure)
    {
        lock (_session.Gate) { W(Op.RenderbufferStorage).U32(target).U32(internalFormat).I32(width).I32(height); return Defer(out failure); }
    }
    public bool TryRenderbufferStorageMultisample(uint target, int samples, uint internalFormat, int width, int height, out GpuFailure? failure)
    {
        lock (_session.Gate) { W(Op.RenderbufferStorageMultisample).U32(target).I32(samples).U32(internalFormat).I32(width).I32(height); return Defer(out failure); }
    }
    public bool TryFramebufferRenderbuffer(uint target, uint attachment, uint renderbufferTarget, WebGlRenderbufferHandle? renderbuffer, out GpuFailure? failure)
    {
        lock (_session.Gate) { W(Op.FramebufferRenderbuffer).U32(target).U32(attachment).U32(renderbufferTarget).U32(H(renderbuffer?.Value)); return Defer(out failure); }
    }

    // ---- vertex arrays and transform feedback -----------------------------------------------
    public bool TryCreateVertexArray(out WebGlVertexArrayHandle vertexArray, out GpuFailure? failure)
    {
        lock (_session.Gate) { var id = NewHandle(); vertexArray = new(id); W(Op.CreateVertexArray).U32(id); return Defer(out failure); }
    }
    public bool TryDeleteVertexArray(WebGlVertexArrayHandle vertexArray, out GpuFailure? failure) { lock (_session.Gate) { W(Op.DeleteVertexArray).U32(vertexArray.Value); return Defer(out failure); } }
    public bool TryBindVertexArray(WebGlVertexArrayHandle? vertexArray, out GpuFailure? failure) { lock (_session.Gate) { W(Op.BindVertexArray).U32(H(vertexArray?.Value)); return Defer(out failure); } }
    public bool TryCreateTransformFeedback(out WebGlTransformFeedbackHandle feedback, out GpuFailure? failure)
    {
        lock (_session.Gate) { var id = NewHandle(); feedback = new(id); W(Op.CreateTransformFeedback).U32(id); return Defer(out failure); }
    }
    public bool TryDeleteTransformFeedback(WebGlTransformFeedbackHandle feedback, out GpuFailure? failure) { lock (_session.Gate) { W(Op.DeleteTransformFeedback).U32(feedback.Value); return Defer(out failure); } }
    public bool TryBindTransformFeedback(uint target, WebGlTransformFeedbackHandle? feedback, out GpuFailure? failure) { lock (_session.Gate) { W(Op.BindTransformFeedback).U32(target).U32(H(feedback?.Value)); return Defer(out failure); } }
    public bool TryBeginTransformFeedback(uint primitiveMode, out GpuFailure? failure) { lock (_session.Gate) { W(Op.BeginTransformFeedback).U32(primitiveMode); return Defer(out failure); } }
    public bool TryEndTransformFeedback(out GpuFailure? failure) => Simple(Op.EndTransformFeedback, out failure);
    public bool TryPauseTransformFeedback(out GpuFailure? failure) => Simple(Op.PauseTransformFeedback, out failure);
    public bool TryResumeTransformFeedback(out GpuFailure? failure) => Simple(Op.ResumeTransformFeedback, out failure);
    public bool TryTransformFeedbackVaryings(WebGlProgramHandle program, IReadOnlyList<string> varyings, uint bufferMode, out GpuFailure? failure)
    {
        lock (_session.Gate) { W(Op.TransformFeedbackVaryings).U32(program.Value).U32(bufferMode); return Blob(Encode(varyings), out failure) && Defer(out failure); }
    }
    public bool TryGetTransformFeedbackVarying(WebGlProgramHandle program, uint index, out WebGlActiveInfo info, out GpuFailure? failure) => SyncActiveInfo(Op.GetTransformFeedbackVarying, program.Value, index, out info, out failure);
    public bool TryEnableVertexAttrib(WebGlAttribLocation location, out GpuFailure? failure) { lock (_session.Gate) { W(Op.EnableVertexAttrib).I32(location.Value); return Defer(out failure); } }
    public bool TryDisableVertexAttrib(WebGlAttribLocation location, out GpuFailure? failure) { lock (_session.Gate) { W(Op.DisableVertexAttrib).I32(location.Value); return Defer(out failure); } }
    public bool TryVertexAttribPointer(WebGlAttribLocation location, int size, uint type, bool normalized, int stride, int offset, out GpuFailure? failure)
    {
        lock (_session.Gate) { W(Op.VertexAttribPointer).I32(location.Value).I32(size).U32(type).Bool(normalized).I32(stride).I32(offset); return Defer(out failure); }
    }
    public bool TryVertexAttribIPointer(WebGlAttribLocation location, int size, uint type, int stride, int offset, out GpuFailure? failure)
    {
        lock (_session.Gate) { W(Op.VertexAttribIPointer).I32(location.Value).I32(size).U32(type).I32(stride).I32(offset); return Defer(out failure); }
    }
    public bool TryVertexAttribDivisor(WebGlAttribLocation location, uint divisor, out GpuFailure? failure) { lock (_session.Gate) { W(Op.VertexAttribDivisor).I32(location.Value).U32(divisor); return Defer(out failure); } }
    public bool TrySetVertexAttribConstant(WebGlAttribLocation location, ReadOnlySpan<float> values, out GpuFailure? failure) => Values(Op.SetVertexAttribConstant, location.Value, MemoryMarshal.AsBytes(values), out failure);
    public bool TrySetVertexAttribIConstant(WebGlAttribLocation location, ReadOnlySpan<int> values, out GpuFailure? failure) => Values(Op.SetVertexAttribIConstant, location.Value, MemoryMarshal.AsBytes(values), out failure);
    public bool TrySetVertexAttribUIConstant(WebGlAttribLocation location, ReadOnlySpan<uint> values, out GpuFailure? failure) => Values(Op.SetVertexAttribUIConstant, location.Value, MemoryMarshal.AsBytes(values), out failure);
    public bool TryGetVertexAttrib(WebGlAttribLocation location, uint parameter, out WebGlParameterValue value, out GpuFailure? failure)
    {
        value = default;
        lock (_session.Gate)
        {
            W(Op.GetVertexAttrib).I32(location.Value).U32(parameter);
            if (!Sync(out var result, out failure)) return false;
            var reader = result.Reader(); value = GpuWebGlWire.Parameter(ref reader); return true;
        }
    }
    public bool TryGetVertexAttribOffset(WebGlAttribLocation location, uint parameter, out long offset, out GpuFailure? failure)
    {
        offset = 0;
        lock (_session.Gate)
        {
            W(Op.GetVertexAttribOffset).I32(location.Value).U32(parameter);
            if (!Sync(out var result, out failure)) return false;
            offset = result.Reader().I64(); return true;
        }
    }

    // ---- uniforms ---------------------------------------------------------------------------
    public bool TryUniformFloat(WebGlUniformLocation location, int components, ReadOnlySpan<float> values, out GpuFailure? failure) => Uniform(Op.UniformFloat, location.Value, components, MemoryMarshal.AsBytes(values), out failure);
    public bool TryUniformInt(WebGlUniformLocation location, int components, ReadOnlySpan<int> values, out GpuFailure? failure) => Uniform(Op.UniformInt, location.Value, components, MemoryMarshal.AsBytes(values), out failure);
    public bool TryUniformUInt(WebGlUniformLocation location, int components, ReadOnlySpan<uint> values, out GpuFailure? failure) => Uniform(Op.UniformUInt, location.Value, components, MemoryMarshal.AsBytes(values), out failure);
    public bool TryUniformMatrix(WebGlUniformLocation location, int columns, int rows, ReadOnlySpan<float> values, bool transpose, out GpuFailure? failure)
    {
        lock (_session.Gate) { W(Op.UniformMatrix).I32(location.Value).I32(columns).I32(rows).Bool(transpose); return Blob(MemoryMarshal.AsBytes(values), out failure) && Defer(out failure); }
    }
    public bool TryGetUniform(WebGlProgramHandle program, WebGlUniformLocation location, out WebGlUniformValue value, out GpuFailure? failure)
    {
        value = default;
        lock (_session.Gate)
        {
            W(Op.GetUniform).U32(program.Value).I32(location.Value);
            if (!Sync(out var result, out failure)) return false;
            var reader = result.Reader(); value = GpuWebGlWire.Uniform(ref reader); return true;
        }
    }

    // ---- fixed-function state ---------------------------------------------------------------
    public bool TrySetViewport(int x, int y, int width, int height, out GpuFailure? failure) { lock (_session.Gate) { W(Op.SetViewport).I32(x).I32(y).I32(width).I32(height); return Defer(out failure); } }
    public bool TrySetScissor(int x, int y, int width, int height, bool enabled, out GpuFailure? failure) { lock (_session.Gate) { W(Op.SetScissor).I32(x).I32(y).I32(width).I32(height).Bool(enabled); return Defer(out failure); } }
    public bool TrySetCapability(uint capability, bool enabled, out GpuFailure? failure) { lock (_session.Gate) { W(Op.SetCapability).U32(capability).Bool(enabled); return Defer(out failure); } }
    public bool TryDrawBuffers(ReadOnlySpan<uint> buffers, out GpuFailure? failure)
    {
        lock (_session.Gate) { W(Op.DrawBuffers); return Blob(MemoryMarshal.AsBytes(buffers), out failure) && Defer(out failure); }
    }
    public bool TryClearBufferfv(uint buffer, int drawBuffer, ReadOnlySpan<float> values, out GpuFailure? failure) => ClearBuffer(Op.ClearBufferfv, buffer, drawBuffer, MemoryMarshal.AsBytes(values), out failure);
    public bool TryClearBufferiv(uint buffer, int drawBuffer, ReadOnlySpan<int> values, out GpuFailure? failure) => ClearBuffer(Op.ClearBufferiv, buffer, drawBuffer, MemoryMarshal.AsBytes(values), out failure);
    public bool TryClearBufferuiv(uint buffer, int drawBuffer, ReadOnlySpan<uint> values, out GpuFailure? failure) => ClearBuffer(Op.ClearBufferuiv, buffer, drawBuffer, MemoryMarshal.AsBytes(values), out failure);
    public bool TryClearBufferfi(uint buffer, int drawBuffer, float depth, int stencil, out GpuFailure? failure) { lock (_session.Gate) { W(Op.ClearBufferfi).U32(buffer).I32(drawBuffer).F32(depth).I32(stencil); return Defer(out failure); } }
    public bool TryReadBuffer(uint buffer, out GpuFailure? failure) { lock (_session.Gate) { W(Op.ReadBuffer).U32(buffer); return Defer(out failure); } }
    public bool TrySetClearColor(float red, float green, float blue, float alpha, out GpuFailure? failure) { lock (_session.Gate) { W(Op.SetClearColor).F32(red).F32(green).F32(blue).F32(alpha); return Defer(out failure); } }
    public bool TryClearMask(uint mask, out GpuFailure? failure) { lock (_session.Gate) { W(Op.ClearMask).U32(mask); return Defer(out failure); } }
    public bool TryPixelStore(uint parameter, int value, out GpuFailure? failure) { lock (_session.Gate) { W(Op.PixelStore).U32(parameter).I32(value); return Defer(out failure); } }
    public bool TrySetActiveTexture(uint texture, out GpuFailure? failure) { lock (_session.Gate) { W(Op.SetActiveTexture).U32(texture); return Defer(out failure); } }
    public bool TrySetDepthFunction(uint function, out GpuFailure? failure) { lock (_session.Gate) { W(Op.SetDepthFunction).U32(function); return Defer(out failure); } }
    public bool TrySetDepthMask(bool enabled, out GpuFailure? failure) { lock (_session.Gate) { W(Op.SetDepthMask).Bool(enabled); return Defer(out failure); } }
    public bool TrySetClearDepth(float value, out GpuFailure? failure) { lock (_session.Gate) { W(Op.SetClearDepth).F32(value); return Defer(out failure); } }
    public bool TrySetStencilFunction(uint face, uint function, int reference, uint mask, out GpuFailure? failure) { lock (_session.Gate) { W(Op.SetStencilFunction).U32(face).U32(function).I32(reference).U32(mask); return Defer(out failure); } }
    public bool TrySetStencilMask(uint face, uint mask, out GpuFailure? failure) { lock (_session.Gate) { W(Op.SetStencilMask).U32(face).U32(mask); return Defer(out failure); } }
    public bool TrySetStencilOperation(uint face, uint fail, uint depthFail, uint depthPass, out GpuFailure? failure) { lock (_session.Gate) { W(Op.SetStencilOperation).U32(face).U32(fail).U32(depthFail).U32(depthPass); return Defer(out failure); } }
    public bool TrySetCullFace(uint face, out GpuFailure? failure) { lock (_session.Gate) { W(Op.SetCullFace).U32(face); return Defer(out failure); } }
    public bool TrySetFrontFace(uint mode, out GpuFailure? failure) { lock (_session.Gate) { W(Op.SetFrontFace).U32(mode); return Defer(out failure); } }
    public bool TrySetBlendColor(float red, float green, float blue, float alpha, out GpuFailure? failure) { lock (_session.Gate) { W(Op.SetBlendColor).F32(red).F32(green).F32(blue).F32(alpha); return Defer(out failure); } }
    public bool TrySetBlendFunction(uint sourceRgb, uint destinationRgb, uint sourceAlpha, uint destinationAlpha, out GpuFailure? failure) { lock (_session.Gate) { W(Op.SetBlendFunction).U32(sourceRgb).U32(destinationRgb).U32(sourceAlpha).U32(destinationAlpha); return Defer(out failure); } }
    public bool TrySetBlendEquation(uint rgb, uint alpha, out GpuFailure? failure) { lock (_session.Gate) { W(Op.SetBlendEquation).U32(rgb).U32(alpha); return Defer(out failure); } }
    public bool TrySetColorMask(bool red, bool green, bool blue, bool alpha, out GpuFailure? failure) { lock (_session.Gate) { W(Op.SetColorMask).Bool(red).Bool(green).Bool(blue).Bool(alpha); return Defer(out failure); } }
    public bool TrySetDepthState(uint function, bool writeMask, float clearValue, out GpuFailure? failure) { lock (_session.Gate) { W(Op.SetDepthState).U32(function).Bool(writeMask).F32(clearValue); return Defer(out failure); } }
    public bool TrySetStencilState(uint face, uint function, int reference, uint compareMask, uint writeMask, uint fail, uint depthFail, uint depthPass, out GpuFailure? failure)
    {
        lock (_session.Gate) { W(Op.SetStencilState).U32(face).U32(function).I32(reference).U32(compareMask).U32(writeMask).U32(fail).U32(depthFail).U32(depthPass); return Defer(out failure); }
    }
    public bool TrySetStencilClear(int value, out GpuFailure? failure) { lock (_session.Gate) { W(Op.SetStencilClear).I32(value); return Defer(out failure); } }
    public bool TrySetCullState(uint face, uint frontFace, out GpuFailure? failure) { lock (_session.Gate) { W(Op.SetCullState).U32(face).U32(frontFace); return Defer(out failure); } }
    public bool TrySetDepthRange(float nearValue, float farValue, out GpuFailure? failure) { lock (_session.Gate) { W(Op.SetDepthRange).F32(nearValue).F32(farValue); return Defer(out failure); } }
    public bool TrySetLineWidth(float width, out GpuFailure? failure) { lock (_session.Gate) { W(Op.SetLineWidth).F32(width); return Defer(out failure); } }
    public bool TrySetPolygonOffset(float factor, float units, out GpuFailure? failure) { lock (_session.Gate) { W(Op.SetPolygonOffset).F32(factor).F32(units); return Defer(out failure); } }
    public bool TrySetSampleCoverage(float value, bool invert, out GpuFailure? failure) { lock (_session.Gate) { W(Op.SetSampleCoverage).F32(value).Bool(invert); return Defer(out failure); } }
    public bool TrySetHint(uint target, uint mode, out GpuFailure? failure) { lock (_session.Gate) { W(Op.SetHint).U32(target).U32(mode); return Defer(out failure); } }
    public bool TryFlush(out GpuFailure? failure) { lock (_session.Gate) { W(Op.Flush); return Defer(out failure) && _session.Flush(out failure); } }
    public bool TryFinish(out GpuFailure? failure) { lock (_session.Gate) { W(Op.Finish); return Sync(out _, out failure); } }
    public bool TryClear(float red, float green, float blue, float alpha, uint mask, out GpuFailure? failure) { lock (_session.Gate) { W(Op.Clear).F32(red).F32(green).F32(blue).F32(alpha).U32(mask); return Defer(out failure); } }
    public bool TryDrawArrays(uint mode, int first, int count, out GpuFailure? failure) { lock (_session.Gate) { W(Op.DrawArrays).U32(mode).I32(first).I32(count); return Defer(out failure); } }
    public bool TryDrawElements(uint mode, int count, uint type, int byteOffset, out GpuFailure? failure) { lock (_session.Gate) { W(Op.DrawElements).U32(mode).I32(count).U32(type).I32(byteOffset); return Defer(out failure); } }
    public bool TryDrawArraysInstanced(uint mode, int first, int count, int instanceCount, out GpuFailure? failure) { lock (_session.Gate) { W(Op.DrawArraysInstanced).U32(mode).I32(first).I32(count).I32(instanceCount); return Defer(out failure); } }
    public bool TryDrawElementsInstanced(uint mode, int count, uint type, int byteOffset, int instanceCount, out GpuFailure? failure) { lock (_session.Gate) { W(Op.DrawElementsInstanced).U32(mode).I32(count).U32(type).I32(byteOffset).I32(instanceCount); return Defer(out failure); } }
    public bool TryReadPixels(int x, int y, int width, int height, Span<byte> destination, uint format, uint type, out GpuFailure? failure)
    {
        lock (_session.Gate)
        {
            if (!CheckReadback(destination.Length, out failure)) return false;
            W(Op.ReadPixels).I32(x).I32(y).I32(width).I32(height).I32(destination.Length).U32(format).U32(type);
            return Readback(destination, out failure);
        }
    }
    public bool TryReadPixelsPresentation(int x, int y, int width, int height, Span<byte> destination, out GpuFailure? failure)
    {
        lock (_session.Gate)
        {
            if (!CheckReadback(destination.Length, out failure)) return false;
            W(Op.ReadPixelsPresentation).I32(x).I32(y).I32(width).I32(height).I32(destination.Length);
            return Readback(destination, out failure);
        }
    }
    public bool TryGetParameter(uint parameter, out WebGlParameterValue value, out GpuFailure? failure)
    {
        value = default;
        lock (_session.Gate)
        {
            W(Op.GetParameter).U32(parameter);
            if (!Sync(out var result, out failure)) return false;
            var reader = result.Reader(); value = GpuWebGlWire.Parameter(ref reader); return true;
        }
    }
    public bool TryGetError(out uint error, out GpuFailure? failure)
    {
        error = 0;
        lock (_session.Gate)
        {
            W(Op.GetError);
            if (!Sync(out var result, out failure)) return false;
            error = result.Reader().U32(); return true;
        }
    }
    public bool TryGetExtensions(out IReadOnlyList<string> extensions, out GpuFailure? failure)
    {
        extensions = [];
        lock (_session.Gate)
        {
            W(Op.GetExtensions);
            if (!Sync(out var result, out failure)) return false;
            var reader = result.Reader(); extensions = reader.Strings(); return true;
        }
    }
    public bool TryResize(int width, int height, out GpuFailure? failure)
    {
        lock (_session.Gate) { W(Op.Resize).I32(width).I32(height); return Sync(out _, out failure); }
    }

    public void Dispose()
    {
        lock (_session.Gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (!_session.IsLost) { W(Op.DestroyContext); Defer(out _); }
            _session.Forget(_id);
        }
    }

    // ---- shared synchronous shapes ----------------------------------------------------------
    private bool SyncString(Op op, uint handle, out string value, out GpuFailure? failure)
    {
        value = string.Empty;
        lock (_session.Gate)
        {
            W(op).U32(handle);
            if (!Sync(out var result, out failure)) return false;
            value = result.Reader().String(); return true;
        }
    }
    private bool SyncFlagAndLog(Op op, uint handle, out bool flag, out string log, out GpuFailure? failure)
    {
        flag = false; log = string.Empty;
        lock (_session.Gate)
        {
            W(op).U32(handle);
            if (!Sync(out var result, out failure)) return false;
            var reader = result.Reader(); flag = reader.Bool(); log = reader.String(); return true;
        }
    }
    private bool SyncActiveInfo(Op op, uint program, uint index, out WebGlActiveInfo info, out GpuFailure? failure)
    {
        info = new(string.Empty, 0, 0);
        lock (_session.Gate)
        {
            W(op).U32(program).U32(index);
            if (!Sync(out var result, out failure)) return false;
            var reader = result.Reader(); info = GpuWebGlWire.ActiveInfo(ref reader); return true;
        }
    }
    private bool SyncNamedInt(Op op, uint program, string name, out int value, out GpuFailure? failure)
    {
        value = -1;
        lock (_session.Gate)
        {
            W(op).U32(program);
            if (!Blob(Utf8(name), out failure)) return false;
            if (!Sync(out var result, out failure)) return false;
            value = result.Reader().I32(); return true;
        }
    }
    private bool SyncParameter(Op op, uint target, uint parameter, out WebGlParameterValue value, out GpuFailure? failure)
    {
        value = default;
        lock (_session.Gate)
        {
            W(op).U32(target).U32(parameter);
            if (!Sync(out var result, out failure)) return false;
            var reader = result.Reader(); value = GpuWebGlWire.Parameter(ref reader); return true;
        }
    }
    private bool Values(Op op, int location, ReadOnlySpan<byte> values, out GpuFailure? failure)
    {
        lock (_session.Gate) { W(op).I32(location); return Blob(values, out failure) && Defer(out failure); }
    }
    private bool Uniform(Op op, int location, int components, ReadOnlySpan<byte> values, out GpuFailure? failure)
    {
        lock (_session.Gate) { W(op).I32(location).I32(components); return Blob(values, out failure) && Defer(out failure); }
    }
    private bool ClearBuffer(Op op, uint buffer, int drawBuffer, ReadOnlySpan<byte> values, out GpuFailure? failure)
    {
        lock (_session.Gate) { W(op).U32(buffer).I32(drawBuffer); return Blob(values, out failure) && Defer(out failure); }
    }

    /// <summary>The outputs of one synchronous call, decoded on demand.</summary>
    private readonly struct GpuPayloadResult(byte[] bytes)
    {
        internal GpuPayloadReader Reader() => new(bytes);
    }
}
