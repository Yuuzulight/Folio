using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Folio.Scripting.WebGl;

namespace Folio.Scripting.Dom;

// Everything WebGL draws and links: shaders and programs, uniforms and attributes, vertex arrays, texture units, the drawing buffer and
// the pipeline that fills it.
internal sealed partial class WebGlContextState
{
    public HashSet<uint> EnabledCapabilities { get; } = [];

    // ---- shaders and programs ----

    public Dictionary<uint, GlslShader> CompiledShaders { get; } = [];
    public Dictionary<uint, GlLinkedProgram> LinkedProgramObjects { get; } = [];
    public Dictionary<uint, Dictionary<string, int>> BoundAttributes { get; } = [];

    public GlLinkedProgram? CurrentProgramObject => CurrentProgram is { } p && LinkedProgramObjects.TryGetValue(p, out var linked) ? linked : null;

    public void CompileShader(uint handle)
    {
        if (!Shaders.TryGetValue(handle, out var shader)) return;
        CompiledShaders.Remove(handle);
        var validation = WebGlShaderValidator.Validate(shader.Type, shader.Source);
        string log;
        var line = validation.LineNumber;
        GlslShader? compiled = null;
        if (!validation.IsValid)
        {
            log = validation.InfoLog;
        }
        else
        {
            var result = GlslCompiler.Compile(shader.Type == 0x8B31 ? GlslStage.Vertex : GlslStage.Fragment, shader.Source);
            compiled = result.Shader;
            log = result.InfoLog;
            line = result.ErrorLine;
        }
        Shaders[handle] = shader with { Compiled = compiled is not null, InfoLog = log };
        if (compiled is not null) CompiledShaders[handle] = compiled;
        else
        {
            _canvas.Element.OwnerDocument.DiagnosticSink?.Invoke(new Diagnostic(
                DiagnosticCode.ScriptError,
                Severity.Warning,
                $"WebGL shader compilation failed: {log}",
                line > 0 ? new SourceLocation(line, 1) : null,
                "webgl"));
        }
    }

    public void LinkProgram(uint program)
    {
        Programs.Add(program);
        LinkedPrograms.Remove(program);
        var hasShaders = ProgramShaders.TryGetValue(program, out var attached) && attached.Count > 0;
        var allCompiled = hasShaders && attached!.All(shader => Shaders.TryGetValue(shader, out var val) && val.Compiled);
        var vertex = hasShaders ? attached!.Where(shader => Shaders.TryGetValue(shader, out var val) && val.Type == 0x8B31 && val.Compiled).Select(s => CompiledShaders.GetValueOrDefault(s)).FirstOrDefault() : null;
        var fragment = hasShaders ? attached!.Where(shader => Shaders.TryGetValue(shader, out var val) && val.Type == 0x8B30 && val.Compiled).Select(s => CompiledShaders.GetValueOrDefault(s)).FirstOrDefault() : null;
        var hasVertex = hasShaders && attached!.Any(shader => Shaders.TryGetValue(shader, out var val) && val.Type == 0x8B31);
        var hasFragment = hasShaders && attached!.Any(shader => Shaders.TryGetValue(shader, out var val) && val.Type == 0x8B30);
        string? failure = null;
        GlLinkedProgram? linked = null;
        if (!hasShaders) failure = "No shaders attached to program.";
        else if (!hasVertex) failure = "Program is missing a vertex shader.";
        else if (!hasFragment) failure = "Program is missing a fragment shader.";
        else if (!allCompiled || vertex is null || fragment is null) failure = "One or more attached shaders failed compilation.";
        else
        {
            var (program2, log) = GlLinkedProgram.Link(vertex, fragment, BoundAttributes.GetValueOrDefault(program) ?? []);
            if (program2 is null) failure = log;
            else linked = program2;
        }
        if (linked is not null)
        {
            LinkedProgramObjects[program] = linked;
            LinkedPrograms.Add(program);
            ProgramInfoLogs.Remove(program);
            return;
        }
        LinkedProgramObjects.Remove(program);
        var message = failure!.StartsWith("ERROR:", StringComparison.Ordinal) ? failure : $"ERROR: WebGL program link failed: {failure}";
        ProgramInfoLogs[program] = message;
        _canvas.Element.OwnerDocument.DiagnosticSink?.Invoke(new Diagnostic(DiagnosticCode.ScriptError, Severity.Warning, message, null, "webgl"));
    }

