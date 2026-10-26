using System.Numerics;

namespace Folio.Scripting.WebGl;

internal sealed partial class GlslCompiler
{
    // ---- reading and writing places ----

    private static Val[] StoreOf(ShaderContext c, bool global) => global ? c.G : c.L;

    private static Func<ShaderContext, int> Absolute(bool global, int slot) => global ? _ => slot : c => c.Base + slot;

    private static Val ReadPlace(ShaderContext c, LValue p)
    {
        var v = StoreOf(c, p.Global)[p.Slot(c)];
        if (p.Column is not null) v = new Val(v.Column(Math.Clamp(p.Column(c), 0, 3)));
        if (p.Swizzle is { } sw) return Swizzled(v, sw);
        if (p.Comp is not null) return new Val(v.Get(Math.Clamp(p.Comp(c), 0, 3)));
        return v;
    }

    private static Val Swizzled(Val v, int[] sw) => sw.Length switch
    {
        1 => new Val(v.Get(sw[0])),
        2 => new Val(v.Get(sw[0]), v.Get(sw[1]), 0, 0),
        3 => new Val(v.Get(sw[0]), v.Get(sw[1]), v.Get(sw[2]), 0),
        _ => new Val(v.Get(sw[0]), v.Get(sw[1]), v.Get(sw[2]), v.Get(sw[3])),
    };

    private static void WritePlace(ShaderContext c, LValue p, Val value)
    {
        var store = StoreOf(c, p.Global);
        var slot = p.Slot(c);
        if (p.IsWhole)
        {
            store[slot] = value;
            return;
        }
        var whole = store[slot];
        if (p.Column is not null)
        {
            var column = Math.Clamp(p.Column(c), 0, 3);
            var col = new Val(whole.Column(column));
            if (p.Swizzle is { } sw1) for (var i = 0; i < sw1.Length; i++) col.Set(sw1[i], value.Get(i));
            else if (p.Comp is not null) col.Set(Math.Clamp(p.Comp(c), 0, 3), value.X);
            else col = value;
            whole.SetColumn(column, col.C0);
        }
        else if (p.Swizzle is { } sw)
        {
            for (var i = 0; i < sw.Length; i++) whole.Set(sw[i], value.Get(i));
        }
        else
        {
            whole.Set(Math.Clamp(p.Comp!(c), 0, 3), value.X);
        }
        store[slot] = whole;
    }

    private static Func<ShaderContext, Val> ReadOf(LValue p)
    {
        if (p.IsWhole)
        {
            var slot = p.Slot;
            return p.Global ? c => c.G[slot(c)] : c => c.L[slot(c)];
        }
        return c => ReadPlace(c, p);
    }

    private static Func<ShaderContext, Val[]> ReadAggregate(LValue p, int count)
    {
        var slot = p.Slot;
        var global = p.Global;
        return c =>
        {
            var copy = new Val[count];
            Array.Copy(StoreOf(c, global), slot(c), copy, 0, count);
            return copy;
        };
    }

    private static CExpr FromPlace(GlslType type, LValue place, GlslVariable? source = null, int[]? sourceSwizzle = null, GlslExpr? ast = null) =>
        type.IsAggregate
            ? new CExpr { Type = type, A = ReadAggregate(place, type.Slots), Place = place, Source = source, SourceSwizzle = sourceSwizzle, Ast = ast }
            : new CExpr { Type = type, V = ReadOf(place), Place = place, Source = source, SourceSwizzle = sourceSwizzle, Ast = ast };

    private static CExpr Rvalue(GlslType type, Func<ShaderContext, Val> v, GlslExpr? ast = null) => new() { Type = type, V = v, Ast = ast };

    private static CExpr Aggregate(GlslType type, Func<ShaderContext, Val[]> a) => new() { Type = type, A = a };

    // ---- the expression compiler ----

