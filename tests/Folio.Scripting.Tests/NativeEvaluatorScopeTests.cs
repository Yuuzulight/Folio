using Folio.Scripting.Native;

namespace Folio.Scripting.Tests;

/// <summary>
/// The evaluator avoids work that cannot be seen from script: a block with no declarations runs in its enclosing scope, a
/// function's hoisted names and receiver binding are computed once, and an assignment target is evaluated once. These pin
/// what must stay visible.
/// </summary>
public sealed class NativeEvaluatorScopeTests
{
    private static string Run(string source)
    {
        using var engine = new NativeJavaScriptEngine();
        return engine.Evaluate(source).AsString();
    }

    [Fact]
    public void ABlockWithLetOrConstStillKeepsItsNamesToItself()
    {
        Assert.Equal("1,1,undefined,undefined", Run("""
            var x = 1, y = 1;
            { let x = 2; const y = 2; }
            { let a = 1; } { const b = 1; }
            [x, y, typeof a, typeof b].join()
            """));
    }

    [Fact]
    public void ABlockWithNoDeclarationsLeavesVarAndAssignmentsInTheEnclosingScope()
    {
        Assert.Equal("5,6,7", Run("""
            function f() {
              var a = 1;
              if (a) { var b = 6; a = 5; c = 7; }
              return [a, b, c].join();
            }
            f()
            """));
    }

    [Fact]
    public void ClosuresInALoopBodyWithLetKeepEachIterationAndWithVarShareOne()
    {
        Assert.Equal("0,1,2|3,3,3", Run("""
            var kept = [], shared = [];
            for (let i = 0; i < 3; i++) { kept.push(function () { return i; }); }
            for (var j = 0; j < 3; j++) { shared.push(function () { return j; }); }
            kept.map(function (f) { return f(); }).join() + '|' + shared.map(function (f) { return f(); }).join()
            """));
    }

    [Fact]
    public void ACatchParameterDoesNotLeakOutOfABlockThatHoldsTheTry()
    {
        Assert.Equal("outer", Run("""
            function f(e) {
              if (true) { try { throw 'inner'; } catch (e) { } }
              return e;
            }
            f('outer')
            """));
    }

    [Fact]
    public void BreakContinueAndReturnLeaveBlocksThatHaveNoScopeOfTheirOwn()
    {
        Assert.Equal("0,1,3|found", Run("""
            var seen = [];
            for (var i = 0; i < 5; i++) { if (i === 2) { continue; } if (i === 4) { break; } seen.push(i); }
            function find(list) { for (var k = 0; k < list.length; k++) { if (list[k] === 'b') { return 'found'; } } return 'missing'; }
            seen.join() + '|' + find(['a', 'b'])
            """));
    }

    [Fact]
    public void AVarInANestedBlockOfAFunctionIsHoistedOnEveryCall()
    {
        Assert.Equal("undefined:undefined,undefined:number,undefined:undefined", Run("""
            function f(go) {
              var before = typeof late;
              if (go) { for (var late = 2; late < 3; late++) {} }
              return before + ':' + typeof late;
            }
            [f(false), f(true), f(false)].join()
            """));
    }

    [Fact]
    public void AnAssignmentTargetIsEvaluatedOnce()
    {
        Assert.Equal("1,1,1,5,5", Run("""
            var calls = 0, box = { value: 0 }, list = [0], fn = function () {};
            function target() { calls++; return box; }
            function listTarget() { calls++; return list; }
            function fnTarget() { calls++; return fn; }
            target().value = 5; var a = calls;
            listTarget()[0] = 5; var b = calls - a;
            fnTarget().tag = 5; var c = calls - a - b;
            [a, b, c, box.value, list[0]].join()
            """));
    }

    [Fact]
    public void FunctionsKeepTheirReceiverThroughCallApplyBindAndMethodCalls()
    {
        Assert.Equal("b,b,c,a", Run("""
            function name() { return this.n; }
            var one = { n: 'a', m: name }, two = { n: 'b' }, three = { n: 'c' };
            [name.call(two), name.apply(two), name.bind(three)(), one.m()].join()
            """));
    }

    [Fact]
    public void TwoEnginesDoNotShareFunctionState()
    {
        using var first = new NativeJavaScriptEngine();
        using var second = new NativeJavaScriptEngine();
        const string source = "var counter = {0}; function bump() {{ var local = counter; counter = local + 1; return counter; }}";
        first.Evaluate(string.Format(source, 0));
        second.Evaluate(string.Format(source, 100));
        Assert.Equal("1,2,101,3", $"{first.Evaluate("bump()").AsNumber()},{first.Evaluate("bump()").AsNumber()},{second.Evaluate("bump()").AsNumber()},{first.Evaluate("bump()").AsNumber()}");
    }
}
