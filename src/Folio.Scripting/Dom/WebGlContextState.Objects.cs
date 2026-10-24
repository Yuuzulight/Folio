namespace Folio.Scripting.Dom;

internal enum GlKind { Buffer, Shader, Program, Texture, Framebuffer, Renderbuffer, VertexArray, Sampler, Query, Sync, UniformLocation }

/// <summary>
/// What a script holds for a buffer, shader, texture and so on. The context keeps its resources under numeric handles; this is the stable,
/// typed object that stands for one, so <c>gl.createBuffer() === gl.getParameter(gl.ARRAY_BUFFER_BINDING)</c> and the object can key a WeakMap.
/// </summary>
internal sealed class GlObject(GlKind kind, uint handle, WebGlContextState owner, uint program = 0)
{
    public GlKind Kind { get; } = kind;
    /// <summary>The resource's handle; for a uniform location, the location plus one.</summary>
    public uint Handle { get; } = handle;
    public WebGlContextState Owner { get; } = owner;
    /// <summary>The program a uniform location belongs to.</summary>
    public uint Program { get; } = program;
}

/// <summary>An extension object (<c>gl.getExtension(...)</c>): one per extension per context, so repeated calls return the same object.</summary>
internal sealed class GlExtension(string name, WebGlContextState owner)
{
    public string Name { get; } = name;
    public WebGlContextState Owner { get; } = owner;
}

internal sealed record GlActiveInfo(string Name, int Size, int Type);

internal sealed record GlPrecisionFormat(int RangeMin, int RangeMax, int Precision);

internal sealed partial class WebGlContextState
{
    private readonly Dictionary<(GlKind Kind, uint Handle, uint Program), GlObject> _objects = [];
    private readonly Dictionary<string, GlExtension> _extensions = new(StringComparer.OrdinalIgnoreCase);

    public GlObject ObjectFor(GlKind kind, uint handle, uint program = 0)
    {
        var key = (kind, handle, program);
        if (!_objects.TryGetValue(key, out var existing))
            _objects[key] = existing = new GlObject(kind, handle, this, program);
        return existing;
    }

    public void ForgetObject(GlObject target) => _objects.Remove((target.Kind, target.Handle, target.Program));

    public GlExtension ExtensionFor(string name)
    {
        if (!_extensions.TryGetValue(name, out var existing))
            _extensions[name] = existing = new GlExtension(name, this);
        return existing;
    }

    /// <summary>Whether the resource still exists: created here and not deleted since.</summary>
    public bool Exists(GlObject target) => target.Kind switch
    {
        GlKind.Buffer => Buffers.ContainsKey(target.Handle),
        GlKind.Shader => Shaders.ContainsKey(target.Handle),
        GlKind.Program => Programs.Contains(target.Handle),
        GlKind.Texture => Textures.ContainsKey(target.Handle),
        GlKind.Framebuffer => Framebuffers.ContainsKey(target.Handle),
        GlKind.Renderbuffer => Renderbuffers.ContainsKey(target.Handle),
        GlKind.VertexArray => VertexArrays.Contains(target.Handle),
        GlKind.Sampler => Samplers.Contains(target.Handle),
        GlKind.Query => Queries.Contains(target.Handle),
        GlKind.Sync => Syncs.Contains(target.Handle),
        GlKind.UniformLocation => Programs.Contains(target.Program),
        _ => false,
    };
}
