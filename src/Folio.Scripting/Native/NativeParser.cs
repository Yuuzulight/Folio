namespace Folio.Scripting.Native;

public sealed class NativeParser
{
    private readonly IReadOnlyList<NativeToken> _tokens;
    private int _current;

    public NativeParser(IReadOnlyList<NativeToken> tokens)
    {
        _tokens = tokens ?? [];
    }

    public static ProgramNode Parse(string source)
    {
        var tokenizer = new NativeTokenizer(source);
        var tokens = tokenizer.Tokenize();
        var parser = new NativeParser(tokens);
        return parser.ParseProgram();
    }

    public ProgramNode ParseProgram()
    {
        var stmts = new List<StatementNode>();
        var first = Peek();
        while (!IsAtEnd())
        {
            if (Match(NativeTokenType.Semicolon))
                continue;
            stmts.Add(ParseStatement());
        }
        return new ProgramNode(stmts, first.Line, first.Column);
    }

    private StatementNode ParseStatement()
    {
        var token = Peek();
        if (Match(NativeTokenType.OpenBrace))
            return ParseBlock();
        if (Match(NativeTokenType.Var, NativeTokenType.Let, NativeTokenType.Const))
            return ParseVarStatement(Previous());
        if (Match(NativeTokenType.If))
            return ParseIfStatement();
        if (Match(NativeTokenType.While))
            return ParseWhileStatement();
        if (Match(NativeTokenType.Do))
            return ParseDoWhileStatement();
        if (Match(NativeTokenType.For))
            return ParseForStatement();
        if (Match(NativeTokenType.Switch))
            return ParseSwitchStatement();
        if (Match(NativeTokenType.Return))
            return ParseReturnStatement();
        if (Match(NativeTokenType.Break))
        {
            var line = Previous().Line;
            var col = Previous().Column;
            Match(NativeTokenType.Semicolon);
            return new BreakStatement(line, col);
        }
        if (Match(NativeTokenType.Continue))
        {
            var line = Previous().Line;
            var col = Previous().Column;
            Match(NativeTokenType.Semicolon);
            return new ContinueStatement(line, col);
        }
        if (Match(NativeTokenType.Throw))
            return ParseThrowStatement();
        if (Match(NativeTokenType.Try))
            return ParseTryStatement();
        if (Match(NativeTokenType.Function))
            return ParseFunctionDeclaration();

        return ParseExpressionStatement();
    }

    private BlockStatement ParseBlock()
    {
        var line = Previous().Line;
        var col = Previous().Column;
        var stmts = new List<StatementNode>();
        while (!Check(NativeTokenType.CloseBrace) && !IsAtEnd())
        {
            if (Match(NativeTokenType.Semicolon))
                continue;
            stmts.Add(ParseStatement());
        }
        Consume(NativeTokenType.CloseBrace, "Expected '}' after block.");
        return new BlockStatement(stmts, line, col);
    }

    private VarStatement ParseVarStatement(NativeToken kindToken)
    {
        var decls = new List<VarDeclaration>();
        do
        {
            var nameToken = Consume(NativeTokenType.Identifier, "Expected variable name.");
            ExpressionNode? init = null;
            if (Match(NativeTokenType.Equal))
            {
                init = ParseExpression();
            }
            decls.Add(new VarDeclaration(nameToken.Text, init, nameToken.Line, nameToken.Column));
        } while (Match(NativeTokenType.Comma));

        Match(NativeTokenType.Semicolon);
        return new VarStatement(kindToken.Text, decls, kindToken.Line, kindToken.Column);
    }

    private IfStatement ParseIfStatement()
    {
        var line = Previous().Line;
        var col = Previous().Column;
        Consume(NativeTokenType.OpenParen, "Expected '(' after 'if'.");
        var cond = ParseExpression();
        Consume(NativeTokenType.CloseParen, "Expected ')' after if condition.");

        var thenBranch = ParseStatement();
        StatementNode? elseBranch = null;
        if (Match(NativeTokenType.Else))
        {
            elseBranch = ParseStatement();
        }
        return new IfStatement(cond, thenBranch, elseBranch, line, col);
    }

