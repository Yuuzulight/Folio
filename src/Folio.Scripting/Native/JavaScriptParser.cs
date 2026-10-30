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
        if (_current.Kind == JavaScriptTokenKind.Identifier && Peek(0).Text == ":")
        {
            var label = _current.Text;
            Take(); Take();
            var body = ParseStatement();
            return body switch
            {
                JavaScriptWhileStatement loop => loop with { Label = label },
                JavaScriptDoWhileStatement loop => loop with { Label = label },
                JavaScriptForStatement loop => loop with { Label = label },
                JavaScriptForEachStatement loop => loop with { Label = label },
                _ => new JavaScriptLabeledStatement(label, body),
            };
        }
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
            if (_current.Text == "*" && Peek(0).Kind == JavaScriptTokenKind.Identifier)
            {
                Take();
                var generatorName = _current.Text; Take();
                return new JavaScriptFunctionDeclarationStatement(generatorName, ParseFunctionTail(generatorAlreadyConsumed: true));
            }
            if (_current.Kind != JavaScriptTokenKind.Identifier)
                return new JavaScriptExpressionStatement(ParseFunctionTail());
            var name = _current.Text; Take();
            return new JavaScriptFunctionDeclarationStatement(name, ParseFunctionTail());
        }
        if (_current.Kind == JavaScriptTokenKind.Identifier && _current.Text == "async" && Peek(0).Text == "function")
        {
            Take(); Take();
            if (_current.Kind != JavaScriptTokenKind.Identifier)
                return new JavaScriptExpressionStatement(ParseFunctionTail(asyncFunction: true));
            var name = _current.Text; Take();
            return new JavaScriptFunctionDeclarationStatement(name, ParseFunctionTail(asyncFunction: true));
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
                var name = ParseBindingName();
                JavaScriptExpression? initializer = Match("=") ? ParseBinary(0) : null;
                declarations.Add(new JavaScriptVariableStatement(name, initializer));
            } while (Match(","));
            Match(";");
            return declarations.Count == 1 ? declarations[0] : new JavaScriptBlockStatement(declarations);
        }
        if (_current.Kind == JavaScriptTokenKind.Identifier && _current.Text == "return")
        {
            Take();
            JavaScriptExpression? expression = _current.Kind == JavaScriptTokenKind.Punctuation && _current.Text is ";" or "}" ? null : ParseSequenceExpression();
            Match(";");
            return new JavaScriptReturnStatement(expression);
        }
        if (_current.Kind == JavaScriptTokenKind.Identifier && _current.Text == "yield")
        {
            Take();
            var delegateYield = Match("*");
            var expression = _current.Kind == JavaScriptTokenKind.Punctuation && _current.Text is ";" or "}" ? null : ParseSequenceExpression();
            Match(";");
            return new JavaScriptYieldStatement(expression, delegateYield);
        }
        if (_current.Kind == JavaScriptTokenKind.Identifier && _current.Text == "if")
        {
            Take(); Expect("("); var condition = ParseSequenceExpression(); Expect(")");
            var then = ParseStatement();
            JavaScriptStatement? @else = null;
            if (_current.Kind == JavaScriptTokenKind.Identifier && _current.Text == "else") { Take(); @else = ParseStatement(); }
            return new JavaScriptIfStatement(condition, then, @else);
        }
        if (_current.Kind == JavaScriptTokenKind.Identifier && _current.Text == "with")
        {
            Take(); Expect("("); var scope = ParseSequenceExpression(); Expect(")");
            return new JavaScriptWithStatement(scope, ParseStatement());
        }
        if (_current.Kind == JavaScriptTokenKind.Identifier && _current.Text == "while")
        {
            Take(); Expect("("); var condition = ParseSequenceExpression(); Expect(")");
            return new JavaScriptWhileStatement(condition, ParseStatement());
        }
        if (_current.Kind == JavaScriptTokenKind.Identifier && _current.Text == "do")
        {
            Take();
            var body = ParseStatement();
            if (_current.Kind != JavaScriptTokenKind.Identifier || _current.Text != "while") throw Error("Expected while after do body");
            Take(); Expect("("); var condition = ParseSequenceExpression(); Expect(")"); Match(";");
            return new JavaScriptDoWhileStatement(body, condition);
        }
        if (_current.Kind == JavaScriptTokenKind.Identifier && _current.Text is "break" or "continue")
        {
            var keyword = _current.Text; Take();
            string? label = null;
            if (_current.Kind == JavaScriptTokenKind.Identifier) { label = _current.Text; Take(); }
            Match(";");
            return keyword == "break" ? new JavaScriptBreakStatement(label) : new JavaScriptContinueStatement(label);
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
                    var name = ParseBindingName();
                    if (_current.Kind == JavaScriptTokenKind.Identifier && _current.Text is "of" or "in")
                    {
                        var keys = _current.Text == "in"; Take();
                        var iterable = ParseSequenceExpression(); Expect(")");
                        return new JavaScriptForEachStatement(name, iterable, ParseStatement(), keys);
                    }
                    var declarations = new List<JavaScriptStatement>
                    {
                        new JavaScriptVariableStatement(name, Match("=") ? ParseBinary(0) : null)
                    };
                    while (Match(","))
                    {
                        var nextName = ParseBindingName();
                        declarations.Add(new JavaScriptVariableStatement(nextName, Match("=") ? ParseBinary(0) : null));
                    }
                    initializer = declarations.Count == 1 ? declarations[0] : new JavaScriptBlockStatement(declarations);
                }
                else
                {
                    if (_current.Kind == JavaScriptTokenKind.Identifier && Peek(0).Text == "in")
                    {
                        var bareName = _current.Text;
                        Take(); Take();
                        var bareIterable = ParseSequenceExpression();
                        Expect(")");
                        return new JavaScriptForEachStatement(bareName, bareIterable, ParseStatement(), true);
                    }
                    var expression = ParseSequenceExpression();
                    // `for (name in object)` is parsed by the binary parser as
                    // an `in` expression; lower that form to the foreach AST
                    // just like the declaration form above.
                    if (expression is JavaScriptBinaryExpression { Operator: "in", Left: JavaScriptIdentifierExpression name, Right: var iterable })
                    {
                        while (Match(","))
                            iterable = Node(new JavaScriptBinaryExpression(",", iterable, ParseBinary(0)));
                        Expect(")");
                        return new JavaScriptForEachStatement(name.Name, iterable, ParseStatement(), true);
                    }
                    initializer = new JavaScriptExpressionStatement(expression);
                }
                Expect(";");
            }
            JavaScriptExpression? condition = null;
            if (!Match(";")) { condition = ParseSequenceExpression(); Expect(";"); }
            JavaScriptExpression? update = null;
            if (!Match(")")) { update = ParseSequenceExpression(); Expect(")"); }
            return new JavaScriptForStatement(initializer, condition, update, ParseStatement());
        }
        if (_current.Kind == JavaScriptTokenKind.Identifier && _current.Text == "throw")
        {
            Take();
            var expression = ParseSequenceExpression(); Match(";");
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
            Take(); Expect("("); var expression = ParseSequenceExpression(); Expect(")"); Expect("{");
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
        var result = ParseSequenceExpression(); Match(";"); return new JavaScriptExpressionStatement(result);
    }

    private string ParseBindingName()
    {
        if (_current.Kind == JavaScriptTokenKind.Identifier)
        {
            var name = _current.Text; Take(); return name;
        }
        if (Match("["))
        {
            var names = new List<string>();
            if (!Match("]"))
            {
                while (true)
                {
                    if (Match(",")) { names.Add(string.Empty); continue; }
                    if (Match("..."))
                    {
                        if (_current.Kind != JavaScriptTokenKind.Identifier) throw Error("Expected a rest binding name");
                        names.Add("..." + _current.Text); Take();
                        break;
                    }
                    if (_current.Kind != JavaScriptTokenKind.Identifier) throw Error("Expected a destructured binding name");
                    names.Add(_current.Text); Take();
                    if (!Match(",")) break;
                    if (_current.Text == "]") { names.Add(string.Empty); break; }
                }
                Expect("]");
            }
            return "[" + string.Join(",", names) + "]";
        }
        if (Match("{"))
        {
            var names = new List<string>();
            if (!Match("}"))
            {
                do
                {
                    if (_current.Kind != JavaScriptTokenKind.Identifier) throw Error("Expected a destructured property name");
                    var property = _current.Text; Take();
                    var local = property;
                    if (Match(":"))
                    {
                        if (_current.Text is "[" or "{") local = ParseBindingName();
                        else
                        {
                            if (_current.Kind != JavaScriptTokenKind.Identifier) throw Error("Expected a destructured binding name");
                            local = _current.Text; Take();
                        }
                    }
                    names.Add(property + ":" + local);
                } while (Match(","));
                Expect("}");
            }
            return "{" + string.Join(",", names) + "}";
        }
        throw Error("Expected a binding name");
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
            // Assignment and exponentiation are right-associative; all other
            // binary operators consume only tighter-precedence expressions.
            var right = ParseBinary(op is "=" or "+=" or "-=" or "*=" or "/=" or "%=" or "&=" or "|=" or "^=" or "&&=" or "||=" or "??=" or "**" ? (op == "**" ? precedence : 0) : precedence + 1);
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
            left = Node(new JavaScriptFunctionExpression([parameter.Name], body, IsArrow: true));
        }
        return left;
    }

    private JavaScriptExpression ParsePostfix()
    {
        return ParsePostfixTail(ParseUnary());
    }

    private JavaScriptExpression ParsePostfixTail(JavaScriptExpression expression)
    {
        while (true)
        {
            if (_current.Kind == JavaScriptTokenKind.Template)
            {
                var template = new JavaScriptTemplateExpression(_current.Text);
                Take();
                expression = Node(new JavaScriptCallExpression(expression, [template]));
                continue;
            }
            if (Match("."))
            {
                if (_current.Kind != JavaScriptTokenKind.Identifier) throw Error("Expected a member name");
                var name = _current.Text; Take();
                expression = Node(new JavaScriptMemberExpression(expression, name));
                continue;
            }
            if (Match("?."))
            {
                if (_current.Kind != JavaScriptTokenKind.Identifier) throw Error("Expected a member name after optional chaining");
                var name = _current.Text; Take();
                expression = Node(new JavaScriptMemberExpression(expression, name, Optional: true));
                continue;
            }
            if (Match("("))
            {
                var arguments = new List<JavaScriptExpression>();
                if (!Match(")"))
                {
                    do { arguments.Add(Match("...") ? new JavaScriptSpreadExpression(ParseBinary(0)) : ParseBinary(0)); } while (Match(","));
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
            if (_current.Text == "await")
            {
                Take();
                // Await consumes a complete unary/postfix operand (for example
                // `await Promise.resolve(value)`), while keeping the postfix
                // operations attached to the awaited expression rather than to
                // the resulting value.
                return Node(new JavaScriptAwaitExpression(ParsePostfixTail(ParseUnary())));
            }
            if (_current.Text == "async")
            {
                Take();
                if (_current.Kind == JavaScriptTokenKind.Identifier && _current.Text == "function")
                {
                    Take();
                    return Node(ParseFunctionTail(asyncFunction: true));
                }
                if (_current.Kind == JavaScriptTokenKind.Identifier && Peek(0).Text == "=>")
                {
                    var parameter = _current.Text;
                    Take(); Expect("=>");
                    var body = _current.Kind == JavaScriptTokenKind.Punctuation && _current.Text == "{"
                        ? ParseBlockStatement().Statements
                        : [new JavaScriptReturnStatement(ParseBinary(0))];
                    return Node(new JavaScriptFunctionExpression([parameter], body, IsAsync: true, IsArrow: true));
                }
                if (IsArrowParameterList(out var asyncArrowParameters))
                    return Node(ParseArrowFunction(asyncArrowParameters, isAsync: true));
                // `async` is also a valid ordinary identifier.
                return Node(new JavaScriptIdentifierExpression("async"));
            }
            if (Peek(0).Text == "=>")
            {
                var parameter = _current.Text;
                Take();
                Expect("=>");
                var body = _current.Kind == JavaScriptTokenKind.Punctuation && _current.Text == "{"
                    ? ParseBlockStatement().Statements
                    : [new JavaScriptReturnStatement(ParseBinary(0))];
                return Node(new JavaScriptFunctionExpression([parameter], body, IsArrow: true));
            }
            var name = _current.Text; Take();
            if (name == "yield")
            {
                var delegateYield = Match("*");
                return Node(new JavaScriptYieldExpression(_current.Kind == JavaScriptTokenKind.Punctuation && _current.Text is ";" or ")" or "}" ? null : ParseBinary(0), delegateYield));
            }
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
            return Node(ParseArrowFunction(arrowParameters, isAsync: false));
        }
        if (Match("("))
        {
            // Parenthesised sequence expressions are common in UMD wrappers,
            // e.g. `(global = global || self, factory(global.React = {}))`.
            // Commas remain delimiters in calls/arrays, so consume them only
            // inside this explicit parenthesised-expression production.
            var result = ParseSequenceExpression();
            Expect(")"); return result;
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
                    if (Match("..."))
                    {
                        properties.Add(("...", ParseBinary(0)));
                        continue;
                    }
                    if (Match("["))
                    {
                        var key = ParseBinary(0);
                        Expect("]");
                        var computedName = key switch
                        {
                            JavaScriptMemberExpression member when member.Target is JavaScriptIdentifierExpression target => target.Name + "." + member.Name,
                            JavaScriptIdentifierExpression identifier => identifier.Name,
                            JavaScriptLiteralExpression literal => literal.Text,
                            _ => string.Empty,
                        };
                        if (Match(":")) properties.Add((computedName, ParseBinary(0)));
                        else if (_current.Text == "(") properties.Add(("__folio_method__" + computedName, ParseFunctionTail()));
                        else properties.Add((computedName, new JavaScriptIdentifierExpression(computedName)));
                        continue;
                    }
                    if (_current.Kind is not (JavaScriptTokenKind.Identifier or JavaScriptTokenKind.String or JavaScriptTokenKind.Number)) throw Error("Expected an object property name");
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

    private JavaScriptExpression ParseSequenceExpression()
    {
        var result = ParseBinary(0);
        while (Match(","))
            result = Node(new JavaScriptBinaryExpression(",", result, ParseBinary(0)));
        return result;
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

    private JavaScriptFunctionExpression ParseFunctionTail(bool generatorAlreadyConsumed = false, bool asyncFunction = false)
    {
        var generator = generatorAlreadyConsumed || Match("*");
        // Function expressions may carry an internal name (for recursion and
        // stack traces), e.g. `value: function createEvent(...) { ... }`.
        // The bounded evaluator does not need a separate named scope, but it
        // must consume the name before the parameter list.
        string? functionName = null;
        if (_current.Kind == JavaScriptTokenKind.Identifier)
        {
            functionName = _current.Text;
            Take();
        }
        Expect("(");
        var parameters = new List<string>();
        var defaults = new List<JavaScriptExpression?>();
        if (!Match(")"))
        {
            do
            {
                var rest = Match("...");
                var name = ParseBindingName();
                JavaScriptExpression? defaultValue = null;
                if (!rest && Match("=")) defaultValue = ParseBinary(0);
                parameters.Add(rest ? "..." + name : name);
                defaults.Add(defaultValue);
            } while (Match(","));
            Expect(")");
        }
        var body = ParseBlockStatement();
        return new JavaScriptFunctionExpression(parameters, body.Statements, defaults, generator, functionName, asyncFunction);
    }

    private JavaScriptFunctionExpression ParseArrowFunction(IReadOnlyList<string> arrowParameters, bool isAsync)
    {
        Expect("(");
        var parsedParameters = new List<string>();
        var defaults = new List<JavaScriptExpression?>();
        for (var i = 0; i < arrowParameters.Count; i++)
        {
            var parameter = arrowParameters[i];
            var rest = parameter.StartsWith("...", StringComparison.Ordinal);
            if (rest) Expect("...");
            var expectedName = rest ? parameter[3..] : parameter;
            if (expectedName.StartsWith("[", StringComparison.Ordinal) || expectedName.StartsWith("{", StringComparison.Ordinal))
            {
                if (ParseBindingName() != expectedName) throw Error("Expected an arrow binding parameter");
            }
            else
            {
                if (_current.Kind != JavaScriptTokenKind.Identifier || _current.Text != expectedName) throw Error("Expected an arrow parameter");
                Take();
            }
            JavaScriptExpression? defaultValue = null;
            if (!rest && Match("=")) defaultValue = ParseBinary(0);
            parsedParameters.Add(parameter);
            defaults.Add(defaultValue);
            if (i + 1 < arrowParameters.Count) Expect(",");
        }
        Expect(")"); Expect("=>");
        var body = _current.Kind == JavaScriptTokenKind.Punctuation && _current.Text == "{"
            ? ParseBlockStatement().Statements
            : [new JavaScriptReturnStatement(ParseBinary(0))];
        return new JavaScriptFunctionExpression(parsedParameters, body, defaults, IsAsync: isAsync, IsArrow: true);
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
            var generator = Match("*");
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
            methods.Add(new JavaScriptClassMethod(methodName, getter, setter, isStatic, ParseFunctionTail(generator)));
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
            if (token.Text == "...")
            {
                var restName = Peek(offset++);
                if (restName.Kind != JavaScriptTokenKind.Identifier) return false;
                parameters.Add("..." + restName.Text);
            }
            else
            {
                if (token.Kind == JavaScriptTokenKind.Identifier)
                {
                    parameters.Add(token.Text);
                }
                else if (token.Text is "[" or "{")
                {
                    var open = token.Text;
                    var close = open == "[" ? "]" : "}";
                    var depth = 1;
                    var binding = open;
                    while (depth > 0)
                    {
                        var part = Peek(offset++);
                        if (part.Kind == JavaScriptTokenKind.End) return false;
                        binding += part.Text;
                        if (part.Text == open) depth++;
                        else if (part.Text == close) depth--;
                    }
                    parameters.Add(binding);
                }
                else return false;
            }
            var separator = Peek(offset++);
            if (separator.Text == "=")
            {
                // Skip a default expression while identifying the arrow shape.
                // The full expression is parsed in ParseUnary after recognition.
                var depth = 0;
                while (true)
                {
                    var defaultToken = Peek(offset++);
                    if (defaultToken.Text is "(" or "[" or "{") depth++;
                    else if (defaultToken.Text is ")" or "]" or "}")
                    {
                        if (depth == 0) { separator = defaultToken; break; }
                        depth--;
                    }
                    if (defaultToken.Kind == JavaScriptTokenKind.End) return false;
                }
            }
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
