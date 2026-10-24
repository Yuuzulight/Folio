using System.Numerics;

namespace Folio.Scripting.WebGl;

/// <summary>The built-in functions of GLSL ES 1.00 and the 3.00 subset the pipeline supports.</summary>
internal static class GlslBuiltins
{
    private static CExpr R(GlslType t, Func<ShaderContext, Val> v) => new() { Type = t, V = v };

    private static bool IsGen(GlslType t, bool allowInt = false) =>
        (t.IsScalar || t.IsVector) && (t.ScalarKind == GlslBase.Float || (allowInt && t.ScalarKind == GlslBase.Int));

    private static Vector4 Lanes(Vector4 x, Func<float, float> f) => new(f(x.X), f(x.Y), f(x.Z), f(x.W));

    private static float DotN(Vector4 a, Vector4 b, int n)
    {
        var s = a.X * b.X;
        if (n > 1) s += a.Y * b.Y;
        if (n > 2) s += a.Z * b.Z;
        if (n > 3) s += a.W * b.W;
        return s;
    }

    private static CExpr ToFloat(GlslCompiler compiler, CExpr e, int line)
    {
        if (e.Type.ScalarKind == GlslBase.Int && !e.Type.IsMatrix && !e.Type.IsArray)
            return new CExpr { Type = GlslType.Vector(GlslBase.Float, e.Type.Size), V = e.V, Source = e.Source, SourceSwizzle = e.SourceSwizzle, Ast = e.Ast };
        return e;
    }

