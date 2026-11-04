using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Folio.Scripting.Gpu;

internal sealed partial class FolioNativeWebGlContext
{
    internal sealed class GlslVariableDecl
    {
        public required string Storage { get; init; } // "in", "out", "attribute", "varying", "uniform"
        public required string GlslType { get; init; }
        public required string Name { get; init; }
        public int ArraySize { get; init; } // 0 if not array, >0 if array
        public int? LayoutLocation { get; init; }
        public bool IsFlat { get; init; }
    }

    internal sealed class GlslUniformBlockDecl
    {
        public required string BlockName { get; init; }
        public string? InstanceName { get; init; }
        public required List<GlslVariableDecl> Members { get; init; }
    }

    internal sealed class GlslParsedShader
    {
        public required uint ShaderType { get; init; }
        public required int Version { get; init; }
        public required List<GlslVariableDecl> Inputs { get; init; }
        public required List<GlslVariableDecl> Outputs { get; init; }
        public required List<GlslVariableDecl> Uniforms { get; init; }
        public required List<GlslUniformBlockDecl> UniformBlocks { get; init; }
        public required string MainBody { get; init; }
        public required string HelperFunctions { get; init; }
        public bool UsesFragColor { get; init; }
    }

    internal sealed class ProgramUniformSlot
    {
        public required int Location { get; init; }
        public required string Name { get; init; } // base name or name[0]
        public required string BaseName { get; init; }
        public required uint GlType { get; init; }
        public required int ArraySize { get; init; }
        public required int Components { get; init; } // 1..16
        public required bool IsSampler { get; init; }
        public required bool IsMatrix { get; init; }
        public required bool IsInteger { get; init; }
        public required bool IsUnsigned { get; init; }
        public required int ConstantBufferFloatOffset { get; init; } // index into float[] cbuffer
        public int BlockIndex { get; init; } = -1;
        public int ByteOffset { get; init; } = -1;
        public required float[] FloatValues { get; init; }
        public required int[] IntValues { get; init; }
        public required uint[] UIntValues { get; init; }
    }

    internal sealed class ProgramAttribSlot
    {
        public required int Location { get; init; }
        public required string Name { get; init; }
        public required uint GlType { get; init; }
        public required int Size { get; init; }
        public required bool IsInteger { get; init; }
        public required bool IsUnsigned { get; init; }
    }

    internal sealed class ProgramUniformBlockSlot
    {
        public required uint BlockIndex { get; init; }
        public required string BlockName { get; init; }
        public string? InstanceName { get; init; }
        public required int RegisterSlot { get; init; } // b1, b2, ...
        public required int ByteSize { get; init; }
        public required List<GlslVariableDecl> Members { get; init; }
        public uint BindingPoint;
    }

    internal sealed class ProgramSamplerSlot
    {
        public required string Name { get; init; }
        public required uint GlType { get; init; }
        public required int RegisterSlot { get; init; } // t0..t31, s0..s31
        public required int UniformLocation { get; init; }
    }

    internal sealed class ProgramEntry : IDisposable
    {
        public uint Handle { get; }
        public readonly List<uint> AttachedShaders = [];
        public readonly Dictionary<string, uint> BoundAttribLocations = new(StringComparer.Ordinal);
        public readonly List<string> TransformFeedbackVaryings = [];
        public uint TransformFeedbackBufferMode = GlInterleavedAttribs;

        public bool Linked;
        public bool DeletePending;
        public string InfoLog = string.Empty;

        public IntPtr VertexShader;
        public IntPtr PixelShader;
        public byte[] VertexShaderBytecode = [];
        public byte[] PixelShaderBytecode = [];
        public GlslParsedShader? LinkedVertexShader;
        public GlslParsedShader? LinkedFragmentShader;

        public readonly List<ProgramAttribSlot> ActiveAttribs = [];
        public readonly Dictionary<string, int> AttribLocations = new(StringComparer.Ordinal);

        public readonly List<ProgramUniformSlot> ActiveUniforms = [];
        public readonly Dictionary<int, (ProgramUniformSlot Slot, int ElementIndex)> UniformLocations = [];
        public readonly Dictionary<string, int> UniformNameToLocation = new(StringComparer.Ordinal);

        public readonly List<ProgramUniformBlockSlot> ActiveUniformBlocks = [];
        public readonly List<ProgramSamplerSlot> Samplers = [];
        public readonly List<WebGlActiveInfo> LinkedTfVaryings = [];

