using System.Numerics;

namespace Folio.Scripting.WebGl;

internal sealed partial class GlslCompiler
{
    private CExpr CompileCall(CallExpr call)
    {
        var args = call.Args.Select(CompileExpr).ToList();

        if (call.Name == ".length")
        {
            var t = args[0].Type;
            if (t.IsArray) return Rvalue(GlslType.Int, _ => new Val(t.ArraySize));
            if (t.IsVector) return Rvalue(GlslType.Int, _ => new Val(t.Size));
            if (t.IsMatrix) return Rvalue(GlslType.Int, _ => new Val(t.Size));
            throw Error(call.Line, "'length' : method requires an array, vector or matrix");
        }

        // Array constructor: float[3](...)
        var bracket = call.Name.IndexOf('[', StringComparison.Ordinal);
        if (bracket > 0)
        {
            GlslType.TryParse(call.Name[..bracket], out var elementType);
            var size = int.Parse(call.Name[(bracket + 1)..^1]);
            return ArrayConstructor(elementType.Struct is null ? elementType with { ArraySize = size } : elementType, args, call.Line);
        }

        if (GlslType.TryParse(call.Name, out var ctorType) && ctorType.Base != GlslBase.Void)
            return Constructor(ctorType, args, call.Line);
        if (_unit.Structs.TryGetValue(call.Name, out var def))
            return StructConstructor(new GlslType(GlslBase.Struct, 0, def), args, call.Line);

        if (_functions.TryGetValue(call.Name, out var overloads))
            return UserCall(call, overloads, args);

        if (GlslBuiltins.TryCompile(this, call.Name, args, call, out var builtin))
            return builtin;
        throw Error(call.Line, $"'{call.Name}' : no matching overloaded function found");
    }

    internal static GlslCompileException Fail(int line, string message) => Error(line, message);

    internal void NoteDerivatives() => _usesDerivatives = true;

    // ---- user functions ----

    private CExpr UserCall(CallExpr call, List<FunctionInfo> overloads, List<CExpr> args)
    {
        FunctionInfo? chosen = null;
        foreach (var fn in overloads)
        {
            if (fn.Params.Count != args.Count) continue;
            var exact = true;
            var possible = true;
            for (var i = 0; i < args.Count; i++)
            {
                var pt = fn.Params[i].Type;
                var at = args[i].Type;
                if (at.SameAs(pt)) continue;
                exact = false;
                if (!(at.ScalarKind == GlslBase.Int && pt.ScalarKind == GlslBase.Float && at.Components == pt.Components && !at.IsArray && !pt.IsArray && !at.IsAggregate && !pt.IsAggregate))
                    possible = false;
            }
            if (exact) { chosen = fn; break; }
            if (possible) chosen ??= fn;
        }
        if (chosen is null)
            throw Error(call.Line, $"'{call.Name}' : no matching overloaded function found");
        var fnInfo = chosen;
        var count = args.Count;
        var coerced = new CExpr[count];
        for (var i = 0; i < count; i++)
        {
            var p = fnInfo.Params[i];
            coerced[i] = Coerce(args[i], p.Type, call.Line);
            if (p.Qualifier is "out" or "inout" && args[i].Place is null)
                throw Error(call.Line, $"'{call.Name}' : l-value required for the '{p.Qualifier}' parameter '{p.Name}'");
        }
        var returnType = fnInfo.Return;

        Func<ShaderContext, (Val Value, Val[]? Agg)> invoke = c =>
        {
            // Arguments are evaluated in the caller's frame.
            var values = new Val[count];
            var aggs = new Val[]?[count];
            for (var i = 0; i < count; i++)
            {
                var p = fnInfo.Params[i];
                if (p.Qualifier == "out") continue;
                if (p.Type.IsAggregate) aggs[i] = coerced[i].A!(c);
                else values[i] = coerced[i].V!(c);
            }
            var callerBase = c.Base;
            var calleeBase = c.Top;
            if (calleeBase + fnInfo.FrameSize >= c.L.Length) throw new ShaderTimeoutException();
            c.Base = calleeBase;
            c.Top = calleeBase + fnInfo.FrameSize;
            for (var i = 0; i < count; i++)
            {
                var slot = calleeBase + fnInfo.ParamSlots[i];
                var p = fnInfo.Params[i];
                if (p.Qualifier == "out")
                    Array.Clear(c.L, slot, p.Type.Slots);
                else if (p.Type.IsAggregate)
                    Array.Copy(aggs[i]!, 0, c.L, slot, p.Type.Slots);
                else
                    c.L[slot] = values[i];
            }
            c.Tick();
            fnInfo.Body!(c);
            var result = (c.Ret, c.RetAgg);
            c.RetAgg = null;
            // Copy out and inout parameters back before the frame goes away.
            var back = new (Val V, Val[]? A)?[count];
            for (var i = 0; i < count; i++)
            {
                var p = fnInfo.Params[i];
                if (p.Qualifier is not ("out" or "inout")) continue;
                var slot = calleeBase + fnInfo.ParamSlots[i];
                back[i] = p.Type.IsAggregate ? (default, c.L.AsSpan(slot, p.Type.Slots).ToArray()) : (c.L[slot], null);
            }
            c.Top = calleeBase;
            c.Base = callerBase;
            for (var i = 0; i < count; i++)
            {
                if (back[i] is not { } b) continue;
                var place = args[i].Place!;
                if (b.A is { } agg) Array.Copy(agg, 0, StoreOf(c, place.Global), place.Slot(c), agg.Length);
                else WritePlace(c, place, b.V);
            }
            return result;
        };

        if (returnType.IsAggregate)
            return Aggregate(returnType, c => invoke(c).Agg ?? new Val[returnType.Slots]);
        return new CExpr { Type = returnType, V = c => invoke(c).Value };
    }

