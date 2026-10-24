using System.Numerics;
using Folio.Scripting.Dom;

namespace Folio.Scripting.WebGl;

/// <summary>Texture lookups for a draw: the texture bound to each unit, its filters and wrapping, and mip levels chosen from the screen-space change of the coordinates.</summary>
internal sealed partial class GlTextureSampler(WebGlContextState state) : IShaderSampler
{
    private static readonly Vector4 Black = new(0, 0, 0, 1);

    private (List<GlImage?> Levels, int Min, int Mag, uint WrapS, uint WrapT)? Resolve(int unit)
    {
        if (unit < 0 || unit >= state.TextureUnits.Length || state.TextureUnits[unit] is not { } handle || !state.Textures.TryGetValue(handle, out var texture) || texture.Base is not { } baseTexture)
            return null;
        if (baseTexture.Width <= 0 || baseTexture.Height <= 0) return null;
        int Parameter(uint name, int fallback) => TextureParameter(handle, unit, name, fallback);
        return (texture.Levels, Parameter(0x2801, 0x2702), Parameter(0x2800, 0x2601), (uint)Parameter(0x2802, 0x2901), (uint)Parameter(0x2803, 0x2901));
    }

    /// <summary>A parameter of the texture on a unit; a sampler object bound to the unit overrides the texture's own.</summary>
    private int TextureParameter(uint handle, int unit, uint name, int fallback)
    {
        var sampler = state.BoundSamplers.GetValueOrDefault((uint)unit);
        if (sampler is { } s && state.SamplerParameters.TryGetValue(s, out var overrides) && overrides.TryGetValue(name, out var v)) return (int)v;
        return state.TextureParameters.TryGetValue(handle, out var parameters) && parameters.TryGetValue(name, out var own) ? own : fallback;
    }

    /// <summary>
    /// Whether the image can be read with these filters: depth is sampled nearest (unless it is being compared), and 32-bit floats filter only
    /// once OES_texture_float_linear is enabled. A texture that cannot is incomplete and reads black.
    /// </summary>
    private bool Filterable(GlImage baseLevel, int min, int mag, bool comparing)
    {
        var linear = mag is 0x2601 || min is 0x2601 or 0x2701 or 0x2702 or 0x2703;
        if (!linear) return true;
        if (baseLevel.Format.IsDepth) return comparing;
        return !(baseLevel.Format.Storage == GlStorage.F32 && baseLevel.Format.IsColor && !state.EnabledExtensions.Contains("OES_texture_float_linear"));
    }

    /// <summary>The result of looking up a texture at a (possibly fractional) level: nearest or linear within a level, and between two levels for a mipmapped filter.</summary>
    private static Vector4 ByLevel(int levelCount, int min, int mag, float level, Func<int, bool, Vector4> at)
    {
        var mipmapped = min is 0x2700 or 0x2701 or 0x2702 or 0x2703;
        var magnify = level <= 0;
        if (!mipmapped || magnify || levelCount == 1)
            return at(0, (magnify ? mag : min) is 0x2601 or 0x2701 or 0x2703);
        var maxLevel = levelCount - 1;
        switch (min)
        {
            case 0x2700:
                return at(Math.Clamp((int)MathF.Round(level), 0, maxLevel), false);
            case 0x2701:
                return at(Math.Clamp((int)MathF.Round(level), 0, maxLevel), true);
            default:
            {
                var l0 = Math.Clamp((int)MathF.Floor(level), 0, maxLevel);
                var l1 = Math.Min(l0 + 1, maxLevel);
                var f = Math.Clamp(level - l0, 0, 1);
                var linear = min == 0x2703;
                return Vector4.Lerp(at(l0, linear), at(l1, linear), f);
            }
        }
    }

    private static int FullChain(int width, int height)
    {
        var levels = 1;
        while (width > 1 || height > 1)
        {
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
            levels++;
        }
        return levels;
    }

