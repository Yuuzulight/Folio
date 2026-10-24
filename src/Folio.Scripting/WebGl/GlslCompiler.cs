using System.Numerics;

namespace Folio.Scripting.WebGl;

/// <summary>Where an assignable expression lives: a slot in the globals or the current frame, optionally narrowed to a matrix column and a component or swizzle.</summary>
internal sealed class LValue
{
    public bool Global;
    public required Func<ShaderContext, int> Slot { get; init; }
    public Func<ShaderContext, int>? Column { get; init; }
    public Func<ShaderContext, int>? Comp { get; init; }
    public int[]? Swizzle { get; init; }

    public bool IsWhole => Column is null && Comp is null && Swizzle is null;

    public LValue Same(bool global) => new() { Global = global, Slot = Slot, Column = Column, Comp = Comp, Swizzle = Swizzle };
}

/// <summary>A compiled expression: its type and how to evaluate it, plus where it can be assigned.</summary>
internal sealed class CExpr
{
    public required GlslType Type { get; init; }
    public Func<ShaderContext, Val>? V { get; init; }
    public Func<ShaderContext, Val[]>? A { get; init; }
    public LValue? Place { get; init; }
    public GlslVariable? Source { get; init; }
    public int[]? SourceSwizzle { get; init; }
    public GlslExpr? Ast { get; init; }
}

internal sealed class FunctionInfo(string name, GlslType returnType, List<ParamDecl> parameters)
{
    public string Name { get; } = name;
    public GlslType Return { get; } = returnType;
    public List<ParamDecl> Params { get; } = parameters;
    public int[] ParamSlots { get; set; } = [];
    public int FrameSize { get; set; }
    public Func<ShaderContext, GlslFlow>? Body { get; set; }
}

internal sealed partial class GlslCompiler
{
    private readonly GlslUnit _unit;
    private readonly GlslStage _stage;
    private readonly List<GlslVariable> _globals = [];
    private readonly List<Val> _initial = [];
    private readonly List<Action<ShaderContext>> _constantInits = [];
    private readonly List<Action<ShaderContext>> _globalInits = [];
    private readonly Dictionary<string, List<FunctionInfo>> _functions = [];
    private readonly List<Dictionary<string, GlslVariable>> _scopes = [];
    private const int MaxFrameSlots = 4096;
    private const int MaxGlobalSlots = 65536;
    private FunctionInfo? _current;
    private int _frame;
    private bool _usesDerivatives;
    private int _positionSlot = -1, _pointSizeSlot = -1, _fragCoordSlot = -1, _frontFacingSlot = -1, _pointCoordSlot = -1,
        _vertexIdSlot = -1, _instanceIdSlot = -1, _fragDepthSlot = -1;
    private readonly List<int> _fragOutputs = [];
    private int _fragDataSlot = -1;

    private GlslCompiler(GlslUnit unit, GlslStage stage)
    {
        _unit = unit;
        _stage = stage;
        _scopes.Add([]);
    }

    public static GlslCompileResult Compile(GlslStage stage, string source)
    {
        try
        {
            var unit = GlslParser.Parse(source);
            var compiler = new GlslCompiler(unit, stage);
            var shader = compiler.Build();
            return new GlslCompileResult(shader, "", 0);
        }
        catch (GlslCompileException e)
        {
            return new GlslCompileResult(null, $"ERROR: 0:{e.Line}: {e.Message}", e.Line);
        }
    }

    private static GlslCompileException Error(int line, string message) => new(message, line);

    // ---- variables ----

    private GlslVariable DeclareGlobal(string name, GlslType type, GlslStorage storage, int location = -1, bool isConst = false)
    {
        var slot = _initial.Count;
        if (slot + (long)type.Slots > MaxGlobalSlots) throw Error(0, $"'{name}' : too much global storage");
        for (var i = 0; i < type.Slots; i++) _initial.Add(default);
        var variable = new GlslVariable(name, type, storage, slot, location) { IsConst = isConst };
        _globals.Add(variable);
        _scopes[0][name] = variable;
        return variable;
    }