    public static bool TryCompile(GlslCompiler compiler, string name, List<CExpr> args, CallExpr call, out CExpr result)
    {
        result = null!;
        var line = call.Line;
        CExpr Bad() => throw GlslCompiler.Fail(line, $"'{name}' : no matching overloaded function found");

        // one generic argument, component-wise
        CExpr? Unary(Func<float, float> f, bool allowInt = false)
        {
            if (args.Count != 1) return null;
            var a = args[0];
            if (allowInt && a.Type.ScalarKind == GlslBase.Int && IsGen(a.Type, true)) { }
            else a = ToFloat(compiler, a, line);
            if (!IsGen(a.Type, allowInt)) return null;
            var v = a.V!;
            return R(a.Type, c => new Val(Lanes(v(c).C0, f)));
        }

        // n generic arguments of one shape, or scalars that spread to it
        CExpr? Multi(Func<Vector4[], Vector4> f, int count, bool allowInt = false, int scalarFrom = 1)
        {
            if (args.Count != count) return null;
            var list = args.Select(a => allowInt && a.Type.ScalarKind == GlslBase.Int ? a : ToFloat(compiler, a, line)).ToList();
            if (list.Any(a => !IsGen(a.Type, allowInt))) return null;
            var kind = list[0].Type.ScalarKind;
            if (list.Any(a => a.Type.ScalarKind != kind)) return null;
            var size = list.Max(a => a.Type.Size);
            if (list.Any(a => a.Type.Size != size && a.Type.Size != 1)) return null;
            var resultType = GlslType.Vector(kind, size);
            var evals = list.Select(a => a.V!).ToArray();
            var spread = list.Select(a => a.Type.Size == 1 && size > 1).ToArray();
            return R(resultType, c =>
            {
                var xs = new Vector4[evals.Length];
                for (var i = 0; i < xs.Length; i++)
                {
                    var x = evals[i](c).C0;
                    xs[i] = spread[i] ? new Vector4(x.X) : x;
                }
                return new Val(f(xs));
            });
        }

        CExpr? found = name switch
        {
            "radians" => Unary(x => x * (MathF.PI / 180)),
            "degrees" => Unary(x => x * (180 / MathF.PI)),
            "sin" => Unary(MathF.Sin),
            "cos" => Unary(MathF.Cos),
            "tan" => Unary(MathF.Tan),
            "asin" => Unary(x => MathF.Asin(Math.Clamp(x, -1, 1))),
            "acos" => Unary(x => MathF.Acos(Math.Clamp(x, -1, 1))),
            "sinh" => Unary(MathF.Sinh),
            "cosh" => Unary(MathF.Cosh),
            "tanh" => Unary(MathF.Tanh),
            "asinh" => Unary(MathF.Asinh),
            "acosh" => Unary(MathF.Acosh),
            "atanh" => Unary(MathF.Atanh),
            "exp" => Unary(MathF.Exp),
            "log" => Unary(MathF.Log),
            "exp2" => Unary(x => MathF.Pow(2, x)),
            "log2" => Unary(MathF.Log2),
            "sqrt" => Unary(MathF.Sqrt),
            "inversesqrt" => Unary(x => 1 / MathF.Sqrt(x)),
            "abs" => Unary(MathF.Abs, allowInt: true),
            "sign" => Unary(x => x > 0 ? 1 : x < 0 ? -1 : 0, allowInt: true),
            "floor" => Unary(MathF.Floor),
            "ceil" => Unary(MathF.Ceiling),
            "fract" => Unary(x => x - MathF.Floor(x)),
            "trunc" => Unary(MathF.Truncate),
            "round" => Unary(x => MathF.Round(x, MidpointRounding.AwayFromZero)),
            "roundEven" => Unary(x => MathF.Round(x, MidpointRounding.ToEven)),
            "atan" when args.Count == 1 => Unary(MathF.Atan),
            "atan" => Multi(a => new Vector4(MathF.Atan2(a[0].X, a[1].X), MathF.Atan2(a[0].Y, a[1].Y), MathF.Atan2(a[0].Z, a[1].Z), MathF.Atan2(a[0].W, a[1].W)), 2),
            "pow" => Multi(a => new Vector4(MathF.Pow(a[0].X, a[1].X), MathF.Pow(a[0].Y, a[1].Y), MathF.Pow(a[0].Z, a[1].Z), MathF.Pow(a[0].W, a[1].W)), 2),
            "mod" => Multi(a => a[0] - a[1] * new Vector4(MathF.Floor(a[0].X / a[1].X), MathF.Floor(a[0].Y / a[1].Y), MathF.Floor(a[0].Z / a[1].Z), MathF.Floor(a[0].W / a[1].W)), 2),
            "min" => Multi(a => Vector4.Min(a[0], a[1]), 2, allowInt: true),
            "max" => Multi(a => Vector4.Max(a[0], a[1]), 2, allowInt: true),
            "clamp" => Multi(a => Vector4.Min(Vector4.Max(a[0], a[1]), a[2]), 3, allowInt: true),
            "mix" when args.Count == 3 && args[2].Type.ScalarKind == GlslBase.Bool => MixSelect(args, line),
            "mix" => Multi(a => a[0] * (Vector4.One - a[2]) + a[1] * a[2], 3),
            "step" => Multi(a => new Vector4(a[1].X < a[0].X ? 0 : 1, a[1].Y < a[0].Y ? 0 : 1, a[1].Z < a[0].Z ? 0 : 1, a[1].W < a[0].W ? 0 : 1), 2),
            "smoothstep" => Multi(a =>
            {
                var t = Vector4.Min(Vector4.Max((a[2] - a[0]) / (a[1] - a[0]), Vector4.Zero), Vector4.One);
                return t * t * (new Vector4(3) - new Vector4(2) * t);
            }, 3),
            "isnan" => Predicate(args, line, x => float.IsNaN(x)),
            "isinf" => Predicate(args, line, x => float.IsInfinity(x)),
            "length" => Length(args, line),
            "distance" => Distance(args, line),
            "dot" => Dot(args, line),
            "cross" => Cross(args, line),
            "normalize" => Normalize(args, line),
            "faceforward" => FaceForward(args, line),
            "reflect" => Reflect(args, line),
            "refract" => Refract(args, line),
            "matrixCompMult" => MatrixCompMult(args, line),
            "transpose" => Transpose(args, line),
            "determinant" => Determinant(args, line),
            "inverse" => Inverse(args, line),
            "outerProduct" => OuterProduct(args, line),
            "lessThan" => Compare(args, line, (x, y) => x < y),
            "lessThanEqual" => Compare(args, line, (x, y) => x <= y),
            "greaterThan" => Compare(args, line, (x, y) => x > y),
            "greaterThanEqual" => Compare(args, line, (x, y) => x >= y),
            "equal" => Compare(args, line, (x, y) => x == y, allowBool: true),
            "notEqual" => Compare(args, line, (x, y) => x != y, allowBool: true),
            "any" => AnyAll(args, line, any: true),
            "all" => AnyAll(args, line, any: false),
            "not" => Not(args, line),
            "texture2D" or "texture" or "texture2DProj" or "textureProj" or "texture2DLod" or "textureLod" or "textureCube" or "textureCubeLod"
                => Texture(compiler, name, args, line),
            "texelFetch" => TexelFetch(args, line),
            "textureSize" => TextureSize(args, line),
            "dFdx" or "dFdy" or "fwidth" => Derivative(compiler, name, args, line),
            _ => null,
        };
        if (found is null)
        {
            if (!IsKnown(name)) return false;
            Bad();
        }
        result = found!;
        return true;
    }