    public Vector4 Sample(int unit, Vector2 uv, Vector2 dx, Vector2 dy, float lod, float bias)
    {
        if (Resolve(unit) is not { } t) return Black;
        var (levels, min, mag, wrapS, wrapT) = t;
        var baseLevel = levels[0]!;
        var mipmapped = min is 0x2700 or 0x2701 or 0x2702 or 0x2703;
        // A mip filter on a texture without all its levels leaves it incomplete, and an incomplete texture reads as black.
        if (mipmapped && (levels.Count < FullChain(baseLevel.Width, baseLevel.Height) || levels.Any(l => l is null || l.Width <= 0))) return Black;
        // Linear filtering needs a filterable format: depth is sampled nearest, and 32-bit floats are filterable only with OES_texture_float_linear.
        var comparing = state.TextureParameters.TryGetValue(state.TextureUnits[unit] ?? 0, out var textureParameters) && textureParameters.GetValueOrDefault(0x884Cu) == 0x884E;
        if (!Filterable(baseLevel, min, mag, comparing)) return Black;

        float level;
        if (float.IsNaN(lod))
        {
            var sx = new Vector2(dx.X * baseLevel.Width, dx.Y * baseLevel.Height).Length();
            var sy = new Vector2(dy.X * baseLevel.Width, dy.Y * baseLevel.Height).Length();
            var rho = MathF.Max(sx, sy);
            level = rho > 0 ? MathF.Log2(rho) : 0;
            level += bias;
        }
        else
        {
            level = lod;
        }
        var magnify = level <= 0;
        var filter = magnify ? mag : min;
        if (!mipmapped || magnify || levels.Count == 1)
        {
            var linear = (magnify ? mag : min) is 0x2601 or 0x2701 or 0x2703;
            return SampleLevel(levels[0]!, uv, wrapS, wrapT, linear);
        }
        var maxLevel = levels.Count - 1;
        switch (filter)
        {
            case 0x2700:
                return SampleLevel(levels[Math.Clamp((int)MathF.Round(level), 0, maxLevel)]!, uv, wrapS, wrapT, linear: false);
            case 0x2701:
                return SampleLevel(levels[Math.Clamp((int)MathF.Round(level), 0, maxLevel)]!, uv, wrapS, wrapT, linear: true);
            default:
            {
                var l0 = Math.Clamp((int)MathF.Floor(level), 0, maxLevel);
                var l1 = Math.Min(l0 + 1, maxLevel);
                var f = Math.Clamp(level - l0, 0, 1);
                var linear = filter == 0x2703;
                return Vector4.Lerp(SampleLevel(levels[l0]!, uv, wrapS, wrapT, linear), SampleLevel(levels[l1]!, uv, wrapS, wrapT, linear), f);
            }
        }
    }

    private static float Wrap(float coordinate, uint mode)
    {
        switch (mode)
        {
            case 0x812F:
                return Math.Clamp(coordinate, 0f, 1f);
            case 0x8370:
            {
                var m = MathF.Abs(coordinate) % 2f;
                return m > 1f ? 2f - m : m;
            }
            default:
                return coordinate - MathF.Floor(coordinate);
        }
    }

    private static int WrapIndex(int index, int size, uint mode)
    {
        switch (mode)
        {
            case 0x812F:
                return Math.Clamp(index, 0, size - 1);
            case 0x8370:
            {
                var period = size * 2;
                var m = ((index % period) + period) % period;
                return m >= size ? period - 1 - m : m;
            }
            default:
                return ((index % size) + size) % size;
        }
    }

    private static Vector4 Texel(GlImage level, int x, int y) => level.LoadColor(y * level.Width + x);