    // ---- uniforms ----

    /// <summary>Sets <paramref name="elements"/> consecutive elements of the uniform at <paramref name="location"/> from the components in <paramref name="data"/>.</summary>
    public void SetUniform(int location, int components, bool integer, ReadOnlySpan<float> data, int elements)
    {
        if (CurrentProgramObject is not { } program) { SetError(0x0502); return; }
        if (!program.TryGetUniform(location, out var uniform, out var element)) { SetError(0x0502); return; }
        var type = uniform.Type;
        if (type.IsMatrix || type.Components != components) { SetError(0x0502); return; }
        var kind = type.ScalarKind;
        var ok = type.IsSampler ? integer : kind switch
        {
            GlslBase.Float => !integer,
            GlslBase.Int => integer,
            _ => true,
        };
        if (!ok) { SetError(0x0502); return; }
        if (elements > 1 && uniform.Size == 1) { SetError(0x0502); return; }
        for (var i = 0; i < elements && element + i < uniform.Size; i++)
        {
            var v = new Val();
            for (var c = 0; c < components; c++)
            {
                var x = data[i * components + c];
                v.Set(c, kind switch { GlslBase.Int => MathF.Truncate(x), GlslBase.Bool => x != 0 ? 1 : 0, _ => x });
            }
            if (type.IsSampler) v = new Val(MathF.Truncate(data[i]));
            program.WriteUniform(uniform, element + i, v);
        }
    }

    public void SetUniformMatrix(int location, int size, ReadOnlySpan<float> data, int elements, bool transpose)
    {
        if (CurrentProgramObject is not { } program) { SetError(0x0502); return; }
        if (!program.TryGetUniform(location, out var uniform, out var element)) { SetError(0x0502); return; }
        if (!uniform.Type.IsMatrix || uniform.Type.Size != size) { SetError(0x0502); return; }
        if (elements > 1 && uniform.Size == 1) { SetError(0x0502); return; }
        for (var i = 0; i < elements && element + i < uniform.Size; i++)
        {
            var v = new Val();
            for (var col = 0; col < size; col++)
                for (var row = 0; row < size; row++)
                {
                    var index = i * size * size + (transpose ? row * size + col : col * size + row);
                    v.SetCell(col, row, data[index]);
                }
            program.WriteUniform(uniform, element + i, v);
        }
    }

    /// <summary>The components of a uniform element as the API returns them, matrices column by column.</summary>
    public float[] ReadUniform(GlLinkedProgram program, GlUniform uniform, int element)
    {
        var value = program.ReadUniform(uniform, element);
        var type = uniform.Type;
        var result = new float[type.Components];
        if (type.IsMatrix)
        {
            var n = type.Size;
            for (var col = 0; col < n; col++) for (var row = 0; row < n; row++) result[col * n + row] = value.Cell(col, row);
        }
        else
        {
            for (var i = 0; i < result.Length; i++) result[i] = value.Get(i);
        }
        return result;
    }

    // ---- vertex arrays ----

    public sealed class VertexArrayState
    {
        public HashSet<int> Enabled { get; } = [];
        public Dictionary<int, (int Size, uint Type, bool Normalized, int Stride, int Offset)> Pointers { get; } = [];
        public Dictionary<int, uint> Buffers { get; } = [];
        public Dictionary<int, uint> Divisors { get; } = [];
        public uint? ElementBuffer { get; set; }
    }

    private readonly VertexArrayState _defaultVertexArray = new();
    private readonly Dictionary<uint, VertexArrayState> _vertexArrays = [];

