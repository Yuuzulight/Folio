using System.Globalization;
using System.Text;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;

namespace Folio.Scripting;

/// <summary>
/// WebIDL exception types and error codes as defined by W3C / WHATWG WebIDL specification.
/// </summary>
public enum WebIdlExceptionKind
{
    TypeError,
    RangeError,
    DOMException,
}

/// <summary>
/// Standard DOMException error names and their legacy code values.
/// </summary>
public static class DomExceptionNames
{
    public const string IndexSizeError = "IndexSizeError";
    public const string HierarchyRequestError = "HierarchyRequestError";
    public const string WrongDocumentError = "WrongDocumentError";
    public const string InvalidCharacterError = "InvalidCharacterError";
    public const string NoModificationAllowedError = "NoModificationAllowedError";
    public const string NotFoundError = "NotFoundError";
    public const string NotSupportedError = "NotSupportedError";
    public const string InUseAttributeError = "InUseAttributeError";
    public const string InvalidStateError = "InvalidStateError";
    public const string SyntaxError = "SyntaxError";
    public const string InvalidModificationError = "InvalidModificationError";
    public const string NamespaceError = "NamespaceError";
    public const string InvalidAccessError = "InvalidAccessError";
    public const string SecurityError = "SecurityError";
    public const string NetworkError = "NetworkError";
    public const string AbortError = "AbortError";
    public const string TimeoutError = "TimeoutError";
    public const string QuotaExceededError = "QuotaExceededError";

    public static ushort GetLegacyCode(string name) => name switch
    {
        IndexSizeError => 1,
        HierarchyRequestError => 3,
        WrongDocumentError => 4,
        InvalidCharacterError => 5,
        NoModificationAllowedError => 7,
        NotFoundError => 8,
        NotSupportedError => 9,
        InUseAttributeError => 10,
        InvalidStateError => 11,
        SyntaxError => 12,
        InvalidModificationError => 13,
        NamespaceError => 14,
        InvalidAccessError => 15,
        SecurityError => 18,
        NetworkError => 19,
        AbortError => 20,
        TimeoutError => 23,
        QuotaExceededError => 22,
        _ => 0,
    };
}

/// <summary>
/// Base exception thrown for WebIDL type errors, range errors, and DOM exceptions.
/// </summary>
public class WebIdlException : Exception
{
    public WebIdlException(WebIdlExceptionKind kind, string name, string message, ushort legacyCode = 0)
        : base(message)
    {
        Kind = kind;
        Name = name;
        LegacyCode = legacyCode;
    }

    public WebIdlExceptionKind Kind { get; }
    public string Name { get; }
    public ushort LegacyCode { get; }

    public static WebIdlException TypeError(string message) =>
        new(WebIdlExceptionKind.TypeError, "TypeError", message);

    public static WebIdlException RangeError(string message) =>
        new(WebIdlExceptionKind.RangeError, "RangeError", message);

    public static WebIdlException DomException(string name, string message) =>
        new(WebIdlExceptionKind.DOMException, name, message, DomExceptionNames.GetLegacyCode(name));
}

/// <summary>
/// WebIDL type conversion algorithms conforming to the WebIDL specification:
/// https://webidl.spec.whatwg.org/#es-type-mapping
/// </summary>
public static class WebIdlConversions
{
    private static readonly ConditionalWeakTable<IScriptEngine, ScriptWrapperCache> WrapperCaches = new();
    private static readonly ConditionalWeakTable<IScriptEngine, ConcurrentDictionary<string, HostObjectTemplate>> TemplateCaches = new();

    /// <summary>Retires wrappers belonging to a disposed/rebuilt script realm.</summary>
    internal static void ResetWrapperCache(IScriptEngine engine)
    {
        WrapperCaches.Remove(engine);
        TemplateCaches.Remove(engine);
    }

    public static HostObjectTemplate GetOrCreateTemplate(IScriptEngine engine, string key, Func<HostObjectTemplate> factory) =>
        TemplateCaches.GetValue(engine, static _ => new ConcurrentDictionary<string, HostObjectTemplate>())
            .GetOrAdd(key, static (_, state) => state(), factory);

