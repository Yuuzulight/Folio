namespace Folio.Scripting.Gpu;

/// <summary>The native shader language/context profile selected for one canvas.</summary>
internal enum WebGlContextVersion
{
    WebGl1,
    WebGl2,
}

/// <summary>Requested and actual drawing-buffer attributes for one native WebGL context.</summary>
internal sealed record WebGlContextAttributes(
    WebGlContextVersion Version,
    bool Alpha,
    bool Depth,
    bool Stencil,
    bool Antialias,
    bool PremultipliedAlpha,
    bool PreserveDrawingBuffer);

internal sealed record WebGlContextOptions(
    WebGlContextVersion Version,
    int Width,
    int Height,
    WebGlContextAttributes RequestedAttributes,
    long MaxBytes,
    int MaxShaderSourceBytes = 1_000_000);

internal sealed record WebGlActiveInfo(string Name, int Size, uint Type);

internal enum WebGlParameterKind
{
    Int32,
    UInt32,
    Int64,
    Float32,
    Boolean,
    BooleanArray,
    String,
    Int32Array,
    UInt32Array,
    Float32Array,
}

internal readonly record struct WebGlParameterValue(
    WebGlParameterKind Kind,
    int Int32 = 0,
    uint UInt32 = 0,
    long Int64 = 0,
    float Float32 = 0,
    bool Boolean = false,
    string? String = null,
    IReadOnlyList<int>? Int32Array = null,
    IReadOnlyList<uint>? UInt32Array = null,
    IReadOnlyList<float>? Float32Array = null);

internal readonly record struct WebGlUniformValue(
    WebGlParameterKind Kind,
    IReadOnlyList<int>? Int32 = null,
    IReadOnlyList<uint>? UInt32 = null,
    IReadOnlyList<float>? Float32 = null);

/// <summary>Typed native handles. These are internal GPU handles, never script-visible WebGL objects.</summary>
internal readonly record struct WebGlShaderHandle(uint Value);
internal readonly record struct WebGlProgramHandle(uint Value);
internal readonly record struct WebGlBufferHandle(uint Value);
internal readonly record struct WebGlTextureHandle(uint Value);
internal readonly record struct WebGlFramebufferHandle(uint Value);
internal readonly record struct WebGlRenderbufferHandle(uint Value);
internal readonly record struct WebGlVertexArrayHandle(uint Value);
internal readonly record struct WebGlTransformFeedbackHandle(uint Value);
internal readonly record struct WebGlUniformLocation(int Value);
internal readonly record struct WebGlAttribLocation(int Value);

internal sealed record WebGlShaderResult(WebGlShaderHandle Handle, bool Compiled, string InfoLog);
internal sealed record WebGlProgramResult(WebGlProgramHandle Handle, bool Linked, string InfoLog);

/// <summary>
/// Engine-independent native WebGL execution boundary. WebIDL conversion, opaque script
/// wrappers, object ownership and WebGL error semantics remain in the scripting layer.
/// Implementations must serialize calls on the owning EGL context and release every native
/// object on failed creation, context loss and disposal.
/// </summary>
internal interface INativeWebGlContext : IDisposable
{
    WebGlContextAttributes Attributes { get; }
    int DrawingBufferWidth { get; }
    int DrawingBufferHeight { get; }
    bool IsLost { get; }
    long ReservedBytes { get; }
    long PeakReservedBytes { get; }

