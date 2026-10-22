using System.Globalization;

namespace Folio.Scripting.Native;

public enum NativeValueKind
{
    Undefined,
    Null,
    Boolean,
    Number,
    String,
    Object,
    Array,
    Function,
    HostObject,
}

public readonly struct NativeValue : IEquatable<NativeValue>
{
    public static readonly NativeValue Undefined = new(NativeValueKind.Undefined, null);
    public static readonly NativeValue Null = new(NativeValueKind.Null, null);
    public static readonly NativeValue True = new(NativeValueKind.Boolean, true);
    public static readonly NativeValue False = new(NativeValueKind.Boolean, false);

    private readonly object? _raw;

    public NativeValueKind Kind { get; }

    internal NativeValue(NativeValueKind kind, object? raw)
    {
        Kind = kind;
        _raw = raw;
    }

    public bool IsUndefined => Kind == NativeValueKind.Undefined;
    public bool IsNull => Kind == NativeValueKind.Null;
    public bool IsNullOrUndefined => Kind is NativeValueKind.Null or NativeValueKind.Undefined;
    public bool IsBoolean => Kind == NativeValueKind.Boolean;
    public bool IsNumber => Kind == NativeValueKind.Number;
    public bool IsString => Kind == NativeValueKind.String;
    public bool IsObject => Kind is NativeValueKind.Object or NativeValueKind.Array or NativeValueKind.Function or NativeValueKind.HostObject;
    public bool IsArray => Kind == NativeValueKind.Array;
    public bool IsFunction => Kind == NativeValueKind.Function;
    public bool IsHostObject => Kind == NativeValueKind.HostObject;

    public object? Raw => _raw;

    public bool AsBoolean() => _raw is true;

    public double AsNumber() => _raw switch
    {
        double d => d,
        int i => i,
        long l => l,
        float f => f,
        bool b => b ? 1.0 : 0.0,
        _ => 0.0,
    };

    public string AsString() => _raw as string ?? ToString();

    public NativeObject AsObject() => _raw as NativeObject ?? throw new InvalidCastException("Value is not an object.");
    public NativeArray AsArray() => _raw as NativeArray ?? throw new InvalidCastException("Value is not an array.");
    public NativeFunction AsFunction() => _raw as NativeFunction ?? throw new InvalidCastException("Value is not a function.");
    public NativeHostObject AsHostObject() => _raw as NativeHostObject ?? throw new InvalidCastException("Value is not a host object.");

    public static NativeValue FromBoolean(bool value) => value ? True : False;
    public static NativeValue FromNumber(double value) => new(NativeValueKind.Number, value);
    public static NativeValue FromString(string? value) => value is null ? Null : new(NativeValueKind.String, value);
    public static NativeValue FromObject(NativeObject obj) => new(NativeValueKind.Object, obj);
    public static NativeValue FromArray(NativeArray arr) => new(NativeValueKind.Array, arr);
    public static NativeValue FromFunction(NativeFunction func) => new(NativeValueKind.Function, func);
    public static NativeValue FromHostObject(NativeHostObject host) => new(NativeValueKind.HostObject, host);

    public bool IsTruthy() => Kind switch
    {
        NativeValueKind.Undefined or NativeValueKind.Null => false,
        NativeValueKind.Boolean => AsBoolean(),
        NativeValueKind.Number => AsNumber() is var n && n != 0 && !double.IsNaN(n),
        NativeValueKind.String => AsString().Length > 0,
        _ => true,
    };

    public static NativeValue Add(in NativeValue a, in NativeValue b)
    {
        if (a.IsString || b.IsString)
            return FromString(a.ToString() + b.ToString());
        return FromNumber(a.AsNumber() + b.AsNumber());
    }

    public static NativeValue Sub(in NativeValue a, in NativeValue b) => FromNumber(a.AsNumber() - b.AsNumber());
    public static NativeValue Mul(in NativeValue a, in NativeValue b) => FromNumber(a.AsNumber() * b.AsNumber());
    public static NativeValue Div(in NativeValue a, in NativeValue b) => FromNumber(a.AsNumber() / b.AsNumber());
    public static NativeValue Mod(in NativeValue a, in NativeValue b) => FromNumber(a.AsNumber() % b.AsNumber());

    public static NativeValue StrictEqual(in NativeValue a, in NativeValue b)
    {
        if (a.Kind != b.Kind) return False;
        return Equal(a, b);
    }

    public static NativeValue Equal(in NativeValue a, in NativeValue b)
    {
        if (a.IsNullOrUndefined && b.IsNullOrUndefined) return True;
        if (a.IsNumber && b.IsNumber) return FromBoolean(a.AsNumber() == b.AsNumber());
        if (a.IsString && b.IsString) return FromBoolean(string.Equals(a.AsString(), b.AsString(), StringComparison.Ordinal));
        if (a.IsBoolean && b.IsBoolean) return FromBoolean(a.AsBoolean() == b.AsBoolean());
        if (Equals(a._raw, b._raw)) return True;
        return False;
    }

    public static NativeValue LessThan(in NativeValue a, in NativeValue b)
    {
        if (a.IsString && b.IsString)
            return FromBoolean(string.CompareOrdinal(a.AsString(), b.AsString()) < 0);
        return FromBoolean(a.AsNumber() < b.AsNumber());
    }

    public static NativeValue GreaterThan(in NativeValue a, in NativeValue b)
    {
        if (a.IsString && b.IsString)
            return FromBoolean(string.CompareOrdinal(a.AsString(), b.AsString()) > 0);
        return FromBoolean(a.AsNumber() > b.AsNumber());
    }

    public bool Equals(NativeValue other) => Kind == other.Kind && Equals(_raw, other._raw);
    public override bool Equals(object? obj) => obj is NativeValue other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Kind, _raw);

    public static bool operator ==(NativeValue left, NativeValue right) => left.Equals(right);
    public static bool operator !=(NativeValue left, NativeValue right) => !left.Equals(right);

    public override string ToString() => Kind switch
    {
        NativeValueKind.Undefined => "undefined",
        NativeValueKind.Null => "null",
        NativeValueKind.Boolean => AsBoolean() ? "true" : "false",
        NativeValueKind.Number => AsNumber().ToString(CultureInfo.InvariantCulture),
        NativeValueKind.String => _raw as string ?? "",
        NativeValueKind.Object => "[object Object]",
        NativeValueKind.Array => _raw is NativeArray arr ? $"[{string.Join(",", arr.Elements)}]" : "[object Array]",
        NativeValueKind.Function => "[object Function]",
        NativeValueKind.HostObject => _raw is NativeHostObject host ? $"[object {host.Template.Name}]" : "[object HostObject]",
        _ => "",
    };

    public ScriptValue ToScriptValue() => Kind switch
    {
        NativeValueKind.Undefined => ScriptValue.Undefined,
        NativeValueKind.Null => ScriptValue.Null,
        NativeValueKind.Boolean => ScriptValue.FromBoolean(AsBoolean()),
        NativeValueKind.Number => ScriptValue.FromNumber(AsNumber()),
        NativeValueKind.String => ScriptValue.FromString(AsString()),
        NativeValueKind.Array => ScriptValue.FromArray(_raw!),
        NativeValueKind.Function => ScriptValue.FromFunction(_raw!),
        NativeValueKind.HostObject => ScriptValue.FromObject(_raw!),
        _ => ScriptValue.FromObject(_raw ?? new object()),
    };

    public static NativeValue FromScriptValue(ScriptValue sv) => sv.Kind switch
    {
        ScriptValueKind.Undefined => Undefined,
        ScriptValueKind.Null => Null,
        ScriptValueKind.Boolean => FromBoolean(sv.AsBoolean()),
        ScriptValueKind.Number => FromNumber(sv.AsNumber()),
        ScriptValueKind.String => FromString(sv.AsString()),
        ScriptValueKind.Array when sv.Raw is NativeArray arr => FromArray(arr),
        ScriptValueKind.Function when sv.Raw is NativeFunction fn => FromFunction(fn),
        ScriptValueKind.Object when sv.Raw is NativeObject obj => FromObject(obj),
        ScriptValueKind.Object when sv.Raw is NativeHostObject host => FromHostObject(host),
        _ => sv.Raw is not null ? new NativeValue(NativeValueKind.Object, sv.Raw) : Undefined,
    };
}

