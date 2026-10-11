using System.Globalization;
using System.Text;

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
    public static bool ToBoolean(ScriptValue val) => val.Kind switch
    {
        ScriptValueKind.Boolean => val.AsBoolean(),
        ScriptValueKind.Number => val.AsNumber() is not (0.0 or double.NaN),
        ScriptValueKind.String => !string.IsNullOrEmpty(val.AsString()),
        ScriptValueKind.Object or ScriptValueKind.Array or ScriptValueKind.Function => true,
        _ => false,
    };

    public static string ToDOMString(ScriptValue val) => val.Kind switch
    {
        ScriptValueKind.Null => "null",
        ScriptValueKind.Undefined => "undefined",
        ScriptValueKind.String => val.AsString() ?? "",
        ScriptValueKind.Boolean => val.AsBoolean() ? "true" : "false",
        ScriptValueKind.Number => val.AsNumber().ToString(CultureInfo.InvariantCulture),
        _ => val.ToString(),
    };

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

    public static double ToUnrestrictedDouble(ScriptValue val) => val.Kind switch
    {
        ScriptValueKind.Number => val.AsNumber(),
        ScriptValueKind.Boolean => val.AsBoolean() ? 1.0 : 0.0,
        ScriptValueKind.String => double.TryParse(val.AsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN,
        ScriptValueKind.Null => 0.0,
        ScriptValueKind.Undefined => double.NaN,
        _ => double.NaN,
    };

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
        var i = ToIntegerHelper(val);
        return unchecked((int)(uint)(long)i);
    }

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
        var i = ToIntegerHelper(val);
        return unchecked((uint)(long)i);
    }

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
}
