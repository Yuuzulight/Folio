using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Folio.Scripting.Gpu;

internal static class FolioGlslCompiler
{
    private static readonly Regex LayoutLocationRegex = new(
        @"layout\s*\(\s*location\s*=\s*(-?\d+)\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UniformBlockRegex = new(
        @"(?:layout\s*\([^)]*\)\s*)?uniform\s+([A-Za-z_][A-Za-z0-9_]*)\s*\{([^}]*)\}\s*([A-Za-z_][A-Za-z0-9_]*)?\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex DeclLineRegex = new(
        @"^(?:layout\s*\(\s*location\s*=\s*(?<loc>-?\d+)\s*\)\s*)?(?<flat>flat\s+)?(?<prec>(?:lowp|mediump|highp)\s+)?(?<storage>in|out|attribute|varying|uniform)\s+(?<prec2>(?:lowp|mediump|highp)\s+)?(?<type>[A-Za-z_][A-Za-z0-9_]*)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:\s*\[\s*(?<arr>\d+)\s*\])?\s*;$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MemberDeclRegex = new(
        @"^(?:(?:lowp|mediump|highp)\s+)?(?<type>[A-Za-z_][A-Za-z0-9_]*)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:\s*\[\s*(?<arr>\d+)\s*\])?\s*;$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static bool TryParseShader(
        string rawSource,
        uint shaderType,
        WebGlContextVersion contextVersion,
        out FolioNativeWebGlContext.GlslParsedShader? parsed,
        out string infoLog)
    {
        parsed = null;
        infoLog = string.Empty;

        if (string.IsNullOrWhiteSpace(rawSource))
        {
            infoLog = "ERROR: 0:1: Shader source is empty.";
            return false;
        }

        var source = StripComments(rawSource);
        var trimmed = source.TrimStart();

        var version = 100;
        if (trimmed.StartsWith("#version", StringComparison.Ordinal))
        {
            var lineEnd = trimmed.IndexOf('\n');
            var versionLine = (lineEnd >= 0 ? trimmed[..lineEnd] : trimmed).Trim();
            if (versionLine.StartsWith("#version 300 es", StringComparison.Ordinal))
            {
                if (contextVersion == WebGlContextVersion.WebGl1)
                {
                    infoLog = "ERROR: 0:1: '#version 300 es' is not supported in WebGL 1.";
                    return false;
                }
                version = 300;
            }
            else if (versionLine.StartsWith("#version 100", StringComparison.Ordinal))
            {
                version = 100;
            }
            else
            {
                infoLog = $"ERROR: 0:1: Unsupported GLSL version directive '{versionLine}'.";
                return false;
            }
            trimmed = lineEnd >= 0 ? trimmed[(lineEnd + 1)..] : string.Empty;
        }

        // Reject unknown preprocessor directives (keep #define / #ifdef / #endif / #pragma / #extension)
        var lines = trimmed.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var cleanLines = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            var l = line.Trim();
            if (l.StartsWith('#'))
            {
                if (l.StartsWith("#version", StringComparison.Ordinal))
                {
                    infoLog = "ERROR: 0:1: #version must occur first in shader.";
                    return false;
                }
                continue;
            }
            if (Regex.IsMatch(l, @"^precision\s+(lowp|mediump|highp)\s+(float|int|sampler2D|samplerCube|sampler3D|sampler2DArray|isampler2D|usampler2D|isampler3D|usampler3D|isampler2DArray|usampler2DArray)\s*;$"))
                continue;
            cleanLines.Add(line);
        }

        var bodyText = string.Join("\n", cleanLines);

        // Extract uniform blocks (GLSL ES 3.00)
        var uniformBlocks = new List<FolioNativeWebGlContext.GlslUniformBlockDecl>();
        bodyText = UniformBlockRegex.Replace(bodyText, match =>
        {
            if (version < 300)
                return match.Value;
            var blockName = match.Groups[1].Value;
            var membersRaw = match.Groups[2].Value;
            var instName = match.Groups[3].Success && !string.IsNullOrEmpty(match.Groups[3].Value) ? match.Groups[3].Value : null;
            var members = new List<FolioNativeWebGlContext.GlslVariableDecl>();
            foreach (var stmt in SplitStatementsOutsideBraces(membersRaw))
            {
                var m = MemberDeclRegex.Match(stmt.Trim());
                if (m.Success)
                {
                    members.Add(new FolioNativeWebGlContext.GlslVariableDecl
                    {
                        Storage = "uniform",
                        GlslType = m.Groups["type"].Value,
                        Name = m.Groups["name"].Value,
                        ArraySize = m.Groups["arr"].Success ? int.Parse(m.Groups["arr"].Value, CultureInfo.InvariantCulture) : 0,
                    });
                }
            }
            uniformBlocks.Add(new FolioNativeWebGlContext.GlslUniformBlockDecl
            {
                BlockName = blockName,
                InstanceName = instName,
                Members = members,
            });
            return string.Empty;
        });

        // Locate `void main(...) { ... }`
        var mainMatch = Regex.Match(bodyText, @"\bvoid\s+main\s*\(\s*(?:void)?\s*\)\s*\{");
        if (!mainMatch.Success)
        {
            infoLog = "ERROR: 0:1: Missing 'void main()' entry point.";
            return false;
        }

        var openBraceIdx = mainMatch.Index + mainMatch.Length - 1;
        var closeBraceIdx = FindMatchingBrace(bodyText, openBraceIdx);
        if (closeBraceIdx < 0)
        {
            infoLog = "ERROR: 0:1: Unclosed '{' in 'void main()'.";
            return false;
        }

        var mainBody = bodyText[(openBraceIdx + 1)..closeBraceIdx];
        var outsideMain = bodyText[..mainMatch.Index] + "\n" + bodyText[(closeBraceIdx + 1)..];

        var inputs = new List<FolioNativeWebGlContext.GlslVariableDecl>();
        var outputs = new List<FolioNativeWebGlContext.GlslVariableDecl>();
        var uniforms = new List<FolioNativeWebGlContext.GlslVariableDecl>();
        var helpers = new StringBuilder();

        foreach (var topItem in SplitTopLevelItems(outsideMain))
        {
            var item = topItem.Trim();
            if (string.IsNullOrEmpty(item)) continue;
            if (Regex.IsMatch(item, @"^precision\s+(lowp|mediump|highp)\s+(float|int|sampler2D|samplerCube|sampler3D|sampler2DArray|isampler2D|usampler2D|isampler3D|usampler3D|isampler2DArray|usampler2DArray)\s*;$"))
                continue;

            var dm = DeclLineRegex.Match(item);
            if (dm.Success)
            {
                var storage = dm.Groups["storage"].Value;
                var type = dm.Groups["type"].Value;
                var name = dm.Groups["name"].Value;
                var arr = dm.Groups["arr"].Success ? int.Parse(dm.Groups["arr"].Value, CultureInfo.InvariantCulture) : 0;
                var loc = dm.Groups["loc"].Success ? int.Parse(dm.Groups["loc"].Value, CultureInfo.InvariantCulture) : (int?)null;
                var isFlat = dm.Groups["flat"].Success;

                if (loc is < 0 or >= FolioNativeWebGlContext.MaxVertexAttribs)
                {
                    infoLog = $"ERROR: 0:1: Invalid layout location {loc}.";
                    return false;
                }

                if (version == 100 && storage is "in" or "out")
                {
                    infoLog = $"ERROR: 0:1: '{storage}' storage qualifier requires #version 300 es.";
                    return false;
                }
                if (version == 300 && storage is "attribute" or "varying")
                {
                    infoLog = $"ERROR: 0:1: '{storage}' is removed in #version 300 es.";
                    return false;
                }
                if (!TryMapGlslType(type, out _, out _, out _, out _, out _, out _))
                {
                    infoLog = $"ERROR: 0:1: Unknown GLSL type '{type}'.";
                    return false;
                }

                var decl = new FolioNativeWebGlContext.GlslVariableDecl
                {
                    Storage = storage,
                    GlslType = type,
                    Name = name,
                    ArraySize = arr,
                    LayoutLocation = loc,
                    IsFlat = isFlat,
                };

                if (storage == "uniform")
                {
                    uniforms.Add(decl);
                }
                else if (shaderType == FolioNativeWebGlContext.GlVertexShader)
                {
                    if (storage is "in" or "attribute") inputs.Add(decl);
                    else if (storage is "out" or "varying") outputs.Add(decl);
                    else
                    {
                        infoLog = $"ERROR: 0:1: Invalid qualifier '{storage}' in vertex shader.";
                        return false;
                    }
                }
                else
                {
                    if (storage is "in" or "varying") inputs.Add(decl);
                    else if (storage == "out") outputs.Add(decl);
                    else
                    {
                        infoLog = $"ERROR: 0:1: Invalid qualifier '{storage}' in fragment shader.";
                        return false;
                    }
                }
                continue;
            }

            // Check if it looks like a function definition
            if (item.Contains('(') && item.EndsWith('}'))
            {
                helpers.AppendLine(item);
                continue;
            }

            infoLog = $"ERROR: 0:1: Syntax error at '{item}'.";
            return false;
        }

