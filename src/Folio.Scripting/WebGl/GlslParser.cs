namespace Folio.Scripting.WebGl;

internal abstract record GlslExpr(int Line);
internal sealed record IntLit(long Value, int Line) : GlslExpr(Line);
internal sealed record FloatLit(double Value, int Line) : GlslExpr(Line);
internal sealed record BoolLit(bool Value, int Line) : GlslExpr(Line);
internal sealed record NameExpr(string Name, int Line) : GlslExpr(Line);
internal sealed record UnaryExpr(string Op, GlslExpr Operand, int Line) : GlslExpr(Line);
internal sealed record PostfixExpr(string Op, GlslExpr Operand, int Line) : GlslExpr(Line);
internal sealed record BinaryExpr(string Op, GlslExpr Left, GlslExpr Right, int Line) : GlslExpr(Line);
internal sealed record AssignExpr(string Op, GlslExpr Target, GlslExpr Value, int Line) : GlslExpr(Line);
internal sealed record TernaryExpr(GlslExpr Condition, GlslExpr Then, GlslExpr Else, int Line) : GlslExpr(Line);
internal sealed record CallExpr(string Name, List<GlslExpr> Args, int Line) : GlslExpr(Line);
internal sealed record FieldExpr(GlslExpr Target, string Name, int Line) : GlslExpr(Line);
internal sealed record IndexExpr(GlslExpr Target, GlslExpr Index, int Line) : GlslExpr(Line);
internal sealed record SequenceExpr(GlslExpr Left, GlslExpr Right, int Line) : GlslExpr(Line);

internal abstract record GlslStmt(int Line);
internal sealed record BlockStmt(List<GlslStmt> Body, int Line) : GlslStmt(Line);
internal sealed record DeclStmt(List<VarDecl> Vars, int Line) : GlslStmt(Line);
internal sealed record ExprStmt(GlslExpr Expr, int Line) : GlslStmt(Line);
internal sealed record IfStmt(GlslExpr Condition, GlslStmt Then, GlslStmt? Else, int Line) : GlslStmt(Line);
internal sealed record ForStmt(GlslStmt? Init, GlslExpr? Condition, GlslExpr? Step, GlslStmt Body, int Line) : GlslStmt(Line);
internal sealed record WhileStmt(GlslExpr Condition, GlslStmt Body, int Line) : GlslStmt(Line);
internal sealed record DoWhileStmt(GlslStmt Body, GlslExpr Condition, int Line) : GlslStmt(Line);
internal sealed record ReturnStmt(GlslExpr? Value, int Line) : GlslStmt(Line);
internal sealed record JumpStmt(string Kind, int Line) : GlslStmt(Line); // break, continue, discard
internal sealed record EmptyStmt(int Line) : GlslStmt(Line);

internal sealed record VarDecl(string Name, GlslType Type, GlslExpr? Init, bool IsConst, int Line);

internal sealed record ParamDecl(string Name, GlslType Type, string Qualifier);

internal sealed record FunctionDecl(string Name, GlslType Return, List<ParamDecl> Params, BlockStmt? Body, int Line);

internal sealed record GlobalDecl(string Storage, GlslType Type, string Name, GlslExpr? Init, bool IsConst, int Location, int Line);

internal sealed class GlslUnit
{
    public List<GlobalDecl> Globals { get; } = [];
    public List<FunctionDecl> Functions { get; } = [];
    public Dictionary<string, GlslStructDef> Structs { get; } = [];
    /// <summary>Global variable declarations and functions in source order, for the compiler to resolve names against what came before.</summary>
    public List<object> Items { get; } = [];
    public int Version { get; set; } = 100;
}

internal sealed class GlslParser
{
    private readonly List<GlslToken> _tokens;
    private int _position;
    private readonly GlslUnit _unit = new();

    private GlslParser(List<GlslToken> tokens) => _tokens = tokens;

    public static GlslUnit Parse(string source)
    {
        var tokens = GlslLexer.Lex(source, out var version);
        var parser = new GlslParser(tokens);
        parser._unit.Version = version;
        parser.ParseUnit();
        return parser._unit;
    }

    // Deeply nested source would otherwise run the parser, and later the compiler, out of stack.
    private const int MaxDepth = 200;
    private int _depth;

    private void Enter()
    {
        if (++_depth > MaxDepth)
            throw new GlslCompileException("syntax error: nesting is too deep", Peek().Line);
    }