        public float[] ConstantBufferFloats = new float[256]; // 1024 bytes (64 float4s)

        public ProgramEntry(uint handle) => Handle = handle;

        public void ReleaseCompiledObjects()
        {
            if (VertexShader != IntPtr.Zero) { Marshal.Release(VertexShader); VertexShader = IntPtr.Zero; }
            if (PixelShader != IntPtr.Zero) { Marshal.Release(PixelShader); PixelShader = IntPtr.Zero; }
            VertexShaderBytecode = [];
            PixelShaderBytecode = [];
        }

        public void Dispose() => ReleaseCompiledObjects();
    }

    public bool TryCreateShader(uint shaderType, out WebGlShaderHandle shader, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            shader = default;
            if (!CheckAvailable(out failure)) return false;
            if (shaderType is not (GlVertexShader or GlFragmentShader))
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            var id = _nextHandle++;
            _shaders[id] = new ShaderEntry(id, shaderType);
            shader = new WebGlShaderHandle(id);
            return true;
        }
    }

    public bool TrySetShaderSource(WebGlShaderHandle shader, ReadOnlySpan<byte> utf8Source, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (utf8Source.Length > _options.MaxShaderSourceBytes)
            {
                failure = new(GpuFailureKind.BudgetExceeded, "The shader source exceeds the maximum size budget.");
                return false;
            }
            if (!_shaders.TryGetValue(shader.Value, out var entry))
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            entry.Source = Encoding.UTF8.GetString(utf8Source);
            return true;
        }
    }

    public bool TryCompileShader(WebGlShaderHandle shader, out bool compiled, out string infoLog, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            compiled = false;
            infoLog = string.Empty;
            if (!CheckAvailable(out failure)) return false;
            if (!_shaders.TryGetValue(shader.Value, out var entry))
            {
                RecordGlError(GlInvalidValue);
                return true;
            }

            if (FolioGlslCompiler.TryParseShader(entry.Source, entry.ShaderType, _options.Version, out var parsed, out var log))
            {
                // Also test-compile a standalone HLSL stub with D3DCompile so any syntax/type errors in expressions are caught at compileShader time!
                if (FolioGlslCompiler.TryVerifyShaderWithD3DCompile(parsed!, out var hlslError))
                {
                    entry.Compiled = true;
                    entry.Parsed = parsed;
                    entry.InfoLog = string.Empty;
                    compiled = true;
                    infoLog = string.Empty;
                    return true;
                }
                log = hlslError;
            }

            entry.Compiled = false;
            entry.Parsed = null;
            entry.InfoLog = log;
            compiled = false;
            infoLog = log;
            return true;
        }
    }

    public bool TryGetShaderParameter(WebGlShaderHandle shader, uint parameter, out WebGlParameterValue value, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            value = default;
            if (!CheckAvailable(out failure)) return false;
            if (!_shaders.TryGetValue(shader.Value, out var entry))
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            switch (parameter)
            {
                case GlShaderType:
                    value = new(WebGlParameterKind.UInt32, UInt32: entry.ShaderType, Int32: (int)entry.ShaderType);
                    return true;
                case GlDeleteStatus:
                    value = new(WebGlParameterKind.Boolean, Boolean: entry.DeletePending, Int32: entry.DeletePending ? 1 : 0);
                    return true;
                case GlCompileStatus:
                    value = new(WebGlParameterKind.Boolean, Boolean: entry.Compiled, Int32: entry.Compiled ? 1 : 0);
                    return true;
                case GlInfoLogLength:
                    value = new(WebGlParameterKind.Int32, Int32: entry.InfoLog.Length);
                    return true;
                case GlShaderSourceLength:
                    value = new(WebGlParameterKind.Int32, Int32: entry.Source.Length);
                    return true;
                default:
                    RecordGlError(GlInvalidEnum);
                    return true;
            }
        }
    }

    public bool TryGetShaderSource(WebGlShaderHandle shader, out string source, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            source = string.Empty;
            if (!CheckAvailable(out failure)) return false;
            if (!_shaders.TryGetValue(shader.Value, out var entry))
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            source = entry.Source;
            return true;
        }
    }

    public bool TryGetShaderInfoLog(WebGlShaderHandle shader, out string infoLog, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            infoLog = string.Empty;
            if (!CheckAvailable(out failure)) return false;
            if (!_shaders.TryGetValue(shader.Value, out var entry))
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            infoLog = entry.InfoLog;
            return true;
        }
    }

    public bool TryGetShaderPrecisionFormat(uint shaderType, uint precisionType, out int rangeMin, out int rangeMax, out int precision, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            rangeMin = 0; rangeMax = 0; precision = 0;
            if (!CheckAvailable(out failure)) return false;
            if (shaderType is not (GlVertexShader or GlFragmentShader))
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            switch (precisionType)
            {
                case GlLowFloat:
                case GlMediumFloat:
                case GlHighFloat:
                    rangeMin = 127;
                    rangeMax = 127;
                    precision = 23;
                    return true;
                case GlLowInt:
                case GlMediumInt:
                case GlHighInt:
                    rangeMin = 31;
                    rangeMax = 30;
                    precision = 0;
                    return true;
                default:
                    RecordGlError(GlInvalidEnum);
                    return true;
            }
        }
    }

    public bool TryDeleteShader(WebGlShaderHandle shader, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (shader.Value == 0) return true;
            if (!_shaders.TryGetValue(shader.Value, out var entry))
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            entry.DeletePending = true;
            var attachedAnywhere = false;
            foreach (var prog in _programs.Values)
            {
                if (prog.AttachedShaders.Contains(shader.Value))
                {
                    attachedAnywhere = true;
                    break;
                }
            }
            if (!attachedAnywhere)
                _shaders.Remove(shader.Value);
            return true;
        }
    }

    public bool TryCreateProgram(out WebGlProgramHandle program, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            program = default;
            if (!CheckAvailable(out failure)) return false;
            var id = _nextHandle++;
            _programs[id] = new ProgramEntry(id);
            program = new WebGlProgramHandle(id);
            return true;
        }
    }

    public bool TryDeleteProgram(WebGlProgramHandle program, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (program.Value == 0) return true;
            if (!_programs.TryGetValue(program.Value, out var entry))
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            if (_currentProgram == program.Value)
                _currentProgram = null;
            entry.DeletePending = true;
            entry.Dispose();
            _programs.Remove(program.Value);
            return true;
        }
    }

    public bool TryAttachShader(WebGlProgramHandle program, WebGlShaderHandle shader, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (!_programs.TryGetValue(program.Value, out var prog) || !_shaders.TryGetValue(shader.Value, out var sh))
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            if (prog.AttachedShaders.Contains(shader.Value))
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            foreach (var existingId in prog.AttachedShaders)
            {
                if (_shaders.TryGetValue(existingId, out var existing) && existing.ShaderType == sh.ShaderType)
                {
                    RecordGlError(GlInvalidOperation);
                    return true;
                }
            }
            prog.AttachedShaders.Add(shader.Value);
            return true;
        }
    }

    public bool TryDetachShader(WebGlProgramHandle program, WebGlShaderHandle shader, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (!_programs.TryGetValue(program.Value, out var prog) || !_shaders.TryGetValue(shader.Value, out var sh))
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            if (!prog.AttachedShaders.Remove(shader.Value))
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            if (sh.DeletePending)
                _shaders.Remove(shader.Value);
            return true;
        }
    }

    public bool TryLinkProgram(WebGlProgramHandle program, out bool linked, out string infoLog, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            linked = false;
            infoLog = string.Empty;
            if (!CheckAvailable(out failure)) return false;
            if (!_programs.TryGetValue(program.Value, out var prog))
            {
                RecordGlError(GlInvalidValue);
                return true;
            }

            ShaderEntry? vs = null;
            ShaderEntry? fs = null;
            foreach (var shId in prog.AttachedShaders)
            {
                if (_shaders.TryGetValue(shId, out var sh))
                {
                    if (sh.ShaderType == GlVertexShader) vs = sh;
                    else if (sh.ShaderType == GlFragmentShader) fs = sh;
                }
            }

            if (vs is null || fs is null || !vs.Compiled || !fs.Compiled || vs.Parsed is null || fs.Parsed is null)
            {
                prog.Linked = false;
                prog.InfoLog = "Program requires both a compiled vertex shader and a compiled fragment shader.";
                infoLog = prog.InfoLog;
                return true;
            }

            if (!FolioGlslCompiler.TryLinkProgram(_device.DevicePtr, _createVertexShader, _createPixelShader, prog, vs.Parsed, fs.Parsed, out var linkError))
            {
                prog.Linked = false;
                prog.InfoLog = linkError;
                infoLog = linkError;
                return true;
            }

            prog.Linked = true;
            prog.InfoLog = string.Empty;
            linked = true;
            infoLog = string.Empty;
            return true;
        }
    }

    public bool TryValidateProgram(WebGlProgramHandle program, out bool valid, out string infoLog, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            valid = false;
            infoLog = string.Empty;
            if (!CheckAvailable(out failure)) return false;
            if (!_programs.TryGetValue(program.Value, out var prog))
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            valid = prog.Linked;
            infoLog = prog.InfoLog;
            return true;
        }
    }

    public bool TryGetProgramParameter(WebGlProgramHandle program, uint parameter, out WebGlParameterValue value, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            value = default;
            if (!CheckAvailable(out failure)) return false;
            if (!_programs.TryGetValue(program.Value, out var prog))
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            switch (parameter)
            {
                case GlDeleteStatus:
                    value = new(WebGlParameterKind.Boolean, Boolean: prog.DeletePending, Int32: prog.DeletePending ? 1 : 0);
                    return true;
                case GlLinkStatus:
                    value = new(WebGlParameterKind.Boolean, Boolean: prog.Linked, Int32: prog.Linked ? 1 : 0);
                    return true;
                case GlValidateStatus:
                    value = new(WebGlParameterKind.Boolean, Boolean: prog.Linked, Int32: prog.Linked ? 1 : 0);
                    return true;
                case GlInfoLogLength:
                    value = new(WebGlParameterKind.Int32, Int32: prog.InfoLog.Length);
                    return true;
                case GlAttachedShaders:
                    value = new(WebGlParameterKind.Int32, Int32: prog.AttachedShaders.Count);
                    return true;
                case GlActiveAttributes:
                    value = new(WebGlParameterKind.Int32, Int32: prog.ActiveAttribs.Count);
                    return true;
                case GlActiveUniforms:
                    value = new(WebGlParameterKind.Int32, Int32: prog.ActiveUniforms.Count);
                    return true;
                case GlTransformFeedbackVaryings:
                    value = new(WebGlParameterKind.Int32, Int32: prog.LinkedTfVaryings.Count);
                    return true;
                case GlTransformFeedbackBufferMode:
                    value = new(WebGlParameterKind.UInt32, UInt32: prog.TransformFeedbackBufferMode, Int32: (int)prog.TransformFeedbackBufferMode);
                    return true;
                case GlActiveUniformBlocks:
                    value = new(WebGlParameterKind.Int32, Int32: prog.ActiveUniformBlocks.Count);
                    return true;
                default:
                    RecordGlError(GlInvalidEnum);
                    return true;
            }
        }
    }

    public bool TryGetProgramInfoLog(WebGlProgramHandle program, out string infoLog, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            infoLog = string.Empty;
            if (!CheckAvailable(out failure)) return false;
            if (!_programs.TryGetValue(program.Value, out var prog))
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            infoLog = prog.InfoLog;
            return true;
        }
    }

    public bool TryGetAttachedShaders(WebGlProgramHandle program, Span<WebGlShaderHandle> shaders, out int count, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            count = 0;
            if (!CheckAvailable(out failure)) return false;
            if (!_programs.TryGetValue(program.Value, out var prog))
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            count = Math.Min(shaders.Length, prog.AttachedShaders.Count);
            for (var i = 0; i < count; i++)
                shaders[i] = new WebGlShaderHandle(prog.AttachedShaders[i]);
            return true;
        }
    }

    public bool TryGetActiveAttrib(WebGlProgramHandle program, uint index, out WebGlActiveInfo info, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            info = new(string.Empty, 0, 0);
            if (!CheckAvailable(out failure)) return false;
            if (!_programs.TryGetValue(program.Value, out var prog))
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            if (!prog.Linked || index >= (uint)prog.ActiveAttribs.Count)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            var attr = prog.ActiveAttribs[(int)index];
            info = new(attr.Name, attr.Size, attr.GlType);
            return true;
        }
    }

    public bool TryGetActiveUniform(WebGlProgramHandle program, uint index, out WebGlActiveInfo info, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            info = new(string.Empty, 0, 0);
            if (!CheckAvailable(out failure)) return false;
            if (!_programs.TryGetValue(program.Value, out var prog))
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            if (!prog.Linked || index >= (uint)prog.ActiveUniforms.Count)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            var uni = prog.ActiveUniforms[(int)index];
            info = new(uni.Name, uni.ArraySize, uni.GlType);
            return true;
        }
    }

    public bool TryBindAttribLocation(WebGlProgramHandle program, uint index, ReadOnlySpan<byte> utf8Name, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (!_programs.TryGetValue(program.Value, out var prog))
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            if (index >= MaxVertexAttribs)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            var name = Encoding.UTF8.GetString(utf8Name);
            if (name.StartsWith("gl_", StringComparison.Ordinal))
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            prog.BoundAttribLocations[name] = index;
            return true;
        }
    }

    public bool TryUseProgram(WebGlProgramHandle? program, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (program is null || program.Value.Value == 0)
            {
                _currentProgram = null;
                return true;
            }
            if (!_programs.TryGetValue(program.Value.Value, out var prog))
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            if (!prog.Linked)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            _currentProgram = prog.Handle;
            return true;
        }
    }

    public bool TryGetAttribLocation(WebGlProgramHandle program, string name, out WebGlAttribLocation location, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            location = new(-1);
            if (!CheckAvailable(out failure)) return false;
            if (!_programs.TryGetValue(program.Value, out var prog))
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            if (!prog.Linked)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            if (prog.AttribLocations.TryGetValue(name, out var loc))
                location = new(loc);
            return true;
        }
    }

    public bool TryGetUniformLocation(WebGlProgramHandle program, string name, out WebGlUniformLocation location, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            location = new(-1);
            if (!CheckAvailable(out failure)) return false;
            if (!_programs.TryGetValue(program.Value, out var prog))
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            if (!prog.Linked)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            if (prog.UniformNameToLocation.TryGetValue(name, out var loc))
            {
                location = new(loc);
                return true;
            }
            if (!name.EndsWith("[0]", StringComparison.Ordinal) && prog.UniformNameToLocation.TryGetValue(name + "[0]", out loc))
            {
                location = new(loc);
                return true;
            }
            return true;
        }
    }

    public bool TryGetUniformIndices(WebGlProgramHandle program, IReadOnlyList<string> names, Span<uint> indices, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (!_programs.TryGetValue(program.Value, out var prog) || !prog.Linked)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            var count = Math.Min(names.Count, indices.Length);
            for (var i = 0; i < count; i++)
            {
                var n = names[i];
                var found = uint.MaxValue;
                for (var u = 0; u < prog.ActiveUniforms.Count; u++)
                {
                    if (prog.ActiveUniforms[u].BaseName == n || prog.ActiveUniforms[u].Name == n)
                    {
                        found = (uint)u;
                        break;
                    }
                }
                indices[i] = found;
            }
            return true;
        }
    }

    public bool TryGetActiveUniforms(WebGlProgramHandle program, ReadOnlySpan<uint> indices, uint parameter, Span<int> values, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (!_programs.TryGetValue(program.Value, out var prog) || !prog.Linked)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            var count = Math.Min(indices.Length, values.Length);
            for (var i = 0; i < count; i++)
            {
                var idx = indices[i];
                if (idx >= (uint)prog.ActiveUniforms.Count)
                {
                    values[i] = 0;
                    continue;
                }
                var u = prog.ActiveUniforms[(int)idx];
                values[i] = parameter switch
                {
                    0x8A37 => (int)u.GlType, // UNIFORM_TYPE
                    0x8A38 => u.ArraySize, // UNIFORM_SIZE
                    0x8A39 => u.Name.Length, // UNIFORM_NAME_LENGTH
                    0x8A3A => u.BlockIndex, // UNIFORM_BLOCK_INDEX
                    0x8A3B => u.ByteOffset, // UNIFORM_OFFSET
                    0x8A3C => u.ArraySize > 1 ? 16 : 0, // UNIFORM_ARRAY_STRIDE
                    0x8A3D => u.IsMatrix ? 16 : 0, // UNIFORM_MATRIX_STRIDE
                    0x8A3E => 0, // UNIFORM_IS_ROW_MAJOR
                    _ => 0,
                };
            }
            return true;
        }
    }

    public bool TryGetUniformBlockIndex(WebGlProgramHandle program, ReadOnlySpan<byte> name, out uint index, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            index = uint.MaxValue;
            if (!CheckAvailable(out failure)) return false;
            if (!_programs.TryGetValue(program.Value, out var prog) || !prog.Linked)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            var blockName = Encoding.UTF8.GetString(name);
            for (var i = 0; i < prog.ActiveUniformBlocks.Count; i++)
            {
                if (prog.ActiveUniformBlocks[i].BlockName == blockName)
                {
                    index = (uint)i;
                    return true;
                }
            }
            return true;
        }
    }

    public bool TryUniformBlockBinding(WebGlProgramHandle program, uint index, uint binding, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (!_programs.TryGetValue(program.Value, out var prog) || !prog.Linked)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            if (index >= (uint)prog.ActiveUniformBlocks.Count || binding >= MaxUniformBufferBindings)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            prog.ActiveUniformBlocks[(int)index].BindingPoint = binding;
            return true;
        }
    }

    public bool TryTransformFeedbackVaryings(WebGlProgramHandle program, IReadOnlyList<string> varyings, uint bufferMode, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (!_programs.TryGetValue(program.Value, out var prog))
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            if (bufferMode is not (GlInterleavedAttribs or GlSeparateAttribs))
            {
                RecordGlError(GlInvalidEnum);
                return true;
            }
            if (bufferMode == GlSeparateAttribs && varyings.Count > MaxTransformFeedbackBuffers)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            prog.TransformFeedbackVaryings.Clear();
            prog.TransformFeedbackVaryings.AddRange(varyings);
            prog.TransformFeedbackBufferMode = bufferMode;
            return true;
        }
    }

    public bool TryGetTransformFeedbackVarying(WebGlProgramHandle program, uint index, out WebGlActiveInfo info, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            info = new(string.Empty, 0, 0);
            if (!CheckAvailable(out failure)) return false;
            if (!_programs.TryGetValue(program.Value, out var prog) || !prog.Linked)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            if (index >= (uint)prog.LinkedTfVaryings.Count)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            info = prog.LinkedTfVaryings[(int)index];
            return true;
        }
    }

    public bool TryUniformFloat(WebGlUniformLocation location, int components, ReadOnlySpan<float> values, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (location.Value < 0) return true;
            if (_currentProgram is not uint progId || !_programs.TryGetValue(progId, out var prog) || !prog.Linked)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            if (!prog.UniformLocations.TryGetValue(location.Value, out var target))
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            var (slot, elemIdx) = target;
            if (slot.IsMatrix || slot.IsInteger || slot.IsUnsigned || slot.IsSampler || slot.Components != components)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            if (values.Length == 0 || values.Length % components != 0)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            var count = Math.Min(values.Length / components, slot.ArraySize - elemIdx);
            for (var e = 0; e < count; e++)
            {
                var dstIdx = (elemIdx + e) * components;
                var cbIdx = slot.ConstantBufferFloatOffset + (elemIdx + e) * 4;
                for (var c = 0; c < components; c++)
                {
                    var v = values[e * components + c];
                    slot.FloatValues[dstIdx + c] = v;
                    prog.ConstantBufferFloats[cbIdx + c] = v;
                }
            }
            return true;
        }
    }

    public bool TryUniformInt(WebGlUniformLocation location, int components, ReadOnlySpan<int> values, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (location.Value < 0) return true;
            if (_currentProgram is not uint progId || !_programs.TryGetValue(progId, out var prog) || !prog.Linked)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            if (!prog.UniformLocations.TryGetValue(location.Value, out var target))
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            var (slot, elemIdx) = target;
            if (slot.IsSampler && components == 1)
            {
                var count = Math.Min(values.Length, slot.ArraySize - elemIdx);
                for (var e = 0; e < count; e++)
                {
                    slot.IntValues[elemIdx + e] = values[e];
                    slot.FloatValues[elemIdx + e] = values[e];
                }
                return true;
            }
            if (slot.IsMatrix || (!slot.IsInteger && slot.GlType is not (GlBool or GlBoolVec2 or GlBoolVec3 or GlBoolVec4)) || slot.Components != components)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            if (values.Length == 0 || values.Length % components != 0)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            var elemCount = Math.Min(values.Length / components, slot.ArraySize - elemIdx);
            for (var e = 0; e < elemCount; e++)
            {
                var dstIdx = (elemIdx + e) * components;
                var cbIdx = slot.ConstantBufferFloatOffset + (elemIdx + e) * 4;
                for (var c = 0; c < components; c++)
                {
                    var v = values[e * components + c];
                    slot.IntValues[dstIdx + c] = v;
                    slot.FloatValues[dstIdx + c] = v;
                    prog.ConstantBufferFloats[cbIdx + c] = BitConverter.Int32BitsToSingle(v);
                }
            }
            return true;
        }
    }

    public bool TryUniformUInt(WebGlUniformLocation location, int components, ReadOnlySpan<uint> values, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (location.Value < 0) return true;
            if (_currentProgram is not uint progId || !_programs.TryGetValue(progId, out var prog) || !prog.Linked)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            if (!prog.UniformLocations.TryGetValue(location.Value, out var target))
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            var (slot, elemIdx) = target;
            if (!slot.IsUnsigned || slot.Components != components)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            if (values.Length == 0 || values.Length % components != 0)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            var elemCount = Math.Min(values.Length / components, slot.ArraySize - elemIdx);
            for (var e = 0; e < elemCount; e++)
            {
                var dstIdx = (elemIdx + e) * components;
                var cbIdx = slot.ConstantBufferFloatOffset + (elemIdx + e) * 4;
                for (var c = 0; c < components; c++)
                {
                    var v = values[e * components + c];
                    slot.UIntValues[dstIdx + c] = v;
                    slot.FloatValues[dstIdx + c] = v;
                    prog.ConstantBufferFloats[cbIdx + c] = BitConverter.UInt32BitsToSingle(v);
                }
            }
            return true;
        }
    }

    public bool TryUniformMatrix(WebGlUniformLocation location, int columns, int rows, ReadOnlySpan<float> values, bool transpose, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (!CheckAvailable(out failure)) return false;
            if (location.Value < 0) return true;
            if (transpose && _options.Version == WebGlContextVersion.WebGl1)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            if (_currentProgram is not uint progId || !_programs.TryGetValue(progId, out var prog) || !prog.Linked)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            if (!prog.UniformLocations.TryGetValue(location.Value, out var target))
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            var (slot, elemIdx) = target;
            var matSize = columns * rows;
            if (!slot.IsMatrix || slot.Components != matSize)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            if (values.Length == 0 || values.Length % matSize != 0)
            {
                RecordGlError(GlInvalidValue);
                return true;
            }
            var elemCount = Math.Min(values.Length / matSize, slot.ArraySize - elemIdx);
            for (var e = 0; e < elemCount; e++)
            {
                var dstIdx = (elemIdx + e) * matSize;
                var srcSlice = values.Slice(e * matSize, matSize);
                // Store column-major in slot.FloatValues and in constant buffer (padded to 4 floats per column)
                var cbBase = slot.ConstantBufferFloatOffset + (elemIdx + e) * (columns * 4);
                for (var col = 0; col < columns; col++)
                {
                    for (var row = 0; row < rows; row++)
                    {
                        var val = transpose ? srcSlice[row * columns + col] : srcSlice[col * rows + row];
                        slot.FloatValues[dstIdx + col * rows + row] = val;
                        prog.ConstantBufferFloats[cbBase + col * 4 + row] = val;
                    }
                }
            }
            return true;
        }
    }

    public bool TryGetUniform(WebGlProgramHandle program, WebGlUniformLocation location, out WebGlUniformValue value, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            value = default;
            if (!CheckAvailable(out failure)) return false;
            if (!_programs.TryGetValue(program.Value, out var prog) || !prog.Linked)
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            if (!prog.UniformLocations.TryGetValue(location.Value, out var target))
            {
                RecordGlError(GlInvalidOperation);
                return true;
            }
            var (slot, elemIdx) = target;
            var offset = elemIdx * slot.Components;
            if (slot.IsUnsigned)
            {
                var arr = new uint[slot.Components];
                Array.Copy(slot.UIntValues, offset, arr, 0, slot.Components);
                value = new(WebGlParameterKind.UInt32Array, UInt32: arr);
                return true;
            }
            if (slot.IsInteger || slot.IsSampler)
            {
                var arr = new int[slot.Components];
                Array.Copy(slot.IntValues, offset, arr, 0, slot.Components);
                value = new(WebGlParameterKind.Int32Array, Int32: arr);
                return true;
            }
            var farr = new float[slot.Components];
            Array.Copy(slot.FloatValues, offset, farr, 0, slot.Components);
            value = new(WebGlParameterKind.Float32Array, Float32: farr);
            return true;
        }
    }
}