    private WhileStatement ParseWhileStatement()
    {
        var line = Previous().Line;
        var col = Previous().Column;
        Consume(NativeTokenType.OpenParen, "Expected '(' after 'while'.");
        var cond = ParseExpression();
        Consume(NativeTokenType.CloseParen, "Expected ')' after while condition.");
        var body = ParseStatement();
        return new WhileStatement(cond, body, line, col);
    }

    private DoWhileStatement ParseDoWhileStatement()
    {
        var line = Previous().Line;
        var col = Previous().Column;
        var body = ParseStatement();
        Consume(NativeTokenType.While, "Expected 'while' after 'do' body.");
        Consume(NativeTokenType.OpenParen, "Expected '(' after 'while'.");
        var cond = ParseExpression();
        Consume(NativeTokenType.CloseParen, "Expected ')' after condition.");
        Match(NativeTokenType.Semicolon);
        return new DoWhileStatement(body, cond, line, col);
    }

    private SwitchStatement ParseSwitchStatement()
    {
        var line = Previous().Line;
        var col = Previous().Column;
        Consume(NativeTokenType.OpenParen, "Expected '(' after 'switch'.");
        var discriminant = ParseExpression();
        Consume(NativeTokenType.CloseParen, "Expected ')' after switch value.");
        Consume(NativeTokenType.OpenBrace, "Expected '{' before switch cases.");

        var cases = new List<SwitchCase>();
        while (!Check(NativeTokenType.CloseBrace) && !IsAtEnd())
        {
            if (Match(NativeTokenType.Case))
            {
                var caseLine = Previous().Line;
                var caseCol = Previous().Column;
                var test = ParseExpression();
                Consume(NativeTokenType.Colon, "Expected ':' after case value.");
                var stmts = new List<StatementNode>();
                while (!Check(NativeTokenType.Case) && !Check(NativeTokenType.Default) && !Check(NativeTokenType.CloseBrace) && !IsAtEnd())
                {
                    if (Match(NativeTokenType.Semicolon)) continue;
                    stmts.Add(ParseStatement());
                }
                cases.Add(new SwitchCase(test, stmts, caseLine, caseCol));
            }
            else if (Match(NativeTokenType.Default))
            {
                var defLine = Previous().Line;
                var defCol = Previous().Column;
                Consume(NativeTokenType.Colon, "Expected ':' after 'default'.");
                var stmts = new List<StatementNode>();
                while (!Check(NativeTokenType.Case) && !Check(NativeTokenType.Default) && !Check(NativeTokenType.CloseBrace) && !IsAtEnd())
                {
                    if (Match(NativeTokenType.Semicolon)) continue;
                    stmts.Add(ParseStatement());
                }
                cases.Add(new SwitchCase(null, stmts, defLine, defCol));
            }
            else
            {
                break;
            }
        }
        Consume(NativeTokenType.CloseBrace, "Expected '}' after switch.");
        return new SwitchStatement(discriminant, cases, line, col);
    }

    private ForStatement ParseForStatement()
    {
        var line = Previous().Line;
        var col = Previous().Column;
        Consume(NativeTokenType.OpenParen, "Expected '(' after 'for'.");

        AstNode? init = null;
        if (Match(NativeTokenType.Semicolon))
        {
            init = null;
        }
        else if (Match(NativeTokenType.Var, NativeTokenType.Let, NativeTokenType.Const))
        {
            init = ParseVarStatement(Previous());
        }
        else
        {
            init = ParseExpression();
            Consume(NativeTokenType.Semicolon, "Expected ';' after for init.");
        }

        ExpressionNode? test = null;
        if (!Check(NativeTokenType.Semicolon))
        {
            test = ParseExpression();
        }
        Consume(NativeTokenType.Semicolon, "Expected ';' after for test condition.");

        ExpressionNode? update = null;
        if (!Check(NativeTokenType.CloseParen))
        {
            update = ParseExpression();
        }
        Consume(NativeTokenType.CloseParen, "Expected ')' after for clauses.");

        var body = ParseStatement();
        return new ForStatement(init, test, update, body, line, col);
    }

