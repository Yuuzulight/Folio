using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;

namespace Folio.Scripting.Jint;

/// <summary>
/// Jint-backed host object wrapper that attaches an <see cref="IHostObjectTemplate"/> and a target object
/// to a JavaScript object instance without exposing any .NET CLR members directly.
/// </summary>
internal sealed class JintHostObject : ObjectInstance
{
    public JintHostObject(Engine engine, IHostObjectTemplate template, object target) : base(engine)
    {
        Template = template;
        Target = target;

        InstallTemplate(template);
    }

    public IHostObjectTemplate Template { get; }
    public object Target { get; }

    private void InstallTemplate(IHostObjectTemplate template)
    {
        // First install prototype template members if any
        if (template.PrototypeTemplate is { } proto)
        {
            InstallTemplate(proto);
        }

        // Install properties
        foreach (var propName in template.PropertyNames)
        {
            if (template.TryGetProperty(propName, out var getter, out var setter))
            {
                var jsGetter = new ClrFunction(
                    Engine,
                    $"get {propName}",
                    (thisObj, args) =>
                    {
                        var target = (thisObj as JintHostObject)?.Target ?? Target;
                        var result = getter(target, JintScriptEngine.ToScriptValue(thisObj));
                        return JintScriptEngine.ToJsValue(Engine, result);
                    },
                    0,
                    PropertyFlag.Configurable);

                ClrFunction? jsSetter = null;
                if (setter is not null)
                {
                    jsSetter = new ClrFunction(
                        Engine,
                        $"set {propName}",
                        (thisObj, args) =>
                        {
                            var target = (thisObj as JintHostObject)?.Target ?? Target;
                            var argVal = args.Length > 0 ? JintScriptEngine.ToScriptValue(args[0]) : ScriptValue.Undefined;
                            setter(target, JintScriptEngine.ToScriptValue(thisObj), argVal);
                            return JsValue.Undefined;
                        },
                        1,
                        PropertyFlag.Configurable);
                }

                FastSetProperty(propName, new GetSetPropertyDescriptor(jsGetter, jsSetter, enumerable: true, configurable: true));
            }
        }

        // Install methods
        foreach (var methodName in template.MethodNames)
        {
            if (template.TryGetMethod(methodName, out var callback))
            {
                var jsMethod = new ClrFunction(
                    Engine,
                    methodName,
                    (thisObj, args) =>
                    {
                        var target = (thisObj as JintHostObject)?.Target ?? Target;
                        var convertedArgs = new ScriptValue[args.Length];
                        for (var i = 0; i < args.Length; i++)
                            convertedArgs[i] = JintScriptEngine.ToScriptValue(args[i]);

                        var result = callback(target, JintScriptEngine.ToScriptValue(thisObj), convertedArgs);
                        return JintScriptEngine.ToJsValue(Engine, result);
                    },
                    0,
                    PropertyFlag.Configurable);

                FastSetProperty(methodName, new PropertyDescriptor(jsMethod, writable: true, enumerable: true, configurable: true));
            }
        }
    }
}
