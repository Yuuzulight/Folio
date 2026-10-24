using System.Numerics;

namespace Folio.Scripting.WebGl;

/// <summary>
/// Derivatives of any expression, as a GPU gets them: the fragment shader runs on the neighbouring pixels of a 2x2 quad and the
/// difference of the expression's values is the derivative.
/// <para>
/// The interpreter cannot suspend a shader at a <c>dFdx</c> to let its neighbours catch up, so a neighbour is run again from the start until it reaches
/// the same derivative site (the shader has no side effects, and the language requires the site to be reached in uniform control flow).
/// Each run records the value of the argument at every site it passes; a site asks the memo first. Sites are matched by how many sites
/// the run has passed, so <c>dFdx</c>, <c>fwidth</c> and an implicit-level texture lookup all count.
/// </para>
/// </summary>
internal sealed class QuadShading
{
    private readonly GlslShader _fragment;
    private readonly ShaderContext _main;
    private readonly Val[] _baseGlobals;
    private readonly ShaderContext?[] _lanes = new ShaderContext?[4];
    private readonly List<Val?>[] _memo = [[], [], [], []];
    private int _quadX;
    private int _quadY;

    public QuadShading(GlslShader fragment, ShaderContext main)
    {
        _fragment = fragment;
        _main = main;
        // The uniforms are in place now and do not change during a draw; every lane starts from them.
        _baseGlobals = (Val[])main.G.Clone();
    }

    /// <summary>Loads the inputs of the fragment at pixel (x, y) into a context: varyings, fragment coordinates, outputs. Supplied by the rasteriser for the current triangle.</summary>
    public Action<ShaderContext, int, int>? LoadInputs { get; set; }

    /// <summary>Starts shading pixel (x, y) as one lane of its quad on the pipeline's own context.</summary>
    public void Begin(int x, int y)
    {
        _quadX = x & ~1;
        _quadY = y & ~1;
        foreach (var memo in _memo) memo.Clear();
        _main.Quad = this;
        _main.Lane = (x & 1) | ((y & 1) << 1);
        _main.CallIndex = 0;
        _main.StopAt = -1;
        _main.Stop = false;
    }

    /// <summary>
    /// A derivative site: evaluates <paramref name="argument"/> for this lane and for the lane next to it in x and in y. Returns false when this
    /// run is only a partial one that has just reached the site it was started for (the context is then marked stopped).
    /// </summary>
    public bool Evaluate(ShaderContext context, Func<ShaderContext, Val> argument, out Val own, out Val neighbourX, out Val neighbourY)
    {
        own = neighbourX = neighbourY = default;
        if (context.Stop) return false;
        var site = context.CallIndex++;
        own = argument(context);
        Record(context.Lane, site, own);
        if (site == context.StopAt)
        {
            context.Stop = true;
            return false;
        }
        neighbourX = Neighbour(context.Lane ^ 1, site, own);
        neighbourY = Neighbour(context.Lane ^ 2, site, own);
        return true;
    }

    /// <summary>The fine derivative along x: the pair difference in the quad's row, signed so it points toward increasing x.</summary>
    public static Vector4 Along(int lane, int bit, Val own, Val neighbour) => (lane & bit) == 0 ? neighbour.C0 - own.C0 : own.C0 - neighbour.C0;

    private void Record(int lane, int site, Val value)
    {
        var memo = _memo[lane];
        while (memo.Count <= site) memo.Add(null);
        memo[site] = value;
    }

    private Val Neighbour(int lane, int site, Val fallback)
    {
        var memo = _memo[lane];
        if (site < memo.Count && memo[site] is { } known) return known;
        Run(lane, site);
        // A neighbour that never reached the site (it discarded first) contributes no change.
        return site < memo.Count && memo[site] is { } value ? value : fallback;
    }

    private void Run(int lane, int site)
    {
        var context = _lanes[lane] ??= _fragment.CreateContext();
        Array.Copy(_baseGlobals, context.G, _baseGlobals.Length);
        context.Quad = this;
        context.Lane = lane;
        context.CallIndex = 0;
        context.StopAt = site;
        context.Stop = false;
        context.Discarded = false;
        context.Deadline = _main.Deadline;
        context.Sampler = _main.Sampler;
        context.DerivX = _main.DerivX;
        context.DerivY = _main.DerivY;
        LoadInputs!(context, _quadX + (lane & 1), _quadY + (lane >> 1));
        _fragment.Main(context);
    }
}