    private static bool IsKnown(string name) => name is "radians" or "degrees" or "sin" or "cos" or "tan" or "asin" or "acos" or "sinh" or "cosh" or "tanh"
        or "asinh" or "acosh" or "atanh" or "exp" or "log" or "exp2" or "log2" or "sqrt" or "inversesqrt" or "abs" or "sign" or "floor" or "ceil" or "fract"
        or "trunc" or "round" or "roundEven" or "atan" or "pow" or "mod" or "min" or "max" or "clamp" or "mix" or "step" or "smoothstep" or "isnan" or "isinf"
        or "length" or "distance" or "dot" or "cross" or "normalize" or "faceforward" or "reflect" or "refract" or "matrixCompMult" or "transpose"
        or "determinant" or "inverse" or "outerProduct" or "lessThan" or "lessThanEqual" or "greaterThan" or "greaterThanEqual" or "equal" or "notEqual"
        or "any" or "all" or "not" or "texture2D" or "texture" or "texture2DProj" or "textureProj" or "texture2DLod" or "textureLod" or "textureCube"
        or "textureCubeLod" or "texelFetch" or "textureSize" or "dFdx" or "dFdy" or "fwidth";

    private static CExpr? MixSelect(List<CExpr> args, int line)
    {
        var a = args[0]; var b = args[1]; var s = args[2];
        if (!a.Type.SameAs(b.Type) || !IsGen(a.Type) || s.Type.Size != a.Type.Size) return null;
        var av = a.V!; var bv = b.V!; var sv = s.V!;
        return R(a.Type, c =>
        {
            var x = av(c).C0; var y = bv(c).C0; var t = sv(c).C0;
            return new Val(new Vector4(t.X != 0 ? y.X : x.X, t.Y != 0 ? y.Y : x.Y, t.Z != 0 ? y.Z : x.Z, t.W != 0 ? y.W : x.W));
        });
    }

    private static CExpr? Predicate(List<CExpr> args, int line, Func<float, bool> f)
    {
        if (args.Count != 1 || !IsGen(args[0].Type)) return null;
        var v = args[0].V!;
        return R(GlslType.Vector(GlslBase.Bool, args[0].Type.Size), c =>
        {
            var x = v(c).C0;
            return new Val(new Vector4(f(x.X) ? 1 : 0, f(x.Y) ? 1 : 0, f(x.Z) ? 1 : 0, f(x.W) ? 1 : 0));
        });
    }

    private static CExpr? Length(List<CExpr> args, int line)
    {
        if (args.Count != 1 || !IsGen(args[0].Type)) return null;
        var n = args[0].Type.Size;
        var v = args[0].V!;
        return R(GlslType.Float, c => { var x = v(c).C0; return new Val(MathF.Sqrt(DotN(x, x, n))); });
    }

    private static CExpr? Distance(List<CExpr> args, int line)
    {
        if (args.Count != 2 || !IsGen(args[0].Type) || !args[0].Type.SameAs(args[1].Type)) return null;
        var n = args[0].Type.Size;
        var a = args[0].V!; var b = args[1].V!;
        return R(GlslType.Float, c => { var d = a(c).C0 - b(c).C0; return new Val(MathF.Sqrt(DotN(d, d, n))); });
    }

    private static CExpr? Dot(List<CExpr> args, int line)
    {
        if (args.Count != 2 || !IsGen(args[0].Type, true) || args[0].Type.Size != args[1].Type.Size) return null;
        var n = args[0].Type.Size;
        var a = args[0].V!; var b = args[1].V!;
        return R(GlslType.Float, c => new Val(DotN(a(c).C0, b(c).C0, n)));
    }