    private ReturnStatement ParseReturnStatement()
    {
        var line = Previous().Line;
        var col = Previous().Column;
        ExpressionNode? value = null;
        if (!Check(NativeTokenType.Semicolon) && !Check(NativeTokenType.CloseBrace) && !IsAtEnd())
        {
            value = ParseExpression();
        }
        Match(NativeTokenType.Semicolon);
        return new ReturnStatement(value, line, col);
    }

    private ThrowStatement ParseThrowStatement()
    {
        var line = Previous().Line;
        var col = Previous().Column;
        var expr = ParseExpression();
        Match(NativeTokenType.Semicolon);
        return new ThrowStatement(expr, line, col);
    }

    private TryCatchStatement ParseTryStatement()
    {
        var line = Previous().Line;
        var col = Previous().Column;
        Consume(NativeTokenType.OpenBrace, "Expected '{' for try block.");
        var tryBlock = ParseBlock();

        string? catchParam = null;
        BlockStatement? catchBlock = null;
        if (Match(NativeTokenType.Catch))
        {
            if (Match(NativeTokenType.OpenParen))
            {
                var param = Consume(NativeTokenType.Identifier, "Expected catch parameter name.");
                catchParam = param.Text;
                Consume(NativeTokenType.CloseParen, "Expected ')' after catch parameter.");
            }
            Consume(NativeTokenType.OpenBrace, "Expected '{' for catch block.");
            catchBlock = ParseBlock();
        }

        BlockStatement? finallyBlock = null;
        if (Match(NativeTokenType.Finally))
        {
            Consume(NativeTokenType.OpenBrace, "Expected '{' for finally block.");
            finallyBlock = ParseBlock();
        }

        return new TryCatchStatement(tryBlock, catchParam, catchBlock, finallyBlock, line, col);
    }

    private FunctionDeclaration ParseFunctionDeclaration()
    {
        var line = Previous().Line;
        var col = Previous().Column;
        var nameToken = Consume(NativeTokenType.Identifier, "Expected function name.");
        Consume(NativeTokenType.OpenParen, "Expected '(' after function name.");

        var parameters = new List<string>();
        if (!Check(NativeTokenType.CloseParen))
        {
            do
            {
                var p = Consume(NativeTokenType.Identifier, "Expected parameter name.");
                parameters.Add(p.Text);
            } while (Match(NativeTokenType.Comma));
        }
        Consume(NativeTokenType.CloseParen, "Expected ')' after parameters.");
        Consume(NativeTokenType.OpenBrace, "Expected '{' before function body.");
        var body = ParseBlock();
        return new FunctionDeclaration(nameToken.Text, parameters, body, line, col);
    }

    private ExpressionStatement ParseExpressionStatement()
    {
        var expr = ParseExpression();
        Match(NativeTokenType.Semicolon);
        return new ExpressionStatement(expr, expr.Line, expr.Column);
    }

    public ExpressionNode ParseExpression()
    {
        return ParseAssignment();
    }

    private ExpressionNode ParseAssignment()
    {
        var expr = ParseTernary();

        if (Match(NativeTokenType.Equal, NativeTokenType.PlusEqual, NativeTokenType.MinusEqual, NativeTokenType.StarEqual, NativeTokenType.SlashEqual))
        {
            var op = Previous().Text;
            var value = ParseAssignment();
            return new AssignmentExpression(op, expr, value, expr.Line, expr.Column);
        }

        return expr;
    }

    private ExpressionNode ParseTernary()
    {
        var expr = ParseLogicalOr();

        if (Match(NativeTokenType.Question))
        {
            var trueExpr = ParseExpression();
            Consume(NativeTokenType.Colon, "Expected ':' in ternary expression.");
            var falseExpr = ParseAssignment();
            return new TernaryExpression(expr, trueExpr, falseExpr, expr.Line, expr.Column);
        }

        return expr;
    }

