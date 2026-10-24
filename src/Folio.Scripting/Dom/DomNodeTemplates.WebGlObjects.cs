namespace Folio.Scripting.Dom;

/// <summary>
/// The typed objects of the WebGL API (WebGLBuffer, WebGLProgram, ...) and the extension objects. The context itself keeps its resources under
/// numeric handles; these wrap them so a script sees real objects with a stable identity, a constructor to test against, and the type checks
/// the specification asks for.
/// </summary>
internal sealed partial class DomNodeTemplates
{
    private static readonly (GlKind Kind, string Name)[] GlObjectNames =
    [
        (GlKind.Buffer, "WebGLBuffer"), (GlKind.Shader, "WebGLShader"), (GlKind.Program, "WebGLProgram"), (GlKind.Texture, "WebGLTexture"),
        (GlKind.Framebuffer, "WebGLFramebuffer"), (GlKind.Renderbuffer, "WebGLRenderbuffer"), (GlKind.VertexArray, "WebGLVertexArrayObject"),
        (GlKind.Sampler, "WebGLSampler"), (GlKind.Query, "WebGLQuery"), (GlKind.Sync, "WebGLSync"), (GlKind.UniformLocation, "WebGLUniformLocation"),
    ];

    /// <summary>Names of the WebGL interfaces that scripts can test against; <see cref="DomBindings"/> declares a constructor for each.</summary>
    internal static IEnumerable<string> WebGlInterfaceNames => GlObjectNames.Select(o => o.Name).Concat(["WebGLActiveInfo", "WebGLShaderPrecisionFormat", "WebGL2RenderingContext"]);

    private readonly Dictionary<GlKind, HostObjectTemplate> GlObjectTemplates = [];

    private readonly HostObjectTemplate _glActiveInfoTemplate = new HostObjectTemplate("WebGLActiveInfo")
        .DefineProperty("name", (target, _) => ScriptValue.FromString(((GlActiveInfo)target).Name))
        .DefineProperty("size", (target, _) => ScriptValue.FromNumber(((GlActiveInfo)target).Size))
        .DefineProperty("type", (target, _) => ScriptValue.FromNumber(((GlActiveInfo)target).Type));

    private readonly HostObjectTemplate _glPrecisionFormatTemplate = new HostObjectTemplate("WebGLShaderPrecisionFormat")
        .DefineProperty("rangeMin", (target, _) => ScriptValue.FromNumber(((GlPrecisionFormat)target).RangeMin))
        .DefineProperty("rangeMax", (target, _) => ScriptValue.FromNumber(((GlPrecisionFormat)target).RangeMax))
        .DefineProperty("precision", (target, _) => ScriptValue.FromNumber(((GlPrecisionFormat)target).Precision));

    /// <summary>
    /// Which parameters of a method are WebGL objects, one letter each: B buffer, S shader, P program, T texture, F framebuffer, R renderbuffer,
    /// V vertex array, M sampler, Q query, Y sync, L uniform location, - anything else.
    /// </summary>
    private static readonly Dictionary<string, string> GlArgSpecs = new()
    {
        ["bindBuffer"] = "-B", ["deleteBuffer"] = "B", ["isBuffer"] = "B",
        ["shaderSource"] = "S", ["compileShader"] = "S", ["deleteShader"] = "S", ["isShader"] = "S", ["getShaderParameter"] = "S", ["getShaderInfoLog"] = "S", ["getShaderSource"] = "S",
        ["attachShader"] = "PS", ["detachShader"] = "PS", ["linkProgram"] = "P", ["deleteProgram"] = "P", ["isProgram"] = "P", ["getProgramParameter"] = "P",
        ["getProgramInfoLog"] = "P", ["getAttachedShaders"] = "P", ["useProgram"] = "P", ["validateProgram"] = "P", ["bindAttribLocation"] = "P",
        ["getAttribLocation"] = "P", ["getUniformLocation"] = "P", ["getActiveUniform"] = "P", ["getActiveAttrib"] = "P", ["getUniform"] = "PL",
        ["bindTexture"] = "-T", ["deleteTexture"] = "T", ["isTexture"] = "T",
        ["bindFramebuffer"] = "-F", ["deleteFramebuffer"] = "F", ["isFramebuffer"] = "F", ["framebufferTexture2D"] = "---T", ["framebufferRenderbuffer"] = "---R",
        ["bindRenderbuffer"] = "-R", ["deleteRenderbuffer"] = "R", ["isRenderbuffer"] = "R",
        ["bindVertexArray"] = "V", ["deleteVertexArray"] = "V", ["isVertexArray"] = "V",
        ["deleteSampler"] = "M", ["isSampler"] = "M", ["bindSampler"] = "-M", ["samplerParameteri"] = "M", ["samplerParameterf"] = "M", ["getSamplerParameter"] = "M",
        ["clientWaitSync"] = "Y", ["deleteSync"] = "Y", ["isSync"] = "Y", ["getSyncParameter"] = "Y",
        ["beginQuery"] = "-Q", ["deleteQuery"] = "Q", ["isQuery"] = "Q", ["getQueryParameter"] = "Q",
        ["uniform1f"] = "L", ["uniform2f"] = "L", ["uniform3f"] = "L", ["uniform4f"] = "L", ["uniform1i"] = "L", ["uniform2i"] = "L", ["uniform3i"] = "L", ["uniform4i"] = "L",
        ["uniform1fv"] = "L", ["uniform2fv"] = "L", ["uniform3fv"] = "L", ["uniform4fv"] = "L", ["uniform1iv"] = "L", ["uniform2iv"] = "L", ["uniform3iv"] = "L", ["uniform4iv"] = "L",
        ["uniformMatrix2fv"] = "L", ["uniformMatrix3fv"] = "L", ["uniformMatrix4fv"] = "L",
    };