    private CExpr CompileExpr(GlslExpr expr)
    {
        switch (expr)
        {
            case IntLit i:
            {
                var v = new Val((float)i.Value);
                return Rvalue(GlslType.Int, _ => v, expr);
            }
            case FloatLit f:
            {
                var v = new Val((float)f.Value);
                return Rvalue(GlslType.Float, _ => v, expr);
            }
            case BoolLit b:
            {
                var v = new Val(b.Value ? 1f : 0f);
                return Rvalue(GlslType.Bool, _ => v, expr);
            }
            case NameExpr n:
                return CompileName(n);
            case UnaryExpr u:
                return CompileUnary(u);
            case PostfixExpr p:
                return CompileIncrement(p.Operand, p.Op, prefix: false, p.Line);
            case BinaryExpr b:
                return CompileBinary(b);
            case AssignExpr a:
                return CompileAssign(a);
            case TernaryExpr t:
                return CompileTernary(t);
            case SequenceExpr s:
            {
                var left = CompileExpr(s.Left);
                var right = CompileExpr(s.Right);
                var lv = left.V ?? (c => { left.A!(c); return default; });
                if (right.Type.IsAggregate) { var ra = right.A!; return Aggregate(right.Type, c => { lv(c); return ra(c); }); }
                var rv = right.V!;
                return Rvalue(right.Type, c => { lv(c); return rv(c); });
            }
            case CallExpr call:
                return CompileCall(call);
            case FieldExpr f:
                return CompileField(f);
            case IndexExpr ix:
                return CompileIndex(ix);
            default:
                throw Error(expr.Line, "unsupported expression");
        }
    }

    private CExpr CompileName(NameExpr n)
    {
        var variable = Lookup(n.Name) ?? throw Error(n.Line, $"'{n.Name}' : undeclared identifier");
        var global = variable.Storage != GlslStorage.Local;
        var place = new LValue { Global = global, Slot = Absolute(global, global ? variable.Slot : variable.FrameSlot) };
        if (variable.IsConst || variable.Storage is GlslStorage.Uniform or GlslStorage.Attribute or GlslStorage.VaryingIn or GlslStorage.BuiltinIn)
        {
            var read = FromPlace(variable.Type, place, variable, null, n);
            return new CExpr { Type = read.Type, V = read.V, A = read.A, Place = null, Source = variable, Ast = n };
        }
        return FromPlace(variable.Type, place, variable, null, n);
    }

    // ---- type conversion ----

    /// <summary>The expression as a value of <paramref name="target"/>: ints are accepted where floats are expected, since both are stored as floats.</summary>
    private CExpr Coerce(CExpr e, GlslType target, int line)
    {
        if (e.Type.SameAs(target)) return e;
        if (!e.Type.IsArray && !target.IsArray && e.Type.ScalarKind == GlslBase.Int && target.ScalarKind == GlslBase.Float &&
            e.Type.Components == target.Components && e.Type.IsMatrix == target.IsMatrix && e.Type.Base != GlslBase.Struct && target.Base != GlslBase.Struct)
        {
            return new CExpr { Type = target, V = e.V, Ast = e.Ast, Source = e.Source };
        }
        throw Error(line, $"'' : cannot convert from '{e.Type}' to '{target}'");
    }

    // ---- unary, binary, ternary, assignment ----

    private CExpr CompileUnary(UnaryExpr u)
    {
        if (u.Op is "++" or "--") return CompileIncrement(u.Operand, u.Op, prefix: true, u.Line);
        var operand = CompileExpr(u.Operand);
        var t = operand.Type;
        var v = operand.V ?? throw Error(u.Line, $"'{u.Op}' : wrong operand type");
        switch (u.Op)
        {
            case "+":
                if (!t.IsNumeric) throw Error(u.Line, $"'+' : wrong operand type - no operation '+' exists that takes an operand of type '{t}'");
                return operand;
            case "-":
                if (!t.IsNumeric) throw Error(u.Line, $"'-' : wrong operand type - no operation '-' exists that takes an operand of type '{t}'");
                if (t.IsMatrix) return Rvalue(t, c => { var x = v(c); return new Val { C0 = -x.C0, C1 = -x.C1, C2 = -x.C2, C3 = -x.C3 }; });
                return Rvalue(t, c => new Val(-v(c).C0));
            case "!":
                if (t.Base != GlslBase.Bool || t.IsArray) throw Error(u.Line, "'!' : wrong operand type - no operation '!' exists that takes an operand of this type");
                return Rvalue(t, c => new Val(v(c).X == 0 ? 1 : 0));
            case "~":
                if (t.ScalarKind != GlslBase.Int || t.IsMatrix) throw Error(u.Line, "'~' : wrong operand type");
                return Rvalue(t, c => { var x = v(c).C0; return new Val(new Vector4(~(int)x.X, ~(int)x.Y, ~(int)x.Z, ~(int)x.W)); });
            default:
                throw Error(u.Line, $"'{u.Op}' : unsupported operator");
        }
    }

