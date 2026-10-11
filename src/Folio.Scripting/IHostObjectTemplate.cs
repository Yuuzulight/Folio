namespace Folio.Scripting;

/// <summary>
/// Delegate representing a host-invoked method on an object template.
/// </summary>
public delegate ScriptValue HostMethodCallback(object target, ScriptValue thisValue, ReadOnlySpan<ScriptValue> args);

/// <summary>
/// Delegate representing a host getter for a property.
/// </summary>
public delegate ScriptValue HostPropertyGetter(object target, ScriptValue thisValue);

/// <summary>
/// Delegate representing a host setter for a property.
/// </summary>
public delegate void HostPropertySetter(object target, ScriptValue thisValue, ScriptValue value);

/// <summary>
/// Describes the members and behavior of a host-exposed class/interface in the script engine.
/// Used by generated WebIDL bindings to wrap DOM objects cleanly without exposing CLR internals.
/// </summary>
public interface IHostObjectTemplate
{
    /// <summary>Name of the interface or class in script (e.g. "Node", "Element", "Document").</summary>
    string Name { get; }

    /// <summary>Parent template in the prototype hierarchy, if any.</summary>
    IHostObjectTemplate? PrototypeTemplate { get; }

    /// <summary>Lookup a method by name on this template.</summary>
    bool TryGetMethod(string name, out HostMethodCallback callback);

    /// <summary>Lookup a property getter and optional setter by name on this template.</summary>
    bool TryGetProperty(string name, out HostPropertyGetter getter, out HostPropertySetter? setter);

    /// <summary>Enumerates all property names defined by this template.</summary>
    IEnumerable<string> PropertyNames { get; }

    /// <summary>Enumerates all method names defined by this template.</summary>
    IEnumerable<string> MethodNames { get; }
}

/// <summary>
/// A convenient builder and standard implementation of <see cref="IHostObjectTemplate"/>.
/// </summary>
public class HostObjectTemplate : IHostObjectTemplate
{
    private readonly Dictionary<string, HostMethodCallback> _methods = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (HostPropertyGetter Getter, HostPropertySetter? Setter)> _properties = new(StringComparer.Ordinal);

    public HostObjectTemplate(string name, IHostObjectTemplate? prototypeTemplate = null)
    {
        Name = name;
        PrototypeTemplate = prototypeTemplate;
    }

    public string Name { get; }
    public IHostObjectTemplate? PrototypeTemplate { get; }

    public IEnumerable<string> PropertyNames => _properties.Keys;
    public IEnumerable<string> MethodNames => _methods.Keys;

    public HostObjectTemplate DefineMethod(string name, HostMethodCallback callback)
    {
        _methods[name] = callback;
        return this;
    }

    public HostObjectTemplate DefineProperty(string name, HostPropertyGetter getter, HostPropertySetter? setter = null)
    {
        _properties[name] = (getter, setter);
        return this;
    }

    public bool TryGetMethod(string name, out HostMethodCallback callback)
    {
        if (_methods.TryGetValue(name, out callback!))
            return true;
        if (PrototypeTemplate is not null)
            return PrototypeTemplate.TryGetMethod(name, out callback);
        callback = null!;
        return false;
    }

    public bool TryGetProperty(string name, out HostPropertyGetter getter, out HostPropertySetter? setter)
    {
        if (_properties.TryGetValue(name, out var prop))
        {
            getter = prop.Getter;
            setter = prop.Setter;
            return true;
        }
        if (PrototypeTemplate is not null)
            return PrototypeTemplate.TryGetProperty(name, out getter, out setter);
        getter = null!;
        setter = null;
        return false;
    }
}
