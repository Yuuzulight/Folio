namespace Folio.Scripting.Native;

/// <summary>Bounded expression parser for the first Folio-owned JavaScript front-end slice.</summary>
internal sealed class JavaScriptParser
{
    private readonly JavaScriptLexer _lexer;
    private readonly int _maxNodes;
    private readonly Queue<JavaScriptToken> _lookahead = new();
    private JavaScriptToken _current;
    private int _nodes;

    internal JavaScriptParser(string source, int maxTokens = 1_000_000, int maxNodes = 1_000_000)
    {
        _lexer = new JavaScriptLexer(source, maxTokens);
        if (maxNodes <= 0) throw new ArgumentOutOfRangeException(nameof(maxNodes));
        _maxNodes = maxNodes;
        _current = _lexer.Next();
    }

    internal JavaScriptExpression ParseExpression()
    {
        var result = ParseBinary(0);
        if (_current.Kind != JavaScriptTokenKind.End)
            throw Error("Unexpected token");
        return result;
    }

    internal IReadOnlyList<JavaScriptStatement> ParseProgram()
    {
        var statements = new List<JavaScriptStatement>();
        while (_current.Kind != JavaScriptTokenKind.End)
            statements.Add(ParseStatement());
        return statements;
    }

    private JavaScriptStatement ParseStatement()
    {
        if (Match(";")) return new JavaScriptExpressionStatement(new JavaScriptLiteralExpression("undefined", JavaScriptTokenKind.Identifier));
        if (Match("{"))
        {
            var statements = new List<JavaScriptStatement>();
            var closed = false;
            while (_current.Kind != JavaScriptTokenKind.End)
            {
                if (Match("}")) { closed = true; break; }
                statements.Add(ParseStatement());
            }
            if (!closed) throw Error("The block is unterminated");
            return new JavaScriptBlockStatement(statements);
        }
        if (_current.Kind == JavaScriptTokenKind.Identifier && _current.Text == "function")
        {
            Take();
            if (_current.Kind != JavaScriptTokenKind.Identifier)
                return new JavaScriptExpressionStatement(ParseFunctionTail());
            var name = _current.Text; Take();
            return new JavaScriptFunctionDeclarationStatement(name, ParseFunctionTail());
        }
        if (_current.Kind == JavaScriptTokenKind.Identifier && _current.Text == "class")
        {
            Take();
            var expression = ParseClassTail();
            return expression.Name is { } className
                ? new JavaScriptClassDeclarationStatement(className, expression)
                : new JavaScriptExpressionStatement(expression);
        }
        if (_current.Kind == JavaScriptTokenKind.Identifier && _current.Text is "const" or "let" or "var")
        {
            Take();
            var declarations = new List<JavaScriptStatement>();
            do
            {
                string name;
                if (Match("["))
                {
                    var names = new List<string>();
                    if (!Match("]"))
                    {
                        do
                        {
                            if (_current.Kind != JavaScriptTokenKind.Identifier) throw Error("Expected a destructured binding name");
                            names.Add(_current.Text); Take();
                        } while (Match(","));
                        Expect("]");
                    }
                    name = "[" + string.Join(",", names) + "]";
                }
                else
                {
                    if (_current.Kind != JavaScriptTokenKind.Identifier) throw Error("Expected a binding name");
                    name = _current.Text; Take();
                }
                JavaScriptExpression? initializer = Match("=") ? ParseBinary(0) : null;
                declarations.Add(new JavaScriptVariableStatement(name, initializer));
            } while (Match(","));
            Match(";");
            return declarations.Count == 1 ? declarations[0] : new JavaScriptBlockStatement(declarations);
        }
        if (_current.Kind == JavaScriptTokenKind.Identifier && _current.Text == "return")
        {
            Take();
            JavaScriptExpression? expression = _current.Kind == JavaScriptTokenKind.Punctuation && _current.Text == ";" ? null : ParseBinary(0);
            Match(";");
            return new JavaScriptReturnStatement(expression);
        }
        if (_current.Kind == JavaScriptTokenKind.Identifier && _current.Text == "if")
        {
            Take(); Expect("("); var condition = ParseBinary(0); Expect(")");
            var then = ParseStatement();
            JavaScriptStatement? @else = null;
            if (_current.Kind == JavaScriptTokenKind.Identifier && _current.Text == "else") { Take(); @else = ParseStatement(); }
            return new JavaScriptIfStatement(condition, then, @else);
        }
        if (_current.Kind == JavaScriptTokenKind.Identifier && _current.Text == "with")
        {
            Take(); Expect("("); var scope = ParseBinary(0); Expect(")");
            return new JavaScriptWithStatement(scope, ParseStatement());
        }
        if (_current.Kind == JavaScriptTokenKind.Identifier && _current.Text == "while")
        {
            Take(); Expect("("); var condition = ParseBinary(0); Expect(")");
            return new JavaScriptWhileStatement(condition, ParseStatement());
        }
        if (_current.Kind == JavaScriptTokenKind.Identifier && _current.Text == "do")
        {
            Take();
            var body = ParseStatement();
            if (_current.Kind != JavaScriptTokenKind.Identifier || _current.Text != "while") throw Error("Expected while after do body");
            Take(); Expect("("); var condition = ParseBinary(0); Expect(")"); Match(";");
            return new JavaScriptDoWhileStatement(body, condition);
        }
        if (_current.Kind == JavaScriptTokenKind.Identifier && _current.Text is "break" or "continue")
        {
            var keyword = _current.Text; Take(); Match(";");
            return keyword == "break" ? new JavaScriptBreakStatement() : new JavaScriptContinueStatement();
        }
        if (_current.Kind == JavaScriptTokenKind.Identifier && _current.Text == "for")
        {
            Take(); Expect("(");
            JavaScriptStatement? initializer = null;
            if (!Match(";"))
            {
                if (_current.Kind == JavaScriptTokenKind.Identifier && _current.Text is "const" or "let" or "var")
                {
                    Take();
                    string name;
                    if (Match("["))
                    {
                        var names = new List<string>();
                        if (!Match("]"))
                        {
                            do
                            {
                                if (_current.Kind != JavaScriptTokenKind.Identifier) throw Error("Expected a destructured binding name");
                                names.Add(_current.Text); Take();
                            } while (Match(","));
                            Expect("]");
                        }
                        name = "[" + string.Join(",", names) + "]";
                    }
                    else
                    {
                        if (_current.Kind != JavaScriptTokenKind.Identifier) throw Error("Expected a binding name");
                        name = _current.Text; Take();
                    }
                    if (_current.Kind == JavaScriptTokenKind.Identifier && _current.Text is "of" or "in")
                    {
                        var keys = _current.Text == "in"; Take();
                        var iterable = ParseBinary(0); Expect(")");
                        return new JavaScriptForEachStatement(name, iterable, ParseStatement(), keys);
                    }
                    JavaScriptExpression? value = Match("=") ? ParseBinary(0) : null;
                    initializer = new JavaScriptVariableStatement(name, value);
                }
                else initializer = new JavaScriptExpressionStatement(ParseBinary(0));
                Expect(";");
            }
            JavaScriptExpression? condition = null;
            if (!Match(";")) { condition = ParseBinary(0); Expect(";"); }
            JavaScriptExpression? update = null;
            if (!Match(")")) { update = ParseBinary(0); Expect(")"); }
            return new JavaScriptForStatement(initializer, condition, update, ParseStatement());
        }
        if (_current.Kind == JavaScriptTokenKind.Identifier && _current.Text == "throw")
        {
            Take();
            var expression = ParseBinary(0); Match(";");
            return new JavaScriptThrowStatement(expression);
        }
        if (_current.Kind == JavaScriptTokenKind.Identifier && _current.Text == "try")
        {
            Take();
            var body = ParseBlockStatement();
            string? catchName = null;
            JavaScriptBlockStatement? catchBody = null;
            if (_current.Kind == JavaScriptTokenKind.Identifier && _current.Text == "catch")
            {
                Take(); Expect("(");
                if (_current.Kind != JavaScriptTokenKind.Identifier) throw Error("Expected a catch binding name");
                catchName = _current.Text; Take(); Expect(")");
                catchBody = ParseBlockStatement();
            }
            JavaScriptBlockStatement? finallyBody = null;
            if (_current.Kind == JavaScriptTokenKind.Identifier && _current.Text == "finally") { Take(); finallyBody = ParseBlockStatement(); }
            if (catchBody is null && finallyBody is null) throw Error("A try statement requires catch or finally");
            return new JavaScriptTryStatement(body, catchName, catchBody, finallyBody);
        }
        if (_current.Kind == JavaScriptTokenKind.Identifier && _current.Text == "switch")
        {
            Take(); Expect("("); var expression = ParseBinary(0); Expect(")"); Expect("{");
            var cases = new List<JavaScriptSwitchCase>();
            while (_current.Kind != JavaScriptTokenKind.End && !Match("}"))
            {
                JavaScriptExpression? test;
                if (_current.Kind == JavaScriptTokenKind.Identifier && _current.Text == "case") { Take(); test = ParseBinary(0); Expect(":"); }
                else if (_current.Kind == JavaScriptTokenKind.Identifier && _current.Text == "default") { Take(); Expect(":"); test = null; }
                else throw Error("Expected case or default in switch");
                var statements = new List<JavaScriptStatement>();
                while (_current.Kind != JavaScriptTokenKind.End && !(_current.Kind == JavaScriptTokenKind.Identifier && (_current.Text == "case" || _current.Text == "default")) && !(_current.Kind == JavaScriptTokenKind.Punctuation && _current.Text == "}"))
                    statements.Add(ParseStatement());
                cases.Add(new JavaScriptSwitchCase(test, statements));
            }
            return new JavaScriptSwitchStatement(expression, cases);
        }
        var result = ParseBinary(0); Match(";"); return new JavaScriptExpressionStatement(result);
    }