    private CExpr CompileIncrement(GlslExpr operand, string op, bool prefix, int line)
    {
        var target = CompileExpr(operand);
        if (target.Place is null || !target.Type.IsNumeric)
            throw Error(line, $"'{op}' : l-value required");
        var place = target.Place;
        var type = target.Type;
        var delta = op == "++" ? 1f : -1f;
        var read = target.V!;
        return Rvalue(type, c =>
        {
            var old = read(c);
            Val next;
            if (type.IsMatrix)
                next = new Val { C0 = old.C0 + new Vector4(delta), C1 = old.C1 + new Vector4(delta), C2 = old.C2 + new Vector4(delta), C3 = old.C3 + new Vector4(delta) };
            else
                next = new Val(old.C0 + new Vector4(delta));
            WritePlace(c, place, next);
            return prefix ? next : old;
        });
    }

    private CExpr CompileTernary(TernaryExpr t)
    {
        var condition = BoolOf(CompileExpr(t.Condition), t.Line);
        var a = CompileExpr(t.Then);
        var b = CompileExpr(t.Else);
        if (!a.Type.SameAs(b.Type))
        {
            if (b.Type.ScalarKind == GlslBase.Int && a.Type.ScalarKind == GlslBase.Float) b = Coerce(b, a.Type, t.Line);
            else if (a.Type.ScalarKind == GlslBase.Int && b.Type.ScalarKind == GlslBase.Float) a = Coerce(a, b.Type, t.Line);
            else throw Error(t.Line, "':' : operands have incompatible types");
        }
        if (a.Type.IsAggregate)
        {
            var aa = a.A!; var ba = b.A!;
            return Aggregate(a.Type, c => condition(c).X != 0 ? aa(c) : ba(c));
        }
        var av = a.V!; var bv = b.V!;
        return Rvalue(a.Type, c => condition(c).X != 0 ? av(c) : bv(c));
    }

    private CExpr CompileAssign(AssignExpr a)
    {
        var target = CompileExpr(a.Target);
        if (target.Place is null)
            throw Error(a.Line, "'=' : l-value required");
        var place = target.Place;
        var value = CompileExpr(a.Value);
        if (a.Op != "=")
        {
            var op = a.Op[..^1];
            var combined = MakeBinary(op, target, value, a.Line);
            value = Coerce(combined, target.Type, a.Line);
        }
        else
        {
            value = Coerce(value, target.Type, a.Line);
        }
        if (target.Type.IsAggregate)
        {
            var src = value.A!;
            var count = target.Type.Slots;
            return Aggregate(target.Type, c =>
            {
                var copy = src(c);
                Array.Copy(copy, 0, StoreOf(c, place.Global), place.Slot(c), count);
                return copy;
            });
        }
        var v = value.V!;
        // A swizzle or component write stores the components in order; the value of the assignment is the whole new value read back.
        if (place.IsWhole)
        {
            var slot = place.Slot;
            return place.Global
                ? Rvalue(target.Type, c => c.G[slot(c)] = v(c))
                : Rvalue(target.Type, c => c.L[slot(c)] = v(c));
        }
        return Rvalue(target.Type, c =>
        {
            var x = v(c);
            WritePlace(c, place, x);
            return x;
        });
    }

    private CExpr CompileBinary(BinaryExpr b)
    {
        if (b.Op is "&&" or "||")
        {
            var left = BoolOf(CompileExpr(b.Left), b.Line);
            var right = BoolOf(CompileExpr(b.Right), b.Line);
            return b.Op == "&&"
                ? Rvalue(GlslType.Bool, c => new Val(left(c).X != 0 && right(c).X != 0 ? 1 : 0))
                : Rvalue(GlslType.Bool, c => new Val(left(c).X != 0 || right(c).X != 0 ? 1 : 0));
        }
        return MakeBinary(b.Op, CompileExpr(b.Left), CompileExpr(b.Right), b.Line);
    }

