using System.Globalization;
using Folio.Hosting;

namespace Folio.Scripting.Dom;

/// <summary>
/// One Storage area (https://html.spec.whatwg.org/multipage/webstorage.html): ordered string pairs under a quota counted in UTF-16
/// code units of keys and values. With a host storage the pairs persist under the artifact's own namespace; without one they
/// live in memory and go with the document.
/// </summary>
internal sealed class StorageArea
{
    /// <summary>5 MB of UTF-16 code units per artifact (study 16).</summary>
    internal const long Quota = 5 * 1024 * 1024;

    private readonly List<KeyValuePair<string, string>> _items = [];
    private readonly IArtifactStorage? _host;
    private readonly string _prefix;
    private long _size;
    private bool _loaded;

    /// <summary>The namespace of an area's keys in the host storage; the length-delimited artifact id cannot collide with another artifact's.</summary>
    internal static string Prefix(string artifact, string area) =>
        "folio/" + area + "-v1/" + artifact.Length.ToString(CultureInfo.InvariantCulture) + ":" + artifact + "/";

    internal StorageArea(IArtifactStorage? host, string artifact, string area)
    {
        _host = host;
        _prefix = Prefix(artifact, area);
        _loaded = host is null;
    }

    // The host is asked on first use, so a document that never touches storage never depends on it.
    private void Load()
    {
        if (_loaded)
            return;
        try
        {
            foreach (var stored in _host!.Keys(_prefix))
            {
                if (!stored.StartsWith(_prefix, StringComparison.Ordinal) || _host.Read(stored) is not { } value)
                    continue;
                var key = Uri.UnescapeDataString(stored[_prefix.Length..]);
                _items.Add(new(key, value));
                _size += key.Length + value.Length;
            }
        }
        catch (Exception)
        {
            _items.Clear();
            _size = 0;
            throw WebIdlException.DomException("SecurityError", "The host could not read web storage.");
        }
        _loaded = true;
    }

    internal int Length
    {
        get
        {
            Load();
            return _items.Count;
        }
    }

    internal string? Key(int index)
    {
        Load();
        return index >= 0 && index < _items.Count ? _items[index].Key : null;
    }

    internal IEnumerable<string> Keys
    {
        get
        {
            Load();
            return _items.Select(i => i.Key).ToList();
        }
    }

    internal string? Get(string key)
    {
        Load();
        var at = IndexOf(key);
        return at < 0 ? null : _items[at].Value;
    }

    internal void Set(string key, string value)
    {
        Load();
        var at = IndexOf(key);
        if (at >= 0 && _items[at].Value == value)
            return;
        var size = _size - (at >= 0 ? key.Length + _items[at].Value.Length : 0) + key.Length + value.Length;
        if (size > Quota)
            throw WebIdlException.DomException("QuotaExceededError", "The storage quota has been exceeded.");
        Persist(() => _host?.Write(_prefix + Uri.EscapeDataString(key), value));
        if (at >= 0)
            _items[at] = new(key, value);
        else
            _items.Add(new(key, value));
        _size = size;
    }

    internal void Remove(string key)
    {
        Load();
        var at = IndexOf(key);
        if (at < 0)
            return;
        Persist(() => _host?.Delete(_prefix + Uri.EscapeDataString(key)));
        _size -= key.Length + _items[at].Value.Length;
        _items.RemoveAt(at);
    }

    internal void Clear()
    {
        Load();
        if (_items.Count == 0)
            return;
        var keys = _items.Select(i => i.Key).ToList();
        // Each delete is its own host call; the pairs the host failed to delete stay.
        foreach (var key in keys)
            Remove(key);
    }

    private int IndexOf(string key) => _items.FindIndex(i => string.Equals(i.Key, key, StringComparison.Ordinal));

    // The host's write or delete comes first: if it throws, the area is unchanged.
    private static void Persist(Action write)
    {
        try
        {
            write();
        }
        catch (Exception)
        {
            throw WebIdlException.DomException("QuotaExceededError", "The host could not store the value.");
        }
    }
}

/// <summary><c>localStorage</c> and <c>sessionStorage</c>.</summary>
internal static class StorageBindings
{
    internal static void Install(IScriptEngine engine, IArtifactStorage? host, string artifact)
    {
        // Anything but a string becomes one the way the script's own String(value) makes it: an object by its toString, a number in its JavaScript form.
        var stringify = engine.Evaluate("(function (v) { return String(v); })", "folio:storage-string");
        string Text(ScriptValue value) => value.Kind == ScriptValueKind.String
            ? value.AsString() ?? ""
            : engine.Call(stringify, ScriptValue.Undefined, [value]).AsString() ?? "";
        var template = new HostObjectTemplate("Storage")
            .DefineProperty("length", (t, _) => ScriptValue.FromNumber(((StorageArea)t).Length))
            .DefineMethod("key", (t, _, a) =>
            {
                Need(a, 1, "key");
                var index = a[0].AsNumber();
                return ScriptValue.FromString(double.IsFinite(index) && index >= 0 && index < int.MaxValue ? ((StorageArea)t).Key((int)index) : null);
            })
            .DefineMethod("getItem", (t, _, a) =>
            {
                Need(a, 1, "getItem");
                return ScriptValue.FromString(((StorageArea)t).Get(Text(a[0])));
            })
            .DefineMethod("setItem", (t, _, a) =>
            {
                Need(a, 2, "setItem");
                ((StorageArea)t).Set(Text(a[0]), Text(a[1]));
                return ScriptValue.Undefined;
            })
            .DefineMethod("removeItem", (t, _, a) =>
            {
                Need(a, 1, "removeItem");
                ((StorageArea)t).Remove(Text(a[0]));
                return ScriptValue.Undefined;
            })
            .DefineMethod("clear", (t, _, _) =>
            {
                ((StorageArea)t).Clear();
                return ScriptValue.Undefined;
            })
            .DefineNamedProperty(
                getter: (t, _, name) => ScriptValue.FromString(((StorageArea)t).Get(name)) is { Kind: not ScriptValueKind.Null } found ? found : ScriptValue.Undefined,
                setter: (t, _, name, value) => ((StorageArea)t).Set(name, Text(value)),
                deleter: (t, _, name) =>
                {
                    ((StorageArea)t).Remove(name);
                    return true;
                },
                enumerator: (t, _) => ((StorageArea)t).Keys.ToList());
        engine.SetGlobalProperty("localStorage", engine.CreateHostObject(template, new StorageArea(host, artifact, "local")));
        engine.SetGlobalProperty("sessionStorage", engine.CreateHostObject(template, new StorageArea(null, artifact, "session")));
    }

    private static void Need(ReadOnlySpan<ScriptValue> args, int count, string method)
    {
        if (args.Length < count)
            throw WebIdlException.TypeError($"Failed to execute '{method}' on 'Storage': {count} argument{(count == 1 ? "" : "s")} required, but only {args.Length} present.");
    }
}