    private JavaScriptExpression ParseBinary(int minimumPrecedence)
    {
        var left = ParsePostfix();
        while (_current.Kind == JavaScriptTokenKind.Punctuation || _current.Kind == JavaScriptTokenKind.Identifier && _current.Text is "in" or "instanceof")
        {
            var op = _current.Text;
            var precedence = op switch { "**" => 80, "*" or "/" or "%" => 70, "+" or "-" => 60, "<<" or ">>" or ">>>" => 55, "<" or ">" or "<=" or ">=" => 50, "==" or "!=" or "===" or "!==" => 40, "&" => 35, "^" => 34, "|" => 33, "&&" => 30, "||" or "??" => 20, "=" or "+=" or "-=" or "*=" or "/=" or "%=" or "&=" or "|=" or "^=" or "<<=" or ">>=" or ">>>=" or "&&=" or "||=" or "??=" => 10, _ => -1 };
            if (op is "in" or "instanceof") precedence = 45;
            if (precedence < minimumPrecedence) break;
            Take();
            var right = ParseBinary(op is "=" or "+=" or "-=" or "*=" or "/=" or "%=" or "&=" or "|=" or "^=" or "&&=" or "||=" or "??=" ? 0 : precedence + 1);
            left = Node(new JavaScriptBinaryExpression(op, left, right));
        }
        if (minimumPrecedence <= 5 && Match("?"))
        {
            var whenTrue = ParseBinary(0);
            Expect(":");
            var whenFalse = ParseBinary(5);
            left = Node(new JavaScriptConditionalExpression(left, whenTrue, whenFalse));
        }
        if (minimumPrecedence <= 5 && Match("=>") && left is JavaScriptIdentifierExpression parameter)
        {
            var body = _current.Kind == JavaScriptTokenKind.Punctuation && _current.Text == "{"
                ? ParseBlockStatement().Statements
                : [new JavaScriptReturnStatement(ParseBinary(0))];
            left = Node(new JavaScriptFunctionExpression([parameter.Name], body));
        }
        return left;
    }