public class NativeObject
{
    private readonly Dictionary<string, NativeValue> _properties = new(StringComparer.Ordinal);

    public int Version { get; private set; } = 1;

    public NativeObject? Prototype { get; set; }

    public NativeObject()
    {
        Prototype = NativeVirtualMachine.ObjectPrototype;
    }

    public virtual NativeValue Get(string name)
    {
        if (_properties.TryGetValue(name, out var val))
            return val;
        return Prototype?.Get(name) ?? NativeValue.Undefined;
    }

    public virtual void Set(string name, NativeValue value)
    {
        _properties[name] = value;
        Version++;
    }

    public virtual bool Delete(string name)
    {
        var removed = _properties.Remove(name);
        if (removed) Version++;
        return removed;
    }

    public virtual bool Has(string name) => _properties.ContainsKey(name) || Prototype?.Has(name) == true;

    public IEnumerable<string> Keys => _properties.Keys;
}

public sealed class NativeArray : NativeObject
{
    public List<NativeValue> Elements { get; }

    public NativeArray(int capacity = 0)
    {
        Prototype = NativeVirtualMachine.ArrayPrototype;
        Elements = new List<NativeValue>(capacity);
    }

    public NativeArray(IEnumerable<NativeValue> elements)
    {
        Prototype = NativeVirtualMachine.ArrayPrototype;
        Elements = new List<NativeValue>(elements);
    }