    private static CExpr? Cross(List<CExpr> args, int line)
    {
        if (args.Count != 2 || args[0].Type.Base != GlslBase.Vec3 || args[1].Type.Base != GlslBase.Vec3) return null;
        var a = args[0].V!; var b = args[1].V!;
        return R(GlslType.Vec3, c =>
        {
            var x = a(c).C0; var y = b(c).C0;
            return new Val(x.Y * y.Z - y.Y * x.Z, x.Z * y.X - y.Z * x.X, x.X * y.Y - y.X * x.Y, 0);
        });
    }

    private static CExpr? Normalize(List<CExpr> args, int line)
    {
        if (args.Count != 1 || !IsGen(args[0].Type)) return null;
        var n = args[0].Type.Size;
        var v = args[0].V!;
        return R(args[0].Type, c =>
        {
            var x = v(c).C0;
            var len = MathF.Sqrt(DotN(x, x, n));
            return new Val(len == 0 ? x : x / len);
        });
    }

    private static CExpr? FaceForward(List<CExpr> args, int line)
    {
        if (args.Count != 3 || !IsGen(args[0].Type) || !args[0].Type.SameAs(args[1].Type) || !args[0].Type.SameAs(args[2].Type)) return null;
        var n = args[0].Type.Size;
        var a = args[0].V!; var b = args[1].V!; var d = args[2].V!;
        return R(args[0].Type, c => new Val(DotN(d(c).C0, b(c).C0, n) < 0 ? a(c).C0 : -a(c).C0));
    }

    private static CExpr? Reflect(List<CExpr> args, int line)
    {
        if (args.Count != 2 || !IsGen(args[0].Type) || !args[0].Type.SameAs(args[1].Type)) return null;
        var n = args[0].Type.Size;
        var i = args[0].V!; var nrm = args[1].V!;
        return R(args[0].Type, c =>
        {
            var iv = i(c).C0; var nv = nrm(c).C0;
            return new Val(iv - nv * (2 * DotN(nv, iv, n)));
        });
    }

    private static CExpr? Refract(List<CExpr> args, int line)
    {
        if (args.Count != 3 || !IsGen(args[0].Type) || !args[0].Type.SameAs(args[1].Type) || !args[2].Type.IsScalar) return null;
        var n = args[0].Type.Size;
        var i = args[0].V!; var nrm = args[1].V!; var e = args[2].V!;
        return R(args[0].Type, c =>
        {
            var iv = i(c).C0; var nv = nrm(c).C0; var eta = e(c).X;
            var d = DotN(nv, iv, n);
            var k = 1 - eta * eta * (1 - d * d);
            return new Val(k < 0 ? Vector4.Zero : iv * eta - nv * (eta * d + MathF.Sqrt(k)));
        });
    }

    private static CExpr? MatrixCompMult(List<CExpr> args, int line)
    {
        if (args.Count != 2 || !args[0].Type.IsMatrix || !args[0].Type.SameAs(args[1].Type)) return null;
        var a = args[0].V!; var b = args[1].V!;
        return R(args[0].Type, c =>
        {
            var x = a(c); var y = b(c);
            return new Val { C0 = x.C0 * y.C0, C1 = x.C1 * y.C1, C2 = x.C2 * y.C2, C3 = x.C3 * y.C3 };
        });
    }

    private static CExpr? Transpose(List<CExpr> args, int line)
    {
        if (args.Count != 1 || !args[0].Type.IsMatrix) return null;
        var n = args[0].Type.Size;
        var v = args[0].V!;
        return R(args[0].Type, c =>
        {
            var m = v(c);
            var r = new Val();
            for (var col = 0; col < n; col++) for (var row = 0; row < n; row++) r.SetCell(col, row, m.Cell(row, col));
            return r;
        });
    }