    bool TryCreateShader(uint shaderType, out WebGlShaderHandle shader, out GpuFailure? failure);
    bool TrySetShaderSource(WebGlShaderHandle shader, ReadOnlySpan<byte> utf8Source, out GpuFailure? failure);
    bool TryCompileShader(WebGlShaderHandle shader, out bool compiled, out string infoLog, out GpuFailure? failure);
    bool TryGetShaderParameter(WebGlShaderHandle shader, uint parameter, out WebGlParameterValue value, out GpuFailure? failure);
    bool TryGetShaderSource(WebGlShaderHandle shader, out string source, out GpuFailure? failure);
    bool TryGetShaderInfoLog(WebGlShaderHandle shader, out string infoLog, out GpuFailure? failure);
    bool TryGetShaderPrecisionFormat(uint shaderType, uint precisionType, out int rangeMin, out int rangeMax, out int precision, out GpuFailure? failure);
    bool TryDeleteShader(WebGlShaderHandle shader, out GpuFailure? failure);
    bool TryCreateProgram(out WebGlProgramHandle program, out GpuFailure? failure);
    bool TryDeleteProgram(WebGlProgramHandle program, out GpuFailure? failure);
    bool TryAttachShader(WebGlProgramHandle program, WebGlShaderHandle shader, out GpuFailure? failure);
    bool TryDetachShader(WebGlProgramHandle program, WebGlShaderHandle shader, out GpuFailure? failure);
    bool TryLinkProgram(WebGlProgramHandle program, out bool linked, out string infoLog, out GpuFailure? failure);
    bool TryValidateProgram(WebGlProgramHandle program, out bool valid, out string infoLog, out GpuFailure? failure);
    bool TryGetProgramParameter(WebGlProgramHandle program, uint parameter, out WebGlParameterValue value, out GpuFailure? failure);
    bool TryGetProgramInfoLog(WebGlProgramHandle program, out string infoLog, out GpuFailure? failure);
    bool TryGetAttachedShaders(WebGlProgramHandle program, Span<WebGlShaderHandle> shaders, out int count, out GpuFailure? failure);
    bool TryGetActiveAttrib(WebGlProgramHandle program, uint index, out WebGlActiveInfo info, out GpuFailure? failure);
    bool TryGetActiveUniform(WebGlProgramHandle program, uint index, out WebGlActiveInfo info, out GpuFailure? failure);
    bool TryBindAttribLocation(WebGlProgramHandle program, uint index, ReadOnlySpan<byte> utf8Name, out GpuFailure? failure);
    bool TryUseProgram(WebGlProgramHandle? program, out GpuFailure? failure);
    bool TryGetAttribLocation(WebGlProgramHandle program, string name, out WebGlAttribLocation location, out GpuFailure? failure);
    bool TryGetUniformLocation(WebGlProgramHandle program, string name, out WebGlUniformLocation location, out GpuFailure? failure);
    bool TryGetUniformIndices(WebGlProgramHandle program, IReadOnlyList<string> names, Span<uint> indices, out GpuFailure? failure);
    bool TryGetActiveUniforms(WebGlProgramHandle program, ReadOnlySpan<uint> indices, uint parameter, Span<int> values, out GpuFailure? failure);
    bool TryGetUniformBlockIndex(WebGlProgramHandle program, ReadOnlySpan<byte> name, out uint index, out GpuFailure? failure);
    bool TryUniformBlockBinding(WebGlProgramHandle program, uint index, uint binding, out GpuFailure? failure);

    bool TryCreateBuffer(out WebGlBufferHandle buffer, out GpuFailure? failure);
    bool TryDeleteBuffer(WebGlBufferHandle buffer, out GpuFailure? failure);
    bool TryBindBuffer(uint target, WebGlBufferHandle? buffer, out GpuFailure? failure);
    bool TryAllocateBufferStorage(uint target, long size, uint usage, out GpuFailure? failure);
    bool TryUploadBuffer(uint target, ReadOnlySpan<byte> data, uint usage, out GpuFailure? failure);
    bool TryUpdateBuffer(uint target, int byteOffset, ReadOnlySpan<byte> data, out GpuFailure? failure);
    bool TryCopyBuffer(uint readTarget, uint writeTarget, long readOffset, long writeOffset, long size, out GpuFailure? failure);
    bool TryBindBufferBase(uint target, uint index, WebGlBufferHandle? buffer, out GpuFailure? failure);
    bool TryBindBufferRange(uint target, uint index, WebGlBufferHandle? buffer, long offset, long size, out GpuFailure? failure);
    bool TryGetBufferParameter(uint target, uint parameter, out WebGlParameterValue value, out GpuFailure? failure);
    bool TryReadBuffer(uint target, long offset, Span<byte> destination, out GpuFailure? failure);

