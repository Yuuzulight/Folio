namespace Folio.Scripting;

/// <summary>
/// Discriminated kind of a <see cref="ScriptValue"/>.
/// </summary>
public enum ScriptValueKind
{
    Undefined,
    Null,
    Boolean,
    Number,
    String,
    Object,
    Array,
    Function,
}

/// <summary>
/// Represents a value in the script engine. Primitives are stored by value;
/// objects, arrays, and functions retain an opaque engine-level handle.
/// </summary>
public readonly struct ScriptValue : IEquatable<ScriptValue>
{
    public static readonly ScriptValue Undefined = new(ScriptValueKind.Undefined, null);
    public static readonly ScriptValue Null = new(ScriptValueKind.Null, null);
    public static readonly ScriptValue True = new(ScriptValueKind.Boolean, true);
    public static readonly ScriptValue False = new(ScriptValueKind.Boolean, false);

    private readonly object? _raw;

    internal ScriptValue(ScriptValueKind kind, object? raw)
    {
        Kind = kind;
        _raw = raw;
    }

    public ScriptValueKind Kind { get; }

    public bool IsUndefined => Kind == ScriptValueKind.Undefined;
    public bool IsNull => Kind == ScriptValueKind.Null;
    public bool IsNullOrUndefined => Kind is ScriptValueKind.Null or ScriptValueKind.Undefined;
    public bool IsBoolean => Kind == ScriptValueKind.Boolean;
    public bool IsNumber => Kind == ScriptValueKind.Number;
    public bool IsString => Kind == ScriptValueKind.String;
    public bool IsObject => Kind is ScriptValueKind.Object or ScriptValueKind.Array or ScriptValueKind.Function;
    public bool IsArray => Kind == ScriptValueKind.Array;
    public bool IsFunction => Kind == ScriptValueKind.Function;

    public bool AsBoolean() => _raw is true;

    public double AsNumber() => _raw switch
    {
        double d => d,
        int i => i,
        long l => l,
        float f => f,
        _ => 0.0,
    };

    public string? AsString() => _raw as string;

    public object? Raw => _raw;

    public static ScriptValue FromBoolean(bool value) => value ? True : False;
    public static ScriptValue FromNumber(double value) => new(ScriptValueKind.Number, value);
    public static ScriptValue FromString(string? value) => value is null ? Null : new(ScriptValueKind.String, value);
    public static ScriptValue FromObject(object raw) => new(ScriptValueKind.Object, raw);
    public static ScriptValue FromArray(object raw) => new(ScriptValueKind.Array, raw);
    public static ScriptValue FromFunction(object raw) => new(ScriptValueKind.Function, raw);

    public bool Equals(ScriptValue other) => Kind == other.Kind && Equals(_raw, other._raw);

    public override bool Equals(object? obj) => obj is ScriptValue other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Kind, _raw);

    public static bool operator ==(ScriptValue left, ScriptValue right) => left.Equals(right);
    public static bool operator !=(ScriptValue left, ScriptValue right) => !left.Equals(right);

    public override string ToString() => Kind switch
    {
        ScriptValueKind.Undefined => "undefined",
        ScriptValueKind.Null => "null",
        ScriptValueKind.Boolean => AsBoolean() ? "true" : "false",
        ScriptValueKind.Number => AsNumber().ToString(System.Globalization.CultureInfo.InvariantCulture),
        ScriptValueKind.String => AsString() ?? "",
        _ => $"[{Kind}]",
    };
}
