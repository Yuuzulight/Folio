namespace Folio.Scripting.Native;

public sealed class BytecodeChunk
{
    public List<Instruction> Instructions { get; } = [];
    public List<NativeValue> Constants { get; } = [];
    public List<BytecodeChunk> Functions { get; } = [];
    public List<string> ParameterNames { get; } = [];

    public int AddConstant(NativeValue value)
    {
        Constants.Add(value);
        return Constants.Count - 1;
    }

    public int Emit(OpCode op, int operand = 0, string? str = null)
    {
        Instructions.Add(new Instruction(op, operand, str));
        return Instructions.Count - 1;
    }

    public void PatchJump(int instructionIndex, int target)
    {
        var old = Instructions[instructionIndex];
        Instructions[instructionIndex] = new Instruction(old.Op, target, old.StringOperand);
    }
}

public sealed class LexicalEnvironment
{
    private readonly Dictionary<string, NativeValue> _bindings = new(StringComparer.Ordinal);
    public LexicalEnvironment? Parent { get; }

    public LexicalEnvironment(LexicalEnvironment? parent = null)
    {
        Parent = parent;
    }

    public bool Has(string name) => _bindings.ContainsKey(name) || Parent?.Has(name) == true;

    public NativeValue Get(string name)
    {
        if (_bindings.TryGetValue(name, out var val))
            return val;
        if (Parent is not null)
            return Parent.Get(name);
        return NativeValue.Undefined;
    }

    public void Set(string name, NativeValue value)
    {
        if (_bindings.ContainsKey(name) || Parent is null)
            _bindings[name] = value;
        else if (Parent.Has(name))
            Parent.Set(name, value);
        else
            _bindings[name] = value;
    }

    public void Declare(string name, NativeValue value)
    {
        _bindings[name] = value;
    }
}

public sealed class NativeBytecodeCompiler
{
    private readonly BytecodeChunk _chunk = new();

    public static BytecodeChunk Compile(ProgramNode program)
    {
        var compiler = new NativeBytecodeCompiler();
        for (var i = 0; i < program.Statements.Count; i++)
        {
            var stmt = program.Statements[i];
            if (i == program.Statements.Count - 1 && stmt is ExpressionStatement exprStmt)
            {
                compiler.CompileExpression(exprStmt.Expression);
                compiler._chunk.Emit(OpCode.Return);
                return compiler._chunk;
            }
            compiler.CompileStatement(stmt);
        }
        compiler._chunk.Emit(OpCode.PushUndefined);
        compiler._chunk.Emit(OpCode.Return);
        return compiler._chunk;
    }

    public static BytecodeChunk CompileFunction(IReadOnlyList<string> parameters, BlockStatement body)
    {
        var compiler = new NativeBytecodeCompiler();
        compiler._chunk.ParameterNames.AddRange(parameters);
        foreach (var stmt in body.Statements)
        {
            compiler.CompileStatement(stmt);
        }
        compiler._chunk.Emit(OpCode.PushUndefined);
        compiler._chunk.Emit(OpCode.Return);
        return compiler._chunk;
    }