    private GlslToken Peek(int offset = 0) => _tokens[Math.Min(_position + offset, _tokens.Count - 1)];
    private GlslToken Next() => _tokens[Math.Min(_position++, _tokens.Count - 1)];
    private bool At(string text) => Peek().Is(text);

    private bool Accept(string text)
    {
        if (!At(text)) return false;
        _position++;
        return true;
    }

    private GlslToken Expect(string text)
    {
        if (!At(text))
            throw new GlslCompileException($"syntax error: expected '{text}' but found '{Peek().Text}'", Peek().Line);
        return Next();
    }

    private string ExpectIdent()
    {
        if (Peek().Kind != GlslTokenKind.Ident)
            throw new GlslCompileException($"syntax error: expected an identifier but found '{Peek().Text}'", Peek().Line);
        return Next().Text;
    }

    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "if", "else", "for", "while", "do", "return", "break", "continue", "discard", "struct", "switch", "case", "default",
        "const", "in", "out", "inout", "uniform", "attribute", "varying", "layout", "precision", "highp", "mediump", "lowp",
        "invariant", "flat", "smooth", "centroid", "true", "false", "void",
    };

    private static readonly HashSet<string> Qualifiers = new(StringComparer.Ordinal)
    {
        "const", "in", "out", "inout", "uniform", "attribute", "varying", "layout", "highp", "mediump", "lowp", "invariant", "flat", "smooth", "centroid",
    };

    private bool AtTypeStart(int offset = 0)
    {
        var t = Peek(offset);
        if (t.Kind != GlslTokenKind.Ident) return false;
        return GlslType.TryParse(t.Text, out _) || _unit.Structs.ContainsKey(t.Text) || t.Text == "struct";
    }

    private void ParseUnit()
    {
        while (Peek().Kind != GlslTokenKind.End)
            ParseExternal();
    }

    private void ParseExternal()
    {
        if (Accept(";")) return;
        if (At("precision"))
        {
            Next();
            Next(); // precision qualifier
            ParseTypeSpecifier();
            Expect(";");
            return;
        }
        if (At("invariant") && Peek(1).Kind == GlslTokenKind.Ident && !AtTypeStart(1) && !Qualifiers.Contains(Peek(1).Text))
        {
            Next(); ExpectIdent(); Expect(";");
            return;
        }
        var line = Peek().Line;
        var storage = "";
        var isConst = false;
        var location = -1;
        while (Qualifiers.Contains(Peek().Text) && Peek().Kind == GlslTokenKind.Ident)
        {
            var q = Next().Text;
            if (q == "layout")
            {
                Expect("(");
                while (!At(")"))
                {
                    var key = ExpectIdent();
                    if (Accept("=") && key == "location") location = (int)Next().Number;
                    else if (key != "location" && _tokens[_position - 1].Kind == GlslTokenKind.Int) { }
                    if (!Accept(",")) break;
                }
                Expect(")");
            }
            else if (q == "const") isConst = true;
            else if (q is "uniform" or "attribute" or "varying" or "in" or "out") storage = q;
        }
        if (At("struct") && Peek(2).Is("{"))
        {
            // struct definition, possibly followed by a declarator
            var type = ParseTypeSpecifier();
            if (Accept(";")) return;
            ParseGlobalDeclarators(type, storage, isConst, location, line);
            return;
        }
        var specifier = ParseTypeSpecifier();
        if (Peek().Kind == GlslTokenKind.Ident && Peek(1).Is("("))
        {
            ParseFunction(specifier, line);
            return;
        }
        ParseGlobalDeclarators(specifier, storage, isConst, location, line);
    }

    private void ParseGlobalDeclarators(GlslType type, string storage, bool isConst, int location, int line)
    {
        do
        {
            var name = ExpectIdent();
            var t = type;
            if (Accept("["))
            {
                var size = ParseConstantSize();
                Expect("]");
                t = t with { ArraySize = size };
            }
            GlslExpr? init = null;
            if (Accept("=")) init = ParseAssignment();
            RecordConstant(t, name, init, isConst);
            var decl = new GlobalDecl(storage, t, name, init, isConst, location, line);
            _unit.Globals.Add(decl);
            _unit.Items.Add(decl);
        } while (Accept(","));
        Expect(";");
    }

    private readonly Dictionary<string, long> _constInts = [];

    // An array size: an integer expression of literals and named integer constants.
    private int ParseConstantSize()
    {
        var line = Peek().Line;
        var size = FoldInt(ParseTernary(), line);
        if (size is < 1 or > 65536) throw new GlslCompileException("array size must be a positive integer constant", line);
        return (int)size;
    }

    private long FoldInt(GlslExpr expr, int line) => expr switch
    {
        IntLit lit => lit.Value,
        NameExpr name when _constInts.TryGetValue(name.Name, out var value) => value,
        UnaryExpr { Op: "-" } u => -FoldInt(u.Operand, line),
        UnaryExpr { Op: "+" } u => FoldInt(u.Operand, line),
        BinaryExpr b => (b.Op, FoldInt(b.Left, line), FoldInt(b.Right, line)) switch
        {
            ("+", var l, var r) => l + r,
            ("-", var l, var r) => l - r,
            ("*", var l, var r) => l * r,
            ("/", var l, var r) when r != 0 => l / r,
            _ => throw new GlslCompileException("array size must be a constant integer expression", line),
        },
        _ => throw new GlslCompileException("array size must be a constant integer expression", line),
    };

    private void RecordConstant(GlslType type, string name, GlslExpr? init, bool isConst)
    {
        if (isConst && type.Base == GlslBase.Int && !type.IsArray && init is not null)
        {
            try { _constInts[name] = FoldInt(init, 0); } catch (GlslCompileException) { }
        }
    }

    private GlslType ParseTypeSpecifier()
    {
        // precision qualifiers are not part of the type
        while (At("highp") || At("mediump") || At("lowp")) Next();
        if (At("struct"))
        {
            Next();
            var name = Peek().Kind == GlslTokenKind.Ident && !At("{") ? Next().Text : $"anonymous{_unit.Structs.Count}";
            Expect("{");
            var def = new GlslStructDef(name);
            while (!At("}"))
            {
                var fieldType = ParseTypeSpecifier();
                do
                {
                    var fname = ExpectIdent();
                    var ft = fieldType;
                    if (Accept("[")) { ft = ft with { ArraySize = ParseConstantSize() }; Expect("]"); }
                    def.Fields.Add((fname, ft));
                } while (Accept(","));
                Expect(";");
            }
            Expect("}");
            _unit.Structs[name] = def;
            return new GlslType(GlslBase.Struct, 0, def);
        }
        var t = Peek();
        if (t.Kind != GlslTokenKind.Ident)
            throw new GlslCompileException($"syntax error: expected a type but found '{t.Text}'", t.Line);
        Next();
        GlslType result;
        if (GlslType.TryParse(t.Text, out var builtin)) result = builtin;
        else if (_unit.Structs.TryGetValue(t.Text, out var def2)) result = new GlslType(GlslBase.Struct, 0, def2);
        else throw new GlslCompileException($"'{t.Text}' : undeclared identifier or unknown type", t.Line);
        if (At("[") && Peek(1).Kind == GlslTokenKind.Int && Peek(2).Is("]"))
        {
            Next();
            var size = (int)Next().Number;
            Next();
            result = result with { ArraySize = size };
        }
        return result;
    }

    private void ParseFunction(GlslType returnType, int line)
    {
        var name = ExpectIdent();
        Expect("(");
        var parameters = new List<ParamDecl>();
        if (At("void") && Peek(1).Is(")")) Next();
        while (!At(")"))
        {
            var qualifier = "in";
            var isConst = false;
            while (Qualifiers.Contains(Peek().Text) && Peek().Kind == GlslTokenKind.Ident)
            {
                var q = Next().Text;
                if (q is "in" or "out" or "inout") qualifier = q;
                else if (q == "const") isConst = true;
            }
            _ = isConst;
            var pt = ParseTypeSpecifier();
            var pname = Peek().Kind == GlslTokenKind.Ident ? Next().Text : $"_arg{parameters.Count}";
            if (Accept("[")) { pt = pt with { ArraySize = ParseConstantSize() }; Expect("]"); }
            parameters.Add(new ParamDecl(pname, pt, qualifier));
            if (!Accept(",")) break;
        }
        Expect(")");
        BlockStmt? body = null;
        if (At("{")) body = ParseBlock();
        else Expect(";");
        var fn = new FunctionDecl(name, returnType, parameters, body, line);
        _unit.Functions.Add(fn);
        _unit.Items.Add(fn);
    }

    private BlockStmt ParseBlock()
    {
        var line = Expect("{").Line;
        var body = new List<GlslStmt>();
        while (!At("}"))
        {
            if (Peek().Kind == GlslTokenKind.End) throw new GlslCompileException("syntax error: unexpected end of shader (missing '}')", Peek().Line);
            body.Add(ParseStatement());
        }
        Expect("}");
        return new BlockStmt(body, line);
    }

    private bool AtDeclaration()
    {
        var t = Peek();
        if (t.Kind != GlslTokenKind.Ident) return false;
        if (Qualifiers.Contains(t.Text) && t.Text is "const" or "highp" or "mediump" or "lowp") return true;
        if (!AtTypeStart()) return false;
        // `vec3(1.0)` at the start of a statement is a constructor expression, `vec3 x` a declaration.
        var next = Peek(1);
        if (t.Text == "struct") return true;
        return next.Kind == GlslTokenKind.Ident || next.Is("[") && Peek(2).Kind == GlslTokenKind.Int && Peek(3).Is("]");
    }

    private GlslStmt ParseStatement()
    {
        Enter();
        try { return ParseStatementCore(); }
        finally { _depth--; }
    }

    private GlslStmt ParseStatementCore()
    {
        var t = Peek();
        var line = t.Line;
        if (At("{")) return ParseBlock();
        if (Accept(";")) return new EmptyStmt(line);
        if (At("if"))
        {
            Next(); Expect("(");
            var cond = ParseExpression();
            Expect(")");
            var then = ParseStatement();
            GlslStmt? otherwise = null;
            if (Accept("else")) otherwise = ParseStatement();
            return new IfStmt(cond, then, otherwise, line);
        }
        if (At("for"))
        {
            Next(); Expect("(");
            GlslStmt? init = null;
            if (!Accept(";")) init = ParseStatement();
            GlslExpr? cond = null;
            if (!At(";")) cond = ParseExpression();
            Expect(";");
            GlslExpr? step = null;
            if (!At(")")) step = ParseExpression();
            Expect(")");
            return new ForStmt(init, cond, step, ParseStatement(), line);
        }
        if (At("while"))
        {
            Next(); Expect("(");
            var cond = ParseExpression();
            Expect(")");
            return new WhileStmt(cond, ParseStatement(), line);
        }
        if (At("do"))
        {
            Next();
            var body = ParseStatement();
            Expect("while"); Expect("(");
            var cond = ParseExpression();
            Expect(")"); Expect(";");
            return new DoWhileStmt(body, cond, line);
        }
        if (At("return"))
        {
            Next();
            GlslExpr? value = At(";") ? null : ParseExpression();
            Expect(";");
            return new ReturnStmt(value, line);
        }
        if (At("break") || At("continue") || At("discard"))
        {
            var kind = Next().Text;
            Expect(";");
            return new JumpStmt(kind, line);
        }
        if (AtDeclaration())
        {
            var decl = ParseLocalDeclaration();
            Expect(";");
            return decl;
        }
        var expr = ParseExpression();
        Expect(";");
        return new ExprStmt(expr, line);
    }

    private DeclStmt ParseLocalDeclaration()
    {
        var line = Peek().Line;
        var isConst = false;
        while (Peek().Kind == GlslTokenKind.Ident && Qualifiers.Contains(Peek().Text))
        {
            if (Next().Text == "const") isConst = true;
        }
        var type = ParseTypeSpecifier();
        var vars = new List<VarDecl>();
        do
        {
            var name = ExpectIdent();
            var t = type;
            if (Accept("[")) { t = t with { ArraySize = ParseConstantSize() }; Expect("]"); }
            GlslExpr? init = null;
            if (Accept("=")) init = ParseAssignment();
            RecordConstant(t, name, init, isConst);
            vars.Add(new VarDecl(name, t, init, isConst, line));
        } while (Accept(","));
        return new DeclStmt(vars, line);
    }

    private GlslExpr ParseExpression()
    {
        var left = ParseAssignment();
        while (At(","))
        {
            var line = Next().Line;
            left = new SequenceExpr(left, ParseAssignment(), line);
        }
        return left;
    }

    private static readonly HashSet<string> AssignOps = ["=", "+=", "-=", "*=", "/=", "%=", "<<=", ">>=", "&=", "|=", "^="];

    private GlslExpr ParseAssignment()
    {
        Enter();
        try { return ParseAssignmentCore(); }
        finally { _depth--; }
    }

    private GlslExpr ParseAssignmentCore()
    {
        var left = ParseTernary();
        if (Peek().Kind == GlslTokenKind.Punct && AssignOps.Contains(Peek().Text))
        {
            var op = Next();
            return new AssignExpr(op.Text, left, ParseAssignment(), op.Line);
        }
        return left;
    }

    private GlslExpr ParseTernary()
    {
        var cond = ParseBinary(0);
        if (At("?"))
        {
            var line = Next().Line;
            var then = ParseAssignment();
            Expect(":");
            var otherwise = ParseAssignment();
            return new TernaryExpr(cond, then, otherwise, line);
        }
        return cond;
    }

    private static readonly string[][] Levels =
    [
        ["||"], ["^^"], ["&&"], ["|"], ["^"], ["&"], ["==", "!="], ["<", ">", "<=", ">="], ["<<", ">>"], ["+", "-"], ["*", "/", "%"],
    ];

    private GlslExpr ParseBinary(int level)
    {
        if (level >= Levels.Length) return ParseUnary();
        var left = ParseBinary(level + 1);
        while (Peek().Kind == GlslTokenKind.Punct && Levels[level].Contains(Peek().Text))
        {
            var op = Next();
            left = new BinaryExpr(op.Text, left, ParseBinary(level + 1), op.Line);
        }
        return left;
    }

    private GlslExpr ParseUnary()
    {
        Enter();
        try { return ParseUnaryCore(); }
        finally { _depth--; }
    }

    private GlslExpr ParseUnaryCore()
    {
        var t = Peek();
        if (t.Kind == GlslTokenKind.Punct && t.Text is "+" or "-" or "!" or "~" or "++" or "--")
        {
            Next();
            return new UnaryExpr(t.Text, ParseUnary(), t.Line);
        }
        return ParsePostfix();
    }

    private GlslExpr ParsePostfix()
    {
        var expr = ParsePrimary();
        for (;;)
        {
            var t = Peek();
            if (Accept("."))
            {
                var member = ExpectIdent();
                if (At("(") && member == "length")
                {
                    Next(); Expect(")");
                    expr = new CallExpr(".length", [expr], t.Line);
                }
                else
                {
                    expr = new FieldExpr(expr, member, t.Line);
                }
            }
            else if (Accept("["))
            {
                var index = ParseExpression();
                Expect("]");
                expr = new IndexExpr(expr, index, t.Line);
            }
            else if (t.Is("++") || t.Is("--"))
            {
                Next();
                expr = new PostfixExpr(t.Text, expr, t.Line);
            }
            else
            {
                return expr;
            }
        }
    }

    private GlslExpr ParsePrimary()
    {
        var t = Next();
        switch (t.Kind)
        {
            case GlslTokenKind.Int:
                return new IntLit((long)t.Number, t.Line);
            case GlslTokenKind.Float:
                return new FloatLit(t.Number, t.Line);
            case GlslTokenKind.Ident:
                if (t.Text == "true") return new BoolLit(true, t.Line);
                if (t.Text == "false") return new BoolLit(false, t.Line);
                if (Accept("("))
                {
                    var args = new List<GlslExpr>();
                    if (At("void") && Peek(1).Is(")")) Next();
                    while (!At(")"))
                    {
                        args.Add(ParseAssignment());
                        if (!Accept(",")) break;
                    }
                    Expect(")");
                    return new CallExpr(t.Text, args, t.Line);
                }
                // An array constructor: float[3](...)
                if (At("[") && Peek(1).Kind == GlslTokenKind.Int && Peek(2).Is("]") && Peek(3).Is("(") && GlslType.TryParse(t.Text, out _))
                {
                    Next(); var size = (int)Next().Number; Next(); Next();
                    var args = new List<GlslExpr>();
                    while (!At(")")) { args.Add(ParseAssignment()); if (!Accept(",")) break; }
                    Expect(")");
                    return new CallExpr($"{t.Text}[{size}]", args, t.Line);
                }
                if (Reserved.Contains(t.Text) || GlslType.TryParse(t.Text, out _))
                    throw new GlslCompileException($"syntax error: unexpected '{t.Text}'", t.Line);
                return new NameExpr(t.Text, t.Line);
            case GlslTokenKind.Punct when t.Text == "(":
            {
                var inner = ParseExpression();
                Expect(")");
                return inner;
            }
            default:
                throw new GlslCompileException($"syntax error: unexpected '{t.Text}'", t.Line);
        }
    }
}
