using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

public sealed class SequenceConversionTests
{
    [Fact]
    public void IteratorDoneUsesJavaScriptTruthinessAndReadsNextOnce()
    {
        using var engine = new JintScriptEngine();
        var value = engine.Evaluate("""
            var nextReads = 0;
            ({ [Symbol.iterator]() { return {
                get next() { nextReads++; return () => ({ done: 1n }); }
            }; } })
            """);
        var calls = 0;
        Assert.True(engine.TryReadSequence(value, _ => calls++));
        Assert.Equal(0, calls);
        Assert.Equal(1, engine.Evaluate("nextReads").AsNumber());
    }

    [Fact]
    public void ConversionFailureClosesIteratorAndPreservesOriginalFailure()
    {
        using var engine = new JintScriptEngine();
        var value = engine.Evaluate("""
            var closed = 0;
            ({ [Symbol.iterator]() { return {
                next() { return { done: false, value: 3 }; },
                return() { closed++; throw Error('cleanup failed'); }
            }; } })
            """);
        var original = new InvalidOperationException("conversion failed");
        var actual = Assert.Throws<InvalidOperationException>(() => engine.TryReadSequence(value, _ => throw original));
        Assert.Same(original, actual);
        Assert.Equal(1, engine.Evaluate("closed").AsNumber());
    }

    [Theory]
    [InlineData("next() { throw Error('step failed'); }")]
    [InlineData("next() { return { get value() { throw Error('value failed'); }, done: false }; }")]
    public void IteratorStepFailureDoesNotInvokeReturn(string next)
    {
        using var engine = new JintScriptEngine();
        var value = engine.Evaluate("var closed=0; ({ [Symbol.iterator]() { return { " + next + ", return() { closed++; return {}; } }; } })");
        Assert.Throws<ScriptRuntimeException>(() => engine.TryReadSequence(value, _ => { }));
        Assert.Equal(0, engine.Evaluate("closed").AsNumber());
    }

    [Fact]
    public void RetiredAndForeignRealmIterablesAreRejected()
    {
        using var engine = new JintScriptEngine();
        using var other = new JintScriptEngine();
        var retired = engine.Evaluate("[1,2]");
        engine.Configure(ScriptLimits.Default);
        Assert.False(engine.TryReadSequence(retired, _ => throw new Exception("retired value visited")));
        Assert.False(engine.TryReadSequence(other.Evaluate("[1,2]"), _ => throw new Exception("foreign value visited")));
    }
}
