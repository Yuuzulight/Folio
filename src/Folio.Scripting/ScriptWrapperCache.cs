using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Folio.Scripting;

/// <summary>
/// Maintains a 1:1 mapping between engine instances, host objects, and their script-visible wrappers.
/// Plain rendering never allocates wrappers (study 02, study 17); wrappers are created on first access.
/// </summary>
public sealed class ScriptWrapperCache
{
    // ConditionalWeakTable ensures that when target object is collected, its wrapper entry is freed.
    private readonly ConditionalWeakTable<object, ScriptValueBox> _table = new();

    public ScriptValue GetOrCreate(IScriptEngine engine, IHostObjectTemplate template, object target)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(target);

        lock (_table)
        {
            if (_table.TryGetValue(target, out var boxed))
                return boxed.Value;

            var created = engine.CreateHostObject(template, target);
            _table.Add(target, new ScriptValueBox(created));
            return created;
        }
    }

    public bool TryGet(object target, out ScriptValue value)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_table)
        {
            if (_table.TryGetValue(target, out var boxed))
            {
                value = boxed.Value;
                return true;
            }
            value = ScriptValue.Undefined;
            return false;
        }
    }

    private sealed class ScriptValueBox
    {
        public ScriptValueBox(ScriptValue value) => Value = value;
        public ScriptValue Value { get; }
    }
}