    public VertexArrayState CurrentVertexArray
    {
        get
        {
            if (BoundVertexArray is not { } handle) return _defaultVertexArray;
            if (_vertexArrays.TryGetValue(handle, out var state)) return state;
            if (!VertexArrays.Contains(handle)) return _defaultVertexArray;
            return _vertexArrays[handle] = new VertexArrayState();
        }
    }

    public HashSet<int> EnabledVertexAttributes => CurrentVertexArray.Enabled;
    public Dictionary<int, (int Size, uint Type, bool Normalized, int Stride, int Offset)> VertexAttribPointers => CurrentVertexArray.Pointers;
    public Dictionary<int, uint> VertexAttribBuffers => CurrentVertexArray.Buffers;
    public Dictionary<int, uint> VertexAttribDivisors => CurrentVertexArray.Divisors;
    public uint? BoundElementArrayBuffer { get => CurrentVertexArray.ElementBuffer; set => CurrentVertexArray.ElementBuffer = value; }
    public Vector4[] GenericAttributes { get; } = Enumerable.Repeat(new Vector4(0, 0, 0, 1), 16).ToArray();

    public void ForgetVertexArray(uint handle) => _vertexArrays.Remove(handle);

    // ---- texture units ----

    public uint?[] TextureUnits { get; } = new uint?[32];
    internal int UnitIndex => (int)Math.Clamp(ActiveTextureUnit - 0x84C0u, 0u, 31u);
    public uint? BoundTexture { get => TextureUnits[UnitIndex]; set => TextureUnits[UnitIndex] = value; }

    // ---- state the fixed-function stages read ----

    public Vector4 BlendColor { get; set; }
    public uint CullFaceMode { get; set; } = 0x0405;
    public uint FrontFaceMode { get; set; } = 0x0901;
    public float DepthRangeNear { get; set; }
    public float DepthRangeFar { get; set; } = 1;
    public uint StencilWriteMask { get; set; } = 0xFFFFFFFF;
    public float LineWidth { get; set; } = 1;
    public float PolygonOffsetFactor { get; set; }
    public float PolygonOffsetUnits { get; set; }

    public void InitializeViewport()
    {
        ViewportWidth = ScissorWidth = _canvas.Width;
        ViewportHeight = ScissorHeight = _canvas.Height;
    }

    private GlFixedState CaptureFixedState() => new()
    {
        DepthTest = EnabledCapabilities.Contains(0x0B71),
        Blend = EnabledCapabilities.Contains(0x0BE2),
        CullFace = EnabledCapabilities.Contains(0x0B44),
        StencilTest = EnabledCapabilities.Contains(0x0B90),
        ScissorTest = EnabledCapabilities.Contains(0x0C11),
        DepthFunc = DepthFunction,
        DepthMask = DepthMask,
        DepthNear = DepthRangeNear,
        DepthFar = DepthRangeFar,
        BlendSrcRgb = BlendSourceFactor,
        BlendDstRgb = BlendDestinationFactor,
        BlendSrcAlpha = BlendSourceAlphaFactor,
        BlendDstAlpha = BlendDestinationAlphaFactor,
        BlendEquationRgb = BlendEquation,
        BlendEquationAlpha = BlendAlphaEquation,
        BlendColor = BlendColor,
        MaskR = ColorMaskRed,
        MaskG = ColorMaskGreen,
        MaskB = ColorMaskBlue,
        MaskA = ColorMaskAlpha,
        CullMode = CullFaceMode,
        FrontFace = FrontFaceMode,
        ViewportX = ViewportX,
        ViewportY = ViewportY,
        ViewportWidth = ViewportWidth,
        ViewportHeight = ViewportHeight,
        ScissorX = ScissorX,
        ScissorY = ScissorY,
        ScissorWidth = ScissorWidth,
        ScissorHeight = ScissorHeight,
        StencilFunc = StencilFunction,
        StencilRef = StencilReference,
        StencilReadMask = StencilMask,
        StencilWriteMask = StencilWriteMask,
        StencilFail = StencilFail,
        StencilDepthFail = StencilDepthFail,
        StencilDepthPass = StencilDepthPass,
        PremultipliedAlpha = PremultipliedAlpha,
        PolygonOffsetFill = EnabledCapabilities.Contains(0x8037),
        PolygonOffsetFactor = PolygonOffsetFactor,
        PolygonOffsetUnits = PolygonOffsetUnits,
    };

