using System.Globalization;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;

namespace Folio.Scripting.Native;

/// <summary>Bounded evaluator for the expression AST; the full VM will replace this staged implementation.</summary>
internal sealed class JavaScriptEvaluator
{
    private static readonly ConditionalWeakTable<object, Dictionary<string, object?>> CallableProperties = new();
    private readonly IDictionary<string, object?> _bindings;
    private readonly IDictionary<string, object?>? _parentBindings;
    private readonly JavaScriptEvaluator? _parentEvaluator;
    private readonly IReadOnlyList<IDictionary<string, object?>> _withScopes;
    private readonly int _maxSteps;
    private readonly bool _preserveUndefined;
    private readonly Func<bool>? _deadlineExceeded;
    private int _steps;

    internal JavaScriptEvaluator(IDictionary<string, object?>? bindings = null, int maxSteps = 100_000, IDictionary<string, object?>? parentBindings = null, JavaScriptEvaluator? parentEvaluator = null, bool preserveUndefined = false, IReadOnlyList<IDictionary<string, object?>>? withScopes = null, Func<bool>? deadlineExceeded = null)
    {
        _bindings = bindings ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        _parentBindings = parentBindings;
        _parentEvaluator = parentEvaluator;
        _preserveUndefined = preserveUndefined;
        _withScopes = withScopes ?? Array.Empty<IDictionary<string, object?>>();
        _deadlineExceeded = deadlineExceeded ?? parentEvaluator?._deadlineExceeded;
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
            JavaScriptRegexExpression regex => RegexValue.Create(regex.Text),
            JavaScriptIdentifierExpression identifier => Lookup(identifier.Name),
            JavaScriptUnaryExpression unary => Unary(unary),
            JavaScriptUpdateExpression update => Update(update),
            JavaScriptBinaryExpression binary => Binary(binary),
            JavaScriptConditionalExpression conditional => Truth(Evaluate(conditional.Condition)) ? Evaluate(conditional.WhenTrue) : Evaluate(conditional.WhenFalse),
            JavaScriptMemberExpression member => Member(member),
            JavaScriptCallExpression call => Call(call),
            JavaScriptArrayExpression array => array.Elements.Select(Evaluate).ToList(),
            JavaScriptIndexExpression index => Index(index),
            JavaScriptObjectExpression objectExpression => Object(objectExpression),
            JavaScriptFunctionExpression function => Function(function),
            JavaScriptClassExpression @class => Class(@class),
            _ => throw new InvalidDataException("Unknown JavaScript expression node."),
        };
    }

    internal object? Execute(IReadOnlyList<JavaScriptStatement> statements)
    {
        try { return ExecuteCore(statements); }
        catch (ReturnSignal returned) { return returned.Value; }
    }

    private object? ExecuteCore(IReadOnlyList<JavaScriptStatement> statements)
    {
        ThrowIfExpired();
        object? lastResult = UndefinedValue.Instance;
        foreach (var statement in statements)
        {
            ThrowIfExpired();
            if (++_steps > _maxSteps) throw new InvalidOperationException("The script exceeded its evaluation step limit.");
            switch (statement)
            {
                case JavaScriptExpressionStatement expression: lastResult = Evaluate(expression.Expression); break;
                case JavaScriptVariableStatement variable: _bindings[variable.Name] = variable.Initializer is null ? null : Evaluate(variable.Initializer); break;
                case JavaScriptFunctionDeclarationStatement function: _bindings[function.Name] = Evaluate(function.Function); break;
                case JavaScriptClassDeclarationStatement @class: _bindings[@class.Name] = Evaluate(@class.Class); break;
                case JavaScriptBlockStatement block:
                    ExecuteCore(block.Statements);
                    break;
                case JavaScriptWithStatement with:
                    var scope = Evaluate(with.Scope);
                    if (scope is not IDictionary<string, object?> properties)
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
                            catch (ContinueSignal) { }
                        }
                    }
                    catch (BreakSignal) { }
                    break;
                case JavaScriptDoWhileStatement loop:
                    try
                    {
                        do
                        {
                            if (++_steps > _maxSteps) throw new InvalidOperationException("The script exceeded its evaluation step limit.");
                            try { ExecuteCore([loop.Body]); }
                            catch (ContinueSignal) { }
                        }
                        while (Truth(Evaluate(loop.Condition)));
                    }
                    catch (BreakSignal) { }
                    break;
                case JavaScriptBreakStatement: throw new BreakSignal();
                case JavaScriptContinueStatement: throw new ContinueSignal();
                case JavaScriptForStatement loop:
                    if (loop.Initializer is not null) ExecuteCore([loop.Initializer]);
                    try
                    {
                        while (loop.Condition is null || Truth(Evaluate(loop.Condition)))
                        {
                            if (++_steps > _maxSteps) throw new InvalidOperationException("The script exceeded its evaluation step limit.");
                            try { ExecuteCore([loop.Body]); }
                            catch (ContinueSignal) { }
                            if (loop.Update is not null) Evaluate(loop.Update);
                        }
                    }
                    catch (BreakSignal) { }
                    break;
                case JavaScriptForEachStatement each:
                    var source = Evaluate(each.Iterable);
                    var items = each.Keys && source is IReadOnlyDictionary<string, object?> dictionary
                        ? dictionary.Keys.Cast<object?>().ToArray()
                        : source is IReadOnlyList<object?> array ? (each.Keys ? Enumerable.Range(0, array.Count).Cast<object?>().ToArray() : array.ToArray()) : Array.Empty<object?>();
                    try
                    {
                        foreach (var item in items)
                        {
                            if (++_steps > _maxSteps) throw new InvalidOperationException("The script exceeded its evaluation step limit.");
                            if (each.Name.StartsWith("[", StringComparison.Ordinal) && each.Name.EndsWith("]", StringComparison.Ordinal))
                            {
                                var names = each.Name[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries);
                                var values = item as IReadOnlyList<object?>;
                                for (var index = 0; index < names.Length; index++) _bindings[names[index]] = values is not null && index < values.Count ? values[index] : null;
                            }
                            else _bindings[each.Name] = item;
                            try { ExecuteCore([each.Body]); }
                            catch (ContinueSignal) { }
                        }
                    }
                    catch (BreakSignal) { }
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
                    catch (BreakSignal) { }
                    break;
                case JavaScriptReturnStatement returned: throw new ReturnSignal(returned.Expression is null ? UndefinedOrNull() : Evaluate(returned.Expression));
                default: throw new InvalidDataException("Unknown JavaScript statement node.");
            }
        }
        return lastResult is UndefinedValue ? UndefinedOrNull() : lastResult;
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
            return Evaluate(unary.Operand);
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
        var constructor = call.Callee is JavaScriptIdentifierExpression identifier ? Lookup(identifier.Name) : Evaluate(call.Callee);
        var arguments = call.Arguments.Select(Evaluate).ToArray();
        var instance = new Dictionary<string, object?>(StringComparer.Ordinal) { ["__constructor"] = constructor };
        object? result = constructor switch
        {
            NativeJavaScriptEngine.NativeReceiverFunction receiverFunction => receiverFunction.Invoke(instance, arguments),
            NativeJavaScriptEngine.INativeCallable callable => callable.Invoke(arguments),
            Func<IReadOnlyList<object?>, object?> function => function(arguments),
            _ => throw new NotSupportedException($"Constructor is not callable (callee: {call.Callee})."),
        };
        return result is null or UndefinedValue ? instance : result;
    }

    private object? Update(JavaScriptUpdateExpression update)
    {
        var oldValue = Evaluate(update.Operand);
        var next = Number(oldValue) + (update.Operator == "++" ? 1 : -1);
        if (update.Operand is not JavaScriptIdentifierExpression identifier)
            throw new InvalidDataException("Only lexical update assignment is supported.");
        Assign(identifier.Name, next);
        return update.Prefix ? next : oldValue;
    }

    private object? Binary(JavaScriptBinaryExpression binary)
    {
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
            "==" or "===" => Equals(left, right), "!=" or "!==" => !Equals(left, right),
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
                if (!host.TrySetSetValue(member.Name, assigned)) throw new InvalidDataException($"Host property '{member.Name}' is read-only.");
                break;
            case JavaScriptMemberExpression member when Evaluate(member.Target) is IDictionary<string, object?> properties:
                properties[member.Name] = assigned;
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
                indexedProperties[StringValue(Evaluate(index.Index))] = assigned;
                break;
            case JavaScriptIndexExpression index when Evaluate(index.Target) is NativeJavaScriptEngine.INativeBuffer buffer:
                var bufferPosition = (int)Number(Evaluate(index.Index));
                if (!buffer.TrySetIndex(bufferPosition, assigned)) throw new IndexOutOfRangeException();
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

    private static bool IsInstanceOf(object? value, object? constructor) => constructor switch
    {
        "Array" when value is IReadOnlyList<object?> => true,
        "Object" when value is IReadOnlyDictionary<string, object?> => true,
        "Function" when value is Func<IReadOnlyList<object?>, object?> or NativeJavaScriptEngine.INativeCallable => true,
        string name when value is NativeJavaScriptEngine.INativeBuffer buffer && string.Equals(buffer.TypeName, name, StringComparison.Ordinal) => true,
        string name when value is IReadOnlyDictionary<string, object?> properties
            && properties.TryGetValue("__constructor", out var constructorName)
            && string.Equals(constructorName as string, name, StringComparison.Ordinal) => true,
        _ => false,
    };

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
            throw new NotSupportedException("Only explicitly supplied host functions can be called in the staged VM.");
        var arguments = new object?[call.Arguments.Count];
        for (var i = 0; i < arguments.Length; i++) arguments[i] = Evaluate(call.Arguments[i]);
        if (target is NativeJavaScriptEngine.NativeReceiverFunction receiverCallable)
            return receiverCallable.Invoke(receiver, arguments);
        return target is NativeJavaScriptEngine.INativeCallable callable
            ? callable.Invoke(arguments)
            : ((Func<IReadOnlyList<object?>, object?>)target)(arguments);
    }

    private object? Index(JavaScriptIndexExpression index)
    {
        var target = Evaluate(index.Target);
        var key = Evaluate(index.Index);
        if (target is NativeJavaScriptEngine.INativeHostObject namedHost
            && namedHost.TryGetValue(StringValue(key), out var namedValue)) return namedValue;
        if (target is IReadOnlyDictionary<string, object?> dictionary
            && dictionary.TryGetValue(StringValue(key), out var dictionaryValue)) return dictionaryValue;
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
        if (target is NativeJavaScriptEngine.INativeCallable callable && callable.TryGetProperty(name, out var callableValue)) return callableValue;
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
            if (name == "bind") return (Func<IReadOnlyList<object?>, object?>)(args => (Func<IReadOnlyList<object?>, object?>)(callArgs => receiverFunction.Invoke(args.Count > 0 ? args[0] : null, callArgs)));
            if (name == "call") return (Func<IReadOnlyList<object?>, object?>)(args => receiverFunction.Invoke(args.Count > 0 ? args[0] : null, args.Count > 1 ? args.Skip(1).ToArray() : []));
            if (name == "apply") return (Func<IReadOnlyList<object?>, object?>)(args => receiverFunction.Invoke(args.Count > 0 ? args[0] : null, args.Count > 1 && args[1] is IReadOnlyList<object?> callArgs ? callArgs : []));
        }
        if (target is NativeJavaScriptEngine.INativePromise promise && promise.TryGetProperty(name, out var promiseValue)) return promiseValue;
        if (target is IList<object?> values && TryArrayMethod(values, name, out var arrayMethod)) return arrayMethod;
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
        if (target is NativeJavaScriptEngine.INativeHostObject host && host.TryGetValue(name, out var hostValue)) return hostValue;
        if (target is Func<IReadOnlyList<object?>, object?> delegateValue)
        {
            var delegateProperties = CallableProperties.GetOrCreateValue(delegateValue);
            if (name == "bind") return (Func<IReadOnlyList<object?>, object?>)(args => (Func<IReadOnlyList<object?>, object?>)(callArgs => delegateValue(callArgs)));
            if (name == "call") return (Func<IReadOnlyList<object?>, object?>)(args => delegateValue(args.Count > 1 ? args.Skip(1).ToArray() : Array.Empty<object?>()));
            if (name == "apply") return (Func<IReadOnlyList<object?>, object?>)(args => delegateValue(args.Count > 1 && args[1] is IReadOnlyList<object?> callArgs ? callArgs : Array.Empty<object?>()));
            if (delegateProperties.TryGetValue(name, out var delegateProperty)) return delegateProperty;
            if (name == "prototype") return delegateProperties[name] = new Dictionary<string, object?>(StringComparer.Ordinal);
        }
        if (target is IReadOnlyDictionary<string, object?> properties && properties.TryGetValue(name, out var value))
            return value is NativeJavaScriptEngine.NativePropertyGetter getter ? getter.Get() : value;
        return UndefinedOrNull();
    }

    private object Object(JavaScriptObjectExpression expression)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in expression.Properties)
        {
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
        for (var i = 0; i < function.Parameters.Count; i++) local[function.Parameters[i]] = i < arguments.Count ? arguments[i] : UndefinedOrNull();
        return new JavaScriptEvaluator(local, Math.Max(1, _maxSteps - _steps), _bindings, this, _preserveUndefined, _withScopes, _deadlineExceeded).Execute(function.Body);
    }

    private sealed class RegexValue
    {
        private readonly Regex _regex;
        private RegexValue(Regex regex) => _regex = regex;
        internal static RegexValue Create(string source)
        {
            var slash = source.LastIndexOf('/');
            var pattern = slash > 0 ? source[1..slash] : source;
            var flags = slash > 0 ? source[(slash + 1)..] : string.Empty;
            var options = flags.Contains('i') ? RegexOptions.IgnoreCase : RegexOptions.None;
            return new RegexValue(new Regex(pattern, options, TimeSpan.FromMilliseconds(100)));
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
    }

    private static bool TryArrayMethod(IList<object?> values, string name, out object? method)
    {
        method = name switch
        {
            "join" => (Func<IReadOnlyList<object?>, object?>)(args => string.Join(args.Count > 0 ? StringValue(args[0]) : ",", values.Select(StringValue))),
            "indexOf" => (Func<IReadOnlyList<object?>, object?>)(args => Enumerable.Range(0, values.Count).FirstOrDefault(index => Equals(values[index], args.Count > 0 ? args[0] : null), -1)),
            "includes" => (Func<IReadOnlyList<object?>, object?>)(args => values.Any(item => Equals(item, args.Count > 0 ? args[0] : null))),
            "push" => (Func<IReadOnlyList<object?>, object?>)(args => { foreach (var value in args) values.Add(value); return (double)values.Count; }),
            "pop" => (Func<IReadOnlyList<object?>, object?>)(_ => values.Count == 0 ? null : Pop(values)),
            "shift" => (Func<IReadOnlyList<object?>, object?>)(_ => values.Count == 0 ? null : Shift(values)),
            "unshift" => (Func<IReadOnlyList<object?>, object?>)(args => { for (var i = args.Count - 1; i >= 0; i--) values.Insert(0, args[i]); return (double)values.Count; }),
            "splice" => (Func<IReadOnlyList<object?>, object?>)(args => Splice(values, args)),
            "map" => (Func<IReadOnlyList<object?>, object?>)(args => Map(values, args, filter: false)),
            "filter" => (Func<IReadOnlyList<object?>, object?>)(args => Map(values, args, filter: true)),
            "forEach" => (Func<IReadOnlyList<object?>, object?>)(args => { _ = Map(values, args, filter: false, discard: true); return null; }),
            "reduce" => (Func<IReadOnlyList<object?>, object?>)(args => Reduce(values, args)),
            "find" => (Func<IReadOnlyList<object?>, object?>)(args => Find(values, args, mode: "find")),
            "some" => (Func<IReadOnlyList<object?>, object?>)(args => Find(values, args, mode: "some")),
            "every" => (Func<IReadOnlyList<object?>, object?>)(args => Find(values, args, mode: "every")),
            "slice" => (Func<IReadOnlyList<object?>, object?>)(args =>
            {
                var start = args.Count > 0 ? Math.Clamp((int)Number(args[0]), 0, values.Count) : 0;
                var end = args.Count > 1 ? Math.Clamp((int)Number(args[1]), start, values.Count) : values.Count;
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

    private static object?[] Map(IList<object?> values, IReadOnlyList<object?> args, bool filter, bool discard = false)
    {
        if (args.Count == 0 || !IsCallable(args[0])) return [];
        var result = new List<object?>();
        for (var i = 0; i < values.Count; i++)
        {
            TryInvoke(args[0], [values[i], (double)i, values], out var mapped);
            if (!filter || Truth(mapped)) result.Add(filter ? values[i] : mapped);
        }
        return discard ? [] : result.ToArray();
    }

    private static object? Reduce(IList<object?> values, IReadOnlyList<object?> args)
    {
        if (args.Count == 0 || !IsCallable(args[0])) return null;
        var index = args.Count > 1 ? 0 : 1;
        object? accumulator = args.Count > 1 ? args[1] : values.Count > 0 ? values[0] : null;
        for (; index < values.Count; index++) TryInvoke(args[0], [accumulator, values[index], (double)index, values], out accumulator);
        return accumulator;
    }

    private static object? Find(IList<object?> values, IReadOnlyList<object?> args, string mode)
    {
        if (args.Count == 0 || !IsCallable(args[0]))
            return mode == "every";
        for (var i = 0; i < values.Count; i++)
        {
            TryInvoke(args[0], [values[i], (double)i, values], out var callbackResult);
            var matched = Truth(callbackResult);
            if (mode == "find" && matched) return values[i];
            if (mode == "some" && matched) return true;
            if (mode == "every" && !matched) return false;
        }
        return mode switch { "find" => UndefinedValue.Instance, "some" => false, _ => true };
    }

    private static bool IsCallable(object? value) => value is Func<IReadOnlyList<object?>, object?> or NativeJavaScriptEngine.INativeCallable;
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
            "replace" => (Func<IReadOnlyList<object?>, object?>)(args => args.Count > 1 ? value.Replace(StringValue(args[0]), StringValue(args[1]), StringComparison.Ordinal) : value),
            "split" => (Func<IReadOnlyList<object?>, object?>)(args => args.Count == 0 ? new[] { value } : value.Split(StringValue(args[0]), StringSplitOptions.None)),
            "trim" => (Func<IReadOnlyList<object?>, object?>)(_ => value.Trim()),
            "indexOf" => (Func<IReadOnlyList<object?>, object?>)(args => args.Count == 0 ? -1d : (double)value.IndexOf(StringValue(args[0]), StringComparison.Ordinal)),
            "charAt" => (Func<IReadOnlyList<object?>, object?>)(args => args.Count == 0 || (int)Number(args[0]) < 0 || (int)Number(args[0]) >= value.Length ? string.Empty : value[(int)Number(args[0])].ToString()),
            "charCodeAt" => (Func<IReadOnlyList<object?>, object?>)(args => args.Count == 0 || (int)Number(args[0]) < 0 || (int)Number(args[0]) >= value.Length ? double.NaN : (double)value[(int)Number(args[0])]),
            "slice" => (Func<IReadOnlyList<object?>, object?>)(args => value[Math.Clamp(args.Count > 0 ? (int)Number(args[0]) : 0, 0, value.Length)..Math.Clamp(args.Count > 1 ? (int)Number(args[1]) : value.Length, 0, value.Length)]),
            "substring" => (Func<IReadOnlyList<object?>, object?>)(args => value[Math.Clamp(args.Count > 0 ? (int)Number(args[0]) : 0, 0, value.Length)..Math.Clamp(args.Count > 1 ? (int)Number(args[1]) : value.Length, 0, value.Length)]),
            _ => null,
        };
        return method is not null;
    }

    private object Function(JavaScriptFunctionExpression function)
    {
        var captured = _bindings;
        var capturedWithScopes = _withScopes.ToArray();
        return (Func<IReadOnlyList<object?>, object?>)(arguments =>
        {
            var local = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < function.Parameters.Count; i++)
                local[function.Parameters[i]] = i < arguments.Count ? arguments[i] : null;
            local["arguments"] = arguments.ToList();
            return new JavaScriptEvaluator(local, Math.Max(1, _maxSteps - _steps), captured, this, _preserveUndefined, capturedWithScopes, _deadlineExceeded).Execute(function.Body);
        });
    }

    private NativeJavaScriptEngine.NativeFunction Class(JavaScriptClassExpression @class)
    {
        var constructor = @class.Methods.FirstOrDefault(method => method.Name == "constructor" && !method.Static)?.Function;
        var methods = @class.Methods.Where(method => method.Name != "constructor" && !method.Static).ToArray();
        var type = new NativeJavaScriptEngine.NativeFunction(arguments =>
        {
            var instance = new Dictionary<string, object?>(StringComparer.Ordinal) { ["__constructor"] = @class.Name ?? "class" };
            foreach (var method in methods)
            {
                if (!method.Getter && !method.Setter) instance[method.Name] = (Func<IReadOnlyList<object?>, object?>)(args => ExecuteClassMethod(method.Function, instance, args));
            }
            if (constructor is not null) _ = ExecuteClassMethod(constructor, instance, arguments);
            foreach (var method in methods.Where(method => method.Getter)) instance[method.Name] = ExecuteClassMethod(method.Function, instance, []);
            return instance;
        });
        foreach (var method in @class.Methods.Where(method => method.Static && !method.Getter && !method.Setter))
            type.SetProperty(method.Name, (Func<IReadOnlyList<object?>, object?>)(args => ExecuteClassMethod(method.Function, new Dictionary<string, object?> { ["__constructor"] = @class.Name ?? "class" }, args)));
        foreach (var method in @class.Methods.Where(method => method.Static && method.Getter))
            type.SetProperty(method.Name, ExecuteClassMethod(method.Function, new Dictionary<string, object?> { ["__constructor"] = @class.Name ?? "class" }, []));
        return type;
    }

    private object? ExecuteClassMethod(JavaScriptFunctionExpression function, IDictionary<string, object?> instance, IReadOnlyList<object?> arguments)
    {
        var local = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["this"] = instance,
            ["super"] = (Func<IReadOnlyList<object?>, object?>)(_ => null),
            ["arguments"] = arguments.ToList(),
        };
            for (var i = 0; i < function.Parameters.Count; i++) local[function.Parameters[i]] = i < arguments.Count ? arguments[i] : UndefinedOrNull();
        return new JavaScriptEvaluator(local, Math.Max(1, _maxSteps - _steps), _bindings, this, _preserveUndefined, _withScopes, _deadlineExceeded).Execute(function.Body);
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
            if (_withScopes[i].ContainsKey(name)) { _withScopes[i][name] = value; return; }
        if (_bindings.ContainsKey(name) || _parentBindings is null) _bindings[name] = value;
        else if (_parentBindings.ContainsKey(name)) _parentBindings[name] = value;
        else if (_parentEvaluator is not null) _parentEvaluator.Assign(name, value);
        else _bindings[name] = value;
    }

    private static bool Truth(object? value) => value switch { null or UndefinedValue => false, bool boolean => boolean, double number => number != 0 && !double.IsNaN(number), string text => text.Length != 0, _ => true };
    private static double Number(object? value) => value switch { null => 0, UndefinedValue => double.NaN, bool boolean => boolean ? 1 : 0, double number => number, int integer => integer, string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number, _ => double.NaN };
    private static int ToInt32(double value) => unchecked((int)value);
    private static string StringValue(object? value) => value switch { null => "null", UndefinedValue => "undefined", bool boolean => boolean ? "true" : "false", double number => number.ToString("G15", CultureInfo.InvariantCulture), _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty };
    private object? UndefinedOrNull() => _preserveUndefined ? UndefinedValue.Instance : null;

    internal sealed class UndefinedValue
    {
        internal static readonly UndefinedValue Instance = new();
        private UndefinedValue() { }
    }

    private sealed class BreakSignal : Exception;
    private sealed class ContinueSignal : Exception;
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