    private CExpr MakeBinary(string op, CExpr left, CExpr right, int line)
    {
        var lt = left.Type;
        var rt = right.Type;
        string BadOperands() => $"'{op}' : wrong operand types - no operation '{op}' exists that takes a left-hand operand of type '{lt}' and a right-hand operand of type '{rt}' (or there is no acceptable conversion)";

        if (op is "==" or "!=")
        {
            if (!lt.SameAs(rt))
            {
                if (lt.ScalarKind == GlslBase.Int && rt.ScalarKind == GlslBase.Float && lt.Components == rt.Components) left = Coerce(left, rt, line);
                else if (rt.ScalarKind == GlslBase.Int && lt.ScalarKind == GlslBase.Float && lt.Components == rt.Components) right = Coerce(right, lt, line);
                else throw Error(line, BadOperands());
            }
            return MakeEquality(op == "==", left, right);
        }
        if (lt.IsAggregate || rt.IsAggregate || lt.IsSampler || rt.IsSampler || lt.Base == GlslBase.Void || rt.Base == GlslBase.Void)
            throw Error(line, BadOperands());

        // Relational operators take scalars.
        if (op is "<" or ">" or "<=" or ">=")
        {
            if (!lt.IsScalar || !rt.IsScalar || lt.Base == GlslBase.Bool || rt.Base == GlslBase.Bool) throw Error(line, BadOperands());
            var l = left.V!; var r = right.V!;
            return op switch
            {
                "<" => Rvalue(GlslType.Bool, c => new Val(l(c).X < r(c).X ? 1 : 0)),
                ">" => Rvalue(GlslType.Bool, c => new Val(l(c).X > r(c).X ? 1 : 0)),
                "<=" => Rvalue(GlslType.Bool, c => new Val(l(c).X <= r(c).X ? 1 : 0)),
                _ => Rvalue(GlslType.Bool, c => new Val(l(c).X >= r(c).X ? 1 : 0)),
            };
        }
        if (op == "^^")
        {
            if (lt.Base != GlslBase.Bool || rt.Base != GlslBase.Bool) throw Error(line, BadOperands());
            var l = left.V!; var r = right.V!;
            return Rvalue(GlslType.Bool, c => new Val((l(c).X != 0) != (r(c).X != 0) ? 1 : 0));
        }
        if (!lt.IsNumeric || !rt.IsNumeric) throw Error(line, BadOperands());

        // Mixed int/float operands: ints convert up.
        if (lt.ScalarKind != rt.ScalarKind)
        {
            if (lt.ScalarKind == GlslBase.Int && rt.ScalarKind == GlslBase.Float && !lt.IsMatrix) left = Coerce(left, GlslType.Vector(GlslBase.Float, lt.Size), line);
            else if (rt.ScalarKind == GlslBase.Int && lt.ScalarKind == GlslBase.Float && !rt.IsMatrix) right = Coerce(right, GlslType.Vector(GlslBase.Float, rt.Size), line);
            else throw Error(line, BadOperands());
            lt = left.Type;
            rt = right.Type;
        }

        if (op is "%" or "&" or "|" or "^" or "<<" or ">>")
        {
            if (lt.ScalarKind != GlslBase.Int || lt.IsMatrix || rt.IsMatrix) throw Error(line, BadOperands());
            var shape = lt.IsScalar ? rt : lt;
            if (!lt.IsScalar && !rt.IsScalar && lt.Size != rt.Size) throw Error(line, BadOperands());
            var l = left.V!; var r = right.V!;
            var lScalar = lt.IsScalar; var rScalar = rt.IsScalar;
            return Rvalue(shape, c =>
            {
                var x = l(c).C0; var y = r(c).C0;
                if (lScalar) x = new Vector4(x.X);
                if (rScalar) y = new Vector4(y.X);
                return new Val(new Vector4(IntOp(op, (int)x.X, (int)y.X), IntOp(op, (int)x.Y, (int)y.Y), IntOp(op, (int)x.Z, (int)y.Z), IntOp(op, (int)x.W, (int)y.W)));
            });
        }
        if (op is not ("+" or "-" or "*" or "/")) throw Error(line, $"'{op}' : unsupported operator");
        var isInt = lt.ScalarKind == GlslBase.Int;
        var lv = left.V!; var rv = right.V!;

        // Matrix forms.
        if (lt.IsMatrix || rt.IsMatrix)
        {
            if (lt.IsMatrix && rt.IsMatrix)
            {
                if (lt.Size != rt.Size) throw Error(line, BadOperands());
                var n = lt.Size;
                if (op == "*") return Rvalue(lt, c => MatMul(lv(c), rv(c), n));
                return Rvalue(lt, c => MatCompOp(op, lv(c), rv(c)));
            }
            if (lt.IsMatrix)
            {
                var n = lt.Size;
                if (rt.IsScalar) return Rvalue(lt, c => MatScalar(op, lv(c), rv(c).X, matrixFirst: true));
                if (rt.IsVector && op == "*")
                {
                    if (rt.Size != n) throw Error(line, BadOperands());
                    return Rvalue(rt, c => MatVec(lv(c), rv(c), n));
                }
                throw Error(line, BadOperands());
            }
            else
            {
                var n = rt.Size;
                if (lt.IsScalar) return Rvalue(rt, c => MatScalar(op, rv(c), lv(c).X, matrixFirst: false));
                if (lt.IsVector && op == "*")
                {
                    if (lt.Size != n) throw Error(line, BadOperands());
                    return Rvalue(lt, c => VecMat(lv(c), rv(c), n));
                }
                throw Error(line, BadOperands());
            }
        }

        // Scalars and vectors, component-wise with a scalar broadcast.
        if (!lt.IsScalar && !rt.IsScalar && lt.Size != rt.Size) throw Error(line, BadOperands());
        var resultType = lt.IsScalar ? rt : lt;
        var broadcastL = lt.IsScalar && !rt.IsScalar;
        var broadcastR = rt.IsScalar && !lt.IsScalar;
        switch (op)
        {
            case "+":
                return Rvalue(resultType, c => new Val(Spread(lv(c), broadcastL) + Spread(rv(c), broadcastR)));
            case "-":
                return Rvalue(resultType, c => new Val(Spread(lv(c), broadcastL) - Spread(rv(c), broadcastR)));
            case "*":
                return Rvalue(resultType, c => new Val(Spread(lv(c), broadcastL) * Spread(rv(c), broadcastR)));
            default:
                return isInt
                    ? Rvalue(resultType, c => new Val(IntDivide(Spread(lv(c), broadcastL), Spread(rv(c), broadcastR))))
                    : Rvalue(resultType, c => new Val(Spread(lv(c), broadcastL) / Spread(rv(c), broadcastR)));
        }
    }