    /// <summary>Returns the stable wrapper for a host target in this engine.</summary>
    public static IScriptEngine RequireEngine(IScriptEngine? engine) => engine ?? throw WebIdlException.TypeError("An engine is required for this binding");

    public static ScriptValue WrapHostObject(IScriptEngine? engine, IHostObjectTemplate template, object target)
    {
        var actualEngine = RequireEngine(engine);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(target);
        return WrapperCaches.GetValue(actualEngine, static _ => new ScriptWrapperCache()).GetOrCreate(actualEngine, template, target);
    }

    public static T UnwrapHostObject<T>(IScriptEngine? engine, ScriptValue value, string interfaceName)
        where T : class
    {
        if (!RequireEngine(engine).TryGetHostTarget(value, out var target) || target is not T typed)
            throw WebIdlException.TypeError($"Illegal invocation: value is not a {interfaceName}");
        return typed;
    }

    public static float ToFloat(ScriptValue val, bool unrestricted = false)
    {
        var d = unrestricted ? ToUnrestrictedDouble(val) : ToDouble(val);
        if ((!unrestricted && (double.IsNaN(d) || double.IsInfinity(d))) ||
            (!unrestricted && !double.IsInfinity(d) && (d < -float.MaxValue || d > float.MaxValue)))
            throw WebIdlException.TypeError($"Value is not a valid float: {d}");
        return (float)d;
    }

    public static float ToFloat(IScriptEngine? engine, ScriptValue val, bool unrestricted = false)
    {
        var d = unrestricted ? ToUnrestrictedDouble(engine, val) : ToDouble(engine, val);
        if ((!unrestricted && (double.IsNaN(d) || double.IsInfinity(d))) ||
            (!unrestricted && !double.IsInfinity(d) && (d < -float.MaxValue || d > float.MaxValue)))
            throw WebIdlException.TypeError($"Value is not a valid float: {d}");
        return (float)d;
    }

    public static sbyte ToByte(ScriptValue val, bool enforceRange = false, bool clamp = false)
    {
        var x = ToUnrestrictedDouble(val);
        if (enforceRange)
        {
            if (double.IsNaN(x) || double.IsInfinity(x) || x < sbyte.MinValue || x > sbyte.MaxValue)
                throw WebIdlException.TypeError($"[EnforceRange] value out of range for byte: {x}");
            return (sbyte)Math.Truncate(x);
        }
        if (clamp)
        {
            if (double.IsNaN(x)) return 0;
            return (sbyte)Math.Clamp(Math.Round(x, MidpointRounding.ToEven), sbyte.MinValue, sbyte.MaxValue);
        }
        var integer = ToIntegerHelper(val);
        var remainder = integer % 256.0;
        if (remainder < 0) remainder += 256.0;
        return unchecked((sbyte)(byte)remainder);
    }
    public static sbyte ToByte(IScriptEngine? engine, ScriptValue val, bool enforceRange = false, bool clamp = false) =>
        ToByte(engine is null ? val : ScriptValue.FromNumber(engine.CoerceNumber(val)), enforceRange, clamp);

    public static ulong ToUint64(ScriptValue val, bool enforceRange = false, bool clamp = false)
    {
        var d = ToUnrestrictedDouble(val);
        const double Modulus = 18446744073709551616.0;
        if (enforceRange && (double.IsNaN(d) || double.IsInfinity(d) || d < 0 || d >= Modulus))
            throw WebIdlException.TypeError($"[EnforceRange] value out of range for unsigned long long: {d}");
        if (clamp) return double.IsNaN(d) ? 0 : (ulong)Math.Clamp(Math.Round(d, MidpointRounding.ToEven), 0, Modulus - 1);
        var integer = ToIntegerHelperForPublic(val);
        if (integer >= -1e19 && integer <= 1e19)
        {
            var exact = decimal.Truncate((decimal)integer);
            const decimal DecimalModulus = 18446744073709551616m;
            var reduced = exact % DecimalModulus;
            if (reduced < 0) reduced += DecimalModulus;
            return (ulong)reduced;
        }
        var remainder = integer % Modulus;
        if (remainder < 0) remainder += Modulus;
        return (ulong)remainder;
    }
    public static ulong ToUint64(IScriptEngine? engine, ScriptValue val, bool enforceRange = false, bool clamp = false) =>
        ToUint64(engine is null ? val : ScriptValue.FromNumber(engine.CoerceNumber(val)), enforceRange, clamp);