    private GlslVariable DeclareLocal(string name, GlslType type, int line, bool isConst = false)
    {
        var scope = _scopes[^1];
        if (scope.ContainsKey(name) && _scopes.Count > 1)
            throw Error(line, $"'{name}' : redefinition");
        var variable = new GlslVariable(name, type, GlslStorage.Local, -1, -1) { IsConst = isConst, FrameSlot = _frame };
        _frame += type.Slots;
        scope[name] = variable;
        return variable;
    }

    private GlslVariable? Lookup(string name)
    {
        for (var i = _scopes.Count - 1; i >= 0; i--)
            if (_scopes[i].TryGetValue(name, out var v)) return v;
        return null;
    }

    private void DeclareBuiltins()
    {
        if (_stage == GlslStage.Vertex)
        {
            _positionSlot = DeclareGlobal("gl_Position", GlslType.Vec4, GlslStorage.BuiltinOut).Slot;
            _pointSizeSlot = DeclareGlobal("gl_PointSize", GlslType.Float, GlslStorage.BuiltinOut).Slot;
            _vertexIdSlot = DeclareGlobal("gl_VertexID", GlslType.Int, GlslStorage.BuiltinIn).Slot;
            _instanceIdSlot = DeclareGlobal("gl_InstanceID", GlslType.Int, GlslStorage.BuiltinIn).Slot;
        }
        else
        {
            _fragCoordSlot = DeclareGlobal("gl_FragCoord", GlslType.Vec4, GlslStorage.BuiltinIn).Slot;
            _frontFacingSlot = DeclareGlobal("gl_FrontFacing", GlslType.Bool, GlslStorage.BuiltinIn).Slot;
            _pointCoordSlot = DeclareGlobal("gl_PointCoord", GlslType.Vec2, GlslStorage.BuiltinIn).Slot;
            _fragDepthSlot = DeclareGlobal("gl_FragDepth", GlslType.Float, GlslStorage.BuiltinOut).Slot;
            if (_unit.Version == 100)
            {
                // gl_FragColor is gl_FragData[0] under another name.
                var data = DeclareGlobal("gl_FragData", new GlslType(GlslBase.Vec4, 4), GlslStorage.BuiltinOut);
                _fragDataSlot = data.Slot;
                var color = new GlslVariable("gl_FragColor", GlslType.Vec4, GlslStorage.BuiltinOut, data.Slot);
                _globals.Add(color);
                _scopes[0]["gl_FragColor"] = color;
                // EXT_frag_depth: the same output under the extension's name.
                var depthAlias = new GlslVariable("gl_FragDepthEXT", GlslType.Float, GlslStorage.BuiltinOut, _fragDepthSlot);
                _globals.Add(depthAlias);
                _scopes[0]["gl_FragDepthEXT"] = depthAlias;
            }
        }
        foreach (var (name, value) in new (string, int)[]
        {
            ("gl_MaxVertexAttribs", 16), ("gl_MaxVertexUniformVectors", 1024), ("gl_MaxVaryingVectors", 30), ("gl_MaxVertexTextureImageUnits", 16),
            ("gl_MaxCombinedTextureImageUnits", 32), ("gl_MaxTextureImageUnits", 16), ("gl_MaxFragmentUniformVectors", 1024), ("gl_MaxDrawBuffers", 4),
        })
        {
            var v = DeclareGlobal(name, GlslType.Int, GlslStorage.Global, isConst: true);
            _initial[v.Slot] = new Val(value);
        }
    }

    // ---- the whole unit ----