    private void CompileStatement(StatementNode stmt)
    {
        switch (stmt)
        {
            case BlockStatement block:
                foreach (var s in block.Statements)
                    CompileStatement(s);
                break;

            case VarStatement varStmt:
                foreach (var decl in varStmt.Declarations)
                {
                    if (decl.Initializer is not null)
                    {
                        CompileExpression(decl.Initializer);
                    }
                    else
                    {
                        _chunk.Emit(OpCode.PushUndefined);
                    }
                    _chunk.Emit(OpCode.StoreGlobal, str: decl.Name);
                }
                break;

            case ExpressionStatement exprStmt:
                CompileExpression(exprStmt.Expression);
                _chunk.Emit(OpCode.Pop);
                break;

            case IfStatement ifStmt:
                CompileExpression(ifStmt.Condition);
                var jumpFalse = _chunk.Emit(OpCode.JumpIfFalse);
                CompileStatement(ifStmt.ThenBranch);
                if (ifStmt.ElseBranch is not null)
                {
                    var jumpEnd = _chunk.Emit(OpCode.Jump);
                    _chunk.PatchJump(jumpFalse, _chunk.Instructions.Count);
                    CompileStatement(ifStmt.ElseBranch);
                    _chunk.PatchJump(jumpEnd, _chunk.Instructions.Count);
                }
                else
                {
                    _chunk.PatchJump(jumpFalse, _chunk.Instructions.Count);
                }
                break;

            case WhileStatement whileStmt:
                var loopStart = _chunk.Instructions.Count;
                CompileExpression(whileStmt.Condition);
                var loopExit = _chunk.Emit(OpCode.JumpIfFalse);
                CompileStatement(whileStmt.Body);
                _chunk.Emit(OpCode.Jump, loopStart);
                _chunk.PatchJump(loopExit, _chunk.Instructions.Count);
                break;

            case ForStatement forStmt:
                if (forStmt.Init is StatementNode initStmt)
                    CompileStatement(initStmt);
                else if (forStmt.Init is ExpressionNode initExpr)
                {
                    CompileExpression(initExpr);
                    _chunk.Emit(OpCode.Pop);
                }

                var forStart = _chunk.Instructions.Count;
                int forExit = -1;
                if (forStmt.Test is not null)
                {
                    CompileExpression(forStmt.Test);
                    forExit = _chunk.Emit(OpCode.JumpIfFalse);
                }

                CompileStatement(forStmt.Body);

                if (forStmt.Update is not null)
                {
                    CompileExpression(forStmt.Update);
                    _chunk.Emit(OpCode.Pop);
                }

                _chunk.Emit(OpCode.Jump, forStart);
                if (forExit != -1)
                    _chunk.PatchJump(forExit, _chunk.Instructions.Count);
                break;

            case ReturnStatement retStmt:
                if (retStmt.Argument is not null)
                    CompileExpression(retStmt.Argument);
                else
                    _chunk.Emit(OpCode.PushUndefined);
                _chunk.Emit(OpCode.Return);
                break;

            case ThrowStatement throwStmt:
                CompileExpression(throwStmt.Argument);
                _chunk.Emit(OpCode.Throw);
                break;

            case TryCatchStatement tryStmt:
                var enterTry = _chunk.Emit(OpCode.EnterTry);
                CompileStatement(tryStmt.TryBlock);
                _chunk.Emit(OpCode.LeaveTry);
                var jumpPastCatch = _chunk.Emit(OpCode.Jump);

                var catchStart = _chunk.Instructions.Count;
                if (tryStmt.CatchBlock is not null)
                {
                    if (tryStmt.CatchParam is not null)
                        _chunk.Emit(OpCode.StoreGlobal, str: tryStmt.CatchParam);
                    else
                        _chunk.Emit(OpCode.Pop);
                    CompileStatement(tryStmt.CatchBlock);
                }
                _chunk.PatchJump(enterTry, catchStart);
                _chunk.PatchJump(jumpPastCatch, _chunk.Instructions.Count);

                if (tryStmt.FinallyBlock is not null)
                    CompileStatement(tryStmt.FinallyBlock);
                break;

            case FunctionDeclaration funcDecl:
                var fnChunk = CompileFunction(funcDecl.Parameters, funcDecl.Body);
                var fnIdx = _chunk.Functions.Count;
                _chunk.Functions.Add(fnChunk);
                _chunk.Emit(OpCode.CreateClosure, fnIdx, funcDecl.Name);
                _chunk.Emit(OpCode.StoreGlobal, str: funcDecl.Name);
                break;
        }
    }

