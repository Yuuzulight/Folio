using System.Globalization;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;

namespace Folio.Scripting.Native;

/// <summary>Bounded evaluator for the expression AST; the full VM will replace this staged implementation.</summary>
internal sealed class JavaScriptEvaluator
{
    private static readonly ConditionalWeakTable<object, Dictionary<string, object?>> CallableProperties = new();

    internal static void SetCallableProperty(object callable, string name, object? value) => CallableProperties.GetOrCreateValue(callable)[name] = value;
    internal static bool TryGetCallableProperty(object callable, string name, out object? value)
    {
        if (CallableProperties.TryGetValue(callable, out var properties) && properties.TryGetValue(name, out value)) return true;
        value = null;
        return false;
    }
    private sealed class ReceiverBinding(Func<object?, IReadOnlyList<object?>, object?> invoke)
    {
        internal object? Invoke(object? receiver, IReadOnlyList<object?> args) => invoke(receiver, args);
    }
    private static readonly ConditionalWeakTable<Func<IReadOnlyList<object?>, object?>, ReceiverBinding> ReceiverBindings = new();
    private readonly IDictionary<string, object?> _bindings;
    private readonly IDictionary<string, object?>? _parentBindings;
    private readonly JavaScriptEvaluator? _parentEvaluator;
    private readonly IReadOnlyList<IDictionary<string, object?>> _withScopes;
    private readonly int _maxSteps;
    private readonly bool _preserveUndefined;
    private readonly Func<bool>? _deadlineExceeded;
    private readonly List<object?>? _yieldValues;
    private int _steps;

    internal JavaScriptEvaluator(IDictionary<string, object?>? bindings = null, int maxSteps = 100_000, IDictionary<string, object?>? parentBindings = null, JavaScriptEvaluator? parentEvaluator = null, bool preserveUndefined = false, IReadOnlyList<IDictionary<string, object?>>? withScopes = null, Func<bool>? deadlineExceeded = null, List<object?>? yieldValues = null)
    {
        _bindings = bindings ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        _parentBindings = parentBindings;
        _parentEvaluator = parentEvaluator;
        _preserveUndefined = preserveUndefined;
        _withScopes = withScopes ?? Array.Empty<IDictionary<string, object?>>();
        _deadlineExceeded = deadlineExceeded ?? parentEvaluator?._deadlineExceeded;
        _yieldValues = yieldValues;
        if (maxSteps <= 0) throw new ArgumentOutOfRangeException(nameof(maxSteps));
        _maxSteps = maxSteps;
    }

    internal object? Evaluate(JavaScriptExpression expression)
    {
        ThrowIfExpired();
        if (++_steps > _maxSteps) throw new InvalidOperationException("The script exceeded its evaluation step limit.");
        return expression switch
        {
            JavaScriptLiteralExpression literal => Literal(literal),
            JavaScriptTemplateExpression template => Template(template.Text),
            JavaScriptRegexExpression regex => RegexValue.Create(regex.Text),
            JavaScriptIdentifierExpression identifier => Lookup(identifier.Name),
            JavaScriptYieldExpression yielded => EvaluateYield(yielded),
            JavaScriptUnaryExpression unary => Unary(unary),
            JavaScriptUpdateExpression update => Update(update),
            JavaScriptBinaryExpression binary => Binary(binary),
            JavaScriptConditionalExpression conditional => Truth(Evaluate(conditional.Condition)) ? Evaluate(conditional.WhenTrue) : Evaluate(conditional.WhenFalse),
            JavaScriptMemberExpression member => Member(member),
            JavaScriptCallExpression call => Call(call),
            JavaScriptArrayExpression array => EvaluateArray(array),
            JavaScriptSpreadExpression spread => Evaluate(spread.Operand),
            JavaScriptIndexExpression index => Index(index),
            JavaScriptObjectExpression objectExpression => Object(objectExpression),
            JavaScriptFunctionExpression function => Function(function),
            JavaScriptClassExpression @class => Class(@class),
            _ => throw new InvalidDataException("Unknown JavaScript expression node."),
        };
    }

    private object Template(string text)
    {
        var result = new System.Text.StringBuilder();
        for (var offset = 0; offset < text.Length;)
        {
            var marker = text.IndexOf("${", offset, StringComparison.Ordinal);
            if (marker < 0) { result.Append(text[offset..]); break; }
            result.Append(text[offset..marker]);
            var depth = 1;
            var end = marker + 2;
            while (end < text.Length && depth > 0)
            {
                if (text[end] == '{') depth++;
                else if (text[end] == '}') depth--;
                end++;
            }
            if (depth != 0) { result.Append(text[marker..]); break; }
            var expression = new JavaScriptParser(text[(marker + 2)..(end - 1)]).ParseProgram();
            var value = Execute(expression);
            result.Append(StringValue(value));
            offset = end;
        }
        return result.ToString();
    }

    private object? EvaluateArray(JavaScriptArrayExpression array)
    {
        var result = new List<object?>();
        foreach (var element in array.Elements)
        {
            if (element is not JavaScriptSpreadExpression spread)
            {
                result.Add(Evaluate(element));
                continue;
            }

            var value = Evaluate(spread.Operand);
            if (value is NativeJavaScriptEngine.NativeSet set) result.AddRange(set.Values());
            else if (value is IEnumerable<object?> sequence) result.AddRange(sequence);
            else if (value is NativeJavaScriptEngine.INativeHostObject host
                && host.TryGetValue("length", out var length))
            {
                for (var index = 0; index < Math.Max(0, (int)Number(length)); index++)
                    if (host.TryGetIndex(index, out var item)) result.Add(item);
            }
        }
        return result;
    }

    internal object? Execute(IReadOnlyList<JavaScriptStatement> statements)
    {
        try { HoistVariables(statements); return ExecuteCore(statements); }
        catch (ReturnSignal returned) { return returned.Value; }
    }

    internal object? ExecuteFunctionBody(IReadOnlyList<JavaScriptStatement> statements)
    {
        try
        {
            HoistVariables(statements);
            ExecuteCore(statements);
            return UndefinedOrNull();
        }
        catch (ReturnSignal returned) { return returned.Value; }
    }

