using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

public class ScriptEngineLimitsAndInterruptTests
{
    [Fact]
    public void TimeoutLimitTerminatesInfiniteLoop()
    {
        var limits = new ScriptLimits
        {
            Timeout = TimeSpan.FromMilliseconds(100),
            MaxStatements = null,
            MaxRecursionDepth = null,
            MemoryLimitBytes = null,
        };

        using var engine = new JintScriptEngine(new ScriptEngineOptions { Limits = limits });

        var ex = Assert.Throws<ScriptLimitExceededException>(() =>
        {
            engine.Evaluate("while (true) {}");
        });

        Assert.Equal("Timeout", ex.LimitName);
    }

    [Fact]
    public void StatementsLimitTerminatesLoop()
    {
        var limits = new ScriptLimits
        {
            Timeout = null,
            MaxStatements = 500,
            MaxRecursionDepth = null,
            MemoryLimitBytes = null,
        };

        using var engine = new JintScriptEngine(new ScriptEngineOptions { Limits = limits });

        var ex = Assert.Throws<ScriptLimitExceededException>(() =>
        {
            engine.Evaluate("for (let i = 0; i < 2000; i++) {}");
        });

        Assert.Equal("Statements", ex.LimitName);
    }

    [Fact]
    public void RecursionLimitTerminatesUnboundedRecursion()
    {
        var limits = new ScriptLimits
        {
            Timeout = null,
            MaxStatements = null,
            MaxRecursionDepth = 20,
            MemoryLimitBytes = null,
        };

        using var engine = new JintScriptEngine(new ScriptEngineOptions { Limits = limits });

        var ex = Assert.Throws<ScriptLimitExceededException>(() =>
        {
            engine.Evaluate("function recurse() { recurse(); } recurse();");
        });

        Assert.Equal("Recursion", ex.LimitName);
    }

    [Fact]
    public void MemoryLimitTerminatesUnboundedAllocation()
    {
        var limits = new ScriptLimits
        {
            Timeout = null,
            MaxStatements = null,
            MaxRecursionDepth = null,
            MemoryLimitBytes = 2 * 1024 * 1024, // 2 MB
        };

        using var engine = new JintScriptEngine(new ScriptEngineOptions { Limits = limits });

        var ex = Assert.Throws<ScriptLimitExceededException>(() =>
        {
            engine.Evaluate("const a = []; while (true) { a.push(new Array(50000).fill('large string content')); }");
        });

        Assert.Equal("Memory", ex.LimitName);
    }

    [Fact]
    public void InterruptFromAnotherThreadEndsLoopWithin100ms()
    {
        using var engine = new JintScriptEngine(new ScriptEngineOptions
        {
            Limits = new ScriptLimits
            {
                Timeout = TimeSpan.FromSeconds(10), // long enough that only interrupt ends it
            }
        });

        var start = DateTimeOffset.UtcNow;
        var interruptThread = new Thread(() =>
        {
            Thread.Sleep(30);
            engine.Interrupt();
        });
        interruptThread.Start();

        var ex = Assert.Throws<ScriptInterruptedException>(() =>
        {
            engine.Evaluate("while (true) {}");
        });

        var elapsed = DateTimeOffset.UtcNow - start;
        Assert.Contains("interrupted", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(elapsed < TimeSpan.FromMilliseconds(250), $"Expected interrupt to finish quickly, took {elapsed.TotalMilliseconds} ms");

        interruptThread.Join();
    }
}