    // ---- constructors ----

    private static int Flatten(Val v, GlslType t, Span<float> into, int at)
    {
        if (t.IsMatrix)
        {
            var n = t.Size;
            for (var col = 0; col < n; col++)
                for (var row = 0; row < n; row++)
                    into[at++] = v.Cell(col, row);
            return at;
        }
        for (var i = 0; i < Math.Max(1, t.Size); i++) into[at++] = v.Get(i);
        return at;
    }

    private static float Convert(float x, GlslBase kind) => kind switch
    {
        GlslBase.Int => float.IsNaN(x) ? 0 : MathF.Truncate(x),
        GlslBase.Bool => x != 0 ? 1 : 0,
        _ => x,
    };

    private CExpr Constructor(GlslType type, List<CExpr> args, int line)
    {
        if (type.IsSampler) throw Error(line, $"'{type}' : cannot construct this type");
        if (args.Count == 0) throw Error(line, $"'{type}' : constructor needs arguments");
        if (args.Any(a => a.Type.IsAggregate || a.Type.IsSampler || a.Type.Base == GlslBase.Void))
            throw Error(line, $"'{type}' : cannot convert from an aggregate or sampler");
        var kind = type.ScalarKind;
        var needed = type.Components;
        var total = args.Sum(a => a.Type.Components);
        var evals = args.Select(a => a.V!).ToArray();
        var types = args.Select(a => a.Type).ToArray();

        if (type.IsMatrix)
        {
            var n = type.Size;
            if (args.Count == 1 && args[0].Type.IsMatrix)
            {
                var m = args[0].Type.Size;
                var e0 = evals[0];
                return Rvalue(type, c =>
                {
                    var src = e0(c);
                    var r = new Val();
                    for (var col = 0; col < n; col++)
                        for (var row = 0; row < n; row++)
                            r.SetCell(col, row, col < m && row < m ? src.Cell(col, row) : col == row ? 1 : 0);
                    return r;
                });
            }
            if (args.Count == 1 && args[0].Type.IsScalar)
            {
                var e0 = evals[0];
                return Rvalue(type, c =>
                {
                    var s = e0(c).X;
                    var r = new Val();
                    for (var i = 0; i < n; i++) r.SetCell(i, i, s);
                    return r;
                });
            }
            if (total < needed) throw Error(line, $"'{type}' : not enough data provided for construction");
            return Rvalue(type, c =>
            {
                Span<float> comps = stackalloc float[64];
                var at = 0;
                for (var i = 0; i < evals.Length; i++) at = Flatten(evals[i](c), types[i], comps, at);
                var r = new Val();
                for (var col = 0; col < n; col++)
                    for (var row = 0; row < n; row++)
                        r.SetCell(col, row, comps[col * n + row]);
                return r;
            });
        }

        // scalars and vectors
        if (args.Count == 1 && args[0].Type.IsScalar)
        {
            var e0 = evals[0];
            if (type.IsScalar) return Rvalue(type, c => new Val(Convert(e0(c).X, kind)));
            return Rvalue(type, c => new Val(new Vector4(Convert(e0(c).X, kind))));
        }
        if (total < needed) throw Error(line, $"'{type}' : not enough data provided for construction");
        if (total > needed && args.Count > 1 && args.Take(args.Count - 1).Sum(a => a.Type.Components) >= needed)
            throw Error(line, $"'{type}' : too many arguments");
        return Rvalue(type, c =>
        {
            Span<float> comps = stackalloc float[64];
            var at = 0;
            for (var i = 0; i < evals.Length; i++) at = Flatten(evals[i](c), types[i], comps, at);
            var r = new Val();
            for (var i = 0; i < needed; i++) r.Set(i, Convert(comps[i], kind));
            return r;
        });
    }

    private CExpr StructConstructor(GlslType type, List<CExpr> args, int line)
    {
        var fields = type.Struct!.Fields;
        if (args.Count != fields.Count) throw Error(line, $"'{type.Struct.Name}' : number of constructor arguments does not match the structure");
        var parts = new CExpr[fields.Count];
        for (var i = 0; i < fields.Count; i++) parts[i] = Coerce(args[i], fields[i].Type, line);
        var total = type.Slots;
        return Aggregate(type, c =>
        {
            var result = new Val[total];
            var at = 0;
            for (var i = 0; i < parts.Length; i++)
            {
                var ft = fields[i].Type;
                if (ft.IsAggregate) { var a = parts[i].A!(c); Array.Copy(a, 0, result, at, a.Length); }
                else result[at] = parts[i].V!(c);
                at += ft.Slots;
            }
            return result;
        });
    }

    private CExpr ArrayConstructor(GlslType type, List<CExpr> args, int line)
    {
        var element = type.Element;
        if (args.Count != type.ArraySize) throw Error(line, $"'{type}' : array constructor needs {type.ArraySize} arguments");
        var parts = args.Select(a => Coerce(a, element, line)).ToArray();
        var stride = element.Slots;
        return Aggregate(type, c =>
        {
            var result = new Val[type.Slots];
            for (var i = 0; i < parts.Length; i++)
            {
                if (element.IsAggregate) { var a = parts[i].A!(c); Array.Copy(a, 0, result, i * stride, a.Length); }
                else result[i * stride] = parts[i].V!(c);
            }
            return result;
        });
    }
}