    bool TryCreateTexture(out WebGlTextureHandle texture, out GpuFailure? failure);
    bool TryDeleteTexture(WebGlTextureHandle texture, out GpuFailure? failure);
    bool TryBindTexture(uint target, WebGlTextureHandle? texture, out GpuFailure? failure);
    bool TryTextureParameter(uint target, uint parameter, int value, out GpuFailure? failure);
    bool TryGetTextureParameter(uint target, uint parameter, out WebGlParameterValue value, out GpuFailure? failure);
    bool TryGetTextureLevelParameter(uint target, int level, uint parameter, out WebGlParameterValue value, out GpuFailure? failure);
    bool TryTextureImage2D(uint target, int level, int internalFormat, int width, int height, uint format, uint type,
                           ReadOnlySpan<byte> pixels, out GpuFailure? failure);
    bool TryTextureStorage2D(uint target, int levels, uint internalFormat, int width, int height, out GpuFailure? failure);
    bool TryTextureSubImage2D(uint target, int level, int xOffset, int yOffset, int width, int height, uint format, uint type,
                              ReadOnlySpan<byte> pixels, out GpuFailure? failure);
    bool TryCopyTextureImage2D(uint target, int level, int internalFormat, int x, int y, int width, int height, int border, out GpuFailure? failure);
    bool TryCopyTextureSubImage2D(uint target, int level, int xOffset, int yOffset, int x, int y, int width, int height, out GpuFailure? failure);
    bool TryTextureImage3D(uint target, int level, int internalFormat, int width, int height, int depth, uint format, uint type,
                           ReadOnlySpan<byte> pixels, out GpuFailure? failure);
    bool TryTextureStorage3D(uint target, int levels, uint internalFormat, int width, int height, int depth, out GpuFailure? failure);
    bool TryTextureSubImage3D(uint target, int level, int xOffset, int yOffset, int zOffset, int width, int height, int depth, uint format, uint type,
                              ReadOnlySpan<byte> pixels, out GpuFailure? failure);
    bool TryGenerateMipmap(uint target, out GpuFailure? failure);

    bool TryCreateFramebuffer(out WebGlFramebufferHandle framebuffer, out GpuFailure? failure);
    bool TryDeleteFramebuffer(WebGlFramebufferHandle framebuffer, out GpuFailure? failure);
    bool TryBindFramebuffer(uint target, WebGlFramebufferHandle? framebuffer, out GpuFailure? failure);
    bool TryFramebufferTexture2D(uint target, uint attachment, uint textureTarget, WebGlTextureHandle? texture, int level, out GpuFailure? failure);
    bool TryFramebufferTextureLayer(uint target, uint attachment, WebGlTextureHandle? texture, int level, int layer, out GpuFailure? failure);
    bool TryCheckFramebufferStatus(uint target, out uint status, out GpuFailure? failure);
    bool TryBlitFramebuffer(int srcX0, int srcY0, int srcX1, int srcY1, int dstX0, int dstY0, int dstX1, int dstY1, uint mask, uint filter, out GpuFailure? failure);

    bool TryCreateRenderbuffer(out WebGlRenderbufferHandle renderbuffer, out GpuFailure? failure);
    bool TryDeleteRenderbuffer(WebGlRenderbufferHandle renderbuffer, out GpuFailure? failure);
    bool TryBindRenderbuffer(uint target, WebGlRenderbufferHandle? renderbuffer, out GpuFailure? failure);
    bool TryRenderbufferStorage(uint target, uint internalFormat, int width, int height, out GpuFailure? failure);
    bool TryRenderbufferStorageMultisample(uint target, int samples, uint internalFormat, int width, int height, out GpuFailure? failure);
    bool TryFramebufferRenderbuffer(uint target, uint attachment, uint renderbufferTarget, WebGlRenderbufferHandle? renderbuffer, out GpuFailure? failure);