    private GlslShader Build()
    {
        DeclareBuiltins();
        var fragOutputs = new List<(int Location, int Slot)>();
        foreach (var item in _unit.Items)
        {
            switch (item)
            {
                case GlobalDecl g:
                    DeclareGlobalDecl(g, fragOutputs);
                    break;
                case FunctionDecl f:
                    CompileFunction(f);
                    break;
            }
        }
        if (!_functions.TryGetValue("main", out var mains) || mains.All(m => m.Body is null))
            throw Error(1, "'main' : function is not defined");

        var main = mains.First(m => m.Body is not null);
        var body = main.Body!;
        var frame = main;
        var inits = _globalInits.ToArray();

        // The constant initialisers run once now, on a scratch context, into the initial values.
        var scratch = new ShaderContext { G = _initial.ToArray() };
        foreach (var init in _constantInits) init(scratch);

        void RunMain(ShaderContext c)
        {
            c.Base = 0;
            c.Top = frame.FrameSize;
            c.Discarded = false;
            foreach (var init in inits) init(c);
            body(c);
        }

        int[] outputs;
        if (_stage == GlslStage.Fragment)
        {
            if (_unit.Version == 100)
            {
                outputs = [_fragDataSlot, _fragDataSlot + 1, _fragDataSlot + 2, _fragDataSlot + 3];
            }
            else
            {
                var max = fragOutputs.Count == 0 ? -1 : fragOutputs.Max(o => o.Location);
                outputs = new int[Math.Max(1, max + 1)];
                Array.Fill(outputs, -1);
                foreach (var (location, slot) in fragOutputs) outputs[location] = slot;
            }
        }
        else
        {
            outputs = [];
        }

        return new GlslShader
        {
            Stage = _stage,
            Version = _unit.Version,
            Variables = _globals,
            Main = RunMain,
            InitGlobals = _ => { },
            Initial = scratch.G,
            GlobalSlots = _initial.Count,
            PositionSlot = _positionSlot,
            PointSizeSlot = _pointSizeSlot,
            FragCoordSlot = _fragCoordSlot,
            FrontFacingSlot = _frontFacingSlot,
            PointCoordSlot = _pointCoordSlot,
            VertexIdSlot = _vertexIdSlot,
            InstanceIdSlot = _instanceIdSlot,
            FragDepthSlot = _fragDepthSlot,
            UsesDerivatives = _usesDerivatives,
            FragOutputSlots = outputs,
        };
    }

    private void DeclareGlobalDecl(GlobalDecl g, List<(int Location, int Slot)> fragOutputs)
    {
        var storage = g.Storage switch
        {
            "uniform" => GlslStorage.Uniform,
            "attribute" => GlslStorage.Attribute,
            "varying" => _stage == GlslStage.Vertex ? GlslStorage.VaryingOut : GlslStorage.VaryingIn,
            "in" => _stage == GlslStage.Vertex ? GlslStorage.Attribute : GlslStorage.VaryingIn,
            "out" => _stage == GlslStage.Vertex ? GlslStorage.VaryingOut : GlslStorage.BuiltinOut,
            _ => GlslStorage.Global,
        };
        if (g.Storage == "attribute" && _stage == GlslStage.Fragment)
            throw Error(g.Line, "'attribute' : supported in vertex shaders only");
        if (_scopes[0].ContainsKey(g.Name) && !_scopes[0][g.Name].Name.StartsWith("gl_", StringComparison.Ordinal))
            throw Error(g.Line, $"'{g.Name}' : redefinition");
        var variable = DeclareGlobal(g.Name, g.Type, storage, g.Location, g.IsConst);
        if (_stage == GlslStage.Fragment && g.Storage == "out")
            fragOutputs.Add((g.Location < 0 ? fragOutputs.Count : g.Location, variable.Slot));
        if (g.Init is null) return;
        if (g.Storage is "uniform" or "attribute" or "varying" or "in" or "out")
            throw Error(g.Line, $"'{g.Name}' : cannot initialize this type of qualifier");
        _current = new FunctionInfo("<global>", GlslType.Void, []);
        _frame = 0;
        var value = Coerce(CompileExpr(g.Init), g.Type, g.Line);
        var store = StoreGlobal(variable, value, g.Line);
        if (g.IsConst) _constantInits.Add(store);
        else _globalInits.Add(store);
        _current = null;
    }

    private Action<ShaderContext> StoreGlobal(GlslVariable variable, CExpr value, int line)
    {
        var slot = variable.Slot;
        if (value.Type.IsAggregate)
        {
            var a = value.A!;
            return c => Array.Copy(a(c), 0, c.G, slot, a(c).Length);
        }
        var v = value.V!;
        return c => c.G[slot] = v(c);
    }