    private ExpressionNode ParseLogicalOr()
    {
        var expr = ParseLogicalAnd();
        while (Match(NativeTokenType.PipePipe))
        {
            var op = Previous().Text;
            var right = ParseLogicalAnd();
            expr = new BinaryExpression(op, expr, right, expr.Line, expr.Column);
        }
        return expr;
    }

    private ExpressionNode ParseLogicalAnd()
    {
        var expr = ParseEquality();
        while (Match(NativeTokenType.AmpersandAmpersand))
        {
            var op = Previous().Text;
            var right = ParseEquality();
            expr = new BinaryExpression(op, expr, right, expr.Line, expr.Column);
        }
        return expr;
    }

    private ExpressionNode ParseEquality()
    {
        var expr = ParseComparison();
        while (Match(NativeTokenType.EqualEqual, NativeTokenType.EqualEqualEqual, NativeTokenType.ExclamationEqual, NativeTokenType.ExclamationDoubleEqual))
        {
            var op = Previous().Text;
            var right = ParseComparison();
            expr = new BinaryExpression(op, expr, right, expr.Line, expr.Column);
        }
        return expr;
    }

    private ExpressionNode ParseComparison()
    {
        var expr = ParseAdditive();
        while (Match(NativeTokenType.Less, NativeTokenType.LessEqual, NativeTokenType.Greater, NativeTokenType.GreaterEqual))
        {
            var op = Previous().Text;
            var right = ParseAdditive();
            expr = new BinaryExpression(op, expr, right, expr.Line, expr.Column);
        }
        return expr;
    }

    private ExpressionNode ParseAdditive()
    {
        var expr = ParseMultiplicative();
        while (Match(NativeTokenType.Plus, NativeTokenType.Minus))
        {
            var op = Previous().Text;
            var right = ParseMultiplicative();
            expr = new BinaryExpression(op, expr, right, expr.Line, expr.Column);
        }
        return expr;
    }

    private ExpressionNode ParseMultiplicative()
    {
        var expr = ParseUnary();
        while (Match(NativeTokenType.Star, NativeTokenType.Slash, NativeTokenType.Percent))
        {
            var op = Previous().Text;
            var right = ParseUnary();
            expr = new BinaryExpression(op, expr, right, expr.Line, expr.Column);
        }
        return expr;
    }

    private ExpressionNode ParseUnary()
    {
        if (Match(NativeTokenType.Exclamation, NativeTokenType.Minus, NativeTokenType.Plus, NativeTokenType.TypeOf, NativeTokenType.Delete, NativeTokenType.PlusPlus, NativeTokenType.MinusMinus))
        {
            var op = Previous().Text;
            var operand = ParseUnary();
            return new UnaryExpression(op, operand, true, Previous().Line, Previous().Column);
        }

        var expr = ParseCallAndMember();

        if (Match(NativeTokenType.PlusPlus, NativeTokenType.MinusMinus))
        {
            var op = Previous().Text;
            return new UnaryExpression(op, expr, false, expr.Line, expr.Column);
        }

        return expr;
    }