    bool TryCreateVertexArray(out WebGlVertexArrayHandle vertexArray, out GpuFailure? failure);
    bool TryDeleteVertexArray(WebGlVertexArrayHandle vertexArray, out GpuFailure? failure);
    bool TryBindVertexArray(WebGlVertexArrayHandle? vertexArray, out GpuFailure? failure);
    bool TryCreateTransformFeedback(out WebGlTransformFeedbackHandle feedback, out GpuFailure? failure);
    bool TryDeleteTransformFeedback(WebGlTransformFeedbackHandle feedback, out GpuFailure? failure);
    bool TryBindTransformFeedback(uint target, WebGlTransformFeedbackHandle? feedback, out GpuFailure? failure);
    bool TryBeginTransformFeedback(uint primitiveMode, out GpuFailure? failure);
    bool TryEndTransformFeedback(out GpuFailure? failure);
    bool TryPauseTransformFeedback(out GpuFailure? failure);
    bool TryResumeTransformFeedback(out GpuFailure? failure);
    bool TryTransformFeedbackVaryings(WebGlProgramHandle program, IReadOnlyList<string> varyings, uint bufferMode, out GpuFailure? failure);
    bool TryGetTransformFeedbackVarying(WebGlProgramHandle program, uint index, out WebGlActiveInfo info, out GpuFailure? failure);
    bool TryEnableVertexAttrib(WebGlAttribLocation location, out GpuFailure? failure);
    bool TryDisableVertexAttrib(WebGlAttribLocation location, out GpuFailure? failure);
    bool TryVertexAttribPointer(WebGlAttribLocation location, int size, uint type, bool normalized, int stride, int offset, out GpuFailure? failure);
    bool TryVertexAttribIPointer(WebGlAttribLocation location, int size, uint type, int stride, int offset, out GpuFailure? failure);
    bool TryVertexAttribDivisor(WebGlAttribLocation location, uint divisor, out GpuFailure? failure);
    bool TrySetVertexAttribConstant(WebGlAttribLocation location, ReadOnlySpan<float> values, out GpuFailure? failure);
    bool TrySetVertexAttribIConstant(WebGlAttribLocation location, ReadOnlySpan<int> values, out GpuFailure? failure);
    bool TrySetVertexAttribUIConstant(WebGlAttribLocation location, ReadOnlySpan<uint> values, out GpuFailure? failure);
    bool TryGetVertexAttrib(WebGlAttribLocation location, uint parameter, out WebGlParameterValue value, out GpuFailure? failure);
    bool TryGetVertexAttribOffset(WebGlAttribLocation location, uint parameter, out long offset, out GpuFailure? failure);

    bool TryUniformFloat(WebGlUniformLocation location, int components, ReadOnlySpan<float> values, out GpuFailure? failure);
    bool TryUniformInt(WebGlUniformLocation location, int components, ReadOnlySpan<int> values, out GpuFailure? failure);
    bool TryUniformUInt(WebGlUniformLocation location, int components, ReadOnlySpan<uint> values, out GpuFailure? failure);
    bool TryUniformMatrix(WebGlUniformLocation location, int columns, int rows, ReadOnlySpan<float> values, bool transpose, out GpuFailure? failure);
    bool TryGetUniform(WebGlProgramHandle program, WebGlUniformLocation location, out WebGlUniformValue value, out GpuFailure? failure);

