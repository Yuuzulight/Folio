using Jint;
using Jint.Native;
using Jint.Native.Function;
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

        ResolvePrototype(template);
    }

    public IHostObjectTemplate Template { get; }
    public object Target { get; }

    private void ResolvePrototype(IHostObjectTemplate template)
    {
        // Attach prototype from global constructor if registered
        var ctor = Engine.GetValue(template.Name);
        if (!ctor.IsUndefined() && !ctor.IsNull() && ctor.IsObject())
        {
            var protoVal = ctor.Get("prototype");
            if (protoVal is ObjectInstance protoObj)
            {
                Prototype = protoObj;
                return;
            }
        }

        if (template.PrototypeTemplate is not null)
        {
            ResolvePrototype(template.PrototypeTemplate);
            return;
        }

        Prototype = Engine.Intrinsics.Object.PrototypeObject;
    }

    public override PropertyDescriptor GetOwnProperty(JsValue property)
    {
        var desc = base.GetOwnProperty(property);
        if (desc != PropertyDescriptor.Undefined)
            return desc;

        if (Template.IndexedGetter is not null)
        {
            if (property.IsNumber())
            {
                var num = property.AsNumber();
                if (num >= 0 && num == Math.Floor(num))
                {
                    var val = Template.IndexedGetter(Target, JintScriptEngine.ToScriptValue(this), (int)num);
                    if (!val.IsUndefined)
                        return new PropertyDescriptor(JintScriptEngine.ToJsValue(Engine, val), writable: false, enumerable: true, configurable: true);
                }
            }
            else if (property.IsString() && int.TryParse(property.AsString(), out var idx) && idx >= 0)
            {
                var val = Template.IndexedGetter(Target, JintScriptEngine.ToScriptValue(this), idx);
                if (!val.IsUndefined)
                    return new PropertyDescriptor(JintScriptEngine.ToJsValue(Engine, val), writable: false, enumerable: true, configurable: true);
            }
        }

        if (Template.NamedGetter is not null && property.IsString())
        {
            var str = property.AsString();
            var val = Template.NamedGetter(Target, JintScriptEngine.ToScriptValue(this), str);
            if (!val.IsUndefined)
                return new PropertyDescriptor(JintScriptEngine.ToJsValue(Engine, val), writable: false, enumerable: true, configurable: true);
        }

        return PropertyDescriptor.Undefined;
    }

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
                        var target = (thisObj as JintHostObject)?.Target;
                        if (target is null)
                            throw new JavaScriptException(Engine.Intrinsics.TypeError, "Illegal invocation");
                        try
                        {
                            var result = getter(target, JintScriptEngine.ToScriptValue(thisObj));
                            return JintScriptEngine.ToJsValue(Engine, result);
                        }
                        catch (WebIdlException wie)
                        {
                            throw CreateJavaScriptException(Engine, wie);
                        }
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
                            var target = (thisObj as JintHostObject)?.Target;
                            if (target is null)
                                throw new JavaScriptException(Engine.Intrinsics.TypeError, "Illegal invocation");
                            var argVal = args.Length > 0 ? JintScriptEngine.ToScriptValue(args[0]) : ScriptValue.Undefined;
                            try
                            {
                                setter(target, JintScriptEngine.ToScriptValue(thisObj), argVal);
                                return JsValue.Undefined;
                            }
                            catch (WebIdlException wie)
                            {
                                throw CreateJavaScriptException(Engine, wie);
                            }
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
                        var target = (thisObj as JintHostObject)?.Target;
                        if (target is null)
                            throw new JavaScriptException(Engine.Intrinsics.TypeError, "Illegal invocation");

                        var convertedArgs = new ScriptValue[args.Length];
                        for (var i = 0; i < args.Length; i++)
                            convertedArgs[i] = JintScriptEngine.ToScriptValue(args[i]);

                        try
                        {
                            var result = callback(target, JintScriptEngine.ToScriptValue(thisObj), convertedArgs);
                            return JintScriptEngine.ToJsValue(Engine, result);
                        }
                        catch (WebIdlException wie)
                        {
                            throw CreateJavaScriptException(Engine, wie);
                        }
                    },
                    0,
                    PropertyFlag.Configurable);

                FastSetProperty(methodName, new PropertyDescriptor(jsMethod, writable: true, enumerable: true, configurable: true));
            }
        }
    }

    private static JavaScriptException CreateJavaScriptException(Engine engine, WebIdlException wie)
    {
        if (wie.Kind == WebIdlExceptionKind.RangeError)
        {
            var rangeErrCtor = engine.GetValue("RangeError");
            if (rangeErrCtor is Function)
                return new JavaScriptException(engine.Construct(rangeErrCtor, [new JsString(wie.Message)]));
            return new JavaScriptException(engine.Intrinsics.TypeError, wie.Message);
        }

        if (wie.Kind == WebIdlExceptionKind.DOMException)
        {
            var domExCtor = engine.GetValue("DOMException");
            if (domExCtor is Function)
            {
                var instance = engine.Construct(domExCtor, [new JsString(wie.Message), new JsString(wie.Name)]);
                return new JavaScriptException(instance);
            }
            else
            {
                var err = engine.Intrinsics.Error.Construct(wie.Message);
                err.Set("name", wie.Name);
                err.Set("code", (double)wie.LegacyCode);
                return new JavaScriptException(err);
            }
        }

        return new JavaScriptException(engine.Intrinsics.TypeError, wie.Message);
    }
}