    private ExpressionNode ParseCallAndMember()
    {
        ExpressionNode expr;
        if (Match(NativeTokenType.New))
        {
            var line = Previous().Line;
            var col = Previous().Column;
            var callee = ParsePrimary();
            var args = new List<ExpressionNode>();
            if (Match(NativeTokenType.OpenParen))
            {
                if (!Check(NativeTokenType.CloseParen))
                {
                    do
                    {
                        args.Add(ParseExpression());
                    } while (Match(NativeTokenType.Comma));
                }
                Consume(NativeTokenType.CloseParen, "Expected ')' after constructor arguments.");
            }
            expr = new NewExpression(callee, args, line, col);
        }
        else
        {
            expr = ParsePrimary();
        }

        while (true)
        {
            if (Match(NativeTokenType.OpenParen))
            {
                var args = new List<ExpressionNode>();
                if (!Check(NativeTokenType.CloseParen))
                {
                    do
                    {
                        args.Add(ParseExpression());
                    } while (Match(NativeTokenType.Comma));
                }
                Consume(NativeTokenType.CloseParen, "Expected ')' after arguments.");
                expr = new CallExpression(expr, args, expr.Line, expr.Column);
            }
            else if (Match(NativeTokenType.Dot))
            {
                var name = Consume(NativeTokenType.Identifier, "Expected property name after '.'.");
                expr = new MemberExpression(expr, new LiteralExpression(name.Text, NativeTokenType.String, name.Line, name.Column), false, expr.Line, expr.Column);
            }
            else if (Match(NativeTokenType.OpenBracket))
            {
                var propExpr = ParseExpression();
                Consume(NativeTokenType.CloseBracket, "Expected ']' after member access.");
                expr = new MemberExpression(expr, propExpr, true, expr.Line, expr.Column);
            }
            else
            {
                break;
            }
        }

        return expr;
    }