    private static Vector4 Spread(Val v, bool broadcast) => broadcast ? new Vector4(v.C0.X) : v.C0;

    private static int IntOp(string op, int x, int y) => op switch
    {
        "%" => y == 0 ? 0 : x % y,
        "&" => x & y,
        "|" => x | y,
        "^" => x ^ y,
        "<<" => x << (y & 31),
        _ => x >> (y & 31),
    };

    private static Vector4 IntDivide(Vector4 a, Vector4 b) => new(
        b.X == 0 ? 0 : MathF.Truncate(a.X / b.X), b.Y == 0 ? 0 : MathF.Truncate(a.Y / b.Y),
        b.Z == 0 ? 0 : MathF.Truncate(a.Z / b.Z), b.W == 0 ? 0 : MathF.Truncate(a.W / b.W));

    private static Val MatCompOp(string op, Val a, Val b)
    {
        Vector4 F(Vector4 x, Vector4 y) => op switch { "+" => x + y, "-" => x - y, _ => x / y };
        return new Val { C0 = F(a.C0, b.C0), C1 = F(a.C1, b.C1), C2 = F(a.C2, b.C2), C3 = F(a.C3, b.C3) };
    }

    private static Val MatScalar(string op, Val m, float s, bool matrixFirst)
    {
        Vector4 F(Vector4 x)
        {
            var y = new Vector4(s);
            return op switch
            {
                "+" => x + y,
                "-" => matrixFirst ? x - y : y - x,
                "*" => x * y,
                _ => matrixFirst ? x / y : y / x,
            };
        }
        return new Val { C0 = F(m.C0), C1 = F(m.C1), C2 = F(m.C2), C3 = F(m.C3) };
    }