    public static string ToEnum(ScriptValue value, IReadOnlySet<string> allowed, string name)
    {
        var result = ToDOMString(value);
        if (!allowed.Contains(result)) throw WebIdlException.TypeError($"Invalid value for {name}: {result}");
        return result;
    }

    public static string ToEnum(IScriptEngine? engine, ScriptValue value, IReadOnlySet<string> allowed, string name)
    {
        var result = ToDOMString(engine, value);
        if (!allowed.Contains(result)) throw WebIdlException.TypeError($"Invalid value for {name}: {result}");
        return result;
    }

    public static IReadOnlyList<T> ToSequence<T>(IScriptEngine? engine, ScriptValue value, Func<ScriptValue, T> convert)
    {
        var result = new List<T>();
        if (!RequireEngine(engine).TryReadSequence(value, item => result.Add(convert(item))))
            throw WebIdlException.TypeError("Value is not a sequence");
        return result;
    }

    public static ScriptValue ToScriptSequence<T>(IScriptEngine? engine, IReadOnlyList<T> values, Func<T, ScriptValue> convert) =>
        RequireEngine(engine).CreateArray(values.Select(convert).ToArray());

    public static ScriptBufferSource ToBufferSource(IScriptEngine? engine, ScriptValue value,
        bool allowArrayBuffer = true, bool requireArrayBuffer = false,
        ScriptBufferSourceKind? requiredKind = null)
    {
        if (!RequireEngine(engine).TryGetBufferSourceInfo(value, out var info) ||
            (!allowArrayBuffer && info.Kind == ScriptBufferSourceKind.ArrayBuffer) ||
            (requireArrayBuffer && info.Kind != ScriptBufferSourceKind.ArrayBuffer) ||
            (requiredKind is { } expected && info.Kind != expected))
            throw WebIdlException.TypeError("Value is not the required branded BufferSource");
        return new ScriptBufferSource(value, info);
    }

    public static ScriptValue RequireObject(ScriptValue value, string typeName = "object")
    {
        if (!value.IsObject) throw WebIdlException.TypeError($"Value is not an {typeName}");
        return value;
    }

    private static double ToIntegerHelperForPublic(ScriptValue value) => ToIntegerHelper(value);
    public static bool ToBoolean(ScriptValue val) => val.Kind switch
    {
        ScriptValueKind.Boolean => val.AsBoolean(),
        ScriptValueKind.Number => val.AsNumber() is not (0.0 or double.NaN),
        ScriptValueKind.String => !string.IsNullOrEmpty(val.AsString()),
        ScriptValueKind.Object or ScriptValueKind.Array or ScriptValueKind.Function => true,
        _ => false,
    };

    public static bool ToBoolean(IScriptEngine? engine, ScriptValue val) =>
        engine is null ? ToBoolean(val) : engine.CoerceBoolean(val);

    public static string ToDOMString(ScriptValue val) => val.Kind switch
    {
        ScriptValueKind.Null => "null",
        ScriptValueKind.Undefined => "undefined",
        ScriptValueKind.String => val.AsString() ?? "",
        ScriptValueKind.Boolean => val.AsBoolean() ? "true" : "false",
        ScriptValueKind.Number => val.AsNumber().ToString(CultureInfo.InvariantCulture),
        _ => val.ToString(),
    };

    public static string ToDOMString(IScriptEngine? engine, ScriptValue val) =>
        engine is null ? ToDOMString(val) : engine.CoerceString(val);