    private JavaScriptExpression ParsePostfix()
    {
        var expression = ParseUnary();
        while (true)
        {
            if (Match("."))
            {
                if (_current.Kind != JavaScriptTokenKind.Identifier) throw Error("Expected a member name");
                var name = _current.Text; Take();
                expression = Node(new JavaScriptMemberExpression(expression, name));
                continue;
            }
            if (Match("("))
            {
                var arguments = new List<JavaScriptExpression>();
                if (!Match(")"))
                {
                    do { arguments.Add(ParseBinary(0)); } while (Match(","));
                    Expect(")");
                }
                expression = Node(new JavaScriptCallExpression(expression, arguments));
                continue;
            }
            if (Match("["))
            {
                var index = ParseBinary(0);
                Expect("]");
                expression = Node(new JavaScriptIndexExpression(expression, index));
                continue;
            }
            if (_current.Kind == JavaScriptTokenKind.Punctuation && _current.Text is "++" or "--")
            {
                var op = _current.Text; Take();
                expression = Node(new JavaScriptUpdateExpression(op, expression, false));
                continue;
            }
            break;
        }
        return expression;
    }

    private JavaScriptExpression ParseUnary()
    {
        if (_current.Kind == JavaScriptTokenKind.Punctuation && _current.Text is "!" or "-" or "+" or "~" or "++" or "--")
        {
            var op = _current.Text; Take();
            var operand = ParsePostfix();
            if (op is "++" or "--") return Node(new JavaScriptUpdateExpression(op, operand, true));
            return Node(new JavaScriptUnaryExpression(op, operand));
        }
        if (_current.Kind == JavaScriptTokenKind.Template)
        {
            var token = _current; Take();
            return Node(new JavaScriptTemplateExpression(token.Text));
        }
        if (_current.Kind is JavaScriptTokenKind.Number or JavaScriptTokenKind.String)
        {
            var token = _current; Take();
            return Node(new JavaScriptLiteralExpression(token.Text, token.Kind));
        }
        if (_current.Kind == JavaScriptTokenKind.Regex)
        {
            var regex = _current.Text; Take();
            return Node(new JavaScriptRegexExpression(regex));
        }
        if (_current.Kind == JavaScriptTokenKind.Identifier)
        {
            if (Peek(0).Text == "=>")
            {
                var parameter = _current.Text;
                Take();
                Expect("=>");
                var body = _current.Kind == JavaScriptTokenKind.Punctuation && _current.Text == "{"
                    ? ParseBlockStatement().Statements
                    : [new JavaScriptReturnStatement(ParseBinary(0))];
                return Node(new JavaScriptFunctionExpression([parameter], body));
            }
            var name = _current.Text; Take();
            if (name == "new" && Match("."))
            {
                if (_current.Kind != JavaScriptTokenKind.Identifier || _current.Text != "target") throw Error("Expected new.target");
                Take();
                // NativeFunction calls are constructor-shaped in this bounded
                // runtime; expose a truthy constructor meta-property so the
                // standard bootstrap's illegal-constructor guards behave.
                return Node(new JavaScriptLiteralExpression("true", JavaScriptTokenKind.Identifier));
            }
            if (name is "typeof" or "void" or "new") return Node(new JavaScriptUnaryExpression(name, ParsePostfix()));
            if (name == "delete") return Node(new JavaScriptUnaryExpression(name, ParseDeleteTarget()));
            if (name == "function")
                return Node(ParseFunctionTail());
            if (name == "class")
                return Node(ParseClassTail());
            if (name is "true" or "false" or "null" or "undefined")
                return Node(new JavaScriptLiteralExpression(name, JavaScriptTokenKind.Identifier));
            return Node(new JavaScriptIdentifierExpression(name));
        }
        if (IsArrowParameterList(out var arrowParameters))
        {
            Expect("(");
            for (var i = 0; i < arrowParameters.Count; i++)
            {
                var parameter = arrowParameters[i];
                if (_current.Kind != JavaScriptTokenKind.Identifier || _current.Text != parameter) throw Error("Expected an arrow parameter");
                Take();
                if (i + 1 < arrowParameters.Count) Expect(",");
            }
            Expect(")"); Expect("=>");
            var body = _current.Kind == JavaScriptTokenKind.Punctuation && _current.Text == "{"
                ? ParseBlockStatement().Statements
                : [new JavaScriptReturnStatement(ParseBinary(0))];
            return Node(new JavaScriptFunctionExpression(arrowParameters, body));
        }
        if (Match("("))
        {
            var result = ParseBinary(0); Expect(")"); return result;
        }
        if (Match("["))
        {
            var elements = new List<JavaScriptExpression>();
            if (!Match("]"))
            {
                do
                {
                    elements.Add(Match("...") ? new JavaScriptSpreadExpression(ParseBinary(0)) : ParseBinary(0));
                } while (Match(","));
                Expect("]");
            }
            return Node(new JavaScriptArrayExpression(elements));
        }
        if (Match("{"))
        {
            var properties = new List<(string Name, JavaScriptExpression Value)>();
            if (!Match("}"))
            {
                do
                {
                    if (_current.Kind is not (JavaScriptTokenKind.Identifier or JavaScriptTokenKind.String)) throw Error("Expected an object property name");
                    var name = _current.Text; Take();
                    if (name == "get" && _current.Kind == JavaScriptTokenKind.Identifier)
                    {
                        var getterName = _current.Text; Take();
                        properties.Add(("__folio_getter__" + getterName, ParseFunctionTail()));
                    }
                    else if (_current.Kind == JavaScriptTokenKind.Punctuation && _current.Text == "(")
                    {
                        properties.Add(("__folio_method__" + name, ParseFunctionTail()));
                    }
                    else
                    {
                        if (Match(":")) properties.Add((name, ParseBinary(0)));
                        else properties.Add((name, new JavaScriptIdentifierExpression(name)));
                    }
                } while (Match(",") && _current.Text != "}");
                Expect("}");
            }
            return Node(new JavaScriptObjectExpression(properties));
        }
        throw Error($"Expected an expression (got {_current.Kind} '{_current.Text}')");
    }

