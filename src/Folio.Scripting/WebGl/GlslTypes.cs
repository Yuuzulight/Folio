using System.Numerics;

namespace Folio.Scripting.WebGl;

internal enum GlslBase
{
    Void, Float, Int, Bool,
    Vec2, Vec3, Vec4, IVec2, IVec3, IVec4, BVec2, BVec3, BVec4,
    Mat2, Mat3, Mat4,
    Sampler2D, SamplerCube, Struct,
}

internal sealed class GlslStructDef(string name)
{
    public string Name { get; } = name;
    public List<(string Name, GlslType Type)> Fields { get; } = [];

    public int Slots => Fields.Sum(f => f.Type.Slots);

    public int OffsetOf(string field)
    {
        var offset = 0;
        foreach (var (name, type) in Fields)
        {
            if (name == field) return offset;
            offset += type.Slots;
        }
        return -1;
    }
}

/// <summary>A GLSL type; every value of a single-slot type (scalars, vectors, matrices, samplers) lives in one <see cref="Val"/>, an aggregate in a run of them.</summary>
internal sealed record GlslType(GlslBase Base, int ArraySize = 0, GlslStructDef? Struct = null)
{
    public static readonly GlslType Void = new(GlslBase.Void);
    public static readonly GlslType Float = new(GlslBase.Float);
    public static readonly GlslType Int = new(GlslBase.Int);
    public static readonly GlslType Bool = new(GlslBase.Bool);
    public static readonly GlslType Vec2 = new(GlslBase.Vec2);
    public static readonly GlslType Vec3 = new(GlslBase.Vec3);
    public static readonly GlslType Vec4 = new(GlslBase.Vec4);
    public static readonly GlslType Mat2 = new(GlslBase.Mat2);
    public static readonly GlslType Mat3 = new(GlslBase.Mat3);
    public static readonly GlslType Mat4 = new(GlslBase.Mat4);
    public static readonly GlslType Sampler2D = new(GlslBase.Sampler2D);

    public bool IsArray => ArraySize > 0;
    public bool IsStruct => Base == GlslBase.Struct && !IsArray;

    /// <summary>The type of one element of an array type.</summary>
    public GlslType Element => this with { ArraySize = 0 };

    public bool IsAggregate => IsArray || Base == GlslBase.Struct;

    /// <summary>How many <see cref="Val"/> slots a value takes.</summary>
    public int Slots => IsArray ? ArraySize * Element.Slots : Base == GlslBase.Struct ? Math.Max(1, Struct!.Slots) : 1;

    public bool IsScalar => !IsArray && Base is GlslBase.Float or GlslBase.Int or GlslBase.Bool;
    public bool IsVector => !IsArray && Base is >= GlslBase.Vec2 and <= GlslBase.BVec4;
    public bool IsMatrix => !IsArray && Base is GlslBase.Mat2 or GlslBase.Mat3 or GlslBase.Mat4;
    public bool IsSampler => !IsArray && Base is GlslBase.Sampler2D or GlslBase.SamplerCube;
    public bool IsNumeric => (IsScalar || IsVector || IsMatrix) && ScalarKind != GlslBase.Bool;

    /// <summary>float, int or bool: what a scalar, vector or matrix is made of.</summary>
    public GlslBase ScalarKind => Base switch
    {
        GlslBase.Int or GlslBase.IVec2 or GlslBase.IVec3 or GlslBase.IVec4 => GlslBase.Int,
        GlslBase.Bool or GlslBase.BVec2 or GlslBase.BVec3 or GlslBase.BVec4 => GlslBase.Bool,
        _ => GlslBase.Float,
    };

    /// <summary>Components of a scalar (1), vector (2-4) or the side of a matrix.</summary>
    public int Size => Base switch
    {
        GlslBase.Vec2 or GlslBase.IVec2 or GlslBase.BVec2 or GlslBase.Mat2 => 2,
        GlslBase.Vec3 or GlslBase.IVec3 or GlslBase.BVec3 or GlslBase.Mat3 => 3,
        GlslBase.Vec4 or GlslBase.IVec4 or GlslBase.BVec4 or GlslBase.Mat4 => 4,
        _ => 1,
    };

    /// <summary>The number of float components in the value as the API counts them (a mat3 has nine).</summary>
    public int Components => IsMatrix ? Size * Size : Size;