    // ---- functions ----

    private void CompileFunction(FunctionDecl f)
    {
        var info = new FunctionInfo(f.Name, f.Return, f.Params);
        if (!_functions.TryGetValue(f.Name, out var overloads))
            _functions[f.Name] = overloads = [];
        var existing = overloads.FirstOrDefault(o => SameSignature(o.Params, f.Params));
        if (existing is not null)
        {
            if (f.Body is not null && existing.Body is not null)
                throw Error(f.Line, $"'{f.Name}' : function already has a body");
            if (!existing.Return.SameAs(f.Return))
                throw Error(f.Line, $"'{f.Name}' : overloaded functions must have the same return type");
            info = existing;
            if (f.Body is null) return;
        }
        else
        {
            overloads.Add(info);
        }
        if (f.Body is null) return;

        var savedFrame = _frame;
        var savedCurrent = _current;
        _current = info;
        _frame = 0;
        _scopes.Add([]);
        var slots = new int[f.Params.Count];
        for (var i = 0; i < f.Params.Count; i++)
        {
            var p = f.Params[i];
            var local = DeclareLocal(p.Name, p.Type, f.Line);
            slots[i] = local.FrameSlot;
        }
        info.ParamSlots = slots;
        info.Body = CompileBlockBody(f.Body);
        info.FrameSize = _frame + 1;
        if (info.FrameSize > MaxFrameSlots) throw Error(f.Line, $"'{f.Name}' : too many local variables");
        _scopes.RemoveAt(_scopes.Count - 1);
        _frame = savedFrame;
        _current = savedCurrent;
    }

    private static bool SameSignature(List<ParamDecl> a, List<ParamDecl> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.Type.SameAs(p.Second.Type));

    // ---- statements ----

    private Func<ShaderContext, GlslFlow> CompileBlockBody(BlockStmt block)
    {
        var statements = block.Body.Select(CompileStatement).ToArray();
        return c =>
        {
            foreach (var s in statements)
            {
                var flow = s(c);
                if (flow != GlslFlow.Next) return flow;
            }
            return GlslFlow.Next;
        };
    }

    private Func<ShaderContext, GlslFlow> CompileStatement(GlslStmt stmt)
    {
        switch (stmt)
        {
            case BlockStmt b:
            {
                _scopes.Add([]);
                var run = CompileBlockBody(b);
                _scopes.RemoveAt(_scopes.Count - 1);
                return run;
            }
            case EmptyStmt:
                return _ => GlslFlow.Next;
            case DeclStmt d:
                return CompileDeclaration(d);
            case ExprStmt e:
            {
                var expr = CompileExpr(e.Expr);
                if (expr.Type.IsAggregate) { var a = expr.A!; return c => { a(c); return GlslFlow.Next; }; }
                var v = expr.V!;
                return c => { v(c); return GlslFlow.Next; };
            }
            case IfStmt i:
            {
                var condition = BoolOf(CompileExpr(i.Condition), i.Line);
                _scopes.Add([]);
                var then = CompileStatement(i.Then);
                _scopes.RemoveAt(_scopes.Count - 1);
                Func<ShaderContext, GlslFlow>? otherwise = null;
                if (i.Else is not null)
                {
                    _scopes.Add([]);
                    otherwise = CompileStatement(i.Else);
                    _scopes.RemoveAt(_scopes.Count - 1);
                }
                return otherwise is null
                    ? c => condition(c).X != 0 ? then(c) : GlslFlow.Next
                    : c => condition(c).X != 0 ? then(c) : otherwise(c);
            }
            case ForStmt f:
            {
                _scopes.Add([]);
                var init = f.Init is null ? null : CompileStatement(f.Init);
                var condition = f.Condition is null ? null : BoolOf(CompileExpr(f.Condition), f.Line);
                var step = f.Step is null ? null : CompileExpr(f.Step);
                _scopes.Add([]);
                var body = CompileStatement(f.Body);
                _scopes.RemoveAt(_scopes.Count - 1);
                _scopes.RemoveAt(_scopes.Count - 1);
                var stepV = step?.V;
                return c =>
                {
                    init?.Invoke(c);
                    for (;;)
                    {
                        c.Tick();
                        if (condition is not null && condition(c).X == 0) break;
                        var flow = body(c);
                        if (flow == GlslFlow.Break) break;
                        if (flow == GlslFlow.Return) return flow;
                        stepV?.Invoke(c);
                    }
                    return GlslFlow.Next;
                };
            }
            case WhileStmt w:
            {
                _scopes.Add([]);
                var condition = BoolOf(CompileExpr(w.Condition), w.Line);
                var body = CompileStatement(w.Body);
                _scopes.RemoveAt(_scopes.Count - 1);
                return c =>
                {
                    for (;;)
                    {
                        c.Tick();
                        if (condition(c).X == 0) break;
                        var flow = body(c);
                        if (flow == GlslFlow.Break) break;
                        if (flow == GlslFlow.Return) return flow;
                    }
                    return GlslFlow.Next;
                };
            }
            case DoWhileStmt dw:
            {
                _scopes.Add([]);
                var body = CompileStatement(dw.Body);
                var condition = BoolOf(CompileExpr(dw.Condition), dw.Line);
                _scopes.RemoveAt(_scopes.Count - 1);
                return c =>
                {
                    for (;;)
                    {
                        c.Tick();
                        var flow = body(c);
                        if (flow == GlslFlow.Break) break;
                        if (flow == GlslFlow.Return) return flow;
                        if (condition(c).X == 0) break;
                    }
                    return GlslFlow.Next;
                };
            }
            case ReturnStmt r:
                return CompileReturn(r);
            case JumpStmt j:
                return j.Kind switch
                {
                    "break" => _ => GlslFlow.Break,
                    "continue" => _ => GlslFlow.Continue,
                    _ => c =>
                    {
                        if (_stage != GlslStage.Fragment) return GlslFlow.Next;
                        c.Discarded = true;
                        return GlslFlow.Return;
                    },
                };
            default:
                throw Error(stmt.Line, "unsupported statement");
        }
    }

