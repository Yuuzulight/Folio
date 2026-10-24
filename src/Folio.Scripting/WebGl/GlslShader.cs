using System.Diagnostics;
using System.Numerics;

namespace Folio.Scripting.WebGl;

internal enum GlslStage { Vertex, Fragment }

internal enum GlslFlow { Next, Break, Continue, Return }

/// <summary>Thrown from a running shader when it has used up the time it was given.</summary>
internal sealed class ShaderTimeoutException() : Exception("The shader ran out of time.");

/// <summary>Texture lookups for the shader, supplied by whoever runs it (the pipeline).</summary>
internal interface IShaderSampler
{
    /// <summary>
    /// Samples texture unit <paramref name="unit"/> at <paramref name="uv"/>. <paramref name="lod"/> is NaN for an implicit level
    /// (chosen from the screen-space change of the coordinates, <paramref name="dx"/> and <paramref name="dy"/>), else the level asked for.
    /// </summary>
    Vector4 Sample(int unit, Vector2 uv, Vector2 dx, Vector2 dy, float lod, float bias);

    /// <summary>One texel by integer coordinates, no filtering.</summary>
    Vector4 Fetch(int unit, int x, int y, int level);

    (int Width, int Height) Size(int unit, int level);
}

/// <summary>The state of one shader invocation: global slots, a window of local slots, and the outputs of flow control.</summary>
internal sealed class ShaderContext
{
    public Val[] G = [];
    public readonly Val[] L = new Val[16384];
    public int Base;
    public int Top;
    public Val Ret;
    public Val[]? RetAgg;
    public bool Discarded;
    public long Deadline = long.MaxValue;
    public int Ticks;
    public IShaderSampler? Sampler;

    /// <summary>Screen-space derivatives of the global slots that are varyings (null where the invocation has none).</summary>
    public Vector4[]? DerivX;
    public Vector4[]? DerivY;

    public void Tick()
    {
        if ((++Ticks & 0x3FF) == 0 && Stopwatch.GetTimestamp() > Deadline)
            throw new ShaderTimeoutException();
    }
}

internal enum GlslStorage { Local, Global, Uniform, Attribute, VaryingIn, VaryingOut, BuiltinIn, BuiltinOut }

/// <summary>A global shader variable: its name, type, where it lives in <see cref="ShaderContext.G"/> and what it is for.</summary>
internal sealed class GlslVariable(string name, GlslType type, GlslStorage storage, int slot, int location = -1)
{
    public string Name { get; } = name;
    public GlslType Type { get; } = type;
    public GlslStorage Storage { get; } = storage;
    public int Slot { get; } = slot;
    public int Location { get; set; } = location;
    public bool IsConst { get; init; }
    public int FrameSlot { get; init; } = -1;
}

/// <summary>A compiled shader: the variables it exchanges with the pipeline and the code of <c>main</c>.</summary>
internal sealed class GlslShader
{
    public GlslStage Stage { get; init; }
    public int Version { get; init; }
    public required List<GlslVariable> Variables { get; init; }
    public required Action<ShaderContext> Main { get; init; }
    public required Action<ShaderContext> InitGlobals { get; init; }
    public required Val[] Initial { get; init; }
    public required int GlobalSlots { get; init; }

    public int PositionSlot { get; init; } = -1;
    public int PointSizeSlot { get; init; } = -1;
    public int FragCoordSlot { get; init; } = -1;
    public int FrontFacingSlot { get; init; } = -1;
    public int PointCoordSlot { get; init; } = -1;
    public int VertexIdSlot { get; init; } = -1;
    public int InstanceIdSlot { get; init; } = -1;
    public int FragDepthSlot { get; init; } = -1;
    public bool UsesDerivatives { get; init; }

    /// <summary>The slots of the fragment outputs, indexed by color attachment (gl_FragColor / gl_FragData[n] / the declared outputs).</summary>
    public int[] FragOutputSlots { get; init; } = [];

    public IEnumerable<GlslVariable> Uniforms => Variables.Where(v => v.Storage == GlslStorage.Uniform);
    public IEnumerable<GlslVariable> Attributes => Variables.Where(v => v.Storage == GlslStorage.Attribute);

    public ShaderContext CreateContext()
    {
        var context = new ShaderContext { G = (Val[])Initial.Clone() };
        return context;
    }
}

internal sealed record GlslCompileResult(GlslShader? Shader, string InfoLog, int ErrorLine)
{
    public bool Ok => Shader is not null;
}