    public static GlslType Vector(GlslBase kind, int size) => (kind, size) switch
    {
        (GlslBase.Float, 1) => Float,
        (GlslBase.Int, 1) => Int,
        (GlslBase.Bool, 1) => Bool,
        (GlslBase.Float, 2) => new(GlslBase.Vec2),
        (GlslBase.Float, 3) => new(GlslBase.Vec3),
        (GlslBase.Float, 4) => new(GlslBase.Vec4),
        (GlslBase.Int, 2) => new(GlslBase.IVec2),
        (GlslBase.Int, 3) => new(GlslBase.IVec3),
        (GlslBase.Int, 4) => new(GlslBase.IVec4),
        (GlslBase.Bool, 2) => new(GlslBase.BVec2),
        (GlslBase.Bool, 3) => new(GlslBase.BVec3),
        (GlslBase.Bool, 4) => new(GlslBase.BVec4),
        _ => throw new ArgumentOutOfRangeException(nameof(size)),
    };

    public static GlslType Matrix(int size) => size switch { 2 => Mat2, 3 => Mat3, _ => Mat4 };

    public bool SameAs(GlslType other) =>
        Base == other.Base && ArraySize == other.ArraySize && (Base != GlslBase.Struct || ReferenceEquals(Struct, other.Struct));

    public override string ToString() => (Base switch
    {
        GlslBase.Struct => Struct!.Name,
        GlslBase.Sampler2D => "sampler2D",
        GlslBase.SamplerCube => "samplerCube",
        _ => Base.ToString().ToLowerInvariant(),
    }) + (IsArray ? $"[{ArraySize}]" : "");

    public static bool TryParse(string name, out GlslType type)
    {
        type = name switch
        {
            "void" => Void,
            "float" => Float,
            "int" => Int,
            "bool" => Bool,
            "vec2" => Vec2,
            "vec3" => Vec3,
            "vec4" => Vec4,
            "ivec2" => new(GlslBase.IVec2),
            "ivec3" => new(GlslBase.IVec3),
            "ivec4" => new(GlslBase.IVec4),
            "bvec2" => new(GlslBase.BVec2),
            "bvec3" => new(GlslBase.BVec3),
            "bvec4" => new(GlslBase.BVec4),
            "mat2" or "mat2x2" => Mat2,
            "mat3" or "mat3x3" => Mat3,
            "mat4" or "mat4x4" => Mat4,
            "sampler2D" => Sampler2D,
            "samplerCube" => new(GlslBase.SamplerCube),
            _ => null!,
        };
        return type is not null;
    }
}

/// <summary>
/// One shader value: a scalar or vector in <see cref="C0"/> (x, y, z, w), or a matrix as columns <see cref="C0"/> to
/// <see cref="C3"/>. Integers and booleans are kept as floats and truncated or clamped to 0/1 where the language says so.
/// </summary>
internal struct Val
{
    public Vector4 C0, C1, C2, C3;

    public Val(float x) { C0 = new Vector4(x, 0, 0, 0); C1 = default; C2 = default; C3 = default; }

    public Val(Vector4 v) { C0 = v; C1 = default; C2 = default; C3 = default; }

    public Val(float x, float y, float z, float w) { C0 = new Vector4(x, y, z, w); C1 = default; C2 = default; C3 = default; }

    public float X { readonly get => C0.X; set => C0.X = value; }

    public readonly float Get(int index) => index switch { 0 => C0.X, 1 => C0.Y, 2 => C0.Z, _ => C0.W };

    public void Set(int index, float value)
    {
        switch (index)
        {
            case 0: C0.X = value; break;
            case 1: C0.Y = value; break;
            case 2: C0.Z = value; break;
            default: C0.W = value; break;
        }
    }

    /// <summary>Component <paramref name="index"/> of a column-major matrix flattened (column * size + row not assumed): column, row.</summary>
    public readonly float Cell(int column, int row)
    {
        var c = column switch { 0 => C0, 1 => C1, 2 => C2, _ => C3 };
        return row switch { 0 => c.X, 1 => c.Y, 2 => c.Z, _ => c.W };
    }

    public void SetCell(int column, int row, float value)
    {
        ref var c = ref (column == 0 ? ref C0 : ref (column == 1 ? ref C1 : ref (column == 2 ? ref C2 : ref C3)));
        switch (row)
        {
            case 0: c.X = value; break;
            case 1: c.Y = value; break;
            case 2: c.Z = value; break;
            default: c.W = value; break;
        }
    }

    public readonly Vector4 Column(int index) => index switch { 0 => C0, 1 => C1, 2 => C2, _ => C3 };

    public void SetColumn(int index, Vector4 value)
    {
        switch (index)
        {
            case 0: C0 = value; break;
            case 1: C1 = value; break;
            case 2: C2 = value; break;
            default: C3 = value; break;
        }
    }
}