    private Func<ShaderContext, GlslFlow> CompileReturn(ReturnStmt r)
    {
        var fn = _current!;
        if (r.Value is null)
        {
            if (fn.Return.Base != GlslBase.Void) throw Error(r.Line, "'return' : function return is not matching type");
            return _ => GlslFlow.Return;
        }
        if (fn.Return.Base == GlslBase.Void) throw Error(r.Line, "'return' : void function cannot return a value");
        var value = Coerce(CompileExpr(r.Value), fn.Return, r.Line);
        if (value.Type.IsAggregate)
        {
            var a = value.A!;
            return c => { c.RetAgg = a(c); return GlslFlow.Return; };
        }
        var v = value.V!;
        return c => { c.Ret = v(c); return GlslFlow.Return; };
    }

    private Func<ShaderContext, GlslFlow> CompileDeclaration(DeclStmt d)
    {
        var actions = new List<Action<ShaderContext>>();
        foreach (var decl in d.Vars)
        {
            CExpr? init = null;
            if (decl.Init is not null) init = Coerce(CompileExpr(decl.Init), decl.Type, decl.Line);
            var local = DeclareLocal(decl.Name, decl.Type, decl.Line, decl.IsConst);
            var slot = local.FrameSlot;
            var count = decl.Type.Slots;
            if (init is null)
            {
                actions.Add(c => Array.Clear(c.L, c.Base + slot, count));
            }
            else if (decl.Type.IsAggregate)
            {
                var a = init.A!;
                actions.Add(c => Array.Copy(a(c), 0, c.L, c.Base + slot, count));
            }
            else
            {
                var v = init.V!;
                actions.Add(c => c.L[c.Base + slot] = v(c));
            }
        }
        var all = actions.ToArray();
        return c =>
        {
            foreach (var a in all) a(c);
            return GlslFlow.Next;
        };
    }

    private Func<ShaderContext, Val> BoolOf(CExpr e, int line)
    {
        if (e.Type.Base != GlslBase.Bool || e.Type.IsArray)
            throw Error(line, "'' : boolean expression expected");
        return e.V!;
    }
}