    private static float Det(Val m, int n)
    {
        if (n == 2) return m.Cell(0, 0) * m.Cell(1, 1) - m.Cell(1, 0) * m.Cell(0, 1);
        if (n == 3)
        {
            return m.Cell(0, 0) * (m.Cell(1, 1) * m.Cell(2, 2) - m.Cell(2, 1) * m.Cell(1, 2))
                 - m.Cell(1, 0) * (m.Cell(0, 1) * m.Cell(2, 2) - m.Cell(2, 1) * m.Cell(0, 2))
                 + m.Cell(2, 0) * (m.Cell(0, 1) * m.Cell(1, 2) - m.Cell(1, 1) * m.Cell(0, 2));
        }
        var mat = ToMatrix4(m);
        return mat.GetDeterminant();
    }

    private static Matrix4x4 ToMatrix4(Val m) => new(
        m.Cell(0, 0), m.Cell(0, 1), m.Cell(0, 2), m.Cell(0, 3),
        m.Cell(1, 0), m.Cell(1, 1), m.Cell(1, 2), m.Cell(1, 3),
        m.Cell(2, 0), m.Cell(2, 1), m.Cell(2, 2), m.Cell(2, 3),
        m.Cell(3, 0), m.Cell(3, 1), m.Cell(3, 2), m.Cell(3, 3));

    private static CExpr? Determinant(List<CExpr> args, int line)
    {
        if (args.Count != 1 || !args[0].Type.IsMatrix) return null;
        var n = args[0].Type.Size;
        var v = args[0].V!;
        return R(GlslType.Float, c => new Val(Det(v(c), n)));
    }

    private static CExpr? Inverse(List<CExpr> args, int line)
    {
        if (args.Count != 1 || !args[0].Type.IsMatrix) return null;
        var n = args[0].Type.Size;
        var v = args[0].V!;
        return R(args[0].Type, c =>
        {
            var m = v(c);
            if (n == 4)
            {
                return Matrix4x4.Invert(ToMatrix4(m), out var inv)
                    ? FromMatrix4(inv)
                    : new Val { C0 = new Vector4(float.NaN), C1 = new Vector4(float.NaN), C2 = new Vector4(float.NaN), C3 = new Vector4(float.NaN) };
            }
            var det = Det(m, n);
            var r = new Val();
            if (n == 2)
            {
                r.SetCell(0, 0, m.Cell(1, 1) / det); r.SetCell(0, 1, -m.Cell(0, 1) / det);
                r.SetCell(1, 0, -m.Cell(1, 0) / det); r.SetCell(1, 1, m.Cell(0, 0) / det);
                return r;
            }
            // 3x3 by cofactors
            for (var col = 0; col < 3; col++)
            {
                for (var row = 0; row < 3; row++)
                {
                    var c1 = (col + 1) % 3; var c2 = (col + 2) % 3; var r1 = (row + 1) % 3; var r2 = (row + 2) % 3;
                    var cofactor = m.Cell(c1, r1) * m.Cell(c2, r2) - m.Cell(c2, r1) * m.Cell(c1, r2);
                    r.SetCell(row, col, cofactor / det);
                }
            }
            return r;
        });
    }

    private static Val FromMatrix4(Matrix4x4 m) => new()
    {
        C0 = new Vector4(m.M11, m.M12, m.M13, m.M14),
        C1 = new Vector4(m.M21, m.M22, m.M23, m.M24),
        C2 = new Vector4(m.M31, m.M32, m.M33, m.M34),
        C3 = new Vector4(m.M41, m.M42, m.M43, m.M44),
    };

    private static CExpr? OuterProduct(List<CExpr> args, int line)
    {
        if (args.Count != 2 || !args[0].Type.IsVector || !args[0].Type.SameAs(args[1].Type) || args[0].Type.ScalarKind != GlslBase.Float) return null;
        var n = args[0].Type.Size;
        var a = args[0].V!; var b = args[1].V!;
        return R(GlslType.Matrix(n), c =>
        {
            var x = a(c).C0; var y = b(c).C0;
            var r = new Val();
            for (var col = 0; col < n; col++) r.SetColumn(col, x * Lane(y, col));
            return r;
        });
    }