    bool TrySetViewport(int x, int y, int width, int height, out GpuFailure? failure);
    bool TrySetScissor(int x, int y, int width, int height, bool enabled, out GpuFailure? failure);
    bool TrySetCapability(uint capability, bool enabled, out GpuFailure? failure);
    bool TryDrawBuffers(ReadOnlySpan<uint> buffers, out GpuFailure? failure);
    bool TryClearBufferfv(uint buffer, int drawBuffer, ReadOnlySpan<float> values, out GpuFailure? failure);
    bool TryClearBufferiv(uint buffer, int drawBuffer, ReadOnlySpan<int> values, out GpuFailure? failure);
    bool TryClearBufferuiv(uint buffer, int drawBuffer, ReadOnlySpan<uint> values, out GpuFailure? failure);
    bool TryClearBufferfi(uint buffer, int drawBuffer, float depth, int stencil, out GpuFailure? failure);
    bool TryReadBuffer(uint buffer, out GpuFailure? failure);
    bool TrySetClearColor(float red, float green, float blue, float alpha, out GpuFailure? failure);
    bool TryClearMask(uint mask, out GpuFailure? failure);
    bool TryPixelStore(uint parameter, int value, out GpuFailure? failure);
    bool TrySetActiveTexture(uint texture, out GpuFailure? failure);
    bool TrySetDepthFunction(uint function, out GpuFailure? failure);
    bool TrySetDepthMask(bool enabled, out GpuFailure? failure);
    bool TrySetClearDepth(float value, out GpuFailure? failure);
    bool TrySetStencilFunction(uint face, uint function, int reference, uint mask, out GpuFailure? failure);
    bool TrySetStencilMask(uint face, uint mask, out GpuFailure? failure);
    bool TrySetStencilOperation(uint face, uint fail, uint depthFail, uint depthPass, out GpuFailure? failure);
    bool TrySetCullFace(uint face, out GpuFailure? failure);
    bool TrySetFrontFace(uint mode, out GpuFailure? failure);
    bool TrySetBlendColor(float red, float green, float blue, float alpha, out GpuFailure? failure);
    bool TrySetBlendFunction(uint sourceRgb, uint destinationRgb, uint sourceAlpha, uint destinationAlpha, out GpuFailure? failure);
    bool TrySetBlendEquation(uint rgb, uint alpha, out GpuFailure? failure);
    bool TrySetColorMask(bool red, bool green, bool blue, bool alpha, out GpuFailure? failure);
    bool TrySetDepthState(uint function, bool writeMask, float clearValue, out GpuFailure? failure);
    bool TrySetStencilState(uint face, uint function, int reference, uint compareMask, uint writeMask, uint fail, uint depthFail, uint depthPass, out GpuFailure? failure);
    bool TrySetStencilClear(int value, out GpuFailure? failure);
    bool TrySetCullState(uint face, uint frontFace, out GpuFailure? failure);
    bool TrySetDepthRange(float nearValue, float farValue, out GpuFailure? failure);
    bool TrySetLineWidth(float width, out GpuFailure? failure);
    bool TrySetPolygonOffset(float factor, float units, out GpuFailure? failure);
    bool TrySetSampleCoverage(float value, bool invert, out GpuFailure? failure);
    bool TrySetHint(uint target, uint mode, out GpuFailure? failure);
    bool TryFlush(out GpuFailure? failure);
    bool TryFinish(out GpuFailure? failure);
    bool TryClear(float red, float green, float blue, float alpha, uint mask, out GpuFailure? failure);
    bool TryDrawArrays(uint mode, int first, int count, out GpuFailure? failure);
    bool TryDrawElements(uint mode, int count, uint type, int byteOffset, out GpuFailure? failure);
    bool TryDrawArraysInstanced(uint mode, int first, int count, int instanceCount, out GpuFailure? failure);
    bool TryDrawElementsInstanced(uint mode, int count, uint type, int byteOffset, int instanceCount, out GpuFailure? failure);
    bool TryReadPixels(int x, int y, int width, int height, Span<byte> destination, uint format, uint type, out GpuFailure? failure);
    bool TryReadPixelsPresentation(int x, int y, int width, int height, Span<byte> destination, out GpuFailure? failure);
    bool TryGetParameter(uint parameter, out WebGlParameterValue value, out GpuFailure? failure);
    bool TryGetError(out uint error, out GpuFailure? failure);
    bool TryGetExtensions(out IReadOnlyList<string> extensions, out GpuFailure? failure);
    bool TryResize(int width, int height, out GpuFailure? failure);
}

internal interface INativeWebGlFactory
{
    long ReservedBytes { get; }
    long PeakReservedBytes { get; }
    bool TryCreate(WebGlContextOptions options, out INativeWebGlContext? context, out GpuFailure? failure);
}