    public int Length => Elements.Count;

    public override NativeValue Get(string name)
    {
        if (name == "length")
            return NativeValue.FromNumber(Elements.Count);

        if (int.TryParse(name, out var index) && index >= 0 && index < Elements.Count)
            return Elements[index];

        return base.Get(name);
    }

    public override void Set(string name, NativeValue value)
    {
        if (name == "length")
        {
            var newLen = (int)value.AsNumber();
            if (newLen < Elements.Count)
                Elements.RemoveRange(newLen, Elements.Count - newLen);
            return;
        }

        if (int.TryParse(name, out var index) && index >= 0)
        {
            while (Elements.Count <= index)
                Elements.Add(NativeValue.Undefined);
            Elements[index] = value;
            return;
        }

        base.Set(name, value);
    }
}

public sealed class NativeFunction : NativeObject
{
    public string? Name { get; }
    public IReadOnlyList<string> Parameters { get; }
    public BytecodeChunk? Chunk { get; }
    public LexicalEnvironment? Environment { get; }
    public Func<NativeVirtualMachine, NativeValue, ReadOnlySpan<NativeValue>, NativeValue>? HostCallback { get; }

    public NativeFunction(string? name, IReadOnlyList<string> parameters, BytecodeChunk chunk, LexicalEnvironment? env)
    {
        Name = name;
        Parameters = parameters;
        Chunk = chunk;
        Environment = env;
    }

    public NativeFunction(string? name, Func<NativeVirtualMachine, NativeValue, ReadOnlySpan<NativeValue>, NativeValue> hostCallback)
    {
        Name = name;
        Parameters = [];
        HostCallback = hostCallback;
    }
}

public sealed class NativeHostObject : NativeObject
{
    public IHostObjectTemplate Template { get; }
    public object Target { get; }

    public NativeHostObject(IHostObjectTemplate template, object target)
    {
        Template = template;
        Target = target;
    }

    public override NativeValue Get(string name)
    {
        // 1. Check template properties
        if (Template.TryGetProperty(name, out var getter, out _))
        {
            var thisSv = NativeValue.FromHostObject(this).ToScriptValue();
            var sv = getter(Target, thisSv);
            return NativeValue.FromScriptValue(sv);
        }

        // 2. Check template methods
        if (Template.TryGetMethod(name, out var method))
        {
            return NativeValue.FromFunction(new NativeFunction(name, (vm, thisVal, args) =>
            {
                var thisSv = thisVal.ToScriptValue();
                var svArgs = new ScriptValue[args.Length];
                for (var i = 0; i < args.Length; i++)
                    svArgs[i] = args[i].ToScriptValue();
                var result = method(Target, thisSv, svArgs);
                return NativeValue.FromScriptValue(result);
            }));
        }

        // 3. Check indexed getter
        if (Template.IndexedGetter is not null && int.TryParse(name, out var index) && index >= 0)
        {
            var thisSv = NativeValue.FromHostObject(this).ToScriptValue();
            var sv = Template.IndexedGetter(Target, thisSv, index);
            if (!sv.IsUndefined)
                return NativeValue.FromScriptValue(sv);
        }

        // 4. Check named getter
        if (Template.NamedGetter is not null)
        {
            var thisSv = NativeValue.FromHostObject(this).ToScriptValue();
            var sv = Template.NamedGetter(Target, thisSv, name);
            if (!sv.IsUndefined)
                return NativeValue.FromScriptValue(sv);
        }

        return base.Get(name);
    }

    public override void Set(string name, NativeValue value)
    {
        if (Template.TryGetProperty(name, out _, out var setter) && setter is not null)
        {
            var thisSv = NativeValue.FromHostObject(this).ToScriptValue();
            setter(Target, thisSv, value.ToScriptValue());
            return;
        }

        if (Template.NamedSetter is not null)
        {
            var thisSv = NativeValue.FromHostObject(this).ToScriptValue();
            Template.NamedSetter(Target, thisSv, name, value.ToScriptValue());
            return;
        }

        base.Set(name, value);
    }
}