    private static GlKind KindOf(char code) => code switch
    {
        'B' => GlKind.Buffer, 'S' => GlKind.Shader, 'P' => GlKind.Program, 'T' => GlKind.Texture, 'F' => GlKind.Framebuffer, 'R' => GlKind.Renderbuffer,
        'V' => GlKind.VertexArray, 'M' => GlKind.Sampler, 'Q' => GlKind.Query, 'Y' => GlKind.Sync, _ => GlKind.UniformLocation,
    };

    private string GlInterfaceName(GlKind kind) => GlObjectTemplates[kind].Name;

    private ScriptValue GlWrap(WebGlContextState state, GlKind kind, uint handle, uint program = 0) =>
        _context.WrapperCache.GetOrCreate(_context.Engine, GlObjectTemplates[kind], state.ObjectFor(kind, handle, program));

    /// <summary>The object for a binding, or null when nothing is bound.</summary>
    private ScriptValue GlBound(WebGlContextState state, GlKind kind, uint? handle) =>
        handle is { } h && h != 0 ? GlWrap(state, kind, h) : ScriptValue.Null;

    private ScriptValue GlActiveInfo(string name, int size, int type) =>
        _context.Engine.CreateHostObject(_glActiveInfoTemplate, new GlActiveInfo(name, size, type));

    /// <summary>
    /// Replaces the WebGL objects among a call's arguments with their handles, which is what the context's methods work with. A value that is
    /// not an object of the expected type is a TypeError; an object that was deleted or belongs to another context is INVALID_OPERATION
    /// (false from an is* query, nothing from a delete*).
    /// </summary>
    private bool ConvertGlObjects(WebGlContextState state, string method, string spec, ReadOnlySpan<ScriptValue> args, out ScriptValue[] converted, out ScriptValue result)
    {
        converted = args.ToArray();
        result = ScriptValue.Undefined;
        GlObject? location = null;
        uint? program = null;
        for (var i = 0; i < spec.Length && i < args.Length; i++)
        {
            if (spec[i] == '-' || args[i].IsNullOrUndefined) continue;
            var kind = KindOf(spec[i]);
            if (!_context.Engine.TryGetHostTarget(args[i], out var host) || host is not GlObject glObject || glObject.Kind != kind)
                throw WebIdlException.TypeError($"Failed to execute '{method}' on 'WebGL2RenderingContext': parameter {i + 1} is not of type '{GlInterfaceName(kind)}'.");
            if (glObject.Owner != state || !state.Exists(glObject))
            {
                if (method.StartsWith("is", StringComparison.Ordinal)) result = ScriptValue.FromBoolean(false);
                else if (method.StartsWith("delete", StringComparison.Ordinal)) result = ScriptValue.Undefined;
                else
                {
                    state.SetError(0x0502);
                    result = method.StartsWith("get", StringComparison.Ordinal) ? ScriptValue.Null : ScriptValue.Undefined;
                }
                if (glObject.Owner != state && method.StartsWith("delete", StringComparison.Ordinal)) state.SetError(0x0502);
                return false;
            }
            converted[i] = ScriptValue.FromNumber(glObject.Handle);
            if (kind == GlKind.UniformLocation) location = glObject;
            else if (kind == GlKind.Program) program = glObject.Handle;
        }
        // A location can be used only with the program it came from: the current one for the uniform setters, the named one for getUniform.
        if (location is not null)
        {
            var expected = method == "getUniform" ? program : state.CurrentProgram;
            if (location.Program != expected)
            {
                state.SetError(0x0502);
                result = method.StartsWith("get", StringComparison.Ordinal) ? ScriptValue.Null : ScriptValue.Undefined;
                return false;
            }
        }
        return true;
    }

