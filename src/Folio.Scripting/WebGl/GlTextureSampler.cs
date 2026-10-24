using System.Numerics;
using Folio.Scripting.Dom;

namespace Folio.Scripting.WebGl;

/// <summary>Texture lookups for a draw: the texture bound to each unit, its filters and wrapping, and mip levels chosen from the screen-space change of the coordinates.</summary>
internal sealed class GlTextureSampler(WebGlContextState state) : IShaderSampler
{
    private static readonly Vector4 Black = new(0, 0, 0, 1);

    private (List<(int Width, int Height, byte[] Pixels)> Levels, int Min, int Mag, uint WrapS, uint WrapT)? Resolve(int unit)
    {
        if (unit < 0 || unit >= state.TextureUnits.Length || state.TextureUnits[unit] is not { } handle || !state.Textures.TryGetValue(handle, out var baseTexture))
            return null;
        if (baseTexture.Width <= 0 || baseTexture.Height <= 0) return null;
        var levels = state.TextureMipLevels.TryGetValue(handle, out var mips) && mips.Count > 0 ? mips : [baseTexture];
        state.TextureParameters.TryGetValue(handle, out var parameters);
        int Parameter(uint name, int fallback)
        {
            // A sampler object bound to the unit overrides the texture's own parameters.
            var sampler = state.BoundSamplers.GetValueOrDefault((uint)unit);
            if (sampler is { } s && state.SamplerParameters.TryGetValue(s, out var overrides) && overrides.TryGetValue(name, out var v)) return (int)v;
            return parameters is not null && parameters.TryGetValue(name, out var own) ? own : fallback;
        }
        return (levels, Parameter(0x2801, 0x2702), Parameter(0x2800, 0x2601), (uint)Parameter(0x2802, 0x2901), (uint)Parameter(0x2803, 0x2901));
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
        var baseLevel = levels[0];
        var mipmapped = min is 0x2700 or 0x2701 or 0x2702 or 0x2703;
        // A mip filter on a texture without all its levels leaves it incomplete, and an incomplete texture reads as black.
        if (mipmapped && (levels.Count < FullChain(baseLevel.Width, baseLevel.Height) || levels.Any(l => l.Width <= 0))) return Black;

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
            return SampleLevel(levels[0], uv, wrapS, wrapT, linear);
        }
        var maxLevel = levels.Count - 1;
        switch (filter)
        {
            case 0x2700:
                return SampleLevel(levels[Math.Clamp((int)MathF.Round(level), 0, maxLevel)], uv, wrapS, wrapT, linear: false);
            case 0x2701:
                return SampleLevel(levels[Math.Clamp((int)MathF.Round(level), 0, maxLevel)], uv, wrapS, wrapT, linear: true);
            default:
            {
                var l0 = Math.Clamp((int)MathF.Floor(level), 0, maxLevel);
                var l1 = Math.Min(l0 + 1, maxLevel);
                var f = Math.Clamp(level - l0, 0, 1);
                var linear = filter == 0x2703;
                return Vector4.Lerp(SampleLevel(levels[l0], uv, wrapS, wrapT, linear), SampleLevel(levels[l1], uv, wrapS, wrapT, linear), f);
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

    private static Vector4 Texel((int Width, int Height, byte[] Pixels) level, int x, int y)
    {
        var i = (y * level.Width + x) * 4;
        return new Vector4(level.Pixels[i], level.Pixels[i + 1], level.Pixels[i + 2], level.Pixels[i + 3]) * (1f / 255f);
    }

    private static Vector4 SampleLevel((int Width, int Height, byte[] Pixels) level, Vector2 uv, uint wrapS, uint wrapT, bool linear)
    {
        if (level.Width <= 0 || level.Height <= 0 || level.Pixels.Length < level.Width * level.Height * 4) return Black;
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

    public Vector4 Fetch(int unit, int x, int y, int level)
    {
        if (Resolve(unit) is not { } t) return Vector4.Zero;
        var levels = t.Levels;
        if (level < 0 || level >= levels.Count) return Vector4.Zero;
        var l = levels[level];
        if (x < 0 || y < 0 || x >= l.Width || y >= l.Height) return Vector4.Zero;
        return Texel(l, x, y);
    }

    public (int Width, int Height) Size(int unit, int level)
    {
        if (Resolve(unit) is not { } t || level < 0 || level >= t.Levels.Count) return (0, 0);
        return (t.Levels[level].Width, t.Levels[level].Height);
    }
}