    // ---- the drawing buffer ----

    private GlDefaultFramebuffer? _drawingBuffer;
    private bool _bufferDirty;
    private bool _bufferPresented;
    private bool _flushing;

    private const long MaxDrawingBufferPixels = 4096L * 4096;

    /// <summary>The canvas's drawing buffer at the canvas's current size; null when the canvas has no pixels or too many.</summary>
    public GlDefaultFramebuffer? DrawingBuffer
    {
        get
        {
            var width = _canvas.Width;
            var height = _canvas.Height;
            if (width <= 0 || height <= 0 || (long)width * height > MaxDrawingBufferPixels) return null;
            if (_drawingBuffer is null || _drawingBuffer.Width != width || _drawingBuffer.Height != height)
            {
                _drawingBuffer = new GlDefaultFramebuffer(width, height, Antialias && (long)width * height <= 2_000_000, Alpha);
                _bufferDirty = false;
                _bufferPresented = false;
            }
            return _drawingBuffer;
        }
    }

    // After the page's frame has been shown, the next drawing starts from an empty buffer unless it asked to keep it.
    private GlDefaultFramebuffer? BeginDrawingBufferUse()
    {
        var buffer = DrawingBuffer;
        if (buffer is null) return null;
        if (_bufferPresented && !PreserveDrawingBuffer)
        {
            Array.Clear(buffer.Color);
            buffer.FillDepth(1f);
            Array.Clear(buffer.Stencil);
            _bufferPresented = false;
        }
        return buffer;
    }

    /// <summary>Writes the drawing buffer into the canvas bitmap if it changed since it was last shown, and starts a new frame after it.</summary>
    public void Present()
    {
        if (_flushing || !_bufferDirty || _drawingBuffer is not { } buffer) return;
        _flushing = true;
        try
        {
            if (_canvas.PixelSurface() is { } surface)
            {
                surface.WritePixels(0, 0, buffer.Width, buffer.Height, buffer.ToBitmap(PremultipliedAlpha, Alpha));
                _canvas.Touch();
                _bufferDirty = false;
                _bufferPresented = true;
            }
        }
        finally
        {
            _flushing = false;
        }
    }

    internal bool IsPresenting => _flushing;

    private void MarkDrawn()
    {
        _bufferDirty = true;
        _canvas.Touch();
    }

    // ---- drawing ----

    private GlRenderTarget? SelectRenderTarget(out bool isDefault)
    {
        isDefault = false;
        if (DrawFramebufferObject is not { } framebuffer)
        {
            // A framebuffer name that was never created or is gone leaves nothing to draw into.
            if ((BoundDrawFramebuffer ?? BoundFramebuffer) is not null) return null;
            isDefault = true;
            return BeginDrawingBufferUse()?.Target();
        }
        if (FramebufferStatus(framebuffer) != 0x8CD5) return null;
        var colors = new GlImage?[framebuffer.DrawBuffers.Length];
        for (var i = 0; i < colors.Length; i++)
            if (framebuffer.DrawBuffers[i] == ColorAttachment0 + (uint)i) colors[i] = AttachedImage(framebuffer, ColorAttachment0 + (uint)i);
        var depth = DepthImage(framebuffer);
        var stencil = StencilImage(framebuffer);
        var any = colors.FirstOrDefault(c => c is not null) ?? depth ?? stencil;
        if (any is null) return null;
        return new GlRenderTarget(any.Width, any.Height, 1, colors, depth?.Data, stencil?.Stencil);
    }

    public void DrawArrays(uint mode, int first, int count)
    {
        DrawCalls++;
        if (first < 0 || count < 0) { SetError(0x0501); return; }
        if (count == 0) return;
        Draw(mode, count, vertex => first + vertex, first + count - 1, 1);
    }

    public void DrawElements(uint mode, uint type, int count, int offset) => DrawElementsCore(mode, type, count, offset, 1, countCall: true);

