using System.Globalization;
using System.Numerics;

namespace Folio.Style;

/// <summary>One computed transform function (https://www.w3.org/TR/css-transforms-2/#transform-functions), in the
/// primitive form it takes in the matrix: lengths in px, percentages kept for the reference box, angles in degrees.</summary>
internal abstract record TransformOp
{
    /// <summary>The function's matrix in row-vector form (points multiply on the left), for a reference box size.</summary>
    public abstract Matrix4x4 ToMatrix(float width, float height);

    protected static string N(float value) => Math.Round(value, 4).ToString(CultureInfo.InvariantCulture);

    protected static float Radians(float degrees) => degrees * MathF.PI / 180;
}

internal sealed record TranslateOp(LengthPercentage X, LengthPercentage Y, float Z) : TransformOp
{
    public override Matrix4x4 ToMatrix(float width, float height) => Matrix4x4.CreateTranslation(X.Resolve(width), Y.Resolve(height), Z);

    public override string ToString() => Z == 0 ? $"translate({X}, {Y})" : $"translate3d({X}, {Y}, {N(Z)}px)";
}

internal sealed record ScaleOp(float X, float Y, float Z) : TransformOp
{
    public override Matrix4x4 ToMatrix(float width, float height) => Matrix4x4.CreateScale(X, Y, Z);

    public override string ToString() => Z == 1 ? $"scale({N(X)}, {N(Y)})" : $"scale3d({N(X)}, {N(Y)}, {N(Z)})";
}

/// <summary>A rotation about an axis; 2D rotations are about (0, 0, 1). The axis is normalised when the matrix is built.</summary>
internal sealed record RotateOp(float X, float Y, float Z, float Degrees) : TransformOp
{
    public override Matrix4x4 ToMatrix(float width, float height)
    {
        var axis = new Vector3(X, Y, Z);
        // A zero axis is no rotation (https://www.w3.org/TR/css-transforms-2/#funcdef-rotate3d).
        return axis == Vector3.Zero ? Matrix4x4.Identity : Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(axis), Radians(Degrees));
    }

    public override string ToString() => (X, Y) == (0, 0) && Z > 0 ? $"rotate({N(Degrees)}deg)" : $"rotate3d({N(X)}, {N(Y)}, {N(Z)}, {N(Degrees)}deg)";
}

internal sealed record SkewOp(float XDegrees, float YDegrees) : TransformOp
{
    public override Matrix4x4 ToMatrix(float width, float height) => new(
        1, MathF.Tan(Radians(YDegrees)), 0, 0,
        MathF.Tan(Radians(XDegrees)), 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1);

    public override string ToString() => $"skew({N(XDegrees)}deg, {N(YDegrees)}deg)";
}

/// <summary><c>matrix()</c> and <c>matrix3d()</c>: CSS lists the values column by column, which is this row-vector form's row order.</summary>
internal sealed record MatrixOp(Matrix4x4 Matrix) : TransformOp
{
    public override Matrix4x4 ToMatrix(float width, float height) => Matrix;

    public bool Is2D => Matrix is { M13: 0, M14: 0, M23: 0, M24: 0, M31: 0, M32: 0, M33: 1, M34: 0, M43: 0, M44: 1 };

    public override string ToString()
    {
        var m = Matrix;
        return Is2D
            ? $"matrix({N(m.M11)}, {N(m.M12)}, {N(m.M21)}, {N(m.M22)}, {N(m.M41)}, {N(m.M42)})"
            : $"matrix3d({string.Join(", ", new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 }.Select(N))})";
    }
}

/// <summary><c>perspective()</c>; a null distance is <c>none</c> (no perspective).</summary>
internal sealed record PerspectiveOp(float? Distance) : TransformOp
{
    public override Matrix4x4 ToMatrix(float width, float height) =>
        Distance is { } d ? Matrix4x4.Identity with { M34 = -1 / Math.Max(d, 1) } : Matrix4x4.Identity;

    public override string ToString() => Distance is { } d ? $"perspective({N(d)}px)" : "perspective(none)";
}