    private JavaScriptBlockStatement ParseBlockStatement()
    {
        if (!Match("{")) throw Error("Expected a block");
        var statements = new List<JavaScriptStatement>();
        var closed = false;
        while (_current.Kind != JavaScriptTokenKind.End)
        {
            if (Match("}")) { closed = true; break; }
            statements.Add(ParseStatement());
        }
        if (!closed) throw Error("The block is unterminated");
        return new JavaScriptBlockStatement(statements);
    }

    private JavaScriptFunctionExpression ParseFunctionTail()
    {
        // Function expressions may carry an internal name (for recursion and
        // stack traces), e.g. `value: function createEvent(...) { ... }`.
        // The bounded evaluator does not need a separate named scope, but it
        // must consume the name before the parameter list.
        if (_current.Kind == JavaScriptTokenKind.Identifier)
            Take();
        Expect("(");
        var parameters = new List<string>();
        if (!Match(")"))
        {
            do
            {
                if (_current.Kind != JavaScriptTokenKind.Identifier) throw Error("Expected a function parameter");
                parameters.Add(_current.Text); Take();
            } while (Match(","));
            Expect(")");
        }
        var body = ParseBlockStatement();
        return new JavaScriptFunctionExpression(parameters, body.Statements);
    }

    private JavaScriptClassExpression ParseClassTail()
    {
        string? name = null;
        if (_current.Kind == JavaScriptTokenKind.Identifier) { name = _current.Text; Take(); }
        string? parent = null;
        if (_current.Kind == JavaScriptTokenKind.Identifier && _current.Text == "extends")
        {
            Take();
            if (_current.Kind != JavaScriptTokenKind.Identifier) throw Error("Expected a class parent");
            parent = _current.Text; Take();
        }
        Expect("{");
        var methods = new List<JavaScriptClassMethod>();
        while (_current.Kind != JavaScriptTokenKind.End && !Match("}"))
        {
            var getter = false;
            var setter = false;
            var isStatic = false;
            if (_current.Kind == JavaScriptTokenKind.Identifier && _current.Text == "static")
            {
                isStatic = true;
                Take();
            }
            if (_current.Kind == JavaScriptTokenKind.Identifier && _current.Text is "get" or "set"
                && (Peek(0).Kind == JavaScriptTokenKind.Identifier || Peek(0).Text == "["))
            {
                getter = _current.Text == "get";
                setter = _current.Text == "set";
                Take();
            }
            string methodName;
            if (Match("["))
            {
                var computed = ParseBinary(0);
                Expect("]");
                methodName = computed.ToString() ?? "computed";
            }
            else
            {
                if (_current.Kind != JavaScriptTokenKind.Identifier) throw Error("Expected a class method name");
                methodName = _current.Text; Take();
            }
            methods.Add(new JavaScriptClassMethod(methodName, getter, setter, isStatic, ParseFunctionTail()));
        }
        return Node(new JavaScriptClassExpression(name, parent, methods));
    }