    /// <summary>The extension objects <c>getExtension</c> hands out, built on the context's own methods.</summary>
    private Dictionary<string, HostObjectTemplate> CreateWebGlExtensions(HostObjectTemplate context)
    {
        // The extension's alias for a method of the context; the context's method is looked up at call time so it is the guarded one.
        static void Forward(HostObjectTemplate extension, HostObjectTemplate context, string alias, string method) =>
            extension.DefineMethod(alias, (target, receiver, args) => context.TryGetMethod(method, out var callback) ? callback(((GlExtension)target).Owner, receiver, args) : ScriptValue.Undefined);

        static HostObjectTemplate Constants(string name, params (string Name, int Value)[] constants)
        {
            var template = new HostObjectTemplate(name);
            foreach (var (constantName, value) in constants)
            {
                var number = ScriptValue.FromNumber(value);
                template.DefineProperty(constantName, (_, _) => number);
            }
            return template;
        }

        var loseContext = new HostObjectTemplate("WEBGL_lose_context")
            .DefineMethod("loseContext", (target, _, _) => { ((GlExtension)target).Owner.Lose(); return ScriptValue.Undefined; })
            .DefineMethod("restoreContext", (target, _, _) => { if (_context.CheckCapability("WebGL", _context.Capabilities.WebGl)) ((GlExtension)target).Owner.Restore(); return ScriptValue.Undefined; });

        var instanced = Constants("ANGLE_instanced_arrays", ("VERTEX_ATTRIB_ARRAY_DIVISOR_ANGLE", 0x88FE));
        Forward(instanced, context, "drawArraysInstancedANGLE", "drawArraysInstanced");
        Forward(instanced, context, "drawElementsInstancedANGLE", "drawElementsInstanced");
        Forward(instanced, context, "vertexAttribDivisorANGLE", "vertexAttribDivisor");

        var vertexArrays = Constants("OES_vertex_array_object", ("VERTEX_ARRAY_BINDING_OES", 0x85B5));
        Forward(vertexArrays, context, "createVertexArrayOES", "createVertexArray");
        Forward(vertexArrays, context, "deleteVertexArrayOES", "deleteVertexArray");
        Forward(vertexArrays, context, "isVertexArrayOES", "isVertexArray");
        Forward(vertexArrays, context, "bindVertexArrayOES", "bindVertexArray");

        return new Dictionary<string, HostObjectTemplate>(StringComparer.OrdinalIgnoreCase)
        {
            ["WEBGL_lose_context"] = loseContext,
            ["ANGLE_instanced_arrays"] = instanced,
            ["OES_vertex_array_object"] = vertexArrays,
            ["OES_standard_derivatives"] = Constants("OES_standard_derivatives", ("FRAGMENT_SHADER_DERIVATIVE_HINT_OES", 0x8B8B)),
            ["OES_element_index_uint"] = Constants("OES_element_index_uint"),
            ["EXT_blend_minmax"] = Constants("EXT_blend_minmax", ("MIN_EXT", 0x8007), ("MAX_EXT", 0x8008)),
            ["EXT_frag_depth"] = Constants("EXT_frag_depth"),
            ["EXT_texture_filter_anisotropic"] = Constants("EXT_texture_filter_anisotropic", ("TEXTURE_MAX_ANISOTROPY_EXT", 0x84FE), ("MAX_TEXTURE_MAX_ANISOTROPY_EXT", 0x84FF)),
            ["WEBGL_debug_renderer_info"] = Constants("WEBGL_debug_renderer_info", ("UNMASKED_VENDOR_WEBGL", 0x9245), ("UNMASKED_RENDERER_WEBGL", 0x9246)),
        };
    }
}