/// <summary>A computed transform list (also used for the individual properties, which hold at most one function); empty is <c>none</c>.</summary>
internal sealed class TransformList(IReadOnlyList<TransformOp> ops) : IEquatable<TransformList>
{
    public static TransformList None { get; } = new([]);

    public IReadOnlyList<TransformOp> Ops { get; } = ops;

    public bool IsNone => Ops.Count == 0;

    public bool Equals(TransformList? other) => other is not null && Ops.SequenceEqual(other.Ops);

    public override bool Equals(object? obj) => Equals(obj as TransformList);

    public override int GetHashCode() => Ops.Count == 0 ? 0 : HashCode.Combine(Ops.Count, Ops[0]);

    public override string ToString() => IsNone ? "none" : string.Join(" ", Ops);
}

/// <summary>A computed <c>transform-origin</c>: percentages of the reference box, and a z offset in px.</summary>
internal readonly record struct TransformOrigin(LengthPercentage X, LengthPercentage Y, float Z)
{
    public static TransformOrigin Center { get; } = new(new LengthPercentage(0, 50), new LengthPercentage(0, 50), 0);

    public override string ToString() =>
        Z == 0 ? $"{X} {Y}" : $"{X} {Y} {Math.Round(Z, 3).ToString(CultureInfo.InvariantCulture)}px";
}

/// <summary>Transform properties (not inherited): https://www.w3.org/TR/css-transforms-2/.</summary>
/// <param name="Perspective">The <c>perspective</c> distance in px the box views its children from; null is none.</param>
internal sealed record TransformGroup(TransformList Transform, TransformList Translate, TransformList Rotate, TransformList Scale, TransformOrigin Origin,
                                      float? Perspective = null, TransformOrigin PerspectiveOrigin = default)
{
    public static TransformGroup Initial { get; } = new(TransformList.None, TransformList.None, TransformList.None, TransformList.None, TransformOrigin.Center,
        null, TransformOrigin.Center);

    /// <summary>
    /// The perspective matrix for the box's children (https://www.w3.org/TR/css-transforms-2/#perspective-matrix),
    /// relative to the box's top-left corner; null without a perspective.
    /// </summary>
    public Matrix4x4? PerspectiveMatrix(float width, float height)
    {
        if (Perspective is not { } d)
            return null;
        var (ox, oy) = (PerspectiveOrigin.X.Resolve(width), PerspectiveOrigin.Y.Resolve(height));
        return Matrix4x4.CreateTranslation(-ox, -oy, 0) * new PerspectiveOp(d).ToMatrix(width, height) * Matrix4x4.CreateTranslation(ox, oy, 0);
    }

    /// <summary>Whether any of the transform properties is set: the box is transformed and establishes a stacking context.</summary>
    public bool IsTransformed => !(Transform.IsNone && Translate.IsNone && Rotate.IsNone && Scale.IsNone);

    /// <summary>
    /// The current transformation matrix for a reference box of this size, relative to the box's top-left corner
    /// (https://www.w3.org/TR/css-transforms-2/#ctm): origin, translate, rotate, scale, then each transform function,
    /// then minus origin. In row-vector form the last step multiplies first.
    /// </summary>
    public Matrix4x4 Matrix(float width, float height)
    {
        var (ox, oy) = (Origin.X.Resolve(width), Origin.Y.Resolve(height));
        var matrix = Matrix4x4.CreateTranslation(ox, oy, Origin.Z);
        foreach (var op in Translate.Ops.Concat(Rotate.Ops).Concat(Scale.Ops).Concat(Transform.Ops))
            matrix = op.ToMatrix(width, height) * matrix;
        return Matrix4x4.CreateTranslation(-ox, -oy, -Origin.Z) * matrix;
    }

    /// <summary>
    /// The matrix flattened to 2D (https://www.w3.org/TR/css-transforms-2/#2d-matrix): what a 2D canvas applies.
    /// </summary>
    // ponytail: drops perspective (the w divide) instead of projecting; add projection when 3D artifacts need it.
    public Matrix3x2 Matrix2D(float width, float height)
    {
        var m = Matrix(width, height);
        return new Matrix3x2(m.M11, m.M12, m.M21, m.M22, m.M41, m.M42);
    }
}