        var usesFragColor = Regex.IsMatch(mainBody, @"\bgl_FragColor\b");
        if (shaderType == FolioNativeWebGlContext.GlFragmentShader)
        {
            if (version == 300 && usesFragColor)
            {
                infoLog = "ERROR: 0:1: gl_FragColor is not available in #version 300 es.";
                return false;
            }
            if (version == 300 && outputs.Count == 0)
            {
                infoLog = "ERROR: 0:1: Fragment shader in #version 300 es must declare an 'out' color variable.";
                return false;
            }
        }
        else
        {
            if (usesFragColor)
            {
                infoLog = "ERROR: 0:1: gl_FragColor cannot be used in a vertex shader.";
                return false;
            }
        }

        parsed = new FolioNativeWebGlContext.GlslParsedShader
        {
            ShaderType = shaderType,
            Version = version,
            Inputs = inputs,
            Outputs = outputs,
            Uniforms = uniforms,
            UniformBlocks = uniformBlocks,
            MainBody = mainBody,
            HelperFunctions = helpers.ToString(),
            UsesFragColor = usesFragColor,
        };
        return true;
    }

    internal static bool TryVerifyShaderWithD3DCompile(
        FolioNativeWebGlContext.GlslParsedShader parsed,
        out string errorLog)
    {
        errorLog = string.Empty;
        var hlsl = BuildStandaloneHlslForValidation(parsed);
        var entry = parsed.ShaderType == FolioNativeWebGlContext.GlVertexShader ? "VSMain" : "PSMain";
        var profile = parsed.ShaderType == FolioNativeWebGlContext.GlVertexShader ? "vs_5_0" : "ps_5_0";
        return TryCompileHlsl(hlsl, entry, profile, out _, out errorLog);
    }

    internal static bool TryLinkProgram(
        IntPtr devicePtr,
        FolioD3D11Interop.CreateVertexShaderDelegate createVs,
        FolioD3D11Interop.CreatePixelShaderDelegate createPs,
        FolioNativeWebGlContext.ProgramEntry prog,
        FolioNativeWebGlContext.GlslParsedShader vs,
        FolioNativeWebGlContext.GlslParsedShader fs,
        out string errorLog)
    {
        errorLog = string.Empty;
        if (vs.Version != fs.Version)
        {
            errorLog = "Vertex and fragment shaders must use the same GLSL version.";
            return false;
        }

        // Validate varyings match between VS outputs and FS inputs
        foreach (var fsIn in fs.Inputs)
        {
            var vsOut = vs.Outputs.Find(o => o.Name == fsIn.Name);
            if (vsOut is null)
            {
                errorLog = $"Fragment shader input '{fsIn.Name}' is not written by the vertex shader.";
                return false;
            }
            if (vsOut.GlslType != fsIn.GlslType || vsOut.ArraySize != fsIn.ArraySize || vsOut.IsFlat != fsIn.IsFlat)
            {
                errorLog = $"Type mismatch for varying '{fsIn.Name}' between vertex and fragment shaders.";
                return false;
            }
        }

        // Validate uniform types match across VS and FS
        var mergedUniformDecls = new List<FolioNativeWebGlContext.GlslVariableDecl>();
        foreach (var u in vs.Uniforms)
            mergedUniformDecls.Add(u);
        foreach (var u in fs.Uniforms)
        {
            var existing = mergedUniformDecls.Find(x => x.Name == u.Name);
            if (existing is not null)
            {
                if (existing.GlslType != u.GlslType || existing.ArraySize != u.ArraySize)
                {
                    errorLog = $"Uniform '{u.Name}' declared with conflicting types in vertex and fragment shaders.";
                    return false;
                }
            }
            else
            {
                mergedUniformDecls.Add(u);
            }
        }

        // Assign vertex attribute locations
        var activeAttribs = new List<FolioNativeWebGlContext.ProgramAttribSlot>();
        var attribLocations = new Dictionary<string, int>(StringComparer.Ordinal);
        var usedLocations = new HashSet<int>();

        // 1st pass: explicit layout(location = N) or BindAttribLocation
        foreach (var input in vs.Inputs)
        {
            if (input.LayoutLocation is int explicitLoc)
            {
                if (!usedLocations.Add(explicitLoc))
                {
                    errorLog = $"Duplicate vertex attribute location {explicitLoc}.";
                    return false;
                }
                attribLocations[input.Name] = explicitLoc;
            }
            else if (prog.BoundAttribLocations.TryGetValue(input.Name, out var boundLoc))
            {
                if (usedLocations.Add((int)boundLoc))
                    attribLocations[input.Name] = (int)boundLoc;
            }
        }

        // 2nd pass: auto-assign remaining attributes
        var nextLoc = 0;
        foreach (var input in vs.Inputs)
        {
            if (attribLocations.ContainsKey(input.Name)) continue;
            while (usedLocations.Contains(nextLoc)) nextLoc++;
            if (nextLoc >= FolioNativeWebGlContext.MaxVertexAttribs)
            {
                errorLog = "Exceeded maximum vertex attributes.";
                return false;
            }
            usedLocations.Add(nextLoc);
            attribLocations[input.Name] = nextLoc;
        }

        foreach (var input in vs.Inputs)
        {
            TryMapGlslType(input.GlslType, out var glType, out _, out var isSampler, out _, out var isInt, out var isUInt);
            if (isSampler)
            {
                errorLog = "Vertex attributes cannot be samplers.";
                return false;
            }
            activeAttribs.Add(new FolioNativeWebGlContext.ProgramAttribSlot
            {
                Location = attribLocations[input.Name],
                Name = input.Name,
                GlType = glType,
                Size = Math.Max(1, input.ArraySize),
                IsInteger = isInt,
                IsUnsigned = isUInt,
            });
        }

        // Transform feedback varyings validation
        var linkedTf = new List<WebGlActiveInfo>();
        foreach (var tfName in prog.TransformFeedbackVaryings)
        {
            var vsOut = vs.Outputs.Find(o => o.Name == tfName);
            if (vsOut is null)
            {
                errorLog = $"Transform feedback varying '{tfName}' was not found in vertex shader outputs.";
                return false;
            }
            TryMapGlslType(vsOut.GlslType, out var glType, out _, out _, out _, out _, out _);
            linkedTf.Add(new WebGlActiveInfo(tfName, Math.Max(1, vsOut.ArraySize), glType));
        }

        // Assign uniform locations and constant buffer offsets
        var activeUniforms = new List<FolioNativeWebGlContext.ProgramUniformSlot>();
        var uniformLocations = new Dictionary<int, (FolioNativeWebGlContext.ProgramUniformSlot Slot, int ElementIndex)>();
        var uniformNameToLoc = new Dictionary<string, int>(StringComparer.Ordinal);
        var samplers = new List<FolioNativeWebGlContext.ProgramSamplerSlot>();
        var nextUniformLoc = 0;
        var cbFloatOffset = 0; // Each element or matrix column uses 4 floats (1 float4 register)
        var samplerReg = 0;

        foreach (var u in mergedUniformDecls)
        {
            TryMapGlslType(u.GlslType, out var glType, out var comps, out var isSampler, out var isMatrix, out var isInt, out var isUInt);
            var arrSize = Math.Max(1, u.ArraySize);
            var baseLoc = nextUniformLoc;
            nextUniformLoc += arrSize;

            var floatRegsPerElement = isMatrix ? (comps == 4 ? 2 : comps == 9 ? 3 : 4) : 1;
            var thisCbOffset = isSampler ? 0 : cbFloatOffset;
            if (!isSampler)
                cbFloatOffset += arrSize * floatRegsPerElement * 4;

            var slot = new FolioNativeWebGlContext.ProgramUniformSlot
            {
                Location = baseLoc,
                Name = u.ArraySize > 0 ? $"{u.Name}[0]" : u.Name,
                BaseName = u.Name,
                GlType = glType,
                ArraySize = arrSize,
                Components = comps,
                IsSampler = isSampler,
                IsMatrix = isMatrix,
                IsInteger = isInt,
                IsUnsigned = isUInt,
                ConstantBufferFloatOffset = thisCbOffset,
                FloatValues = new float[arrSize * comps],
                IntValues = new int[arrSize * comps],
                UIntValues = new uint[arrSize * comps],
            };
            activeUniforms.Add(slot);

            uniformNameToLoc[u.Name] = baseLoc;
            for (var e = 0; e < arrSize; e++)
            {
                var loc = baseLoc + e;
                uniformLocations[loc] = (slot, e);
                if (u.ArraySize > 0)
                    uniformNameToLoc[$"{u.Name}[{e}]"] = loc;
            }

            if (isSampler)
            {
                samplers.Add(new FolioNativeWebGlContext.ProgramSamplerSlot
                {
                    Name = u.Name,
                    GlType = glType,
                    RegisterSlot = samplerReg++,
                    UniformLocation = baseLoc,
                });
            }
        }

        // Uniform blocks
        var mergedBlocks = new List<FolioNativeWebGlContext.ProgramUniformBlockSlot>();
        var allBlockDecls = new List<FolioNativeWebGlContext.GlslUniformBlockDecl>();
        allBlockDecls.AddRange(vs.UniformBlocks);
        foreach (var fb in fs.UniformBlocks)
        {
            if (!allBlockDecls.Exists(b => b.BlockName == fb.BlockName))
                allBlockDecls.Add(fb);
        }
        for (var i = 0; i < allBlockDecls.Count; i++)
        {
            var b = allBlockDecls[i];
            var memberByteOffset = 0;
            foreach (var m in b.Members)
            {
                TryMapGlslType(m.GlslType, out var mGlType, out var mComps, out _, out var mIsMat, out var mIsInt, out var mIsUInt);
                var mArrSize = Math.Max(1, m.ArraySize);
                var alignBytes = mComps switch { 1 => 4, 2 => 8, _ => 16 };
                if (mArrSize > 1 || mIsMat) alignBytes = 16;
                memberByteOffset = ((memberByteOffset + alignBytes - 1) / alignBytes) * alignBytes;
                activeUniforms.Add(new FolioNativeWebGlContext.ProgramUniformSlot
                {
                    Location = -1,
                    Name = b.InstanceName is not null ? $"{b.BlockName}.{m.Name}" : m.Name,
                    BaseName = m.Name,
                    GlType = mGlType,
                    ArraySize = mArrSize,
                    Components = mComps,
                    IsSampler = false,
                    IsMatrix = mIsMat,
                    IsInteger = mIsInt,
                    IsUnsigned = mIsUInt,
                    ConstantBufferFloatOffset = -1,
                    BlockIndex = i,
                    ByteOffset = memberByteOffset,
                    FloatValues = [],
                    IntValues = [],
                    UIntValues = [],
                });
                var elemBytes = (mArrSize > 1 || mIsMat) ? 16 * (mIsMat ? (mComps == 4 ? 2 : mComps == 9 ? 3 : 4) : 1) : mComps * 4;
                memberByteOffset += mArrSize * elemBytes;
            }
            mergedBlocks.Add(new FolioNativeWebGlContext.ProgramUniformBlockSlot
            {
                BlockIndex = (uint)i,
                BlockName = b.BlockName,
                InstanceName = b.InstanceName,
                RegisterSlot = i + 1, // b1, b2, ...
                ByteSize = Math.Max(16, ((memberByteOffset + 15) / 16) * 16),
                Members = b.Members,
                BindingPoint = 0,
            });
        }

        // Generate paired VS and PS HLSL and compile to DXBC
        var vsHlsl = GenerateProgramHlsl(vs, fs, activeAttribs, activeUniforms, mergedBlocks, samplers, isVertex: true);
        var psHlsl = GenerateProgramHlsl(vs, fs, activeAttribs, activeUniforms, mergedBlocks, samplers, isVertex: false);

        if (!TryCompileHlsl(vsHlsl, "VSMain", "vs_5_0", out var vsBytes, out var vsErr))
        {
            errorLog = $"Vertex shader HLSL link failed: {vsErr}";
            return false;
        }
        if (!TryCompileHlsl(psHlsl, "PSMain", "ps_5_0", out var psBytes, out var psErr))
        {
            errorLog = $"Fragment shader HLSL link failed: {psErr}";
            return false;
        }

        var hrVs = createVs(devicePtr, vsBytes, (UIntPtr)vsBytes.Length, IntPtr.Zero, out var d3dVs);
        if (hrVs < 0 || d3dVs == IntPtr.Zero)
        {
            errorLog = $"CreateVertexShader failed with HRESULT 0x{hrVs:X8}.";
            return false;
        }

        var hrPs = createPs(devicePtr, psBytes, (UIntPtr)psBytes.Length, IntPtr.Zero, out var d3dPs);
        if (hrPs < 0 || d3dPs == IntPtr.Zero)
        {
            Marshal.Release(d3dVs);
            errorLog = $"CreatePixelShader failed with HRESULT 0x{hrPs:X8}.";
            return false;
        }

        prog.ReleaseCompiledObjects();
        prog.VertexShader = d3dVs;
        prog.PixelShader = d3dPs;
        prog.VertexShaderBytecode = vsBytes;
        prog.PixelShaderBytecode = psBytes;
        prog.LinkedVertexShader = vs;
        prog.LinkedFragmentShader = fs;

        prog.ActiveAttribs.Clear();
        prog.ActiveAttribs.AddRange(activeAttribs);
        prog.AttribLocations.Clear();
        foreach (var kv in attribLocations) prog.AttribLocations[kv.Key] = kv.Value;

        prog.ActiveUniforms.Clear();
        prog.ActiveUniforms.AddRange(activeUniforms);
        prog.UniformLocations.Clear();
        foreach (var kv in uniformLocations) prog.UniformLocations[kv.Key] = kv.Value;
        prog.UniformNameToLocation.Clear();
        foreach (var kv in uniformNameToLoc) prog.UniformNameToLocation[kv.Key] = kv.Value;

        prog.ActiveUniformBlocks.Clear();
        prog.ActiveUniformBlocks.AddRange(mergedBlocks);
        prog.Samplers.Clear();
        prog.Samplers.AddRange(samplers);
        prog.LinkedTfVaryings.Clear();
        prog.LinkedTfVaryings.AddRange(linkedTf);

        prog.ConstantBufferFloats = new float[Math.Max(256, ((cbFloatOffset + 63) / 64) * 64)];
        return true;
    }

    internal static bool TryMapGlslType(
        string glslType,
        out uint glEnum,
        out int components,
        out bool isSampler,
        out bool isMatrix,
        out bool isInteger,
        out bool isUnsigned)
    {
        isSampler = false;
        isMatrix = false;
        isInteger = false;
        isUnsigned = false;
        switch (glslType)
        {
            case "float": glEnum = FolioNativeWebGlContext.GlFloat; components = 1; return true;
            case "vec2": glEnum = FolioNativeWebGlContext.GlFloatVec2; components = 2; return true;
            case "vec3": glEnum = FolioNativeWebGlContext.GlFloatVec3; components = 3; return true;
            case "vec4": glEnum = FolioNativeWebGlContext.GlFloatVec4; components = 4; return true;
            case "int": glEnum = FolioNativeWebGlContext.GlInt; components = 1; isInteger = true; return true;
            case "ivec2": glEnum = FolioNativeWebGlContext.GlIntVec2; components = 2; isInteger = true; return true;
            case "ivec3": glEnum = FolioNativeWebGlContext.GlIntVec3; components = 3; isInteger = true; return true;
            case "ivec4": glEnum = FolioNativeWebGlContext.GlIntVec4; components = 4; isInteger = true; return true;
            case "uint": glEnum = FolioNativeWebGlContext.GlUnsignedInt; components = 1; isUnsigned = true; return true;
            case "uvec2": glEnum = FolioNativeWebGlContext.GlUnsignedIntVec2; components = 2; isUnsigned = true; return true;
            case "uvec3": glEnum = FolioNativeWebGlContext.GlUnsignedIntVec3; components = 3; isUnsigned = true; return true;
            case "uvec4": glEnum = FolioNativeWebGlContext.GlUnsignedIntVec4; components = 4; isUnsigned = true; return true;
            case "bool": glEnum = FolioNativeWebGlContext.GlBool; components = 1; isInteger = true; return true;
            case "bvec2": glEnum = FolioNativeWebGlContext.GlBoolVec2; components = 2; isInteger = true; return true;
            case "bvec3": glEnum = FolioNativeWebGlContext.GlBoolVec3; components = 3; isInteger = true; return true;
            case "bvec4": glEnum = FolioNativeWebGlContext.GlBoolVec4; components = 4; isInteger = true; return true;
            case "mat2": glEnum = FolioNativeWebGlContext.GlFloatMat2; components = 4; isMatrix = true; return true;
            case "mat3": glEnum = FolioNativeWebGlContext.GlFloatMat3; components = 9; isMatrix = true; return true;
            case "mat4": glEnum = FolioNativeWebGlContext.GlFloatMat4; components = 16; isMatrix = true; return true;
            case "sampler2D": glEnum = FolioNativeWebGlContext.GlSampler2D; components = 1; isSampler = true; return true;
            case "sampler3D": glEnum = FolioNativeWebGlContext.GlSampler3D; components = 1; isSampler = true; return true;
            case "samplerCube": glEnum = FolioNativeWebGlContext.GlSamplerCube; components = 1; isSampler = true; return true;
            case "sampler2DShadow": glEnum = FolioNativeWebGlContext.GlSampler2DShadow; components = 1; isSampler = true; return true;
            case "sampler2DArray": glEnum = FolioNativeWebGlContext.GlSampler2DArray; components = 1; isSampler = true; return true;
            case "sampler2DArrayShadow": glEnum = FolioNativeWebGlContext.GlSampler2DArrayShadow; components = 1; isSampler = true; return true;
            case "samplerCubeShadow": glEnum = FolioNativeWebGlContext.GlSamplerCubeShadow; components = 1; isSampler = true; return true;
            case "isampler2D": glEnum = FolioNativeWebGlContext.GlIntSampler2D; components = 1; isSampler = true; isInteger = true; return true;
            case "isampler3D": glEnum = FolioNativeWebGlContext.GlIntSampler3D; components = 1; isSampler = true; isInteger = true; return true;
            case "isamplerCube": glEnum = FolioNativeWebGlContext.GlIntSamplerCube; components = 1; isSampler = true; isInteger = true; return true;
            case "isampler2DArray": glEnum = FolioNativeWebGlContext.GlIntSampler2DArray; components = 1; isSampler = true; isInteger = true; return true;
            case "usampler2D": glEnum = FolioNativeWebGlContext.GlUnsignedIntSampler2D; components = 1; isSampler = true; isUnsigned = true; return true;
            case "usampler3D": glEnum = FolioNativeWebGlContext.GlUnsignedIntSampler3D; components = 1; isSampler = true; isUnsigned = true; return true;
            case "usamplerCube": glEnum = FolioNativeWebGlContext.GlUnsignedIntSamplerCube; components = 1; isSampler = true; isUnsigned = true; return true;
            case "usampler2DArray": glEnum = FolioNativeWebGlContext.GlUnsignedIntSampler2DArray; components = 1; isSampler = true; isUnsigned = true; return true;
            default:
                glEnum = 0;
                components = 0;
                return false;
        }
    }

    internal static string MapGlslTypeToHlsl(string glslType) => glslType switch
    {
        "float" => "float",
        "vec2" => "float2",
        "vec3" => "float3",
        "vec4" => "float4",
        "int" => "int",
        "ivec2" => "int2",
        "ivec3" => "int3",
        "ivec4" => "int4",
        "uint" => "uint",
        "uvec2" => "uint2",
        "uvec3" => "uint3",
        "uvec4" => "uint4",
        "bool" => "bool",
        "bvec2" => "bool2",
        "bvec3" => "bool3",
        "bvec4" => "bool4",
        "mat2" => "row_major float2x2",
        "mat3" => "row_major float3x3",
        "mat4" => "row_major float4x4",
        _ => glslType,
    };

    private static string BuildStandaloneHlslForValidation(FolioNativeWebGlContext.GlslParsedShader parsed)
    {
        var attribs = new List<FolioNativeWebGlContext.ProgramAttribSlot>();
        var uniforms = new List<FolioNativeWebGlContext.ProgramUniformSlot>();
        var samplers = new List<FolioNativeWebGlContext.ProgramSamplerSlot>();
        var blocks = new List<FolioNativeWebGlContext.ProgramUniformBlockSlot>();

        if (parsed.ShaderType == FolioNativeWebGlContext.GlVertexShader)
        {
            for (var i = 0; i < parsed.Inputs.Count; i++)
            {
                var inp = parsed.Inputs[i];
                TryMapGlslType(inp.GlslType, out var glt, out _, out _, out _, out var isI, out var isU);
                attribs.Add(new FolioNativeWebGlContext.ProgramAttribSlot
                {
                    Location = inp.LayoutLocation ?? i,
                    Name = inp.Name,
                    GlType = glt,
                    Size = Math.Max(1, inp.ArraySize),
                    IsInteger = isI,
                    IsUnsigned = isU,
                });
            }
        }

        var cbOff = 0;
        var sReg = 0;
        for (var i = 0; i < parsed.Uniforms.Count; i++)
        {
            var u = parsed.Uniforms[i];
            TryMapGlslType(u.GlslType, out var glt, out var comps, out var isSamp, out var isMat, out var isI, out var isU);
            var arr = Math.Max(1, u.ArraySize);
            uniforms.Add(new FolioNativeWebGlContext.ProgramUniformSlot
            {
                Location = i,
                Name = u.Name,
                BaseName = u.Name,
                GlType = glt,
                ArraySize = arr,
                Components = comps,
                IsSampler = isSamp,
                IsMatrix = isMat,
                IsInteger = isI,
                IsUnsigned = isU,
                ConstantBufferFloatOffset = cbOff,
                FloatValues = [],
                IntValues = [],
                UIntValues = [],
            });
            if (isSamp)
            {
                samplers.Add(new FolioNativeWebGlContext.ProgramSamplerSlot
                {
                    Name = u.Name,
                    GlType = glt,
                    RegisterSlot = sReg++,
                    UniformLocation = i,
                });
            }
            else
            {
                cbOff += arr * (isMat ? 16 : 4);
            }
        }

        for (var i = 0; i < parsed.UniformBlocks.Count; i++)
        {
            var b = parsed.UniformBlocks[i];
            blocks.Add(new FolioNativeWebGlContext.ProgramUniformBlockSlot
            {
                BlockIndex = (uint)i,
                BlockName = b.BlockName,
                InstanceName = b.InstanceName,
                RegisterSlot = i + 1,
                ByteSize = 64,
                Members = b.Members,
            });
        }

        var dummyVs = parsed.ShaderType == FolioNativeWebGlContext.GlVertexShader
            ? parsed
            : new FolioNativeWebGlContext.GlslParsedShader
            {
                ShaderType = FolioNativeWebGlContext.GlVertexShader,
                Version = parsed.Version,
                Inputs = [],
                Outputs = parsed.Inputs,
                Uniforms = [],
                UniformBlocks = [],
                MainBody = "gl_Position = vec4(0.0, 0.0, 0.0, 1.0);",
                HelperFunctions = string.Empty,
            };

        var dummyFs = parsed.ShaderType == FolioNativeWebGlContext.GlFragmentShader
            ? parsed
            : new FolioNativeWebGlContext.GlslParsedShader
            {
                ShaderType = FolioNativeWebGlContext.GlFragmentShader,
                Version = parsed.Version,
                Inputs = parsed.Outputs,
                Outputs = [new FolioNativeWebGlContext.GlslVariableDecl { Storage = "out", GlslType = "vec4", Name = "_outColor" }],
                Uniforms = [],
                UniformBlocks = [],
                MainBody = "_outColor = vec4(1.0, 1.0, 1.0, 1.0);",
                HelperFunctions = string.Empty,
            };

        return GenerateProgramHlsl(
            dummyVs,
            dummyFs,
            attribs,
            uniforms,
            blocks,
            samplers,
            isVertex: parsed.ShaderType == FolioNativeWebGlContext.GlVertexShader);
    }

    internal static string GenerateProgramHlsl(
        FolioNativeWebGlContext.GlslParsedShader vs,
        FolioNativeWebGlContext.GlslParsedShader fs,
        List<FolioNativeWebGlContext.ProgramAttribSlot> attribs,
        List<FolioNativeWebGlContext.ProgramUniformSlot> uniforms,
        List<FolioNativeWebGlContext.ProgramUniformBlockSlot> uniformBlocks,
        List<FolioNativeWebGlContext.ProgramSamplerSlot> samplers,
        bool isVertex)
    {
        var sb = new StringBuilder();

        // Standard GLSL -> HLSL intrinsic preamble
        sb.AppendLine("""
            float2 _f_vec2(float x, float y) { return float2(x, y); }

            float3 _f_vec3(float x, float y, float z) { return float3(x, y, z); }
            float3 _f_vec3(float2 xy, float z) { return float3(xy.x, xy.y, z); }
            float3 _f_vec3(float x, float2 yz) { return float3(x, yz.x, yz.y); }

            float4 _f_vec4(float x, float y, float z, float w) { return float4(x, y, z, w); }
            float4 _f_vec4(float2 xy, float z, float w) { return float4(xy.x, xy.y, z, w); }
            float4 _f_vec4(float x, float2 yz, float w) { return float4(x, yz.x, yz.y, w); }
            float4 _f_vec4(float x, float y, float2 zw) { return float4(x, y, zw.x, zw.y); }
            float4 _f_vec4(float2 xy, float2 zw) { return float4(xy.x, xy.y, zw.x, zw.y); }
            float4 _f_vec4(float3 xyz, float w) { return float4(xyz.x, xyz.y, xyz.z, w); }
            float4 _f_vec4(float x, float3 yzw) { return float4(x, yzw.x, yzw.y, yzw.z); }

            int2 _f_ivec2(int x, int y) { return int2(x, y); }

            int3 _f_ivec3(int x, int y, int z) { return int3(x, y, z); }
            int3 _f_ivec3(int2 xy, int z) { return int3(xy.x, xy.y, z); }
            int3 _f_ivec3(int x, int2 yz) { return int3(x, yz.x, yz.y); }

            int4 _f_ivec4(int x, int y, int z, int w) { return int4(x, y, z, w); }
            int4 _f_ivec4(int2 xy, int z, int w) { return int4(xy.x, xy.y, z, w); }
            int4 _f_ivec4(int3 xyz, int w) { return int4(xyz.x, xyz.y, xyz.z, w); }

            uint2 _f_uvec2(uint x, uint y) { return uint2(x, y); }

            uint3 _f_uvec3(uint x, uint y, uint z) { return uint3(x, y, z); }
            uint3 _f_uvec3(uint2 xy, uint z) { return uint3(xy.x, xy.y, z); }
            uint3 _f_uvec3(uint x, uint2 yz) { return uint3(x, yz.x, yz.y); }

            uint4 _f_uvec4(uint x, uint y, uint z, uint w) { return uint4(x, y, z, w); }
            uint4 _f_uvec4(uint2 xy, uint z, uint w) { return uint4(xy.x, xy.y, z, w); }
            uint4 _f_uvec4(uint3 xyz, uint w) { return uint4(xyz.x, xyz.y, xyz.z, w); }

            bool2 _f_bvec2(bool x, bool y) { return bool2(x, y); }
            bool3 _f_bvec3(bool x, bool y, bool z) { return bool3(x, y, z); }
            bool4 _f_bvec4(bool x, bool y, bool z, bool w) { return bool4(x, y, z, w); }

            float2x2 _f_mat2(float a, float b, float c, float d) { return float2x2(a, b, c, d); }
            float2x2 _f_mat2(float2 c0, float2 c1) { return float2x2(c0, c1); }
            float3x3 _f_mat3(float3 c0, float3 c1, float3 c2) { return float3x3(c0, c1, c2); }
            float4x4 _f_mat4(float4 c0, float4 c1, float4 c2, float4 c3) { return float4x4(c0, c1, c2, c3); }

            float _f_mod(float x, float y) { return x - y * floor(x / y); }
            float2 _f_mod(float2 x, float y) { return x - y * floor(x / y); }
            float3 _f_mod(float3 x, float y) { return x - y * floor(x / y); }
            float4 _f_mod(float4 x, float y) { return x - y * floor(x / y); }
            float2 _f_mod(float2 x, float2 y) { return x - y * floor(x / y); }
            float3 _f_mod(float3 x, float3 y) { return x - y * floor(x / y); }
            float4 _f_mod(float4 x, float4 y) { return x - y * floor(x / y); }

            float _f_shadow2D(Texture2D<float4> t, SamplerState s, float3 uvc) {
                float d = t.SampleLevel(s, uvc.xy, 0.0).r;
                return (uvc.z <= d + 1e-5) ? 1.0 : 0.0;
            }
            uint4 _f_sample_u2D(Texture2D<uint4> t, float2 uv) {
                uint w, h; t.GetDimensions(w, h);
                int2 c = clamp(int2(frac(uv) * float2(w, h)), int2(0, 0), int2(max(int(w) - 1, 0), max(int(h) - 1, 0)));
                return t.Load(int3(c, 0));
            }
            int4 _f_sample_i2D(Texture2D<int4> t, float2 uv) {
                uint w, h; t.GetDimensions(w, h);
                int2 c = clamp(int2(frac(uv) * float2(w, h)), int2(0, 0), int2(max(int(w) - 1, 0), max(int(h) - 1, 0)));
                return t.Load(int3(c, 0));
            }
            uint4 _f_sample_u3D(Texture3D<uint4> t, float3 uvw) {
                uint w, h, d; t.GetDimensions(w, h, d);
                int3 c = clamp(int3(frac(uvw) * float3(w, h, d)), int3(0, 0, 0), int3(max(int(w) - 1, 0), max(int(h) - 1, 0), max(int(d) - 1, 0)));
                return t.Load(int4(c, 0));
            }
            int4 _f_sample_i3D(Texture3D<int4> t, float3 uvw) {
                uint w, h, d; t.GetDimensions(w, h, d);
                int3 c = clamp(int3(frac(uvw) * float3(w, h, d)), int3(0, 0, 0), int3(max(int(w) - 1, 0), max(int(h) - 1, 0), max(int(d) - 1, 0)));
                return t.Load(int4(c, 0));
            }
            uint4 _f_sample_u2DArray(Texture2DArray<uint4> t, float3 uvl) {
                uint w, h, elems; t.GetDimensions(w, h, elems);
                int2 c = clamp(int2(frac(uvl.xy) * float2(w, h)), int2(0, 0), int2(max(int(w) - 1, 0), max(int(h) - 1, 0)));
                int layer = clamp(int(round(uvl.z)), 0, max(int(elems) - 1, 0));
                return t.Load(int4(c, layer, 0));
            }
            int4 _f_sample_i2DArray(Texture2DArray<int4> t, float3 uvl) {
                uint w, h, elems; t.GetDimensions(w, h, elems);
                int2 c = clamp(int2(frac(uvl.xy) * float2(w, h)), int2(0, 0), int2(max(int(w) - 1, 0), max(int(h) - 1, 0)));
                int layer = clamp(int(round(uvl.z)), 0, max(int(elems) - 1, 0));
                return t.Load(int4(c, layer, 0));
            }
            """);

        // Constant buffer b0 for default-block uniforms
        sb.AppendLine("cbuffer _FolioUniforms : register(b0)");
        sb.AppendLine("{");
        var hasAnyNonSamplerUniform = false;
        foreach (var u in uniforms)
        {
            if (u.IsSampler || u.BlockIndex >= 0) continue;
            hasAnyNonSamplerUniform = true;
            var shaderDecl = (isVertex ? vs : fs).Uniforms.Find(d => d.Name == u.BaseName)
                ?? vs.Uniforms.Find(d => d.Name == u.BaseName)
                ?? fs.Uniforms.Find(d => d.Name == u.BaseName)!;
            var hlslType = MapGlslTypeToHlsl(shaderDecl.GlslType);
            var regIndex = u.ConstantBufferFloatOffset / 4;
            if (u.ArraySize > 1)
                sb.AppendLine($"    {hlslType} {u.BaseName}[{u.ArraySize}] : packoffset(c{regIndex});");
            else
                sb.AppendLine($"    {hlslType} {u.BaseName} : packoffset(c{regIndex});");
        }
        if (!hasAnyNonSamplerUniform)
            sb.AppendLine("    float4 _folioDummyUniform : packoffset(c0);");
        sb.AppendLine("};");

        // Uniform blocks b1, b2, ...
        foreach (var block in uniformBlocks)
        {
            if (block.InstanceName is { } instName)
            {
                sb.AppendLine($"struct _BlockStruct_{block.BlockName}");
                sb.AppendLine("{");
                foreach (var m in block.Members)
                {
                    var mType = MapGlslTypeToHlsl(m.GlslType);
                    var arr = m.ArraySize > 0 ? $"[{m.ArraySize}]" : string.Empty;
                    sb.AppendLine($"    {mType} {m.Name}{arr};");
                }
                sb.AppendLine("};");
                sb.AppendLine($"cbuffer _Ubo_{block.BlockName} : register(b{block.RegisterSlot})");
                sb.AppendLine("{");
                sb.AppendLine($"    _BlockStruct_{block.BlockName} {instName};");
                sb.AppendLine("};");
            }
            else
            {
                sb.AppendLine($"cbuffer _Ubo_{block.BlockName} : register(b{block.RegisterSlot})");
                sb.AppendLine("{");
                foreach (var m in block.Members)
                {
                    var mType = MapGlslTypeToHlsl(m.GlslType);
                    var arr = m.ArraySize > 0 ? $"[{m.ArraySize}]" : string.Empty;
                    sb.AppendLine($"    {mType} {m.Name}{arr};");
                }
                sb.AppendLine("};");
            }
        }

        // Samplers and Textures
        foreach (var s in samplers)
        {
            var texType = s.GlType switch
            {
                FolioNativeWebGlContext.GlSampler3D => "Texture3D<float4>",
                FolioNativeWebGlContext.GlIntSampler3D => "Texture3D<int4>",
                FolioNativeWebGlContext.GlUnsignedIntSampler3D => "Texture3D<uint4>",
                FolioNativeWebGlContext.GlSamplerCube or FolioNativeWebGlContext.GlSamplerCubeShadow => "TextureCube<float4>",
                FolioNativeWebGlContext.GlIntSamplerCube => "TextureCube<int4>",
                FolioNativeWebGlContext.GlUnsignedIntSamplerCube => "TextureCube<uint4>",
                FolioNativeWebGlContext.GlSampler2DArray or FolioNativeWebGlContext.GlSampler2DArrayShadow => "Texture2DArray<float4>",
                FolioNativeWebGlContext.GlIntSampler2DArray => "Texture2DArray<int4>",
                FolioNativeWebGlContext.GlUnsignedIntSampler2DArray => "Texture2DArray<uint4>",
                FolioNativeWebGlContext.GlIntSampler2D => "Texture2D<int4>",
                FolioNativeWebGlContext.GlUnsignedIntSampler2D => "Texture2D<uint4>",
                _ => "Texture2D<float4>",
            };
            sb.AppendLine($"{texType} _tex_{s.Name} : register(t{s.RegisterSlot});");
            sb.AppendLine($"SamplerState _samp_{s.Name} : register(s{s.RegisterSlot});");
        }

        var activeShader = isVertex ? vs : fs;

        // Global static variables representing shader inputs/outputs so main() and helper functions can read/write them directly
        if (isVertex)
        {
            sb.AppendLine("static float4 gl_Position = float4(0.0, 0.0, 0.0, 1.0);");
            sb.AppendLine("static float gl_PointSize = 1.0;");
            sb.AppendLine("static int gl_VertexID = 0;");
            sb.AppendLine("static int gl_InstanceID = 0;");
            foreach (var inp in vs.Inputs)
                sb.AppendLine($"static {MapGlslTypeToHlsl(inp.GlslType)} {inp.Name};");
            foreach (var outp in vs.Outputs)
                sb.AppendLine($"static {MapGlslTypeToHlsl(outp.GlslType)} {outp.Name};");
        }
        else
        {
            sb.AppendLine("static float4 gl_FragCoord = float4(0.0, 0.0, 0.0, 1.0);");
            sb.AppendLine("static float2 gl_PointCoord = float2(0.0, 0.0);");
            sb.AppendLine("static bool gl_FrontFacing = true;");
            sb.AppendLine("static float4 gl_FragColor = float4(0.0, 0.0, 0.0, 0.0);");
            foreach (var inp in fs.Inputs)
                sb.AppendLine($"static {MapGlslTypeToHlsl(inp.GlslType)} {inp.Name};");
            foreach (var outp in fs.Outputs)
                sb.AppendLine($"static {MapGlslTypeToHlsl(outp.GlslType)} {outp.Name};");
        }

        // Helper functions
        if (!string.IsNullOrWhiteSpace(activeShader.HelperFunctions))
            sb.AppendLine(TranslateGlslBody(activeShader.HelperFunctions, vs, fs, samplers, isVertex));

        // Translated user main
        sb.AppendLine("void _folio_user_main()");
        sb.AppendLine("{");
        sb.AppendLine(TranslateGlslBody(activeShader.MainBody, vs, fs, samplers, isVertex));
        sb.AppendLine("}");

        if (isVertex)
        {
            sb.AppendLine("struct VSInput");
            sb.AppendLine("{");
            foreach (var inp in vs.Inputs)
            {
                var slot = attribs.Find(a => a.Name == inp.Name)!;
                sb.AppendLine($"    {MapGlslTypeToHlsl(inp.GlslType)} {inp.Name} : ATTRIB{slot.Location};");
            }
            sb.AppendLine("    uint _vertexId : SV_VertexID;");
            sb.AppendLine("    uint _instanceId : SV_InstanceID;");
            sb.AppendLine("};");

            sb.AppendLine("struct VSOutput");
            sb.AppendLine("{");
            sb.AppendLine("    float4 _pos : SV_Position;");
            for (var i = 0; i < vs.Outputs.Count; i++)
            {
                var outp = vs.Outputs[i];
                var interp = outp.IsFlat || outp.GlslType.StartsWith('i') || outp.GlslType.StartsWith('u') ? "nointerpolation " : string.Empty;
                sb.AppendLine($"    {interp}{MapGlslTypeToHlsl(outp.GlslType)} {outp.Name} : TEXCOORD{i};");
            }
            sb.AppendLine("};");

            sb.AppendLine("VSOutput VSMain(VSInput _in)");
            sb.AppendLine("{");
            if (vs.Outputs.Exists(o => o.IsFlat))
                sb.AppendLine("    gl_VertexID = (int)((_in._vertexId / 3u) * 3u + (_in._vertexId + 2u) % 3u);");
            else
                sb.AppendLine("    gl_VertexID = (int)_in._vertexId;");
            sb.AppendLine("    gl_InstanceID = (int)_in._instanceId;");
            foreach (var inp in vs.Inputs)
                sb.AppendLine($"    {inp.Name} = _in.{inp.Name};");
            sb.AppendLine("    _folio_user_main();");
            sb.AppendLine("    VSOutput _out;");
            // Negate gl_Position.y so WebGL y=-1 (bottom) maps to D3D11 row 0 (top), and map z from [-w, w] to [0, w]
            sb.AppendLine("    float4 _p = gl_Position;");
            sb.AppendLine("    _p.y = -_p.y;");
            sb.AppendLine("    _p.z = (_p.z + _p.w) * 0.5f;");
            sb.AppendLine("    _out._pos = _p;");
            foreach (var outp in vs.Outputs)
                sb.AppendLine($"    _out.{outp.Name} = {outp.Name};");
            sb.AppendLine("    return _out;");
            sb.AppendLine("}");
        }
        else
        {
            sb.AppendLine("struct PSInput");
            sb.AppendLine("{");
            sb.AppendLine("    float4 _pos : SV_Position;");
            for (var i = 0; i < vs.Outputs.Count; i++)
            {
                var outp = vs.Outputs[i];
                var interp = outp.IsFlat || outp.GlslType.StartsWith('i') || outp.GlslType.StartsWith('u') ? "nointerpolation " : string.Empty;
                sb.AppendLine($"    {interp}{MapGlslTypeToHlsl(outp.GlslType)} {outp.Name} : TEXCOORD{i};");
            }
            sb.AppendLine("};");

            sb.AppendLine("struct PSOutput");
            sb.AppendLine("{");
            if (fs.Outputs.Count == 0)
            {
                sb.AppendLine("    float4 _color0 : SV_Target0;");
            }
            else
            {
                for (var i = 0; i < fs.Outputs.Count; i++)
                {
                    var outp = fs.Outputs[i];
                    var targetIdx = outp.LayoutLocation ?? i;
                    sb.AppendLine($"    {MapGlslTypeToHlsl(outp.GlslType)} {outp.Name} : SV_Target{targetIdx};");
                }
            }
            sb.AppendLine("};");

            sb.AppendLine("PSOutput PSMain(PSInput _in)");
            sb.AppendLine("{");
            sb.AppendLine("    gl_FragCoord = float4(_in._pos.x, _in._pos.y, _in._pos.z, 1.0 / max(_in._pos.w, 1e-6));");
            foreach (var inp in fs.Inputs)
                sb.AppendLine($"    {inp.Name} = _in.{inp.Name};");
            sb.AppendLine("    _folio_user_main();");
            sb.AppendLine("    PSOutput _out;");
            if (fs.Outputs.Count == 0)
            {
                sb.AppendLine("    _out._color0 = gl_FragColor;");
            }
            else
            {
                foreach (var outp in fs.Outputs)
                    sb.AppendLine($"    _out.{outp.Name} = {outp.Name};");
            }
            sb.AppendLine("    return _out;");
            sb.AppendLine("}");
        }

        return sb.ToString();
    }

    private static string TranslateGlslBody(
        string code,
        FolioNativeWebGlContext.GlslParsedShader vs,
        FolioNativeWebGlContext.GlslParsedShader fs,
        List<FolioNativeWebGlContext.ProgramSamplerSlot> samplers,
        bool isVertex)
    {
        var s = code;

        // Strip precision qualifiers inside expressions/declarations
        s = Regex.Replace(s, @"\b(lowp|mediump|highp)\s+", string.Empty);

        // Replace texture functions:
        // texelFetch(samp, coord, lod) -> _tex_samp.Load(int3(coord, lod)) or int4(coord, lod) for 3D/2DArray
        s = Regex.Replace(
            s,
            @"\btexelFetch\s*\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*,",
            match =>
            {
                var sampName = match.Groups[1].Value;
                var samp = samplers.Find(x => x.Name == sampName);
                var is3DOrArray = samp is not null && samp.GlType is
                    FolioNativeWebGlContext.GlSampler3D or FolioNativeWebGlContext.GlIntSampler3D or FolioNativeWebGlContext.GlUnsignedIntSampler3D or
                    FolioNativeWebGlContext.GlSampler2DArray or FolioNativeWebGlContext.GlIntSampler2DArray or FolioNativeWebGlContext.GlUnsignedIntSampler2DArray;
                return is3DOrArray
                    ? $"_tex_{sampName}.Load(_f_ivec4("
                    : $"_tex_{sampName}.Load(_f_ivec3(";
            });
        // Close the extra paren for _tex_samp.Load(_f_ivec3(coord, lod))
        s = FixTexelFetchParens(s);

        // textureLod(samp, uv, lod) -> _tex_samp.SampleLevel(_samp_samp, uv, lod)
        s = Regex.Replace(
            s,
            @"\b(?:textureLod|texture2DLodEXT|textureCubeLodEXT)\s*\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*,",
            "_tex_$1.SampleLevel(_samp_$1,");

        // texture(samp, uv) / texture2D(samp, uv) / textureCube(samp, uv)
        s = Regex.Replace(
            s,
            @"\b(?:texture|texture2D|textureCube)\s*\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*,",
            match =>
            {
                var sampName = match.Groups[1].Value;
                var samp = samplers.Find(x => x.Name == sampName);
                if (samp is not null)
                {
                    switch (samp.GlType)
                    {
                        case FolioNativeWebGlContext.GlSampler2DShadow:
                            return $"_f_shadow2D(_tex_{sampName}, _samp_{sampName},";
                        case FolioNativeWebGlContext.GlUnsignedIntSampler2D:
                            return $"_f_sample_u2D(_tex_{sampName},";
                        case FolioNativeWebGlContext.GlIntSampler2D:
                            return $"_f_sample_i2D(_tex_{sampName},";
                        case FolioNativeWebGlContext.GlUnsignedIntSampler3D:
                            return $"_f_sample_u3D(_tex_{sampName},";
                        case FolioNativeWebGlContext.GlIntSampler3D:
                            return $"_f_sample_i3D(_tex_{sampName},";
                        case FolioNativeWebGlContext.GlUnsignedIntSampler2DArray:
                            return $"_f_sample_u2DArray(_tex_{sampName},";
                        case FolioNativeWebGlContext.GlIntSampler2DArray:
                            return $"_f_sample_i2DArray(_tex_{sampName},";
                    }
                }
                return isVertex
                    ? $"_tex_{sampName}.SampleLevel(_samp_{sampName},"
                    : $"_tex_{sampName}.Sample(_samp_{sampName},";
            });
        if (isVertex)
            s = FixVertexSampleCalls(s);

        // Derivatives & math intrinsics
        s = Regex.Replace(s, @"\bdFdx\b", "ddx");
        s = Regex.Replace(s, @"\bdFdy\b", "-ddy"); // Because Y is flipped in D3D11 RT, dFdy in GLSL corresponds to -ddy in D3D11
        s = Regex.Replace(s, @"\bfract\b", "frac");
        s = Regex.Replace(s, @"\bmix\b", "lerp");
        s = Regex.Replace(s, @"\binversesqrt\b", "rsqrt");
        s = Regex.Replace(s, @"\bmod\s*\(", "_f_mod(");
        s = Regex.Replace(s, @"\batan\s*\(\s*([^,()]+)\s*,\s*([^,()]+)\s*\)", "atan2($1, $2)");

        // Replace constructor calls vec2(...) -> _f_vec2(...) etc., and type declarations vec2 x -> float2 x
        s = Regex.Replace(s, @"\bvec2\s*\(", "_f_vec2(");
        s = Regex.Replace(s, @"\bvec3\s*\(", "_f_vec3(");
        s = Regex.Replace(s, @"\bvec4\s*\(", "_f_vec4(");
        s = Regex.Replace(s, @"\bivec2\s*\(", "_f_ivec2(");
        s = Regex.Replace(s, @"\bivec3\s*\(", "_f_ivec3(");
        s = Regex.Replace(s, @"\bivec4\s*\(", "_f_ivec4(");
        s = Regex.Replace(s, @"\buvec2\s*\(", "_f_uvec2(");
        s = Regex.Replace(s, @"\buvec3\s*\(", "_f_uvec3(");
        s = Regex.Replace(s, @"\buvec4\s*\(", "_f_uvec4(");
        s = Regex.Replace(s, @"\bbvec2\s*\(", "_f_bvec2(");
        s = Regex.Replace(s, @"\bbvec3\s*\(", "_f_bvec3(");
        s = Regex.Replace(s, @"\bbvec4\s*\(", "_f_bvec4(");
        s = Regex.Replace(s, @"\bmat2\s*\(", "_f_mat2(");
        s = Regex.Replace(s, @"\bmat3\s*\(", "_f_mat3(");
        s = Regex.Replace(s, @"\bmat4\s*\(", "_f_mat4(");
        s = RewriteSingleArgVectorConstructors(s);

        // Remaining type keywords
        s = Regex.Replace(s, @"\bvec2\b", "float2");
        s = Regex.Replace(s, @"\bvec3\b", "float3");
        s = Regex.Replace(s, @"\bvec4\b", "float4");
        s = Regex.Replace(s, @"\bivec2\b", "int2");
        s = Regex.Replace(s, @"\bivec3\b", "int3");
        s = Regex.Replace(s, @"\bivec4\b", "int4");
        s = Regex.Replace(s, @"\buvec2\b", "uint2");
        s = Regex.Replace(s, @"\buvec3\b", "uint3");
        s = Regex.Replace(s, @"\buvec4\b", "uint4");
        s = Regex.Replace(s, @"\bbvec2\b", "bool2");
        s = Regex.Replace(s, @"\bbvec3\b", "bool3");
        s = Regex.Replace(s, @"\bbvec4\b", "bool4");
        s = Regex.Replace(s, @"\bmat2\b", "float2x2");
        s = Regex.Replace(s, @"\bmat3\b", "float3x3");
        s = Regex.Replace(s, @"\bmat4\b", "float4x4");

        // Matrix * vector multiplication:
        // For any declared mat2/mat3/mat4 variable M, rewrite `M * expr` to `mul(expr, M)` (since M is stored column-major in `row_major floatNxN`, row i of HLSL matrix = column i of GLSL matrix, so `mul(v, M)` computes `v.x * col0 + v.y * col1 + ...` = GLSL `M * v`!).
        var matNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var u in vs.Uniforms) if (u.GlslType is "mat2" or "mat3" or "mat4") matNames.Add(u.Name);
        foreach (var u in fs.Uniforms) if (u.GlslType is "mat2" or "mat3" or "mat4") matNames.Add(u.Name);
        foreach (var b in vs.UniformBlocks) foreach (var m in b.Members) if (m.GlslType is "mat2" or "mat3" or "mat4") matNames.Add(m.Name);
        foreach (var b in fs.UniformBlocks) foreach (var m in b.Members) if (m.GlslType is "mat2" or "mat3" or "mat4") matNames.Add(m.Name);

        foreach (var mName in matNames)
        {
            s = RewriteMatrixMultiply(s, mName);
        }

        return s;
    }

    private static readonly (string Prefix, string HlslType)[] SingleArgVecPrefixes =
    [
        ("_f_vec2(", "float2"),
        ("_f_vec3(", "float3"),
        ("_f_vec4(", "float4"),
        ("_f_ivec2(", "int2"),
        ("_f_ivec3(", "int3"),
        ("_f_ivec4(", "int4"),
        ("_f_uvec2(", "uint2"),
        ("_f_uvec3(", "uint3"),
        ("_f_uvec4(", "uint4"),
        ("_f_bvec2(", "bool2"),
        ("_f_bvec3(", "bool3"),
        ("_f_bvec4(", "bool4"),
    ];

    private static string RewriteSingleArgVectorConstructors(string s)
    {
        var current = s;
        var changed = true;
        while (changed)
        {
            changed = false;
            var sb = new StringBuilder(current.Length + 32);
            var i = 0;
            while (i < current.Length)
            {
                string? matchedPrefix = null;
                string? hlslType = null;
                foreach (var (prefix, t) in SingleArgVecPrefixes)
                {
                    if (current.AsSpan(i).StartsWith(prefix, StringComparison.Ordinal) &&
                        (i == 0 || !char.IsLetterOrDigit(current[i - 1]) && current[i - 1] != '_'))
                    {
                        matchedPrefix = prefix;
                        hlslType = t;
                        break;
                    }
                }

                if (matchedPrefix is null)
                {
                    sb.Append(current[i++]);
                    continue;
                }

                var argStart = i + matchedPrefix.Length;
                var j = argStart;
                var depth = 1;
                var commas = 0;
                while (j < current.Length && depth > 0)
                {
                    var c = current[j++];
                    if (c == '(') depth++;
                    else if (c == ')') depth--;
                    else if (c == ',' && depth == 1) commas++;
                }

                if (depth == 0 && commas == 0)
                {
                    var inner = current[argStart..(j - 1)];
                    sb.Append("((").Append(hlslType).Append(")(").Append(inner).Append("))");
                    i = j;
                    changed = true;
                }
                else
                {
                    sb.Append(matchedPrefix);
                    i = argStart;
                }
            }
            current = sb.ToString();
        }
        return current;
    }

    private static string FixTexelFetchParens(string s)
    {
        const string marker1 = ".Load(_f_ivec3(";
        const string marker2 = ".Load(_f_ivec4(";
        var sb = new StringBuilder(s.Length + 32);
        var i = 0;
        while (i < s.Length)
        {
            var mLen = 0;
            if (s.AsSpan(i).StartsWith(marker1, StringComparison.Ordinal)) mLen = marker1.Length;
            else if (s.AsSpan(i).StartsWith(marker2, StringComparison.Ordinal)) mLen = marker2.Length;

            if (mLen == 0)
            {
                sb.Append(s[i++]);
                continue;
            }

            sb.Append(s, i, mLen);
            i += mLen;
            var depth = 1;
            while (i < s.Length && depth > 0)
            {
                var c = s[i++];
                if (c == '(') depth++;
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        sb.Append("))");
                        break;
                    }
                }
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static string FixVertexSampleCalls(string s)
    {
        // Rewrites `_tex_foo.SampleLevel(_samp_foo, uv)` (2 args) to `_tex_foo.SampleLevel(_samp_foo, uv, 0.0)`
        var pattern = new Regex(@"_tex_([A-Za-z_][A-Za-z0-9_]*)\.SampleLevel\(_samp_\1,");
        var sb = new StringBuilder(s.Length + 32);
        var lastIdx = 0;
        foreach (Match m in pattern.Matches(s))
        {
            sb.Append(s, lastIdx, m.Index + m.Length - lastIdx);
            var i = m.Index + m.Length;
            var depth = 1;
            var commas = 0;
            var startArgs = i;
            while (i < s.Length && depth > 0)
            {
                var c = s[i++];
                if (c == '(') depth++;
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        sb.Append(s, startArgs, i - 1 - startArgs);
                        if (commas == 0)
                            sb.Append(", 0.0)");
                        else
                            sb.Append(')');
                        break;
                    }
                }
                else if (c == ',' && depth == 1)
                {
                    commas++;
                }
            }
            lastIdx = i;
        }
        sb.Append(s, lastIdx, s.Length - lastIdx);
        return sb.ToString();
    }

    private static string RewriteMatrixMultiply(string s, string matName)
    {
        var pattern = new Regex($@"\b{Regex.Escape(matName)}(?:\s*\[[^\]]+\])?\s*\*");
        var result = s;
        while (true)
        {
            var m = pattern.Match(result);
            if (!m.Success) break;
            var matExpr = m.Value[..^1].Trim();
            var rhsStart = m.Index + m.Length;
            var i = rhsStart;
            while (i < result.Length && char.IsWhiteSpace(result[i])) i++;
            var depth = 0;
            while (i < result.Length)
            {
                var c = result[i];
                if (c is '(' or '[') depth++;
                else if (c is ')' or ']')
                {
                    if (depth == 0) break;
                    depth--;
                }
                else if (depth == 0 && c is ';' or ',' or '+' or '-')
                {
                    break;
                }
                i++;
            }
            var rhsExpr = result[rhsStart..i].Trim();
            result = result[..m.Index] + $"mul({rhsExpr}, {matExpr})" + result[i..];
        }
        return result;
    }

    internal static bool TryCompileHlsl(string hlsl, string entryPoint, string targetProfile, out byte[] bytecode, out string errorLog)
    {
        bytecode = [];
        errorLog = string.Empty;
        var srcBytes = Encoding.UTF8.GetBytes(hlsl);
        var hr = FolioD3D11Interop.D3DCompile(
            srcBytes,
            (UIntPtr)srcBytes.Length,
            null,
            IntPtr.Zero,
            IntPtr.Zero,
            entryPoint,
            targetProfile,
            0,
            0,
            out var codeBlob,
            out var errBlob);

        try
        {
            if (hr < 0 || codeBlob == IntPtr.Zero)
            {
                errorLog = errBlob != IntPtr.Zero
                    ? FolioD3D11Interop.ReadBlobString(errBlob)
                    : $"D3DCompile failed with HRESULT 0x{hr:X8}.";
                return false;
            }
            bytecode = FolioD3D11Interop.ReadBlobBytes(codeBlob);
            return bytecode.Length > 0;
        }
        finally
        {
            if (codeBlob != IntPtr.Zero) Marshal.Release(codeBlob);
            if (errBlob != IntPtr.Zero) Marshal.Release(errBlob);
        }
    }

    private static string StripComments(string src)
    {
        var sb = new StringBuilder(src.Length);
        var i = 0;
        while (i < src.Length)
        {
            if (i + 1 < src.Length && src[i] == '/' && src[i + 1] == '/')
            {
                i += 2;
                while (i < src.Length && src[i] != '\n') i++;
                continue;
            }
            if (i + 1 < src.Length && src[i] == '/' && src[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/'))
                {
                    if (src[i] == '\n') sb.Append('\n');
                    i++;
                }
                i += 2;
                continue;
            }
            sb.Append(src[i++]);
        }
        return sb.ToString();
    }

    private static int FindMatchingBrace(string text, int openIdx)
    {
        var depth = 0;
        for (var i = openIdx; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private static List<string> SplitStatementsOutsideBraces(string text)
    {
        var list = new List<string>();
        var start = 0;
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '{') depth++;
            else if (c == '}') depth--;
            else if (c == ';' && depth == 0)
            {
                list.Add(text[start..(i + 1)]);
                start = i + 1;
            }
        }
        return list;
    }

    private static List<string> SplitTopLevelItems(string text)
    {
        var list = new List<string>();
        var start = 0;
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    list.Add(text[start..(i + 1)]);
                    start = i + 1;
                }
            }
            else if (c == ';' && depth == 0)
            {
                list.Add(text[start..(i + 1)]);
                start = i + 1;
            }
        }
        var tail = text[start..].Trim();
        if (!string.IsNullOrEmpty(tail))
            list.Add(tail);
        return list;
    }
}