    private JavaScriptExpression ParseDeleteTarget()
    {
        var target = ParseUnary();
        while (true)
        {
            if (Match("."))
            {
                if (_current.Kind != JavaScriptTokenKind.Identifier) throw Error("Expected a member name");
                var name = _current.Text; Take();
                target = Node(new JavaScriptMemberExpression(target, name));
                continue;
            }
            if (Match("["))
            {
                var index = ParseBinary(0); Expect("]");
                target = Node(new JavaScriptIndexExpression(target, index));
                continue;
            }
            return target;
        }
    }

    private bool Match(string text)
    {
        if (_current.Kind != JavaScriptTokenKind.Punctuation || _current.Text != text) return false;
        Take(); return true;
    }

    private void Expect(string text)
    {
        if (!Match(text)) throw Error($"Expected '{text}'");
    }

    private bool IsArrowParameterList(out List<string> parameters)
    {
        parameters = [];
        if (_current.Kind != JavaScriptTokenKind.Punctuation || _current.Text != "(") return false;
        var offset = 0;
        if (Peek(offset).Text == ")") return Peek(offset + 1).Text == "=>";
        while (true)
        {
            var token = Peek(offset++);
            if (token.Kind != JavaScriptTokenKind.Identifier) return false;
            parameters.Add(token.Text);
            var separator = Peek(offset++);
            if (separator.Text == ")") break;
            if (separator.Text != ",") return false;
        }
        return Peek(offset).Text == "=>";
    }

    private JavaScriptToken Peek(int offset)
    {
        while (_lookahead.Count <= offset) _lookahead.Enqueue(_lexer.Next());
        return _lookahead.ElementAt(offset);
    }

    private void Take() => _current = _lookahead.Count > 0 ? _lookahead.Dequeue() : _lexer.Next();
    private T Node<T>(T node) where T : JavaScriptExpression
    {
        if (++_nodes > _maxNodes) throw Error("The expression contains too many nodes");
        return node;
    }
    private InvalidDataException Error(string message) => new($"{message} (line {_current.Line}, column {_current.Column}).");
}