    private static CExpr? Compare(List<CExpr> args, int line, Func<float, float, bool> f, bool allowBool = false)
    {
        if (args.Count != 2 || !args[0].Type.IsVector || !args[0].Type.SameAs(args[1].Type)) return null;
        if (args[0].Type.ScalarKind == GlslBase.Bool && !allowBool) return null;
        var a = args[0].V!; var b = args[1].V!;
        return R(GlslType.Vector(GlslBase.Bool, args[0].Type.Size), c =>
        {
            var x = a(c).C0; var y = b(c).C0;
            return new Val(new Vector4(f(x.X, y.X) ? 1 : 0, f(x.Y, y.Y) ? 1 : 0, f(x.Z, y.Z) ? 1 : 0, f(x.W, y.W) ? 1 : 0));
        });
    }

    private static CExpr? AnyAll(List<CExpr> args, int line, bool any)
    {
        if (args.Count != 1 || args[0].Type.ScalarKind != GlslBase.Bool || !args[0].Type.IsVector) return null;
        var n = args[0].Type.Size;
        var v = args[0].V!;
        return R(GlslType.Bool, c =>
        {
            var x = v(c);
            for (var i = 0; i < n; i++)
            {
                var set = x.Get(i) != 0;
                if (any && set) return new Val(1);
                if (!any && !set) return new Val(0);
            }
            return new Val(any ? 0 : 1);
        });
    }

    private static CExpr? Not(List<CExpr> args, int line)
    {
        if (args.Count != 1 || args[0].Type.ScalarKind != GlslBase.Bool || !args[0].Type.IsVector) return null;
        var v = args[0].V!;
        return R(args[0].Type, c => { var x = v(c).C0; return new Val(new Vector4(x.X == 0 ? 1 : 0, x.Y == 0 ? 1 : 0, x.Z == 0 ? 1 : 0, x.W == 0 ? 1 : 0)); });
    }

    // ---- textures ----

    private static (Func<ShaderContext, Vector2> Dx, Func<ShaderContext, Vector2> Dy) Derivatives(CExpr coordinate)
    {
        if (coordinate.Source is { Storage: GlslStorage.VaryingIn } source)
        {
            var slot = source.Slot;
            var sw = coordinate.SourceSwizzle;
            int Comp(int k) => sw is not null && k < sw.Length ? sw[k] : k;
            var c0 = Comp(0); var c1 = Comp(1);
            return (
                c => c.DerivX is null ? Vector2.Zero : new Vector2(Lane(c.DerivX[slot], c0), Lane(c.DerivX[slot], c1)),
                c => c.DerivY is null ? Vector2.Zero : new Vector2(Lane(c.DerivY[slot], c0), Lane(c.DerivY[slot], c1)));
        }
        return (_ => Vector2.Zero, _ => Vector2.Zero);
    }

    private static float Lane(Vector4 v, int i) => i switch { 0 => v.X, 1 => v.Y, 2 => v.Z, _ => v.W };

