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
    public void TwoEnginesRunningTheSameSourceDoNotShareFunctionState()
    {
        // The same text: the second engine takes the first one's cached parse, so the functions share one syntax tree (and its
        // cached hoisted names), and must still keep separate bindings.
        const string source = "var counter = 0; function bump() { var local = counter; counter = local + 1; return counter; }";
        using var first = new NativeJavaScriptEngine();
        using var second = new NativeJavaScriptEngine();
        first.Evaluate(source);
        second.Evaluate(source);
        Assert.Equal("1,2,1,3,2", string.Join(',', new[]
        {
            first.Evaluate("bump()").AsNumber(), first.Evaluate("bump()").AsNumber(),
            second.Evaluate("bump()").AsNumber(), first.Evaluate("bump()").AsNumber(), second.Evaluate("bump()").AsNumber(),
        }));
    }

    [Fact]
    public void ALetInASwitchCaseOrADestructuringLetStaysInItsBlock()
    {
        Assert.Equal("1,1,undefined,undefined", Run("""
            var s = 1, d = 1;
            { switch (1) { case 1: let s = 2; } }
            { let [d] = [2]; }
            { switch (1) { case 1: let t = 3; } } { let { u } = { u: 4 }; }
            [s, d, typeof t, typeof u].join()
            """));
    }

    [Fact]
    public void AFunctionOrClassInABlockStaysInTheBlock()
    {
        Assert.Equal("1,1,block,block", Run("""
            var f = 1, C = 1, inner, innerClass;
            { function f() {} class C {} inner = typeof f; innerClass = typeof C; }
            [f, C, inner === 'function' ? 'block' : inner, innerClass === 'function' ? 'block' : innerClass].join()
            """));
    }

    [Fact]
    public void ALabelledBlockWithALetKeepsItAndBreakLeavesIt()
    {
        Assert.Equal("1,1,done", Run("""
            var x = 1, y = 1, r;
            outer: { let x = 2; if (x === 2) break outer; r = 'not reached'; }
            blank: { y = y; r = 'done'; }
            [x, y, r].join()
            """));
    }

    [Fact]
    public void ForOfAndForLetClosuresCaptureTheirOwnIterationBinding()
    {
        Assert.Equal("a,b,c|0,1,2|x,y", Run("""
            var of = [], lets = [], ins = [];
            for (let item of ['a', 'b', 'c']) { of.push(function () { return item; }); }
            for (let i = 0; i < 3; i++) { lets.push(() => i); }
            for (const key in { x: 1, y: 2 }) { ins.push(function () { return key; }); }
            of.map(f => f()).join() + '|' + lets.map(f => f()).join() + '|' + ins.map(f => f()).join()
            """));
    }

    [Fact]
    public void ADeclarationInAWithBodyDoesNotLeakOutOfAnElidedBlock()
    {
        Assert.Equal("1,undefined,object", Run("""
            var x = 1, o = {};
            { with ({}) { let x = 2; } }
            if (true) { with (o) { function f() {} } }
            [x, typeof f, typeof o].join()
            """));
    }

    [Fact]
    public void CachedAnalysisOnASyntaxNodeDoesNotChangeItsEquality()
    {
        var body = new JavaScriptStatement[] { new JavaScriptVariableStatement("v", null) };
        var parameters = new[] { "p" };
        var one = new JavaScriptFunctionExpression(parameters, body);
        var two = new JavaScriptFunctionExpression(parameters, body);
        one.HoistedNames = JavaScriptEvaluator.CollectHoistedNames(body);
        Assert.Equal(one, two);
        Assert.Equal(one.GetHashCode(), two.GetHashCode());
        var block = new JavaScriptBlockStatement(body);
        var same = new JavaScriptBlockStatement(body);
        Assert.False(block.DeclaresNames);
        Assert.Equal(block, same);
        Assert.Equal(block.GetHashCode(), same.GetHashCode());
    }
}