    private static Val MatVec(Val m, Val v, int n)
    {
        var r = m.C0 * v.C0.X;
        if (n > 1) r += m.C1 * v.C0.Y;
        if (n > 2) r += m.C2 * v.C0.Z;
        if (n > 3) r += m.C3 * v.C0.W;
        return new Val(r);
    }

    private static Val VecMat(Val v, Val m, int n)
    {
        var r = new Val();
        for (var j = 0; j < n; j++)
        {
            var column = m.Column(j);
            var s = v.C0.X * column.X + v.C0.Y * column.Y;
            if (n > 2) s += v.C0.Z * column.Z;
            if (n > 3) s += v.C0.W * column.W;
            r.Set(j, s);
        }
        return r;
    }

    private static Val MatMul(Val a, Val b, int n)
    {
        var r = new Val();
        for (var j = 0; j < n; j++) r.SetColumn(j, MatVec(a, new Val(b.Column(j)), n).C0);
        return r;
    }

    // ---- equality ----

    private CExpr MakeEquality(bool equal, CExpr left, CExpr right)
    {
        var type = left.Type;
        if (type.IsAggregate)
        {
            var la = left.A!; var ra = right.A!;
            var leaves = Leaves(type).ToArray();
            return Rvalue(GlslType.Bool, c =>
            {
                var x = la(c); var y = ra(c);
                var same = true;
                for (var i = 0; i < leaves.Length && same; i++) same = LeafEqual(x[i], y[i], leaves[i]);
                return new Val(same == equal ? 1 : 0);
            });
        }
        var l = left.V!; var r = right.V!;
        return Rvalue(GlslType.Bool, c => new Val(LeafEqual(l(c), r(c), type) == equal ? 1 : 0));
    }

    private static IEnumerable<GlslType> Leaves(GlslType type)
    {
        if (type.IsArray)
        {
            for (var i = 0; i < type.ArraySize; i++)
                foreach (var leaf in Leaves(type.Element)) yield return leaf;
        }
        else if (type.Base == GlslBase.Struct)
        {
            foreach (var (_, ft) in type.Struct!.Fields)
                foreach (var leaf in Leaves(ft)) yield return leaf;
            if (type.Struct.Fields.Count == 0) yield return GlslType.Float;
        }
        else
        {
            yield return type;
        }
    }

    private static bool LeafEqual(Val a, Val b, GlslType t)
    {
        if (t.IsMatrix)
        {
            var n = t.Size;
            for (var col = 0; col < n; col++)
                for (var row = 0; row < n; row++)
                    if (a.Cell(col, row) != b.Cell(col, row)) return false;
            return true;
        }
        for (var i = 0; i < Math.Max(1, t.Size); i++) if (a.Get(i) != b.Get(i)) return false;
        return true;
    }

    // ---- fields and indexing ----