    /// <summary>
    /// Converts a string to USVString by replacing lone surrogates with U+FFFD.
    /// </summary>
    public static string ToUSVString(ScriptValue val)
    {
        var s = ToDOMString(val);
        if (string.IsNullOrEmpty(s))
            return s;

        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    sb.Append(c);
                    sb.Append(s[++i]);
                }
                else
                {
                    sb.Append('\uFFFD');
                }
            }
            else if (char.IsLowSurrogate(c))
            {
                sb.Append('\uFFFD');
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    public static string ToUSVString(IScriptEngine? engine, ScriptValue val) =>
        ToUSVString(engine is null ? val : ScriptValue.FromString(engine.CoerceString(val)));

    public static double ToDouble(ScriptValue val)
    {
        var num = val.Kind switch
        {
            ScriptValueKind.Number => val.AsNumber(),
            ScriptValueKind.Boolean => val.AsBoolean() ? 1.0 : 0.0,
            ScriptValueKind.String => double.TryParse(val.AsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN,
            ScriptValueKind.Null => 0.0,
            ScriptValueKind.Undefined => double.NaN,
            _ => double.NaN,
        };

        if (double.IsNaN(num) || double.IsInfinity(num))
            throw WebIdlException.TypeError($"Value is not a finite double: {num}");

        return num;
    }

    public static double ToDouble(IScriptEngine? engine, ScriptValue val)
    {
        var num = engine is null ? ToDouble(val) : engine.CoerceNumber(val);
        if (double.IsNaN(num) || double.IsInfinity(num))
            throw WebIdlException.TypeError($"Value is not a finite double: {num}");
        return num;
    }

    public static double ToUnrestrictedDouble(ScriptValue val) => val.Kind switch
    {
        ScriptValueKind.Number => val.AsNumber(),
        ScriptValueKind.Boolean => val.AsBoolean() ? 1.0 : 0.0,
        ScriptValueKind.String => double.TryParse(val.AsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN,
        ScriptValueKind.Null => 0.0,
        ScriptValueKind.Undefined => double.NaN,
        _ => double.NaN,
    };

    public static double ToUnrestrictedDouble(IScriptEngine? engine, ScriptValue val) =>
        engine is null ? ToUnrestrictedDouble(val) : engine.CoerceNumber(val);

    // Integer conversion helper
    private static double ToIntegerHelper(ScriptValue val)
    {
        var d = ToUnrestrictedDouble(val);
        if (double.IsNaN(d) || d == 0.0 || double.IsInfinity(d))
            return 0.0;
        return Math.Truncate(d);
    }

    public static long ToInt64(ScriptValue val, bool enforceRange = false, bool clamp = false)
    {
        var x = ToUnrestrictedDouble(val);
        if (enforceRange)
        {
            if (double.IsNaN(x) || double.IsInfinity(x) || x < long.MinValue || x > long.MaxValue)
                throw WebIdlException.TypeError($"[EnforceRange] value out of range for long: {x}");
            return (long)Math.Truncate(x);
        }
        if (clamp)
        {
            if (double.IsNaN(x)) return 0;
            return (long)Math.Clamp(Math.Round(x, MidpointRounding.ToEven), long.MinValue, long.MaxValue);
        }
        var i = ToIntegerHelper(val);
        return unchecked((long)(ulong)(decimal)i);
    }
    public static long ToInt64(IScriptEngine? engine, ScriptValue val, bool enforceRange = false, bool clamp = false) =>
        ToInt64(engine is null ? val : ScriptValue.FromNumber(engine.CoerceNumber(val)), enforceRange, clamp);

    // The integer modulo 2^32, as WebIDL's conversion to a 32-bit integer defines it; a cast of a double beyond the range of long is not that.
    private static uint ModuloTwoTo32(double integer)
    {
        var remainder = integer % 4294967296.0;
        if (remainder < 0)
            remainder += 4294967296.0;
        return (uint)remainder;
    }

    public static int ToInt32(ScriptValue val, bool enforceRange = false, bool clamp = false)
    {
        var x = ToUnrestrictedDouble(val);
        if (enforceRange)
        {
            if (double.IsNaN(x) || double.IsInfinity(x) || x < int.MinValue || x > int.MaxValue)
                throw WebIdlException.TypeError($"[EnforceRange] value out of range for int: {x}");
            return (int)Math.Truncate(x);
        }
        if (clamp)
        {
            if (double.IsNaN(x)) return 0;
            return (int)Math.Clamp(Math.Round(x, MidpointRounding.ToEven), int.MinValue, int.MaxValue);
        }
        return unchecked((int)ModuloTwoTo32(ToIntegerHelper(val)));
    }
    public static int ToInt32(IScriptEngine? engine, ScriptValue val, bool enforceRange = false, bool clamp = false) =>
        ToInt32(engine is null ? val : ScriptValue.FromNumber(engine.CoerceNumber(val)), enforceRange, clamp);

    public static uint ToUint32(ScriptValue val, bool enforceRange = false, bool clamp = false)
    {
        var x = ToUnrestrictedDouble(val);
        if (enforceRange)
        {
            if (double.IsNaN(x) || double.IsInfinity(x) || x < 0 || x > uint.MaxValue)
                throw WebIdlException.TypeError($"[EnforceRange] value out of range for unsigned long: {x}");
            return (uint)Math.Truncate(x);
        }
        if (clamp)
        {
            if (double.IsNaN(x)) return 0;
            return (uint)Math.Clamp(Math.Round(x, MidpointRounding.ToEven), 0, uint.MaxValue);
        }
        return ModuloTwoTo32(ToIntegerHelper(val));
    }
    public static uint ToUint32(IScriptEngine? engine, ScriptValue val, bool enforceRange = false, bool clamp = false) =>
        ToUint32(engine is null ? val : ScriptValue.FromNumber(engine.CoerceNumber(val)), enforceRange, clamp);

    public static short ToInt16(ScriptValue val, bool enforceRange = false, bool clamp = false)
    {
        var x = ToUnrestrictedDouble(val);
        if (enforceRange)
        {
            if (double.IsNaN(x) || double.IsInfinity(x) || x < short.MinValue || x > short.MaxValue)
                throw WebIdlException.TypeError($"[EnforceRange] value out of range for short: {x}");
            return (short)Math.Truncate(x);
        }
        if (clamp)
        {
            if (double.IsNaN(x)) return 0;
            return (short)Math.Clamp(Math.Round(x, MidpointRounding.ToEven), short.MinValue, short.MaxValue);
        }
        var i = ToIntegerHelper(val);
        return unchecked((short)(ushort)(int)i);
    }
    public static short ToInt16(IScriptEngine? engine, ScriptValue val, bool enforceRange = false, bool clamp = false) =>
        ToInt16(engine is null ? val : ScriptValue.FromNumber(engine.CoerceNumber(val)), enforceRange, clamp);

    public static ushort ToUint16(ScriptValue val, bool enforceRange = false, bool clamp = false)
    {
        var x = ToUnrestrictedDouble(val);
        if (enforceRange)
        {
            if (double.IsNaN(x) || double.IsInfinity(x) || x < 0 || x > ushort.MaxValue)
                throw WebIdlException.TypeError($"[EnforceRange] value out of range for unsigned short: {x}");
            return (ushort)Math.Truncate(x);
        }
        if (clamp)
        {
            if (double.IsNaN(x)) return 0;
            return (ushort)Math.Clamp(Math.Round(x, MidpointRounding.ToEven), 0, ushort.MaxValue);
        }
        var i = ToIntegerHelper(val);
        return unchecked((ushort)(uint)(int)i);
    }
    public static ushort ToUint16(IScriptEngine? engine, ScriptValue val, bool enforceRange = false, bool clamp = false) =>
        ToUint16(engine is null ? val : ScriptValue.FromNumber(engine.CoerceNumber(val)), enforceRange, clamp);

    public static byte ToUint8(ScriptValue val, bool enforceRange = false, bool clamp = false)
    {
        var x = ToUnrestrictedDouble(val);
        if (enforceRange)
        {
            if (double.IsNaN(x) || double.IsInfinity(x) || x < 0 || x > byte.MaxValue)
                throw WebIdlException.TypeError($"[EnforceRange] value out of range for octet: {x}");
            return (byte)Math.Truncate(x);
        }
        if (clamp)
        {
            if (double.IsNaN(x)) return 0;
            return (byte)Math.Clamp(Math.Round(x, MidpointRounding.ToEven), 0, byte.MaxValue);
        }
        var i = ToIntegerHelper(val);
        return unchecked((byte)(uint)(int)i);
    }
    public static byte ToUint8(IScriptEngine? engine, ScriptValue val, bool enforceRange = false, bool clamp = false) =>
        ToUint8(engine is null ? val : ScriptValue.FromNumber(engine.CoerceNumber(val)), enforceRange, clamp);
}
