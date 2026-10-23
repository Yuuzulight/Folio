using System.Numerics;

namespace Folio.Painting.Software;

/// <summary>
/// A projective transform of the plane, in row-vector form: a point (x, y) maps to (x·M11 + y·M21 + M31, x·M12 + y·M22 + M32)
/// divided by w = x·M13 + y·M23 + M33. Without perspective (M13 = M23 = 0, M33 = 1) it is an ordinary 2D transform.
/// </summary>
internal readonly struct Matrix33(float m11, float m12, float m13, float m21, float m22, float m23, float m31, float m32, float m33)
{
    public readonly float M11 = m11, M12 = m12, M13 = m13, M21 = m21, M22 = m22, M23 = m23, M31 = m31, M32 = m32, M33 = m33;

    public static Matrix33 Identity => new(1, 0, 0, 0, 1, 0, 0, 0, 1);

    public static Matrix33 FromAffine(in Matrix3x2 m) => new(m.M11, m.M12, 0, m.M21, m.M22, 0, m.M31, m.M32, 1);

    /// <summary>The projective transform a 4x4 one makes of the plane: its z row and column are ignored.</summary>
    public static Matrix33 FromProjective(in Matrix4x4 m) => new(m.M11, m.M12, m.M14, m.M21, m.M22, m.M24, m.M41, m.M42, m.M44);

    public bool IsAffine => M13 == 0 && M23 == 0;

    /// <summary>The first transform, then <paramref name="b"/>.</summary>
    public static Matrix33 operator *(in Matrix33 a, in Matrix33 b) => new(
        a.M11 * b.M11 + a.M12 * b.M21 + a.M13 * b.M31, a.M11 * b.M12 + a.M12 * b.M22 + a.M13 * b.M32, a.M11 * b.M13 + a.M12 * b.M23 + a.M13 * b.M33,
        a.M21 * b.M11 + a.M22 * b.M21 + a.M23 * b.M31, a.M21 * b.M12 + a.M22 * b.M22 + a.M23 * b.M32, a.M21 * b.M13 + a.M22 * b.M23 + a.M23 * b.M33,
        a.M31 * b.M11 + a.M32 * b.M21 + a.M33 * b.M31, a.M31 * b.M12 + a.M32 * b.M22 + a.M33 * b.M32, a.M31 * b.M13 + a.M32 * b.M23 + a.M33 * b.M33);

    public static Matrix33 operator *(in Matrix33 a, in Matrix3x2 b) => a * FromAffine(b);

    public static Matrix33 operator *(in Matrix3x2 a, in Matrix33 b) => FromAffine(a) * b;

    public Vector2 Apply(Vector2 p)
    {
        var w = p.X * M13 + p.Y * M23 + M33;
        // Behind the viewer (w <= 0) there is no picture; keep the point finite.
        if (MathF.Abs(w) < 1e-6f)
            w = w < 0 ? -1e-6f : 1e-6f;
        return new Vector2((p.X * M11 + p.Y * M21 + M31) / w, (p.X * M12 + p.Y * M22 + M32) / w);
    }

    public static bool Invert(in Matrix33 m, out Matrix33 inverse)
    {
        var c11 = m.M22 * m.M33 - m.M23 * m.M32;
        var c12 = m.M13 * m.M32 - m.M12 * m.M33;
        var c13 = m.M12 * m.M23 - m.M13 * m.M22;
        var determinant = m.M11 * c11 + m.M21 * c12 + m.M31 * c13;
        if (MathF.Abs(determinant) < 1e-12f || !float.IsFinite(determinant))
        {
            inverse = Identity;
            return false;
        }
        var d = 1 / determinant;
        inverse = new Matrix33(
            c11 * d, c12 * d, c13 * d,
            (m.M23 * m.M31 - m.M21 * m.M33) * d, (m.M11 * m.M33 - m.M13 * m.M31) * d, (m.M13 * m.M21 - m.M11 * m.M23) * d,
            (m.M21 * m.M32 - m.M22 * m.M31) * d, (m.M12 * m.M31 - m.M11 * m.M32) * d, (m.M11 * m.M22 - m.M12 * m.M21) * d);
        return true;
    }

    /// <summary>The affine transform that matches this one near the origin of the plane; exact without perspective.</summary>
    public Matrix3x2 Affine
    {
        get
        {
            var w = MathF.Abs(M33) < 1e-6f ? 1 : M33;
            return new Matrix3x2(M11 / w, M12 / w, M21 / w, M22 / w, M31 / w, M32 / w);
        }
    }
}