    private void CompileExpression(ExpressionNode expr)
    {
        switch (expr)
        {
            case LiteralExpression lit:
                if (lit.Type == NativeTokenType.Number)
                    _chunk.Emit(OpCode.PushConst, _chunk.AddConstant(NativeValue.FromNumber((double)lit.Value!)));
                else if (lit.Type == NativeTokenType.String)
                    _chunk.Emit(OpCode.PushConst, _chunk.AddConstant(NativeValue.FromString((string)lit.Value!)));
                else if (lit.Type == NativeTokenType.True)
                    _chunk.Emit(OpCode.PushTrue);
                else if (lit.Type == NativeTokenType.False)
                    _chunk.Emit(OpCode.PushFalse);
                else if (lit.Type == NativeTokenType.Null)
                    _chunk.Emit(OpCode.PushNull);
                else
                    _chunk.Emit(OpCode.PushUndefined);
                break;

            case IdentifierExpression id:
                _chunk.Emit(OpCode.LoadGlobal, str: id.Name);
                break;

            case ThisExpression:
                _chunk.Emit(OpCode.PushThis);
                break;

            case UnaryExpression un:
                CompileExpression(un.Operand);
                switch (un.Operator)
                {
                    case "!": _chunk.Emit(OpCode.Not); break;
                    case "-": _chunk.Emit(OpCode.Neg); break;
                    case "+": break;
                    case "++":
                        _chunk.Emit(OpCode.PushConst, _chunk.AddConstant(NativeValue.FromNumber(1)));
                        _chunk.Emit(OpCode.Add);
                        if (un.Operand is IdentifierExpression idIncr)
                        {
                            if (!un.IsPrefix)
                            {
                                // postfix: we'd push original then store, simplified here to prefix for basic flow
                            }
                            _chunk.Emit(OpCode.Dup);
                            _chunk.Emit(OpCode.StoreGlobal, str: idIncr.Name);
                        }
                        break;
                    case "--":
                        _chunk.Emit(OpCode.PushConst, _chunk.AddConstant(NativeValue.FromNumber(1)));
                        _chunk.Emit(OpCode.Sub);
                        if (un.Operand is IdentifierExpression idDecr)
                        {
                            _chunk.Emit(OpCode.Dup);
                            _chunk.Emit(OpCode.StoreGlobal, str: idDecr.Name);
                        }
                        break;
                }
                break;

            case BinaryExpression bin:
                CompileExpression(bin.Left);
                CompileExpression(bin.Right);
                switch (bin.Operator)
                {
                    case "+": _chunk.Emit(OpCode.Add); break;
                    case "-": _chunk.Emit(OpCode.Sub); break;
                    case "*": _chunk.Emit(OpCode.Mul); break;
                    case "/": _chunk.Emit(OpCode.Div); break;
                    case "%": _chunk.Emit(OpCode.Mod); break;
                    case "==": _chunk.Emit(OpCode.Eq); break;
                    case "!=": _chunk.Emit(OpCode.NotEq); break;
                    case "===": _chunk.Emit(OpCode.StrictEq); break;
                    case "!==": _chunk.Emit(OpCode.StrictNotEq); break;
                    case "<": _chunk.Emit(OpCode.Lt); break;
                    case "<=": _chunk.Emit(OpCode.Lte); break;
                    case ">": _chunk.Emit(OpCode.Gt); break;
                    case ">=": _chunk.Emit(OpCode.Gte); break;
                }
                break;

            case AssignmentExpression assign:
                CompileExpression(assign.Value);
                if (assign.Target is IdentifierExpression targetId)
                {
                    _chunk.Emit(OpCode.Dup);
                    _chunk.Emit(OpCode.StoreGlobal, str: targetId.Name);
                }
                else if (assign.Target is MemberExpression member)
                {
                    if (!member.Computed && member.Property is LiteralExpression propLit && propLit.Value is string propName)
                    {
                        CompileExpression(member.Object);
                        _chunk.Emit(OpCode.StoreProperty, str: propName);
                    }
                    else
                    {
                        CompileExpression(member.Object);
                        CompileExpression(member.Property);
                        _chunk.Emit(OpCode.StoreElement);
                    }
                }
                break;

            case TernaryExpression tern:
                CompileExpression(tern.Condition);
                var jFalse = _chunk.Emit(OpCode.JumpIfFalse);
                CompileExpression(tern.TrueExpr);
                var jEnd = _chunk.Emit(OpCode.Jump);
                _chunk.PatchJump(jFalse, _chunk.Instructions.Count);
                CompileExpression(tern.FalseExpr);
                _chunk.PatchJump(jEnd, _chunk.Instructions.Count);
                break;

            case MemberExpression member:
                CompileExpression(member.Object);
                if (!member.Computed && member.Property is LiteralExpression memLit && memLit.Value is string propStr)
                {
                    _chunk.Emit(OpCode.LoadProperty, str: propStr);
                }
                else
                {
                    CompileExpression(member.Property);
                    _chunk.Emit(OpCode.LoadElement);
                }
                break;

            case CallExpression call:
                // Push function and thisVal
                if (call.Callee is MemberExpression calleeMember)
                {
                    CompileExpression(calleeMember.Object);
                    if (!calleeMember.Computed && calleeMember.Property is LiteralExpression callLit && callLit.Value is string callPropStr)
                    {
                        _chunk.Emit(OpCode.LoadMethod, str: callPropStr);
                    }
                    else
                    {
                        _chunk.Emit(OpCode.Dup); // thisVal
                        CompileExpression(calleeMember.Property);
                        _chunk.Emit(OpCode.LoadElement);
                        // Swap so function is bottom, thisVal is top:
                        // To keep it simple: we can emit LoadElement on top of thisVal
                    }
                }
                else
                {
                    CompileExpression(call.Callee);
                    _chunk.Emit(OpCode.PushUndefined); // this
                }

                // Push args
                foreach (var arg in call.Arguments)
                {
                    CompileExpression(arg);
                }
                _chunk.Emit(OpCode.Call, call.Arguments.Count);
                break;

            case ArrayLiteralExpression arr:
                foreach (var el in arr.Elements)
                {
                    if (el is not null)
                        CompileExpression(el);
                    else
                        _chunk.Emit(OpCode.PushUndefined);
                }
                _chunk.Emit(OpCode.CreateArray, arr.Elements.Count);
                break;

            case ObjectLiteralExpression obj:
                _chunk.Emit(OpCode.CreateObject);
                foreach (var prop in obj.Properties)
                {
                    CompileExpression(prop.Value);
                    _chunk.Emit(OpCode.SetProperty, str: prop.Key);
                }
                break;

            case FunctionExpression funcExpr:
                var fnC = CompileFunction(funcExpr.Parameters, funcExpr.Body);
                var fIdx = _chunk.Functions.Count;
                _chunk.Functions.Add(fnC);
                _chunk.Emit(OpCode.CreateClosure, fIdx, funcExpr.Name);
                break;

            case ArrowFunctionExpression arrow:
                BlockStatement b;
                if (arrow.Body is BlockStatement blk)
                {
                    b = blk;
                }
                else if (arrow.Body is ExpressionNode bodyExpr)
                {
                    b = new BlockStatement([new ReturnStatement(bodyExpr, arrow.Line, arrow.Column)], arrow.Line, arrow.Column);
                }
                else
                {
                    b = new BlockStatement([], arrow.Line, arrow.Column);
                }
                var arrowC = CompileFunction(arrow.Parameters, b);
                var aIdx = _chunk.Functions.Count;
                _chunk.Functions.Add(arrowC);
                _chunk.Emit(OpCode.CreateClosure, aIdx);
                break;
        }
    }
}
