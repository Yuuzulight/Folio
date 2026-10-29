using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Buffers.Binary;

using System.Numerics;

namespace Folio.Scripting.Native;

/// <summary>
/// Folio-owned staged engine adapter. It deliberately exposes only the semantics
/// implemented by the bounded front end; production routing remains gated until
/// differential parity is demonstrated. The public surface exists so hosts and
/// differential-test runners can select the engine explicitly without reaching
/// into test-only internals.
/// </summary>
public sealed class NativeJavaScriptEngine : IScriptEngine, IScriptEngineFactory, IHostScriptEvaluator, IStructuredCloneEngine
{
    private readonly Dictionary<string, object?> _bindings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> _domPrototypes = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<object, Dictionary<IHostObjectTemplate, NativeHostValue>> _hostObjectCache = new();
    private readonly Queue<Action> _microtasks = new();
    private int _maxSteps = ScriptLimits.Default.MaxStatements ?? 100_000;
    private TimeSpan? _timeout = ScriptLimits.Default.Timeout;
    private long _deadlineTicks;
    private bool _interrupted;
    private bool _disposed;

    public NativeJavaScriptEngine(int? inheritedMaxSteps = null, TimeSpan? inheritedTimeout = null)
    {
        if (inheritedMaxSteps is > 0) _maxSteps = inheritedMaxSteps.Value;
        if (inheritedTimeout is not null) _timeout = inheritedTimeout;
        _bindings["Infinity"] = double.PositiveInfinity;
        _bindings["NaN"] = double.NaN;
        var arrayConstructor = new NativeFunction(args => args.Count == 1 && args[0] is double length
            ? Enumerable.Repeat<object?>(JavaScriptEvaluator.UndefinedValue.Instance, Math.Max(0, (int)length)).ToList()
            : args.ToList());
        arrayConstructor.SetProperty("isArray", (Func<IReadOnlyList<object?>, object?>)(args => args.Count > 0 && args[0] is IReadOnlyList<object?>));
        arrayConstructor.SetProperty("from", (Func<IReadOnlyList<object?>, object?>)(args =>
        {
            if (args.Count == 0 || args[0] is null or JavaScriptEvaluator.UndefinedValue) return Array.Empty<object?>();
            var values = args[0] switch
            {
                IReadOnlyList<object?> list => list.ToArray(),
                string text => text.EnumerateRunes().Select(rune => (object?)rune.ToString()).ToArray(),
                IReadOnlyDictionary<string, object?> arrayLike when arrayLike.TryGetValue("length", out var length)
                    => Enumerable.Range(0, Math.Max(0, (int)NumberValue(length)))
                        .Select(index => arrayLike.GetValueOrDefault(index.ToString(CultureInfo.InvariantCulture), JavaScriptEvaluator.UndefinedValue.Instance))
                        .ToArray(),
                _ => Array.Empty<object?>(),
            };
            if (args.Count > 1)
            {
                if (!IsCallable(args[1])) throw WebIdlException.TypeError("Array.from mapfn must be callable.");
                for (var index = 0; index < values.Length; index++) values[index] = InvokeCallable(args[1]!, [values[index], (double)index]);
            }
            return values;
        }));
        var arrayPrototype = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["push"] = new NativeReceiverFunction((receiver, args) =>
            {
                if (receiver is IDictionary<string, object?> properties)
                {
                    var length = properties.TryGetValue("length", out var current) ? (int)NumberValue(current) : 0;
                    foreach (var value in args) properties[(length++).ToString(CultureInfo.InvariantCulture)] = value;
                    properties["length"] = (double)length;
                    return (double)length;
                }
                if (receiver is IList<object?> list)
                {
                    foreach (var value in args) list.Add(value);
                    return (double)list.Count;
                }
                return 0d;
            }),
            ["pop"] = new NativeReceiverFunction((receiver, _) =>
            {
                if (receiver is IDictionary<string, object?> properties && properties.TryGetValue("length", out var current))
                {
                    var length = (int)NumberValue(current);
                    if (length == 0) return JavaScriptEvaluator.UndefinedValue.Instance;
                    var index = (--length).ToString(CultureInfo.InvariantCulture);
                    var value = properties.TryGetValue(index, out var stored) ? stored : JavaScriptEvaluator.UndefinedValue.Instance;
                    properties.Remove(index);
                    properties["length"] = (double)length;
                    return value;
                }
                if (receiver is IList<object?> list && list.Count > 0)
                {
                    var index = list.Count - 1;
                    var value = list[index];
                    list.RemoveAt(index);
                    return value;
                }
                return JavaScriptEvaluator.UndefinedValue.Instance;
            }),
            ["Symbol.iterator"] = new NativeReceiverFunction((receiver, _) => receiver switch
            {
                IReadOnlyList<object?> list => list.ToArray(),
                IReadOnlyDictionary<string, object?> properties when properties.TryGetValue("length", out var length)
                    => Enumerable.Range(0, Math.Max(0, (int)NumberValue(length)))
                        .Select(index => properties.TryGetValue(index.ToString(CultureInfo.InvariantCulture), out var stored) ? stored : JavaScriptEvaluator.UndefinedValue.Instance).ToArray(),
                _ => Array.Empty<object?>(),
            }),
            ["slice"] = new NativeReceiverFunction((receiver, args) =>
            {
                var values = receiver switch
                {
                    IReadOnlyList<object?> list => list,
                    IReadOnlyDictionary<string, object?> properties when properties.TryGetValue("length", out var length)
                        => Enumerable.Range(0, Math.Max(0, (int)NumberValue(length))).Select(index => properties.GetValueOrDefault(index.ToString(CultureInfo.InvariantCulture), JavaScriptEvaluator.UndefinedValue.Instance)).ToArray(),
                    _ => Array.Empty<object?>(),
                };
                var start = JavaScriptEvaluator.RelativeArrayIndex(args.Count > 0 ? args[0] : null, values.Count, 0);
                var end = JavaScriptEvaluator.RelativeArrayIndex(args.Count > 1 ? args[1] : null, values.Count, values.Count);
                if (end < start) end = start;
                return values.Skip(start).Take(end - start).ToArray();
            }),
            ["map"] = new NativeReceiverFunction((receiver, args) =>
            {
                if (receiver is not IReadOnlyList<object?> values) return Array.Empty<object?>();
                if (args.Count == 0 || !IsCallable(args[0])) throw WebIdlException.TypeError("Array callback must be callable.");
                return values.Select((value, index) => InvokeCallable(args[0], [value, (double)index, values])).ToList();
            }),
            ["flatMap"] = new NativeReceiverFunction((receiver, args) =>
            {
                if (receiver is not IReadOnlyList<object?> values) return Array.Empty<object?>();
                if (args.Count == 0 || !IsCallable(args[0])) throw WebIdlException.TypeError("Array callback must be callable.");
                return values.SelectMany((value, index) =>
                {
                    var mapped = InvokeCallable(args[0], [value, (double)index, values]);
                    return mapped is IEnumerable<object?> nested ? nested : [mapped];
                }).ToList();
            }),
        };
        arrayConstructor.SetProperty("prototype", arrayPrototype);
        _bindings["Array"] = arrayConstructor;
        var objectPrototype = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["hasOwnProperty"] = new NativeReceiverFunction((receiver, args) =>
                receiver is IReadOnlyDictionary<string, object?> properties
                    && args.Count > 0
                    && properties.ContainsKey(StringValue(args[0]))),
            ["toString"] = new NativeReceiverFunction((receiver, _) => receiver switch
            {
                null => "[object Null]",
                JavaScriptEvaluator.UndefinedValue => "[object Undefined]",
                IReadOnlyList<object?> => "[object Array]",
                _ => "[object Object]",
            }),
        };
        var objectBindings = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["prototype"] = objectPrototype,
            ["assign"] = (Func<IReadOnlyList<object?>, object?>)(args =>
            {
                if (args.Count == 0 || args[0] is not IDictionary<string, object?> target) return args.Count > 0 ? args[0] : null;
                foreach (var source in args.Skip(1))
                    if (source is IReadOnlyDictionary<string, object?> properties)
                        foreach (var pair in properties) target[pair.Key] = pair.Value;
                return target;
            }),
            ["keys"] = (Func<IReadOnlyList<object?>, object?>)(args => args.Count > 0
                ? args[0] switch
                {
                    IReadOnlyDictionary<string, object?> properties => properties.Keys.Cast<object?>().ToArray(),
                    IReadOnlyList<object?> values => Enumerable.Range(0, values.Count).Select(index => (object?)index.ToString(CultureInfo.InvariantCulture)).ToArray(),
                    _ => Array.Empty<object?>(),
                }
                : Array.Empty<object?>()),
            ["entries"] = (Func<IReadOnlyList<object?>, object?>)(args => args.Count > 0
                ? args[0] switch
                {
                    IReadOnlyDictionary<string, object?> properties => properties.Select(pair => (object?)new object?[] { pair.Key, pair.Value }).ToArray(),
                    IReadOnlyList<object?> values => values.Select((value, index) => (object?)new object?[] { index.ToString(CultureInfo.InvariantCulture), value }).ToArray(),
                    _ => Array.Empty<object?>(),
                }
                : Array.Empty<object?>()),
            ["values"] = (Func<IReadOnlyList<object?>, object?>)(args => args.Count > 0
                ? args[0] switch
                {
                    IReadOnlyDictionary<string, object?> properties => properties.Values.ToArray(),
                    IReadOnlyList<object?> values => values.ToArray(),
                    _ => Array.Empty<object?>(),
                }
                : Array.Empty<object?>()),
            ["getOwnPropertyNames"] = (Func<IReadOnlyList<object?>, object?>)(args =>
            {
                if (args.Count == 0 || args[0] is null || args[0] is JavaScriptEvaluator.UndefinedValue) return Array.Empty<object?>();
                return args[0] switch
                {
                    IReadOnlyDictionary<string, object?> properties => properties.Keys.Cast<object?>().ToArray(),
                    IList<object?> values => Enumerable.Range(0, values.Count).Select(index => (object?)index.ToString(CultureInfo.InvariantCulture)).Append("length").ToArray(),
                    INativeCallable callable => callable.GetPropertyNames().Cast<object?>().ToArray(),
                    _ => Array.Empty<object?>(),
                };
            }),
            ["getOwnPropertySymbols"] = (Func<IReadOnlyList<object?>, object?>)(_ => Array.Empty<object?>()),
            ["create"] = (Func<IReadOnlyList<object?>, object?>)(args =>
            {
                var result = new Dictionary<string, object?>(StringComparer.Ordinal);
                if (args.Count > 0 && args[0] is IReadOnlyDictionary<string, object?> prototype)
                    result["__proto__"] = prototype;
                if (args.Count > 1 && args[1] is IReadOnlyDictionary<string, object?> descriptors)
                    foreach (var (name, descriptor) in descriptors)
                        if (descriptor is IReadOnlyDictionary<string, object?> entry && entry.TryGetValue("value", out var value)) result[name] = value;
                return result;
            }),
            ["defineProperty"] = (Func<IReadOnlyList<object?>, object?>)(args =>
            {
                if (args.Count > 2 && args[1] is not null)
                {
                    var name = StringValue(args[1]);
                    object? value = null;
                    if (args[2] is IReadOnlyDictionary<string, object?> descriptor)
                    {
                        var hasGetter = descriptor.TryGetValue("get", out var getter) && IsCallable(getter);
                        var hasSetter = descriptor.TryGetValue("set", out var setter) && IsCallable(setter);
                        if (hasGetter || hasSetter)
                            value = new NativePropertyAccessor(
                                hasGetter ? receiver => JavaScriptEvaluator.InvokeWithReceiver(getter!, receiver, []) : null,
                                hasSetter ? (receiver, assigned) => JavaScriptEvaluator.InvokeWithReceiver(setter!, receiver, [assigned]) : null);
                        else if (descriptor.TryGetValue("value", out var descriptorValue)) value = descriptorValue;
                    }
                    if (args[0] is IDictionary<string, object?> target)
                        target[name] = value;
                    else if (args[0] is NativeFunction function)
                        function.SetProperty(name, value);
                    else if (args[0] is NativeReceiverFunction receiverFunction)
                        receiverFunction.SetProperty(name, value);
                    else if (args[0] is Func<IReadOnlyList<object?>, object?> callable)
                        JavaScriptEvaluator.SetCallableProperty(callable, name, value);
                    else if (args[0] is INativeHostObject host)
                        host.TrySetSetValue(name, value);
                    else if (args[0] is IList<object?> values && int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 0)
                    {
                        while (values.Count <= index) values.Add(JavaScriptEvaluator.UndefinedValue.Instance);
                        values[index] = value;
                    }
                }
                return args.Count > 0 ? args[0] : null;
            }),
            ["defineProperties"] = (Func<IReadOnlyList<object?>, object?>)(args =>
            {
                if (args.Count > 1 && args[0] is IDictionary<string, object?> target && args[1] is IReadOnlyDictionary<string, object?> descriptors)
                    foreach (var (name, descriptor) in descriptors)
                        if (descriptor is IReadOnlyDictionary<string, object?> entry)
                        {
                            var hasGetter = entry.TryGetValue("get", out var getter) && IsCallable(getter);
                            var hasSetter = entry.TryGetValue("set", out var setter) && IsCallable(setter);
                            if (hasGetter || hasSetter)
                                target[name] = new NativePropertyAccessor(
                                    hasGetter ? receiver => JavaScriptEvaluator.InvokeWithReceiver(getter!, receiver, []) : null,
                                    hasSetter ? (receiver, assigned) => JavaScriptEvaluator.InvokeWithReceiver(setter!, receiver, [assigned]) : null);
                            else if (entry.TryGetValue("value", out var value)) target[name] = value;
                        }
                return args.Count > 0 ? args[0] : null;
            }),
            ["setPrototypeOf"] = (Func<IReadOnlyList<object?>, object?>)(args =>
            {
                if (args.Count > 1 && args[0] is IDictionary<string, object?> target)
                    target["__proto__"] = args[1];
                return args.Count > 0 ? args[0] : null;
            }),
            ["getPrototypeOf"] = (Func<IReadOnlyList<object?>, object?>)(args =>
            {
                if (args.Count == 0 || args[0] is null or JavaScriptEvaluator.UndefinedValue)
                    return null;
                if (args[0] is IReadOnlyDictionary<string, object?> properties && properties.TryGetValue("__proto__", out var prototype))
                    return prototype;
                return null;
            }),
            ["hasOwn"] = (Func<IReadOnlyList<object?>, object?>)(args => args.Count > 1 && args[0] is IReadOnlyDictionary<string, object?> own && own.ContainsKey(StringValue(args[1]))),
            ["is"] = (Func<IReadOnlyList<object?>, object?>)(args => args.Count > 1 && Equals(args[0], args[1])),
            ["freeze"] = (Func<IReadOnlyList<object?>, object?>)(args => args.Count > 0 ? args[0] : null),
            ["getOwnPropertyDescriptor"] = (Func<IReadOnlyList<object?>, object?>)(args =>
            {
                if (args.Count < 2 || args[0] is null || args[0] is JavaScriptEvaluator.UndefinedValue) return null;
                var name = StringValue(args[1]);
                object? value;
                var found = args[0] switch
                {
                    IReadOnlyDictionary<string, object?> target => target.TryGetValue(name, out value),
                    INativeHostObject host => host.TryGetValue(name, out value),
                    INativeCallable callable => callable.TryGetProperty(name, out value),
                    INativeBuffer buffer => buffer.TryGetProperty(name, out value),
                    IList<object?> values when name == "length" => (value = (double)values.Count) is not null,
                    IList<object?> values when int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < values.Count
                        => (value = values[index]) is not null,
                    _ => (value = null) is not null,
                };
                return found ? new Dictionary<string, object?> { ["value"] = value, ["writable"] = true, ["enumerable"] = name != "length", ["configurable"] = true } : null;
            }),
            ["prototype"] = new Dictionary<string, object?>(StringComparer.Ordinal),
        };
        var objectConstructor = new NativeFunction(args =>
            args.Count > 0 && args[0] is not null && args[0] is not JavaScriptEvaluator.UndefinedValue
                ? args[0]
                : new Dictionary<string, object?>(StringComparer.Ordinal));
        foreach (var (name, value) in objectBindings)
            objectConstructor.SetProperty(name, value);
        _bindings["Object"] = objectConstructor;

        _bindings["Reflect"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["apply"] = (Func<IReadOnlyList<object?>, object?>)(args =>
            {
                if (args.Count == 0) return null;
                var fn = args[0];
                var callArgs = args.Count > 2 && args[2] is IReadOnlyList<object?> list ? list : Array.Empty<object?>();
                return fn switch
                {
                    NativeReceiverFunction receiverCallable => receiverCallable.Invoke(args.Count > 1 ? args[1] : null, callArgs),
                    Func<IReadOnlyList<object?>, object?> callable => callable(callArgs),
                    INativeCallable callable => callable.Invoke(callArgs),
                    _ => throw new InvalidOperationException("Reflect.apply target is not callable (" + (fn is null ? "null" : fn.GetType().FullName) + ")."),
                };
            }),
        };
        _bindings["Math"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["abs"] = (Func<IReadOnlyList<object?>, object?>)(args => Math.Abs(Number(args, 0))),
            ["floor"] = (Func<IReadOnlyList<object?>, object?>)(args => Math.Floor(Number(args, 0))),
            ["ceil"] = (Func<IReadOnlyList<object?>, object?>)(args => Math.Ceiling(Number(args, 0))),
            ["max"] = (Func<IReadOnlyList<object?>, object?>)(args => args.Count == 0 ? double.NegativeInfinity : args.Max(value => value is double number ? number : 0)),
            ["min"] = (Func<IReadOnlyList<object?>, object?>)(args => args.Count == 0 ? double.PositiveInfinity : args.Min(value => value is double number ? number : 0)),
            ["random"] = (Func<IReadOnlyList<object?>, object?>)(_ => Random.Shared.NextDouble()),
            ["log"] = (Func<IReadOnlyList<object?>, object?>)(args => Math.Log(Number(args, 0))),
            ["pow"] = (Func<IReadOnlyList<object?>, object?>)(args => Math.Pow(Number(args, 0), Number(args, 1))),
            ["sqrt"] = (Func<IReadOnlyList<object?>, object?>)(args => Math.Sqrt(Number(args, 0))),
            ["sign"] = (Func<IReadOnlyList<object?>, object?>)(args => Math.Sign(Number(args, 0))),
            ["trunc"] = (Func<IReadOnlyList<object?>, object?>)(args => Math.Truncate(Number(args, 0))),
            ["sin"] = (Func<IReadOnlyList<object?>, object?>)(args => Math.Sin(Number(args, 0))),
            ["cos"] = (Func<IReadOnlyList<object?>, object?>)(args => Math.Cos(Number(args, 0))),
            ["atan2"] = (Func<IReadOnlyList<object?>, object?>)(args => Math.Atan2(Number(args, 0), Number(args, 1))),
            ["asin"] = (Func<IReadOnlyList<object?>, object?>)(args => Math.Asin(Number(args, 0))),
            ["acos"] = (Func<IReadOnlyList<object?>, object?>)(args => Math.Acos(Number(args, 0))),
            ["tan"] = (Func<IReadOnlyList<object?>, object?>)(args => Math.Tan(Number(args, 0))),
            ["exp"] = (Func<IReadOnlyList<object?>, object?>)(args => Math.Exp(Number(args, 0))),
            ["clz32"] = (Func<IReadOnlyList<object?>, object?>)(args => BitOperations.LeadingZeroCount((uint)(long)Number(args, 0))),
        };
        var stringConstructor = new NativeFunction(args => args.Count == 0 ? string.Empty : StringValue(args[0]));
        stringConstructor.SetProperty("fromCharCode", (Func<IReadOnlyList<object?>, object?>)(args =>
            string.Concat(args.Select(value => (char)((int)NumberValue(value) & 0xffff)))));
        _bindings["String"] = stringConstructor;
        var numberConstructor = new NativeFunction(args => args.Count == 0 ? 0d : NumberValue(args[0]));
        numberConstructor.SetProperty("isInteger", (Func<IReadOnlyList<object?>, object?>)(args => args.Count > 0 && NumberValue(args[0]) is var number && !double.IsNaN(number) && !double.IsInfinity(number) && number == Math.Truncate(number)));
        numberConstructor.SetProperty("isSafeInteger", (Func<IReadOnlyList<object?>, object?>)(args => args.Count > 0 && NumberValue(args[0]) is var number && !double.IsNaN(number) && !double.IsInfinity(number) && number == Math.Truncate(number) && Math.Abs(number) <= 9007199254740991d));
        numberConstructor.SetProperty("isNaN", (Func<IReadOnlyList<object?>, object?>)(args => args.Count == 0 || double.IsNaN(NumberValue(args[0]))));
        numberConstructor.SetProperty("isFinite", (Func<IReadOnlyList<object?>, object?>)(args => args.Count > 0 && !double.IsNaN(NumberValue(args[0])) && !double.IsInfinity(NumberValue(args[0]))));
        _bindings["Number"] = numberConstructor;
        _bindings["Boolean"] = (Func<IReadOnlyList<object?>, object?>)(args => args.Count > 0 && TruthValue(args[0]));
        _bindings["parseInt"] = (Func<IReadOnlyList<object?>, object?>)(args => ParseInteger(args.Count == 0 ? string.Empty : StringValue(args[0])));
        _bindings["encodeURIComponent"] = (Func<IReadOnlyList<object?>, object?>)(args => Uri.EscapeDataString(args.Count == 0 ? string.Empty : StringValue(args[0])));
        _bindings["decodeURIComponent"] = (Func<IReadOnlyList<object?>, object?>)(args => Uri.UnescapeDataString(args.Count == 0 ? string.Empty : StringValue(args[0])));
        _bindings["isNaN"] = (Func<IReadOnlyList<object?>, object?>)(args => args.Count == 0 || double.IsNaN(NumberValue(args[0])));
        _bindings["Error"] = CreateErrorConstructor("Error");
        _bindings["TypeError"] = CreateErrorConstructor("TypeError");
        _bindings["RangeError"] = CreateErrorConstructor("RangeError");
        _bindings["console"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["log"] = (Func<IReadOnlyList<object?>, object?>)(_ => null),
            ["warn"] = (Func<IReadOnlyList<object?>, object?>)(_ => null),
            ["error"] = (Func<IReadOnlyList<object?>, object?>)(_ => null),
        };
        _bindings["JSON"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["stringify"] = (Func<IReadOnlyList<object?>, object?>)(args => args.Count == 0 ? null : JsonSerializer.Serialize(args[0])),
            ["parse"] = (Func<IReadOnlyList<object?>, object?>)(args => args.Count == 0 ? null : ParseJson(StringValue(args[0]))),
        };
        var weakMapPrototype = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["set"] = new NativeReceiverFunction((receiver, args) => receiver is NativeWeakMap map && args.Count > 1 && args[0] is { } key ? map.Set(key, args[1]) : receiver),
            ["get"] = new NativeReceiverFunction((receiver, args) => receiver is NativeWeakMap map && args.Count > 0 && args[0] is { } key ? map.Get(key) : JavaScriptEvaluator.UndefinedValue.Instance),
            ["has"] = new NativeReceiverFunction((receiver, args) => receiver is NativeWeakMap map && args.Count > 0 && args[0] is { } key && map.Has(key)),
            ["delete"] = new NativeReceiverFunction((receiver, args) => receiver is NativeWeakMap map && args.Count > 0 && args[0] is { } key && map.Delete(key)),
        };
        var weakMapConstructor = new NativeFunction(_ => new NativeWeakMap());
        weakMapConstructor.SetProperty("prototype", weakMapPrototype);
        _bindings["WeakMap"] = weakMapConstructor;
        var weakSetPrototype = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["add"] = new NativeReceiverFunction((receiver, args) => receiver is NativeWeakSet set && args.Count > 0 && args[0] is { } key ? set.Add(key) : receiver),
            ["has"] = new NativeReceiverFunction((receiver, args) => receiver is NativeWeakSet set && args.Count > 0 && args[0] is { } key && set.Has(key)),
            ["delete"] = new NativeReceiverFunction((receiver, args) => receiver is NativeWeakSet set && args.Count > 0 && args[0] is { } key && set.Delete(key)),
        };
        var weakSetConstructor = new NativeFunction(_ => new NativeWeakSet());
        weakSetConstructor.SetProperty("prototype", weakSetPrototype);
        _bindings["WeakSet"] = weakSetConstructor;
        var setPrototype = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["add"] = new NativeReceiverFunction((receiver, args) => receiver is NativeSet set && args.Count > 0 ? set.Add(args[0]) : receiver),
            ["has"] = new NativeReceiverFunction((receiver, args) => receiver is NativeSet set && set.Has(args.Count > 0 ? args[0] : null)),
            ["delete"] = new NativeReceiverFunction((receiver, args) => receiver is NativeSet set && set.Delete(args.Count > 0 ? args[0] : null)),
            ["forEach"] = new NativeReceiverFunction((receiver, args) => { if (receiver is NativeSet set && args.Count > 0 && IsCallable(args[0])) set.ForEach(args[0]!); return JavaScriptEvaluator.UndefinedValue.Instance; }),
        };
        var setConstructor = new NativeFunction(args => new NativeSet(args.Count > 0 ? args[0] : null));
        setConstructor.SetProperty("prototype", setPrototype);
        _bindings["Set"] = setConstructor;
        var mapPrototype = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["set"] = new NativeReceiverFunction((receiver, args) => receiver is NativeMap map && args.Count > 1 && args[0] is { } key ? map.Set(key, args[1]) : receiver),
            ["get"] = new NativeReceiverFunction((receiver, args) => receiver is NativeMap map && args.Count > 0 && args[0] is { } key ? map.Get(key) : JavaScriptEvaluator.UndefinedValue.Instance),
            ["has"] = new NativeReceiverFunction((receiver, args) => receiver is NativeMap map && args.Count > 0 && args[0] is { } key && map.Has(key)),
            ["delete"] = new NativeReceiverFunction((receiver, args) => receiver is NativeMap map && args.Count > 0 && args[0] is { } key && map.Delete(key)),
            ["forEach"] = new NativeReceiverFunction((receiver, args) => { if (receiver is NativeMap map && args.Count > 0 && IsCallable(args[0])) map.ForEach(args[0]!); return JavaScriptEvaluator.UndefinedValue.Instance; }),
        };
        var mapConstructor = new NativeFunction(args => new NativeMap(args.Count > 0 ? args[0] : null));
        mapConstructor.SetProperty("prototype", mapPrototype);
        _bindings["Map"] = mapConstructor;
        var symbolConstructor = new NativeFunction(args => $"Symbol:{(args.Count > 0 ? args[0] : "undefined")}");
        symbolConstructor.SetProperty("iterator", "Symbol.iterator");
        symbolConstructor.SetProperty("toStringTag", "Symbol.toStringTag");
        symbolConstructor.SetProperty("for", (Func<IReadOnlyList<object?>, object?>)(args => $"Symbol.for:{(args.Count > 0 ? args[0] : "undefined")}"));
        _bindings["Symbol"] = symbolConstructor;
        var promiseConstructor = new NativeFunction(args =>
        {
            var promise = new NativePromise(this, null, false, settled: false);
            if (args.Count > 0 && IsCallable(args[0]))
            {
                var resolve = new NativeFunction(resolveArgs => { promise.Resolve(resolveArgs.Count > 0 ? resolveArgs[0] : null); return null; });
                var reject = new NativeFunction(rejectArgs => { promise.Reject(rejectArgs.Count > 0 ? rejectArgs[0] : null); return null; });
                try { InvokeCallable(args[0], [resolve, reject]); }
                catch (Exception error) { promise.Reject(error); }
            }
            return promise;
        });
        promiseConstructor.SetProperty("resolve", (Func<IReadOnlyList<object?>, object?>)(args => new NativePromise(this, args.Count > 0 ? args[0] : null, false)));
        promiseConstructor.SetProperty("reject", (Func<IReadOnlyList<object?>, object?>)(args => new NativePromise(this, args.Count > 0 ? args[0] : null, true)));
        _bindings["Promise"] = promiseConstructor;
        _bindings["queueMicrotask"] = (Func<IReadOnlyList<object?>, object?>)(args =>
        {
            if (args.Count > 0 && IsCallable(args[0])) _microtasks.Enqueue(() => InvokeCallable(args[0], []));
            return null;
        });
        _bindings["Function"] = (Func<IReadOnlyList<object?>, object?>)(args => CreateDynamicFunction(args));
        var dateConstructor = new NativeFunction(args => new NativeDate(args.Count > 0 ? NumberValue(args[0]) : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        dateConstructor.SetProperty("prototype", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["getTime"] = new NativeReceiverFunction((receiver, _) => receiver is NativeDate date ? date.Milliseconds : double.NaN),
        });
        dateConstructor.SetProperty("now", (Func<IReadOnlyList<object?>, object?>)(_ => (double)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        _bindings["Date"] = dateConstructor;
        _bindings["RegExp"] = new NativeFunction(args => JavaScriptEvaluator.RegexValue.Create("/" + (args.Count > 0 ? StringValue(args[0]) : string.Empty) + "/" + (args.Count > 1 ? StringValue(args[1]) : string.Empty)));
        _bindings["BigInt"] = (Func<IReadOnlyList<object?>, object?>)(args => args.Count > 0 ? NumberValue(args[0]) : 0d);
        _bindings["Proxy"] = (Func<IReadOnlyList<object?>, object?>)(args => args.Count > 1 ? new NativeProxyValue(args[0], args[1]) : args.Count > 0 ? args[0] : new Dictionary<string, object?>());
        _bindings["atob"] = (Func<IReadOnlyList<object?>, object?>)(args => args.Count == 0 ? string.Empty : Encoding.Latin1.GetString(Convert.FromBase64String(StringValue(args[0]))));
        _bindings["btoa"] = (Func<IReadOnlyList<object?>, object?>)(args => args.Count == 0 ? string.Empty : Convert.ToBase64String(Encoding.Latin1.GetBytes(StringValue(args[0]))));
        var arrayBuffer = new NativeFunction(args =>
        {
            var length = args.Count == 0 ? 0 : Math.Max(0, (int)NumberValue(args[0]));
            return new NativeBuffer(ScriptBufferSourceKind.ArrayBuffer, new byte[length], 1);
        });
        arrayBuffer.SetProperty("isView", (Func<IReadOnlyList<object?>, object?>)(args => args.Count > 0 && args[0] is INativeBuffer buffer && buffer.TypeName != "ArrayBuffer"));
        arrayBuffer.SetProperty("prototype", new Dictionary<string, object?>(StringComparer.Ordinal));
        _bindings["ArrayBuffer"] = arrayBuffer;
        foreach (var errorName in new[] { "EvalError", "ReferenceError", "SyntaxError", "URIError" })
            _bindings[errorName] = CreateErrorConstructor(errorName);
        foreach (var (typedName, kind) in new[]
        {
            ("Uint8Array", ScriptBufferSourceKind.Uint8), ("Uint8ClampedArray", ScriptBufferSourceKind.Uint8Clamped),
            ("Int8Array", ScriptBufferSourceKind.Int8), ("Uint16Array", ScriptBufferSourceKind.Uint16),
            ("Int16Array", ScriptBufferSourceKind.Int16), ("Uint32Array", ScriptBufferSourceKind.Uint32),
            ("Int32Array", ScriptBufferSourceKind.Int32), ("Float32Array", ScriptBufferSourceKind.Float32),
            ("Float64Array", ScriptBufferSourceKind.Float64), ("BigInt64Array", ScriptBufferSourceKind.BigInt64),
            ("BigUint64Array", ScriptBufferSourceKind.BigUint64), ("DataView", ScriptBufferSourceKind.DataView),
        }) _bindings[typedName] = TypedArrayConstructor(typedName, kind);
        // Host bootstrap scripts use globalThis for standards-shaped globals.
        // Keep it as a live view of the engine binding table so assignments made
        // through either spelling observe the same state.
        _bindings["globalThis"] = _bindings;
        // At script top level (including UMD libraries) `this` is the global
        // object. Function calls replace it with their actual receiver in the
        // evaluator, so this does not leak into ordinary function scope.
        _bindings["this"] = _bindings;
        if (_bindings["Object"] is NativeFunction objectConstructorBinding)
            objectConstructorBinding.SetProperty("prototype", objectPrototype);
    }

    public void Configure(ScriptLimits limits)
    {
        _maxSteps = Math.Max(1, limits.MaxStatements ?? 100_000);
        _timeout = limits.Timeout;
    }

    private NativeFunction StubConstructor(string name)
    {
        var constructor = new NativeFunction(args => new Dictionary<string, object?> { ["__constructor"] = name });
        constructor.SetProperty("prototype", new Dictionary<string, object?>(StringComparer.Ordinal));
        return constructor;
    }

    private NativeFunction TypedArrayConstructor(string name, ScriptBufferSourceKind kind)
    {
        var constructor = new NativeFunction(args =>
        {
            var elementSize = ElementSize(kind);
            if (args.Count == 0) return new NativeBuffer(kind, [], elementSize);
            if (args[0] is NativeBuffer existing)
                return new NativeBuffer(kind, existing.Bytes.ToArray(), elementSize);
            if (args[0] is IReadOnlyList<object?> values)
                return new NativeBuffer(kind, EncodeElements(kind, values), elementSize);
            var length = Math.Max(0, (int)NumberValue(args[0]));
            return new NativeBuffer(kind, new byte[length * elementSize], elementSize);
        });
        constructor.SetProperty("prototype", new Dictionary<string, object?>(StringComparer.Ordinal));
        return constructor;
    }

    private static byte[] EncodeElements(ScriptBufferSourceKind kind, IReadOnlyList<object?> values)
    {
        var size = ElementSize(kind);
        var bytes = new byte[values.Count * size];
        for (var i = 0; i < values.Count; i++)
        {
            var number = values[i] switch { double d => d, int n => n, long n => n, _ when double.TryParse(values[i]?.ToString(), out var parsed) => parsed, _ => 0 };
            var offset = i * size;
            switch (kind)
            {
                case ScriptBufferSourceKind.Uint8 or ScriptBufferSourceKind.Uint8Clamped: bytes[offset] = (byte)Math.Clamp((int)number, 0, 255); break;
                case ScriptBufferSourceKind.Int8: bytes[offset] = unchecked((byte)(sbyte)number); break;
                case ScriptBufferSourceKind.Uint16: BitConverter.TryWriteBytes(bytes.AsSpan(offset, 2), (ushort)number); break;
                case ScriptBufferSourceKind.Int16: BitConverter.TryWriteBytes(bytes.AsSpan(offset, 2), (short)number); break;
                case ScriptBufferSourceKind.Uint32: BitConverter.TryWriteBytes(bytes.AsSpan(offset, 4), (uint)number); break;
                case ScriptBufferSourceKind.Int32: BitConverter.TryWriteBytes(bytes.AsSpan(offset, 4), (int)number); break;
                case ScriptBufferSourceKind.Float32: BitConverter.TryWriteBytes(bytes.AsSpan(offset, 4), (float)number); break;
                case ScriptBufferSourceKind.Float64: BitConverter.TryWriteBytes(bytes.AsSpan(offset, 8), number); break;
                default: break;
            }
        }
        return bytes;
    }

    public ScriptValue Evaluate(string source, string url = "eval")
    {
        EnsureLive();
        if (_interrupted) { _interrupted = false; throw new ScriptInterruptedException("The native script was interrupted."); }
        var previousDeadline = _deadlineTicks;
        if (previousDeadline == 0) _deadlineTicks = NewDeadline();
        try
        {
            var statements = new JavaScriptParser(source).ParseProgram();
            var evaluator = new JavaScriptEvaluator(_bindings, _maxSteps, preserveUndefined: true, deadlineExceeded: IsDeadlineExceeded);
            var result = statements.Count == 1 && statements[0] is JavaScriptExpressionStatement expression
                ? evaluator.Evaluate(expression.Expression)
                : evaluator.Execute(statements);
            return ToScriptValue(result);
        }
        finally { _deadlineTicks = previousDeadline; }
    }

    /// <summary>
    /// Evaluates Folio-owned bootstrap code through the same bounded evaluator as
    /// page code. Keeping this explicit prevents host bootstrap from silently
    /// bypassing the native engine when a native context is selected.
    /// </summary>
    ScriptValue IHostScriptEvaluator.EvaluateHostScript(string source, string url) => Evaluate(source, url);

    public ScriptValue Call(ScriptValue function, ScriptValue thisValue, ReadOnlySpan<ScriptValue> args)
    {
        EnsureLive();
        var previousDeadline = _deadlineTicks;
        if (previousDeadline == 0) _deadlineTicks = NewDeadline();
        try
        {
            var values = args.ToArray().Select(ToObject).ToArray();
            var receiver = ToObject(thisValue);
            return function.Raw switch
            {
                Func<IReadOnlyList<object?>, object?> callable => ToScriptValue(JavaScriptEvaluator.InvokeWithReceiver(callable, receiver, values)),
                NativeReceiverFunction callable => ToScriptValue(callable.Invoke(receiver, values)),
                INativeCallable callable => ToScriptValue(callable.Invoke(values)),
                _ => throw new InvalidOperationException("Value is not callable."),
            };
        }
        finally { _deadlineTicks = previousDeadline; }
    }

    private long NewDeadline() => _timeout is not { } timeout || timeout <= TimeSpan.Zero
        ? 0
        : Stopwatch.GetTimestamp() + Math.Max(1, (long)(timeout.TotalSeconds * Stopwatch.Frequency));

    private bool IsDeadlineExceeded() => _deadlineTicks != 0 && Stopwatch.GetTimestamp() >= _deadlineTicks;

    public ScriptValue CreateHostObject(IHostObjectTemplate template, object target)
    {
        if (target.GetType().IsValueType) return ScriptValue.FromObject(new NativeHostValue(this, template, target));
        var objects = _hostObjectCache.GetValue(target, static _ => new Dictionary<IHostObjectTemplate, NativeHostValue>());
        if (!objects.TryGetValue(template, out var host))
            objects[template] = host = new NativeHostValue(this, template, target);
        return ScriptValue.FromObject(host);
    }
    public bool TryGetHostTarget(ScriptValue value, out object? target)
    {
        if (value.Raw is NativeHostValue host) { target = host.Target; return true; }
        target = null; return false;
    }
    public ScriptValue CreateArray(ReadOnlySpan<ScriptValue> elements) => ScriptValue.FromArray(elements.ToArray().Select(ToObject).ToArray());
    public ScriptValue CreatePlainObject(IReadOnlyDictionary<string, ScriptValue> properties) => ScriptValue.FromObject(properties.ToDictionary(pair => pair.Key, pair => ToObject(pair.Value), StringComparer.Ordinal));
    public bool TryGetArrayItems(ScriptValue value, out IReadOnlyList<ScriptValue> items)
    {
        if (value.Raw is IReadOnlyList<object?> array) { items = array.Select(ToScriptValue).ToArray(); return true; }
        items = []; return false;
    }
    public bool TrySetArrayItem(ScriptValue value, int index, ScriptValue item)
    {
        if (value.Raw is not IList<object?> array || index < 0 || index >= array.Count) return false;
        array[index] = ToObject(item);
        return true;
    }
    public ScriptValue CreateUint8ClampedArray(ReadOnlySpan<byte> bytes) =>
        ScriptValue.FromObject(new NativeBuffer(ScriptBufferSourceKind.Uint8Clamped, bytes.ToArray(), 1));
    public bool TryGetBytes(ScriptValue value, out byte[] bytes)
    {
        if (value.Raw is NativeBuffer buffer && buffer.Kind is ScriptBufferSourceKind.Uint8 or ScriptBufferSourceKind.Uint8Clamped && buffer.IsAttached && buffer.ByteOffset == 0)
        {
            bytes = buffer.Bytes.AsSpan(buffer.ByteOffset, buffer.ByteLength).ToArray();
            return true;
        }
        bytes = [];
        return false;
    }
    public bool TrySetBytes(ScriptValue value, ReadOnlySpan<byte> bytes)
    {
        if (value.Raw is not NativeBuffer buffer || buffer.Kind is not (ScriptBufferSourceKind.Uint8 or ScriptBufferSourceKind.Uint8Clamped) || !buffer.IsAttached || buffer.ByteOffset != 0 || buffer.ByteLength != bytes.Length)
            return false;
        bytes.CopyTo(buffer.Bytes.AsSpan(buffer.ByteOffset, buffer.ByteLength));
        return true;
    }
    public bool TryGetArrayBufferBytes(ScriptValue value, out byte[] bytes)
    {
        if (value.Raw is NativeBuffer buffer && buffer.Kind == ScriptBufferSourceKind.ArrayBuffer && buffer.IsAttached)
        {
            bytes = buffer.Bytes.ToArray();
            return true;
        }
        bytes = [];
        return false;
    }
    public bool TryGetArrayBufferBytesOwned(ScriptValue value, out byte[] bytes)
    {
        if (value.Raw is NativeBuffer buffer && buffer.Kind == ScriptBufferSourceKind.ArrayBuffer && buffer.IsAttached)
        {
            bytes = buffer.Bytes;
            return true;
        }
        bytes = [];
        return false;
    }
    public bool TryGetBufferSourceInfo(ScriptValue value, out ScriptBufferSourceInfo info)
    {
        if (value.Raw is NativeBuffer buffer && buffer.IsAttached)
        {
            info = new(buffer.Kind, buffer.ByteLength, buffer.ByteOffset, buffer.ElementSize, buffer.Length);
            return true;
        }
        info = default;
        return false;
    }
    public bool TryCopyBufferSource(ScriptValue value, int byteOffset, Span<byte> destination)
    {
        if (value.Raw is not NativeBuffer buffer || !buffer.IsAttached || byteOffset < 0 || byteOffset > buffer.ByteLength - destination.Length) return false;
        buffer.Bytes.AsSpan(buffer.ByteOffset + byteOffset, destination.Length).CopyTo(destination);
        return true;
    }
    public bool TryWriteBufferSource(ScriptValue value, int byteOffset, ReadOnlySpan<byte> source)
    {
        if (value.Raw is not NativeBuffer buffer || !buffer.IsAttached || byteOffset < 0 || byteOffset > buffer.ByteLength - source.Length) return false;
        source.CopyTo(buffer.Bytes.AsSpan(buffer.ByteOffset + byteOffset, source.Length));
        return true;
    }
    public ScriptValue CreateTypedArray(ScriptBufferSourceKind kind, ReadOnlySpan<byte> bytes)
    {
        if (kind == ScriptBufferSourceKind.ArrayBuffer) return CreateArrayBuffer(bytes);
        var elementSize = ElementSize(kind);
        if (bytes.Length % elementSize != 0) throw new ArgumentException("Typed-array storage must be element aligned.", nameof(bytes));
        return ScriptValue.FromObject(new NativeBuffer(kind, bytes.ToArray(), elementSize));
    }
    public ScriptValue CreateArrayBuffer(ReadOnlySpan<byte> bytes) => ScriptValue.FromObject(new NativeBuffer(ScriptBufferSourceKind.ArrayBuffer, bytes.ToArray(), 1));
    public ScriptValue CreateArrayBufferOwned(byte[] bytes) => ScriptValue.FromObject(new NativeBuffer(ScriptBufferSourceKind.ArrayBuffer, bytes, 1));
    public bool TryDetachArrayBuffer(ScriptValue value)
    {
        if (value.Raw is not NativeBuffer buffer || buffer.Kind != ScriptBufferSourceKind.ArrayBuffer || !buffer.IsAttached) return false;
        buffer.Detach();
        return true;
    }
    public ScriptValue GetProperty(ScriptValue target, string name)
    {
        if (target.Raw is NativeHostValue host && host.TryGetValue(name, out var hostValue))
            return ToScriptValue(hostValue);
        if (target.Raw is JavaScriptEvaluator.RegexValue regex && regex.TryGetProperty(name, out var regexValue))
            return ToScriptValue(regexValue);
        if (target.Raw is NativeMap map && map.TryGetProperty(name, out var mapValue))
            return ToScriptValue(mapValue);
        if (target.Raw is NativeSet set && set.TryGetProperty(name, out var setValue))
            return ToScriptValue(setValue);
        if (target.Raw is NativeDate date && name == "getTime")
            return ToScriptValue(new NativeReceiverFunction((_, _) => date.Milliseconds));
        if (target.Raw is INativeBuffer buffer && buffer.TryGetProperty(name, out var bufferValue))
            return ToScriptValue(bufferValue);
        if (target.Raw is IReadOnlyList<object?> array && name == "length") return ScriptValue.FromNumber(array.Count);
        if (target.Raw is string text && name == "length") return ScriptValue.FromNumber(text.Length);
        if (target.Raw is IReadOnlyDictionary<string, object?> properties && properties.TryGetValue(name, out var item))
            return ToScriptValue(ReadProperty(item));
        return ScriptValue.Undefined;
    }
    public bool TrySetProperty(ScriptValue target, string name, ScriptValue value)
    {
        if (target.Raw is NativeHostValue host)
            return host.TrySetSetValue(name, ToObject(value)) || host.TrySetPrototypeValue(name, ToObject(value));
        if (target.Raw is IDictionary<string, object?> properties)
        {
            var assigned = ToObject(value);
            if (properties.TryGetValue(name, out var existing)
                && existing is NativePropertyAccessor accessor)
                return accessor.Set(properties, assigned);
            properties[name] = assigned;
            return true;
        }
        return false;
    }

    internal static object? ReadProperty(object? value) => value switch
    {
        NativePropertyAccessor accessor => accessor.Get(),
        NativePropertyGetter getter => getter.Get(),
        _ => value,
    };

    internal sealed class NativePropertyGetter(Func<object?> getter)
    {
        internal object? Get() => getter();
    }

    internal sealed class NativePropertyAccessor(Func<object?, object?>? getter, Func<object?, object?, object?>? setter)
    {
        internal object? Get(object? receiver = null) => getter?.Invoke(receiver);
        internal bool Set(object? receiver, object? value) { if (setter is null) return false; setter(receiver, value); return true; }
    }
    public void SetGlobalProperty(string name, ScriptValue value) { EnsureLive(); _bindings[name] = ToObject(value); }
    public ScriptValue GetGlobalProperty(string name) => _bindings.TryGetValue(name, out var value) ? ToScriptValue(value) : ScriptValue.Undefined;

    internal void InstallDomException()
    {
        var codes = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["IndexSizeError"] = 1d, ["DOMStringSizeError"] = 2d, ["HierarchyRequestError"] = 3d,
            ["WrongDocumentError"] = 4d, ["InvalidCharacterError"] = 5d, ["NoDataAllowedError"] = 6d,
            ["NoModificationAllowedError"] = 7d, ["NotFoundError"] = 8d, ["NotSupportedError"] = 9d,
            ["InUseAttributeError"] = 10d, ["InvalidStateError"] = 11d, ["SyntaxError"] = 12d,
            ["InvalidModificationError"] = 13d, ["NamespaceError"] = 14d, ["InvalidAccessError"] = 15d,
            ["SecurityError"] = 18d, ["NetworkError"] = 19d, ["AbortError"] = 20d,
            ["TimeoutError"] = 23d, ["QuotaExceededError"] = 22d, ["DataCloneError"] = 25d,
        };
        var constructor = new NativeFunction(args =>
        {
            var message = args.Count > 0 ? StringValue(args[0]) : string.Empty;
            var name = args.Count > 1 ? StringValue(args[1]) : "Error";
            var code = codes.TryGetValue(name, out var value) ? value : 0d;
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = name, ["message"] = message, ["code"] = code,
            };
        });
        foreach (var (name, value) in codes)
            constructor.SetProperty(name switch
            {
                "IndexSizeError" => "INDEX_SIZE_ERR", "DOMStringSizeError" => "DOMSTRING_SIZE_ERR",
                "HierarchyRequestError" => "HIERARCHY_REQUEST_ERR", "WrongDocumentError" => "WRONG_DOCUMENT_ERR",
                "InvalidCharacterError" => "INVALID_CHARACTER_ERR", "NoDataAllowedError" => "NO_DATA_ALLOWED_ERR",
                "NoModificationAllowedError" => "NO_MODIFICATION_ALLOWED_ERR", "NotFoundError" => "NOT_FOUND_ERR",
                "NotSupportedError" => "NOT_SUPPORTED_ERR", "InUseAttributeError" => "INUSE_ATTRIBUTE_ERR",
                "InvalidStateError" => "INVALID_STATE_ERR", "SyntaxError" => "SYNTAX_ERR",
                "InvalidModificationError" => "INVALID_MODIFICATION_ERR", "NamespaceError" => "NAMESPACE_ERR",
                "InvalidAccessError" => "INVALID_ACCESS_ERR", "SecurityError" => "SECURITY_ERR",
                "NetworkError" => "NETWORK_ERR", "AbortError" => "ABORT_ERR", "TimeoutError" => "TIMEOUT_ERR",
                "QuotaExceededError" => "QUOTA_EXCEEDED_ERR", "DataCloneError" => "DATA_CLONE_ERR", _ => name,
            }, value);
        _bindings["DOMException"] = constructor;
    }

    internal void InstallDomConstructors()
    {
        static NativeFunction Illegal(string name)
        {
            var constructor = new NativeFunction(_ => new Dictionary<string, object?> { ["__constructor"] = name });
            constructor.SetProperty("name", name);
            return constructor;
        }
        var node = Illegal("Node");
        var nodePrototype = new Dictionary<string, object?>(StringComparer.Ordinal);
        node.SetProperty("prototype", nodePrototype);
        _domPrototypes["Node"] = nodePrototype;
        foreach (var methodName in new[] { "isEqualNode", "compareDocumentPosition", "cloneNode", "normalize", "appendChild", "insertBefore", "removeChild", "replaceChild" })
            nodePrototype[methodName] = new NativeReceiverFunction((receiver, args) => InvokeDomPrototypeMethod(receiver, methodName, args));
        foreach (var (name, value) in new (string, double)[]
        {
            ("ELEMENT_NODE", 1), ("ATTRIBUTE_NODE", 2), ("TEXT_NODE", 3), ("CDATA_SECTION_NODE", 4),
            ("ENTITY_REFERENCE_NODE", 5), ("ENTITY_NODE", 6), ("PROCESSING_INSTRUCTION_NODE", 7),
            ("COMMENT_NODE", 8), ("DOCUMENT_NODE", 9), ("DOCUMENT_TYPE_NODE", 10),
            ("DOCUMENT_FRAGMENT_NODE", 11), ("NOTATION_NODE", 12), ("DOCUMENT_POSITION_DISCONNECTED", 1),
            ("DOCUMENT_POSITION_PRECEDING", 2), ("DOCUMENT_POSITION_FOLLOWING", 4),
            ("DOCUMENT_POSITION_CONTAINS", 8), ("DOCUMENT_POSITION_CONTAINED_BY", 16),
            ("DOCUMENT_POSITION_IMPLEMENTATION_SPECIFIC", 32),
        }) node.SetProperty(name, value);
        _bindings["Node"] = node;
        foreach (var name in new[]
        {
            "Document", "DocumentType", "DocumentFragment", "CharacterData", "Text", "Comment", "Element",
            "SVGElement", "HTMLElement", "HTMLMediaElement", "HTMLAudioElement", "HTMLVideoElement",
            "HTMLIFrameElement", "Attr", "NodeList", "HTMLCollection", "DOMTokenList", "DOMStringMap",
            "CSSStyleDeclaration", "NamedNodeMap", "HTMLHtmlElement", "HTMLHeadElement", "HTMLBodyElement",
            "HTMLTitleElement", "HTMLDivElement", "HTMLSpanElement", "HTMLParagraphElement", "HTMLAnchorElement",
            "HTMLInputElement", "HTMLButtonElement", "HTMLImageElement", "HTMLOptionElement", "HTMLSelectElement",
            "HTMLTextAreaElement", "HTMLTemplateElement", "HTMLScriptElement", "HTMLUnknownElement",
        })
        {
            var ctor = Illegal(name);
            var prototype = new Dictionary<string, object?>(StringComparer.Ordinal);
            prototype["__proto__"] = nodePrototype;
            ctor.SetProperty("prototype", prototype);
            _domPrototypes[name] = prototype;
            _bindings[name] = ctor;
        }

        void AddPrototypeMethods(string prototypeName, params string[] methodNames)
        {
            if (!_domPrototypes.TryGetValue(prototypeName, out var prototypeValue) || prototypeValue is not Dictionary<string, object?> prototype) return;
            foreach (var methodName in methodNames)
                prototype[methodName] = new NativeReceiverFunction((receiver, args) => InvokeDomPrototypeMethod(receiver, methodName, args));
        }

        AddPrototypeMethods("Document", "createElement", "createElementNS", "createTextNode", "createComment", "createDocumentFragment", "getElementById", "querySelector", "querySelectorAll", "getElementsByTagName", "getElementsByClassName", "createEvent", "createRange", "addEventListener", "removeEventListener", "dispatchEvent");
        AddPrototypeMethods("Element", "setAttribute", "getAttribute", "removeAttribute", "hasAttribute", "toggleAttribute", "querySelector", "querySelectorAll", "closest", "matches", "insertAdjacentHTML", "addEventListener", "removeEventListener", "dispatchEvent", "getBoundingClientRect", "focus", "blur");

        static double[] MatrixValues(object? value)
        {
            var result = new[] { 1d, 0d, 0d, 1d, 0d, 0d };
            if (value is IReadOnlyList<object?> list)
                for (var i = 0; i < Math.Min(6, list.Count); i++) result[i] = NumberValue(list[i]);
            else if (value is IReadOnlyDictionary<string, object?> map)
            {
                var names = new[] { "a", "b", "c", "d", "e", "f" };
                var aliases = new[] { "m11", "m12", "m21", "m22", "m41", "m42" };
                for (var i = 0; i < 6; i++)
                    if (map.TryGetValue(names[i], out var named)) result[i] = NumberValue(named);
                    else if (map.TryGetValue(aliases[i], out var alias)) result[i] = NumberValue(alias);
            }
            return result;
        }

        static double[] MatrixValuesFromReceiver(object? receiver)
        {
            if (receiver is not IReadOnlyDictionary<string, object?> map) return new[] { 1d, 0d, 0d, 1d, 0d, 0d };
            return [NumberValue(map.TryGetValue("a", out var a) ? a : null), NumberValue(map.TryGetValue("b", out var b) ? b : null),
                NumberValue(map.TryGetValue("c", out var c) ? c : null), NumberValue(map.TryGetValue("d", out var d) ? d : null),
                NumberValue(map.TryGetValue("e", out var e) ? e : null), NumberValue(map.TryGetValue("f", out var f) ? f : null)];
        }

        static Dictionary<string, object?> CreateMatrix(double[] values)
        {
            for (var i = 0; i < values.Length; i++) if (values[i] == 0) values[i] = 0;
            var result = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["a"] = values[0], ["b"] = values[1], ["c"] = values[2], ["d"] = values[3], ["e"] = values[4], ["f"] = values[5],
                ["m11"] = values[0], ["m12"] = values[1], ["m13"] = 0d, ["m14"] = 0d,
                ["m21"] = values[2], ["m22"] = values[3], ["m23"] = 0d, ["m24"] = 0d,
                ["m31"] = 0d, ["m32"] = 0d, ["m33"] = 1d, ["m34"] = 0d,
                ["m41"] = values[4], ["m42"] = values[5], ["m43"] = 0d, ["m44"] = 1d,
                ["is2D"] = true, ["isIdentity"] = values.SequenceEqual(new[] { 1d, 0d, 0d, 1d, 0d, 0d }),
            };
            result["toString"] = new NativeReceiverFunction((receiver, _) =>
                "matrix(" + string.Join(", ", MatrixValuesFromReceiver(receiver).Select(value => value.ToString(CultureInfo.InvariantCulture))) + ")");
            result["multiply"] = new NativeReceiverFunction((receiver, args) =>
            {
                var left = MatrixValuesFromReceiver(receiver);
                var right = MatrixValues(args.Count > 0 ? args[0] : null);
                return CreateMatrix([
                    left[0] * right[0] + left[2] * right[1], left[1] * right[0] + left[3] * right[1],
                    left[0] * right[2] + left[2] * right[3], left[1] * right[2] + left[3] * right[3],
                    left[0] * right[4] + left[2] * right[5] + left[4], left[1] * right[4] + left[3] * right[5] + left[5]]);
            });
            result["multiplySelf"] = new NativeReceiverFunction((receiver, args) =>
            {
                var left = MatrixValuesFromReceiver(receiver);
                var right = MatrixValues(args.Count > 0 ? args[0] : null);
                var product = new[]
                {
                    left[0] * right[0] + left[2] * right[1], left[1] * right[0] + left[3] * right[1],
                    left[0] * right[2] + left[2] * right[3], left[1] * right[2] + left[3] * right[3],
                    left[0] * right[4] + left[2] * right[5] + left[4], left[1] * right[4] + left[3] * right[5] + left[5],
                };
                if (receiver is IDictionary<string, object?> target)
                {
                    foreach (var pair in CreateMatrix(product)) target[pair.Key] = pair.Value;
                    return target;
                }
                return receiver;
            });
            static object? MutateMultiply(object? receiver, double[] right)
            {
                var left = MatrixValuesFromReceiver(receiver);
                var product = new[]
                {
                    left[0] * right[0] + left[2] * right[1], left[1] * right[0] + left[3] * right[1],
                    left[0] * right[2] + left[2] * right[3], left[1] * right[2] + left[3] * right[3],
                    left[0] * right[4] + left[2] * right[5] + left[4], left[1] * right[4] + left[3] * right[5] + left[5],
                };
                if (receiver is IDictionary<string, object?> target)
                {
                    foreach (var pair in CreateMatrix(product)) target[pair.Key] = pair.Value;
                    return target;
                }
                return receiver;
            }
            result["translate"] = new NativeReceiverFunction((receiver, args) =>
            {
                var x = args.Count > 0 ? NumberValue(args[0]) : 0d;
                var y = args.Count > 1 ? NumberValue(args[1]) : 0d;
                return MutateMultiply(receiver, [1, 0, 0, 1, x, y]);
            });
            result["scale"] = new NativeReceiverFunction((receiver, args) =>
            {
                var x = args.Count > 0 ? NumberValue(args[0]) : 1d;
                var y = args.Count > 1 ? NumberValue(args[1]) : x;
                return MutateMultiply(receiver, [x, 0, 0, y, 0, 0]);
            });
            result["rotate"] = new NativeReceiverFunction((receiver, args) =>
            {
                var angle = (args.Count > 0 ? NumberValue(args[0]) : 0d) * Math.PI / 180d;
                var c = Math.Cos(angle); var s = Math.Sin(angle);
                return MutateMultiply(receiver, [c, s, -s, c, 0, 0]);
            });
            result["flipX"] = new NativeReceiverFunction((receiver, _) => MutateMultiply(receiver, [-1, 0, 0, 1, 0, 0]));
            result["flipY"] = new NativeReceiverFunction((receiver, _) => MutateMultiply(receiver, [1, 0, 0, -1, 0, 0]));
            result["skewX"] = new NativeReceiverFunction((receiver, args) =>
            {
                var tangent = Math.Tan((args.Count > 0 ? NumberValue(args[0]) : 0d) * Math.PI / 180d);
                return MutateMultiply(receiver, [1, 0, tangent, 1, 0, 0]);
            });
            result["skewY"] = new NativeReceiverFunction((receiver, args) =>
            {
                var tangent = Math.Tan((args.Count > 0 ? NumberValue(args[0]) : 0d) * Math.PI / 180d);
                return MutateMultiply(receiver, [1, tangent, 0, 1, 0, 0]);
            });
            result["transformPoint"] = new NativeReceiverFunction((receiver, args) =>
            {
                var matrix = MatrixValuesFromReceiver(receiver);
                var point = args.Count > 0 && args[0] is IReadOnlyDictionary<string, object?> map ? map : null;
                var x = NumberValue(point is not null && point.TryGetValue("x", out var px) ? px : null);
                var y = NumberValue(point is not null && point.TryGetValue("y", out var py) ? py : null);
                var z = NumberValue(point is not null && point.TryGetValue("z", out var pz) ? pz : null);
                var w = NumberValue(point is not null && point.TryGetValue("w", out var pw) ? pw : 1d);
                return new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["x"] = matrix[0] * x + matrix[2] * y + matrix[4] * w,
                    ["y"] = matrix[1] * x + matrix[3] * y + matrix[5] * w,
                    ["z"] = z,
                    ["w"] = w,
                };
            });
            result["inverse"] = new NativeReceiverFunction((receiver, _) =>
            {
                var m = MatrixValuesFromReceiver(receiver);
                var determinant = m[0] * m[3] - m[1] * m[2];
                if (determinant == 0) throw new InvalidOperationException("The matrix is not invertible.");
                return CreateMatrix([m[3] / determinant, -m[1] / determinant, -m[2] / determinant, m[0] / determinant,
                    (m[2] * m[5] - m[3] * m[4]) / determinant, (m[1] * m[4] - m[0] * m[5]) / determinant]);
            });
            return result;
        }

        _bindings["DOMMatrix"] = _bindings["DOMMatrixReadOnly"] = new NativeFunction(args =>
        {
            var values = args.Count >= 6 ? args.Take(6).Select(NumberValue).ToArray() : MatrixValues(args.Count > 0 ? args[0] : null);
            return CreateMatrix(values);
        });
    }

    private static object? InvokeDomPrototypeMethod(object? receiver, string name, IReadOnlyList<object?> args)
    {
        if (receiver is not NativeHostValue host || !host.Template.TryGetMethod(name, out var method))
            return null;
        return ToObject(method(host.Target, ScriptValue.FromObject(host), args.Select(ToScriptValue).ToArray()));
    }
    public void PerformMicrotaskCheckpoint()
    {
        EnsureLive();
        var budget = _maxSteps;
        while (_microtasks.Count > 0 && budget-- > 0) _microtasks.Dequeue()();
        if (_microtasks.Count > 0) throw new InvalidOperationException("The microtask queue exceeded its evaluation step limit.");
    }
    public void Interrupt() => _interrupted = true;
    public IScriptEngine CreateIsolated() => new NativeJavaScriptEngine(_maxSteps, _timeout);

    string IStructuredCloneEngine.ClassifyCloneObject(ScriptValue value)
    {
        if (value.Raw is NativeBuffer buffer)
            return buffer.Kind == ScriptBufferSourceKind.ArrayBuffer ? "buffer" : "typedarray";
        if (value.Raw is NativeMap) return "map";
        if (value.Raw is NativeSet) return "set";
        if (value.Raw is NativeDate) return "date";
        if (value.Raw is NativeError) return "error";
        if (value.Raw is JavaScriptEvaluator.RegexValue) return "regexp";
        if (value.Raw is IReadOnlyList<object?>) return "array";
        if (value.Raw is IReadOnlyDictionary<string, object?>) return "object";
        return "unsupported";
    }

    (string Source, string Flags) IStructuredCloneEngine.GetCloneRegExp(ScriptValue value) =>
        value.Raw is JavaScriptEvaluator.RegexValue regex
            ? (regex.Source, regex.Flags)
            : throw new ArgumentException("The value is not a regular expression.", nameof(value));
    public void Dispose() { _disposed = true; _bindings.Clear(); _microtasks.Clear(); }

    private void EnsureLive() { if (_disposed) throw new ObjectDisposedException(nameof(NativeJavaScriptEngine)); }

    private object CreateDynamicFunction(IReadOnlyList<object?> args)
    {
        var parameterCount = Math.Max(0, args.Count - 1);
        var parameters = Enumerable.Range(0, parameterCount).Select(index => StringValue(args[index])).ToArray();
        var source = args.Count == 0 ? string.Empty : StringValue(args[^1]);
        var body = new JavaScriptParser(source).ParseProgram();
        var captured = _bindings;
        var isolatedScope = source.Contains("with (scope)", StringComparison.Ordinal);
        return (Func<IReadOnlyList<object?>, object?>)(values =>
        {
            var local = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < parameters.Length; i++) local[parameters[i]] = i < values.Count ? values[i] : null;
            local["arguments"] = values.ToList();
            return new JavaScriptEvaluator(local, _maxSteps, isolatedScope ? null : captured, parentEvaluator: null, preserveUndefined: true).Execute(body);
        });
    }

    private static NativeFunction CreateErrorConstructor(string name) => new(args => new NativeError(name, args.Count > 0 ? StringValue(args[0]) : string.Empty));
    private void Enqueue(Action action) { EnsureLive(); _microtasks.Enqueue(action); }
    private static object? ToObject(ScriptValue value) => value.Kind switch
    {
        ScriptValueKind.Undefined => JavaScriptEvaluator.UndefinedValue.Instance,
        ScriptValueKind.Null => null,
        ScriptValueKind.Boolean => value.AsBoolean(),
        ScriptValueKind.Number => value.AsNumber(),
        ScriptValueKind.String => value.AsString(),
        ScriptValueKind.Array when value.Raw is IEnumerable<ScriptValue> items => items.Select(ToObject).ToList(),
        ScriptValueKind.Array when value.Raw is IEnumerable<object?> items => items.ToList(),
        _ => value.Raw,
    };
    private static ScriptValue ToScriptValue(object? value) => value switch
    {
        ScriptValue scriptValue => scriptValue,
        JavaScriptEvaluator.UndefinedValue => ScriptValue.Undefined,
        null => ScriptValue.Null,
        bool boolean => ScriptValue.FromBoolean(boolean),
        string text => ScriptValue.FromString(text),
        double number => ScriptValue.FromNumber(number),
        float number => ScriptValue.FromNumber(number),
        int number => ScriptValue.FromNumber(number),
        IReadOnlyList<object?> array => ScriptValue.FromArray(array),
        Func<IReadOnlyList<object?>, object?> function => ScriptValue.FromFunction(function),
        INativeCallable function => ScriptValue.FromFunction(function),
        _ => ScriptValue.FromObject(value),
    };

    private static int ElementSize(ScriptBufferSourceKind kind) => kind switch
    {
        ScriptBufferSourceKind.Int8 or ScriptBufferSourceKind.Uint8 or ScriptBufferSourceKind.Uint8Clamped => 1,
        ScriptBufferSourceKind.Int16 or ScriptBufferSourceKind.Uint16 or ScriptBufferSourceKind.Float16 => 2,
        ScriptBufferSourceKind.Int32 or ScriptBufferSourceKind.Uint32 or ScriptBufferSourceKind.Float32 => 4,
        ScriptBufferSourceKind.Float64 or ScriptBufferSourceKind.BigInt64 or ScriptBufferSourceKind.BigUint64 => 8,
        _ => 1,
    };

    private static double Number(IReadOnlyList<object?> args, int index) => index < args.Count && args[index] is double number ? number : 0;
    private static double NumberValue(object? value) => value switch
    {
        null => 0,
        bool boolean => boolean ? 1 : 0,
        double number => number,
        int number => number,
        string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
        _ => double.NaN,
    };
    private static double ParseInteger(string text)
    {
        text = text.Trim();
        var length = 0;
        if (text.StartsWith('+') || text.StartsWith('-')) length++;
        while (length < text.Length && char.IsDigit(text[length])) length++;
        return length == 0 || (length == 1 && (text[0] is '+' or '-')) || !double.TryParse(text[..length], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? double.NaN : parsed;
    }
    private static bool TruthValue(object? value) => value switch
    {
        null or JavaScriptEvaluator.UndefinedValue => false,
        bool boolean => boolean,
        double number => number != 0 && !double.IsNaN(number),
        string text => text.Length != 0,
        _ => true,
    };
    private static string StringValue(object? value) => value switch
    {
        null => "null",
        JavaScriptEvaluator.UndefinedValue => "undefined",
        bool boolean => boolean ? "true" : "false",
        double number => number.ToString("G15", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    internal interface INativeCallable
    {
        object? Invoke(IReadOnlyList<object?> args);
        bool TryGetProperty(string name, out object? value);
        IEnumerable<string> GetPropertyNames();
    }

    internal sealed class NativeFunction(Func<IReadOnlyList<object?>, object?> callback) : INativeCallable
    {
        private readonly Dictionary<string, object?> _properties = new(StringComparer.Ordinal);
        public object? Invoke(IReadOnlyList<object?> args) => callback(args);
        public void SetProperty(string name, object? value) => _properties[name] = value;
        public bool TryGetProperty(string name, out object? value) => _properties.TryGetValue(name, out value);
        public IEnumerable<string> GetPropertyNames() => _properties.Keys;
    }

    internal static bool IsCallable(object? value) => value is Func<IReadOnlyList<object?>, object?> or INativeCallable;
    internal static object? InvokeCallable(object? value, IReadOnlyList<object?> args) => value switch
    {
        Func<IReadOnlyList<object?>, object?> function => function(args),
        INativeCallable callable => callable.Invoke(args),
        _ => null,
    };

    internal sealed class NativeReceiverFunction(Func<object?, IReadOnlyList<object?>, object?> callback) : INativeCallable
    {
        private readonly Dictionary<string, object?> _properties = new(StringComparer.Ordinal);
        internal object? Invoke(object? receiver, IReadOnlyList<object?> args) => callback(receiver, args);
        internal bool TryGetProperty(string name, out object? value) => _properties.TryGetValue(name, out value);
        object? INativeCallable.Invoke(IReadOnlyList<object?> args) => callback(null, args);
        bool INativeCallable.TryGetProperty(string name, out object? value) => _properties.TryGetValue(name, out value);
        IEnumerable<string> INativeCallable.GetPropertyNames() => _properties.Keys;
        internal void SetProperty(string name, object? value) => _properties[name] = value;
    }

    internal sealed class NativeWeakMap
    {
        private readonly Dictionary<object, object?> _entries = new(ReferenceEqualityComparer.Instance);
        internal object Set(object key, object? value) { _entries[key] = value; return this; }
        internal object? Get(object key) => _entries.TryGetValue(key, out var value) ? value : JavaScriptEvaluator.UndefinedValue.Instance;
        internal bool Has(object key) => _entries.ContainsKey(key);
        internal bool Delete(object key) => _entries.Remove(key);
        internal bool TryGetProperty(string name, out object? value)
        {
            value = name switch
            {
                "set" => new NativeReceiverFunction((_, args) => args.Count > 1 && args[0] is { } key ? Set(key, args[1]) : this),
                "get" => new NativeReceiverFunction((_, args) => args.Count > 0 && args[0] is { } key ? Get(key) : JavaScriptEvaluator.UndefinedValue.Instance),
                "has" => new NativeReceiverFunction((_, args) => args.Count > 0 && args[0] is { } key && Has(key)),
                "delete" => new NativeReceiverFunction((_, args) => args.Count > 0 && args[0] is { } key && Delete(key)),
                _ => null,
            };
            return value is not null;
        }
    }

    internal sealed class NativeWeakSet
    {
        private readonly HashSet<object> _entries = new(ReferenceEqualityComparer.Instance);
        internal object Add(object key) { _entries.Add(key); return this; }
        internal bool Has(object key) => _entries.Contains(key);
        internal bool Delete(object key) => _entries.Remove(key);
        internal bool TryGetProperty(string name, out object? value)
        {
            value = name switch
            {
                "add" => new NativeReceiverFunction((_, args) => args.Count > 0 && args[0] is { } key ? Add(key) : this),
                "has" => new NativeReceiverFunction((_, args) => args.Count > 0 && args[0] is { } key && Has(key)),
                "delete" => new NativeReceiverFunction((_, args) => args.Count > 0 && args[0] is { } key && Delete(key)),
                _ => null,
            };
            return value is not null;
        }
    }

    internal sealed class NativeSet(object? initial)
    {
        private readonly List<object?> _values = initial is IReadOnlyList<object?> items
            ? items.Distinct().ToList()
            : [];
        internal object Add(object? value) { if (!_values.Contains(value)) _values.Add(value); return this; }
        internal bool Has(object? value) => _values.Contains(value);
        internal bool Delete(object? value) => _values.Remove(value);
        internal void ForEach(object callback) { if (IsCallable(callback)) foreach (var item in _values) InvokeCallable(callback, [item, item, this]); }
        internal object?[] Values() => _values.ToArray();
        internal bool TryGetProperty(string name, out object? value)
        {
            value = name switch
            {
                "add" => new NativeReceiverFunction((_, args) => Add(args.Count > 0 ? args[0] : null)),
                "has" => new NativeReceiverFunction((_, args) => Has(args.Count > 0 ? args[0] : null)),
                "delete" => new NativeReceiverFunction((_, args) => Delete(args.Count > 0 ? args[0] : null)),
                "values" or "keys" => new NativeReceiverFunction((_, _) => Values()),
                "forEach" => new NativeReceiverFunction((_, args) => { if (args.Count > 0 && IsCallable(args[0])) foreach (var item in _values) InvokeCallable(args[0], [item, item, this]); return JavaScriptEvaluator.UndefinedValue.Instance; }),
                "size" => (double)_values.Count,
                _ => null,
            };
            return value is not null;
        }
    }

    internal sealed class NativeMap
    {
        private readonly Dictionary<object, object?> _values = new();
        internal NativeMap(object? initial)
        {
            if (initial is IReadOnlyList<object?> entries)
                foreach (var entry in entries) if (entry is IReadOnlyList<object?> pair && pair.Count > 1 && pair[0] is { } key) _values[key] = pair[1];
        }
        internal object Set(object key, object? value) { _values[key] = value; return this; }
        internal object? Get(object key) => _values.TryGetValue(key, out var value) ? value : JavaScriptEvaluator.UndefinedValue.Instance;
        internal bool Has(object key) => _values.ContainsKey(key);
        internal bool Delete(object key) => _values.Remove(key);
        internal void ForEach(object callback) { if (IsCallable(callback)) foreach (var pair in _values) InvokeCallable(callback, [pair.Value, pair.Key, this]); }
        internal object?[] Entries() => _values.Select(pair => (object?)new object?[] { pair.Key, pair.Value }).ToArray();
        internal bool TryGetProperty(string name, out object? value)
        {
            value = name switch
            {
                "set" => new NativeReceiverFunction((_, args) => args.Count > 1 && args[0] is { } key ? Set(key, args[1]) : this),
                "get" => new NativeReceiverFunction((_, args) => args.Count > 0 && args[0] is { } key ? Get(key) : JavaScriptEvaluator.UndefinedValue.Instance),
                "has" => new NativeReceiverFunction((_, args) => args.Count > 0 && args[0] is { } key && Has(key)),
                "delete" => new NativeReceiverFunction((_, args) => args.Count > 0 && args[0] is { } key && Delete(key)),
                "entries" or "keys" or "values" => new NativeReceiverFunction((_, _) => name == "values" ? _values.Values.ToArray() : name == "keys" ? _values.Keys.ToArray() : Entries()),
                "forEach" => new NativeReceiverFunction((_, args) => { if (args.Count > 0 && IsCallable(args[0])) foreach (var pair in _values) InvokeCallable(args[0], [pair.Value, pair.Key, this]); return JavaScriptEvaluator.UndefinedValue.Instance; }),
                "size" => (double)_values.Count,
                _ => null,
            };
            return value is not null;
        }
    }

    internal sealed class NativeDate(double milliseconds)
    {
        internal double Milliseconds { get; } = milliseconds;
    }

    internal sealed class NativeError(string name, string message) : IReadOnlyDictionary<string, object?>
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal)
        {
            ["name"] = name, ["message"] = message, ["stack"] = string.IsNullOrEmpty(message) ? name : $"{name}: {message}",
        };
        public IEnumerable<string> Keys => _values.Keys;
        public IEnumerable<object?> Values => _values.Values;
        public int Count => _values.Count;
        public object? this[string key] => _values[key];
        public bool ContainsKey(string key) => _values.ContainsKey(key);
        public bool TryGetValue(string key, out object? value) => _values.TryGetValue(key, out value);
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _values.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static object? ParseJson(string source)
    {
        using var document = JsonDocument.Parse(source);
        return ParseJson(document.RootElement);
    }
    private static object? ParseJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when element.TryGetDouble(out var number) => number,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Array => element.EnumerateArray().Select(ParseJson).ToArray(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(property => property.Name, property => ParseJson(property.Value), StringComparer.Ordinal),
        _ => null,
    };

    internal interface INativeBuffer
    {
        string TypeName { get; }
        bool TryGetProperty(string name, out object? value);
        bool TryGetIndex(int index, out object? value);
        bool TrySetIndex(int index, object? value);
    }

    internal interface INativePromise
    {
        bool TryGetProperty(string name, out object? value);
    }

    private sealed class NativePromise : INativePromise
    {
        private readonly NativeJavaScriptEngine _engine;
        private object? _value;
        private bool _rejected;
        private bool _settled;
        private readonly List<(object? Fulfilled, object? Rejected, NativePromise Next)> _continuations = [];
        internal NativePromise(NativeJavaScriptEngine engine, object? value, bool rejected, bool settled = true)
        {
            _engine = engine; _value = value; _rejected = rejected; _settled = settled;
        }
        internal void Resolve(object? value) => Settle(value, rejected: false);
        internal void Reject(object? value) => Settle(value, rejected: true);
        public bool TryGetProperty(string name, out object? value)
        {
            value = name switch
            {
                "then" => (Func<IReadOnlyList<object?>, object?>)(args => Chain(args.Count > 0 ? args[0] : null, args.Count > 1 ? args[1] : null)),
                "catch" => (Func<IReadOnlyList<object?>, object?>)(args => Chain(null, args.Count > 0 ? args[0] : null)),
                "finally" => (Func<IReadOnlyList<object?>, object?>)(args => Finally(args.Count > 0 ? args[0] : null)),
                _ => null,
            };
            return value is not null;
        }
        private NativePromise Chain(object? onFulfilled, object? onRejected)
        {
            var next = new NativePromise(_engine, null, false);
            next._settled = false;
            if (_settled) ScheduleContinuation(onFulfilled, onRejected, next);
            else _continuations.Add((onFulfilled, onRejected, next));
            return next;
        }
        private NativePromise Finally(object? callback)
        {
            var next = new NativePromise(_engine, null, false);
            next._settled = false;
            if (_settled) ScheduleFinally(callback, next);
            else _continuations.Add((new NativeFunction(_ => { ScheduleFinally(callback, next); return null; }), null, next));
            return next;
        }
        private void Settle(object? value, bool rejected)
        {
            if (_settled) return;
            _value = value;
            _rejected = rejected;
            _settled = true;
            foreach (var continuation in _continuations)
                ScheduleContinuation(continuation.Fulfilled, continuation.Rejected, continuation.Next);
            _continuations.Clear();
        }
        private void ScheduleContinuation(object? onFulfilled, object? onRejected, NativePromise next)
        {
            _engine.Enqueue(() =>
            {
                var callback = _rejected ? onRejected : onFulfilled;
                if (!IsCallable(callback)) { next.Settle(_value, _rejected); return; }
                try
                {
                    var result = InvokeCallable(callback, [_value]);
                    if (result is NativePromise promise)
                    {
                        if (promise._settled) next.Settle(promise._value, promise._rejected);
                        else promise._continuations.Add((new NativeFunction(args => { next.Resolve(args.Count > 0 ? args[0] : null); return null; }), new NativeFunction(args => { next.Reject(args.Count > 0 ? args[0] : null); return null; }), next));
                    }
                    else next.Settle(result, rejected: false);
                }
                catch (Exception error)
                {
                    next.Settle(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["name"] = error is WebIdlException webError ? webError.Name : "Error",
                        ["message"] = error.Message,
                    }, rejected: true);
                }
            });
        }
        private void ScheduleFinally(object? callback, NativePromise next)
        {
            ScheduleContinuation(callback, null, next);
        }
    }

    private sealed class NativeBuffer : INativeBuffer
    {
        private sealed class BufferStorage(byte[] bytes)
        {
            internal byte[] Bytes { get; set; } = bytes;
            internal bool Detached { get; set; }
        }
        private readonly BufferStorage _storage;
        private readonly int _offset;
        private int _viewByteLength;
        internal NativeBuffer(ScriptBufferSourceKind kind, byte[] bytes, int elementSize)
            : this(kind, new BufferStorage(bytes), 0, bytes.Length, elementSize) { }
        private NativeBuffer(ScriptBufferSourceKind kind, BufferStorage storage, int offset, int viewByteLength, int elementSize)
        {
            Kind = kind;
            _storage = storage;
            _offset = offset;
            _viewByteLength = viewByteLength;
            ElementSize = elementSize;
        }
        internal ScriptBufferSourceKind Kind { get; }
        public string TypeName => Kind switch
        {
            ScriptBufferSourceKind.Uint8 => "Uint8Array",
            ScriptBufferSourceKind.Uint8Clamped => "Uint8ClampedArray",
            ScriptBufferSourceKind.Int8 => "Int8Array",
            ScriptBufferSourceKind.Uint16 => "Uint16Array",
            ScriptBufferSourceKind.Int16 => "Int16Array",
            ScriptBufferSourceKind.Uint32 => "Uint32Array",
            ScriptBufferSourceKind.Int32 => "Int32Array",
            ScriptBufferSourceKind.Float32 => "Float32Array",
            ScriptBufferSourceKind.Float64 => "Float64Array",
            ScriptBufferSourceKind.BigInt64 => "BigInt64Array",
            ScriptBufferSourceKind.BigUint64 => "BigUint64Array",
            ScriptBufferSourceKind.DataView => "DataView",
            _ => "ArrayBuffer",
        };
        internal byte[] Bytes => _storage.Bytes;
        internal int ElementSize { get; }
        internal int ByteOffset => IsAttached ? _offset : 0;
        internal int ByteLength => IsAttached ? _viewByteLength : 0;
        internal int Length => IsAttached ? _viewByteLength / ElementSize : 0;
        internal bool IsAttached => !_storage.Detached;
        internal void Detach() { _storage.Bytes = []; _storage.Detached = true; _viewByteLength = 0; }
        public bool TryGetProperty(string name, out object? value)
        {
            if (!IsAttached) { value = name is "byteLength" or "length" or "byteOffset" ? 0d : null; return name is "byteLength" or "length" or "byteOffset"; }
            value = name switch
            {
                "byteLength" => (double)ByteLength,
                "length" => (double)Length,
                "byteOffset" => (double)ByteOffset,
                "buffer" => new NativeBuffer(ScriptBufferSourceKind.ArrayBuffer, _storage, 0, Bytes.Length, 1),
                "slice" => (Func<IReadOnlyList<object?>, object?>)(args =>
                {
                    var start = args.Count > 0 ? RelativeIndex(Number(args[0]), Length) : 0;
                    var end = args.Count > 1 ? RelativeIndex(Number(args[1]), Length) : Length;
                    if (end < start) end = start;
                    return new NativeBuffer(Kind, Bytes.AsSpan(_offset + start * ElementSize, (end - start) * ElementSize).ToArray(), ElementSize);
                }),
                "subarray" => (Func<IReadOnlyList<object?>, object?>)(args =>
                {
                    var start = args.Count > 0 ? RelativeIndex(Number(args[0]), Length) : 0;
                    var end = args.Count > 1 ? RelativeIndex(Number(args[1]), Length) : Length;
                    if (end < start) end = start;
                    return new NativeBuffer(Kind, _storage, _offset + start * ElementSize, (end - start) * ElementSize, ElementSize);
                }),
                "set" => (Func<IReadOnlyList<object?>, object?>)(args =>
                {
                    if (args.Count == 0) return null;
                    var source = args[0] is NativeBuffer sourceBuffer
                        ? sourceBuffer.Bytes.AsSpan(sourceBuffer.ByteOffset, sourceBuffer.ByteLength).ToArray()
                        : EncodeElements(Kind, args[0] is IReadOnlyList<object?> values ? values : []);
                    var offset = args.Count > 1 ? Math.Max(0, (int)Number(args[1])) * ElementSize : 0;
                    if (offset < ByteLength) source.AsSpan(0, Math.Min(source.Length, ByteLength - offset)).CopyTo(Bytes.AsSpan(_offset + offset));
                    return null;
                }),
                _ => null,
            };
            return name is "byteLength" or "length" or "byteOffset" or "buffer" or "slice" or "subarray" or "set";
        }
        private static double Number(object? value) => value switch
        {
            double number => number,
            int integer => integer,
            long integer => integer,
            _ when double.TryParse(value?.ToString(), out var parsed) => parsed,
            _ => 0,
        };
        private static int RelativeIndex(double value, int length)
        {
            var index = (int)value;
            return index < 0 ? Math.Max(length + index, 0) : Math.Min(index, length);
        }
        public bool TryGetIndex(int index, out object? value)
        {
            if (!IsAttached || Kind == ScriptBufferSourceKind.ArrayBuffer || index < 0 || index >= Length) { value = null; return false; }
            value = ReadElement(index);
            return true;
        }
        public bool TrySetIndex(int index, object? value)
        {
            if (!IsAttached || Kind == ScriptBufferSourceKind.ArrayBuffer || index < 0 || index >= Length) return false;
            var number = value switch { double d => d, int i => i, _ => double.NaN };
            if (double.IsNaN(number)) return false;
            WriteElement(index, number);
            return true;
        }
        private double ReadElement(int index)
        {
            var span = Bytes.AsSpan(_offset + index * ElementSize, ElementSize);
            return Kind switch
            {
                ScriptBufferSourceKind.Uint8 or ScriptBufferSourceKind.Uint8Clamped => span[0],
                ScriptBufferSourceKind.Int8 => unchecked((sbyte)span[0]),
                ScriptBufferSourceKind.Uint16 => BinaryPrimitives.ReadUInt16LittleEndian(span),
                ScriptBufferSourceKind.Int16 => BinaryPrimitives.ReadInt16LittleEndian(span),
                ScriptBufferSourceKind.Uint32 => BinaryPrimitives.ReadUInt32LittleEndian(span),
                ScriptBufferSourceKind.Int32 => BinaryPrimitives.ReadInt32LittleEndian(span),
                ScriptBufferSourceKind.Float32 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(span)),
                ScriptBufferSourceKind.Float64 => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(span)),
                _ => span[0],
            };
        }
        private void WriteElement(int index, double number)
        {
            var span = Bytes.AsSpan(_offset + index * ElementSize, ElementSize);
            switch (Kind)
            {
                case ScriptBufferSourceKind.Uint8 or ScriptBufferSourceKind.Uint8Clamped:
                    span[0] = unchecked((byte)Math.Clamp((int)number, 0, 255)); break;
                case ScriptBufferSourceKind.Int8:
                    span[0] = unchecked((byte)(sbyte)Math.Clamp((int)number, -128, 127)); break;
                case ScriptBufferSourceKind.Uint16: BinaryPrimitives.WriteUInt16LittleEndian(span, unchecked((ushort)number)); break;
                case ScriptBufferSourceKind.Int16: BinaryPrimitives.WriteInt16LittleEndian(span, unchecked((short)number)); break;
                case ScriptBufferSourceKind.Uint32: BinaryPrimitives.WriteUInt32LittleEndian(span, unchecked((uint)number)); break;
                case ScriptBufferSourceKind.Int32: BinaryPrimitives.WriteInt32LittleEndian(span, unchecked((int)number)); break;
                case ScriptBufferSourceKind.Float32: BinaryPrimitives.WriteInt32LittleEndian(span, BitConverter.SingleToInt32Bits((float)number)); break;
                case ScriptBufferSourceKind.Float64: BinaryPrimitives.WriteInt64LittleEndian(span, BitConverter.DoubleToInt64Bits(number)); break;
                default: span[0] = unchecked((byte)number); break;
            }
        }
    }

    internal interface INativeHostObject
    {
        bool TryGetValue(string name, out object? value);
        bool TryGetPrototypeValue(string name, out object? value);
        bool TrySetPrototypeValue(string name, object? value);
        bool TrySetSetValue(string name, object? value);
        bool TryDeleteValue(string name);
        bool TryGetIndex(int index, out object? value);
        bool Contains(string name);
    }

    internal sealed class NativeHostValue : IReadOnlyDictionary<string, object?>, INativeHostObject
    {
        internal NativeHostValue(NativeJavaScriptEngine engine, IHostObjectTemplate template, object target) { Engine = engine; Template = template; Target = target; }
        internal NativeJavaScriptEngine Engine { get; }
        internal IHostObjectTemplate Template { get; }
        internal object Target { get; }
        private readonly Dictionary<string, object?> _expandos = new(StringComparer.Ordinal);
        private readonly Dictionary<string, NativeReceiverFunction> _methods = new(StringComparer.Ordinal);

        public IEnumerable<string> Keys => Names();
        public IEnumerable<object?> Values => Names().Select(name => TryGetValue(name, out var value) ? value : null);
        public int Count => Names().Count();
        public object? this[string key] => TryGetValue(key, out var value) ? value : null;
        public bool ContainsKey(string key) => Contains(key);
        public bool TryGetValue(string name, out object? value)
        {
            if (_expandos.TryGetValue(name, out value)) return true;
            // DOM interface prototypes are script-visible and may override native methods/accessors.
            // Resolve them before the generated template so assignments such as
            // Node.prototype.appendChild = ... observe normal JavaScript lookup semantics.
            if (Engine.TryGetDomPrototypeValue(Template.Name, name, this, out value)) return true;
            if (Template.TryGetProperty(name, out var getter, out _))
            {
                value = ToObject(getter(Target, ScriptValue.FromObject(this)));
                return true;
            }
            if (Template.TryGetMethod(name, out var method))
            {
                if (!_methods.TryGetValue(name, out var receiverMethod))
                {
                    receiverMethod = new NativeReceiverFunction((receiver, args) =>
                    {
                        var host = receiver as NativeHostValue ?? this;
                        return ToObject(method(host.Target, ScriptValue.FromObject(host), args.Select(ToScriptValue).ToArray()));
                    });
                    _methods[name] = receiverMethod;
                }
                value = receiverMethod;
                return true;
            }
            if (Template.NamedGetter is not null)
            {
                value = ToObject(Template.NamedGetter(Target, ScriptValue.FromObject(this), name));
                return true;
            }
            value = null;
            return false;
        }
        public bool TryGetPrototypeValue(string name, out object? value) => Engine.TryGetDomPrototypeValue(Template.Name, name, this, out value);
        public bool TrySetPrototypeValue(string name, object? value) => Engine.TrySetDomPrototypeValue(Template.Name, name, value, this);
        public bool TrySetSetValue(string name, object? value)
        {
            if (Template.TryGetProperty(name, out _, out var setter) && setter is not null)
            {
                setter(Target, ScriptValue.FromObject(this), ToScriptValue(value));
                return true;
            }
            // Script-installed Web IDL accessors live on the interface prototype
            // (for example HTMLElement.prototype.onclick). Honour a prototype
            // setter before creating an expando that would shadow that accessor.
            if (Engine.TrySetDomPrototypeValue(Template.Name, name, value, this)) return true;
            if (Template.NamedSetter is not null)
            {
                Template.NamedSetter(Target, ScriptValue.FromObject(this), name, ToScriptValue(value));
                return true;
            }
            _expandos[name] = value;
            return true;
        }
        public bool TryDeleteValue(string name) => _expandos.Remove(name) || (Template.NamedDeleter?.Invoke(Target, ScriptValue.FromObject(this), name) ?? false);
        public bool TryGetIndex(int index, out object? value)
        {
            if (Template.IndexedGetter is not null)
            {
                value = ToObject(Template.IndexedGetter(Target, ScriptValue.FromObject(this), index));
                return true;
            }
            value = null;
            return false;
        }
        public bool Contains(string name) => _expandos.ContainsKey(name) || Template.TryGetProperty(name, out _, out _) || Template.TryGetMethod(name, out _) || Template.NamedGetter is not null;
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => Names().Select(name => new KeyValuePair<string, object?>(name, this[name])).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        private IEnumerable<string> Names() => _expandos.Keys.Concat(Template.PropertyNames).Concat(Template.MethodNames).Concat(Template.DynamicPropertyEnumerator?.Invoke(Target, ScriptValue.FromObject(this)) ?? []).Distinct(StringComparer.Ordinal);
    }

    private sealed class NativeProxyValue(object? target, object? handler) : IReadOnlyDictionary<string, object?>, INativeHostObject
    {
        private object? TargetValue(string name)
        {
            if (target is INativeHostObject host && host.TryGetValue(name, out var hostValue)) return ReadProperty(hostValue);
            if (target is IReadOnlyDictionary<string, object?> properties && properties.TryGetValue(name, out var propertyValue)) return ReadProperty(propertyValue);
            return JavaScriptEvaluator.UndefinedValue.Instance;
        }

        private object? Trap(string trapName, string propertyName, object? fallback)
        {
            if (handler is IReadOnlyDictionary<string, object?> properties
                && properties.TryGetValue(trapName, out var trap)
                && JavaScriptEvaluator.IsCallable(trap))
                return JavaScriptEvaluator.InvokeWithReceiver(trap!, handler, [target!, propertyName, this]);
            return fallback;
        }

        public IEnumerable<string> Keys => target is IReadOnlyDictionary<string, object?> properties ? properties.Keys : [];
        public IEnumerable<object?> Values => Keys.Select(name => this[name]);
        public int Count => Keys.Count();
        public object? this[string key] => TryGetValue(key, out var value) ? value : JavaScriptEvaluator.UndefinedValue.Instance;
        public bool ContainsKey(string key) => Contains(key);
        public bool TryGetValue(string name, out object? value) { value = Trap("get", name, TargetValue(name)); return true; }
        public bool TryGetPrototypeValue(string name, out object? value) { value = null; return false; }
        public bool TrySetPrototypeValue(string name, object? value) => false;
        public bool TrySetSetValue(string name, object? value)
        {
            var result = Trap("set", name, null);
            if (result is not null && !JavaScriptEvaluator.Truth(result)) return false;
            if (target is IDictionary<string, object?> dictionary) { dictionary[name] = value; return true; }
            return target is INativeHostObject host && host.TrySetSetValue(name, value);
        }
        public bool TryDeleteValue(string name) => target is IDictionary<string, object?> dictionary && dictionary.Remove(name);
        public bool TryGetIndex(int index, out object? value) => TryGetValue(index.ToString(CultureInfo.InvariantCulture), out value);
        public bool Contains(string name) => TryGetValue(name, out _);
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => Keys.Select(name => new KeyValuePair<string, object?>(name, this[name])).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private bool TryGetDomPrototypeValue(string templateName, string name, object receiver, out object? value)
    {
        for (var current = templateName; ;)
        {
            if (_domPrototypes.TryGetValue(current, out var prototype) && TryPrototypeChain(prototype, name, receiver, out value)) return true;
            if (current is "Node" || current.StartsWith("HTML", StringComparison.Ordinal) is false && current is not "Element" and not "HTMLElement") break;
            current = current switch
            {
                "HTMLElement" => "Element",
                "Element" => "Node",
                _ => "HTMLElement",
            };
        }
        value = null;
        return false;
    }

    private bool TrySetDomPrototypeValue(string templateName, string name, object? value, object receiver)
    {
        for (var current = templateName; ;)
        {
            if (_domPrototypes.TryGetValue(current, out var prototype) && prototype is IReadOnlyDictionary<string, object?> prototypeProperties && prototypeProperties.TryGetValue(name, out var property))
            {
                if (property is NativePropertyAccessor accessor) return accessor.Set(receiver, value);
                return false;
            }
            if (current is "Node" || current.StartsWith("HTML", StringComparison.Ordinal) is false && current is not "Element" and not "HTMLElement") break;
            current = current switch
            {
                "HTMLDocument" => "Document", "HTMLBodyElement" => "HTMLElement", "HTMLElement" => "Element",
                "SVGElement" => "Element", "Element" => "Node", "Document" => "Node", _ => "HTMLElement",
            };
        }
        return false;
    }

    private static bool TryPrototypeChain(object? prototype, string name, object receiver, out object? value)
    {
        if (prototype is IReadOnlyDictionary<string, object?> properties && properties.TryGetValue(name, out var own))
        {
            value = own is NativePropertyAccessor accessor ? accessor.Get(receiver) : ReadProperty(own);
            return true;
        }
        if (prototype is IReadOnlyDictionary<string, object?> chain && chain.TryGetValue("__proto__", out var parent)) return TryPrototypeChain(parent, name, receiver, out value);
        value = null;
        return false;
    }
}
