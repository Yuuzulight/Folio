using System.Numerics;

namespace Folio.Scripting.WebGl;

internal sealed partial class GlTextureSampler
{
    // The three axes of each cube face, as unit vectors: s runs along A, t along B, and C points out through the face.
    private static readonly (Vector3 A, Vector3 B, Vector3 C)[] FaceAxes =
    [
        (new(0, 0, -1), new(0, -1, 0), new(1, 0, 0)),    // +X
        (new(0, 0, 1), new(0, -1, 0), new(-1, 0, 0)),    // -X
        (new(1, 0, 0), new(0, 0, 1), new(0, 1, 0)),      // +Y
        (new(1, 0, 0), new(0, 0, -1), new(0, -1, 0)),    // -Y
        (new(1, 0, 0), new(0, -1, 0), new(0, 0, 1)),     // +Z
        (new(-1, 0, 0), new(0, -1, 0), new(0, 0, -1)),   // -Z
    ];

    /// <summary>The face a direction leaves through, and where on it, as texture coordinates in 0 to 1.</summary>
    internal static (int Face, float S, float T) CubeFace(Vector3 direction)
    {
        var ax = MathF.Abs(direction.X); var ay = MathF.Abs(direction.Y); var az = MathF.Abs(direction.Z);
        int face;
        if (ax >= ay && ax >= az) face = direction.X >= 0 ? 0 : 1;
        else if (ay >= az) face = direction.Y >= 0 ? 2 : 3;
        else face = direction.Z >= 0 ? 4 : 5;
        var (a, b, c) = FaceAxes[face];
        var ma = Vector3.Dot(c, direction);
        if (ma == 0 || float.IsNaN(ma)) return (face, 0.5f, 0.5f);
        return (face, 0.5f * (Vector3.Dot(a, direction) / ma + 1), 0.5f * (Vector3.Dot(b, direction) / ma + 1));
    }

    private static Vector3 FaceDirection(int face, float s, float t)
    {
        var (a, b, c) = FaceAxes[face];
        return a * (2 * s - 1) + b * (2 * t - 1) + c;
    }

    private (GlTexture Texture, int Min, int Mag)? ResolveCube(int unit)
    {
        if (unit < 0 || unit >= state.CubeTextureUnits.Length || state.CubeTextureUnits[unit] is not { } handle || !state.Textures.TryGetValue(handle, out var texture) || texture.Base is not { } baseLevel)
            return null;
        if (baseLevel.Width <= 0) return null;
        return (texture, TextureParameter(handle, unit, 0x2801, 0x2702), TextureParameter(handle, unit, 0x2800, 0x2601));
    }

    public Vector4 SampleCube(int unit, Vector3 direction, Vector3 dx, Vector3 dy, float lod, float bias)
    {
        if (ResolveCube(unit) is not { } t) return Black;
        var (texture, min, mag) = t;
        var baseLevel = texture.Base!;
        var mipmapped = min is 0x2700 or 0x2701 or 0x2702 or 0x2703;
        if (!texture.IsCubeComplete(levelZeroOnly: !mipmapped) || (mipmapped && texture.Faces[0].Count < FullChain(baseLevel.Width, baseLevel.Width))) return Black;
        if (!Filterable(baseLevel, min, mag, comparing: false)) return Black;

        var (face, s, tt) = CubeFace(direction);
        float level;
        if (float.IsNaN(lod))
        {
            // The change of the face coordinates across a pixel, from the change of the direction.
            var (a, b, c) = FaceAxes[face];
            var ma = Vector3.Dot(c, direction);
            var sc = Vector3.Dot(a, direction);
            var tc = Vector3.Dot(b, direction);
            float Spread(Vector3 d)
            {
                if (ma == 0) return 0;
                var ds = 0.5f * (Vector3.Dot(a, d) * ma - sc * Vector3.Dot(c, d)) / (ma * ma);
                var dt = 0.5f * (Vector3.Dot(b, d) * ma - tc * Vector3.Dot(c, d)) / (ma * ma);
                return new Vector2(ds, dt).Length() * baseLevel.Width;
            }
            var rho = MathF.Max(Spread(dx), Spread(dy));
            level = (rho > 0 ? MathF.Log2(rho) : 0) + bias;
        }
        else
        {
            level = lod;
        }
        return ByLevel(texture.Faces[0].Count, min, mag, level, (index, linear) => SampleCubeLevel(texture, index, face, s, tt, linear));
    }

    private static Vector4 SampleCubeLevel(GlTexture texture, int level, int face, float s, float t, bool linear)
    {
        var size = texture.Level(level, face)?.Width ?? 0;
        if (size <= 0) return Black;
        Vector4 Texel(int f, int i, int j) => texture.Level(level, f)!.LoadColor(Math.Clamp(j, 0, size - 1) * size + Math.Clamp(i, 0, size - 1));
        if (!linear) return Texel(face, (int)MathF.Floor(s * size), (int)MathF.Floor(t * size));

        // Bilinear, seamless: a tap beyond the edge of the face is found on the face next to it.
        var px = s * size - 0.5f;
        var py = t * size - 0.5f;
        var x0 = (int)MathF.Floor(px);
        var y0 = (int)MathF.Floor(py);
        var fx = px - x0;
        var fy = py - y0;
        Vector4 Tap(int i, int j)
        {
            if (i >= 0 && i < size && j >= 0 && j < size) return Texel(face, i, j);
            var (other, os, ot) = CubeFace(FaceDirection(face, (i + 0.5f) / size, (j + 0.5f) / size));
            return Texel(other, (int)MathF.Floor(os * size), (int)MathF.Floor(ot * size));
        }
        var top = Vector4.Lerp(Tap(x0, y0), Tap(x0 + 1, y0), fx);
        var bottom = Vector4.Lerp(Tap(x0, y0 + 1), Tap(x0 + 1, y0 + 1), fx);
        return Vector4.Lerp(top, bottom, fy);
    }
}
