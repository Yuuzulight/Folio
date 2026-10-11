using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

public class ScriptEngineDeterminismAndSecurityTests
{
    [Fact]
    public void SeededRandomRepeatsAcrossRuns()
    {
        using var engine1 = new JintScriptEngine(new ScriptEngineOptions { RandomSeed = 42 });
        using var engine2 = new JintScriptEngine(new ScriptEngineOptions { RandomSeed = 42 });

        var numbers1 = new double[5];
        var numbers2 = new double[5];

        for (var i = 0; i < 5; i++)
        {
            numbers1[i] = engine1.Evaluate("Math.random()").AsNumber();
            numbers2[i] = engine2.Evaluate("Math.random()").AsNumber();
        }

        Assert.Equal(numbers1, numbers2);
    }

    [Fact]
    public void InjectedClockControlsDate()
    {
        var fixedTime = new DateTimeOffset(2026, 10, 12, 10, 30, 0, TimeSpan.Zero);
        using var engine = new JintScriptEngine(new ScriptEngineOptions
        {
            Clock = () => fixedTime,
        });

        var isoString = engine.Evaluate("new Date().toISOString()").AsString();
        Assert.Equal("2026-10-12T10:30:00.000Z", isoString);

        var epochMs = engine.Evaluate("Date.now()").AsNumber();
        Assert.Equal(fixedTime.ToUnixTimeMilliseconds(), (long)epochMs);
    }

    [Fact]
    public void ScriptsCannotReachDotNetTypes()
    {
        using var engine = new JintScriptEngine();

        // 1. Direct System access
        Assert.Equal("undefined", engine.Evaluate("typeof System").AsString());

        // 2. importNamespace / clr helper
        Assert.Equal("undefined", engine.Evaluate("typeof importNamespace").AsString());

        // 3. ({}).constructor.constructor function escapes
        var res = engine.Evaluate("new (({}).constructor.constructor)('return typeof System')()").AsString();
        Assert.Equal("undefined", res);

        // 4. Access to System.IO.File or Process
        Assert.Equal("undefined", engine.Evaluate("typeof (typeof System !== 'undefined' ? System.IO : undefined)").AsString());
    }
}
