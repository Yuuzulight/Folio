using Folio.Scripting;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

public sealed class HostCallbackExceptionTests
{
    [Theory]
    [InlineData("bridge.invoke(throwMarker)")]
    [InlineData("bridge.value")]
    [InlineData("bridge.value = throwMarker")]
    public void ScriptThrowsRetainTheirIdentityAcrossAHostCallback(string invocation)
    {
        using var engine = new JintScriptEngine();
        var bridge = new HostObjectTemplate("CallbackBridge")
            .DefineMethod("invoke", (_, _, args) => engine.Call(args[0], ScriptValue.Undefined, []))
            .DefineProperty("value", (_, _) => engine.Call(engine.GetGlobalProperty("throwMarker"), ScriptValue.Undefined, []),
                (_, _, value) => { engine.Call(value, ScriptValue.Undefined, []); });
        engine.SetGlobalProperty("bridge", engine.CreateHostObject(bridge, new object()));
        engine.Evaluate("globalThis.marker = new TypeError('marker'); globalThis.throwMarker = () => { throw marker; };");
        Assert.True(engine.Evaluate("""
            let caughtOriginal = false;
            try {
            """ + invocation + ";" + """
            } catch (error) { caughtOriginal = error === marker; }
            caughtOriginal;
            """).AsBoolean());
    }

    [Fact]
    public void AnExceptionFromAnotherEngineIsNotInjectedIntoTheCallingRealm()
    {
        using var first = new JintScriptEngine();
        using var second = new JintScriptEngine();
        var bridge = new HostObjectTemplate("ForeignCallbackBridge")
            .DefineMethod("invoke", (_, _, _) => second.Evaluate("throw new Error('foreign realm')"));
        first.SetGlobalProperty("bridge", first.CreateHostObject(bridge, new object()));
        var error = Assert.Throws<ScriptRuntimeException>(() => first.Evaluate("try { bridge.invoke(); } catch (error) { globalThis.injected = error; }"));
        Assert.Equal("foreign realm", error.Message);
        Assert.True(first.GetGlobalProperty("injected").IsUndefined);
    }
}