    private static CExpr? Texture(GlslCompiler compiler, string name, List<CExpr> args, int line)
    {
        if (args.Count < 2 || !args[0].Type.IsSampler) return null;
        var sampler = args[0].V!;
        var coordinate = args[1];
        var cube = args[0].Type.Base == GlslBase.SamplerCube || name.StartsWith("textureCube", StringComparison.Ordinal);
        if (cube)
            return TextureCube(compiler, name, args);

        if (args[0].Type.Base == GlslBase.Sampler2DShadow)
        {
            // texture(sampler2DShadow, vec3(uv, reference)) -> float
            if (args.Count != 2 || coordinate.Type.Base != GlslBase.Vec3) return null;
            var shadowUnit = sampler;
            var shadowCoordinate = coordinate.V!;
            return R(GlslType.Float, c =>
            {
                var q = shadowCoordinate(c);
                return c.Sampler is { } s ? new Val(s.SampleShadow((int)shadowUnit(c).X, new Vector2(q.C0.X, q.C0.Y), q.C0.Z)) : new Val(1);
            });
        }

        var projective = name is "texture2DProj" or "textureProj";
        var explicitLod = name is "texture2DLod" or "textureLod";
        if (projective ? coordinate.Type.Base is not (GlslBase.Vec3 or GlslBase.Vec4) : coordinate.Type.Base != GlslBase.Vec2) return null;
        var uv = coordinate.V!;
        var extra = args.Count > 2 ? ToFloatScalar(args[2]) : null;
        if (args.Count > 2 && extra is null) return null;
        if (args.Count > 3) return null;
        if (explicitLod && extra is null) return null;
        var (dx, dy) = Derivatives(coordinate);
        var wIndex = coordinate.Type.Base == GlslBase.Vec3 ? 2 : 3;
        // A coordinate that is not a plain varying has no derivative we can write down; the quad's neighbours supply it.
        var quadLevel = !explicitLod && coordinate.Source is not { Storage: GlslStorage.VaryingIn };
        if (quadLevel) compiler.NoteQuad();
        Val Coordinates(ShaderContext c)
        {
            var q = uv(c);
            var coords = new Vector2(q.C0.X, q.C0.Y);
            if (projective) coords /= q.Get(wIndex);
            return new Val(coords.X, coords.Y, 0, 0);
        }
        Func<ShaderContext, Val> coordinatesOf = Coordinates;
        return R(GlslType.Vec4, c =>
        {
            var unit = (int)sampler(c).X;
            Vector2 coords, dxCoords, dyCoords;
            if (quadLevel)
            {
                if (c.Quad is not { } quad) return new Val(0, 0, 0, 1);
                if (!quad.Evaluate(c, coordinatesOf, out var own, out var neighbourX, out var neighbourY)) return default;
                coords = new Vector2(own.C0.X, own.C0.Y);
                var ax = QuadShading.Along(c.Lane, 1, own, neighbourX);
                var ay = QuadShading.Along(c.Lane, 2, own, neighbourY);
                dxCoords = new Vector2(ax.X, ax.Y);
                dyCoords = new Vector2(ay.X, ay.Y);
            }
            else
            {
                coords = Coordinates(c).C0 is var cc ? new Vector2(cc.X, cc.Y) : default;
                dxCoords = dx(c);
                dyCoords = dy(c);
            }
            var lod = explicitLod ? extra!(c) : float.NaN;
            var bias = !explicitLod && extra is not null ? extra(c) : 0;
            if (c.Sampler is not { } s) return new Val(0, 0, 0, 1);
            return new Val(s.Sample(unit, coords, dxCoords, dyCoords, lod, bias));
        });
    }

    private static (Func<ShaderContext, Vector3> Dx, Func<ShaderContext, Vector3> Dy) Derivatives3(CExpr coordinate)
    {
        if (coordinate.Source is { Storage: GlslStorage.VaryingIn } source)
        {
            var slot = source.Slot;
            var sw = coordinate.SourceSwizzle;
            int Comp(int k) => sw is not null && k < sw.Length ? sw[k] : k;
            Vector3 Take(Vector4 v) => new(Lane(v, Comp(0)), Lane(v, Comp(1)), Lane(v, Comp(2)));
            return (c => c.DerivX is null ? Vector3.Zero : Take(c.DerivX[slot]), c => c.DerivY is null ? Vector3.Zero : Take(c.DerivY[slot]));
        }
        return (_ => Vector3.Zero, _ => Vector3.Zero);
    }

    private static CExpr? TextureCube(GlslCompiler compiler, string name, List<CExpr> args)
    {
        if (args[0].Type.Base != GlslBase.SamplerCube || args[1].Type.Base != GlslBase.Vec3) return null;
        if (name is not ("textureCube" or "textureCubeLod" or "texture" or "textureLod")) return null;
        var explicitLod = name is "textureCubeLod" or "textureLod";
        var extra = args.Count > 2 ? ToFloatScalar(args[2]) : null;
        if (args.Count > 3 || (args.Count > 2 && extra is null) || (explicitLod && extra is null)) return null;
        var sampler = args[0].V!;
        var coordinate = args[1];
        var direction = coordinate.V!;
        var quadLevel = !explicitLod && coordinate.Source is not { Storage: GlslStorage.VaryingIn };
        if (quadLevel) compiler.NoteQuad();
        var (derivativeX, derivativeY) = Derivatives3(coordinate);
        return R(GlslType.Vec4, c =>
        {
            var unit = (int)sampler(c).X;
            Vector3 toward, dx, dy;
            if (quadLevel)
            {
                if (c.Quad is not { } quad) return new Val(0, 0, 0, 1);
                if (!quad.Evaluate(c, direction, out var own, out var neighbourX, out var neighbourY)) return default;
                toward = new Vector3(own.C0.X, own.C0.Y, own.C0.Z);
                var ax = QuadShading.Along(c.Lane, 1, own, neighbourX);
                var ay = QuadShading.Along(c.Lane, 2, own, neighbourY);
                dx = new Vector3(ax.X, ax.Y, ax.Z);
                dy = new Vector3(ay.X, ay.Y, ay.Z);
            }
            else
            {
                var q = direction(c).C0;
                toward = new Vector3(q.X, q.Y, q.Z);
                dx = derivativeX(c);
                dy = derivativeY(c);
            }
            var lod = explicitLod ? extra!(c) : float.NaN;
            var bias = !explicitLod && extra is not null ? extra(c) : 0;
            return c.Sampler is { } s ? new Val(s.SampleCube(unit, toward, dx, dy, lod, bias)) : new Val(0, 0, 0, 1);
        });
    }