    private CExpr CompileField(FieldExpr f)
    {
        var target = CompileExpr(f.Target);
        var t = target.Type;
        if (t.IsStruct)
        {
            var offset = t.Struct!.OffsetOf(f.Name);
            if (offset < 0) throw Error(f.Line, $"'{f.Name}' : no such field in structure");
            var fieldType = t.Struct.Fields.First(x => x.Name == f.Name).Type;
            if (target.Place is { } place && place.IsWhole)
            {
                var slot = place.Slot;
                var child = new LValue { Global = place.Global, Slot = c => slot(c) + offset };
                return FromPlace(fieldType, child, target.Source, null, f);
            }
            var a = target.A!;
            if (fieldType.IsAggregate)
            {
                var count = fieldType.Slots;
                return Aggregate(fieldType, c => a(c).AsSpan(offset, count).ToArray());
            }
            return Rvalue(fieldType, c => a(c)[offset]);
        }
        if (t.IsVector)
        {
            var sw = ParseSwizzle(f.Name, t.Size, f.Line);
            var resultType = GlslType.Vector(t.ScalarKind, sw.Length);
            if (target.Place is { } vp && vp.Column is null)
            {
                var composed = vp.Swizzle is { } inner ? sw.Select(i => inner[i]).ToArray() : sw;
                var place = new LValue { Global = vp.Global, Slot = vp.Slot, Swizzle = composed };
                return FromPlace(resultType, place, target.Source, composed, f);
            }
            var v = target.V!;
            return new CExpr { Type = resultType, V = c => Swizzled(v(c), sw), Source = target.Source, SourceSwizzle = target.SourceSwizzle is { } ss ? sw.Select(i => ss[i]).ToArray() : sw, Ast = f };
        }
        throw Error(f.Line, $"'.' : field selection requires structure or vector on left hand side (found '{t}')");
    }

    private static int[] ParseSwizzle(string name, int size, int line)
    {
        if (name.Length is < 1 or > 4) throw Error(line, $"'{name}' : illegal vector field selection");
        string? set = null;
        foreach (var s in new[] { "xyzw", "rgba", "stpq" })
            if (s.Contains(name[0])) set = s;
        if (set is null) throw Error(line, $"'{name}' : illegal vector field selection");
        var indices = new int[name.Length];
        for (var i = 0; i < name.Length; i++)
        {
            var k = set.IndexOf(name[i]);
            if (k < 0) throw Error(line, $"'{name}' : illegal vector field selection");
            if (k >= size) throw Error(line, $"'{name}' : vector field selection out of range");
            indices[i] = k;
        }
        return indices;
    }

    private CExpr CompileIndex(IndexExpr ix)
    {
        var target = CompileExpr(ix.Target);
        var indexExpr = CompileExpr(ix.Index);
        if (indexExpr.Type.Base != GlslBase.Int || indexExpr.Type.IsArray)
            throw Error(ix.Line, "'[' : integer expression required");
        var index = indexExpr.V!;
        var t = target.Type;
        if (t.IsArray)
        {
            var element = t.Element;
            var size = t.ArraySize;
            var stride = element.Slots;
            if (target.Place is { } place && place.IsWhole)
            {
                var slot = place.Slot;
                var child = new LValue { Global = place.Global, Slot = c => slot(c) + Math.Clamp((int)index(c).X, 0, size - 1) * stride };
                return FromPlace(element, child, target.Source, null, ix);
            }
            var a = target.A!;
            if (element.IsAggregate)
                return Aggregate(element, c => a(c).AsSpan(Math.Clamp((int)index(c).X, 0, size - 1) * stride, stride).ToArray());
            return Rvalue(element, c => a(c)[Math.Clamp((int)index(c).X, 0, size - 1)]);
        }
        if (t.IsMatrix)
        {
            var n = t.Size;
            var columnType = GlslType.Vector(GlslBase.Float, n);
            Func<ShaderContext, int> column = c => Math.Clamp((int)index(c).X, 0, n - 1);
            if (target.Place is { } mp && mp.IsWhole)
            {
                var place = new LValue { Global = mp.Global, Slot = mp.Slot, Column = column };
                return FromPlace(columnType, place, null, null, ix);
            }
            var v = target.V!;
            return Rvalue(columnType, c => new Val(v(c).Column(column(c))));
        }
        if (t.IsVector)
        {
            var n = t.Size;
            var scalar = GlslType.Vector(t.ScalarKind, 1);
            Func<ShaderContext, int> comp = c => Math.Clamp((int)index(c).X, 0, n - 1);
            if (target.Place is { } vp && vp.Swizzle is null && vp.Comp is null)
            {
                var place = new LValue { Global = vp.Global, Slot = vp.Slot, Column = vp.Column, Comp = comp };
                return FromPlace(scalar, place, null, null, ix);
            }
            var v = target.V!;
            return Rvalue(scalar, c => new Val(v(c).Get(comp(c))));
        }
        throw Error(ix.Line, $"'[' : left of '[' is not of type array, matrix, or vector (found '{t}')");
    }
}