    private void HoistVariables(IReadOnlyList<JavaScriptStatement> statements)
    {
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case JavaScriptVariableStatement variable:
                    HoistBinding(variable.Name);
                    break;
                case JavaScriptBlockStatement block:
                    HoistVariables(block.Statements);
                    break;
                case JavaScriptIfStatement conditional:
                    HoistStatement(conditional.Then);
                    if (conditional.Else is { } alternative) HoistStatement(alternative);
                    break;
                case JavaScriptWhileStatement loop:
                    HoistStatement(loop.Body);
                    break;
                case JavaScriptDoWhileStatement loop:
                    HoistStatement(loop.Body);
                    break;
                case JavaScriptForStatement loop:
                    if (loop.Initializer is { } initializer) HoistStatement(initializer);
                    HoistStatement(loop.Body);
                    break;
                case JavaScriptForEachStatement each:
                    HoistBinding(each.Name);
                    HoistStatement(each.Body);
                    break;
                case JavaScriptLabeledStatement labeled:
                    HoistStatement(labeled.Body);
                    break;
                case JavaScriptWithStatement with:
                    HoistStatement(with.Body);
                    break;
                case JavaScriptTryStatement guarded:
                    HoistVariables(guarded.Body.Statements);
                    if (guarded.CatchName is { } catchName) HoistBinding(catchName);
                    if (guarded.CatchBody is { } catchBody) HoistVariables(catchBody.Statements);
                    if (guarded.FinallyBody is { } finallyBody) HoistVariables(finallyBody.Statements);
                    break;
                case JavaScriptSwitchStatement selection:
                    foreach (var @case in selection.Cases) HoistVariables(@case.Statements);
                    break;
            }
        }
    }

    private void HoistStatement(JavaScriptStatement statement) => HoistVariables([statement]);

    private void HoistBinding(string name)
    {
        if (name.StartsWith("[", StringComparison.Ordinal) && name.EndsWith("]", StringComparison.Ordinal))
        {
            foreach (var item in name[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (!item.StartsWith("...", StringComparison.Ordinal)) HoistBinding(item.Trim());
            return;
        }
        if (name.StartsWith("{", StringComparison.Ordinal) && name.EndsWith("}", StringComparison.Ordinal))
        {
            foreach (var item in name[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = item.Split(':', 2);
                HoistBinding((parts.Length == 2 ? parts[1] : parts[0]).Trim());
            }
            return;
        }
        if (name.Length != 0) _bindings.TryAdd(name, UndefinedValue.Instance);
    }

    private object? ExecuteCore(IReadOnlyList<JavaScriptStatement> statements)
    {
        ThrowIfExpired();
        foreach (var declaration in statements.OfType<JavaScriptFunctionDeclarationStatement>())
            _bindings[declaration.Name] = Evaluate(declaration.Function);
        object? lastResult = UndefinedValue.Instance;
        foreach (var statement in statements)
        {
            ThrowIfExpired();
            if (++_steps > _maxSteps) throw new InvalidOperationException("The script exceeded its evaluation step limit.");
            switch (statement)
            {
                case JavaScriptExpressionStatement expression: lastResult = Evaluate(expression.Expression); break;
                case JavaScriptVariableStatement variable:
                {
                    var value = variable.Initializer is null ? UndefinedOrNull() : Evaluate(variable.Initializer);
                    if (variable.Name.StartsWith("[", StringComparison.Ordinal) && variable.Name.EndsWith("]", StringComparison.Ordinal))
                    {
                        var names = variable.Name[1..^1].Split(',', StringSplitOptions.None);
                        for (var index = 0; index < names.Length; index++)
                            if (!string.IsNullOrEmpty(names[index])) _bindings[names[index]] = value is IList<object?> list && index < list.Count ? list[index] : value is IReadOnlyList<object?> readOnly && index < readOnly.Count ? readOnly[index] : UndefinedValue.Instance;
                    }
                    else if (variable.Name.StartsWith("{", StringComparison.Ordinal) && variable.Name.EndsWith("}", StringComparison.Ordinal))
                    {
                        foreach (var entry in variable.Name[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var parts = entry.Split(':', 2);
                            var property = parts[0];
                            var local = parts.Length == 2 ? parts[1] : property;
                            var propertyValue = MemberValue(value, property);
                            _bindings[local] = propertyValue is UndefinedValue ? UndefinedValue.Instance : propertyValue;
                        }
                    }
                    else _bindings[variable.Name] = value;
                    break;
                }
                case JavaScriptFunctionDeclarationStatement function: _bindings[function.Name] = Evaluate(function.Function); break;
                case JavaScriptClassDeclarationStatement @class: _bindings[@class.Name] = Evaluate(@class.Class); break;
                case JavaScriptBlockStatement block:
                    ExecuteCore(block.Statements);
                    break;
                case JavaScriptWithStatement with:
                    var scope = Evaluate(with.Scope);
                    IDictionary<string, object?>? properties = scope switch
                    {
                        IDictionary<string, object?> map => map,
                        IReadOnlyDictionary<string, object?> readOnly => readOnly.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                        _ => null,
                    };
                    if (properties is null)
                        throw new InvalidDataException("The with scope must be an object.");
                    var scoped = new JavaScriptEvaluator(_bindings, Math.Max(1, _maxSteps - _steps), _parentBindings, _parentEvaluator, _preserveUndefined, [.. _withScopes, properties], _deadlineExceeded);
                    var scopedResult = with.Body is JavaScriptBlockStatement scopedBlock
                        ? scoped.ExecuteCore(scopedBlock.Statements)
                        : scoped.ExecuteCore([with.Body]);
                    _steps += scoped._steps;
                    break;
                case JavaScriptIfStatement conditional:
                    if (Truth(Evaluate(conditional.Condition))) ExecuteCore([conditional.Then]);
                    else if (conditional.Else is { } alternative) ExecuteCore([alternative]);
                    break;
                case JavaScriptWhileStatement loop:
                    try
                    {
                        while (Truth(Evaluate(loop.Condition)))
                        {
                            if (++_steps > _maxSteps) throw new InvalidOperationException("The script exceeded its evaluation step limit.");
                            try { ExecuteCore([loop.Body]); }
                            catch (ContinueSignal signal) when (signal.Label is null || signal.Label == loop.Label) { }
                        }
                    }
                    catch (BreakSignal signal) when (signal.Label is null || signal.Label == loop.Label) { }
                    break;
                case JavaScriptDoWhileStatement loop:
                    try
                    {
                        do
                        {
                            if (++_steps > _maxSteps) throw new InvalidOperationException("The script exceeded its evaluation step limit.");
                            try { ExecuteCore([loop.Body]); }
                            catch (ContinueSignal signal) when (signal.Label is null || signal.Label == loop.Label) { }
                        }
                        while (Truth(Evaluate(loop.Condition)));
                    }
                    catch (BreakSignal signal) when (signal.Label is null || signal.Label == loop.Label) { }
                    break;
                case JavaScriptBreakStatement breakStatement: throw new BreakSignal(breakStatement.Label);
                case JavaScriptContinueStatement continueStatement: throw new ContinueSignal(continueStatement.Label);
                case JavaScriptForStatement loop:
                    if (loop.Initializer is not null) ExecuteCore([loop.Initializer]);
                    try
                    {
                        while (loop.Condition is null || Truth(Evaluate(loop.Condition)))
                        {
                            if (++_steps > _maxSteps) throw new InvalidOperationException("The script exceeded its evaluation step limit.");
                            try { ExecuteCore([loop.Body]); }
                            catch (ContinueSignal signal) when (signal.Label is null || signal.Label == loop.Label) { }
                            if (loop.Update is not null) Evaluate(loop.Update);
                        }
                    }
                    catch (BreakSignal signal) when (signal.Label is null || signal.Label == loop.Label) { }
                    break;
                case JavaScriptForEachStatement each:
                    var source = Evaluate(each.Iterable);
                    var items = each.Keys && source is IReadOnlyDictionary<string, object?> dictionary
                        ? dictionary.Keys.Cast<object?>().ToArray()
                        : source is IReadOnlyList<object?> array ? (each.Keys ? Enumerable.Range(0, array.Count).Cast<object?>().ToArray() : array.ToArray())
                        : source is NativeJavaScriptEngine.INativeHostObject host && host.TryGetValue("length", out var hostLength)
                            ? Enumerable.Range(0, Math.Max(0, (int)Number(hostLength)))
                                .Select(index => host.TryGetIndex(index, out var value) ? value : UndefinedOrNull()).ToArray()
                            : Array.Empty<object?>();
                    try
                    {
                        foreach (var item in items)
                        {
                            if (++_steps > _maxSteps) throw new InvalidOperationException("The script exceeded its evaluation step limit.");
                            if (each.Name.StartsWith("[", StringComparison.Ordinal) && each.Name.EndsWith("]", StringComparison.Ordinal))
                            {
                                var names = each.Name[1..^1].Split(',', StringSplitOptions.None);
                                var values = item as IReadOnlyList<object?>;
                                for (var index = 0; index < names.Length; index++) if (!string.IsNullOrEmpty(names[index])) _bindings[names[index]] = values is not null && index < values.Count ? values[index] : null;
                            }
                            else _bindings[each.Name] = item;
                            try { ExecuteCore([each.Body]); }
                            catch (ContinueSignal signal) when (signal.Label is null || signal.Label == each.Label) { }
                        }
                    }
                    catch (BreakSignal signal) when (signal.Label is null || signal.Label == each.Label) { }
                    break;
                case JavaScriptLabeledStatement labeled:
                    try { ExecuteCore([labeled.Body]); }
                    catch (BreakSignal signal) when (signal.Label == labeled.Label) { }
                    break;
                case JavaScriptThrowStatement thrown: throw new JavaScriptThrownException(Evaluate(thrown.Expression));
                case JavaScriptTryStatement guarded:
                    try { ExecuteCore([guarded.Body]); }
                    catch (JavaScriptThrownException error) when (guarded.CatchBody is not null)
                    {
                        if (guarded.CatchName is not null) _bindings[guarded.CatchName] = error.Value;
                        ExecuteCore([guarded.CatchBody]);
                    }
                    finally
                    {
                        if (guarded.FinallyBody is not null) ExecuteCore([guarded.FinallyBody]);
                    }
                    break;
                case JavaScriptSwitchStatement selection:
                    var switchValue = Evaluate(selection.Expression);
                    var activeCase = false;
                    try
                    {
                        foreach (var @case in selection.Cases)
                        {
                            if (!activeCase && (@case.Test is null || Equals(switchValue, Evaluate(@case.Test)))) activeCase = true;
                            if (activeCase) ExecuteCore(@case.Statements);
                        }
                    }
                    // An unlabeled break exits the switch. A labeled break may
                    // target an enclosing labeled statement and must continue
                    // propagating until that label handles it.
                    catch (BreakSignal signal) when (signal.Label is null) { }
                    break;
                case JavaScriptReturnStatement returned: throw new ReturnSignal(returned.Expression is null ? UndefinedOrNull() : Evaluate(returned.Expression));
                case JavaScriptYieldStatement yielded:
                    if (_yieldValues is null) throw new InvalidDataException("yield is only valid inside a generator.");
                    AddYield(yielded.Expression is null ? UndefinedOrNull() : Evaluate(yielded.Expression), yielded.Delegate);
                    break;
                default: throw new InvalidDataException("Unknown JavaScript statement node.");
            }
        }
        return lastResult is UndefinedValue ? UndefinedOrNull() : lastResult;
    }

    private object? EvaluateYield(JavaScriptYieldExpression yielded)
    {
        var value = yielded.Expression is null ? UndefinedOrNull() : Evaluate(yielded.Expression);
        if (_yieldValues is null) throw new InvalidDataException("yield is only valid inside a generator.");
        AddYield(value, yielded.Delegate);
        return value;
    }

    private void AddYield(object? value, bool delegated)
    {
        if (_yieldValues is null) throw new InvalidDataException("yield is only valid inside a generator.");
        if (delegated && value is IEnumerable<object?> sequence) _yieldValues.AddRange(sequence);
        else _yieldValues.Add(value);
    }

    private object? Literal(JavaScriptLiteralExpression literal)
    {
        if (literal.Kind == JavaScriptTokenKind.String) return literal.Text;
        if (literal.Kind == JavaScriptTokenKind.Number)
        {
            var text = literal.Text.Replace("_", string.Empty, StringComparison.Ordinal);
            if (text.EndsWith('n')) text = text[..^1];
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return Convert.ToDouble(Convert.ToInt64(text[2..], 16), CultureInfo.InvariantCulture);
            if (text.StartsWith("0b", StringComparison.OrdinalIgnoreCase)) return Convert.ToDouble(Convert.ToInt64(text[2..], 2), CultureInfo.InvariantCulture);
            if (text.StartsWith("0o", StringComparison.OrdinalIgnoreCase)) return Convert.ToDouble(Convert.ToInt64(text[2..], 8), CultureInfo.InvariantCulture);
            return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        return literal.Text switch { "true" => true, "false" => false, "undefined" => UndefinedOrNull(), _ => null };
    }

    private object? Unary(JavaScriptUnaryExpression unary)
    {
        if (unary.Operator == "typeof")
        {
            var typeValue = Evaluate(unary.Operand);
            return typeValue switch
            {
                null or UndefinedValue => "undefined",
                bool => "boolean",
                string => "string",
                double or float or int or long or decimal => "number",
                Func<IReadOnlyList<object?>, object?> or NativeJavaScriptEngine.INativeCallable => "function",
                _ => "object",
            };
        }
        if (unary.Operator == "void") { Evaluate(unary.Operand); return UndefinedOrNull(); }
        if (unary.Operator == "new")
        {
            if (unary.Operand is JavaScriptCallExpression constructorCall) return Construct(constructorCall);
            return Construct(new JavaScriptCallExpression(unary.Operand, []));
        }
        if (unary.Operator == "delete") return Delete(unary.Operand);
        var value = Evaluate(unary.Operand);
        return unary.Operator switch
        {
            "!" => !Truth(value),
            "+" => Number(value),
            "-" => -Number(value),
            "~" => (double)~ToInt32(Number(value)),
            _ => throw new NotSupportedException($"Unsupported unary operator '{unary.Operator}'."),
        };
    }

    private object Delete(JavaScriptExpression operand)
    {
        switch (operand)
        {
            case JavaScriptMemberExpression member when Evaluate(member.Target) is NativeJavaScriptEngine.INativeHostObject host:
                return host.TryDeleteValue(member.Name);
            case JavaScriptMemberExpression member when Evaluate(member.Target) is IDictionary<string, object?> properties:
                return properties.Remove(member.Name);
            case JavaScriptIndexExpression index when Evaluate(index.Target) is IList<object?> values:
                var position = (int)Number(Evaluate(index.Index));
                if (position < 0 || position >= values.Count) return true;
                values[position] = null;
                return true;
            default:
                Evaluate(operand);
                return true;
        }
    }

    private object? Construct(JavaScriptCallExpression call)
    {
        // `new Ctor(args).member` is parsed as a member call whose target is the
        // constructor call. Construct the target first, then apply the member
        // access; treating the whole member expression as the constructor makes
        // the evaluator reject otherwise valid Web APIs such as `new URL(...).href`.
        if (call.Callee is JavaScriptMemberExpression member && member.Target is JavaScriptCallExpression targetCall)
        {
            var receiver = Construct(targetCall);
            var property = MemberValue(receiver, member.Name);
            // The parser represents both `new Ctor().value` and
            // `new Ctor().method()` as this shape. Invoke callable members so
            // the latter keeps JavaScript's construction/member precedence;
            // ordinary properties (such as URL.href) are returned directly.
            return property is Func<IReadOnlyList<object?>, object?> or NativeJavaScriptEngine.INativeCallable or NativeJavaScriptEngine.NativeReceiverFunction
                ? InvokeWithReceiver(property!, receiver, call.Arguments.Select(Evaluate).ToArray())
                : property;
        }
        if (call.Callee is JavaScriptMemberExpression memberChain
            && memberChain.Target is JavaScriptMemberExpression nestedMember
            && nestedMember.Target is JavaScriptCallExpression chainedConstructor)
        {
            var receiver = Construct(chainedConstructor);
            var intermediate = MemberValue(receiver, nestedMember.Name);
            var property = MemberValue(intermediate, memberChain.Name);
            return property is Func<IReadOnlyList<object?>, object?> or NativeJavaScriptEngine.INativeCallable or NativeJavaScriptEngine.NativeReceiverFunction
                ? InvokeWithReceiver(property!, intermediate, call.Arguments.Select(Evaluate).ToArray())
                : property;
        }
        var constructor = call.Callee is JavaScriptIdentifierExpression identifier ? Lookup(identifier.Name) : Evaluate(call.Callee);
        var arguments = call.Arguments.Select(Evaluate).ToArray();
        var instance = new Dictionary<string, object?>(StringComparer.Ordinal) { ["__constructor"] = constructor };
        object? result = constructor switch
        {
            NativeJavaScriptEngine.NativeReceiverFunction receiverFunction => receiverFunction.Invoke(instance, arguments),
            NativeJavaScriptEngine.INativeCallable callable => callable.Invoke(arguments),
            Func<IReadOnlyList<object?>, object?> function => InvokeFunction(function, instance, arguments),
            _ => throw new NotSupportedException($"Constructor is not callable (callee: {call.Callee})."),
        };
        if (instance is IDictionary<string, object?> properties && TryGetPrototype(constructor, out var prototype))
            foreach (var pair in prototype) properties.TryAdd(pair.Key, pair.Value);
        return result is null or UndefinedValue ? instance : result;
    }

    private static bool TryGetPrototype(object? constructor, out IReadOnlyDictionary<string, object?> prototype)
    {
        if (constructor is NativeJavaScriptEngine.INativeCallable callable && callable.TryGetProperty("prototype", out var nativePrototype) && nativePrototype is IReadOnlyDictionary<string, object?> nativeProperties)
        {
            prototype = nativeProperties;
            return true;
        }
        if (constructor is Func<IReadOnlyList<object?>, object?> function && CallableProperties.TryGetValue(function, out var properties) && properties.TryGetValue("prototype", out var functionPrototype) && functionPrototype is IReadOnlyDictionary<string, object?> functionProperties)
        {
            prototype = functionProperties;
            return true;
        }
        prototype = null!;
        return false;
    }

    private object? Update(JavaScriptUpdateExpression update)
    {
        var oldValue = Evaluate(update.Operand);
        var next = Number(oldValue) + (update.Operator == "++" ? 1 : -1);
        if (update.Operand is JavaScriptIdentifierExpression identifier)
            Assign(identifier.Name, next);
        else
            AssignValue(update.Operand, next);
        return update.Prefix ? next : oldValue;
    }

    private object? Binary(JavaScriptBinaryExpression binary)
    {
        if (binary.Operator == ",")
        {
            Evaluate(binary.Left);
            return Evaluate(binary.Right);
        }
        if (binary.Operator is "&&=" or "||=" or "??=")
        {
            var current = Evaluate(binary.Left);
            var takeRight = binary.Operator switch { "&&=" => Truth(current), "||=" => !Truth(current), _ => current is null or UndefinedValue };
            if (!takeRight) return current;
            var assigned = Evaluate(binary.Right);
            AssignValue(binary.Left, assigned);
            return assigned;
        }
        if (binary.Operator is "=" or "+=" or "-=" or "*=" or "/=" or "%=" or "&=" or "|=" or "^=" or "<<=" or ">>=" or ">>>=")
        {
            var assigned = binary.Operator == "=" ? Evaluate(binary.Right) : ApplyCompound(binary.Operator, binary.Left, binary.Right);
            AssignValue(binary.Left, assigned);
            return assigned;
        }
        var left = Evaluate(binary.Left);
        if (binary.Operator == "&&") return Truth(left) ? Evaluate(binary.Right) : left;
        if (binary.Operator == "||") return Truth(left) ? left : Evaluate(binary.Right);
        if (binary.Operator == "??") return left is null or UndefinedValue ? Evaluate(binary.Right) : left;
        if (binary.Operator == "instanceof") return IsInstanceOf(left, binary.Right is JavaScriptIdentifierExpression constructor ? constructor.Name : Evaluate(binary.Right));
        var right = Evaluate(binary.Right);
        if (binary.Operator == "in") return ContainsProperty(left, right);
        if (binary.Operator == "+" && (left is string || right is string)) return StringValue(left) + StringValue(right);
        var l = Number(left); var r = Number(right);
        return binary.Operator switch
        {
            "+" => l + r, "-" => l - r, "*" => l * r,
            "**" => Math.Pow(l, r),
            "/" => r == 0 ? double.NaN : l / r,
            "%" => r == 0 ? double.NaN : l % r,
            "<" => l < r, ">" => l > r, "<=" => l <= r, ">=" => l >= r,
            "==" => JavaScriptNullishEquals(left, right) ?? Equals(left, right),
            "===" => Equals(left, right),
            "!=" => !(JavaScriptNullishEquals(left, right) ?? Equals(left, right)),
            "!==" => !Equals(left, right),
            "<<" => (double)(ToInt32(l) << (ToInt32(r) & 31)),
            ">>" => (double)(ToInt32(l) >> (ToInt32(r) & 31)), ">>>" => (double)((uint)ToInt32(l) >> (ToInt32(r) & 31)),
            "&" => (double)(ToInt32(l) & ToInt32(r)), "^" => (double)(ToInt32(l) ^ ToInt32(r)), "|" => (double)(ToInt32(l) | ToInt32(r)),
            _ => throw new NotSupportedException($"Unsupported binary operator '{binary.Operator}'."),
        };
    }

    private void AssignValue(JavaScriptExpression target, object? assigned)
    {
        switch (target)
        {
            case JavaScriptIdentifierExpression identifier:
                Assign(identifier.Name, assigned);
                break;
            case JavaScriptMemberExpression member when Evaluate(member.Target) is NativeJavaScriptEngine.INativeHostObject host:
                if (!host.TrySetSetValue(member.Name, assigned) && !host.TrySetPrototypeValue(member.Name, assigned))
                    throw new InvalidDataException($"Host property '{member.Name}' is read-only.");
                break;
            case JavaScriptMemberExpression member when Evaluate(member.Target) is IDictionary<string, object?> properties:
                if (properties.TryGetValue(member.Name, out var existing)
                    && existing is NativeJavaScriptEngine.NativePropertyAccessor accessor)
                {
                    if (!accessor.Set(properties, assigned))
                        throw new InvalidDataException($"Property '{member.Name}' is read-only.");
                }
                else
                {
                    properties[member.Name] = assigned;
                }
                break;
            case JavaScriptMemberExpression member when Evaluate(member.Target) is IList<object?> values && member.Name == "length":
                var length = (int)Number(assigned);
                if (length < 0) throw new ArgumentOutOfRangeException(nameof(assigned), "Invalid array length.");
                while (values.Count > length) values.RemoveAt(values.Count - 1);
                while (values.Count < length) values.Add(JavaScriptEvaluator.UndefinedValue.Instance);
                break;
            case JavaScriptMemberExpression member when Evaluate(member.Target) is NativeJavaScriptEngine.NativeFunction function:
                function.SetProperty(member.Name, assigned);
                break;
            case JavaScriptMemberExpression member when Evaluate(member.Target) is NativeJavaScriptEngine.NativeReceiverFunction receiverFunction:
                receiverFunction.SetProperty(member.Name, assigned);
                break;
            case JavaScriptMemberExpression member when Evaluate(member.Target) is Func<IReadOnlyList<object?>, object?> callable:
                CallableProperties.GetOrCreateValue(callable)[member.Name] = assigned;
                break;
            case JavaScriptIndexExpression index when Evaluate(index.Target) is IList<object?> values:
                var position = (int)Number(Evaluate(index.Index));
                if (position < 0) throw new IndexOutOfRangeException();
                if (position < values.Count) values[position] = assigned;
                else
                {
                    while (values.Count < position) values.Add(null);
                    values.Add(assigned);
                }
                break;
            case JavaScriptIndexExpression index when Evaluate(index.Target) is IDictionary<string, object?> indexedProperties:
                var indexName = StringValue(Evaluate(index.Index));
                if (indexedProperties.TryGetValue(indexName, out var indexedExisting)
                    && indexedExisting is NativeJavaScriptEngine.NativePropertyAccessor indexedAccessor)
                {
                    if (!indexedAccessor.Set(indexedProperties, assigned))
                        throw new InvalidDataException($"Property '{indexName}' is read-only.");
                }
                else
                {
                    indexedProperties[indexName] = assigned;
                }
                break;
            case JavaScriptIndexExpression index when Evaluate(index.Target) is NativeJavaScriptEngine.INativeBuffer buffer:
                var bufferPosition = (int)Number(Evaluate(index.Index));
                if (!buffer.TrySetIndex(bufferPosition, assigned)) throw new IndexOutOfRangeException();
                break;
            case JavaScriptIndexExpression index when Evaluate(index.Target) is NativeJavaScriptEngine.INativeHostObject host:
                if (!host.TrySetSetValue(StringValue(Evaluate(index.Index)), assigned))
                    throw new InvalidDataException("Host indexed property is read-only.");
                break;
            default:
                throw new InvalidDataException($"Only lexical, object-property, and array-index assignment is supported (target: {target}).");
        }
    }

    private object? ApplyCompound(string op, JavaScriptExpression leftExpression, JavaScriptExpression rightExpression)
    {
        var left = Evaluate(leftExpression);
        var right = Evaluate(rightExpression);
        var operation = op[..^1];
        if (operation == "&" || operation == "|" || operation == "^")
            return operation switch { "&" => (double)(ToInt32(Number(left)) & ToInt32(Number(right))), "|" => (double)(ToInt32(Number(left)) | ToInt32(Number(right))), _ => (double)(ToInt32(Number(left)) ^ ToInt32(Number(right))) };
        if (operation is "<<" or ">>")
            return operation == "<<" ? (double)(ToInt32(Number(left)) << (ToInt32(Number(right)) & 31)) : (double)(ToInt32(Number(left)) >> (ToInt32(Number(right)) & 31));
        if (operation == ">>>") return (double)((uint)ToInt32(Number(left)) >> (ToInt32(Number(right)) & 31));
        if (operation == "+" && (left is string || right is string))
            return StringValue(left) + StringValue(right);
        var l = Number(left); var r = Number(right);
        return operation switch { "+" => l + r, "-" => l - r, "*" => l * r, "/" => r == 0 ? double.NaN : l / r, "%" => r == 0 ? double.NaN : l % r, _ => throw new NotSupportedException($"Unsupported compound operator '{op}'.") };
    }

    private static bool ContainsProperty(object? key, object? target) => target switch
    {
        IReadOnlyDictionary<string, object?> properties => properties.ContainsKey(StringValue(key)),
        IReadOnlyList<object?> values when int.TryParse(StringValue(key), out var index) => index >= 0 && index < values.Count,
        string text => StringValue(key) is var property && property == "length",
        _ => false,
    };

    private static bool IsInstanceOf(object? value, object? constructor)
    {
        if (constructor is string name)
        {
            if (name == "Array") return value is IReadOnlyList<object?>;
            if (name == "Object") return value is IReadOnlyDictionary<string, object?>;
            if (name == "Function") return value is Func<IReadOnlyList<object?>, object?> or NativeJavaScriptEngine.INativeCallable;
            if (value is NativeJavaScriptEngine.INativeBuffer buffer && string.Equals(buffer.TypeName, name, StringComparison.Ordinal)) return true;
            if (value is IReadOnlyDictionary<string, object?> named
                && named.TryGetValue("__constructor", out var constructorName)
                && string.Equals(constructorName as string, name, StringComparison.Ordinal)) return true;
            if (value is NativeJavaScriptEngine.NativeHostValue hostValue)
            {
                for (var template = hostValue.Template; template is not null; template = template.PrototypeTemplate)
                    if (string.Equals(template.Name, name, StringComparison.Ordinal)) return true;
            }
        }
        if (value is NativeJavaScriptEngine.NativeHostValue host
            && TryGetCallableName(constructor, out var hostConstructorName))
        {
            for (var template = host.Template; template is not null; template = template.PrototypeTemplate)
                if (string.Equals(template.Name, StringValue(hostConstructorName), StringComparison.Ordinal)) return true;
        }
        if (value is IReadOnlyDictionary<string, object?> constructed
            && constructed.TryGetValue("__constructor", out var marker)
            && (ReferenceEquals(marker, constructor)
                || (marker is string markerName && TryGetCallableName(constructor, out var markerConstructorName)
                    && string.Equals(markerName, StringValue(markerConstructorName), StringComparison.Ordinal)))) return true;
        return false;
    }

    private static bool TryGetCallableName(object? constructor, out object? name)
    {
        if (constructor is NativeJavaScriptEngine.NativeFunction function
            && function.TryGetProperty("name", out name)) return true;
        if (constructor is NativeJavaScriptEngine.NativeReceiverFunction receiver
            && receiver.TryGetProperty("name", out name)) return true;
        if (constructor is Func<IReadOnlyList<object?>, object?> callable
            && (TryGetCallableProperty(callable, "__folio_template", out name)
                || TryGetCallableProperty(callable, "name", out name))) return true;
        name = null;
        return false;
    }

    private object? Call(JavaScriptCallExpression call)
    {
        object? receiver = null;
        object? target;
        if (call.Callee is JavaScriptMemberExpression memberCallee)
        {
            receiver = Evaluate(memberCallee.Target);
            target = MemberValue(receiver, memberCallee.Name);
        }
        else
        {
            target = call.Callee is JavaScriptIdentifierExpression identifier
                ? Lookup(identifier.Name)
                : Evaluate(call.Callee);
        }
        if (target is not Func<IReadOnlyList<object?>, object?>
            && target is not NativeJavaScriptEngine.INativeCallable
            && target is not NativeJavaScriptEngine.NativeReceiverFunction)
            throw new NotSupportedException($"Only explicitly supplied host functions can be called in the staged VM: {call.Callee} resolved to {target?.GetType().Name ?? "null"}.");
        var expandedArguments = new List<object?>();
        foreach (var argument in call.Arguments)
        {
            var value = Evaluate(argument is JavaScriptSpreadExpression spread ? spread.Operand : argument);
            if (argument is JavaScriptSpreadExpression && value is IEnumerable<object?> sequence) expandedArguments.AddRange(sequence);
            else expandedArguments.Add(value);
        }
        var arguments = expandedArguments.ToArray();
        if (target is NativeJavaScriptEngine.NativeReceiverFunction receiverCallable)
            return receiverCallable.Invoke(receiver, arguments);
        return target is NativeJavaScriptEngine.INativeCallable callable
            ? callable.Invoke(arguments)
            : InvokeFunction((Func<IReadOnlyList<object?>, object?>)target, receiver, arguments);
    }

    private object? Index(JavaScriptIndexExpression index)
    {
        var target = Evaluate(index.Target);
        var key = Evaluate(index.Index);
        if (target is NativeJavaScriptEngine.INativeHostObject namedHost
            && namedHost.TryGetValue(StringValue(key), out var namedValue)) return namedValue;
        if (target is IReadOnlyDictionary<string, object?> dictionary
            && dictionary.TryGetValue(StringValue(key), out var dictionaryValue)) return dictionaryValue;
        if (target is IList<object?> iterable && StringValue(key) == "Symbol.iterator")
            return (Func<IReadOnlyList<object?>, object?>)(_ => iterable.ToArray());
        var position = (int)Number(key);
        return target switch
        {
            NativeJavaScriptEngine.INativeHostObject host when int.TryParse(StringValue(key), out var hostIndex) && host.TryGetIndex(hostIndex, out var value) => value,
            NativeJavaScriptEngine.INativeBuffer buffer when buffer.TryGetIndex(position, out var bufferValue) => bufferValue,
            IReadOnlyList<object?> values when position >= 0 && position < values.Count => values[position],
            IReadOnlyList<object?> values when position >= 0 && position < values.Count => values[position],
            string text when position >= 0 && position < text.Length => text[position].ToString(),
            _ => UndefinedOrNull(),
        };
    }

    private object? Member(JavaScriptMemberExpression member)
    {
        return MemberValue(Evaluate(member.Target), member.Name);
    }

    private object? MemberValue(object? target, string name)
    {
        if (target is IReadOnlyDictionary<string, object?> dictionary && name == "hasOwnProperty" && !dictionary.ContainsKey(name))
            return (Func<IReadOnlyList<object?>, object?>)(args => args.Count > 0 && dictionary.ContainsKey(StringValue(args[0])));
        if (target is IReadOnlyDictionary<string, object?> ownProperties && name == "prototype" && ownProperties.TryGetValue("prototype", out var ownPrototype))
            return ownPrototype;
        if (target is NativeJavaScriptEngine.NativeReceiverFunction receiverFunction)
        {
            if (name == "prototype")
            {
                if (!receiverFunction.TryGetProperty("prototype", out var prototype))
                {
                    prototype = new Dictionary<string, object?>(StringComparer.Ordinal);
                    receiverFunction.SetProperty("prototype", prototype);
                }
                return prototype;
            }
            if (name == "bind") return (Func<IReadOnlyList<object?>, object?>)(args =>
            {
                var boundReceiver = args.Count > 0 ? args[0] : null;
                var boundArguments = args.Count > 1 ? args.Skip(1).ToArray() : Array.Empty<object?>();
                return (Func<IReadOnlyList<object?>, object?>)(callArgs => receiverFunction.Invoke(boundReceiver, [.. boundArguments, .. callArgs]));
            });
            if (name == "call") return (Func<IReadOnlyList<object?>, object?>)(args => receiverFunction.Invoke(args.Count > 0 ? args[0] : null, args.Count > 1 ? args.Skip(1).ToArray() : []));
            if (name == "apply") return (Func<IReadOnlyList<object?>, object?>)(args => receiverFunction.Invoke(args.Count > 0 ? args[0] : null, args.Count > 1 && args[1] is IReadOnlyList<object?> callArgs ? callArgs : []));
        }
        if (target is NativeJavaScriptEngine.INativeCallable callable && callable.TryGetProperty(name, out var callableValue)) return callableValue;
        if (target is NativeJavaScriptEngine.NativeFunction nativeFunction)
        {
            if (name == "prototype")
            {
                if (!nativeFunction.TryGetProperty("prototype", out var prototype))
                {
                    prototype = new Dictionary<string, object?>(StringComparer.Ordinal);
                    nativeFunction.SetProperty("prototype", prototype);
                }
                return prototype;
            }
            if (name == "bind") return (Func<IReadOnlyList<object?>, object?>)(args =>
            {
                var boundArguments = args.Count > 1 ? args.Skip(1).ToArray() : Array.Empty<object?>();
                return (Func<IReadOnlyList<object?>, object?>)(callArgs => nativeFunction.Invoke([.. boundArguments, .. callArgs]));
            });
            if (name == "call") return (Func<IReadOnlyList<object?>, object?>)(args => nativeFunction.Invoke(args.Count > 1 ? args.Skip(1).ToArray() : []));
            if (name == "apply") return (Func<IReadOnlyList<object?>, object?>)(args => nativeFunction.Invoke(args.Count > 1 && args[1] is IReadOnlyList<object?> callArgs ? callArgs : []));
        }
        if (target is NativeJavaScriptEngine.INativePromise promise && promise.TryGetProperty(name, out var promiseValue)) return promiseValue;
        if (target is IList<object?> values)
        {
            if (name == "length") return (double)values.Count;
            if (name == "Symbol.iterator") return (Func<IReadOnlyList<object?>, object?>)(_ => values.ToArray());
            if (TryArrayMethod(values, name, out var arrayMethod)) return arrayMethod;
        }
        if (target is double or int or long)
        {
            if (name == "toString")
                return (Func<IReadOnlyList<object?>, object?>)(args => StringValue(target));
            if (name == "toFixed")
                return (Func<IReadOnlyList<object?>, object?>)(args =>
                {
                    var digits = args.Count > 0 ? Math.Clamp((int)Number(args[0]), 0, 100) : 0;
                    return Number(target).ToString($"F{digits}", CultureInfo.InvariantCulture);
                });
            if (name == "toLocaleString")
                return (Func<IReadOnlyList<object?>, object?>)(args =>
                {
                    var number = Number(target);
                    return number.ToString(number == Math.Truncate(number) ? "N0" : "N", CultureInfo.InvariantCulture);
                });
        }
        if (target is string stringValue && TryStringMethod(stringValue, name, out var stringMethod)) return stringMethod;
        if (target is RegexValue regex && regex.TryGetProperty(name, out var regexMethod)) return regexMethod;
        if (target is NativeJavaScriptEngine.NativeWeakMap weakMap && weakMap.TryGetProperty(name, out var weakMapMethod)) return weakMapMethod;
        if (target is NativeJavaScriptEngine.NativeWeakSet weakSet && weakSet.TryGetProperty(name, out var weakSetMethod)) return weakSetMethod;
        if (target is NativeJavaScriptEngine.NativeSet set && set.TryGetProperty(name, out var setMethod)) return setMethod;
        if (target is NativeJavaScriptEngine.NativeMap map && map.TryGetProperty(name, out var mapMethod)) return mapMethod;
        if (name == "length")
        {
            if (target is IReadOnlyList<object?> array) return (double)array.Count;
            if (target is string text) return (double)text.Length;
        }
        if (target is NativeJavaScriptEngine.INativeBuffer buffer && buffer.TryGetProperty(name, out var bufferValue)) return bufferValue;
        if (target is NativeJavaScriptEngine.INativeHostObject host && host.TryGetValue(name, out var hostValue)) return NativeJavaScriptEngine.ReadProperty(hostValue);
        if (target is NativeJavaScriptEngine.INativeHostObject prototypeHost && prototypeHost.TryGetPrototypeValue(name, out var prototypeValue)) return prototypeValue;
        if (target is Func<IReadOnlyList<object?>, object?> delegateValue)
        {
            var delegateProperties = CallableProperties.GetOrCreateValue(delegateValue);
            if (name == "bind") return (Func<IReadOnlyList<object?>, object?>)(args =>
            {
                var boundReceiver = args.Count > 0 ? args[0] : null;
                var boundArguments = args.Count > 1 ? args.Skip(1).ToArray() : Array.Empty<object?>();
                return (Func<IReadOnlyList<object?>, object?>)(callArgs =>
                    InvokeFunction(delegateValue, boundReceiver, [.. boundArguments, .. callArgs]));
            });
            if (name == "call") return (Func<IReadOnlyList<object?>, object?>)(args => InvokeFunction(delegateValue, args.Count > 0 ? args[0] : null, args.Count > 1 ? args.Skip(1).ToArray() : Array.Empty<object?>()));
            if (name == "apply") return (Func<IReadOnlyList<object?>, object?>)(args => InvokeFunction(delegateValue, args.Count > 0 ? args[0] : null, args.Count > 1 && args[1] is IReadOnlyList<object?> callArgs ? callArgs : Array.Empty<object?>()));
            if (delegateProperties.TryGetValue(name, out var delegateProperty)) return delegateProperty;
            if (name == "prototype") return delegateProperties[name] = new Dictionary<string, object?>(StringComparer.Ordinal);
        }
        if (target is IReadOnlyDictionary<string, object?> properties)
            return PrototypeMemberValue(properties, name, target);
        return UndefinedOrNull();
    }

    private object? PrototypeMemberValue(IReadOnlyDictionary<string, object?> properties, string name, object receiver)
    {
        if (properties.TryGetValue(name, out var value))
        {
            return value switch
            {
                NativeJavaScriptEngine.NativePropertyAccessor accessor => accessor.Get(receiver),
                NativeJavaScriptEngine.NativePropertyGetter getter => getter.Get(),
                NativeJavaScriptEngine.NativeReceiverFunction method => method,
                _ => NativeJavaScriptEngine.ReadProperty(value),
            };
        }
        if (properties.TryGetValue("__proto__", out var parent) && parent is IReadOnlyDictionary<string, object?> parentProperties)
            return PrototypeMemberValue(parentProperties, name, receiver);
        return UndefinedOrNull();
    }

    private object Object(JavaScriptObjectExpression expression)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in expression.Properties)
        {
            if (property.Name == "...")
            {
                var spread = Evaluate(property.Value);
                if (spread is IReadOnlyDictionary<string, object?> spreadDictionary)
                    foreach (var item in spreadDictionary) result[item.Key] = item.Value;
                continue;
            }
            if (property.Name.StartsWith("__folio_getter__", StringComparison.Ordinal))
            {
                result[property.Name[16..]] = new NativeJavaScriptEngine.NativePropertyGetter(() => Evaluate(property.Value) is Func<IReadOnlyList<object?>, object?> getter ? getter([]) : null);
            }
            else if (property.Name.StartsWith("__folio_method__", StringComparison.Ordinal) && property.Value is JavaScriptFunctionExpression method)
            {
                var name = property.Name[16..];
                result[name] = new NativeJavaScriptEngine.NativeReceiverFunction((receiver, args) => ExecuteFunctionWithThis(method, receiver, args));
            }
            else
            {
                result[property.Name] = property.Value is JavaScriptFunctionExpression function
                    ? new NativeJavaScriptEngine.NativeReceiverFunction((receiver, args) => ExecuteFunctionWithThis(function, receiver, args))
                    : Evaluate(property.Value);
            }
        }
        return result;
    }

    private object? ExecuteFunctionWithThis(JavaScriptFunctionExpression function, object? receiver, IReadOnlyList<object?> arguments)
    {
        var local = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["this"] = receiver,
            ["arguments"] = arguments.ToList(),
        };
        var parameterEvaluator = new JavaScriptEvaluator(local, Math.Max(1, _maxSteps - _steps), _bindings, this, _preserveUndefined, _withScopes, _deadlineExceeded);
        for (var i = 0; i < function.Parameters.Count; i++)
        {
            var parameter = function.Parameters[i];
            if (parameter.StartsWith("...", StringComparison.Ordinal))
            {
                BindParameter(local, parameter[3..], arguments.Skip(i).ToArray());
                break;
            }
            var value = i < arguments.Count ? arguments[i] : UndefinedOrNull();
            var defaultValue = function.DefaultValues is { } values && i < values.Count ? values[i] : null;
            if ((i >= arguments.Count || value is UndefinedValue) && defaultValue is not null) value = parameterEvaluator.Evaluate(defaultValue);
            BindParameter(local, parameter, value);
        }
        if (function.IsGenerator)
        {
            var values = new List<object?>();
            new JavaScriptEvaluator(local, Math.Max(1, _maxSteps - _steps), _bindings, this, _preserveUndefined, _withScopes, _deadlineExceeded, values).ExecuteFunctionBody(function.Body);
            return values;
        }
        return new JavaScriptEvaluator(local, Math.Max(1, _maxSteps - _steps), _bindings, this, _preserveUndefined, _withScopes, _deadlineExceeded).ExecuteFunctionBody(function.Body);
    }

    internal sealed class RegexValue
    {
        private readonly Regex _regex;
        internal string Source { get; }
        internal string Flags { get; }
        private RegexValue(Regex regex, string source, string flags) { _regex = regex; Source = source; Flags = flags; }
        internal static RegexValue Create(string source)
        {
            var slash = source.LastIndexOf('/');
            var pattern = slash > 0 ? source[1..slash] : source;
            var flags = slash > 0 ? source[(slash + 1)..] : string.Empty;
            var options = flags.Contains('i') ? RegexOptions.IgnoreCase : RegexOptions.None;
            return new RegexValue(new Regex(pattern, options, TimeSpan.FromMilliseconds(100)), pattern, flags);
        }
        internal bool TryGetProperty(string name, out object? value)
        {
            value = name switch
            {
                "test" => (Func<IReadOnlyList<object?>, object?>)(args => args.Count > 0 && _regex.IsMatch(StringValue(args[0]))),
                "exec" => (Func<IReadOnlyList<object?>, object?>)(args =>
                {
                    if (args.Count == 0) return null;
                    var match = _regex.Match(StringValue(args[0]));
                    return match.Success ? match.Groups.Cast<Group>().Select(group => (object?)group.Value).ToArray() : null;
                }),
                _ => null,
            };
            return value is not null;
        }
        internal object? Match(string input)
        {
            if (Flags.Contains('g'))
                return _regex.Matches(input).Cast<Match>().Select(match => (object?)match.Value).ToArray();
            var match = _regex.Match(input);
            return match.Success ? match.Groups.Cast<Group>().Select(group => (object?)group.Value).ToArray() : null;
        }
        internal string Replace(string input, Func<string, string> replacement, bool all) => all ? _regex.Replace(input, match => replacement(match.Value)) : _regex.Replace(input, match => replacement(match.Value), 1);
        internal string Replace(string input, string replacement, bool all) => all ? _regex.Replace(input, replacement) : _regex.Replace(input, replacement, 1);
        internal string[] Split(string input) => _regex.Split(input);
    }

    private static bool TryArrayMethod(IList<object?> values, string name, out object? method)
    {
        method = name switch
        {
            "join" => (Func<IReadOnlyList<object?>, object?>)(args => string.Join(args.Count > 0 ? StringValue(args[0]) : ",", values.Select(value => value is null or JavaScriptEvaluator.UndefinedValue ? string.Empty : StringValue(value)))),
            "indexOf" => (Func<IReadOnlyList<object?>, object?>)(args =>
            {
                if (args.Count == 0) return -1d;
                var start = RelativeArrayIndex(args.Count > 1 ? args[1] : null, values.Count, 0);
                return (double)Enumerable.Range(start, Math.Max(0, values.Count - start)).FirstOrDefault(index => Equals(values[index], args[0]), -1);
            }),
            "at" => (Func<IReadOnlyList<object?>, object?>)(args =>
            {
                var index = args.Count == 0 ? 0 : (int)Number(args[0]);
                if (index < 0) index += values.Count;
                return index < 0 || index >= values.Count ? JavaScriptEvaluator.UndefinedValue.Instance : values[index];
            }),
            "includes" => (Func<IReadOnlyList<object?>, object?>)(args =>
            {
                if (args.Count == 0) return values.Count > 0 && values.Any(item => item is null or JavaScriptEvaluator.UndefinedValue);
                var start = RelativeArrayIndex(args.Count > 1 ? args[1] : null, values.Count, 0);
                return values.Skip(start).Any(item => SameValueZero(item, args[0]));
            }),
            "push" => (Func<IReadOnlyList<object?>, object?>)(args => { foreach (var value in args) values.Add(value); return (double)values.Count; }),
            "pop" => (Func<IReadOnlyList<object?>, object?>)(_ => values.Count == 0 ? null : Pop(values)),
            "shift" => (Func<IReadOnlyList<object?>, object?>)(_ => values.Count == 0 ? null : Shift(values)),
            "unshift" => (Func<IReadOnlyList<object?>, object?>)(args => { for (var i = args.Count - 1; i >= 0; i--) values.Insert(0, args[i]); return (double)values.Count; }),
            "splice" => (Func<IReadOnlyList<object?>, object?>)(args => Splice(values, args)),
            "map" => (Func<IReadOnlyList<object?>, object?>)(args => Map(values, args, filter: false)),
            "flatMap" => (Func<IReadOnlyList<object?>, object?>)(args => FlatMap(values, args)),
            "flat" => (Func<IReadOnlyList<object?>, object?>)(args =>
            {
                var depth = args.Count == 0 ? 1 : Number(args[0]);
                return Flatten(values, double.IsPositiveInfinity(depth) ? int.MaxValue : Math.Max(0, (int)depth));
            }),
            "filter" => (Func<IReadOnlyList<object?>, object?>)(args => Map(values, args, filter: true)),
            "forEach" => (Func<IReadOnlyList<object?>, object?>)(args => { _ = Map(values, args, filter: false, discard: true); return null; }),
            "reduce" => (Func<IReadOnlyList<object?>, object?>)(args => Reduce(values, args)),
            "reduceRight" => (Func<IReadOnlyList<object?>, object?>)(args => ReduceRight(values, args)),
            "find" => (Func<IReadOnlyList<object?>, object?>)(args => Find(values, args, mode: "find")),
            "findIndex" => (Func<IReadOnlyList<object?>, object?>)(args => Find(values, args, mode: "findIndex")),
            "findLast" => (Func<IReadOnlyList<object?>, object?>)(args => Find(values, args, mode: "findLast")),
            "findLastIndex" => (Func<IReadOnlyList<object?>, object?>)(args => Find(values, args, mode: "findLastIndex")),
            "some" => (Func<IReadOnlyList<object?>, object?>)(args => Find(values, args, mode: "some")),
            "every" => (Func<IReadOnlyList<object?>, object?>)(args => Find(values, args, mode: "every")),
            "sort" => (Func<IReadOnlyList<object?>, object?>)(args =>
            {
                // Array.prototype.sort mutates the receiver and returns it.  Keep the
                // comparison bounded and deterministic for the native engine; this is
                // also required by the conformance corpus' interactive table artifact.
                Comparison<object?> comparison = (left, right) =>
                {
                    if (args.Count > 0 && NativeJavaScriptEngine.IsCallable(args[0]))
                    {
                        var result = NativeJavaScriptEngine.InvokeCallable(args[0], [left, right]);
                        var number = result switch
                        {
                            double value => value,
                            int value => value,
                            long value => value,
                            _ when double.TryParse(StringValue(result), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
                            _ => 0,
                        };
                        return double.IsNaN(number) ? 0 : Math.Clamp((int)Math.Sign(number), -1, 1);
                    }

                    return string.Compare(StringValue(left), StringValue(right), StringComparison.Ordinal);
                };
                var sorted = values.ToList();
                sorted.Sort(comparison);
                for (var i = 0; i < sorted.Count; i++) values[i] = sorted[i];
                return values;
            }),
            "slice" => (Func<IReadOnlyList<object?>, object?>)(args =>
            {
                var start = RelativeArrayIndex(args.Count > 0 ? args[0] : null, values.Count, 0);
                var end = RelativeArrayIndex(args.Count > 1 ? args[1] : null, values.Count, values.Count);
                if (end < start) end = start;
                return values.Skip(start).Take(end - start).ToArray();
            }),
            "concat" => (Func<IReadOnlyList<object?>, object?>)(args =>
            {
                var result = new List<object?>(values);
                foreach (var arg in args)
                    if (arg is IReadOnlyList<object?> items) result.AddRange(items); else result.Add(arg);
                return result.ToArray();
            }),
            _ => null,
        };
        return method is not null;
    }

    internal static int RelativeArrayIndex(object? value, int length, int defaultIndex)
    {
        if (value is null or JavaScriptEvaluator.UndefinedValue) return defaultIndex;
        var number = Number(value);
        if (double.IsNaN(number)) return 0;
        if (double.IsNegativeInfinity(number)) return 0;
        if (double.IsPositiveInfinity(number)) return length;
        var integer = (int)Math.Truncate(number);
        return integer < 0 ? Math.Max(length + integer, 0) : Math.Min(integer, length);
    }

    private static bool SameValueZero(object? left, object? right) =>
        left is double leftNumber && right is double rightNumber
            ? leftNumber == rightNumber || (double.IsNaN(leftNumber) && double.IsNaN(rightNumber))
            : Equals(left, right);

    private static object? Pop(IList<object?> values)
    {
        var index = values.Count - 1;
        var value = values[index];
        values.RemoveAt(index);
        return value;
    }

    private static object? Shift(IList<object?> values)
    {
        var value = values[0];
        values.RemoveAt(0);
        return value;
    }

    private static object?[] Splice(IList<object?> values, IReadOnlyList<object?> args)
    {
        var start = args.Count > 0 ? Math.Clamp((int)Number(args[0]), 0, values.Count) : 0;
        var deleteCount = args.Count > 1 ? Math.Clamp((int)Number(args[1]), 0, values.Count - start) : values.Count - start;
        var removed = values.Skip(start).Take(deleteCount).ToArray();
        for (var i = 0; i < deleteCount; i++) values.RemoveAt(start);
        for (var i = 2; i < args.Count; i++) values.Insert(start + i - 2, args[i]);
        return removed;
    }

    private static object? Map(IList<object?> values, IReadOnlyList<object?> args, bool filter, bool discard = false)
    {
        if (args.Count == 0 || !IsCallable(args[0])) return Array.Empty<object?>();
        var result = new List<object?>();
        for (var i = 0; i < values.Count; i++)
        {
            TryInvoke(args[0], [values[i], (double)i, values], out var mapped);
            if (!filter || Truth(mapped)) result.Add(filter ? values[i] : mapped);
        }
        return discard ? Array.Empty<object?>() : result.ToList();
    }

    private static object?[] FlatMap(IList<object?> values, IReadOnlyList<object?> args)
    {
        if (args.Count == 0 || !IsCallable(args[0])) return [];
        var result = new List<object?>();
        for (var i = 0; i < values.Count; i++)
        {
            TryInvoke(args[0], [values[i], (double)i, values], out var mapped);
            if (mapped is IEnumerable<object?> nested) result.AddRange(nested);
            else result.Add(mapped);
        }
        return result.ToArray();
    }

    private static object?[] Flatten(IEnumerable<object?> values, int depth)
    {
        var result = new List<object?>();
        foreach (var value in values)
        {
            if (depth > 0 && value is IEnumerable<object?> nested)
                result.AddRange(Flatten(nested, depth == int.MaxValue ? int.MaxValue : depth - 1));
            else
                result.Add(value);
        }
        return result.ToArray();
    }

    private static object? Reduce(IList<object?> values, IReadOnlyList<object?> args)
    {
        if (args.Count == 0 || !IsCallable(args[0])) throw WebIdlException.TypeError("Array.prototype.reduce callback must be callable.");
        if (args.Count < 2 && values.Count == 0) throw WebIdlException.TypeError("Reduce of empty array with no initial value.");
        var index = args.Count > 1 ? 0 : 1;
        object? accumulator = args.Count > 1 ? args[1] : values[0];
        for (; index < values.Count; index++) TryInvoke(args[0], [accumulator, values[index], (double)index, values], out accumulator);
        return accumulator;
    }

    private static object? ReduceRight(IList<object?> values, IReadOnlyList<object?> args)
    {
        if (args.Count == 0 || !IsCallable(args[0])) throw WebIdlException.TypeError("Array.prototype.reduceRight callback must be callable.");
        if (args.Count < 2 && values.Count == 0) throw WebIdlException.TypeError("Reduce of empty array with no initial value.");
        var index = args.Count > 1 ? values.Count - 1 : values.Count - 2;
        object? accumulator = args.Count > 1 ? args[1] : values[^1];
        for (; index >= 0; index--) TryInvoke(args[0], [accumulator, values[index], (double)index, values], out accumulator);
        return accumulator;
    }

    private static object? Find(IList<object?> values, IReadOnlyList<object?> args, string mode)
    {
        if (args.Count == 0 || !IsCallable(args[0]))
            return mode == "every";
        var reverse = mode is "findLast" or "findLastIndex";
        var first = reverse ? values.Count - 1 : 0;
        var last = reverse ? -1 : values.Count;
        var step = reverse ? -1 : 1;
        for (var i = first; i != last; i += step)
        {
            TryInvoke(args[0], [values[i], (double)i, values], out var callbackResult);
            var matched = Truth(callbackResult);
            if (mode == "find" && matched) return values[i];
            if (mode == "findIndex" && matched) return (double)i;
            if (mode == "findLast" && matched) return values[i];
            if (mode == "findLastIndex" && matched) return (double)i;
            if (mode == "some" && matched) return true;
            if (mode == "every" && !matched) return false;
        }
        return mode switch { "find" or "findLast" or "findIndex" or "findLastIndex" => mode is "find" or "findLast" ? UndefinedValue.Instance : -1d, "some" => false, _ => true };
    }

    internal static bool IsCallable(object? value) => value is Func<IReadOnlyList<object?>, object?> or NativeJavaScriptEngine.INativeCallable;
    private static bool TryInvoke(object? value, IReadOnlyList<object?> args, out object? result)
    {
        switch (value)
        {
            case Func<IReadOnlyList<object?>, object?> function:
                result = function(args); return true;
            case NativeJavaScriptEngine.INativeCallable callable:
                result = callable.Invoke(args); return true;
            default:
                result = null; return false;
        }
    }

    private static bool TryStringMethod(string value, string name, out object? method)
    {
        method = name switch
        {
            "toUpperCase" => (Func<IReadOnlyList<object?>, object?>)(_ => value.ToUpperInvariant()),
            "toLowerCase" => (Func<IReadOnlyList<object?>, object?>)(_ => value.ToLowerInvariant()),
            "includes" => (Func<IReadOnlyList<object?>, object?>)(args => args.Count > 0 && value.Contains(StringValue(args[0]), StringComparison.Ordinal)),
            "startsWith" => (Func<IReadOnlyList<object?>, object?>)(args => args.Count > 0 && value.StartsWith(StringValue(args[0]), StringComparison.Ordinal)),
            "endsWith" => (Func<IReadOnlyList<object?>, object?>)(args => args.Count > 0 && value.EndsWith(StringValue(args[0]), StringComparison.Ordinal)),
            "localeCompare" => (Func<IReadOnlyList<object?>, object?>)(args => (double)string.Compare(value, args.Count > 0 ? StringValue(args[0]) : string.Empty, StringComparison.CurrentCulture)),
            "replace" => (Func<IReadOnlyList<object?>, object?>)(args =>
            {
                if (args.Count < 2) return value;
                if (args[0] is RegexValue regex)
                    return IsCallable(args[1])
                        ? regex.Replace(value, match => StringValue(NativeJavaScriptEngine.InvokeCallable(args[1], [match])), regex.Flags.Contains('g'))
                        : regex.Replace(value, StringValue(args[1]), regex.Flags.Contains('g'));
                if (IsCallable(args[1]))
                {
                    var needle = StringValue(args[0]);
                    var index = value.IndexOf(needle, StringComparison.Ordinal);
                    return index < 0 ? value : value[..index] + StringValue(NativeJavaScriptEngine.InvokeCallable(args[1], [needle])) + value[(index + needle.Length)..];
                }
                var literalNeedle = StringValue(args[0]);
                var position = value.IndexOf(literalNeedle, StringComparison.Ordinal);
                return position < 0
                    ? value
                    : value[..position] + StringValue(args[1]) + value[(position + literalNeedle.Length)..];
            }),
            "replaceAll" => (Func<IReadOnlyList<object?>, object?>)(args =>
            {
                if (args.Count < 2) return value;
                if (args[0] is RegexValue regex)
                {
                    return regex.Replace(value, StringValue(args[1]), all: true);
                }
                return value.Replace(StringValue(args[0]), StringValue(args[1]), StringComparison.Ordinal);
            }),
            "match" => (Func<IReadOnlyList<object?>, object?>)(args =>
            {
                if (args.Count == 0) return null;
                if (args[0] is RegexValue regex) return regex.Match(value);
                var needle = StringValue(args[0]);
                var index = value.IndexOf(needle, StringComparison.Ordinal);
                return index < 0 ? null : new object?[] { needle };
            }),
            "split" => (Func<IReadOnlyList<object?>, object?>)(args => args.Count == 0
                ? new List<object?> { value }
                : args[0] is RegexValue regex
                    ? regex.Split(value).Cast<object?>().ToList()
                    : value.Split(StringValue(args[0]), StringSplitOptions.None).Cast<object?>().ToList()),
            "trim" => (Func<IReadOnlyList<object?>, object?>)(_ => value.Trim()),
            "trimStart" => (Func<IReadOnlyList<object?>, object?>)(_ => value.TrimStart()),
            "trimEnd" => (Func<IReadOnlyList<object?>, object?>)(_ => value.TrimEnd()),
            "padStart" => (Func<IReadOnlyList<object?>, object?>)(args =>
            {
                var target = args.Count == 0 ? value.Length : Math.Max(0, (int)Number(args[0]));
                if (target <= value.Length) return value;
                var fill = args.Count > 1 ? StringValue(args[1]) : " ";
                if (fill.Length == 0) return value;
                return string.Concat(string.Concat(Enumerable.Repeat(fill, (target - value.Length + fill.Length - 1) / fill.Length))[..(target - value.Length)], value);
            }),
            "padEnd" => (Func<IReadOnlyList<object?>, object?>)(args =>
            {
                var target = args.Count == 0 ? value.Length : Math.Max(0, (int)Number(args[0]));
                if (target <= value.Length) return value;
                var fill = args.Count > 1 ? StringValue(args[1]) : " ";
                if (fill.Length == 0) return value;
                return value + string.Concat(Enumerable.Repeat(fill, (target - value.Length + fill.Length - 1) / fill.Length))[..(target - value.Length)];
            }),
            "repeat" => (Func<IReadOnlyList<object?>, object?>)(args =>
            {
                var count = args.Count == 0 ? 0 : (int)Number(args[0]);
                if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), "Invalid count value.");
                return string.Concat(Enumerable.Repeat(value, count));
            }),
            "indexOf" => (Func<IReadOnlyList<object?>, object?>)(args => args.Count == 0 ? -1d : (double)value.IndexOf(StringValue(args[0]), StringComparison.Ordinal)),
            "at" => (Func<IReadOnlyList<object?>, object?>)(args =>
            {
                var index = args.Count == 0 ? 0 : (int)Number(args[0]);
                if (index < 0) index += value.Length;
                return index < 0 || index >= value.Length ? string.Empty : value[index].ToString();
            }),
            "charAt" => (Func<IReadOnlyList<object?>, object?>)(args => args.Count == 0 || (int)Number(args[0]) < 0 || (int)Number(args[0]) >= value.Length ? string.Empty : value[(int)Number(args[0])].ToString()),
            "charCodeAt" => (Func<IReadOnlyList<object?>, object?>)(args => args.Count == 0 || (int)Number(args[0]) < 0 || (int)Number(args[0]) >= value.Length ? double.NaN : (double)value[(int)Number(args[0])]),
            "slice" => (Func<IReadOnlyList<object?>, object?>)(args =>
            {
                var start = RelativeStringIndex(args.Count > 0 ? args[0] : null, value.Length, 0);
                var end = RelativeStringIndex(args.Count > 1 ? args[1] : null, value.Length, value.Length);
                return value[start..Math.Max(start, end)];
            }),
            "substring" => (Func<IReadOnlyList<object?>, object?>)(args =>
            {
                var start = ClampStringIndex(args.Count > 0 ? args[0] : null, value.Length);
                var end = ClampStringIndex(args.Count > 1 ? args[1] : null, value.Length, value.Length);
                if (start > end) (start, end) = (end, start);
                return value[start..end];
            }),
            _ => null,
        };
        return method is not null;
    }

    private static int RelativeStringIndex(object? value, int length, int fallback)
    {
        var index = value is null or UndefinedValue ? fallback : (int)Number(value);
        return index < 0 ? Math.Max(length + index, 0) : Math.Min(index, length);
    }

    private static int ClampStringIndex(object? value, int length, int fallback = 0) => Math.Clamp(value is null or UndefinedValue ? fallback : (int)Number(value), 0, length);

    private object Function(JavaScriptFunctionExpression function)
    {
        var captured = _bindings;
        var capturedWithScopes = _withScopes.ToArray();
        Func<IReadOnlyList<object?>, object?>? functionValue = null;
        functionValue = arguments => InvokeBody(null, arguments);
        ReceiverBindings.Add(functionValue, new ReceiverBinding((receiver, arguments) => InvokeBody(receiver, arguments)));
        return functionValue;

        object? InvokeBody(object? receiver, IReadOnlyList<object?> arguments)
        {
            var local = new Dictionary<string, object?>(StringComparer.Ordinal);
            var parameterEvaluator = new JavaScriptEvaluator(local, Math.Max(1, _maxSteps - _steps), captured, this, _preserveUndefined, capturedWithScopes, _deadlineExceeded);
            if (function.Name is { } functionName) local[functionName] = functionValue;
            for (var i = 0; i < function.Parameters.Count; i++)
            {
                var parameter = function.Parameters[i];
                if (parameter.StartsWith("...", StringComparison.Ordinal))
                {
                    BindParameter(local, parameter[3..], arguments.Skip(i).ToArray());
                    break;
                }
                var value = i < arguments.Count ? arguments[i] : UndefinedOrNull();
                var defaultValue = function.DefaultValues is { } values && i < values.Count ? values[i] : null;
                if ((i >= arguments.Count || value is UndefinedValue) && defaultValue is not null) value = parameterEvaluator.Evaluate(defaultValue);
                BindParameter(local, parameter, value);
            }
            // Non-strict top-level/function calls receive the global object when
            // invoked without an explicit receiver. UMD libraries rely on this
            // for their `(function(){ ... })(this)` wrapper; method calls still
            // supply their actual receiver through ReceiverBindings.
            local["this"] = receiver ?? captured;
            local["arguments"] = arguments.ToList();
            if (function.IsGenerator) return RunGenerator(local, function.Body);
            return new JavaScriptEvaluator(local, Math.Max(1, _maxSteps - _steps), captured, this, _preserveUndefined, capturedWithScopes, _deadlineExceeded).ExecuteFunctionBody(function.Body);
        }

        List<object?> RunGenerator(IDictionary<string, object?> local, IReadOnlyList<JavaScriptStatement> body)
        {
            var values = new List<object?>();
            new JavaScriptEvaluator(local, Math.Max(1, _maxSteps - _steps), captured, this, _preserveUndefined, capturedWithScopes, _deadlineExceeded, values).ExecuteFunctionBody(body);
            return values;
        }
    }

    private void BindParameter(IDictionary<string, object?> local, string parameter, object? value)
    {
        if (parameter.StartsWith("{", StringComparison.Ordinal) && parameter.EndsWith("}", StringComparison.Ordinal))
        {
            foreach (var entry in parameter[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = entry.Split(':', 2);
                local[parts.Length == 2 ? parts[1] : parts[0]] = MemberValue(value, parts[0]);
            }
            return;
        }
        if (parameter.StartsWith("[", StringComparison.Ordinal) && parameter.EndsWith("]", StringComparison.Ordinal))
        {
            var values = value as IReadOnlyList<object?>;
            var names = parameter[1..^1].Split(',', StringSplitOptions.None);
            for (var i = 0; i < names.Length; i++) if (!string.IsNullOrEmpty(names[i])) local[names[i]] = values is not null && i < values.Count ? values[i] : UndefinedOrNull();
            return;
        }
        local[parameter] = value;
    }

    internal static object? InvokeWithReceiver(object function, object? receiver, IReadOnlyList<object?> arguments) =>
        function is Func<IReadOnlyList<object?>, object?> callback ? InvokeFunction(callback, receiver, arguments) :
        function is NativeJavaScriptEngine.NativeReceiverFunction receiverCallable ? receiverCallable.Invoke(receiver, arguments) :
        function is NativeJavaScriptEngine.INativeCallable callable ? callable.Invoke(arguments) : null;

    private static object? InvokeFunction(Func<IReadOnlyList<object?>, object?> function, object? receiver, IReadOnlyList<object?> arguments)
        => ReceiverBindings.TryGetValue(function, out var binding) ? binding.Invoke(receiver, arguments) : function(arguments);

    private NativeJavaScriptEngine.NativeFunction Class(JavaScriptClassExpression @class)
    {
        var constructor = @class.Methods.FirstOrDefault(method => method.Name == "constructor" && !method.Static)?.Function;
        var methods = @class.Methods.Where(method => method.Name != "constructor" && !method.Static).ToArray();
        var prototype = new Dictionary<string, object?>(StringComparer.Ordinal);
        var type = new NativeJavaScriptEngine.NativeFunction(arguments =>
        {
            var instance = new Dictionary<string, object?>(StringComparer.Ordinal) { ["__constructor"] = @class.Name ?? "class" };
            instance["__proto__"] = prototype;
            foreach (var method in methods)
            {
                if (!method.Getter && !method.Setter) instance[method.Name] = (Func<IReadOnlyList<object?>, object?>)(args => ExecuteClassMethod(method.Function, instance, args));
            }
            if (constructor is not null)
                _ = ExecuteClassMethod(constructor, instance, arguments, @class.Parent);
            else if (@class.Parent is not null)
                // JavaScript supplies an implicit super(...arguments) when a derived
                // class omits its own constructor. Preserve that initialization for
                // DOM/CSSOM handle classes, including chains with several constructor-
                // free intermediate classes (CSSStyleRule -> CSSGroupingRule -> CSSRule).
                InitializeInheritedConstructor(@class.Parent, instance, arguments);
            foreach (var method in methods.Where(method => method.Getter))
                instance[method.Name] = new NativeJavaScriptEngine.NativePropertyAccessor(
                    receiver => ExecuteClassMethod(method.Function, receiver ?? instance, []),
                    null);
            return instance;
        });
        // Class instances carry their source-level name as a marker. Preserve the
        // same name on the constructor so `instanceof` can identify native class
        // instances (DOM Event subclasses and React's host checks rely on this).
        type.SetProperty("name", @class.Name ?? "class");
        type.SetProperty("prototype", prototype);
        prototype["constructor"] = type;
        if (@class.Parent is not null && _bindings.TryGetValue(@class.Parent, out var parentType) && parentType is NativeJavaScriptEngine.NativeFunction parentCtor && parentCtor.TryGetProperty("prototype", out var parentPrototype))
            prototype["__proto__"] = parentPrototype;
        foreach (var method in methods)
            if (!method.Getter && !method.Setter)
                prototype[method.Name] = new NativeJavaScriptEngine.NativeReceiverFunction((receiver, args) =>
                    ExecuteClassMethod(method.Function, receiver ?? new Dictionary<string, object?>(), args));
        if (constructor is not null) type.SetProperty("__classConstructor", constructor);
        if (@class.Parent is not null) type.SetProperty("__classParent", @class.Parent);
        foreach (var method in methods.Where(method => method.Getter))
            prototype[method.Name] = new NativeJavaScriptEngine.NativePropertyAccessor(
                receiver => ExecuteClassMethod(method.Function, receiver ?? new Dictionary<string, object?>(), []),
                null);
        foreach (var method in @class.Methods.Where(method => method.Static && !method.Getter && !method.Setter))
            type.SetProperty(method.Name, (Func<IReadOnlyList<object?>, object?>)(args => ExecuteClassMethod(method.Function, new Dictionary<string, object?> { ["__constructor"] = @class.Name ?? "class" }, args)));
        foreach (var method in @class.Methods.Where(method => method.Static && method.Getter))
            type.SetProperty(method.Name, ExecuteClassMethod(method.Function, new Dictionary<string, object?> { ["__constructor"] = @class.Name ?? "class" }, []));
        return type;
    }

    private void InitializeInheritedConstructor(string parentName, object instance, IReadOnlyList<object?> arguments)
    {
        if (!_bindings.TryGetValue(parentName, out var parent)
            || parent is not NativeJavaScriptEngine.NativeFunction constructor)
            return;
        if (constructor.TryGetProperty("__classConstructor", out var body) && body is JavaScriptFunctionExpression function)
        {
            _ = ExecuteClassMethod(function, instance, arguments, parentName);
            return;
        }
        if (constructor.TryGetProperty("__classParent", out var grandParent) && grandParent is string grandParentName)
            InitializeInheritedConstructor(grandParentName, instance, arguments);
    }

    private object? ExecuteClassMethod(JavaScriptFunctionExpression function, object instance, IReadOnlyList<object?> arguments, string? parentName = null)
    {
        var local = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["this"] = instance,
            ["super"] = (Func<IReadOnlyList<object?>, object?>)(args =>
            {
                if (parentName is null || !_bindings.TryGetValue(parentName, out var parent) || parent is not NativeJavaScriptEngine.NativeFunction parentCtor || !parentCtor.TryGetProperty("__classConstructor", out var parentFunction) || parentFunction is not JavaScriptFunctionExpression parentBody)
                    return null;
                var inheritedParent = parentCtor.TryGetProperty("__classParent", out var inherited) ? inherited as string : null;
                return ExecuteClassMethod(parentBody, instance, args, inheritedParent);
            }),
            ["arguments"] = arguments.ToList(),
        };
        var parameterEvaluator = new JavaScriptEvaluator(local, Math.Max(1, _maxSteps - _steps), _bindings, this, _preserveUndefined, _withScopes, _deadlineExceeded);
            for (var i = 0; i < function.Parameters.Count; i++)
            {
                var parameter = function.Parameters[i];
                if (parameter.StartsWith("...", StringComparison.Ordinal))
                {
                    BindParameter(local, parameter[3..], arguments.Skip(i).ToArray());
                    break;
                }
                var value = i < arguments.Count ? arguments[i] : UndefinedOrNull();
                var defaultValue = function.DefaultValues is { } values && i < values.Count ? values[i] : null;
                if ((i >= arguments.Count || value is UndefinedValue) && defaultValue is not null) value = parameterEvaluator.Evaluate(defaultValue);
                BindParameter(local, parameter, value);
            }
        return new JavaScriptEvaluator(local, Math.Max(1, _maxSteps - _steps), _bindings, this, _preserveUndefined, _withScopes, _deadlineExceeded).ExecuteFunctionBody(function.Body);
    }

    private void ThrowIfExpired()
    {
        if (_deadlineExceeded?.Invoke() == true)
            throw new ScriptLimitExceededException("Timeout", "The native script exceeded its execution time limit.");
    }

    private object? Lookup(string name) => TryLookup(name, out var value) ? value : UndefinedOrNull();
    private bool TryLookup(string name, out object? value)
    {
        for (var i = _withScopes.Count - 1; i >= 0; i--)
            if (_withScopes[i].TryGetValue(name, out value)) return true;
        if (_bindings.TryGetValue(name, out value)) return true;
        if (_parentBindings is not null && _parentBindings.TryGetValue(name, out value)) return true;
        return _parentEvaluator is not null && _parentEvaluator.TryLookup(name, out value);
    }
    private void Assign(string name, object? value)
    {
        for (var i = _withScopes.Count - 1; i >= 0; i--)
            if (_withScopes[i].ContainsKey(name)) { SetBinding(_withScopes[i], name, value); return; }
        if (_bindings.ContainsKey(name) || _parentBindings is null) SetBinding(_bindings, name, value);
        else if (_parentBindings.ContainsKey(name)) SetBinding(_parentBindings, name, value);
        else if (_parentEvaluator is not null) _parentEvaluator.Assign(name, value);
        else SetBinding(_bindings, name, value);
    }

    private static void SetBinding(IDictionary<string, object?> bindings, string name, object? value)
    {
        if (bindings.TryGetValue(name, out var existing)
            && existing is NativeJavaScriptEngine.NativePropertyAccessor accessor)
        {
            if (!accessor.Set(bindings, value))
                throw new InvalidDataException($"Property '{name}' is read-only.");
            return;
        }
        bindings[name] = value;
    }

    internal static bool Truth(object? value) => value switch { null or UndefinedValue => false, bool boolean => boolean, double number => number != 0 && !double.IsNaN(number), string text => text.Length != 0, _ => true };
    private static double Number(object? value) => value switch { null => 0, UndefinedValue => double.NaN, bool boolean => boolean ? 1 : 0, double number => number, int integer => integer, string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number, _ => double.NaN };
    private static int ToInt32(double value) => unchecked((int)value);

    private static bool? JavaScriptNullishEquals(object? left, object? right)
    {
        var leftUndefined = left is UndefinedValue;
        var rightUndefined = right is UndefinedValue;
        if (leftUndefined || rightUndefined || left is null || right is null)
            return (leftUndefined || left is null) && (rightUndefined || right is null);
        return null;
    }

    private static string StringValue(object? value) => value switch { null => "null", UndefinedValue => "undefined", bool boolean => boolean ? "true" : "false", double number => number.ToString("G15", CultureInfo.InvariantCulture), _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty };
    private object? UndefinedOrNull() => _preserveUndefined ? UndefinedValue.Instance : null;

    internal sealed class UndefinedValue
    {
        internal static readonly UndefinedValue Instance = new();
        private UndefinedValue() { }
    }

    private sealed class BreakSignal(string? label) : Exception
    {
        internal string? Label { get; } = label;
    }
    private sealed class ContinueSignal(string? label) : Exception
    {
        internal string? Label { get; } = label;
    }
    private sealed class ReturnSignal(object? value) : Exception
    {
        internal object? Value { get; } = value;
    }
    private sealed class JavaScriptThrownException : Exception
    {
        internal JavaScriptThrownException(object? value) : base(FormatMessage(value)) { Value = value; }
        internal object? Value { get; }
        private static string FormatMessage(object? value) => value is IReadOnlyDictionary<string, object?> properties
            && properties.TryGetValue("name", out var name) && properties.TryGetValue("message", out var message)
            ? $"JavaScript throw: {name}: {message}"
            : "JavaScript throw: " + (value?.GetType().FullName ?? "null");
    }
}