    private ExpressionNode ParsePrimary()
    {
        var token = Peek();
        if (Match(NativeTokenType.Number))
            return new LiteralExpression(Previous().NumberValue, NativeTokenType.Number, token.Line, token.Column);
        if (Match(NativeTokenType.String))
            return new LiteralExpression(Previous().Text, NativeTokenType.String, token.Line, token.Column);
        if (Match(NativeTokenType.True))
            return new LiteralExpression(true, NativeTokenType.True, token.Line, token.Column);
        if (Match(NativeTokenType.False))
            return new LiteralExpression(false, NativeTokenType.False, token.Line, token.Column);
        if (Match(NativeTokenType.Null))
            return new LiteralExpression(null, NativeTokenType.Null, token.Line, token.Column);
        if (Match(NativeTokenType.Undefined))
            return new LiteralExpression(null, NativeTokenType.Undefined, token.Line, token.Column);
        if (Match(NativeTokenType.This))
            return new ThisExpression(token.Line, token.Column);

        if (Match(NativeTokenType.Identifier))
        {
            var idToken = Previous();
            if (Match(NativeTokenType.Arrow))
            {
                // Single-parameter arrow function: x => ...
                var body = Check(NativeTokenType.OpenBrace) ? (AstNode)ParseBlock() : ParseAssignment();
                return new ArrowFunctionExpression([idToken.Text], body, idToken.Line, idToken.Column);
            }
            return new IdentifierExpression(idToken.Text, idToken.Line, idToken.Column);
        }

        if (Match(NativeTokenType.Function))
        {
            var line = Previous().Line;
            var col = Previous().Column;
            string? name = null;
            if (Match(NativeTokenType.Identifier))
                name = Previous().Text;
            Consume(NativeTokenType.OpenParen, "Expected '(' after function.");
            var parameters = new List<string>();
            if (!Check(NativeTokenType.CloseParen))
            {
                do
                {
                    parameters.Add(Consume(NativeTokenType.Identifier, "Expected parameter name.").Text);
                } while (Match(NativeTokenType.Comma));
            }
            Consume(NativeTokenType.CloseParen, "Expected ')' after parameters.");
            Consume(NativeTokenType.OpenBrace, "Expected '{' before function body.");
            var body = ParseBlock();
            return new FunctionExpression(name, parameters, body, line, col);
        }

        if (Match(NativeTokenType.OpenParen))
        {
            var line = Previous().Line;
            var col = Previous().Column;
            // Arrow function with parameter list: () => ... or (a, b) => ...
            if (Match(NativeTokenType.CloseParen))
            {
                if (Match(NativeTokenType.Arrow))
                {
                    var body = Check(NativeTokenType.OpenBrace) ? (AstNode)ParseBlock() : ParseAssignment();
                    return new ArrowFunctionExpression([], body, line, col);
                }
                throw new FormatException($"Empty parentheses at line {line}, col {col}");
            }

            var expr = ParseExpression();

            if (Match(NativeTokenType.Comma))
            {
                // Multi-parameter arrow function (a, b) => ...
                var parameters = new List<string>();
                if (expr is IdentifierExpression id)
                    parameters.Add(id.Name);
                do
                {
                    parameters.Add(Consume(NativeTokenType.Identifier, "Expected parameter name.").Text);
                } while (Match(NativeTokenType.Comma));
                Consume(NativeTokenType.CloseParen, "Expected ')' after parameters.");
                Consume(NativeTokenType.Arrow, "Expected '=>' after arrow parameters.");
                var body = Check(NativeTokenType.OpenBrace) ? (AstNode)ParseBlock() : ParseAssignment();
                return new ArrowFunctionExpression(parameters, body, line, col);
            }

            Consume(NativeTokenType.CloseParen, "Expected ')' after expression.");
            if (Match(NativeTokenType.Arrow))
            {
                var parameters = new List<string>();
                if (expr is IdentifierExpression id)
                    parameters.Add(id.Name);
                var body = Check(NativeTokenType.OpenBrace) ? (AstNode)ParseBlock() : ParseAssignment();
                return new ArrowFunctionExpression(parameters, body, line, col);
            }
            return expr;
        }

        if (Match(NativeTokenType.OpenBracket))
        {
            var line = Previous().Line;
            var col = Previous().Column;
            var elements = new List<ExpressionNode?>();
            if (!Check(NativeTokenType.CloseBracket))
            {
                do
                {
                    if (Check(NativeTokenType.CloseBracket))
                        break;
                    if (Match(NativeTokenType.Comma))
                    {
                        elements.Add(null);
                        continue;
                    }
                    elements.Add(ParseExpression());
                } while (Match(NativeTokenType.Comma));
            }
            Consume(NativeTokenType.CloseBracket, "Expected ']' after array elements.");
            return new ArrayLiteralExpression(elements, line, col);
        }

        if (Match(NativeTokenType.OpenBrace))
        {
            var line = Previous().Line;
            var col = Previous().Column;
            var properties = new List<ObjectProperty>();
            if (!Check(NativeTokenType.CloseBrace))
            {
                do
                {
                    if (Check(NativeTokenType.CloseBrace))
                        break;
                    string key;
                    if (Match(NativeTokenType.Identifier, NativeTokenType.String))
                        key = Previous().Text;
                    else if (Match(NativeTokenType.Number))
                        key = Previous().NumberValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    else
                        throw new FormatException($"Expected property name at line {Peek().Line}, col {Peek().Column}");

                    Consume(NativeTokenType.Colon, "Expected ':' after property name.");
                    var val = ParseExpression();
                    properties.Add(new ObjectProperty(key, val, line, col));
                } while (Match(NativeTokenType.Comma));
            }
            Consume(NativeTokenType.CloseBrace, "Expected '}' after object properties.");
            return new ObjectLiteralExpression(properties, line, col);
        }

        throw new FormatException($"Unexpected token '{token.Type}' at line {token.Line}, col {token.Column}");
    }

    private bool Match(params NativeTokenType[] types)
    {
        foreach (var type in types)
        {
            if (Check(type))
            {
                Advance();
                return true;
            }
        }
        return false;
    }

    private bool Check(NativeTokenType type)
    {
        if (IsAtEnd()) return type == NativeTokenType.Eof;
        return Peek().Type == type;
    }

    private NativeToken Advance()
    {
        if (!IsAtEnd()) _current++;
        return Previous();
    }

    private bool IsAtEnd() => _current >= _tokens.Count || _tokens[_current].Type == NativeTokenType.Eof;

    private NativeToken Peek() => _tokens[_current];

    private NativeToken Previous() => _tokens[_current - 1];

    private NativeToken Consume(NativeTokenType type, string message)
    {
        if (Check(type)) return Advance();
        throw new FormatException($"{message} (got '{Peek().Type}' at line {Peek().Line}, col {Peek().Column})");
    }
}