    private static Vector4 SampleLevel(GlImage level, Vector2 uv, uint wrapS, uint wrapT, bool linear)
    {
        if (level.Width <= 0 || level.Height <= 0) return Black;
        if (!linear)
        {
            var u = Wrap(uv.X, wrapS);
            var v = Wrap(uv.Y, wrapT);
            return Texel(level, Math.Clamp((int)MathF.Floor(u * level.Width), 0, level.Width - 1), Math.Clamp((int)MathF.Floor(v * level.Height), 0, level.Height - 1));
        }
        // Bilinear: the four texels around the point, wrapped by the mode rather than the coordinate (so a repeating texture blends across its seam).
        var px = uv.X * level.Width - 0.5f;
        var py = uv.Y * level.Height - 0.5f;
        var x0 = (int)MathF.Floor(px);
        var y0 = (int)MathF.Floor(py);
        var fx = px - x0;
        var fy = py - y0;
        var i0 = WrapIndex(x0, level.Width, wrapS);
        var i1 = WrapIndex(x0 + 1, level.Width, wrapS);
        var j0 = WrapIndex(y0, level.Height, wrapT);
        var j1 = WrapIndex(y0 + 1, level.Height, wrapT);
        var top = Vector4.Lerp(Texel(level, i0, j0), Texel(level, i1, j0), fx);
        var bottom = Vector4.Lerp(Texel(level, i0, j1), Texel(level, i1, j1), fx);
        return Vector4.Lerp(top, bottom, fy);
    }

    public float SampleShadow(int unit, Vector2 uv, float reference)
    {
        if (Resolve(unit) is not { } t || t.Levels[0] is not { } level || !level.Format.IsDepth) return 0;
        var handle = state.TextureUnits[unit] ?? 0;
        state.TextureParameters.TryGetValue(handle, out var parameters);
        var function = (uint)(parameters?.GetValueOrDefault(0x884Du, 0x0203) ?? 0x0203);
        float Test(int x, int y)
        {
            var depth = BitConverter.ToSingle(level.Data, (y * level.Width + x) * 4);
            var passes = function switch
            {
                0x0200 => false,
                0x0201 => reference < depth,
                0x0202 => reference == depth,
                0x0203 => reference <= depth,
                0x0204 => reference > depth,
                0x0205 => reference != depth,
                0x0206 => reference >= depth,
                _ => true,
            };
            return passes ? 1f : 0f;
        }
        if (t.Mag != 0x2601 && t.Min is not (0x2601 or 0x2701 or 0x2703))
            return Test(Math.Clamp((int)MathF.Floor(Wrap(uv.X, t.WrapS) * level.Width), 0, level.Width - 1), Math.Clamp((int)MathF.Floor(Wrap(uv.Y, t.WrapT) * level.Height), 0, level.Height - 1));
        // Percentage-closer filtering: the comparison results of the four texels around the point, blended.
        var px = uv.X * level.Width - 0.5f;
        var py = uv.Y * level.Height - 0.5f;
        var x0 = (int)MathF.Floor(px);
        var y0 = (int)MathF.Floor(py);
        var fx = px - x0;
        var fy = py - y0;
        var i0 = WrapIndex(x0, level.Width, t.WrapS); var i1 = WrapIndex(x0 + 1, level.Width, t.WrapS);
        var j0 = WrapIndex(y0, level.Height, t.WrapT); var j1 = WrapIndex(y0 + 1, level.Height, t.WrapT);
        var top = Test(i0, j0) * (1 - fx) + Test(i1, j0) * fx;
        var bottom = Test(i0, j1) * (1 - fx) + Test(i1, j1) * fx;
        return top * (1 - fy) + bottom * fy;
    }

    public Vector4 Fetch(int unit, int x, int y, int level)
    {
        if (Resolve(unit) is not { } t) return Vector4.Zero;
        if (t.Levels.ElementAtOrDefault(level) is not { } l) return Vector4.Zero;
        if (x < 0 || y < 0 || x >= l.Width || y >= l.Height) return Vector4.Zero;
        return Texel(l, x, y);
    }

    public (int Width, int Height) Size(int unit, int level)
    {
        if (Resolve(unit) is not { } t || t.Levels.ElementAtOrDefault(level) is not { } l) return (0, 0);
        return (l.Width, l.Height);
    }
}
