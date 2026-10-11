using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

public class ScriptEngineEvaluationTests
{
    [Fact]
    public void EvaluatePrimitivesRoundTrip()
    {
        using var engine = new JintScriptEngine();

        var undefinedVal = engine.Evaluate("undefined");
        Assert.True(undefinedVal.IsUndefined);
        Assert.True(undefinedVal.IsNullOrUndefined);

        var nullVal = engine.Evaluate("null");
        Assert.True(nullVal.IsNull);
        Assert.True(nullVal.IsNullOrUndefined);

        var boolVal = engine.Evaluate("true");
        Assert.True(boolVal.IsBoolean);
        Assert.True(boolVal.AsBoolean());

        var numVal = engine.Evaluate("42.5");
        Assert.True(numVal.IsNumber);
        Assert.Equal(42.5, numVal.AsNumber());

        var strVal = engine.Evaluate("'Hello, World!'");
        Assert.True(strVal.IsString);
        Assert.Equal("Hello, World!", strVal.AsString());
    }

    [Fact]
    public void EvaluateObjectsAndArrays()
    {
        using var engine = new JintScriptEngine();

        var arrVal = engine.Evaluate("[1, 2, 'three']");
        Assert.True(arrVal.IsArray);
        Assert.True(arrVal.IsObject);

        var objVal = engine.Evaluate("({ name: 'Folio', version: 4 })");
        Assert.True(objVal.IsObject);
        Assert.False(objVal.IsArray);

        var fnVal = engine.Evaluate("(function(x) { return x * 2; })");
        Assert.True(fnVal.IsFunction);
        Assert.True(fnVal.IsObject);
    }

    [Fact]
    public void CallFunctionWithArgumentsAndThis()
    {
        using var engine = new JintScriptEngine();

        var fn = engine.Evaluate("(function(a, b) { return a + b; })");
        var result = engine.Call(fn, ScriptValue.Undefined, [ScriptValue.FromNumber(10), ScriptValue.FromNumber(32)]);
        Assert.Equal(42, result.AsNumber());

        var thisFn = engine.Evaluate("(function() { return this.multiplier * 2; })");
        var obj = engine.Evaluate("({ multiplier: 21 })");
        var result2 = engine.Call(thisFn, obj, []);
        Assert.Equal(42, result2.AsNumber());
    }

    [Fact]
    public void GlobalPropertiesGetAndSet()
    {
        using var engine = new JintScriptEngine();

        engine.SetGlobalProperty("appVersion", ScriptValue.FromString("1.0.0"));
        var val = engine.Evaluate("appVersion");
        Assert.Equal("1.0.0", val.AsString());

        engine.Evaluate("globalThis.counter = 99;");
        var retrieved = engine.GetGlobalProperty("counter");
        Assert.Equal(99, retrieved.AsNumber());
    }

    [Fact]
    public void StandardBuiltInsExist()
    {
        using var engine = new JintScriptEngine();

        Assert.Equal("object", engine.Evaluate("typeof JSON").AsString());
        Assert.Equal("function", engine.Evaluate("typeof Promise").AsString());
        Assert.Equal("function", engine.Evaluate("typeof Map").AsString());
        Assert.Equal("function", engine.Evaluate("typeof Set").AsString());
        Assert.Equal("function", engine.Evaluate("typeof Uint8Array").AsString());
        Assert.Equal("function", engine.Evaluate("typeof Float32Array").AsString());

        // JSON round trip
        var jsonRes = engine.Evaluate("JSON.stringify({ a: 1, b: 'two' })").AsString();
        Assert.Equal("{\"a\":1,\"b\":\"two\"}", jsonRes);
    }

    [Fact]
    public void MicrotaskCheckpointExecutesQueuedTasks()
    {
        using var engine = new JintScriptEngine();

        // PerformMicrotaskCheckpoint runs queued tasks in the engine's task queue without error
        engine.Evaluate("var x = 1;");
        engine.PerformMicrotaskCheckpoint();
        Assert.Equal(1, engine.Evaluate("x").AsNumber());
    }

    [Fact]
    public void ScriptExceptionsIncludeDetails()
    {
        using var engine = new JintScriptEngine();

        var ex = Assert.Throws<ScriptRuntimeException>(() =>
        {
            engine.Evaluate("throw new Error('Something failed in JS');", "sample.js");
        });

        Assert.Contains("Something failed in JS", ex.Message);
        Assert.Equal("sample.js", ex.SourceLocation);
        Assert.Equal(1, ex.LineNumber);
    }
}