    private static Func<ShaderContext, float>? ToFloatScalar(CExpr e)
    {
        if (!e.Type.IsScalar || e.Type.Base == GlslBase.Bool) return null;
        var v = e.V!;
        return c => v(c).X;
    }

    private static CExpr? TexelFetch(List<CExpr> args, int line)
    {
        if (args.Count != 3 || !args[0].Type.IsSampler || args[1].Type.Base != GlslBase.IVec2 || args[2].Type.Base != GlslBase.Int) return null;
        var sampler = args[0].V!; var pos = args[1].V!; var lod = args[2].V!;
        return R(GlslType.Vec4, c =>
        {
            var p = pos(c).C0;
            return c.Sampler is { } s ? new Val(s.Fetch((int)sampler(c).X, (int)p.X, (int)p.Y, (int)lod(c).X)) : new Val(0, 0, 0, 1);
        });
    }

    private static CExpr? TextureSize(List<CExpr> args, int line)
    {
        if (args.Count != 2 || !args[0].Type.IsSampler || args[1].Type.Base != GlslBase.Int) return null;
        var sampler = args[0].V!; var lod = args[1].V!;
        return R(new GlslType(GlslBase.IVec2), c =>
        {
            if (c.Sampler is not { } s) return default;
            var (w, h) = s.Size((int)sampler(c).X, (int)lod(c).X);
            return new Val(w, h, 0, 0);
        });
    }

    private static CExpr? Derivative(GlslCompiler compiler, string name, List<CExpr> args, int line)
    {
        if (args.Count != 1 || !IsGen(args[0].Type)) return null;
        compiler.NoteDerivatives();
        var a = args[0];
        var type = a.Type;
        if (a.Source is { Storage: GlslStorage.VaryingIn } source)
        {
            var slot = source.Slot;
            var sw = a.SourceSwizzle;
            var n = type.Size;
            int Comp(int k) => sw is not null && k < sw.Length ? sw[k] : k;
            return R(type, c =>
            {
                Vector4 dx = c.DerivX is null ? Vector4.Zero : c.DerivX[slot];
                Vector4 dy = c.DerivY is null ? Vector4.Zero : c.DerivY[slot];
                var rx = new Val(); var ry = new Val();
                for (var i = 0; i < n; i++) { rx.Set(i, Lane(dx, Comp(i))); ry.Set(i, Lane(dy, Comp(i))); }
                return name switch
                {
                    "dFdx" => rx,
                    "dFdy" => ry,
                    _ => new Val(Vector4.Abs(rx.C0) + Vector4.Abs(ry.C0)),
                };
            });
        }
        // The derivative of anything else comes from shading the neighbouring pixels of the quad.
        compiler.NoteQuad();
        var argument = a.V!;
        var size = type.Size;
        return R(type, c =>
        {
            if (c.Quad is not { } quad || !quad.Evaluate(c, argument, out var own, out var neighbourX, out var neighbourY)) return default;
            var dx = QuadShading.Along(c.Lane, 1, own, neighbourX);
            var dy = QuadShading.Along(c.Lane, 2, own, neighbourY);
            var result = new Val();
            for (var i = 0; i < size; i++)
            {
                var x = Lane(dx, i); var y = Lane(dy, i);
                result.Set(i, name switch { "dFdx" => x, "dFdy" => y, _ => MathF.Abs(x) + MathF.Abs(y) });
            }
            return result;
        });
    }
}