    public void DrawArraysInstanced(uint mode, int first, int count, int instanceCount)
    {
        DrawCalls++;
        if (first < 0 || count < 0 || instanceCount < 0) { SetError(0x0501); return; }
        if (count == 0 || instanceCount == 0) return;
        Draw(mode, count, vertex => first + vertex, first + count - 1, Math.Min(instanceCount, 100_000));
    }

    public void DrawElementsInstanced(uint mode, uint type, int count, int offset, int instanceCount)
    {
        if (instanceCount < 0) { DrawCalls++; SetError(0x0501); return; }
        DrawElementsCore(mode, type, count, offset, Math.Min(instanceCount, 100_000), countCall: true);
    }

    private void DrawElementsCore(uint mode, uint type, int count, int offset, int instances, bool countCall)
    {
        if (countCall) DrawCalls++;
        var indexSize = type switch { 0x1401u => 1, 0x1403u => 2, 0x1405u => 4, _ => 0 };
        if (indexSize == 0) { SetError(0x0500); return; }
        if (count < 0 || offset < 0) { SetError(0x0501); return; }
        if (offset % indexSize != 0) { SetError(0x0502); return; }
        if (count == 0 || instances == 0) return;
        if (BoundElementArrayBuffer is not { } elementBuffer || !Buffers.TryGetValue(elementBuffer, out var bytes) || (long)offset + (long)count * indexSize > bytes.Length)
        {
            SetError(0x0502);
            return;
        }
        var maxIndex = 0;
        Func<int, int> indexOf = indexSize switch
        {
            1 => i => bytes[offset + i],
            2 => i => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + i * 2, 2)),
            _ => i => (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + i * 4, 4)),
        };
        for (var i = 0; i < count; i++) maxIndex = Math.Max(maxIndex, indexOf(i));
        Draw(mode, count, indexOf, maxIndex, instances);
    }

    private void Draw(uint mode, int count, Func<int, int> indexOf, int maxIndex, int instances)
    {
        if (mode > 6) { SetError(0x0500); return; }
        if (CurrentProgramObject is not { } program) { SetError(0x0502); return; }
        var sources = new GlAttributeSource[16];
        var vao = CurrentVertexArray;
        for (var i = 0; i < sources.Length; i++) sources[i] = new GlAttributeSource { Generic = GenericAttributes[i] };
        foreach (var attribute in program.Attributes)
        {
            for (var column = 0; column < attribute.Locations; column++)
            {
                var location = attribute.Location + column;
                if (location >= 16 || !vao.Enabled.Contains(location)) continue;
                if (!vao.Pointers.TryGetValue(location, out var pointer) || !vao.Buffers.TryGetValue(location, out var bufferHandle) || !Buffers.TryGetValue(bufferHandle, out var buffer))
                {
                    SetError(0x0502);
                    return;
                }
                var source = sources[location];
                source.Enabled = true;
                source.Size = pointer.Size;
                source.Type = pointer.Type;
                source.Normalized = pointer.Normalized;
                source.Stride = pointer.Stride;
                source.Offset = pointer.Offset;
                source.Buffer = buffer;
                source.Divisor = (int)Math.Min(vao.Divisors.GetValueOrDefault(location), (uint)int.MaxValue);
                // Every vertex the draw touches has to lie inside the buffer.
                var componentBytes = pointer.Type switch { 0x1400u or 0x1401u => 1, 0x1402u or 0x1403u => 2, _ => 4 };
                var stride = pointer.Stride > 0 ? pointer.Stride : pointer.Size * componentBytes;
                var lastElement = source.Divisor == 0 ? maxIndex : (instances - 1) / source.Divisor;
                if ((long)lastElement * stride + pointer.Offset + pointer.Size * componentBytes > buffer.Length)
                {
                    SetError(0x0502);
                    return;
                }
            }
        }
        var target = SelectRenderTarget(out _);
        if (target is null)
        {
            SetError(0x0506);
            return;
        }
        var deadline = Stopwatch.GetTimestamp() + (long)(_maxShaderExecutionTime.TotalSeconds * Stopwatch.Frequency);
        var call = new GlDrawCall
        {
            Program = program,
            Attributes = sources,
            Mode = mode,
            Count = count,
            IndexOf = indexOf,
            MaxIndex = maxIndex,
            InstanceCount = instances,
            Target = target,
            State = CaptureFixedState(),
            Sampler = new GlTextureSampler(this),
            Deadline = deadline,
        };
        var status = GlPipeline.Draw(call);
        if (status == GlDrawStatus.InvalidOperation)
        {
            SetError(0x0502);
            return;
        }
        if (status == GlDrawStatus.Timeout)
        {
            Lose();
            _canvas.Element.OwnerDocument.DiagnosticSink?.Invoke(new Diagnostic(DiagnosticCode.LimitExceeded, Severity.Warning, "WebGL shader execution time exceeded.", null, "webgl"));
            return;
        }
        if ((BoundDrawFramebuffer ?? BoundFramebuffer) is null) MarkDrawn();
    }

    // ---- clearing, reading and copying ----

    public void Clear(uint mask)
    {
        var clearColor = (mask & 0x4000) != 0;
        var clearDepth = (mask & 0x0100) != 0;
        var clearStencil = (mask & 0x0400) != 0;
        if ((mask & ~0x4500u) != 0) { SetError(0x0501); return; }
        if (!clearColor && !clearDepth && !clearStencil)
            return;
        var scissor = EnabledCapabilities.Contains(0x0C11);
        var color = new Vector4(ClearR, ClearG, ClearB, ClearA);
        if ((BoundDrawFramebuffer ?? BoundFramebuffer) is not null)
        {
            if (DrawFramebufferObject is not { } framebuffer || FramebufferStatus(framebuffer) != 0x8CD5) { SetError(0x0506); return; }
            if (clearColor)
                for (var i = 0; i < framebuffer.DrawBuffers.Length; i++)
                    if (framebuffer.DrawBuffers[i] == ColorAttachment0 + (uint)i && AttachedImage(framebuffer, ColorAttachment0 + (uint)i) is { } image)
                        ClearColorRegion(image, color, scissor);
            if (clearDepth && DepthMask && DepthImage(framebuffer) is { } depthImage) ClearDepthRegion(depthImage.Data, depthImage.Width, depthImage.Height, 1, scissor);
            if (clearStencil && StencilImage(framebuffer) is { Stencil: { } stencilBits } stencilImage) ClearStencilRegion(stencilBits, stencilImage.Width, stencilImage.Height, 1, scissor);
            return;
        }
        if (BeginDrawingBufferUse() is not { } buffer) return;
        var samples = buffer.Samples;
        if (clearColor) ClearColorRegion(buffer.Image, color, scissor);
        if (clearDepth && DepthMask) ClearDepthRegion(buffer.DepthBytes, buffer.Width, buffer.Height, samples, scissor);
        if (clearStencil) ClearStencilRegion(buffer.Stencil, buffer.Width, buffer.Height, samples, scissor);
        MarkDrawn();
    }

    private void ClearColorRegion(GlImage image, Vector4 color, bool useScissor)
    {
        var samples = image.Samples;
        ForRegion(image.Width, image.Height, useScissor, pixel =>
        {
            for (var s = 0; s < samples; s++)
                image.Store(pixel * samples + s, color, ColorMaskRed, ColorMaskGreen, ColorMaskBlue, ColorMaskAlpha);
        });
    }

    private void ClearDepthRegion(byte[] pixels, int width, int height, int samplesPerPixel, bool useScissor, float? value = null)
    {
        var bits = BitConverter.SingleToInt32Bits(value ?? ClearDepth);
        ForRegion(width, height, useScissor, pixel =>
        {
            for (var s = 0; s < samplesPerPixel; s++)
                BinaryPrimitives.WriteInt32LittleEndian(pixels.AsSpan((pixel * samplesPerPixel + s) * 4, 4), bits);
        });
    }

    private void ClearStencilRegion(byte[] stencil, int width, int height, int samplesPerPixel, bool useScissor, int? value = null)
    {
        var clear = (uint)(value ?? ClearStencil);
        ForRegion(width, height, useScissor, pixel =>
        {
            for (var s = 0; s < samplesPerPixel; s++)
            {
                var i = pixel * samplesPerPixel + s;
                stencil[i] = (byte)((stencil[i] & ~StencilWriteMask) | (clear & StencilWriteMask));
            }
        });
    }

    private void ForRegion(int width, int height, bool useScissor, Action<int> each)
    {
        var left = 0; var bottom = 0; var right = width; var top = height;
        if (useScissor)
        {
            left = Math.Clamp(ScissorX, 0, width);
            bottom = Math.Clamp(ScissorY, 0, height);
            right = (int)Math.Clamp((long)ScissorX + ScissorWidth, 0, width);
            top = (int)Math.Clamp((long)ScissorY + ScissorHeight, 0, height);
        }
        for (var y = bottom; y < top; y++)
            for (var x = left; x < right; x++)
                each(y * width + x);
    }

    /// <summary>
    /// clearBufferfv, clearBufferiv, clearBufferuiv and clearBufferfi: clears one buffer of the draw framebuffer to the given value, honouring
    /// the scissor and write masks. A colour buffer takes the value as it is (floating-point images keep values outside 0 to 1).
    /// </summary>
    public void ClearBuffer(uint buffer, int drawBuffer, Vector4 value)
    {
        var scissor = EnabledCapabilities.Contains(0x0C11);
        var framebuffer = DrawFramebufferObject;
        if (buffer == 0x1800) // COLOR
        {
            if (drawBuffer < 0 || drawBuffer >= MaxColorAttachments) { SetError(0x0501); return; }
            if (framebuffer is null)
            {
                if (drawBuffer != 0 || BeginDrawingBufferUse() is not { } canvas) return;
                ClearColorRegion(canvas.Image, value, scissor);
                MarkDrawn();
                return;
            }
            if (framebuffer.DrawBuffers.Length > drawBuffer && framebuffer.DrawBuffers[drawBuffer] == ColorAttachment0 + (uint)drawBuffer && AttachedImage(framebuffer, ColorAttachment0 + (uint)drawBuffer) is { } image)
                ClearColorRegion(image, value, scissor);
        }
        else if (buffer == 0x1801) // DEPTH
        {
            if (!DepthMask) return;
            if (framebuffer is null)
            {
                if (BeginDrawingBufferUse() is { } canvas) { ClearDepthRegion(canvas.DepthBytes, canvas.Width, canvas.Height, canvas.Samples, scissor, value.X); MarkDrawn(); }
            }
            else if (DepthImage(framebuffer) is { } depth) ClearDepthRegion(depth.Data, depth.Width, depth.Height, 1, scissor, value.X);
        }
        else if (buffer == 0x1802) // STENCIL
        {
            if (framebuffer is null)
            {
                if (BeginDrawingBufferUse() is { } canvas) { ClearStencilRegion(canvas.Stencil, canvas.Width, canvas.Height, canvas.Samples, scissor, (int)value.X); MarkDrawn(); }
            }
            else if (StencilImage(framebuffer) is { Stencil: { } stencil } image) ClearStencilRegion(stencil, image.Width, image.Height, 1, scissor, (int)value.X);
        }
        else SetError(0x0500);
    }

    /// <summary>The depth buffer a read or blit takes from (float32 per sample, rows from the bottom) with its size, or null.</summary>
    public (byte[] Depth, int Width, int Height)? ReadDepth()
    {
        if (ReadFramebufferObject is { } framebuffer)
            return DepthImage(framebuffer) is { } image ? (image.Data, image.Width, image.Height) : null;
        return BeginDrawingBufferUse() is { } buffer ? (buffer.DepthBytes, buffer.Width, buffer.Height) : null;
    }

    public void BlitFramebuffer(int srcX0, int srcY0, int srcX1, int srcY1, int dstX0, int dstY0, int dstX1, int dstY1, uint mask, uint filter)
    {
        if ((mask & ~0x4500u) != 0) { SetError(0x0500); return; }
        if (dstX1 == dstX0 || dstY1 == dstY0) return;
        var drawFramebuffer = DrawFramebufferObject;
        var canvas = drawFramebuffer is null ? BeginDrawingBufferUse() : null;

        if ((mask & 0x4000) != 0 && ReadImage() is { } source)
        {
            var destination = canvas?.Image;
            if (drawFramebuffer is not null && drawFramebuffer.DrawBuffers.Length > 0 && drawFramebuffer.DrawBuffers[0] == ColorAttachment0)
                destination = AttachedImage(drawFramebuffer, ColorAttachment0);
            if (destination is { } target)
            {
                var samples = target.Samples;
                BlitRegion(target.Width, target.Height, dstX0, dstY0, dstX1, dstY1, source.Width, source.Height, srcX0, srcY0, srcX1, srcY1, (sx, sy, dx, dy) =>
                {
                    var value = source.LoadColor(sy * source.Width + sx);
                    for (var s = 0; s < samples; s++)
                        target.Store((dy * target.Width + dx) * samples + s, value, ColorMaskRed, ColorMaskGreen, ColorMaskBlue, ColorMaskAlpha);
                });
            }
        }
        if ((mask & 0x0100) != 0 && DepthMask && ReadDepth() is { } depthSource)
        {
            var destination = canvas?.DepthBytes;
            int width = canvas?.Width ?? 0, height = canvas?.Height ?? 0, samples = canvas?.Samples ?? 1;
            if (drawFramebuffer is not null && DepthImage(drawFramebuffer) is { } depthImage) (destination, width, height) = (depthImage.Data, depthImage.Width, depthImage.Height);
            if (destination is not null)
            {
                var sourceSamples = ReadFramebufferObject is null ? (_drawingBuffer?.Samples ?? 1) : 1;
                BlitRegion(width, height, dstX0, dstY0, dstX1, dstY1, depthSource.Width, depthSource.Height, srcX0, srcY0, srcX1, srcY1, (sx, sy, dx, dy) =>
                {
                    var depth = BitConverter.ToSingle(depthSource.Depth, (sy * depthSource.Width + sx) * sourceSamples * 4);
                    for (var s = 0; s < samples; s++)
                        BitConverter.TryWriteBytes(destination.AsSpan(((dy * width + dx) * samples + s) * 4, 4), depth);
                });
            }
        }
        if (drawFramebuffer is null) MarkDrawn();
    }

    // Nearest-neighbour copy of the source rectangle onto the destination rectangle (either may be flipped by giving its corners the other way round).
    private static void BlitRegion(int dstWidth, int dstHeight, int dstX0, int dstY0, int dstX1, int dstY1, int srcWidth, int srcHeight, int srcX0, int srcY0, int srcX1, int srcY1, Action<int, int, int, int> copy)
    {
        if (srcWidth <= 0 || srcHeight <= 0) return;
        var minX = Math.Max(0, Math.Min(dstX0, dstX1));
        var maxX = Math.Min(dstWidth, Math.Max(dstX0, dstX1));
        var minY = Math.Max(0, Math.Min(dstY0, dstY1));
        var maxY = Math.Min(dstHeight, Math.Max(dstY0, dstY1));
        var spanX = dstX1 - dstX0;
        var spanY = dstY1 - dstY0;
        for (var dy = minY; dy < maxY; dy++)
        {
            var ty = (dy + 0.5f - dstY0) / spanY;
            var sy = Math.Clamp((int)MathF.Floor(srcY0 + ty * (srcY1 - srcY0)), 0, srcHeight - 1);
            for (var dx = minX; dx < maxX; dx++)
            {
                var tx = (dx + 0.5f - dstX0) / spanX;
                var sx = Math.Clamp((int)MathF.Floor(srcX0 + tx * (srcX1 - srcX0)), 0, srcWidth - 1);
                copy(sx, sy, dx, dy);
            }
        }
    }
}
