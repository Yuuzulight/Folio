using Folio.Scripting.Native;

namespace Folio.Scripting.Tests;

public sealed class NativeExecutionLimitTests
{
    [Theory]
    [InlineData("function recurse(){return recurse();}recurse();")]
    [InlineData("var x={run(){return x.run();}};x.run();")]
    [InlineData("var recurse=new Function('return recurse();');recurse();")]
    [InlineData("var a=[];a.push(a);a.flat(Infinity);")]
    public void RecursionIsBoundedAndTheNextTaskCanRun(string source)
    {
        using var engine = new NativeJavaScriptEngine();
        engine.Configure(ScriptLimits.Default with { MaxRecursionDepth = 12, MaxStatements = null, Timeout = null });
        var error = Assert.Throws<ScriptLimitExceededException>(() => engine.Evaluate(source));
        Assert.Equal("Recursion", error.LimitName);
        Assert.Equal(2d, engine.Evaluate("1+1").AsNumber());
    }

    [Theory]
    [InlineData("new Array(1000000000)")]
    [InlineData("new ArrayBuffer(1000000000)")]
    [InlineData("new Float64Array(100000000)")]
    [InlineData("'x'.repeat(1000000000)")]
    [InlineData("'x'.padStart(1000000000)")]
    [InlineData("'x'.padEnd(1000000000)")]
    public void AllocationLimitRejectsAHugeArrayBeforeAllocatingIt(string source)
    {
        using var engine = new NativeJavaScriptEngine();
        engine.Configure(ScriptLimits.Default with { MemoryLimitBytes = 1024 * 1024 });
        Assert.Equal("Memory", Assert.Throws<ScriptLimitExceededException>(() => engine.Evaluate(source)).LimitName);
        Assert.Equal(2d, engine.Evaluate("1+1").AsNumber());
    }

    [Fact]
    public void RepeatedCallsShareOneStatementBudget()
    {
        using var engine = new NativeJavaScriptEngine();
        engine.Configure(ScriptLimits.Default with { MaxStatements = 500 });
        Assert.Equal("Statements", Assert.Throws<ScriptLimitExceededException>(() => engine.Evaluate("function work(){for(var i=0;i<10;i++){} }for(var j=0;j<100;j++){work();}")).LimitName);
    }

    [Fact]
    public void IsolatedEnginesInheritTheConfiguredRecursionLimit()
    {
        using var parent = new NativeJavaScriptEngine();
        parent.Configure(ScriptLimits.Default with { MaxRecursionDepth = 8 });
        using var child = parent.CreateIsolated();
        Assert.Equal("Recursion", Assert.Throws<ScriptLimitExceededException>(() => child.Evaluate("function f(){f();}f();")).LimitName);
    }

    [Fact]
    public void DeepSyntaxFailsWithoutCrashingTheProcess()
    {
        using var engine = new NativeJavaScriptEngine();
        var source = new string('(', 200_000) + "1" + new string(')', 200_000);
        Assert.Equal("Recursion", Assert.Throws<ScriptLimitExceededException>(() => engine.Evaluate(source)).LimitName);
        Assert.Equal(2d, engine.Evaluate("1+1").AsNumber());
    }

    [Fact]
    public void ScriptCatchCannotHideAResourceLimit()
    {
        using var engine = new NativeJavaScriptEngine();
        engine.Configure(ScriptLimits.Default with { MaxRecursionDepth = 8 });
        Assert.Equal("Recursion", Assert.Throws<ScriptLimitExceededException>(() => engine.Evaluate("function f(){f();}try{f();}catch(e){}42;")).LimitName);
    }

    [Fact]
    public void InterruptDuringSequenceConversionCannotBeCaughtAndHidden()
    {
        using var engine = new NativeJavaScriptEngine();
        engine.SetGlobalProperty("consume", ScriptValue.FromObject((Func<IReadOnlyList<object?>, object?>)(_ =>
        {
            var admitted = 0;
            engine.TryReadSequence(ScriptValue.FromObject(new object?[] { 1d, 2d }), _ =>
            {
                if (++admitted == 1) engine.Interrupt();
            });
            return null;
        })));
        Assert.Throws<ScriptInterruptedException>(() => engine.Evaluate("try{consume();}catch(e){}42;"));
        Assert.Equal(2d, engine.Evaluate("1+1").AsNumber());
    }

    [Fact]
    public async Task InterruptStopsANativeLoop()
    {
        using var engine = new NativeJavaScriptEngine();
        engine.Configure(ScriptLimits.Default with { Timeout = null, MaxStatements = null });
        using var entered = new ManualResetEventSlim();
        engine.SetGlobalProperty("started", ScriptValue.FromObject((Func<IReadOnlyList<object?>, object?>)(_ => { entered.Set(); return null; })));
        var execution = Task.Factory.StartNew(() => engine.Evaluate("started();try{while(true){}}catch(e){}42;"), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            engine.Interrupt();
            await Assert.ThrowsAsync<ScriptInterruptedException>(async () => await execution.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            Assert.Equal(2d, engine.Evaluate("1+1").AsNumber());
        }
        finally { engine.Interrupt(); }
    }
}
